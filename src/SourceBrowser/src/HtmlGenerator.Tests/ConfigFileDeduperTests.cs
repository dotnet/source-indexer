using System.Collections.Generic;
using System.Linq;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    [TestClass]
    public class ConfigFileDeduperTests
    {
        [TestMethod]
        public void SingleConfig_IsPassThroughAndKeepsOriginalPath()
        {
            var perConfig = new Dictionary<string, string>
            {
                ["windows"] = "<html>same</html>",
            };

            var variants = ConfigFileDeduper.Dedupe(perConfig);

            variants.Count.ShouldBe(1);
            variants[0].Configs.ShouldBe(new[] { "windows" });

            ConfigFileDeduper.AssignPhysicalPaths("Foo.cs.html", variants);
            variants[0].PhysicalPath.ShouldBe("Foo.cs.html");
        }

        [TestMethod]
        public void IdenticalRenderingAcrossAllConfigs_CollapsesToOneVariant()
        {
            // Simulates a shared, non-#if-gated file: every config renders the exact same HTML.
            var perConfig = new Dictionary<string, string>
            {
                ["linux"] = "<html>shared content</html>",
                ["mac"] = "<html>shared content</html>",
                ["windows"] = "<html>shared content</html>",
            };

            var variants = ConfigFileDeduper.Dedupe(perConfig);

            variants.Count.ShouldBe(1);
            variants[0].Configs.OrderBy(c => c).ShouldBe(new[] { "linux", "mac", "windows" });
            ConfigFileDeduper.IsFullyShared(variants, perConfig.Keys.ToList()).ShouldBeTrue();

            ConfigFileDeduper.AssignPhysicalPaths("Shared.cs.html", variants);
            variants[0].PhysicalPath.ShouldBe("Shared.cs.html");
        }

        [TestMethod]
        public void DivergentRendering_KeepsOneVariantPerDistinctHash()
        {
            // Simulates a shared file gated by "#if WINDOWS" whose active/inactive region rendering
            // differs on windows vs. everywhere else, but linux and mac (which take the same branch)
            // converge to the identical rendered HTML.
            var perConfig = new Dictionary<string, string>
            {
                ["linux"] = "<html>unix branch active</html>",
                ["mac"] = "<html>unix branch active</html>",
                ["windows"] = "<html>windows branch active</html>",
            };

            var variants = ConfigFileDeduper.Dedupe(perConfig);

            variants.Count.ShouldBe(2);
            ConfigFileDeduper.IsFullyShared(variants, perConfig.Keys.ToList()).ShouldBeFalse();

            var unixVariant = variants.Single(v => v.Content == "<html>unix branch active</html>");
            var windowsVariant = variants.Single(v => v.Content == "<html>windows branch active</html>");

            unixVariant.Configs.OrderBy(c => c).ShouldBe(new[] { "linux", "mac" });
            windowsVariant.Configs.ShouldBe(new[] { "windows" });

            ConfigFileDeduper.AssignPhysicalPaths("EnvHelper.cs.html", variants);

            // Exactly one variant keeps the original path (so existing links / the no-config default
            // case are unaffected); the other gets a distinct, deterministic suffixed name.
            variants.Count(v => v.PhysicalPath == "EnvHelper.cs.html").ShouldBe(1);
            var suffixed = variants.Single(v => v.PhysicalPath != "EnvHelper.cs.html");
            suffixed.PhysicalPath.ShouldStartWith("EnvHelper.cs~");
            suffixed.PhysicalPath.ShouldEndWith(".html");
        }

        [TestMethod]
        public void ConfigUniqueFile_ProducesSingleVariantTaggedToThatConfigOnly()
        {
            // Environment.Windows.cs isn't part of linux's/mac's compilation at all, so those configs
            // simply have no entry for this logical path -- this falls naturally out of Dedupe without
            // any special-cased "unique file" branch.
            var perConfig = new Dictionary<string, string>
            {
                ["windows"] = "<html>windows-only implementation</html>",
            };

            var variants = ConfigFileDeduper.Dedupe(perConfig);

            variants.Count.ShouldBe(1);
            variants[0].Configs.ShouldBe(new[] { "windows" });
            ConfigFileDeduper.IsFullyShared(variants, new[] { "linux", "mac", "windows" }).ShouldBeFalse();
        }

        [TestMethod]
        public void TwoConfigSyntheticFixture_MixOfDivergentAndNonDivergentFiles_CollapsesAndDivergesCorrectly()
        {
            // The explicitly-requested 2-config synthetic fixture: a small set of "files" mixing
            // #if-divergent and non-divergent content, asserting non-divergent files collapse to ONE
            // physical page and divergent ones keep exactly one variant per distinct render.
            var files = new Dictionary<string, Dictionary<string, string>>
            {
                ["Program.cs.html"] = new Dictionary<string, string>
                {
                    ["linux"] = "<html>Program.cs: identical everywhere</html>",
                    ["windows"] = "<html>Program.cs: identical everywhere</html>",
                },
                ["EnvHelper.cs.html"] = new Dictionary<string, string>
                {
                    ["linux"] = "<html>EnvHelper.cs: unix path taken</html>",
                    ["windows"] = "<html>EnvHelper.cs: windows path taken</html>",
                },
                ["Environment.Windows.cs.html"] = new Dictionary<string, string>
                {
                    ["windows"] = "<html>windows-only file</html>",
                },
                ["Environment.Unix.cs.html"] = new Dictionary<string, string>
                {
                    ["linux"] = "<html>unix-only file</html>",
                },
            };

            var allConfigs = new[] { "linux", "windows" };
            var results = new Dictionary<string, List<ConfigFileDeduper.FileVariant>>();

            foreach (var kvp in files)
            {
                var variants = ConfigFileDeduper.Dedupe(kvp.Value);
                ConfigFileDeduper.AssignPhysicalPaths(kvp.Key, variants);
                results[kvp.Key] = variants;
            }

            // Non-divergent shared file: one physical page, shared by both configs.
            results["Program.cs.html"].Count.ShouldBe(1);
            ConfigFileDeduper.IsFullyShared(results["Program.cs.html"], allConfigs).ShouldBeTrue();
            results["Program.cs.html"][0].PhysicalPath.ShouldBe("Program.cs.html");

            // Divergent shared file: exactly one variant per distinct render (2 configs, 2 renders -> 2
            // variants here), one of which keeps the original path.
            results["EnvHelper.cs.html"].Count.ShouldBe(2);
            results["EnvHelper.cs.html"].Count(v => v.PhysicalPath == "EnvHelper.cs.html").ShouldBe(1);
            results["EnvHelper.cs.html"].Sum(v => v.Configs.Count).ShouldBe(2);

            // Config-unique files: exactly one variant each, tagged only to their owning config.
            results["Environment.Windows.cs.html"].Count.ShouldBe(1);
            results["Environment.Windows.cs.html"][0].Configs.ShouldBe(new[] { "windows" });
            results["Environment.Unix.cs.html"].Count.ShouldBe(1);
            results["Environment.Unix.cs.html"][0].Configs.ShouldBe(new[] { "linux" });
        }
    }
}
