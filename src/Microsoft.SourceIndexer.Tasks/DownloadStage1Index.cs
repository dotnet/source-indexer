using System;
using System.IO;
using System.Linq;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.SourceIndexer.Tasks
{
    public class DownloadStage1Index : Task
    {
        [Required]
        public string BlobContainerSasUrl { get; set; }

        [Required]
        public string RepoName { get; set; }

        [Required]
        public string OutputDirectory { get; set; }

        public override bool Execute()
        {
            try
            {
                ExecuteCore();
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, true);
            }
            return !Log.HasLoggedErrors;
        }

        private void ExecuteCore()
        {
            var containerClient = new BlobContainerClient(new Uri(BlobContainerSasUrl));
            Pageable<BlobItem> blobs = containerClient.GetBlobs(prefix: RepoName + "/");
            BlobItem newest = blobs.OrderByDescending(b => b.Name).FirstOrDefault();
            if (newest == null)
            {
                Log.LogError($"Unable to find stage1 output for repo {RepoName}");
                return;
            }

            BlobClient blobClient = containerClient.GetBlobClient(newest.Name);
            var loggableUrl = new UriBuilder(blobClient.Uri) {Fragment = "", Query = ""};
            Log.LogMessage($"Extracting {loggableUrl} to {OutputDirectory}");
            using Stream fileStream = blobClient.OpenRead();
            using var input = new GZipInputStream(fileStream);
            using var archive = TarArchive.CreateInputTarArchive(input, Encoding.UTF8);
            archive.ExtractContents(OutputDirectory, false);
        }
    }
}