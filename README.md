# source-indexer
This repo contains the code for building http://source.dot.net

## Documentation
- [Local inner loop (Aspire)](docs/inner-loop.md) - **Recommended for development.** Runs the full pipeline (sample build → BinLogToSln → HtmlGenerator → blob → SourceIndexServer) locally against a tiny sample library.
- [Source Selection Algorithm](docs/source-selection-algorithm.md) - How the indexer chooses the best implementation when multiple builds exist for the same assembly

## Build Status
[![Build Status](https://dev.azure.com/dnceng/internal/_apis/build/status/dotnet-source-indexer/dotnet-source-indexer%20CI?branchName=main)](https://dev.azure.com/dnceng/internal/_build/latest?definitionId=612&branchName=main)

## What Is It?
This repo uses https://github.com/KirillOsenkov/SourceBrowser (with a few additions here https://github.com/dotnet/SourceBrowser/tree/source-indexer) to index the dotnet sources and produce a navigatable and searchable website containing the full source code. This includes code from the runtime, winforms, wpf, aspnetcore, and msbuild, among others. For a full list see here https://github.com/dotnet/source-indexer/blob/main/src/index/repositories.props.

## Local development (Aspire)
The repository ships with an [Aspire](https://aspire.dev) AppHost (`src/source-indexer.AppHost/`) that emulates the full production pipeline locally against a tiny sample library — **this is the recommended way to work on the repo**.

```pwsh
aspire start
```

The dashboard URL (with login token) is printed to the console. From there you can run the `bootstrap-all` step to build the sample, run `BinLogToSln`, run `HtmlGenerator`, upload to Azurite, and start the web app. See [docs/inner-loop.md](docs/inner-loop.md) for the full resource graph and walkthroughs.

You can also open the repo in **Visual Studio** or **VS Code** to set breakpoints and debug individual components (e.g., `HtmlGenerator`, `SourceIndexServer`, `BinLogToSln`) while the rest of the pipeline runs under the AppHost.

## Building the production index (Windows-only)
The official pipeline build only works on Windows because `HtmlGenerator` is a .NET Framework executable. For local dev prefer the Aspire flow above; use this path only if you need to reproduce the exact pipeline build.

**Prerequisites:** .NET 8.0 and Visual Studio 2022.

1. `git clone https://github.com/dotnet/source-indexer.git`
2. For each *.sln file `dotnet restore`
3. Find VS 2022 msbuild.exe on your machine, typically found at `C:\Program Files (x86)\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe`
4. `msbuild build.proj`

After the build is finished the index will exist in `bin\index` and can be run by running `dotnet Microsoft.SourceBrowser.SourceIndexServer.dll` in that folder. The index will be served on `http://localhost:5000`

## Deployment
The index is deployed by the VSTS build to the netsourceindex azure app service, with the index data stored in the netsourceindex storage account. The deployment does the following things.
1. Split the generated index from the binaries and static data for the website.
2. Upload the generated index into a new container in the netsourceindex storage account.
3. Deploy the binaries and static data to the staging slot of the app service.
4. Update the app service settings with the url of the storage container the index data was uploaded to
5. Restart the app service
6. Test the application by performing a GET of the url, fail if it doesn't return 200 OK
7. Swap the staging slot into production for the app service
8. Delete storage containers that haven't been used by the app service in the last 10 builds.

## Monitoring
https://source.dot.net is monitored using availability tests from the dotnet-eng application insights resource. Alerting is handled through grafana here https://dotnet-eng-grafana.westus2.cloudapp.azure.com/d/arcadeAvailability/service-availability?orgId=1&refresh=30s
