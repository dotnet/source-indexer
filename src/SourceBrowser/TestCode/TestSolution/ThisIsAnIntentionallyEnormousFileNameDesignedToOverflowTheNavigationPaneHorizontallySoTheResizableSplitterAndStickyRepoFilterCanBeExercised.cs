namespace TestSolution
{
    /// <summary>
    /// Deliberately long file name and type name used to exercise the nav-pane layout: a
    /// long result line must scroll within its own group (and a long tree node within the
    /// Solution Explorer) without pushing the repo filter dropdown off-screen, and the
    /// side-by-side panes stay resizable via the splitter. Search for "ThisIsAn" to see it.
    /// </summary>
    public static class ThisIsAnIntentionallyEnormousTypeNameDesignedToOverflowTheNavigationPaneHorizontallySoTheResizableSplitterAndStickyRepoFilterCanBeExercised
    {
        public static string ThisIsAnIntentionallyEnormousMethodNameThatAlsoContributesToTheHorizontalWidthOfASingleResultLineForTestingPurposes()
        {
            return "overflow";
        }
    }
}
