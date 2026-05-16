using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.SourceBrowser.SourceIndexServer.Models
{
    public class AzureBlobFileSystem : IFileSystem
    {
        private readonly BlobContainerClient container;
        private readonly TokenCredential credential;

        public AzureBlobFileSystem(string uri)
        {
            // Local-dev path: when AZURE_STORAGE_CONNECTION_STRING is set (e.g. by
            // the Aspire inner-loop AppHost wiring this service to an Azurite
            // emulator), use shared-key auth from the connection string. The
            // container name is still taken from the last path segment of `uri`
            // so the existing SOURCE_BROWSER_INDEX_PROXY_URL contract is preserved.
            string? connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
            if (!string.IsNullOrEmpty(connectionString))
            {
                string containerName = GetContainerNameFromUri(uri);
                container = new BlobContainerClient(connectionString, containerName);
                return;
            }

            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            credential = string.IsNullOrEmpty(clientId)
                            ? new AzureCliCredential()
                            : new ManagedIdentityCredential(clientId);

            container = new BlobContainerClient(new Uri(uri),
                                                credential);
        }

        private static string GetContainerNameFromUri(string uri)
        {
            var parsed = new Uri(uri);
            // Azurite URLs are http://host:port/devstoreaccount1/<container>, real
            // Azure URLs are https://<account>.blob.core.windows.net/<container>.
            // In both cases the last non-empty path segment is the container.
            string[] segments = parsed.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                throw new ArgumentException(
                    $"URI '{uri}' has no path segments; cannot derive container name.",
                    nameof(uri));
            }

            return segments[segments.Length - 1];
        }

        public bool DirectoryExists(string name)
        {
            return true;
        }

        public IEnumerable<string> ListFiles(string dirName)
        {
            dirName = dirName.ToLowerInvariant();
            dirName = dirName.Replace("\\", "/");
            if (!dirName.EndsWith("/", StringComparison.Ordinal))
            {
                dirName += "/";
            }

            return container.GetBlobsByHierarchy(prefix: dirName)
                .Where(item => item.IsBlob)
                .Select(item => item.Blob.Name)
                .ToList();
        }

        public bool FileExists(string name)
        {
            name = name.ToLowerInvariant();
            BlobClient blob = container.GetBlobClient(name);
            
            return blob.Exists();
        }

        public BlobProperties FileProperties(string name)
        {
            name = name.ToLowerInvariant();
            BlobClient blob = container.GetBlobClient(name);

            return blob.GetProperties();
        }

        public Stream OpenSequentialReadStream(string name)
        {
            name = name.ToLowerInvariant();
            BlobClient blob = container.GetBlobClient(name);

            return blob.OpenRead();
        }

        public IEnumerable<string> ReadLines(string name)
        {
            name = name.ToLowerInvariant();
            BlobClient blob = container.GetBlobClient(name);

            using Stream stream = blob.OpenRead();
            using StreamReader reader = new (stream);

            while (!reader.EndOfStream)
            {
                yield return reader.ReadLine();
            }
        }
    }
}
