# Self-hosted Ashlar agent server (portal + cluster)

Docker lane for **Ashlar.API** with a **mounted workspace**, bundled **Ollama**, the **Director portal**, and the **same** background agent JSON as Runtime Studio (`ASHLAR_BACKGROUND_AGENTS_CONFIG` → `apps/runtime-studio/config/agent_set.local.json` by default).

**Mental model:** [How this fits](../apps/runtime-studio/README.md#how-this-fits-one-config-flexible-hosts) in `apps/runtime-studio/README.md` — one JSON agent set; pick CLI daemon, API-hosted cluster, or portal-only compose as needed.

Not the Cursor IDE remote stack — Ashlar is the framework that hosts agents over HTTP.

## What you get

| Surface | Purpose |
|--------|---------|
| `http://<host>:<port>/` | Director portal (static, **intent-adaptive** UI — local preferences only); host port defaults to **8080** (`ASHLAR_AGENT_SERVER_HTTP_PORT`) |
| `POST /api/director/run` | One directorial iteration → daily JSON |
| `GET /api/director/dailies` | List dailies |
| `POST /api/agent`, `POST /api/orchestrate` | On-demand agent / orchestration calls |
| `GET /api/status` | API + aggressiveness mode |
| Background agents | Loaded from JSON; configurable path (`ASHLAR_BACKGROUND_AGENTS_CONFIG`) |

The API process registers the same **dogfood runners** as `ashlar background-agent daemon` (analysis, tests, self-extend), so scheduled agents can act on the **mounted repository**. The self-extender is **Passive by default** (observe only): it is armed by the aggressiveness mode file (`{"Mode":"active"}` written by `ashlar background-agent mode set --value active`, path `ASHLAR_AGENT_MODE_PATH`, default `~/.ashlar/agent-mode.json` — inside the container that is ephemeral, so point it at the mounted tree, e.g. `/work/.ashlar/agent-mode.json`); a missing file, `{}` or an unknown value all read as Passive.

## Prerequisites

- **Docker Compose v2** (Docker Engine, **Docker Desktop** on macOS/Windows, or **Podman Compose** / **Rancher Desktop** with compose support)
- A checkout of this repository (or another tree you want agents to use) on the host
- Disk space for Ollama models

## Quick start

From the **repository root**:

```bash
docker compose -f deploy/compose/docker-compose.agent-server.yml up -d --build
```

No `ASHLAR_REPO_ROOT` is needed for the default layout: relative host paths in a compose file resolve against the compose file's own directory (`deploy/compose/`), and the mount default is `../..` — the repo root — regardless of your shell CWD. Set `ASHLAR_REPO_ROOT` only to mount a different tree.

Pull the model referenced by your agent config (the shipped `agent_set.local.json` uses **qwen2.5:7b**; `OLLAMA_MODEL` defaults to `llama3.1:latest` for provider fallbacks):

```bash
docker compose -f deploy/compose/docker-compose.agent-server.yml exec ollama ollama pull qwen2.5:7b
```

Smoke check:

```bash
curl -s "http://localhost:${ASHLAR_AGENT_SERVER_HTTP_PORT:-8080}/api/status"
```

**Windows (PowerShell)** from repo root:

```powershell
docker compose -f deploy/compose/docker-compose.agent-server.yml up -d --build
docker compose -f deploy/compose/docker-compose.agent-server.yml exec ollama ollama pull qwen2.5:7b
Invoke-RestMethod "http://localhost:8080/api/status"
```

## Configuration (flexibility first)

### 1) Environment file (recommended)

Copy the template and edit:

- Template: `docs/config/agent-server.env.example`
- Typical usage: save as **`deploy/compose/.env`** (Compose v2 loads `.env` from the directory of the first `-f` compose file, **not** the shell CWD or repo root), or pass any path explicitly:

```bash
docker compose --env-file ./docs/config/agent-server.env.example -f deploy/compose/docker-compose.agent-server.yml up -d --build
```

Use **one `.env` per machine** or per environment (`dev`, `staging`) and swap `--env-file` as needed.

### 2) Compose-substituted variables

| Variable | Default | Purpose |
|----------|---------|---------|
| `ASHLAR_REPO_ROOT` | `../..` (repo root, relative to `deploy/compose/`) | Host path bind-mounted into the container (your Ashlar tree or another project). Relative values resolve against the compose file directory, not the shell CWD. |
| `ASHLAR_CONTAINER_WORKDIR` | `/work` | Working directory inside the container; mount target must match. |
| `ASHLAR_REPO_MOUNT_SUFFIX` | *(empty)* | Appended to the bind mount (e.g. **`:z`** or **`:Z`** on Linux with SELinux; **`:ro`** for read-only trees). |
| `ASHLAR_AGENT_SERVER_HTTP_PORT` | `8080` | Published host port for Ashlar.API. |
| `ASHLAR_OLLAMA_HOST_PORT` | `11434` | Published host port for bundled Ollama. |
| `ASHLAR_OLLAMA_IMAGE` | `ollama/ollama:latest` | Ollama image pin (tag for reproducibility). |
| `OLLAMA_BASE_URL` | `http://ollama:11434` | Base URL **inside** the API container (service name when using bundled Ollama). |
| `OLLAMA_MODEL` | `llama3.1:latest` | Default model hint for providers (align with `ollama pull`). |
| `ASHLAR_BACKGROUND_AGENTS_CONFIG` | `/work/apps/runtime-studio/config/agent_set.local.json` | JSON with `BackgroundAgents:Agents`. If you change `ASHLAR_CONTAINER_WORKDIR`, update this path to match. |
| `ASHLAR_DAILIES_PATH` | `/data/dailies` | App path for director dailies. Default compose keeps a **named volume** at `/data/dailies`; if you change this path, add a matching volume in a local override file. |
| `ASHLAR_STATE_DIR` | `/data/state` | Runtime state (LiteDB `ashlar-*.db`, `ashlar-snapshots/`). Default compose keeps a **named volume** at `/data/state` so state never lands in the mounted repo under `/work`; if you change this path, add a matching volume in a local override file (see `docs/DEPLOYMENT.md`, "Runtime state"). |
| `Ashlar__Barriers__RequireExplicitBarrier` | `false` | Hosted-friendly barrier default; tighten for stricter deployments. |
| `ASHLAR_OBSERVATION_DEGRADED_MODE` | `1` | Safer observation pipeline on some bind-mount / network FS setups. |
| `ASHLAR_BUILD_CONTEXT` | `../..` (repo root, relative to `deploy/compose/`) | Docker build context (advanced monorepo layouts). |
| `ASHLAR_AGENT_SERVER_DOCKERFILE` | `.docker/Dockerfile.agent-server` | Alternate Dockerfile path. |
| `COMPOSE_PROJECT_NAME` | *(compose default)* | Prefixes named volumes/networks so multiple stacks can coexist on one host. |

### 3) Per-platform notes

- **macOS / Windows (Docker Desktop)**  
  - Prefer forward slashes in `ASHLAR_REPO_ROOT` where possible.  
  - **External Ollama on the host:** set `OLLAMA_BASE_URL=http://host.docker.internal:11434` and start **only** the API (see below).

- **Linux (native Docker)**  
  - **SELinux (Fedora, RHEL, …):** often need `ASHLAR_REPO_MOUNT_SUFFIX=:z` (or `:Z` for private bind).  
  - **External Ollama on the host:** try `OLLAMA_BASE_URL=http://172.17.0.1:11434` or your bridge gateway IP; ensure Ollama listens on that interface.

- **WSL2**  
  - Put the repo on the **Linux filesystem** (`~/...`) for sane I/O; use that path as `ASHLAR_REPO_ROOT` when you run Compose from the WSL shell.

- **Podman / Rancher Desktop**  
  - Same compose file; verify `docker compose` is wired to your runtime. Volume suffix options may differ; see your distro docs for SELinux labels.

### 4) Bundled Ollama vs Ollama on the host

**Bundled** (default): both services start; `OLLAMA_BASE_URL=http://ollama:11434`.

**External** (host or another VM):

1. Set in `.env`, for example:  
   `OLLAMA_BASE_URL=http://host.docker.internal:11434` (Docker Desktop) or your Linux gateway IP.
2. Start **only** the API so Compose does not start the bundled Ollama container:

```bash
docker compose -f deploy/compose/docker-compose.agent-server.yml up -d --build ashlar-api --no-deps
```

Run `ollama serve` (or your package manager service) on the host on the port you point to.

### 5) Local compose overrides (advanced)

Create a **gitignored** file such as `deploy/compose/local/agent-server.yml` — `deploy/compose/local/` is ignored precisely for this, because the example below mounts a secrets file and a home-directory path. Do **not** use `deploy/compose/docker-compose.agent-server.local.yml`: that name belongs to a shipped Docker Desktop override that is tracked in the repo, so writing your own settings there both clobbers it and stages your machine's paths for commit.

```yaml
services:
  ashlar-api:
    environment:
      OLLAMA_BASE_URL: http://host.docker.internal:11434
    volumes:
      - ${HOME}/secrets/ashlar-api.appsettings.json:/app/appsettings.Production.json:ro
```

Merge explicitly (order matters — later files override):

```bash
docker compose -f deploy/compose/docker-compose.agent-server.yml -f deploy/compose/local/agent-server.yml up -d --build
```

Never commit secrets; keep overrides local or in your deployment pipeline.

## Image choice

`deploy/compose/docker-compose.agent-server.yml` builds from `.docker/Dockerfile.agent-server`, which uses the **.NET SDK** in the final image so **test** and **build** style agents can run `dotnet` against the mounted workspace. The lighter `deploy/compose/docker-compose.portal.yml` image (`Dockerfile.api`) remains appropriate when you only need the portal + API **without** a full agent cluster on a mounted repo.

## Hardening

For anything beyond a trusted LAN, put **TLS + authentication** in front of the published HTTP port, restrict network access, and treat the mounted repo as **read/write** within agent policy.


## Stop

```bash
docker compose -f deploy/compose/docker-compose.agent-server.yml down
```

Named volumes (`ollama-models`, `ashlar-dailies`, `ashlar-state`) are kept unless you `down -v` — which also deletes runtime state (LiteDB stores, snapshots), not just models and dailies.
