# Deployment paths (operator guide)

This document is the **default “what do I run in production?”** map. Other compose files exist for labs, mesh, and CI; this page names the **golden paths** and how to **pin versions** so `latest` does not drift under you.

## Golden path A — Portal + API + Ollama (single host)

**Use when:** you want the web portal, HTTP API on port 8080, and local Ollama on the same machine.

- **Compose file:** `deploy/compose/docker-compose.portal.yml`
- **Build context:** repo root; API image builds from `.docker/Dockerfile.api`
- **Bind:** API and Ollama default to **loopback** (`127.0.0.1`) — good for laptop + Tailscale / reverse proxy patterns (see `docs/TailscaleAndAshlar.md`)
- **First boot:** the bundled Ollama ships with no models — `docker compose -f deploy/compose/docker-compose.portal.yml exec ollama ollama pull llama3.1:latest` (or the tag you set in `OLLAMA_MODEL`). The stack points every Ollama key family at the `ollama` service; see `docs/Configuration.md`, "Ollama".

**Pin a version (recommended):**

1. Build and tag images yourself in CI or locally, **or** use GHCR images once you publish them with **semver or digest** tags (see below).
2. In `deploy/compose/docker-compose.portal.yml`, replace `build:` with `image: ghcr.io/<owner>/nexo-api:<tag>` (and pin `ollama/ollama` to a digest if you need full reproducibility).
3. Never rely on **`latest`** for production unless you accept silent upgrades.

## Golden path B — CLI only (agents, CI, minimal host)

**Use when:** you need `ashlar` in a container with no portal stack.

- **Image:** `ghcr.io/<owner>/nexo-cli` (built by `.github/workflows/container-image-publish.yml` from `.docker/Dockerfile.cli`)
- **Pin:** use tag **`sha-<12-char-commit>`** (always pushed) or a **semver tag** `v1.2.3` / `1.2.3` when you cut a Git **annotated tag** `v1.2.3` on `master`/`main` (workflow publishes those tags in addition to `latest`).
- **Example:** `docker pull ghcr.io/ianfrelinger/nexo-cli:sha-abc123def456`

## Golden path C — Agent server (mounted workspace)

**Use when:** background agents with a host-mounted repo (see `docs/SelfHostedAgentServer.md`).

- **Compose file:** `deploy/compose/docker-compose.agent-server.yml`
- **Pin:** same rules as A — prefer **immutable image references** for `nexo-api` (or your wrapper image).

## Other compose files (not default production)

| File | Role |
|------|------|
| `deploy/compose/docker-compose.agent-server.yml` | Agent server + workspace |
| `deploy/compose/docker-compose.mesh-lab.yml` | Multi-node lab |
| `deploy/compose/docker-compose.friend-mesh.yml` | Friend mesh prefab |
| `deploy/compose/docker-compose.ollama.yml` | Ollama sidecar only |
| `deploy/compose/docker-compose.test.yml` | Test harness |
| `deploy/compose/docker-compose.ephemeral.yml` | Disposable Ollama / Postgres + `ashlar` CLI (`.docker/Dockerfile.cli`) for one-off `run --rm ashlar ...` |
| `deploy/k8s/ashlar-mesh-worker-deployment.yaml` | Kubernetes mesh-worker Deployment sample (`docs/WorkloadScaling.md`) |

## Container health, readiness and hardening

The API images (`.docker/Dockerfile.api`, `Dockerfile.quickstart`, `Dockerfile.fleet-host`) share one shape:

- **`HEALTHCHECK`** probes `GET /health` with bash's `/dev/tcp` (the `mcr.microsoft.com/dotnet/aspnet` runtime images ship no `curl`), so `docker ps` shows `(healthy)` and `docker compose up --wait` returns as soon as the API answers. `scripts/prod-dry-run.sh` no longer hides `--wait` failures.
- **`GET /health`** is liveness (constant 200 while the process serves HTTP). **`GET /ready`** is readiness: 200 once the host has finished starting (DI built, hosted services started) and 503 while starting or once shutdown begins (`IHostApplicationLifetime`), so orchestrators drain traffic before Kestrel closes. Both are unauthenticated and outside `/api`. Use `/ready` for Kubernetes `readinessProbe` / load-balancer checks and `/health` for `livenessProbe`; the k8s sample wires both.
- **Non-root:** the runtime stages run as the aspnet image's unprivileged `app` user (`USER $APP_UID`, uid 1654 in .NET 8+). `/app` is root-owned; the process writes under `/data` (owned by `app`), plus two writable, non-persistent scratch dirs the runtime creates on demand - `/app/.ashlar` (cycle telemetry, taxonomy) and `/app/config` (trust-pack registry) - which the images pre-create and chown. Named volumes created by **older, root-running images** keep root ownership — fix once with `docker run --rm -v <volume>:/v alpine chown -R 1654:1654 /v` (e.g. `ashlar-dailies`) or recreate the volume.
- **Swagger** (`/swagger`, `/swagger/v1/swagger.json`) is on in the `Development` environment or when `Ashlar__Api__EnableSwagger=true`, and otherwise off; the images set no `ASPNETCORE_ENVIRONMENT`, so unless a deployment sets one they run as `Production` with it off. `Dockerfile.fleet-host` honours the same key (it used to serve Swagger in every environment). One exception: `ASPNETCORE_ENVIRONMENT=Testing` also turns it on in all three images, because `application/src/Ashlar.API/appsettings.Testing.json` sets the key and every image ships that file (the fleet-host image through its ProjectReference to Ashlar.API). Do not run an image in `Testing`.
- **Kubernetes:** `ASHLAR_DEPLOYMENT_PROFILE` must be one of `full`, `server`, `edge`, `air-gapped`, `secure-workstation` (alias `workstation`), `system`. Underscores and collapsed forms also parse (`air_gapped`, `secure_workstation`). `AddAshlar` refuses anything else at startup. `air-gapped` is **not** the IDE workstation profile — use `secure-workstation`, with `ASHLAR_TRUST_ENABLED=1` (the profile registers trust services but does not enable them). The mesh-worker sample uses `server`.

## Runtime state (`ASHLAR_STATE_DIR`)

LiteDB stores and snapshots (`ashlar-patterns.db`, `ashlar-adaptation.db`, `ashlar-adaptation-audit.db`, `ashlar-copilot-tasks.db`, `ashlar-execution.db`, `ashlar-test-failures.db`, `ashlar-snapshots/`) default to **`<repo or app root>/.ashlar/state/`** (gitignored) unless `Ashlar:PatternStorePath` / `--store-path` names an explicit location. Set **`ASHLAR_STATE_DIR`** (absolute, or relative to that root) to move the whole directory. The images set `ASHLAR_STATE_DIR=/data/state`, and the portal and agent-server stacks mount the **`ashlar-state`** named volume there, so state survives `docker compose up --force-recreate` and never lands in a bind-mounted repo. Existing installs that already have `ashlar-*.db` at the repo root keep using them until you move the files into `.ashlar/state/` (see `docs/Configuration.md`, "Runtime state").

## NuGet packages (embed or tool repos)

- **Hosting (full graph):** `Ashlar.Hosting.Bundle` at one version — see `docs/PUBLISHING.md`
- **HTTP client:** `Ashlar.Sdk` / `Ashlar.Client` — pin the **same release** as your server image or API deployment when possible

## One-button release (recommended)

1. **Tag** `vX.Y.Z` on the commit you want to ship and **push the tag**.
2. GitHub runs **`.github/workflows/release.yml`**: **GHCR** `nexo-cli` + `nexo-api` (sha + semver tags) and **NuGet** pack/push (per `NUGET_PUBLISH_MODE`).
3. Open the workflow run **Summary** for copy-paste **pin lines** (sha + semver + NuGet version), NuGet **manifest** artifact, and optional **GHCR re-pull smoke** result.

**Which workflow?** **`docs/RELEASE.md`** (hub) → **`docs/RELEASE_RUNBOOK.md`** (checklist + decision table).

**NuGet-only** (e.g. hotfix packages without retagging images): **Actions → Release NuGet packages** (`release-nuget.yml`).

**Images on `master` without a release:** still driven by **Container Image Publish** on path-filtered pushes.

## Production-shaped dry run on Linux (containers)

For an operations-level dry run—**same Compose topology and images** as the golden paths above—see **`docs/prod-dry-run.md`** and run **`make prod-dry-run`** or **`./scripts/prod-dry-run.sh`**.

## Observability

Out of the box the API container writes **human-readable console lines** (read them with `docker compose logs -f ashlar-api`) and keeps metrics **in-process only** — nothing is exported. Both upgrades are opt-in through the host configuration (see `docs/Configuration.md` § Observability):

| Want | Set on the `ashlar-api` service (compose `environment:` or an override file) |
|------|-------------------------------------------------------------------------------|
| One JSON object per log line (for Loki / CloudWatch / Datadog agents) | `ASHLAR_LOG_JSON: "1"` — same flag works for `ashlar background-agent daemon` |
| Traces + metrics to an OpenTelemetry Collector | `OTEL_EXPORTER_OTLP_ENDPOINT: http://otel-collector:4317` (add `OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_PROTOCOL: http/protobuf`, `OTEL_EXPORTER_OTLP_HEADERS` as your backend needs) |

Example override next to the portal stack:

```yaml
# docker-compose.observability.override.yml
services:
  ashlar-api:
    environment:
      ASHLAR_LOG_JSON: "1"
      OTEL_EXPORTER_OTLP_ENDPOINT: http://otel-collector:4317
      OTEL_SERVICE_NAME: nexo-api
```

`docker compose -f deploy/compose/docker-compose.portal.yml -f docker-compose.observability.override.yml up -d`

What is exported when the endpoint is set: ASP.NET Core request spans and HttpClient client spans (traces); ASP.NET Core / HttpClient metrics plus the `Ashlar` meter, whose two instruments `ashlar.operation.duration` and `ashlar.operation.count` carry the `ncr.*` / `ashlar.*` operation names as attributes (see `docs/NcrReleaseSLOs.md`). A collector that is down or unreachable does **not** fail startup or requests; the exporter drops batches and reports through OTel self-diagnostics. There is no Prometheus-style `/metrics` scrape endpoint in the shipped hosts (`GET /api/runtime-studio/metrics` is a backlog snapshot, not process telemetry) — use the collector's Prometheus exporter if you need pull-based scraping.

## CI vs production

- **Green on `master`** does not imply every optional workflow gate ran (path filters). For a release, run **`runtime-release-gate`** (and your own smoke) on the **tag** you intend to ship.
- **Forks** — Default **`GITHUB_TOKEN`** in a fork typically **cannot** publish to **`ghcr.io/<upstream-owner>/...`**. Use the **upstream** repo for release workflows, retarget images to your fork’s GHCR org, or add a **PAT** with `packages: write` and matching `docker/login-action` credentials. Same idea applies if you expect NuGet OIDC from a fork (usually run releases upstream).

## Secrets checklist

| Secret | Purpose |
|--------|---------|
| `NUGET_USER` | NuGet.org **profile name** (not email) for **Trusted Publishing** + `NuGet/login@v1` |
| `NUGET_API_KEY` | Optional **fallback** long-lived key if Trusted Publishing is not configured yet |
| `GITHUB_TOKEN` | Provided by Actions; used for GHCR push in `container-image-publish` |

Configure **Trusted Publishing** on nuget.org per [NuGet trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): register the reusable workflow **`reusable-release-nuget.yml`** (nuget.org matches the workflow that runs `NuGet/login`, not the caller); one policy covers both tag releases and NuGet-only dispatch. See **`docs/PUBLISHING.md`** for the full matrix and **`docs/RELEASE_RUNBOOK.md`** for operator steps.
