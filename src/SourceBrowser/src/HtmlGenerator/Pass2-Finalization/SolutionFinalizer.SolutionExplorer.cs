using System;
using System.IO;
using Microsoft.SourceBrowser.Common;
using Folder = Microsoft.SourceBrowser.HtmlGenerator.Folder<Microsoft.SourceBrowser.HtmlGenerator.ProjectSkeleton>;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class SolutionFinalizer
    {
        private void WriteSolutionExplorer(bool emitAssemblyList, Folder root = null)
        {
            if (root == null)
            {
                return;
            }

            Sort(root);

            using (var writer = new StreamWriter(Path.Combine(SolutionDestinationFolder, Constants.SolutionExplorer + ".html")))
            {
                Log.Write("Solution Explorer...");
                Markup.WriteSolutionExplorerPrefix(writer);
                WriteFolder(root, writer);
                Markup.WriteSolutionExplorerSuffix(writer, emitAssemblyList);
            }
        }

        private void Sort(Folder<ProjectSkeleton> root, Comparison<string> customRootSorter = null)
        {
            if (Configuration.FlattenSolutionExplorer)
            {
                if (customRootSorter == null)
                {
                    customRootSorter = (l, r) => StringComparer.OrdinalIgnoreCase.Compare(l, r);
                }

                root.Sort((l, r) => customRootSorter(l.AssemblyName, r.AssemblyName));
            }
            else
            {
                root.Sort((l, r) => StringComparer.OrdinalIgnoreCase.Compare(l.Name, r.Name));
            }
        }

        private void WriteFolder(Folder folder, StreamWriter writer)
        {
            if (folder.Folders != null)
            {
                foreach (var subfolder in folder.Folders.Values)
                {
                    // Repo/Solution grouping nodes (see Program.IndexSolutionsAsync) get an extra
                    // class on their *title* div, so styles.css can style them distinctly, plus a
                    // data-repo attribute on both divs so the client-side repo filter can hide the
                    // whole group (not just its individual project items) when it doesn't match.
                    // The container div's class must stay exactly "folder" -- scripts.js does a
                    // strict className equality check (not a class-list check) when recursively
                    // wiring up expand/collapse icons for child folders, so any additional class
                    // there would silently break icons on repo/solution subfolders' children;
                    // adding a data-* attribute doesn't affect className, so it's safe.
                    var titleClass = subfolder.Kind switch
                    {
                        FolderKind.Repo => "folderTitle repoTitle",
                        FolderKind.Solution => "folderTitle solutionTitle",
                        _ => "folderTitle"
                    };

                    var dataRepoAttribute = string.IsNullOrEmpty(subfolder.RepoName)
                        ? ""
                        : string.Format(" data-repo=\"{0}\"", subfolder.RepoName);

                    writer.WriteLine(
                        @"<div class=""{0}""{1}>{2}</div><div class=""folder""{1}>",
                        titleClass, dataRepoAttribute, subfolder.Name);
                    WriteFolder(subfolder, writer);
                    writer.WriteLine("</div>");
                }
            }

            if (folder.Items != null)
            {
                foreach (var project in folder.Items)
                {
                    WriteProject(project.AssemblyName, project.RepoName, writer);
                }
            }
        }

        private void WriteProject(string assemblyName, string repoName, StreamWriter writer)
        {
            var projectExplorerText = GetProjectExplorerText(assemblyName, repoName);
            if (!string.IsNullOrEmpty(projectExplorerText))
            {
                writer.WriteLine(projectExplorerText);
            }
        }

        private string GetProjectExplorerText(string assemblyName, string repoName)
        {
            var fileName = Path.Combine(SolutionDestinationFolder, assemblyName, Constants.ProjectExplorer + ".html");
            if (!File.Exists(fileName))
            {
                return null;
            }

            var text = File.ReadAllText(fileName);
            const string startText = "<div id=\"rootFolder\"";
            var start = text.IndexOf(startText, StringComparison.Ordinal) + startText.Length;
            var end = text.IndexOf("<script>", StringComparison.Ordinal);
            text = text.Substring(start, end - start);
            text = "<div" + text;

            // Only add a data-repo attribute (and thus a client-side filtering hook) when the site
            // actually has a repo tag -- keeps the untagged/single-repo output identical to before
            // repo tagging existed.
            var folderAttributes = string.IsNullOrEmpty(repoName)
                ? string.Format("class=\"folder\" data-assembly=\"{0}\"", assemblyName)
                : string.Format("class=\"folder\" data-assembly=\"{0}\" data-repo=\"{1}\"", assemblyName, repoName);
            text = text.Replace("</div><div>", string.Format("</div><div {0}>", folderAttributes));

            // The project title (the always-visible "Project2"-style line) is a sibling that comes
            // before the folder div patched above, not a child of it -- so it needs its own
            // data-repo attribute or hiding the folder above leaves the title behind in the list.
            if (!string.IsNullOrEmpty(repoName))
            {
                text = text.Replace("class=\"projectCS\"", string.Format("class=\"projectCS\" data-repo=\"{0}\"", repoName));
                text = text.Replace("class=\"projectVB\"", string.Format("class=\"projectVB\" data-repo=\"{0}\"", repoName));
            }

            text = text.Replace("projectCS", "projectCSInSolution");
            text = text.Replace("projectVB", "projectVBInSolution");

            var projectInfoStart = text.IndexOf("<p class=\"projectInfo", StringComparison.Ordinal);
            if (projectInfoStart != -1)
            {
                var projectInfoEnd = text.IndexOf("</p>", projectInfoStart, StringComparison.Ordinal) + 4;
                if (projectInfoEnd != -1)
                {
                    text = text.Remove(projectInfoStart, projectInfoEnd - projectInfoStart);
                }
            }

            return text;
        }
    }
}
