using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.SourceBrowser.BinLogParser
{
    public enum CompilerKind
    {
        CSharp,
        VisualBasic
    }

    public class BinLogCompilerInvocationsReader
    {
        /// <summary>
        /// Binlog reader does not handle concurrent accesses appropriately so handle it here
        /// </summary>
        private static readonly ConcurrentDictionary<string, Lazy<List<CompilerInvocation>>> m_binlogInvocationMap
            = new ConcurrentDictionary<string, Lazy<List<CompilerInvocation>>>(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<CompilerInvocation> ExtractInvocations(string binLogFilePath)
        {
            // Normalize the path
            binLogFilePath = Path.GetFullPath(binLogFilePath);

            if (!File.Exists(binLogFilePath))
            {
                throw new FileNotFoundException(binLogFilePath);
            }

            var lazyResult = m_binlogInvocationMap.GetOrAdd(binLogFilePath, new Lazy<List<CompilerInvocation>>(() =>
            {
                // Use the tree-based reader for all formats. The event-based BinLogReader.Replay()
                // doesn't reliably fire events for newer binlog formats (e.g. MSBuild 18.x / .NET 10).
                return ExtractInvocationsFromBuild(binLogFilePath);
            }));

            var result = lazyResult.Value;

            return result;
        }

        private static List<CompilerInvocation> ExtractInvocationsFromBuild(string logFilePath)
        {
            var build = Microsoft.Build.Logging.StructuredLogger.Serialization.Read(logFilePath);
            var solutionRoot = Path.GetDirectoryName(logFilePath);
            var invocations = new List<CompilerInvocation>();
            build.VisitAllChildren<Microsoft.Build.Logging.StructuredLogger.Task>(t =>
            {
                var invocation = TryGetInvocationFromTask(t, build);
                if (invocation != null && !IsTemporaryWpfProject(invocation.ProjectFilePath))
                {
                    invocation.SolutionRoot = solutionRoot;
                    invocations.Add(invocation);
                }
            });

            return invocations;
        }

        // WPF builds compile a throwaway "<project>_wpftmp.csproj" for temporary XAML-generated sources.
        // Its compiler invocation duplicates the real project under a temp path and only pollutes the
        // index, so drop any invocation whose project file name carries the _wpftmp marker.
        // See https://github.com/KirillOsenkov/SourceBrowser/issues/188.
        public static bool IsTemporaryWpfProject(string projectFilePath)
        {
            return projectFilePath != null &&
                Path.GetFileName(projectFilePath).IndexOf("_wpftmp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static CompilerInvocation TryGetInvocationFromTask(Microsoft.Build.Logging.StructuredLogger.Task task, Microsoft.Build.Logging.StructuredLogger.Build build)
        {
            var name = task.Name;
            if (name != "Csc" && name != "Vbc" || ((task.Parent as Microsoft.Build.Logging.StructuredLogger.Target)?.Name != "CoreCompile"))
            {
                return null;
            }

            var language = name == "Csc" ? LanguageNames.CSharp : LanguageNames.VisualBasic;
            var commandLine = task.CommandLineArguments;
            commandLine = TrimCompilerExeFromCommandLine(commandLine, name == "Csc"
                ? CompilerKind.CSharp
                : CompilerKind.VisualBasic);
            
            // Get the project once and reuse it
            var project = task.GetNearestParent<Microsoft.Build.Logging.StructuredLogger.Project>();
            
            var properties = project?.GetEvaluation(build)?.GetProperties() ?? new Dictionary<string, string>();

            string outputAssemblyPath = null;
            if (properties.TryGetValue("TargetPath", out var targetPath))
            {
                outputAssemblyPath = targetPath;
            }

            var invocation = new CompilerInvocation
            {
                Language = language,
                CommandLineArguments = commandLine,
                ProjectFilePath = project?.ProjectFile,
                OutputAssemblyPath = outputAssemblyPath,
                ProjectProperties = properties,
            };


            return invocation;
        }


        public static string TrimCompilerExeFromCommandLine(string commandLine, CompilerKind language)
        {
            var stringsToTrim = new[]
            {
                "csc.exe ",
                "csc ",
                "vbc.exe ",
                "vbc ",
                "dotnet exec csc.dll ",
                "dotnet.exe exec csc.dll",
                "dotnet exec vbc.dll ",
                "dotnet.exe exec vbc.dll",
            };

            foreach (var trim in stringsToTrim)
            {
                if (commandLine.StartsWith(trim, StringComparison.Ordinal))
                {
                    return commandLine.Substring(trim.Length);
                }
            }

            string TrimHere(int i)
            {
                if (commandLine[i] == '"')
                {
                    i++;
                }

                while (i < commandLine.Length && commandLine[i] == ' ')
                {
                    i++;
                }

                return commandLine.Substring(i);
            }

            {
                // Trim dotnet cli csc or vbc invocation

                var i1 = commandLine.IndexOf("dotnet.exe", StringComparison.OrdinalIgnoreCase);
                if (i1 == -1)
                {
                    i1 = commandLine.IndexOf("dotnet ", StringComparison.OrdinalIgnoreCase);
                }
                var i2 = commandLine.IndexOf(" exec ", StringComparison.OrdinalIgnoreCase);
                var i3 = commandLine.IndexOf("csc.dll", StringComparison.OrdinalIgnoreCase);
                if (i3 == -1)
                {
                    i3 = commandLine.IndexOf("vbc.dll", StringComparison.OrdinalIgnoreCase);
                }

                if (i1 != -1 &&
                    i2 != -1 &&
                    i3 != -1 &&
                    i1 < i2 &&
                    i2 < i3)
                {
                    return TrimHere(i3 + "csc.dll".Length);
                }
            }

            {
                // Trim full path csc or vbc invocation (with or without .exe extension)
                // Handles both Windows (csc.exe) and Linux (bincore/csc) paths

                foreach (var pattern in new[] { "csc.exe", "vbc.exe" })
                {
                    var i1 = commandLine.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                    if (i1 != -1)
                    {
                        return TrimHere(i1 + pattern.Length);
                    }
                }

                // Linux: compiler path ends with /csc or /vbc (no extension)
                foreach (var pattern in new[] { "/csc ", "/vbc " })
                {
                    var i1 = commandLine.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                    if (i1 != -1)
                    {
                        return commandLine.Substring(i1 + pattern.Length);
                    }
                }
            }

            return commandLine;
        }

    }
}