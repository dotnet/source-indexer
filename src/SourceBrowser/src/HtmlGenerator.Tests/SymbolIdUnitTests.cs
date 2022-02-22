using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    [TestClass]
    public class SymbolIdUnitTests
    {
        [TestMethod]
        public void TestHash()
        {
            var symbolId = "T:Microsoft.CodeAnalysis.CSharp.Symbols.SourceNamedTypeSymbol";
            var bytes = Paths.GetSHA2Hash(symbolId, 16);
            Assert.AreEqual("e057f27c02e5f77e", bytes);
        }
    }
}
