using Microsoft.SourceBrowser.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public sealed class CommandLineOptions
    {
        public CommandLineOptions(
            string solutionDestinationFolder,
            IReadOnlyList<string> projects,
            IReadOnlyDictionary<string, string> properties,
            bool emitAssemblyList,
            bool doNotIncludeReferencedProjects,
            bool force,
            bool noBuiltInFederations,
            IReadOnlyDictionary<string, string> offlineFederations,
            IReadOnlyCollection<string> federations,
            IReadOnlyDictionary<string, string> serverPathMappings,
            IReadOnlyDictionary<string, string> repoPathMappings,
            IReadOnlyList<string> pluginBlacklist,
            bool loadPlugins,
            bool excludeTests,
            string rootPath,
            bool includeSourceGeneratedDocuments,
            bool suppressWarnings,
            bool showBranding,
            bool incremental,
            string config,
            IReadOnlyDictionary<string, string> configAxes,
            bool mergeConfigsOnly)
        {
            SolutionDestinationFolder = solutionDestinationFolder;
            Projects = projects;
            Properties = properties;
            EmitAssemblyList = emitAssemblyList;
            DoNotIncludeReferencedProjects = doNotIncludeReferencedProjects;
            Force = force;
            NoBuiltInFederations = noBuiltInFederations;
            OfflineFederations = offlineFederations;
            Federations = federations;
            ServerPathMappings = serverPathMappings;
            RepoPathMappings = repoPathMappings;
            PluginBlacklist = pluginBlacklist;
            LoadPlugins = loadPlugins;
            ExcludeTests = excludeTests;
            RootPath = rootPath;
            IncludeSourceGeneratedDocuments = includeSourceGeneratedDocuments;
            SuppressWarnings = suppressWarnings;
            ShowBranding = showBranding;
            Incremental = incremental;
            Config = config;
            ConfigAxes = configAxes ?? EmptyConfigAxes;
            MergeConfigsOnly = mergeConfigsOnly;
        }

        public string SolutionDestinationFolder { get; }
        public IReadOnlyList<string> Projects { get; }
        public IReadOnlyDictionary<string, string> Properties { get; }
        public bool EmitAssemblyList { get; }
        public bool DoNotIncludeReferencedProjects { get; }
        public bool IncludeSourceGeneratedDocuments { get; }
        public bool Force { get; }
        public bool NoBuiltInFederations { get; }
        public IReadOnlyDictionary<string, string> OfflineFederations { get; }
        public IReadOnlyCollection<string> Federations { get; }
        public IReadOnlyDictionary<string, string> ServerPathMappings { get; }

        /// <summary>
        /// Maps a local source folder (full path, no trailing slash) to a repo display name,
        /// used to optionally tag every assembly generated from a project under that folder so
        /// search can later be scoped to just that repo. Populated via /repoPath: or the /repo:
        /// sugar option. Empty by default, which keeps every assembly untagged.
        /// </summary>
        public IReadOnlyDictionary<string, string> RepoPathMappings { get; }
        public IReadOnlyList<string> PluginBlacklist { get; }
        public bool LoadPlugins { get; }
        public bool ExcludeTests { get; }
        public string RootPath { get; }
        public bool SuppressWarnings { get; }

        /// <summary>
        /// <summary>
        /// Shows the .NET/Microsoft logo marks in the generated site's header. Off by default --
        /// most sites generated with this tool aren't Microsoft's own code, so the branding is
        /// opt-in via /showBranding rather than something every consumer has to remember to hide.
        /// The "Source Browser" title/home link itself is unaffected either way.
        /// </summary>
        public bool ShowBranding { get; }

        /// <summary>
        /// /incremental -- skip regenerating/re-copying assemblies whose Pass1 staleness key is
        /// unchanged since the last run into the same destination folder. See <see cref="ProjectStaleness"/>.
        /// </summary>
        public bool Incremental { get; }

        /// <summary>
        /// /config:&lt;name&gt; -- names this run as one of possibly several configs (e.g. mac/linux/
        /// windows builds of the same sources) being indexed into the same /out. Configs merge into a
        /// single served index rather than partitioning it: symbols/files identical across configs
        /// collapse into shared entries, and only genuinely config-divergent locations/files are
        /// tagged per config. Null (the default) means "no config" -- output is unaffected, byte-for-
        /// byte identical to a run without this flag.
        /// </summary>
        public string Config { get; }

        /// <summary>
        /// /configAxes:&lt;axis&gt;=&lt;value&gt;;&lt;axis&gt;=&lt;value&gt;... -- optional structured tags for this
        /// run's /config:&lt;name&gt; (e.g. /configAxes:os=windows;arch=x64 for a "windows-x64" config), so
        /// the client selector can group configs by axis (OS, Arch, ...) instead of one flat, unstructured
        /// checkbox per config name -- necessary once a real build matrix has more than a couple of
        /// configs (e.g. os x arch). Only meaningful alongside /config:; ignored (and harmless) without
        /// it. Empty (never null) when not given, which is the common/default case and leaves the
        /// config registered exactly as before -- one flat, ungrouped name.
        /// </summary>
        public IReadOnlyDictionary<string, string> ConfigAxes { get; }

        private static readonly IReadOnlyDictionary<string, string> EmptyConfigAxes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// /mergeConfigsOnly -- runs ONLY the cross-config merge step (declarations + references +
        /// file-render dedup) over whatever configs are already registered in this /out's Configs.txt,
        /// without loading MSBuild/running Pass1 at all. This is the standalone merge invocation a
        /// distributed CI aggregation job calls after collecting each per-platform job's obj/&lt;config&gt;
        /// staging as an artifact onto one /out -- it needs no toolchain, only the raw staged output
        /// already on disk. A normal /config:&lt;name&gt; run also performs this same merge step as its
        /// final tail (the "convenience path" for a shared /out / local multi-config run); this flag
        /// just lets it be invoked directly and independently of Pass1.
        /// </summary>
        public bool MergeConfigsOnly { get; }

        public static CommandLineOptions Parse(params string[] args)
        {
            var solutionDestinationFolder = (string)null;
            var projects = new List<string>();
            var properties = new Dictionary<string, string>();
            var emitAssemblyList = false;
            var doNotIncludeReferencedProjects = false;
            var force = false;
            var noBuiltInFederations = false;
            var offlineFederations = new Dictionary<string, string>();
            var federations = new HashSet<string>();
            var serverPathMappings = new Dictionary<string, string>();
            var repoPathMappings = new Dictionary<string, string>();
            var pluginBlacklist = new List<string>();
            var loadPlugins = false;
            var excludeTests = false;
            var includeSourceGeneratedDocuments = true;
            var rootPath = (string)null;
            var suppressWarnings = false;
            var showBranding = false;
            var incremental = false;
            var config = (string)null;
            var configAxes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var mergeConfigsOnly = false;

            foreach (var arg in args)
            {
                if (arg.StartsWith("/out:", StringComparison.Ordinal))
                {
                    solutionDestinationFolder = Path.GetFullPath(arg.Substring("/out:".Length).StripQuotes());
                    continue;
                }

                if (arg.StartsWith("/serverPath:", StringComparison.Ordinal))
                {
                    // Allowed forms:
                    // /serverPath:a=b
                    // /serverPath:"a=b" (for backwards compatibility)
                    // /serverPath:"a=1"="b=2"
                    // /serverPath:"a"="b" (for consistency to make it easy to produce a safe form)

                    var match = Regex.Match(
                        arg.Substring("/serverPath:".Length),
                        @"\A(?:
                            # Each side may be quoted or unquoted but may only contain '=' if quoted
                            (?:(?<from>[^""=]*)|""(?<from>[^""]*)"")=(?:(?<to>[^""=]*)|""(?<to>[^""]*)"")
                            |
                            # Backwards compatibility, not advertised as an option because it doesn't allow '=' even though there are quotes
                            ""(?<from>[^""=]*)=(?<to>[^""=]*)""
                        )\Z", RegexOptions.IgnorePatternWhitespace);

                    if (!match.Success)
                    {
                        Log.Write("Server path argument usage: /serverPath:\"path to local repository root\"=\"root URL\"" + Environment.NewLine +
                                  "Quotes are optional if you have no spaces or equals signs but recommended. Paths relative to the local repository root will be appended to the root URL.", ConsoleColor.Red);
                        continue;
                    }

                    serverPathMappings[Path.GetFullPath(match.Groups["from"].Value)] = match.Groups["to"].Value;
                    continue;
                }

                if (arg.StartsWith("/repoPath:", StringComparison.Ordinal))
                {
                    // Tags every project under the given local folder with a repo display name,
                    // so search can optionally be scoped to that repo. Allowed forms mirror
                    // /serverPath:
                    // /repoPath:a=b
                    // /repoPath:"a=1"="b=2"
                    // /repoPath:"a"="b"
                    var match = Regex.Match(
                        arg.Substring("/repoPath:".Length),
                        @"\A(?:
                            (?:(?<from>[^""=]*)|""(?<from>[^""]*)"")=(?:(?<name>[^""=]*)|""(?<name>[^""]*)"")
                        )\Z", RegexOptions.IgnorePatternWhitespace);

                    if (!match.Success)
                    {
                        Log.Write("Repo path argument usage: /repoPath:\"path to local repository root\"=\"repo display name\"" + Environment.NewLine +
                                  "Quotes are optional if you have no spaces or equals signs but recommended.", ConsoleColor.Red);
                        continue;
                    }

                    repoPathMappings[Path.GetFullPath(match.Groups["from"].Value)] = match.Groups["name"].Value;
                    continue;
                }

                if (arg.StartsWith("/repo:", StringComparison.Ordinal))
                {
                    // Sugar for the common case of specifying a repo's local folder, display name,
                    // and server URL together in one flag: equivalent to specifying both
                    // /repoPath:"folder"="name" and /serverPath:"folder"="url".
                    // /repo:"path to local repository root"="repo display name"="root URL"
                    var match = Regex.Match(
                        arg.Substring("/repo:".Length),
                        @"\A(?:
                            (?:(?<from>[^""=]*)|""(?<from>[^""]*)"")=(?:(?<name>[^""=]*)|""(?<name>[^""]*)"")=(?:(?<to>[^""=]*)|""(?<to>[^""]*)"")
                        )\Z", RegexOptions.IgnorePatternWhitespace);

                    if (!match.Success)
                    {
                        Log.Write("Repo argument usage: /repo:\"path to local repository root\"=\"repo display name\"=\"root URL\"" + Environment.NewLine +
                                  "Quotes are optional if you have no spaces or equals signs but recommended.", ConsoleColor.Red);
                        continue;
                    }

                    var repoFolder = Path.GetFullPath(match.Groups["from"].Value);
                    repoPathMappings[repoFolder] = match.Groups["name"].Value;
                    serverPathMappings[repoFolder] = match.Groups["to"].Value;
                    continue;
                }

                if (arg == "/force")
                {
                    force = true;
                    continue;
                }

                if (string.Equals(arg, "/incremental", StringComparison.OrdinalIgnoreCase))
                {
                    incremental = true;
                    continue;
                }

                if (arg.StartsWith("/config:", StringComparison.OrdinalIgnoreCase))
                {
                    config = arg.Substring("/config:".Length).StripQuotes();
                    continue;
                }

                if (arg.StartsWith("/configAxes:", StringComparison.OrdinalIgnoreCase))
                {
                    // /configAxes:os=windows;arch=x64
                    var axesField = arg.Substring("/configAxes:".Length).StripQuotes();
                    foreach (var pair in axesField.Split(';'))
                    {
                        if (string.IsNullOrWhiteSpace(pair))
                        {
                            continue;
                        }

                        var equalsIndex = pair.IndexOf('=');
                        if (equalsIndex <= 0)
                        {
                            Log.Write("Invalid /configAxes: entry (expected axis=value): " + pair, ConsoleColor.Red);
                            continue;
                        }

                        var axisName = pair.Substring(0, equalsIndex).Trim();
                        var axisValue = pair.Substring(equalsIndex + 1).Trim();
                        configAxes[axisName] = axisValue;
                    }
                    continue;
                }

                if (string.Equals(arg, "/mergeConfigsOnly", StringComparison.OrdinalIgnoreCase))
                {
                    mergeConfigsOnly = true;
                    continue;
                }

                if (arg.StartsWith("/in:", StringComparison.Ordinal))
                {
                    string inputPath = arg.Substring("/in:".Length).StripQuotes();
                    try
                    {
                        if (!File.Exists(inputPath))
                        {
                            continue;
                        }

                        string[] paths = File.ReadAllLines(inputPath);
                        foreach (string path in paths)
                        {
                            AddProject(projects, path);
                        }
                    }
                    catch
                    {
                        Log.Write("Invalid argument: " + arg, ConsoleColor.Red);
                    }

                    continue;
                }

                if (arg.StartsWith("/p:", StringComparison.Ordinal))
                {
                    var match = Regex.Match(arg, "/p:(?<name>[^=]+)=(?<value>.+)");
                    if (match.Success)
                    {
                        var propertyName = match.Groups["name"].Value;
                        var propertyValue = match.Groups["value"].Value;
                        properties.Add(propertyName, propertyValue);
                        Log.Message($"Adding property {propertyName}={propertyValue}");
                        continue;
                    }
                }

                if (arg == "/assemblylist")
                {
                    emitAssemblyList = true;
                    continue;
                }

                if (string.Equals(arg, "/donotincludereferencedprojects", StringComparison.OrdinalIgnoreCase))
                {
                    doNotIncludeReferencedProjects = true;
                    continue;
                }

                if (arg == "/nobuiltinfederations")
                {
                    noBuiltInFederations = true;
                    Log.Message("Disabling built-in federations.");
                    continue;
                }

                if (arg.StartsWith("/federation:", StringComparison.Ordinal))
                {
                    string server = arg.Substring("/federation:".Length);
                    Log.Message($"Adding federation '{server}'.");
                    federations.Add(server);
                    continue;
                }

                if (arg.StartsWith("/offlinefederation:", StringComparison.Ordinal))
                {
                    var match = Regex.Match(arg, "/offlinefederation:(?<server>[^=]+)=(?<file>.+)");
                    if (match.Success)
                    {
                        var server = match.Groups["server"].Value;
                        var assemblyListFileName = match.Groups["file"].Value;
                        offlineFederations[server] = assemblyListFileName;
                        Log.Message($"Adding federation '{server}' (offline from '{assemblyListFileName}').");
                    }
                    continue;
                }

                if (string.Equals(arg, "/noplugins", StringComparison.OrdinalIgnoreCase))
                {
                    loadPlugins = false;
                    continue;
                }

                if (string.Equals(arg, "/useplugins", StringComparison.OrdinalIgnoreCase))
                {
                    loadPlugins = true;
                    continue;
                }

                if (arg.StartsWith("/noplugin:", StringComparison.Ordinal))
                {
                    pluginBlacklist.Add(arg.Substring("/noplugin:".Length));
                    continue;
                }

                if (arg == "/excludetests")
                {
                    excludeTests = true;
                    continue;
                }

                if (arg == "/excludeSourceGeneratedDocuments")
                {
                    includeSourceGeneratedDocuments = false;
                    continue;
                }

                if (string.Equals(arg, "/noWarnings", StringComparison.OrdinalIgnoreCase))
                {
                    suppressWarnings = true;
                    continue;
                }

                if (arg.StartsWith("/root:", StringComparison.Ordinal))
                {
                    rootPath = Path.GetFullPath(arg.Substring("/root:".Length).StripQuotes());
                    continue;
                }

                if (string.Equals(arg, "/showBranding", StringComparison.OrdinalIgnoreCase))
                {
                    showBranding = true;
                    continue;
                }

                try
                {
                    AddProject(projects, arg);
                }
                catch (Exception ex)
                {
                    Log.Write("Exception: " + ex, ConsoleColor.Red);
                }
            }

            if (rootPath is object)
            {
                foreach (var project in projects)
                {
                    if (!Paths.IsOrContains(rootPath, project))
                    {
                        Log.Exception("If /root is specified, it must be an ancestor folder of all specified projects.", isSevere: true);
                        projects.Clear();
                        break;
                    }
                }
            }

            return new CommandLineOptions(
                solutionDestinationFolder,
                projects,
                properties,
                emitAssemblyList,
                doNotIncludeReferencedProjects,
                force,
                noBuiltInFederations,
                offlineFederations,
                federations,
                serverPathMappings,
                repoPathMappings,
                pluginBlacklist,
                loadPlugins,
                excludeTests,
                rootPath,
                includeSourceGeneratedDocuments,
                suppressWarnings,
                showBranding,
                incremental,
                config,
                configAxes,
                mergeConfigsOnly);
        }

        private static void AddProject(List<string> projects, string path)
        {
            var project = Path.GetFullPath(path);
            if (IsSupportedProject(project))
            {
                projects.Add(project);
            }
            else
            {
                Log.Exception("Project not found or not supported: " + path, isSevere: false);
            }
        }

        private static bool IsSupportedProject(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            return filePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".binlog", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".buildlog", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }
    }
}
