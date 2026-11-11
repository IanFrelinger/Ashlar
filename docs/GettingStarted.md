# Getting Started with Ashlar

This guide covers initial setup, trust configuration, and first pipeline validation. Ashlar operates on local infrastructure with no external service dependencies. Trust controls are available but disabled by default — enable with `ASHLAR_TRUST_ENABLED=1`.

**First run?** Use `docs/TesterQuickstart.md` instead: one lane (build `Ashlar.Kernel.sln`, run the API on loopback, submit a task, read its audit trail, run the certification gate), no Docker, no API keys. This guide is the longer tour that follows it.

The **default** path here is **containers + CLI**: develop inside the **Dev Container** (or run published **GHCR** images / **compose** stacks). If you cannot use Docker at all, use **`scripts/setup/*`** on a machine with **.NET SDK 10** — those are the only native bootstrap scripts. The `scripts/install/*` helpers are not installers: `container-bootstrap.*` bootstrap the **container** lane (Docker + image pull + smoke run) and `quickstart.sh` starts the portal with Docker or an already-installed SDK. See `README.md` for the full map.

## Quickest path (recommended)

**Cursor / VS Code:** install [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers), open the repo, **Dev Containers: Reopen in Container**. The first open restores the setup-gate NuGet graph via `.devcontainer/post-create.sh`.

**Portal in Docker only** (no IDE):

```bash
git clone https://github.com/IanFrelinger/Ashlar.git && cd Ashlar
docker build -f .docker/Dockerfile.quickstart -t ashlar:quickstart .
docker run --rm -p 127.0.0.1:8080:8080 ashlar:quickstart
# Open http://localhost:8080 — mock provider; no API keys needed.
```

The image has no auth; publish on all interfaces (`-p 8080:8080`) only behind auth + TLS — see `README.md` → *Security Defaults* and `SECURITY.md`.

**One-command script** (uses Docker when present, otherwise tries a local SDK): `bash scripts/install/quickstart.sh` — see `scripts/install/quickstart.sh`.

## What you will do (manual path)

In ~10-15 minutes, you will:

1. Pick one startup lane (**Dev Container**, **GHCR CLI**, **quickstart image**, or **native** escape hatch).
2. Verify CLI is working.
3. Validate and run a minimal pipeline.
4. Move to deeper validation/testing only after first success.

## Prerequisites

- **Default:** Docker (Desktop or Engine) and Git. You do **not** need a host .NET SDK for Dev Container, quickstart image, or `docker run … ghcr.io/ianfrelinger/nexo-cli`.
- **Native lane:** .NET SDK **10.x** (LTS; repo is pinned in `global.json`). The CLI and API ship on `net10.0`; libraries and the remaining `net8.0` test hosts roll forward onto the 10.x runtime (`RollForward=Major`), so an SDK-10-only machine works; no separate .NET 8 runtime is needed.
- Optional: Ollama/OpenAI/Azure credentials (model-backed commands).

## 1) Choose your startup lane

### Lane A (fastest): container-first — Dev Container + `dotnet` CLI

Use **Dev Containers: Reopen in Container** (see `README.md`). Then:

```bash
dotnet build application/src/Ashlar.CLI/Ashlar.CLI.csproj --no-restore
dotnet run --project application/src/Ashlar.CLI -- --help
```

### Lane B: published CLI image (minimal host)

```bash
docker pull ghcr.io/ianfrelinger/nexo-cli:latest
docker run --rm ghcr.io/ianfrelinger/nexo-cli:latest --help
```

With workspace mount:

```bash
docker run --rm -v "$PWD:/work" -w /work ghcr.io/ianfrelinger/nexo-cli:latest --help
```

The published **`nexo-cli`** image is **runtime-only** (no `git`/`curl` in the container OS), so **`ashlar doctor --json`** is expected to report missing host tools there. Run **`doctor`** on your workstation or inside the **Dev Container** for a full dependency check; CI validates the image with **`--help`** and **`pipeline validate --help`** instead (see **`docs/DistributionModels.md`**).

### Lane C (escape hatch): native setup scripts + CLI build

**`bash scripts/setup/setup.sh all`** (same as **`bash scripts/setup/setup-unix.sh all`** on macOS/Linux; those dispatch to `setup-linux.sh` / `setup-macos.sh`) and Windows **`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup\setup.ps1 -Mode all`** install missing host tools and run `dotnet restore` for the setup graph. They finish in the time a restore takes; **no model benchmark runs by default**.

**Optional hardware tune (opt-in):** add **`--tune`** (Unix) or **`-Tune`** (Windows `setup.ps1`; requires Git Bash) to `all` and setup also runs a **bounded** Runtime Studio **`workflow optimize`** (`--budget-runs 24`, several minutes against local Ollama models) and writes the winning **Ollama `ModelName` values** into the **gitignored** `.ashlar/runtime-studio/agent_set.local.json` (seeded from the tracked `apps/runtime-studio/config/agent_set.local.json`, which setup never modifies). `run_agent_set_local.sh`, `optimize_agent_cluster.sh` and `ashlar runtime-studio status|doctor|apply-tune` read that local copy first and fall back to the tracked file. `ASHLAR_SKIP_RUNTIME_STUDIO_TUNE=1` / `-SkipRuntimeStudioTune` still force-skip it, and it is always skipped in **CI** (`CI` / `GITHUB_ACTIONS`).

Clone the repo, then run setup and build (same graph CI uses):

```bash
git clone https://github.com/IanFrelinger/Ashlar.git
cd Ashlar
bash scripts/setup/setup.sh all
dotnet build application/src/Ashlar.CLI/Ashlar.CLI.csproj --no-restore
```

```powershell
git clone https://github.com/IanFrelinger/Ashlar.git
Set-Location Ashlar
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup\setup.ps1 -Mode all
dotnet build application/src/Ashlar.CLI/Ashlar.CLI.csproj --no-restore
```

After the CLI builds, optional **hero** checks (doctor + quickstart pipeline):

```bash
dotnet run --project application/src/Ashlar.CLI -- doctor --json
# then run pipeline validate/run/diagnostics — section 3 and section 5 below
# (the same sequence as README → "Lane 2 — Develop" → "Run your first pipeline")
```

```powershell
dotnet run --project application/src/Ashlar.CLI -- doctor --json
```

## 2) Verify CLI is available

```bash
dotnet run --project application/src/Ashlar.CLI -- --help
dotnet run --project application/src/Ashlar.CLI -- doctor --json
```

You should see commands including `analyze`, `validate`, `pipeline`, `trust`, `test`, `orchestrate`, `agent`, `chat`, `runtime`, `workflow`, `mesh`, `config`, `doctor`, `dogfood`, `escalate`, `metrics`, and more.

### Machine-readable command output

The global `--format-json` flag requests JSON from commands that implement it, with host logs on
stderr. Commands without that rendering refuse with exit 1, empty stdout and an explanation on
stderr **before doing any work**. Their help descriptions identify this limitation. `--help` remains
human-readable even when `--format-json` is also present; parser errors are usage errors, not JSON
results. Explicit boolean values (`--format-json=true`, `--format-json:false`) and response files
follow the same parser semantics as other options.

Existing JSON renderings retain their schemas. Examples include `doctor`, `certify brick`,
`pipeline`, `docker`, and `test portable --list`. `test portable --scope all`, `dogfood all`, and
`background-agent daemon` refuse the global flag because their combined/lifecycle output is not one
JSON document. Choose a single portable scope or dogfood block instead. Commands with only a local
`--json` option still support that option, but do not automatically support the global spelling.

## 2b) Try the copilot task flow (optional)

Submit a coding task and receive output with an audit trail:

```bash
# Via API (mock provider for testing):
ASHLAR_ALLOW_MOCK=1 dotnet run --project application/src/Ashlar.API -f net10.0 &
curl -s http://localhost:5000/api/copilot/task \
  -H "Content-Type: application/json" \
  -d '{"task": "Analyze the security posture of this project"}' | jq .
```

Or open `http://localhost:5000` in a browser — the portal includes a setup wizard, Quick chat, activity feed, and changelog assistant.

See `docs/CopilotMvpWalkthrough.md` for the full walkthrough.

## 2c) Run background-agent daemon mode (optional)

Use this when you want Ashlar to run as a long-lived local process with hosted background agents.
If your config does not define `Ashlar:Barriers:Levels`, the daemon defaults to `["public","internal"]` for local bootstrap.

```bash
# run for 30 seconds (smoke test)
dotnet run --project application/src/Ashlar.CLI -- background-agent daemon --duration 30s

# run until Ctrl+C
dotnet run --project application/src/Ashlar.CLI -- background-agent daemon

# use an explicit background-agent config file
dotnet run --project application/src/Ashlar.CLI -- background-agent daemon --config docs/background-agents/examples/minimal-agent.json
```

## 3) First success command (high confidence, low friction)

```bash
tmp_dir="$(mktemp -d)"
template_path="$tmp_dir/ashlar_quickstart.json"
cat > "$template_path" <<'JSON'
{
  "templateId": "quickstart",
  "version": "1.0",
  "stages": [
    { "id": "ingest", "name": "Ingest", "mode": "Deterministic" },
    { "id": "hybrid", "name": "Hybrid", "mode": "Hybrid", "fallbackChain": ["Deterministic", "Agentic"] }
  ],
  "edges": [
    { "fromStageId": "ingest", "toStageId": "hybrid" }
  ]
}
JSON
dotnet run --project application/src/Ashlar.CLI -- pipeline validate --template "$template_path"
```

## 4) Run first high-signal commands

Run these from the repository root:

```bash
# code and assembly analysis
dotnet run --project application/src/Ashlar.CLI -- analyze --path .

# optional (heavier) architecture/test validation
dotnet run --project application/src/Ashlar.CLI -- validate
```

## 5) Run your first pipeline

Create a minimal template:

```bash
cat > /tmp/ashlar_pipeline_demo.json <<'JSON'
{
  "templateId": "demo",
  "version": "1.0",
  "stages": [
    { "id": "ingest", "name": "Ingest", "mode": "Deterministic" },
    { "id": "hybrid", "name": "Hybrid", "mode": "Hybrid", "fallbackChain": ["Deterministic", "Agentic"] }
  ],
  "edges": [
    { "fromStageId": "ingest", "toStageId": "hybrid" }
  ]
}
JSON
```

Validate + run:

```bash
dotnet run --project application/src/Ashlar.CLI -- pipeline validate --template /tmp/ashlar_pipeline_demo.json
dotnet run --project application/src/Ashlar.CLI -- pipeline run --template /tmp/ashlar_pipeline_demo.json --run-id demo-run --format-json
dotnet run --project application/src/Ashlar.CLI -- pipeline diagnostics --format-json
```

## 6) Optional provider setup

If you plan to run model-backed workflows:

### OpenAI

```bash
export OPENAI_API_KEY="sk-..."
export OPENAI_MODEL="gpt-4o-mini"
```

### Ollama

```bash
export OLLAMA_BASE_URL="http://localhost:11434"
export OLLAMA_MODEL="llama3.1:latest"
```

Provider behavior and full configuration are documented in `docs/Configuration.md`.

## 7) Testing workflows

```bash
# targeted infrastructure tests
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~Pipelines"

# execution routing smoke + stress (NCR local, peer network fallback, cloud routing behavior)
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj \
  --filter "FullyQualifiedName~PeerToPeerRoutingSmokeTests|FullyQualifiedName~CapabilityRoutingBrickTests"

# broader local test command
dotnet run --project application/src/Ashlar.CLI -- test local
```

For timeout policy and anti-hang guidance, see `docs/Testing.md`.

## 8) Embed in your host application

At minimum:

```csharp
using Ashlar.Hosting;

services.AddAshlar();
```

Then resolve application ports from DI (analysis, validation, orchestration, etc.).

## Common pitfalls

- If commands fail due to SDK mismatch, ensure your local SDK honors `global.json` (`10.x`).
- **`dotnet build Ashlar.sln`** should succeed on Linux with a stock .NET SDK; use **`Ashlar.LocalDevCore.slnf`** or **`Ashlar.PrimeTime.slnf`** when you want a smaller/faster slice.
- Prefer running heavy validations sequentially (not in parallel terminals) to avoid resource pressure.
- For CI parity, use the documented gate workflows under `.github/workflows/`.
- If native setup is blocking, switch to container lane first and continue there.

## Next documents to read

- `README.md` — high-level orientation and full CLI command map.
- `docs/Architecture.md` — subsystem layout and flow.
- `docs/Configuration.md` — environment variables and config reference.
- `docs/api/index.md` — REST API endpoints and hosting options.
- `docs/sdk.md` — SDK integration (host embedding and HTTP client).
- `docs/TrustAndInformationArchitecture.md` — barrier/trust model.
- `docs/Persistence.md` — persistence defaults and LiteDB options.
- `docs/ProductionReadinessGate-v1.md` — release gate and operator checks.
- `docs/DocsIndex.md` — full documentation index.
