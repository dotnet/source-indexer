using System;
using System.IO;
using System.Linq;
using Microsoft.SourceBrowser.Common;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    /// <summary>
    /// Characterizes the /incremental behavior added on top of the Pass1/Pass2 output split
    /// (see Pass2FinalizationTests): Pass2 retaining an assembly's existing finalized output instead of
    /// re-copying it from Pass1's source index when nothing about that assembly changed, while still
    /// correctly recomputing cross-assembly aggregates (the "Used By" backlink in particular) over the
    /// full current project set every run -- including for retained, not-recopied assemblies.
    ///
    /// These tests drive SolutionFinalizer/ProjectFinalizer directly against synthetic on-disk fixtures,
    /// simulating what two successive HtmlGenerator /incremental runs would leave on disk, rather than
    /// running Pass1 (ProjectStaleness/ProjectGenerator) against a real MSBuild-backed compilation.
    /// </summary>
    [TestClass]
    public class IncrementalFinalizationTests
    {
        private string testRoot;
        private string sourceIndexFolder;
        private string outputFolder;

        [TestInitialize]
        public void Setup()
        {
            testRoot = Path.Combine(Path.GetTempPath(), "SourceBrowserIncrementalTests_" + Guid.NewGuid().ToString("N"));
            sourceIndexFolder = Path.Combine(testRoot, "obj");
            outputFolder = Path.Combine(testRoot, "index");
            Directory.CreateDirectory(sourceIndexFolder);
            Directory.CreateDirectory(outputFolder);
            Microsoft.SourceBrowser.HtmlGenerator.Paths.SolutionDestinationFolder = sourceIndexFolder;
        }

        [TestCleanup]
        public void Cleanup()
        {
            // Configuration.Incremental is a process-wide static; always leave it back at its default so
            // other tests (which assume the non-incremental, always-copy behavior) aren't affected.
            Configuration.Incremental = false;

            if (Directory.Exists(testRoot))
            {
                try
                {
                    Directory.Delete(testRoot, recursive: true);
                }
                catch
                {
                    // best effort
                }
            }
        }

        [TestMethod]
        public void ProjectFinalizer_retains_existing_output_when_staleness_key_matches()
        {
            Configuration.Incremental = true;

            CreateAssemblyFixture(sourceIndexFolder, "AssemblyA", referencedAssemblies: null, stalenessKey: "key-a-1");

            // Simulate output already finalized by a previous run with the same key, plus a marker file
            // that a fresh CopyDirectory (delete-then-copy) would wipe out.
            var outputAssemblyFolder = Path.Combine(outputFolder, "AssemblyA");
            FileUtilities.CopyDirectory(Path.Combine(sourceIndexFolder, "AssemblyA"), outputAssemblyFolder);
            File.WriteAllText(Path.Combine(outputAssemblyFolder, "RETAINED_MARKER.txt"), "still here");

            var finalizer = new SolutionFinalizer(sourceIndexFolder, outputFolder);
            var project = finalizer.projects.Single(p => p.AssemblyId == "AssemblyA");

            project.RetainedExistingOutput.ShouldBeTrue();
            File.Exists(Path.Combine(outputAssemblyFolder, "RETAINED_MARKER.txt")).ShouldBeTrue();
        }

        [TestMethod]
        public void ProjectFinalizer_recopies_when_staleness_key_changes()
        {
            Configuration.Incremental = true;

            CreateAssemblyFixture(sourceIndexFolder, "AssemblyA", referencedAssemblies: null, stalenessKey: "key-a-2");

            var outputAssemblyFolder = Path.Combine(outputFolder, "AssemblyA");
            FileUtilities.CopyDirectory(Path.Combine(sourceIndexFolder, "AssemblyA"), outputAssemblyFolder);
            // The output copy still reflects the OLD key -- this must be treated as stale and re-copied.
            File.WriteAllText(Path.Combine(outputAssemblyFolder, Constants.StalenessKeyFileName + ".txt"), "key-a-1");
            File.WriteAllText(Path.Combine(outputAssemblyFolder, "SHOULD_BE_REMOVED.txt"), "stale leftover");

            var finalizer = new SolutionFinalizer(sourceIndexFolder, outputFolder);
            var project = finalizer.projects.Single(p => p.AssemblyId == "AssemblyA");

            project.RetainedExistingOutput.ShouldBeFalse();
            File.Exists(Path.Combine(outputAssemblyFolder, "SHOULD_BE_REMOVED.txt")).ShouldBeFalse();
        }

        [TestMethod]
        public void FinalizeProjects_updates_used_by_for_a_retained_assembly_without_recopying_it()
        {
            Configuration.Incremental = true;

            // Round 1: AssemblyA references AssemblyB. Nothing exists in the output yet.
            CreateAssemblyFixture(sourceIndexFolder, "AssemblyB", referencedAssemblies: null, stalenessKey: "key-b-1");
            CreateAssemblyFixture(sourceIndexFolder, "AssemblyA", referencedAssemblies: new[] { "AssemblyB" }, stalenessKey: "key-a-1");

            var round1 = new SolutionFinalizer(sourceIndexFolder, outputFolder);
            round1.FinalizeProjects(emitAssemblyList: false, federation: new Federation());

            var bExplorerAfterRound1 = File.ReadAllText(Path.Combine(outputFolder, "AssemblyB", "ProjectExplorer.html"));
            bExplorerAfterRound1.ShouldContain("Used By");
            bExplorerAfterRound1.ShouldContain("AssemblyA");

            // Plant a sentinel in AssemblyB's finalized output that only a fresh re-copy from source would
            // destroy -- this is how we prove AssemblyB's own per-project output is left alone.
            var bOutputFolder = Path.Combine(outputFolder, "AssemblyB");
            var sentinelFile = Path.Combine(bOutputFolder, "SomeDocument.html");
            File.WriteAllText(sentinelFile, "<html>unchanged document content</html>");

            // Round 2: AssemblyA changes -- it no longer references AssemblyB -- so AssemblyA gets a new
            // staleness key and is regenerated/recopied. AssemblyB itself is completely unchanged (same
            // staleness key as round 1), so it should be retained rather than recopied.
            RewriteAssemblyFixture(sourceIndexFolder, "AssemblyA", referencedAssemblies: null, stalenessKey: "key-a-2");

            var round2 = new SolutionFinalizer(sourceIndexFolder, outputFolder);
            var bProjectRound2 = round2.projects.Single(p => p.AssemblyId == "AssemblyB");
            var aProjectRound2 = round2.projects.Single(p => p.AssemblyId == "AssemblyA");

            bProjectRound2.RetainedExistingOutput.ShouldBeTrue("AssemblyB did not change and should have been retained, not recopied");
            aProjectRound2.RetainedExistingOutput.ShouldBeFalse("AssemblyA changed and should have been recopied");

            round2.FinalizeProjects(emitAssemblyList: false, federation: new Federation());

            // AssemblyB's own output (the sentinel document) must be untouched -- proving Pass1/Pass2 never
            // regenerated or recopied it.
            File.Exists(sentinelFile).ShouldBeTrue("AssemblyB's retained output should not have been recopied/wiped");
            File.ReadAllText(sentinelFile).ShouldBe("<html>unchanged document content</html>");

            // But AssemblyB's cross-assembly aggregate (its "Used By" backlink) must still be correct for
            // the new project graph: AssemblyA no longer references it, so the block must be gone.
            var bExplorerAfterRound2 = File.ReadAllText(Path.Combine(outputFolder, "AssemblyB", "ProjectExplorer.html"));
            bExplorerAfterRound2.ShouldNotContain("Used By");
        }

        private static void CreateAssemblyFixture(string indexRoot, string assemblyName, string[] referencedAssemblies, string stalenessKey)
        {
            var assemblyFolder = Path.Combine(indexRoot, assemblyName);
            Directory.CreateDirectory(assemblyFolder);
            Directory.CreateDirectory(Path.Combine(assemblyFolder, Constants.ReferencesFileName));

            File.WriteAllLines(
                Path.Combine(assemblyFolder, Constants.ProjectInfoFileName + ".txt"),
                new[]
                {
                    "ProjectSourcePath=C:\\src\\" + assemblyName,
                    "DocumentCount=1",
                    "LinesOfCode=10",
                    "BytesOfCode=100",
                    "DeclaredSymbols=0",
                    "DeclaredTypes=0",
                    "PublicTypes=0",
                });

            if (referencedAssemblies != null && referencedAssemblies.Length > 0)
            {
                File.WriteAllLines(Path.Combine(assemblyFolder, Constants.ReferencedAssemblyList + ".txt"), referencedAssemblies);
            }

            File.WriteAllText(Path.Combine(assemblyFolder, Constants.ProjectExplorer + ".html"), ProjectExplorerFixture(assemblyName));

            if (stalenessKey != null)
            {
                File.WriteAllText(Path.Combine(assemblyFolder, Constants.StalenessKeyFileName + ".txt"), stalenessKey);
            }
        }

        /// <summary>
        /// Simulates Pass1 regenerating a single assembly on an incremental re-run: rewrites its source
        /// folder contents (and staleness key) in place, exactly as ProjectGenerator would when a project's
        /// key no longer matches what's on disk.
        /// </summary>
        private static void RewriteAssemblyFixture(string indexRoot, string assemblyName, string[] referencedAssemblies, string stalenessKey)
        {
            var assemblyFolder = Path.Combine(indexRoot, assemblyName);
            Directory.Delete(assemblyFolder, recursive: true);
            CreateAssemblyFixture(indexRoot, assemblyName, referencedAssemblies, stalenessKey);
        }

        private static string ProjectExplorerFixture(string assemblyName)
        {
            return string.Join(
                Environment.NewLine,
                $"<div id=\"rootFolder\" class=\"projectCS\">{assemblyName}</div>",
                "<div>",
                "<div class=\"folderTitle\">References</div><div class=\"folder\">",
                "</div>",
                "</div>",
                "<script></script>");
        }
    }
}
