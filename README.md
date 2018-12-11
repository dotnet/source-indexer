# source-indexer
This repo contains the code for building http://source.dot.net

## Build Status
[![Build status](https://dev.azure.com/mseng/Tools/_apis/build/status/.NET%20Core%20Source%20Index)](https://dev.azure.com/mseng/Tools/_build/latest?definitionId=5341)

## Building

To clone and build locally (Windows only):
1. `git clone --recursive https://github.com/dotnet/source-indexer.git`
2. `cd source-indexer\src\SourceBrowser`
3. `git fetch --all`
4. `git checkout source-indexer`
5. `dotnet restore`
6. `cd ..\..`
7. `msbuild build.proj`
