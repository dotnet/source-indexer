using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SourceBrowser.HtmlGenerator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace HtmlGenerator.Tests
{
    /// <summary>
    /// Proves <see cref="ConfigDataReader"/> round-trips exactly what the real Pass1 writers
    /// (<see cref="ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap"/>,
    /// <see cref="ProjectGenerator.GenerateReferencesDataFilesToAssembly"/>) produce, and -- unlike
    /// Pass2's own consuming readers -- never deletes what it reads, since the config merge step may
    /// need to re-read the same obj/&lt;config&gt; data more than once.
    /// </summary>
    [TestClass]
    public class ConfigDataReaderTests
    {
        private string testRoot;

        [TestInitialize]
        public void Setup()
        {
            testRoot = Path.Combine(Path.GetTempPath(), "ConfigDataReaderTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ReadDeclarationMap_ReturnsEmpty_WhenFileDoesNotExist()
        {
            var result = ConfigDataReader.ReadDeclarationMap(Path.Combine(testRoot, "DeclarationMap.txt"));

            result.ShouldNotBeNull();
            result.Count.ShouldBe(0);
        }

        [TestMethod]
        public void ReadDeclarationMap_RoundTrips_WhatGenerateSymbolIDToListOfDeclarationLocationsMapWrote()
        {
            ProjectGenerator.GenerateSymbolIDToListOfDeclarationLocationsMap(
                testRoot,
                new Dictionary<string, List<Tuple<string, long>>>
                {
                    ["deadbeef"] = new List<Tuple<string, long>>
                    {
                        Tuple.Create("File.cs", 42L),
                        Tuple.Create("File.Windows.cs", 7L),
                    },
                    ["cafef00d"] = new List<Tuple<string, long>>
                    {
                        Tuple.Create("Other.cs", 100L),
                    },
                });

            var declarationMapFile = Path.Combine(testRoot, Constants.DeclarationMap + ".txt");
            var result = ConfigDataReader.ReadDeclarationMap(declarationMapFile);

            result.Keys.OrderBy(k => k).ShouldBe(new[] { "cafef00d", "deadbeef" });
            result["deadbeef"].Select(t => (t.Item1, t.Item2)).ShouldBe(new[] { ("File.cs", 42L), ("File.Windows.cs", 7L) });
            result["cafef00d"].Select(t => (t.Item1, t.Item2)).ShouldBe(new[] { ("Other.cs", 100L) });

            // Non-destructive: the file this reader just read must still be there afterward.
            File.Exists(declarationMapFile).ShouldBeTrue();
        }

        [TestMethod]
        public void ReadReferenceShards_ReturnsEmpty_WhenReferencesFolderDoesNotExist()
        {
            var result = ConfigDataReader.ReadReferenceShards(Path.Combine(testRoot, "r"));

            result.ShouldNotBeNull();
            result.Count.ShouldBe(0);
        }

        [TestMethod]
        public void ReadReferenceShards_RoundTrips_WhatGenerateReferencesDataFilesToAssemblyWrote()
        {
            ProjectGenerator.GenerateReferencesDataFilesToAssembly(
                testRoot,
                "AssemblyB",
                new Dictionary<string, List<Reference>>
                {
                    ["deadbeef"] = new List<Reference>
                    {
                        new Reference
                        {
                            FromAssemblyId = "AssemblyA",
                            Url = "AssemblyA/File.cs.html",
                            FromLocalPath = "File.cs",
                            ReferenceLineNumber = 1,
                            ReferenceColumnStart = 2,
                            ReferenceColumnEnd = 5,
                            ReferenceLineText = "someCall();",
                            ToSymbolName = "Method",
                            Kind = ReferenceKind.Reference,
                        },
                    },
                });

            var referencesFolder = Path.Combine(testRoot, "AssemblyB", Constants.ReferencesFileName);
            var result = ConfigDataReader.ReadReferenceShards(referencesFolder);

            result.Keys.ShouldBe(new[] { "deadbeef" });
            var reference = result["deadbeef"].ShouldHaveSingleItem();
            reference.FromAssemblyId.ShouldBe("AssemblyA");
            reference.FromLocalPath.ShouldBe("File.cs");
            reference.ReferenceLineNumber.ShouldBe(1);
            reference.ReferenceColumnStart.ShouldBe(2);
            reference.ReferenceColumnEnd.ShouldBe(5);
            reference.Kind.ShouldBe(ReferenceKind.Reference);

            // Non-destructive: unlike Pass2's consuming reader, the shard file must survive being read.
            Directory.GetFiles(referencesFolder, "*" + ProjectGenerator.ReferenceShardExtension).ShouldNotBeEmpty();
        }
    }
}
