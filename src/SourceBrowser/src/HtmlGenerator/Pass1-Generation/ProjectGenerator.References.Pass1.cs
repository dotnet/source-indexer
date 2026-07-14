using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class ProjectGenerator
    {
        public readonly Dictionary<string, Dictionary<string, List<Reference>>> ReferencesByTargetAssemblyAndSymbolId =
            new Dictionary<string, Dictionary<string, List<Reference>>>();

        public IEnumerable<string> UsedReferences { get; private set; }

        /// <summary>
        /// Lock-free, per-partition accumulator for the data a document contributes to the project --
        /// references as well as declared symbols, redirect-map locations, base members and implemented
        /// interface members. Each partition Task processes its documents sequentially, so a single
        /// collector is only ever touched by one document at a time and needs no synchronization. The
        /// collectors are merged into the project-wide maps single-threaded once generation completes
        /// (see <see cref="MergeReferences"/> and <see cref="MergeDeclarations"/>), which avoids
        /// contending project-global locks on the per-symbol hot path.
        /// </summary>
        public sealed class ReferenceCollector
        {
            public readonly Dictionary<string, Dictionary<string, List<Reference>>> ReferencesByTargetAssemblyAndSymbolId =
                new Dictionary<string, Dictionary<string, List<Reference>>>();

            public readonly Dictionary<ISymbol, string> DeclaredSymbols =
                new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);

            public readonly Dictionary<string, List<Tuple<string, long>>> SymbolIDToListOfLocationsMap =
                new Dictionary<string, List<Tuple<string, long>>>();

            public readonly Dictionary<ISymbol, ISymbol> BaseMembers =
                new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default);

            public readonly MultiDictionary<ISymbol, ISymbol> ImplementedInterfaceMembers =
                new MultiDictionary<ISymbol, ISymbol>();

            public void Add(string toAssemblyId, string toSymbolId, Reference reference)
            {
                if (!ReferencesByTargetAssemblyAndSymbolId.TryGetValue(toAssemblyId, out var referencesToAssembly))
                {
                    referencesToAssembly = new Dictionary<string, List<Reference>>(StringComparer.OrdinalIgnoreCase);
                    ReferencesByTargetAssemblyAndSymbolId.Add(toAssemblyId, referencesToAssembly);
                }

                if (!referencesToAssembly.TryGetValue(toSymbolId, out var referencesToSymbol))
                {
                    referencesToSymbol = new List<Reference>();
                    referencesToAssembly.Add(toSymbolId, referencesToSymbol);
                }

                referencesToSymbol.Add(reference);
            }

            public void AddDeclaredSymbolLocation(string symbolId, string documentRelativeFilePath, long positionInFile)
            {
                if (!SymbolIDToListOfLocationsMap.TryGetValue(symbolId, out var bucket))
                {
                    bucket = new List<Tuple<string, long>>();
                    SymbolIDToListOfLocationsMap.Add(symbolId, bucket);
                }

                bucket.Add(Tuple.Create(documentRelativeFilePath, positionInFile));
            }

            public void AddBaseMember(ISymbol member, ISymbol baseMember)
            {
                BaseMembers[member] = baseMember;
            }

            public void AddImplementedInterfaceMember(ISymbol implementationMember, ISymbol interfaceMember)
            {
                if (implementationMember == null)
                {
                    throw new ArgumentNullException(nameof(implementationMember));
                }

                if (interfaceMember == null)
                {
                    throw new ArgumentNullException(nameof(interfaceMember));
                }

                ImplementedInterfaceMembers.Add(implementationMember, interfaceMember);
            }
        }

        /// <summary>
        /// Merges a per-partition <see cref="ReferenceCollector"/> into the shared project map. Must be
        /// called single-threaded (i.e. after all generation Tasks have completed), so it takes no locks.
        /// </summary>
        public void MergeReferences(ReferenceCollector collector)
        {
            foreach (var referencesToAssembly in collector.ReferencesByTargetAssemblyAndSymbolId)
            {
                if (!ReferencesByTargetAssemblyAndSymbolId.TryGetValue(referencesToAssembly.Key, out var targetReferencesToAssembly))
                {
                    // No other partition touched this assembly; splice the whole subtree in directly.
                    ReferencesByTargetAssemblyAndSymbolId.Add(referencesToAssembly.Key, referencesToAssembly.Value);
                    continue;
                }

                foreach (var referencesToSymbol in referencesToAssembly.Value)
                {
                    if (!targetReferencesToAssembly.TryGetValue(referencesToSymbol.Key, out var targetReferencesToSymbol))
                    {
                        targetReferencesToAssembly.Add(referencesToSymbol.Key, referencesToSymbol.Value);
                    }
                    else
                    {
                        targetReferencesToSymbol.AddRange(referencesToSymbol.Value);
                    }
                }
            }
        }

        public void AddReference(
            string documentDestinationPath,
            SourceText referenceText,
            string destinationAssemblyName,
            ISymbol symbol,
            string symbolId,
            int startPosition,
            int endPosition,
            ReferenceKind kind,
            ReferenceCollector collector)
        {
            string referenceString = referenceText.ToString(TextSpan.FromBounds(startPosition, endPosition));
            if (symbol is INamedTypeSymbol && (referenceString == "this" || referenceString == "base"))
            {
                // Don't count "this" or "base" expressions that bind to this type as references
                return;
            }

            var line = referenceText.Lines.GetLineFromPosition(startPosition);
            int start = referenceText.Lines.GetLinePosition(startPosition).Character;
            int end = start + endPosition - startPosition;
            int lineNumber = line.LineNumber + 1;
            string lineText = line.ToString();

            AddReference(
                documentDestinationPath,
                lineText,
                start,
                referenceString.Length,
                lineNumber,
                AssemblyName,
                destinationAssemblyName,
                symbol,
                symbolId,
                kind,
                collector);
        }

        public void AddReference(
            string documentDestinationPath,
            string lineText,
            int referenceStartOnLine,
            int referenceLength,
            int lineNumber,
            string fromAssemblyName,
            string toAssemblyName,
            ISymbol symbol,
            string symbolId,
            ReferenceKind kind,
            ReferenceCollector collector)
        {
            string localPath = Paths.MakeRelativeToFolder(
                documentDestinationPath,
                Path.Combine(SolutionGenerator.SolutionDestinationFolder, fromAssemblyName));
            localPath = Path.ChangeExtension(localPath, null);

            int referenceEndOnLine = referenceStartOnLine + referenceLength;

            lineText = Markup.HtmlEscape(lineText, ref referenceStartOnLine, ref referenceEndOnLine);

            string symbolName = GetSymbolName(symbol, symbolId);

            var reference = new Reference()
            {
                ToAssemblyId = toAssemblyName,
                ToSymbolId = symbolId,
                ToSymbolName = symbolName,
                FromAssemblyId = fromAssemblyName,
                FromLocalPath = localPath,
                ReferenceLineText = lineText,
                ReferenceLineNumber = lineNumber,
                ReferenceColumnStart = referenceStartOnLine,
                ReferenceColumnEnd = referenceEndOnLine,
                Kind = kind
            };

            if (referenceStartOnLine < 0 ||
                referenceStartOnLine >= referenceEndOnLine ||
                referenceEndOnLine > lineText.Length)
            {
                Log.Exception(
                    string.Format("AddReference: start = {0}, end = {1}, lineText = {2}, documentDestinationPath = {3}",
                    referenceStartOnLine,
                    referenceEndOnLine,
                    lineText,
                    documentDestinationPath));
            }

            string linkRelativePath = GetLinkRelativePath(reference);

            reference.Url = linkRelativePath;

            // The caller supplies a collector scoped to its own thread/partition, so this appends with
            // no synchronization. Collectors are merged into the shared map single-threaded via
            // MergeReferences once the owning generation phase completes.
            collector.Add(reference.ToAssemblyId, reference.ToSymbolId, reference);
        }

        private static string GetLinkRelativePath(Reference reference)
        {
            string linkRelativePath = reference.FromLocalPath.Replace('\\', '/') + ".html#" + reference.ReferenceLineNumber;
            if (reference.ToAssemblyId == reference.FromAssemblyId)
            {
                linkRelativePath = "../" + linkRelativePath;
            }
            else
            {
                linkRelativePath = "../../" + reference.FromAssemblyId + "/" + linkRelativePath;
            }

            return linkRelativePath;
        }

        private static string GetSymbolName(ISymbol symbol, string symbolId)
        {
            string symbolName = null;
            if (symbol != null)
            {
                symbolName = SymbolIdService.GetName(symbol);
                if (symbolName == ".ctor")
                {
                    symbolName = SymbolIdService.GetName(symbol.ContainingType) + " .ctor";
                }
            }
            else
            {
                symbolName = symbolId;
            }

            return symbolName;
        }

        private void GenerateUsedReferencedAssemblyList()
        {
            this.UsedReferences = ReferencesByTargetAssemblyAndSymbolId
                .Select(r => r.Key)
                .Where(a =>
                    a != AssemblyName &&
                    a != Constants.MSBuildPropertiesAssembly &&
                    a != Constants.MSBuildItemsAssembly &&
                    a != Constants.MSBuildTargetsAssembly &&
                    a != Constants.MSBuildTasksAssembly &&
                    a != Constants.GuidAssembly)
                .Concat(ForwardedReferenceAssemblies);
            File.WriteAllLines(Path.Combine(ProjectDestinationFolder, Constants.UsedReferencedAssemblyList + ".txt"), this.UsedReferences);
        }

        internal HashSet<string> ForwardedReferenceAssemblies = new HashSet<string>();

        private void GenerateReferencedAssemblyList()
        {
            Log.Write("Referenced assembly list...");
            var index = Path.Combine(ProjectDestinationFolder, Constants.ReferencedAssemblyList + ".txt");
            var list = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var projectReference in Project.ProjectReferences.OrderBy(p => Project.Solution.GetProject(p.ProjectId).AssemblyName))
            {
                list.Add(Project.Solution.GetProject(projectReference.ProjectId).AssemblyName);
            }

            foreach (var metadataReference in Project.MetadataReferences.OrderBy(m => Path.GetFileNameWithoutExtension(m.Display)))
            {
                list.Add(Path.GetFileNameWithoutExtension(metadataReference.Display));
            }

            foreach (var assembly in ForwardedReferenceAssemblies)
            {
                list.Add(assembly);
            }

            File.WriteAllText(index, string.Join(Environment.NewLine, list));
        }

        public static void GenerateReferencesDataFiles(
            string solutionDestinationFolder,
            Dictionary<string, Dictionary<string, List<Reference>>> referencesByTargetAssemblyAndSymbolId)
        {
            Log.Write("References data files...", ConsoleColor.White);

            foreach (var referencesToAssembly in referencesByTargetAssemblyAndSymbolId)
            {
                GenerateReferencesDataFilesToAssembly(
                    solutionDestinationFolder,
                    referencesToAssembly.Key,
                    referencesToAssembly.Value);
            }
        }

        // Reference data is handed from Pass1 to Pass2 through disk so the two phases stay decoupled
        // (Pass2 rediscovers projects from the output folder and runs after Pass1's memory is freed).
        // Rather than one tiny file per referenced symbol -- which produced ~185K files for the largest
        // assembly and made both the Pass1 write and the Pass2 read/delete filesystem-metadata bound --
        // references are consolidated into a small, fixed number of shard files per assembly, keyed by a
        // stable hash of the symbol id. Every project maps a given symbol to the same shard so appends
        // aggregate correctly, and Pass2 reconstructs each symbol's references by grouping one shard in
        // memory at a time; the shard count bounds that per-shard grouping memory.
        public const string ReferenceShardPrefix = "_r";
        public const string ReferenceShardExtension = ".dat";

        public static readonly int ReferenceShardCount = ComputeReferenceShardCount();

        // ReferenceShardCount is always a power of two, so a symbol id maps to its shard with a cheap
        // mask instead of a modulo. Kept as its own field so the mask is computed once, not per call.
        private static readonly int ReferenceShardMask = ReferenceShardCount - 1;

        // This fallback only runs if the GC can't report a memory budget, which is rare -- so bias it
        // pessimistically. ProcessorCount counts logical cores (hardware threads), and even high-end
        // workstations are often only ~1-2 GiB per thread today (e.g. a 16-core/32-thread box with 32-64
        // GB, given current RAM prices), while laptops sit lower still. 1.5 GiB/thread models that range
        // and errs toward more (smaller, safer) shards; users on unusual hardware can set
        // SOURCEBROWSER_REFERENCE_SHARDS explicitly.
        private const int FallbackMiBPerCore = 1536;

        private static int ComputeReferenceShardCount()
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("SOURCEBROWSER_REFERENCE_SHARDS"), out int configured) &&
                configured > 0)
            {
                // Round an explicit override up to a power of two so the mask-based placement stays valid.
                return 1 << CeilLog2(configured);
            }

            // The shard count is a power of two chosen as the larger of two independent floors:
            //
            //  * Hardware parallelism -- at least one shard per logical core, so Pass1's Parallel.For over
            //    shards can keep every core busy writing distinct files.
            //
            //  * Memory pressure -- more, smaller shards when less memory is available, so Pass2's
            //    one-shard-at-a-time grouping stays bounded. Starting from a 16 GiB comfort point, each
            //    halving of available memory adds one power of two (16 GiB -> 32, 8 -> 64, 4 -> 128, ...).
            //
            // Taking the max keeps both guarantees; the clamp keeps the intermediate file count sane at the
            // extremes. Shards stay small (tens of MB) at any of these counts, so there is no spill/OOM risk.
            int processorCount = Environment.ProcessorCount;
            int coreExponent = CeilLog2(processorCount);

            long availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (availableBytes <= 0)
            {
                availableBytes = (long)processorCount * FallbackMiBPerCore * 1024 * 1024;
            }
            int availableGiB = int.Max(1, (int)(availableBytes / (1024L * 1024 * 1024)));
            int memoryExponent = 9 - int.Log2(availableGiB);

            int exponent = int.Clamp(int.Max(coreExponent, memoryExponent), 5, 10);
            return 1 << exponent;
        }

        private static int CeilLog2(int value) =>
            value <= 1 ? 0 : int.Log2(value - 1) + 1;

        private static int GetReferenceShard(string symbolId)
        {
            // FNV-1a over the symbol id: deterministic within and across processes (unlike
            // string.GetHashCode, which is randomized per run) so a symbol always lands in the same shard.
            const uint OffsetBasis = 2166136261;
            const uint Prime = 16777619;

            uint hash = OffsetBasis;
            foreach (char c in symbolId)
            {
                hash = (hash ^ c) * Prime;
            }

            return (int)(hash & (uint)ReferenceShardMask);
        }

        public static void GenerateReferencesDataFilesToAssembly(
            string solutionDestinationFolder,
            string toAssemblyId,
            Dictionary<string, List<Reference>> referencesToAssembly)
        {
            var assemblyReferencesDataFolder = Path.Combine(
                solutionDestinationFolder,
                toAssemblyId,
                Constants.ReferencesFileName);
            Directory.CreateDirectory(assemblyReferencesDataFolder);

            var symbolsByShard = new List<KeyValuePair<string, List<Reference>>>[ReferenceShardCount];
            foreach (var referencesToSymbol in referencesToAssembly)
            {
                int shard = GetReferenceShard(referencesToSymbol.Key);
                (symbolsByShard[shard] ??= new List<KeyValuePair<string, List<Reference>>>()).Add(referencesToSymbol);
            }

            Parallel.For(
                0,
                ReferenceShardCount,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                shard =>
                {
                    var symbols = symbolsByShard[shard];
                    if (symbols == null)
                    {
                        return;
                    }

                    var shardFile = Path.Combine(
                        assemblyReferencesDataFolder,
                        ReferenceShardPrefix + shard + ReferenceShardExtension);

                    try
                    {
                        WriteShardReferencesToFile(symbols, shardFile);
                    }
                    catch (ArgumentException ex)
                    {
                        Log.Exception("ArgumentException writing reference shard: " + ex.ToString() + "\r\n\r\n" + "assemblyReferencesDataFolder: " + assemblyReferencesDataFolder + "   shard: " + shard);
                    }
                });
        }

        // Each record is three lines: the symbol id followed by the two lines Reference.WriteTo emits.
        // Projects generate sequentially, so appending to a shard is safe without synchronization; within
        // one project each shard index is written by exactly one task, so the parallel writes never race.
        private static void WriteShardReferencesToFile(
            List<KeyValuePair<string, List<Reference>>> symbols,
            string shardFile)
        {
            using (var writer = new StreamWriter(shardFile, append: true, Encoding.UTF8, bufferSize: 65536))
            {
                foreach (var referencesToSymbol in symbols)
                {
                    string symbolId = referencesToSymbol.Key;
                    foreach (var reference in referencesToSymbol.Value)
                    {
                        writer.WriteLine(symbolId);
                        reference.WriteTo(writer);
                    }
                }
            }
        }
    }
}
