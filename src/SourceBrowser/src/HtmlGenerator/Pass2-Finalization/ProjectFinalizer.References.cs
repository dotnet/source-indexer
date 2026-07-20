using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class ProjectFinalizer
    {
        public void CreateReferencesFiles(
            HashSet<string> additionalReferencedSymbolIds = null,
            Dictionary<string, List<Reference>> mergedDivergentReferencesBySymbolId = null,
            IReadOnlyCollection<string> allConfigs = null)
        {
            BackpatchUnreferencedDeclarations(referencesFolder, additionalReferencedSymbolIds);
            Markup.WriteRedirectFile(ProjectDestinationFolder);
            GenerateFinalReferencesFiles(referencesFolder, mergedDivergentReferencesBySymbolId, allConfigs);
        }

        public void GenerateFinalReferencesFiles(
            string referencesFolder,
            Dictionary<string, List<Reference>> mergedDivergentReferencesBySymbolId = null,
            IReadOnlyCollection<string> allConfigs = null)
        {
            var shardFiles = Directory.Exists(referencesFolder)
                ? Directory.GetFiles(
                    referencesFolder,
                    ProjectGenerator.ReferenceShardPrefix + "*" + ProjectGenerator.ReferenceShardExtension)
                : Array.Empty<string>();

            // Symbols that only have a base member or implemented interface member link (and no actual
            // references) still need a references file so those links render. They are no longer tracked
            // via per-symbol marker files -- the base/interface member maps loaded in Pass2 already hold
            // exactly that set -- so track which symbols the shards produced and backfill the rest below.
            var writtenSymbols = new HashSet<string>(StringComparer.Ordinal);

            // Regular assemblies pack their per-symbol reference fragments into a single file per assembly
            // instead of emitting ~185K individual <symbolId>.html files. Profiling showed the finalize
            // phase is dominated by opening and closing those files (FileStream Dispose/ctor ~56%), while
            // the fragment generation itself is negligible. The MSBuild/Guid assemblies keep individual
            // files because IndexLoader enumerates their reference folders by file name.
            bool packOutput = ShouldPackReferences();
            ReferencePackBuilder pack = null;

            try
            {
                if (packOutput)
                {
                    // The builder creates its files lazily on the first fragment, so a regular assembly with
                    // no references produces no pack (and no R folder), matching the previous per-file output.
                    pack = new ReferencePackBuilder(referencesFolder);
                }

                if (shardFiles.Length != 0)
                {
                    Log.Write("Creating references files for " + this.AssemblyId);

                    // Process one shard at a time so the per-symbol grouping only ever holds a single shard's
                    // references in memory, then write that shard's fragments in parallel. A symbol maps to
                    // exactly one shard, so its full reference set is always grouped from a single file.
                    foreach (var shardFile in shardFiles)
                    {
                        try
                        {
                            GenerateReferencesFilesFromShard(shardFile, referencesFolder, writtenSymbols, pack);
                        }
                        catch (Exception ex)
                        {
                            Log.Exception(ex, "Failed to generate references files for shard: " + shardFile);
                        }
                    }
                }

                GenerateBaseAndInterfaceOnlyReferencesFiles(referencesFolder, writtenSymbols, pack);

                // Config-aware pass: for symbols whose merged reference set genuinely diverges across
                // configs (ConfigReferenceMerger.IsFullyShared == false), append a corrected, config-tagged
                // fragment for that symbolId. ReferencePackBuilder's index is last-write-wins (see its
                // remarks), so this later record transparently replaces whatever the ordinary shard-based
                // render above produced for that symbol -- no rewrite of the earlier bytes is needed. Only
                // supported for the packed/regular-assembly path; MSBuild/Guid pseudo-assemblies never
                // participate in a real config merge.
                if (packOutput && mergedDivergentReferencesBySymbolId != null && mergedDivergentReferencesBySymbolId.Count != 0)
                {
                    GenerateMergedReferencesFragments(mergedDivergentReferencesBySymbolId, allConfigs, pack);
                }

            }
            finally
            {
                pack?.Complete();
            }
        }

        // Renders and appends one config-tagged fragment per divergent symbol, straight from
        // ConfigReferenceMerger's merged Reference objects -- bypassing raw-shard-line parsing entirely,
        // since these objects are already fully materialized (ConfigSet included).
        private void GenerateMergedReferencesFragments(
            Dictionary<string, List<Reference>> mergedDivergentReferencesBySymbolId,
            IReadOnlyCollection<string> allConfigs,
            ReferencePackBuilder pack)
        {
            var symbols = new List<KeyValuePair<string, List<Reference>>>(mergedDivergentReferencesBySymbolId);
            var fragments = new byte[symbols.Count][];

            Parallel.For(
                0,
                symbols.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                i =>
                {
                    try
                    {
                        fragments[i] = GenerateMergedReferencesFragment(symbols[i].Key, symbols[i].Value, allConfigs);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex, "Failed to generate merged references fragment for symbol: " + symbols[i].Key);
                    }
                });

            for (int i = 0; i < symbols.Count; i++)
            {
                if (fragments[i] != null)
                {
                    pack.Add(symbols[i].Key, fragments[i]);
                }
            }
        }

        private byte[] GenerateMergedReferencesFragment(string symbolId, List<Reference> references, IReadOnlyCollection<string> allConfigs)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 65536, leaveOpen: true))
                {
                    WriteReferencesContent(writer, symbolId, references, allConfigs);
                }

                return stream.ToArray();
            }
        }

        private bool ShouldPackReferences()
        {
            return this.AssemblyId != Constants.MSBuildItemsAssembly &&
                this.AssemblyId != Constants.MSBuildPropertiesAssembly &&
                this.AssemblyId != Constants.MSBuildTargetsAssembly &&
                this.AssemblyId != Constants.MSBuildTasksAssembly &&
                this.AssemblyId != Constants.GuidAssembly;
        }

        private void GenerateBaseAndInterfaceOnlyReferencesFiles(string referencesFolder, HashSet<string> writtenSymbols, ReferencePackBuilder pack)
        {
            if (!ShouldPackReferences())
            {
                return;
            }

            var pending = new HashSet<ulong>();
            foreach (var id in BaseMembers.Keys)
            {
                pending.Add(id);
            }
            foreach (var id in ImplementedInterfaceMembers.Keys)
            {
                pending.Add(id);
            }

            if (pending.Count == 0)
            {
                return;
            }

            var backfill = new List<string>();
            foreach (var id in pending)
            {
                var symbolId = Serialization.ULongToHexString(id);
                if (!writtenSymbols.Contains(symbolId))
                {
                    backfill.Add(symbolId);
                }
            }

            var fragments = new byte[backfill.Count][];
            Parallel.For(
                0,
                backfill.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                i =>
                {
                    try
                    {
                        fragments[i] = GenerateReferencesFragment(backfill[i], Array.Empty<string>());
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex, "Failed to generate base/interface references file for symbol: " + backfill[i]);
                    }
                });

            for (int i = 0; i < backfill.Count; i++)
            {
                if (fragments[i] != null)
                {
                    pack.Add(backfill[i], fragments[i]);
                }
            }
        }

        private void GenerateReferencesFilesFromShard(string shardFile, string referencesFolder, HashSet<string> writtenSymbols, ReferencePackBuilder pack)
        {
            var referencesBySymbol = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            // Read the shard and mark it delete-on-close so it is removed once consumed. Each record is
            // three lines: the symbol id followed by the two lines Reference.WriteTo emits.
            using (var stream = new FileStream(shardFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 65536, FileOptions.SequentialScan | FileOptions.DeleteOnClose))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string symbolId;
                while ((symbolId = reader.ReadLine()) != null)
                {
                    string separatedLine = reader.ReadLine();
                    string sourceLine = reader.ReadLine();
                    if (separatedLine == null || sourceLine == null)
                    {
                        break;
                    }

                    if (!referencesBySymbol.TryGetValue(symbolId, out var lines))
                    {
                        lines = new List<string>();
                        referencesBySymbol.Add(symbolId, lines);
                    }

                    lines.Add(separatedLine);
                    lines.Add(sourceLine);
                }
            }

            // Record which symbols this shard produced so the base/interface-only backfill can skip them.
            // A symbol maps to exactly one shard, so this runs single-threaded across shards without racing.
            foreach (var symbolId in referencesBySymbol.Keys)
            {
                writtenSymbols.Add(symbolId);
            }

            if (pack != null)
            {
                // Generate every fragment for this shard in parallel, then append them to the pack
                // sequentially. The pack build only holds one shard's fragments in memory at a time.
                var symbols = new List<KeyValuePair<string, List<string>>>(referencesBySymbol);
                var fragments = new byte[symbols.Count][];

                Parallel.For(
                    0,
                    symbols.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    i =>
                    {
                        try
                        {
                            fragments[i] = GenerateReferencesFragment(symbols[i].Key, symbols[i].Value.ToArray());
                        }
                        catch (Exception ex)
                        {
                            Log.Exception(ex, "Failed to generate references fragment for symbol: " + symbols[i].Key);
                        }
                    });

                for (int i = 0; i < symbols.Count; i++)
                {
                    if (fragments[i] != null)
                    {
                        pack.Add(symbols[i].Key, fragments[i]);
                    }
                }

                return;
            }

            Parallel.ForEach(
                referencesBySymbol,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                symbol =>
                {
                    try
                    {
                        WriteReferencesFile(symbol.Key, symbol.Value.ToArray(), referencesFolder);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex, "Failed to generate references file for symbol: " + symbol.Key);
                    }
                });
        }

        // Generates a reference file fragment as its exact on-disk bytes. This routes through the same
        // StreamWriter + Encoding.UTF8 path a per-symbol file would use, so the packed bytes -- including
        // the UTF-8 preamble and CRLF line endings -- are identical to the standalone .html file.
        private byte[] GenerateReferencesFragment(string symbolId, string[] referencesLines)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 65536, leaveOpen: true))
                {
                    WriteReferencesContent(writer, symbolId, referencesLines);
                }

                return stream.ToArray();
            }
        }

        private void WriteReferencesFile(string symbolId, string[] referencesLines, string referencesFolder)
        {
            string referencesFile = Path.Combine(referencesFolder, symbolId + ".html");

            using (var writer = new StreamWriter(referencesFile, append: false, Encoding.UTF8, bufferSize: 65536))
            {
                WriteReferencesContent(writer, symbolId, referencesLines);
            }
        }

        private void WriteReferencesContent(TextWriter writer, string symbolId, string[] referencesLines)
        {
            var referenceKindGroups = CreateReferences(referencesLines, out string symbolName);
            WriteReferencesContent(writer, symbolId, referenceKindGroups, symbolName, allConfigs: null);
        }

        // Config-aware entry point: renders from already-merged Reference objects (ConfigSet populated
        // by ConfigReferenceMerger) instead of parsing raw shard lines, and tags each reference line with
        // data-configs when its ConfigSet doesn't cover every registered config -- see
        // GenerateMergedReferencesFragment for how this is wired into the packed output.
        private void WriteReferencesContent(TextWriter writer, string symbolId, List<Reference> references, IReadOnlyCollection<string> allConfigs)
        {
            var referenceKindGroups = CreateReferences(references, out string symbolName);
            WriteReferencesContent(writer, symbolId, referenceKindGroups, symbolName, allConfigs);
        }

        private void WriteReferencesContent(
            TextWriter writer,
            string symbolId,
            List<ReferenceKindGroup> referenceKindGroups,
            string symbolName,
            IReadOnlyCollection<string> allConfigs)
        {
            Markup.WriteReferencesFileHeader(writer, symbolName);

            if (this.AssemblyId != Constants.MSBuildItemsAssembly &&
                this.AssemblyId != Constants.MSBuildPropertiesAssembly &&
                this.AssemblyId != Constants.MSBuildTargetsAssembly &&
                this.AssemblyId != Constants.MSBuildTasksAssembly &&
                this.AssemblyId != Constants.GuidAssembly)
            {
                var id = Serialization.HexStringToULong(symbolId);
                WriteBaseMember(id, writer);
                WriteImplementedInterfaceMembers(id, writer);
            }

            foreach (var referenceKind in referenceKindGroups.OrderBy(k => (int)k.Kind))
            {
                string formatString = "";

                switch (referenceKind.Kind)
                {
                    case ReferenceKind.Reference:
                        formatString = "{0} reference{1} to {2}";
                        break;
                    case ReferenceKind.DerivedType:
                        formatString = "{0} type{1} derived from {2}";
                        break;
                    case ReferenceKind.InterfaceInheritance:
                        formatString = "{0} interface{1} inheriting from {2}";
                        break;
                    case ReferenceKind.InterfaceImplementation:
                        formatString = "{0} implementation{1} of {2}";
                        break;
                    case ReferenceKind.Read:
                        formatString = "{0} read{1} of {2}";
                        break;
                    case ReferenceKind.Write:
                        formatString = "{0} write{1} to {2}";
                        break;
                    case ReferenceKind.Instantiation:
                        formatString = "{0} instantiation{1} of {2}";
                        break;
                    case ReferenceKind.Override:
                        formatString = "{0} override{1} of {2}";
                        break;
                    case ReferenceKind.InterfaceMemberImplementation:
                        formatString = "{0} implementation{1} of {2}";
                        break;
                    case ReferenceKind.GuidUsage:
                        formatString = "{0} usage{1} of Guid {2}";
                        break;
                    case ReferenceKind.EmptyArrayAllocation:
                        formatString = "{0} allocation{1} of empty arrays";
                        break;
                    case ReferenceKind.MSBuildPropertyAssignment:
                        formatString = "{0} assignment{1} to MSBuild property {2}";
                        break;
                    case ReferenceKind.MSBuildPropertyUsage:
                        formatString = "{0} usage{1} of MSBuild property {2}";
                        break;
                    case ReferenceKind.MSBuildItemAssignment:
                        formatString = "{0} assignment{1} to MSBuild item {2}";
                        break;
                    case ReferenceKind.MSBuildItemUsage:
                        formatString = "{0} usage{1} of MSBuild item {2}";
                        break;
                    case ReferenceKind.MSBuildTargetDeclaration:
                        formatString = "{0} declaration{1} of MSBuild target {2}";
                        break;
                    case ReferenceKind.MSBuildTargetUsage:
                        formatString = "{0} usage{1} of MSBuild target {2}";
                        break;
                    case ReferenceKind.MSBuildTaskDeclaration:
                        formatString = "{0} import{1} of MSBuild task {2}";
                        break;
                    case ReferenceKind.MSBuildTaskUsage:
                        formatString = "{0} call{1} to MSBuild task {2}";
                        break;
                    default:
                        throw new NotImplementedException("Missing case for " + referenceKind.Kind);
                }

                int totalReferenceCount = referenceKind.Count;
                string headerText = string.Format(
                    formatString,
                    totalReferenceCount,
                    totalReferenceCount == 1 ? "" : "s",
                    symbolName);

                writer.Write(@"<div class=""rH"">");
                writer.Write(headerText);
                writer.Write("</div>");

                foreach (var sameAssemblyReferencesGroup in referenceKind.Assemblies.OrderBy(a => a.AssemblyName))
                {
                    string assemblyName = sameAssemblyReferencesGroup.AssemblyName;
                    writer.Write("<div class=\"rA\">");
                    writer.Write(assemblyName);
                    writer.Write(" (");
                    writer.Write(sameAssemblyReferencesGroup.Count);
                    writer.Write(")</div>");

                    writer.Write("<div class=\"rG\" id=\"");
                    writer.Write(assemblyName);
                    writer.Write("\">");

                    foreach (var sameFileReferencesGroup in sameAssemblyReferencesGroup.Files.OrderBy(f => f.FilePath))
                    {
                        writer.Write("<div class=\"rF\">");
                        writer.Write("<div class=\"rN\">");
                        writer.Write(sameFileReferencesGroup.FilePath);
                        writer.Write(" (");
                        writer.Write(sameFileReferencesGroup.Count);
                        writer.Write(")</div>");
                        writer.WriteLine();

                        foreach (var sameLineReferencesGroup in sameFileReferencesGroup.Lines)
                        {
                            var references = sameLineReferencesGroup.References;
                            var url = references[0].Url;
                            writer.Write("<a href=\"");
                            writer.Write(url);
                            writer.Write("\"");
                            WriteDataConfigsAttribute(writer, references, allConfigs);
                            writer.Write(">");

                            writer.Write("<b>");
                            writer.Write(sameLineReferencesGroup.LineNumber);
                            writer.Write("</b>");
                            MergeOccurrences(writer, references);
                            writer.Write("</a>");
                            writer.WriteLine();
                        }

                        writer.Write("</div>");
                        writer.WriteLine();
                    }

                    writer.Write("</div>");
                    writer.WriteLine();
                }
            }

            Write(writer, "</body></html>");
        }

        // Emits data-configs="a,b" when this line's references don't cover every registered config --
        // i.e. this occurrence is config-specific (e.g. only compiled under "windows"). Omitted entirely
        // for the ordinary single-config path (allConfigs null) and for the common case where a
        // reference is present under every config, so today's single-config byte-for-byte output is
        // unaffected.
        private static void WriteDataConfigsAttribute(TextWriter writer, List<Reference> referencesOnTheSameLine, IReadOnlyCollection<string> allConfigs)
        {
            if (allConfigs == null || allConfigs.Count == 0)
            {
                return;
            }

            HashSet<string> union = null;
            foreach (var reference in referencesOnTheSameLine)
            {
                if (reference.ConfigSet == null)
                {
                    // At least one occurrence on this line has no config data at all -- treat the whole
                    // line as unconditional/shared rather than tagging a partial picture.
                    return;
                }

                union ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                union.UnionWith(reference.ConfigSet);
            }

            if (union == null || allConfigs.All(union.Contains))
            {
                // Fully shared across every registered config -- inert, matches the untagged single-config
                // rendering.
                return;
            }

            writer.Write(" data-configs=\"");
            writer.Write(string.Join(",", union.OrderBy(c => c, StringComparer.OrdinalIgnoreCase)));
            writer.Write("\"");
        }

        private void WriteImplementedInterfaceMembers(ulong symbolId, TextWriter writer)
        {
            if (!ImplementedInterfaceMembers.TryGetValue(symbolId, out HashSet<Tuple<string, ulong>> implementedInterfaceMembers))
            {
                return;
            }

            Write(writer, string.Format(@"<div class=""rH"">Implemented interface member{0}:</div>", implementedInterfaceMembers.Count > 1 ? "s" : ""));

            foreach (var implementedInterfaceMember in implementedInterfaceMembers)
            {
                var assemblyName = implementedInterfaceMember.Item1;
                var interfaceSymbolId = implementedInterfaceMember.Item2;

                if (!this.SolutionFinalizer.assemblyNameToProjectMap.TryGetValue(assemblyName, out ProjectFinalizer baseProject))
                {
                    return;
                }

                if (baseProject.DeclaredSymbols.TryGetValue(interfaceSymbolId, out DeclaredSymbolInfo symbol))
                {
                    var sb = new StringBuilder();
                    Markup.WriteSymbol(symbol, sb);
                    writer.Write(sb.ToString());
                }
            }
        }

        private void WriteBaseMember(ulong symbolId, TextWriter writer)
        {
            if (!BaseMembers.TryGetValue(symbolId, out Tuple<string, ulong> baseMemberLink))
            {
                return;
            }

            Write(writer, @"<div class=""rH"">Base:</div>");

            var assemblyName = baseMemberLink.Item1;
            var baseSymbolId = baseMemberLink.Item2;

            if (!this.SolutionFinalizer.assemblyNameToProjectMap.TryGetValue(assemblyName, out ProjectFinalizer baseProject))
            {
                return;
            }

            if (baseProject.DeclaredSymbols.TryGetValue(baseSymbolId, out DeclaredSymbolInfo symbol))
            {
                var sb = new StringBuilder();
                Markup.WriteSymbol(symbol, sb);
                writer.Write(sb.ToString());
            }
        }

        // Materialized reference tree: built once in a single pass over the raw reference
        // lines with per-level counts precomputed. This replaces the previous lazy nested
        // GroupBy chain, which re-executed the grouping on every re-enumeration and required
        // separate CountItems passes that fully re-walked each subtree.
        private sealed class ReferenceKindGroup
        {
            public ReferenceKind Kind;
            public int Count;
            public readonly List<ReferenceAssemblyGroup> Assemblies = new List<ReferenceAssemblyGroup>();
            public readonly Dictionary<string, ReferenceAssemblyGroup> AssemblyMap = new Dictionary<string, ReferenceAssemblyGroup>();
        }

        private sealed class ReferenceAssemblyGroup
        {
            public string AssemblyName;
            public int Count;
            public readonly List<ReferenceFileGroup> Files = new List<ReferenceFileGroup>();
            public readonly Dictionary<string, ReferenceFileGroup> FileMap = new Dictionary<string, ReferenceFileGroup>();
        }

        private sealed class ReferenceFileGroup
        {
            public string FilePath;
            public int Count;
            public readonly List<ReferenceLineGroup> Lines = new List<ReferenceLineGroup>();
            public readonly Dictionary<int, ReferenceLineGroup> LineMap = new Dictionary<int, ReferenceLineGroup>();
        }

        private sealed class ReferenceLineGroup
        {
            public int LineNumber;
            public readonly List<Reference> References = new List<Reference>();
        }

        private static List<ReferenceKindGroup> CreateReferences(
            string[] referencesLines,
            out string referencedSymbolName)
        {
            var references = new List<Reference>(referencesLines.Length / 2);
            for (int i = 0; i < referencesLines.Length; i += 2)
            {
                references.Add(new Reference(referencesLines[i], referencesLines[i + 1]));
            }

            return CreateReferences(references, out referencedSymbolName);
        }

        // Shared grouping core: builds the same kind/assembly/file/line hierarchy regardless of whether
        // the Reference objects were just parsed from raw shard lines (the ordinary path, ConfigSet
        // always null) or came pre-built from ConfigReferenceMerger's merged set (the config-aware FAR
        // path, ConfigSet populated) -- see GenerateMergedReferencesFragment.
        private static List<ReferenceKindGroup> CreateReferences(
            IEnumerable<Reference> references,
            out string referencedSymbolName)
        {
            referencedSymbolName = null;

            var kindGroups = new List<ReferenceKindGroup>();
            var kindMap = new Dictionary<ReferenceKind, ReferenceKindGroup>();

            foreach (var reference in references)
            {
                if (referencedSymbolName == null &&
                    reference.ToSymbolName != "this" &&
                    reference.ToSymbolName != "base" &&
                    reference.ToSymbolName != "var" &&
                    reference.ToSymbolName != "UsingTask" &&
                    reference.ToSymbolName != "[")
                {
                    referencedSymbolName = reference.ToSymbolName;
                }

                if (!kindMap.TryGetValue(reference.Kind, out var kindGroup))
                {
                    kindGroup = new ReferenceKindGroup { Kind = reference.Kind };
                    kindMap.Add(reference.Kind, kindGroup);
                    kindGroups.Add(kindGroup);
                }

                kindGroup.Count++;

                if (!kindGroup.AssemblyMap.TryGetValue(reference.FromAssemblyId, out var assemblyGroup))
                {
                    assemblyGroup = new ReferenceAssemblyGroup { AssemblyName = reference.FromAssemblyId };
                    kindGroup.AssemblyMap.Add(reference.FromAssemblyId, assemblyGroup);
                    kindGroup.Assemblies.Add(assemblyGroup);
                }

                assemblyGroup.Count++;

                if (!assemblyGroup.FileMap.TryGetValue(reference.FromLocalPath, out var fileGroup))
                {
                    fileGroup = new ReferenceFileGroup { FilePath = reference.FromLocalPath };
                    assemblyGroup.FileMap.Add(reference.FromLocalPath, fileGroup);
                    assemblyGroup.Files.Add(fileGroup);
                }

                fileGroup.Count++;

                if (!fileGroup.LineMap.TryGetValue(reference.ReferenceLineNumber, out var lineGroup))
                {
                    lineGroup = new ReferenceLineGroup { LineNumber = reference.ReferenceLineNumber };
                    fileGroup.LineMap.Add(reference.ReferenceLineNumber, lineGroup);
                    fileGroup.Lines.Add(lineGroup);
                }

                lineGroup.References.Add(reference);
            }

            return kindGroups;
        }

        private static void MergeOccurrences(TextWriter writer, IEnumerable<Reference> referencesOnTheSameLine)
        {
            var text = referencesOnTheSameLine.First().ReferenceLineText;
            referencesOnTheSameLine = referencesOnTheSameLine.OrderBy(r => r.ReferenceColumnStart);
            int current = 0;
            foreach (var occurrence in referencesOnTheSameLine)
            {
                if (occurrence.ReferenceColumnStart < 0 ||
                    occurrence.ReferenceColumnStart >= text.Length ||
                    occurrence.ReferenceColumnEnd <= occurrence.ReferenceColumnStart)
                {
                    string message = "occurrence.ReferenceColumnStart = " + occurrence.ReferenceColumnStart;
                    message += "\r\noccurrence.ReferenceColumnEnd = " + occurrence.ReferenceColumnEnd;
                    message += "\r\ntext = " + text;
                    Log.Exception("MergeOccurrences1: " + message);
                }

                if (occurrence.ReferenceColumnStart > current)
                {
                    if (current < 0 ||
                        current >= text.Length ||
                        occurrence.ReferenceColumnStart < current ||
                        occurrence.ReferenceColumnStart >= text.Length)
                    {
                        string message = "occurrence.ReferenceColumnStart = " + occurrence.ReferenceColumnStart;
                        message += "\r\noccurrence.ReferenceColumnEnd = " + occurrence.ReferenceColumnEnd;
                        message += "\r\ntext = " + text;
                        message += "\r\ncurrent = " + current;
                        Log.Exception("MergeOccurrences2: " + message);
                    }
                    else
                    {
                        Write(writer, text.Substring(current, occurrence.ReferenceColumnStart - current));
                    }
                }

                Write(writer, "<i>");
                Write(writer, text.Substring(occurrence.ReferenceColumnStart, occurrence.ReferenceColumnEnd - occurrence.ReferenceColumnStart));
                Write(writer, "</i>");
                current = occurrence.ReferenceColumnEnd;
            }

            if (current < text.Length)
            {
                Write(writer, text.Substring(current, text.Length - current));
            }
        }

        private static void Write(TextWriter sw, string text)
        {
            sw.Write(text);
        }

        // Appends per-symbol reference fragments into a single pack file for an assembly and records each
        // fragment's byte range in a companion index. Fragments are appended sequentially by a single
        // caller, so no locking is required; the parallelism happens in fragment generation upstream.
        //
        // Pack file:  the raw fragment bytes concatenated back-to-back (each is a complete .html body,
        //             preamble and all, so the server can return them verbatim).
        // Index file: int32 record count, then per record a length-prefixed symbol-id string (the request
        //             file name), int64 offset, int32 length. Ids are usually 16-char hex hashes, but the
        //             GUID assembly uses full 36-char guid strings, so the length must not be assumed.
        private sealed class ReferencePackBuilder
        {
            private readonly string _referencesFolder;
            private readonly List<(string Id, long Offset, int Length)> _records = new List<(string, long, int)>();
            private FileStream _packStream;
            private long _offset;

            public ReferencePackBuilder(string referencesFolder)
            {
                _referencesFolder = referencesFolder;
            }

            public void Add(string symbolId, byte[] fragment)
            {
                if (_packStream == null)
                {
                    Directory.CreateDirectory(_referencesFolder);
                    var packPath = Path.Combine(_referencesFolder, Constants.ReferencePackFileName);
                    _packStream = new FileStream(packPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20, FileOptions.SequentialScan);
                }

                _packStream.Write(fragment, 0, fragment.Length);
                _records.Add((symbolId, _offset, fragment.Length));
                _offset += fragment.Length;
            }

            public void Complete()
            {
                if (_packStream == null)
                {
                    // Nothing was written, so leave no pack or index behind.
                    return;
                }

                _packStream.Dispose();

                var indexPath = Path.Combine(_referencesFolder, Constants.ReferenceIndexFileName);
                using (var stream = new FileStream(indexPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20, FileOptions.SequentialScan))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(_records.Count);

                    foreach (var (id, offset, length) in _records)
                    {
                        writer.Write(id);
                        writer.Write(offset);
                        writer.Write(length);
                    }
                }
            }
        }
    }
}
