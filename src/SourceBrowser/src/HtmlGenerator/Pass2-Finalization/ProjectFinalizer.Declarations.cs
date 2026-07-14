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
        private void BackpatchUnreferencedDeclarations(string referencesFolder)
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
            GetLocationsToPatch(referencesFolder, locationsToPatch, symbolIDToListOfLocationsMap);
            Patch(locationsToPatch);

            // The map is a Pass1 intermediate consumed only here, so drop it rather than leaving tens
            // of MB per assembly in the served output where it would also be directly downloadable.
            File.Delete(declarationMapFile);
        }

        private void GetLocationsToPatch(string referencesFolder, Dictionary<string, List<long>> locationsToPatch, Dictionary<string, List<Tuple<string, long>>> symbolIDToListOfLocationsMap)
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
            var result = new Dictionary<string, List<Tuple<string, long>>>();

            var lines = File.ReadAllLines(declarationMapFile);

            List<Tuple<string, long>> bucket = null;
            string symbolId = null;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("=", StringComparison.Ordinal))
                {
                    symbolId = line.Substring(1);
                    bucket = new List<Tuple<string, long>>();
                    result.Add(symbolId, bucket);
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    var parts = line.Split(';');
                    var streamOffset = long.Parse(parts[1]);
                    var tuple = Tuple.Create(parts[0], streamOffset);
                    bucket.Add(tuple);
                }
            }

            return result;
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
