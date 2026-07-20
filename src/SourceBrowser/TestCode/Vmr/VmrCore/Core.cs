namespace VmrCore
{
    /// <summary>
    /// Sits directly under the Vmr root (not under any src/SubRepo* /repoPath), so per-project repo
    /// resolution falls back to the whole input's tag (dotnet/vmr) -- the fallback arm of the fix.
    /// </summary>
    public class Core
    {
        public string Describe() => CoreInfo.Label;
    }

    public static class CoreInfo
    {
        public static string Label => "VmrCore.Core";
    }
}
