namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class ProjectSkeleton
    {
        public string AssemblyName { get; }
        public string Name { get; }

        /// <summary>Optional repo tag threaded through to the merged Solution Explorer so it can
        /// be filtered client-side; empty for untagged sites (see SolutionGenerator.RepoName).</summary>
        public string RepoName { get; }

        public ProjectSkeleton(string assemblyName, string name, string repoName = "")
        {
            AssemblyName = assemblyName;
            Name = name;
            RepoName = repoName ?? "";
        }
    }
}