# 04 – Arcade and dotnet/* Integration

`dotnet/source-indexer` is not a self-contained product — it's wired into the rest of the .NET engineering system in two directions:

1. **Inbound (V1 model):** this pipeline calls into each cloned repo's Arcade build script to produce binlogs.
2. **Outbound (V2 model):** other `dotnet/*` repos call into a NuGet tool that this repo publishes, to push prebuilt index bundles into Azure Blob Storage where this pipeline later picks them up.

Sibling docs:

- [00-overview.md](./00-overview.md)
- [01-repo-layout.md](./01-repo-layout.md)
- [02-build-and-local-dev.md](./02-build-and-local-dev.md)
- [03-indexing-pipeline.md](./03-indexing-pipeline.md)

---

## 1. What "Arcade" means here

[**`dotnet/arcade`**](https://github.com/dotnet/arcade) is the shared engineering system used by virtually every `dotnet/*` repo. The piece this repo cares about is the `eng/common/` folder that Arcade-onboarded repos check into their own tree. In particular, the `eng/common/build.ps1` script exposes a stable command-line surface across all those repos — including a `-binarylog` switch that emits MSBuild binlogs alongside the build output.

That stable surface is what makes V1 indexing tractable: source-indexer doesn't need to know anything repo-specific to build, say, `dotnet/iot` vs `dotnet/sdk` — it just runs:

```
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command
  "eng/common/build.ps1 -restore -build -binarylog -nodeReuse:$false"
```

This string is defined once as `$(ArcadeBuildCmd)` at the top of [`src/index/repositories.props`](../../src/index/repositories.props):

```xml
<PropertyGroup>
  <ArcadeBuildCmd>
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "eng/common/build.ps1 -restore -build -binarylog -nodeReuse:$false"
  </ArcadeBuildCmd>
</PropertyGroup>
```

Per-repo overrides happen by setting a different `PrepareCommand` metadata on the `<Repository>` item (see [03-indexing-pipeline.md §2.1](./03-indexing-pipeline.md#21-v1--repository-items-cloned--built-here)).

### Arcade-related repos visible in `repositories.props`

The indexer ingests the Arcade SDK itself and many of its primary consumers. Currently, all of these are V2:

- `arcade` → https://github.com/dotnet/arcade
- `runtime` → https://github.com/dotnet/runtime
- `aspnetcore` → https://github.com/dotnet/aspnetcore
- `roslyn` → https://github.com/dotnet/roslyn
- `winforms`, `wpf`, `maui`, `wcf`, `machinelearning`, `aspire`, `extensions`

And these are V1:

- `iot`, `msbuild`, `performance`, `sdk`

---

## 2. Integration point #1 — this repo CALLING Arcade (V1)

The `PrepareV1` target in [`src/index/index.proj`](../../src/index/index.proj) does this for every `<Repository>` item:

```xml
<Exec Command="cmd /c &quot;$(PrepareCommand)&quot;"
      WorkingDirectory="%(ClonedRepository.LocalPath)"
      ContinueOnError="true"
      IgnoreStandardErrorWarningFormat="true"
      LogStandardErrorAsError="false"
      IgnoreExitCode="true"/>
```

- `WorkingDirectory` is `bin/repo/<identity>/`, i.e. the freshly cloned repo's root.
- `PrepareCommand` defaults to `$(ArcadeBuildCmd)`. Repos with non-default project sets override:
  - `performance`: `$(ArcadeBuildCmd) -projects src\benchmarks\micro\MicroBenchmarks.sln`
  - `sdk`: `$(ArcadeBuildCmd) -projects src\benchmarks\micro\MicroBenchmarks.sln`
- Non-zero exit codes become warnings, not errors — a transiently broken upstream build does not block the index build.

The output we care about is the set of `*.binlog` files emitted by Arcade under `bin/repo/<identity>/` (recursively). Those are picked up by `FindBinlogs` and fed to HtmlGenerator. See [03-indexing-pipeline.md](./03-indexing-pipeline.md) for the full pipeline.

### 2.1 `SourceIndex.targets` — what gets injected into each V1 build

For V1 builds to produce useful binlogs that resolve assembly references to live source (rather than to compiled NuGet binaries), this repo injects targets *above* the cloned repo. The mechanism:

1. `PrepareOutput` (in `index.proj`) copies two stub files into `bin/repo/`:
   - [`Directory.Build.props.tmpl`](../../src/index/Directory.Build.props.tmpl) → `bin/repo/Directory.Build.props`
   - [`Directory.Packages.props.tmpl`](../../src/index/Directory.Packages.props.tmpl) → `bin/repo/Directory.Packages.props`

   Both are intentionally empty — they exist purely to terminate MSBuild's "walk up looking for `Directory.Build.props`" search at `bin/repo/`, preventing the cloned repo from accidentally inheriting the source-indexer repo's own props/packages files. The stubs set:
   ```xml
   <ImportDirectoryPackagesProps>false</ImportDirectoryPackagesProps>
   <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
   ```

2. [`src/index/SourceIndex.targets`](../../src/index/SourceIndex.targets) is the file that does the real work. It:
   - Adds `0433;1685` to `$(NoWarn)`.
   - Hooks `BeforeTargets="ResolveProjectReferences"` with `FixProjectReferencesForSourceIndex`, which sets `<UndefineProperties>%(UndefineProperties);CustomAfterBuildCommonTargets</UndefineProperties>` on every `<ProjectReference>`. This neutralizes a class of Arcade/custom-targets weirdness that would otherwise pollute downstream project references and confuse the indexer.
   - Hooks `AfterTargets="ResolveNuGetPackages;FilterTargetingPackResolvedNugetPackages"` with `RewritePackageReferences`. This is the centerpiece: it strips out compiled `_ReferenceFromPackage` assembly references and re-resolves them through the `ResolveLivePackageReferences` MSBuild task (from [`Microsoft.SourceIndexer.Tasks`](../../src/Microsoft.SourceIndexer.Tasks)), so that `<PackageReference>` ends up pointing at the cloned **source** in another `bin/repo/<id>/` rather than at a binary in the NuGet cache. That cross-repo redirect is what gives source.dot.net its "go-to-definition across repo boundaries" behaviour.

> **TODO (tribal knowledge):** `SourceIndex.targets` is in the repo but no `<Import>` of it appears in either `Directory.Build.props.tmpl` or `Directory.Packages.props.tmpl`. The outgoing team should document exactly how this file is hooked into each V1 build — whether the templates were intended to import it, whether an upstream Arcade convention picks it up automatically, or whether this injection is currently dormant in favour of V2.

### 2.2 The `dnceng/public` and `dotnet-public` NuGet feed

V1 repos need their normal package restore to work. [`NuGet.config`](../../NuGet.config) at the repo root pins this repo to a single feed:

```
https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet-public/nuget/v3/index.json
```

That's the public-readable feed under the [dnceng AzDO organization](https://dev.azure.com/dnceng) that mirrors `nuget.org` plus dotnet pre-release packages. Cloned V1 repos use their own `NuGet.config`; this one only matters for building source-indexer's own projects.

---

## 3. Integration point #2 — other dotnet/* repos CALLING this repo (V2)

The V2 model inverts the relationship: rather than source-indexer cloning and building each repo, **the upstream repo builds itself (in its own pipeline, with its own dependencies pre-configured) and then uploads a tarball of inputs into Azure Blob Storage**, where source-indexer picks it up on its next run.

This is the path used today for the bulk of the V2 list in [`repositories.props`](../../src/index/repositories.props): `arcade`, `roslyn`, `runtime`, `aspnetcore`, `aspire`, `extensions`, `winforms`, `wpf`, `maui`, `machinelearning`, `wcf`.

### 3.1 What this repo publishes

Two NuGet packages are produced by the source-indexer CI run ([`azure-pipelines.yml`](../../azure-pipelines.yml)) and pushed to an internal AzDO feed:

1. **`UploadIndexStage1`** — packed as a `dotnet tool`. See [`src/UploadIndexStage1/UploadIndexStage1.csproj`](../../src/UploadIndexStage1/UploadIndexStage1.csproj):
   ```xml
   <OutputType>Exe</OutputType>
   <TargetFramework>net10.0</TargetFramework>
   <PackAsTool>true</PackAsTool>
   <RollForward>Major</RollForward>
   <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
   <VersionPrefix>2.0.0</VersionPrefix>
   ```
2. **`Microsoft.SourceIndexer.Tasks`** — the MSBuild tasks assembly (`net472`) that contains `DownloadStage1Index`, `ResolveLivePackageReferences`, `SelectProjects`. Consumed by `index.proj` here; also exposed so upstream repos can take a dependency on the same task surface if they want.

The CI step that publishes them ([`azure-pipelines.yml`](../../azure-pipelines.yml)):

```yaml
templateContext:
  outputs:
  - output: nuget
    displayName: 'NuGet push'
    packageParentPath: '$(Build.ArtifactStagingDirectory)'
    packagesToPush: '$(Build.ArtifactStagingDirectory)/packages/*.nupkg'
    nuGetFeedType: 'internal'
    publishVstsFeed: '9ee6d478-d288-47f7-aacc-f6e6d082ae6d/d1622942-d16f-48e5-bc83-96f4539e7601'
```

The GUIDs decompose as `<AzDO project id>/<feed id>` inside the **`dnceng/internal`** AzDO organization, which is the internal `dotnet-tools-internal` feed.

> **TODO (tribal knowledge):** confirm the exact display name of the feed behind GUID `d1622942-d16f-48e5-bc83-96f4539e7601` (commonly `dotnet-tools-internal` under `https://dev.azure.com/dnceng/internal`), and document the package id under which `UploadIndexStage1` is consumed by upstream repos (it's the .csproj's package id — verify it matches what those repos' `dotnet tool install` lines reference).

### 3.2 How an upstream repo uses it

The pattern (executed inside the upstream repo's own Arcade-based pipeline, after their normal `-binarylog` build):

1. Install the tool from the internal AzDO feed: `dotnet tool install <package-id> --version 2.* --add-source <dnceng/internal feed URL>`.
2. Run it pointing at the folder that contains their source + binlogs + a top-level `hash` file:
   ```
   UploadIndexStage1
     -i <source-folder>
     -n <repo-blob-prefix, e.g. dotnet-roslyn>
     -s netsourceindexstage1
     -b stage1
     [-c <client id, optional>]
   ```
3. The tool ([`src/UploadIndexStage1/Program.cs`](../../src/UploadIndexStage1/Program.cs)):
   - Resolves credentials via the same precedence as `DownloadStage1Index` (explicit `-c` → `ARM_CLIENT_ID` env var → `AzureCliCredential`).
   - Builds an in-memory `tar.gz` of the entire `-i` folder.
   - Uploads it as `<repoName>/<UTC timestamp ISO-8601>.tar.gz` into the container.
   - Garbage-collects: keeps the 10 newest blobs under `<repoName>/`, deletes the rest.

### 3.3 Bundle on-disk layout

What gets tarred is whatever's in the `-i` folder. The contracts that this repo's `DownloadStage1Index` + `index.proj` rely on are:

| File | Purpose | Read by |
|---|---|---|
| `hash` (at bundle root) | Single line containing the source-commit SHA | `ResolveHashV2` (via `ReadLinesFromFile`) |
| `src/**` (or similar) | The actual source tree for `HtmlGenerator` to render | `HtmlGenerator` via `serverPath` mapping `LocalPath = bin/repo/<id>/src/` |
| `**/*.sln` | Solution files | `FindSolutions` |
| `**/*.binlog` | Build logs (when applicable) | (V2 currently only globs `.sln`; see [03-indexing-pipeline.md §3.11](./03-indexing-pipeline.md#311-findsolutions)) |

> **TODO (tribal knowledge):** finalize the canonical V2 bundle layout. The pipeline's contract is loose (a `hash` file plus arbitrary `.sln`/source under `ExtractPath`, and `LocalPath` defaults to `ExtractPath/src/`), but each upstream repo currently chooses its own internal structure. Document the agreed convention here, or codify it as a check inside `DownloadStage1Index`.

### 3.4 The storage account

| Property | Value |
|---|---|
| Storage account | `netsourceindexstage1` |
| Container | `stage1` |
| AzDO service connection (read & write) | `SourceDotNet Stage1 Publish` |
| Auth mechanism | `AzureCLI@2` + `addSpnToEnvironment: true` (exports `ARM_CLIENT_ID` to the env, which both `DownloadStage1Index` and `UploadIndexStage1` consume) |

Wired into source-indexer CI's "🟣Clone Stage1 data" step ([`azure-pipelines.yml`](../../azure-pipelines.yml)):

```
dotnet build build.proj /t:Clone /v:n
  /p:Stage1StorageAccount=netsourceindexstage1
  /p:Stage1StorageContainer=stage1
```

Upstream repos that publish must have their own AzDO project federated to a service principal with **write** access to the same container. Source-indexer only needs **read**.

### 3.5 V2 onboarding is gated on upstream work

A repo can only move from V1 → V2 once its own pipeline does steps 1–3 in §3.2 above on every successful main-branch build. Until then, source-indexer must keep cloning + building it the V1 way.

> **TODO (tribal knowledge):** list which currently-V1 repos (`iot`, `msbuild`, `performance`, `sdk`) are scheduled to migrate to V2, what's blocking each one (typically: their pipeline doesn't yet install/run the `UploadIndexStage1` tool, or the `SourceDotNet Stage1 Publish` SPN doesn't have access to their AzDO project), and any historical context on why some `dnceng/internal` repos still index via V1 instead of V2.

---

## 4. Dependencies on the rest of the dotnet eng ecosystem

| Dependency | Where it shows up | Notes |
|---|---|---|
| **`dotnet/arcade`** — `eng/common/build.ps1` | `$(ArcadeBuildCmd)` in [`repositories.props`](../../src/index/repositories.props); invoked by `PrepareV1` | Stable command-line surface; this repo treats it as a contract |
| **`dnceng/public` AzDO** — `dotnet-public` NuGet feed | [`NuGet.config`](../../NuGet.config) at repo root | Only feed used to build this repo's own projects |
| **`dnceng/internal` AzDO** — internal NuGet feed | [`azure-pipelines.yml`](../../azure-pipelines.yml) `publishVstsFeed: 9ee6d478-d288-47f7-aacc-f6e6d082ae6d/d1622942-d16f-48e5-bc83-96f4539e7601` | Destination for `UploadIndexStage1` + `Microsoft.SourceIndexer.Tasks` packages |
| **1ES Pipeline Templates** — `1ESPipelineTemplates/1ESPipelineTemplates` | `resources.repositories` in `azure-pipelines.yml`; `extends.template: v1/1ES.Official.PipelineTemplate.yml@1ESPipelineTemplates` | Standard Microsoft compliance pipeline shell (SDL, CFS, signing, etc.) |
| **Azure Blob Storage** — `netsourceindexstage1/stage1` | V2 bundle exchange | See §3.4 |
| **`KirillOsenkov/SourceBrowser`** — `HtmlGenerator` | Submodule at [`src/SourceBrowser`](../../src/SourceBrowser); built by `BuildGenerator` | Upstream of the actual HTML-rendering tool; not part of dotnet/* per se but treated as a hard dependency |
| **Azure App Service** — `netsourceindexprod` | `azure-pipelines.yml` deploy steps | Final hosting target; `staging` slot is swapped into `production` on official builds. Resource group: `source.dot.net` |

---

## 5. Visual: the two integration models side by side

```
V1 (this repo drives upstream's Arcade build):

  source-indexer CI
        │
        ├── git clone github.com/dotnet/<repo>     (CloneRepository)
        ├── cmd /c "powershell eng/common/build.ps1 -restore -build -binarylog"   (PrepareV1)
        │       │
        │       └── *.binlog written under bin/repo/<repo>/
        ├── git rev-parse HEAD                     (ResolveHashV1) → ServerPath
        └── HtmlGenerator ← bin/index.list


V2 (upstream drives, source-indexer just downloads):

  upstream repo's CI (Arcade)
        │
        ├── normal Arcade -binarylog build
        ├── write `hash` file with current SHA
        └── dotnet UploadIndexStage1 -i <folder> -n dotnet-<repo>
                -s netsourceindexstage1 -b stage1
                │
                └── tar.gz uploaded to
                    netsourceindexstage1/stage1/dotnet-<repo>/<UTC>.tar.gz

  source-indexer CI
        │
        ├── DownloadStage1Index (newest blob under dotnet-<repo>/)   (DownloadRepositoryV2)
        ├── extract tar.gz into bin/repo/<repo>/                     (DownloadRepositoryV2)
        ├── read bin/repo/<repo>/hash                                (ResolveHashV2) → ServerPath
        └── HtmlGenerator ← bin/index.list
```

---

## 6. If something breaks here…

| Symptom | Look first at |
|---|---|
| All V1 repos fail in `PrepareV1` | Something changed in `eng/common/build.ps1` across the Arcade SDK; check the [dotnet/arcade](https://github.com/dotnet/arcade) repo for recent script changes and re-run one V1 prep locally |
| A single V1 repo fails in `PrepareV1` | The upstream repo's build is broken on `main` — verify on its own CI; consider pinning `OldCommit` in `repositories.props` until they recover |
| `DownloadStage1Index` reports `Unable to find stage1 output for repo X` | Upstream repo has not published a fresh bundle. Check their CI for the `UploadIndexStage1` step; verify they're writing to `netsourceindexstage1`/`stage1`/`<RepoName>/`; verify their SPN still has write access |
| Auth failures in `DownloadStage1Index` or `UploadIndexStage1` | The service connection (`SourceDotNet Stage1 Publish` for read; the upstream repo's own service connection for write). Confirm `addSpnToEnvironment: true` and that `ARM_CLIENT_ID` is in scope |
| Upstream consumers of `UploadIndexStage1` get version conflicts | Bump `VersionPrefix` in [`src/UploadIndexStage1/UploadIndexStage1.csproj`](../../src/UploadIndexStage1/UploadIndexStage1.csproj) and let CI publish a new package to the internal feed (`9ee6d478-d288-47f7-aacc-f6e6d082ae6d/d1622942-d16f-48e5-bc83-96f4539e7601` in `dnceng/internal`) |
| Cross-repo "go to definition" stops working on source.dot.net | Likely `SourceIndex.targets` / `ResolveLivePackageReferences` not getting picked up; verify the templates in `bin/repo/` are landing and look at one V1 build's binlog to confirm `RewritePackageReferences` ran |
| 1ES template breaks the pipeline | Pin / unpin `ref: refs/tags/release` on `1ESPipelineTemplates` in [`azure-pipelines.yml`](../../azure-pipelines.yml); coordinate with the 1ES team via the standard 1ES support channels |
| `dotnet-public` feed is unreachable | The pipeline cannot restore anything; check [status.dev.azure.com](https://status.dev.azure.com) and the dnceng AzDO instance |
