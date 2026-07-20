namespace RepoBLib2
{
    /// <summary>
    /// Lives in a second solution (RepoB2.slnx) under the same RepoB /repoPath, so the merged
    /// site groups both RepoB solutions under a Solution Explorer solutionTitle node -- the only
    /// place the multi-solution grouping (and its theming) is exercised.
    /// </summary>
    public class Widget
    {
        public string Describe() => WidgetInfo.Label;
    }

    /// <summary>
    /// Referenced by <see cref="Widget"/> so RepoBLib2 has an indexed cross-symbol reference; without
    /// one, Pass2's DiscoverProjects skips the assembly (no R folder) and it never reaches the site.
    /// </summary>
    public static class WidgetInfo
    {
        public static string Label => "RepoBLib2.Widget";
    }
}
