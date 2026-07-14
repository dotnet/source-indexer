using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    [TestClass]
    public class SerializationUnitTests
    {
        [TestMethod]
        public void TestULongToHexStringRoundtrip()
        {
            for (int i = 0; i < 1000; i++)
            {
                var originalStringId = Paths.GetMD5Hash(i.ToString(), 16);
                var id = Paths.GetMD5HashULong(i.ToString(), 16);
                var stringId = Serialization.ULongToHexString(id);
                Assert.AreEqual(originalStringId, stringId);
                Assert.AreEqual(16, stringId.Length);
                var actualId = Serialization.HexStringToULong(stringId);
                Assert.AreEqual(id, actualId);
            }
        }

        [TestMethod]
        public void WriteProjectMap_omits_repo_and_solution_fields_when_untagged()
        {
            var outputPath = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var assemblies = new[] { Tuple.Create("A", "A.csproj"), Tuple.Create("B", "B.csproj") };
                var referencingCounts = new Dictionary<string, int> { { "A", 1 }, { "B", 0 } };

                Serialization.WriteProjectMap(outputPath, assemblies, referencingCounts);

                var lines = File.ReadAllLines(Path.Combine(outputPath, Constants.MasterAssemblyMap + ".txt"));
                Assert.AreEqual(2, lines.Length);
                foreach (var line in lines)
                {
                    Assert.AreEqual(3, line.Split(';').Length, "Untagged assemblies must keep the original 3-field format.");
                }
            }
            finally
            {
                Directory.Delete(outputPath, recursive: true);
            }
        }

        [TestMethod]
        public void WriteProjectMap_adds_repo_and_solution_fields_when_any_assembly_is_tagged()
        {
            var outputPath = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var assemblies = new[] { Tuple.Create("A", "A.csproj"), Tuple.Create("B", "B.csproj") };
                var referencingCounts = new Dictionary<string, int> { { "A", 1 }, { "B", 0 } };
                var repoAndSolutionNames = new Dictionary<string, Tuple<string, string>>
                {
                    { "A", Tuple.Create("clangsharp", "ClangSharp") },
                    { "B", Tuple.Create("", "") },
                };

                Serialization.WriteProjectMap(outputPath, assemblies, referencingCounts, repoAndSolutionNames);

                var lines = File.ReadAllLines(Path.Combine(outputPath, Constants.MasterAssemblyMap + ".txt"));
                var lineA = lines.Single(l => l.StartsWith("A;", StringComparison.Ordinal));
                var lineB = lines.Single(l => l.StartsWith("B;", StringComparison.Ordinal));

                Assert.AreEqual("A;0;1;clangsharp;ClangSharp", lineA);
                Assert.AreEqual("B;1;0;;", lineB);
            }
            finally
            {
                Directory.Delete(outputPath, recursive: true);
            }
        }
    }
}
