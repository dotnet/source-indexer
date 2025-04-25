using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Core.Diagnostics;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using Mono.Options;

namespace UploadIndexStage1
{
    class Program
    {
        static async Task Main(string[] args)
        {
            string sourceFolder = null;
            string repoName = null;
            string clientId = null;
            string storageAccount = null;
            string blobContainer = null;
            var options = new OptionSet
            {
                {"i=", "The source folder", i => sourceFolder = i},
                {"n=", "The repo name", n => repoName = n},
                {"c=", "The Azure Client ID (optional)", c => clientId = c},
                {"s=", "The destination storage account name or URL", s => storageAccount = s},
                {"b=", "The destination storage account container", b => blobContainer = b},
            };

            List<string> extra = options.Parse(args);

            if (extra.Any())
            {
                Fatal($"Unexpected argument {extra.First()}");
            }

            if (string.IsNullOrEmpty(sourceFolder))
            {
                Fatal("Missing argument -i");
            }

            if (string.IsNullOrEmpty(repoName))
            {
                Fatal("Missing argument -n");
            }

            if (string.IsNullOrEmpty(storageAccount))
            {
                Fatal("Missing argument -s");
            }

            if (string.IsNullOrEmpty(blobContainer))
            {
                Fatal("Missing argument -b");
            }

            if (!storageAccount.StartsWith("https://"))
            {
                storageAccount = "https://" + storageAccount + ".blob.core.windows.net";
            }

            using AzureEventSourceListener listener = AzureEventSourceListener.CreateConsoleLogger();

            TokenCredential credential;

            if (string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARM_CLIENT_ID")))
            {
                clientId = Environment.GetEnvironmentVariable("ARM_CLIENT_ID");
                System.Console.WriteLine("Found client ID in environment variable; using it");
            }

            if (string.IsNullOrEmpty(clientId))
            {
                credential = new AzureCliCredential();
                System.Console.WriteLine("Trying to use managed identity without default identity");
            }
            else
            {
                System.Console.WriteLine("Trying to use ManagedIdentityCredential with ClientID");
                credential = new ManagedIdentityCredential(clientId);
            }

            BlobServiceClient blobServiceClient = new(
                new Uri(storageAccount),
                credential);

            var containerClient = blobServiceClient.GetBlobContainerClient(blobContainer);
            string newBlobName = $"{repoName}/{DateTime.UtcNow:O}.tar.gz";
            BlobClient newBlobClient = containerClient.GetBlobClient(newBlobName);

            Console.WriteLine($"Uploading folder {sourceFolder} to blob {new UriBuilder(newBlobClient.Uri) {Fragment = "", Query = ""}.Uri.AbsoluteUri}");

            await using (var outputFileStream = new MemoryStream())
            {
                await using (var gzoStream = new GZipOutputStream(outputFileStream){ IsStreamOwner = false })
                using (var tarArchive = TarArchive.CreateOutputTarArchive(gzoStream, Encoding.UTF8))
                {
                    string sourceRoot = Path.GetFullPath(sourceFolder).Replace('\\', '/').TrimEnd('/');

                    void AddEntry(string path)
                    {
                        string normalizedPath = Path.GetFullPath(path).Replace('\\', '/');
                        var e = TarEntry.CreateEntryFromFile(path);
                        e.Name = normalizedPath.Substring(sourceRoot.Length).TrimStart('/');
                        Console.WriteLine($"Adding {path} as {e.Name}");
                        tarArchive.WriteEntry(e, false);
                    }

                    void AddFolder(string path)
                    {
                        AddEntry(path);

                        foreach (string file in Directory.GetFiles(path))
                        {
                            AddEntry(file);
                        }

                        foreach (string dir in Directory.GetDirectories(path))
                        {
                            AddFolder(dir);
                        }
                    }
                    AddFolder(sourceRoot);
                }

                outputFileStream.Position = 0;
                try
                {
                    await newBlobClient.UploadAsync(outputFileStream);
                }
                catch (AuthenticationFailedException e)
                {
                    Fatal($"*** UPLOAD FAILED: {e.Message}");
                }
            }

            Console.WriteLine("Cleaning up old blobs");
            List<BlobItem> blobs = containerClient.GetBlobs(prefix: repoName + "/").ToList();
            List<BlobItem> toDelete = blobs.OrderByDescending(b => b.Name).Skip(10).ToList();
            foreach (BlobItem d in toDelete)
            {
                Console.WriteLine($"Deleting blob {d.Name}");
                try
                {
                    await containerClient.DeleteBlobAsync(d.Name);
                }
                catch (AuthenticationFailedException e)
                {
                    Fatal($"*** CONTAINER \"{d.Name}\" CLEANUP FAILED: {e.Message}");
                }
            }
            Console.WriteLine("Finished.");
        }

        [DoesNotReturn]
        private static void Fatal(string msg)
        {
            Console.Error.WriteLine($"fatal: {msg}");
            Environment.Exit(-1);
        }

    }
}
