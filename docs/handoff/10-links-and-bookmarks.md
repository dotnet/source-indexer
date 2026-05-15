# 10 — Links & bookmarks

A single-page "open these tabs on day one" reference. Everything new owners need to bookmark to operate `source.dot.net`.

Items tagged **`TODO @radical`** are values that aren't discoverable from the repo (subscription IDs, tenant info, group memberships, etc.) — Ankit ([@radical](https://github.com/radical)) please fill in.

---

## Code & docs

| What | Where |
|---|---|
| Main repo | [`dotnet/source-indexer`](https://github.com/dotnet/source-indexer) |
| Vendored renderer (upstream) | [`KirillOsenkov/SourceBrowser`](https://github.com/KirillOsenkov/SourceBrowser) |
| Vendored renderer (dotnet fork, used by `update-source-browser.ps1`) | [`dotnet/SourceBrowser`](https://github.com/dotnet/SourceBrowser) (branch `source-indexer`) |
| Live site | [https://source.dot.net](https://source.dot.net) |
| Staging slot | [https://staging.source.dot.net](https://staging.source.dot.net) |
| Validation slot | **TODO @radical** — confirm the public URL for the `validation` slot on `netsourceindexprod` (likely `https://netsourceindexprod-validation.azurewebsites.net` but verify). |
| Source-selection algorithm doc | [`docs/source-selection-algorithm.md`](../source-selection-algorithm.md) |
| Repo README | [`README.md`](../../README.md) |

## Azure DevOps

| What | Where |
|---|---|
| Pipeline definition (daily prod build) | [`dnceng/internal` pipeline **612**](https://dev.azure.com/dnceng/internal/_build?definitionId=612) |
| Pipeline YAML in repo | [`azure-pipelines.yml`](../../azure-pipelines.yml) |
| CodeQL pipeline YAML in repo | [`azure-pipelines-codeql.yml`](../../azure-pipelines-codeql.yml) |
| CodeQL pipeline definition | **TODO @radical** — definition ID + URL for the CodeQL pipeline in `dnceng/internal`. |
| Build status badge | See top of [`README.md`](../../README.md) |
| Internal NuGet feed (raw GUIDs from YAML) | `9ee6d478-d288-47f7-aacc-f6e6d082ae6d / d1622942-d16f-48e5-bc83-96f4539e7601` — resolves to **`dnceng/public` / `dotnet-tools`** feed. |
| Internal NuGet feed URL | [https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json](https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json) (this is the URL Arcade's `enableSourceIndex` template defaults to as `sourceIndexPackageSource`). |
| Feed UI page | **TODO @radical** — `https://dev.azure.com/dnceng/public/_artifacts/feed/dotnet-tools` (verify the slug matches the feed-name URL fragment). |
| Agent pool (prod) | `NetSourceIndexProd-Pool` — **TODO @radical** add portal link to the pool admin page in `dnceng`. |
| Agent pool (validation) | `NetSourceIndexValid-Pool` — **TODO @radical** add portal link. |
| Service connections (in `dnceng/internal`) | `SourceDotNet Stage1 Publish`, `NetSourceIndex-Prod`, `NetSourceIndex-Validation-Prod` — **TODO @radical** add deep-link to the service-connections admin page in `dnceng/internal`. |

## Azure portal — production resources

All prod resources live in resource group **`source.dot.net`**.

| What | Resource | Portal link |
|---|---|---|
| Subscription (prod) | — | **TODO @radical** — subscription name + ID for the prod resources. |
| Resource group | `source.dot.net` | `https://portal.azure.com/#@<tenant>/resource/subscriptions/<subId>/resourceGroups/source.dot.net/overview` — **TODO @radical** fill in `<tenant>` and `<subId>`. |
| Web app | `netsourceindexprod` | **TODO @radical** — direct portal URL to the App Service. |
| Web app — `production` slot | (default) | **TODO @radical** |
| Web app — `staging` slot | slot `staging` | **TODO @radical** |
| Web app — `validation` slot | slot `validation` | **TODO @radical** |
| Storage account (prod indexes) | `netsourceindexprod` | **TODO @radical** — direct portal URL. |
| Storage account → blob containers (prod) | `netsourceindexprod` → Containers | **TODO @radical** — useful for incident-time inspection of `index-<GUID>` containers and their TTL metadata. |

## Azure portal — validation resources

| What | Resource | Portal link |
|---|---|---|
| Subscription (validation) | — | **TODO @radical** — name + ID. May or may not be the same subscription as prod. |
| Resource group | **TODO @radical** — name of the resource group containing `netsourceindexvalidprod`. |
| Storage account (validation) | `netsourceindexvalidprod` | **TODO @radical** |

## Azure portal — stage1 (upstream-published bundles)

| What | Resource | Portal link |
|---|---|---|
| Subscription (stage1) | — | **TODO @radical** — name + ID. |
| Resource group | **TODO @radical** — name of the resource group containing `netsourceindexstage1`. |
| Storage account (stage1 bundles) | `netsourceindexstage1` | **TODO @radical** — direct portal URL. |
| Stage1 container | `stage1` | **TODO @radical** — direct portal URL to the container blade. |

## Monitoring & on-call

| What | Where |
|---|---|
| Grafana dashboard | [Service Availability dashboard](https://dotnet-eng-grafana.westus2.cloudapp.azure.com/d/arcadeAvailability/service-availability) |
| Grafana root | [https://dotnet-eng-grafana.westus2.cloudapp.azure.com](https://dotnet-eng-grafana.westus2.cloudapp.azure.com) |
| App Insights resource | `dotnet-eng` — **TODO @radical** direct portal URL (subscription + resource group). |
| Availability tests | **TODO @radical** — direct link to the App Insights Availability blade for `dotnet-eng`, plus the names of the source.dot.net probes. |
| Alert routes (PagerDuty / Teams / email DL) | **TODO @radical** — where alerts from these availability tests are routed and how to add a new on-call contact. |
| App Service log stream (prod) | **TODO @radical** — portal deep-link to "Log stream" on `netsourceindexprod`. |

## DNS / TLS

| What | Where |
|---|---|
| DNS zone owner for `dot.net` | **TODO @radical** — which Azure DNS zone / external DNS service hosts `dot.net`, and which team owns it. |
| `source.dot.net` record | **TODO @radical** — record type (CNAME / A), target, and who can edit. |
| `staging.source.dot.net` record | **TODO @radical** |
| TLS certificate | **TODO @radical** — App Service Managed Certificate? Imported from Key Vault? Renewal owner if not auto-managed. |

## External docs we depend on

| What | Where |
|---|---|
| Arcade SDK | [`dotnet/arcade`](https://github.com/dotnet/arcade) |
| Arcade docs root | [`dotnet/arcade/Documentation`](https://github.com/dotnet/arcade/tree/main/Documentation) |
| 1ES Pipeline Templates | [https://eng.ms/docs/cloud-ai-platform/devdiv/one-engineering-system-1es/1es-docs/1es-pipeline-templates](https://eng.ms/docs/cloud-ai-platform/devdiv/one-engineering-system-1es/1es-docs/1es-pipeline-templates) |
| `AzureCLI@2` task reference | [Microsoft Learn — AzureCLI@2](https://learn.microsoft.com/azure/devops/pipelines/tasks/reference/azure-cli-v2) |
| `AzureFileCopy@6` task reference | [Microsoft Learn — AzureFileCopy@6](https://learn.microsoft.com/azure/devops/pipelines/tasks/reference/azure-file-copy-v6) |
| `DefaultAzureCredential` | [Microsoft Learn — DefaultAzureCredential](https://learn.microsoft.com/dotnet/api/azure.identity.defaultazurecredential) |
| App Service deployment slots | [Microsoft Learn — Set up staging environments](https://learn.microsoft.com/azure/app-service/deploy-staging-slots) |

## Cross-reference into the rest of this handoff

| To learn about | Read |
|---|---|
| End-to-end data flow + glossary | [`00-overview.md`](./00-overview.md) |
| Repo layout (what each folder is) | [`01-repo-layout.md`](./01-repo-layout.md) |
| Building locally | [`02-build-and-local-dev.md`](./02-build-and-local-dev.md) |
| The Clone → Prepare → BuildIndex flow | [`03-indexing-pipeline.md`](./03-indexing-pipeline.md) |
| How this repo plugs into Arcade and the rest of dotnet | [`04-arcade-and-dotnet-integration.md`](./04-arcade-and-dotnet-integration.md) |
| Step-by-step pipeline walkthrough | [`05-azure-pipeline.md`](./05-azure-pipeline.md) |
| Azure resource inventory | [`06-azure-infrastructure.md`](./06-azure-infrastructure.md) |
| Deployment + rollback playbook | [`07-deployment-and-rollback.md`](./07-deployment-and-rollback.md) |
| Monitoring + runbook | [`08-monitoring-and-runbook.md`](./08-monitoring-and-runbook.md) |
| Service connections + RBAC checklist | [`09-access-and-permissions.md`](./09-access-and-permissions.md) |
