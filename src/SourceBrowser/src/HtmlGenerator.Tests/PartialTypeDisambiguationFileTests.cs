using System.Collections.Generic;
using System.IO;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    [TestClass]
    public class PartialTypeDisambiguationFileTests
    {
        private string testRoot;

        [TestInitialize]
        public void Setup()
        {
            testRoot = Path.Combine(Path.GetTempPath(), "sb-partialtype-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }

        private string DisambiguationFilePath(string symbolId) =>
            Path.Combine(testRoot, Constants.PartialResolvingFileName, symbolId) + ".html";

        [TestMethod]
        public void Untagged_overload_and_null_config_tags_produce_byte_identical_output()
        {
            // Back-compat requirement: when there is no config involved (the overwhelming majority of
            // real usage today -- ordinary partial types with no config concept at all), the new
            // config-aware overload must render exactly what the original untagged overload does.
            var filePaths = new[] { "Foo.cs", "Foo.Partial.cs" };

            Markup.GeneratePartialTypeDisambiguationFile(testRoot, testRoot, "abc123", filePaths);
            var untagged = File.ReadAllText(DisambiguationFilePath("abc123"));
            File.Delete(DisambiguationFilePath("abc123"));

            Markup.GeneratePartialTypeDisambiguationFile(testRoot, testRoot, "abc123", filePaths, configTagsByFilePath: null);
            var explicitNull = File.ReadAllText(DisambiguationFilePath("abc123"));

            explicitNull.ShouldBe(untagged);
        }

        [TestMethod]
        public void Config_tags_are_rendered_alongside_each_location_link()
        {
            var filePaths = new[] { "Environment.Windows.cs", "Environment.Unix.cs" };
            var configTags = new Dictionary<string, IEnumerable<string>>
            {
                ["Environment.Windows.cs"] = new[] { "windows" },
                ["Environment.Unix.cs"] = new[] { "linux", "mac" },
            };

            Markup.GeneratePartialTypeDisambiguationFile(testRoot, testRoot, "envnewline", filePaths, configTags);

            var content = File.ReadAllText(DisambiguationFilePath("envnewline"));

            content.ShouldContain("Environment.Windows.cs");
            content.ShouldContain("Environment.Unix.cs");
            content.ShouldContain("[windows]");
            content.ShouldContain("[linux, mac]");
        }

        [TestMethod]
        public void A_file_with_no_config_tag_entry_renders_without_a_tag()
        {
            var filePaths = new[] { "Foo.cs" };
            var configTags = new Dictionary<string, IEnumerable<string>>();

            Markup.GeneratePartialTypeDisambiguationFile(testRoot, testRoot, "abc123", filePaths, configTags);

            var content = File.ReadAllText(DisambiguationFilePath("abc123"));
            content.ShouldNotContain("partialTypeConfigTag");
        }

        [TestMethod]
        public void Omitting_allConfigs_renders_byte_identically_to_the_five_argument_overload()
        {
            // Back-compat: passing configTagsByFilePath without allConfigs (the pre-existing overload)
            // must render exactly what the six-argument overload renders when allConfigs is null.
            var filePaths = new[] { "Environment.Windows.cs", "Environment.Unix.cs" };
            var configTags = new Dictionary<string, IEnumerable<string>>
            {
                ["Environment.Windows.cs"] = new[] { "windows" },
                ["Environment.Unix.cs"] = new[] { "linux", "mac" },
            };

            Markup.GeneratePartialTypeDisambiguationFile(testRoot, testRoot, "envnewline", filePaths, configTags);
            var fiveArg = File.ReadAllText(DisambiguationFilePath("envnewline"));
            File.Delete(DisambiguationFilePath("envnewline"));

            Markup.GeneratePartialTypeDisambiguationFile(testRoot, testRoot, "envnewline", filePaths, configTags, allConfigs: null);
            var sixArgNullAllConfigs = File.ReadAllText(DisambiguationFilePath("envnewline"));

            sixArgNullAllConfigs.ShouldBe(fiveArg);
            sixArgNullAllConfigs.ShouldNotContain("data-configs");
        }

        [TestMethod]
        public void DataConfigs_attribute_is_emitted_for_a_link_that_does_not_cover_every_registered_config()
        {
            var filePaths = new[] { "Environment.Windows.cs", "Environment.Unix.cs" };
            var configTags = new Dictionary<string, IEnumerable<string>>
            {
                ["Environment.Windows.cs"] = new[] { "windows" },
                ["Environment.Unix.cs"] = new[] { "linux", "mac" },
            };
            var allConfigs = new[] { "windows", "linux", "mac" };

            Markup.GeneratePartialTypeDisambiguationFile(testRoot, testRoot, "envnewline", filePaths, configTags, allConfigs);

            var content = File.ReadAllText(DisambiguationFilePath("envnewline"));
            content.ShouldContain("data-configs=\"windows\"");
            content.ShouldContain("data-configs=\"linux,mac\"");
            // Visible tag untouched alongside the new attribute.
            content.ShouldContain("[windows]");
            content.ShouldContain("[linux, mac]");
        }

        [TestMethod]
        public void DataConfigs_attribute_is_omitted_for_a_link_that_covers_every_registered_config()
        {
            // A location present under every registered config is fully shared/inert -- tagging it
            // would be noise the client selector would have to filter as "always shown" anyway.
            var filePaths = new[] { "Foo.cs" };
            var configTags = new Dictionary<string, IEnumerable<string>>
            {
                ["Foo.cs"] = new[] { "windows", "linux" },
            };
            var allConfigs = new[] { "windows", "linux" };

            Markup.GeneratePartialTypeDisambiguationFile(testRoot, testRoot, "abc123", filePaths, configTags, allConfigs);

            var content = File.ReadAllText(DisambiguationFilePath("abc123"));
            content.ShouldNotContain("data-configs");
        }

        [TestMethod]
        public void Config_aware_render_includes_scripts_and_the_config_filter_onload()
        {
            // The config-selector's data-configs attributes are inert without the client filter script
            // actually running on this page -- so a config-aware render must include scripts.js and
            // call sbApplyConfigFilter on load, unlike the ordinary single/no-config disambiguation page.
            var filePaths = new[] { "Environment.Windows.cs", "Environment.Unix.cs" };
            var configTags = new Dictionary<string, IEnumerable<string>>
            {
                ["Environment.Windows.cs"] = new[] { "windows" },
                ["Environment.Unix.cs"] = new[] { "linux" },
            };
            var allConfigs = new[] { "windows", "linux" };

            Markup.GeneratePartialTypeDisambiguationFile(testRoot, testRoot, "envnewline", filePaths, configTags, allConfigs);

            var content = File.ReadAllText(DisambiguationFilePath("envnewline"));
            content.ShouldContain("scripts.js\"></script>");
            content.ShouldContain("onload=\"sbApplyConfigFilter(document);\"");
        }

        [TestMethod]
        public void Single_config_render_never_includes_the_config_filter_script()
        {
            // allConfigs null/empty is the ordinary single/no-config path -- must stay exactly as
            // before this feature existed, with no new script tag or onload.
            var filePaths = new[] { "Foo.cs", "Foo.Partial.cs" };

            Markup.GeneratePartialTypeDisambiguationFile(testRoot, testRoot, "abc123", filePaths);

            var content = File.ReadAllText(DisambiguationFilePath("abc123"));
            content.ShouldNotContain("scripts.js");
            content.ShouldNotContain("sbApplyConfigFilter");
            content.ShouldNotContain("onload");
        }
    }
}
