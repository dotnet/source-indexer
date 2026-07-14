using System.Collections.Generic;

namespace Microsoft.SourceBrowser.MEF
{
    public interface ISourceBrowserPlugin
    {
        string Name { get; }
        void Init(Dictionary<string, string> configuration, ILog logger);
        IEnumerable<ISymbolVisitor> ManufactureSymbolVisitors(string projectPath);
        IEnumerable<ITextVisitor> ManufactureTextVisitors(string projectPath);
    }
}
