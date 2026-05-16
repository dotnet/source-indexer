using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = DistributedApplication.CreateBuilder(args);

// =============================================================================
// Storage — emulates the two prod storage accounts via Azurite.
//
// Prod has TWO storage accounts:
//   netsourceindexstage1  ← V2 upstream repos push <repo>/<ts>.tar.gz here
//   netsourceindexprod    ← HtmlGenerator's output is uploaded as index-<guid>/
//
// We model each as its own AddAzureStorage().RunAsEmulator() (= two Azurite
// containers) so the boundary is visually distinct in the dashboard.
// =============================================================================

var stage1Storage = builder.AddAzureStorage("stage1Storage")
    .RunAsEmulator(azurite =>
    {
        azurite.WithLifetime(ContainerLifetime.Persistent)
               .WithDataVolume("source-indexer-stage1-data");
    });

var stage1Blobs = stage1Storage.AddBlobs("stage1-blobs");
var stage1Container = stage1Storage.AddBlobContainer("stage1", blobContainerName: "stage1");

var prodStorage = builder.AddAzureStorage("prodStorage")
    .RunAsEmulator(azurite =>
    {
        azurite.WithLifetime(ContainerLifetime.Persistent)
               .WithDataVolume("source-indexer-prod-data");
    });

var prodBlobs = prodStorage.AddBlobs("prod-blobs");
var indexContainer = prodStorage.AddBlobContainer("index-local", blobContainerName: "index-local");

// =============================================================================
// Pipeline resources — all WithExplicitStart() so they only run when the user
// clicks "Start" in the dashboard (or via the bootstrap-all composite command).
// They emulate the prod stages from azure-pipelines.yml / src/index/index.proj.
// =============================================================================

string repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
string sampleProj = Path.Combine(repoRoot, "samples", "MiniRuntime", "MiniRuntime.csproj");
string sampleBinDir = Path.Combine(repoRoot, "samples", "MiniRuntime", "bin", "sample");
string sampleBinlog = Path.Combine(sampleBinDir, "msbuild.binlog");
string sampleSourceDir = Path.Combine(repoRoot, "samples", "MiniRuntime");
string indexOutDir = Path.Combine(repoRoot, "bin", "index");
string indexUploadDir = Path.Combine(indexOutDir, "index");
string htmlGeneratorProj = Path.Combine(repoRoot, "src", "SourceBrowser", "src", "HtmlGenerator", "HtmlGenerator.csproj");

// 1. sample-build — `dotnet build /bl:` on MiniRuntime, producing a binlog.
//    Emulates: Arcade-driven V1 repo build that produces a binlog.
var sampleBuild = builder.AddExecutable(
        "sample-build",
        "dotnet",
        repoRoot,
        "build",
        sampleProj,
        $"/bl:{sampleBinlog}",
        "-c", "Debug")
    .WithExplicitStart();

// 2. upload-stage1 — runs the real UploadIndexStage1 tool against Azurite.
//    Emulates: V2 upstream repos uploading their source bundle to
//    netsourceindexstage1/stage1/<repo>/<ts>.tar.gz.
//    This is the same .NET console app that runs in upstream pipelines, so
//    you can attach a debugger to it directly from the Aspire dashboard.
var uploadStage1 = builder.AddProject<Projects.UploadIndexStage1>("upload-stage1")
    .WithExplicitStart()
    .WithReference(stage1Blobs)
    .WaitFor(stage1Container)
    .WithEnvironment("AZURE_STORAGE_CONNECTION_STRING", stage1Blobs.Resource.ConnectionStringExpression)
    .WithArgs(
        "-i", sampleSourceDir,
        "-n", "MiniRuntime",
        "-b", "stage1");

// 3. htmlgenerator — runs HtmlGenerator (net472) over the binlog from step 1
//    and produces static HTML under bin/index/. Modelled via AddExecutable
//    (`dotnet run --project HtmlGenerator.csproj`) because net472 can't be
//    referenced from a net10 AppHost project, but `dotnet run` still works
//    on Windows. Args mirror the /in: and /out: shape from index.proj.
var htmlGenerator = builder.AddExecutable(
        "htmlgenerator",
        "dotnet",
        repoRoot,
        "run",
        "--project", htmlGeneratorProj,
        "-c", "Debug",
        "--",
        sampleBinlog,
        $"/out:{indexOutDir}",
        $"/serverPath:{sampleSourceDir}=https://github.com/dotnet/source-indexer/tree/main/samples/MiniRuntime/")
    .WithExplicitStart();

// 4. publish-index — wraps the Azure CLI (matching prod's AzureFileCopy@6
//    task). Uploads bin/index/index/* to prodStorage/index-local.
var publishIndex = builder.AddExecutable(
        "publish-index",
        "az",
        repoRoot,
        "storage", "blob", "upload-batch",
        "-s", indexUploadDir,
        "-d", "index-local",
        "--overwrite",
        "true")
    .WithExplicitStart()
    .WithReference(prodBlobs)
    .WaitFor(indexContainer)
    .WithEnvironment(ctx =>
    {
        // The az CLI reads AZURE_STORAGE_CONNECTION_STRING natively.
        ctx.EnvironmentVariables["AZURE_STORAGE_CONNECTION_STRING"] =
            prodBlobs.Resource.ConnectionStringExpression;
    });

// =============================================================================
// Web — SourceIndexServer, the real app. Auto-starts and serves indexed HTML
// out of prodStorage/index-local via SOURCE_BROWSER_INDEX_PROXY_URL (the same
// env var contract prod uses; see deployment/deploy-storage-proxy.ps1).
// =============================================================================

var web = builder.AddProject<Projects.SourceIndexServer>("web")
    .WithReference(prodBlobs)
    .WaitFor(indexContainer)
    .WithEnvironment("AZURE_STORAGE_CONNECTION_STRING", prodBlobs.Resource.ConnectionStringExpression)
    .WithEnvironment("SOURCE_BROWSER_INDEX_PROXY_URL", indexContainer.Resource.ConnectionStringExpression)
    .WithExternalHttpEndpoints();

// =============================================================================
// bootstrap-all — composite command that runs the full pipeline end-to-end.
// Attached to prodStorage (the "end of the pipeline" resource) since
// app-host-level commands aren't yet available in this Aspire SDK.
// =============================================================================

prodStorage.WithCommand(
    "bootstrap-all",
    "Bootstrap full pipeline",
    async context =>
    {
        var ct = context.CancellationToken;
        var commandService = context.ServiceProvider.GetRequiredService<ResourceCommandService>();
        var notifications = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        var logger = context.Logger;

        var stages = new (string Name, IResource Resource)[]
        {
            ("sample-build", sampleBuild.Resource),
            ("upload-stage1", uploadStage1.Resource),
            ("htmlgenerator", htmlGenerator.Resource),
            ("publish-index", publishIndex.Resource),
        };

        foreach (var (name, resource) in stages)
        {
            logger.LogInformation("[bootstrap-all] Starting {Stage}...", name);

            var start = await commandService.ExecuteCommandAsync(
                resource: resource,
                commandName: "resource-start",
                cancellationToken: ct);

            if (!start.Success)
            {
                return CommandResults.Failure($"Failed to start {name}: {start.Message}");
            }

            // Wait until the resource reaches a terminal state.
            var snapshot = await notifications.WaitForResourceAsync(
                resource.Name,
                e => e.Snapshot.State?.Text is "Finished" or "Exited" or "FailedToStart" or "RuntimeUnhealthy",
                ct);

            var state = snapshot.Snapshot.State?.Text;
            var exitCode = snapshot.Snapshot.ExitCode;

            if (state != "Finished" && state != "Exited" || (exitCode is int code && code != 0))
            {
                return CommandResults.Failure(
                    $"{name} ended in state '{state}' with exit code {exitCode?.ToString() ?? "<none>"}.");
            }

            logger.LogInformation("[bootstrap-all] {Stage} finished cleanly.", name);
        }

        return CommandResults.Success();
    });

builder.Build().Run();

