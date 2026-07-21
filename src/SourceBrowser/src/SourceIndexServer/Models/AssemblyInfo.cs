namespace Microsoft.SourceBrowser.SourceIndexServer
{
    public struct AssemblyInfo
    {
        public string AssemblyName;
        public short ProjectKey;
        public short ReferencingAssembliesCount;

        /// <summary>
        /// Optional repo/solution display-name tags applied at generation time via /repoPath,
        /// /repo, or auto-derived from the input .sln/.slnx file name. Empty when the site (or
        /// this particular assembly) wasn't tagged -- which is the default, unified-search case.
        /// </summary>
        public string RepoName;
        public string SolutionName;

        /// <summary>Full repo ancestry (outermost first, own repo last), so a parent repo filter can
        /// include its nested sub-repos. Falls back to a single-element chain of <see cref="RepoName"/>
        /// for indexes generated before chains were persisted.</summary>
        public string[] RepoChain;

        public AssemblyInfo(string line)
        {
            var parts = line.Split(';');
            AssemblyName = parts[0];
            ProjectKey = short.Parse(parts[1]);
            ReferencingAssembliesCount = short.Parse(parts[2]);
            RepoName = parts.Length > 3 ? parts[3] : "";
            SolutionName = parts.Length > 4 ? parts[4] : "";
            RepoChain = parts.Length > 5 && parts[5].Length > 0
                ? parts[5].Split('|')
                : (RepoName.Length > 0 ? new[] { RepoName } : System.Array.Empty<string>());
        }
    }
}
