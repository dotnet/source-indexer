using System.Collections.Generic;
using Microsoft.SourceBrowser.BinLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BinLogToSln.Tests
{
    [TestClass]
    public class InvocationScoringTests
    {
        private static CompilerInvocation CreateBaseInvocation()
        {
            return new CompilerInvocation
            {
                ProjectFilePath = "/test/project.csproj",
                OutputAssemblyPath = "/test/output.dll",
                CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll File1.cs",
                ProjectProperties = new Dictionary<string, string>
                {
                    ["TargetFramework"] = "net8.0"
                }
            };
        }

        [TestMethod]
        public void CalculateInvocationScore_UseForSourceIndex_ScoresHigherThanWithout()
        {
            // Arrange
            var baseInvocation = CreateBaseInvocation();
            var withUseForSourceIndex = baseInvocation.Clone();
            withUseForSourceIndex.ProjectProperties["UseForSourceIndex"] = "true";

            var withoutUseForSourceIndex = baseInvocation.Clone();

            // Act
            var scoreWith = Program.CalculateInvocationScore(withUseForSourceIndex);
            var scoreWithout = Program.CalculateInvocationScore(withoutUseForSourceIndex);

            // Assert
            Assert.IsTrue(scoreWith > scoreWithout, "UseForSourceIndex should score higher than without it");
        }

        [TestMethod]
        public void CalculateInvocationScore_PlatformNotSupported_ScoresLowerThanSupported()
        {
            // Arrange
            var baseInvocation = CreateBaseInvocation();
            var platformNotSupported = baseInvocation.Clone();
            platformNotSupported.ProjectProperties["IsPlatformNotSupportedAssembly"] = "true";

            var platformSupported = baseInvocation.Clone();

            // Act
            var notSupportedScore = Program.CalculateInvocationScore(platformNotSupported);
            var supportedScore = Program.CalculateInvocationScore(platformSupported);

            // Assert
            Assert.IsTrue(supportedScore > notSupportedScore, "Platform supported assembly should score higher than platform not supported");
        }

        [TestMethod]
        public void CalculateInvocationScore_FrameworkVersion_NewerScoresHigher()
        {
            // Test that newer framework versions score higher than older ones
            var baseInvocation = CreateBaseInvocation();
            var newerFramework = baseInvocation.Clone();
            newerFramework.ProjectProperties["TargetFramework"] = "net8.0";

            var olderFramework = baseInvocation.Clone();
            olderFramework.ProjectProperties["TargetFramework"] = "net6.0";

            // Act
            var newerScore = Program.CalculateInvocationScore(newerFramework);
            var olderScore = Program.CalculateInvocationScore(olderFramework);

            // Assert
            Assert.IsTrue(newerScore > olderScore, "Newer framework version should score higher than older version");

            // Test that net48 scores higher than netstandard2.1
            var net48Framework = baseInvocation.Clone();
            net48Framework.ProjectProperties["TargetFramework"] = "net48";

            var netstandardFramework = baseInvocation.Clone();
            netstandardFramework.ProjectProperties["TargetFramework"] = "netstandard2.1";

            var net48Score = Program.CalculateInvocationScore(net48Framework);
            var netstandardScore = Program.CalculateInvocationScore(netstandardFramework);

            Assert.IsTrue(net48Score > netstandardScore, "net48 should score higher than netstandard2.1");
        }

        [TestMethod]
        public void CalculateInvocationScore_PlatformSpecific_ScoresHigherThanGeneric()
        {
            // Test that platform-specific frameworks score higher than generic ones
            var baseInvocation = CreateBaseInvocation();
            var platformSpecific = baseInvocation.Clone();
            platformSpecific.ProjectProperties["TargetFramework"] = "net8.0-linux";

            var generic = baseInvocation.Clone();
            generic.ProjectProperties["TargetFramework"] = "net8.0";

            // Act
            var platformScore = Program.CalculateInvocationScore(platformSpecific);
            var genericScore = Program.CalculateInvocationScore(generic);

            // Assert
            Assert.IsTrue(platformScore > genericScore, "Platform-specific framework should score higher than generic framework");

            // Test that linux scores higher than windows for same framework
            var linuxFramework = baseInvocation.Clone();
            linuxFramework.ProjectProperties["TargetFramework"] = "net8.0-linux";

            var windowsFramework = baseInvocation.Clone();
            windowsFramework.ProjectProperties["TargetFramework"] = "net8.0-windows";

            var linuxScore = Program.CalculateInvocationScore(linuxFramework);
            var windowsScore = Program.CalculateInvocationScore(windowsFramework);

            Assert.IsTrue(linuxScore > windowsScore, "Linux framework should score higher than Windows framework");
        }

        [TestMethod]
        public void CalculateInvocationScore_SourceFileCount_MoreFilesScoreHigher()
        {
            // Arrange
            var baseInvocation = CreateBaseInvocation();
            var moreSourceFiles = baseInvocation.Clone();
            moreSourceFiles.CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll Class1.cs Class2.cs Class3.cs";

            var fewerSourceFiles = baseInvocation.Clone();
            fewerSourceFiles.CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll Class1.cs";

            // Act
            var moreFilesScore = Program.CalculateInvocationScore(moreSourceFiles);
            var fewerFilesScore = Program.CalculateInvocationScore(fewerSourceFiles);

            // Assert
            Assert.IsTrue(moreFilesScore > fewerFilesScore, "Invocation with more source files should score higher");
        }

        [TestMethod]
        public void CalculateInvocationScore_ComplexScenario_ScoresHigherThanSimple()
        {
            // Arrange - Linux platform with multiple source files
            var baseInvocation = CreateBaseInvocation();
            var complexScenario = baseInvocation.Clone();
            complexScenario.CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll File1.cs File2.cs File3.cs File4.cs File5.cs";
            complexScenario.ProjectProperties["TargetFramework"] = "net8.0-linux";

            var simpleScenario = baseInvocation.Clone();
            simpleScenario.CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll File1.cs";
            simpleScenario.ProjectProperties["TargetFramework"] = "net8.0";

            // Act
            var complexScore = Program.CalculateInvocationScore(complexScenario);
            var simpleScore = Program.CalculateInvocationScore(simpleScenario);

            // Assert
            Assert.IsTrue(complexScore > simpleScore, "Complex scenario (platform-specific with more files) should score higher than simple scenario");
        }

        [TestMethod]
        public void CalculateInvocationScore_NoProjectProperties_ScoresLowerThanWithProperties()
        {
            // Arrange
            var baseInvocation = CreateBaseInvocation();
            var noProperties = baseInvocation.Clone();
            noProperties.ProjectProperties = null;

            var withProperties = baseInvocation.Clone();

            // Act
            var noPropertiesScore = Program.CalculateInvocationScore(noProperties);
            var withPropertiesScore = Program.CalculateInvocationScore(withProperties);

            // Assert
            Assert.IsTrue(withPropertiesScore > noPropertiesScore, "Invocation with project properties should score higher than one without");
        }

        [TestMethod]
        public void CalculateInvocationScore_EmptyProjectProperties_ScoresLowerThanWithFramework()
        {
            // Arrange
            var baseInvocation = CreateBaseInvocation();
            var emptyProperties = baseInvocation.Clone();
            emptyProperties.ProjectProperties.Clear();
            emptyProperties.CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll File1.cs File2.cs";

            var withFramework = baseInvocation.Clone();
            withFramework.CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll File1.cs File2.cs";

            // Act
            var emptyPropertiesScore = Program.CalculateInvocationScore(emptyProperties);
            var withFrameworkScore = Program.CalculateInvocationScore(withFramework);

            // Assert
            Assert.IsTrue(withFrameworkScore > emptyPropertiesScore, "Invocation with framework should score higher than one with empty properties");
        }

        [TestMethod]
        public void CalculateInvocationScore_InvalidTargetFramework_ScoresLowerThanValid()
        {
            // Arrange
            var baseInvocation = CreateBaseInvocation();
            var invalidFramework = baseInvocation.Clone();
            invalidFramework.ProjectProperties["TargetFramework"] = "invalid-framework-name";

            var validFramework = baseInvocation.Clone();
            validFramework.ProjectProperties["TargetFramework"] = "net8.0";

            // Act
            var invalidScore = Program.CalculateInvocationScore(invalidFramework);
            var validScore = Program.CalculateInvocationScore(validFramework);

            // Assert
            Assert.IsTrue(validScore > invalidScore, "Valid framework should score higher than invalid framework");
        }

        [TestMethod]
        public void CalculateInvocationScore_PrioritiesWork_CorrectOrdering()
        {
            // Create invocations with different priority features
            var baseInvocation = CreateBaseInvocation();
            
            var useForSourceIndexInvocation = baseInvocation.Clone();
            useForSourceIndexInvocation.ProjectFilePath = "/test/project1.csproj";
            useForSourceIndexInvocation.OutputAssemblyPath = "/test/output1.dll";
            useForSourceIndexInvocation.CommandLineArguments = "/noconfig /target:library /out:output1.dll";
            useForSourceIndexInvocation.ProjectProperties["UseForSourceIndex"] = "true";
            useForSourceIndexInvocation.ProjectProperties["IsPlatformNotSupportedAssembly"] = "true"; // Should be ignored due to UseForSourceIndex
            useForSourceIndexInvocation.ProjectProperties["TargetFramework"] = "net6.0";

            var notSupportedInvocation = baseInvocation.Clone();
            notSupportedInvocation.ProjectFilePath = "/test/project2.csproj";
            notSupportedInvocation.OutputAssemblyPath = "/test/output2.dll";
            notSupportedInvocation.CommandLineArguments = "/noconfig /target:library /out:output2.dll File1.cs File2.cs File3.cs File4.cs File5.cs";
            notSupportedInvocation.ProjectProperties["IsPlatformNotSupportedAssembly"] = "true";
            notSupportedInvocation.ProjectProperties["TargetFramework"] = "net8.0";

            var newerFrameworkInvocation = baseInvocation.Clone();
            newerFrameworkInvocation.ProjectFilePath = "/test/project3.csproj";
            newerFrameworkInvocation.OutputAssemblyPath = "/test/output3.dll";
            newerFrameworkInvocation.CommandLineArguments = "/noconfig /target:library /out:output3.dll File1.cs";
            newerFrameworkInvocation.ProjectProperties["TargetFramework"] = "net8.0";

            var olderFrameworkInvocation = baseInvocation.Clone();
            olderFrameworkInvocation.ProjectFilePath = "/test/project4.csproj";
            olderFrameworkInvocation.OutputAssemblyPath = "/test/output4.dll";
            olderFrameworkInvocation.CommandLineArguments = "/noconfig /target:library /out:output4.dll File1.cs";
            olderFrameworkInvocation.ProjectProperties["TargetFramework"] = "net6.0";

            // Act
            var useForSourceIndexScore = Program.CalculateInvocationScore(useForSourceIndexInvocation);
            var notSupportedScore = Program.CalculateInvocationScore(notSupportedInvocation);
            var newerFrameworkScore = Program.CalculateInvocationScore(newerFrameworkInvocation);
            var olderFrameworkScore = Program.CalculateInvocationScore(olderFrameworkInvocation);

            // Assert - Higher scores should be better
            Assert.IsTrue(useForSourceIndexScore > notSupportedScore, "UseForSourceIndex should beat platform not supported");
            Assert.IsTrue(useForSourceIndexScore > newerFrameworkScore, "UseForSourceIndex should beat newer framework");
            Assert.IsTrue(newerFrameworkScore > notSupportedScore, "Newer framework should beat platform not supported");
            Assert.IsTrue(newerFrameworkScore > olderFrameworkScore, "Newer framework should beat older framework");
        }
    }
}