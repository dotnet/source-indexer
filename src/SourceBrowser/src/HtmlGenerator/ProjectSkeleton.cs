namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class ProjectSkeleton
    {
        public string AssemblyName { get; }
        public string Name { get; }

        /// <summary>Optional repo tag threaded through to the merged Solution Explorer so it can
        /// be filtered client-side; empty for untagged sites (see SolutionGenerator.RepoName).</summary>
        public string RepoName { get; }

        /// <summary>Full repo ancestry (outermost first, this project's own repo last) so the client
        /// filter can scope a parent repo to include its nested sub-repos. Single-element for a
        /// non-nested repo; empty for untagged sites.</summary>
        public System.Collections.Generic.IReadOnlyList<string> RepoChain { get; }

        public ProjectSkeleton(string assemblyName, string name, string repoName = "", System.Collections.Generic.IReadOnlyList<string> repoChain = null)
        {
            AssemblyName = assemblyName;
            Name = name;
            RepoName = repoName ?? "";
            RepoChain = repoChain ?? (string.IsNullOrEmpty(RepoName) ? System.Array.Empty<string>() : new[] { RepoName });
        }
    }
}