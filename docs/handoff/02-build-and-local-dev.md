# 02 — Build & local dev

> **TODO @radical:** the entire content of this doc was reverse-engineered from
> [`azure-pipelines.yml`](../../azure-pipelines.yml), [`build.proj`](../../build.proj),
> [`src/index/index.proj`](../../src/index/index.proj), [`global.json`](../../global.json),
> [`src/index/repositories.props`](../../src/index/repositories.props), and the
> two .cs task files — there is no `CONTRIBUTING.md` or build README in this
> repo to source from. **Please confirm your actual day-to-day local build
> workflow** (which MSBuild you use, whether `az login` is sufficient for the
> stage1 storage account, anything you do differently from what's written
> below), and flag anything here that's wrong or that doesn't match how the
> previous team actually worked. Specifically suspect spots:
>
> - The hardcoded `MSBuild.exe` path under VS 2022 Enterprise — should this
>   be `vswhere`-derived or are you fine assuming the conventional install?
> - The "fully local build with `az login`" alternative under
>   [First-time build](#first-time-build) — never validated end-to-end; the
>   storage ACLs may not grant individual devs read access.
> - The "Updating the vendored SourceBrowser" section below is now mostly a
>   warning against re-syncing — confirm that matches your intent for the
>   receiving team.

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

> **⚠️ Heads up — the fork has diverged. Re-syncing from upstream is no longer recommended.**
>
> The original design intent was to vendor SourceBrowser and periodically sync from upstream via the patch-based helper below. In practice we have not run that workflow since [PR #184 on 2025-05-12](https://github.com/dotnet/source-indexer/pull/184) (which pinned us to upstream commit [`bf64cd8`](https://github.com/KirillOsenkov/SourceBrowser/commit/bf64cd8ac09f60e605e1a86784da47cc2c034a89)). Since then we've made **~21 local commits** touching `src/SourceBrowser/`, including substantive feature work that is *not* upstream — notably:
>
> - [#183](https://github.com/dotnet/source-indexer/pull/183) — Include signing key in `BinLogToSln`
> - [#192](https://github.com/dotnet/source-indexer/pull/192) — Prefer real implementation over `*.notsupported.cs` in dedup
> - [#193](https://github.com/dotnet/source-indexer/pull/193) — Support source-generated files
> - [#255](https://github.com/dotnet/source-indexer/pull/255) — Update to .NET 10 (target framework changes throughout)
> - [#257](https://github.com/dotnet/source-indexer/pull/257) — `BinLogReader` Linux binlog fix + `HtmlGenerator` duplicate `serverPath` fix
> - …plus build/target updates and ~12 Dependabot bumps.
>
> Bumping `SourceBrowser.hash` blindly will overwrite these via the patch step (`.rej` files will likely flag the conflicts, but it's easy to silently lose features). **Treat the vendored copy as a hard fork now.** If a fix is needed, prefer cherry-picking the specific upstream commit into `src/SourceBrowser/` directly, rather than running `update-source-browser.ps1` against current upstream `HEAD`. If a full re-sync is genuinely required (e.g. a major upstream refactor we want), expect it to be a multi-day rebase effort and plan to re-apply the PRs above by hand.
>
> The workflow below is preserved for historical reference and for the cherry-pick case (where you point the script at a specific upstream commit rather than `HEAD`).

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
