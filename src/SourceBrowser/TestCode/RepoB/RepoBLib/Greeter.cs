namespace RepoBLib
{
    /// <summary>
    /// A shared (non-divergent) type in RepoB -- present to show that most files in a
    /// config-aware, multi-repo site are entirely unaffected: no config selector is
    /// mounted here, since there is nothing tagged data-configs on this page.
    /// </summary>
    public class Greeter
    {
        public string Greet(string name) => $"Hello, {name}! ({PlatformInfo.Describe()})";
    }
}
