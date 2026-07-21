using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    [TestClass]
    public class PathsUnitTests
    {
        [TestMethod]
        public void MakeRelativeToFile1()
        {
            MakeRelativeToFile(
                @"c:\root\abcd\e\f\g.txt",
                @"c:\root\abc\h\i.txt",
                @"..\..\abcd\e\f\g.txt");
        }

        [TestMethod]
        public void MakeRelativeToFile2()
        {
            MakeRelativeToFile(
                @"c:\root\1.txt",
                @"c:\root\2.txt",
                @"1.txt");
        }

        [TestMethod]
        public void MakeRelativeToFile3()
        {
            MakeRelativeToFile(
                @"c:\root\a\1.txt",
                @"c:\root\2.txt",
                @"a\1.txt");
        }

        [TestMethod]
        public void MakeRelativeToFile4()
        {
            MakeRelativeToFile(
                @"c:\1.txt",
                @"c:\root\2.txt",
                @"..\1.txt");
        }

        [TestMethod]
        public void MakeRelativeToFile5()
        {
            MakeRelativeToFile(
                @"c:\root\1.txt",
                @"c:\root\1.txt",
                @"1.txt");
        }

        [TestMethod]
        public void MakeRelativeToFile6()
        {
            MakeRelativeToFile(
                @"c:\solution\project\R\1.html",
                @"c:\solution\project\document.txt",
                @"R\1.html");
        }

        [TestMethod]
        public void MakeRelativeToFile7()
        {
            MakeRelativeToFile(
                @"c:\solution\assembly\A.html",
                @"c:\solution\project\a\document.txt",
                @"..\..\assembly\A.html");
        }

        [TestMethod]
        public void MakeRelativeToFile8()
        {
            MakeRelativeToFile(
                @"c:\solution",
                @"c:\solution\project\a\document.txt",
                @"..\..\");
        }

        [TestMethod]
        public void MakeRelativeToFile9()
        {
            MakeRelativeToFile(
                @"c:\solution\",
                @"c:\solution\project\a\document.txt",
                @"..\..\");
        }

        [TestMethod]
        public void MakeRelativeToFile10()
        {
            MakeRelativeToFile(
                @"c:\solution\",
                @"c:\solution",
                @"solution\");
        }

        [TestMethod]
        public void MakeRelativeToFile11()
        {
            MakeRelativeToFile(
                @"c:\solution",
                @"c:\solution",
                @"solution");
        }

        [TestMethod]
        public void MakeRelativeToFile12()
        {
            MakeRelativeToFile(
                @"c:\solution",
                @"c:\solution\",
                @"");
        }

        [TestMethod]
        public void MakeRelativeToFile13()
        {
            MakeRelativeToFile(
                @"c:\solution\",
                @"c:\solution\",
                @"");
        }

        [TestMethod]
        public void MakeRelativeToFolder1()
        {
            MakeRelativeToFolder(
                @"c:\root\1.txt",
                @"c:\root\",
                @"1.txt");
        }

        [TestMethod]
        public void MakeRelativeToFolder2()
        {
            MakeRelativeToFolder(
                @"c:\root\1.txt",
                @"c:\root",
                @"1.txt");
        }

        [TestMethod]
        public void MakeRelativeToFolder3()
        {
            MakeRelativeToFolder(
                @"c:\root\a\1.txt",
                @"c:\root",
                @"a\1.txt");
        }

        [TestMethod]
        public void MakeRelativeToFolder4()
        {
            MakeRelativeToFolder(
                @"c:\root\1.txt",
                @"c:\root\a",
                @"..\1.txt");
        }

        [TestMethod]
        public void MakeRelativeToFolder5()
        {
            MakeRelativeToFolder(
                @"c:\root\1.txt",
                @"c:\root\a\",
                @"..\1.txt");
        }

        private void MakeRelativeToFile(string a, string b, string expected)
        {
            var actual = Paths.MakeRelativeToFile(a, b);
            Assert.AreEqual(expected, actual);
        }

        private void MakeRelativeToFolder(string a, string b, string expected)
        {
            var actual = Paths.MakeRelativeToFolder(a, b);
            Assert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void DisambiguateRelativePaths_NoCollision_PassesThroughUnchanged()
        {
            var relativePaths = new[] { @"System\Collections\IEnumerable.cs", @"System\Collections\IEnumerator.cs" };
            var identityKeys = new[] { @"c:\src\a\IEnumerable.cs", @"c:\src\a\IEnumerator.cs" };

            var actual = Paths.DisambiguateRelativePaths(relativePaths, identityKeys);

            CollectionAssert.AreEqual(relativePaths, actual);
        }

        [TestMethod]
        public void DisambiguateRelativePaths_SameFileSurfacedTwice_KeepsOriginalPathForBoth()
        {
            // Same physical file linked into the project more than once (e.g. a shared/linked
            // source file) -- this is the existing, legitimate dedup and must not be disambiguated.
            var relativePaths = new[] { @"System\Collections\IEnumerable.cs", @"System\Collections\IEnumerable.cs" };
            var identityKeys = new[] { @"c:\src\shared\IEnumerable.cs", @"c:\src\shared\IEnumerable.cs" };

            var actual = Paths.DisambiguateRelativePaths(relativePaths, identityKeys);

            CollectionAssert.AreEqual(relativePaths, actual);
        }

        [TestMethod]
        public void DisambiguateRelativePaths_DifferentFilesCollide_AssignsDeterministicSuffixes()
        {
            // Two genuinely different physical files that happen to resolve to the same
            // folders+filename (the #194 repro: two unrelated "IEnumerable.cs" files).
            var relativePaths = new[] { @"System\Collections\IEnumerable.cs", @"System\Collections\IEnumerable.cs" };
            var identityKeys = new[] { @"c:\src\b\IEnumerable.cs", @"c:\src\a\IEnumerable.cs" };

            var actual = Paths.DisambiguateRelativePaths(relativePaths, identityKeys);

            // Ordered by identity key ("a" before "b"), so the "a" file (index 1) keeps the
            // original path and the "b" file (index 0) gets the suffix -- regardless of input order.
            Assert.AreEqual(@"System\Collections\IEnumerable_2.cs", actual[0]);
            Assert.AreEqual(@"System\Collections\IEnumerable.cs", actual[1]);
        }

        [TestMethod]
        public void DisambiguateRelativePaths_ThreeDifferentFilesCollide_AssignsIncrementingSuffixes()
        {
            var relativePaths = new[]
            {
                @"System\Collections\IEnumerable.cs",
                @"System\Collections\IEnumerable.cs",
                @"System\Collections\IEnumerable.cs",
            };
            var identityKeys = new[] { @"c:\src\a\IEnumerable.cs", @"c:\src\b\IEnumerable.cs", @"c:\src\c\IEnumerable.cs" };

            var actual = Paths.DisambiguateRelativePaths(relativePaths, identityKeys);

            Assert.AreEqual(@"System\Collections\IEnumerable.cs", actual[0]);
            Assert.AreEqual(@"System\Collections\IEnumerable_2.cs", actual[1]);
            Assert.AreEqual(@"System\Collections\IEnumerable_3.cs", actual[2]);
        }

        [TestMethod]
        public void DisambiguateRelativePaths_CollisionAtProjectRoot_SuffixesFileNameOnly()
        {
            var relativePaths = new[] { "IEnumerable.cs", "IEnumerable.cs" };
            var identityKeys = new[] { @"c:\src\b\IEnumerable.cs", @"c:\src\a\IEnumerable.cs" };

            var actual = Paths.DisambiguateRelativePaths(relativePaths, identityKeys);

            Assert.AreEqual("IEnumerable_2.cs", actual[0]);
            Assert.AreEqual("IEnumerable.cs", actual[1]);
        }

        [TestMethod]
        public void ShortenRelativePathIfNecessary_ShortPath_PassesThroughUnchanged()
        {
            var relativePath = @"System\Collections\Generic\List.cs";

            var actual = Paths.ShortenRelativePathIfNecessary(relativePath);

            Assert.AreEqual(relativePath, actual);
        }

        [TestMethod]
        public void ShortenRelativePathIfNecessary_LongSourceGeneratedPath_ShortensAndKeepsGroupingAndFileName()
        {
            // A representative source-generated document path: Generated\<generator assembly>\<fully
            // qualified generator type>\<hint name>. The fully-qualified type folder is what blows the
            // path budget.
            var relativePath =
                @"Generated\System.Text.Json.SourceGeneration\System.Text.Json.SourceGeneration.JsonSourceGenerator\" +
                @"System.Text.Json.Serialization.JsonSerializerContext.SomeVeryLongContextTypeName.g.cs";
            Assert.IsTrue(relativePath.Length > 140, "test input should exceed the cap");

            var actual = Paths.ShortenRelativePathIfNecessary(relativePath);

            Assert.IsTrue(actual.Length <= 140, "shortened path must fit within the cap");
            Assert.IsTrue(actual.StartsWith(@"Generated\", StringComparison.Ordinal), "top-level folder is preserved for grouping");
            Assert.IsTrue(actual.EndsWith(@"SomeVeryLongContextTypeName.g.cs", StringComparison.Ordinal), "leaf file name is preserved");
            // Deterministic: the same input always maps to the same shortened path so links stay stable.
            Assert.AreEqual(actual, Paths.ShortenRelativePathIfNecessary(relativePath));
        }

        [TestMethod]
        public void ShortenRelativePathIfNecessary_DistinctLongFolders_DoNotCollide()
        {
            var basePath = @"Generated\Some.Generator\Some.Generator.TheGenerator";
            var padding = new string('x', 140);
            var a = basePath + @"\A\" + padding + ".g.cs";
            var b = basePath + @"\B\" + padding + ".g.cs";

            Assert.AreNotEqual(
                Paths.ShortenRelativePathIfNecessary(a),
                Paths.ShortenRelativePathIfNecessary(b));
        }

        [TestMethod]
        public void ShortenRelativePathIfNecessary_LongFileNameAtRoot_ShortensFileName()
        {
            var relativePath = new string('a', 200) + ".g.cs";

            var actual = Paths.ShortenRelativePathIfNecessary(relativePath);

            Assert.IsTrue(actual.Length <= 140, "shortened path must fit within the cap");
            Assert.IsTrue(actual.EndsWith(".cs", StringComparison.Ordinal), "extension is preserved");
        }

        [TestMethod]
        public void GetFullPathInFolderCone1()
        {
            TestGetFullPathInFolderCone(
                @"c:\a\b",
                @"c:\1.txt",
                @"c:\a\b\1.txt");
            TestGetFullPathInFolderCone(
                @"c:\a\b",
                @"c:\a\1.txt",
                @"c:\a\b\1.txt");
        }

        private void TestGetFullPathInFolderCone(string folder, string filePath, string expected)
        {
            var actual = Paths.GetFullPathInFolderCone(folder, filePath);
            Assert.AreEqual(expected, actual);
        }
    }
}
