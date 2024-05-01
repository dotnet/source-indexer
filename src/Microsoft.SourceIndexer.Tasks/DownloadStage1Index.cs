using System;
using System.IO;
using System.Linq;
using System.Text;
using Azure;
using Azure.Identity;
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
        // If specified, auth will attempt to use a specific client ID. If not, auth will use any available
        // credential it finds on your computer, in the order specified by
        // https://learn.microsoft.com/en-us/dotnet/api/azure.identity.defaultazurecredential
        public string ClientId { get; set; }

        // This can be either just an account name, or a full https://XXX.blob.core.windows.net domain
        [Required]
        public string StorageAccount { get; set; }

        // This should be a container name like "stage1"
        [Required]
        public string BlobContainer { get; set; }

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
            if (!StorageAccount.StartsWith("https://"))
            {
                StorageAccount = "https://" + StorageAccount + ".blob.core.windows.net";
            }

            DefaultAzureCredential credential;

            if (string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARM_CLIENT_ID")))
            {
                ClientId = Environment.GetEnvironmentVariable("ARM_CLIENT_ID");
                Log.LogMessage("Found client ID in environment variable; using it");
            }

            if (string.IsNullOrEmpty(ClientId))
            {
                credential = new DefaultAzureCredential(options);
                Log.LogMessage($"Trying to use managed identity without default identity");
            }
            else
            {
                credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = ClientId });
                Log.LogMessage($"Trying to use managed identity with client id: {ClientId}");
            }

            BlobServiceClient blobServiceClient = new(
                new Uri(StorageAccount),
                credential);

            var containerClient = blobServiceClient.GetBlobContainerClient(BlobContainer);
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
            archive.ExtractContents(OutputDirectory, true); // would like this to be false, but SharpZipLib has a bug in 1.3.3
        }
    }
}
