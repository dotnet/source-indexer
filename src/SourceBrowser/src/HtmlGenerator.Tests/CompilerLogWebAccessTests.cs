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
    public void Standalone_stage_one_compiler_log_uses_sibling_src_server_path()
    {
        var serverPathMappings = new Dictionary<string, string>
        {
            [@"C:\index\extensions\src\"] = "https://github.com/dotnet/extensions/tree/abc/",
        };

        var result = SolutionGenerator.AddCompilerLogServerPathMapping(
            serverPathMappings,
            @"C:\index\extensions\build.complog",
            new Dictionary<string, string>
            {
                [@"D:\a\_work\1\s\"] = @"/_/",
            });

        result.Count.ShouldBe(2);
        result[@"D:\a\_work\1\s\"].ShouldBe("https://github.com/dotnet/extensions/tree/abc/");
    }

    [TestMethod]
    public void Case_variant_server_paths_keep_last_value()
    {
        var serverPathMappings = new Dictionary<string, string>
        {
            [@"C:\INDEX\EXTENSIONS\"] = "https://example.test/first/",
            [@"c:\index\extensions\"] = "https://example.test/last/",
        };

        var result = SolutionGenerator.AddCompilerLogServerPathMapping(
            serverPathMappings,
            @"C:\index\extensions\build.complog",
            new Dictionary<string, string>
            {
                [@"D:\a\_work\1\s\"] = @"/_/",
            });

        result.Count.ShouldBe(2);
        result[@"C:\index\extensions\"].ShouldBe("https://example.test/last/");
        result[@"D:\a\_work\1\s\"].ShouldBe("https://example.test/last/");
    }

    [TestMethod]
    public void Projects_with_distinct_repository_roots_each_add_an_alias()
    {
        IReadOnlyDictionary<string, string> result = new Dictionary<string, string>
        {
            [@"C:\index\extensions\src\"] = "https://github.com/dotnet/extensions/tree/abc/",
        };

        result = SolutionGenerator.AddCompilerLogServerPathMapping(
            result,
            @"C:\index\extensions\build.complog",
            new Dictionary<string, string>
            {
                [@"D:\work\repo1\"] = @"/_/",
            });
        result = SolutionGenerator.AddCompilerLogServerPathMapping(
            result,
            @"C:\index\extensions\build.complog",
            new Dictionary<string, string>
            {
                [@"E:\work\repo2\"] = @"/_/",
            });

        result[@"D:\work\repo1\"].ShouldBe("https://github.com/dotnet/extensions/tree/abc/");
        result[@"E:\work\repo2\"].ShouldBe("https://github.com/dotnet/extensions/tree/abc/");
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

    [TestMethod]
    public void Vmr_subrepo_paths_are_aliased_under_the_original_compiler_root()
    {
        var repoPathMappings = new Dictionary<string, string>
        {
            [@"C:\index\dotnet\"] = "dotnet/dotnet",
            [@"C:\index\dotnet\src\runtime"] = "dotnet/runtime",
            [@"C:\index\dotnet\src\sdk"] = "dotnet/sdk",
        };

        var result = SolutionGenerator.AddCompilerLogRepoPathMappings(
            repoPathMappings,
            @"C:\index\dotnet\logs\runtime.complog",
            new Dictionary<string, string>
            {
                [@"D:\a\_work\1\s"] = @"/_/",
            });

        Program.ResolveRepoChain(
                @"D:\a\_work\1\s\src\runtime\src\libraries\System.Private.CoreLib\System.Private.CoreLib.csproj",
                result,
                "dotnet/dotnet")
            .ShouldBe(new[] { "dotnet/dotnet", "dotnet/runtime" });
        Program.ResolveRepoName(
                @"D:\a\_work\1\s\src\sdk\src\Cli\dotnet.csproj",
                result,
                "dotnet/dotnet")
            .ShouldBe("dotnet/sdk");
    }
}
