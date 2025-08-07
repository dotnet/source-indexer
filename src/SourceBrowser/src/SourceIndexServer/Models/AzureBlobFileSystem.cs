using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.SourceBrowser.SourceIndexServer.Models
{
    public class AzureBlobFileSystem : IFileSystem
    {
        private readonly BlobContainerClient container;
        private TokenCredential credential;
        private string clientId;

        // Service provider for DI resolution when running under Aspire orchestration
        public static IServiceProvider ServiceProvider { get; set; }

        public AzureBlobFileSystem(string uri)
        {
            // Check if running under Aspire orchestration
            if (Environment.GetEnvironmentVariable("SOURCE_BROWSER_ASPIRE_ORCHESTRATED") == "true" && ServiceProvider != null)
            {
                // Use DI-configured BlobServiceClient and get container client from it
                var blobServiceClient = ServiceProvider.GetRequiredService<BlobServiceClient>();
                var containerName = Environment.GetEnvironmentVariable("SOURCE_BROWSER_BLOB_CONTAINER_NAME") ?? "sourceindex";
                container = blobServiceClient.GetBlobContainerClient(containerName);
            }
            else
            {
                // Use the original logic for direct instantiation
                if (string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARM_CLIENT_ID")))
                {
                    clientId = Environment.GetEnvironmentVariable("ARM_CLIENT_ID");
                }

                if (string.IsNullOrEmpty(clientId))
                {
                    credential = new AzureCliCredential();
                }
                else
                {
                    credential = new ManagedIdentityCredential(clientId);
                }

                container = new BlobContainerClient(new Uri(uri), credential);
            }
        }

        // Additional constructor for direct DI injection
        public AzureBlobFileSystem(BlobContainerClient containerClient)
        {
            container = containerClient ?? throw new ArgumentNullException(nameof(containerClient));
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
