using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    /// <summary>
    /// Non-destructive readers for the two raw Pass1 artifacts a cross-config merge needs: the
    /// per-project declaration-location map and reference shards. Both formats already exist today --
    /// <see cref="ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap"/> and
    /// <see cref="ProjectGenerator.GenerateReferencesDataFilesToAssembly"/> write them -- and Pass2's
    /// ordinary single-config finalizer already has readers for them
    /// (<c>ProjectFinalizer.ReadSymbolIDToListOfLocationsMap</c>, <c>GenerateReferencesFilesFromShard</c>),
    /// but those consume (delete) the file as part of reading it, because in the single-config path
    /// nothing else will ever need it again. A config-mode merge must be able to read the SAME files
    /// from obj/&lt;config&gt; potentially more than once (e.g. every /config: run's auto-tail merge,
    /// plus a later standalone /mergeConfigsOnly invocation) without destroying them, so this class
    /// duplicates only the minimal parse loop -- reading with ordinary FileShare.Read, no
    /// FileOptions.DeleteOnClose -- rather than the surrounding consume-and-delete orchestration.
    /// </summary>
    public static class ConfigDataReader
    {
        /// <summary>
        /// Reads a DeclarationMap.txt (format: "=" + symbolId header lines, then "path;offset" lines)
        /// into the same shape <see cref="ConfigLocationMerger.Merge"/> expects. Returns an empty map
        /// if the file doesn't exist (a project with no declarations for this config).
        /// </summary>
        public static Dictionary<string, List<Tuple<string, long>>> ReadDeclarationMap(string declarationMapFile)
        {
            var result = new Dictionary<string, List<Tuple<string, long>>>();
            if (!File.Exists(declarationMapFile))
            {
                return result;
            }

            var lines = File.ReadAllLines(declarationMapFile);

            List<Tuple<string, long>> bucket = null;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("=", StringComparison.Ordinal))
                {
                    var symbolId = line.Substring(1);
                    bucket = new List<Tuple<string, long>>();
                    result[symbolId] = bucket;
                }
                else if (!string.IsNullOrWhiteSpace(line) && bucket != null)
                {
                    var parts = line.Split(';');
                    var streamOffset = long.Parse(parts[1]);
                    bucket.Add(Tuple.Create(parts[0], streamOffset));
                }
            }

            return result;
        }

        /// <summary>
        /// Reads every reference shard (<see cref="ProjectGenerator.ReferenceShardPrefix"/>*<see
        /// cref="ProjectGenerator.ReferenceShardExtension"/>) under a project's references data folder
        /// into the same shape <see cref="ConfigReferenceMerger.Merge"/> expects: target symbolId -> the
        /// list of <see cref="Reference"/> records to it. Each shard record is the symbol id line
        /// followed by the two lines <see cref="Reference.WriteTo"/> emits -- the exact format <see
        /// cref="Reference(string, string)"/> already parses, reused here unchanged. Returns an empty
        /// map if the references folder doesn't exist (a project with no references for this config).
        /// </summary>
        public static Dictionary<string, List<Reference>> ReadReferenceShards(string referencesFolder)
        {
            var result = new Dictionary<string, List<Reference>>(StringComparer.Ordinal);
            if (!Directory.Exists(referencesFolder))
            {
                return result;
            }

            var shardFiles = Directory.GetFiles(
                referencesFolder,
                ProjectGenerator.ReferenceShardPrefix + "*" + ProjectGenerator.ReferenceShardExtension);

            foreach (var shardFile in shardFiles)
            {
                using (var stream = new FileStream(shardFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, FileOptions.SequentialScan))
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

                        if (!result.TryGetValue(symbolId, out var references))
                        {
                            references = new List<Reference>();
                            result.Add(symbolId, references);
                        }

                        references.Add(new Reference(separatedLine, sourceLine));
                    }
                }
            }

            return result;
        }
    }
}
