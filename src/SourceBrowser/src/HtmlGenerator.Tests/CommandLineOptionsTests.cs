using System.IO;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    [TestClass]
    public class CommandLineOptionsTests
    {
        [TestMethod]
        public void Server_path_allows_equals_sign_on_each_side_when_quoted()
        {
            var mapping = CommandLineOptions.Parse("/serverPath:\"a=1\"=\"b=2\"").ServerPathMappings.ShouldHaveSingleItem();
            mapping.Key.ShouldBe(Path.GetFullPath("a=1"));
            mapping.Value.ShouldBe("b=2");
        }

        [TestMethod]
        public void Server_path_allows_equals_sign_on_left_side_when_only_left_side_is_quoted()
        {
            var mapping = CommandLineOptions.Parse("/serverPath:\"a=1\"=b").ServerPathMappings.ShouldHaveSingleItem();
            mapping.Key.ShouldBe(Path.GetFullPath("a=1"));
            mapping.Value.ShouldBe("b");
        }

        [TestMethod]
        public void Server_path_allows_equals_sign_on_right_side_when_only_right_side_is_quoted()
        {
            var mapping = CommandLineOptions.Parse("/serverPath:a=\"b=2\"").ServerPathMappings.ShouldHaveSingleItem();
            mapping.Key.ShouldBe(Path.GetFullPath("a"));
            mapping.Value.ShouldBe("b=2");
        }

        [TestMethod]
        public void Server_path_allows_equals_neither_side_to_be_quoted()
        {
            var mapping = CommandLineOptions.Parse("/serverPath:a=b").ServerPathMappings.ShouldHaveSingleItem();
            mapping.Key.ShouldBe(Path.GetFullPath("a"));
            mapping.Value.ShouldBe("b");
        }

        [TestMethod]
        public void Server_path_allows_legacy_quoting()
        {
            var mapping = CommandLineOptions.Parse("/serverPath:\"a 1=b 2\"").ServerPathMappings.ShouldHaveSingleItem();
            mapping.Key.ShouldBe(Path.GetFullPath("a 1"));
            mapping.Value.ShouldBe("b 2");
        }

        [TestMethod]
        public void Server_path_requires_equals_sign()
        {
            CommandLineOptions.Parse("/serverPath:a").ServerPathMappings.ShouldBeEmpty();
        }

        [TestMethod]
        public void Server_path_disallows_further_unquoted_equals_signs_after_the_equals_sign()
        {
            CommandLineOptions.Parse("/serverPath:a=b=2").ServerPathMappings.ShouldBeEmpty();
        }

        [TestMethod]
        public void Server_path_disallows_characters_outside_quotes()
        {
            CommandLineOptions.Parse("/serverPath:c\"a\"=\"b\"").ServerPathMappings.ShouldBeEmpty();
            CommandLineOptions.Parse("/serverPath:\"a\"c=\"b\"").ServerPathMappings.ShouldBeEmpty();
            CommandLineOptions.Parse("/serverPath:\"a\"=c\"b\"").ServerPathMappings.ShouldBeEmpty();
            CommandLineOptions.Parse("/serverPath:\"a\"=\"b\"c").ServerPathMappings.ShouldBeEmpty();
        }

        [TestMethod]
        public void No_warnings_switch_is_recognized()
        {
            CommandLineOptions.Parse("/noWarnings").SuppressWarnings.ShouldBeTrue();
            CommandLineOptions.Parse("/nowarnings").SuppressWarnings.ShouldBeTrue();
        }

        [TestMethod]
        public void Warnings_are_enabled_by_default()
        {
            CommandLineOptions.Parse("/force").SuppressWarnings.ShouldBeFalse();
        }

        [TestMethod]
        public void Repo_path_tags_projects_under_the_given_folder()
        {
            var mapping = CommandLineOptions.Parse("/repoPath:\"a\"=\"clangsharp\"").RepoPathMappings.ShouldHaveSingleItem();
            mapping.Key.ShouldBe(Path.GetFullPath("a"));
            mapping.Value.ShouldBe("clangsharp");
        }

        [TestMethod]
        public void Repo_path_allows_unquoted_form()
        {
            var mapping = CommandLineOptions.Parse("/repoPath:a=clangsharp").RepoPathMappings.ShouldHaveSingleItem();
            mapping.Key.ShouldBe(Path.GetFullPath("a"));
            mapping.Value.ShouldBe("clangsharp");
        }

        [TestMethod]
        public void Repo_meta_option_sets_both_repo_path_and_server_path_mappings()
        {
            var options = CommandLineOptions.Parse("/repo:\"a\"=\"clangsharp\"=\"https://example.com/\"");

            var repoMapping = options.RepoPathMappings.ShouldHaveSingleItem();
            repoMapping.Key.ShouldBe(Path.GetFullPath("a"));
            repoMapping.Value.ShouldBe("clangsharp");

            var serverMapping = options.ServerPathMappings.ShouldHaveSingleItem();
            serverMapping.Key.ShouldBe(Path.GetFullPath("a"));
            serverMapping.Value.ShouldBe("https://example.com/");
        }

        [TestMethod]
        public void Repo_meta_option_requires_all_three_segments()
        {
            var options = CommandLineOptions.Parse("/repo:a=clangsharp");
            options.RepoPathMappings.ShouldBeEmpty();
            options.ServerPathMappings.ShouldBeEmpty();
        }

        [TestMethod]
        public void No_repo_options_leave_repo_path_mappings_empty()
        {
            CommandLineOptions.Parse("/force").RepoPathMappings.ShouldBeEmpty();
        }

        [TestMethod]
        public void Config_is_null_by_default()
        {
            CommandLineOptions.Parse("/force").Config.ShouldBeNull();
        }

        [TestMethod]
        public void Config_flag_is_parsed()
        {
            CommandLineOptions.Parse("/config:windows").Config.ShouldBe("windows");
        }

        [TestMethod]
        public void Config_flag_strips_quotes()
        {
            CommandLineOptions.Parse("/config:\"windows x64\"").Config.ShouldBe("windows x64");
        }

        [TestMethod]
        public void MergeConfigsOnly_defaults_to_false()
        {
            CommandLineOptions.Parse("/out:foo", "a.sln").MergeConfigsOnly.ShouldBeFalse();
        }

        [TestMethod]
        public void MergeConfigsOnly_flag_is_parsed()
        {
            CommandLineOptions.Parse("/mergeConfigsOnly", "/out:foo").MergeConfigsOnly.ShouldBeTrue();
        }

        [TestMethod]
        public void MergeConfigsOnly_flag_is_case_insensitive()
        {
            CommandLineOptions.Parse("/MERGECONFIGSONLY", "/out:foo").MergeConfigsOnly.ShouldBeTrue();
        }
    }
}
