using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using System.Collections.Generic;
using System.Linq;

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
                [@"D:\a\_work\1\s\src\runtime"] = @"/_/",
            });

        result[@"D:\a\_work\1\s\"].ShouldBe("dotnet/dotnet");
        result[@"D:\a\_work\1\s\src\runtime\"].ShouldBe("dotnet/runtime");
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

    [TestMethod]
    public void Vmr_server_path_is_aliased_from_the_original_outer_root()
    {
        var repoPathMappings = new Dictionary<string, string>
        {
            [@"C:\index\dotnet\"] = "dotnet/dotnet",
            [@"C:\index\dotnet\src\runtime"] = "dotnet/runtime",
        };
        var serverPathMappings = new Dictionary<string, string>
        {
            [@"C:\index\dotnet\"] = "https://github.com/dotnet/dotnet/tree/abc/",
        };

        var result = SolutionGenerator.AddCompilerLogServerPathMapping(
            serverPathMappings,
            @"C:\index\dotnet\runtime.complog",
            new Dictionary<string, string>
            {
                [@"D:\a\_work\1\s\src\runtime"] = @"/_/",
            },
            repoPathMappings);

        result[@"D:\a\_work\1\s\"].ShouldBe("https://github.com/dotnet/dotnet/tree/abc/");
        result.ShouldNotContainKey(@"D:\a\_work\1\s\src\runtime\");
    }

    [TestMethod]
    public void Compiler_log_original_project_paths_are_mapped_by_project_identity()
    {
        var runtimeId = ProjectId.CreateNewId();
        var sdkId = ProjectId.CreateNewId();
        var solutionInfo = Microsoft.CodeAnalysis.SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Default,
            projects: new[]
            {
                ProjectInfo.Create(
                    runtimeId,
                    VersionStamp.Default,
                    "Runtime",
                    "Runtime",
                    LanguageNames.CSharp,
                    filePath: "Runtime.csproj"),
                ProjectInfo.Create(
                    sdkId,
                    VersionStamp.Default,
                    "Sdk",
                    "Sdk",
                    LanguageNames.CSharp,
                    filePath: "Sdk.csproj"),
            });

        var result = SolutionGenerator.CreateCompilerLogProjectPathMap(
            solutionInfo,
            new[]
            {
                @"D:\a\_work\1\s\src\runtime\Runtime.csproj",
                @"D:\a\_work\1\s\src\sdk\Sdk.csproj",
            });

        result[runtimeId].ShouldBe(@"D:\a\_work\1\s\src\runtime\Runtime.csproj");
        result[sdkId].ShouldBe(@"D:\a\_work\1\s\src\sdk\Sdk.csproj");

        var normalized = SolutionGenerator.NormalizeCompilerLogAssemblyNames(solutionInfo);
        normalized.Projects.ShouldContain(project => project.Id == runtimeId);
        normalized.Projects.ShouldContain(project => project.Id == sdkId);

        var resolutionPath = SolutionGenerator.GetRepoResolutionPath(
            runtimeId,
            "Runtime.csproj",
            result);
        Program.ResolveRepoName(
                resolutionPath,
                new Dictionary<string, string>
                {
                    [@"D:\a\_work\1\s"] = "dotnet/dotnet",
                    [@"D:\a\_work\1\s\src\runtime"] = "dotnet/runtime",
                },
                "dotnet/dotnet")
            .ShouldBe("dotnet/runtime");

        SolutionGenerator.CreateCompilerLogProjectPathMap(
                solutionInfo,
                new[]
                {
                    @"D:\a\_work\1\s\src\sdk\Sdk.csproj",
                    @"D:\a\_work\1\s\src\runtime\Runtime.csproj",
                })
            .ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Vmr_repeated_subrepo_path_keeps_projects_in_the_same_repo(bool executableFirst)
    {
        const string compilerLog = @"D:\index\dotnet\msbuild.complog";
        const string originalRoot = @"C:\code\__w\1\s\";
        IReadOnlyDictionary<string, string> mappings = new Dictionary<string, string>
        {
            [@"D:\index\dotnet"] = "dotnet/dotnet",
            [@"D:\index\dotnet\src\msbuild"] = "dotnet/msbuild",
        };
        IReadOnlyDictionary<string, string> serverMappings = new Dictionary<string, string>
        {
            [@"D:\index\dotnet"] = "https://github.com/dotnet/dotnet/tree/abc/",
        };
        var projectPaths = new[]
        {
            originalRoot + @"src\msbuild\src\Build\Microsoft.Build.csproj",
            originalRoot + @"src\msbuild\src\MSBuild\MSBuild.csproj",
        };

        foreach (var projectPath in executableFirst ? projectPaths.Reverse() : projectPaths)
        {
            SolutionGenerator.TryGetCompilerLogOriginalRoot(mappings, compilerLog, projectPath, out var root)
                .ShouldBeTrue();
            root.ShouldBe(originalRoot);
            mappings = SolutionGenerator.AddCompilerLogRepoPathMappings(mappings, compilerLog, root);
            serverMappings = SolutionGenerator.AddCompilerLogServerPathMapping(
                serverMappings, compilerLog, new Dictionary<string, string> { [root] = "/_/" }, mappings);
        }

        mappings[originalRoot + @"src\msbuild\"].ShouldBe("dotnet/msbuild");
        serverMappings[originalRoot].ShouldBe("https://github.com/dotnet/dotnet/tree/abc/");
        serverMappings.ShouldNotContainKey(originalRoot + @"src\msbuild\");

        var explorer = new Folder<ProjectSkeleton>();
        var solutionCounts = new Dictionary<string, int> { ["dotnet/dotnet"] = 2 };
        foreach (var projectPath in projectPaths)
        {
            var chain = Program.ResolveRepoChain(projectPath, mappings, "dotnet/dotnet");
            chain.ShouldBe(new[] { "dotnet/dotnet", "dotnet/msbuild" });
            Program.GetSolutionExplorerGroupingFolder(explorer, chain, "msbuild", 2, solutionCounts);
        }

        explorer.Folders["dotnet/dotnet"].Folders.Keys.ShouldBe(new[] { "dotnet/msbuild" });
    }

    [TestMethod]
    public void Vmr_original_root_is_derived_from_project_path_and_configured_subrepo()
    {
        var mappings = new Dictionary<string, string>
        {
            [@"D:\index\dotnet"] = "dotnet/dotnet",
            [@"D:\index\dotnet\src\runtime"] = "dotnet/runtime",
            [@"D:\index\dotnet\src\sdk"] = "dotnet/sdk",
        };

        SolutionGenerator.TryGetCompilerLogOriginalRoot(
                mappings,
                @"D:\index\dotnet\runtime.complog",
                @"C:\code\__w\1\s\src\runtime\src\coreclr\nativeaot\System.Private.CoreLib\src\System.Private.CoreLib.csproj",
                out var originalRoot)
            .ShouldBeTrue();

        originalRoot.ShouldBe(@"C:\code\__w\1\s\");

        var aliases = SolutionGenerator.AddCompilerLogRepoPathMappings(
            mappings,
            @"D:\index\dotnet\runtime.complog",
            originalRoot);
        Program.ResolveRepoChain(
                @"C:\code\__w\1\s\src\runtime\src\libraries\System.Private.CoreLib\System.Private.CoreLib.csproj",
                aliases,
                "dotnet/dotnet")
            .ShouldBe(new[] { "dotnet/dotnet", "dotnet/runtime" });
    }
}
