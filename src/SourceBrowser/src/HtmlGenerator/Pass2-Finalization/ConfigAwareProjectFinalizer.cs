using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    /// <summary>
    /// The finalizer used once two or more configs are registered against a shared /out (see
    /// <see cref="ConfigMergeCoordinator"/>/<c>Program.RunConfigMergeIfNeeded</c>). With exactly one
    /// config, that method uses the ordinary <see cref="SolutionFinalizer"/> directly, which stays
    /// byte-identical to the no-/config path. This type is what's genuinely new for 2+ configs.
    ///
    /// ROOT CAUSE, and why this type keeps growing: step 2 below runs the ordinary, unmodified
    /// <see cref="SolutionFinalizer"/> pipeline against ONLY the primary config's data. EVERY output
    /// that pipeline produces which depends on the full cross-project/cross-symbol picture (not just
    /// one project examined in isolation) is therefore primary-config-biased and needs an explicit
    /// correction here -- this is not a closed list by construction, it's whatever
    /// <c>SolutionFinalizer.FinalizeProjects</c>/<c>WebsiteFinalizer.Finalize</c> happen to emit today.
    /// Known outputs and their status:
    /// <list type="bullet">
    /// <item>Per-project "Used By" HTML block (<see cref="Constants.ReferencedAssemblyList"/>-driven) --
    /// FIXED, step 3/<see cref="PatchCrossConfigUsedByAndAggregates"/>.</item>
    /// <item><see cref="Constants.TopReferencedAssemblies"/> and the referencing-assembly counts in
    /// <see cref="Constants.MasterAssemblyMap"/> (<c>SolutionFinalizer.CreateProjectMap</c>) -- same
    /// edge data as Used-By, same bias, FIXED alongside it in
    /// <see cref="PatchCrossConfigUsedByAndAggregates"/>.</item>
    /// <item>Per-symbol unreferenced-declaration backpatch (<c>ProjectFinalizer.Declarations.cs</c>'s
    /// <c>BackpatchUnreferencedDeclarations</c>, driven by per-symbol <see cref="Reference"/> records,
    /// NOT the assembly graph) -- FIXED, step 1/
    /// <see cref="StagePrimaryConfigAndComputeAdditionalReferencedSymbolIds"/>, by computing the extra
    /// referenced-symbol-ID set in memory and threading it into
    /// <c>SolutionFinalizer.FinalizeProjects</c>'s <c>additionalReferencedSymbolIdsByAssembly</c>
    /// parameter, which only affects the backpatch decision -- see below for why this data never
    /// touches disk as a reference shard.</item>
    /// <item>Rendered "Find All References" HTML fragments (<c>ProjectFinalizer.References.cs</c>'s
    /// <c>GenerateReferencesFilesFromShard</c>) -- an earlier version of the backpatch fix above fixed
    /// it by injecting an extra on-disk reference shard, which this renderer also globbed and consumed
    /// (same <c>_r*.dat</c> pattern, no config-tagging mechanism, so the injected cross-config-only
    /// record rendered into the FAR list indistinguishable from a primary-config reference). That
    /// injection was removed (kept in memory instead, see the backpatch item above), which left every
    /// symbol's FAR list exactly its primary-config references, full stop -- no partial or inconsistent
    /// union, but also no cross-config data at all. FIXED for real by this item: <see
    /// cref="ProjectFinalizer.GenerateMergedReferencesFragments"/> appends one re-rendered fragment per
    /// symbol whose merged reference set is NOT <see cref="ConfigReferenceMerger.IsFullyShared"/>,
    /// straight from <see cref="ConfigReferenceMerger"/>'s merged, config-tagged <see cref="Reference"/>
    /// objects (via a new <c>WriteReferencesContent</c> overload that renders from already-built
    /// <see cref="Reference"/> objects instead of parsing raw shard lines), each occurrence carrying a
    /// <c>data-configs="..."</c> attribute when its own <see cref="Reference.ConfigSet"/> doesn't cover
    /// every config this project exists under (a "mixed" symbol -- referenced under both the primary
    /// and another config -- keeps its shared occurrences untagged and its divergent ones tagged,
    /// rather than losing the divergent ones or tagging everything). This REPLACES, not augments, the
    /// ordinary shard-based fragment for that symbol: <see cref="ProjectFinalizer.ReferencePackBuilder"/>'s
    /// on-disk index is last-write-wins (a later <c>Add</c> for the same symbol id simply shadows the
    /// earlier record when the server reads it back), so appending the corrected fragment after the
    /// ordinary render is sufficient -- no in-place rewrite of the earlier bytes is needed. A symbol
    /// whose reference set is identical under every config is left completely untouched, preserving the
    /// single-config byte-identical invariant for the common case. Known, measured, NON-blocking
    /// follow-up: every genuinely divergent symbol is rendered TWICE -- once by the ordinary
    /// shard-based base pass (immediately superseded, its bytes becoming unreachable dead weight in
    /// <c>references.pack</c> once the index's last write wins) and once by this item's merged-set
    /// render. Both the redundant render and the dead pack bytes are proportional to the count of
    /// genuinely divergent symbols (small relative to the whole symbol table), on the same FileStream
    /// path profiling showed dominates finalize time -- so a cleaner design would exclude divergent
    /// symbols from the base pass entirely and render each such symbol exactly once, at the cost of
    /// coupling the base pass to the divergent set computed here. Do not do this pre-emptively; measure
    /// whether it matters at real-repo scale first.</item>
    /// <item><c>SolutionFinalizer.CreateMasterDeclarationsIndex</c> (the master declared-symbols index
    /// driving whole-solution search): FIXED as a side effect of the item below -- it enumerates
    /// <c>project.DeclaredSymbols</c> for each project in <c>this.projects</c>, which is now the union of
    /// every config's projects (see <see cref="StageNonPrimaryOnlyProjects"/>), not just the primary
    /// config's own Pass1-rendered set.</item>
    /// <item>A project (and everything it declares) that exists ONLY under a non-primary config --
    /// FIXED, <see cref="StageNonPrimaryOnlyProjects"/>: its full raw obj/&lt;config&gt; folder is copied
    /// into the staging root (from whichever config actually has it) so the ordinary
    /// <see cref="SolutionFinalizer"/> pipeline discovers and finalizes it like any other project.
    /// A FILE (not a whole project) that exists ONLY under a non-primary config -- e.g. a
    /// platform-specific partial-class file -- is FIXED the same way, at file granularity, by
    /// <see cref="StageDivergentlyPathedFiles"/>. Re-rendering a file that exists under the SAME
    /// relative path in two or more configs but with DIFFERENT rendered content remains the separate,
    /// larger follow-up -- see the remarks on <see cref="StageDivergentlyPathedFiles"/> for exactly
    /// why that bucket needs a variant-discovery mechanism (the still-deferred client selector, most
    /// likely) before it can be wired without producing unreachable output.</item>
    /// <item>The partial-type/member disambiguation page (<c>P/&lt;symbolId&gt;.html</c>) for a symbol
    /// whose declaration locations genuinely diverge across configs -- e.g. single-location under every
    /// individual config, but a DIFFERENT single file per config (<c>Foo.Windows.cs</c> vs.
    /// <c>Foo.Unix.cs</c>), so no disambiguation page existed at all under any one config alone, or an
    /// existing disambiguation page whose file set differs from the merged one -- FIXED,
    /// <see cref="RewritePartialTypeDisambiguationPages"/>: re-renders using
    /// <see cref="ConfigLocationMerger"/>'s merged, config-tagged locations via the milestone-1
    /// config-tag overload of <see cref="Markup.GeneratePartialTypeDisambiguationFile"/>. An ordinary
    /// partial type identical under every config (<see cref="ConfigLocationMerger.IsFullyShared"/>) is
    /// left untouched, preserving the single-config byte-identical invariant for the common case.</item>
    /// <item><c>SolutionFinalizer.WriteSolutionExplorer</c> (<c>SolutionExplorer.html</c>, the site's
    /// project-tree navigation) -- FIXED, <see cref="ComputeMergedSolutionExplorerRoot"/>: Pass1 now
    /// additionally persists each project's solution-folder chain to
    /// <see cref="Constants.SolutionFolderFileName"/> (a purely additive per-project file, next to its
    /// other output), which this reads back for every discovered project across all registered configs
    /// to reconstruct the tree the ordinary single-config path would otherwise only have in memory.</item>
    /// </list>
    ///
    /// The mechanism for the FIXED items above:
    ///
    /// 1. Staging the primary (alphabetically-first) config's raw obj/&lt;config&gt; into a scratch
    ///    merge-input copy, and separately computing (purely in memory, never written to disk) the set
    ///    of symbol IDs declared under the primary config whose only reference records exist under some
    ///    OTHER config (see <see cref="StagePrimaryConfigAndComputeAdditionalReferencedSymbolIds"/>) --
    ///    this is the OTHER cross-config correctness concern, distinct from Used-By: <see
    ///    cref="ProjectFinalizer"/>'s per-symbol unreferenced-declaration backpatch
    ///    (<c>BackpatchUnreferencedDeclarations</c>) greys out a declaration whose own project folder
    ///    has zero reference-shard records for it. That decision is driven by <see
    ///    cref="ConfigReferenceMerger"/>'s per-symbol data, NOT the assembly graph -- a symbol declared
    ///    in a project and referenced ONLY from a call site that only exists under "windows" must not
    ///    be greyed as unreferenced just because the primary (rendering) config happens to be "linux"
    ///    and its own on-disk shard has no record of that reference. Threading the extra symbol-ID set
    ///    into <c>BackpatchUnreferencedDeclarations</c> directly -- rather than writing it as an on-disk
    ///    shard that every consumer of that shard glob would also see -- means the ordinary,
    ///    already-correct backpatch mechanism in <see cref="ProjectFinalizer"/> runs unmodified over
    ///    the true cross-config picture, while the FAR renderer (which globs the same shard pattern)
    ///    stays untouched.
    /// 2. Copying in every project that exists ONLY under a non-primary config
    ///    (<see cref="StageNonPrimaryOnlyProjects"/>), every FILE that exists ONLY under a non-primary
    ///    config within a project the primary config DOES have (<see cref="StageDivergentlyPathedFiles"/>),
    ///    and reconstructing the merged solution-explorer tree from persisted per-project folder-chain
    ///    metadata across all configs (<see cref="ComputeMergedSolutionExplorerRoot"/>), so all three are
    ///    genuinely present for step 3 to discover and render, not just patched onto whatever step 3
    ///    alone would have produced.
    /// 3. Finalizing that staged copy through the ordinary single-config <see cref="SolutionFinalizer"/>
    ///    pipeline. This establishes real, rendered HTML content -- and is exactly why the non-divergent
    ///    common case (every project/file/declaration whose render and reference set don't actually
    ///    differ across configs) looks byte-identical to what a single-config run of any one config
    ///    would have produced.
    /// 4. Re-patching every discovered project's "Used By" block, <see
    ///    cref="Constants.TopReferencedAssemblies"/>, and the referencing counts in
    ///    <see cref="Constants.MasterAssemblyMap"/> using the MERGED, config-tagged referenced-assembly
    ///    edges from <see cref="ConfigProjectMerger"/> -- overriding whatever step 3 wrote when it only
    ///    knew about the primary config's own edges (<see cref="PatchCrossConfigUsedByAndAggregates"/>) --
    ///    re-rendering every genuinely-divergent partial-type disambiguation page
    ///    (<see cref="RewritePartialTypeDisambiguationPages"/>), for the same reason: step 3 only ever
    ///    knew about the primary config's own DeclarationMap.txt, and appending a config-tagged,
    ///    replacement "Find All References" fragment for every symbol whose merged reference set is
    ///    genuinely divergent (threaded into <c>SolutionFinalizer.FinalizeProjects</c>'s
    ///    <c>mergedDivergentReferencesByAssembly</c>/<c>configsByAssembly</c> parameters, which flow all
    ///    the way to <see cref="ProjectFinalizer.GenerateMergedReferencesFragments"/>).
    ///
    /// A file that exists at the SAME relative path in two or more configs but renders DIFFERENT
    /// content (<see cref="ConfigFileDeduper"/>'s "shared-render-divergent" bucket) -- FIXED,
    /// <see cref="StageDivergentlyRenderedFiles"/>: now that the client config selector exists (see
    /// scripts.js) to give a reader a way to actually reach a non-chosen variant, every distinct
    /// rendering is staged as its own physical page (the primary config's own rendering keeps the
    /// file's ordinary URL, preserving the single-config byte-identical invariant), and every page for
    /// such a file gets a small config-tagged switcher banner so the selector has something concrete to
    /// grey/hide between. A file whose rendering is identical across every config it exists under is
    /// left completely untouched.
    /// </summary>
    public static class ConfigAwareProjectFinalizer
    {
        /// <param name="configObjRoots">configName -> that config's obj/&lt;config&gt; root path. Must contain 2+ entries.</param>
        /// <param name="websiteDestinationFolder">The shared "index/" folder every config's run writes into.</param>
        /// <param name="axisTagsByConfig">
        /// configName -> its structured axis tags (e.g. {"os":"windows","arch":"x64"}), from
        /// <see cref="ConfigRegistryEntry.AxisTags"/>. Null or a config missing from this map means that
        /// config has no axis tags -- the client falls back to treating it as one flat, ungrouped entry.
        /// Purely a client-presentation concern (see <see cref="WriteRegisteredConfigsForClient"/>); does
        /// not affect any server-side merge behavior above.
        /// </param>
        public static void Finalize(
            IReadOnlyDictionary<string, string> configObjRoots,
            string websiteDestinationFolder,
            bool emitAssemblyList,
            Federation federation,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> axisTagsByConfig = null)
        {
            if (configObjRoots == null || configObjRoots.Count < 2)
            {
                throw new ArgumentException("ConfigAwareProjectFinalizer requires 2 or more configs; use the ordinary SolutionFinalizer for 0/1.", nameof(configObjRoots));
            }

            var primaryConfig = configObjRoots.Keys.OrderBy(c => c, StringComparer.Ordinal).First();
            var projectNames = ConfigProjectMerger.DiscoverProjects(configObjRoots);

            var stagingRoot = Path.Combine(Path.GetTempPath(), "SourceBrowserConfigMergeStaging_" + Guid.NewGuid().ToString("N"));
            try
            {
                var additionalReferencedSymbolIdsByAssembly = StagePrimaryConfigAndComputeAdditionalReferencedSymbolIds(
                    configObjRoots, primaryConfig, stagingRoot, projectNames, out var mergedDivergentReferencesByAssembly, out var configsByAssembly);
                StageNonPrimaryOnlyProjects(configObjRoots, primaryConfig, stagingRoot, projectNames);
                StageDivergentlyPathedFiles(configObjRoots, primaryConfig, stagingRoot, projectNames);
                StageDivergentlyRenderedFiles(configObjRoots, primaryConfig, stagingRoot, projectNames);

                var mergedSolutionExplorerRoot = ComputeMergedSolutionExplorerRoot(configObjRoots, primaryConfig, projectNames);

                var solutionFinalizer = new SolutionFinalizer(stagingRoot, websiteDestinationFolder);
                solutionFinalizer.FinalizeProjects(
                    emitAssemblyList,
                    federation,
                    solutionExplorerRoot: mergedSolutionExplorerRoot,
                    additionalReferencedSymbolIdsByAssembly: additionalReferencedSymbolIdsByAssembly,
                    mergedDivergentReferencesByAssembly: mergedDivergentReferencesByAssembly,
                    configsByAssembly: configsByAssembly);
            }
            finally
            {
                if (Directory.Exists(stagingRoot))
                {
                    try
                    {
                        Directory.Delete(stagingRoot, recursive: true);
                    }
                    catch
                    {
                        // Best-effort scratch cleanup -- never let a leftover temp dir fail the merge
                        // that already succeeded.
                    }
                }
            }

            PatchCrossConfigUsedByAndAggregates(configObjRoots, websiteDestinationFolder);
            RewritePartialTypeDisambiguationPages(configObjRoots, websiteDestinationFolder, projectNames);
            WriteRegisteredConfigsForClient(configObjRoots.Keys, axisTagsByConfig, websiteDestinationFolder);
        }

        /// <summary>
        /// Writes <see cref="Constants.RegisteredConfigsFileName"/> at the website root -- the one piece
        /// of config metadata the merged site needs to expose to the BROWSER (everything else discussed
        /// in this type's remarks is server-side merge logic). The client config-selector fetches this
        /// to know which configs exist (and, when present, how they group into axes like OS/Arch), so it
        /// can render its picker; its absence (single/no-config sites never call this method at all) is
        /// how the selector's bootstrap script knows to render nothing rather than a single-option no-op
        /// UI. Hand-written as minimal JSON rather than pulling in a serializer for a handful of fields.
        ///
        /// Shape:
        /// <code>
        /// {
        ///   "configs": ["linux-x64", "windows-x64"],
        ///   "axes": { "os": ["linux", "windows"], "arch": ["x64"] },
        ///   "configAxisValues": { "linux-x64": {"os":"linux","arch":"x64"}, "windows-x64": {"os":"windows","arch":"x64"} }
        /// }
        /// </code>
        /// "axes"/"configAxisValues" are empty objects (not omitted) when no registered config carries
        /// any axis tags -- the client renders one flat, ungrouped list of "configs" in that case, same
        /// as before axis support existed. A config with no tags of its own is simply absent from
        /// "configAxisValues" even when OTHER configs do have tags (a mixed site); the client groups the
        /// tagged ones by axis and falls back to a flat entry for the rest, rather than silently dropping
        /// it.
        /// </summary>
        private static void WriteRegisteredConfigsForClient(
            IEnumerable<string> configs,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> axisTagsByConfig,
            string websiteDestinationFolder)
        {
            var orderedConfigs = configs.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

            // axis name -> distinct values seen across all configs, each sorted for deterministic output.
            var axisValues = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (axisTagsByConfig != null)
            {
                foreach (var configName in orderedConfigs)
                {
                    if (!axisTagsByConfig.TryGetValue(configName, out var tags) || tags == null)
                    {
                        continue;
                    }

                    foreach (var kvp in tags)
                    {
                        if (!axisValues.TryGetValue(kvp.Key, out var values))
                        {
                            values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                            axisValues[kvp.Key] = values;
                        }

                        values.Add(kvp.Value);
                    }
                }
            }

            var json = new StringBuilder();
            json.Append('{');

            json.Append("\"configs\":[");
            json.Append(string.Join(",", orderedConfigs.Select(JsonString)));
            json.Append("],");

            json.Append("\"axes\":{");
            json.Append(string.Join(",", axisValues.Select(kvp =>
                JsonString(kvp.Key) + ":[" + string.Join(",", kvp.Value.Select(JsonString)) + "]")));
            json.Append("},");

            json.Append("\"configAxisValues\":{");
            json.Append(string.Join(",", orderedConfigs
                .Where(c => axisTagsByConfig != null && axisTagsByConfig.TryGetValue(c, out var tags) && tags != null && tags.Count > 0)
                .Select(c =>
                {
                    var tags = axisTagsByConfig[c];
                    return JsonString(c) + ":{" + string.Join(",", tags.Select(kvp => JsonString(kvp.Key) + ":" + JsonString(kvp.Value))) + "}";
                })));
            json.Append('}');

            json.Append('}');

            File.WriteAllText(Path.Combine(websiteDestinationFolder, Constants.RegisteredConfigsFileName), json.ToString(), Encoding.UTF8);
        }

        private static string JsonString(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// Data shared by <see cref="PatchCrossConfigUsedByAndAggregates"/>: for each project, the set of
        /// OTHER projects that reference it, and under which config(s) each such edge holds.
        /// </summary>
        private static Dictionary<string, Dictionary<string, HashSet<string>>> ComputeMergedReferencingAssemblies(
            IReadOnlyDictionary<string, string> configObjRoots,
            IReadOnlyList<string> projectNames)
        {
            // referenced assembly -> (referencing assembly -> configs under which that edge exists)
            var mergedReferencingAssemblies = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);

            foreach (var assemblyId in projectNames)
            {
                var merged = ConfigProjectMerger.MergeProject(assemblyId, configObjRoots);

                foreach (var kvp in merged.ReferencedAssemblies)
                {
                    var referencedAssembly = kvp.Key;
                    var configsForEdge = kvp.Value;

                    if (!mergedReferencingAssemblies.TryGetValue(referencedAssembly, out var referencingMap))
                    {
                        referencingMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                        mergedReferencingAssemblies[referencedAssembly] = referencingMap;
                    }

                    if (!referencingMap.TryGetValue(assemblyId, out var existingConfigs))
                    {
                        existingConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        referencingMap[assemblyId] = existingConfigs;
                    }

                    existingConfigs.UnionWith(configsForEdge);
                }
            }

            return mergedReferencingAssemblies;
        }

        /// <summary>
        /// Copies the primary config's raw obj/&lt;config&gt; into a scratch directory (never mutating
        /// the real obj/&lt;primaryConfig&gt; -- that stays a pure, re-derivable artifact, same invariant
        /// as everywhere else in this pipeline) and, for each project, computes the set of symbol IDs
        /// that are declared under the primary config but have NO reference record at all in the primary
        /// config's own on-disk shards -- only under some OTHER config. This is the minimal input needed
        /// for <see cref="ProjectFinalizer"/>'s existing, unmodified <c>BackpatchUnreferencedDeclarations</c>
        /// to correctly NOT grey out a symbol that's genuinely referenced only under a non-primary config.
        ///
        /// This is passed to <c>SolutionFinalizer.FinalizeProjects</c>'s
        /// <c>additionalReferencedSymbolIdsByAssembly</c> parameter, which threads it through to
        /// <c>BackpatchUnreferencedDeclarations</c>'s in-memory "is this symbol referenced" decision ONLY
        /// -- it is never written to disk as a reference shard. This deliberately avoids an earlier
        /// version of this fix that injected an extra <c>_r*.dat</c> shard file into the staged
        /// References/ folder: that shard was consumed not just by the backpatch decision but also by
        /// <c>ProjectFinalizer.References.cs</c>'s <c>GenerateFinalReferencesFiles</c>/
        /// <c>GenerateReferencesFilesFromShard</c>, which globs the exact same <c>_r*.dat</c> pattern to
        /// render each symbol's actual "Find All References" HTML -- so the injected cross-config-only
        /// record would also render into that symbol's FAR list, indistinguishable from a primary-config
        /// reference (no config-tagging mechanism exists in that renderer). Keeping this data in memory
        /// and threading it only to the backpatch call closes that blast radius entirely: the FAR
        /// renderer only ever sees the primary config's real on-disk shards, unchanged.
        /// </summary>
        private static Dictionary<string, HashSet<string>> StagePrimaryConfigAndComputeAdditionalReferencedSymbolIds(
            IReadOnlyDictionary<string, string> configObjRoots,
            string primaryConfig,
            string stagingRoot,
            IReadOnlyList<string> projectNames,
            out Dictionary<string, Dictionary<string, List<Reference>>> mergedDivergentReferencesByAssembly,
            out Dictionary<string, IReadOnlyList<string>> configsByAssembly)
        {
            // This copy is still load-bearing even though nothing is injected into it anymore: it's the
            // ONE merge-input root that StageNonPrimaryOnlyProjects also copies into (obj/<primaryConfig>
            // alone doesn't contain projects that only exist under a different config), and
            // SolutionFinalizer.DiscoverProjects/FinalizeProjects operate over a single root. Finalizing
            // obj/<primaryConfig> directly (skipping this copy) would mean either mutating that real Pass1
            // artifact to add non-primary-only projects into it -- violating the "obj is read-only input"
            // invariant -- or finalizing from two roots, which SolutionFinalizer doesn't support. So this
            // stays a real scratch merge root, not a vestige of the old on-disk shard-injection design.
            FileUtilities.CopyDirectory(configObjRoots[primaryConfig], stagingRoot);

            var additionalReferencedSymbolIdsByAssembly = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            mergedDivergentReferencesByAssembly = new Dictionary<string, Dictionary<string, List<Reference>>>(StringComparer.OrdinalIgnoreCase);
            configsByAssembly = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var assemblyId in projectNames)
            {
                if (!Directory.Exists(Path.Combine(configObjRoots[primaryConfig], assemblyId)))
                {
                    // Doesn't exist under the primary config at all -- StageNonPrimaryOnlyProjects handles
                    // copying its content in from whichever config actually has it; nothing to backpatch
                    // here since there's no primary-rendered page for it in this run.
                    continue;
                }

                // CopyDirectory only copies files, so an empty References/ folder (perfectly normal for
                // a project with zero incoming references) wouldn't otherwise exist in the staged copy --
                // and SolutionFinalizer.DiscoverProjects requires that folder to exist to recognize the
                // project at all. Ensure it's present regardless of whether this project needs augmentation.
                var stagedReferencesFolder = Path.Combine(stagingRoot, assemblyId, Constants.ReferencesFileName);
                Directory.CreateDirectory(stagedReferencesFolder);

                var merged = ConfigProjectMerger.MergeProject(assemblyId, configObjRoots);
                configsByAssembly[assemblyId] = merged.Configs;

                // Only symbols actually declared under the primary config have a rendered page in this
                // run to un-grey at all -- see the type-level remarks.
                var primaryDeclaredSymbols = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kvp in merged.DeclarationLocations)
                {
                    if (kvp.Value.Any(location => location.Configs.Contains(primaryConfig)))
                    {
                        primaryDeclaredSymbols.Add(kvp.Key);
                    }
                }

                HashSet<string> additionalReferencedSymbolIds = null;
                Dictionary<string, List<Reference>> divergentReferences = null;
                foreach (var kvp in merged.References)
                {
                    var symbolId = kvp.Key;

                    // Only render/tag a symbol whose merged reference set genuinely diverges across the
                    // configs this project actually exists under -- the common case (identical everywhere)
                    // stays untouched, preserving the single-config byte-identical output.
                    if (!ConfigReferenceMerger.IsFullyShared(kvp.Value, merged.Configs))
                    {
                        divergentReferences ??= new Dictionary<string, List<Reference>>(StringComparer.Ordinal);
                        divergentReferences[symbolId] = kvp.Value;
                    }

                    if (!primaryDeclaredSymbols.Contains(symbolId))
                    {
                        continue;
                    }

                    bool hasPrimaryReference = kvp.Value.Any(r => r.ConfigSet != null && r.ConfigSet.Contains(primaryConfig));
                    if (hasPrimaryReference)
                    {
                        // The primary config's own on-disk shard already has at least one record for
                        // this symbol -- the ordinary backpatch will already correctly see it referenced.
                        continue;
                    }

                    // Every reference to this symbol comes from a non-primary config only (e.g. a call
                    // site that only exists under "windows"). Record it in-memory so the backpatch
                    // decision sees the true cross-config picture without any of that data reaching the
                    // FAR renderer -- see the remarks above.
                    additionalReferencedSymbolIds ??= new HashSet<string>(StringComparer.Ordinal);
                    additionalReferencedSymbolIds.Add(symbolId);
                }

                if (additionalReferencedSymbolIds != null)
                {
                    additionalReferencedSymbolIdsByAssembly[assemblyId] = additionalReferencedSymbolIds;
                }

                if (divergentReferences != null)
                {
                    mergedDivergentReferencesByAssembly[assemblyId] = divergentReferences;
                }
            }

            return additionalReferencedSymbolIdsByAssembly;
        }

        /// <summary>
        /// A project (and everything it declares) that exists ONLY under a non-primary config was
        /// entirely absent from the merged site before this method -- step 2 of <see cref="Finalize"/>
        /// only ever discovers what's physically present in the staged copy, which started as a pure
        /// copy of just the primary config's obj/&lt;config&gt;. Copies each such project's full raw
        /// obj/&lt;config&gt; folder (from whichever registered config actually has it, alphabetically
        /// first if more than one) into the staging root so <see cref="SolutionFinalizer.DiscoverProjects"/>
        /// finds and finalizes it exactly like any other project, on this run's ordinary path. Its
        /// content genuinely only reflects that one config -- rendering it as a disambiguation across
        /// configs would only make sense if it also existed under the primary config, which by
        /// definition it does not -- so there is no cross-config choice to make here, unlike the
        /// genuinely-divergent-file case that remains a separate follow-up.
        /// </summary>
        private static void StageNonPrimaryOnlyProjects(
            IReadOnlyDictionary<string, string> configObjRoots,
            string primaryConfig,
            string stagingRoot,
            IReadOnlyList<string> projectNames)
        {
            var orderedConfigs = configObjRoots.Keys.OrderBy(c => c, StringComparer.Ordinal).ToArray();

            foreach (var assemblyId in projectNames)
            {
                if (Directory.Exists(Path.Combine(configObjRoots[primaryConfig], assemblyId)))
                {
                    // Already staged (and possibly backpatch-augmented) above.
                    continue;
                }

                foreach (var config in orderedConfigs)
                {
                    var sourceProjectFolder = Path.Combine(configObjRoots[config], assemblyId);
                    if (Directory.Exists(sourceProjectFolder))
                    {
                        var stagedProjectFolder = Path.Combine(stagingRoot, assemblyId);
                        FileUtilities.CopyDirectory(sourceProjectFolder, stagedProjectFolder);

                        // CopyDirectory only copies files, so a project with zero incoming references
                        // (a perfectly normal empty References/ folder) wouldn't otherwise exist in the
                        // staged copy -- and SolutionFinalizer.DiscoverProjects requires that folder to
                        // exist to recognize the project at all (same fix as the primary-config staging
                        // step above).
                        Directory.CreateDirectory(Path.Combine(stagedProjectFolder, Constants.ReferencesFileName));
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Narrower, deliberately-scoped half of the "genuinely-divergent per-file content" follow-up:
        /// a source file that a NON-primary config compiles but the primary config does not (its
        /// relative path simply doesn't exist under obj/&lt;primaryConfig&gt; for this project at all --
        /// e.g. Foo.Windows.cs, only part of the windows build) was previously invisible in the merged
        /// site: the staged copy started as a pure copy of the primary config's own project folder, and
        /// nothing ever pulled in a path the primary config never produced. Copies each such file in
        /// from whichever registered config actually has it (alphabetically first if more than one),
        /// so the ordinary render already has a page for it, and so a disambiguation page built by
        /// <see cref="RewritePartialTypeDisambiguationPages"/> can safely link to it.
        ///
        /// Deliberately NOT handled here: a file that exists under the SAME relative path in two or
        /// more configs but renders DIFFERENT content (e.g. an `#if WINDOWS` region that changes what's
        /// classified/linked) -- that's <see cref="ConfigFileDeduper"/>'s "shared-render-divergent"
        /// bucket, handled separately by <see cref="StageDivergentlyRenderedFiles"/> once this method has
        /// finished filling in every divergently-PATHED file (so that method sees a complete picture of
        /// which relative paths exist under which configs).
        /// </summary>
        private static void StageDivergentlyPathedFiles(
            IReadOnlyDictionary<string, string> configObjRoots,
            string primaryConfig,
            string stagingRoot,
            IReadOnlyList<string> projectNames)
        {
            var orderedNonPrimaryConfigs = configObjRoots.Keys
                .Where(c => !string.Equals(c, primaryConfig, StringComparison.Ordinal))
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToArray();

            foreach (var assemblyId in projectNames)
            {
                var primaryProjectFolder = Path.Combine(configObjRoots[primaryConfig], assemblyId);
                if (!Directory.Exists(primaryProjectFolder))
                {
                    // Handled wholesale by StageNonPrimaryOnlyProjects -- nothing to fill in here.
                    continue;
                }

                var stagedProjectFolder = Path.Combine(stagingRoot, assemblyId);

                foreach (var config in orderedNonPrimaryConfigs)
                {
                    var otherProjectFolder = Path.Combine(configObjRoots[config], assemblyId);
                    if (!Directory.Exists(otherProjectFolder))
                    {
                        continue;
                    }

                    foreach (var relativePath in EnumerateContentFileRelativePaths(otherProjectFolder))
                    {
                        var stagedFilePath = Path.Combine(stagedProjectFolder, relativePath);
                        if (File.Exists(stagedFilePath))
                        {
                            // Already present -- either shared with the primary config, or a different
                            // non-primary config already filled this exact path in first.
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(stagedFilePath));
                        File.Copy(Path.Combine(otherProjectFolder, relativePath), stagedFilePath);
                    }
                }
            }
        }

        /// <summary>
        /// Enumerates a project folder's rendered source-file pages -- *.html files that represent an
        /// actual source file's content, as opposed to the project's metadata/aggregate pages
        /// (<see cref="Constants.ReferencesFileName"/>'s and <see cref="Constants.PartialResolvingFileName"/>'s
        /// subfolders, and the handful of fixed top-level page names SolutionFinalizer regenerates
        /// itself for every project regardless of which config(s) produced it).
        /// </summary>
        private static IEnumerable<string> EnumerateContentFileRelativePaths(string projectFolder)
        {
            var excludedTopLevelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "index.html",
                Constants.Namespaces,
                Constants.ProjectExplorer + ".html",
            };

            foreach (var file in Directory.GetFiles(projectFolder, "*.html", SearchOption.AllDirectories))
            {
                var relativePath = file.Substring(projectFolder.Length + 1);
                var topLevelSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

                if (string.Equals(topLevelSegment, Constants.ReferencesFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(topLevelSegment, Constants.PartialResolvingFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!relativePath.Contains(Path.DirectorySeparatorChar) &&
                    !relativePath.Contains(Path.AltDirectorySeparatorChar) &&
                    excludedTopLevelNames.Contains(relativePath))
                {
                    continue;
                }

                yield return relativePath;
            }
        }

        /// <summary>
        /// Wires <see cref="ConfigFileDeduper"/>'s "shared-render-divergent" bucket into the staged
        /// merge input: a source file that exists at the SAME relative path under two or more
        /// registered configs (by the time this runs, every config's project folder that has this
        /// project at all has this path filled in, thanks to <see cref="StageDivergentlyPathedFiles"/>
        /// running first) but whose Pass1-rendered HTML genuinely differs between them -- typically an
        /// "#if"-gated region whose active branch differs by config.
        ///
        /// The staged copy already has the PRIMARY config's own rendering at the file's ordinary path
        /// (an untouched copy from the primary config's own obj/&lt;config&gt; folder) -- that invariant
        /// is preserved explicitly here by pinning the primary config's variant to the original path via
        /// <see cref="ConfigFileDeduper.AssignPhysicalPaths"/>'s <c>preferredConfig</c> parameter, rather
        /// than letting an arbitrary hash-order tie-break potentially swap in a different config's
        /// content at a URL that already existed before this method ran. Every OTHER distinct rendering
        /// is written to its own new, deterministically-suffixed physical page. <see cref="ProjectFinalizer"/>'s
        /// constructor copies a project's ENTIRE staged folder wholesale into the website output, so
        /// these extra staged files reach the final site without any further plumbing.
        ///
        /// Every physical page for a divergent file gets a small config-tagged switcher banner inserted
        /// right after its existing file/project link panel (<see cref="InsertConfigVariantBanner"/>),
        /// tagged with <c>data-configs</c> exactly like declaration and FAR entries, so the client
        /// selector -- which per its locked scope reads <c>data-configs</c> on declarations, FAR entries,
        /// AND divergent files -- has something concrete to grey/hide between and a link to actually
        /// reach the other variant(s).
        ///
        /// A file whose rendering is IDENTICAL across every config it exists under (the overwhelmingly
        /// common case) is left completely untouched: no banner, no extra variant, byte-identical to
        /// today's single-config output.
        ///
        /// Known, deliberately-accepted limitation: the per-symbol "unreferenced declaration" backpatch
        /// (<c>ProjectFinalizer.Declarations.cs</c>) still only ever touches the file staged at each
        /// symbol's DeclarationMap.txt-listed location, i.e. the PRIMARY variant written by earlier
        /// staging steps -- a non-primary variant produced here reflects only that one config's own
        /// local backpatch decision from Pass1, same as the existing, established limitation for
        /// non-primary-only projects/files (see <see cref="StageNonPrimaryOnlyProjects"/>): "there is no
        /// cross-config choice to make here" beyond what's already fixed for declarations/FAR/Used-By.
        /// </summary>
        private static void StageDivergentlyRenderedFiles(
            IReadOnlyDictionary<string, string> configObjRoots,
            string primaryConfig,
            string stagingRoot,
            IReadOnlyList<string> projectNames)
        {
            var orderedConfigs = configObjRoots.Keys.OrderBy(c => c, StringComparer.Ordinal).ToArray();

            foreach (var assemblyId in projectNames)
            {
                var primaryProjectFolder = Path.Combine(configObjRoots[primaryConfig], assemblyId);
                if (!Directory.Exists(primaryProjectFolder))
                {
                    // Handled wholesale by StageNonPrimaryOnlyProjects -- no primary rendering exists to
                    // compare anything else against.
                    continue;
                }

                var stagedProjectFolder = Path.Combine(stagingRoot, assemblyId);

                foreach (var relativePath in EnumerateContentFileRelativePaths(primaryProjectFolder))
                {
                    Dictionary<string, string> perConfigContent = null;

                    foreach (var config in orderedConfigs)
                    {
                        var candidatePath = Path.Combine(configObjRoots[config], assemblyId, relativePath);
                        if (!File.Exists(candidatePath))
                        {
                            continue;
                        }

                        perConfigContent ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        perConfigContent[config] = File.ReadAllText(candidatePath);
                    }

                    if (perConfigContent == null || perConfigContent.Count < 2)
                    {
                        // Only one config actually has this exact path -- nothing to compare against.
                        continue;
                    }

                    var variants = ConfigFileDeduper.Dedupe(perConfigContent);
                    if (variants.Count == 1)
                    {
                        // Fully shared rendering -- already correct at the original path, untouched.
                        continue;
                    }

                    ConfigFileDeduper.AssignPhysicalPaths(relativePath, variants, preferredConfig: primaryConfig);

                    foreach (var variant in variants)
                    {
                        var contentWithBanner = InsertConfigVariantBanner(variant.Content, assemblyId, variants);
                        var destinationPath = Path.Combine(stagedProjectFolder, variant.PhysicalPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                        File.WriteAllText(destinationPath, contentWithBanner, Encoding.UTF8);
                    }
                }
            }
        }

        /// <summary>
        /// Builds the hash-routed URL (the same "/#assembly/path" scheme <c>DocumentGenerator</c> uses
        /// for its own file link) for one variant's physical page, and inserts a small switcher banner
        /// listing every variant right after the divergent file's existing file/project link panel
        /// (the first "&lt;/div&gt;" in the rendered page -- <c>Markup.WriteLinkPanel</c>'s closing tag,
        /// always the first div closed since it's the very first thing a rendered document writes).
        /// Each variant gets its own <c>data-configs</c>-tagged span so the client selector can grey out
        /// links to configs the reader hasn't selected, exactly like declaration/FAR tags.
        /// </summary>
        private static string InsertConfigVariantBanner(
            string content,
            string assemblyId,
            IReadOnlyList<ConfigFileDeduper.FileVariant> variants)
        {
            var links = variants.Select(variant =>
            {
                var configList = string.Join(",", variant.Configs.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
                var label = System.Net.WebUtility.HtmlEncode(configList);
                var url = BuildVariantDocumentUrl(assemblyId, variant.PhysicalPath);

                return string.Format(
                    "<span class=\"configFileVariantLink\" data-configs=\"{0}\"><a class=\"blueLink\" href=\"{1}\" target=\"_top\">{2}</a></span>",
                    System.Net.WebUtility.HtmlEncode(configList),
                    url,
                    label);
            });

            var banner = "<div class=\"configFileVariantBanner\">This file's content differs by config: " + string.Join(" | ", links) + "</div>";

            var insertionPoint = content.IndexOf("</div>", StringComparison.Ordinal);
            if (insertionPoint < 0)
            {
                // Shouldn't happen for any real rendered document page, but never corrupt content we
                // can't safely patch.
                return content;
            }

            insertionPoint += "</div>".Length;
            return content.Insert(insertionPoint, banner);
        }

        private static string BuildVariantDocumentUrl(string assemblyId, string physicalRelativePath)
        {
            var withoutExtension = physicalRelativePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                ? physicalRelativePath.Substring(0, physicalRelativePath.Length - ".html".Length)
                : physicalRelativePath;

            var urlPath = withoutExtension
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

            return "/#" + assemblyId + "/" + urlPath;
        }

        /// <summary>
        /// Re-renders the partial-type/member disambiguation page for every symbol whose merged
        /// (cross-config) declaration-location set is genuinely divergent -- i.e. NOT
        /// <see cref="ConfigLocationMerger.IsFullyShared"/>, meaning at least one location doesn't exist
        /// under every config that has this project. The ordinary <see cref="SolutionFinalizer"/> render
        /// (step 3 of <see cref="Finalize"/>) already wrote a byte-identical page for the common,
        /// non-divergent case (a symbol declared in the same N files under every config -- an ordinary
        /// partial type, unrelated to config merging) using only the primary config's own
        /// DeclarationMap.txt; this only overwrites (or, for a symbol whose primary-config location count
        /// was 1 -- e.g. <c>Foo.Windows.cs</c> under windows, a DIFFERENT single file
        /// <c>Foo.Unix.cs</c> under linux -- creates for the first time) the page for symbols where that
        /// isn't true, using <see cref="ConfigLocationMerger"/>'s merged, config-tagged location set so
        /// each link carries which config(s) it applies under.
        /// </summary>
        private static void RewritePartialTypeDisambiguationPages(
            IReadOnlyDictionary<string, string> configObjRoots,
            string websiteDestinationFolder,
            IReadOnlyList<string> projectNames)
        {
            foreach (var assemblyId in projectNames)
            {
                var merged = ConfigProjectMerger.MergeProject(assemblyId, configObjRoots);
                var projectDestinationFolder = Path.Combine(websiteDestinationFolder, assemblyId);
                if (!Directory.Exists(projectDestinationFolder))
                {
                    // Shouldn't happen (every discovered project is staged and finalized by this point),
                    // but never let a disambiguation-page rewrite fail the merge that already succeeded.
                    continue;
                }

                foreach (var kvp in merged.DeclarationLocations)
                {
                    var symbolId = kvp.Key;
                    var locations = kvp.Value;

                    if (locations.Count <= 1)
                    {
                        // Never needed a disambiguation page in the first place.
                        continue;
                    }

                    if (ConfigLocationMerger.IsFullyShared(locations, merged.Configs))
                    {
                        // An ordinary partial type/member, identical under every config that has this
                        // project -- the primary config's own render is already correct; don't touch it
                        // (config tags would be inert noise, and re-rendering unconditionally would
                        // break the single-config byte-identical invariant for the common case).
                        continue;
                    }

                    var configTagsByFilePath = locations
                        .GroupBy(l => l.FilePath, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            g => g.Key,
                            g => (IEnumerable<string>)g.SelectMany(l => l.Configs).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                            StringComparer.OrdinalIgnoreCase);

                    Markup.GeneratePartialTypeDisambiguationFile(
                        websiteDestinationFolder,
                        projectDestinationFolder,
                        symbolId,
                        configTagsByFilePath.Keys,
                        configTagsByFilePath,
                        merged.Configs);
                }
            }
        }

        /// <summary>
        /// Reconstructs the tree <see cref="SolutionFinalizer.WriteSolutionExplorer"/> needs to render
        /// <c>SolutionExplorer.html</c> -- the site's project-tree navigation, otherwise silently absent
        /// in config-aware mode (see the type-level remarks). The ordinary single-config path builds
        /// this tree in memory during Pass1, straight from the loaded .sln/.slnx; the merge step here
        /// runs as its own separate invocation with no access to that (each config may even have been
        /// generated from a different process run, at a different time, with no in-memory state left).
        /// Instead, each project's solution-folder chain is persisted by Pass1 to
        /// <see cref="Constants.SolutionFolderFileName"/> next to its other per-project output (a purely
        /// additive Pass1 change -- nothing else reads it), and read back here for every discovered
        /// project across all registered configs (preferring the primary config's own copy; falling back
        /// to whichever other config has the project, for the non-primary-only case handled by
        /// <see cref="StageNonPrimaryOnlyProjects"/>). A project's folder chain is the same regardless of
        /// config (it comes from the .sln/.slnx structure, not per-config content), so there is no
        /// cross-config conflict to resolve here, unlike Used-By or the aggregates. RepoName/SolutionName
        /// are read back the same way (from <see cref="Constants.ProjectInfoFileName"/>, same file the
        /// ordinary single-config <see cref="ProjectFinalizer.ReadProjectInfo"/> already reads them from)
        /// but looked up independently of the folder chain, so a project's tags don't disappear just
        /// because it happens to lack a SolutionFolder.txt. The Repo/Solution top-level grouping nodes
        /// (see <see cref="Program.GetSolutionExplorerGroupingFolder"/>) are recomputed here too, across
        /// the merged project set, so a config-merged AND multi-repo site still gets the same grouping a
        /// single-config multi-repo site would.
        /// </summary>
        private static Folder<ProjectSkeleton> ComputeMergedSolutionExplorerRoot(
            IReadOnlyDictionary<string, string> configObjRoots,
            string primaryConfig,
            IReadOnlyList<string> projectNames)
        {
            var orderedConfigs = configObjRoots.Keys.OrderBy(c => c, StringComparer.Ordinal).ToArray();
            var root = new Folder<ProjectSkeleton>();

            var tagsByAssembly = new Dictionary<string, (string Repo, string Solution, string[] Chain)>(StringComparer.OrdinalIgnoreCase);
            foreach (var assemblyId in projectNames)
            {
                tagsByAssembly[assemblyId] = ReadRepoAndSolutionName(configObjRoots, orderedConfigs, primaryConfig, assemblyId);
            }

            // Same grouping decision Program.IndexSolutionsAsync makes for a single Pass1 run, just
            // computed over the merged (cross-config) project set instead of one run's own inputs --
            // only introduce Repo/Solution nodes when the merged site actually spans more than one repo
            // (or, within a repo, more than one solution), so single-repo/untagged config-merged sites
            // stay exactly as before this grouping feature existed.
            var distinctRepoCount = tagsByAssembly.Values
                .Select(t => t.Repo)
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var solutionCountsByRepo = tagsByAssembly.Values
                .Where(t => !string.IsNullOrEmpty(t.Repo))
                .GroupBy(t => t.Repo, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(t => t.Solution).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var assemblyId in projectNames)
            {
                string[] folderChain = null;

                var primaryProjectFolder = configObjRoots[primaryConfig];
                var primarySolutionFolderFile = Path.Combine(primaryProjectFolder, assemblyId, Constants.SolutionFolderFileName);
                if (File.Exists(primarySolutionFolderFile))
                {
                    folderChain = File.ReadAllLines(primarySolutionFolderFile);
                }
                else
                {
                    foreach (var config in orderedConfigs)
                    {
                        var projectFolder = configObjRoots[config];
                        var solutionFolderFile = Path.Combine(projectFolder, assemblyId, Constants.SolutionFolderFileName);
                        if (File.Exists(solutionFolderFile))
                        {
                            folderChain = File.ReadAllLines(solutionFolderFile);
                            break;
                        }
                    }
                }

                var (repoName, solutionName, repoChain) = tagsByAssembly[assemblyId];

                var folder = Program.GetSolutionExplorerGroupingFolder(root, repoChain, solutionName, distinctRepoCount, solutionCountsByRepo);
                if (folderChain != null)
                {
                    foreach (var segment in folderChain)
                    {
                        if (segment.Length > 0)
                        {
                            folder = folder.GetOrCreateFolder(segment);
                        }
                    }
                }

                // ProjectSkeleton.Name only affects sort order in the non-flattened tree (WriteFolder
                // renders by AssemblyName regardless) -- the assemblyId is a reasonable stand-in for the
                // Roslyn project display name we no longer have access to at merge time. RepoName/RepoChain
                // are read back from the same per-project ProjectInfo.txt (Constants.ProjectInfoFileName)
                // that the ordinary single-config ProjectFinalizer.ReadProjectInfo reads, so /repoPath
                // tags (and their ancestry) survive the config merge instead of dropping out of
                // SolutionExplorer.html.
                folder.Add(new ProjectSkeleton(assemblyId, assemblyId, repoName, repoChain));
            }

            return root;
        }

        /// <summary>
        /// Reads the RepoName=/SolutionName= lines Pass1's ProjectGenerator.GenerateProjectInfo persists
        /// to each project's ProjectInfo.txt (see <see cref="Constants.ProjectInfoFileName"/>), mirroring
        /// ProjectFinalizer.ReadProjectInfo's own read of the same file for the non-config-merge path.
        /// Primary-config-first, then falls back to any other registered config, same order/rationale as
        /// the folder-chain lookup above -- a project's tags shouldn't disappear just because the primary
        /// config happens to lack this file (e.g. the non-primary-only-project case).
        /// </summary>
        private static (string Repo, string Solution, string[] Chain) ReadRepoAndSolutionName(
            IReadOnlyDictionary<string, string> configObjRoots,
            IReadOnlyList<string> orderedConfigs,
            string primaryConfig,
            string assemblyId)
        {
            var tags = ReadRepoAndSolutionNameFromConfig(configObjRoots[primaryConfig], assemblyId);
            if (tags == null)
            {
                foreach (var config in orderedConfigs)
                {
                    tags = ReadRepoAndSolutionNameFromConfig(configObjRoots[config], assemblyId);
                    if (tags != null)
                    {
                        break;
                    }
                }
            }

            return tags ?? (string.Empty, string.Empty, System.Array.Empty<string>());
        }

        private static (string Repo, string Solution, string[] Chain)? ReadRepoAndSolutionNameFromConfig(string configObjRoot, string assemblyId)
        {
            var projectInfoFile = Path.Combine(configObjRoot, assemblyId, Constants.ProjectInfoFileName + ".txt");
            if (!File.Exists(projectInfoFile))
            {
                return null;
            }

            var lines = File.ReadAllLines(projectInfoFile);
            var repo = Serialization.ReadValue(lines, "RepoName") ?? string.Empty;
            var chainValue = Serialization.ReadValue(lines, "RepoChain") ?? string.Empty;
            var chain = string.IsNullOrEmpty(chainValue)
                ? (string.IsNullOrEmpty(repo) ? System.Array.Empty<string>() : new[] { repo })
                : chainValue.Split('|');
            return (
                repo,
                Serialization.ReadValue(lines, "SolutionName") ?? string.Empty,
                chain);
        }

        /// <summary>
        /// Recomputes every whole-solution/aggregate output of the ordinary <see cref="SolutionFinalizer"/>
        /// pipeline that step 2 of <see cref="Finalize"/> only ever populated from the primary config's own
        /// edges: the per-project "Used By" HTML block, <see cref="Constants.TopReferencedAssemblies"/>,
        /// and the referencing-assembly counts in <see cref="Constants.MasterAssemblyMap"/> (written by
        /// <see cref="SolutionFinalizer.CreateProjectMap"/>). All three are derived from the same
        /// referenced-assembly-edge data (<see cref="Constants.ReferencedAssemblyList"/>), so they share one
        /// merge pass here -- see the type-level remarks for why every output of that step is a candidate
        /// for this same primary-config bias, and which of them are (not yet) covered.
        /// </summary>
        private static void PatchCrossConfigUsedByAndAggregates(IReadOnlyDictionary<string, string> configObjRoots, string websiteDestinationFolder)
        {
            var totalConfigCount = configObjRoots.Count;
            var projectNames = ConfigProjectMerger.DiscoverProjects(configObjRoots);
            var mergedReferencingAssemblies = ComputeMergedReferencingAssemblies(configObjRoots, projectNames);

            foreach (var referencedAssembly in projectNames)
            {
                var fileName = Path.Combine(websiteDestinationFolder, referencedAssembly, Constants.ProjectExplorer + ".html");

                mergedReferencingAssemblies.TryGetValue(referencedAssembly, out var referencingMap);

                var entries = (referencingMap ?? new Dictionary<string, HashSet<string>>())
                    .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kvp => (
                        AssemblyId: kvp.Key,
                        // Omit the tag when the edge holds under every registered config -- the common
                        // case, where config is inert metadata (mirrors ConfigLocationMerger.IsFullyShared).
                        ConfigLabel: kvp.Value.Count == totalConfigCount
                            ? null
                            : string.Join(",", kvp.Value.OrderBy(c => c, StringComparer.Ordinal))))
                    .ToList();

                SolutionFinalizer.PatchUsedByBlock(fileName, entries);
            }

            // referencedAssembly -> total number of distinct referencing assemblies across ALL configs --
            // the same shape SolutionFinalizer's constructor (CalculateReferencingAssemblies) computes,
            // except that one only ever saw the primary config's own ReferencedAssemblyList.txt files.
            var mergedReferencingCounts = projectNames.ToDictionary(
                assemblyId => assemblyId,
                assemblyId => mergedReferencingAssemblies.TryGetValue(assemblyId, out var referencingMap) ? referencingMap.Count : 0,
                StringComparer.OrdinalIgnoreCase);

            RewriteTopReferencedAssemblies(websiteDestinationFolder, mergedReferencingCounts);
            RewriteProjectMapReferencingCounts(websiteDestinationFolder, projectNames, mergedReferencingCounts);
        }

        /// <summary>
        /// Rewrites <see cref="Constants.TopReferencedAssemblies"/> (originally written by
        /// <see cref="SolutionFinalizer"/>'s constructor from the primary config's own edges only) using
        /// the merged, cross-config referencing counts.
        /// </summary>
        private static void RewriteTopReferencedAssemblies(string websiteDestinationFolder, IReadOnlyDictionary<string, int> mergedReferencingCounts)
        {
            var mostReferencedProjects = mergedReferencingCounts
                .OrderByDescending(kvp => kvp.Value)
                .Select(kvp => kvp.Key + ";" + kvp.Value)
                .Take(100)
                .ToArray();

            var filePath = Path.Combine(websiteDestinationFolder, Constants.TopReferencedAssemblies + ".txt");
            File.WriteAllLines(filePath, mostReferencedProjects);
        }

        /// <summary>
        /// Rewrites the referencing-assembly counts embedded in <see cref="Constants.MasterAssemblyMap"/>
        /// (written by <see cref="SolutionFinalizer.CreateProjectMap"/> from the primary config's own edges
        /// only) using the merged, cross-config counts. <c>ProjectInfoLine</c> (the project's source path)
        /// doesn't vary by config -- it's the same project/csproj regardless of which config compiled it --
        /// so it's read straight from the already-finalized per-project ProjectInfo.txt rather than
        /// re-derived.
        /// </summary>
        private static void RewriteProjectMapReferencingCounts(
            string websiteDestinationFolder,
            IReadOnlyList<string> projectNames,
            IReadOnlyDictionary<string, int> mergedReferencingCounts)
        {
            var assembliesAndProjects = new List<Tuple<string, string>>();
            var repoAndSolutionNamesByAssembly = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);
            var repoChainByAssembly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assemblyId in projectNames)
            {
                var projectInfoFile = Path.Combine(websiteDestinationFolder, assemblyId, Constants.ProjectInfoFileName + ".txt");
                string projectInfoLine = null;
                if (File.Exists(projectInfoFile))
                {
                    var lines = File.ReadAllLines(projectInfoFile);
                    projectInfoLine = Serialization.ReadValue(lines, "ProjectSourcePath");
                    repoAndSolutionNamesByAssembly[assemblyId] = Tuple.Create(
                        Serialization.ReadValue(lines, "RepoName") ?? "",
                        Serialization.ReadValue(lines, "SolutionName") ?? "");
                    repoChainByAssembly[assemblyId] = Serialization.ReadValue(lines, "RepoChain") ?? "";
                }

                assembliesAndProjects.Add(Tuple.Create(assemblyId, projectInfoLine));
            }

            // This rewrite recomputes the cross-config MERGED referencing counts (mergedReferencingCounts
            // above) and must carry forward the repo/solution tags FinalizeProjects' own CreateProjectMap
            // already wrote into this same Assemblies.txt -- otherwise this second write silently wipes
            // them back out, exactly the kind of drop ComputeMergedSolutionExplorerRoot's ReadRepoName fix
            // was written to prevent for SolutionExplorer.html.
            Serialization.WriteProjectMap(websiteDestinationFolder, assembliesAndProjects, mergedReferencingCounts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), repoAndSolutionNamesByAssembly, repoChainByAssembly);
        }
    }
}
