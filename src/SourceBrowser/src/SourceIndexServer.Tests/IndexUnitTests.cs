using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.SourceBrowser.Common;
using Microsoft.SourceBrowser.SourceIndexServer;
using Microsoft.SourceBrowser.SourceIndexServer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    [TestClass]
    public class IndexUnitTests
    {
        [TestMethod]
        public void Test()
        {
            Test(
                new[] { "a" },
                "bbb",
                new string[0]);
        }

        [TestMethod]
        public void Test2()
        {
            Test(
                new[] { "aaa" },
                "aaa",
                new[] { "aaa" });
        }

        [TestMethod]
        public void Test3()
        {
            Test(
                new[] { "aaa", "bbb" },
                "aaa",
                new[] { "aaa" });
        }

        [TestMethod]
        public void Test4()
        {
            Test(
                new[] { "aaa", "bbb" },
                "bbb",
                new[] { "bbb" });
        }

        [TestMethod]
        public void Test5()
        {
            Test(
                new[] { "aa", "aaa" },
                "aaa",
                new[] { "aaa" });
        }

        [TestMethod]
        public void Test6()
        {
            Test(
                new[] { "aaa", "aaa" },
                "aaa",
                new[] { "aaa", "aaa" });
        }

        [TestMethod]
        public void Test7()
        {
            Test(
                new[] { "aaa", "aba" },
                "aab",
                new string[0]);
        }

        [TestMethod]
        public void TestGotoSpaceStripQuotes()
        {
            Test(new[] { "a" }, "\"b", new string[0]);
            Test(new[] { "a" }, "\"b\"", new string[0]);
            Test(new[] { "a" }, "\"a \"", new string[0]);
        }

        [TestMethod]
        public void TestE2E2()
        {
            Test(
                new[] { "a", "b", "bin", "bin", "c", "z" },
                "bin",
                new[] { "bin", "bin" });
        }

        [TestMethod]
        public void TestE2E3()
        {
            Test(
                new[] { "a", "b", "bin", "binary", "c", "z" },
                "binary",
                new[] { "binary" });
        }

        // https://github.com/KirillOsenkov/SourceBrowser/issues/29 -- "NeCl" is the issue's own
        // example, and matches neither "MyNewClass" nor "NewClass" via prefix (nor even via the
        // SortedSearch bounds check, since 'C' doesn't line up with "NewClass"[2] == 'w') -- both
        // are only found through the unified fuzzy (boundary-aware subsequence) fallback.
        [TestMethod]
        public void TestCamelCaseSearch_MatchesIssue29Example()
        {
            Test(
                new[] { "MyNewClass", "NewClass", "SomethingElse" },
                "NeCl",
                new[] { "MyNewClass", "NewClass" });
        }

        [TestMethod]
        public void TestCamelCaseSearch_SkipsLeadingHump()
        {
            Test(
                new[] { "MyNewClass", "Unrelated" },
                "NeCl",
                new[] { "MyNewClass" });
        }

        [TestMethod]
        public void TestSubstringAnywhereSearch()
        {
            Test(
                new[] { "unrelated", "xxwidgetxx" },
                "widget",
                new[] { "xxwidgetxx" });
        }

        [TestMethod]
        public void TestVerbatimQuery_DoesNotUseCamelOrSubstringFallback()
        {
            Test(
                new[] { "MyNewClass", "NewClass" },
                "\"NeCl\"",
                new string[0]);
        }

        // Exact/prefix must always outrank a fuzzy (camelCase-hump-boundary or plain substring)
        // match -- regardless of Name-sort order among the candidates. Within the fuzzy band, a
        // camelCase-hump-boundary-aligned match ("MyWidgetFactory", where "widget" starts right at
        // the "Widget" hump) scores higher than an arbitrary mid-word substring match
        // ("xxwidgetxx") via SymbolNameMatcher.TryScoreFuzzy's boundary bonus, so it still ranks
        // above it -- the same single scorer produces both outcomes.
        [TestMethod]
        public void TestRankingOrder_ExactBeatsPrefixBeatsCamelBeatsSubstring()
        {
            Test(
                new[] { "MyWidgetFactory", "widget", "widgetFactory", "xxwidgetxx" },
                "widget",
                new[] { "widget", "widgetFactory", "MyWidgetFactory", "xxwidgetxx" });
        }

        // Phase-4 perf-gate regression: once the prefix pass has already filled the MaxRawResults
        // display cap with prefix-or-better matches, the fuzzy fallback scan is skipped entirely
        // for the rest of this interpretation -- see the prefixOrBetterCount guard in
        // Index.FindSymbols. This asserts the *result* is unaffected by that skip: the
        // fallback-only candidates never appear, exactly as if the (skipped) scan had actually run
        // and correctly ranked them below the cutoff.
        [TestMethod]
        public void TestPrefixCapFill_SkipsFallbackScanWithoutChangingResults()
        {
            var prefixHits = Enumerable.Range(0, Index.MaxRawResults)
                .Select(i => "Widget" + i.ToString("D3"))
                .ToArray();

            // "xxWidgetxx" is substring-only, "MyWidgetFactory" is camelCase-hump-only -- neither
            // is a prefix match for "Widget", so both would only ever be found by the fallback
            // scan the guard is meant to skip.
            var fallbackOnly = new[] { "xxWidgetxx", "MyWidgetFactory" };

            var input = prefixHits
                .Concat(fallbackOnly)
                .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Test(input, "Widget", prefixHits);
        }

        // Regression for a truncate-before-rank bug in the fuzzy fallback scan itself:
        // MaxCandidatesPerPass used to stop the SCAN after collecting MaxCandidatesPerPass fuzzy
        // matches, in Name-sorted (scan) order -- but the fuzzy tier ranks by a *continuous*
        // score (SymbolNameMatcher.TryScoreFuzzy), not scan order, so a genuinely higher-scoring
        // match that happens to sort alphabetically after more than MaxCandidatesPerPass weaker
        // matches would never even be scored, let alone ranked, and would silently vanish from
        // the results. Build more low-scoring fuzzy matches than MaxCandidatesPerPass, all
        // sorting before a single high-scoring match ("NewClass", the issue's own #29 example)
        // for the same query, and assert the high-scoring match still surfaces. This must fail
        // against the version of Index.FindSymbols that capped the fuzzy scan itself and pass
        // once every prune-surviving candidate is scored (with only the final, globally-ranked
        // result list truncated to MaxRawResults).
        [TestMethod]
        public void TestFuzzyFallback_DoesNotTruncateBeforeRankingAcrossManyCandidates()
        {
            int fillerCount = Index.MaxCandidatesPerPass + 500;

            // Each filler is a valid but weak subsequence match for "NeCl": the letters appear
            // lower-case, consecutively, with no camelCase-hump/start-of-string boundary and no
            // case agreement -- so it scores well below "NewClass", which matches "NeCl" with a
            // start-of-string bonus, a hump-boundary bonus at "Class", and full case agreement.
            var filler = Enumerable.Range(0, fillerCount)
                .Select(i => $"Aaa{i:D6}necl")
                .ToArray();

            var input = filler
                .Concat(new[] { "NewClass" })
                .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase)
                .ToArray();

            using (var index = new Index())
            {
                index.symbols = new List<IndexEntry>(input.Select(s => new IndexEntry(s)));
                index.PopulateSymbolsById();

                var foundSymbols = index.FindSymbols("NeCl");

                Assert.IsNotNull(foundSymbols);
                Assert.IsTrue(
                    foundSymbols.Any(s => s.Name == "NewClass"),
                    "The higher-scoring fuzzy match 'NewClass' must not be dropped just because " +
                    "more than MaxCandidatesPerPass weaker fuzzy matches sort alphabetically before it.");
            }
        }

        public class EntryList : List<KeyValuePair<string, string>>
        {
            public void Add(string name, string description)
            {
                Add(new KeyValuePair<string, string>(name, description));
            }
        }

        [TestMethod]
        public void TestNamespaceSearch1()
        {
            Test(
                new EntryList
                {
                    { "Console", "System.Console" },
                    { "Console", "Foo.Console" }
                },
                "System.Con",
                "System.Console");
        }

        [TestMethod]
        public void TestNamespaceSearch2()
        {
            Test(
                new EntryList
                {
                    { "Console", "System.Console" },
                    { "Console", "System.Foo.Console" }
                },
                "System.Con",
                "System.Console");
        }

        [TestMethod]
        public void TestSortingOfResultsWithDottedQuery()
        {
            Test(
                new EntryList
                {
                    { "ConsoleSpecialKey", "System.ConsoleSpecialKey" },
                    { "Console", "System.Console" }
                },
                "System.Console",
                "System.Console",
                "System.ConsoleSpecialKey");
        }

        [TestMethod]
        public void TestStringFormatCurlies()
        {
            EndToEnd("{ab}",
                @"<div class=""note"">No results found</div>
<p>Try also searching on:</p>
<ul>
<li><a href=""http://stackoverflow.com/search?q=%7bab%7d"" target=""_blank"">http://stackoverflow.com/search?q=%7bab%7d</a></li>
<li><a href=""http://social.msdn.microsoft.com/Search/en-US?query=%7bab%7d"" target=""_blank"">http://social.msdn.microsoft.com/Search/en-US?query=%7bab%7d</a></li>
<li><a href=""https://www.google.com/search?q=%7bab%7d"" target=""_blank"">https://www.google.com/search?q=%7bab%7d</a></li>
<li><a href=""http://www.bing.com/search?q=%7bab%7d"" target=""_blank"">http://www.bing.com/search?q=%7bab%7d</a></li>
</ul>
");
        }

        [TestMethod]
        public void TestHex()
        {
            var foo = Serialization.ULongToHexString(12199771775727863114);
            var res = SourceIndexServer.Controllers.SymbolsController.TryParseHexStringToULong(foo, out var result);
            foo = Serialization.ULongToHexString(5369725591829040809);
            res = SourceIndexServer.Controllers.SymbolsController.TryParseHexStringToULong(foo, out result);
        }

        [TestMethod]
        public void TestFilteringByOtherWords()
        {
            Test(
                new EntryList
                {
                    { "SourceNamedTypeSymbol", "Roslyn.Compilers.CSharp.SourceNamedTypeSymbol" },
                    { "SourceNamespaceSymbol", "Roslyn.Compilers.CSharp.SourceNamespaceSymbol" },
                    { "SourceFolder", "Roslyn.Compilers.SourceFolder" }
                },
                "Source Symbol",
                "Roslyn.Compilers.CSharp.SourceNamedTypeSymbol",
                "Roslyn.Compilers.CSharp.SourceNamespaceSymbol");
        }

        [TestMethod]
        public void TestRepoFilter_ScopesResultsToTheSelectedRepo()
        {
            using (var index = new Index())
            {
                var testData = new List<DeclaredSymbolInfo>
                {
                    new DeclaredSymbolInfo { Name = "WidgetA", Description = "A.WidgetA", AssemblyNumber = 0 },
                    new DeclaredSymbolInfo { Name = "WidgetB", Description = "B.WidgetB", AssemblyNumber = 1 },
                };

                var huffman = Huffman.Create(testData.Select(d => d.Description));
                index.indexFinishedPopulating = true;
                index.huffman = huffman;
                index.symbols = testData.Select(dsi => new IndexEntry(dsi)).ToList();
                index.PopulateSymbolsById();
                index.assemblies = new List<AssemblyInfo>
                {
                    new AssemblyInfo { AssemblyName = "A", ProjectKey = 0, RepoName = "clangsharp", SolutionName = "ClangSharp" },
                    new AssemblyInfo { AssemblyName = "B", ProjectKey = 1, RepoName = "llvmsharp", SolutionName = "LLVMSharp" },
                };

                var unfiltered = index.Get("Widget");
                Assert.AreEqual(2, unfiltered.ResultSymbols.Count, "No repo filter must reproduce today's unified, all-repos results.");

                var filtered = index.Get("repo:clangsharp Widget");
                CollectionAssert.AreEqual(new[] { "WidgetA" }, filtered.ResultSymbols.Select(s => s.Name).ToArray());

                var filteredBySolution = index.Get("solution:LLVMSharp Widget");
                CollectionAssert.AreEqual(new[] { "WidgetB" }, filteredBySolution.ResultSymbols.Select(s => s.Name).ToArray());

                var noMatch = index.Get("repo:doesnotexist Widget");
                Assert.AreEqual(0, noMatch.ResultSymbols.Count);
            }
        }

        [TestMethod]
        public void TestRepoFilter_ParentRepoIncludesItsNestedSubRepos()
        {
            using (var index = new Index())
            {
                var testData = new List<DeclaredSymbolInfo>
                {
                    new DeclaredSymbolInfo { Name = "WidgetVmr", Description = "V.WidgetVmr", AssemblyNumber = 0 },
                    new DeclaredSymbolInfo { Name = "WidgetSubX", Description = "X.WidgetSubX", AssemblyNumber = 1 },
                    new DeclaredSymbolInfo { Name = "WidgetOther", Description = "O.WidgetOther", AssemblyNumber = 2 },
                };

                var huffman = Huffman.Create(testData.Select(d => d.Description));
                index.indexFinishedPopulating = true;
                index.huffman = huffman;
                index.symbols = testData.Select(dsi => new IndexEntry(dsi)).ToList();
                index.PopulateSymbolsById();
                index.assemblies = new List<AssemblyInfo>
                {
                    new AssemblyInfo { AssemblyName = "V", ProjectKey = 0, RepoName = "dotnet/vmr", SolutionName = "Vmr", RepoChain = new[] { "dotnet/vmr" } },
                    new AssemblyInfo { AssemblyName = "X", ProjectKey = 1, RepoName = "dotnet/subx", SolutionName = "SubX", RepoChain = new[] { "dotnet/vmr", "dotnet/subx" } },
                    new AssemblyInfo { AssemblyName = "O", ProjectKey = 2, RepoName = "dotnet/other", SolutionName = "Other", RepoChain = new[] { "dotnet/other" } },
                };

                // Selecting the parent repo pulls in its nested sub-repo, but not an unrelated repo.
                var parent = index.Get("repo:dotnet/vmr Widget");
                CollectionAssert.AreEquivalent(new[] { "WidgetVmr", "WidgetSubX" }, parent.ResultSymbols.Select(s => s.Name).ToArray());

                // Selecting the sub-repo directly stays scoped to just that sub-repo.
                var child = index.Get("repo:dotnet/subx Widget");
                CollectionAssert.AreEqual(new[] { "WidgetSubX" }, child.ResultSymbols.Select(s => s.Name).ToArray());
            }
        }

        public void Test(IEnumerable<KeyValuePair<string, string>> input, string pattern, params string[] expectedResults)
        {
            using (var index = new Index())
            {
                var huffman = Huffman.Create(input.Select(kvp => kvp.Value));
                index.indexFinishedPopulating = true;
                index.huffman = huffman;
                index.symbols = new List<IndexEntry>(input.Select(kvp => new IndexEntry(kvp.Key, huffman.CompressToNative(kvp.Value))));
                index.PopulateSymbolsById();
                var query = index.Get(pattern);
                var resultSymbols = query.ResultSymbols;
                Assert.IsNotNull(resultSymbols);
                var actualResults = resultSymbols.Select(i => i.Description);
                Assert.IsTrue(actualResults.SequenceEqual(expectedResults));
            }
        }

        static IndexUnitTests()
        {
            Index.SetRootPath(Path.GetDirectoryName(typeof(IndexUnitTests).GetTypeInfo().Assembly.Location)); 
        }

        public void EndToEnd(string queryString, string expectedHtml)
        {
            var testData = new List<DeclaredSymbolInfo>
            {
                new DeclaredSymbolInfo()
                {
                    Name = "Console",
                    Description = "System.Console",
                    // T:System.Console, f907d79481da6ba4
                    ID = 11847803494810978297UL
                }
            };

            using (var index = new Index())
            {
                var huffman = Huffman.Create(testData.Select(i => i.Description));
                index.indexFinishedPopulating = true;
                index.huffman = huffman;
                index.symbols = testData.Select(dsi => new IndexEntry(dsi)).ToList();
                index.PopulateSymbolsById();
                var query = index.Get(queryString);
                var actualHtml = new ResultsHtmlGenerator(query).Generate(index: index);
                Assert.AreEqual(expectedHtml, actualHtml);
            }
        }

        private void Test(string[] input, string pattern, string[] expectedResults)
        {
            var index = new Index();
            index.symbols = new List<IndexEntry>(input.Select(s => new IndexEntry(s)));
            index.PopulateSymbolsById();
            var foundSymbols = index.FindSymbols(pattern);
            if ((expectedResults == null || expectedResults.Length == 0) && foundSymbols == null)
            {
                return;
            }

            var actualResults = foundSymbols.Select(i => i.Name);
            Assert.IsTrue(actualResults.SequenceEqual(expectedResults));
        }
    }
}
