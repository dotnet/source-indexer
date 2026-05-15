# 09 — Access and permissions

This document inventories the service connections, role assignments, and accounts a new owner needs to operate `source.dot.net`. Where information is not discoverable from this repo, it is flagged as `**TODO (tribal knowledge):**` and needs to be filled in by the outgoing team.

For pipeline mechanics that consume these, see [`05-azure-pipeline.md`](./05-azure-pipeline.md). For incident-time use of the same credentials, see [`07-deployment-and-rollback.md`](./07-deployment-and-rollback.md) and [`08-monitoring-and-oncall.md`](./08-monitoring-and-oncall.md).

## Azure DevOps service connections

All three connection names below are reproduced verbatim from [`azure-pipelines.yml`](../../azure-pipelines.yml). They live in the `dnceng/internal` Azure DevOps project (pipeline definition **612**).

| Service connection name (pipeline variable) | Subscription / target | Used for | Notes / required role |
|---|---|---|---|
| `SourceDotNet Stage1 Publish` (`azureSubscriptionForStage1Download`) | Stage1 subscription containing the `netsourceindexstage1` storage account | The **Clone Stage1 data** step — `dotnet build build.proj /t:Clone /p:Stage1StorageAccount=netsourceindexstage1 /p:Stage1StorageContainer=stage1` — downloads prebuilt bundles for V2 repos. | Needs at least **Storage Blob Data Reader** on the `netsourceindexstage1` storage account (or on the `stage1` container). Uses `addSpnToEnvironment: true` so `DefaultAzureCredential` inside the tool picks up the workload-identity SPN. |
| `NetSourceIndex-Prod` (`azureSubscriptionForStorageAndWebAppSlot` when `isOfficialBuild == True`) | Prod subscription containing the `netsourceindexprod` storage account and the `netsourceindexprod` web app in resource group `source.dot.net` | All prod deployment steps: container create ([`create-container.ps1`](../../deployment/create-container.ps1)), blob upload (`AzureFileCopy@6`), app-settings update ([`deploy-storage-proxy.ps1`](../../deployment/deploy-storage-proxy.ps1)), restart, slot swap, container cleanup ([`cleanup-old-containers.ps1`](../../deployment/cleanup-old-containers.ps1)). | Needs **Storage Blob Data Contributor** on `netsourceindexprod` storage account (the scripts use `--auth-mode login`) and **Website Contributor** on the `netsourceindexprod` web app (for `az webapp config appsettings set`, `az webapp restart`, `az webapp deployment slot swap`). |
| `NetSourceIndex-Validation-Prod` (`azureSubscriptionForStorageAndWebAppSlot` for validation runs — PRs, non-`main`, public project) | Validation subscription containing the `netsourceindexvalidprod` storage account | Same shape as `NetSourceIndex-Prod`, but the staging slot used here is named `validation` and there is no swap-into-production step (the validation branch of the `${{ if }}` sets `isOfficialBuild: False`). | Same role shape as `NetSourceIndex-Prod`, scoped to `netsourceindexvalidprod`. |

**TODO (tribal knowledge):**
- Which Azure AD tenant each subscription lives in.
- Whether each service connection is backed by a workload-identity federation (preferred) or a classic SPN with secret. The `AzureCLI@2` task with `addSpnToEnvironment: true` works with both; the actual identity binding is held in the AzDO service-connection config which is not in the repo.
- Who can edit each service connection (AzDO service-connection administrators in `dnceng/internal`).

## Agent pools

From [`azure-pipelines.yml`](../../azure-pipelines.yml):

- `NetSourceIndexProd-Pool` — used for official builds (the `main` branch path).
- `NetSourceIndexValid-Pool` — used for validation builds (PRs and other branches).

Both are private 1ES pools running the `1es-pt-agent-image` Windows image.

**TODO (tribal knowledge):**
- Which Azure DevOps project/organization owns each pool (`dnceng`? a different org?).
- How a new owner requests pool membership / "use" permission.
- Who administers the pool (image refresh cadence, capacity, allowlisted pipelines).
- Cost-center / billing owner.

## Pipeline access

Pipeline definition **612** in `dnceng/internal` (build status badge in [`README.md`](../../README.md)).

**TODO (tribal knowledge):**
- Who has **Edit** rights on definition 612 (variables, triggers, YAML override).
- Who can **Queue** manual runs.
- Who can disable or re-enable the daily 10:00 UTC cron schedule.
- Which AzDO security group / DL is the pipeline owner.

## Internal NuGet feed

[`azure-pipelines.yml`](../../azure-pipelines.yml) pushes packages from `$(Build.ArtifactStagingDirectory)/packages/*.nupkg` to the internal feed:

- Project + Feed GUIDs: `9ee6d478-d288-47f7-aacc-f6e6d082ae6d/d1622942-d16f-48e5-bc83-96f4539e7601`
- Hosted in `dnceng/internal`.

The primary published artifact is the `UploadIndexStage1` global tool, which downstream repos consume with something like `dotnet tool install UploadIndexStage1 --add-source <feed>` in their own publish pipelines.

**TODO (tribal knowledge):**
- The human-readable feed name (the YAML only carries the GUID pair).
- Who administers the feed (Feed Owners role) and can grant/revoke push/read permissions.
- The list of downstream consumer repos that `dotnet tool install UploadIndexStage1` from this feed — the "fan-out" that would be impacted by a breaking change to the tool.
- Whether the feed has views (`@prerelease`, `@release`) that need to be promoted into.

## Custom domain and DNS for `source.dot.net`

The public hostname `source.dot.net` is bound to the `netsourceindexprod` web app, with `staging.source.dot.net` bound to its `staging` slot (referenced as `$(stagingHost)` in [`azure-pipelines.yml`](../../azure-pipelines.yml)).

**TODO (tribal knowledge):**
- The full custom-hostname configuration on the App Service (which slot, what bindings, SNI/IP).
- TLS certificate source — App Service Managed Certificate, an imported cert from Key Vault, or something else; renewal owner if it's not auto-managed.
- Owner of the `dot.net` DNS zone (and which Azure DNS / external DNS service it is hosted in).
- Where the CNAME / A record for `source.dot.net` and `staging.source.dot.net` actually lives and who can change it.

## App Insights and Grafana access

Per [`08-monitoring-and-oncall.md`](./08-monitoring-and-oncall.md), monitoring data flows through the `dotnet-eng` Application Insights resource and surfaces on `dotnet-eng-grafana.westus2.cloudapp.azure.com`.

**TODO (tribal knowledge):**
- The subscription and resource group hosting the `dotnet-eng` Application Insights resource.
- Who currently has **Reader** / **Contributor** on that resource group, and how to request membership.
- Grafana authentication — which Azure AD group(s) are mapped to Viewer / Editor / Admin roles on the `dotnet-eng-grafana` instance.
- Who owns the alert routes / destinations (PagerDuty service, Teams webhook, email DL) and how to add a new on-call contact to the routes.

## Managed identity / federated identity

[`Helpers.cs`](../../src/SourceBrowser/src/SourceIndexServer/Helpers.cs) reads `SOURCE_BROWSER_INDEX_PROXY_URL` and proxies blob requests through `AzureBlobFileSystem`. The upstream/downstream tools that interact with the stage1 storage account (`DownloadStage1Index`, `UploadIndexStage1`) authenticate with `DefaultAzureCredential` and optionally a `ClientId`, so they work with either:

- A managed identity assigned to the runtime host (web app or pipeline agent), or
- A federated workload identity exposed via `AzureCLI@2 ... addSpnToEnvironment: true` (which is how the `Clone Stage1 data` step in [`azure-pipelines.yml`](../../azure-pipelines.yml) is wired — `addSpnToEnvironment: true` injects `AZURESUBSCRIPTION_*`/`servicePrincipalId` etc. into the environment so `DefaultAzureCredential` can pick the right principal).

**TODO (tribal knowledge):**
- The specific managed identity or federated workload-identity SPN that backs each AzDO service connection above (object ID, app ID, display name).
- The managed identity (if any) assigned to the `netsourceindexprod` web app, used at runtime by `AzureBlobFileSystem` against the index container.
- Which Entra ID tenant each identity lives in.
- The role assignments on `netsourceindexprod` / `netsourceindexstage1` that grant each identity its required access (Storage Blob Data Reader / Contributor on which scope).

## GitHub repository permissions

The codebase is [`dotnet/source-indexer`](https://github.com/dotnet/source-indexer) on GitHub.

**TODO (tribal knowledge):**
- Current `CODEOWNERS` (no `CODEOWNERS` file is checked into this repo; presumably defaults to org-level admins).
- Members of the `dotnet/source-indexer` admin / maintain teams on GitHub.
- Branch protection rules on `main` (required reviewers, required checks).
- Whether the GitHub repo is mirrored into Azure DevOps or vice versa, and who owns the mirror configuration if so.

## Day-one access checklist for new owners

A new owning team should confirm each of the following before the outgoing team disengages:

- [ ] Access to the `dnceng/internal` Azure DevOps project — at minimum **Reader**, ideally **Build Administrator** (or equivalent group membership) on pipeline definition **612** so triggers and variables can be edited.
- [ ] Membership / "use" permission on **`NetSourceIndexProd-Pool`** and **`NetSourceIndexValid-Pool`** so queued builds actually pick up an agent.
- [ ] **Contributor** on the `source.dot.net` resource group (or the equivalent fine-grained roles: Storage Blob Data Contributor on `netsourceindexprod`, Website Contributor on the `netsourceindexprod` web app, and the same shape on the validation subscription).
- [ ] **Storage Blob Data Reader** (minimum) on the stage1 storage subscription / `netsourceindexstage1` account — needed for incident-time inspection even if the pipeline does it via its own service connection.
- [ ] Access to the **`dotnet-eng` Application Insights** resource (Reader at minimum) and the **`dotnet-eng-grafana`** instance.
- [ ] **Admin** on the [`dotnet/source-indexer`](https://github.com/dotnet/source-indexer) GitHub repo (so the new team can manage branch protection, CODEOWNERS, and webhooks).
- [ ] Listed as a contact on any availability-test alerting routes (PagerDuty rotation, Teams webhook destination, or email DL) so probe failures actually page the new team.
- [ ] Service-connection administrator on `dnceng/internal` for the three connections listed above, so credentials can be rotated and federated identities re-bound when needed.
- [ ] Owner / contributor on the **internal NuGet feed** `9ee6d478-d288-47f7-aacc-f6e6d082ae6d/d1622942-d16f-48e5-bc83-96f4539e7601` so the `UploadIndexStage1` tool can be re-published.
- [ ] Documented owner of the `source.dot.net` DNS record and the TLS certificate that fronts the site.
