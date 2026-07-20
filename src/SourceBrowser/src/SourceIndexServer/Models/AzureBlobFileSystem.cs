using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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
                            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));

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

            return container.GetBlobsByHierarchy(traits: BlobTraits.None, states: BlobStates.None, delimiter: null, prefix: dirName)
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

        // Streams a byte range of a blob to the destination, used to serve individual reference fragments
        // out of the packed references.pack without downloading the whole (potentially hundreds of MB) blob
        // or buffering the fragment in memory.
        public async Task CopyBytesToAsync(string name, long offset, int length, Stream destination)
        {
            name = name.ToLowerInvariant();
            BlobClient blob = container.GetBlobClient(name);

            var options = new BlobDownloadOptions { Range = new HttpRange(offset, length) };
            var response = await blob.DownloadStreamingAsync(options).ConfigureAwait(false);
            using Stream stream = response.Value.Content;

            await stream.CopyToAsync(destination).ConfigureAwait(false);
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
