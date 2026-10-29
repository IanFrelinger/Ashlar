# Phase 13 — Data plane & federation waterproofing

Automates mesh features that Phase 0–12 did not cover in Docker E2E: **knowledge sync**, **federated brick catalog**, **task retry/result**, and **elastic placement**.

## Scripts

| Script | Checks |
|--------|--------|
| [`mesh-lab-verify-knowledge.sh`](../scripts/mesh-lab-verify-knowledge.sh) | Seed import on peer-b → export → import on director → duplicate skip |
| [`mesh-lab-verify-federation.sh`](../scripts/mesh-lab-verify-federation.sh) | `RemoteCatalogBaseUrls` on peer-a lists peer-b bricks; execute via peer-a |
| [`mesh-lab-verify-retry-result.sh`](../scripts/mesh-lab-verify-retry-result.sh) | Retry skips prior peer; `GET …/result` downloads file artifact |
| [`mesh-lab-verify-elastic.sh`](../scripts/mesh-lab-verify-elastic.sh) | Queue-depth placement; heartbeat updates; `GET /api/mesh/elastic/status` |

Invoked from [`mesh-lab-verify.sh`](../scripts/mesh-lab-verify.sh) after network-negative (unless skipped).

## Compose defaults (peer-a director)

- `Ashlar__BrickHost__RemoteCatalogBaseUrls__0=http://peer-b:8080` with API key auth for catalog fetch
- `Ashlar__Mesh__Elastic__*` disabled by default; set `MESH_LAB_ELASTIC_REBALANCER=1` in env to run rebalancer wait in verify-elastic (also set `MESH_LAB_ELASTIC_REBALANCER=true` in compose for `Elastic:Enabled`)

## Skip flags

| Env | Effect |
|-----|--------|
| `MESH_LAB_SKIP_KNOWLEDGE_VERIFY=1` | Skip knowledge round-trip |
| `MESH_LAB_SKIP_FEDERATION_VERIFY=1` | Skip federated catalog/execute |
| `MESH_LAB_SKIP_RETRY_RESULT_VERIFY=1` | Skip retry + result download |
| `MESH_LAB_SKIP_ELASTIC_VERIFY=1` | Skip elastic placement |

## CI

[`.github/workflows/mesh-lab-gate.yml`](../.github/workflows/mesh-lab-gate.yml) runs this lab only when started by hand. Its automatic triggers have been off since 2026-08-11; the comment at the top of the workflow says why.

## Revision history

| Date | Change |
|------|--------|
| 2026-05-19 | Phase 13 scripts + compose BrickHost/Elastic hooks. |
