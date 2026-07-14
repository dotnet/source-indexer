using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    /// <summary>
    /// Merges N configs' independently-generated reference records for the same target symbol into a
    /// single config-tagged list -- the reference-side counterpart to <see cref="ConfigLocationMerger"/>.
    /// This is what makes Find All References unified-but-filterable per the approved design: a
    /// reference that resolves identically under every config collapses into one entry tagged with all
    /// of them (so it always shows, unaffected by which config is selected), while a reference that
    /// only exists under a subset of configs (e.g. a call site inside a "#if WINDOWS" block) keeps its
    /// own entry tagged with just that subset (so the client can grey/hide it when a non-matching
    /// config is selected).
    ///
    /// Two configs' shards can only ever disagree about a reference's <em>presence</em>, not its
    /// resolution target: <see cref="SymbolIdService.GetId"/> is config-independent, so a reference
    /// that compiles at all always resolves to the same target symbol ID regardless of which config
    /// compiled it. What differs per config is only whether the referencing call site was compiled in
    /// the first place (Roslyn simply never emits a reference for code inside an inactive #if branch).
    /// </summary>
    public static class ConfigReferenceMerger
    {
        /// <param name="perConfigReferences">
        /// configName -> (target symbolId -> list of references), one entry per config that was
        /// independently Pass1-generated for this project/assembly.
        /// </param>
        public static Dictionary<string, List<Reference>> Merge(
            IReadOnlyDictionary<string, Dictionary<string, List<Reference>>> perConfigReferences)
        {
            if (perConfigReferences == null)
            {
                throw new ArgumentNullException(nameof(perConfigReferences));
            }

            var merged = new Dictionary<string, List<Reference>>(StringComparer.Ordinal);

            // Stable order so a 0/1-config call produces the same reference order the single-config
            // path already does -- required for byte-identical back-compat output.
            foreach (var configName in perConfigReferences.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var referencesBySymbol = perConfigReferences[configName];

                foreach (var kvp in referencesBySymbol)
                {
                    var targetSymbolId = kvp.Key;

                    if (!merged.TryGetValue(targetSymbolId, out var mergedReferences))
                    {
                        mergedReferences = new List<Reference>();
                        merged.Add(targetSymbolId, mergedReferences);
                    }

                    foreach (var reference in kvp.Value)
                    {
                        var existing = mergedReferences.FirstOrDefault(r => r.HasSameOccurrenceAs(reference));
                        if (existing != null)
                        {
                            existing.ConfigSet ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            existing.ConfigSet.Add(configName);
                        }
                        else
                        {
                            reference.ConfigSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { configName };
                            mergedReferences.Add(reference);
                        }
                    }
                }
            }

            return merged;
        }

        /// <summary>
        /// True when every reference in the list carries the full config set, i.e. this symbol's
        /// reference set is identical everywhere -- the common case, where ConfigSet is inert metadata
        /// and Find All References needs no config-specific filtering at all for this symbol.
        /// </summary>
        public static bool IsFullyShared(IReadOnlyList<Reference> references, IReadOnlyCollection<string> allConfigs)
        {
            if (references == null || references.Count == 0)
            {
                return false;
            }

            return references.All(r => r.ConfigSet != null && allConfigs.All(c => r.ConfigSet.Contains(c)));
        }
    }
}
