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

            AssertTraceListener.Register();
            AppDomain.CurrentDomain.FirstChanceException += FirstChanceExceptionHandler.HandleFirstChanceException;

            if (Paths.SolutionDestinationFolder == null)
            {
                Paths.SolutionDestinationFolder = Path.Combine(Microsoft.SourceBrowser.Common.Paths.BaseAppFolder, "index");
            }

            var websiteDestination = Paths.SolutionDestinationFolder;

            // Warning, this will delete and recreate your destination folder
            Paths.PrepareDestinationFolder(options.Force);

            Paths.SolutionDestinationFolder = Path.Combine(Paths.SolutionDestinationFolder, "index"); //The actual index files need to be written to the "index" subdirectory

            Directory.CreateDirectory(Paths.SolutionDestinationFolder);

            Log.ErrorLogFilePath = Path.Combine(Paths.SolutionDestinationFolder, Log.ErrorLogFile);
            Log.MessageLogFilePath = Path.Combine(Paths.SolutionDestinationFolder, Log.MessageLogFile);

            using (Disposable.Timing("Generating website"))
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

                using (var cts = new CancellationTokenSource())
                {
                    Console.CancelKeyPress += (sender, eventArgs) =>
                    {
                        Console.WriteLine("Cancellation requested...");
                        cts.Cancel();
                        eventArgs.Cancel = true;
                    };

                    await IndexSolutionsAsync(options.Projects, options.Properties, federation, options.ServerPathMappings, options.PluginBlacklist, cts.Token, options.DoNotIncludeReferencedProjects, options.RootPath,
                        options.IncludeSourceGeneratedDocuments);
                }
                FinalizeProjects(options.EmitAssemblyList, federation);
                WebsiteFinalizer.Finalize(websiteDestination, options.EmitAssemblyList, federation);
            }
            Log.Close();

            // Surface a non-zero exit code when any severe error was logged so callers (notably CI that
            // reindexes on a schedule) can tell a run that limped to the end apart from a clean one.
            return Log.ErrorCount > 0 ? 1 : 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: HtmlGenerator "
                + "[/out:<outputdirectory>] "
                + "[/force] "
                + "[/useplugins] "
                + "[/noplugins] "
                + "[/noplugin:Git] "
                + "<pathtosolution1.csproj|vbproj|sln|slnx|binlog|buildlog|dll|exe> [more solutions/projects..] "
                + "[/root:<root folder to enable relative .sln/.slnx folders>] "
                + "[/in:<filecontaingprojectlist>] "
                + "[/nobuiltinfederations] "
                + "[/offlinefederation:server=assemblyListFile] "
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

            var domain = AppDomain.CreateDomain("TypeForwards");
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
                    GetTypeForwards(path, properties, typeForwards, domain);
                }
            }
            AppDomain.Unload(domain);
            domain = null;

            foreach (var path in solutionFilePaths)
            {
                var solutionFolder = mergedSolutionExplorerRoot;

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
                                includeSourceGeneratedDocuments);
                        }
                        
                        continue;
                    }

                    using (var solutionGenerator = await SolutionGenerator.CreateAsync(
                        path,
                        Paths.SolutionDestinationFolder,
                        cancellationToken,
                        properties: properties.ToImmutableDictionary(),
                        federation: federation,
                        serverPathMappings: serverPathMappings,
                        pluginBlacklist: pluginBlacklist,
                        doNotIncludeReferencedProjects: doNotIncludeReferencedProjects,
                        includeSourceGeneratedDocuments: includeSourceGeneratedDocuments,
                        typeForwards: typeForwards))
                    {
                        solutionGenerator.GlobalAssemblyList = assemblyNames;
                        await solutionGenerator.GenerateAsync(cancellationToken, processedAssemblyList, solutionFolder);
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        private static void GetTypeForwards(string path, IReadOnlyDictionary<string, string> properties, Dictionary<(string, string), string> typeForwards, AppDomain domain)
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
                var obj = (TypeForwardReader) domain.CreateInstanceFromAndUnwrap(Assembly.GetEntryAssembly().CodeBase,
                    "Microsoft.SourceBrowser.HtmlGenerator.TypeForwardReader");
                var forwards = obj.GetTypeForwards(path, properties);
                foreach (var forward in forwards)
                {
                    typeForwards[ValueTuple.Create(forward.Item1, forward.Item2)] = forward.Item3;
                }
            }
        }

        private static void FinalizeProjects(bool emitAssemblyList, Federation federation)
        {
            GenerateLooseFilesProject(Constants.MSBuildFiles, Paths.SolutionDestinationFolder);
            GenerateLooseFilesProject(Constants.TypeScriptFiles, Paths.SolutionDestinationFolder);
            using (Disposable.Timing("Finalizing references"))
            {
                try
                {
                    var solutionFinalizer = new SolutionFinalizer(Paths.SolutionDestinationFolder);
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
        public static void Finalize(string destinationFolder, bool emitAssemblyList, Federation federation)
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

            if (emitAssemblyList)
            {
                ToggleSolutionExplorerOff(destinationFolder);
            }

            SetExternalUrlMap(destinationFolder, federation);
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

        private static void ToggleSolutionExplorerOff(string destinationFolder)
        {
            var source = Path.Combine(destinationFolder, "wwwroot/scripts.js");
            var dst = Path.Combine(destinationFolder, "index/scripts.js");
            if (File.Exists(source))
            {
                var text = File.ReadAllText(source);
                text = text.Replace("/*USE_SOLUTION_EXPLORER*/true/*USE_SOLUTION_EXPLORER*/", "false");
                File.WriteAllText(dst, text);
            }
        }

        private static void SetExternalUrlMap(string destinationFolder, Federation federation)
        {
            var source = Path.Combine(destinationFolder, "wwwroot/scripts.js");
            var dst = Path.Combine(destinationFolder, "index/scripts.js");
            if (File.Exists(source))
            {
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
                    var text = File.ReadAllText(source);
                    text = Regex.Replace(text, @"/\*EXTERNAL_URL_MAP\*/.*/\*EXTERNAL_URL_MAP\*/", sb.ToString());
                    File.WriteAllText(dst, text);
                }
            }
        }
    }
}
