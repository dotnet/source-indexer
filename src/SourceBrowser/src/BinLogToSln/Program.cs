using LibGit2Sharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.SourceBrowser.BinLogParser;
using Mono.Options;
using NuGet.Frameworks;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("BinLogToSln.Tests")]

namespace BinLogToSln
{
    class Program
    {
        private static CompilerInvocation SelectBestInvocation(IGrouping<string, CompilerInvocation> invocationGroup)
        {
            var invocations = invocationGroup.ToList();
            if (invocations.Count == 1)
            {
                return invocations[0];
            }

            Console.WriteLine($"Found {invocations.Count} candidates for assembly '{invocationGroup.Key}', selecting best...");

            // Score each invocation based on our criteria
            var scoredInvocations = invocations.Select(inv => new
            {
                Invocation = inv,
                Score = CalculateInvocationScore(inv)
            }).ToList();

            // Select the highest scored invocation
            var best = scoredInvocations.OrderByDescending(x => x.Score).First();
            
            Console.WriteLine($"Selected '{best.Invocation.ProjectFilePath}' (score: {best.Score})");
            return best.Invocation;
        }

        internal static int CalculateInvocationScore(CompilerInvocation invocation)
        {
            int score = 0;

            try
            {
                if (invocation.ProjectProperties is null)
                {
                    Console.WriteLine($"Warning: No project properties for {invocation.ProjectFilePath}.");
                }

                // 1. UseForSourceIndex (highest priority)
                if (invocation.ProjectProperties?.TryGetValue("UseForSourceIndex", out var useForSourceIndex) == true &&
                    bool.TryParse(useForSourceIndex, out var shouldUse) && shouldUse)
                {
                    return int.MaxValue; // Highest possible score
                }

                // 2. Not IsPlatformNotSupportedAssembly (second priority)
                if (invocation.ProjectProperties?.TryGetValue("IsPlatformNotSupportedAssembly", out var isPlatformNotSupported) == true &&
                    bool.TryParse(isPlatformNotSupported, out var isNotSupported) && isNotSupported)
                {
                    score -= 10000; // Heavy penalty for platform not supported assemblies
                }

                // 3. Newest TargetFramework version (third priority)
                if (invocation.ProjectProperties?.TryGetValue("TargetFramework", out var targetFramework) == true &&
                    !string.IsNullOrEmpty(targetFramework))
                {
                    try
                    {
                        var framework = NuGetFramework.Parse(targetFramework);
                        
                        // Prefer newer frameworks (high weight)
                        if (framework.Version != null)
                        {
                            score += (int)(framework.Version.Major * 1000 + framework.Version.Minor * 100);
                        }

                        // 4. Has a platform (fourth priority)
                        // Prefer platform-specific frameworks
                        if (framework.HasPlatform)
                        {
                            score += 500;

                            if (framework.Platform.Equals("linux", StringComparison.OrdinalIgnoreCase))
                            {
                                score += 100; // Linux is preferred over other platforms
                            }
                            else if (framework.Platform.Equals("unix", StringComparison.OrdinalIgnoreCase))
                            {
                                score += 50; // Unix is also preferred, but less than Linux
                            }
                        }

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Could not parse TargetFramework '{targetFramework}': {ex.Message}");
                    }
                }

                // 5. More source files (lowest priority)
                var sourceFiles = invocation.Parsed?.SourceFiles;
                if (sourceFiles.HasValue)
                {
                    int totalSourceFiles = sourceFiles.Value.Length;
                    score += totalSourceFiles; // Lower weight than other factors
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Error calculating score for {invocation.ProjectFilePath}: {ex.Message}");
                // Return a base score so we don't exclude this invocation entirely
                score = 1;
            }

            return score;
        }

        private static bool ShouldExcludeInvocation(CompilerInvocation invocation)
        {
            if (string.IsNullOrEmpty(invocation.ProjectDirectory))
            {
                return true;
            }

            string projectFolder = Path.GetFileName(invocation.ProjectDirectory);
            if (projectFolder == "ref" || projectFolder == "stubs")
            {
                Console.WriteLine($"Skipping Ref Assembly project {invocation.ProjectFilePath}");
                return true;
            }
            
            if (Path.GetFileName(Path.GetDirectoryName(invocation.ProjectDirectory)) == "cycle-breakers")
            {
                Console.WriteLine($"Skipping Wpf Cycle-Breaker project {invocation.ProjectFilePath}");
                return true;
            }
            
            return false;
        }

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

            // Group invocations by assembly name and select the best one for each
            var invocationGroups = invocations
                .Where(invocation => !ShouldExcludeInvocation(invocation))
                .GroupBy(invocation => invocation.AssemblyName)
                .Select(group => SelectBestInvocation(group));

            var processed = new HashSet<string>();
            foreach (CompilerInvocation invocation in invocationGroups)
            {
                if (invocation == null)
                {
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

                (string typeGuid, Guid languageGuid, string languageExtension) = invocation.Language switch
                {
                    LanguageNames.VisualBasic => (VBProjectTypeGuid, VBLanguageGuid, ".vb"),
                    _ => (CSharpProjectTypeGuid, CSharpLanguageGuid, ".cs"),
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
                project.WriteLine("    <_SkipAnalyzers>true</_SkipAnalyzers>"); // we only need source generators
                project.WriteLine($"    <AssemblyName>{invocation.AssemblyName}</AssemblyName>");
                int idx = 1;
                if (invocation.Parsed.CompilationOptions is CSharpCompilationOptions cSharpOptions)
                {
                    if (cSharpOptions.AllowUnsafe)
                    {
                        project.WriteLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
                    }
                    if (cSharpOptions.PublicSign)
                    {
                        project.WriteLine("    <PublicSign>true</PublicSign>");
                    }
                    if (cSharpOptions.CryptoKeyFile != null)
                    {
                        includeFile(cSharpOptions.CryptoKeyFile, out string projectRelativePath, out _);
                        project.WriteLine($"    <KeyOriginatorFile>{projectRelativePath}</KeyOriginatorFile>");
                    }
                }

                if (invocation.Parsed.ParseOptions is CSharpParseOptions cSharpParseOptions)
                {
                    project.WriteLine($"    <LangVersion>{cSharpParseOptions.SpecifiedLanguageVersion}</LangVersion>");
                    project.WriteLine($"    <DefineConstants>{string.Join(";", cSharpParseOptions.PreprocessorSymbolNames)}</DefineConstants>");
                }
                project.WriteLine("  </PropertyGroup>");
                project.WriteLine("  <ItemGroup>");
                foreach (CommandLineSourceFile sourceFile in invocation.Parsed.SourceFiles)
                {
                    includeFile(sourceFile.Path, out string projectRelativePath, out string link);
                    project.WriteLine($"    <Compile Include=\"{projectRelativePath}\"{(link != null ? $" Link=\"{link}\"" : "")}/>");
                }
                project.WriteLine("  </ItemGroup>");
                project.WriteLine("  <ItemGroup>");
                foreach (CommandLineReference reference in invocation.Parsed.MetadataReferences)
                {
                    includeReference("ReferencePath", reference.Reference);
                }
                project.WriteLine("  </ItemGroup>");

                // Add generated files.
                project.WriteLine("  <ItemGroup>");
                foreach (var generatedFile in getGeneratedFiles())
                {
                    string filePath = generatedFile.FilePath;
                    if (!File.Exists(filePath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                        var stream = generatedFile.Stream;
                        stream.Position = 0;
                        using var fileStream = File.OpenWrite(filePath);
                        stream.CopyTo(fileStream);
                    }
                    includeFile(filePath, out string projectRelativePath, out string link);
                    project.WriteLine($"    <Compile Include=\"{projectRelativePath}\"{(link != null ? $" Link=\"{link}\"" : "")}/>");
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

                void includeFile(string originalPath, out string projectRelativePath, out string link)
                {
                    string filePath = Path.GetFullPath(originalPath);
                    string repoRelativePath = Path.GetRelativePath(repoRoot, filePath);
                    string outputFile;
                    link = null;
                    if (repoRelativePath.StartsWith("..\\", StringComparison.Ordinal) || repoRelativePath.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(repoRelativePath))
                    {
                        string externalPath = Path.Join("_external", idx++.ToString(), Path.GetFileName(filePath));
                        // not in the repo dir, treat as external
                        projectRelativePath = Path.Join(Path.GetRelativePath(invocation.ProjectDirectory, repoRoot), "..", externalPath);
                        outputFile = Path.Join(output, externalPath);
                    }
                    else
                    {
                        projectRelativePath = Path.GetRelativePath(invocation.ProjectDirectory, filePath);
                        if (projectRelativePath.StartsWith("..", StringComparison.Ordinal))
                        {
                            link = repoRelativePath;
                        }
                        outputFile = Path.Join(output, "src", repoRelativePath);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
                    if (!File.Exists(outputFile))
                    {
                        File.Copy(filePath, outputFile);
                    }
                }

                void includeReference(string kind, string path)
                {
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"Could not find {kind} '{path}'");
                        return;
                    }

                    string projToRepoPath = Path.GetRelativePath(invocation.ProjectDirectory, repoRoot);
                    string projToOutputPath = Path.Join(projToRepoPath, "..");
                    string refPath = DedupeReference(output, path);
                    project.WriteLine($"    <{kind} Include=\"{Path.Join(projToOutputPath, refPath)}\"/>");
                }

#nullable enable
                // From https://github.com/jaredpar/complog/blob/a629fb3c05e40ebe673410144e8911bd5f86bdf2/src/Basic.CompilerLog.Util/RoslynUtil.cs#L440.
                IEnumerable<(string FilePath, MemoryStream Stream)> getGeneratedFiles()
                {
                    try
                    {
                        if (!invocation.Parsed.EmitPdb)
                        {
                            throw new InvalidOperationException($"{nameof(CommandLineArguments.EmitPdb)} is {false}.");
                        }

                        if (invocation.Parsed.EmitOptions.DebugInformationFormat is not (DebugInformationFormat.Embedded or DebugInformationFormat.PortablePdb) and var format)
                        {
                            throw new InvalidOperationException($"Unsupported {nameof(DebugInformationFormat)}={format}.");
                        }

                        using var reader = File.OpenRead(invocation.OutputAssemblyPath);
                        using var peReader = new PEReader(reader);
                        if (!peReader.TryOpenAssociatedPortablePdb(invocation.OutputAssemblyPath, pdbFileStreamProvider, out var pdbReaderProvider, out var pdbPath))
                        {
                            throw new InvalidOperationException($"Could not open PDB for '{invocation.OutputAssemblyPath}'.");
                        }

                        var pdbReader = pdbReaderProvider!.GetMetadataReader();
                        var generatedFiles = new List<(string FilePath, MemoryStream Stream)>();
                        foreach (var documentHandle in pdbReader.Documents.Skip(invocation.Parsed.SourceFiles.Length))
                        {
                            if (getContentStream(languageGuid, languageExtension, pdbReader, documentHandle) is { } tuple)
                            {
                                generatedFiles.Add(tuple);
                            }
                        }
                        return generatedFiles;
                    }
                    catch (Exception ex)
                    {
                        // We don't want to fail official builds during stage 1, so just log a warning.
                        Console.WriteLine($"##vso[task.logissue type=warning;]Error processing generated files for '{invocation.ProjectFilePath}': {ex}");
                        return [];
                    }

                    static Stream? pdbFileStreamProvider(string filePath)
                    {
                        if (!File.Exists(filePath))
                        {
                            return null;
                        }

                        return File.OpenRead(filePath);
                    }

                    static (string FilePath, MemoryStream Stream)? getContentStream(
                        Guid languageGuid,
                        string languageExtension,
                        MetadataReader pdbReader,
                        DocumentHandle documentHandle)
                    {
                        var document = pdbReader.GetDocument(documentHandle);
                        if (pdbReader.GetGuid(document.Language) != languageGuid)
                        {
                            return null;
                        }

                        var filePath = pdbReader.GetString(document.Name);

                        if (Path.GetExtension(filePath) != languageExtension)
                        {
                            return null;
                        }

                        foreach (var cdiHandle in pdbReader.GetCustomDebugInformation(documentHandle))
                        {
                            var cdi = pdbReader.GetCustomDebugInformation(cdiHandle);
                            if (pdbReader.GetGuid(cdi.Kind) != EmbeddedSourceGuid)
                            {
                                continue;
                            }

                            var hashAlgorithmGuid = pdbReader.GetGuid(document.HashAlgorithm);
                            var hashAlgorithm =
                                hashAlgorithmGuid == HashAlgorithmSha1 ? SourceHashAlgorithm.Sha1
                                : hashAlgorithmGuid == HashAlgorithmSha256 ? SourceHashAlgorithm.Sha256
                                : SourceHashAlgorithm.None;
                            if (hashAlgorithm == SourceHashAlgorithm.None)
                            {
                                continue;
                            }

                            var bytes = pdbReader.GetBlobBytes(cdi.Value);
                            if (bytes is null)
                            {
                                continue;
                            }

                            int uncompressedSize = BitConverter.ToInt32(bytes, 0);
                            var stream = new MemoryStream(bytes, sizeof(int), bytes.Length - sizeof(int));

                            if (uncompressedSize != 0)
                            {
                                var decompressed = new MemoryStream(uncompressedSize);
                                using (var deflateStream = new DeflateStream(stream, CompressionMode.Decompress))
                                {
                                    deflateStream.CopyTo(decompressed);
                                }

                                if (decompressed.Length != uncompressedSize)
                                {
                                    throw new InvalidOperationException("Stream did not decompress to expected size");
                                }

                                stream = decompressed;
                            }

                            stream.Position = 0;
                            return (filePath, stream);
                        }

                        return null;
                    }
                }
#nullable disable
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

        private static readonly string CSharpProjectTypeGuid = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";
        private static readonly string VBProjectTypeGuid = "{F184B08F-C81C-45F6-A57F-5ABD9991F28F}";
        private static readonly Guid CSharpLanguageGuid = new("{3f5162f8-07c6-11d3-9053-00c04fa302a1}");
        private static readonly Guid VBLanguageGuid = new("{3a12d0b8-c26c-11d0-b442-00a0244a1dd2}");
        private static readonly Guid EmbeddedSourceGuid = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
        private static readonly Guid HashAlgorithmSha1 = unchecked(new Guid((int)0xff1816ec, (short)0xaa5e, 0x4d10, 0x87, 0xf7, 0x6f, 0x49, 0x63, 0x83, 0x34, 0x60));
        private static readonly Guid HashAlgorithmSha256 = unchecked(new Guid((int)0x8829d00f, 0x11b8, 0x4213, 0x87, 0x8b, 0x77, 0x0e, 0x85, 0x97, 0xac, 0x16));
    }
}
