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
            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            credential = string.IsNullOrEmpty(clientId)
                            ? new AzureCliCredential()
                            : new ManagedIdentityCredential(clientId);

            container = new BlobContainerClient(new Uri(uri),
                                                credential);
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
