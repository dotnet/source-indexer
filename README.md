# source-indexer
This repo contains the code for building http://source.dot.net

## Build Status
[![Build Status](https://dev.azure.com/dnceng/internal/_apis/build/status/dotnet-source-indexer/dotnet-source-indexer%20CI?branchName=main)](https://dev.azure.com/dnceng/internal/_build/latest?definitionId=612&branchName=main)

## What Is It?
This repo uses https://github.com/KirillOsenkov/SourceBrowser (with a few additions here https://github.com/dotnet/SourceBrowser/tree/source-indexer) to index the dotnet sources and produce a navigatable and searchable website containing the full source code. This includes code from the runtime, winforms, wpf, aspnetcore, and msbuild, among others. For a full list see here https://github.com/dotnet/source-indexer/blob/main/src/index/repositories.props.

## Build Prerequsites
The build requires .net core 3.1.201 and Visual Studio 2019 to build.

## Build
The build will only work on windows because the source indexer executable is a .net framework executable.
1. `git clone https://github.com/dotnet/source-indexer.git`
2. For each *.sln file `dotnet restore`
3. Find VS 2019 msbuild.exe on your machine, typically found at `C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe`
4. `msbuild build.proj`

## Running the built index
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
