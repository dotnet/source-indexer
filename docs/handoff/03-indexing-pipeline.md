# 03 – Indexing Pipeline (Clone → Prepare → BuildIndex)

This document walks through every MSBuild target that produces the static HTML index served by [source.dot.net](https://source.dot.net). All work is orchestrated by the top-level [`build.proj`](../../build.proj), which delegates into [`src/index/index.proj`](../../src/index/index.proj). The repo list is in [`src/index/repositories.props`](../../src/index/repositories.props).

Sibling docs:

- [00-overview.md](./00-overview.md)
- [01-repo-layout.md](./01-repo-layout.md)
- [02-build-and-local-dev.md](./02-build-and-local-dev.md)
- [04-arcade-and-dotnet-integration.md](./04-arcade-and-dotnet-integration.md)

The CI definition that strings these targets together for production is [`azure-pipelines.yml`](../../azure-pipelines.yml).

---

## 1. High-level flow

```
┌──────────────┐   ┌───────┐   ┌─────────┐   ┌────────────┐   ┌──────────────────┐
│  BuildTasks  │ → │ Clone │ → │ Prepare │ → │ BuildIndex │ → │ overwrite/ files │
└──────────────┘   └───────┘   └─────────┘   └────────────┘   └──────────────────┘
   (compile           (V1: git clone        (V1: invoke      (HtmlGenerator →
    MSBuild            V2: download tar.gz   eng/common       static HTML in
    tasks .dll)        from blob storage)    build.ps1;       bin/index/)
                                              V2: no-op,
                                              binlogs already
                                              inside bundle)
```

All output is written under `bin/` (defined in [`dir.props`](../../dir.props)):

| Property | Value | Purpose |
|---|---|---|
| `SourcesDir` | `src/` | Source roots |
| `OutDir` | `bin/` | All build/index output |
| `RepositoryPath` | `bin/repo/` | One subdirectory per cloned/downloaded repo |

The pipeline is **Windows-only** — `index.proj` enforces this in the `EnsurePreconditions` initial target (`<Error Condition="'$(OS)' != 'Windows_NT'" .../>`).

### Why `build.proj` exists separately from `index.proj`

`build.proj` is a thin wrapper whose only job is to build the [`Microsoft.SourceIndexer.Tasks`](../../src/Microsoft.SourceIndexer.Tasks) assembly first (`BuildTasks` target), then re-invoke `index.proj` with `SourceIndexerTasksAssembly=$(SourceIndexerTasksAssembly)`. This is required because `index.proj` uses `<UsingTask>` to load `DownloadStage1Index` from that assembly, and MSBuild needs the .dll to already exist on disk before evaluating `index.proj`.

Targets `build.proj` exposes (all `DependsOnTargets="BuildTasks"`): `Clone`, `Prepare`, `BuildIndex`, `Build`, `Rebuild`, `Clean`, `SelectProjects`.

---

## 2. Repository models: V1 vs V2

`repositories.props` declares two MSBuild item types, both ultimately consumed by `BuildIndex`. The split exists because some upstream repos publish prebuilt indexing inputs and some do not.

### 2.1 V1 — `<Repository>` items (cloned + built here)

V1 repos are cloned by this pipeline and built in-place to produce MSBuild binlogs.

Current V1 repos (in [`repositories.props`](../../src/index/repositories.props)):

| Identity | URL | PrepareCommand |
|---|---|---|
| `iot` | https://github.com/dotnet/iot | `$(ArcadeBuildCmd)` |
| `msbuild` | https://github.com/dotnet/msbuild | `$(ArcadeBuildCmd)` (also sets `DeepClone=true`) |
| `performance` | https://github.com/dotnet/performance | `$(ArcadeBuildCmd) -projects src\benchmarks\micro\MicroBenchmarks.sln` |
| `sdk` | https://github.com/dotnet/sdk | `$(ArcadeBuildCmd) -projects src\benchmarks\micro\MicroBenchmarks.sln` |

`ArcadeBuildCmd` is defined at the top of `repositories.props`:

```
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command
  "eng/common/build.ps1 -restore -build -binarylog -nodeReuse:$false"
```

The output of the Arcade build is one or more `*.binlog` files inside `bin/repo/<identity>/`. Those are picked up by `FindBinlogs` and fed to `HtmlGenerator`.

#### `<Repository>` schema (V1)

A real V1 entry looks like this — `msbuild` from [`repositories.props`](../../src/index/repositories.props):

```xml
<Repository Include="msbuild">
  <Url>https://github.com/dotnet/msbuild</Url>
  <Branch>main</Branch>
  <DeepClone>true</DeepClone>
  <PrepareCommand>
    $(ArcadeBuildCmd)
  </PrepareCommand>
</Repository>
```

The `Include="..."` attribute is the item's *Identity* (used as a folder name and in log output). Only `Url` and `PrepareCommand` are actually required per-item — everything else either has a default from `<ItemDefinitionGroup>` in [`index.proj`](../../src/index/index.proj):

```xml
<Repository>
  <Branch>main</Branch>
  <LocalPath>$(RepositoryPath)%(Identity)/</LocalPath>
  <ServerPath>%(Url)/tree/%(Branch)/</ServerPath>
</Repository>
```

…or is optional and only used when explicitly set (e.g. `DeepClone`, `SparseCheckout`).

Per-item metadata read by the targets:

| Metadata | Required | Default | Used by | Notes |
|---|---|---|---|---|
| `Url` | yes | — | `CloneRepository`, `ResolveHashV1` | GitHub repo URL (no `.git`); a `git remote add origin <Url>.git` is performed |
| `PrepareCommand` | yes | — | `PrepareV1` | Shell command run via `cmd /c` inside `LocalPath` |
| `Branch` | no | `main` | `CloneRepository` | Used in `git pull origin <Branch>` |
| `DeepClone` | no | _(unset)_ | (declared; no functional effect on clone — `CloneRepository` always uses `git pull` from a fresh `git init`) | Documents intent; `msbuild` sets it |
| `SparseCheckout` | no | _(unset)_ | `CloneRepository` | If non-empty, enables `core.sparsecheckout` and writes the value into `.git/info/sparse-checkout` |
| `CheckoutSubmodules` | no | _(unset)_ | `CloneRepository` | If `true`, runs `git submodule update --init --recursive` |
| `OldCommit` | no | _(unset)_ | `CheckoutSources` | If set, checks out a fixed SHA instead of `HEAD` |
| `LocalPath` | no | `$(RepositoryPath)<identity>/` | everywhere | Computed from item identity |
| `ServerPath` | no | `<Url>/tree/<branch>/` | `BuildIndex` (until rewritten by `ResolveHashV1`) | Rewritten to `<Url>/tree/<sha>/` after `git rev-parse HEAD` |

### 2.2 V2 — `<RepositoryV2>` items (prebuilt bundle downloaded)

V2 repos do **not** clone or build here. The pipeline downloads a `tar.gz` bundle that the upstream repo's own CI has already produced (containing source files, binlogs, and a `hash` file) and extracts it under `bin/repo/<identity>/`. See [04-arcade-and-dotnet-integration.md](./04-arcade-and-dotnet-integration.md) for how upstream repos produce that bundle via the `UploadIndexStage1` tool.

Current V2 repos (in [`repositories.props`](../../src/index/repositories.props)):

| Identity | RepoName (blob prefix) | URL |
|---|---|---|
| `arcade` | `dotnet-arcade` | https://github.com/dotnet/arcade |
| `roslyn` | `dotnet-roslyn` | https://github.com/dotnet/roslyn |
| `runtime` | `dotnet-runtime` | https://github.com/dotnet/runtime |
| `winforms` | `dotnet-winforms` | https://github.com/dotnet/winforms |
| `wpf` | `dotnet-wpf` | https://github.com/dotnet/wpf |
| `maui` | `dotnet-maui` | https://github.com/dotnet/maui |
| `machinelearning` | `dotnet-machinelearning` | https://github.com/dotnet/machinelearning |
| `wcf` | `dotnet-wcf` | https://github.com/dotnet/wcf |
| `aspnetcore` | `dotnet-aspnetcore` | https://github.com/dotnet/aspnetcore |
| `aspire` | `dotnet-aspire` | https://github.com/dotnet/aspire |
| `extensions` | `dotnet-extensions` | https://github.com/dotnet/extensions |

#### `<RepositoryV2>` schema

A real V2 entry looks like this — `roslyn` from [`repositories.props`](../../src/index/repositories.props):

```xml
<RepositoryV2 Include="roslyn">
  <RepoName>dotnet-roslyn</RepoName>
  <Url>https://github.com/dotnet/roslyn</Url>
</RepositoryV2>
```

Defaults applied from `<ItemDefinitionGroup>` in [`index.proj`](../../src/index/index.proj):

```xml
<RepositoryV2>
  <LocalPath>$(RepositoryPath)%(Identity)/src/</LocalPath>
  <ExtractPath>$(RepositoryPath)%(Identity)/</ExtractPath>
</RepositoryV2>
```

Per-item metadata:

| Metadata | Required | Default | Used by | Notes |
|---|---|---|---|---|
| `RepoName` | yes | — | `DownloadRepositoryV2` | Blob name prefix in the stage1 container (e.g. `dotnet-arcade/...tar.gz`) |
| `Url` | yes | — | `ResolveHashV2` | Used to compute `ServerPath = <Url>/tree/<sha>/` |
| `LocalPath` | no | `$(RepositoryPath)<identity>/src/` | `BuildIndex` | The source root inside the extracted bundle |
| `ExtractPath` | no | `$(RepositoryPath)<identity>/` | `DownloadRepositoryV2`, `ResolveHashV2` | The top-level extract directory (contains `hash` file plus extracted source/binlogs) |

> **TODO (tribal knowledge):** confirm the exact on-disk layout produced inside each V2 bundle (which subfolders contain `.sln` vs `.binlog`, whether the bundle ships full source for HTML rendering, and how Roslyn vs Runtime vary). The `UploadIndexStage1` tool simply tars whatever folder it's pointed at, so the layout is set by each upstream repo's Arcade publish step.

---

## 3. Target-by-target walkthrough of `src/index/index.proj`

Targets are listed roughly in dependency order. `Build` is `DependsOnTargets="Clone;Prepare;BuildIndex"`, and `Rebuild` is `DependsOnTargets="Clean;Build"`.

### 3.1 `EnsurePreconditions` (InitialTargets)

Fails the build if `$(OS) != Windows_NT`. Source-indexer requires Windows because it shells out to PowerShell + `cmd` and consumes MSBuild binlogs produced by the Windows Arcade build.

### 3.2 `Clean`

`RemoveDir Directories="$(OutDir)"` — wipes the entire `bin/` folder.

### 3.3 `PrepareOutput`

- Ensures `bin/` and `bin/repo/` exist.
- Copies [`src/index/Directory.Build.props.tmpl`](../../src/index/Directory.Build.props.tmpl) → `bin/repo/Directory.Build.props`.
- Copies [`src/index/Directory.Packages.props.tmpl`](../../src/index/Directory.Packages.props.tmpl) → `bin/repo/Directory.Packages.props`.

These two files are intentionally **empty stubs** (`ImportDirectoryPackagesProps=false`, `ImportDirectoryBuildTargets=false`). They sit one directory above every cloned repo so that MSBuild's well-known "walk up looking for Directory.Build.props" stops at `bin/repo/` and does not accidentally pick up the source-indexer repo's own props. See [`SourceIndex.targets`](../../src/index/SourceIndex.targets) for the actual logic that gets injected — discussed in [04-arcade-and-dotnet-integration.md](./04-arcade-and-dotnet-integration.md).

### 3.4 V1 clone chain

#### `CloneRepository` (`Outputs="%(Repository.Identity)"` — batched per V1 repo)

For each `<Repository>` item, when the `LocalPath` does not already exist:

1. `git init` in `LocalPath`.
2. `git config core.longpaths true` (Windows path-length workaround).
3. If `SparseCheckout` is set: `git config core.sparsecheckout true` and write the value into `.git/info/sparse-checkout`.
4. `git remote add origin <Url>.git`.
5. `git pull origin <Branch>`.
6. If `CheckoutSubmodules=true`: `git submodule update --init --recursive`.

If `LocalPath` already exists, all of these steps are skipped — re-running `Clone` is idempotent.

#### `CheckoutSources` (depends on `CloneRepository`)

- If `OldCommit` metadata is set on the item: `git checkout <OldCommit>`.
- Otherwise: `git checkout HEAD` (a no-op safety net).

#### `ResolveHashV1`

For each `<Repository>`:

```
git rev-parse HEAD
```

The output (the SHA at the tip of the checked-out branch) is captured into `$(CommitHash)` and used to build a `ClonedRepository` item with `ServerPath = <Url>/tree/<sha>/`. This is what HtmlGenerator turns into deep links from source.dot.net pages back to a specific commit on GitHub.

#### `CloneV1`

Aggregate target: `DependsOnTargets="CloneRepository;CheckoutSources;ResolveHashV1"`, conditioned on `'@(Repository)' != ''`.

### 3.5 V2 download chain

#### `DownloadRepositoryV2` (batched per V2 repo)

Calls the `DownloadStage1Index` MSBuild task (defined in [`src/Microsoft.SourceIndexer.Tasks/DownloadStage1Index.cs`](../../src/Microsoft.SourceIndexer.Tasks/DownloadStage1Index.cs)) with:

| Parameter | Source |
|---|---|
| `RepoName` | `%(RepositoryV2.RepoName)` |
| `OutputDirectory` | `%(RepositoryV2.ExtractPath)` (= `bin/repo/<identity>/`) |
| `StorageAccount` | `$(Stage1StorageAccount)` (pipeline property; CI sets `netsourceindexstage1`) |
| `BlobContainer` | `$(Stage1StorageContainer)` (pipeline property; CI sets `stage1`) |

The task:

1. Resolves credentials in this priority order: explicit `ClientId` task parameter → `ARM_CLIENT_ID` env var (`ManagedIdentityCredential`) → fallback to `AzureCliCredential()`. CI sets `addSpnToEnvironment: true` on the `AzureCLI@2` task, which exports `ARM_CLIENT_ID`.
2. Lists blobs in `<container>/<RepoName>/`.
3. Picks the **newest blob by name** (blob names are ISO-8601 UTC timestamps, so lexicographic = chronological order — see `UploadIndexStage1/Program.cs` which names blobs `"{repoName}/{DateTime.UtcNow:O}.tar.gz"`).
4. Streams the blob through `GZipInputStream` → `TarArchive` and extracts into `OutputDirectory`.

#### `ResolveHashV2`

```xml
<ReadLinesFromFile File="%(RepositoryV2.ExtractPath)hash">
```

The bundle's top-level `hash` file (written by the upstream repo's publish step — see `UploadIndexStage1`) contains the source commit SHA. Its contents become `$(CommitHash)` and feed `ClonedRepositoryV2.ServerPath = <Url>/tree/<sha>/`.

#### `CloneV2`

Aggregate: `DependsOnTargets="DownloadRepositoryV2;ResolveHashV2"`, conditioned on `'@(RepositoryV2)' != ''`.

### 3.6 `Clone`

`DependsOnTargets="CloneV1;CloneV2"`. This is the public entry point invoked from `build.proj` and from CI's "🟣Clone Stage1 data" step. Note that the CI step also sets `Stage1StorageAccount=netsourceindexstage1` and `Stage1StorageContainer=stage1` on the command line.

### 3.7 `PrepareV1`

For each `ClonedRepository` produced by `ResolveHashV1`:

```xml
<Exec Command="cmd /c &quot;$(PrepareCommand)&quot;"
      WorkingDirectory="%(ClonedRepository.LocalPath)"
      ContinueOnError="true"
      IgnoreStandardErrorWarningFormat="true"
      LogStandardErrorAsError="false"
      IgnoreExitCode="true" />
```

Notable design choices:

- The shell is `cmd /c` (not PowerShell) — `PrepareCommand` itself launches PowerShell to run `eng/common/build.ps1`.
- A non-zero exit code produces a `Warning`, **not an error**. A repo whose build is currently broken will not fail the overall indexing run; the index simply won't include its binlogs.
- `IgnoreStandardErrorWarningFormat="true"` and `LogStandardErrorAsError="false"` are critical — Arcade builds emit a lot of warning text that MSBuild would otherwise misclassify.

### 3.8 `PrepareV2`

Empty target — V2 bundles already contain pre-built binlogs and source. `DependsOnTargets="ResolveHashV2"` only.

### 3.9 `Prepare`

`DependsOnTargets="PrepareV1;PrepareV2"`. Public entry point.

### 3.10 `FindBinlogs`

For each `ClonedRepository` (V1 only):

```xml
<BinlogToIndex Include="%(ClonedRepository.LocalPath)\**\*.binlog"/>
```

Captures every `.binlog` file Arcade produced anywhere under `bin/repo/<identity>/`.

### 3.11 `FindSolutions`

For each `ClonedRepositoryV2`:

```xml
<SolutionToIndex Include="%(ClonedRepositoryV2.ExtractPath)\**\*.sln"/>
```

Captures every `.sln` shipped inside the V2 bundle.

> Note: the V2 bundle's `.binlog`s are also discoverable on disk under `ExtractPath`, but this target only globs for `.sln`. **TODO (tribal knowledge):** confirm whether V2 bundles are expected to ship binlogs at all or whether HtmlGenerator's `.sln` ingestion is the entire V2 path. (The bundle's tar layout, including which directories contain which file types, is set by each upstream repo and is not documented in this repo.)

### 3.12 `BuildGenerator`

```xml
<MSBuild Projects="$(SourcesDir)SourceBrowser/src/HtmlGenerator/HtmlGenerator.csproj"
         Targets="Restore;Build"
         Condition="!Exists('$(HtmlGeneratorExePath)')">
```

`HtmlGenerator` is the upstream [KirillOsenkov/SourceBrowser](https://github.com/KirillOsenkov/SourceBrowser) tool, vendored under [`src/SourceBrowser`](../../src/SourceBrowser) (git submodule). The built path is captured as `$(HtmlGeneratorExePath)`.

### 3.13 `BuildIndex`

This is where source.dot.net is actually generated. Depends on `BuildGenerator;FindBinlogs;FindSolutions`. Steps:

1. Validate `HtmlGeneratorExePath` exists.
2. `RemoveDuplicates` on the binlog + solution item lists.
3. `RemoveDir` of `bin/index/` to start clean.
4. Write **`bin/index.list`** — one line per binlog, then one line per solution. This is the master input file passed to HtmlGenerator.
5. Compose the command line and invoke HtmlGenerator.
6. Copy [`src/index/overwrite/**/*`](../../src/index/overwrite) on top of `bin/index/` (see §5 below).

#### The HtmlGenerator command line

Reproduced from `index.proj` (line-broken for readability):

```
"$(HtmlGeneratorExePath)"
  /donotincludereferencedprojects
  /nobuiltinfederations
  /noplugins
  /out:"$(OutDir)index/"
  /in:"$(OutDir)index.list"
  @(ClonedRepository    -> ' /serverPath:"%(LocalPath)=%(ServerPath)"', '')
  @(ClonedRepositoryV2  -> ' /serverPath:"%(LocalPath)=%(ServerPath)"', '')
```

`/serverPath:<localPath>=<serverUrl>` is the bridge that turns local file paths inside binlogs/source into GitHub deep links of the form `https://github.com/dotnet/<repo>/tree/<sha>/path/to/file.cs`. Each cloned repo contributes one mapping.

### 3.14 `Build` / `Rebuild`

- `Build`: `DependsOnTargets="Clone;Prepare;BuildIndex"`.
- `Rebuild`: `DependsOnTargets="Clean;Build"`.

### 3.15 `SelectProjects`

Standalone target exposed by [`build.proj`](../../build.proj) that delegates into `index.proj`'s `SelectProjects` (implemented by the [`SelectProjects`](../../src/Microsoft.SourceIndexer.Tasks/SelectProjects.cs) MSBuild task). It exists for diagnostic / source-selection experiments — see [docs/source-selection-algorithm.md](../source-selection-algorithm.md) for the algorithm. Not part of the production index build path.

---

## 4. Where the SHA comes from (server-path deep links)

| Model | Resolved by | Source of truth |
|---|---|---|
| V1 | `ResolveHashV1` | `git rev-parse HEAD` inside the cloned working tree |
| V2 | `ResolveHashV2` | The `hash` text file at the top of the extracted bundle |

That SHA is interpolated into `ServerPath = <Url>/tree/<sha>/` and threaded into HtmlGenerator via `/serverPath:...`. The result is that every "Open on GitHub" link rendered by source.dot.net points at the exact commit that was indexed.

For V2 repos, this means deep links are only as fresh as the upstream repo's publish job — whatever SHA they wrote into the bundle's `hash` file is authoritative.

---

## 5. `bin/index.list` and the `overwrite/` mechanism

### `bin/index.list`

Plain-text manifest written by `BuildIndex` (`WriteLinesToFile`) before invoking HtmlGenerator. Contains:

```
<absolute path to binlog 1>
<absolute path to binlog 2>
...
<absolute path to solution 1>
<absolute path to solution 2>
...
```

If you need to debug what HtmlGenerator saw, look at this file in `bin/index.list` after a build.

### `src/index/overwrite/`

After HtmlGenerator runs, every file under [`src/index/overwrite/`](../../src/index/overwrite) is copied **on top of** `bin/index/`, preserving relative paths. This is the place to inject static content that overrides whatever HtmlGenerator generated. Current contents:

| Path under `overwrite/` | Lands at |
|---|---|
| `Web.config` | `bin/index/Web.config` |
| `index/AffiliateLinks.txt` | `bin/index/index/AffiliateLinks.txt` |
| `index/header.html` | `bin/index/index/header.html` |
| `index/overview.html` | `bin/index/index/overview.html` |
| `index/SolutionExplorer.html` | `bin/index/index/SolutionExplorer.html` |
| `wwwroot/styles.css` | `bin/index/wwwroot/styles.css` |

The Copy task uses the `OverwriteFiles` item group, which is populated unconditionally at evaluation time:

```xml
<OverwriteFiles Include="$(MSBuildThisFileDirectory)/overwrite/**/*"/>
```

To customize the home page header, branding, etc., edit the file under `src/index/overwrite/` and re-run `BuildIndex`.

---

## 6. Adding a new repo

Pick V1 or V2 based on whether the upstream repo's CI already publishes a stage1 bundle (see [04-arcade-and-dotnet-integration.md](./04-arcade-and-dotnet-integration.md) for what publishing entails).

### 6.1 Add as V2 (preferred when possible)

The upstream repo's Arcade pipeline must already install + invoke the `dotnet-source-indexer-stage1` tool to upload a `tar.gz` into the `netsourceindexstage1`/`stage1` container under a blob prefix matching `RepoName`.

In [`src/index/repositories.props`](../../src/index/repositories.props):

```xml
<RepositoryV2 Include="<short-id>">
  <RepoName>dotnet-<short-id></RepoName>
  <Url>https://github.com/dotnet/<short-id></Url>
</RepositoryV2>
```

That's it — `DownloadRepositoryV2` will pick up the newest blob automatically.

### 6.2 Add as V1

Use this when the upstream repo cannot (yet) publish a stage1 bundle. The source-indexer pipeline will clone and build it on each run, which is slow and brittle.

```xml
<Repository Include="<short-id>">
  <Url>https://github.com/dotnet/<short-id></Url>
  <Branch>main</Branch>
  <PrepareCommand>
    $(ArcadeBuildCmd)
  </PrepareCommand>
</Repository>
```

If the repo isn't a stock Arcade repo, override `PrepareCommand` with whatever produces `*.binlog` files. Optional metadata: `DeepClone`, `SparseCheckout`, `CheckoutSubmodules`, `OldCommit` (pin to a specific SHA — useful when `main` is broken).

---

## 7. Source selection

`HtmlGenerator` indexes the closure of projects reachable from the binlogs/solutions listed in `bin/index.list` — the same closure that Arcade actually built upstream. Project-level filtering (when needed) is delegated to the `SelectProjects` MSBuild task ([`src/Microsoft.SourceIndexer.Tasks/SelectProjects.cs`](../../src/Microsoft.SourceIndexer.Tasks/SelectProjects.cs)).

The semantics of which files end up in the index — and which are excluded — are documented separately in [docs/source-selection-algorithm.md](../source-selection-algorithm.md). Read that document for the full rules; this pipeline does not duplicate them.

---

## 8. How CI runs the pipeline

From [`azure-pipelines.yml`](../../azure-pipelines.yml), the relevant steps in order:

1. **`dotnet build src/source-indexer.sln src/SourceBrowser/SourceBrowser.sln`** — builds tasks .dll, `UploadIndexStage1`, `HtmlGenerator`, and packs the NuGet packages into `$(Build.ArtifactStagingDirectory)/packages`.
2. **Clone Stage1 data** (`AzureCLI@2`, with `azureSubscription: 'SourceDotNet Stage1 Publish'`, `addSpnToEnvironment: true`):
   ```
   dotnet build build.proj /t:Clone /v:n
     /bl:.../clone.binlog
     /p:Stage1StorageAccount=netsourceindexstage1
     /p:Stage1StorageContainer=stage1
   ```
3. **Prepare All Repositories**:
   ```
   dotnet build build.proj /t:Prepare /v:n /bl:.../prepare.binlog
   ```
4. **Build source index**:
   ```
   dotnet build build.proj /t:BuildIndex /v:n /bl:.../build.binlog
   ```
5. Post-processing (outside the scope of `index.proj`): copy `bin/index/` to a staging dir, create a `.health` marker file, run `deployment/normalize-case.ps1`, upload to Azure Blob Storage, deploy to the Azure App Service staging slot, smoke-test, then slot-swap on `main`.

The same `Clone` / `Prepare` / `BuildIndex` targets can be invoked locally via `dotnet build build.proj /t:<Target>` (Windows only). See [02-build-and-local-dev.md](./02-build-and-local-dev.md).

---

## 9. If something breaks here…

| Symptom | Look first at |
|---|---|
| `EnsurePreconditions` fails | You're on non-Windows; this pipeline is Windows-only |
| `Clone` fails on a V1 repo | The `clone.binlog`; the `git pull origin <Branch>` step in `CloneRepository` is the usual culprit (e.g. branch renamed) |
| `Clone` fails on a V2 repo with auth error | The `AzureCLI@2` task's service connection (`SourceDotNet Stage1 Publish`); `DownloadStage1Index` reads creds from `ARM_CLIENT_ID` set by `addSpnToEnvironment: true` |
| `Clone` succeeds but `Unable to find stage1 output for repo X` | The upstream repo hasn't published a fresh bundle — check whether its Arcade publish step is healthy |
| `Prepare` produces "exit code N" warning for a repo | Open `bin/repo/<identity>/` and re-run the `PrepareCommand` by hand; most likely the upstream repo's `eng/common/build.ps1` is broken on `main` |
| `BuildIndex` runs but a repo is missing from the index | Check `bin/index.list` — if no binlogs/solutions for that repo are listed, `FindBinlogs`/`FindSolutions` didn't find them under the expected path; for V1, that usually means `Prepare` failed silently |
| Deep links on source.dot.net point at `tree/main/...` instead of a SHA | `ResolveHashV1`/`ResolveHashV2` didn't run, or the bundle's `hash` file is missing/empty |
| Overrides (header, styles, etc.) aren't reflected | Confirm the file lives under `src/index/overwrite/` with the correct relative path; the Copy step is the last thing `BuildIndex` does |
| `HtmlGenerator` itself crashes | Look at the `build.binlog` artifact uploaded by CI; the command line is reproduced in §3.13 above and can be replayed locally |
