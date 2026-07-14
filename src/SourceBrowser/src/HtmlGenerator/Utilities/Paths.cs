using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public static class Paths
    {
        private static string solutionDestinationFolder;
        public static string SolutionDestinationFolder
        {
            get { return solutionDestinationFolder; }
            set { solutionDestinationFolder = value.MustBeAbsolute(); }
        }

        /// <summary>
        /// The finalized, servable website root that Pass2 (<see cref="SolutionFinalizer"/>) writes into.
        /// This is deliberately a different folder than <see cref="SolutionDestinationFolder"/> (Pass1's raw,
        /// per-assembly index), so that Pass1's output stays a pure, re-derivable artifact that Pass2 never
        /// mutates in place -- Pass2 copies each assembly's Pass1 folder here before patching/finalizing it.
        /// </summary>
        private static string websiteDestinationFolder;
        public static string WebsiteDestinationFolder
        {
            get { return websiteDestinationFolder; }
            set { websiteDestinationFolder = value.MustBeAbsolute(); }
        }

        public static string ProcessedAssemblies
        {
            get
            {
                string root = SolutionDestinationFolder ?? Common.Paths.BaseAppFolder;

                return Path.Combine(root, "ProcessedAssemblies.txt");
            }
        }

        public static HashSet<string> LoadProcessedAssemblies()
        {
            return File.Exists(Paths.ProcessedAssemblies)
                ? new HashSet<string>(File.ReadAllLines(Paths.ProcessedAssemblies), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public static string AssemblyPathsFile => Path.Combine(Microsoft.SourceBrowser.Common.Paths.BaseAppFolder, Constants.AssemblyPaths);

        public static void PrepareDestinationFolder(bool forceOverwrite = false, bool incremental = false)
        {
            if (!Configuration.CreateFoldersOnDisk &&
                !Configuration.WriteDocumentsToDisk &&
                !Configuration.WriteProjectAuxiliaryFilesToDisk)
            {
                return;
            }

            if (incremental)
            {
                // Incremental runs deliberately do not wipe the destination -- the whole point is to let
                // Pass1 (via ProjectStaleness) and Pass2 detect which per-assembly output is still valid
                // and reuse it. Just make sure the folder exists.
                Directory.CreateDirectory(SolutionDestinationFolder);
                return;
            }

            if (Directory.Exists(SolutionDestinationFolder))
            {
                if (!forceOverwrite)
                {
                    Log.Write(string.Format("Warning, {0} will be deleted! Are you sure? (y/n)", SolutionDestinationFolder), ConsoleColor.Red);
                    var ch = Console.ReadKey().KeyChar;
                    if (ch != 'y')
                    {
                        if (!File.Exists(Paths.ProcessedAssemblies))
                        {
                            Console.WriteLine($"You pressed '{ch}', exiting.");
                            Environment.Exit(0);
                        }

                        Log.Write("Would you like to continue previously aborted index operation where it left off?", ConsoleColor.Green);
                        if (Console.ReadKey().KeyChar != 'y')
                        {
                            Environment.Exit(0);
                        }
                        else
                        {
                            return;
                        }
                    }
                    else
                    {
                        Console.WriteLine();
                    }
                }

                Log.Write("Deleting " + SolutionDestinationFolder);
                try
                {
                    Directory.Delete(SolutionDestinationFolder, recursive: true);
                }
                catch (Exception)
                {
                }
            }

            Directory.CreateDirectory(SolutionDestinationFolder);
        }

        public static bool IsOrContains(string path, string possibleDescendent)
        {
            return EnsureTrailingSlash(possibleDescendent).StartsWith(EnsureTrailingSlash(path), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns a path to <paramref name="filePath"/> if you start in a folder where the file
        /// <paramref name="relativeToPath"/> is located.
        /// </summary>
        /// <param name="filePath">C:\A\B\1.txt</param>
        /// <param name="relativeToPath">C:\C\D\2.txt</param>
        /// <returns>..\..\A\B\1.txt</returns>
        public static string MakeRelativeToFile(string filePath, string relativeToPath)
        {
            relativeToPath = Path.GetDirectoryName(relativeToPath);
            string result = MakeRelativeToFolder(filePath, relativeToPath);
            return result;
        }

        /// <summary>
        /// Returns a path to <paramref name="filePath"/> if you start in folder <paramref name="relativeToPath"/>.
        /// </summary>
        /// <param name="filePath">C:\A\B\1.txt</param>
        /// <param name="relativeToPath">C:\C\D</param>
        /// <returns>..\..\A\B\1.txt</returns>
        public static string MakeRelativeToFolder(string filePath, string relativeToPath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            if (string.IsNullOrEmpty(relativeToPath))
            {
                throw new ArgumentNullException(nameof(relativeToPath));
            }

            // the file is on a different drive
            if (filePath[0] != relativeToPath[0])
            {
                // better than crashing
                return Path.GetFileName(filePath);
            }

            if (relativeToPath.EndsWith("\\", StringComparison.Ordinal))
            {
                relativeToPath = relativeToPath.TrimEnd('\\');
            }

            StringBuilder result = new StringBuilder();
            while (!IsOrContains(relativeToPath, filePath))
            {
                result.Append(@"..\");
                relativeToPath = Path.GetDirectoryName(relativeToPath);
            }

            if (filePath.Length > relativeToPath.Length)
            {
                filePath = filePath.Substring(relativeToPath.Length);
                if (filePath.StartsWith("\\", StringComparison.Ordinal))
                {
                    filePath = filePath.Substring(1);
                }

                result.Append(filePath);
            }

            return result.ToString();
        }

        /// <summary>
        /// Resolves a unique relative destination path per item, given each item's (possibly
        /// colliding) logical relative path and a physical-identity key (e.g. <c>document.FilePath</c>).
        /// Two items that share both the same relative path AND the same identity key are treated
        /// as the genuine same shared/linked file surfaced more than once -- they keep the original,
        /// un-suffixed path so today's single-render-wins behavior for legitimate shared files is
        /// preserved. Items that share only the relative path but have distinct identity keys are a
        /// real name collision (e.g. two unrelated "IEnumerable.cs" files landing in the same
        /// logical folder) and get a deterministic suffix appended to the file name, ordered by
        /// identity key so the assignment doesn't depend on parallel generation scheduling.
        /// </summary>
        /// <param name="relativePaths">The relative path computed for each item, e.g. via <see cref="GetRelativeFilePathInProject(Document)"/>.</param>
        /// <param name="identityKeys">A key identifying the physical source of each item, e.g. <c>document.FilePath</c>.</param>
        /// <returns>An array parallel to the inputs with a unique relative path per distinct identity.</returns>
        public static string[] DisambiguateRelativePaths(IReadOnlyList<string> relativePaths, IReadOnlyList<string> identityKeys)
        {
            if (relativePaths.Count != identityKeys.Count)
            {
                throw new ArgumentException("relativePaths and identityKeys must have the same length");
            }

            var result = new string[relativePaths.Count];

            var groupsByRelativePath = Enumerable.Range(0, relativePaths.Count)
                .GroupBy(i => relativePaths[i], StringComparer.OrdinalIgnoreCase);

            foreach (var group in groupsByRelativePath)
            {
                var groupsByIdentity = group
                    .GroupBy(i => identityKeys[i], StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToArray();

                for (int rank = 0; rank < groupsByIdentity.Length; rank++)
                {
                    var disambiguatedPath = rank == 0
                        ? group.Key
                        : AppendDisambiguatingSuffix(group.Key, rank + 1);

                    foreach (var index in groupsByIdentity[rank])
                    {
                        result[index] = disambiguatedPath;
                    }
                }
            }

            return result;
        }

        private static string AppendDisambiguatingSuffix(string relativePath, int occurrence)
        {
            var directory = Path.GetDirectoryName(relativePath);
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(relativePath);
            var extension = Path.GetExtension(relativePath);
            var disambiguatedFileName = fileNameWithoutExtension + "_" + occurrence + extension;
            return string.IsNullOrEmpty(directory)
                ? disambiguatedFileName
                : Path.Combine(directory, disambiguatedFileName);
        }

        public static string GetRelativeFilePathInProject(Document document)
        {
            var folders = document.Folders;

            if (folders.Count == 0 && document.FilePath != null && document is SourceGeneratedDocument)
            {
                var parts = document.FilePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3) // Last 3 directories: generator assembly name, generator full class name, hint name
                {
                    folders = ["Generated", ..parts.Skip(parts.Length - 3).Take(2)];
                }
            }

            string result = Path.Combine(folders
                .Select(SanitizeFolder)
                .ToArray());

            string fileName;
            if (document.FilePath != null && !document.GetLinkedDocumentIds().Any())
            {
                fileName = Path.GetFileName(document.FilePath);
            }
            else
            {
                fileName = document.Name;
            }

            result = Path.Combine(result, fileName);

            return result;
        }

        private static char[] invalidFileChars = Path.GetInvalidFileNameChars();
        private static char[] invalidPathChars = Path.GetInvalidPathChars();

        public static string SanitizeFileName(string fileName)
        {
            return ReplaceInvalidChars(fileName, invalidFileChars);
        }

        private static string ReplaceInvalidChars(string fileName, char[] invalidChars)
        {
            var sb = new StringBuilder(fileName.Length);
            for (int i = 0; i < fileName.Length; i++)
            {
                if (invalidChars.Contains(fileName[i]))
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(fileName[i]);
                }
            }

            return sb.ToString();
        }

        public static string SanitizeFolder(string folderName)
        {
            string result = folderName;

            if (folderName == ".")
            {
                result = "current";
            }
            else if (folderName == "..")
            {
                result = "parent";
            }
            else if (folderName.EndsWith(":", StringComparison.Ordinal))
            {
                result = folderName.TrimEnd(':');
            }
            else
            {
                result = folderName;
            }

            result = ReplaceInvalidChars(result, invalidPathChars);
            return result;
        }

        private static bool IsValidFolder(string folderName)
        {
            return !string.IsNullOrEmpty(folderName) &&
                folderName != "." &&
                folderName != ".." &&
                !folderName.EndsWith(":", StringComparison.Ordinal);
        }

        public static string GetRelativePathInProject(SyntaxTree syntaxTree, Project project)
        {
            var document = project.GetDocument(syntaxTree);
            return GetRelativeFilePathInProject(document);
        }

        public static string EnsureTrailingSlash(this string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            if (!path.EndsWith("\\", StringComparison.Ordinal))
            {
                path += "\\";
            }

            return path;
        }

        public static string GetCssPathFromFile(string solutionDestinationPath, string fileName)
        {
            string result = MakeRelativeToFile(solutionDestinationPath, fileName);
            result = Path.Combine(result, "styles.css");
            result = result.Replace('\\', '/');
            return result;
        }

        public static string GetMD5Hash(string input, int digits)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = MD5.HashData(bytes);
            return Serialization.ByteArrayToHexString(hashBytes, digits);
        }

        public static ulong GetMD5HashULong(string input, int digits)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = MD5.HashData(bytes);
            return BitConverter.ToUInt64(hashBytes, 0);
        }

        public static string StripExtension(string fileName)
        {
            return Path.ChangeExtension(fileName, null);
        }

        public static string GetDocumentDestinationPath(Document document, string projectDestinationFolder)
        {
            var documentRelativeFilePathWithoutHtmlExtension = GetRelativeFilePathInProject(document);
            var documentDestinationFilePath = Path.Combine(projectDestinationFolder, documentRelativeFilePathWithoutHtmlExtension) + ".html";
            return documentDestinationFilePath;
        }

        public static string CalculateRelativePathToRoot(string filePath, string rootFolder)
        {
            var relativePath = filePath.Substring(rootFolder.Length + 1);
            var depth = relativePath.Count(c => c == '\\') + relativePath.Count(c => c == '/');
            var sb = new StringBuilder();
            for (int i = 0; i < depth; i++)
            {
                sb.Append("../");
            }

            return sb.ToString();
        }

        /// <summary>
        /// This makes sure that a filePath that can be outside the folder is replanted inside the folder.
        /// This is important when a project references a file outside the project cone and we want to
        /// display it as if it is inside the project.
        /// </summary>
        public static string GetFullPathInFolderCone(string folder, string filePath)
        {
            if (!Path.IsPathRooted(filePath))
            {
                filePath = Path.Combine(folder, filePath);
            }

            return GetFullPathInFolderConeForRootedFilePath(folder, filePath);
        }

        private static string GetFullPathInFolderConeForRootedFilePath(string folder, string rootedFilePath)
        {
            folder = Path.GetFullPath(folder);
            rootedFilePath = Path.GetFullPath(rootedFilePath);
            if (rootedFilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                return rootedFilePath;
            }

            var folderParts = folder.Split(Path.DirectorySeparatorChar);
            var rootedFilePathParts = rootedFilePath.Split(Path.DirectorySeparatorChar);
            int commonParts = 0;
            for (int i = 0; i < Math.Min(folderParts.Length, rootedFilePathParts.Length); i++)
            {
                if (string.Equals(folderParts[i], rootedFilePathParts[i], StringComparison.OrdinalIgnoreCase))
                {
                    commonParts++;
                }
                else
                {
                    break;
                }
            }

            var relativePath = string.Join(Path.DirectorySeparatorChar.ToString(), rootedFilePathParts.Skip(commonParts));
            relativePath = relativePath.Replace(":", "");
            rootedFilePath = Path.Combine(folder, relativePath);
            return rootedFilePath;
        }
    }
}
