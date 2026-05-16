namespace MiniRuntime;

/// <summary>
/// Provides static information about the MiniRuntime fixture library.
/// </summary>
/// <remarks>
/// This type exists so the source indexer has something with XML doc
/// comments, public surface, and internal helpers to render.
/// </remarks>
public static class MiniRuntimeInfo
{
    /// <summary>
    /// Gets the human-readable name of this runtime fixture.
    /// </summary>
    public static string Name => "MiniRuntime";

    /// <summary>
    /// Gets the version string for this runtime fixture.
    /// </summary>
    public static string Version => "0.1.0-inner-loop";

    /// <summary>
    /// Returns a banner suitable for logging at startup.
    /// </summary>
    /// <returns>A formatted banner string.</returns>
    public static string GetBanner() => Banners.Format(Name, Version);

    internal static class Banners
    {
        internal static string Format(string name, string version)
            => $"=== {name} v{version} ===";
    }
}
