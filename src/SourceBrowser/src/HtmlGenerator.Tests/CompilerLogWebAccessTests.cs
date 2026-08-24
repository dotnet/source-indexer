using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HtmlGenerator.Tests;

[TestClass]
public sealed class CompilerLogWebAccessTests
{
    [TestMethod]
    public void Source_link_and_path_map_create_web_access_url()
    {
        var mappings = SolutionGenerator.CreateCompilerLogWebAccessMappings(
            new Dictionary<string, string>
            {
                [@"D:\a\_work\1\s\.packages\"] = @"/_1/",
                [@"D:\a\_work\1\s"] = @"/_/",
            },
            new Dictionary<string, string>
            {
                ["/_/*"] = "https://raw.githubusercontent.com/dotnet/extensions/abc/*",
            });

        var url = CompilerLogWebAccessMapping.GetWebAccessUrl(
            mappings,
            @"D:\a\_work\1\s\src\Library\File.cs");

        url.ShouldBe("https://github.com/dotnet/extensions/tree/abc/src/Library/File.cs");
        CompilerLogWebAccessMapping.GetWebAccessUrl(
            mappings,
            @"D:\a\_work\1\s\.packages\Package.cs").ShouldBeNull();
    }

    [TestMethod]
    public void Exact_source_link_mapping_creates_exact_web_access_url()
    {
        var mappings = SolutionGenerator.CreateCompilerLogWebAccessMappings(
            new Dictionary<string, string> { [@"D:\repo"] = @"/_/" },
            new Dictionary<string, string>
            {
                ["/_/src/File.cs"] = "https://example.test/source/File.cs?raw=true",
            });

        CompilerLogWebAccessMapping.GetWebAccessUrl(mappings, @"D:\repo\src\File.cs")
            .ShouldBe("https://example.test/source/File.cs?raw=true");
        CompilerLogWebAccessMapping.GetWebAccessUrl(mappings, @"D:\repo\src\Other.cs")
            .ShouldBeNull();
    }

    [TestMethod]
    public void Source_link_url_template_preserves_content_after_wildcard()
    {
        var mappings = SolutionGenerator.CreateCompilerLogWebAccessMappings(
            new Dictionary<string, string> { [@"D:\repo"] = @"/_/" },
            new Dictionary<string, string>
            {
                ["/_/*"] = "https://example.test/source/*?raw=true",
            });

        CompilerLogWebAccessMapping.GetWebAccessUrl(mappings, @"D:\repo\src\File name.cs")
            .ShouldBe("https://example.test/source/src/File%20name.cs?raw=true");
    }

    [TestMethod]
    public void Unsafe_source_link_url_is_ignored()
    {
        var mappings = SolutionGenerator.CreateCompilerLogWebAccessMappings(
            new Dictionary<string, string> { [@"D:\repo"] = @"/_/" },
            new Dictionary<string, string>
            {
                ["/_/*"] = "javascript:alert(*)",
            });

        mappings.ShouldBeEmpty();
    }

    [TestMethod]
    public void Source_link_url_is_canonically_escaped()
    {
        var mappings = SolutionGenerator.CreateCompilerLogWebAccessMappings(
            new Dictionary<string, string> { [@"D:\repo"] = @"/_/" },
            new Dictionary<string, string>
            {
                ["/_/*"] = "https://example.test/source/*?value=\"quoted\"",
            });

        var url = CompilerLogWebAccessMapping.GetWebAccessUrl(mappings, @"D:\repo\File.cs");

        url.ShouldBe("https://example.test/source/File.cs?value=%22quoted%22");
        url.ShouldNotContain("\"");
    }

    [TestMethod]
    public void Longest_source_link_mapping_wins()
    {
        var mappings = SolutionGenerator.CreateCompilerLogWebAccessMappings(
            new Dictionary<string, string> { [@"D:\repo"] = @"/_/" },
            new Dictionary<string, string>
            {
                ["/_/*"] = "https://example.test/general/*",
                ["/_/src/nested/*"] = "https://example.test/nested/*",
            });

        CompilerLogWebAccessMapping.GetWebAccessUrl(mappings, @"D:\repo\src\nested\File.cs")
            .ShouldBe("https://example.test/nested/File.cs");
    }

    [TestMethod]
    public void Longest_server_path_mapping_wins()
    {
        var url = ProjectGenerator.GetWebAccessUrl(
            @"D:\repo\nested\File.cs",
            [],
            new Dictionary<string, string>
            {
                [@"D:\repo\"] = "https://example.test/general/",
                [@"D:\repo\nested\"] = "https://example.test/nested/",
            });

        url.ShouldBe("https://example.test/nested/File.cs");
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("""{"documents":[]}""")]
    public void Invalid_source_link_data_is_ignored(string sourceLinkJson)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sourceLinkJson));

        var success = SolutionGenerator.TryReadSourceLinkMappings(stream, out var mappings);

        success.ShouldBeFalse();
        mappings.ShouldBeNull();
    }

    [TestMethod]
    public void Compilations_with_the_same_path_map_can_use_different_source_link_mappings()
    {
        var pathMappings = new Dictionary<string, string>
        {
            [@"D:\a\_work\1\s"] = @"/_/",
        };
        var first = SolutionGenerator.CreateCompilerLogWebAccessMappings(
            pathMappings,
            new Dictionary<string, string>
            {
                ["/_/src/first/*"] = "https://example.test/first/*",
            });
        var second = SolutionGenerator.CreateCompilerLogWebAccessMappings(
            pathMappings,
            new Dictionary<string, string>
            {
                ["/_/src/second/*"] = "https://example.test/second/*",
            });

        CompilerLogWebAccessMapping.GetWebAccessUrl(first, @"D:\a\_work\1\s\src\first\File.cs")
            .ShouldBe("https://example.test/first/File.cs");
        CompilerLogWebAccessMapping.GetWebAccessUrl(second, @"D:\a\_work\1\s\src\second\File.cs")
            .ShouldBe("https://example.test/second/File.cs");
    }

    [TestMethod]
    public void Compiler_log_project_file_paths_are_normalized_for_lookup()
    {
        SolutionGenerator.NormalizeCompilerLogProjectFilePath(
            @"D:\repo\src\Project\..\Project\Project.csproj")
            .ShouldBe(@"D:\repo\src\Project\Project.csproj");
    }
}
