# Runtime Studio (Application Layer)

`runtime-studio` is an application-level integration that composes Ashlar runtime services into a planner + worker agent set.

This sits outside kernel internals and uses:

- `Ashlar.CLI background-agent daemon --config ...`
- project-scoped sandboxing under `agent-sandbox/` (a sibling of `.ashlar/`, which is governance state and is refused at the write edge)
- local-first model routing via Ollama

<a id="how-runtime-studio-fits-with-ashlar-api"></a>

## How this fits (one config, flexible hosts)

There is **no second “Runtime Studio app”** in the repo — only this folder (config + scripts) plus the shared Ashlar kernel.

| Piece | What it is |
|--------|------------|
| **`config/agent_set.local.json`** | **Single source of truth** for the planner / optimizer / tester **background** agent definitions (`BackgroundAgents:Agents`). |
| **CLI daemon** (`run_agent_set_local.sh`) | Runs that JSON in a **standalone** `ashlar background-agent daemon` process (best for local dev matching CI-style scripts). |
| **`Ashlar.API` + `ASHLAR_BACKGROUND_AGENTS_CONFIG`** | Runs the **same JSON** inside the API process (best with Docker / a mounted repo — see `docs/SelfHostedAgentServer.md`). |
| **Director portal** (`Ashlar.API` `/`) | **Different workflow**: human-driven goals → orchestration → **dailies** JSON. It does not replace the background cluster; it often lives on the **same** API host. |

**Compose (pick the lane you need):**

- **`deploy/compose/docker-compose.portal.yml`** — portal + API + Ollama; **no** mounted workspace / default agent-server cluster wiring.
- **`deploy/compose/docker-compose.agent-server.yml`** — portal + API + Ollama + **mounted repo** + default `ASHLAR_BACKGROUND_AGENTS_CONFIG` → this folder’s JSON.

Cross-platform env tuning: `docs/config/agent-server.env.example`.

## Why this exists in `apps/`

This is a runtime application of Ashlar, not a kernel primitive.
Kernel packages stay reusable; Runtime Studio defines product workflow, operator scripts, and agent-set policy.

## Local-first agent set

Config file:

- `apps/runtime-studio/config/agent_set.local.json`

Agent roles included:

- `runtime-planner` (`extender`) - planning + safe code actions through policy-gated self-extend
- `runtime-worker-optimizer` (`optimizer`) - code analysis worker
- `runtime-worker-tester` (`tester`) - test verification worker

The game-director run mode (a commercial vertical) no longer lives in this
repository: the game vertical was removed in the 2026-08-31 native-responsibility
slim and is preserved on the archive branch `archive/verticals-2026-08-31` for
extraction to its own repository (see `/LICENSING.md`).

## Operator CLI quick reference

See **[OPERATOR.md](./OPERATOR.md)** for copy-paste `background-agent` commands and env vars.

## Quick start

From repo root:

```bash
bash apps/runtime-studio/scripts/bootstrap_runtime_studio.sh
bash apps/runtime-studio/scripts/run_agent_set_local.sh --duration 5m --disable-observation
```


The run script configures:

- `ASHLAR_SANDBOX_ROOT`
- `ASHLAR_PATH_ALLOWLIST_EXTRA`
- cache relocation (`TMPDIR`, `NUGET_PACKAGES`, `NPM_CONFIG_CACHE`)
- local model defaults (`OLLAMA_BASE_URL`, `OLLAMA_MODEL`)

## Hardware-tuned workflow optimize (optional)

After bootstrap, benchmark workflow compositions on this machine and emit a recommendation report:

```bash
bash apps/runtime-studio/scripts/optimize_agent_cluster.sh --objective 'your tuning goal' --verbose
```

The script forwards **`--budget-runs`** to `ashlar workflow optimize` (default **48** measured runs cap for laptop-friendly runs; use **`--budget-runs 0`** for no cap), then runs **`ashlar runtime-studio apply-tune`** so the winning local model profile is written into the **gitignored** **`.ashlar/runtime-studio/agent_set.local.json`** (seeded from the tracked `config/agent_set.local.json` on first use; pass **`--config apps/runtime-studio/config/agent_set.local.json`** to tune the tracked file in place, or **`--skip-apply-agent-set`** to only benchmark). `run_agent_set_local.sh` and **`ashlar runtime-studio status|doctor|apply-tune`** read the local copy first and fall back to the tracked file. Run **`bash apps/runtime-studio/scripts/optimize_agent_cluster.sh --help`** for all flags.

The setup scripts run the same optimize + apply flow only when asked: **`bash scripts/setup/setup.sh all --tune`** (macOS/Linux) or **`scripts/setup/setup.ps1 -Mode all -Tune`** (Windows, needs Git Bash). Without the flag `setup … all` finishes after restore. **`ASHLAR_SKIP_RUNTIME_STUDIO_TUNE=1`** / **`-SkipRuntimeStudioTune`** force-skip even with the flag.

Inspect current state anytime: **`dotnet run --project application/src/Ashlar.CLI -- runtime-studio status`** (or **`--format-json`**).

## Customize the agent set

Edit:

- `apps/runtime-studio/config/agent_set.local.json`

Common changes:

- tune schedule intervals
- update `ModelName` for planner/worker split
- change tester filter
- adjust exfiltration policy boundaries
- adjust each game worker `Objective` to match your project pillars (art style, encounter pacing, systems depth)

## Phase 2 (shipped in tree)

| Track | Status |
|--------|--------|
| **Daemon in CI** | Black-box: timed daemon with/without `--disable-observation` (`RuntimeStudioBlackBoxSmokeTests`). |
| **Passive + forge** | `ForgeToolsTests` (propose/check/**forge.build**/**forge.test**) + `ForgeMediatedWritesPolicy` + `ProposalsBackgroundAgentCommandTests` (`build`, `test`, `apply --verify-build` / `--verify-test`). |
| **Operator UX** | **[OPERATOR.md](./OPERATOR.md)** — env vars, CLI one-liners for observations / objectives / proposals / mode / daemon. |
| **HTTP client demos** | **`docs/demos/README.md`**, **`Ashlar.Demos.sln`** — Console, Blazor, Avalonia (`net8.0`, Linux-friendly). |
| **Performance** | `CliRunner` cross-process mutex + `CONTRIBUTING.md` guidance; smoke blame-hang 180s on Cross-Platform Tests. |

Contributing note: avoid parallel full `dotnet build` on one clone (see `CONTRIBUTING.md` — `*.deps.json` locks).

## Phase 3 (shipped)

| Track | Status |
|--------|--------|
| **Objective claim in daemon (E2E)** | Black-box: `RuntimeStudioBlackBoxSmokeTests.Daemon_extender_claims_objective_from_store_increments_attempts` — timed extender with no pinned `Objective`, deterministic model, asserts backlog `Attempts` after release-on-no-action. |
| **Operator dashboard** | `ashlar background-agent dashboard [--port 5055] [--open]` — read-only JSON + auto-refresh UI on **127.0.0.1** only (same `ASHLAR_*` paths as the CLI). |

## Phase 4 (shipped)

| Track | Status |
|--------|--------|
| **Objective SLA-style metrics in API** | `GET /api/runtime-studio/metrics` — counts by objective/proposal status, `OldestPendingAgeHours` / `OldestInProgressAgeHours`, observation log path + file size. |
| **Dashboard auth + TLS notes** | `ashlar background-agent dashboard --auth-token …` or `ASHLAR_DASHBOARD_AUTH_TOKEN`; `?token=` / `Authorization: Bearer`; reverse-proxy snippets in **[OPERATOR.md](./OPERATOR.md)**. |
| **Play Internal testing** | **[PLAY_INTERNAL.md](./PLAY_INTERNAL.md)** — packaging lives outside this repo; API/client integration only. |

## Phase 5 (shipped)

| Track | Status |
|--------|--------|
| **Shared metrics core** | `Ashlar.BackgroundAgents.RuntimeStudio` — `RuntimeStudioPathResolver`, `RuntimeStudioMetricsCollector` (+ unit tests). API `GET /api/runtime-studio/metrics` and CLI/dashboard consume the same logic. |
| **CLI parity** | `ashlar runtime-studio metrics [--format-json]` — backlog counts, SLA ages, observation file size (paths from `ASHLAR_*` + repo root). |
| **Dashboard JSON** | `background-agent dashboard` `/api/summary.json` includes a `metrics` object (`RuntimeStudioDiskMetrics`) alongside `observationsTail`. |

## Phase 6 (shipped)

| Track | Status |
|--------|--------|
| **Operator dashboard UI** | HTML **at-a-glance** cards + paths summary + collapsible raw JSON (reads `metrics` / PascalCase-safe). |
| **Status + metrics** | `ashlar runtime-studio status --with-metrics` (text block or JSON `runtimeStudioMetrics` when combined with `--format-json`). |
| **Smoke** | `RuntimeStudioBlackBoxSmokeTests.Runtime_studio_metrics_format_json_exits_zero` — CLI metrics with isolated `ASHLAR_*` env. |

## Phase 7 (shipped)

| Track | Status |
|--------|--------|
| **Observation tail metrics** | `RuntimeStudioDiskMetrics` adds `ObservationsTailLineCount` (tail window) + `ObservationsLastTimestamp`; `ObservationLogTailReader` (+ tests). API, CLI, dashboard cards updated. |
| **Runtime Studio doctor** | `ashlar runtime-studio doctor [--format-json] [--strict]` — validates agent-set JSON (`BackgroundAgents.Agents`) and path layout; strict mode errors on missing objectives/forge dirs. |
| **Smoke** | `Runtime_studio_doctor_format_json_exits_zero` — doctor against the real repo agent set. |

