using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SourceBrowser.SourceIndexServer;
using Microsoft.SourceBrowser.SourceIndexServer.Controllers;
using Microsoft.SourceBrowser.SourceIndexServer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Index = Microsoft.SourceBrowser.SourceIndexServer.Models.Index;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    [TestClass]
    public class SymbolsControllerTests
    {
        [TestMethod]
        public void SymbolRedirectUsesIndexedImplementationAssembly()
        {
            const string symbolIdText = "8281103e6f23cb5c";
            Assert.IsTrue(SymbolsController.TryParseHexStringToULong(symbolIdText, out ulong symbolId));
            using var index = new Index();
            index.assemblies.Add(new AssemblyInfo("System.Private.CoreLib;-1;0"));
            index.symbols.Add(new IndexEntry(new DeclaredSymbolInfo
            {
                AssemblyNumber = 0,
                ID = symbolId,
                Name = "String"
            }));
            index.PopulateSymbolsById();

            using var provider = new ServiceCollection()
                .AddSingleton(index)
                .BuildServiceProvider();
            var controller = new SymbolsController(provider);

            var result = controller.RedirectToSymbol(symbolIdText) as LocalRedirectResult;

            Assert.IsNotNull(result);
            Assert.AreEqual(
                "/System.Private.CoreLib/A.html#8281103e6f23cb5c",
                result.Url);
        }
    }
}
