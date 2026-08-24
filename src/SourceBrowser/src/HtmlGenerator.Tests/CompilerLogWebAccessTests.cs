using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using System.Collections.Generic;

namespace HtmlGenerator.Tests;

[TestClass]
public sealed class CompilerLogWebAccessTests
{
    [TestMethod]
    public void Compiler_log_repository_root_aliases_configured_server_path()
    {
        var serverPathMappings = new Dictionary<string, string>
        {
            [@"C:\index\extensions\"] = "https://github.com/dotnet/extensions/tree/abc/",
        };
        var compilerPathMappings = new Dictionary<string, string>
        {
            [@"D:\a\_work\1\s\.packages\"] = @"/_1/",
            [@"D:\a\_work\1\s\"] = @"/_/",
        };

        var result = SolutionGenerator.AddCompilerLogServerPathMapping(
            serverPathMappings,
            @"C:\index\extensions\build.complog",
            compilerPathMappings);

        result.Count.ShouldBe(2);
        result[@"D:\a\_work\1\s\"].ShouldBe("https://github.com/dotnet/extensions/tree/abc/");
        result.ShouldNotContainKey(@"D:\a\_work\1\s\.packages\");
    }

    [TestMethod]
    public void Compiler_log_without_repository_path_map_adds_no_alias()
    {
        var serverPathMappings = new Dictionary<string, string>
        {
            [@"C:\index\extensions\"] = "https://github.com/dotnet/extensions/tree/abc/",
        };

        var result = SolutionGenerator.AddCompilerLogServerPathMapping(
            serverPathMappings,
            @"C:\index\extensions\build.complog",
            new Dictionary<string, string>
            {
                [@"D:\a\_work\1\s\.packages\"] = @"/_1/",
            });

        result.ShouldBeSameAs(serverPathMappings);
    }

    [TestMethod]
    public void Compiler_log_outside_configured_repository_adds_no_alias()
    {
        var serverPathMappings = new Dictionary<string, string>
        {
            [@"C:\index\extensions\"] = "https://github.com/dotnet/extensions/tree/abc/",
        };

        var result = SolutionGenerator.AddCompilerLogServerPathMapping(
            serverPathMappings,
            @"C:\other\build.complog",
            new Dictionary<string, string>
            {
                [@"D:\a\_work\1\s\"] = @"/_/",
            });

        result.ShouldBeSameAs(serverPathMappings);
    }
}
