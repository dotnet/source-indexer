using System.Collections.Generic;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    [TestClass]
    public class ConfigReferenceMergerTests
    {
        private static Reference MakeReference(
            string fromAssemblyId = "AsmA",
            string fromLocalPath = "Caller.cs",
            int line = 10,
            int colStart = 4,
            int colEnd = 8,
            ReferenceKind kind = ReferenceKind.Reference,
            string url = "AsmB/Target.html")
        {
            return new Reference
            {
                FromAssemblyId = fromAssemblyId,
                FromLocalPath = fromLocalPath,
                ReferenceLineNumber = line,
                ReferenceColumnStart = colStart,
                ReferenceColumnEnd = colEnd,
                Kind = kind,
                Url = url,
                ReferenceLineText = "    Target();",
                ToSymbolName = "Target",
            };
        }

        [TestMethod]
        public void No_configs_produces_an_empty_merged_map()
        {
            var merged = ConfigReferenceMerger.Merge(new Dictionary<string, Dictionary<string, List<Reference>>>());
            merged.ShouldBeEmpty();
        }

        [TestMethod]
        public void Single_config_is_byte_identical_to_that_configs_own_references_and_tags_them_with_it()
        {
            var perConfig = new Dictionary<string, Dictionary<string, List<Reference>>>
            {
                ["windows"] = new Dictionary<string, List<Reference>>
                {
                    ["targetSymbol"] = new List<Reference> { MakeReference(line: 10), MakeReference(line: 20) }
                }
            };

            var merged = ConfigReferenceMerger.Merge(perConfig);

            merged.Count.ShouldBe(1);
            merged["targetSymbol"].Count.ShouldBe(2);
            merged["targetSymbol"][0].ReferenceLineNumber.ShouldBe(10);
            merged["targetSymbol"][0].ConfigSet.ShouldBe(new[] { "windows" });
            merged["targetSymbol"][1].ConfigSet.ShouldBe(new[] { "windows" });
        }

        [TestMethod]
        public void Identical_reference_across_configs_collapses_to_one_entry_tagged_with_both()
        {
            var perConfig = new Dictionary<string, Dictionary<string, List<Reference>>>
            {
                ["windows"] = new Dictionary<string, List<Reference>> { ["targetSymbol"] = new List<Reference> { MakeReference() } },
                ["linux"] = new Dictionary<string, List<Reference>> { ["targetSymbol"] = new List<Reference> { MakeReference() } },
            };

            var merged = ConfigReferenceMerger.Merge(perConfig);

            merged["targetSymbol"].Count.ShouldBe(1);
            merged["targetSymbol"][0].ConfigSet.Count.ShouldBe(2);
            merged["targetSymbol"][0].ConfigSet.ShouldContain("windows");
            merged["targetSymbol"][0].ConfigSet.ShouldContain("linux");
            ConfigReferenceMerger.IsFullyShared(merged["targetSymbol"], perConfig.Keys).ShouldBeTrue();
        }

        [TestMethod]
        public void Reference_only_present_under_one_config_keeps_its_own_narrow_tag()
        {
            // A call site inside "#if WINDOWS" simply never gets emitted for linux/mac's compilation --
            // Roslyn resolves the same target symbol ID everywhere, but the reference itself only
            // exists where the call site was actually compiled.
            var perConfig = new Dictionary<string, Dictionary<string, List<Reference>>>
            {
                ["windows"] = new Dictionary<string, List<Reference>>
                {
                    ["targetSymbol"] = new List<Reference> { MakeReference(fromLocalPath: "WindowsShim.cs", line: 12) }
                },
                ["linux"] = new Dictionary<string, List<Reference>>
                {
                    ["targetSymbol"] = new List<Reference> { MakeReference(fromLocalPath: "Shared.cs", line: 5) }
                },
            };

            var merged = ConfigReferenceMerger.Merge(perConfig);

            merged["targetSymbol"].Count.ShouldBe(2);
            var windowsOnly = merged["targetSymbol"].Find(r => r.FromLocalPath == "WindowsShim.cs");
            windowsOnly.ConfigSet.ShouldBe(new[] { "windows" });
            var linuxOnly = merged["targetSymbol"].Find(r => r.FromLocalPath == "Shared.cs");
            linuxOnly.ConfigSet.ShouldBe(new[] { "linux" });

            ConfigReferenceMerger.IsFullyShared(merged["targetSymbol"], perConfig.Keys).ShouldBeFalse();
        }

        [TestMethod]
        public void Distinct_call_sites_in_the_same_config_stay_distinct_entries()
        {
            var perConfig = new Dictionary<string, Dictionary<string, List<Reference>>>
            {
                ["windows"] = new Dictionary<string, List<Reference>>
                {
                    ["targetSymbol"] = new List<Reference> { MakeReference(line: 10), MakeReference(line: 99) }
                }
            };

            var merged = ConfigReferenceMerger.Merge(perConfig);
            merged["targetSymbol"].Count.ShouldBe(2);
        }
    }

    [TestClass]
    public class ReferenceOccurrenceEqualityTests
    {
        [TestMethod]
        public void Same_identity_fields_are_the_same_occurrence_even_if_display_text_differs()
        {
            var a = new Reference { FromAssemblyId = "A", FromLocalPath = "F.cs", ReferenceLineNumber = 1, ReferenceColumnStart = 2, ReferenceColumnEnd = 5, Kind = ReferenceKind.Reference, ReferenceLineText = "foo" };
            var b = new Reference { FromAssemblyId = "A", FromLocalPath = "F.cs", ReferenceLineNumber = 1, ReferenceColumnStart = 2, ReferenceColumnEnd = 5, Kind = ReferenceKind.Reference, ReferenceLineText = "bar (rendered differently under another config)" };

            a.HasSameOccurrenceAs(b).ShouldBeTrue();
        }

        [TestMethod]
        public void Different_kind_is_a_different_occurrence()
        {
            var a = new Reference { FromAssemblyId = "A", FromLocalPath = "F.cs", ReferenceLineNumber = 1, ReferenceColumnStart = 2, ReferenceColumnEnd = 5, Kind = ReferenceKind.Reference };
            var b = new Reference { FromAssemblyId = "A", FromLocalPath = "F.cs", ReferenceLineNumber = 1, ReferenceColumnStart = 2, ReferenceColumnEnd = 5, Kind = ReferenceKind.Write };

            a.HasSameOccurrenceAs(b).ShouldBeFalse();
        }
    }
}
