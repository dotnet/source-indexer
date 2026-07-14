using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    /// <summary>
    /// Dedupes N configs' independently Pass1-rendered HTML for the same logical file path into as few
    /// distinct physical variants as the actual content requires -- the file-output half of the
    /// config-as-facet merge (alongside <see cref="ConfigLocationMerger"/> for declarations and
    /// <see cref="ConfigReferenceMerger"/> for references).
    ///
    /// Deliberately hashes the RENDERED HTML, not the source: Roslyn's classifier marks a "#if"-excluded
    /// region as inactive/undecorated per config, so byte-identical SOURCE can render to different HTML
    /// depending which branch is active for that config (different active/inactive styling, different
    /// hyperlinks). Source-hash dedup would therefore either miss real divergence or require a second,
    /// separate divergence check on top -- rendered-content hashing is the single check that's actually
    /// correct.
    ///
    /// This single mechanism naturally covers all three conceptual buckets from the design note without
    /// needing separate cases:
    ///   - A file only compiled under one config (e.g. Environment.Windows.cs isn't part of linux's
    ///     project at all) simply never has an entry for the other configs -- one variant, tagged to
    ///     just that config.
    ///   - A shared "#if"-gated file whose active region happens to resolve the same way everywhere
    ///     hashes identically across all configs -- one variant, tagged with all of them.
    ///   - A shared file that genuinely renders differently per config produces as many variants as
    ///     there are distinct hashes (often just 2, even across many configs, since configs sharing the
    ///     same active branch converge to the same hash).
    /// </summary>
    public static class ConfigFileDeduper
    {
        public sealed class FileVariant
        {
            public string ContentHash { get; }
            public string Content { get; }
            public HashSet<string> Configs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// The physical file path this variant should be written to. Assigned by
            /// <see cref="AssignPhysicalPaths"/>; null until then.
            /// </summary>
            public string PhysicalPath { get; set; }

            public FileVariant(string contentHash, string content, string initialConfig)
            {
                ContentHash = contentHash;
                Content = content;
                Configs.Add(initialConfig);
            }
        }

        /// <param name="perConfigRenderedContent">
        /// configName -> this config's fully-rendered HTML for one logical project-relative file path.
        /// A config that doesn't compile this file at all simply has no entry.
        /// </param>
        public static List<FileVariant> Dedupe(IReadOnlyDictionary<string, string> perConfigRenderedContent)
        {
            if (perConfigRenderedContent == null)
            {
                throw new ArgumentNullException(nameof(perConfigRenderedContent));
            }

            var variantsByHash = new Dictionary<string, FileVariant>(StringComparer.Ordinal);
            var result = new List<FileVariant>();

            // Stable order so a 0/1-config call is deterministic and, combined with AssignPhysicalPaths
            // below, byte-identical to today's single-config output.
            foreach (var configName in perConfigRenderedContent.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var content = perConfigRenderedContent[configName];
                var hash = Paths.GetMD5Hash(content, 32);

                if (variantsByHash.TryGetValue(hash, out var variant))
                {
                    variant.Configs.Add(configName);
                }
                else
                {
                    variant = new FileVariant(hash, content, configName);
                    variantsByHash.Add(hash, variant);
                    result.Add(variant);
                }
            }

            return result;
        }

        /// <summary>
        /// Assigns the physical file path each variant should be written to. When there is exactly one
        /// variant (the fully-shared or single-config case), it keeps <paramref name="originalPath"/>
        /// unchanged -- the explicit back-compat requirement. When a file genuinely diverges into
        /// multiple variants, one keeps the original path and the rest get a short hash suffix, so
        /// existing links into the "original" rendering keep working and only the divergent alternates
        /// need a distinct name.
        /// </summary>
        /// <param name="preferredConfig">
        /// Optional. When given and some variant's <see cref="FileVariant.Configs"/> contains it, THAT
        /// variant keeps <paramref name="originalPath"/>, regardless of hash order. This matters because
        /// a caller like <see cref="ConfigAwareProjectFinalizer"/> may already have the primary config's
        /// rendering staged at <paramref name="originalPath"/> from an earlier, unconditional copy step
        /// -- picking any other variant for that path would silently swap out content that was already
        /// correctly there. Null (the default) preserves the original hash-order tie-break, unchanged
        /// for every existing caller/test.
        /// </param>
        public static void AssignPhysicalPaths(string originalPath, IReadOnlyList<FileVariant> variants, string preferredConfig = null)
        {
            if (variants == null || variants.Count == 0)
            {
                return;
            }

            if (variants.Count == 1)
            {
                variants[0].PhysicalPath = originalPath;
                return;
            }

            var ordered = variants.OrderBy(v => v.ContentHash, StringComparer.Ordinal).ToList();

            if (preferredConfig != null)
            {
                var preferredIndex = ordered.FindIndex(v => v.Configs.Contains(preferredConfig));
                if (preferredIndex > 0)
                {
                    var preferred = ordered[preferredIndex];
                    ordered.RemoveAt(preferredIndex);
                    ordered.Insert(0, preferred);
                }
            }

            var extension = System.IO.Path.GetExtension(originalPath);
            var withoutExtension = Paths.StripExtension(originalPath);

            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].PhysicalPath = i == 0
                    ? originalPath
                    : withoutExtension + "~" + ordered[i].ContentHash.Substring(0, 8) + extension;
            }
        }

        /// <summary>
        /// True when a file's rendering is identical across every indexed config -- the common case,
        /// where no config-specific variant selection is needed at all for this file.
        /// </summary>
        public static bool IsFullyShared(IReadOnlyList<FileVariant> variants, IReadOnlyCollection<string> allConfigs)
        {
            if (variants == null || variants.Count != 1)
            {
                return false;
            }

            return allConfigs.All(c => variants[0].Configs.Contains(c));
        }
    }
}
