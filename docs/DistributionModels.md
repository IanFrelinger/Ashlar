# Distribution models

This page is for **maintainers and integrators**: how Ashlar is shipped, how each channel is **pinned**, and which **automated gates** prove that channel still works.

For day-to-day embedding and extension work, start with **`docs/IntegratorGuide.md`**, **`docs/sdk.md`**, and **`docs/architecture/runtime-vs-application.md`**.

For the authoritative open-vs-commercial boundary, see **`docs/OpenCoreBoundary.md`**.

## Version spine

Ship the **same semantic version** across artifacts that belong together:

- **NuGet:** all `Ashlar.*` packages for a release share one `PackageVersion`; consumers often reference **`Ashlar.Hosting.Bundle`**. See **`docs/PUBLISHING.md`**.
- **Containers:** GHCR images tagged **`X.Y.Z`** (no `v`) from the same **`vX.Y.Z`** git tag. The semver tag is a manifest retag of the smoke-tested immutable **`sha-<12>`** tag, not a rebuild, so it resolves to the same digest and the same platforms (`nexo-cli`: linux/amd64 + linux/arm64 on release tags, like `:latest`; `nexo-api`: linux/amd64). Pin digests for production. See **`docs/RELEASE.md`** and **`docs/DEPLOYMENT.md`**.

Release automation is summarized in **`docs/RELEASE.md`** (NuGet + GHCR from one tag when configured).

## Distribution channels

| Channel | Primary artifacts | How consumers pin | First success (manual) |
|--------|---------------------|-------------------|-------------------------|
| **NuGet host embed** | `Ashlar.Hosting` graph, **`Ashlar.Hosting.Bundle`** | Package version on a feed | Build and run **`docs/samples/StableSdkHostSample/`** (see **`docs/SdkIntegrationGuide.md`**) |
| **NuGet client** | **`Ashlar.Client`** / **`Ashlar.Sdk`** | Package version | **`docs/sdk.md`** (client quick start) |
| **HTTP-only** | Running **`Ashlar.API`** | Base URL + TLS + API key policy | **`curl`** `GET /health`, `GET /api/status` (see **`docs/SelfHostedAgentServer.md`**) |
| **CLI** | **`Ashlar.CLI`** .NET tool (`PackAsTool`; nuget.org pin is **`ci/published-version`**, currently **0.1.2** — or install from a local feed, see **`docs/AuthoringBricks.md`**) or **GHCR `nexo-cli`** image | Image tag or digest; CLI `--version` / package | **`docs/GettingStarted.md`** (`doctor`, `pipeline`) |
| **Compose / operators** | **`deploy/compose/docker-compose*.yml`** + operator docs | Compose file revision + image digests | **`docs/DEPLOYMENT.md`**, stack-specific guides |
| **Source / monorepo** | `ProjectReference` into **`src/`** | Git commit / branch | **`docs/IntegratorGuide.md`** (project reference example) |
| **Mesh / federation (open peers)** | Peer config, local mesh primitives, worker executor | `instances.json`, env vars | **`docs/IntegratorGuide.md`**, **`docs/FriendMeshPrefab.md`**, **`docs/MeshVirtualLab.md`** |
| **Mesh fleet director (commercial)** | `Ashlar.Commercial.Fleet.Host`, `/api/mesh/*` director APIs | Image tag + API key + peer registration key | **`.docker/Dockerfile.fleet-host`**, **`scripts/commercial-fleet-host-smoke.sh`**, mesh-lab peer-a |

**Publication status:** **0.1.2** is on nuget.org (`ci/published-version`).
Repo `VERSION` may already read ahead of that pin for an unpublished cut — do not treat the repo file as the public pin. GHCR `nexo-cli:0.1.2` is the operator image. Until the next tag, NuGet rows that name a newer version are proven against **local folder feeds** (`nuget-local-pack-consumer`, `scripts/verify-standalone-brick-authoring.sh`) plus `ProjectReference` into `src/` (`samples/hello-brick/` is the smallest example).

## Golden reference pins (copy/paste)

Use these as **stable entrypoints** when writing runbooks or samples; replace image digests and package versions with whatever you shipped (`docs/RELEASE.md`).

| Channel | Golden artifact / path |
|--------|-------------------------|
| **NuGet host sample (local pack)** | `docs/samples/StableSdkHostSample/package-consumer/StableSdkHostSample.Package.csproj` + **`scripts/verify-stable-sdk-host-sample-packages.sh`** |
| **NuGet metapackage (consumers)** | **`Ashlar.Hosting.Bundle`** at the release semver (see **`docs/PUBLISHING.md`**) |
| **HTTP / API container** | Build **`.docker/Dockerfile.api`**; container listens on **`8080`** (`ASPNETCORE_URLS=http://+:8080`). Smoke script: **`scripts/ci/distribution-matrix-api-http-smoke.sh`**. |
| **CLI container (public)** | **`ghcr.io/ianfrelinger/nexo-cli:latest`** — smoke with **`--help`** and **`pipeline validate --help`** (not **`doctor`**, see **`docs/GettingStarted.md`**). |
| **Compose (operator lab)** | **`deploy/compose/docker-compose.ephemeral.yml`** (light deps) or **`deploy/compose/docker-compose.portal.yml`** (Director stack) — hub **`docs/DEPLOYMENT.md`**. |
| **Mesh prefab** | **`deploy/compose/docker-compose.friend-mesh.yml`** + **`docs/FriendMeshPrefab.md`** |
| **Mesh lab (heterogeneous)** | **`deploy/compose/docker-compose.mesh-lab.yml`** — peer-a = commercial fleet host, peer-b/worker = open `Ashlar.API` | **`docs/MeshVirtualLab.md`**, **`scripts/mesh-lab-verify.sh`** |

## Contract boundaries

- **Host SDK (stable):** `Ashlar.Hosting.Sdk` — register bricks/agents before **`AddAshlar`**. See **`docs/sdk.md`**.
- **Client SDK (stable):** `Ashlar.Client` / **`IAshlarClient`** — typed HTTP to a running API; use **`InvokeAsync`** for routes not yet wrapped. See **`docs/sdk.md`** and **`docs/api/index.md`**.
- **HTTP:** stable routes and behavior for external clients; breaking changes follow **`docs/api/versioning.md`** (unversioned in `v0.x`, one-minor deprecation window, **Breaking** entries in `CHANGELOG.md`, `/api/v1` at `1.0`; that page also lists the endpoints on the documented surface).
- **CLI:** treat breaking flags as **consumer-facing** changes; document in release notes.

Breaking-change policy for packages: **`docs/SdkCompatibilityPolicy.md`**.

## Automated gates (CI)

The workflow **`.github/workflows/distribution-matrix-gate.yml`** runs **in parallel** on relevant pull requests, on pushes to the default branch (when touched paths match), on **`workflow_dispatch`**, and on a **weekly schedule** so path-filtered PRs do not miss cross-cutting breaks.

| Matrix job | What it proves |
|------------|----------------|
| **nuget-local-pack-consumer** | Local pack → **`scripts/verify-stable-sdk-host-sample-packages.sh`** (isolated NuGet cache, sample restores from folder feed + nuget.org). |
| **standalone-brick-authoring** | Tool-installed **`ashlar new brick`** outside the repo; restore/build/test from local feed only. |
| **external-product-shape** | **`scripts/verify-external-product-shape.sh`** — renders **`consumer-template/host/`** into an ephemeral consumer that restores **`Ashlar.Authoring`**, **`Ashlar.Hosting.Bundle`**, **`Ashlar.Sdk`**, boots a thin host with an authored brick, and round-trips via **`IAshlarClient.InvokeAsync`** (`POST /api/bricks/{id}/execute`). |
| **cli-image-smoke** | **`.docker/Dockerfile.cli`** builds; container runs **`--help`** and **`pipeline validate --help`** (runtime image has no git/curl, so **`doctor`** is not used here). |
| **api-image-http-smoke** | **`.docker/Dockerfile.api`** builds; container serves **`/health`** and **`/api/status`** (host **`curl`** — HTTP-only consumer path). Script: **`scripts/ci/distribution-matrix-api-http-smoke.sh`**. |
| **ashlar-client-inprocess-test** | **`Ashlar.Client`** `GetStatusAsync` against in-process **`Ashlar.API`** (same pipeline as production; **`net10.0`** test filter). |
| **pack-hosting-graph-alignment** | **`scripts/verify-pack-ashlar-hosting-graph-alignment.py`** — pack allowlist matches **`Ashlar.Hosting`** MSBuild graph. |

**Post-publish NuGet** verification (nuget.org index + retries) stays in **`docs/PUBLISHING.md`**, **`docs/NuGetConsumerVerify.md`**, and the reusable workflow **`reusable-verify-nuget-consumer.yml`** (invoked from release flows).

Other related gates (not duplicated here): **`compose-gate.yml`**, **`devcontainer-gate.yml`**, **`container-image-gate.yml`**, **`pack-hosting-graph-alignment.yml`**, mesh prefab / mesh lab workflows under **`.github/workflows/`**.

## Integrator compatibility matrix

Release owners should keep the **version / runtime table** in **`docs/IntegratorGuide.md`** up to date when you cut a release (single source of truth for “which Ashlar version on which .NET”).

## See also

- **`docs/DocsIndex.md`** — documentation hub.
- **`docs/RELEASE_RUNBOOK.md`** — ship decisions and checks after tag.
