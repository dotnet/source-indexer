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
               .WithDataBindMount(".azurite/stage1");
    });

var stage1Blobs = stage1Storage.AddBlobs("stage1-blobs");
var stage1Container = stage1Storage.AddBlobContainer("stage1", blobContainerName: "stage1");

var prodStorage = builder.AddAzureStorage("prodStorage")
    .RunAsEmulator(azurite =>
    {
        azurite.WithLifetime(ContainerLifetime.Persistent)
               .WithDataBindMount(".azurite/prod");
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

// 1. sample-build — `dotnet build /bl:` on MiniRuntime, producing a binlog.
//    Emulates: Arcade-driven V1 repo build that produces a binlog.
// Use /t:Rebuild so MSBuild always re-invokes the C# compiler — otherwise an
// incremental no-op build produces a binlog with zero Csc invocations and
// HtmlGenerator finds nothing to index.
var sampleBuild = builder.AddExecutable(
        "step1-sample-build",
        "dotnet",
        repoRoot,
        "build",
        sampleProj,
        $"/bl:{sampleBinlog}",
        "/t:Rebuild",
        "-c", "Debug")
    .WithExplicitStart();

// 2. upload-stage1 — runs the real UploadIndexStage1 tool against Azurite.
//    Emulates: V2 upstream repos uploading their source bundle to
//    netsourceindexstage1/stage1/<repo>/<ts>.tar.gz.
//    This is the same .NET console app that runs in upstream pipelines, so
//    you can attach a debugger to it directly from the Aspire dashboard.
var uploadStage1 = builder.AddProject<Projects.UploadIndexStage1>("step2-upload-stage1")
    .WithExplicitStart()
    .WithReference(stage1Blobs)
    .WaitFor(stage1Container)
    .WithEnvironment("AZURE_STORAGE_CONNECTION_STRING", stage1Blobs.Resource.ConnectionStringExpression)
    .WithArgs(
        "-i", sampleSourceDir,
        "-n", "MiniRuntime",
        "-b", "stage1");

// 3. htmlgenerator — runs HtmlGenerator (net472) over the binlog from step 1
//    and produces static HTML under bin/index/. HtmlGenerator targets net472
//    so we can't link its output assembly into the net10 AppHost, but
//    `ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true"`
//    on the ProjectReference still gets us the Projects.HtmlGenerator
//    metadata, so we can use AddProject<T> and get debugging / restart-on-
//    rebuild for free.
var htmlGenerator = builder.AddProject<Projects.HtmlGenerator>("step3-htmlgenerator", launchProfileName: null)
    .WithExplicitStart()
    .WithArgs(
        sampleBinlog,
        $"/out:{indexOutDir}",
        "/force",
        $"/serverPath:{sampleSourceDir}=https://github.com/dotnet/source-indexer/tree/main/samples/MiniRuntime/");

// 4. normalize-case — lowercase every filename under bin/index/index/.
//    SourceIndexServer.Helpers.ServeProxiedIndex lowercases incoming request
//    paths before querying the blob (case-sensitive in Azure Storage), so any
//    PascalCase output from HtmlGenerator (Projects.txt, results.html, etc.)
//    would 404 if uploaded as-is. Prod runs the same script — see
//    azure-pipelines.yml line ~165 (deployment/normalize-case.ps1).
var normalizeCase = builder.AddExecutable(
        "step4-normalize-case",
        "pwsh",
        repoRoot,
        "-NoProfile",
        "-File", Path.Combine(repoRoot, "deployment", "normalize-case.ps1"),
        "-Root", indexUploadDir)
    .WithExplicitStart();

// 5. publish-index — wraps the Azure CLI (matching prod's AzureFileCopy@6
//    task). Uploads bin/index/index/* to prodStorage/index-local.
var publishIndex = builder.AddExecutable(
        "step5-publish-index",
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
            ("step1-sample-build", sampleBuild.Resource),
            ("step2-upload-stage1", uploadStage1.Resource),
            ("step3-htmlgenerator", htmlGenerator.Resource),
            ("step4-normalize-case", normalizeCase.Resource),
            ("step5-publish-index", publishIndex.Resource),
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

        // The web's IndexLoader only runs once at startup (Index ctor → Task.Run).
        // In prod the app-service slot-setting flip restarts the app after publish;
        // locally we mirror that by restarting `web` so it re-reads the freshly
        // published container.
        logger.LogInformation("[bootstrap-all] Restarting web to pick up the freshly published index...");
        var restart = await commandService.ExecuteCommandAsync(
            resource: web.Resource,
            commandName: "resource-restart",
            cancellationToken: ct);
        if (!restart.Success)
        {
            return CommandResults.Failure($"Failed to restart web: {restart.Message}");
        }
        await notifications.WaitForResourceHealthyAsync(web.Resource.Name, ct);

        return CommandResults.Success();
    });

builder.Build().Run();

