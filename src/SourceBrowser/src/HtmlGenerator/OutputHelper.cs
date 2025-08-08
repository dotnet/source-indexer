using System.IO;
using System.Threading.Tasks;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    /// <summary>
    /// Utility class that provides file operations using the configured output system
    /// </summary>
    public static class OutputHelper
    {
        /// <summary>
        /// Writes text content to a file using the configured output system
        /// </summary>
        public static async Task WriteAllTextAsync(string path, string content)
        {
            if (Program.OutputSystem != null)
            {
                await Program.OutputSystem.WriteAllTextAsync(path, content);
            }
            else
            {
                // Fallback to local file system
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(path, content);
            }
        }

        /// <summary>
        /// Writes binary content to a file using the configured output system
        /// </summary>
        public static async Task WriteAllBytesAsync(string path, byte[] content)
        {
            if (Program.OutputSystem != null)
            {
                await Program.OutputSystem.WriteAllBytesAsync(path, content);
            }
            else
            {
                // Fallback to local file system
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(path, content);
            }
        }

        /// <summary>
        /// Copies a file using the configured output system
        /// </summary>
        public static async Task CopyFileAsync(string sourcePath, string destinationPath)
        {
            if (Program.OutputSystem != null)
            {
                await Program.OutputSystem.CopyFileAsync(sourcePath, destinationPath);
            }
            else
            {
                // Fallback to local file system
                var directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }

        /// <summary>
        /// Creates a directory using the configured output system
        /// </summary>
        public static async Task CreateDirectoryAsync(string path)
        {
            if (Program.OutputSystem != null)
            {
                await Program.OutputSystem.CreateDirectoryAsync(path);
            }
            else
            {
                // Fallback to local file system
                Directory.CreateDirectory(path);
            }
        }

        /// <summary>
        /// Opens a stream for writing using the configured output system
        /// </summary>
        public static async Task<Stream> OpenWriteStreamAsync(string path)
        {
            if (Program.OutputSystem != null)
            {
                return await Program.OutputSystem.OpenWriteStreamAsync(path);
            }
            else
            {
                // Fallback to local file system
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                return File.OpenWrite(path);
            }
        }

        /// <summary>
        /// Synchronous wrapper for WriteAllTextAsync
        /// </summary>
        public static void WriteAllText(string path, string content)
        {
            WriteAllTextAsync(path, content).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronous wrapper for WriteAllBytesAsync
        /// </summary>
        public static void WriteAllBytes(string path, byte[] content)
        {
            WriteAllBytesAsync(path, content).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronous wrapper for CopyFileAsync
        /// </summary>
        public static void CopyFile(string sourcePath, string destinationPath)
        {
            CopyFileAsync(sourcePath, destinationPath).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronous wrapper for CreateDirectoryAsync
        /// </summary>
        public static void CreateDirectory(string path)
        {
            CreateDirectoryAsync(path).GetAwaiter().GetResult();
        }
    }
}
