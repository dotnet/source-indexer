using System;
using System.Collections.Generic;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    [TestClass]
    public class ConfigLocationMergerTests
    {
        private static Dictionary<string, List<Tuple<string, long>>> DeclarationMap(
            params (string symbolId, string filePath, long offset)[] entries)
        {
            var map = new Dictionary<string, List<Tuple<string, long>>>();
            foreach (var (symbolId, filePath, offset) in entries)
            {
                if (!map.TryGetValue(symbolId, out var list))
                {
                    list = new List<Tuple<string, long>>();
                    map.Add(symbolId, list);
                }

                list.Add(Tuple.Create(filePath, offset));
            }

            return map;
        }

        [TestMethod]
        public void No_configs_produces_an_empty_merged_map()
        {
            var merged = ConfigLocationMerger.Merge(new Dictionary<string, Dictionary<string, List<Tuple<string, long>>>>());
            merged.ShouldBeEmpty();
        }

        [TestMethod]
        public void Single_config_is_byte_identical_to_that_configs_own_declaration_map()
        {
            // This is the back-compat requirement: a run with zero or one config must produce output
            // indistinguishable from today's single-project path, so the merge step must be a
            // pass-through in this case -- one location per (symbolId, file, offset), tagged only with
            // that single config, in the same relative order the config's own map already has them.
            var perConfig = new Dictionary<string, Dictionary<string, List<Tuple<string, long>>>>
            {
                ["windows"] = DeclarationMap(
                    ("abc123", "Foo.cs", 10),
                    ("abc123", "Foo.Partial.cs", 55),
                    ("def456", "Bar.cs", 3))
            };

            var merged = ConfigLocationMerger.Merge(perConfig);

            merged.Count.ShouldBe(2);
            merged["abc123"].Count.ShouldBe(2);
            merged["abc123"][0].FilePath.ShouldBe("Foo.cs");
            merged["abc123"][0].Offset.ShouldBe(10);
            merged["abc123"][0].Configs.ShouldBe(new[] { "windows" });
            merged["abc123"][1].FilePath.ShouldBe("Foo.Partial.cs");
            merged["def456"].Count.ShouldBe(1);
            merged["def456"][0].Configs.ShouldBe(new[] { "windows" });
        }

        [TestMethod]
        public void Identical_declaration_across_configs_collapses_to_one_location_tagged_with_both()
        {
            // System.Environment.NewLine-style case: a symbol declared in the exact same file at the
            // exact same offset under every config is the common case and must merge to ONE location,
            // not one per config.
            var perConfig = new Dictionary<string, Dictionary<string, List<Tuple<string, long>>>>
            {
                ["windows"] = DeclarationMap(("abc123", "Shared.cs", 42)),
                ["linux"] = DeclarationMap(("abc123", "Shared.cs", 42)),
                ["mac"] = DeclarationMap(("abc123", "Shared.cs", 42)),
            };

            var merged = ConfigLocationMerger.Merge(perConfig);

            merged.Count.ShouldBe(1);
            merged["abc123"].Count.ShouldBe(1);
            merged["abc123"][0].FilePath.ShouldBe("Shared.cs");
            merged["abc123"][0].Configs.Count.ShouldBe(3);
            merged["abc123"][0].Configs.ShouldContain("windows");
            merged["abc123"][0].Configs.ShouldContain("linux");
            merged["abc123"][0].Configs.ShouldContain("mac");

            ConfigLocationMerger.IsFullyShared(merged["abc123"], perConfig.Keys).ShouldBeTrue();
        }

        [TestMethod]
        public void Symbol_declared_in_different_files_per_config_keeps_one_location_per_file_each_tagged_to_its_own_configs()
        {
            // Environment.Windows.cs vs Environment.Unix.cs: same symbol ID (config-independent, per
            // SymbolIdService), declared in a DIFFERENT file per config -- exactly the shape of an
            // ordinary partial type/member declared in multiple files, so it must produce one location
            // per file (feeding the existing partial-type disambiguation page), each tagged with only
            // the configs that actually declare it there.
            var perConfig = new Dictionary<string, Dictionary<string, List<Tuple<string, long>>>>
            {
                ["windows"] = DeclarationMap(("envnewline", "Environment.Windows.cs", 40)),
                ["linux"] = DeclarationMap(("envnewline", "Environment.Unix.cs", 55)),
                ["mac"] = DeclarationMap(("envnewline", "Environment.Unix.cs", 55)),
            };

            var merged = ConfigLocationMerger.Merge(perConfig);

            merged.Count.ShouldBe(1);
            var locations = merged["envnewline"];
            locations.Count.ShouldBe(2);

            var windowsLocation = locations.Find(l => l.FilePath == "Environment.Windows.cs");
            windowsLocation.ShouldNotBeNull();
            windowsLocation.Offset.ShouldBe(40);
            windowsLocation.Configs.ShouldBe(new[] { "windows" });

            var unixLocation = locations.Find(l => l.FilePath == "Environment.Unix.cs");
            unixLocation.ShouldNotBeNull();
            unixLocation.Offset.ShouldBe(55);
            unixLocation.Configs.Count.ShouldBe(2);
            unixLocation.Configs.ShouldContain("linux");
            unixLocation.Configs.ShouldContain("mac");

            ConfigLocationMerger.IsFullyShared(locations, perConfig.Keys).ShouldBeFalse();
        }

        [TestMethod]
        public void A_symbol_only_present_under_some_configs_is_tagged_with_only_those_configs()
        {
            // A declaration inside "#if WINDOWS" simply never appears in linux/mac's Pass1 output at
            // all (Roslyn doesn't compile the inactive branch), so it should surface with a config tag
            // covering only the configs that actually declared it -- not silently merged with an
            // unrelated symbol, and not incorrectly tagged as shared.
            var perConfig = new Dictionary<string, Dictionary<string, List<Tuple<string, long>>>>
            {
                ["windows"] = DeclarationMap(("winonly", "WindowsShim.cs", 12)),
                ["linux"] = new Dictionary<string, List<Tuple<string, long>>>(),
            };

            var merged = ConfigLocationMerger.Merge(perConfig);

            merged.Count.ShouldBe(1);
            merged["winonly"].Count.ShouldBe(1);
            merged["winonly"][0].Configs.ShouldBe(new[] { "windows" });
            ConfigLocationMerger.IsFullyShared(merged["winonly"], perConfig.Keys).ShouldBeFalse();
        }
    }
}
