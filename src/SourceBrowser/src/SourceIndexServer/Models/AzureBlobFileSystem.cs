using Azure.Storage.Blobs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.SourceBrowser.SourceIndexServer.Models
{
    public class AzureBlobFileSystem : IFileSystem
    {
        private readonly BlobContainerClient container;

        public AzureBlobFileSystem(string uri)
        {
            container = new BlobContainerClient(new Uri(uri));
        }

        public bool DirectoryExists(string name)
        {
            return true;
        }

        public IEnumerable<string> ListFiles(string dirName)
        {
            dirName = dirName.ToLowerInvariant();
            dirName = dirName.Replace("\\", "/");
            if (!dirName.EndsWith("/"))
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