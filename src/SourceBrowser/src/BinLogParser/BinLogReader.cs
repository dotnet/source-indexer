using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using System;
using System.Collections;
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
                // for old format logs, use the legacy reader - this is less desireable because it loads everything into memory
                if (binLogFilePath.EndsWith(".buildlog", StringComparison.OrdinalIgnoreCase))
                {
                    return ExtractInvocationsFromBuild(binLogFilePath);
                }

                // for new format logs, replay the log to avoid loading everything into memory
                var invocations = new List<CompilerInvocation>();
                var reader = new Microsoft.Build.Logging.StructuredLogger.BinLogReader();
                var taskIdToInvocationMap = new Dictionary<(int, int), CompilerInvocation>();
                var projectEvaluationToPropertiesMap = new Dictionary<int, Dictionary<string, string>>();
                var projectInstanceToEvaluationMap = new Dictionary<int, int>();

                void TryGetInvocationFromEvent(object sender, BuildEventArgs args)
                {
                    Dictionary<string, string> projectProperties = null;
                    if (projectInstanceToEvaluationMap.TryGetValue(args.BuildEventContext?.ProjectInstanceId ?? -1, out var evaluationId) &&
                        projectEvaluationToPropertiesMap.TryGetValue(evaluationId, out var properties))
                    {
                        projectProperties = properties;
                    }

                    var invocation = TryGetInvocationFromRecord(args, taskIdToInvocationMap, projectProperties);
                    if (invocation != null)
                    {
                        invocation.SolutionRoot = Path.GetDirectoryName(binLogFilePath);
                        invocations.Add(invocation);
                    }
                }

                reader.StatusEventRaised += (object sender, BuildStatusEventArgs e) =>
                {
                    if (e?.BuildEventContext?.EvaluationId >= 0 &&
                        e is ProjectEvaluationFinishedEventArgs projectEvalArgs)
                    {
                        if (projectEvalArgs?.Properties is IDictionary<string, string> propertiesDict)
                        {
                            projectEvaluationToPropertiesMap[e.BuildEventContext.EvaluationId] =
                                new Dictionary<string, string>(propertiesDict, StringComparer.OrdinalIgnoreCase);
                        }
                        else
                        {
                            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                            foreach (KeyValuePair<string, string> property in projectEvalArgs.Properties)
                            {
                                properties[property.Key] = property.Value;
                            }

                            projectEvaluationToPropertiesMap[e.BuildEventContext.EvaluationId] = properties;
                        }
                    }
                };

                reader.ProjectStarted += (object sender, ProjectStartedEventArgs e) =>
                {
                    if (e?.BuildEventContext?.EvaluationId >= 0 &&
                        e?.BuildEventContext?.ProjectInstanceId >= 0)
                    {
                        projectInstanceToEvaluationMap[e.BuildEventContext.ProjectInstanceId] = e.BuildEventContext.EvaluationId;
                    }
                };

                reader.TargetStarted += TryGetInvocationFromEvent;
                reader.MessageRaised += TryGetInvocationFromEvent;

                reader.Replay(binLogFilePath);

                return invocations;
            }));

            var result = lazyResult.Value;

            return result;
        }

        private static List<CompilerInvocation> ExtractInvocationsFromBuild(string logFilePath)
        {
            var build = Microsoft.Build.Logging.StructuredLogger.Serialization.Read(logFilePath);
            var invocations = new List<CompilerInvocation>();
            build.VisitAllChildren<Microsoft.Build.Logging.StructuredLogger.Task>(t =>
            {
                var invocation = TryGetInvocationFromTask(t, build);
                if (invocation != null)
                {
                    invocations.Add(invocation);
                }
            });

            return invocations;
        }

        private static CompilerInvocation TryGetInvocationFromRecord(BuildEventArgs args, 
            Dictionary<(int, int), CompilerInvocation> taskIdToInvocationMap,
            Dictionary<string,string> projectProperties)
        {
            int targetId = args.BuildEventContext?.TargetId ?? -1;
            int projectId = args.BuildEventContext?.ProjectInstanceId ?? -1;

            if (targetId < 0)
            {
                return null;
            }

            if (args is TargetStartedEventArgs targetStarted && targetStarted.TargetName == "CoreCompile")
            {
                var invocation = new CompilerInvocation()
                {
                    ProjectFilePath = targetStarted.ProjectFile,
                    ProjectProperties = projectProperties,
                };
                taskIdToInvocationMap[(targetId, projectId)] = invocation;
                return null;
            }

            var commandLine = GetCommandLineFromEventArgs(args, out var language);
            if (commandLine == null)
            {
                return null;
            }

            CompilerInvocation compilerInvocation;
            if (taskIdToInvocationMap.TryGetValue((targetId, projectId), out compilerInvocation))
            {
                compilerInvocation.Language = language == CompilerKind.CSharp ? LanguageNames.CSharp : LanguageNames.VisualBasic;
                compilerInvocation.CommandLineArguments = commandLine;
                Populate(compilerInvocation);
                taskIdToInvocationMap.Remove((targetId, projectId));
            }

            return compilerInvocation;
        }

        private static void Populate(CompilerInvocation compilerInvocation)
        {
            if (compilerInvocation.Language == LanguageNames.CSharp)
            {
                compilerInvocation.OutputAssemblyPath = compilerInvocation.Parsed.GetOutputFilePath(compilerInvocation.Parsed.OutputFileName);
            }
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
            
            var invocation = new CompilerInvocation
            {
                Language = language,
                CommandLineArguments = commandLine,
                ProjectFilePath = project?.ProjectFile,
                ProjectProperties = project?.GetEvaluation(build)?.GetProperties() ?? new Dictionary<string, string>(),
            };


            return invocation;
        }

        public static string TrimCompilerExeFromCommandLine(string commandLine, CompilerKind language)
        {
            var stringsToTrim = new[]
            {
                "csc.exe ",
                "vbc.exe ",
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
                // Trim dotnet cli csc of vbc invocation

                var i1 = commandLine.IndexOf("dotnet.exe", StringComparison.OrdinalIgnoreCase);
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
                // Trim full path csc.exe or vbc.exe invocation

                var i1 = commandLine.IndexOf("csc.exe", StringComparison.OrdinalIgnoreCase);
                if (i1 == -1)
                {
                    i1 = commandLine.IndexOf("vbc.exe", StringComparison.OrdinalIgnoreCase);
                }

                if (i1 != -1)
                {
                    return TrimHere(i1 + "csc.exe".Length);
                }

            }

            return commandLine;
        }

        public static string GetCommandLineFromEventArgs(BuildEventArgs args, out CompilerKind language)
        {
            var task = args as TaskCommandLineEventArgs;
            language = default;
            if (task == null)
            {
                return null;
            }

            var name = task.TaskName;
            if (name != "Csc" && name != "Vbc")
            {
                return null;
            }

            language = name == "Csc" ? CompilerKind.CSharp : CompilerKind.VisualBasic;
            var commandLine = task.CommandLine;
            commandLine = TrimCompilerExeFromCommandLine(commandLine, language);
            return commandLine;
        }
    }
}