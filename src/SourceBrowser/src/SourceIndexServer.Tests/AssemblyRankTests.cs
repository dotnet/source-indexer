using System.Collections.Generic;
using Microsoft.SourceBrowser.Common;
using Microsoft.SourceBrowser.SourceIndexServer;
using Microsoft.SourceBrowser.SourceIndexServer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    [TestClass]
    public class AssemblyRankTests
    {
        [TestMethod]
        [DataRow("System.Private.CoreLib", 0)]
        [DataRow("System.Runtime", 0)]
        [DataRow("mscorlib", 0)]
        [DataRow("System", 1)]
        [DataRow("System.Collections", 1)]
        [DataRow("System.Text.Json", 1)]
        [DataRow("Microsoft.CodeAnalysis", 2)]
        [DataRow("TerraFX.Interop.Windows", 2)]
        public void GetAssemblyRankReturnsExpectedTier(string assemblyName, int expectedRank)
        {
            Assert.AreEqual(expectedRank, DeclaredSymbolInfo.GetAssemblyRank(assemblyName));
        }

        [TestMethod]
        public void GetAssemblyRankTreatsNullAsLowest()
        {
            Assert.AreEqual(3, DeclaredSymbolInfo.GetAssemblyRank(null));
        }

        [TestMethod]
        public void SystemDoesNotMatchArbitrarySystemPrefixedName()
        {
            // `SystemFoo` is not the `System` assembly nor a `System.*` assembly.
            Assert.AreEqual(2, DeclaredSymbolInfo.GetAssemblyRank("SystemFoo"));
        }

        [TestMethod]
        public void SystemAssemblyRanksAboveNonSystemForEqualMatch()
        {
            // Same name, same kind, equal match level. The only differentiator is the
            // assembly, and the lower-numbered assembly (`Microsoft.CodeAnalysis`) would
            // win the legacy tiebreak. Ranking must flip `System.Collections` to the top.
            var descriptions = new[]
            {
                "Microsoft.CodeAnalysis.Dictionary",
                "System.Collections.Generic.Dictionary"
            };

            var huffman = Huffman.Create(descriptions);

            using var index = new Index();
            index.indexFinishedPopulating = true;
            index.huffman = huffman;
            index.assemblies = new List<AssemblyInfo>
            {
                new AssemblyInfo { AssemblyName = "Microsoft.CodeAnalysis", ProjectKey = -1 },
                new AssemblyInfo { AssemblyName = "System.Collections", ProjectKey = -1 }
            };
            index.symbols = new List<IndexEntry>
            {
                new IndexEntry("Dictionary", huffman.CompressToNative(descriptions[0])) { AssemblyNumber = 0, ID = 1 },
                new IndexEntry("Dictionary", huffman.CompressToNative(descriptions[1])) { AssemblyNumber = 1, ID = 2 }
            };
            index.PopulateSymbolsById();

            var results = index.Get("Dictionary").ResultSymbols;

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual("System.Collections", results[0].AssemblyName);
            Assert.AreEqual("Microsoft.CodeAnalysis", results[1].AssemblyName);
        }
    }
}
