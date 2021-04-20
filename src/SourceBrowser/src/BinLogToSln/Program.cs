using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using LibGit2Sharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.SourceBrowser.BinLogParser;
using Mono.Options;

namespace BinLogToSln
{
    class Program
    {
        static void Main(string[] args)
        {
            string binlog = null;
            string repoRoot = null;
            string output = null;
            string solutionName = "output";
            var opts = new OptionSet
            {
                {"i=", "The input binlog", i => binlog = i},
                {"r=", "The repository root", r => repoRoot = r},
                {"o=", "The output directory", o => output = o},
                {"n=", "The solution name", n => solutionName = n},
            };
            List<string> extra = opts.Parse(args);
            if (extra.Any())
            {
                Fatal($"Unexpected argument {extra.First()}");
            }

            if (string.IsNullOrEmpty(binlog))
            {
                Fatal("Missing argument -i");
            }
            if (string.IsNullOrEmpty(repoRoot))
            {
                Fatal("Missing argument -r");
            }
            if (string.IsNullOrEmpty(output))
            {
                Fatal("Missing argument -o");
            }

            solutionName = solutionName.Replace('/', '-').Replace('\\', '-');

            string repoDir = Repository.Discover(repoRoot);
            if (string.IsNullOrEmpty(repoDir))
            {
                Fatal($"Parameter {repoRoot} is not inside a git repo.");
            }
            var repo = new Repository(repoDir);
            string currentSha = repo.Head.Tip.Sha;

            Console.WriteLine($"Processing binlog {binlog} into solution at directory {output}");

            Directory.CreateDirectory(output);

            {
                string hashFilePath = Path.Join(output, "hash");
                using var hashFile = new FileStream(hashFilePath, FileMode.Create);
                using var hash = new StreamWriter(hashFile);
                hash.WriteLine(currentSha);
            }

            string slnFilePath = Path.Join(output, solutionName + ".sln");
            using var slnFile = new FileStream(slnFilePath, FileMode.Create);
            using var sln = new StreamWriter(slnFile);
            WriteSolutionHeader(sln);

            IEnumerable<CompilerInvocation> invocations = BinLogCompilerInvocationsReader.ExtractInvocations(binlog);
            var processed = new HashSet<string>();
            foreach (CompilerInvocation invocation in invocations)
            {
                if (string.IsNullOrEmpty(invocation.ProjectDirectory))
                {
                    continue;
                }

                if (Path.GetFileName(invocation.ProjectDirectory) == "ref")
                {
                    Console.WriteLine($"Skipping Ref Assembly project {invocation.ProjectFilePath}");
                    continue;
                }

                if (Path.GetFileName(Path.GetDirectoryName(invocation.ProjectDirectory)) == "cycle-breakers")
                {
                    Console.WriteLine($"Skipping Wpf Cycle-Breaker project {invocation.ProjectFilePath}");
                    continue;
                }

                if (!processed.Add(invocation.ProjectFilePath))
                {
                    continue;
                }

                if (!processed.Add(Path.GetFileNameWithoutExtension(invocation.ProjectFilePath)))
                {
                    continue;
                }
                Console.WriteLine($"Converting Project: {invocation.ProjectFilePath}");

                string repoRelativeProjectPath = Path.GetRelativePath(repoRoot, invocation.ProjectFilePath);
                string projectFilePath = Path.Join(output, "src", repoRelativeProjectPath);
                string projectName = Path.GetFileNameWithoutExtension(projectFilePath);
                string projectDirectory = Path.GetDirectoryName(projectFilePath);
                Directory.CreateDirectory(projectDirectory);
                using var projFile = new FileStream(projectFilePath, FileMode.Create);
                using var project = new StreamWriter(projFile);

                string typeGuid = invocation.Language switch
                {
                    LanguageNames.CSharp => CSharpProjectTypeGuid,
                    LanguageNames.VisualBasic => VBProjectTypeGuid,
                    _ => CSharpProjectTypeGuid,
                };
                sln.WriteLine($"Project(\"{typeGuid}\") = \"{projectName}\", \"{Path.Join("src", repoRelativeProjectPath)}\", \"{GetProjectGuid()}\"");
                sln.WriteLine("EndProject");
                project.WriteLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
                project.WriteLine("  <PropertyGroup>");
                project.WriteLine("    <TargetFramework>netcoreapp2.1</TargetFramework>");
                project.WriteLine("    <EnableDefaultItems>false</EnableDefaultItems>");
                project.WriteLine("    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
                project.WriteLine("    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>");
                project.WriteLine("    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>");
                project.WriteLine($"    <AssemblyName>{invocation.AssemblyName}</AssemblyName>");
                if (invocation.Parsed.CompilationOptions is CSharpCompilationOptions cSharpOptions)
                {
                    if (cSharpOptions.AllowUnsafe)
                    {
                        project.WriteLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
                    }
                }

                if (invocation.Parsed.ParseOptions is CSharpParseOptions cSharpParseOptions)
                {
                    project.WriteLine($"    <LangVersion>{cSharpParseOptions.SpecifiedLanguageVersion}</LangVersion>");
                    project.WriteLine($"    <DefineConstants>{string.Join(";", cSharpParseOptions.PreprocessorSymbolNames)}</DefineConstants>");
                }
                project.WriteLine("  </PropertyGroup>");
                project.WriteLine("  <ItemGroup>");
                int idx = 1;
                foreach (CommandLineSourceFile sourceFile in invocation.Parsed.SourceFiles)
                {
                    string filePath = Path.GetFullPath(sourceFile.Path);
                    string repoRelativePath = Path.GetRelativePath(repoRoot, filePath);
                    string projectRelativePath;
                    string outputFile;
                    if (repoRelativePath.StartsWith("..\\") || repoRelativePath.StartsWith("../") || Path.IsPathRooted(repoRelativePath))
                    {
                        string externalPath = Path.Join("_external", idx++.ToString(), Path.GetFileName(filePath));
                        // not in the repo dir, treat as external
                        projectRelativePath = Path.Join(Path.GetRelativePath(invocation.ProjectDirectory, repoRoot), "..", externalPath);
                        outputFile = Path.Join(output, externalPath);
                    }
                    else
                    {
                        projectRelativePath = Path.GetRelativePath(invocation.ProjectDirectory, filePath);
                        outputFile = Path.Join(output, "src", repoRelativePath);
                    }
                    project.WriteLine($"    <Compile Include=\"{projectRelativePath}\"/>");
                    Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
                    if (!File.Exists(outputFile))
                    {
                        File.Copy(filePath, outputFile);
                    }
                }
                project.WriteLine("  </ItemGroup>");
                project.WriteLine("  <ItemGroup>");
                foreach (CommandLineReference reference in invocation.Parsed.MetadataReferences)
                {
                    string path = reference.Reference;
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"Could not find reference '{path}'");
                        continue;
                    }
                    string projToRepoPath = Path.GetRelativePath(invocation.ProjectDirectory, repoRoot);
                    string projToOutputPath = Path.Join(projToRepoPath, "..");
                    string refPath = DedupeReference(output, path);
                    project.WriteLine($"    <ReferencePath Include=\"{Path.Join(projToOutputPath, refPath)}\"/>");
                }
                project.WriteLine("  </ItemGroup>");
                project.WriteLine("</Project>");
                if (!string.IsNullOrEmpty(invocation.OutputAssemblyPath))
                {
                    string outputFilePath = Path.Join(projectDirectory, "bin", "Debug", "netcoreapp2.1",
                        Path.GetFileName(invocation.OutputAssemblyPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath));
                    File.Copy(invocation.OutputAssemblyPath, outputFilePath, true);
                }
            }

            Console.WriteLine("Finished");
        }

        private static readonly Dictionary<string, string> dedupedReferences = new Dictionary<string, string>();
        private static string DedupeReference(string output, string referencePath)
        {
            if (dedupedReferences.ContainsKey(referencePath))
            {
                return dedupedReferences[referencePath];
            }
            using var file = new FileStream(referencePath, FileMode.Open, FileAccess.Read);
            using var reader = new PEReader(file);
            MetadataReader mdReader = reader.GetMetadataReader();
            GuidHandle handle = mdReader.GetModuleDefinition().Mvid;
            Guid mvid = mdReader.GetGuid(handle);
            string refPath = Path.Join(output, "ref", mvid.ToString("N"), Path.GetFileName(referencePath));
            if (!File.Exists(refPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(refPath));
                File.Copy(referencePath, refPath);
            }

            return dedupedReferences[referencePath] = Path.GetRelativePath(output, refPath);
        }

        [DoesNotReturn]
        private static void Fatal(string msg)
        {
            Console.Error.WriteLine($"fatal: {msg}");
            Environment.Exit(-1);
        }

        private static void WriteSolutionHeader(StreamWriter sln)
        {
            sln.WriteLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            sln.WriteLine("# Visual Studio Version 16");
            sln.WriteLine("VisualStudioVersion = 16.0.28701.123");
            sln.WriteLine("MinimumVisualStudioVersion = 10.0.40219.1");
        }

        private static string GetProjectGuid()
        {
            return Guid.NewGuid().ToString("B");
        }

        private static string CSharpProjectTypeGuid = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";
        private static string VBProjectTypeGuid = "{F184B08F-C81C-45F6-A57F-5ABD9991F28F}";
    }
}
