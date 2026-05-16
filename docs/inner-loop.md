# Local inner loop (Aspire)

`src/source-indexer.AppHost/` is an [Aspire](https://aspire.dev) AppHost that
emulates the production indexing pipeline end-to-end against a tiny sample
library (`samples/MiniRuntime/`). It lets you exercise the full
binlog → HtmlGenerator → blob → SourceIndexServer chain locally without
pushing to ADO.

## Prerequisites

- **.NET SDK 10** (pinned in root `global.json`).
- **Docker / Podman** running locally (Azurite emulators run as containers).
- **Aspire CLI** — see <https://aspire.dev/docs/getting-started>.
- **Azure CLI** (`az`) on PATH — used by the `publish-index` step.

Then from the repo root:

```pwsh
aspire start
```

The dashboard URL (with login token) is printed to the console.

## Resource graph

Auto-started (running as soon as the AppHost is up):

| Resource | Type | Purpose |
|---|---|---|
| `stage1Storage` | Azurite emulator | Models `netsourceindexstage1` (V2 upstream upload destination). |
| `prodStorage`   | Azurite emulator | Models `netsourceindexprod` (final HTML index destination). |
| `stage1`        | Blob container under `stage1Storage` | Where `upload-stage1` drops the `.tar.gz`. |
| `index-local`   | Blob container under `prodStorage`   | Where the generated HTML index is uploaded. The web app reads from here. |
| `web`           | `SourceIndexServer` project          | ASP.NET Core app. `SOURCE_BROWSER_INDEX_PROXY_URL` is wired to `index-local` so it serves the indexed HTML out of Azurite. |

Explicit-start (stopped by default — click **Start** in the dashboard):

| Resource | Type | What it does |
|---|---|---|
| `sample-build`    | Executable: `dotnet build /bl:` | Builds `samples/MiniRuntime` and produces `samples/MiniRuntime/bin/sample/msbuild.binlog`. |
| `upload-stage1`   | Project: `UploadIndexStage1`    | Tars+gzips the sample folder + binlog and uploads it to `stage1`. Real V2 upstream tool — fully dogfooded. |
| `htmlgenerator`   | Executable: `dotnet run HtmlGenerator` | Runs `HtmlGenerator` on the binlog from `sample-build` to produce static HTML under `bin/index/`. |
| `publish-index`   | Executable: `az storage blob upload-batch` | Uploads `bin/index/index/` to the `index-local` container in `prodStorage`. |

## One-click bootstrap

The `prodStorage` resource exposes a custom **bootstrap-all** command that
runs the four pipeline resources in order (`sample-build` →
`upload-stage1` → `htmlgenerator` → `publish-index`) and waits for each one
to finish. This is the easy first-run path:

1. `aspire start`
2. Open the dashboard.
3. On the `prodStorage` resource, click the **bootstrap-all** command.
4. Wait for it to finish (watch the logs).
5. Open the `web` URL — you should see `MiniRuntime`'s indexed HTML.

Re-running any individual resource regenerates just that stage.

## How this maps to prod

| Prod | Local inner loop |
|---|---|
| V2 upstream repo publishes `.tar.gz` to `netsourceindexstage1/stage1/<repo>/<ts>.tar.gz` (`UploadIndexStage1`) | `upload-stage1` resource → `stage1Storage/stage1` |
| `HtmlGenerator.exe` reads binlog + writes HTML to `bin/index/` (`src/index/index.proj`) | `htmlgenerator` resource (`dotnet run --project HtmlGenerator`) |
| `AzureFileCopy@6` uploads `bin/index/index/*` to `netsourceindexprod/index-<GUID>/` (`azure-pipelines.yml`) | `publish-index` resource (`az storage blob upload-batch`) → `prodStorage/index-local` |
| App Service slot setting `SOURCE_BROWSER_INDEX_PROXY_URL` flipped to the new container (`deployment/deploy-storage-proxy.ps1`) | `web` resource started with `SOURCE_BROWSER_INDEX_PROXY_URL` env var pointing at `prodStorage/index-local` |

See `docs/handoff/03-indexing-pipeline.md` and
`docs/handoff/05-azure-pipeline.md` for the full prod mechanics.

## Auth: Azurite vs prod

Production uses managed identity (`TokenCredential`) to talk to Azure
Storage. Azurite only speaks shared-key / connection-string auth. To keep
both paths working without a fork, `SourceIndexServer/Models/AzureBlobFileSystem.cs`
and `UploadIndexStage1/Program.cs` check for `AZURE_STORAGE_CONNECTION_STRING`
in the environment:

- If set → use `BlobServiceClient(connectionString)` (Azurite-friendly).
- If not set → use the original `BlobServiceClient(uri, TokenCredential)`
  code path (prod-friendly).

Aspire injects the connection string automatically via `WithReference(...)`
on the blob resources in the AppHost. Prod is unaffected.

## Persistence

The two Azurite emulators use `ContainerLifetime.Persistent` and named
Docker volumes (`source-indexer-stage1-data`, `source-indexer-prod-data`).
Blobs uploaded during a session survive `aspire start` restarts. To wipe
state, remove the volumes via Docker / Podman.

## Open follow-ups

- **ServiceDefaults / dashboard telemetry**: `SourceIndexServer` still uses
  the legacy `IHostBuilder` + `Startup<T>` bootstrap. Wiring
  `AddServiceDefaults()` / `MapDefaultEndpoints()` requires migrating to
  the minimal-hosting model first. Until then, the `web` resource won't
  contribute traces/metrics to the Aspire dashboard, but everything else
  works.
- **Debuggable HtmlGenerator**: it's invoked via `dotnet run --project ...`
  rather than a typed `AddProject<T>` reference because `HtmlGenerator`
  targets `net472` and can't be `ProjectReference`'d from the `net10`
  AppHost. Attach manually if you need to debug it.
