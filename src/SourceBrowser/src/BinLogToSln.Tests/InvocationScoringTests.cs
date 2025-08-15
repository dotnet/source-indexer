using System.Collections.Generic;
using Microsoft.SourceBrowser.BinLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BinLogToSln.Tests
{
    [TestClass]
    public class InvocationScoringTests
    {
        [TestMethod]
        public void CalculateInvocationScore_UseForSourceIndex_ReturnsMaxValue()
        {
            // Arrange
            var invocation = new CompilerInvocation
            {
                ProjectFilePath = "/test/project.csproj",
                OutputAssemblyPath = "/test/output.dll",
                ProjectProperties = new Dictionary<string, string>
                {
                    ["UseForSourceIndex"] = "true"
                }
            };

            // Act
            var score = Program.CalculateInvocationScore(invocation);

            // Assert
            Assert.AreEqual(int.MaxValue, score);
        }

        [TestMethod]
        public void CalculateInvocationScore_PlatformNotSupported_AppliesPenalty()
        {
            // Arrange
            var invocation = new CompilerInvocation
            {
                ProjectFilePath = "/test/project.csproj",
                OutputAssemblyPath = "/test/output.dll",
                CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll",
                ProjectProperties = new Dictionary<string, string>
                {
                    ["IsPlatformNotSupportedAssembly"] = "true",
                    ["TargetFramework"] = "net8.0"
                }
            };

            // Act
            var score = Program.CalculateInvocationScore(invocation);

            // Assert
            // Should be 8000 (framework score) - 10000 (penalty) + 0 (source files) = -2000
            Assert.AreEqual(-2000, score);
        }

        [TestMethod]
        public void CalculateInvocationScore_FrameworkVersion_ScoresCorrectly()
        {
            // Test different framework versions
            var testCases = new[]
            {
                new { Framework = "net8.0", ExpectedScore = 8000 },
                new { Framework = "net6.0", ExpectedScore = 6000 },
                new { Framework = "net48", ExpectedScore = 4080 }, // 4 * 1000 + 8 * 100
                new { Framework = "netstandard2.1", ExpectedScore = 2010 } // 2 * 1000 + 1 * 100
            };

            foreach (var testCase in testCases)
            {
                // Arrange
                var invocation = new CompilerInvocation
                {
                    ProjectFilePath = "/test/project.csproj",
                    OutputAssemblyPath = "/test/output.dll",
                    CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll",
                    ProjectProperties = new Dictionary<string, string>
                    {
                        ["TargetFramework"] = testCase.Framework
                    }
                };

                // Act
                var score = Program.CalculateInvocationScore(invocation);

                // Assert
                Assert.AreEqual(testCase.ExpectedScore, score, $"Framework {testCase.Framework} should score {testCase.ExpectedScore}");
            }
        }

        [TestMethod]
        public void CalculateInvocationScore_PlatformSpecific_ReceivesBonus()
        {
            // Test platform-specific frameworks
            var testCases = new[]
            {
                new { Framework = "net8.0-linux", ExpectedBonus = 600 }, // 500 platform + 100 linux
                new { Framework = "net8.0-unix", ExpectedBonus = 550 },  // 500 platform + 50 unix
                new { Framework = "net8.0-windows", ExpectedBonus = 500 }, // 500 platform only
            };

            foreach (var testCase in testCases)
            {
                // Arrange
                var invocation = new CompilerInvocation
                {
                    ProjectFilePath = "/test/project.csproj",
                    OutputAssemblyPath = "/test/output.dll",
                    CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll",
                    ProjectProperties = new Dictionary<string, string>
                    {
                        ["TargetFramework"] = testCase.Framework
                    }
                };

                // Act
                var score = Program.CalculateInvocationScore(invocation);

                // Assert
                var expectedScore = 8000 + testCase.ExpectedBonus; // framework score + platform bonus
                Assert.AreEqual(expectedScore, score, $"Framework {testCase.Framework} should score {expectedScore}");
            }
        }

        [TestMethod]
        public void CalculateInvocationScore_SourceFileCount_AddsCorrectly()
        {
            // Arrange
            var invocation = new CompilerInvocation
            {
                ProjectFilePath = "/test/project.csproj",
                OutputAssemblyPath = "/test/output.dll",
                CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll Class1.cs Class2.cs Class3.cs",
                ProjectProperties = new Dictionary<string, string>
                {
                    ["TargetFramework"] = "net8.0"
                }
            };

            // Act
            var score = Program.CalculateInvocationScore(invocation);

            // Assert
            // Should be 8000 (framework) + 3 (source files) = 8003
            Assert.AreEqual(8003, score);
        }

        [TestMethod]
        public void CalculateInvocationScore_ComplexScenario_ScoresCorrectly()
        {
            // Arrange - Linux platform with multiple source files
            var invocation = new CompilerInvocation
            {
                ProjectFilePath = "/test/project.csproj",
                OutputAssemblyPath = "/test/output.dll",
                CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll File1.cs File2.cs File3.cs File4.cs File5.cs",
                ProjectProperties = new Dictionary<string, string>
                {
                    ["TargetFramework"] = "net8.0-linux"
                }
            };

            // Act
            var score = Program.CalculateInvocationScore(invocation);

            // Assert
            // Should be 8000 (framework) + 500 (platform) + 100 (linux) + 5 (source files) = 8605
            Assert.AreEqual(8605, score);
        }

        [TestMethod]
        public void CalculateInvocationScore_NoProjectProperties_ReturnsBaseScore()
        {
            // Arrange
            var invocation = new CompilerInvocation
            {
                ProjectFilePath = "/test/project.csproj",
                OutputAssemblyPath = "/test/output.dll",
                CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll File1.cs",
                ProjectProperties = null
            };

            // Act
            var score = Program.CalculateInvocationScore(invocation);

            // Assert
            // Should only count source files: 1
            Assert.AreEqual(1, score);
        }

        [TestMethod]
        public void CalculateInvocationScore_EmptyProjectProperties_ReturnsSourceFileCount()
        {
            // Arrange
            var invocation = new CompilerInvocation
            {
                ProjectFilePath = "/test/project.csproj",
                OutputAssemblyPath = "/test/output.dll",
                CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll File1.cs File2.cs",
                ProjectProperties = new Dictionary<string, string>()
            };

            // Act
            var score = Program.CalculateInvocationScore(invocation);

            // Assert
            // Should only count source files: 2
            Assert.AreEqual(2, score);
        }

        [TestMethod]
        public void CalculateInvocationScore_InvalidTargetFramework_IgnoresFrameworkScore()
        {
            // Arrange
            var invocation = new CompilerInvocation
            {
                ProjectFilePath = "/test/project.csproj",
                OutputAssemblyPath = "/test/output.dll",
                CommandLineArguments = "/noconfig /nowarn:1701,1702 /nostdlib+ /target:library /out:output.dll File1.cs",
                ProjectProperties = new Dictionary<string, string>
                {
                    ["TargetFramework"] = "invalid-framework-name"
                }
            };

            // Act
            var score = Program.CalculateInvocationScore(invocation);

            // Assert
            // Should only count source files since framework parsing fails: 1
            Assert.AreEqual(1, score);
        }

        [TestMethod]
        public void CalculateInvocationScore_PrioritiesWork_CorrectOrdering()
        {
            // Create invocations with different priority features
            var useForSourceIndexInvocation = new CompilerInvocation
            {
                ProjectFilePath = "/test/project1.csproj",
                OutputAssemblyPath = "/test/output1.dll",
                CommandLineArguments = "/noconfig /target:library /out:output1.dll",
                ProjectProperties = new Dictionary<string, string>
                {
                    ["UseForSourceIndex"] = "true",
                    ["IsPlatformNotSupportedAssembly"] = "true", // Should be ignored due to UseForSourceIndex
                    ["TargetFramework"] = "net6.0"
                }
            };

            var notSupportedInvocation = new CompilerInvocation
            {
                ProjectFilePath = "/test/project2.csproj",
                OutputAssemblyPath = "/test/output2.dll",
                CommandLineArguments = "/noconfig /target:library /out:output2.dll File1.cs File2.cs File3.cs File4.cs File5.cs",
                ProjectProperties = new Dictionary<string, string>
                {
                    ["IsPlatformNotSupportedAssembly"] = "true",
                    ["TargetFramework"] = "net8.0"
                }
            };

            var newerFrameworkInvocation = new CompilerInvocation
            {
                ProjectFilePath = "/test/project3.csproj",
                OutputAssemblyPath = "/test/output3.dll",
                CommandLineArguments = "/noconfig /target:library /out:output3.dll File1.cs",
                ProjectProperties = new Dictionary<string, string>
                {
                    ["TargetFramework"] = "net8.0"
                }
            };

            var olderFrameworkInvocation = new CompilerInvocation
            {
                ProjectFilePath = "/test/project4.csproj",
                OutputAssemblyPath = "/test/output4.dll",
                CommandLineArguments = "/noconfig /target:library /out:output4.dll File1.cs",
                ProjectProperties = new Dictionary<string, string>
                {
                    ["TargetFramework"] = "net6.0"
                }
            };

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