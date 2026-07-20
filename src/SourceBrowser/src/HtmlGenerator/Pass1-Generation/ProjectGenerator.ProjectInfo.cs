using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class ProjectGenerator
    {
        public long DocumentCount = 0;
        public long LinesOfCode = 0;
        public long BytesOfCode = 0;

        private void GenerateProjectInfo()
        {
            Log.Write("Project info...");
            var projectInfoFile = Path.Combine(ProjectDestinationFolder, Constants.ProjectInfoFileName) + ".txt";
            var namedTypes = this.DeclaredSymbols.Keys.OfType<INamedTypeSymbol>();
            var sb = new StringBuilder();
            sb.Append("ProjectSourcePath=").AppendLine(ProjectSourcePath)
                .Append("DocumentCount=").Append(DocumentCount).AppendLine()
                .Append("LinesOfCode=").Append(LinesOfCode).AppendLine()
                .Append("BytesOfCode=").Append(BytesOfCode).AppendLine()
                .Append("DeclaredSymbols=").Append(DeclaredSymbols.Count).AppendLine()
                .Append("DeclaredTypes=").Append(namedTypes.Count()).AppendLine()
                .Append("PublicTypes=").Append(namedTypes.Count(t => t.DeclaredAccessibility == Accessibility.Public)).AppendLine();

            if (!string.IsNullOrEmpty(RepoName))
            {
                sb.Append("RepoName=").AppendLine(RepoName);
            }

            // Full repo ancestry (outermost first, own repo last), '|'-joined -- lets a parent repo
            // include its nested sub-repos in filtering/grouping. '|' can't occur in an owner/repo name,
            // so it stays a safe field separator here and in Assemblies.txt.
            if (RepoChain is { Count: > 0 })
            {
                sb.Append("RepoChain=").AppendLine(string.Join("|", RepoChain));
            }

            if (!string.IsNullOrEmpty(SolutionName))
            {
                sb.Append("SolutionName=").AppendLine(SolutionName);
            }

            File.WriteAllText(projectInfoFile, sb.ToString());
        }
    }
}
