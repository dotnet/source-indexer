using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SourceBrowser.Common;
using CompilerInvocation = Microsoft.SourceBrowser.BinLogParser.CompilerInvocation;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class GenerateFromBuildLog
    {
        public static readonly Dictionary<string, string> AssemblyNameToFilePathMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void GenerateInvocation(CompilerInvocation invocation,
            IReadOnlyDictionary<string, string> serverPathMappings = null,
            HashSet<string> processedAssemblyList = null,
            HashSet<string> assemblyNames = null,
            Folder<ProjectSkeleton> solutionExplorerRoot = null,
            Dictionary<(string, string), string> typeForwards = null)
        {
            try
            {
                if (invocation.Language == "TypeScript")
                {
                    Log.Write("TypeScript invocation", ConsoleColor.Magenta);
                    var typeScriptGenerator = new TypeScriptSupport();
                    typeScriptGenerator.Generate(invocation.TypeScriptFiles, Paths.SolutionDestinationFolder);
                }
                else if (invocation.ProjectFilePath != "-")
                {
                    Log.Write(invocation.ProjectFilePath, ConsoleColor.Cyan);
                    var solutionGenerator = new SolutionGenerator(
                        invocation.ProjectFilePath,
                        invocation.CommandLineArguments,
                        invocation.OutputAssemblyPath,
                        invocation.SolutionRoot,
                        Paths.SolutionDestinationFolder,
                        typeForwards);
                    solutionGenerator.ServerPathMappings = serverPathMappings;
                    solutionGenerator.GlobalAssemblyList = assemblyNames;
                    solutionGenerator.Generate(processedAssemblyList, solutionExplorerRoot);
                }
                else
                {
                    Log.Write(invocation.OutputAssemblyPath, ConsoleColor.Magenta);
                    var solutionGenerator = new SolutionGenerator(
                        invocation.OutputAssemblyPath,
                        Paths.SolutionDestinationFolder,
                        typeForwards: typeForwards);
                    solutionGenerator.Generate();
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "Generating invocation: " + invocation.ProjectFilePath + " - " + invocation.OutputAssemblyPath);
            }
        }

        public static IEnumerable<CompilerInvocation> GetAllInvocations(string invocationsFile = null)
        {
            var lines = File.ReadAllLines(invocationsFile);
            for (int i = 0; i < lines.Length; i += 3)
            {
                var compilerInvocation = new CompilerInvocation
                {
                    ProjectFilePath = lines[i],
                    OutputAssemblyPath = lines[i + 1],
                    CommandLineArguments = lines[i + 2],
                    SolutionRoot = "",
                };

                yield return compilerInvocation;
            }
        }

        private static IEnumerable<CompilerInvocation> GetInvocationsToProcess()
        {
            var result = new HashSet<CompilerInvocation>();
            HashSet<string> processedAssemblies = Paths.LoadProcessedAssemblies();

            foreach (var compilerInvocation in GetAllInvocations())
            {
                if (!processedAssemblies.Contains(compilerInvocation.AssemblyName) &&
                    !string.IsNullOrEmpty(compilerInvocation.ProjectFilePath) &&
                    !string.IsNullOrEmpty(compilerInvocation.CommandLineArguments))
                {
                    result.Add(compilerInvocation);
                }
            }

            return result;
        }
    }
}
