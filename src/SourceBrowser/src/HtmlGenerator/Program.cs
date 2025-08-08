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
using System.Threading.Tasks;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using Microsoft.SourceBrowser.BinLogParser;
using Microsoft.SourceBrowser.Common;
using Microsoft.Extensions.Hosting;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class Program
    {
        public static IOutputSystem OutputSystem { get; private set; }

        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyLoad += (s, e) =>
            {
                Console.WriteLine($"Assembly Load: {e.LoadedAssembly.GetName().Name} from {e.LoadedAssembly.Location}");
            };
            // This loads the real MSBuild from the toolset so that all targets and SDKs can be found
            // as if a real build is happening
            MSBuildLocator.RegisterDefaults();
            RealMain(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RealMain(string[] args)
        {
            var options = CommandLineOptions.Parse(args);

            if (options.Projects.Count == 0)
            {
                PrintUsage();
                return;
            }

            // Initialize output system based on options
            if (!string.IsNullOrEmpty(options.AzureBlobContainer))
            {
                BlobServiceClient blobServiceClient = CreateBlobServiceClient("sourceindex-blobs");

                if (blobServiceClient == null)
                {
                    Console.WriteLine("ERROR: Azure Blob Storage client not initialized. Please configure the BlobServiceClient.");
                    Console.WriteLine("Container name: " + options.AzureBlobContainer);
                    return;
                }

                OutputSystem = new AzureBlobOutputSystem(blobServiceClient, options.AzureBlobContainer);
                Console.WriteLine($"Using Azure Blob Storage container: {options.AzureBlobContainer}");    
            }
            else
            {
                OutputSystem = new LocalFileOutputSystem();
                Console.WriteLine("Using local file system output");
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

            if (string.IsNullOrEmpty(options.AzureBlobContainer))
            {
                Paths.SolutionDestinationFolder = options.SolutionDestinationFolder;
            }
            SolutionGenerator.LoadPlugins = options.LoadPlugins;
            SolutionGenerator.ExcludeTests = options.ExcludeTests;

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

            OutputHelper.CreateDirectory(Paths.SolutionDestinationFolder);

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

                IndexSolutions(options.Projects, options.Properties, federation, options.ServerPathMappings, options.PluginBlacklist, options.DoNotIncludeReferencedProjects, options.RootPath,
                    options.IncludeSourceGeneratedDocuments);
                FinalizeProjects(options.EmitAssemblyList, federation);
                WebsiteFinalizer.Finalize(websiteDestination, options.EmitAssemblyList, federation);
            }
            Log.Close();
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: HtmlGenerator "
                + "[/out:<outputdirectory>] "
                + "[/azureblob:<containername>] "
                + "[/force] "
                + "[/useplugins] "
                + "[/noplugins] "
                + "[/noplugin:Git] "
                + "<pathtosolution1.csproj|vbproj|sln|binlog|buildlog|dll|exe> [more solutions/projects..] "
                + "[/root:<root folder to enable relative .sln folders>] "
                + "[/in:<filecontaingprojectlist>] "
                + "[/nobuiltinfederations] "
                + "[/offlinefederation:server=assemblyListFile] "
                + "[/assemblylist]"
                + "[/excludetests]"
                + "[/excludeSourceGeneratedDocuments]" +
                "" +
                "Plugins are now off by default.");
        }

        /// <summary>
        /// Creates a BlobServiceClient configured with the connection string from Aspire service discovery.
        /// This replicates the behavior of AddAzureBlobServiceClient for .NET Framework 4.7.2 compatibility.
        /// </summary>
        /// <param name="connectionName">The connection name (e.g., "sourceindex-blobs")</param>
        /// <returns>A configured BlobServiceClient, or null if no connection string is found</returns>
        private static BlobServiceClient CreateBlobServiceClient(string connectionName)
        {
            if (string.IsNullOrEmpty(connectionName))
            {
                throw new ArgumentException("Connection name cannot be null or empty", nameof(connectionName));
            }

            // Aspire injects connection strings as environment variables in the format:
            // ConnectionStrings__<connection-name> where hyphens are converted to underscores
            var environmentVariableName = $"ConnectionStrings__{connectionName}";
            var connectionString = Environment.GetEnvironmentVariable(environmentVariableName);

            if (!string.IsNullOrEmpty(connectionString))
            {
                // Handle both connection string and service URI formats
                if (Uri.TryCreate(connectionString, UriKind.Absolute, out var serviceUri))
                {
                    // If it's a URI, create client with the service URI
                    Console.WriteLine($"Using service URI: {serviceUri}");
                    return new BlobServiceClient(serviceUri);
                }
                else
                {
                    // If it's a connection string, create client with the connection string
                    Console.WriteLine("Using connection string for BlobServiceClient");
                    return new BlobServiceClient(connectionString);
                }
            }

            // Fallback: try standard ConnectionStrings format (e.g., from appsettings.json)
            var standardVariableName = $"ConnectionStrings__{connectionName}";
            connectionString = Environment.GetEnvironmentVariable(standardVariableName);
            
            if (!string.IsNullOrEmpty(connectionString))
            {
                Console.WriteLine($"Found connection string for '{connectionName}' in environment variable '{standardVariableName}'");
                return new BlobServiceClient(connectionString);
            }

            // Final fallback: look for common Azure Storage environment variables
            var azureStorageConnectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
            if (!string.IsNullOrEmpty(azureStorageConnectionString))
            {
                Console.WriteLine("Using AZURE_STORAGE_CONNECTION_STRING environment variable");
                return new BlobServiceClient(azureStorageConnectionString);
            }

            Console.WriteLine($"WARNING: No connection string found for '{connectionName}'. Tried environment variables:");
            Console.WriteLine($"  - {environmentVariableName}");
            Console.WriteLine($"  - {standardVariableName}");
            Console.WriteLine($"  - AZURE_STORAGE_CONNECTION_STRING");
            Console.WriteLine("Available environment variables:");
            
            // List all environment variables that might be related to connection strings
            foreach (System.Collections.DictionaryEntry env in Environment.GetEnvironmentVariables())
            {
                var key = env.Key.ToString();
                if (key.Contains("ConnectionString") || key.Contains("STORAGE") || key.Contains("BLOB"))
                {
                    Console.WriteLine($"  - {key}");
                }
            }

            return null;
        }

        private static readonly Folder<ProjectSkeleton> mergedSolutionExplorerRoot = new Folder<ProjectSkeleton>();

        private static IEnumerable<string> GetAssemblyNames(string filePath)
        {
            if (filePath.EndsWith(".binlog", System.StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".buildlog", System.StringComparison.OrdinalIgnoreCase))
            {
                var invocations = BinLogCompilerInvocationsReader.ExtractInvocations(filePath);
                return invocations.Select(i => Path.GetFileNameWithoutExtension(i.Parsed.OutputFileName)).ToArray();
            }

            return AssemblyNameExtractor.GetAssemblyNames(filePath);
        }

        private static void IndexSolutions(
            IEnumerable<string> solutionFilePaths,
            IReadOnlyDictionary<string, string> properties,
            Federation federation,
            IReadOnlyDictionary<string, string> serverPathMappings,
            IEnumerable<string> pluginBlacklist,
            bool doNotIncludeReferencedProjects = false,
            string rootPath = null,
            bool includeSourceGeneratedDocuments = true)
        {
            var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in solutionFilePaths)
            {
                using (Disposable.Timing("Reading assembly names from " + path))
                {
                    foreach (var assemblyName in GetAssemblyNames(path))
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
                            GenerateFromBuildLog.GenerateInvocation(
                                invocation,
                                serverPathMappings,
                                processedAssemblyList,
                                assemblyNames,
                                solutionFolder,
                                typeForwards);
                        }
                        
                        continue;
                    }

                    using (var solutionGenerator = new SolutionGenerator(
                        path,
                        Paths.SolutionDestinationFolder,
                        properties: properties.ToImmutableDictionary(),
                        federation: federation,
                        serverPathMappings: serverPathMappings,
                        pluginBlacklist: pluginBlacklist,
                        doNotIncludeReferencedProjects: doNotIncludeReferencedProjects,
                        includeSourceGeneratedDocuments: includeSourceGeneratedDocuments,
                        typeForwards: typeForwards))
                    {
                        solutionGenerator.GlobalAssemblyList = assemblyNames;
                        solutionGenerator.Generate(processedAssemblyList, solutionFolder);
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
            var source = Path.Combine(destinationFolder, "wwwroot", "overview.html");
            var dst = Path.Combine(destinationFolder, "index", "overview.html");
            if (File.Exists(source))
            {
                var text = File.ReadAllText(source);
                text = StampOverviewHtmlText(text);
                OutputHelper.WriteAllText(dst, text);
            }
        }

        private static string StampOverviewHtmlText(string text)
        {
            return text.Replace("$(Date)", DateTime.Today.ToString("MMMM d", CultureInfo.InvariantCulture));
        }

        private static void ToggleSolutionExplorerOff(string destinationFolder)
        {
            var source = Path.Combine(destinationFolder, "wwwroot/scripts.js");
            var dst = Path.Combine(destinationFolder, "index/scripts.js");
            if (File.Exists(source))
            {
                var text = File.ReadAllText(source);
                text = text.Replace("/*USE_SOLUTION_EXPLORER*/true/*USE_SOLUTION_EXPLORER*/", "false");
                OutputHelper.WriteAllText(dst, text);
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
                    OutputHelper.WriteAllText(dst, text);
                }
            }
        }
    }
}
