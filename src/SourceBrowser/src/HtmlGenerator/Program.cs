using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.BinLogParser;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class Program
    {
        private static async Task<int> Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyLoad += (s, e) =>
            {
                Console.WriteLine($"Assembly Load: {e.LoadedAssembly.GetName().Name} from {e.LoadedAssembly.Location}");
            };
            // This loads the real MSBuild from the toolset so that all targets and SDKs can be found
            // as if a real build is happening
            MSBuildLocator.RegisterDefaults();
            return await RealMain(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<int> RealMain(string[] args)
        {
            var options = CommandLineOptions.Parse(args);

            if (options.MergeConfigsOnly)
            {
                // The standalone merge invocation needs no projects, no MSBuild, no Pass1 at all -- it
                // only reads whatever /config:<name> runs have already staged under this /out's
                // obj/<config> folders. This is what a distributed CI aggregation job calls after
                // collecting each per-platform job's staging as an artifact onto one /out.
                return RunMergeConfigsOnly(options);
            }

            if (options.Projects.Count == 0)
            {
                PrintUsage();
                Log.Close();
                return 1;
            }

            var msbuildAssembly = typeof(Project).Assembly;
            var version = FileVersionInfo.GetVersionInfo(msbuildAssembly.Location);
            Console.WriteLine($"Using msbuild version {version.FileVersion} from {msbuildAssembly.Location}");
            Console.WriteLine();
            var msbuildDir = Path.GetDirectoryName(msbuildAssembly.Location);
            foreach (var dll in Directory.EnumerateFiles(msbuildDir, "*.dll"))
            {
                Console.WriteLine($"MSBuild Assembly: {Path.GetFileName(dll)}");
            }

            Paths.SolutionDestinationFolder = options.SolutionDestinationFolder;
            SolutionGenerator.LoadPlugins = options.LoadPlugins;
            SolutionGenerator.ExcludeTests = options.ExcludeTests;
            Log.SuppressWarnings = options.SuppressWarnings;
            Configuration.Incremental = options.Incremental;

            AssertTraceListener.Register();
            AppDomain.CurrentDomain.FirstChanceException += FirstChanceExceptionHandler.HandleFirstChanceException;

            if (Paths.SolutionDestinationFolder == null)
            {
                Paths.SolutionDestinationFolder = Path.Combine(Microsoft.SourceBrowser.Common.Paths.BaseAppFolder, "index");
            }

            var runOutputRoot = Paths.SolutionDestinationFolder;

            // Warning, unless /incremental is passed, this will delete and recreate your destination folder
            Paths.PrepareDestinationFolder(options.Force, options.Incremental);

            // The finalized website is still written to the "index" subdirectory, exactly as before, so
            // nothing about the served output's location changes. Pass1, however, now writes its raw,
            // per-assembly index to a separate "obj" subdirectory that Pass2 treats as read-only input --
            // Pass2 copies each assembly's folder from "obj" into "index" before finalizing it, so "obj"
            // stays a pure, re-derivable artifact that no later step mutates in place.
            //
            // When /config:<name> is set, Pass1's raw staging is further namespaced to obj/<config> so
            // that N separately-invoked config runs never clobber each other's raw output on a shared
            // /out -- each config's Pass1 output is its own durable, independently-incremental artifact
            // (the existing per-project staleness key now keyed per (project, config), no new mechanism).
            // The served website ("index") stays a single shared location for every config: this is the
            // "one merged site, not partitioned per config" contract -- Pass2 (today) still writes the
            // single/default-config index exactly as before; the cross-config MERGE of declarations,
            // references, and file-render dedup across obj/<config1>, obj/<config2>, ... happens as a
            // separate step below (mirrored by /mergeConfigsOnly for a standalone invocation), never by
            // partitioning "index" itself.
            Paths.WebsiteDestinationFolder = Path.Combine(runOutputRoot, "index");
            Paths.SolutionDestinationFolder = string.IsNullOrEmpty(options.Config)
                ? Path.Combine(runOutputRoot, "obj")
                : Path.Combine(runOutputRoot, "obj", options.Config);

            Directory.CreateDirectory(Paths.SolutionDestinationFolder);
            Directory.CreateDirectory(Paths.WebsiteDestinationFolder);

            ConfigRegistry.EnsureConfigRegistered(runOutputRoot, options.Config, options.ConfigAxes);

            Log.ErrorLogFilePath = Path.Combine(Paths.WebsiteDestinationFolder, Log.ErrorLogFile);
            Log.MessageLogFilePath = Path.Combine(Paths.WebsiteDestinationFolder, Log.MessageLogFile);

            using (Disposable.Timing("Generating website"))
            {
                var federation = BuildFederation(options);

                using (var cts = new CancellationTokenSource())
                {
                    Console.CancelKeyPress += (sender, eventArgs) =>
                    {
                        Console.WriteLine("Cancellation requested...");
                        cts.Cancel();
                        eventArgs.Cancel = true;
                    };

                    await IndexSolutionsAsync(options.Projects, options.Properties, federation, options.ServerPathMappings, options.RepoPathMappings, options.PluginBlacklist, cts.Token, options.DoNotIncludeReferencedProjects, options.RootPath,
                        options.IncludeSourceGeneratedDocuments);
                }
                if (string.IsNullOrEmpty(options.Config))
                {
                    // Default (no /config) path: completely unchanged from before the config feature
                    // existed. Finalization happens directly, exactly as today -- this is what makes the
                    // no-config case trivially byte-identical (the code path is literally untouched).
                    FinalizeProjects(options.EmitAssemblyList, federation);
                    WebsiteFinalizer.Finalize(runOutputRoot, federation, options.ShowBranding);
                }
                else
                {
                    // Config mode is Pass1-ONLY here: per-project Pass2 finalization (SolutionFinalizer.
                    // FinalizeProjects / WebsiteFinalizer.Finalize) deletes/packs the very raw artifacts
                    // (DeclarationMap.txt, reference shards) the cross-config merge step needs to read --
                    // BackpatchUnreferencedDeclarations deletes DeclarationMap.txt after consuming it, and
                    // GenerateReferencesFilesFromShard opens each shard with FileOptions.DeleteOnClose.
                    // Running Pass2 per-config-invocation would both destroy that raw data before a later
                    // config's merge could read it AND clobber the shared "index/" with a last-config-wins
                    // single-config view instead of ever actually merging. So finalization is deferred
                    // entirely to the merge step below (RunConfigMergeIfNeeded), which decides whether to
                    // run today's ordinary single-config finalizer (only one config registered so far) or
                    // the config-aware merged finalizer (two or more registered).
                    RunConfigMergeIfNeeded(runOutputRoot, options.EmitAssemblyList, federation, options.ShowBranding);
                }
            }
            Log.Close();

            // Surface a non-zero exit code when any severe error was logged so callers (notably CI that
            // reindexes on a schedule) can tell a run that limped to the end apart from a clean one.
            return Log.ErrorCount > 0 ? 1 : 0;
        }

        /// <summary>
        /// The standalone /mergeConfigsOnly entry point: no MSBuild, no Pass1, just the guarded
        /// cross-config merge over whatever is already staged in obj/&lt;config&gt; under the given /out.
        /// </summary>
        private static int RunMergeConfigsOnly(CommandLineOptions options)
        {
            var runOutputRoot = options.SolutionDestinationFolder;
            if (string.IsNullOrEmpty(runOutputRoot))
            {
                Log.Write("/mergeConfigsOnly requires /out:<outputdirectory> to locate the staged configs.", ConsoleColor.Red);
                Log.Close();
                return 1;
            }

            Log.ErrorLogFilePath = Path.Combine(Path.Combine(runOutputRoot, "index"), Log.ErrorLogFile);
            Log.MessageLogFilePath = Path.Combine(Path.Combine(runOutputRoot, "index"), Log.MessageLogFile);

            var federation = BuildFederation(options);
            RunConfigMergeIfNeeded(runOutputRoot, options.EmitAssemblyList, federation, options.ShowBranding);
            Log.Close();
            return Log.ErrorCount > 0 ? 1 : 0;
        }

        private static Federation BuildFederation(CommandLineOptions options)
        {
            var federation = new Federation();

            if (!options.NoBuiltInFederations)
            {
                federation.AddFederations(Federation.DefaultFederatedIndexUrls);
            }

            federation.AddFederations(options.Federations);

            foreach (var entry in options.OfflineFederations)
            {
                federation.AddFederation(entry.Key, entry.Value);
            }

            return federation;
        }

        /// <summary>
        /// Shared by the /config:&lt;name&gt; auto-tail and the standalone /mergeConfigsOnly invocation.
        /// Guarded against a concurrent merge attempt against the same /out (ConfigMergeCoordinator.
        /// RunGuarded). Config mode defers ALL Pass2 finalization to this method (see the comment in
        /// Main): with exactly one config registered so far there is nothing to merge yet, so this runs
        /// today's ordinary single-config finalizer reading straight from that one config's obj/&lt;config&gt;
        /// -- byte-identical to the no-config path, satisfying "a single-config run should still just
        /// work." With two or more configs registered, the real cross-config merge is required.
        ///
        /// NOTE: with two or more configs registered, this reads all registered configs' obj/&lt;config&gt;
        /// raw declaration maps + reference shards (via <see cref="ConfigProjectMerger"/>) and finalizes
        /// through <see cref="ConfigAwareProjectFinalizer"/>: the primary config's raw output establishes
        /// real rendered HTML content (byte-identical to a single-config run for the non-divergent common
        /// case), then every project's "Used By" block is re-patched from the merged, config-tagged
        /// referenced-assembly edges across ALL configs. Re-rendering genuinely divergent per-file content
        /// (disambiguation pages, file-render dedup) remains a separate, tracked follow-up -- see
        /// <see cref="ConfigAwareProjectFinalizer"/>'s remarks.
        /// </summary>
        private static void RunConfigMergeIfNeeded(string outRoot, bool emitAssemblyList, Federation federation, bool showBranding)
        {
            ConfigMergeCoordinator.RunGuarded(outRoot, () =>
            {
                var configEntries = ConfigRegistry.GetRegisteredConfigEntries(outRoot);
                if (configEntries.Count == 0)
                {
                    return;
                }

                var configs = configEntries.Select(e => e.Name).ToList();

                Paths.WebsiteDestinationFolder = Path.Combine(outRoot, "index");
                Directory.CreateDirectory(Paths.WebsiteDestinationFolder);

                if (configs.Count == 1)
                {
                    // Nothing to merge yet -- finalize this one config's raw obj/<config> output exactly
                    // as the default/no-config path would, straight into the shared "index/".
                    Paths.SolutionDestinationFolder = Path.Combine(outRoot, "obj", configs[0]);
                    FinalizeProjects(emitAssemblyList, federation);
                    WebsiteFinalizer.Finalize(outRoot, federation, showBranding);
                    return;
                }

                Log.Message($"Merging {configs.Count} configs into the shared index: {string.Join(", ", configs)}");

                var configObjRoots = configs.ToDictionary(
                    c => c,
                    c => Path.Combine(outRoot, "obj", c),
                    StringComparer.Ordinal);

                var axisTagsByConfig = configEntries.ToDictionary(
                    e => e.Name,
                    e => e.AxisTags,
                    StringComparer.Ordinal);

                ConfigAwareProjectFinalizer.Finalize(configObjRoots, Paths.WebsiteDestinationFolder, emitAssemblyList, federation, axisTagsByConfig);
                WebsiteFinalizer.Finalize(outRoot, federation, showBranding);
            });
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: HtmlGenerator "
                + "[/out:<outputdirectory>] "
                + "[/force] "
                + "[/incremental] "
                + "[/config:<name>] "
                + "[/configAxes:<axis>=<value>;<axis>=<value>...] "
                + "[/mergeConfigsOnly] "
                + "[/useplugins] "
                + "[/noplugins] "
                + "[/noplugin:Git] "
                + "<pathtosolution1.csproj|vbproj|sln|slnx|binlog|buildlog|dll|exe> [more solutions/projects..] "
                + "[/root:<root folder to enable relative .sln/.slnx folders>] "
                + "[/in:<filecontaingprojectlist>] "
                + "[/nobuiltinfederations] "
                + "[/offlinefederation:server=assemblyListFile] "
                + "[/repoPath:\"local repo folder\"=\"repo display name\"] "
                + "[/repo:\"local repo folder\"=\"repo display name\"=\"root URL\"] "
                + "[/assemblylist]"
                + "[/excludetests]"
                + "[/excludeSourceGeneratedDocuments]"
                + "[/noWarnings]" +
                "" +
                "Plugins are now off by default.");
        }

        private static readonly Folder<ProjectSkeleton> mergedSolutionExplorerRoot = new Folder<ProjectSkeleton>();

        private static async Task<IEnumerable<string>> GetAssemblyNamesAsync(string filePath, CancellationToken cancellationToken)
        {
            if (filePath.EndsWith(".binlog", System.StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".buildlog", System.StringComparison.OrdinalIgnoreCase))
            {
                var invocations = BinLogCompilerInvocationsReader.ExtractInvocations(filePath);
                return invocations.Select(i => Path.GetFileNameWithoutExtension(i.Parsed.OutputFileName)).ToArray();
            }

            return await AssemblyNameExtractor.GetAssemblyNamesAsync(filePath, cancellationToken);
        }

        private static async Task IndexSolutionsAsync(
            IEnumerable<string> solutionFilePaths,
            IReadOnlyDictionary<string, string> properties,
            Federation federation,
            IReadOnlyDictionary<string, string> serverPathMappings,
            IReadOnlyDictionary<string, string> repoPathMappings,
            IEnumerable<string> pluginBlacklist,
            CancellationToken cancellationToken,
            bool doNotIncludeReferencedProjects = false,
            string rootPath = null,
            bool includeSourceGeneratedDocuments = true)
        {
            var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in solutionFilePaths)
            {
                using (Disposable.Timing("Reading assembly names from " + path))
                {
                    foreach (var assemblyName in await GetAssemblyNamesAsync(path, cancellationToken))
                    {
                        assemblyNames.Add(assemblyName);
                    }
                }
            }

            var processedAssemblyList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var typeForwards = new Dictionary<ValueTuple<string, string>, string>();

            foreach (var path in solutionFilePaths)
            {
                if (
                    path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    )
                {
                    continue;
                }
                using (Disposable.Timing($"Reading type forwards from {path}"))
                {
                    GetTypeForwards(path, properties, typeForwards);
                }
            }

            // Solution tag is auto-derived from each top-level input's file name when it's a
            // .sln/.slnx; standalone project/binlog inputs aren't part of a solution, so they
            // stay untagged. Repo tag is resolved by longest-prefix match of each input's folder
            // against /repoPath (or /repo) mappings; untagged when no mapping applies. Resolve
            // both up front (rather than per-iteration below) so we know, before building any
            // folder, whether the merged site actually spans more than one repo/solution.
            var pathTags = solutionFilePaths
                .Select(path => (Path: path, RepoName: GetRepoName(path, repoPathMappings), SolutionName: GetSolutionName(path)))
                .ToList();

            var distinctRepoCount = pathTags
                .Select(t => t.RepoName)
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var solutionCountsByRepo = pathTags
                .Where(t => !string.IsNullOrEmpty(t.RepoName))
                .GroupBy(t => t.RepoName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(t => t.SolutionName).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var (path, repoName, solutionName) in pathTags)
            {
                // Only introduce Repo/Solution grouping folders when the merged site actually has
                // more than one repo (or, within a repo, more than one solution) to distinguish --
                // keeps single-repo/untagged sites' Solution Explorer tree byte-identical to before
                // repo tagging existed. Untagged inputs stay flat at the top level even on a
                // multi-repo site, alongside the repo folders.
                var solutionFolder = GetSolutionExplorerGroupingFolder(
                    mergedSolutionExplorerRoot, repoName, solutionName, distinctRepoCount, solutionCountsByRepo);

                if (rootPath is object)
                {
                    var relativePath = Paths.MakeRelativeToFolder(Path.GetDirectoryName(path), rootPath);
                    var segments = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var segment in segments)
                    {
                        solutionFolder = solutionFolder.GetOrCreateFolder(segment);
                    }
                }

                using (Disposable.Timing("Generating " + path))
                {
                    if (path.EndsWith(".binlog", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".buildlog", StringComparison.OrdinalIgnoreCase))
                    {
                        var invocations = BinLogCompilerInvocationsReader.ExtractInvocations(path);
                        foreach (var invocation in invocations)
                        {
                            string projectFolder = Path.GetFileName(invocation.ProjectDirectory);
                            if (projectFolder == "ref" || projectFolder == "stubs")
                            {
                                Log.Write($"Skipping Ref Assembly project {invocation.ProjectFilePath}");
                                continue;
                            }

                            if (Path.GetFileName(Path.GetDirectoryName(invocation.ProjectDirectory)) == "cycle-breakers")
                            {
                                Log.Write($"Skipping Wpf Cycle-Breaker project {invocation.ProjectFilePath}");
                                continue;
                            }
                            Log.Write($"Indexing Project: {invocation.ProjectFilePath}");
                            await GenerateFromBuildLog.GenerateInvocationAsync(
                                invocation,
                                cancellationToken,
                                serverPathMappings,
                                processedAssemblyList,
                                assemblyNames,
                                solutionFolder,
                                typeForwards,
                                includeSourceGeneratedDocuments: includeSourceGeneratedDocuments,
                                repoName: repoName,
                                solutionName: solutionName);
                        }
                        
                        continue;
                    }

                    // Split out separately-timed sub-phases so an /incremental run's log can distinguish
                    // the MSBuildWorkspace load/compile cost (paid every run, regardless of staleness) from
                    // Pass1's actual per-assembly generation/write cost (what staleness gating elides for
                    // unchanged projects) -- see ProjectStaleness/ProjectGenerator.
                    SolutionGenerator solutionGenerator;
                    using (Disposable.Timing("Loading workspace for " + path))
                    {
                        solutionGenerator = await SolutionGenerator.CreateAsync(
                            path,
                            Paths.SolutionDestinationFolder,
                            cancellationToken,
                            properties: properties.ToImmutableDictionary(),
                            federation: federation,
                            serverPathMappings: serverPathMappings,
                            pluginBlacklist: pluginBlacklist,
                            doNotIncludeReferencedProjects: doNotIncludeReferencedProjects,
                            includeSourceGeneratedDocuments: includeSourceGeneratedDocuments,
                            typeForwards: typeForwards);
                    }

                    using (solutionGenerator)
                    {
                        solutionGenerator.GlobalAssemblyList = assemblyNames;
                        solutionGenerator.RepoName = repoName;
                        solutionGenerator.SolutionName = solutionName;
                        using (Disposable.Timing("Pass1 writing for " + path))
                        {
                            await solutionGenerator.GenerateAsync(cancellationToken, processedAssemblyList, solutionFolder);
                        }
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        private static void GetTypeForwards(string path, IReadOnlyDictionary<string, string> properties, Dictionary<(string, string), string> typeForwards)
        {
            if (path.EndsWith(".binlog", StringComparison.Ordinal) ||
                path.EndsWith(".buildlog", StringComparison.Ordinal))
            {
                var invocations = BinLogCompilerInvocationsReader.ExtractInvocations(path);
                var processed = new HashSet<string>();
                foreach (var invocation in invocations)
                {
                    if (!string.IsNullOrEmpty(invocation.OutputAssemblyPath) &&
                        File.Exists(invocation.OutputAssemblyPath) &&
                        processed.Add(invocation.OutputAssemblyPath))
                    {
                        var forwards = TypeForwardReader.ReadTypeForwardsFromAssembly(invocation.OutputAssemblyPath);
                        foreach (var forward in forwards)
                        {
                            typeForwards[ValueTuple.Create(forward.Item1, forward.Item2)] = forward.Item3;
                        }
                    }
                }

                return;
            }

            {
                var obj = new TypeForwardReader();
                var forwards = obj.GetTypeForwards(path, properties);
                foreach (var forward in forwards)
                {
                    typeForwards[ValueTuple.Create(forward.Item1, forward.Item2)] = forward.Item3;
                }
            }
        }

        /// <summary>Descends into (creating as needed) the Repo/Solution grouping folders for a
        /// single input, or returns <paramref name="root"/> unchanged when grouping doesn't apply.
        /// Public and static so it's independently unit-testable without needing a real
        /// solution/build. See the Solution Explorer tree design note on IndexSolutionsAsync's
        /// grouping loop for the byte-identical-by-default rationale.</summary>
        public static Folder<ProjectSkeleton> GetSolutionExplorerGroupingFolder(
            Folder<ProjectSkeleton> root,
            string repoName,
            string solutionName,
            int distinctRepoCount,
            IReadOnlyDictionary<string, int> solutionCountsByRepo)
        {
            var folder = root;

            if (distinctRepoCount > 1 && !string.IsNullOrEmpty(repoName))
            {
                folder = folder.GetOrCreateFolder(repoName);
                folder.Kind = FolderKind.Repo;
                folder.RepoName = repoName;

                if (solutionCountsByRepo.TryGetValue(repoName, out var solutionCount) &&
                    solutionCount > 1 && !string.IsNullOrEmpty(solutionName))
                {
                    folder = folder.GetOrCreateFolder(solutionName);
                    folder.Kind = FolderKind.Solution;
                    folder.RepoName = repoName;
                }
            }

            return folder;
        }

        private static string GetSolutionName(string path)
        {
            if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileNameWithoutExtension(path);
            }

            return string.Empty;
        }

        private static string GetRepoName(string path, IReadOnlyDictionary<string, string> repoPathMappings)
        {
            if (repoPathMappings == null || repoPathMappings.Count == 0)
            {
                return string.Empty;
            }

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                return string.Empty;
            }

            // Longest-prefix match, in case repo folders are nested.
            string bestMatch = null;
            foreach (var candidate in repoPathMappings.Keys)
            {
                if (Paths.IsOrContains(candidate, directory) &&
                    (bestMatch == null || candidate.Length > bestMatch.Length))
                {
                    bestMatch = candidate;
                }
            }

            return bestMatch != null ? repoPathMappings[bestMatch] : string.Empty;
        }

        private static void FinalizeProjects(bool emitAssemblyList, Federation federation)
        {
            GenerateLooseFilesProject(Constants.MSBuildFiles, Paths.SolutionDestinationFolder);
            GenerateLooseFilesProject(Constants.TypeScriptFiles, Paths.SolutionDestinationFolder);
            using (Disposable.Timing("Finalizing references"))
            {
                try
                {
                    var solutionFinalizer = new SolutionFinalizer(Paths.SolutionDestinationFolder, Paths.WebsiteDestinationFolder);
                    solutionFinalizer.FinalizeProjects(emitAssemblyList, federation, mergedSolutionExplorerRoot);
                }
                catch (Exception ex)
                {
                    Log.Exception(ex, "Failure while finalizing projects");
                }
            }
        }

        private static void GenerateLooseFilesProject(string projectName, string solutionDestinationPath)
        {
            var projectGenerator = new ProjectGenerator(projectName, solutionDestinationPath);
            projectGenerator.GenerateNonProjectFolder();
        }
    }

    internal static class WebsiteFinalizer
    {
        public static void Finalize(string destinationFolder, Federation federation, bool showBranding)
        {
            string sourcePath = Assembly.GetEntryAssembly().Location;
            sourcePath = Path.GetDirectoryName(sourcePath);
            string basePath = sourcePath;
            sourcePath = Path.Combine(sourcePath, "Web");
            if (!Directory.Exists(sourcePath))
            {
                return;
            }

            sourcePath = Path.GetFullPath(sourcePath);
            FileUtilities.CopyDirectory(sourcePath, destinationFolder);

            StampOverviewHtmlWithDate(destinationFolder);

            ApplyScriptsJsCustomizations(destinationFolder, federation, showBranding);
        }

        private static void StampOverviewHtmlWithDate(string destinationFolder)
        {
            var indexFolder = Path.Combine(destinationFolder, "index");
            var source = Path.Combine(destinationFolder, "wwwroot", "overview.html");
            var dst = Path.Combine(indexFolder, "overview.html");
            if (File.Exists(source))
            {
                var text = File.ReadAllText(source);
                text = StampOverviewHtmlText(text, indexFolder);
                File.WriteAllText(dst, text);
            }
        }

        private static string StampOverviewHtmlText(string text, string indexFolder)
        {
            // Assemblies.txt and Projects.txt are one line per indexed assembly/project and are written
            // during project finalization, before this runs, so their line counts are the run totals.
            // Assemblies with a project key of -1 are the synthetic loose-file containers (MSBuildFiles,
            // TypeScriptFiles) that the search UI itself excludes, so they are left out of the count too.
            var assemblyCount = CountAssemblies(Path.Combine(indexFolder, Constants.MasterAssemblyMap + ".txt"));
            var projectCount = CountLines(Path.Combine(indexFolder, Constants.MasterProjectMap + ".txt"));

            return text
                .Replace("$(Date)", DateTime.Today.ToString("MMMM d", CultureInfo.InvariantCulture))
                .Replace("$(IndexRunDate)", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))
                .Replace("$(SourceBrowserVersion)", GetSourceBrowserVersion())
                .Replace("$(ProjectCount)", projectCount.ToString("N0", CultureInfo.InvariantCulture))
                .Replace("$(AssemblyCount)", assemblyCount.ToString("N0", CultureInfo.InvariantCulture));
        }

        private static int CountAssemblies(string assembliesFilePath)
        {
            if (!File.Exists(assembliesFilePath))
            {
                return 0;
            }

            var count = 0;
            foreach (var line in File.ReadLines(assembliesFilePath))
            {
                // Lines are name;projectKey;referencingCount. Skip the synthetic loose-file containers
                // that carry a project key of -1.
                var parts = line.Split(';');
                if (parts.Length >= 2 && parts[1] != "-1")
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountLines(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return 0;
            }

            var count = 0;
            foreach (var line in File.ReadLines(filePath))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    count++;
                }
            }

            return count;
        }

        private static string GetSourceBrowserVersion()
        {
            var assembly = typeof(WebsiteFinalizer).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                // Drop the +<commit sha> source-revision suffix that the SDK appends, for readability.
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational.Substring(0, plus) : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "unknown";
        }

        // scripts.js exists in two places in the generated output: wwwroot/ (the byte-identical checked-in
        // template) and index/ (the finalized site under RootPath). The federation and /showBranding toggles
        // below both read wwwroot/scripts.js and rewrite the placeholders. They must be written back to BOTH
        // copies: SourceIndexServer's RootPath handler serves index/scripts.js when reachable, but the proxy
        // deployment (Helpers.ServeProxiedIndex) deliberately serves scripts.js and the other chrome locally
        // from wwwroot -- so writing only index/scripts.js let /showBranding (and federation URLs) silently
        // fall back to the un-rewritten wwwroot copy on source.dot.net. Composed into one read-modify sequence
        // so any combination of flags ends up in both files.
        private static void ApplyScriptsJsCustomizations(string destinationFolder, Federation federation, bool showBranding)
        {
            var source = Path.Combine(destinationFolder, "wwwroot/scripts.js");
            if (!File.Exists(source))
            {
                return;
            }

            var text = File.ReadAllText(source);
            var changed = false;

            var sb = new StringBuilder();
            foreach (var server in federation.GetServers())
            {
                if (sb.Length > 0)
                {
                    sb.Append(",");
                }

                sb.Append("\"");
                sb.Append(server);
                sb.Append("\"");
            }

            if (sb.Length > 0)
            {
                text = Regex.Replace(text, @"/\*EXTERNAL_URL_MAP\*/.*/\*EXTERNAL_URL_MAP\*/", sb.ToString());
                changed = true;
            }

            if (showBranding)
            {
                text = text.Replace("/*SHOW_BRANDING*/false/*SHOW_BRANDING*/", "true");
                changed = true;
            }

            if (changed)
            {
                File.WriteAllText(source, text);
                File.WriteAllText(Path.Combine(destinationFolder, "index/scripts.js"), text);
            }
        }
    }
}
