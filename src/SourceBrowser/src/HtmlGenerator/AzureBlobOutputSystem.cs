using System;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class AzureBlobOutputSystem : IOutputSystem
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly string _containerName;
        private BlobContainerClient _containerClient;

        public AzureBlobOutputSystem(BlobServiceClient blobServiceClient, string containerName)
        {
            _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
            _containerName = containerName ?? throw new ArgumentNullException(nameof(containerName));
        }

        private async Task<BlobContainerClient> GetContainerClientAsync()
        {
            if (_containerClient == null)
            {
                _containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
                
                // Create container if it doesn't exist
                await _containerClient.CreateIfNotExistsAsync();
            }
            
            return _containerClient;
        }

        public Task CreateDirectoryAsync(string path)
        {
            // Blob storage doesn't have directories, so this is a no-op
            return Task.CompletedTask;
        }

        public async Task WriteAllTextAsync(string path, string content)
        {
            var containerClient = await GetContainerClientAsync();
            var blobName = NormalizePath(path);
            var blobClient = containerClient.GetBlobClient(blobName);
            
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            await blobClient.UploadAsync(stream, overwrite: true);
        }

        public async Task WriteAllBytesAsync(string path, byte[] content)
        {
            var containerClient = await GetContainerClientAsync();
            var blobName = NormalizePath(path);
            var blobClient = containerClient.GetBlobClient(blobName);
            
            using var stream = new MemoryStream(content);
            await blobClient.UploadAsync(stream, overwrite: true);
        }

        public async Task CopyFileAsync(string sourcePath, string destinationPath)
        {
            // If source is a local file, read it and upload
            if (File.Exists(sourcePath))
            {
                var content = File.ReadAllBytes(sourcePath);
                await WriteAllBytesAsync(destinationPath, content);
            }
            else
            {
                throw new FileNotFoundException($"Source file not found: {sourcePath}");
            }
        }

        public async Task<bool> FileExistsAsync(string path)
        {
            try
            {
                var containerClient = await GetContainerClientAsync();
                var blobName = NormalizePath(path);
                var blobClient = containerClient.GetBlobClient(blobName);
                
                var response = await blobClient.ExistsAsync();
                return response.Value;
            }
            catch
            {
                return false;
            }
        }

        public Task<bool> DirectoryExistsAsync(string path)
        {
            // Blob storage doesn't have directories, so always return true
            return Task.FromResult(true);
        }

        public async Task<Stream> OpenWriteStreamAsync(string path)
        {
            var containerClient = await GetContainerClientAsync();
            var blobName = NormalizePath(path);
            var blobClient = containerClient.GetBlobClient(blobName);
            
            // For blob storage, we'll return a memory stream that uploads when disposed
            return new BlobWriteStream(blobClient);
        }

        public string NormalizePath(string path)
        {
            var normalized = path;
            
            // Make path relative to SolutionDestinationFolder if it's an absolute path
            if (Path.IsPathRooted(path) && !string.IsNullOrEmpty(Paths.SolutionDestinationFolder))
            {
                var solutionFolder = Path.GetFullPath(Paths.SolutionDestinationFolder);
                var fullPath = Path.GetFullPath(path);
                
                // If the path is under the solution destination folder, make it relative
                if (fullPath.StartsWith(solutionFolder, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = fullPath.Substring(solutionFolder.Length);
                    
                    // Remove leading directory separator
                    if (normalized.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) || 
                        normalized.StartsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    {
                        normalized = normalized.Substring(1);
                    }
                }
            }
            
            // Convert Windows paths to blob-friendly paths
            normalized = normalized.Replace('\\', '/');
            
            // Remove leading slashes
            while (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1);
            }
            
            return normalized;
        }
    }

    /// <summary>
    /// A stream that uploads to blob storage when disposed
    /// </summary>
    internal class BlobWriteStream : MemoryStream
    {
        private readonly BlobClient _blobClient;
        private bool _disposed = false;

        public BlobWriteStream(BlobClient blobClient)
        {
            _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                // Upload the content to blob storage
                Seek(0, SeekOrigin.Begin);
                _blobClient.Upload(this, overwrite: true);
                _disposed = true;
            }
            
            base.Dispose(disposing);
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                // Upload the content to blob storage
                Seek(0, SeekOrigin.Begin);
                _blobClient.Upload(this, overwrite: true);
                _disposed = true;
            }
            
            Dispose();
            return default(ValueTask);
        }
    }
}
