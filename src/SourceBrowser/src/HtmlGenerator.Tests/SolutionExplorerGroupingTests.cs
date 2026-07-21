using System.Collections.Generic;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    [TestClass]
    public class SolutionExplorerGroupingTests
    {
        [TestMethod]
        public void Single_repo_does_not_introduce_any_grouping_folder()
        {
            var root = new Folder<ProjectSkeleton>();
            var solutionCounts = new Dictionary<string, int> { ["ClangSharp"] = 1 };

            var folder = Program.GetSolutionExplorerGroupingFolder(root, "ClangSharp", "ClangSharp", distinctRepoCount: 1, solutionCounts);

            // With only one distinct repo overall, no Repo/Solution folder is created at all --
            // the untagged/single-repo tree stays exactly as it looked before repo tagging existed.
            folder.ShouldBeSameAs(root);
            root.Folders.ShouldBeNull();
        }

        [TestMethod]
        public void Untagged_input_stays_flat_even_on_a_multi_repo_site()
        {
            var root = new Folder<ProjectSkeleton>();
            var solutionCounts = new Dictionary<string, int>();

            var folder = Program.GetSolutionExplorerGroupingFolder(root, repoName: "", solutionName: "", distinctRepoCount: 2, solutionCounts);

            folder.ShouldBeSameAs(root);
            root.Folders.ShouldBeNull();
        }

        [TestMethod]
        public void Multiple_repos_each_with_one_solution_get_a_repo_folder_only()
        {
            var root = new Folder<ProjectSkeleton>();
            var solutionCounts = new Dictionary<string, int> { ["ClangSharp"] = 1, ["LLVMSharp"] = 1 };

            var folder = Program.GetSolutionExplorerGroupingFolder(root, "ClangSharp", "ClangSharp", distinctRepoCount: 2, solutionCounts);

            folder.ShouldNotBeSameAs(root);
            folder.Name.ShouldBe("ClangSharp");
            folder.Kind.ShouldBe(FolderKind.Repo);
            folder.RepoName.ShouldBe("ClangSharp");
            folder.Folders.ShouldBeNull(); // no solution-level nesting when the repo has only one solution
        }

        [TestMethod]
        public void Repo_with_multiple_solutions_nests_a_solution_folder_underneath()
        {
            var root = new Folder<ProjectSkeleton>();
            var solutionCounts = new Dictionary<string, int> { ["ClangSharp"] = 2 };

            var folder = Program.GetSolutionExplorerGroupingFolder(root, "ClangSharp", "ClangSharp.Tools", distinctRepoCount: 2, solutionCounts);

            folder.Name.ShouldBe("ClangSharp.Tools");
            folder.Kind.ShouldBe(FolderKind.Solution);
            folder.RepoName.ShouldBe("ClangSharp");

            var repoFolder = root.Folders["ClangSharp"];
            repoFolder.Kind.ShouldBe(FolderKind.Repo);
            repoFolder.Folders["ClangSharp.Tools"].ShouldBeSameAs(folder);
        }

        [TestMethod]
        public void Repeated_calls_for_the_same_repo_reuse_the_same_folder_node()
        {
            var root = new Folder<ProjectSkeleton>();
            var solutionCounts = new Dictionary<string, int> { ["ClangSharp"] = 1 };

            var first = Program.GetSolutionExplorerGroupingFolder(root, "ClangSharp", "ClangSharp", distinctRepoCount: 2, solutionCounts);
            var second = Program.GetSolutionExplorerGroupingFolder(root, "ClangSharp", "ClangSharp", distinctRepoCount: 2, solutionCounts);

            first.ShouldBeSameAs(second);
            root.Folders.Count.ShouldBe(1);
        }

        [TestMethod]
        public void ResolveRepoName_prefers_the_most_specific_nested_mapping()
        {
            // A single VMR-style input rooted at the parent folder, with nested sub-repo mappings: each
            // project must tag to the deepest mapping containing its own folder, not the parent's.
            var mappings = new Dictionary<string, string>
            {
                [@"C:\vmr"] = "dotnet/vmr",
                [@"C:\vmr\src\arcade"] = "dotnet/arcade",
                [@"C:\vmr\src\roslyn"] = "dotnet/roslyn",
            };

            Program.ResolveRepoName(@"C:\vmr\src\arcade\src\Foo\Foo.csproj", mappings, "dotnet/vmr").ShouldBe("dotnet/arcade");
            Program.ResolveRepoName(@"C:\vmr\src\roslyn\Bar\Bar.csproj", mappings, "dotnet/vmr").ShouldBe("dotnet/roslyn");
        }

        [TestMethod]
        public void ResolveRepoName_falls_back_to_the_input_tag_outside_any_nested_mapping()
        {
            var mappings = new Dictionary<string, string>
            {
                [@"C:\vmr"] = "dotnet/vmr",
                [@"C:\vmr\src\arcade"] = "dotnet/arcade",
            };

            // Directly under the VMR root but not under any sub-repo -> the whole input's tag.
            Program.ResolveRepoName(@"C:\vmr\eng\Build\Build.csproj", mappings, "dotnet/vmr").ShouldBe("dotnet/vmr");
            // No mapping contains it and no fallback -> untagged.
            Program.ResolveRepoName(@"C:\other\Baz\Baz.csproj", mappings, "").ShouldBe("");
        }

        [TestMethod]
        public void ResolveRepoChain_returns_the_full_ancestry_outermost_first()
        {
            var mappings = new Dictionary<string, string>
            {
                [@"C:\vmr"] = "dotnet/vmr",
                [@"C:\vmr\src\arcade"] = "dotnet/arcade",
            };

            // A sub-repo project carries both its parent (vmr) and its own repo (arcade), parent first.
            Program.ResolveRepoChain(@"C:\vmr\src\arcade\src\Foo\Foo.csproj", mappings, "dotnet/vmr")
                .ShouldBe(new[] { "dotnet/vmr", "dotnet/arcade" });

            // A project only under the VMR root is a single-element chain of just the parent.
            Program.ResolveRepoChain(@"C:\vmr\eng\Build\Build.csproj", mappings, "dotnet/vmr")
                .ShouldBe(new[] { "dotnet/vmr" });
        }

        [TestMethod]
        public void ResolveRepoChain_falls_back_to_the_input_tag_or_empty()
        {
            var mappings = new Dictionary<string, string>
            {
                [@"C:\vmr\src\arcade"] = "dotnet/arcade",
            };

            // No mapping contains it -> single-element fallback chain.
            Program.ResolveRepoChain(@"C:\other\Baz\Baz.csproj", mappings, "dotnet/vmr")
                .ShouldBe(new[] { "dotnet/vmr" });
            // No mapping and no fallback -> untagged (empty chain).
            Program.ResolveRepoChain(@"C:\other\Baz\Baz.csproj", mappings, "").ShouldBeEmpty();
        }

        [TestMethod]
        public void Nested_repo_chain_groups_a_sub_repo_under_its_parent_repo_folder()
        {
            var root = new Folder<ProjectSkeleton>();
            var solutionCounts = new Dictionary<string, int> { ["dotnet/subx"] = 1 };

            var folder = Program.GetSolutionExplorerGroupingFolder(
                root, new[] { "dotnet/vmr", "dotnet/subx" }, "SubX", distinctRepoCount: 3, solutionCounts);

            // The sub-repo folder is nested under its parent repo folder, each carrying its own ancestry.
            var vmrFolder = root.Folders["dotnet/vmr"];
            vmrFolder.Kind.ShouldBe(FolderKind.Repo);
            vmrFolder.RepoChain.ShouldBe(new[] { "dotnet/vmr" });

            var subxFolder = vmrFolder.Folders["dotnet/subx"];
            subxFolder.ShouldBeSameAs(folder);
            subxFolder.Kind.ShouldBe(FolderKind.Repo);
            subxFolder.RepoName.ShouldBe("dotnet/subx");
            subxFolder.RepoChain.ShouldBe(new[] { "dotnet/vmr", "dotnet/subx" });
        }
    }
}
