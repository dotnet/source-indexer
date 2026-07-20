using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    /// <summary>
    /// Characterizes <see cref="ProjectStaleness.ComputeKeyAsync"/>, the per-project staleness key that
    /// gates both Pass1's regeneration skip and Pass2's retain-existing-output skip on an incremental run.
    ///
    /// These tests use an in-memory <see cref="AdhocWorkspace"/> project (no on-disk build output), so
    /// they always exercise the content-hash fallback path (source + references + options) rather than
    /// the deterministic-MVID fast path -- which requires an actual built, deterministic assembly on disk
    /// and is covered indirectly by the fact that it degrades gracefully to the content hash whenever no
    /// such output exists (see <see cref="ProjectStaleness"/>'s try/catch and null-path handling).
    /// </summary>
    [TestClass]
    public class ProjectStalenessTests
    {
        [TestMethod]
        public async Task ComputeKeyAsync_is_stable_for_unchanged_inputs()
        {
            var project = CreateProject("class C { }");

            var key1 = await ProjectStaleness.ComputeKeyAsync(project);
            var key2 = await ProjectStaleness.ComputeKeyAsync(project);

            key1.ShouldNotBeNullOrEmpty();
            key1.ShouldBe(key2);
        }

        [TestMethod]
        public async Task ComputeKeyAsync_changes_when_source_changes()
        {
            var unchanged = CreateProject("class C { }");
            var changed = CreateProject("class C { void M() { } }");

            var key1 = await ProjectStaleness.ComputeKeyAsync(unchanged);
            var key2 = await ProjectStaleness.ComputeKeyAsync(changed);

            key1.ShouldNotBe(key2);
        }

        [TestMethod]
        public async Task ComputeKeyAsync_changes_when_a_project_reference_is_added()
        {
            using var workspace = new AdhocWorkspace();
            var solution = workspace.CurrentSolution;

            var referencedProjectId = ProjectId.CreateNewId();
            solution = solution.AddProject(referencedProjectId, "Referenced", "Referenced", LanguageNames.CSharp);

            var mainProjectId = ProjectId.CreateNewId();
            solution = solution.AddProject(mainProjectId, "Main", "Main", LanguageNames.CSharp);
            solution = solution.AddDocument(DocumentId.CreateNewId(mainProjectId), "C.cs", "class C { }");

            var withoutReference = solution.GetProject(mainProjectId);
            var keyWithoutReference = await ProjectStaleness.ComputeKeyAsync(withoutReference);

            var solutionWithReference = solution.AddProjectReference(mainProjectId, new ProjectReference(referencedProjectId));
            var withReference = solutionWithReference.GetProject(mainProjectId);
            var keyWithReference = await ProjectStaleness.ComputeKeyAsync(withReference);

            keyWithoutReference.ShouldNotBe(keyWithReference);
        }

        [TestMethod]
        public async Task ComputeKeyAsync_changes_when_compilation_options_change()
        {
            var project = CreateProject("class C { }");

            var keyBefore = await ProjectStaleness.ComputeKeyAsync(project);

            var changedOptionsProject = project.WithCompilationOptions(
                ((CSharpCompilationOptions)project.CompilationOptions).WithOptimizationLevel(OptimizationLevel.Release));
            var keyAfter = await ProjectStaleness.ComputeKeyAsync(changedOptionsProject);

            keyBefore.ShouldNotBe(keyAfter);
        }

        private static Project CreateProject(string source)
        {
            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution.AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp);
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "C.cs", source);
            solution = solution.WithProjectCompilationOptions(
                projectId,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
            return solution.GetProject(projectId);
        }
    }
}
