using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using System.Collections.Generic;

namespace HtmlGenerator.Tests;

[TestClass]
public sealed class CompilerLogWebAccessTests
{
    [TestMethod]
    public void Source_link_and_path_map_add_original_checkout_as_server_path_alias()
    {
        var serverPathMappings = new Dictionary<string, string>
        {
            [@"C:\index\extensions"] = "https://github.com/dotnet/extensions/tree/fallback/",
        };
        var compilerPathMappings = new Dictionary<string, string>
        {
            [@"D:\a\_work\1\s\.packages\"] = @"/_1/",
            [@"D:\a\_work\1\s"] = @"/_/",
        };
        var sourceLinkMappings = new Dictionary<string, string>
        {
            ["/_/*"] = "https://raw.githubusercontent.com/dotnet/extensions/abc/*",
        };

        var result = SolutionGenerator.AddCompilerLogSourceLinkMappings(
            serverPathMappings,
            compilerPathMappings,
            sourceLinkMappings);

        result.Count.ShouldBe(2);
        result[@"D:\a\_work\1\s\"].ShouldBe("https://github.com/dotnet/extensions/tree/abc/");
        result.ShouldNotContainKey(@"D:\a\_work\1\s\.packages\");
    }

    [TestMethod]
    public void Source_link_mapping_does_not_require_repo_mapping()
    {
        var result = SolutionGenerator.AddCompilerLogSourceLinkMappings(
            new Dictionary<string, string>(),
            new Dictionary<string, string> { [@"D:\a\_work\1\s\"] = @"/_/" },
            new Dictionary<string, string>
            {
                ["/_/*"] = "https://example.test/source/abc/*",
            });

        result.ShouldHaveSingleItem();
        result[@"D:\a\_work\1\s\"].ShouldBe("https://example.test/source/abc/");
    }
}
