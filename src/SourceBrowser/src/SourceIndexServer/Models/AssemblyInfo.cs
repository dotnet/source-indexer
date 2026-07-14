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

        public AssemblyInfo(string line)
        {
            var parts = line.Split(';');
            AssemblyName = parts[0];
            ProjectKey = short.Parse(parts[1]);
            ReferencingAssembliesCount = short.Parse(parts[2]);
            RepoName = parts.Length > 3 ? parts[3] : "";
            SolutionName = parts.Length > 4 ? parts[4] : "";
        }
    }
}
