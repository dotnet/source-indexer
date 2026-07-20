using System;
using System.Collections.Generic;
using LibGit2Sharp;
using Microsoft.SourceBrowser.MEF;

namespace GitGlyph
{
    public class GitSourceBrowserPlugin : ISourceBrowserPlugin, IDisposable
    {
        public string Name => "Git";

        private ILog Logger { get; set; }
        private readonly List<Repository> repositoriesToDispose;

        public GitSourceBrowserPlugin()
        {
            repositoriesToDispose = new List<Repository>();
        }

        public void Dispose()
        {
            foreach (var r in repositoriesToDispose)
            {
                r.Dispose();
            }
        }

        public void Init(Dictionary<string, string> Configuration, ILog logger)
        {
            Logger = logger;
        }

        public IEnumerable<ISymbolVisitor> ManufactureSymbolVisitors(string projectPath)
        {
            yield break;
        }

        public IEnumerable<ITextVisitor> ManufactureTextVisitors(string projectPath)
        {
            var path = Repository.Discover(projectPath);
            if (path == null)
            {
                Logger.Warning("Cannot find git repo");
            }
            else
            {
                Repository r = new Repository(path);
                repositoriesToDispose.Add(r);
                yield return new GitBlameVisitor(r, Logger);
            }
        }
    }
}
