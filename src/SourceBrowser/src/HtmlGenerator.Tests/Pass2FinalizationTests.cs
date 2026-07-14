using System;
using System.IO;
using System.Linq;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    /// <summary>
    /// Characterizes Pass2 (<see cref="SolutionFinalizer"/>/<see cref="ProjectFinalizer"/>) against a
    /// synthetic Pass1 index -- i.e. a minimal on-disk fixture shaped like what <see cref="ProjectGenerator"/>
    /// would have produced -- rather than running a full MSBuild-backed indexing pass. This exercises the
    /// exact file layout Pass2 discovers and mutates without the cost/flakiness of a real build.
    ///
    /// These also serve as the regression tests for the Pass1/Pass2 output split: Pass1's source index must
    /// never be mutated by finalization, and finalization must be safely re-runnable from that same source.
    /// </summary>
    [TestClass]
    public class Pass2FinalizationTests
    {
        private string testRoot;
        private string sourceIndexFolder;
        private string outputFolder;

        [TestInitialize]
        public void Setup()
        {
            testRoot = Path.Combine(Path.GetTempPath(), "SourceBrowserPass2Tests_" + Guid.NewGuid().ToString("N"));
            sourceIndexFolder = Path.Combine(testRoot, "obj");
            outputFolder = Path.Combine(testRoot, "index");
            Directory.CreateDirectory(sourceIndexFolder);
            Directory.CreateDirectory(outputFolder);

            // SolutionFinalizer.SortProcessedAssemblies and Paths.ProcessedAssemblies key off this static,
            // exactly as Program.cs leaves it pointed at Pass1's raw index root through Pass2.
            Paths.SolutionDestinationFolder = sourceIndexFolder;

            CreateAssemblyFixture(
                sourceIndexFolder,
                "AssemblyB",
                referencedAssemblies: null,
                projectExplorerHtml: ProjectExplorerFixture("AssemblyB"));

            CreateAssemblyFixture(
                sourceIndexFolder,
                "AssemblyA",
                referencedAssemblies: new[] { "AssemblyB" },
                projectExplorerHtml: ProjectExplorerFixture("AssemblyA"));
        }

        [TestCleanup]
        public void Cleanup()
        {
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
        public void FinalizeProjects_writes_aggregate_files_to_output_root_not_source()
        {
            var finalizer = new SolutionFinalizer(sourceIndexFolder, outputFolder);
            finalizer.FinalizeProjects(emitAssemblyList: false, federation: new Federation());

            File.Exists(Path.Combine(outputFolder, "Assemblies.txt")).ShouldBeTrue();
            File.Exists(Path.Combine(outputFolder, "Projects.txt")).ShouldBeTrue();
            File.Exists(Path.Combine(outputFolder, "i.txt")).ShouldBeTrue();
            File.Exists(Path.Combine(outputFolder, "results.html")).ShouldBeTrue();

            // None of Pass2's aggregate/root artifacts should ever land in Pass1's raw index.
            File.Exists(Path.Combine(sourceIndexFolder, "Assemblies.txt")).ShouldBeFalse();
            File.Exists(Path.Combine(sourceIndexFolder, "Projects.txt")).ShouldBeFalse();
            File.Exists(Path.Combine(sourceIndexFolder, "results.html")).ShouldBeFalse();
        }

        [TestMethod]
        public void FinalizeProjects_copies_each_assembly_folder_into_the_output_root()
        {
            var finalizer = new SolutionFinalizer(sourceIndexFolder, outputFolder);
            finalizer.FinalizeProjects(emitAssemblyList: false, federation: new Federation());

            Directory.Exists(Path.Combine(outputFolder, "AssemblyA")).ShouldBeTrue();
            Directory.Exists(Path.Combine(outputFolder, "AssemblyB")).ShouldBeTrue();
        }

        [TestMethod]
        public void FinalizeProjects_patches_used_by_backlink_only_in_the_output_copy()
        {
            var finalizer = new SolutionFinalizer(sourceIndexFolder, outputFolder);
            finalizer.FinalizeProjects(emitAssemblyList: false, federation: new Federation());

            var outputProjectExplorer = File.ReadAllText(Path.Combine(outputFolder, "AssemblyB", "ProjectExplorer.html"));
            outputProjectExplorer.ShouldContain("Used By");
            outputProjectExplorer.ShouldContain("AssemblyA");

            // Pass1's own copy of the same file must never see the "Used By" backlink -- that
            // cross-assembly fact isn't known until Pass2 aggregates all projects, so it can only ever
            // be written into the finalized output copy.
            var sourceProjectExplorer = File.ReadAllText(Path.Combine(sourceIndexFolder, "AssemblyB", "ProjectExplorer.html"));
            sourceProjectExplorer.ShouldNotContain("Used By");
        }

        [TestMethod]
        public void FinalizeProjects_never_mutates_the_source_index()
        {
            var beforeFiles = CaptureFileHashes(sourceIndexFolder);

            var finalizer = new SolutionFinalizer(sourceIndexFolder, outputFolder);
            finalizer.FinalizeProjects(emitAssemblyList: false, federation: new Federation());

            var afterFiles = CaptureFileHashes(sourceIndexFolder);

            // Same set of files, same bytes -- Pass1's index is untouched by finalization, including the
            // reference-shard/declaration-map consumption and deletion that Pass2 performs on its own copy.
            afterFiles.Keys.OrderBy(k => k).ShouldBe(beforeFiles.Keys.OrderBy(k => k));
            foreach (var relativePath in beforeFiles.Keys)
            {
                afterFiles[relativePath].ShouldBe(beforeFiles[relativePath], $"File '{relativePath}' in the source index was modified by finalization.");
            }
        }

        [TestMethod]
        public void FinalizeProjects_is_idempotent_when_rerun_from_the_same_source()
        {
            var first = new SolutionFinalizer(sourceIndexFolder, outputFolder);
            first.FinalizeProjects(emitAssemblyList: false, federation: new Federation());
            var firstRunProjectExplorer = File.ReadAllText(Path.Combine(outputFolder, "AssemblyB", "ProjectExplorer.html"));

            // Re-running Pass2 from scratch (a fresh SolutionFinalizer, as a second HtmlGenerator
            // invocation would do) against the same unmodified source and the same output root must
            // reproduce the same result -- not append a second "Used By" block or otherwise double up.
            var second = new SolutionFinalizer(sourceIndexFolder, outputFolder);
            second.FinalizeProjects(emitAssemblyList: false, federation: new Federation());
            var secondRunProjectExplorer = File.ReadAllText(Path.Combine(outputFolder, "AssemblyB", "ProjectExplorer.html"));

            secondRunProjectExplorer.ShouldBe(firstRunProjectExplorer);
            CountOccurrences(secondRunProjectExplorer, "Used By").ShouldBe(1);
        }

        [TestMethod]
        public void FinalizeProjects_never_deletes_the_source_declaration_map_or_reference_shards()
        {
            // These two files are Pass1 "intermediates" that Pass2 DOES delete/consume -- but only from
            // its own copy in the output root. This is the invariant a /config: run's cross-config merge
            // step depends on: a config's raw obj/<config>/<project> data must still be there to read
            // even after that config has already been finalized once (e.g. when it was the only config
            // registered at the time). ProjectFinalizer's constructor copying Pass1's folder into the
            // output root before doing anything destructive is what makes this automatic.
            var assemblyBFolder = Path.Combine(sourceIndexFolder, "AssemblyB");
            var declarationMapFile = Path.Combine(assemblyBFolder, Constants.DeclarationMap + ".txt");
            ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap(
                assemblyBFolder,
                new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Tuple<string, long>>>
                {
                    ["deadbeef"] = new System.Collections.Generic.List<Tuple<string, long>>
                    {
                        Tuple.Create("File.cs", 42L),
                    },
                });

            ProjectGenerator.GenerateReferencesDataFilesToAssembly(
                sourceIndexFolder,
                "AssemblyB",
                new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Reference>>
                {
                    ["deadbeef"] = new System.Collections.Generic.List<Reference>
                    {
                        new Reference
                        {
                            FromAssemblyId = "AssemblyA",
                            Url = "AssemblyA/File.cs.html",
                            FromLocalPath = "File.cs",
                            ReferenceLineNumber = 1,
                            ReferenceColumnStart = 1,
                            ReferenceColumnEnd = 2,
                            ReferenceLineText = "someCall();",
                            ToSymbolName = "Method",
                            Kind = ReferenceKind.Reference,
                        },
                    },
                });

            var referenceShardFiles = Directory.GetFiles(
                Path.Combine(assemblyBFolder, Constants.ReferencesFileName),
                "*" + Microsoft.SourceBrowser.HtmlGenerator.ProjectGenerator.ReferenceShardExtension);
            referenceShardFiles.ShouldNotBeEmpty("The fixture must actually produce a reference shard file for this test to be meaningful.");
            File.Exists(declarationMapFile).ShouldBeTrue("The fixture must actually produce a DeclarationMap.txt for this test to be meaningful.");

            var declarationMapBytesBefore = File.ReadAllBytes(declarationMapFile);
            var shardBytesBefore = referenceShardFiles.ToDictionary(f => f, File.ReadAllBytes);

            var finalizer = new SolutionFinalizer(sourceIndexFolder, outputFolder);
            finalizer.FinalizeProjects(emitAssemblyList: false, federation: new Federation());

            // Survive, unmodified, in the source (obj) copy...
            File.Exists(declarationMapFile).ShouldBeTrue();
            File.ReadAllBytes(declarationMapFile).ShouldBe(declarationMapBytesBefore);
            foreach (var shardFile in referenceShardFiles)
            {
                File.Exists(shardFile).ShouldBeTrue();
                File.ReadAllBytes(shardFile).ShouldBe(shardBytesBefore[shardFile]);
            }

            // ...even though Pass2 deletes/consumes its own copy of both, exactly as it always has.
            File.Exists(Path.Combine(outputFolder, "AssemblyB", Constants.DeclarationMap + ".txt")).ShouldBeFalse();
            Directory.GetFiles(
                Path.Combine(outputFolder, "AssemblyB", Constants.ReferencesFileName),
                "*" + Microsoft.SourceBrowser.HtmlGenerator.ProjectGenerator.ReferenceShardExtension)
                .ShouldBeEmpty();
        }

        private static int CountOccurrences(string text, string substring)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) != -1)
            {
                count++;
                index += substring.Length;
            }

            return count;
        }

        private static System.Collections.Generic.Dictionary<string, string> CaptureFileHashes(string root)
        {
            var result = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(root.Length + 1);
                var bytes = File.ReadAllBytes(file);
                result[relative] = Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(bytes));
            }

            return result;
        }

        private static void CreateAssemblyFixture(
            string indexRoot,
            string assemblyName,
            string[] referencedAssemblies,
            string projectExplorerHtml)
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

            if (projectExplorerHtml != null)
            {
                File.WriteAllText(Path.Combine(assemblyFolder, Constants.ProjectExplorer + ".html"), projectExplorerHtml);
            }
        }

        private static string ProjectExplorerFixture(string assemblyName)
        {
            // Mirrors the exact line breaks ProjectGenerator.ProjectExplorer.cs produces --
            // PatchProjectExplorer's insertion-point scan (SolutionFinalizer.cs) matches whole lines, so the
            // "References" folder title and its closing "</div>" must each be on their own line for the
            // "Used By" patch to find its insertion point.
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
