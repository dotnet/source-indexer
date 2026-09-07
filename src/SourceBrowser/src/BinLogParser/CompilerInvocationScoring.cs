using System;
using System.IO;
using NuGet.Frameworks;

namespace Microsoft.SourceBrowser.BinLogParser
{
    public static class CompilerInvocationScoring
    {
        public static int CalculateScore(string targetFramework, int sourceFileCount)
        {
            var score = sourceFileCount;
            if (!string.IsNullOrEmpty(targetFramework))
            {
                var framework = NuGetFramework.Parse(targetFramework);
                if (framework.Version != null)
                {
                    score += framework.Version.Major * 1000 + framework.Version.Minor * 100;
                }

                if (framework.HasPlatform)
                {
                    score += 500;
                    if (framework.Platform.Equals("linux", StringComparison.OrdinalIgnoreCase))
                    {
                        score += 100;
                    }
                    else if (framework.Platform.Equals("unix", StringComparison.OrdinalIgnoreCase))
                    {
                        score += 50;
                    }
                }
            }

            return score;
        }

        public static bool IsReferenceAssembly(
            string projectDirectory,
            StringComparison comparison = StringComparison.Ordinal)
        {
            var projectFolder = Path.GetFileName(projectDirectory);
            return string.Equals(projectFolder, "ref", comparison) ||
                string.Equals(projectFolder, "stubs", comparison) ||
                string.Equals(Path.GetFileName(Path.GetDirectoryName(projectDirectory)), "cycle-breakers", comparison);
        }
    }
}
