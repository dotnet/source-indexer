using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    /// <summary>
    /// Tests <see cref="ConfigProjectMerger"/> against real per-config obj/&lt;config&gt; fixtures built
    /// with the actual Pass1 writers, including the cross-config "Used By" sentinel scenario tannergooding
    /// asked for: a project-reference edge that only exists under one config must still surface,
    /// config-tagged, in the merged model -- this is what lets the eventual Used-By patch show/grey it
    /// correctly per selected config.
    /// </summary>
    [TestClass]
    public class ConfigProjectMergerTests
    {
        private string testRoot;
        private string windowsObjRoot;
        private string linuxObjRoot;

        [TestInitialize]
        public void Setup()
        {
            testRoot = Path.Combine(Path.GetTempPath(), "ConfigProjectMergerTests_" + Guid.NewGuid().ToString("N"));
            windowsObjRoot = Path.Combine(testRoot, "obj", "windows");
            linuxObjRoot = Path.Combine(testRoot, "obj", "linux");
            Directory.CreateDirectory(windowsObjRoot);
            Directory.CreateDirectory(linuxObjRoot);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }

        private static void WriteProject(
            string objRoot,
            string assemblyId,
            string[] referencedAssemblies,
            Dictionary<string, List<Tuple<string, long>>> declarationMap = null,
            Dictionary<string, List<Reference>> references = null)
        {
            var projectFolder = Path.Combine(objRoot, assemblyId);
            Directory.CreateDirectory(Path.Combine(projectFolder, Constants.ReferencesFileName));

            if (referencedAssemblies != null)
            {
                File.WriteAllLines(Path.Combine(projectFolder, Constants.ReferencedAssemblyList + ".txt"), referencedAssemblies);
            }

            if (declarationMap != null)
            {
                ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap(projectFolder, declarationMap);
            }

            if (references != null)
            {
                ProjectGenerator.GenerateReferencesDataFilesToAssembly(objRoot, assemblyId, references);
            }
        }

        [TestMethod]
        public void DiscoverProjects_ReturnsUnion_OfProjectsAcrossAllConfigs()
        {
            // "WindowsHelper" only exists under the windows config -- a platform-specific project.
            WriteProject(windowsObjRoot, "Shared", referencedAssemblies: Array.Empty<string>());
            WriteProject(windowsObjRoot, "WindowsHelper", referencedAssemblies: Array.Empty<string>());
            WriteProject(linuxObjRoot, "Shared", referencedAssemblies: Array.Empty<string>());

            var configObjRoots = new Dictionary<string, string> { ["windows"] = windowsObjRoot, ["linux"] = linuxObjRoot };
            var projects = ConfigProjectMerger.DiscoverProjects(configObjRoots);

            projects.ShouldBe(new[] { "Shared", "WindowsHelper" });
        }

        [TestMethod]
        public void MergeProject_ConfigConditionalReferenceEdge_IsTaggedOnlyWithTheConfigsThatDeclareIt()
        {
            // The cross-config Used-By sentinel: "App" references "WindowsOnlyLib" only under the
            // windows config (e.g. a conditional ProjectReference in App.csproj) -- under linux, App
            // doesn't reference it at all. The merge must preserve that WindowsOnlyLib's incoming edge
            // from App is config-tagged to "windows" only, not silently applied to every config.
            WriteProject(windowsObjRoot, "App", referencedAssemblies: new[] { "Shared", "WindowsOnlyLib" });
            WriteProject(linuxObjRoot, "App", referencedAssemblies: new[] { "Shared" });

            var configObjRoots = new Dictionary<string, string> { ["windows"] = windowsObjRoot, ["linux"] = linuxObjRoot };
            var merged = ConfigProjectMerger.MergeProject("App", configObjRoots);

            merged.Configs.ShouldBe(new[] { "linux", "windows" });
            merged.ReferencedAssemblies["Shared"].ShouldBe(new[] { "linux", "windows" }, ignoreOrder: true);
            merged.ReferencedAssemblies["WindowsOnlyLib"].ShouldBe(new[] { "windows" });
        }

        [TestMethod]
        public void MergeProject_ReferenceEdgePresentUnderEveryConfig_IsTaggedWithAllOfThem()
        {
            WriteProject(windowsObjRoot, "App", referencedAssemblies: new[] { "Shared" });
            WriteProject(linuxObjRoot, "App", referencedAssemblies: new[] { "Shared" });

            var configObjRoots = new Dictionary<string, string> { ["windows"] = windowsObjRoot, ["linux"] = linuxObjRoot };
            var merged = ConfigProjectMerger.MergeProject("App", configObjRoots);

            merged.ReferencedAssemblies["Shared"].ShouldBe(new[] { "linux", "windows" }, ignoreOrder: true);
        }

        [TestMethod]
        public void MergeProject_MergesDeclarationsAndReferences_UsingTheExistingMergers()
        {
            WriteProject(
                windowsObjRoot,
                "Shared",
                referencedAssemblies: Array.Empty<string>(),
                declarationMap: new Dictionary<string, List<Tuple<string, long>>>
                {
                    ["symbol1"] = new List<Tuple<string, long>> { Tuple.Create("Environment.Windows.cs", 10L) },
                },
                references: new Dictionary<string, List<Reference>>
                {
                    ["symbol1"] = new List<Reference>
                    {
                        new Reference { FromAssemblyId = "App", Url = "u", FromLocalPath = "App.cs", ReferenceLineNumber = 1, ReferenceColumnStart = 0, ReferenceColumnEnd = 1, ReferenceLineText = "x", ToSymbolName = "S", Kind = ReferenceKind.Reference },
                    },
                });

            WriteProject(
                linuxObjRoot,
                "Shared",
                referencedAssemblies: Array.Empty<string>(),
                declarationMap: new Dictionary<string, List<Tuple<string, long>>>
                {
                    ["symbol1"] = new List<Tuple<string, long>> { Tuple.Create("Environment.Unix.cs", 20L) },
                },
                references: null);

            var configObjRoots = new Dictionary<string, string> { ["windows"] = windowsObjRoot, ["linux"] = linuxObjRoot };
            var merged = ConfigProjectMerger.MergeProject("Shared", configObjRoots);

            // Divergent declaration sites -> two locations, each tagged with just its own config.
            merged.DeclarationLocations["symbol1"].Count.ShouldBe(2);
            merged.DeclarationLocations["symbol1"].Single(l => l.FilePath == "Environment.Windows.cs").Configs.ShouldBe(new[] { "windows" });
            merged.DeclarationLocations["symbol1"].Single(l => l.FilePath == "Environment.Unix.cs").Configs.ShouldBe(new[] { "linux" });

            // The windows-only reference to symbol1 must survive the merge, tagged to windows only.
            var reference = merged.References["symbol1"].ShouldHaveSingleItem();
            reference.ConfigSet.ShouldBe(new[] { "windows" });
        }

        [TestMethod]
        public void MergeProject_ProjectMissingFromAConfig_OnlyReflectsTheConfigsItActuallyExistsIn()
        {
            WriteProject(windowsObjRoot, "WindowsHelper", referencedAssemblies: Array.Empty<string>());
            // linux never produces WindowsHelper at all.

            var configObjRoots = new Dictionary<string, string> { ["windows"] = windowsObjRoot, ["linux"] = linuxObjRoot };
            var merged = ConfigProjectMerger.MergeProject("WindowsHelper", configObjRoots);

            merged.Configs.ShouldBe(new[] { "windows" });
        }
    }
}
