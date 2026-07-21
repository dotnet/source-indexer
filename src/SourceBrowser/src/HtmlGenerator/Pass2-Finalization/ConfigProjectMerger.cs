using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    /// <summary>
    /// Assembles the full per-project merged model a config-aware finalization needs, by discovering
    /// which configs actually produced a given project and running the three existing mergers
    /// (<see cref="ConfigLocationMerger"/>, <see cref="ConfigReferenceMerger"/>, and the
    /// assembly-reference-edge merge below) over the real obj/&lt;config&gt;/&lt;project&gt; data read via
    /// <see cref="ConfigDataReader"/>.
    ///
    /// Important, verified-in-code distinction: "Used By" (the backlink block <see
    /// cref="SolutionFinalizer"/> injects into ProjectExplorer.html) is NOT driven by per-symbol <see
    /// cref="Reference"/> records -- it's driven by <see cref="Constants.ReferencedAssemblyList"/>
    /// ("References.txt"), which <see cref="ProjectGenerator.GenerateReferencedAssemblyList"/> writes
    /// from Roslyn's <c>Project.ProjectReferences</c>/<c>Project.MetadataReferences</c>, i.e. the
    /// MSBuild-level project/assembly reference graph. That graph CAN legitimately differ per config
    /// (e.g. a project's .csproj conditionally references a Windows-only helper project only when
    /// building the "windows" config) -- which is exactly the scenario Used-By must merge correctly:
    /// an unchanged assembly's Used-By must still pick up a new referencing edge that only exists under
    /// one config. So the Used-By merge below operates on assembly-level reference EDGES, config-tagged,
    /// entirely separately from the symbol-level FAR merge (<see cref="ConfigReferenceMerger"/>), which
    /// is unrelated to Used-By and only feeds per-symbol reference/FAR pages.
    /// </summary>
    public static class ConfigProjectMerger
    {
        public sealed class MergedProjectData
        {
            public string AssemblyId { get; }

            /// <summary>Every config that actually produced this project (assembly may not exist in all configs).</summary>
            public IReadOnlyList<string> Configs { get; }

            /// <summary>symbolId -> config-tagged declaration locations, from <see cref="ConfigLocationMerger"/>.</summary>
            public Dictionary<string, List<ConfigTaggedLocation>> DeclarationLocations { get; }

            /// <summary>target symbolId -> config-tagged references to it, from <see cref="ConfigReferenceMerger"/>.</summary>
            public Dictionary<string, List<Reference>> References { get; }

            /// <summary>
            /// Assembly names this project references (Roslyn project/metadata references), each tagged
            /// with the configs under which that edge exists. Drives the cross-config "Used By" merge --
            /// see the type-level remarks for why this, not <see cref="References"/>, is the Used-By
            /// source of truth.
            /// </summary>
            public Dictionary<string, HashSet<string>> ReferencedAssemblies { get; }

            public MergedProjectData(
                string assemblyId,
                IReadOnlyList<string> configs,
                Dictionary<string, List<ConfigTaggedLocation>> declarationLocations,
                Dictionary<string, List<Reference>> references,
                Dictionary<string, HashSet<string>> referencedAssemblies)
            {
                AssemblyId = assemblyId;
                Configs = configs;
                DeclarationLocations = declarationLocations;
                References = references;
                ReferencedAssemblies = referencedAssemblies;
            }
        }

        /// <summary>
        /// The union of project (assembly) folder names across every config's obj/&lt;config&gt; -- a
        /// project need not exist in every config (e.g. a platform-specific helper project that's only
        /// part of the "windows" build).
        /// </summary>
        /// <param name="configObjRoots">configName -> that config's obj/&lt;config&gt; root path.</param>
        public static IReadOnlyList<string> DiscoverProjects(IReadOnlyDictionary<string, string> configObjRoots)
        {
            var projectNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var objRoot in configObjRoots.Values)
            {
                if (!Directory.Exists(objRoot))
                {
                    continue;
                }

                foreach (var directory in Directory.GetDirectories(objRoot))
                {
                    var referencesFolder = Path.Combine(directory, Constants.ReferencesFileName);
                    if (Directory.Exists(referencesFolder))
                    {
                        projectNames.Add(Path.GetFileName(directory));
                    }
                }
            }

            return projectNames.ToArray();
        }

        /// <summary>
        /// Builds the full merged model for one project, reading whichever configs actually produced it
        /// (a config missing this project entirely contributes nothing, rather than being an error --
        /// this is the normal case for a platform-specific project).
        /// </summary>
        /// <param name="configObjRoots">configName -> that config's obj/&lt;config&gt; root path.</param>
        public static MergedProjectData MergeProject(string assemblyId, IReadOnlyDictionary<string, string> configObjRoots)
        {
            var perConfigDeclarationMaps = new Dictionary<string, Dictionary<string, List<Tuple<string, long>>>>(StringComparer.Ordinal);
            var perConfigReferences = new Dictionary<string, Dictionary<string, List<Reference>>>(StringComparer.Ordinal);
            var perConfigReferencedAssemblies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var configsWithThisProject = new List<string>();

            foreach (var kvp in configObjRoots)
            {
                var configName = kvp.Key;
                var projectFolder = Path.Combine(kvp.Value, assemblyId);
                if (!Directory.Exists(projectFolder))
                {
                    continue;
                }

                configsWithThisProject.Add(configName);

                var declarationMapFile = Path.Combine(projectFolder, Constants.DeclarationMap + ".txt");
                perConfigDeclarationMaps[configName] = ConfigDataReader.ReadDeclarationMap(declarationMapFile);

                var referencesFolder = Path.Combine(projectFolder, Constants.ReferencesFileName);
                perConfigReferences[configName] = ConfigDataReader.ReadReferenceShards(referencesFolder);

                var referencedAssemblyListFile = Path.Combine(projectFolder, Constants.ReferencedAssemblyList + ".txt");
                perConfigReferencedAssemblies[configName] = File.Exists(referencedAssemblyListFile)
                    ? File.ReadAllLines(referencedAssemblyListFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()
                    : Array.Empty<string>();
            }

            configsWithThisProject.Sort(StringComparer.Ordinal);

            return new MergedProjectData(
                assemblyId,
                configsWithThisProject,
                ConfigLocationMerger.Merge(perConfigDeclarationMaps),
                ConfigReferenceMerger.Merge(perConfigReferences),
                MergeReferencedAssemblies(perConfigReferencedAssemblies));
        }

        /// <summary>
        /// Merges N configs' assembly-reference-edge lists (each config's References.txt) into one
        /// config-tagged edge set: referenced assembly name -> the configs whose edge to it exists. An
        /// edge present under every config where this project exists is tagged with all of them (the
        /// common case, where the config tag is inert); an edge that's config-conditional keeps only the
        /// configs that actually declare it.
        /// </summary>
        private static Dictionary<string, HashSet<string>> MergeReferencedAssemblies(
            IReadOnlyDictionary<string, IReadOnlyList<string>> perConfigReferencedAssemblies)
        {
            var merged = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var configName in perConfigReferencedAssemblies.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                foreach (var assemblyName in perConfigReferencedAssemblies[configName])
                {
                    if (!merged.TryGetValue(assemblyName, out var configs))
                    {
                        configs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        merged.Add(assemblyName, configs);
                    }

                    configs.Add(configName);
                }
            }

            return merged;
        }
    }
}
