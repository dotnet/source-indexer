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
