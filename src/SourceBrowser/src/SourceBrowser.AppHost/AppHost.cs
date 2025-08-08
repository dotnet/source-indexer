var builder = DistributedApplication.CreateBuilder(args);

// Add Azure Storage for source index files
var storage = builder.AddAzureStorage("storage");

// Add blob storage specifically for the source index
var blobs = storage.AddBlobs("sourceindex-blobs");

// In development, use Azurite emulator with persistent lifetime
if (!builder.ExecutionContext.IsPublishMode)
{
    storage.RunAsEmulator(container =>
    {
        container.WithDataBindMount("data") // Persist data between runs
                 .WithLifetime(ContainerLifetime.Persistent); // Keep container running between AppHost restarts
    });
}

// Add the HtmlGenerator console app that will populate the blob storage
// This app is configured to not start automatically and wait for explicit user action from the dashboard
// Use a resolved full path from the AppHost directory to the Aspire.Hosting project
var aspireProjectPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "..", "submodules", "aspire", "src", "Aspire.Hosting", "Aspire.Hosting.csproj"));
var htmlGenerator = builder.AddProject<Projects.HtmlGenerator>("htmlgenerator", launchProfileName: null)
    .WithReference(blobs)
    .WaitFor(blobs)
    .WaitFor(storage)
    .WithArgs("/azureblob:sourceindex", "/force ", $"{aspireProjectPath}")
    .WithExplicitStart();

// Start with a simple setup - just the SourceIndexServer
var sourceIndexServer = builder.AddProject<Projects.SourceIndexServer>("sourceindexserver")
    .WithReference(blobs)
    .WaitFor(blobs)
    .WaitFor(storage)
    .WithEnvironment("SOURCE_BROWSER_INDEX_PROXY_URL", storage.Resource.IsEmulator 
        ? ReferenceExpression.Create($"{storage.Resource.GetEndpoint("blob").Property(EndpointProperty.Url)}")
        : ReferenceExpression.Create($"{storage.Resource.BlobEndpoint}"))
    .WithEnvironment("SOURCE_BROWSER_ASPIRE_ORCHESTRATED", "true")
    .WithEnvironment("SOURCE_BROWSER_BLOB_CONTAINER_NAME", "sourceindex")

    .WithUrls(context =>
    {
        foreach (var url in context.Urls)
        {
            if (url.Endpoint is not null)
            {
                // Add friendly display names based on the endpoint scheme
                if (url.Endpoint.Scheme == "http")
                {
                    url.DisplayText = "Source Browser (HTTP)";
                }
                else if (url.Endpoint.Scheme == "https")
                {
                    url.DisplayText = "Source Browser (HTTPS)";
                }
            }
        }
    })
    .WithHttpHealthCheck("/health");

builder.Build().Run();