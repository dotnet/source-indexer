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
    }
}
