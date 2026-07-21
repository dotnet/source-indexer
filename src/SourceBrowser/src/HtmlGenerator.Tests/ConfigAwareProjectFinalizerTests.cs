using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    /// <summary>
    /// End-to-end test for <see cref="ConfigAwareProjectFinalizer"/>: the cross-config "Used By" sentinel
    /// tannergooding asked for, now exercised all the way through to the rendered ProjectExplorer.html --
    /// not just at the in-memory merge-model level (see <see cref="ConfigProjectMergerTests"/> for that).
    /// A reference that exists only under one config must still surface, config-tagged, in the target's
    /// merged Used-By block; a reference present under every config stays untagged (config is inert
    /// metadata in that common case, same convention as <see cref="ConfigLocationMerger.IsFullyShared"/>).
    /// </summary>
    [TestClass]
    public class ConfigAwareProjectFinalizerTests
    {
        private string testRoot;
        private string linuxObjRoot;
        private string windowsObjRoot;
        private string websiteDestinationFolder;

        [TestInitialize]
        public void Setup()
        {
            testRoot = Path.Combine(Path.GetTempPath(), "ConfigAwareProjectFinalizerTests_" + Guid.NewGuid().ToString("N"));
            // "linux" sorts before "windows" ordinally, so it's the primary config ConfigAwareProjectFinalizer
            // renders real HTML content from; deliberately picked so the test also proves the merge reaches
            // beyond just the primary config's own edges.
            linuxObjRoot = Path.Combine(testRoot, "obj", "linux");
            windowsObjRoot = Path.Combine(testRoot, "obj", "windows");
            websiteDestinationFolder = Path.Combine(testRoot, "index");
            Directory.CreateDirectory(linuxObjRoot);
            Directory.CreateDirectory(windowsObjRoot);
            Directory.CreateDirectory(websiteDestinationFolder);
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
        public void Finalize_PatchesUsedBy_WithMergedConfigTaggedEdges_AcrossAllRegisteredConfigs()
        {
            // "Utils" is a normal shared project present under every config -- this is what we assert on.
            CreateAssemblyFixture(linuxObjRoot, "Utils", referencedAssemblies: null);
            CreateAssemblyFixture(windowsObjRoot, "Utils", referencedAssemblies: null);

            // "App" references Utils under every config -- the common case; its edge should stay untagged.
            CreateAssemblyFixture(linuxObjRoot, "App", referencedAssemblies: new[] { "Utils" });
            CreateAssemblyFixture(windowsObjRoot, "App", referencedAssemblies: new[] { "Utils" });

            // "WindowsFeature" exists (and references Utils) only under the windows config -- e.g. a
            // conditional <ProjectReference> in WindowsFeature.csproj. Under linux, this project and its
            // edge to Utils simply don't exist. Utils' merged Used-By must still surface this edge,
            // tagged to windows only -- not silently dropped (last-config-wins would drop it, since linux
            // is the primary/rendering config here) and not silently applied to every config either.
            CreateAssemblyFixture(windowsObjRoot, "WindowsFeature", referencedAssemblies: new[] { "Utils" });

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var utilsExplorerHtml = File.ReadAllText(Path.Combine(websiteDestinationFolder, "Utils", Constants.ProjectExplorer + ".html"));

            utilsExplorerHtml.ShouldContain("<div class=\"folderTitle\">Used By</div><div class=\"folder\">");

            // App's edge holds under every registered config -- no config tag, exactly like today's
            // single-config Used By rendering.
            utilsExplorerHtml.ShouldContain("<a class=\"reference\" href=\"/#App\" target=\"_top\">App</a>");

            // WindowsFeature's edge only exists under windows -- config-tagged so the milestone-4 client
            // selector can grey/hide it when linux is selected.
            utilsExplorerHtml.ShouldContain("<a class=\"reference\" data-configs=\"windows\" href=\"/#WindowsFeature\" target=\"_top\">WindowsFeature</a>");

            // The root aggregates driven by the same referenced-assembly-edge data must reflect the
            // MERGED count (App + WindowsFeature = 2), not just the primary (linux) config's own count
            // (App only = 1) that SolutionFinalizer's constructor would have originally written.
            var topReferenced = File.ReadAllLines(Path.Combine(websiteDestinationFolder, Constants.TopReferencedAssemblies + ".txt"));
            topReferenced.ShouldContain("Utils;2");

            var masterAssemblyMap = File.ReadAllLines(Path.Combine(websiteDestinationFolder, Constants.MasterAssemblyMap + ".txt"));
            masterAssemblyMap.Any(line => line.StartsWith("Utils;", StringComparison.Ordinal) && line.EndsWith(";2", StringComparison.Ordinal))
                .ShouldBeTrue("MasterAssemblyMap.txt should record Utils' merged referencing count (2), not the primary-config-only count (1). Actual: " + string.Join(" | ", masterAssemblyMap));
        }

        [TestMethod]
        public void Finalize_DoesNotGreyOutADeclaration_ThatIsOnlyReferencedUnderANonPrimaryConfig()
        {
            // "linux" is primary (sorts first) and is what actually renders "File.cs.html". "symbol1" is
            // declared identically under both configs (a non-divergent declaration -- this test is about
            // the reference-driven backpatch, not declaration-location merging) but is referenced ONLY
            // under windows -- e.g. a call site inside "#if WINDOWS". The primary (linux) config's own
            // on-disk shard has ZERO records for symbol1, so the ordinary single-config backpatch would
            // incorrectly grey it out as unreferenced. "symbol2" is the control: declared under both,
            // referenced under NEITHER config -- it must still be correctly greyed, proving this test
            // actually exercises the mechanism rather than accidentally disabling all zeroing.
            // symbol1 uses a real 16-hex-char id (not an arbitrary literal) because it's now also fed
            // through WriteReferencesContent's base-member lookup (Serialization.HexStringToULong) via
            // the merged/divergent FAR append path below.
            const string symbol1 = "0000000000000001";
            const string symbol2 = "symbol2";
            const string marker1 = "1111111111111111"; // 16 bytes -- symbol1's placeholder ID field.
            const string marker2 = "2222222222222222"; // 16 bytes -- symbol2's placeholder ID field.
            var pageContent = "AAAA" + marker1 + "BBBB" + marker2 + "CCCC";
            long symbol1Offset = 4;
            long symbol2Offset = 4 + 16 + 4;

            CreateAssemblyFixture(linuxObjRoot, "Shared", referencedAssemblies: null);
            CreateAssemblyFixture(windowsObjRoot, "Shared", referencedAssemblies: null);

            File.WriteAllText(Path.Combine(linuxObjRoot, "Shared", "File.cs.html"), pageContent, Encoding.ASCII);

            var declarationMap = new Dictionary<string, List<Tuple<string, long>>>
            {
                [symbol1] = new List<Tuple<string, long>> { Tuple.Create("File.cs", symbol1Offset) },
                [symbol2] = new List<Tuple<string, long>> { Tuple.Create("File.cs", symbol2Offset) },
            };
            ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap(Path.Combine(linuxObjRoot, "Shared"), declarationMap);
            ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap(Path.Combine(windowsObjRoot, "Shared"), declarationMap);

            // windows-only reference to symbol1; linux has none; neither config references symbol2 at all.
            ProjectGenerator.GenerateReferencesDataFilesToAssembly(
                windowsObjRoot,
                "Shared",
                new Dictionary<string, List<Reference>>
                {
                    [symbol1] = new List<Reference>
                    {
                        new Reference
                        {
                            FromAssemblyId = "App",
                            Url = "App/File.cs.html",
                            FromLocalPath = "File.cs",
                            ReferenceLineNumber = 1,
                            ReferenceColumnStart = 0,
                            ReferenceColumnEnd = 1,
                            ReferenceLineText = "x",
                            ToSymbolName = "Symbol1",
                            Kind = ReferenceKind.Reference,
                        },
                    },
                });

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var outputPageBytes = File.ReadAllBytes(Path.Combine(websiteDestinationFolder, "Shared", "File.cs.html"));

            var symbol1Bytes = new byte[16];
            Array.Copy(outputPageBytes, symbol1Offset, symbol1Bytes, 0, 16);
            var symbol2Bytes = new byte[16];
            Array.Copy(outputPageBytes, symbol2Offset, symbol2Bytes, 0, 16);

            // symbol1 is genuinely referenced (only under windows) -- must NOT be zeroed just because
            // linux, the rendering config, has no reference to it in its own on-disk shard.
            symbol1Bytes.ShouldNotBe(SymbolIdService.ZeroId);
            Encoding.ASCII.GetString(symbol1Bytes).ShouldBe(marker1);

            // symbol2 is genuinely unreferenced under every config -- must still be greyed, proving the
            // backpatch mechanism is actually running (not just universally skipped).
            symbol2Bytes.ShouldBe(SymbolIdService.ZeroId);

            // Regression-turned-behavior guard for the FAR blast-radius work: the windows-only reference
            // to symbol1 must influence the backpatch grey/no-grey decision above (proven already), AND
            // -- per the milestone-4 "replace the primary-only partial render from the merged set"
            // contract -- must now ALSO surface in the rendered FAR output, config-tagged, since
            // ConfigReferenceMerger correctly reports it as not fully shared (windows only, out of
            // [linux, windows]). This intentionally supersedes the earlier absence-only assertion: linux
            // (the rendering config) has zero on-disk reference records for symbol1, so the pack's only
            // entry for it comes from the merged/divergent append path, tagged data-configs="windows".
            var referencePackFile = Path.Combine(websiteDestinationFolder, "Shared", Constants.ReferencesFileName, Constants.ReferencePackFileName);
            File.Exists(referencePackFile).ShouldBeTrue(
                "A reference pack should now exist for Shared: symbol1's windows-only reference must render, config-tagged, per the milestone-4 replace-from-merged-set contract.");

            var packText = Encoding.UTF8.GetString(File.ReadAllBytes(referencePackFile));
            packText.ShouldContain("data-configs=\"windows\"");
        }

        [TestMethod]
        public void Finalize_IncludesAProject_ThatExistsOnlyUnderANonPrimaryConfig()
        {
            // "Utils" is shared, present under both configs.
            CreateAssemblyFixture(linuxObjRoot, "Utils", referencedAssemblies: null);
            CreateAssemblyFixture(windowsObjRoot, "Utils", referencedAssemblies: null);

            // "WindowsOnlyProject" exists ONLY under windows -- e.g. conditionally included in the
            // windows-only .sln/.slnx. It must not be silently absent from the merged site just because
            // "linux" (the primary/rendering config here) never generated it at all.
            CreateAssemblyFixture(windowsObjRoot, "WindowsOnlyProject", referencedAssemblies: new[] { "Utils" });

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var windowsOnlyProjectExplorer = Path.Combine(websiteDestinationFolder, "WindowsOnlyProject", Constants.ProjectExplorer + ".html");
            File.Exists(windowsOnlyProjectExplorer).ShouldBeTrue(
                "WindowsOnlyProject exists only under the windows config and must still be staged and finalized into the merged site.");

            // And it must show up in the merged aggregates too -- CreateMasterDeclarationsIndex,
            // SolutionFinalizer's project map, etc. are all driven by SolutionFinalizer.projects, which is
            // whatever DiscoverProjects finds in the staged root.
            var masterAssemblyMap = File.ReadAllLines(Path.Combine(websiteDestinationFolder, Constants.MasterAssemblyMap + ".txt"));
            masterAssemblyMap.Any(line => line.StartsWith("WindowsOnlyProject;", StringComparison.Ordinal))
                .ShouldBeTrue("MasterAssemblyMap.txt should list WindowsOnlyProject. Actual: " + string.Join(" | ", masterAssemblyMap));
        }

        [TestMethod]
        public void Finalize_WritesSolutionExplorer_ReconstructedFromPersistedFolderChains()
        {
            // "Utils" lives at the solution root; "App" lives under a "src" solution folder -- mirrors
            // Pass1's newly-persisted SolutionFolder.txt (see SolutionGenerator.SolutionExplorer.cs).
            CreateAssemblyFixture(linuxObjRoot, "Utils", referencedAssemblies: null);
            CreateAssemblyFixture(windowsObjRoot, "Utils", referencedAssemblies: null);
            File.WriteAllLines(Path.Combine(linuxObjRoot, "Utils", Constants.SolutionFolderFileName), Array.Empty<string>());

            CreateAssemblyFixture(linuxObjRoot, "App", referencedAssemblies: new[] { "Utils" });
            CreateAssemblyFixture(windowsObjRoot, "App", referencedAssemblies: new[] { "Utils" });
            File.WriteAllLines(Path.Combine(linuxObjRoot, "App", Constants.SolutionFolderFileName), new[] { "src" });

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var solutionExplorerFile = Path.Combine(websiteDestinationFolder, Constants.SolutionExplorer + ".html");
            File.Exists(solutionExplorerFile).ShouldBeTrue(
                "SolutionExplorer.html must be written in config-aware mode, reconstructed from each project's persisted solution-folder chain.");

            var solutionExplorerHtml = File.ReadAllText(solutionExplorerFile);
            solutionExplorerHtml.ShouldContain("<div class=\"folderTitle\">src</div>");
        }

        [TestMethod]
        public void Finalize_WritesSolutionExplorer_WithRepoNameFromProjectInfo_ForEveryMergedProject()
        {
            // Regression test for a real bug found while validating the config selector against the
            // repo-scoped-search feature together: ComputeMergedSolutionExplorerRoot only reconstructed
            // each project's folder chain from SolutionFolder.txt and never read back RepoName from the
            // same ProjectInfo.txt the ordinary (non-config) ProjectFinalizer.ReadProjectInfo already
            // reads it from -- so /repoPath + /config used together silently dropped every data-repo
            // tag from the merged SolutionExplorer.html. Two projects, tagged with two different repos,
            // and neither is the primary (linux) config's own -- exercising both the primary-found and
            // fallback-config-found branches of ComputeMergedSolutionExplorerRoot.
            CreateAssemblyFixture(linuxObjRoot, "Utils", referencedAssemblies: null, repoName: "RepoA");
            CreateAssemblyFixture(windowsObjRoot, "Utils", referencedAssemblies: null, repoName: "RepoA");
            WriteProjectExplorerWithAdjacentRootFolderDiv(linuxObjRoot, "Utils");

            // "WindowsOnlyProject" exists only under windows (the fallback-config-found branch), tagged
            // with a different repo -- must still surface its own repo tag, not RepoA's or none at all.
            CreateAssemblyFixture(windowsObjRoot, "WindowsOnlyProject", referencedAssemblies: new[] { "Utils" }, repoName: "RepoB");
            WriteProjectExplorerWithAdjacentRootFolderDiv(windowsObjRoot, "WindowsOnlyProject");

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var solutionExplorerHtml = File.ReadAllText(Path.Combine(websiteDestinationFolder, Constants.SolutionExplorer + ".html"));

            // Utils' RepoName is read from ProjectInfo.txt via the primary (linux) config and must
            // survive the merge.
            solutionExplorerHtml.ShouldContain("data-repo=\"RepoA\"");

            // WindowsOnlyProject's RepoName is only found via the fallback (windows) config, since linux
            // never generated this project, and must also survive the merge.
            solutionExplorerHtml.ShouldContain("data-repo=\"RepoB\"");
        }

        [TestMethod]
        public void Finalize_WritesAssembliesTxt_WithRepoNameSurvivingTheReferencingCountRewrite()
        {
            // Regression test for the sibling bug to the one above, found in the same validation pass:
            // SolutionFinalizer.CreateProjectMap (via FinalizeProjects) writes Assemblies.txt with the
            // correct repo/solution tags first, but PatchCrossConfigUsedByAndAggregates always runs next
            // and its RewriteProjectMapReferencingCounts re-writes the SAME Assemblies.txt a second time
            // (to patch in cross-config-merged referencing counts) -- originally without threading the
            // repo/solution tags through at all, silently erasing what CreateProjectMap had just written
            // moments earlier and leaving /api/repos permanently empty for any config-merged site.
            CreateAssemblyFixture(linuxObjRoot, "Utils", referencedAssemblies: null, repoName: "RepoA");
            CreateAssemblyFixture(windowsObjRoot, "Utils", referencedAssemblies: null, repoName: "RepoA");

            CreateAssemblyFixture(linuxObjRoot, "App", referencedAssemblies: new[] { "Utils" }, repoName: "RepoB");
            CreateAssemblyFixture(windowsObjRoot, "App", referencedAssemblies: new[] { "Utils" }, repoName: "RepoB");

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var assembliesFile = Path.Combine(websiteDestinationFolder, Constants.MasterAssemblyMap + ".txt");
            File.Exists(assembliesFile).ShouldBeTrue(
                "Assemblies.txt must be written unconditionally by CreateProjectMap regardless of emitAssemblyList.");

            var assembliesText = File.ReadAllText(assembliesFile);
            assembliesText.Contains(";RepoA;").ShouldBeTrue(
                "Utils' repo tag must survive RewriteProjectMapReferencingCounts' second write to Assemblies.txt. Actual: " + assembliesText);
            assembliesText.Contains(";RepoB;").ShouldBeTrue(
                "App's repo tag must survive RewriteProjectMapReferencingCounts' second write to Assemblies.txt. Actual: " + assembliesText);
        }

        [TestMethod]
        public void Finalize_GroupsSolutionExplorer_UnderRepoFolders_WhenTheMergedSiteSpansMultipleRepos()
        {
            // The Repo/Solution Solution Explorer grouping feature (SolutionExplorerGroupingTests,
            // Program.GetSolutionExplorerGroupingFolder) is computed by Program.IndexSolutionsAsync
            // during a single Pass1 run, over that run's own inputs -- ComputeMergedSolutionExplorerRoot
            // must recompute the same grouping over the MERGED (cross-config) project set, or a
            // config-merged site that also spans multiple repos would silently lose the grouping even
            // though the ordinary single-config path would have shown it.
            CreateAssemblyFixture(linuxObjRoot, "Utils", referencedAssemblies: null, repoName: "ClangSharp");
            CreateAssemblyFixture(windowsObjRoot, "Utils", referencedAssemblies: null, repoName: "ClangSharp");

            CreateAssemblyFixture(linuxObjRoot, "App", referencedAssemblies: new[] { "Utils" }, repoName: "LLVMSharp");
            CreateAssemblyFixture(windowsObjRoot, "App", referencedAssemblies: new[] { "Utils" }, repoName: "LLVMSharp");

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var solutionExplorerHtml = File.ReadAllText(Path.Combine(websiteDestinationFolder, Constants.SolutionExplorer + ".html"));

            solutionExplorerHtml.Contains("<div class=\"folderTitle repoTitle\" data-repo=\"ClangSharp\" data-repo-path=\"ClangSharp\">ClangSharp</div>").ShouldBeTrue(
                "Utils must be nested under a ClangSharp repo folder now that the merged site spans two repos. Actual: " + solutionExplorerHtml);
            solutionExplorerHtml.Contains("<div class=\"folderTitle repoTitle\" data-repo=\"LLVMSharp\" data-repo-path=\"LLVMSharp\">LLVMSharp</div>").ShouldBeTrue(
                "App must be nested under an LLVMSharp repo folder now that the merged site spans two repos. Actual: " + solutionExplorerHtml);
        }

        [TestMethod]
        public void Finalize_StagesAFile_ThatExistsOnlyUnderANonPrimaryConfig()
        {
            // "Shared" exists under both configs, but "Windows.cs" (e.g. a platform-specific partial
            // class file) is only ever compiled under the windows config -- linux (primary) never
            // produced a page for it at all. It must still be reachable in the merged site rather than
            // silently missing just because the rendering (primary) config never generated it.
            CreateAssemblyFixture(linuxObjRoot, "Shared", referencedAssemblies: null);
            CreateAssemblyFixture(windowsObjRoot, "Shared", referencedAssemblies: null);

            File.WriteAllText(Path.Combine(windowsObjRoot, "Shared", "Windows.cs.html"), "<html>windows-only</html>");

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var stagedFile = Path.Combine(websiteDestinationFolder, "Shared", "Windows.cs.html");
            File.Exists(stagedFile).ShouldBeTrue(
                "Windows.cs.html only exists under the windows config and must still be staged into the merged site's Shared project.");
            File.ReadAllText(stagedFile).ShouldBe("<html>windows-only</html>");
        }

        [TestMethod]
        public void Finalize_StagesTaggedVariants_ForAFileThatRendersDifferentlyAcrossConfigs()
        {
            // "EnvHelper.cs" exists at the SAME relative path under BOTH configs but renders DIFFERENT
            // content (e.g. an "#if WINDOWS" region whose active branch differs) -- ConfigFileDeduper's
            // "shared-render-divergent" bucket. "Program.cs" exists at the same path under both configs
            // too, but renders IDENTICALLY -- the common case, which must stay completely untouched (no
            // banner, no extra variant, single physical page).
            CreateAssemblyFixture(linuxObjRoot, "Shared", referencedAssemblies: null);
            CreateAssemblyFixture(windowsObjRoot, "Shared", referencedAssemblies: null);

            string LinkPanel() => "<div class=\"dH\">header</div>";

            File.WriteAllText(Path.Combine(linuxObjRoot, "Shared", "EnvHelper.cs.html"), LinkPanel() + "<div class=\"code\">unix branch active</div>");
            File.WriteAllText(Path.Combine(windowsObjRoot, "Shared", "EnvHelper.cs.html"), LinkPanel() + "<div class=\"code\">windows branch active</div>");

            File.WriteAllText(Path.Combine(linuxObjRoot, "Shared", "Program.cs.html"), LinkPanel() + "<div class=\"code\">identical everywhere</div>");
            File.WriteAllText(Path.Combine(windowsObjRoot, "Shared", "Program.cs.html"), LinkPanel() + "<div class=\"code\">identical everywhere</div>");

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var sharedFolder = Path.Combine(websiteDestinationFolder, "Shared");

            // The non-divergent file must stay a single physical page with no switcher banner at all --
            // byte-identical to what a single-config run would have produced.
            var programHtml = File.ReadAllText(Path.Combine(sharedFolder, "Program.cs.html"));
            programHtml.ShouldNotContain("configFileVariantBanner");
            Directory.GetFiles(sharedFolder, "Program.cs~*.html").ShouldBeEmpty();

            // The divergent file's PRIMARY (linux -- alphabetically first) rendering must keep the
            // ORIGINAL path, not an arbitrary hash-order pick, so any existing link into it keeps working.
            var primaryVariantPath = Path.Combine(sharedFolder, "EnvHelper.cs.html");
            File.Exists(primaryVariantPath).ShouldBeTrue();
            var primaryHtml = File.ReadAllText(primaryVariantPath);
            primaryHtml.ShouldContain("unix branch active");

            // Exactly one alternate, suffixed variant for the windows rendering.
            var suffixedFiles = Directory.GetFiles(sharedFolder, "EnvHelper.cs~*.html");
            suffixedFiles.Length.ShouldBe(1);
            var alternateHtml = File.ReadAllText(suffixedFiles[0]);
            alternateHtml.ShouldContain("windows branch active");

            // Both pages must carry a config-tagged switcher banner the client selector can grey/hide
            // between, each variant tagged with its OWN config(s) -- not the other's.
            primaryHtml.ShouldContain("configFileVariantBanner");
            primaryHtml.ShouldContain("data-configs=\"linux\"");
            alternateHtml.ShouldContain("configFileVariantBanner");
            alternateHtml.ShouldContain("data-configs=\"windows\"");

            // Each banner must link to the OTHER variant so a reader can actually reach it.
            var alternateFileName = Path.GetFileName(suffixedFiles[0]);
            var alternateUrlFragment = "Shared/" + Path.GetFileNameWithoutExtension(alternateFileName);
            primaryHtml.ShouldContain(alternateUrlFragment);
            alternateHtml.ShouldContain("Shared/EnvHelper.cs\"");
        }

        [TestMethod]
        public void Finalize_RewritesDisambiguationPage_ForADeclarationThatDivergesAcrossConfigs()
        {
            // "Widget" is declared in a DIFFERENT single file under each config -- Widget.Windows.cs
            // under windows, Widget.Unix.cs under linux (primary). Neither config alone ever produces a
            // disambiguation page (each has only 1 location for "widget"), so the merged view is where
            // this genuinely first becomes a multi-location symbol that needs one.
            CreateAssemblyFixture(linuxObjRoot, "Shared", referencedAssemblies: null);
            CreateAssemblyFixture(windowsObjRoot, "Shared", referencedAssemblies: null);

            File.WriteAllText(Path.Combine(linuxObjRoot, "Shared", "Widget.Unix.cs.html"), "<html>unix widget</html>");
            File.WriteAllText(Path.Combine(windowsObjRoot, "Shared", "Widget.Windows.cs.html"), "<html>windows widget</html>");

            var linuxDeclarationMap = new Dictionary<string, List<Tuple<string, long>>>
            {
                ["widget"] = new List<Tuple<string, long>> { Tuple.Create("Widget.Unix.cs", 0L) },
            };
            var windowsDeclarationMap = new Dictionary<string, List<Tuple<string, long>>>
            {
                ["widget"] = new List<Tuple<string, long>> { Tuple.Create("Widget.Windows.cs", 0L) },
            };
            ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap(Path.Combine(linuxObjRoot, "Shared"), linuxDeclarationMap);
            ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap(Path.Combine(windowsObjRoot, "Shared"), windowsDeclarationMap);

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var disambiguationFile = Path.Combine(websiteDestinationFolder, "Shared", Constants.PartialResolvingFileName, "widget.html");
            File.Exists(disambiguationFile).ShouldBeTrue(
                "A cross-config disambiguation page must be created for 'widget' even though neither individual config had more than 1 location for it.");

            var disambiguationHtml = File.ReadAllText(disambiguationFile);
            disambiguationHtml.ShouldContain("Widget.Unix.cs");
            disambiguationHtml.ShouldContain("Widget.Windows.cs");
            disambiguationHtml.ShouldContain("<span class=\"partialTypeConfigTag\">[linux]</span>");
            disambiguationHtml.ShouldContain("<span class=\"partialTypeConfigTag\">[windows]</span>");

            // Machine-readable data-configs, for the client config selector -- neither location covers
            // both registered configs, so both links must be tagged (sentinel for the selector's
            // "declarations carry data-configs" contract).
            disambiguationHtml.ShouldContain("data-configs=\"linux\"");
            disambiguationHtml.ShouldContain("data-configs=\"windows\"");

            // And the windows-only file it links to must actually be reachable in the merged site.
            File.Exists(Path.Combine(websiteDestinationFolder, "Shared", "Widget.Windows.cs.html")).ShouldBeTrue();
        }

        [TestMethod]
        public void Finalize_WritesRegisteredConfigsFile_ForTheClientSelectorToDiscover()
        {
            CreateAssemblyFixture(linuxObjRoot, "Shared", referencedAssemblies: null);
            CreateAssemblyFixture(windowsObjRoot, "Shared", referencedAssemblies: null);

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var configsFilePath = Path.Combine(websiteDestinationFolder, Constants.RegisteredConfigsFileName);
            File.Exists(configsFilePath).ShouldBeTrue(
                "A config-aware merge must expose the registered config list at the website root so the client selector can discover it.");

            // No axis tags given (the common/default case, e.g. plain /config:linux, /config:windows)
            // -- "axes"/"configAxisValues" are present but empty, so the client falls back to a flat,
            // ungrouped list exactly like before axis support existed.
            File.ReadAllText(configsFilePath).ShouldBe(
                "{\"configs\":[\"linux\",\"windows\"],\"axes\":{},\"configAxisValues\":{}}");
        }

        [TestMethod]
        public void Finalize_WritesRegisteredConfigsFile_WithAxisTags_WhenGiven()
        {
            CreateAssemblyFixture(linuxObjRoot, "Shared", referencedAssemblies: null);
            CreateAssemblyFixture(windowsObjRoot, "Shared", referencedAssemblies: null);

            var configObjRoots = new Dictionary<string, string> { ["linux-x64"] = linuxObjRoot, ["windows-x64"] = windowsObjRoot };
            var axisTagsByConfig = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["linux-x64"] = new Dictionary<string, string> { ["os"] = "linux", ["arch"] = "x64" },
                ["windows-x64"] = new Dictionary<string, string> { ["os"] = "windows", ["arch"] = "x64" },
            };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation(), axisTagsByConfig: axisTagsByConfig);

            var configsFilePath = Path.Combine(websiteDestinationFolder, Constants.RegisteredConfigsFileName);
            var json = File.ReadAllText(configsFilePath);

            // "axes" collects the distinct values seen per axis across all configs, so the client can
            // group its selector by axis (e.g. an "os" row and an "arch" row) instead of one flat,
            // unstructured checkbox per config name.
            json.ShouldContain("\"configs\":[\"linux-x64\",\"windows-x64\"]");
            json.ShouldContain("\"axes\":{\"arch\":[\"x64\"],\"os\":[\"linux\",\"windows\"]}");
            json.ShouldContain("\"configAxisValues\":{\"linux-x64\":{\"os\":\"linux\",\"arch\":\"x64\"},\"windows-x64\":{\"os\":\"windows\",\"arch\":\"x64\"}}");
        }

        [TestMethod]
        public void Finalize_TagsFarEntries_WithDataConfigs_ForOccurrencesThatDivergeAcrossConfigs()
        {
            // "symbol1" is referenced from the SAME call site (line 1) under both configs -- the common,
            // fully-shared case -- and from a SECOND call site (line 2) that exists ONLY under windows,
            // e.g. a "#if WINDOWS"-guarded call. This is exactly the "mixed symbol" case flagged during
            // this milestone's design discussion: the merged FAR output must show BOTH occurrences,
            // untagged for the shared one and data-configs="windows" for the divergent one -- not drop
            // the windows-only occurrence, and not tag the shared occurrence just because the symbol AS
            // A WHOLE has some divergence. Uses a real 16-hex-char id since it flows through
            // WriteReferencesContent's base-member lookup (Serialization.HexStringToULong).
            const string symbol1 = "0000000000000001";
            CreateAssemblyFixture(linuxObjRoot, "Shared", referencedAssemblies: null);
            CreateAssemblyFixture(windowsObjRoot, "Shared", referencedAssemblies: null);

            var declarationMap = new Dictionary<string, List<Tuple<string, long>>>
            {
                [symbol1] = new List<Tuple<string, long>> { Tuple.Create("File.cs", 0L) },
            };
            ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap(Path.Combine(linuxObjRoot, "Shared"), declarationMap);
            ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap(Path.Combine(windowsObjRoot, "Shared"), declarationMap);
            File.WriteAllText(Path.Combine(linuxObjRoot, "Shared", "File.cs.html"), new string('A', 24), Encoding.ASCII);

            Reference Occurrence(int line) => new Reference
            {
                FromAssemblyId = "App",
                Url = "App/File.cs.html",
                FromLocalPath = "File.cs",
                ReferenceLineNumber = line,
                ReferenceColumnStart = 0,
                ReferenceColumnEnd = 1,
                ReferenceLineText = "x",
                ToSymbolName = "Symbol1",
                Kind = ReferenceKind.Reference,
            };

            // Same occurrence (line 1) under both configs; a second, windows-only occurrence (line 2).
            ProjectGenerator.GenerateReferencesDataFilesToAssembly(
                linuxObjRoot,
                "Shared",
                new Dictionary<string, List<Reference>> { [symbol1] = new List<Reference> { Occurrence(1) } });
            ProjectGenerator.GenerateReferencesDataFilesToAssembly(
                windowsObjRoot,
                "Shared",
                new Dictionary<string, List<Reference>> { [symbol1] = new List<Reference> { Occurrence(1), Occurrence(2) } });

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var referencesFolder = Path.Combine(websiteDestinationFolder, "Shared", Constants.ReferencesFileName);
            var referencePackFile = Path.Combine(referencesFolder, Constants.ReferencePackFileName);
            File.Exists(referencePackFile).ShouldBeTrue();

            // Read ONLY the fragment the index actually serves for this symbol -- not the raw pack file.
            // The base pass (GenerateBaseAndInterfaceOnlyReferencesFiles) renders symbol1's primary-only
            // fragment (line 1) FIRST, then GenerateMergedReferencesFragments appends the merged fragment
            // (lines 1+2) SECOND. Both fragments' bytes physically remain in references.pack, but
            // ReadPackedIndex mirrors the server's ReferencePack.TryLoad: it builds index[id] = i while
            // scanning records in write order, so a later record for a duplicate id overwrites the
            // dictionary entry. Reading the pack through the index (as a real client would) rather than
            // substring-searching the whole raw file is what actually proves there is no double-render --
            // the primary-only fragment's bytes are dead weight on disk but never served.
            var index = ReadPackedIndex(referencesFolder);
            index.ShouldContainKey(symbol1);
            var packText = Encoding.UTF8.GetString(ReadPackedFragment(referencesFolder, index[symbol1]));

            // Both occurrences must be present -- the windows-only one must not be dropped just because
            // the primary (linux) render only ever knew about line 1.
            packText.ShouldContain("<b>1</b>");
            packText.ShouldContain("<b>2</b>");

            // Exactly-once: the served fragment must be the MERGED render, not primary-only-plus-merged
            // concatenated. If the base pass's primary-only fragment were somehow served (or concatenated)
            // alongside the merged one, line 1 would appear twice.
            CountOccurrences(packText, "<b>1</b>").ShouldBe(1);
            CountOccurrences(packText, "<b>2</b>").ShouldBe(1);

            // The shared occurrence (line 1) must render untagged -- same convention as a fully-shared
            // symbol, and the same output an ordinary single-config run would have produced for it.
            var line1AnchorStart = packText.IndexOf("<b>1</b>", StringComparison.Ordinal);
            var line1AnchorTagStart = packText.LastIndexOf("<a href=", line1AnchorStart, StringComparison.Ordinal);
            packText.Substring(line1AnchorTagStart, line1AnchorStart - line1AnchorTagStart).ShouldNotContain("data-configs");

            // The windows-only occurrence (line 2) must carry data-configs="windows".
            var line2AnchorStart = packText.IndexOf("<b>2</b>", StringComparison.Ordinal);
            var line2AnchorTagStart = packText.LastIndexOf("<a href=", line2AnchorStart, StringComparison.Ordinal);
            packText.Substring(line2AnchorTagStart, line2AnchorStart - line2AnchorTagStart).ShouldContain("data-configs=\"windows\"");
        }

        [TestMethod]
        public void Finalize_PacksReferences_ForSymbolIdsLongerThan16Chars()
        {
            // The reference pack index stores one length-prefixed id per record, so it must not assume the
            // usual 16-hex-char symbol hash. Some references are keyed by longer ids (e.g. the GuidAssembly
            // uses full 36-char guid strings), which previously overflowed the fixed 16-byte id field in
            // ReferencePackBuilder.Complete: "The output byte buffer is too small to contain the encoded
            // data". "Shared" is a regular (packed) assembly, and WriteReferencesContent's base-member
            // lookup (Serialization.HexStringToULong) only consumes the first 16 chars, so a longer hex id
            // still renders and packs -- reproducing the overflow end-to-end and reading it back through the
            // same index the server uses.
            const string symbolId = "0000000000000001abcd"; // 20 hex chars -- longer than the 16-char field.

            CreateAssemblyFixture(linuxObjRoot, "Shared", referencedAssemblies: null);
            CreateAssemblyFixture(windowsObjRoot, "Shared", referencedAssemblies: null);

            var declarationMap = new Dictionary<string, List<Tuple<string, long>>>
            {
                [symbolId] = new List<Tuple<string, long>> { Tuple.Create("File.cs", 0L) },
            };
            ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap(Path.Combine(linuxObjRoot, "Shared"), declarationMap);
            ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap(Path.Combine(windowsObjRoot, "Shared"), declarationMap);
            File.WriteAllText(Path.Combine(linuxObjRoot, "Shared", "File.cs.html"), new string('A', 24), Encoding.ASCII);

            var references = new Dictionary<string, List<Reference>>
            {
                [symbolId] = new List<Reference>
                {
                    new Reference
                    {
                        FromAssemblyId = "App",
                        Url = "App/File.cs.html",
                        FromLocalPath = "File.cs",
                        ReferenceLineNumber = 1,
                        ReferenceColumnStart = 0,
                        ReferenceColumnEnd = 1,
                        ReferenceLineText = "x",
                        ToSymbolName = "SomeSymbol",
                        Kind = ReferenceKind.Reference,
                    },
                },
            };

            ProjectGenerator.GenerateReferencesDataFilesToAssembly(linuxObjRoot, "Shared", references);
            ProjectGenerator.GenerateReferencesDataFilesToAssembly(windowsObjRoot, "Shared", references);

            var configObjRoots = new Dictionary<string, string> { ["linux"] = linuxObjRoot, ["windows"] = windowsObjRoot };

            ConfigAwareProjectFinalizer.Finalize(configObjRoots, websiteDestinationFolder, emitAssemblyList: false, federation: new Federation());

            var referencesFolder = Path.Combine(websiteDestinationFolder, "Shared", Constants.ReferencesFileName);
            var index = ReadPackedIndex(referencesFolder);
            index.ShouldContainKey(symbolId);

            var packText = Encoding.UTF8.GetString(ReadPackedFragment(referencesFolder, index[symbolId]));
            packText.ShouldContain("<b>1</b>");
        }

        // Mirrors SourceIndexServer's ReferencePack.TryLoad index format (int32 count, then per record a
        // length-prefixed symbol id + int64 offset + int32 length), scanning in write order so a later
        // record for a duplicate id overwrites the earlier one -- same last-write-wins semantics the real
        // server relies on. Returns symbolId -> (offset, length) for the record that would actually be served.
        private static Dictionary<string, (long Offset, int Length)> ReadPackedIndex(string referencesFolder)
        {
            var indexPath = Path.Combine(referencesFolder, Constants.ReferenceIndexFileName);
            var result = new Dictionary<string, (long, int)>(StringComparer.Ordinal);

            using (var stream = File.OpenRead(indexPath))
            using (var reader = new BinaryReader(stream))
            {
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var id = reader.ReadString();
                    long offset = reader.ReadInt64();
                    int length = reader.ReadInt32();
                    result[id] = (offset, length);
                }
            }

            return result;
        }

        private static byte[] ReadPackedFragment(string referencesFolder, (long Offset, int Length) record)
        {
            var packPath = Path.Combine(referencesFolder, Constants.ReferencePackFileName);
            var fragment = new byte[record.Length];
            using (var stream = File.OpenRead(packPath))
            {
                stream.Seek(record.Offset, SeekOrigin.Begin);
                stream.ReadExactly(fragment, 0, record.Length);
            }

            return fragment;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        /// <summary>
        /// Rewrites this fixture's ProjectExplorer.html with the exact "&lt;/div&gt;&lt;div&gt;" adjacency
        /// real Pass1 output produces (no newline between the rootFolder title and its content div) --
        /// SolutionFinalizer.GetProjectExplorerText's data-repo patch matches that literal substring, and
        /// CreateAssemblyFixture's own newline-separated synthetic HTML never triggers it. Only used by
        /// the repo-tag-survives-merge regression test; other tests here rely on CreateAssemblyFixture's
        /// line-broken format for their own (unrelated) insertion-point scans.
        /// </summary>
        private static void WriteProjectExplorerWithAdjacentRootFolderDiv(string objRoot, string assemblyName)
        {
            File.WriteAllText(
                Path.Combine(objRoot, assemblyName, Constants.ProjectExplorer + ".html"),
                $"<div id=\"rootFolder\" class=\"projectCS\">{assemblyName}</div><div>" +
                "<div class=\"folderTitle\">References</div><div class=\"folder\">" +
                "</div>" +
                "</div>" +
                "<script></script>");
        }

        private static void CreateAssemblyFixture(string objRoot, string assemblyName, string[] referencedAssemblies, string repoName = null)
        {
            var assemblyFolder = Path.Combine(objRoot, assemblyName);
            Directory.CreateDirectory(Path.Combine(assemblyFolder, Constants.ReferencesFileName));

            var projectInfoLines = new List<string>
            {
                "ProjectSourcePath=C:\\src\\" + assemblyName,
                "DocumentCount=1",
                "LinesOfCode=10",
                "BytesOfCode=100",
                "DeclaredSymbols=0",
                "DeclaredTypes=0",
                "PublicTypes=0",
            };
            if (repoName != null)
            {
                projectInfoLines.Add("RepoName=" + repoName);
            }

            File.WriteAllLines(Path.Combine(assemblyFolder, Constants.ProjectInfoFileName + ".txt"), projectInfoLines);

            if (referencedAssemblies != null && referencedAssemblies.Length > 0)
            {
                File.WriteAllLines(Path.Combine(assemblyFolder, Constants.ReferencedAssemblyList + ".txt"), referencedAssemblies);
            }

            // Mirrors the exact line breaks ProjectGenerator.ProjectExplorer.cs produces --
            // PatchUsedByBlock's insertion-point scan matches whole lines, so the "References" folder
            // title and its closing "</div>" must each be on their own line.
            File.WriteAllText(
                Path.Combine(assemblyFolder, Constants.ProjectExplorer + ".html"),
                string.Join(
                    Environment.NewLine,
                    $"<div id=\"rootFolder\" class=\"projectCS\">{assemblyName}</div>",
                    "<div>",
                    "<div class=\"folderTitle\">References</div><div class=\"folder\">",
                    "</div>",
                    "</div>",
                    "<script></script>"));
        }
    }
}
