using System.IO;
using System.Threading.Tasks;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class LocalFileOutputSystem : IOutputSystem
    {
        public Task CreateDirectoryAsync(string path)
        {
            Directory.CreateDirectory(path);
            return Task.CompletedTask;
        }

        public Task WriteAllTextAsync(string path, string content)
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            File.WriteAllText(path, content);
            return Task.CompletedTask;
        }

        public Task WriteAllBytesAsync(string path, byte[] content)
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            File.WriteAllBytes(path, content);
            return Task.CompletedTask;
        }

        public Task CopyFileAsync(string sourcePath, string destinationPath)
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return Task.CompletedTask;
        }

        public Task<bool> FileExistsAsync(string path)
        {
            return Task.FromResult(File.Exists(path));
        }

        public Task<bool> DirectoryExistsAsync(string path)
        {
            return Task.FromResult(Directory.Exists(path));
        }

        public Task<Stream> OpenWriteStreamAsync(string path)
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            var stream = File.OpenWrite(path);
            return Task.FromResult<Stream>(stream);
        }

        public string NormalizePath(string path)
        {
            return Path.GetFullPath(path);
        }
    }
}
