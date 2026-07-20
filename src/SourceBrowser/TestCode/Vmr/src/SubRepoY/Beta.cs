namespace SubRepoY
{
    /// <summary>
    /// Lives under Vmr/src/SubRepoY, tagged via a nested /repoPath to dotnet/suby -- a second sub-repo
    /// under the same input, so the merged site distinguishes subx/suby/vmr rather than one repo.
    /// </summary>
    public class Beta
    {
        public string Describe() => BetaInfo.Label;
    }

    public static class BetaInfo
    {
        public static string Label => "SubRepoY.Beta";
    }
}
