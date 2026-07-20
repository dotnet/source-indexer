namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class Configuration
    {
        // useful knobs to suppress stuff
        public static readonly bool GenerateMetadataAsSourceBodies = true;
        public static readonly bool CalculateRoslynSemantics = true;
        public static readonly bool WriteDocumentsToDisk = true;
        public static readonly bool WriteProjectAuxiliaryFilesToDisk = true;
        public static readonly bool CreateFoldersOnDisk = true;
        public static readonly bool FlattenSolutionExplorer = false;

        /// <summary>
        /// When true (opt-in via /incremental), Pass1 skips regenerating a project whose staleness key
        /// (see <see cref="ProjectStaleness"/>) is unchanged since the last run, and Pass2 skips re-copying
        /// that project's already-finalized output -- retaining it as-is and only refreshing the
        /// cross-assembly aggregates (references, "Used By" backlinks, etc.) that depend on the full
        /// current project set. Off by default so a plain run's behavior/output is unchanged from before.
        /// </summary>
        public static bool Incremental = false;
    }
}
