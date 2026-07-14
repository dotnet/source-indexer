using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class Markup
    {
        public static string HtmlEscape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            text = System.Security.SecurityElement.Escape(text);

            // HTML doesn't support XML's &apos;
            // need to use &#39; instead
            // http://blogs.msdn.com/kirillosenkov/archive/2010/03/19/apos-is-in-xml-in-html-use-39.aspx#comments
            // http://www.w3.org/TR/html4/sgml/entities.html
            // http://lists.whatwg.org/pipermail/whatwg-whatwg.org/2005-October/004973.html
            // http://en.wikipedia.org/wiki/List_of_XML_and_HTML_character_entity_references
            // http://fishbowl.pastiche.org/2003/07/01/the_curse_of_apos/
            // http://nedbatchelder.com/blog/200703/random_html_factoid_no_apos.html
            // Don't want to use System.Web.HttpUtility.HtmlEncode
            // because I don't want to take a dependency on System.Web
            text = text.Replace("&apos;", "&#39;");
            text = IntersperseLineBreaks(text);

            return text;
        }

        private static string IntersperseLineBreaks(string text) => text.Replace("\n\r", "\n \r");

        public static string HtmlEscape(string text, ref int start, ref int end)
        {
            string trimmed = text.TrimStart(' ');

            // pass -1 to make sure both start and end get offset
            // we don't want start to remain where it was
            Offset(ref start, -1, trimmed.Length - text.Length);
            Offset(ref end, -1, trimmed.Length - text.Length);
            text = trimmed;

            trimmed = text.TrimEnd(' ');
            if (end > trimmed.Length)
            {
                end = trimmed.Length;
            }

            text = trimmed;

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '<')
                {
                    Offset(ref start, sb.Length, 3);
                    Offset(ref end, sb.Length, 3);
                    sb.Append("&lt;");
                }
                else if (text[i] == '>')
                {
                    Offset(ref start, sb.Length, 3);
                    Offset(ref end, sb.Length, 3);
                    sb.Append("&gt;");
                }
                else if (text[i] == '\'')
                {
                    Offset(ref start, sb.Length, 4);
                    Offset(ref end, sb.Length, 4);
                    sb.Append("&#39;");
                }
                else if (text[i] == '\"')
                {
                    Offset(ref start, sb.Length, 5);
                    Offset(ref end, sb.Length, 5);
                    sb.Append("&quot;");
                }
                else if (text[i] == '&')
                {
                    Offset(ref start, sb.Length, 4);
                    Offset(ref end, sb.Length, 4);
                    sb.Append("&amp;");
                }
                else
                {
                    sb.Append(text[i]);
                }
            }

            return sb.ToString();
        }

        private static void Offset(ref int position, int barrier, int offset)
        {
            if (position > barrier)
            {
                position += offset;
            }
        }

        private const string referencesFileHeader = @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><title>{0}</title><link rel=""stylesheet"" href=""../../styles.css""/><script src=""../../scripts.js""></script></head><body onload=""ro();"">";

        public static void WriteReferencesFileHeader(TextWriter writer, string title)
        {
            writer.WriteLine(referencesFileHeader, title);
        }

        private const string zeroFileName = "0000000000.html";

        public static void WriteReferencesNotFoundFile(string folder)
        {
            const string html = @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><link rel=""stylesheet"" href=""styles.css""/></head>
<body><div class=""rH"">No references found</div></body></html>";
            string filePath = Path.Combine(folder, zeroFileName);
            if (!File.Exists(filePath))
            {
                File.WriteAllText(filePath, html, Encoding.UTF8);
            }
        }

        public static void WriteRedirectFile(string projectFolder)
        {
            string referencesFolder = Path.Combine(projectFolder, Constants.ReferencesFileName);
            string redirectFileName = Path.Combine(projectFolder, Constants.IDResolvingFileName + ".html");
            if (Directory.Exists(referencesFolder) && !File.Exists(redirectFileName))
            {
                string contents = @"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Redirecting...</title>
<link rel=""stylesheet"" href=""../styles.css"">
<script src=""../scripts.js""></script>
<script>
redirectToReferences();
</script>
</head>
<body>
<div class=""resultGroupAssemblyName"">{0}</div>
<div class=""note"">Assembly is not indexed. References to the symbol in the indexed assemblies are shown to the left.</div>
</body>
</html>";
                contents = string.Format(contents, Path.GetFileName(projectFolder));
                File.WriteAllText(redirectFileName, contents, Encoding.UTF8);
            }
        }

        public static string GetProjectExplorerReference(string url, string assemblyName)
        {
            return string.Format("<a class=\"reference\" href=\"{0}\" target=\"_top\">{1}</a>", url, assemblyName);
        }

        private static string documentHtmlPrefixTemplate = @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><title>{0}</title><link rel=""stylesheet"" href=""{1}styles.css""><script src=""{1}scripts.js""></script></head>
<body class=""cB"" onload=""{3}({2});"">";
        private static string documentTablePrefix = @"<div class=""cz""><div class=""tb""><div class=""gutter"">{1}<pre id=""ln"">{0}</pre></div><div class=""codeScroll""><pre id=""code"">";

        public static string GetDocumentPrefix(string title, string relativePathToRoot, int lineCount, string customJSOnloadFunction = "i")
        {
            var html = string.Format(documentHtmlPrefixTemplate, title, relativePathToRoot, lineCount, customJSOnloadFunction);
            return html;
        }

        public static string GetTablePrefix()
        {
            return string.Format(documentTablePrefix, "", "");
        }

        public static string GetTablePrefix(string documentUrl, int pregenerateLineNumbers, string glyphHtml)
        {
            var lineNumberText = GenerateLineNumberText(pregenerateLineNumbers, documentUrl);
            if (!string.IsNullOrWhiteSpace(glyphHtml))
            {
                glyphHtml = $@"<pre id=""glyph"">{glyphHtml}</pre>";
            }

            return string.Format(documentTablePrefix, lineNumberText, glyphHtml);
        }

        private static string GenerateLineNumberText(int lineNumbers, string documentUrl)
        {
            if (lineNumbers == 0)
            {
                return string.Empty;
            }

            Func<int, string> FormatLineLinkForDocument = i => FormatLineLink(documentUrl, i);

            return string.Concat(Enumerable.Range(1, lineNumbers).Select(FormatLineLinkForDocument));
        }

        public static string FormatLineLink(string documentUrl, int i)
        {
            return string.Format(
                                "<a id=\"{0}\" href=\"{1}#{0}\" target=\"_top\">{0}</a><br/>",
                                i,
                                documentUrl);
        }

        public static string GetDocumentSuffix()
        {
            return "</pre></div></div></div></body></html>";
        }

        public static void WriteMetadataToSourceRedirectPrefix(StreamWriter writer, bool includeFileList = false)
        {
            writer.WriteLine(@"<!DOCTYPE html>
<html><head><title>Redirecting...</title><script src=""../scripts.js""></script>");

            if (includeFileList)
            {
                writer.WriteLine(@"<script src=""A.files.js""></script>");
            }

            writer.WriteLine("<script>");
        }

        public static void WriteMetadataToSourceRedirectSuffix(StreamWriter writer)
        {
            const string contents = @"
</script>
</head><body>
Don't use this page directly, pass #symbolId to get redirected.
</body></html>";
            writer.WriteLine(contents);
        }

        public static void WriteLinkPanel(
            Action<string> writeLine,
            (string Display, string Url) fileLink,
            string webAccessUrl = null,
            (string Display, string Url, string AssemblyName)? projectLink = null)
        {
            writeLine("<div class=\"dH\">");
            writeLine("<table style=\"width: 100%\">");

            string fileCellContents = string.Format("File: <a id=\"filePath\" class=\"blueLink\" href=\"{0}\" target=\"_top\">{1}</a><br/>", fileLink.Url, fileLink.Display);

            var webAccessCellContents = webAccessUrl is object
                ? A(webAccessUrl, "Web&nbsp;Access", "_blank")
                : null;

            writeLine(string.Format("<tr><td>{0}</td><td>{1}</td></tr>", fileCellContents, webAccessCellContents));

            if (projectLink is object)
            {
                string projectCellContents = string.Format("Project: <a id=\"projectPath\" class=\"blueLink\" href=\"{0}\" target=\"_top\">{1}</a> ({2})", projectLink.Value.Url, projectLink.Value.Display, projectLink.Value.AssemblyName);

                writeLine(string.Format("<tr><td>{0}</td></tr>", projectCellContents));
            }

            writeLine("</table>");
            writeLine("</div>");
        }

        public static void WriteProjectExplorerPrefix(StringBuilder sb, string projectTitle)
        {
            sb.AppendFormat(@"<!DOCTYPE html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><title>{0}</title>
<link rel=""stylesheet"" href=""../styles.css"">
<script src=""../scripts.js""></script>
</head><body class=""projectExplorerBody"">
<div class=""tabChannel""><span class=""activeTab"">Project</span><a class=""inactiveTab"" href=""/#{0},namespaces"" target=""_top"">Namespaces</a></div>
", projectTitle);
        }

        public static void WriteProjectExplorerSuffix(StringBuilder sb)
        {
            sb.AppendLine("<script>initializeProjectExplorer();</script></body></html>");
        }

        public static void WriteSolutionExplorerPrefix(TextWriter writer)
        {
            // A short, static "Solution Explorer" label replaces the old instructional
            // sentence (which read oddly once the repo filter sat inline with it). This gives
            // the filter a consistent note-styled home to embed into, matching results.html,
            // without any dynamic per-search text to keep in sync here.
            writer.WriteLine(@"<!DOCTYPE html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><title>Solution Explorer</title><link rel=""stylesheet"" href=""styles.css"" /><script src=""scripts.js""></script></head>
<body class=""solutionExplorerBody"">
    <div class=""note"">
        Solution Explorer
        <select id=""repo-filter"" style=""display:none"" aria-label=""Filter search to a repo""></select>
    </div>
<div id=""rootFolder"" style=""display: none;"" class=""folderTitle"">");
        }

        public static void WriteSolutionExplorerSuffix(TextWriter writer)
        {
            writer.WriteLine("</div><script>onSolutionExplorerLoad();</script></body></html>");
        }

        public static void WriteNamespaceExplorerPrefix(string assemblyName, StreamWriter sw, string pathPrefix = "")
        {
            sw.WriteLine(string.Format(@"<!DOCTYPE html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><title>Namespaces</title>
<link rel=""stylesheet"" href=""{0}styles.css"">
<script src=""{0}scripts.js""></script>
</head><body class=""namespaceExplorerBody"">
<div class=""tabChannel""><a class=""inactiveTab"" href=""/#{1}"" target=""_top"">Project</a><span class=""activeTab"">Namespaces</span></div>
", pathPrefix, assemblyName));
        }

        public static void WriteNamespaceExplorerSuffix(StreamWriter sw)
        {
            sw.WriteLine("<script>initializeNamespaceExplorer();</script></body></html>");
        }

        public static void WriteProjectIndex(StringBuilder sb, string assemblyName)
        {
            sb.AppendFormat(@"<!DOCTYPE html><html><head><title>Redirecting</title>
<script src=""../scripts.js""></script>
<script>initializeProjectIndex(""../#{0}"");</script>
</head><body></body></html>", assemblyName);
        }

        public static void GenerateResultsHtml(string solutionDestinationFolder)
        {
            var sb = new StringBuilder();

            sb.AppendLine(GetResultsHtmlPrefix());
            sb.AppendLine(GetResultsHtmlSuffix(emitSolutionBrowserLink: false));

            File.WriteAllText(Path.Combine(solutionDestinationFolder, "results.html"), sb.ToString());
        }

        public static void GenerateResultsHtmlWithAssemblyList(string solutionDestinationFolder, IEnumerable<string> assemblyList)
        {
            var sb = new StringBuilder();

            sb.AppendLine(GetResultsHtmlPrefix());

            foreach (var assemblyName in assemblyList)
            {
                sb.AppendFormat(
                  @"<a href=""/#{0},namespaces"" target=""_top""><div class=""resultItem""><div class=""resultLine"">{0}</div></div></a>", assemblyName);
                sb.AppendLine();
            }

            sb.AppendLine(GetResultsHtmlSuffix(emitSolutionBrowserLink: true));

            File.WriteAllText(Path.Combine(solutionDestinationFolder, "results.html"), sb.ToString());
        }

        public static string GetResultsHtmlPrefix()
        {
            return @"<!DOCTYPE html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><title>Results</title>
<link rel=""stylesheet"" href=""styles.css"" />
<script src=""scripts.js""></script>
</head>
<body onload=""onResultsLoad();"">
<select id=""repo-filter"" style=""display:none"" aria-label=""Filter search to a repo""></select>
<div id=""symbols"" aria-live=""polite"">
<div class=""note"">
Enter a type or member name or <a href=""/#q=assembly%20"" target=""_top"" class=""blueLink"" onclick=""populateSearchBox('assembly '); return false;"">filter the assembly list</a>.
</div>
<div class=""resultGroup"">
";
        }

        public static string GetResultsHtmlSuffix(bool emitSolutionBrowserLink)
        {
            var solutionExplorerLink = emitSolutionBrowserLink
                ? @"<div class=""note"">Try also browsing the <a href=""SolutionExplorer.html"" class=""blueLink"">solution explorer</a>.</div>"
                : null;

            return "</div></div>" + solutionExplorerLink + "</body></html>";
        }

        private static string partialTypeDisambiguationFileTemplate = @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><link rel=""stylesheet"" href=""{0}"">{2}
</head><body{3}><div class=""partialTypeHeader"">Partial Type</div>
{1}
</body></html>";

        public static void GeneratePartialTypeDisambiguationFile(
            string solutionDestinationFolder,
            string projectDestinationFolder,
            string symbolId,
            IEnumerable<string> filePaths)
        {
            GeneratePartialTypeDisambiguationFile(solutionDestinationFolder, projectDestinationFolder, symbolId, filePaths, configTagsByFilePath: null);
        }

        /// <summary>
        /// Same disambiguation page used for ordinary partial types/members (one symbol ID declared in
        /// multiple files), extended to optionally annotate each link with the config(s) it applies
        /// under -- e.g. a symbol declared in Environment.Windows.cs under "windows" and
        /// Environment.Unix.cs under "linux"/"mac" is just a multi-location symbol like any other
        /// partial type, with a config tag as extra metadata on each location. When
        /// <paramref name="configTagsByFilePath"/> is null (the default, single/no-config case), this
        /// renders byte-identically to the untagged overload.
        /// </summary>
        public static void GeneratePartialTypeDisambiguationFile(
            string solutionDestinationFolder,
            string projectDestinationFolder,
            string symbolId,
            IEnumerable<string> filePaths,
            IReadOnlyDictionary<string, IEnumerable<string>> configTagsByFilePath)
        {
            GeneratePartialTypeDisambiguationFile(solutionDestinationFolder, projectDestinationFolder, symbolId, filePaths, configTagsByFilePath, allConfigs: null);
        }

        /// <summary>
        /// Same as the five-argument overload, but when <paramref name="allConfigs"/> is supplied (the
        /// config-aware merge path) each location link also gets a machine-readable
        /// <c>data-configs="a,b"</c> attribute -- mirroring
        /// <see cref="ProjectFinalizer.WriteDataConfigsAttribute"/>'s FAR gating -- so the client
        /// config-selector can filter these links the same way it filters FAR entries. The existing
        /// visible <c>[a, b]</c> span is untouched. Omitted (both attribute and gating) when
        /// <paramref name="allConfigs"/> is null, so the single/no-config path renders byte-identically
        /// to before this parameter existed.
        /// </summary>
        public static void GeneratePartialTypeDisambiguationFile(
            string solutionDestinationFolder,
            string projectDestinationFolder,
            string symbolId,
            IEnumerable<string> filePaths,
            IReadOnlyDictionary<string, IEnumerable<string>> configTagsByFilePath,
            IReadOnlyCollection<string> allConfigs)
        {
            string partialFolder = Path.Combine(projectDestinationFolder, Constants.PartialResolvingFileName);
            Directory.CreateDirectory(partialFolder);
            var disambiguationFileName = Path.Combine(partialFolder, symbolId) + ".html";
            string list = string.Join(Environment.NewLine,
                filePaths
                .OrderBy(filePath => Paths.StripExtension(filePath))
                .Select((filePath, index) =>
                {
                    string configTag = "";
                    string dataConfigsAttribute = "";
                    if (configTagsByFilePath != null &&
                        configTagsByFilePath.TryGetValue(filePath, out var configs) &&
                        configs != null)
                    {
                        var configList = configs.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
                        if (configList.Count > 0)
                        {
                            configTag = $" <span class=\"partialTypeConfigTag\">[{string.Join(", ", configList)}]</span>";

                            // Fully shared across every registered config -- inert, don't tag (matches
                            // WriteDataConfigsAttribute's "shared -> untagged" convention).
                            if (allConfigs != null && allConfigs.Count > 0 && !allConfigs.All(configList.Contains))
                            {
                                dataConfigsAttribute = $" data-configs=\"{string.Join(",", configList)}\"";
                            }
                        }
                    }

                    return $"<div class=\"partialTypeLink\"><a{(index == 0 ? $" id=\"{symbolId}\"" : "")}{dataConfigsAttribute} href=\"../{filePath}.html#{symbolId}\">{filePath}</a>{configTag}</div>";
                }));

            // Only include scripts.js / call the config-filter entry point when this is a config-aware
            // render (allConfigs != null) -- the ordinary single/no-config path never needed any script
            // on this page and must stay byte-identical to before this parameter existed.
            string scriptsTag = "";
            string bodyOnload = "";
            if (allConfigs != null && allConfigs.Count > 0)
            {
                var scriptsPath = Paths.GetCssPathFromFile(solutionDestinationFolder, disambiguationFileName);
                scriptsPath = scriptsPath.Substring(0, scriptsPath.Length - "styles.css".Length) + "scripts.js";
                scriptsTag = $"<script src=\"{scriptsPath}\"></script>";
                bodyOnload = " onload=\"sbApplyConfigFilter(document);\"";
            }

            string content = string.Format(
                partialTypeDisambiguationFileTemplate,
                Paths.GetCssPathFromFile(solutionDestinationFolder, disambiguationFileName),
                list,
                scriptsTag,
                bodyOnload);
            File.WriteAllText(disambiguationFileName, content, Encoding.UTF8);
        }

        public static string EscapeSemicolons(string text)
        {
            text = text.Replace(';', ':');
            text = text.Replace('\r', ' ');
            text = text.Replace('\n', ' ');
            return text;
        }

        public static string A(string url, string displayText, string target = "")
        {
            if (!string.IsNullOrEmpty(target))
            {
                target = string.Format(" target=\"{0}\"", target);
            }
            else
            {
                target = "";
            }

            string result = string.Format("<a class=\"blueLink\" href=\"{0}\"{2}>{1}</a>", url, displayText, target);
            return result;
        }

        public static string Tag(string tag, string content, IEnumerable<KeyValuePair<string, string>> attributes = null)
        {
            var sb = new StringBuilder();

            sb.Append("<");
            sb.Append(tag);

            if (attributes != null && attributes.Any())
            {
                foreach (var kvp in attributes)
                {
                    sb.Append(" ");
                    sb.Append(kvp.Key);
                    sb.Append("=\"");
                    sb.Append(kvp.Value);
                    sb.Append("\"");
                }
            }

            sb.Append(">");
            sb.Append(content);
            sb.Append("</");
            sb.Append(tag);
            sb.Append(">");

            return sb.ToString();
        }

        public static void WriteSymbol(DeclaredSymbolInfo symbol, StringBuilder sb)
        {
            var url = symbol.GetUrl();
            sb.AppendFormat("<a href=\"{0}\" target=\"s\"><div class=\"resultItem\" onClick=\"resultClick(this);\">", url);
            sb.Append("<div class=\"resultLine\">");
            sb.AppendFormat("<img role=\"presentation\" src=\"/content/icons/{0}\" height=\"16\" width=\"16\" />", GetGlyph(symbol) + ".png");
            sb.AppendFormat("<div class=\"resultKind\">{0}</div>", symbol.Kind);
            sb.AppendFormat("<div class=\"resultName\">{0}</div>", Markup.HtmlEscape(symbol.Name));
            sb.AppendLine("</div>");
            sb.AppendFormat("<div class=\"resultDescription\">{0}</div>", Markup.HtmlEscape(symbol.Description));
            sb.AppendLine();
            sb.AppendLine("</div></a>");
        }

        private static string GetGlyph(DeclaredSymbolInfo symbol)
        {
            var result = symbol.Glyph;
            if (result == 196)
            {
                return "csharp";
            }
            else if (result == 195)
            {
                return "vb";
            }
            else if (result == 227)
            {
                return "xaml";
            }
            else if (result == 228)
            {
                return "typescript";
            }

            return result.ToString();
        }
    }
}
