using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    /// <summary>
    /// One registered config's name plus its optional structured axis tags (e.g. os=windows,
    /// arch=x64 for a "windows-x64" config) -- see <see cref="ConfigRegistry"/>'s remarks for why
    /// these are tracked at all. <see cref="AxisTags"/> is empty (never null) for a config
    /// registered without any /configAxes:, which is the common/default case.
    /// </summary>
    public sealed class ConfigRegistryEntry
    {
        public ConfigRegistryEntry(string name, IReadOnlyDictionary<string, string> axisTags)
        {
            Name = name;
            AxisTags = axisTags ?? EmptyAxisTags;
        }

        public string Name { get; }
        public IReadOnlyDictionary<string, string> AxisTags { get; }

        internal static readonly IReadOnlyDictionary<string, string> EmptyAxisTags =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maintains the shared, root-level Configs.txt that records every /config:&lt;name&gt; indexed into a
    /// given /out root. Separate /config:&lt;name&gt; invocations commonly run concurrently against the
    /// same /out (e.g. one process per platform build), so the read-modify-write-add-if-missing here is
    /// guarded by a named mutex and written via a temp file + atomic rename -- two concurrent runs can
    /// never lose each other's entry or observe a partially-written file.
    ///
    /// A real build matrix is usually multi-axis (e.g. os x arch, not just one flat dimension), so each
    /// line optionally carries structured "axis=value;axis=value" tags (from /configAxes:) after a tab,
    /// e.g. "windows-x64\tos=windows;arch=x64" -- letting the client group its selector by axis (OS,
    /// Arch, ...) instead of one flat, unstructured checkbox per config name, which stops scaling once a
    /// site registers more than a couple of configs. A line with no tab (or no /configAxes: given at
    /// registration) has no axis tags at all -- the client falls back to treating it as one flat,
    /// ungrouped entry, so this is purely additive: existing single-axis or axis-less callers/configs
    /// are unaffected.
    /// </summary>
    public static class ConfigRegistry
    {
        public const string ConfigsFileName = "Configs.txt";

        private const char AxisTagFieldSeparator = '\t';
        private const char AxisPairSeparator = ';';
        private const char AxisKeyValueSeparator = '=';

        /// <summary>
        /// Adds <paramref name="configName"/> (with optional <paramref name="axisTags"/>) to
        /// outRoot/Configs.txt if it isn't already present. Does nothing when
        /// <paramref name="configName"/> is null/empty, so a default (no /config) run never creates
        /// Configs.txt and leaves the output tree exactly as before. If the config is already
        /// registered, its axis tags are left as first-registered (concurrent same-name registrations
        /// with different tags shouldn't happen in practice -- one config name is one point in the
        /// build matrix -- so the first writer wins rather than silently flip-flopping).
        /// </summary>
        public static void EnsureConfigRegistered(string outRoot, string configName, IReadOnlyDictionary<string, string> axisTags = null)
        {
            if (string.IsNullOrEmpty(configName))
            {
                return;
            }

            Directory.CreateDirectory(outRoot);
            var configsFilePath = Path.Combine(outRoot, ConfigsFileName);

            using (var mutex = ConfigMergeCoordinator.CreateNamedMutex(configsFilePath))
            {
                mutex.WaitOne();
                try
                {
                    AddConfigIfMissing(configsFilePath, configName, axisTags);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        /// <summary>
        /// Reads the currently-registered config NAMES for a given /out root (axis tags dropped). Returns
        /// an empty list if no config has ever been registered (default/no-config runs never create the
        /// file). Kept alongside <see cref="GetRegisteredConfigEntries"/> since the large majority of
        /// existing callers only need names, not axis metadata.
        /// </summary>
        public static IReadOnlyList<string> GetRegisteredConfigs(string outRoot)
        {
            return GetRegisteredConfigEntries(outRoot).Select(e => e.Name).ToList();
        }

        /// <summary>
        /// Reads the currently-registered configs, each with its (possibly empty) axis tags, for a given
        /// /out root. Returns an empty list if no config has ever been registered.
        /// </summary>
        public static IReadOnlyList<ConfigRegistryEntry> GetRegisteredConfigEntries(string outRoot)
        {
            var configsFilePath = Path.Combine(outRoot, ConfigsFileName);
            if (!File.Exists(configsFilePath))
            {
                return Array.Empty<ConfigRegistryEntry>();
            }

            return File.ReadAllLines(configsFilePath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(ParseLine)
                .ToList();
        }

        private static ConfigRegistryEntry ParseLine(string line)
        {
            var tabIndex = line.IndexOf(AxisTagFieldSeparator);
            if (tabIndex < 0)
            {
                return new ConfigRegistryEntry(line, null);
            }

            var name = line.Substring(0, tabIndex);
            var axisTagsField = line.Substring(tabIndex + 1);
            var axisTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in axisTagsField.Split(AxisPairSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var equalsIndex = pair.IndexOf(AxisKeyValueSeparator);
                if (equalsIndex <= 0)
                {
                    continue;
                }

                var axisName = pair.Substring(0, equalsIndex);
                var axisValue = pair.Substring(equalsIndex + 1);
                axisTags[axisName] = axisValue;
            }

            return new ConfigRegistryEntry(name, axisTags);
        }

        private static string FormatLine(ConfigRegistryEntry entry)
        {
            if (entry.AxisTags.Count == 0)
            {
                return entry.Name;
            }

            var axisTagsField = string.Join(
                AxisPairSeparator,
                entry.AxisTags.Select(kvp => kvp.Key + AxisKeyValueSeparator + kvp.Value));
            return entry.Name + AxisTagFieldSeparator + axisTagsField;
        }

        private static void AddConfigIfMissing(string configsFilePath, string configName, IReadOnlyDictionary<string, string> axisTags)
        {
            var entries = File.Exists(configsFilePath)
                ? File.ReadAllLines(configsFilePath).Where(l => !string.IsNullOrWhiteSpace(l)).Select(ParseLine).ToList()
                : new List<ConfigRegistryEntry>();

            if (entries.Any(e => string.Equals(e.Name, configName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            entries.Add(new ConfigRegistryEntry(configName, axisTags));

            var tempFilePath = configsFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllLines(tempFilePath, entries.Select(FormatLine));
            File.Move(tempFilePath, configsFilePath, overwrite: true);
        }
    }
}
