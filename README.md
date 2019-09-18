# source-indexer
This repo contains the code for building http://source.dot.net

## Build Status
[![Build Status](https://dev.azure.com/dnceng/internal/_apis/build/status/dotnet-source-indexer/dotnet-source-indexer%20CI?branchName=master)](https://dev.azure.com/dnceng/internal/_build/latest?definitionId=612&branchName=master)

## Building

To clone and build locally (Windows only):
1. `git clone --recursive https://github.com/dotnet/source-indexer.git`
2. `cd source-indexer\src\SourceBrowser`
3. `git fetch --all`
4. `git checkout source-indexer`
5. `dotnet restore`
6. `cd ..\..`
7. `msbuild build.proj`
