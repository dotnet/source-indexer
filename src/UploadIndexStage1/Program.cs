using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
            string blobContainerSasUrl = null;
            var options = new OptionSet
            {
                {"i=", "The source folder", i => sourceFolder = i},
                {"n=", "The repo name", n => repoName = n},
                {"o=", "The destination blob container url, can also be in the BLOB_CONTAINER_URL environment variable", o => blobContainerSasUrl = o},
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

            if (string.IsNullOrEmpty(blobContainerSasUrl))
            {
                blobContainerSasUrl = Environment.GetEnvironmentVariable("BLOB_CONTAINER_URL");
            }

            if (string.IsNullOrEmpty(blobContainerSasUrl))
            {
                Fatal("Missing argument -o");
            }

            var containerClient = new BlobContainerClient(new Uri(blobContainerSasUrl));
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
                await newBlobClient.UploadAsync(outputFileStream);
            }

            Console.WriteLine("Cleaning up old blobs");
            List<BlobItem> blobs = containerClient.GetBlobs(prefix: repoName + "/").ToList();
            List<BlobItem> toDelete = blobs.OrderByDescending(b => b.Name).Skip(10).ToList();
            foreach (BlobItem d in toDelete)
            {
                Console.WriteLine($"Deleting blob {d.Name}");
                await containerClient.DeleteBlobAsync(d.Name);
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
