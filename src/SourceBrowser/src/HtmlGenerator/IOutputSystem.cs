using System.IO;
using System.Threading.Tasks;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public interface IOutputSystem
    {
        /// <summary>
        /// Creates a directory at the specified path
        /// </summary>
        Task CreateDirectoryAsync(string path);

        /// <summary>
        /// Writes text content to a file
        /// </summary>
        Task WriteAllTextAsync(string path, string content);

        /// <summary>
        /// Writes binary content to a file
        /// </summary>
        Task WriteAllBytesAsync(string path, byte[] content);

        /// <summary>
        /// Copies a file from source to destination
        /// </summary>
        Task CopyFileAsync(string sourcePath, string destinationPath);

        /// <summary>
        /// Checks if a file exists
        /// </summary>
        Task<bool> FileExistsAsync(string path);

        /// <summary>
        /// Checks if a directory exists
        /// </summary>
        Task<bool> DirectoryExistsAsync(string path);

        /// <summary>
        /// Opens a stream for writing to a file
        /// </summary>
        Task<Stream> OpenWriteStreamAsync(string path);

        /// <summary>
        /// Normalizes a path for the target system
        /// </summary>
        string NormalizePath(string path);
    }
}
