using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class ProjectFinalizer
    {
        private void BackpatchUnreferencedDeclarations(string referencesFolder, HashSet<string> additionalReferencedSymbolIds = null)
        {
            string declarationMapFile = Path.Combine(ProjectDestinationFolder, Constants.DeclarationMap + ".txt");
            if (!File.Exists(declarationMapFile))
            {
                return;
            }

            Log.Write("Backpatching unreferenced declarations in " + this.AssemblyId);

            var symbolIDToListOfLocationsMap = ReadSymbolIDToListOfLocationsMap(declarationMapFile);

            ProjectGenerator.GenerateRedirectFile(
                this.SolutionFinalizer.SolutionDestinationFolder,
                this.ProjectDestinationFolder,
                symbolIDToListOfLocationsMap.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Select(t => t.Item1.Replace('\\', '/'))));

            var locationsToPatch = new Dictionary<string, List<long>>();
            GetLocationsToPatch(referencesFolder, locationsToPatch, symbolIDToListOfLocationsMap, additionalReferencedSymbolIds);
            Patch(locationsToPatch);

            // The map is a Pass1 intermediate consumed only here, so drop it rather than leaving tens
            // of MB per assembly in the served output where it would also be directly downloadable.
            File.Delete(declarationMapFile);
        }

        private void GetLocationsToPatch(
            string referencesFolder,
            Dictionary<string, List<long>> locationsToPatch,
            Dictionary<string, List<Tuple<string, long>>> symbolIDToListOfLocationsMap,
            HashSet<string> additionalReferencedSymbolIds = null)
        {
            // A symbol needs backpatching only when it has no references file. Reference data is now
            // sharded into a handful of files per assembly rather than one file per symbol, so scan the
            // shard records (symbol id is every third line) to build the set of symbols that do have
            // references. This runs before GenerateFinalReferencesFiles consumes the shards, so they still
            // exist here. Symbols with only a base member or implemented interface member link don't appear
            // in the shards but still get a references file (see GenerateBaseAndInterfaceOnlyReferencesFiles),
            // so union those in as well to avoid zeroing out declarations that link to a real page.
            var symbolsWithReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(referencesFolder))
            {
                foreach (var shardFile in Directory.EnumerateFiles(
                    referencesFolder,
                    ProjectGenerator.ReferenceShardPrefix + "*" + ProjectGenerator.ReferenceShardExtension))
                {
                    using (var reader = new StreamReader(shardFile, System.Text.Encoding.UTF8))
                    {
                        string symbolId;
                        while ((symbolId = reader.ReadLine()) != null)
                        {
                            symbolsWithReferences.Add(symbolId);

                            // Skip the two payload lines of the record.
                            if (reader.ReadLine() == null || reader.ReadLine() == null)
                            {
                                break;
                            }
                        }
                    }
                }
            }

            foreach (var id in BaseMembers.Keys)
            {
                symbolsWithReferences.Add(Serialization.ULongToHexString(id));
            }
            foreach (var id in ImplementedInterfaceMembers.Keys)
            {
                symbolsWithReferences.Add(Serialization.ULongToHexString(id));
            }

            // Config-aware merge: symbols referenced only under a non-primary config are known here
            // in-memory (from the merged reference set), without being written into any *_r*.dat shard --
            // so they influence only this grey/no-grey decision and never bleed into the FAR render below
            // (GenerateFinalReferencesFiles globs that exact same shard pattern and would otherwise render
            // them untagged as if they were unconditional primary-config references).
            if (additionalReferencedSymbolIds != null)
            {
                symbolsWithReferences.UnionWith(additionalReferencedSymbolIds);
            }

            foreach (var kvp in symbolIDToListOfLocationsMap)
            {
                var symbolId = kvp.Key;
                if (!symbolsWithReferences.Contains(symbolId))
                {
                    foreach (var location in kvp.Value)
                    {
                        if (location.Item2 != 0)
                        {
                            var filePath = Path.Combine(ProjectDestinationFolder, location.Item1 + ".html");
                            AddLocationToPatch(locationsToPatch, filePath, location.Item2);
                        }
                    }
                }
            }
        }

        private static void Patch(Dictionary<string, List<long>> locationsToPatch)
        {
            byte[] zeroId = SymbolIdService.ZeroId;
            int zeroIdLength = zeroId.Length;
            Parallel.ForEach(locationsToPatch,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                kvp =>
                {
                    kvp.Value.Sort();

                    using (var stream = new FileStream(kvp.Key, FileMode.Open, FileAccess.ReadWrite))
                    {
                        foreach (var offset in kvp.Value)
                        {
                            stream.Seek(offset, SeekOrigin.Begin);
                            stream.Write(zeroId, 0, zeroIdLength);
                        }
                    }
                });
        }

        private Dictionary<string, List<Tuple<string, long>>> ReadSymbolIDToListOfLocationsMap(string declarationMapFile)
        {
            // Shared with the config-mode merge step's non-destructive reader (ConfigDataReader), which
            // needs the identical parse but without this method's caller deleting the file afterward.
            return ConfigDataReader.ReadDeclarationMap(declarationMapFile);
        }

        private void AddLocationToPatch(Dictionary<string, List<long>> locationsToPatch, string filePath, long offset)
        {
            if (!locationsToPatch.TryGetValue(filePath, out List<long> offsets))
            {
                offsets = new List<long>();
                locationsToPatch.Add(filePath, offsets);
            }

            offsets.Add(offset);
        }
    }
}
