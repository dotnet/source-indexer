# 02 — Build & local dev

## Platform requirements

- **Windows only.** The HtmlGenerator is a .NET Framework 4.7.2+ executable and `index.proj` enforces this via:
  ```xml
  <Target Name="EnsurePreconditions">
    <Error Condition="'$(OS)' != 'Windows_NT'" Text="This tool can only be run on Windows_NT."/>
  </Target>
  ```
- **Visual Studio 2022** (for `MSBuild.exe`).
- **.NET SDK 10.0.101+** (pinned by [`global.json`](../../global.json), `rollForward: major`).
- Plenty of disk: a full local index pulls down ~10+ repos and generates many GB of HTML.

## First-time build

From the repo root in a developer command prompt:

```pwsh
# 1. Restore each solution.
foreach ($sln in Get-ChildItem -Recurse *.sln) { dotnet restore $sln.FullName }

# 2. Build with VS msbuild (NOT `dotnet build` for the orchestrator,
#    because the HtmlGenerator targets net472).
& "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe" build.proj
```

Important: the production pipeline does NOT run `msbuild build.proj` in one shot. It splits the orchestration into discrete targets so it can plug Azure CLI auth in between (see [05 — Azure pipeline](05-azure-pipeline.md)):

```
dotnet build build.proj /t:Clone     # V1: git clone; V2: download stage1 bundles
dotnet build build.proj /t:Prepare   # V1: invoke Arcade build to produce binlogs
dotnet build build.proj /t:BuildIndex
```

For a fully local build (no stage1 access), you can either:
- Set the `Stage1StorageAccount` / `Stage1StorageContainer` MSBuild properties and authenticate locally via `az login` (DefaultAzureCredential will pick it up), **or**
- Comment out the V2 repos in [`src/index/repositories.props`](../../src/index/repositories.props) and rely on V1 cloning only. This is much slower but doesn't require Azure auth.

## Output layout

After a successful build:

```
bin/
├── repo/                      # Cloned (V1) and extracted (V2) source trees.
│   ├── Directory.Build.props  # Dropped from src/index/Directory.Build.props.tmpl
│   ├── Directory.Packages.props
│   ├── runtime/, roslyn/, ...
├── index.list                 # The list of binlogs and slns fed to HtmlGenerator.
└── index/                     # The HtmlGenerator output.
    ├── index/                 # Per-assembly HTML (lots of files; copy to blob storage).
    ├── *.dll                  # Web app binaries (SourceIndexServer).
    ├── wwwroot/
    └── ...
```

## Running the index locally

After a build:

```pwsh
cd bin\index
dotnet Microsoft.SourceBrowser.SourceIndexServer.dll
# Site available at http://localhost:5000
```

By default the server expects to find the index data in the local `index/` folder next to the dll. To point it at remote blob storage instead, set:

```pwsh
$env:SOURCE_BROWSER_INDEX_PROXY_URL = "https://netsourceindexprod.blob.core.windows.net/index-<guid>"
```

(Defined in [`src/SourceBrowser/src/SourceIndexServer/Helpers.cs`](../../src/SourceBrowser/src/SourceIndexServer/Helpers.cs).)

## Updating the vendored SourceBrowser

The repo carries a full copy of [KirillOsenkov/SourceBrowser](https://github.com/KirillOsenkov/SourceBrowser) under `src/SourceBrowser/`, plus a `SourceBrowser.hash` file recording the upstream commit, plus a local patch (the dotnet-specific additions). To roll forward:

```pwsh
# 1. Clone the upstream repo somewhere outside this tree, on the dotnet/source-indexer branch.
git clone https://github.com/dotnet/SourceBrowser.git C:\src\SourceBrowser
cd C:\src\SourceBrowser
git checkout source-indexer   # the branch that carries dotnet's additions
git pull

# 2. Run the helper from inside this repo, pointing at the clone above.
cd <this repo>\src
./update-source-browser.ps1 -SourceBrowserCloneDir C:\src\SourceBrowser
```

The script:
1. `git diff`s from the hash recorded in `SourceBrowser.hash` to upstream `HEAD` and writes the diff to `src/SourceBrowser.patch`.
2. `git apply --reject --whitespace=fix --directory=src/SourceBrowser` applies the patch to the vendored copy.
3. Writes the new hash back to `SourceBrowser.hash`.

If any hunks fail to apply (`.rej` files appear), resolve them by hand. Commit the resulting changes.

> Source: [`src/update-source-browser.ps1`](../../src/update-source-browser.ps1) and [`src/SourceBrowser.hash`](../../src/SourceBrowser.hash).

## Tips for local iteration

- `msbuild build.proj /t:Clean` removes `bin/` entirely.
- `msbuild build.proj /t:BuildIndex` reruns just the index step assuming `bin/repo/` is already populated. This is the fast inner loop once you have all sources.
- Set `/p:EnableDebugLogging=true` (as the pipeline does) to surface SourceIndexServer debug logging.
- Binlogs go to wherever you point `/bl:`. The pipeline writes them under `$(Build.ArtifactStagingDirectory)/logs/`.
- The HtmlGenerator command line built up in `index.proj` is worth knowing if you have to debug indexing:
  ```
  HtmlGenerator.exe /donotincludereferencedprojects /nobuiltinfederations /noplugins
                    /out:bin/index/index/
                    /in:bin/index.list
                    /serverPath:"<localPath>=<https://github.com/dotnet/<repo>/tree/<sha>/>"
  ```
