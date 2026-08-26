using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Basic.CompilerLog.Util;
using Microsoft.Build.Construction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class SolutionGenerator : IDisposable
    {
        public ImmutableDictionary<string, string> Properties { get; }
        public string SolutionSourceFolder { get; private set; }
        public string SolutionDestinationFolder { get; private set; }
        public string ProjectFilePath { get; private set; }
        public IReadOnlyDictionary<string, string> ServerPathMappings { get; set; }

        /// <summary>
        /// Optional repo display name tag applied to every assembly generated from this solution,
        /// set by the caller (see Program.IndexSolutionsAsync) via /repoPath or /repo. Empty when
        /// untagged, which is the default and keeps generated output unchanged. Used only as the
        /// fallback for projects not under any nested mapping -- see <see cref="ResolveRepoName"/>.
        /// </summary>
        public string RepoName { get; set; } = string.Empty;

        /// <summary>
        /// All /repoPath (and /repo) folder-to-name mappings, so each project's repo tag can be
        /// resolved from its own folder rather than inheriting the whole input's tag. Lets one input
        /// (e.g. a VMR) tag its sub-repo projects individually. Null/empty leaves every project on the
        /// <see cref="RepoName"/> fallback.
        /// </summary>
        public IReadOnlyDictionary<string, string> RepoPathMappings { get; set; }

        /// <summary>Site-wide distinct repo count and per-repo solution counts, computed once by the
        /// caller, used to decide whether Solution Explorer Repo/Solution grouping folders apply -- see
        /// Program.GetSolutionExplorerGroupingFolder.</summary>
        public int DistinctRepoCount { get; set; }
        public IReadOnlyDictionary<string, int> SolutionCountsByRepo { get; set; }

        /// <summary>Resolves the repo tag for a single project: most specific /repoPath mapping
        /// containing the project's folder wins, falling back to this solution's <see cref="RepoName"/>.</summary>
        public string ResolveRepoName(string projectFilePath)
            => Program.ResolveRepoName(projectFilePath, RepoPathMappings, RepoName);

        /// <summary>Resolves a single project's full repo ancestry (outermost first, own repo last)
        /// from the /repoPath mappings, so a parent repo can group its nested sub-repos. See
        /// <see cref="Program.ResolveRepoChain"/>.</summary>
        public IReadOnlyList<string> ResolveRepoChain(string projectFilePath)
            => Program.ResolveRepoChain(projectFilePath, RepoPathMappings, RepoName);

        /// <summary>
        /// Optional solution display name tag applied to every assembly generated from this
        /// solution, auto-derived from the top-level .sln/.slnx file name. Empty for standalone
        /// project/binlog inputs that aren't part of a solution.
        /// </summary>
        public string SolutionName { get; set; } = string.Empty;
        private Federation Federation { get; set; }
        public bool IncludeSourceGeneratedDocuments { get; }

        public IEnumerable<string> PluginBlacklist { get; private set; }
        private readonly HashSet<string> typeScriptFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public MEF.PluginAggregator PluginAggregator;
        public IReadOnlyDictionary<ValueTuple<string, string>, string> TypeForwards { get; }

        /// <summary>
        /// List of all assembly names included in the index, from all solutions
        /// </summary>
        public HashSet<string> GlobalAssemblyList { get; set; }

        private Solution solution;
        private Workspace workspace;

        // Kept alive for the lifetime of this generator when the input is a .complog: the Roslyn
        // documents produced by SolutionReader load their source text lazily through this reader,
        // so it must not be disposed until Pass1 generation has finished reading every document.
        private SolutionReader compilerLogReader;

        private SolutionGenerator(
            string solutionFilePath,
            string solutionDestinationFolder,
            ImmutableDictionary<string, string> properties,
            Federation federation,
            IReadOnlyDictionary<string, string> serverPathMappings,
            IEnumerable<string> pluginBlacklist,
            IReadOnlyDictionary<ValueTuple<string, string>, string> typeForwards,
            bool includeSourceGeneratedDocuments)
        {
            this.SolutionSourceFolder = Path.GetDirectoryName(solutionFilePath);
            this.SolutionDestinationFolder = solutionDestinationFolder;
            this.ProjectFilePath = solutionFilePath;
            ServerPathMappings = CopyServerPathMappings(serverPathMappings);
            this.Federation = federation ?? new Federation();
            this.PluginBlacklist = pluginBlacklist ?? Enumerable.Empty<string>();
            this.Properties = properties;
            this.TypeForwards = typeForwards ?? ImmutableDictionary<ValueTuple<string, string>, string>.Empty;
            this.IncludeSourceGeneratedDocuments = includeSourceGeneratedDocuments;
        }

        public static async Task<SolutionGenerator> CreateAsync(
            string solutionFilePath,
            string solutionDestinationFolder,
            CancellationToken cancellationToken,
            ImmutableDictionary<string, string> properties = null,
            Federation federation = null,
            IReadOnlyDictionary<string, string> serverPathMappings = null,
            IEnumerable<string> pluginBlacklist = null,
            IReadOnlyDictionary<ValueTuple<string, string>, string> typeForwards = null,
            bool doNotIncludeReferencedProjects = false,
            bool includeSourceGeneratedDocuments = true)
        {
            var solutionGenerator = new SolutionGenerator(
                solutionFilePath,
                solutionDestinationFolder,
                properties,
                federation,
                serverPathMappings,
                pluginBlacklist,
                typeForwards,
                includeSourceGeneratedDocuments
            );
            solutionGenerator.solution = await solutionGenerator.CreateSolutionAsync(solutionFilePath, cancellationToken, properties, doNotIncludeReferencedProjects);

            if (LoadPlugins)
            {
                solutionGenerator.SetupPluginAggregator();
            }

            return solutionGenerator;
        }

        public static bool LoadPlugins { get; set; }
        public static bool ExcludeTests { get; set; }

        private void SetupPluginAggregator()
        {
            if (!LoadPlugins)
            {
                return;
            }

            var settings = System.Configuration.ConfigurationManager.AppSettings;
            var configs = settings
                .AllKeys
                .Where(k => k.Contains(':'))                            //Ignore keys that don't have a colon to indicate which plugin they go to
                .Select(k => Tuple.Create(k.Split(':'), settings[k]))   //Get the data -- split the key to get the plugin name and setting name, look up the key to get the value
                .GroupBy(t => t.Item1[0])                               //Group the settings based on which plugin they're for
                .ToDictionary(
                    group => group.Key,                                 //Index the outer dictionary based on plugin
                    group => group.ToDictionary(
                        t => t.Item1[1],                                //Index the inner dictionary based on setting name
                        t => t.Item2                                    //The actual value of the setting
                    )
                );
            // Built-in plugins are registered explicitly here now that discovery no longer scans the
            // application directory. A run can still drop any of them by name via /noplugin:<Name>.
            var plugins = new MEF.ISourceBrowserPlugin[]
            {
                new GitGlyph.GitSourceBrowserPlugin(),
            };
            PluginAggregator = new MEF.PluginAggregator(plugins, configs, new Utilities.PluginLogger(), PluginBlacklist);
            FirstChanceExceptionHandler.IgnoreModules(PluginAggregator.Select(p => p.PluginModule));
            PluginAggregator.Init();
        }

        public SolutionGenerator(
            string projectFilePath,
            string commandLineArguments,
            string outputAssemblyPath,
            string solutionSourceFolder,
            string solutionDestinationFolder,
            IReadOnlyDictionary<ValueTuple<string, string>, string> typeForwards = null,
            bool includeSourceGeneratedDocuments = true)
        {
            this.Properties = ImmutableDictionary<string, string>.Empty;
            this.ProjectFilePath = projectFilePath;
            string projectName = Path.GetFileNameWithoutExtension(projectFilePath);
            string language = projectFilePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ?
                LanguageNames.VisualBasic : LanguageNames.CSharp;
            this.SolutionSourceFolder = solutionSourceFolder;
            this.SolutionDestinationFolder = solutionDestinationFolder;
            this.TypeForwards = typeForwards ?? ImmutableDictionary<ValueTuple<string, string>, string>.Empty;
            this.IncludeSourceGeneratedDocuments = includeSourceGeneratedDocuments;
            string projectSourceFolder = Path.GetDirectoryName(projectFilePath);
            SetupPluginAggregator();

            this.solution = CreateSolution(
                commandLineArguments,
                projectName,
                language,
                projectSourceFolder,
                outputAssemblyPath);
        }

        public IEnumerable<string> GetAssemblyNames()
        {
            if (solution != null)
            {
                return solution.Projects.Select(p => p.AssemblyName);
            }
            else
            {
                return Enumerable.Empty<string>();
            }
        }

        internal static MSBuildWorkspace CreateWorkspace(ImmutableDictionary<string, string> propertiesOpt = null)
        {
            propertiesOpt = propertiesOpt ?? ImmutableDictionary<string, string>.Empty;

            propertiesOpt = propertiesOpt.Add("AlwaysCompileMarkupFilesInSeparateDomain", "false");

            var w = MSBuildWorkspace.Create(properties: propertiesOpt);
            w.LoadMetadataForReferencedProjects = true;
            w.AssociateFileExtensionWithLanguage("depproj", LanguageNames.CSharp);
            return w;
        }

        /// <summary>
        /// Basic.CompilerLog's <c>SolutionReader</c> reports each project's AssemblyName with its file
        /// extension (e.g. "Foo.dll"), whereas the binlog/MSBuild path -- and the rest of SourceBrowser,
        /// which keys output folders, the global assembly list, and cross-assembly references off the
        /// bare name -- uses the extension-less form. This rewrites every project's AssemblyName to the
        /// extension-less form so a .complog input produces byte-for-byte the same output as the
        /// equivalent .binlog input.
        /// </summary>
        public static Microsoft.CodeAnalysis.SolutionInfo NormalizeCompilerLogAssemblyNames(Microsoft.CodeAnalysis.SolutionInfo solutionInfo)
        {
            var projectInfos = solutionInfo.Projects
                .Select(p => p.WithAssemblyName(Path.GetFileNameWithoutExtension(p.AssemblyName)))
                .ToList();
            return Microsoft.CodeAnalysis.SolutionInfo.Create(
                solutionInfo.Id, solutionInfo.Version, solutionInfo.FilePath, projectInfos);
        }

        /// <summary>
        /// Logs any diagnostics the compiler log reader produces for a .complog input so problems
        /// (for example a project whose generated sources were not persisted in the log) are visible
        /// in the indexing output instead of being silently dropped. Compilation data is read one
        /// project at a time so only a single compilation is materialized at once, keeping the memory
        /// cost bounded for large solutions.
        /// </summary>
        private void ReadCompilerLogMetadata(string compilerLogFilePath)
        {
            try
            {
                using var reader = CompilerLogReader.Create(compilerLogFilePath, BasicAnalyzerKind.None);
                foreach (var compilerCall in reader.ReadAllCompilerCalls(cc => cc.Kind == CompilerCallKind.Regular))
                {
                    // With BasicAnalyzerKind.None the generated sources must already be present in the
                    // log; if they are not, the indexed output for this project will be missing those
                    // files, so surface it rather than failing silently.
                    if (!reader.HasAllGeneratedFileContent(compilerCall))
                    {
                        Log.Message($"Compiler log '{compilerLogFilePath}' is missing generated source content for '{compilerCall.GetDiagnosticName()}'; generated files will not be indexed for that project.");
                    }

                    var data = reader.ReadCompilationData(compilerCall);
                    foreach (var diagnostic in data.CreationDiagnostics)
                    {
                        if (diagnostic.Severity == DiagnosticSeverity.Warning ||
                            diagnostic.Severity == DiagnosticSeverity.Error)
                        {
                            Log.Message($"Compiler log '{compilerLogFilePath}' diagnostic for '{compilerCall.GetDiagnosticName()}': {diagnostic}");
                        }
                    }

                    try
                    {
                        var pathMappings = compilerCall.IsCSharp
                            ? CSharpCommandLineParser.Default.Parse(reader.ReadRawArguments(compilerCall), compilerCall.ProjectDirectory, sdkDirectory: null).PathMap
                            : VisualBasicCommandLineParser.Default.Parse(reader.ReadRawArguments(compilerCall), compilerCall.ProjectDirectory, sdkDirectory: null).PathMap;
                        ServerPathMappings = AddCompilerLogServerPathMapping(
                            ServerPathMappings,
                            compilerLogFilePath,
                            pathMappings);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(
                            ex,
                            $"Failed to read path mappings for '{compilerCall.ProjectFilePath}' from '{compilerLogFilePath}'.",
                            isSevere: false);
                    }
                }
            }
            catch (Exception ex)
            {
                // Diagnostics logging is best-effort and must never block indexing of an otherwise
                // valid compiler log.
                Log.Exception(ex, "Failed to read diagnostics from compiler log: " + compilerLogFilePath, isSevere: false);
            }
        }

        internal static IReadOnlyDictionary<string, string> AddCompilerLogServerPathMapping(
            IReadOnlyDictionary<string, string> serverPathMappings,
            string compilerLogFilePath,
            IEnumerable<KeyValuePair<string, string>> compilerPathMappings)
        {
            var normalizedServerPathMappings = CopyServerPathMappings(serverPathMappings);
            var compilerLogDirectory = Path.GetDirectoryName(Path.GetFullPath(compilerLogFilePath));
            var standaloneStageOneSourceDirectory = Paths.EnsureTrailingSlash(
                Path.Combine(compilerLogDirectory, "src"));
            var configuredMapping = normalizedServerPathMappings
                .Where(mapping =>
                    Paths.IsOrContains(mapping.Key, compilerLogFilePath) ||
                    string.Equals(
                        Paths.EnsureTrailingSlash(Path.GetFullPath(mapping.Key)),
                        standaloneStageOneSourceDirectory,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(mapping => mapping.Key.Length)
                .FirstOrDefault();
            if (configuredMapping.Key == null)
            {
                return serverPathMappings;
            }

            var repositoryRoot = compilerPathMappings
                .Where(mapping => Path.IsPathRooted(mapping.Key))
                .FirstOrDefault(mapping =>
                    string.Equals(
                        mapping.Value.Replace('\\', '/').TrimEnd('/'),
                        "/_",
                        StringComparison.OrdinalIgnoreCase));
            if (repositoryRoot.Key == null)
            {
                return serverPathMappings;
            }

            normalizedServerPathMappings[Paths.EnsureTrailingSlash(Path.GetFullPath(repositoryRoot.Key))] = configuredMapping.Value;
            return normalizedServerPathMappings;
        }

        private static Dictionary<string, string> CopyServerPathMappings(
            IEnumerable<KeyValuePair<string, string>> serverPathMappings)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (serverPathMappings != null)
            {
                foreach (var mapping in serverPathMappings)
                {
                    result[mapping.Key] = mapping.Value;
                }
            }

            return result;
        }

        private static Solution CreateSolution(
            string commandLineArguments,
            string projectName,
            string language,
            string projectSourceFolder,
            string outputAssemblyPath)
        {
            var workspace = CreateWorkspace();
            var projectInfo = CommandLineProject.CreateProjectInfo(
                projectName,
                language,
                commandLineArguments,
                projectSourceFolder,
                workspace);
            var solution = workspace.CurrentSolution.AddProject(projectInfo);

            solution = RemoveNonExistingFiles(solution);
            solution = AddAssemblyAttributesFile(language, outputAssemblyPath, solution);
            solution = DisambiguateSameNameLinkedFiles(solution);
            solution = DeduplicateProjectReferences(solution);

            solution.Workspace.RegisterWorkspaceFailedHandler(args => WorkspaceFailed(args, solution.Workspace));

            return solution;
        }

        private static Solution DisambiguateSameNameLinkedFiles(Solution solution)
        {
            foreach (var projectId in solution.ProjectIds.ToArray())
            {
                var project = solution.GetProject(projectId);
                solution = DisambiguateSameNameLinkedFiles(project);
            }

            return solution;
        }

        /// <summary>
        /// If there are two linked files both outside the project cone, and they have same names,
        /// they will logically appear as the same file in the project root. To disambiguate, we
        /// remove both files from the project's root and re-add them each into a folder chain that
        /// is formed from the full path of each document.
        /// </summary>
        private static Solution DisambiguateSameNameLinkedFiles(Project project)
        {
            var nameMap = project.Documents.Where(d => !d.Folders.Any()).ToLookup(d => d.Name);
            foreach (var conflictedItemGroup in nameMap.Where(g => g.Count() > 1))
            {
                foreach (var conflictedDocument in conflictedItemGroup)
                {
                    project = project.RemoveDocument(conflictedDocument.Id);
                    string filePath = conflictedDocument.FilePath;
                    DocumentId newId = DocumentId.CreateNewId(project.Id, filePath);
                    var folders = filePath.Split('\\').Select(p => p.TrimEnd(':'));
                    project = project.Solution.AddDocument(
                        newId,
                        conflictedDocument.Name,
                        conflictedDocument.GetTextAsync().Result,
                        folders,
                        filePath).GetProject(project.Id);
                }
            }

            return project.Solution;
        }

        private static Solution RemoveNonExistingFiles(Solution solution)
        {
            foreach (var projectId in solution.ProjectIds.ToArray())
            {
                var project = solution.GetProject(projectId);
                solution = RemoveNonExistingDocuments(project);

                project = solution.GetProject(projectId);
                solution = RemoveNonExistingReferences(project);
            }

            return solution;
        }

        private static Solution RemoveNonExistingDocuments(Project project)
        {
            foreach (var documentId in project.DocumentIds.ToArray())
            {
                var document = project.GetDocument(documentId);
                if (!File.Exists(document.FilePath))
                {
                    Log.Message("Document doesn't exist on disk: " + document.FilePath);
                    project = project.RemoveDocument(documentId);
                }
            }

            return project.Solution;
        }

        private static Solution RemoveNonExistingReferences(Project project)
        {
            foreach (var metadataReference in project.MetadataReferences.ToArray())
            {
                if (!File.Exists(metadataReference.Display))
                {
                    Log.Message("Reference assembly doesn't exist on disk: " + metadataReference.Display);
                    project = project.RemoveMetadataReference(metadataReference);
                }
            }

            return project.Solution;
        }

        private static Solution AddAssemblyAttributesFile(string language, string outputAssemblyPath, Solution solution)
        {
            if (!File.Exists(outputAssemblyPath))
            {
                Log.Exception("AddAssemblyAttributesFile: assembly doesn't exist: " + outputAssemblyPath);
                return solution;
            }

            var assemblyAttributesFileText = MetadataReading.GetAssemblyAttributesFileText(
                assemblyFilePath: outputAssemblyPath,
                language: language);
            if (assemblyAttributesFileText != null)
            {
                var extension = language == LanguageNames.CSharp ? ".cs" : ".vb";
                var newAssemblyAttributesDocumentName = MetadataAsSource.GeneratedAssemblyAttributesFileName + extension;
                var existingAssemblyAttributesFileName = "AssemblyAttributes" + extension;

                var project = solution.Projects.First();
                if (project.Documents.All(d => d.Name != existingAssemblyAttributesFileName || d.Folders.Count != 0))
                {
                    var document = project.AddDocument(
                        newAssemblyAttributesDocumentName,
                        assemblyAttributesFileText,
                        filePath: newAssemblyAttributesDocumentName);
                    solution = document.Project.Solution;
                }
            }

            return solution;
        }

        private static Solution DeduplicateProjectReferences(Solution solution)
        {
            foreach (var projectId in solution.ProjectIds.ToArray())
            {
                var project = solution.GetProject(projectId);

                var distinctProjectReferences = project.AllProjectReferences.Distinct().ToArray();
                if (distinctProjectReferences.Length < project.AllProjectReferences.Count)
                {
                    var duplicates = project.AllProjectReferences.GroupBy(p => p).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
                    foreach (var duplicate in duplicates)
                    {
                        Log.Write($"Duplicate project reference to {duplicate.ProjectId.ToString()} in project: {project.Name}", ConsoleColor.Yellow);
                    }

                    var newProject = project.WithProjectReferences(distinctProjectReferences);
                    solution = newProject.Solution;
                }
            }

            return solution;
        }

        public static string CurrentAssemblyName = null;

        /// <returns>true if only part of the solution was processed and the method needs to be called again, false if all done</returns>
        public async Task<bool> GenerateAsync(CancellationToken cancellationToken, HashSet<string> processedAssemblyList = null, Folder<ProjectSkeleton> solutionExplorerRoot = null)
        {
            if (solution == null)
            {
                // we failed to open the solution earlier; just return
                Log.Message("Solution is null: " + this.ProjectFilePath);
                return false;
            }

            var allProjects = solution.Projects.ToArray();
            if (allProjects.Length == 0)
            {
                // Roslyn's MSBuildWorkspace only loads C# and VB projects; any other project type
                // (F#, C++, shared projects, ...) is silently skipped via SkipUnrecognizedProjects. A
                // solution that only contains such projects legitimately loads zero projects, so only
                // treat an empty solution as suspicious when it declared a project we should have loaded.
                if (DeclaresLoadableProject(this.ProjectFilePath))
                {
                    Log.Exception("Solution " + this.ProjectFilePath + " has 0 projects - this is suspicious");
                }
                else
                {
                    Log.Message("Solution " + this.ProjectFilePath + " has 0 projects because it contains no C# or VB projects");
                }
            }

            var projectsToProcess = allProjects
                .Where(p => processedAssemblyList == null || processedAssemblyList.Add(p.AssemblyName))
                .Where(p => !ExcludeTests || !IsTestProject(p))
                .ToArray();
            var currentBatch = projectsToProcess
                .ToArray();
            foreach (var project in currentBatch)
            {
                try
                {
                    CurrentAssemblyName = project.AssemblyName;

                    var generator = new ProjectGenerator(this, project);
                    await generator.GenerateAsync();

                    File.AppendAllText(Paths.ProcessedAssemblies, project.AssemblyName + Environment.NewLine, Encoding.UTF8);
                }
                finally
                {
                    CurrentAssemblyName = null;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }

            new TypeScriptSupport().Generate(typeScriptFiles, SolutionDestinationFolder);

            await AddProjectsToSolutionExplorerAsync(
                solutionExplorerRoot,
                currentBatch,
                cancellationToken);

            return currentBatch.Length < projectsToProcess.Length;
        }

        private static bool IsTestProject(Project proj)
        {
            return
                proj.MetadataReferences.Any(mdr =>
                {
                    var peRef = mdr as PortableExecutableReference;
                    return
                        IsTestProject(peRef, "xunit.core.dll") ||
                        IsTestProject(peRef, "nunit.framework.dll") ||
                        IsTestProject(peRef, "Microsoft.VisualStudio.TestPlatform.TestFramework.dll");
                }) ||
                IsTestProject(proj, "xunit") ||
                IsTestProject(proj, "nunit") ||
                IsTestProject(proj, "MSTest.TestFramework");
        }

        private static IEnumerable<string> GetPackageRefs(Project proj)
        {
            var projRoot = XElement.Load(proj.FilePath);
            var packageRefs = projRoot.Elements()
                .Where(elem => elem.Name.LocalName == "ItemGroup")
                .SelectMany(elem => elem.Elements())
                .Where(elem => elem.Name.LocalName == "PackageReference")
                .Select(elem => (string)elem.Attribute("Include"));
            return packageRefs;
        }

        private static bool IsTestProject(Project proj, string marker)
        {
            return GetPackageRefs(proj).Any(pr => string.Equals(pr, marker, StringComparison.InvariantCultureIgnoreCase));
        }

        private static bool IsTestProject(PortableExecutableReference peRef, string marker)
        {
            return peRef?.FilePath.EndsWith(marker, StringComparison.InvariantCultureIgnoreCase) ?? false;
        }

        private void SetFieldValue(object instance, string fieldName, object value)
        {
            var type = instance.GetType();
            var fieldInfo = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            fieldInfo.SetValue(instance, null);
        }

        public async Task GenerateExternalReferencesAsync(HashSet<string> assemblyList, CancellationToken cancellationToken)
        {
            var externalReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in solution.Projects)
            {
                var references = project.MetadataReferences
                    .OfType<PortableExecutableReference>()
                    .Where(m => File.Exists(m.FilePath) &&
                                !assemblyList.Contains(Path.GetFileNameWithoutExtension(m.FilePath)) &&
                                !IsPartOfSolution(Path.GetFileNameWithoutExtension(m.FilePath)) &&
                                GetExternalAssemblyIndex(Path.GetFileNameWithoutExtension(m.FilePath)) == -1
                    )
                    .Select(m => Path.GetFullPath(m.FilePath));
                foreach (var reference in references)
                {
                    externalReferences[Path.GetFileNameWithoutExtension(reference)] = reference;
                }
            }

            foreach (var externalReference in externalReferences)
            {
                Log.Write(externalReference.Key, ConsoleColor.Magenta);
                var solutionGenerator = await SolutionGenerator.CreateAsync(
                    externalReference.Value,
                    Paths.SolutionDestinationFolder,
                    cancellationToken,
                    pluginBlacklist: PluginBlacklist);
                await solutionGenerator.GenerateAsync(cancellationToken, assemblyList);
            }
        }

        public bool IsPartOfSolution(string assemblyName)
        {
            if (GlobalAssemblyList == null)
            {
                // if for some reason we don't know a global list, assume everything is in the solution
                // this is better than the alternative
                return true;
            }

            return GlobalAssemblyList.Contains(assemblyName);
        }

        public int GetExternalAssemblyIndex(string assemblyName)
        {
            if (Federation == null)
            {
                return -1;
            }

            return Federation.GetExternalAssemblyIndex(assemblyName);
        }

        private async Task<Solution> CreateSolutionAsync(string solutionFilePath, CancellationToken cancellationToken, ImmutableDictionary<string, string> properties = null, bool doNotIncludeReferencedProjects = false)
        {
            try
            {
                Solution solution = null;
                if (solutionFilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                    solutionFilePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    properties = AddSolutionProperties(properties, solutionFilePath);
                    var workspace = CreateWorkspace(properties);
                    workspace.SkipUnrecognizedProjects = true;
                    workspace.RegisterWorkspaceFailedHandler(args => WorkspaceFailed(args, workspace));
                    solution = await workspace.OpenSolutionAsync(solutionFilePath, cancellationToken: cancellationToken);
                    solution = DeduplicateProjectReferences(solution);
                    this.workspace = workspace;
                }
                else if (
                    solutionFilePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    solutionFilePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
                {
                    var workspace = CreateWorkspace(properties);
                    workspace.RegisterWorkspaceFailedHandler(args => WorkspaceFailed(args, workspace));
                    solution = (await workspace.OpenProjectAsync(solutionFilePath, cancellationToken: cancellationToken)).Solution;
                    solution = DeduplicateProjectReferences(solution);
                    if (doNotIncludeReferencedProjects)
                    {
                        var keepPrimaryProject = solution.Projects.First(p => string.Equals(p.FilePath, solutionFilePath, StringComparison.OrdinalIgnoreCase));
                        foreach (var projectIdToRemove in solution.ProjectIds.Where(id => id != keepPrimaryProject.Id).ToArray())
                        {
                            solution = solution.RemoveProject(projectIdToRemove);
                        }
                    }

                    this.workspace = workspace;
                }
                else if (
                    solutionFilePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    solutionFilePath.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase) ||
                    solutionFilePath.EndsWith(".netmodule", StringComparison.OrdinalIgnoreCase))
                {
                    solution = await MetadataAsSource.LoadMetadataAsSourceSolutionAsync(solutionFilePath, cancellationToken);
                    if (solution != null)
                    {
                        solution.Workspace.RegisterWorkspaceFailedHandler(args => WorkspaceFailed(args, solution.Workspace));
                        workspace = solution.Workspace;
                    }
                }
                else if (solutionFilePath.EndsWith(".complog", StringComparison.OrdinalIgnoreCase))
                {
                    // A compiler log already carries the fully-resolved Roslyn compilation for each
                    // project, so SolutionReader can materialize a SolutionInfo directly -- no MSBuild
                    // evaluation required. The reader is stashed in a field (disposed in Dispose) because
                    // the resulting documents pull their source text from it lazily during Pass1.
                    //
                    // BasicAnalyzerKind.None loads any files that generators originally produced directly
                    // into the compilation, so we don't need to (re-)execute analyzers/source generators
                    // during analysis -- avoiding loading third-party analyzers and their overhead.
                    ReadCompilerLogMetadata(solutionFilePath);
                    var reader = SolutionReader.Create(
                        solutionFilePath,
                        BasicAnalyzerKind.None);
                    this.compilerLogReader = reader;
                    var solutionInfo = NormalizeCompilerLogAssemblyNames(reader.ReadSolutionInfo());

                    var adhocWorkspace = new AdhocWorkspace();
                    adhocWorkspace.AddSolution(solutionInfo);
                    solution = DeduplicateProjectReferences(adhocWorkspace.CurrentSolution);
                    this.workspace = adhocWorkspace;
                }

                return solution;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "Failed to open solution: " + solutionFilePath);
                return null;
            }
        }

        private ImmutableDictionary<string, string> AddSolutionProperties(ImmutableDictionary<string, string> properties, string solutionFilePath)
        {
            // http://referencesource.microsoft.com/#MSBuildFiles/C/ProgramFiles(x86)/MSBuild/14.0/bin_/amd64/Microsoft.Common.CurrentVersion.targets,296
            properties = properties ?? ImmutableDictionary<string, string>.Empty;
            properties = properties.Add("SolutionName", Path.GetFileNameWithoutExtension(solutionFilePath));
            properties = properties.Add("SolutionFileName", Path.GetFileName(solutionFilePath));
            properties = properties.Add("SolutionPath", solutionFilePath);
            properties = properties.Add("SolutionDir", Path.GetDirectoryName(solutionFilePath));
            properties = properties.Add("SolutionExt", Path.GetExtension(solutionFilePath));
            return properties;
        }

        private static void WorkspaceFailed(WorkspaceDiagnosticEventArgs e, Workspace workspace)
        {
            var message = e.Diagnostic.Message;
            if (message.StartsWith("Could not find file", StringComparison.Ordinal) || message.StartsWith("Could not find a part of the path", StringComparison.Ordinal))
            {
                return;
            }

            if (message.StartsWith("The imported project ", StringComparison.Ordinal))
            {
                return;
            }

            // Roslyn's MSBuildWorkspace only recognizes C# and VB projects; every other project type
            // (F#, C++, shared projects, ...) raises this diagnostic and is then dropped because
            // SkipUnrecognizedProjects is set. That is expected, not a failure, so don't log it as severe.
            if (message.Contains("is not associated with a language"))
            {
                return;
            }

            var project = workspace.CurrentSolution.Projects.FirstOrDefault();
            if (project != null)
            {
                message = message + " Project: " + project.Name;
            }

            Log.Exception("Workspace failed: " + message);
            Log.Write(message, ConsoleColor.Red);
        }

        private static readonly string[] LoadableProjectExtensions = { ".csproj", ".vbproj" };

        /// <summary>
        /// Returns true if the solution declares at least one project that Roslyn's MSBuildWorkspace can
        /// load (C# or VB). Only .sln files can be inspected here, so anything else (e.g. .slnx) is
        /// assumed loadable to avoid silently swallowing a genuine failure.
        /// </summary>
        private static bool DeclaresLoadableProject(string solutionFilePath)
        {
            if (!solutionFilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                var solutionFile = SolutionFile.Parse(solutionFilePath);
                return solutionFile.ProjectsInOrder.Any(p =>
                    p.ProjectType != SolutionProjectType.SolutionFolder &&
                    LoadableProjectExtensions.Any(ext =>
                        (p.AbsolutePath ?? p.RelativePath ?? string.Empty).EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                return true;
            }
        }

        public void AddTypeScriptFile(string filePath)
        {
            this.typeScriptFiles.Add(filePath);
        }

        public void Dispose()
        {
            if (workspace != null)
            {
                workspace.Dispose();
                workspace = null;
            }

            if (compilerLogReader != null)
            {
                compilerLogReader.Dispose();
                compilerLogReader = null;
            }
        }
    }
}
