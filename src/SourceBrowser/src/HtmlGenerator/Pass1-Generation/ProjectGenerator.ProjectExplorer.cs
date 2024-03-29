﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.Common;
using Folder = Microsoft.SourceBrowser.HtmlGenerator.Folder<string>;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class ProjectGenerator
    {
        private void GenerateProjectExplorer()
        {
            Log.Write("Project Explorer...");
            var projectExplorerFile = Path.Combine(ProjectDestinationFolder, Constants.ProjectExplorer) + ".html";
            var sb = new StringBuilder();
            Markup.WriteProjectExplorerPrefix(sb, Project.AssemblyName);
            WriteDocuments(sb);
            WriteProjectStats(sb);
            Markup.WriteProjectExplorerSuffix(sb);
            File.WriteAllText(projectExplorerFile, sb.ToString());
        }

        private void WriteProjectStats(StringBuilder sb)
        {
            sb.AppendLine("<p class=\"projectInfo\">");

            var namedTypes = this.DeclaredSymbols.Keys.OfType<INamedTypeSymbol>();
            sb.Append("Project&nbsp;path:&nbsp;").Append(ProjectSourcePath).AppendLine("<br>");
            sb.Append("Files:&nbsp;").Append(DocumentCount.WithThousandSeparators()).AppendLine("<br>");
            sb.Append("Lines&nbsp;of&nbsp;code:&nbsp;").Append(LinesOfCode.WithThousandSeparators()).AppendLine("<br>");
            sb.Append("Bytes:&nbsp;").Append(BytesOfCode.WithThousandSeparators()).AppendLine("<br>");
            sb.Append("Declared&nbsp;symbols:&nbsp;").Append(this.DeclaredSymbols.Count.WithThousandSeparators()).AppendLine("<br>");
            sb.Append("Declared&nbsp;types:&nbsp;").Append(namedTypes.Count().WithThousandSeparators()).AppendLine("<br>");
            sb.Append("Public&nbsp;types:&nbsp;").Append(namedTypes.Where(t => t.DeclaredAccessibility == Accessibility.Public).Count().WithThousandSeparators()).AppendLine("<br>");
            sb.Append("Indexed&nbsp;on:&nbsp;").AppendLine(DateTime.Now.ToString("MMMM dd", CultureInfo.InvariantCulture));

            sb.AppendLine("</p>");
        }

        private void WriteDocuments(StringBuilder sb)
        {
            Folder root = new Folder();
            root.Name = Project.Name;

            foreach (var otherFile in OtherFiles)
            {
                var parts = otherFile.Split('\\');
                AddDocumentToFolder(root, otherFile, parts.Take(parts.Length - 1).ToArray());
            }

            root.Sort((l, r) => string.Compare(Path.GetFileName(l), Path.GetFileName(r), StringComparison.Ordinal));
            WriteRootFolder(root, sb);
        }

        private void WriteRootFolder(Folder folder, StringBuilder sb)
        {
            string className = IsCSharp ?
                "projectCS" :
                "projectVB";
            sb.AppendFormat(
                "<div id=\"rootFolder\" class=\"{0}\">{1}</div>",
                className,
                folder.Name);
            sb.AppendLine("<div>");

            if (folder.Folders != null && folder.Folders.TryGetValue("Properties", out Folder<string> properties))
            {
                WriteFolder(properties, sb);
                folder.Folders.Remove("Properties");
            }

            if (Project.ProjectReferences.Any() || Project.MetadataReferences.Any())
            {
                WriteReferences(sb);
            }

            WriteFolders(folder, sb);
            WriteDocuments(folder, sb);
            sb.AppendLine("</div>");
        }

        private void WriteReferences(StringBuilder sb)
        {
            sb.Append("<div class=\"folderTitle\">References</div>");
            sb.AppendLine("<div class=\"folder\">");

            var assemblyNames = new SortedSet<string>();

            foreach (var projectReference in Project.ProjectReferences.Select(p => Project.Solution.GetProject(p.ProjectId).AssemblyName))
            {
                assemblyNames.Add(projectReference);
            }

            foreach (var metadataReference in Project.MetadataReferences.Select(m => Path.GetFileNameWithoutExtension(m.Display)))
            {
                assemblyNames.Add(metadataReference);
            }

            foreach (var reference in ForwardedReferenceAssemblies)
            {
                assemblyNames.Add(reference);
            }

            var usedReferences = new HashSet<string>(this.UsedReferences, StringComparer.OrdinalIgnoreCase);
            foreach (var reference in assemblyNames)
            {
                var externalIndex = this.SolutionGenerator.GetExternalAssemblyIndex(reference);
                string url = "/#" + reference;
                if (externalIndex != -1)
                {
                    url = "@" + externalIndex.ToString() + "@#" + reference;
                    sb.AppendLine(Markup.GetProjectExplorerReference(url, reference));
                }
                else if ((SolutionGenerator.IsPartOfSolution(reference) || (reference?.Contains("->") ?? false)) && usedReferences.Contains(reference))
                {
                    sb.AppendLine(Markup.GetProjectExplorerReference(url, reference));
                }
                else
                {
                    sb.Append("<span class=\"referenceDisabled\">").Append(reference).AppendLine("</span>");
                }
            }

            sb.AppendLine("</div>");
        }

        private void WriteFolder(Folder folder, StringBuilder sb)
        {
            WriteFolderName(folder, sb);
            sb.AppendLine("<div class=\"folder\">");
            WriteFolders(folder, sb);
            WriteDocuments(folder, sb);
            sb.AppendLine("</div>");
        }

        private void WriteFolders(Folder folder, StringBuilder sb)
        {
            if (folder.Folders != null)
            {
                foreach (var subfolder in folder.Folders.Values)
                {
                    WriteFolder(subfolder, sb);
                }
            }
        }

        private void WriteDocuments(Folder folder, StringBuilder sb)
        {
            if (folder.Items != null)
            {
                foreach (var document in folder.Items)
                {
                    WriteDocument(folder, document, sb);
                }
            }
        }

        private void WriteFolderName(Folder folder, StringBuilder sb)
        {
            sb.Append("<div class=\"folderTitle\">").Append(folder.Name).Append("</div>");
        }

        private void WriteDocument(Folder folder, string document, StringBuilder sb)
        {
            string hyperlink = GetHyperlink(document);
            sb.AppendFormat(
                "<a href=\"{0}\"></a>",
                hyperlink);
            sb.AppendLine();
        }

        private string GetHyperlink(string document)
        {
            if (document.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                var fullPath = Path.Combine(Path.GetDirectoryName(this.ProjectFilePath), document);
                var destination = TypeScriptSupport.GetDestinationFilePath(fullPath);
                destination = destination.Substring(this.SolutionGenerator.SolutionDestinationFolder.Length + 1);
                destination = destination.Replace('\\', '/');
                destination = "/" + destination;
                return destination;
            }

            string localPath = document + ".html";
            localPath = localPath.Replace('\\', '/');
            return localPath;
        }

        private void AddDocumentToFolder(Folder folder, string document, string[] subfolders)
        {
            if (subfolders == null || subfolders.Length == 0)
            {
                folder.Add(document);
                return;
            }

            if (subfolders[0].EndsWith(":", StringComparison.Ordinal))
            {
                return;
            }

            var folderName = Paths.SanitizeFolder(subfolders[0]);
            Folder subfolder = folder.GetOrCreateFolder(folderName);
            AddDocumentToFolder(subfolder, document, subfolders.Skip(1).ToArray());
        }
    }
}
