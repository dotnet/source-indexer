# 01 — Repo layout

A tour of every top-level entry in the repo.

## Top-level files

| Path | Role |
|---|---|
| [`README.md`](../../README.md) | Short public-facing readme. |
| [`LICENSE`](../../LICENSE) | MIT. |
| [`CODE-OF-CONDUCT.md`](../../CODE-OF-CONDUCT.md) | Standard .NET Foundation CoC. |
| [`global.json`](../../global.json) | Pins the .NET SDK version (currently `10.0.101`, `rollForward: major`). |
| [`NuGet.config`](../../NuGet.config) | Single package source: `dotnet-public` on `dnceng`. |
| [`Directory.Build.props`](../../Directory.Build.props) | Sets `VersionSuffix` from the AzDO build number and `LangVersion=latest`. |
| [`Directory.Packages.props`](../../Directory.Packages.props) | Imports `src/SourceBrowser/src/Directory.Packages.props` so the whole repo shares the vendored SourceBrowser's central package management. |
| [`dir.props`](../../dir.props) | Defines `$(SourcesDir)`, `$(OutDir)` (`bin/`), `$(RepositoryPath)` (`bin/repo/`). Imported by `src/index/index.proj`. |
| [`build.proj`](../../build.proj) | The MSBuild entry point. Builds the tasks assembly, then delegates each target (`Clone`, `Prepare`, `BuildIndex`, …) to `src/index/index.proj`. |
| [`azure-pipelines.yml`](../../azure-pipelines.yml) | The 1ES production/validation pipeline. Detailed in [05 — Azure pipeline](05-azure-pipeline.md). |
| [`azure-pipelines-codeql.yml`](../../azure-pipelines-codeql.yml) | Weekly CodeQL scan (Monday 12:00 UTC) on `NetCore1ESPool-Internal`. |

## `src/` — sources

```
src/
├── source-indexer.sln          # Solution containing the orchestration code (tasks + uploader).
├── Microsoft.SourceIndexer.Tasks/  # Custom MSBuild tasks.
├── UploadIndexStage1/          # `dotnet tool` used by upstream repos.
├── index/                      # MSBuild orchestration files driven by build.proj.
├── SourceBrowser/              # Vendored fork of KirillOsenkov/SourceBrowser.
├── SourceBrowser.hash          # Git hash of the upstream commit currently vendored.
└── update-source-browser.ps1   # Patch-based refresh workflow (see 02).
```

### `src/Microsoft.SourceIndexer.Tasks/`

`net472` MSBuild tasks library, referenced from `build.proj` via `SourceIndexerTasksAssembly`.

| File | Task | Used by |
|---|---|---|
| `DownloadStage1Index.cs` | `DownloadStage1Index` | `src/index/index.proj` target `DownloadRepositoryV2` to pull stage1 bundles. Auth via `Azure.Identity.DefaultAzureCredential` (optionally pinned to a `ClientId`). |
| `SelectProjects.cs` | `SelectProjects` | `build.proj` target `SelectProjects`. Implements the source-selection scoring algorithm. See [`docs/source-selection-algorithm.md`](../source-selection-algorithm.md). |
| `Extensions.cs` | helpers | shared utility code. |

There's also a third task referenced from `src/index/SourceIndex.targets`: `ResolveLivePackageReferences`. (TODO tribal knowledge: confirm whether this task is defined inside this assembly or pulled in via a SourceBrowser file.)

### `src/UploadIndexStage1/`

A `net10.0` console app marked `PackAsTool` with `<VersionPrefix>2.0.0</VersionPrefix>`. Published to the `dnceng/internal` NuGet feed (`9ee6d478-d288-47f7-aacc-f6e6d082ae6d/d1622942-d16f-48e5-bc83-96f4539e7601`) by the pipeline.

`Program.cs` accepts these CLI options (parsed with `Mono.Options`):

| Option | Meaning |
|---|---|
| `-i=<folder>` | Source folder (the upstream repo's binlog/src output). |
| `-n=<name>` | Logical repo name (becomes blob name, matches `RepoName` in `repositories.props`). |
| `-c=<clientId>` | Optional Azure client ID for managed identity / federated auth. |
| `-s=<account>` | Destination storage account (name or full `https://*.blob.core.windows.net` URL). |
| `-b=<container>` | Destination container (e.g. `stage1`). |

Bundles the input as `tar.gz` (SharpZipLib) and uploads it as a blob. The matching downloader is `DownloadStage1Index` in the tasks assembly.

### `src/index/`

MSBuild orchestration that runs every build:

| File | Purpose |
|---|---|
| `index.proj` | Defines all per-repo targets: `Clone`, `CheckoutSources`, `ResolveHashV1`, `CloneV1`, `DownloadRepositoryV2`, `ResolveHashV2`, `CloneV2`, `Prepare`, `BuildIndex`. Detailed in [03 — Indexing pipeline](03-indexing-pipeline.md). |
| `repositories.props` | The list of indexed repositories. **This is the file you edit to add/remove a repo.** |
| `SourceIndex.targets` | Imported into every cloned V1 repo's build (via `Directory.Build.props.tmpl`). Fixes up `ProjectReference`/`PackageReference` resolution so the indexer sees the live binaries. |
| `Directory.Build.props.tmpl` | Template `Directory.Build.props` dropped into `bin/repo/` so every cloned/extracted repo imports the right targets. |
| `Directory.Packages.props.tmpl` | Same idea for central package management. |
| `overwrite/` | Static assets (`Web.config`, `index/…`, `wwwroot/…`) copied on top of the generated index. Use this for site-wide overrides. |

### `src/SourceBrowser/`

A submodule-ish vendoring of [KirillOsenkov/SourceBrowser](https://github.com/KirillOsenkov/SourceBrowser) at the commit recorded in [`src/SourceBrowser.hash`](../../src/SourceBrowser.hash). The whole upstream tree is checked in, including the upstream `Directory.Build.props`, `Directory.Packages.props`, and solution. The two parts we care most about:

- **`src/HtmlGenerator/`** — the .NET Framework tool that actually produces the static HTML index. Driven by `src/index/index.proj` target `BuildIndex`.
- **`src/SourceIndexServer/`** — the ASP.NET Core app deployed to `netsourceindexprod`. Reads index files from the blob container pointed at by the `SOURCE_BROWSER_INDEX_PROXY_URL` env var (see [`Helpers.cs`](../../src/SourceBrowser/src/SourceIndexServer/Helpers.cs)).

Updating the vendored copy uses [`src/update-source-browser.ps1`](../../src/update-source-browser.ps1), described in [02 — Build & local dev](02-build-and-local-dev.md).

## `deployment/` — Azure deployment scripts

PowerShell scripts invoked from `azure-pipelines.yml`:

| Script | What it does |
|---|---|
| `util.ps1` | `Check-Failure`, `Get-ContainerTTL`, `Set-ContainerTTL`. Default TTL is `10` builds. |
| `create-container.ps1` | Creates a new `index-<GUID>` blob container and emits `##vso[task.setvariable variable=NEW_CONTAINER_NAME]`. |
| `deploy-storage-proxy.ps1` | After upload, sets the `SOURCE_BROWSER_INDEX_PROXY_URL` app setting on the target slot. |
| `cleanup-old-containers.ps1` | After the swap, decrements the TTL on unused containers and deletes containers whose TTL has hit zero. Containers used by either prod or staging are protected. |
| `normalize-case.ps1` | Renames every file in `bin/index/index/` to lowercase (Azure Blob is case-sensitive but the HTML index links are not always consistent). |
| `install-tool.ps1` | Dev helper for installing `UploadIndexStage1` locally. |

## `docs/`

- [`docs/source-selection-algorithm.md`](../source-selection-algorithm.md) — public docs on the scoring algorithm.
- `docs/handoff/` — this folder.

## Hidden/config folders

- `.azuredevops/` — pull request and pipeline config for AzDO.
- `.config/` — `dotnet-tools.json` for local tool restore (if present).
- `.github/` — issue templates, CODEOWNERS, etc.
- `.vscode/` — VS Code launch/settings.
- `.artifactignore` — controls what AzDO uploads as pipeline artifacts.
