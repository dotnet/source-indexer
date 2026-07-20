using System;

namespace RepoBLib
{
    /// <summary>
    /// Demonstrates the multi-config (#104) feature: this file's body differs by the
    /// "os" config axis (WINDOWS vs. the default/else branch), so a config-aware
    /// HtmlGenerator run renders it as two divergent variant pages, and the client-side
    /// config selector (embedded in the code view) lets a reader flip between them.
    /// </summary>
    public static class PlatformInfo
    {
#if WINDOWS
        public static string Name => "Windows";

        public static char DirectorySeparator => '\\';
#else
        public static string Name => "Linux";

        public static char DirectorySeparator => '/';
#endif

        public static string Describe()
        {
            return $"{Name} (separator: '{DirectorySeparator}')";
        }
    }
}
