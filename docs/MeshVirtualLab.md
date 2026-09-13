# Virtual mesh lab (heterogeneous images + auth)

**Status:** landed. The heterogeneous lab composes the commercial fleet director with open mesh/API worker primitives. See [`OpenCoreBoundary.md`](OpenCoreBoundary.md) for the authoritative open/commercial split.

The lab runs **heterogeneous Ashlar hosts** on one Docker bridge so you can test **different images and security configurations** together without extra hardware. **peer-a** is the commercial fleet director (`Fleet.Host`); **peer-b** and **worker** use open `Ashlar.API`.

## What runs by default

| Role | Dockerfile (override) | Runtime / env highlights | Auth (override) |
|------|-------------------------|---------------------------|-----------------|
| **peer-a** | `MESH_LAB_PEER_A_DOCKERFILE` → **`.docker/Dockerfile.fleet-host`** | `ASPNETCORE_ENVIRONMENT` = Production (default); LiteDB director persistence | **ApiKey** (`Ashlar__Security__ApiKey`) |
| **peer-b** | `MESH_LAB_PEER_B_DOCKERFILE` → **`.docker/Dockerfile.quickstart`** | **`ASHLAR_ALLOW_MOCK=1`**, Staging | **ApiKeyOrBearerToken** — same **`Ashlar__Security__ApiKey`** *or* **`Ashlar__Security__PeerB__BearerToken`** |
| **worker** (profile **`workers`**) | `MESH_LAB_WORKER_DOCKERFILE` → **`.docker/Dockerfile.api`** | Development by default (`MESH_LAB_WORKER_ASPNETCORE_ENVIRONMENT`), `ShowAdvisoryInPortal` off by default | **ApiKeyOrBasic** — API key *or* Basic (**`ashlar`** + **`Ashlar__Security__Worker__BasicAuthPassword`**) |

**Optional heavier worker image:** set `MESH_LAB_WORKER_DOCKERFILE=.docker/Dockerfile.agent-server` (SDK-based final image; slower CI/build, richer for local soak tests).

All **`Ashlar__Security__*`** keys map to the same binding as production (`Ashlar:Security`); see **`docs/Configuration.md`**.

## Prerequisites

- Docker Engine + Compose v2
- **`python3`** on the host (used by **`scripts/mesh-lab-verify.sh`** for mesh task JSON assertions)
- RAM for **two** full image builds (`api` + `quickstart` differ in final stage); workers reuse the **`api`** image by default

## Start

**Automated one-shot (temp secrets, tear down after verify):**

```bash
make mesh-lab-e2e
# same as: bash scripts/run-mesh-lab-e2e.sh
```

**Include the worker tier** (Compose profile `workers`; exercises Basic + API key against worker):

```bash
make mesh-lab-e2e-workers
# or: MESH_LAB_E2E_WORKERS=1 bash scripts/run-mesh-lab-e2e.sh
# or: bash scripts/run-mesh-lab-e2e.sh --workers
```

Requires Docker running (`docker info`). Uses project name **`ashlar_mesh_lab_local`** by default.

On **Apple Silicon**, builds default to **`linux/amd64`** (same as CI) because `grpc.tools`/`protoc` can crash on `linux_arm64` inside Docker. Override with `DOCKER_DEFAULT_PLATFORM=linux/arm64` only if your setup handles it.

**Persistent lab with your `.env.mesh-lab` file:**

```bash
cp docs/config/mesh-lab.env.example .env.mesh-lab
# Set Ashlar__Security__ApiKey, Ashlar__Security__PeerB__BearerToken, Ashlar__Security__Worker__BasicAuthPassword, MESH_LAB_PEER_REGISTRATION_KEY

make mesh-lab-up
make mesh-lab-verify
# …when finished…
make mesh-lab-down
```

With workers:

```bash
MESH_LAB_WORKERS=1 make mesh-lab-up
make mesh-lab-verify
make mesh-lab-down
```

Equivalent raw Compose:

```bash
docker compose -f deploy/compose/docker-compose.mesh-lab.yml --env-file .env.mesh-lab up -d --build
./scripts/mesh-lab-verify.sh .env.mesh-lab
```

Host URLs: **`http://127.0.0.1:18081`** (peer-a), **`http://127.0.0.1:18082`** (peer-b). With the **`workers`** profile, the worker also publishes **`http://127.0.0.1:18083`** by default (`MESH_LAB_WORKER_PUBLISH`).

## What `mesh-lab-verify.sh` checks

Requires **`python3`** on the host (parses mesh task JSON).

- **Host ↔ published ports**: `GET /health` on peer-a and peer-b (API key and/or Bearer where configured).
- **In-network HTTP**: an ephemeral `curl` container on the **`mesh_lab`** bridge resolves **`peer-a`** / **`peer-b`** and calls `http://peer-a:8080/health` and `http://peer-b:8080/health`.
- **Mesh control plane (JSON)**:
  - `GET /api/mesh/fleet/nodes` from the host and from inside the bridge (empty `[]` initially).
  - `GET /api/mesh/elastic/status` on peer-b (Bearer from host; unauthenticated GET from inside the bridge when mesh token is unset).
  - `POST /api/mesh/fleet/nodes` on peer-a with `X-Ashlar-Api-Key`, registering **`http://peer-b:8080`** as **`mesh-lab-verify-peer`**, then verifies the fleet list includes that peer id.
  - **Mesh task placement** (same API key path): `POST /api/mesh/tasks` → `POST /api/mesh/tasks/{taskId}/schedule` → **`Assigned`** with **`assignedPeerId`** / **`assignedApiBaseUrl`**.
  - **Mesh task lifecycle** on the director (peer-a): wrong **`leaseToken`** → **409**; **`PATCH`** **`Running`** then **`Succeeded`** with valid lease; **`GET`** confirms terminal state and cleared lease.
  - **Brick HTTP** (optional): from the bridge, **`GET /api/bricks`** and **`POST /api/bricks/{id}/execute`** on **peer-b** (first catalog entry, or **`MESH_LAB_VERIFY_BRICK_ID`**); skipped if the catalog is empty.
- **Worker tier** when the Compose **`workers`** profile is running: **`GET /health`**, mesh reads with **API key** and **Basic** auth (via published **`:18083`** when mapped).

**Deep checks** ([`scripts/mesh-lab-verify-deep.sh`](../scripts/mesh-lab-verify-deep.sh)): multi-step task, **`lease/extend`**, **`migrate-for-checkpoint`**, reschedule, and terminal **`Succeeded`**. CI runs this after the standard verify script. Locally:

```bash
make mesh-lab-verify-deep
# or one-shot E2E with deep:
MESH_LAB_VERIFY_DEEP=1 make mesh-lab-e2e-workers
```

That covers **Docker DNS, bridge connectivity, security headers, director placement, lease-gated lifecycle, federated brick HTTP, and (with the `workers` profile) an autonomous worker executor** on the worker container (`Ashlar:MeshLab:WorkerExecutor:Enabled`). Tasks named with prefix **`mesh-lab-worker-exec`** are scheduled on the director and completed by the worker (Running → optional brick on assigned peer → Succeeded) without manual PATCH. Set **`MESH_LAB_SKIP_WORKER_EXECUTOR=1`** to skip that check.

**Entitlements (worker tier):** [`scripts/mesh-lab-verify-entitlements.sh`](../scripts/mesh-lab-verify-entitlements.sh) (invoked from standard verify when workers are up) checks **CopilotScoped** API key → **403** on `POST /api/mesh/tasks`, full key → **200**, and **`Ashlar:Entitlements:MaxCopilotSubmissionsPerHour`** → **429** after the configured limit. Configure **`Ashlar__Security__CopilotScopedApiKey`** in `.env.mesh-lab`.

**Trust-tier placement (peer-a director):** default **`Ashlar__Mesh__Placement__PeerTrustPolicy=trusted-only`**. [`scripts/mesh-lab-verify-trust.sh`](../scripts/mesh-lab-verify-trust.sh) registers trusted vs untrusted fleet nodes and asserts placement picks trusted peers only. Set **`MESH_LAB_SKIP_TRUST_VERIFY=1`** to skip.

**Fleet governance (peer-a director):** default **`Ashlar__Mesh__Fleet__RequirePeerRegistrationKey=true`**. Each fleet register must include **`peerRegistrationKey`** in the JSON body (distinct from the operator **`Ashlar__Security__ApiKey`**). Set **`MESH_LAB_PEER_REGISTRATION_KEY`** in `.env.mesh-lab`. [`scripts/mesh-lab-verify-governance.sh`](../scripts/mesh-lab-verify-governance.sh) (invoked from standard verify) checks registration policy, credential rotation (fingerprint change), **`POST /api/mesh/fleet/nodes/{peerId}/revoke`** → placement blocked, and **`/admit`** → placement restored. Set **`MESH_LAB_SKIP_GOV_VERIFY=1`** to skip.

**Director CLI (Product 5.3):** [`scripts/mesh-lab-verify-director-cli.sh`](../scripts/mesh-lab-verify-director-cli.sh) exercises **commercial mesh director CLI `register|revoke|admit`** against the running lab (requires .NET SDK on the host). Set **`MESH_LAB_SKIP_DIRECTOR_CLI_VERIFY=1`** to skip. Ops: [`docs/runbooks/mesh-lab-operations.md`](runbooks/mesh-lab-operations.md) (split-brain, upgrade order, director vs `instances.json`).

**Director persistence (Phase 9):** peer-a uses **LiteDB** (`Ashlar__Mesh__Persistence__Provider=LiteDb`, volume `mesh_lab_peer_a_data`). [`scripts/mesh-lab-verify-persistence.sh`](../scripts/mesh-lab-verify-persistence.sh) restarts peer-a and asserts fleet + tasks survive. Set **`MESH_LAB_SKIP_PERSISTENCE_VERIFY=1`** to skip. See [`MeshPhase9DirectorPersistence.md`](MeshPhase9DirectorPersistence.md).

**Network negative (Phase 11):** [`scripts/mesh-lab-verify-network-negative.sh`](../scripts/mesh-lab-verify-network-negative.sh) exercises blackhole/DNS worker URLs, drained-only placement, **peer-b** stop/start partition, and director restart + lease when LiteDB is on. Set **`MESH_LAB_SKIP_NETWORK_NEGATIVE_VERIFY=1`** to skip. See [`MeshPhase11NetworkNegative.md`](MeshPhase11NetworkNegative.md).

**Data plane & federation (Phase 13):** [`mesh-lab-verify-knowledge.sh`](../scripts/mesh-lab-verify-knowledge.sh), [`mesh-lab-verify-federation.sh`](../scripts/mesh-lab-verify-federation.sh), [`mesh-lab-verify-retry-result.sh`](../scripts/mesh-lab-verify-retry-result.sh), [`mesh-lab-verify-elastic.sh`](../scripts/mesh-lab-verify-elastic.sh) — knowledge export/import, federated brick catalog on peer-a, task retry + result download, queue-depth placement + heartbeat. Skip with **`MESH_LAB_SKIP_*_VERIFY=1`** (see [`MeshPhase13DataPlaneFederation.md`](MeshPhase13DataPlaneFederation.md)).

**Ops / commercial:** [`docs/runbooks/mesh-lab-operations.md`](runbooks/mesh-lab-operations.md), [`docs/commercial/mesh-add-on-sku.md`](commercial/mesh-add-on-sku.md).

## Cloud VM (one-shot bootstrap)

This Cursor/workspace cannot provision cloud VMs or run Docker for you. On **any fresh Ubuntu/Debian VM** (AWS/GCP/Azure/Linode):

1. **SSH** into the VM and install Git if needed.
2. **Clone** this repository and `cd` to the repo root.
3. Run:

   ```bash
   chmod +x scripts/bootstrap-cloud-mesh-lab.sh
   ./scripts/bootstrap-cloud-mesh-lab.sh --install-docker --workers --deep
   ```

   `--install-docker` uses **`apt-get`** to install **`docker.io`** and **`docker-compose-v2`**, then creates **`.env.mesh-lab`** from **`docs/config/mesh-lab.env.example`** (random lab secrets), **`docker compose up --build`**, **`mesh-lab-verify.sh`**, and (with **`--deep`**) **`mesh-lab-verify-deep.sh`**. Use **`--workers`** for the autonomous worker executor checks.

4. From your laptop, **tunnel** the peer ports if the VM has no public listener:

   ```bash
   ssh -L 18081:127.0.0.1:18081 -L 18082:127.0.0.1:18082 user@your-vm
   ```

   Then open **`http://127.0.0.1:18081`** / **`18082`** locally.

If Docker is already installed, omit **`--install-docker`**. For non-apt Linux, install Docker + Compose v2 manually ([Docker Engine install](https://docs.docker.com/engine/install/)), then run **`./scripts/bootstrap-cloud-mesh-lab.sh`** without **`--install-docker`**.

## Workers + stress ramp

```bash
docker compose --profile workers -f deploy/compose/docker-compose.mesh-lab.yml --env-file .env.mesh-lab up -d --scale worker=2 worker
./scripts/mesh-lab-stress-ramp.sh .env.mesh-lab 8 2 30 4
# one-shot (verify + deep + ramp):
make mesh-lab-e2e-stress
# persistent lab already up:
make mesh-lab-stress
```

**CI:** [`.github/workflows/mesh-lab-stress-gate.yml`](../.github/workflows/mesh-lab-stress-gate.yml) runs on `workflow_dispatch` (the Monday 06:00 UTC schedule was removed 2026-08-16 after eight consecutive red runs; see the file header) — full verify (same sub-checks as PR gate), deep, stress ramp (`4` workers, `15` requests/step), then [`mesh-lab-verify-post-stress.sh`](../scripts/mesh-lab-verify-post-stress.sh). The base gate [`mesh-lab-gate.yml`](../.github/workflows/mesh-lab-gate.yml) is **dormant** (manual `workflow_dispatch` only since 2026-08-11; its automatic triggers are preserved commented-out in the file for re-enabling). See [`MeshPhase10LabStressHardening.md`](MeshPhase10LabStressHardening.md).

## Try the mesh CLI

```bash
export ASHLAR_MESH_DIRECTOR_BASE_URL=http://127.0.0.1:18081
export ASHLAR_MESH_API_KEY='your-key'
dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director get /health --json

export ASHLAR_MESH_DIRECTOR_BASE_URL=http://127.0.0.1:18082
# peer-b accepts Bearer OR same API key:
dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director get /health --json
```

## instances.json (optional)

Use host URLs from above; list local peers with `ashlar mesh peers` or fleet nodes with `director list-nodes`.

## Stop

```bash
docker compose --profile workers -f deploy/compose/docker-compose.mesh-lab.yml --env-file .env.mesh-lab down -v
```

## Remote networking (TLS, gRPC, Tailscale)

Pre-production gaps that Docker bridge alone cannot cover are automated in **Phase 12** — see [`MeshPhase12RemoteNetworkingAutomation.md`](MeshPhase12RemoteNetworkingAutomation.md).

| Check | Local | CI |
|-------|-------|-----|
| HTTPS director (Caddy → peer-a) | `make mesh-lab-e2e-tls` | `mesh-lab-tls-gate.yml` (**dormant — manual dispatch only since 2026-09-13**) |
| gRPC transport (Kestrel round-trip) | `dotnet test … --filter Category=ProdStyle` | `grpc-transport-gate.yml` |
| Two-host / tailnet | `scripts/mesh-lab-verify-remote.sh` + env | none — the `mesh-lab-remote-gate.yml` wrapper was deleted 2026-08-16 (never dispatched); run the script from a host on the tailnet |

## CI

> **All three mesh lanes are now manual-only.** `mesh-lab-tls-gate.yml` lost its weekly Tuesday
> schedule on 2026-09-13 after four consecutive red scheduled runs (2026-08-18, -25, 09-01, 09-08;
> ten of the last fourteen red, last green 2026-08-11). It was the one lane the 2026-08 demotion
> policy was never applied to, and it had been failing quietly ever since. `workflow_dispatch`
> stays live on all three. **The failing logs have not been read** — the shared-compose-environment
> story is inherited from the siblings and is a hypothesis, not a diagnosis.

**`.github/workflows/mesh-lab-gate.yml`** (dormant — manual dispatch only since 2026-08-11) writes lab secrets, brings up peers **and** the **`workers`** profile, then runs **`mesh-lab-verify.sh`** and **`mesh-lab-verify-deep.sh`**.

**Optional dotnet mirror:** set **`ASHLAR_RUN_MESH_LAB=1`** and run **`make test-mesh-lab`** (or `dotnet test … --filter Category=MeshLab`). **`MeshLabDockerE2ETests`** starts the same Compose stack and invokes the same verify scripts; skipped when the env var is unset.

## Revision history

| Date | Change |
|------|--------|
| 2026-04-23 | Initial virtual mesh lab. |
| 2026-04-24 | Scalable workers + stress ramp. |
| 2026-05-17 | Mesh verify: task create→schedule placement; worker profile in CI; stronger worker auth checks. |
| 2026-05-18 | Lease lifecycle + brick execute in verify; mesh-lab-verify-deep (checkpoint/migrate/reschedule). |
| 2026-05-18 | Optional `MeshLabDockerE2ETests` (`ASHLAR_RUN_MESH_LAB=1`, `make test-mesh-lab`). |
| 2026-05-18 | Fix GrpcAgentTransport DI (ambient barrier context); worker tier defaults to Development again. |
| 2026-05-19 | Lab worker executor (`Ashlar:MeshLab:WorkerExecutor`) on worker container; verify script autonomous task path. |
| 2026-05-19 | Stress gate workflow; bootstrap `--deep`; `make mesh-lab-e2e-stress` / `make mesh-lab-stress`. |
| 2026-05-19 | Entitlements verify (CopilotScoped + hourly copilot quota on worker). |
| 2026-05-19 | Fleet governance (registration key, admit/revoke); director CLI verify; expanded ops runbook. |
| 2026-05-19 | LiteDB director persistence on peer-a; mesh-lab-verify-persistence (restart survival). |
| 2026-05-19 | Stress gate parity + post-stress placement/persistence (`MeshPhase10LabStressHardening.md`). |
| 2026-05-19 | Network-negative verify (unreachable workers, peer-b outage, director restart lease). |
| 2026-05-19 | Trust-tier placement on director; stress burst pass/fail; mesh lab runbook + SKU sketch. |
| 2026-05-19 | Phase 12: TLS E2E, gRPC gate, remote verify script + automation doc. |
| 2026-05-19 | Phase 13: knowledge, federation, retry/result, elastic verify scripts. |
