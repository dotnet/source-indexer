using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Microsoft.SourceBrowser.MEF
{
    public class PluginAggregator : IReadOnlyCollection<SourceBrowserPluginWrapper>, IDisposable
    {
        private List<SourceBrowserPluginWrapper> Plugins;
        private ILog Logger;

        private Dictionary<string, Dictionary<string, string>> PluginConfigurations;

        public int Count => Plugins.Count;

        // Plugins are now registered explicitly by the host rather than discovered at runtime via a MEF
        // DirectoryCatalog. The blacklist still lets a run drop a plugin by name (e.g. /noplugin:Git).
        public PluginAggregator(IEnumerable<ISourceBrowserPlugin> plugins, Dictionary<string, Dictionary<string, string>> pluginConfigurations, ILog logger, IEnumerable<string> blackList)
        {
            PluginConfigurations = pluginConfigurations;
            Logger = logger;

            var blackListSet = new HashSet<string>(blackList ?? Array.Empty<string>());

            Plugins = plugins
            .Select(plugin => new SourceBrowserPluginWrapper(plugin, Logger))
            .Where(w => !blackListSet.Contains(w.Name))
            .ToList();
        }

        public void Init()
        {
            foreach (var plugin in Plugins)
            {
                if (!PluginConfigurations.TryGetValue(plugin.Name, out Dictionary<string, string> config))
                {
                    config = new Dictionary<string, string>();
                }
                plugin.Init(config, Logger);
            }
        }

        public IEnumerable<ISymbolVisitor> ManufactureSymbolVisitors(Project project)
        {
            return Plugins.SelectMany(p => p.ManufactureSymbolVisitors(project.FilePath));
        }

        private IEnumerable<ISymbolVisitor> ManufactureSymbolVisitors(string name, ISourceBrowserPlugin plugin, Project project)
        {
            try
            {
                return plugin.ManufactureSymbolVisitors(project.FilePath);
            }
            catch (Exception ex)
            {
                Logger.Info(name + " Plugin failed to manufacture symbol visitors", ex);
                return Enumerable.Empty<ISymbolVisitor>();
            }
        }

        public IEnumerable<ITextVisitor> ManufactureTextVisitors(Project project)
        {
            return Plugins.SelectMany(p => p.ManufactureTextVisitors(project.FilePath));
        }

        public void Dispose()
        {
            foreach (var plugin in Plugins)
            {
                plugin.Dispose();
            }
        }

        public IEnumerator<SourceBrowserPluginWrapper> GetEnumerator() => Plugins.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => Plugins.GetEnumerator();
    }
}
