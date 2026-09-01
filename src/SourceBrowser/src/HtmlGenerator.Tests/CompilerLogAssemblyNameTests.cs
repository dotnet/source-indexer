using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using SolutionInfo = Microsoft.CodeAnalysis.SolutionInfo;

namespace HtmlGenerator.Tests
{
    [TestClass]
    public class CompilerLogAssemblyNameTests
    {
        private static SolutionInfo CreateSolutionInfo(params string[] assemblyNames)
        {
            var projects = assemblyNames.Select(name =>
            {
                var projectId = ProjectId.CreateNewId(name);
                return ProjectInfo.Create(
                    projectId,
                    VersionStamp.Default,
                    name: name,
                    assemblyName: name,
                    language: LanguageNames.CSharp);
            });

            return SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default, projects: projects);
        }

        [TestMethod]
        [DataRow("Foo.dll", "Foo")]
        [DataRow("Foo.exe", "Foo")]
        [DataRow("System.Text.Json.dll", "System.Text.Json")]
        [DataRow("Foo", "Foo")]
        public void AssemblyName_extension_is_stripped(string input, string expected)
        {
            var normalized = SolutionGenerator.NormalizeCompilerLogAssemblyNames(CreateSolutionInfo(input));

            normalized.Projects.Single().AssemblyName.ShouldBe(expected);
        }

        [TestMethod]
        public void Normalization_preserves_project_identity_and_count()
        {
            var original = CreateSolutionInfo("A.dll", "B.dll");

            var normalized = SolutionGenerator.NormalizeCompilerLogAssemblyNames(original);

            normalized.Id.ShouldBe(original.Id);
            normalized.Projects.Count.ShouldBe(2);
            normalized.Projects.Select(p => p.Id)
                .ShouldBe(original.Projects.Select(p => p.Id), ignoreOrder: true);
            normalized.Projects.Select(p => p.AssemblyName)
                .ShouldBe(new[] { "A", "B" }, ignoreOrder: true);
        }

        [TestMethod]
        public void Implementation_compilation_precedes_reference_assembly_with_the_same_name()
        {
            var referenceAssembly = CreateProjectInfo("A.dll", documentCount: 1);
            var unrelated = CreateProjectInfo("B.dll", documentCount: 1);
            var implementation = CreateProjectInfo("A.dll", documentCount: 10);
            var original = SolutionInfo.Create(
                SolutionId.CreateNewId(),
                VersionStamp.Default,
                projects: new[] { referenceAssembly, unrelated, implementation });

            var normalized = SolutionGenerator.NormalizeCompilerLogAssemblyNames(original);

            normalized.Projects.Select(p => p.Id)
                .ShouldBe(new[] { implementation.Id, unrelated.Id, referenceAssembly.Id });
        }

        [TestMethod]
        public void Compiler_log_document_folders_match_BinLogToSln_link_behavior()
        {
            const string repositoryRoot = @"D:\a\_work\1\s";
            const string projectDirectory = repositoryRoot + @"\src\runtime\src\coreclr\System.Private.CoreLib";
            var projectId = ProjectId.CreateNewId();
            var localDocument = DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                "Local.cs",
                filePath: projectDirectory + @"\Internal\Local.cs");
            var linkedDocument = DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                "String.cs",
                filePath: repositoryRoot + @"\src\runtime\src\libraries\System.Private.CoreLib\src\System\String.cs");
            var project = ProjectInfo.Create(
                projectId,
                VersionStamp.Default,
                name: "System.Private.CoreLib",
                assemblyName: "System.Private.CoreLib.dll",
                language: LanguageNames.CSharp,
                filePath: projectDirectory + @"\System.Private.CoreLib.csproj",
                documents: new[] { localDocument, linkedDocument });
            var solution = SolutionInfo.Create(
                SolutionId.CreateNewId(),
                VersionStamp.Default,
                projects: new[] { project });
            var normalized = SolutionGenerator.NormalizeCompilerLogAssemblyNames(
                solution,
                compilerLogRepositoryRoots: new[] { repositoryRoot });
            var documents = normalized.Projects.Single().Documents.ToDictionary(d => d.Name);

            documents["Local.cs"].Folders.ShouldBe(new[] { "Internal" });
            documents["String.cs"].Folders.ShouldBe(
                new[] { "src", "runtime", "src", "libraries", "System.Private.CoreLib", "src", "System" });

            using var workspace = new AdhocWorkspace();
            workspace.AddSolution(normalized);
            var stringDocument = workspace.CurrentSolution.Projects.Single().Documents.Single(d => d.Name == "String.cs");
            Paths.GetRelativeFilePathInProject(stringDocument, project.FilePath).ShouldBe(
                @"src\runtime\src\libraries\System.Private.CoreLib\src\System\String.cs");
        }

        [TestMethod]
        public void Existing_compiler_log_document_folders_are_preserved()
        {
            var document = DocumentInfo.Create(
                DocumentId.CreateNewId(ProjectId.CreateNewId()),
                "Linked.cs",
                folders: new[] { "Existing", "Link" },
                filePath: @"D:\repo\src\Linked.cs");

            var normalized = SolutionGenerator.AddCompilerLogDocumentFolders(
                document,
                @"D:\repo\project",
                @"D:\repo");

            normalized.ShouldBeSameAs(document);
        }

        private static ProjectInfo CreateProjectInfo(string assemblyName, int documentCount)
        {
            var projectId = ProjectId.CreateNewId();
            var documents = Enumerable.Range(0, documentCount)
                .Select(i => DocumentInfo.Create(DocumentId.CreateNewId(projectId), $"{i}.cs"));
            return ProjectInfo.Create(
                projectId,
                VersionStamp.Default,
                name: assemblyName,
                assemblyName: assemblyName,
                language: LanguageNames.CSharp,
                documents: documents);
        }
    }
}
