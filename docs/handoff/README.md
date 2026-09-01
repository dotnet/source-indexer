# source-indexer Tech Handoff

This folder is a tech handoff package for [`dotnet/source-indexer`](https://github.com/dotnet/source-indexer), the repository that builds and deploys [https://source.dot.net](https://source.dot.net).

It is written assuming the new owners are .NET engineers familiar with Azure DevOps, MSBuild, and Arcade, but new to this codebase. Anything that requires specific knowledge that is not discoverable from the repo is marked **TODO (tribal knowledge)** so the outgoing team can fill it in.

## Read in this order

1. [00 — Overview](00-overview.md) — what source.dot.net is and the end-to-end data flow.
2. [01 — Repo layout](01-repo-layout.md) — every top-level folder and key file.
3. [02 — Build & local dev](02-build-and-local-dev.md) — how to build and run locally.
4. [03 — Indexing pipeline](03-indexing-pipeline.md) — the Clone → Prepare → BuildIndex flow, V1 vs V2 repositories, source-selection.
5. [04 — Arcade & dotnet integration](04-arcade-and-dotnet-integration.md) — how this repo plugs into Arcade and the other `dotnet/*` repos.
6. [05 — Azure pipeline](05-azure-pipeline.md) — walk-through of `azure-pipelines.yml`.
7. [06 — Azure infrastructure](06-azure-infrastructure.md) — App Service, storage accounts, slots, app settings.
8. [07 — Deployment & rollback](07-deployment-and-rollback.md) — the deployment PowerShell scripts and recovery paths.
9. [08 — Monitoring & runbook](08-monitoring-and-runbook.md) — observability surfaces, log access, common failure modes.
10. [09 — Access & permissions](09-access-and-permissions.md) — service connections and Azure RBAC needed to operate the service.
11. [10 — Links & bookmarks](10-links-and-bookmarks.md) — single-page "open these tabs on day one" reference.

## Existing in-repo documentation that is still authoritative

- [`README.md`](../../README.md) — top-level overview, build, deployment summary.
- [`docs/source-selection-algorithm.md`](../source-selection-algorithm.md) — the scoring algorithm used when multiple builds of the same assembly are seen.

## Conventions used in this handoff

- Paths are relative to the repository root.
- All Azure DevOps links assume the `dnceng` org and the `internal` project.
- Where a value would otherwise look like a hard-coded secret/identifier (e.g. service connection names), it is reproduced verbatim from `azure-pipelines.yml` so search works.
