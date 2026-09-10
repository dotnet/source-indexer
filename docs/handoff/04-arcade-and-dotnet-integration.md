# 04 – Arcade and dotnet/* Integration

`dotnet/source-indexer` is not a self-contained product — it's wired into the rest of the .NET engineering system in two directions:

1. **Inbound (V1 model):** the source-indexer pipeline calls into the Arcade `eng/common/build.ps1` script (or `build.cmd`) of each cloned V1 repo to produce binlogs locally.
2. **Outbound (V2 model):** other `dotnet/*` repos call into a NuGet tool that this repo publishes (from their own Arcade-driven pipelines), to push prebuilt index bundles into Azure Blob Storage where the source-indexer pipeline later picks them up.

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

**Three** NuGet packages are produced by the source-indexer CI run ([`azure-pipelines.yml`](../../azure-pipelines.yml)) and pushed to the **`dnceng/public` `dotnet-tools` AzDO feed** on every build:

1. **`UploadIndexStage1`** — packed as a `dotnet tool`. See [`src/UploadIndexStage1/UploadIndexStage1.csproj`](../../src/UploadIndexStage1/UploadIndexStage1.csproj):
   ```xml
   <OutputType>Exe</OutputType>
   <TargetFramework>net10.0</TargetFramework>
   <PackAsTool>true</PackAsTool>
   <RollForward>Major</RollForward>
   <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
   <VersionPrefix>2.0.0</VersionPrefix>
   ```
   This is the tool that uploads the stage1 tarball to `netsourceindexstage1/stage1/`. Arcade pins it at `2.0.0-20250906.1` in `eng/common/core-templates/steps/source-index-stage1-publish.yml`.
2. **`BinLogToSln`** — also packed as a `dotnet tool`. See [`src/SourceBrowser/src/BinLogToSln/BinLogToSln.csproj`](../../src/SourceBrowser/src/BinLogToSln/BinLogToSln.csproj):
   ```xml
   <OutputType>Exe</OutputType>
   <TargetFramework>net10.0</TargetFramework>
   <PackAsTool>true</PackAsTool>
   <RollForward>Major</RollForward>
   <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
   <VersionPrefix>1.0.1</VersionPrefix>
   ```
   Lives in the vendored SourceBrowser. Reads an MSBuild `.binlog`, picks the best `csc`/`vbc` invocation per assembly (see [`docs/source-selection-algorithm.md`](../source-selection-algorithm.md)), and emits a `.sln` plus a normalised source tree under `.source-index/stage1output/` ready for `HtmlGenerator`. Arcade pins it at `1.0.1-20250906.1`.
3. **`Microsoft.SourceIndexer.Tasks`** — the MSBuild tasks assembly (`net472`) containing `DownloadStage1Index`, `ResolveLivePackageReferences`, `SelectProjects`. Consumed by `index.proj` here; also exposed so any upstream repo that wants to take a dependency on the same task surface can.

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

The GUIDs decompose as `<AzDO project id>/<feed id>`. Despite `nuGetFeedType: 'internal'` (which is just AzDO-task terminology meaning "an AzDO-hosted feed, not nuget.org"), the project GUID `9ee6d478-d288-47f7-aacc-f6e6d082ae6d` is **`dnceng/public`**, and the feed GUID resolves to the **`dotnet-tools`** feed at:

```
https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json
```

This matches the `sourceIndexPackageSource` parameter Arcade defaults to in [`eng/common/core-templates/steps/source-index-stage1-publish.yml`](https://github.com/dotnet/arcade/blob/main/eng/common/core-templates/steps/source-index-stage1-publish.yml), which is what makes the V2 onboarding "just work" (see §3.2). Anyone with network access to `dnceng/public` can `dotnet tool install UploadIndexStage1` directly.

> **TODO @radical (tribal knowledge):** confirm the human-readable feed name (it should be `dotnet-tools` per the arcade default URL) and the package ids each is published under. Based on the csprojs they are `UploadIndexStage1`, `BinLogToSln`, and `Microsoft.SourceIndexer.Tasks` — verify by listing the feed and reconciling against the pinned versions in arcade.

### 3.2 How an upstream repo uses it (via Arcade's `enableSourceIndex`)

Other dotnet repos do **not** call `UploadIndexStage1` by hand. Arcade ships first-class source-indexer support in `eng/common/`, and an upstream repo opts in by flipping a single YAML parameter on the Arcade jobs template:

```yaml
# In the upstream repo's pipeline (e.g. eng/build.yml):
- template: /eng/common/templates/jobs/jobs.yml   # or templates-official for 1ES
  parameters:
    enableSourceIndex: true
    sourceIndexParams: {}   # optional overrides; see below
    jobs:
      - ...
```

When `enableSourceIndex: true`, Arcade injects a sibling job called **`SourceIndexStage1`** into the same stage. Defined at [`eng/common/core-templates/job/source-index-stage1.yml`](https://github.com/dotnet/arcade/blob/main/eng/common/core-templates/job/source-index-stage1.yml), the job:

1. Runs the build with binlogs enabled. Default command:
   ```
   powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command
     "eng/common/build.ps1 -restore -build -binarylog -ci"
   ```
   Override via `sourceIndexParams.sourceIndexBuildCommand` if your repo uses a custom build entrypoint.
2. Calls the publish step at [`eng/common/core-templates/steps/source-index-stage1-publish.yml`](https://github.com/dotnet/arcade/blob/main/eng/common/core-templates/steps/source-index-stage1-publish.yml), which:
   - Installs a private .NET 9 SDK into `$(Agent.TempDirectory)/dotnet` to avoid clashing with the repo's `global.json`.
   - `dotnet tool install`s two tools from the **`dnceng/public` `dotnet-tools` NuGet feed** (`https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json`):
     - **`BinLogToSln`** — pinned version `1.0.1-20250906.1` at the time of writing.
     - **`UploadIndexStage1`** — pinned version `2.0.0-20250906.1`.
   - Runs `BinLogToSln -i <binlog> -r $(System.DefaultWorkingDirectory) -n $(Build.Repository.Name) -o .source-index/stage1output`. **This is the canonical V2 bundle layout producer** — the `.source-index/stage1output` folder is exactly what later gets tarred and uploaded, and answers the open question in §3.3 below about what the bundle should contain.
   - **Conditional upload step:** only runs when `runAsPublic != 'true'` AND `System.TeamProject != 'public'` AND `Build.Reason != 'PullRequest'`. So public repos and PR builds will *produce* a stage1 bundle locally but never push it. This is the security gate that keeps unreviewed code out of `source.dot.net`.
   - Uses `AzureCLI@2` with `azureSubscription: 'SourceDotNet Stage1 Publish'` (the same service connection name source-indexer's own pipeline uses for downloads — see §3.4) and `addSpnToEnvironment: true`, then invokes:
     ```
     UploadIndexStage1 -i .source-index/stage1output -n $(Build.Repository.Name) -s netsourceindexstage1 -b stage1
     ```

> **TODO @radical (tribal knowledge):** confirm what governs the pin bumps (`2.0.0-20250906.1` / `1.0.1-20250906.1`) — are those updated by a darc/maestro flow when source-indexer ships a new tool, or is it a manual PR to arcade's `source-index-stage1-publish.yml`? If manual, document who owns that PR. (`BinLogToSln` is published from this repo too — see §3.1 — so both pinned versions are governed by source-indexer's CI run that updates the `dnceng/public` `dotnet-tools` feed.)

#### 3.2.1 Defaults and how to override them

From [`eng/common/core-templates/job/source-index-stage1.yml`](https://github.com/dotnet/arcade/blob/main/eng/common/core-templates/job/source-index-stage1.yml):

| Parameter | Default | When to override |
|---|---|---|
| `condition` | `eq(variables['Build.SourceBranch'], 'refs/heads/main')` | Default is **main branch only**. Repos that index from a release branch (e.g. `release/9.0`) must pass an `or(...)` condition. |
| `sourceIndexBuildCommand` | `powershell -NoLogo ... eng/common/build.ps1 -restore -build -binarylog -ci` | Override if your repo needs `-projects ...`, extra MSBuild props, or a non-Arcade build entrypoint. |
| `binlogPath` | `artifacts/log/Debug/Build.binlog` | Override if your build emits the binlog elsewhere (e.g. `Release` configuration). |
| `pool` | `windows.vs2026.amd64` on `$(DncEngInternalBuildPool)` for internal projects; `windows.vs2026.amd64.open` on `$(DncEngPublicBuildPool)` for public | Override only if your repo has tighter pool requirements. |
| `preSteps` | `[]` | Use to run any one-time setup before the build (e.g. install a custom toolchain). |
| `dependsOn` | `''` | Use to gate `SourceIndexStage1` on another job (e.g. wait for code-signing to complete first). |

Plus the publish-step defaults from [`source-index-stage1-publish.yml`](https://github.com/dotnet/arcade/blob/main/eng/common/core-templates/steps/source-index-stage1-publish.yml):

| Parameter | Default |
|---|---|
| `sourceIndexUploadPackageVersion` | `2.0.0-20250906.1` |
| `sourceIndexProcessBinlogPackageVersion` | `1.0.1-20250906.1` |
| `sourceIndexPackageSource` | `https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json` |

All of these are passed in as `sourceIndexParams.<key>` from the caller. Example, for a repo that wants to index its `Release` build and also run on `release/10.0`:

```yaml
- template: /eng/common/templates-official/jobs/jobs.yml
  parameters:
    enableSourceIndex: true
    sourceIndexParams:
      binlogPath: artifacts/log/Release/Build.binlog
      sourceIndexBuildCommand: powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "eng/common/build.ps1 -restore -build -binarylog -ci -configuration Release"
      condition: or(eq(variables['Build.SourceBranch'], 'refs/heads/main'), eq(variables['Build.SourceBranch'], 'refs/heads/release/10.0'))
    jobs:
      - ...
```

#### 3.2.2 Onboarding a new V2 repo — checklist

Concretely, to migrate a `dotnet/<repo>` from V1 (cloned by us) to V2 (publishes its own stage1):

1. **In the upstream repo's pipeline YAML:** flip `enableSourceIndex: true` on the `jobs.yml` (or `templates-official/jobs/jobs.yml` for 1ES) template invocation. No other YAML changes are needed if the defaults fit.
2. **Verify the `SourceDotNet Stage1 Publish` service connection is granted to the upstream repo's AzDO project** so the `AzureCLI@2` step has the write SPN injected. This is the most common blocker — see §3.4. The service-connection name must match exactly.
3. **First successful run on `main` should produce a blob** at `netsourceindexstage1/stage1/<Build.Repository.Name>/<UTC>.tar.gz`. Eyeball it with Azure Storage Explorer to confirm before flipping us over.
4. **In *this* repo:** remove the `<Repository Include="..."/>` entry from [`src/index/repositories.props`](../../src/index/repositories.props) and add a matching `<RepositoryV2 Include="dotnet-<repo>" />`. The `dotnet-<repo>` naming convention matches `$(Build.Repository.Name)` — verify by inspecting the blob path the upstream build wrote in step 3.
5. **Trigger a manual run of source-indexer pipeline 612 from `main`** to validate the V2 path works end-to-end before the next daily run is depended on.

> **TODO @radical (tribal knowledge):** confirm the exact `$(Build.Repository.Name)` convention each upstream repo writes under — based on `eng/build.yml` and `azure-pipelines-pr.yml` in arcade, it appears to be `dotnet-<reponame>` (e.g. `dotnet-arcade`, `dotnet-runtime`), but some upstream pipelines may rewrite that variable. Spot-check by listing `netsourceindexstage1/stage1/` blobs and reconciling with [`repositories.props`](../../src/index/repositories.props).

#### 3.2.3 What this means operationally

- **No tool acquisition from this repo's side.** Upstream repos pull `UploadIndexStage1` straight from the `dnceng/public` `dotnet-tools` feed, *not* from any internal feed. Our pipeline's job is just to keep publishing fresh versions to that feed (see §3.1 for the push) and bumping the pinned versions in arcade when there's a breaking change.
- **Breaking changes in `UploadIndexStage1` propagate via Arcade.** Because the version is pinned in `eng/common/core-templates/steps/source-index-stage1-publish.yml`, every consumer flows new versions when they pick up new `eng/common/` via the normal arcade flow. There is no per-repo install line to chase.
- **PRs and public builds are inherently safe.** The `if and(ne(runAsPublic, 'true'), ne(System.TeamProject, 'public'), notin(Build.Reason, 'PullRequest'))` guard inside the publish template means a malicious PR cannot upload arbitrary content to `netsourceindexstage1`.

The older "manual `dotnet tool install`" pattern documented previously in this section still works as a fallback for repos that have not yet onboarded arcade's `eng/common/`, but is no longer the recommended path.

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
| **`dotnet/arcade`** — `eng/common/templates*/jobs/jobs.yml` with `enableSourceIndex: true` | Embedded in each upstream V2 repo's pipeline (via `eng/common/`) | The first-class V2 onboarding entrypoint — see §3.2 |
| **`dotnet/arcade`** — `eng/common/core-templates/job/source-index-stage1.yml` + `steps/source-index-stage1-publish.yml` | Same | Defines the `SourceIndexStage1` job that runs `BinLogToSln` + `UploadIndexStage1` |
| **`dnceng/public` AzDO** — `dotnet-public` NuGet feed | [`NuGet.config`](../../NuGet.config) at repo root | Only feed used to build this repo's own projects |
| **`dnceng/public` AzDO** — `dotnet-tools` NuGet feed | [`azure-pipelines.yml`](../../azure-pipelines.yml) `publishVstsFeed: 9ee6d478-d288-47f7-aacc-f6e6d082ae6d/d1622942-d16f-48e5-bc83-96f4539e7601`; consumed by Arcade as `sourceIndexPackageSource` | Destination for `UploadIndexStage1`, `BinLogToSln`, `Microsoft.SourceIndexer.Tasks` packages |
| **1ES Pipeline Templates** — `1ESPipelineTemplates/1ESPipelineTemplates` | `resources.repositories` in `azure-pipelines.yml`; `extends.template: v1/1ES.Official.PipelineTemplate.yml@1ESPipelineTemplates` | Standard Microsoft compliance pipeline shell (SDL, CFS, signing, etc.) |
| **Azure Blob Storage** — `netsourceindexstage1/stage1` | V2 bundle exchange | See §3.4 |
| **`KirillOsenkov/SourceBrowser`** — `HtmlGenerator` and `BinLogToSln` | Vendored at [`src/SourceBrowser`](../../src/SourceBrowser); built by `BuildGenerator` | Upstream of the actual HTML-rendering tool; not part of dotnet/* per se but treated as a hard dependency |
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


V2 (upstream drives via Arcade's `enableSourceIndex`, source-indexer just downloads):

  upstream repo's CI (Arcade jobs.yml with `enableSourceIndex: true`)
        │
        ├── normal Arcade build with -binarylog
        ├── BinLogToSln  -i <binlog>  -r <repo root>  -n <repo name>
        │     -o .source-index/stage1output
        │     (writes `hash`, `.sln`, normalised source tree)
        └── UploadIndexStage1  -i .source-index/stage1output  -n <repo>
              -s netsourceindexstage1  -b stage1
                │
                └── tar.gz uploaded to
                    netsourceindexstage1/stage1/<repo>/<UTC>.tar.gz

  source-indexer CI
        │
        ├── DownloadStage1Index (newest blob under <repo>/)   (DownloadRepositoryV2)
        ├── extract tar.gz into bin/repo/<repo>/              (DownloadRepositoryV2)
        ├── read bin/repo/<repo>/hash                         (ResolveHashV2) → ServerPath
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
| Upstream consumers of `UploadIndexStage1` get version conflicts | The pinned version lives in Arcade at [`eng/common/core-templates/steps/source-index-stage1-publish.yml`](https://github.com/dotnet/arcade/blob/main/eng/common/core-templates/steps/source-index-stage1-publish.yml) (`sourceIndexUploadPackageVersion` / `sourceIndexProcessBinlogPackageVersion`). To roll out a new version: bump `VersionPrefix` in [`UploadIndexStage1.csproj`](../../src/UploadIndexStage1/UploadIndexStage1.csproj) (or `BinLogToSln.csproj`), let CI publish to the `dnceng/public` `dotnet-tools` feed, then open a PR to arcade updating the two pinned versions. Downstream repos flow it via their normal arcade `eng/common/` update. |
| Upstream repo opted into `enableSourceIndex: true` but no blob shows up under `netsourceindexstage1/stage1/<repo>/` | First check the `Build.SourceBranch` — the default `condition` only runs on `main`. Then verify `runAsPublic` is not set on the jobs.yml call and the project is `internal`, not `public` (the publish step has a hard gate, see §3.2). Last: the `SourceDotNet Stage1 Publish` AzDO service connection has to be granted to that upstream repo's AzDO project, with write access to the storage account. |
| Need to debug what an upstream repo's `BinLogToSln` step actually wrote | Inside the failing upstream pipeline run, view the `SourceIndexStage1` job → `Source Index: Process Binlog into indexable sln` step log; the output folder `.source-index/stage1output` lives under `$(System.DefaultWorkingDirectory)` and isn't published as a pipeline artifact by default, so reproduce locally by `dotnet tool install BinLogToSln --source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json` and running it against the binlog from that build. |
| Cross-repo "go to definition" stops working on source.dot.net | Likely `SourceIndex.targets` / `ResolveLivePackageReferences` not getting picked up; verify the templates in `bin/repo/` are landing and look at one V1 build's binlog to confirm `RewritePackageReferences` ran |
| 1ES template breaks the pipeline | Pin / unpin `ref: refs/tags/release` on `1ESPipelineTemplates` in [`azure-pipelines.yml`](../../azure-pipelines.yml); coordinate with the 1ES team via the standard 1ES support channels |
| `dotnet-public` feed is unreachable | The pipeline cannot restore anything; check [status.dev.azure.com](https://status.dev.azure.com) and the dnceng AzDO instance |
