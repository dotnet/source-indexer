namespace SubRepoX
{
    /// <summary>
    /// Lives under Vmr/src/SubRepoX, tagged via a nested /repoPath to dotnet/subx even though it's part
    /// of the single Vmr.slnx input -- the sub-repo case that used to inherit the parent's tag.
    /// </summary>
    public class Alpha
    {
        public string Describe() => AlphaInfo.Label;
    }

    public static class AlphaInfo
    {
        public static string Label => "SubRepoX.Alpha";
    }
}
