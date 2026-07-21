using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    /// <summary>
    /// A single declaration/redirect location for a symbol ID, tagged with the set of configs (e.g.
    /// "windows"/"linux"/"mac" builds of the same sources) under which it applies. This is the same
    /// shape SourceBrowser already uses for partial types/members declared in multiple files
    /// (<see cref="ProjectGenerator.MetadataToSourceRedirect"/> / <see cref="Markup.GeneratePartialTypeDisambiguationFile"/>)
    /// -- config is just an additional tag on a location, not a parallel per-config scheme. A symbol
    /// that is declared identically under every indexed config collapses to a single location whose
    /// <see cref="Configs"/> set contains all of them; a symbol declared in different files per config
    /// (e.g. Environment.Windows.cs vs Environment.Unix.cs) keeps one location per distinct file, each
    /// tagged with only the configs that declare it there.
    /// </summary>
    public sealed class ConfigTaggedLocation
    {
        public string FilePath { get; }
        public long Offset { get; }
        public HashSet<string> Configs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ConfigTaggedLocation(string filePath, long offset, string initialConfig)
        {
            FilePath = filePath;
            Offset = offset;
            if (initialConfig != null)
            {
                Configs.Add(initialConfig);
            }
        }
    }

    /// <summary>
    /// Merges N configs' independently-generated Pass1 declaration-location maps
    /// (<see cref="ProjectGenerator.SymbolIDToListOfLocationsMap"/>-shaped: symbolId -> list of
    /// (project-relative file path, byte offset)) for the SAME project/assembly into a single
    /// config-tagged map. This is the "symbol locations" half of the config-as-facet merge step;
    /// see <see cref="Reference"/>/reference-shard merging for the other half.
    ///
    /// Each config is Pass1-generated independently (Roslyn only compiles the #if branch active for
    /// that config, so this step can't be skipped), but the merge here is what turns those N separate
    /// per-config outputs into ONE served index instead of N partitioned ones.
    /// </summary>
    public static class ConfigLocationMerger
    {
        /// <param name="perConfigDeclarationMaps">
        /// configName -> (symbolId -> list of (project-relative file path, byte offset)), one entry
        /// per config that was independently Pass1-generated for this project/assembly.
        /// </param>
        public static Dictionary<string, List<ConfigTaggedLocation>> Merge(
            IReadOnlyDictionary<string, Dictionary<string, List<Tuple<string, long>>>> perConfigDeclarationMaps)
        {
            if (perConfigDeclarationMaps == null)
            {
                throw new ArgumentNullException(nameof(perConfigDeclarationMaps));
            }

            var merged = new Dictionary<string, List<ConfigTaggedLocation>>(StringComparer.Ordinal);

            // Iterate configs in a stable order so that, for a fully back-compat (0 or 1 config) call,
            // the resulting location order matches exactly what the single-config path already
            // produces -- byte-identical output is the explicit requirement for that case.
            foreach (var configName in perConfigDeclarationMaps.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var declarationMap = perConfigDeclarationMaps[configName];

                foreach (var kvp in declarationMap)
                {
                    var symbolId = kvp.Key;

                    if (!merged.TryGetValue(symbolId, out var mergedLocations))
                    {
                        mergedLocations = new List<ConfigTaggedLocation>();
                        merged.Add(symbolId, mergedLocations);
                    }

                    foreach (var location in kvp.Value)
                    {
                        var filePath = location.Item1;
                        var offset = location.Item2;

                        // Same physical file + same byte offset under two configs means the same
                        // declaration site rendered identically by both -- the common case for
                        // platform-agnostic code -- so union the config onto the existing entry
                        // rather than duplicating the location.
                        var existing = mergedLocations.FirstOrDefault(l => l.Offset == offset && string.Equals(l.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.Configs.Add(configName);
                        }
                        else
                        {
                            mergedLocations.Add(new ConfigTaggedLocation(filePath, offset, configName));
                        }
                    }
                }
            }

            return merged;
        }

        /// <summary>
        /// True when every location for a symbol carries the full config set, i.e. the symbol is
        /// declared identically everywhere -- the common case, where the config tag is inert metadata
        /// and the disambiguation-page/redirect behavior should be unaffected by configs entirely.
        /// </summary>
        public static bool IsFullyShared(IReadOnlyList<ConfigTaggedLocation> locations, IReadOnlyCollection<string> allConfigs)
        {
            if (locations == null || locations.Count != 1)
            {
                return false;
            }

            return allConfigs.All(c => locations[0].Configs.Contains(c));
        }
    }
}
