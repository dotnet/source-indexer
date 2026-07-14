using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class ProjectGenerator
    {
        private readonly string assemblyAttributesFileName;

        public Project Project { get; private set; }
        public Dictionary<string, List<Tuple<string, long>>> SymbolIDToListOfLocationsMap { get; private set; }
        public Dictionary<ISymbol, string> DeclaredSymbols { get; private set; }
        public Dictionary<ISymbol, ISymbol> BaseMembers { get; private set; }
        public MultiDictionary<ISymbol, ISymbol> ImplementedInterfaceMembers { get; set; }

        public string ProjectDestinationFolder { get; private set; }
        public string AssemblyName { get; private set; }
        public SolutionGenerator SolutionGenerator { get; private set; }
        public string ProjectSourcePath { get; set; }
        public string ProjectFilePath { get; private set; }
        public List<string> OtherFiles { get; set; }
        public IEnumerable<MEF.ISymbolVisitor> PluginSymbolVisitors { get; private set; }
        public IEnumerable<MEF.ITextVisitor> PluginTextVisitors { get; private set; }

        // Populated once, single-threaded, before documents are partitioned for parallel generation
        // (see GenerateAsync). Maps each Document to a relative destination path that's been
        // disambiguated from any other Document that would otherwise collide on the same
        // folders+filename (see Paths.DisambiguateRelativePaths).
        private Dictionary<DocumentId, string> documentRelativePaths;

        /// <summary>
        /// Returns the (possibly disambiguated) relative destination path for <paramref name="document"/>.
        /// Falls back to the raw, non-disambiguated computation for documents outside the normal
        /// per-project document set (e.g. this can be reached for any document, but is only
        /// disambiguated for documents in this project's <c>documents</c> list).
        /// </summary>
        public string GetDocumentRelativePath(Document document)
        {
            if (documentRelativePaths != null &&
                documentRelativePaths.TryGetValue(document.Id, out var relativePath))
            {
                return relativePath;
            }

            return Paths.GetRelativeFilePathInProject(document);
        }

        public ProjectGenerator(SolutionGenerator solutionGenerator, Project project) : this()
        {
            this.SolutionGenerator = solutionGenerator;
            this.Project = project;
            this.ProjectFilePath = project.FilePath ?? solutionGenerator.ProjectFilePath;
            this.DeclaredSymbols = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
            this.BaseMembers = new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default);
            this.ImplementedInterfaceMembers = new MultiDictionary<ISymbol, ISymbol>();
            this.assemblyAttributesFileName = MetadataAsSource.GeneratedAssemblyAttributesFileName + (project.Language == LanguageNames.CSharp ? ".cs" : ".vb");
            PluginSymbolVisitors = SolutionGenerator.PluginAggregator?.ManufactureSymbolVisitors(project).ToArray();
            PluginTextVisitors = SolutionGenerator.PluginAggregator?.ManufactureTextVisitors(project).ToArray();
        }

        /// <summary>
        /// This constructor is used for non-C#/VB projects such as "MSBuildFiles"
        /// </summary>
        public ProjectGenerator(string folderName, string solutionDestinationFolder) : this()
        {
            ProjectDestinationFolder = Path.Combine(solutionDestinationFolder, folderName);
            Directory.CreateDirectory(Path.Combine(ProjectDestinationFolder, Constants.ReferencesFileName));
        }

        private void AddHtmlFilesToRedirectMap()
        {
            var files = Directory
                            .GetFiles(ProjectDestinationFolder, "*.html", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var relativePath = file.Substring(ProjectDestinationFolder.Length + 1).Replace('\\', '/');
                relativePath = relativePath.Substring(0, relativePath.Length - 5); // strip .html
                AddFileToRedirectMap(relativePath);
                OtherFiles.Add(relativePath);
            }
        }

        // Only reached from the single-threaded loose-files pass (GenerateNonProjectFolder), so no
        // locking is needed on the shared map.
        private void AddFileToRedirectMap(string filePath)
        {
            SymbolIDToListOfLocationsMap.Add(
                SymbolIdService.GetId(filePath),
                new List<Tuple<string, long>> { Tuple.Create(filePath, 0L) });
        }

        private ProjectGenerator()
        {
            this.SymbolIDToListOfLocationsMap = new Dictionary<string, List<Tuple<string, long>>>();
            this.OtherFiles = new List<string>();
        }

        private async IAsyncEnumerable<Document> GetDocumentsAsync()
        {
            foreach(var d in Project.Documents)
            {
                yield return d;
            }

            if (SolutionGenerator.IncludeSourceGeneratedDocuments)
            {
                var generatedDocuments = await Project.GetSourceGeneratedDocumentsAsync();
                foreach(var document in generatedDocuments)
                {
                    yield return document;
                }
            }
        }

        public async Task GenerateAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(ProjectFilePath))
                {
                    Log.Exception("ProjectFilePath is empty: " + Project.ToString());
                    return;
                }

                ProjectDestinationFolder = GetProjectDestinationPath(Project, SolutionGenerator.SolutionDestinationFolder);
                if (ProjectDestinationFolder == null)
                {
                    Log.Exception("Errors evaluating project: " + Project.Id);
                    return;
                }

                Log.Write(ProjectDestinationFolder, ConsoleColor.DarkCyan);

                if (SolutionGenerator.SolutionSourceFolder is string solutionFolder)
                {
                    ProjectSourcePath = Paths.MakeRelativeToFolder(ProjectFilePath, solutionFolder);
                }
                else
                {
                    ProjectSourcePath = ProjectFilePath;
                }

                if (File.Exists(Path.Combine(ProjectDestinationFolder, Constants.DeclaredSymbolsFileName + ".txt")))
                {
                    // apparently someone already generated a project with this assembly name - their assembly wins
                    Log.Exception(string.Format(
                        "A project with assembly name {0} was already generated, skipping current project: {1}",
                        this.AssemblyName,
                        this.ProjectFilePath), isSevere: false);
                    return;
                }

                if (Configuration.CreateFoldersOnDisk)
                {
                    Directory.CreateDirectory(ProjectDestinationFolder);
                }

                List<Document> documents = new();
                await foreach(var d in GetDocumentsAsync())
                {
                    if (IncludeDocument(d))
                    {
                        documents.Add(d);
                    }
                }

                // Compute unique destination paths up front, single-threaded, so that two distinct
                // documents that happen to resolve to the same folders+filename (e.g. two unrelated
                // "IEnumerable.cs" files) don't silently collide once generation is partitioned and
                // parallelized below -- see Paths.DisambiguateRelativePaths for the exact semantics.
                var rawRelativePaths = documents.Select(Paths.GetRelativeFilePathInProject).ToArray();
                var identityKeys = documents.Select(d => d.FilePath ?? d.Name).ToArray();
                var disambiguatedRelativePaths = Paths.DisambiguateRelativePaths(rawRelativePaths, identityKeys);

                documentRelativePaths = new Dictionary<DocumentId, string>(documents.Count);
                for (int i = 0; i < documents.Count; i++)
                {
                    documentRelativePaths[documents[i].Id] = disambiguatedRelativePaths[i];
                }

                var collectors = new ConcurrentBag<ReferenceCollector>();
                var generationTasks = Partitioner.Create(documents)
                    .GetPartitions(Environment.ProcessorCount)
                    .Select(partition =>
                        Task.Run(async () =>
                        {
                            var collector = new ReferenceCollector();
                            collectors.Add(collector);
                            using (partition)
                            {
                                while (partition.MoveNext())
                                {
                                  await GenerateDocumentAsync(partition.Current, collector);
                                }
                            }
                        }));

                await Task.WhenAll(generationTasks);

                // Fold the per-partition collectors into the shared maps now that all Tasks have
                // completed, so the merge runs single-threaded and needs no locking.
                foreach (var collector in collectors)
                {
                    MergeReferences(collector);
                    MergeDeclarations(collector);
                }

                foreach (var document in documents)
                {
                    OtherFiles.Add(GetDocumentRelativePath(document));
                }

                if (Configuration.WriteProjectAuxiliaryFilesToDisk)
                {
                    GenerateProjectFile();
                    GenerateDeclarations();
                    GenerateBaseMembers();
                    GenerateImplementedInterfaceMembers();
                    GenerateProjectInfo();
                    GenerateReferencesDataFiles(
                        this.SolutionGenerator.SolutionDestinationFolder,
                        ReferencesByTargetAssemblyAndSymbolId);
                    GenerateSymbolIDToListOfDeclarationLocationsMap(
                        ProjectDestinationFolder,
                        SymbolIDToListOfLocationsMap);
                    GenerateReferencedAssemblyList();
                    GenerateUsedReferencedAssemblyList();
                    GenerateProjectExplorer();
                    GenerateNamespaceExplorer();
                    GenerateIndex();
                }

                var compilation = await Project.GetCompilationAsync();
                var diagnostics = compilation.GetDiagnostics().Select(d => d.ToString()).ToArray();
                if (diagnostics.Length > 0)
                {
                    var diagnosticsTxt = Path.Combine(this.ProjectDestinationFolder, "diagnostics.txt");
                    File.WriteAllLines(diagnosticsTxt, diagnostics);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "Project generation failed for: " + ProjectSourcePath);
            }
        }

        public void GenerateNonProjectFolder()
        {
            AddHtmlFilesToRedirectMap();
            GenerateDeclarations();
            GenerateSymbolIDToListOfDeclarationLocationsMap(
                ProjectDestinationFolder,
                SymbolIDToListOfLocationsMap);
        }

        private void GenerateNamespaceExplorer()
        {
            Log.Write("Namespace Explorer...");
            var symbols = this.DeclaredSymbols.Keys.OfType<INamedTypeSymbol>()
                .Select(s => new DeclaredSymbolInfo(s, this.AssemblyName));
            NamespaceExplorer.WriteNamespaceExplorer(this.AssemblyName, symbols, ProjectDestinationFolder);
        }

        private async Task GenerateDocumentAsync(Document document, ReferenceCollector collector)
        {
            try
            {
                var documentGenerator = new DocumentGenerator(this, document, collector);
                await documentGenerator.GenerateAsync();
            }
            catch (Exception e)
            {
                Log.Exception(e, "Document generation failed for: " + (document.FilePath ?? document.ToString()));
                throw;
            }
        }

        private void GenerateIndex()
        {
            Log.Write("index.html...");
            var index = Path.Combine(ProjectDestinationFolder, "index.html");
            var sb = new StringBuilder();
            Markup.WriteProjectIndex(sb, Project.AssemblyName);
            File.WriteAllText(index, sb.ToString());
        }

        private bool IsCSharp
        {
            get
            {
                return Project.Language == LanguageNames.CSharp;
            }
        }

        private bool IncludeDocument(Document document) => document.Name != assemblyAttributesFileName;

        private string GetProjectDestinationPath(Project project, string solutionDestinationPath)
        {
            var assemblyName = project.AssemblyName;
            if (assemblyName == "<Error>")
            {
                return null;
            }

            AssemblyName = SymbolIdService.GetAssemblyId(assemblyName);
            string subfolder = Path.Combine(solutionDestinationPath, AssemblyName);
            return subfolder;
        }

        public string GetWebAccessUrl(string sourceFilePath)
        {
            // Align with most gitignores as an approximation for knowing whether the file will exist in the source control repository.
            if (!Regex.IsMatch(sourceFilePath, @"(?:[/\\]|\A)obj[/\\]"))
            {
                var fullPath = Path.GetFullPath(sourceFilePath);
                var serverPathMapping = SolutionGenerator.ServerPathMappings.FirstOrDefault(p => fullPath.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase));
                if (serverPathMapping.Key != null)
                {
                    return serverPathMapping.Value + fullPath.Substring(serverPathMapping.Key.Length).Replace('\\', '/');
                }
            }

            return null;
        }
    }
}
