# 07 — Deployment scripts and rollback

This document describes the PowerShell scripts under [`deployment/`](../../deployment/) that the build pipeline invokes, and the playbook for rolling back or recovering from a bad deploy. For the end-to-end pipeline walkthrough (stage ordering, triggers, slot mechanics), see [`05-azure-pipeline.md`](./05-azure-pipeline.md).

## Deployment scripts

All scripts live under [`deployment/`](../../deployment/). They are designed to be called from the Azure DevOps pipeline (see [`azure-pipelines.yml`](../../azure-pipelines.yml)) but most can be invoked locally for incident response — provided the operator has `az login` credentials with sufficient role assignments on the target resources (see [`09-access-and-permissions.md`](./09-access-and-permissions.md)).

### `util.ps1` — shared helpers

[`deployment/util.ps1`](../../deployment/util.ps1) is imported by the other scripts. It exposes three helpers:

- **`Check-Failure`** — accepts a `[ScriptBlock]` via the pipeline, executes it, and `throw`s if `$LastExitCode` is non-zero after the block runs. This is how the scripts surface failures from `az` CLI calls (which set `$LastExitCode` but do not throw on their own). Pattern used throughout the scripts:

  ```powershell
  {
    az storage container create --name "$newContainerName" ...
  } | Check-Failure
  ```

- **`Get-ContainerTTL -StorageAccountName <name> -ContainerName <name>`** — reads the `TTL` metadata key from the named blob container (`az storage container metadata show ... --query 'TTL'`). If the container has no `TTL` metadata, the function returns the default of **10**. This is the mechanism that keeps recently-rotated containers around for rollback (see "Rollback playbook" below).

- **`Set-ContainerTTL -StorageAccountName <name> -ContainerName <name> -TTL <int>`** — writes the `TTL` metadata key on the named container with `az storage container metadata update --metadata "TTL=$TTL"`.

### `create-container.ps1` — provision a fresh index container

[`deployment/create-container.ps1`](../../deployment/create-container.ps1) generates a new container name of the form `index-<GUID>` where `<GUID>` is `(New-Guid).ToString("N")` (32 lowercase hex chars, no hyphens), creates it in the supplied storage account with public access disabled (`--public-access off --fail-on-exist`), and then exposes the new name to the rest of the pipeline via:

```powershell
Write-Output "##vso[task.setvariable variable=NEW_CONTAINER_NAME]$newContainerName"
```

Downstream pipeline tasks reference it as `$(NEW_CONTAINER_NAME)`.

### `normalize-case.ps1` — lower-case every file under the index

[`deployment/normalize-case.ps1`](../../deployment/normalize-case.ps1) recursively walks the directory passed in as `-Root` (the pipeline passes `bin/index/index/`) and renames each file so its name is `ToLowerInvariant()`. The rename is done in two hops (`name → name.tmp → lowercasename`) to avoid the case-only-change pitfall on case-insensitive filesystems.

This step exists because Azure Blob Storage is case-sensitive but the HTML emitted by the SourceBrowser HTML generator does not guarantee consistent casing in cross-file links. By normalising on-disk before upload — and lowercasing the request path in [`Helpers.cs`](../../src/SourceBrowser/src/SourceIndexServer/Helpers.cs) (`proxyRequestPathSuffix = ... .ToLowerInvariant()`) — the runtime proxy lookups always succeed.

### `deploy-storage-proxy.ps1` — point a slot at a container

[`deployment/deploy-storage-proxy.ps1`](../../deployment/deploy-storage-proxy.ps1) sets the `SOURCE_BROWSER_INDEX_PROXY_URL` app setting on a given web app slot to:

```
https://<StorageAccountName>.blob.core.windows.net/<NewContainerName>
```

It validates that every parameter (`NewContainerName`, `ResourceGroup`, `StorageAccountName`, `WebappName`, `Slot`) is non-null/empty before running `az webapp config appsettings set ... --slot $Slot --settings "SOURCE_BROWSER_INDEX_PROXY_URL=..."`. The web app reads this variable at runtime via `Helpers.IndexProxyUrl` (see [`Helpers.cs`](../../src/SourceBrowser/src/SourceIndexServer/Helpers.cs)).

Because the setting is **slot-scoped**, swapping slots also swaps the proxy URL — which is what lets the swap step atomically cut prod over to a new index.

### `cleanup-old-containers.ps1` — TTL-based pruning

[`deployment/cleanup-old-containers.ps1`](../../deployment/cleanup-old-containers.ps1) runs at the end of every successful official build. The algorithm is:

1. List all containers in `$StorageAccountName` (`az storage container list`).
2. Read the `SOURCE_BROWSER_INDEX_PROXY_URL` app setting from **both** the production slot and the `staging` slot of the web app. Parse each into a container name.
3. The containers referenced by the two slots are "used"; all other containers are candidates for deletion.
4. For each candidate, read its TTL metadata via `Get-ContainerTTL` (default **10** when absent).
   - If `TTL > 0`: decrement and write back via `Set-ContainerTTL`. The container is **not** deleted this run.
   - If `TTL == 0`: delete the container with `az storage container delete`.

In practice this means a container survives roughly **10 daily builds** after the last time it was referenced by either slot, giving a wide window to roll back without losing data. The cleanup step only runs when `isOfficialBuild == True` (i.e. `main` branch, non-PR, non-public project — see [`azure-pipelines.yml`](../../azure-pipelines.yml)).

### `install-tool.ps1` — local/CI dev helper

[`deployment/install-tool.ps1`](../../deployment/install-tool.ps1) is a small helper for fetching a prebuilt tool zip from the public `netcorenativeassets` blob and prepending its `BinPath` onto the agent `PATH`. It accepts `-Name`, `-Version`, optional `-TestPath` (default `/<Name>.exe`) and `-BinPath` (default `/`), and:

1. Checks whether `$Agent_ToolsDirectory/<Name>/<Version><TestPath>` already exists. If so it skips the download.
2. Otherwise downloads `https://netcorenativeassets.blob.core.windows.net/resource-packages/external/windows/<Name>/<Name>-<Version>.zip` and `Expand-Archive`s it to that location.
3. Emits `##vso[task.prependpath]<installDir><BinPath>` so subsequent pipeline steps can call the tool by name.

It is not wired into the active deployment path in [`azure-pipelines.yml`](../../azure-pipelines.yml) today; treat it as a reusable utility for pinning a tool version onto a 1ES agent without using a NuGet/`dotnet tool` install. The `UploadIndexStage1` global tool referenced in [`09-access-and-permissions.md`](./09-access-and-permissions.md) is installed by upstream repos with `dotnet tool install`, not by this script.

## Rollback playbook

These procedures are listed roughly in order of cheapest/safest first.

### 1. Quick rollback to the last good build (slot swap back)

When a build promotes a bad index into production via the swap step in [`azure-pipelines.yml`](../../azure-pipelines.yml), the *previous* production slot contents (binaries + `SOURCE_BROWSER_INDEX_PROXY_URL`) are now sitting in the `staging` slot. Swapping back is a single atomic operation:

```bash
az webapp deployment slot swap \
  --resource-group source.dot.net \
  --name netsourceindexprod \
  --slot staging \
  --target-slot production
```

Because `SOURCE_BROWSER_INDEX_PROXY_URL` is a **slot setting**, the swap also flips the index proxy URL — production immediately serves the previously-good index container without any further action.

Caveats:
- This only works until the next official build runs (the next build overwrites `staging`). Either pause daily builds (see step 3) before swapping, or do the swap within the day.
- If the staging slot has *also* been polluted (e.g. two bad builds in a row), use step 2 instead.

### 2. Point production at an older container (no swap)

If swap-back is not viable, you can pin production to any container still in the storage account's TTL window:

```bash
# 1. List candidate containers
az storage container list \
  --account-name netsourceindexprod \
  --auth-mode login \
  --query "[].name" -o tsv

# 2. (Optional) Inspect TTL metadata to confirm the container hasn't aged out
az storage container metadata show \
  --account-name netsourceindexprod \
  --name index-<guid> \
  --auth-mode login \
  --query "TTL"

# 3. Repoint production at the chosen container
az webapp config appsettings set \
  --resource-group source.dot.net \
  --name netsourceindexprod \
  --slot production \
  --settings "SOURCE_BROWSER_INDEX_PROXY_URL=https://netsourceindexprod.blob.core.windows.net/index-<guid>"

# 4. Restart the slot so the new setting is picked up
az webapp restart \
  --resource-group source.dot.net \
  --name netsourceindexprod \
  --slot production
```

Recall that each unreferenced container has its TTL decremented once per official build — so a container last used 5 builds ago has a remaining TTL of ~5 (out of the default 10). Outside that window the container has been deleted.

### 3. Pause daily builds

To stop the 10:00 UTC cron from re-running while you investigate, disable the schedule on **definition 612** in `dnceng/internal`:

1. Open the pipeline in the Azure DevOps UI (`dnceng/internal`, definition id `612`).
2. Edit → ⋮ → **Triggers** → **Scheduled triggers** → disable or remove the cron entry.
3. Save.

This leaves manual runs available. Re-enable when the underlying issue is fixed.

### 4. Recover from a broken vendored SourceBrowser update

If a bad update to the vendored copy of [KirillOsenkov/SourceBrowser](https://github.com/KirillOsenkov/SourceBrowser) causes index generation to crash or produce bad output, revert both the hash and the files:

- Revert `src/SourceBrowser.hash` to its previous SHA.
- Revert the matching commit(s) under `src/SourceBrowser/`.

See [`02-build-and-local-dev.md`](./02-build-and-local-dev.md) for the vendoring workflow and how the hash file relates to the snapshot.

### 5. Stage1 outage — a V2 repo's bundle is missing or stale

The pipeline's **Clone Stage1 data** step downloads pre-built repo bundles from `netsourceindexstage1`. If an upstream repo (one declared as `<RepositoryV2>` in `src/index/repositories.props`) hasn't published a fresh bundle, the `Clone` step either fails or produces stale output, and downstream `Prepare` / `BuildIndex` steps will use yesterday's (or older) data.

Short-term workaround: temporarily downgrade the affected repo from `<RepositoryV2>` to `<Repository>` (V1) in `src/index/repositories.props`. V1 entries clone + build the repo live in this pipeline instead of consuming a prebuilt bundle, which removes the dependency on the upstream publish job. Revert once the upstream publish is healthy again.

### 6. Container cleanup deleted a still-needed container

Should not happen by design — `cleanup-old-containers.ps1` decrements the TTL metadata rather than deleting outright, and only deletes when TTL reaches 0 *and* the container is not referenced by either slot. If you suspect it happened anyway:

- Re-list containers: `az storage container list --account-name netsourceindexprod --auth-mode login`.
- Inspect TTL metadata on each (`az storage container metadata show ... --query "TTL"`).
- The current `SOURCE_BROWSER_INDEX_PROXY_URL` for prod and staging can be read with `az webapp config appsettings list --resource-group source.dot.net --name netsourceindexprod [--slot staging] --query "[?name=='SOURCE_BROWSER_INDEX_PROXY_URL'].value | [0]"`.

If a referenced container is gone, you must trigger a fresh manual build of definition 612 to regenerate one — there is no "undelete" for blob containers in this setup.

## Useful incident commands

A reference card. All commands assume `az login` against the prod subscription and sufficient role assignments (see [`09-access-and-permissions.md`](./09-access-and-permissions.md)).

```bash
# Show which container each slot is currently pointing at
az webapp config appsettings list \
  --resource-group source.dot.net --name netsourceindexprod \
  --query "[?name=='SOURCE_BROWSER_INDEX_PROXY_URL'].value | [0]"

az webapp config appsettings list \
  --resource-group source.dot.net --name netsourceindexprod --slot staging \
  --query "[?name=='SOURCE_BROWSER_INDEX_PROXY_URL'].value | [0]"

# List all index containers and their TTLs
az storage container list \
  --account-name netsourceindexprod --auth-mode login \
  --query "[].name" -o tsv

az storage container metadata show \
  --account-name netsourceindexprod --name index-<guid> --auth-mode login

# Swap staging into production (forward swap or rollback)
az webapp deployment slot swap \
  --resource-group source.dot.net --name netsourceindexprod \
  --slot staging --target-slot production

# Repoint a slot at an arbitrary container
az webapp config appsettings set \
  --resource-group source.dot.net --name netsourceindexprod \
  --slot production \
  --settings "SOURCE_BROWSER_INDEX_PROXY_URL=https://netsourceindexprod.blob.core.windows.net/index-<guid>"

# Restart a slot
az webapp restart \
  --resource-group source.dot.net --name netsourceindexprod --slot production

# Tail live application logs
az webapp log tail \
  --resource-group source.dot.net --name netsourceindexprod

az webapp log tail \
  --resource-group source.dot.net --name netsourceindexprod --slot staging

# Download a zip of recent application logs
az webapp log download \
  --resource-group source.dot.net --name netsourceindexprod \
  --log-file webapp-logs.zip
```

For monitoring/alerting context once a deploy is suspect, see [`08-monitoring-and-oncall.md`](./08-monitoring-and-oncall.md).
