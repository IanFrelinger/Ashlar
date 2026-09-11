# Configuration Reference

Ashlar configures via environment variables and optional `~/.ashlar/config.json`. This document lists the primary configuration options; see inline code comments for the full set. Read the next section before reaching for `appsettings.json`.

## How `Ashlar:*` options bind in the shipped hosts (read first)

There are three kinds of `Ashlar:*` option, and they do **not** read the same configuration:

- **Kernel options — environment variables ONLY.** `AddAshlar()` (`src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs`, `AshlarServiceCollectionExtensions.AddAshlar`) builds its **own** `IConfiguration` from `AddEnvironmentVariables()` alone and hands that to every module it composes. In **Ashlar.API** and **Ashlar.CLI** the options those modules bind therefore come from `Ashlar__Section__Key` environment variables, and **`appsettings.json` does not reach them** — a `"Ashlar": { "Meai": { ... } }` block in the API's `appsettings.json` is silently ignored. This covers at least `Ashlar:Meai:*` / `Ashlar:UseMeaiPipeline`, `Ashlar:NodeCapabilityRuntime:*`, `Ashlar:RemoteCapabilities:*`, `Ashlar:RunPod:*`, `Ashlar:WorkloadScaling:*` and `Ashlar:MeshLab:WorkerExecutor:*`. Set them as `Ashlar__Meai__OllamaBaseUrl=...` etc. (the compose stacks do exactly this). Documented as a known v0 limitation; the fix is architectural, not a docs fix.
- **Host-owned options — the host's configuration (`appsettings.json` + environment).** Sections the API binds itself from `builder.Configuration` in `application/src/Ashlar.API/Program.cs` — `Ashlar:Security:*`, `Ashlar:Execution:*`, `Ashlar:Barriers:*`, `Ashlar:Routing:*`, `Ashlar:Mcp:*`, `Ashlar:A2A:*`, `Ashlar:GrpcTransport`, `Ashlar:Product`, `Ashlar:Entitlements`, `Ashlar:PrivateLicense`, `Ashlar:PatternStorePath` — read `appsettings.json` and `Ashlar__*` variables alike, with the usual precedence (environment wins).
- **Host-composed options — whatever configuration you pass.** `Ashlar:Autonomy:*` is bound by `AddAshlarAutonomy(configuration)`, which the shipped hosts do not call; a host that composes the loop decides what it passes.

The tables below list keys in `Ashlar:A:B` form with the `Ashlar__A__B` environment spelling beside them; whether a JSON file can supply the key at all is decided by which of the three groups above the section belongs to, not by the form the table happens to use.

## Core

| Variable | Description | Default |
|----------|-------------|---------|
| `ASHLAR_CONFIG_PATH` | Path to config file | `~/.ashlar/config.json` |
| `ASHLAR_STATE_DIR` | Runtime-state directory for LiteDB stores and snapshots (see "Runtime state" below); absolute, or relative to the resolved repo/app root | `<repo or app root>/.ashlar/state` |
| `ASHLAR_DOCS_ROOT` | Root that `ashlar improve` writes promoted-adaptation documentation (`docs/bricks/<brick>.md`) beneath; absolute, or relative to the resolved repo/app root. The CLI E2E tests point it at their temp directory so a promotion never lands in the checkout | `<repo or app root>` (Ashlar documents itself in its own tree) |
| `ASHLAR_MESH_INSTANCES_PATH` | Path to **`instances.json`** for **`ashlar mesh`** discovery | `~/.ashlar/instances.json` |
| `ASHLAR_MESH_TRUST_POLICY` | Peer trust policy for `ashlar mesh` discovery **and** capability requests: **`any`**, **`allowlist`** (only **`admitted: true`** peers), **`trusted-only`**, **`trusted-preferred`**; any other value normalizes to `trusted-preferred` (fail-closed). Falls back to `ASHLAR_PEER_TRUST_POLICY` when unset (`MeshTrustPolicyConfiguration`) | unset → **`any`** for discovery, **`trusted-preferred`** for capability requests |
| `ASHLAR_MESH_DIRECTOR_BASE_URL` | Base URL for **commercial mesh director CLI** (`dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director ...`) HTTP calls | unset |
| `ASHLAR_MESH_API_KEY` | Optional **`X-Ashlar-Api-Key`** for director CLI | unset |
| `ASHLAR_MESH_MUTATING_TOKEN` | Optional **`X-Ashlar-Mesh-Token`** for mutating mesh routes on the hub | unset |
| `ASHLAR_MESH_PEER_REGISTRATION_KEY` | Per-peer fleet registration secret for **commercial mesh director CLI `register`** (when director requires distinct key) | unset |
| `ASHLAR_DEPLOYMENT_PROFILE` | Hosting dependency profile for `AddAshlar()` module composition. Canonical values: `full`, `server`, `edge`, `air-gapped`, `secure-workstation` (alias `workstation`), `system`. Hyphens, underscores, and case fold (`airgapped`, `air_gapped`, `secure_workstation`). `air-gapped` is the slim offline profile (no trust/agents/observation). `secure-workstation` is the IDE daemon profile — not a synonym for air-gapped. The profile registers modules; trust still requires `ASHLAR_TRUST_ENABLED=1` or `AddAshlarWorkstation()`. | `full` |
| `ASHLAR_STRICT_MODE` | `1` or `true` = enable strict mode (fail-fast + verbose diagnostics for dev/CI; disable for production) | `false` |
| `ASHLAR_AIRGAP` | `1` or `true` = air-gapped; no cloud calls | unset |
| `ASHLAR_AIRGAP_PROBE` | `1` = probe network to detect air-gap | unset |
| `ASHLAR_TRUST_ENABLED` | `1` = enable Trust & sanitization | `false` |
| `ASHLAR_MODEL_PROVIDER` | Default LLM provider | from config |
| `ASHLAR_LOOP_PARALLEL` | `1` = parallel loop kernel | `false` |
| `ASHLAR_LOOP_INSTRUMENT` | `1` = instrumented loop | `false` |
| `ASHLAR_LLM_RETRY_COUNT` | Retries for cloud LLM (5xx/429) | `3` |

## Strict Mode (`ASHLAR_STRICT_MODE`)

Strict mode is designed for development and CI environments. When enabled, Ashlar fails fast and emits verbose diagnostics instead of silently falling back to defaults or retrying on errors. Flip it to permissive (disabled) once confident in the agentic layer for production.

**Master switch:** `ASHLAR_STRICT_MODE=1` enables all sub-flags below. Individual flags can override the master switch.

| Variable / Config Key | Description | Default |
|-----------------------|-------------|---------|
| `ASHLAR_STRICT_MODE` | Master switch — enables all sub-flags | `false` |
| `Ashlar:StrictMode:FailFastOnValidationErrors` | Throw immediately on validation failures | follows master |
| `Ashlar:StrictMode:FailFastOnProviderErrors` | Throw on provider misconfiguration instead of fallback | follows master |
| `Ashlar:StrictMode:FailFastOnPipelineErrors` | Throw on pipeline stage errors instead of retrying | follows master |
| `Ashlar:StrictMode:VerboseDiagnostics` | Emit debug-level logging and detailed error messages | follows master |
| `Ashlar:StrictMode:FailOnConfigurationWarnings` | Treat missing config files / empty configs as hard errors | follows master |

**Usage examples:**

```bash
# Development / CI — fail fast and verbose
export ASHLAR_STRICT_MODE=1

# Production — permissive (default)
# unset ASHLAR_STRICT_MODE   (or ASHLAR_STRICT_MODE=0)

# Fine-grained: strict for providers only
export ASHLAR_STRICT_MODE=0
# then, in your host's composition (the sub-flags are set programmatically; the shipped hosts
# do not bind Ashlar:StrictMode from appsettings.json — see "How Ashlar:* options bind" above):
# services.AddAshlar(o => o.StrictMode.FailFastOnProviderErrors = true);
```

## Runtime state (`ASHLAR_STATE_DIR`)

LiteDB stores and rollback snapshots that Ashlar writes at runtime — `ashlar-patterns.db`, `ashlar-adaptation.db`, `ashlar-adaptation-audit.db`, `ashlar-copilot-tasks.db`, `ashlar-execution.db`, `ashlar-test-failures.db`, `ashlar-snapshots/` — resolve through `RepoPathResolver.ResolveStateDirectory`:

1. `Ashlar:PatternStorePath` (API / daemon) or `--store-path` (CLI) when set: everything is co-located with that file / directory.
2. Otherwise `ASHLAR_STATE_DIR` when set — absolute, or relative to the resolved repo/app root.
3. Otherwise **`<repo or app root>/.ashlar/state/`** (the root is the nearest directory containing `Ashlar.sln`, else the current directory). `.ashlar/` is gitignored; the directory is created on first use.

Backward compatibility: an install that already has `ashlar-*.db` files directly at the repo root **and no `.ashlar/state/` yet** keeps using the root (nothing is moved or logged). To migrate, stop Ashlar and move the `ashlar-*.db` files and `ashlar-snapshots/` into `.ashlar/state/`.

Containers: the API and CLI images set `ASHLAR_STATE_DIR=/data/state` (owned by the non-root `app` user); the portal and agent-server compose stacks mount the `ashlar-state` named volume there (`docs/DEPLOYMENT.md`, "Runtime state").

## Ashlar.API host (`Ashlar__Api__*`)

| Variable / config key | Description | Default |
|-----------------------|-------------|---------|
| `Ashlar__Api__EnableSwagger` | Serve `/swagger` (UI) and `/swagger/v1/swagger.json`. The document enumerates every mapped route and schema, so it is off outside `Development` unless set | `true` in `Development`, else `false` |

`GET /health` (liveness, constant 200) and `GET /ready` (readiness: 503 while the host is starting or shutting down, 200 in between) are always mapped, unauthenticated, and outside `/api`.

## Centralized Defaults (`AshlarDefaults`)

All tunable constants are centralized in `Ashlar.Core.Domain.AshlarDefaults`. This eliminates hard-coded magic numbers scattered across the codebase. Override any value via environment variables or `appsettings.json` — keys are listed in the relevant sections below.

## Ashlar.API exposure (`Ashlar__Security__*`)

The profile does not enforce firewalls or Tailscale ACLs, but it **fails closed**: `Lan`, `Tailnet` and `Public` refuse to start while `AuthorizationMode` is `None` (and the legacy flag is off) unless `AllowUnauthenticatedNetworkExposure=true` is set explicitly. See **`SECURITY.md`** ("Default posture and in-scope surfaces"), **`docs/TailscaleAndAshlar.md`** and **`docs/config/security-exposure.env.example`**.

| Variable | Description | Default |
|----------|-------------|---------|
| `Ashlar__Security__ExposureProfile` | `Localhost`, `Lan`, `Tailnet`, or `Public` (case-insensitive). Off-loopback values require built-in auth (see above) | `Localhost` in `appsettings.json` |
| `Ashlar__Security__AllowUnauthenticatedNetworkExposure` | `true` — escape hatch: start with an off-loopback profile and no built-in auth (logs a warning). Only when an authenticating proxy or network ACL fronts the API | `false` |
| `Ashlar__Security__CustomAdvisory` | Optional extra line shown in the Director portal advisory | unset |
| `Ashlar__Security__ShowAdvisoryInPortal` | `true` / `false` — show advisory banner in portal | `true` |
| `Ashlar__Security__RequireApiKeyForMutatingEndpoints` | `true` / `false` — legacy: enforce API key checks for POST/PUT/PATCH/DELETE under `/api/*`. Fails closed: with no `ApiKey` configured, mutating requests get 401 | `false` |
| `Ashlar__Security__ApiKey` | Shared secret required for protected mutating requests | unset (disabled) |
| `Ashlar__Security__ApiKeyHeaderName` | Header used for key checks | `X-Ashlar-Api-Key` |
| `Ashlar__Security__ExcludedApiKeyPaths` | Comma-separated API path prefixes exempted from key checks | none |
| `Ashlar__Security__AuthorizationMode` | Built-in auth mode: `None`, `ApiKey`, `BearerToken`, `Basic`, `ApiKeyOrBearerToken`, `ApiKeyOrBasic`, `BearerTokenOrBasic`, `Any` | `None` |
| `Ashlar__Security__AuthorizationScope` | Built-in auth scope: `MutatingApi` or `AllApi` | `MutatingApi` |
| `Ashlar__Security__RequireAuthForCopilotReadApis` | `true` / `false` — credential `GET /api/copilot/tasks*` even under `MutatingApi`, because task history carries the prompts and outputs of past runs. Set `false` only if that history is not sensitive to you | `true` |
| `Ashlar__Security__ExcludedAuthorizationPaths` | Comma-separated API path prefixes exempted from built-in auth checks | none |
| `Ashlar__Security__BearerToken` | Shared secret for bearer token authorization | unset |
| `Ashlar__Security__BearerTokenHeaderName` | Header used for bearer token checks | `Authorization` |
| `Ashlar__Security__BearerTokenScheme` | Bearer scheme prefix when using `Authorization` header | `Bearer` |
| `Ashlar__Security__BasicAuthUsername` | Username for built-in basic auth | unset |
| `Ashlar__Security__BasicAuthPassword` | Password for built-in basic auth | unset |
| `Ashlar__Security__BasicAuthHeaderName` | Header used for basic auth checks | `Authorization` |

Notes:
- If `AuthorizationMode` is set to anything except `None`, built-in auth mode takes precedence over legacy `RequireApiKeyForMutatingEndpoints`.
- `RequireApiKeyForMutatingEndpoints` remains for backward compatibility with existing deployments. Like every other mode it fails closed when its credential is missing.
- `Ashlar__Security__ApiKey` / `BearerToken` / `BasicAuthPassword` are compared in constant time against the configured plaintext value; they are not hashed at rest. Keep them in environment / secret stores, not in committed `appsettings.json`.

## Observability (`ASHLAR_LOG_JSON`, `OTEL_*`)

Shipped hosts (`Ashlar.API`, `ashlar background-agent daemon`) log human-readable console lines and keep metrics in an in-process `MemoryMetricsCollector` by default. Structured output and export are opt-in and read through the **host** configuration (`builder.Configuration` in `Program.cs`), so `appsettings.json`, environment variables and `UseSetting` all work for these keys, unlike the env-only kernel `Ashlar:*` options.

| Variable / config key | Description | Default |
|------------------------|-------------|---------|
| `ASHLAR_LOG_JSON` (or `Ashlar:Logging:Json` / `Ashlar__Logging__Json`) | `1` / `true` switches the console logger to `AddJsonConsole` (one JSON object per line: `Timestamp` (UTC, ISO-8601), `EventId`, `LogLevel`, `Category`, `Message`, `State`, scopes). Applies to `Ashlar.API` and the CLI daemon; other CLI commands keep plain console output | off (plain console) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Standard OTel variable. When set, `Ashlar.API` calls `AddAshlarOpenTelemetry` and exports **traces** (ASP.NET Core + HttpClient instrumentation) and **metrics** (the `Ashlar` meter plus ASP.NET Core / HttpClient) over OTLP; `IMetricsCollector` becomes `OpenTelemetryMetricsCollector`. An unreachable collector never fails startup — the exporter batches in the background and logs failures via OTel self-diagnostics | unset (no export, in-process `MemoryMetricsCollector`) |
| `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_EXPORTER_OTLP_TIMEOUT`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES` | Honoured by the OpenTelemetry SDK as usual once export is on. `OTEL_SERVICE_NAME` defaults to `Ashlar.API` when unset | SDK defaults (`grpc`, 10 s, `Ashlar.API`) |

Metric shape over OTLP: `IMetricsCollector` keys (for example `ncr.model_load.success`, `ncr.ollama.chat.duration`) are **not** individual OTLP instruments; they arrive as attribute values on two instruments from the `Ashlar` meter — `ashlar.operation.duration` (histogram, ms, attribute `operation`) and `ashlar.operation.count` (counter, attribute `counter`). See `docs/NcrReleaseSLOs.md` for how the SLO names map, and `docs/DEPLOYMENT.md` § Observability for compose usage.

## Remote execution surface (`Ashlar__Execution__*`)

`POST /api/execution/build` and `POST /api/execution/run` let a `RemoteExecutionPlatform` caller (`ASHLAR_EXECUTION_REMOTE_URL`) build images and run containers on this host's Docker daemon, including host bind mounts. They are **not mapped** unless opted in, and refuse `AuthorizationMode=None` (403) even when opted in.

| Variable / config key | Description | Default |
|------------------------|-------------|---------|
| `Ashlar__Execution__ServeRemoteExecution` | `true` maps `/api/execution/build` and `/api/execution/run`. Requires a built-in `Ashlar__Security__AuthorizationMode` other than `None`; otherwise every request is refused with 403 | `false` (routes return 404) |
| `Ashlar__Execution__AllowedVolumeMountRoot` | Single host directory under which `VolumeMounts` host paths on `/api/execution/run` are accepted (the remote caller mounts only its test-results directory). Unset = any request carrying volume mounts is rejected with 400 | unset |

## Mesh and brick HTTP hardening (`Ashlar__Security__Mesh__*`, Phase 2)

Optional middleware runs **before** built-in API auth. It applies to **`/api/mesh/*`** and **`POST /api/bricks/*/execute`**. When all options are unset or zero, behavior matches previous releases (no extra mesh checks).

| Variable / config key | Description | Default |
|------------------------|-------------|---------|
| `Ashlar__Security__Mesh__MeshMutatingToken` | When set, **POST/PATCH/DELETE** under `/api/mesh` must send this exact value in the mesh token header | unset |
| `Ashlar__Security__Mesh__MeshTokenHeaderName` | Header for mesh mutating token | `X-Ashlar-Mesh-Token` |
| `Ashlar__Security__Mesh__BrickExecuteToken` | When set, brick execute requires this value in the brick header only | unset |
| `Ashlar__Security__Mesh__BrickExecuteTokenHeaderName` | Header for brick execute token | `X-Ashlar-Brick-Execute-Token` |
| `Ashlar__Security__Mesh__MaxJsonBodyBytes` | Reject POST/PUT/PATCH when `Content-Length` exceeds this (0 = off) | `524288` |
| `Ashlar__Security__Mesh__RateLimitPermitLimit` | Max mutating requests per client IP per window for mesh + brick execute (0 = off) | `120` |
| `Ashlar__Security__Mesh__RateLimitWindowSeconds` | Window length in seconds | `60` |

When **`BrickExecuteToken`** is unset but **`MeshMutatingToken`** is set, brick execute accepts the mesh secret in **`BrickExecuteTokenHeaderName`** *or* **`MeshTokenHeaderName`**.

Combine with **`Ashlar__Security__AuthorizationMode`** and TLS termination for production meshes. See **`docs/MeshPhase2TransportAndAuth.md`**.

## Mesh correlation header (Phase 3)

For **`/api/mesh/*`** and **`POST /api/bricks/*/execute`**, the API assigns or echoes **`X-Ashlar-Correlation-Id`** (see [MeshPhase3DistributedExecution.md](MeshPhase3DistributedExecution.md)). Clients may send their own correlation id to align logs across hops.

## Mesh knowledge sync (`Ashlar__Mesh__KnowledgeSync__*`, Phase 4)

Only active when **`AddAshlar`** registers adaptation (Full/Server/AirGapped profiles with pattern store). Binds section **`Ashlar:Mesh:KnowledgeSync`**.

| Variable / config key | Description | Default |
|------------------------|-------------|---------|
| `Ashlar__Mesh__KnowledgeSync__Enabled` | `true` to run periodic peer pull | `false` |
| `Ashlar__Mesh__KnowledgeSync__PeerBaseUrls__0` | First peer API base URL (https, trailing slash optional) | unset |
| `Ashlar__Mesh__KnowledgeSync__IntervalMinutes` | Minutes between pull rounds | `15` |
| `Ashlar__Mesh__KnowledgeSync__SinceLookbackMultiplier` | `since = now - interval * multiplier` for export window | `2` |
| `Ashlar__Mesh__KnowledgeSync__MaxAdaptations` | Cap per export GET | `500` |
| `Ashlar__Mesh__KnowledgeSync__MaxPatterns` | Cap per export GET | `500` |

See [MeshPhase4KnowledgeSync.md](MeshPhase4KnowledgeSync.md).

## Mesh elastic scheduling (`Ashlar__Mesh__Elastic__*`, Phase 5)

| Variable / config key | Description | Default |
|------------------------|-------------|---------|
| `Ashlar__Mesh__Elastic__Enabled` | `true` to run periodic re-placement for stale **Pending** tasks | `false` |
| `Ashlar__Mesh__Elastic__IntervalMinutes` | Minutes between rebalancer rounds | `2` |
| `Ashlar__Mesh__Elastic__PendingStaleSeconds` | Pending tasks older than this get `TryScheduleAsync` again | `120` |

Workers should POST heartbeat with **`queueDepth`** (local backlog) so placement prefers idle nodes. See [MeshPhase5ElasticScheduling.md](MeshPhase5ElasticScheduling.md).

## Mesh execution leases (`Ashlar__Mesh__Checkpoint__*`, Phase 6)

Binds section **`Ashlar:Mesh:Checkpoint`**. See [MeshPhase6LeasesAndCheckpoints.md](MeshPhase6LeasesAndCheckpoints.md).

| Variable / config key | Description | Default |
|------------------------|-------------|---------|
| `Ashlar__Mesh__Checkpoint__LeaseSeconds` | Default lease duration after assignment (seconds) when schedule body omits **`leaseSeconds`** | `1800` |
| `Ashlar__Mesh__Checkpoint__SweepEnabled` | `true` to periodically move **Assigned**/**Running** tasks with expired leases to **Pending** | `false` |
| `Ashlar__Mesh__Checkpoint__SweepIntervalMinutes` | Minutes between sweep passes | `1` |

## Mesh director persistence (`Ashlar__Mesh__Persistence__*`, Phase 9)

Binds section **`Ashlar:Mesh:Persistence`**. See [MeshPhase9DirectorPersistence.md](MeshPhase9DirectorPersistence.md).

| Variable / config key | Description | Default |
|------------------------|-------------|---------|
| `Ashlar__Mesh__Persistence__Provider` | `InMemory` or `LiteDb` | `InMemory` |
| `Ashlar__Mesh__Persistence__DatabasePath` | LiteDB file path when provider is LiteDb | `mesh-director.db` |

## Mesh director CLI (`ASHLAR_MESH_*`, Phase 7)

Used by **commercial mesh director CLI** (`dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director ...`) when a worker or script talks to a remote **`Ashlar.API`** mesh control plane. See **`docs/MeshPhase7EdgeAlignment.md`**. Discovery-related `ASHLAR_MESH_*` values also appear under **Core** above; this section summarizes hub HTTP access from the CLI.

| Variable | Description |
|----------|-------------|
| `ASHLAR_MESH_DIRECTOR_BASE_URL` | Director base URL (e.g. `https://hub:8080`) |
| `ASHLAR_MESH_API_KEY` | Optional **`X-Ashlar-Api-Key`** when the hub enforces API key auth |
| `ASHLAR_MESH_MUTATING_TOKEN` | Optional **`X-Ashlar-Mesh-Token`** for mutating **`/api/mesh`** requests |
| `ASHLAR_MESH_PEER_REGISTRATION_KEY` | Per-peer registration secret for **commercial mesh director CLI `register`** |

## Pipelines (`ASHLAR_PIPELINE_*`)

Pipeline options resolve in this order: defaults, config (`Ashlar:Pipelines:*`), then environment variables.

| Variable | Description | Default |
|----------|-------------|---------|
| `ASHLAR_PIPELINE_MAX_RETRIES` | Max stage retry attempts before failure | `3` |
| `ASHLAR_PIPELINE_RETRY_DELAY_MS` | Delay between retries in milliseconds | `100` |
| `ASHLAR_PIPELINE_RESUME_FAILED` | `1`/`true` to resume failed stages by default | `false` |
| `ASHLAR_PIPELINE_ALLOW_MISSING_RESUME_SOURCE` | `1`/`true` to continue when source run is missing | `false` |
| `ASHLAR_PIPELINE_ENABLE_TEST_HOOKS` | Enables deterministic failure/test hooks for gate scenarios | `false` |
| `ASHLAR_PIPELINE_COMPLETION_POLICY` | Completion policy override (for example `AllowNonCriticalStageFailures`) | `Strict` |
| `ASHLAR_PIPELINE_STORE_PROVIDER` | Pipeline run store provider (for example `LiteDb`) | in-memory |
| `ASHLAR_PIPELINE_STORE_PATH` | Store path when using file-backed providers | unset |
| `ASHLAR_PIPELINE_DETERMINISTIC_ADAPTER` | Override deterministic adapter identifier | framework default |
| `ASHLAR_PIPELINE_AGENTIC_ADAPTER` | Override agentic adapter identifier | framework default |

## OpenAI

| Variable | Description | Default |
|----------|-------------|---------|
| `OPENAI_API_KEY` | API key | required for `openai` provider |
| `OPENAI_MODEL` | Model name | `gpt-4o-mini` |
| `OPENAI_BASE_URL` | Chat completions URL or API root (`https://api.openai.com`, `https://api.openai.com/v1`, or full `.../v1/chat/completions`); normalized to `POST .../v1/chat/completions` | `https://api.openai.com/v1/chat/completions` |
| `OPENAI_VISION_MODEL` | Vision model | `OPENAI_MODEL` |

## OpenAI-compatible (`openai_compat` provider)

Use **`openai_compat`** when the backend implements OpenAI-style **`POST /v1/chat/completions`** with **`Authorization: Bearer`** (vLLM, LiteLLM, llama.cpp server, etc.). Separate from **`openai`** so local gateways do not overwrite official OpenAI env vars.

| Variable | Description | Default |
|----------|-------------|---------|
| `OPENAI_COMPAT_API_KEY` | Bearer token (required for this provider; use a placeholder if the server ignores auth) | unset |
| `OPENAI_COMPAT_BASE_URL` | Origin (`http://127.0.0.1:8000`), API root (`.../v1`), or full completions URL; normalized like `OPENAI_BASE_URL` | unset |
| `OPENAI_COMPAT_MODEL` | Text model id | `default` (see `AshlarDefaults.OpenAiCompatDefaultModel`) |
| `OPENAI_COMPAT_VISION_MODEL` | Vision model when `OPENAI_COMPAT_MODEL` is not suitable for image input | `OPENAI_COMPAT_MODEL` |

Per-request **`config.model`** overrides the vision model when set.

## Azure OpenAI

| Variable | Description | Default |
|----------|-------------|---------|
| `AZURE_OPENAI_ENDPOINT` | Endpoint URL | required |
| `AZURE_OPENAI_API_KEY` | API key | required |
| `AZURE_OPENAI_DEPLOYMENT` | Deployment name | required |
| `AZURE_OPENAI_API_VERSION` | API version | `2024-06-01` |

## Ollama

Three code paths reach Ollama — the default MEAI model path, the NCR serving backend, and the legacy provider factory — and each has its own key family. They all resolve in the same order, so setting the legacy pair alone is enough on a single host; inside Compose set all three (the shipped stacks do) so no path can fall back to `localhost:11434`, which is the container itself.

**Resolution order** (first non-empty value wins): `ASHLAR_OLLAMA_BASE_URL` / `ASHLAR_OLLAMA_MODEL` env → the path's own config key (`Ashlar:Meai:*` or `Ashlar:NodeCapabilityRuntime:Ollama:BaseUrl`) → legacy `OLLAMA_BASE_URL` / `OLLAMA_MODEL` env → the path's default (`http://localhost:11434` / `llama3.1:latest`; NCR uses `http://127.0.0.1:11434`). Blank values are treated as unset.

| Variable / Key | Path | Description | Default |
|----------------|------|-------------|---------|
| `ASHLAR_USE_MEAI_PIPELINE` (`Ashlar:UseMeaiPipeline`) | selector | `0`/`false` opts out of the default Microsoft.Extensions.AI (MEAI) model path and uses the legacy provider factory instead. Default: on. | on |
| `ASHLAR_OLLAMA_BASE_URL` | MEAI + NCR | Highest-precedence Ollama base URL override. | unset |
| `ASHLAR_OLLAMA_MODEL` | MEAI | Highest-precedence Ollama model override. | unset |
| `Ashlar:Meai:OllamaBaseUrl` (`Ashlar__Meai__OllamaBaseUrl`) | MEAI | Base URL for the default `IModel` / `IChatClient` path (`local:ollama` target). | falls through |
| `Ashlar:Meai:OllamaModel` (`Ashlar__Meai__OllamaModel`) | MEAI | Model for the default `IModel` / `IChatClient` path. | falls through |
| `Ashlar:NodeCapabilityRuntime:Ollama:BaseUrl` (`Ashlar__NodeCapabilityRuntime__Ollama__BaseUrl`) | NCR | Base URL for the NCR serving backend, startup health probe and `/api/tags` model catalog (see below). | falls through |
| `OLLAMA_BASE_URL` | all (legacy) | Base URL. The provider-factory path (`provider=ollama`, `/api/onboarding/status`, IDE endpoints) reads **only** this family; MEAI and NCR fall back to it. | `http://localhost:11434` |
| `OLLAMA_MODEL` | all (legacy) | Text model. | `llama3.1:latest` |
| `OLLAMA_VISION_MODEL` | provider factory | Vision model | `richardyoung/smolvlm2-2.2b-instruct` |
| `OLLAMA_TIMEOUT_SECONDS` | provider factory | Request timeout | `300` |

`GET /api/onboarding/status` reports `meaiOllamaBaseUrl` / `meaiOllamaModel` (what the default model path will dial) next to the provider-factory `ollama` availability, and `scripts/prod-dry-run.sh` fails when the former is a loopback address inside Compose.

**Docker (models in containers):** `docker compose -f deploy/compose/docker-compose.ollama.yml up -d`, then `scripts/run-ollama-docker.ps1` / `scripts/run-ollama-docker.sh` to pull a tag. **Host-run Ashlar.API with Ollama in Docker (all platforms):** `scripts/start-ashlar-api-dev.ps1` or `scripts/start-ashlar-api-dev.sh` (waits for Ollama, sets `OLLAMA_*` + NCR URL, runs `dotnet run`). Use `-Pull` / `--pull` when the model is not yet local. **Phone / another device on the same LAN:** `-ListenLan` / `--listen-lan` binds `http://0.0.0.0:<port>`; browse `http://<host-LAN-IP>:8080` and allow the port in the host firewall. Default bind is loopback-only (`127.0.0.1`). Stop: `scripts/stop-ashlar-api-dev.ps1` / `.sh`.

### Node Capability Runtime (NCR) Ollama

Desktop NCR uses its own options-bound Ollama endpoint for model serving.

| Key / Variable | Description | Default |
|----------------|-------------|---------|
| `Ashlar:NodeCapabilityRuntime:Ollama:BaseUrl` (`Ashlar__NodeCapabilityRuntime__Ollama__BaseUrl`) | NCR Ollama backend base URL used by desktop policy registrations. Overridden by `ASHLAR_OLLAMA_BASE_URL`; falls back to legacy `OLLAMA_BASE_URL` when unset (see resolution order above). | `http://127.0.0.1:11434` |

Behavior notes:
- On startup, NCR runs a health probe against the configured Ollama backend and logs a degraded warning if unreachable.
- A degraded startup does not crash the host; agentic tasks may escalate until Ollama becomes reachable.
- NCR records metrics for model resolution, model load, and Ollama endpoint latencies/error rates via `IMetricsCollector` keys under `ncr.*`.

### NCR Capability Freshness

Remote brick catalogs now use an in-memory stale capability snapshot fallback:
- Fresh `/api/capabilities` responses are cached per remote base URL.
- If a later capability fetch fails, the last known manifest is reused and marked stale internally.
- Consumers should treat stale manifests as routing hints (not hard guarantees), and retry capability refresh periodically.
- `Ashlar:Execution:RemoteCapabilities:MaxStaleAge` (`Ashlar__Execution__RemoteCapabilities__MaxStaleAge`) bounds stale fallback age (default `00:10:00`). If stale data exceeds this age, fallback is rejected.

### NCR Telemetry SLO Suggestions (v1)

Suggested starting SLOs/alerts using `ncr.*` metrics (these are `IMetricsCollector` keys; over OTLP they appear as `operation` / `counter` attribute values on `ashlar.operation.duration` / `ashlar.operation.count` — see "Observability" above and `docs/NcrReleaseSLOs.md`):
- `ncr.model_resolution.target.Escalate`: alert if escalation ratio > 20% over 15 minutes for user-facing workloads.
- `ncr.model_load.error` and `ncr.ollama.*.error`: alert on sustained non-zero error rate over 5 minutes.
- `ncr.ollama.chat.duration`: track p95/p99; alert if p95 exceeds your interactive budget for 10+ minutes.
- `ncr.profile.constraint_change`: watch for bursty spikes that correlate with thermal/memory pressure and escalation increases.

Operational guidance:
- Treat stale capability fallback as degraded mode; prefer conservative routing and periodic refresh attempts.
- Set `ASHLAR_OBSERVATION_FAIL_OPEN=1` for production-style hosts that must continue serving even if observation store permissions are restricted.

## Video (SmolVLM2)

| Variable | Description | Default |
|----------|-------------|---------|
| `VIDEO_SERVICE_URL` | Video analysis service URL | required for `video` provider |

## Trust & Audit

| Variable | Description | Default |
|----------|-------------|---------|
| `ASHLAR_TRUST_AUDIT_DB` | Path to LiteDB audit log | in-memory |
| `ASHLAR_KNOWLEDGE_LOG_PATH` | Path to user knowledge log | in-memory |
| `ASHLAR_ACCESS_BOUNDARY_CONFIG` | Path to access boundary JSON | unset |
| `ASHLAR_TRUST_POLICY_PACKS_PATH` | Directory containing trust policy pack JSON files | `config/trust-packs` (repo root) |
| `ASHLAR_ACTIVE_TRUST_POLICY_PACK_PATH` | Path to active pack selection file | `active-pack.json` in packs dir |

## Mesh

For a **layered breakdown** of mesh capabilities (identity, registry, transport, trust, request/fulfill, sync, WAN gaps), see [`MeshAgentSetupCapabilityBreakdown.md`](./MeshAgentSetupCapabilityBreakdown.md).

| Variable | Description | Default |
|----------|-------------|---------|
| `ASHLAR_MESH_PEER_ID` | Mesh peer identifier | random GUID |
| `ASHLAR_MESH_INSTANCES_PATH` | Path to file-based mesh instance registry | unset |
| `ASHLAR_TRUSTED_PEER_IDS` | Comma-separated peer IDs trusted for execution (mesh + RunPod/peer capability routing) | unset (all peers trusted) |
| `ASHLAR_UNTRUSTED_PEER_IDS` | Comma-separated peer IDs blocked from execution | unset |
| `ASHLAR_MESH_TRUST_POLICY` | Same variable as under **Core**: `any`, `allowlist`, `trusted-only`, `trusted-preferred` (unknown → `trusted-preferred`). There is no `open`/`denylist` value; use the peer-id lists above for that | unset (`any` for discovery, `trusted-preferred` for capability requests) |
| `ASHLAR_PEER_TRUST_POLICY` | Fallback for `ASHLAR_MESH_TRUST_POLICY` when that is unset; same values | unset |
| `ASHLAR_SHARED_ADAPTATIONS_PATH` | Path for shared adaptation artifacts across mesh | unset |

## RunPod + Capability Routing (`Ashlar:RunPod:*`)

Generation execution routing uses NCR + peer network + RunPod cloud. These options are bound from `Ashlar:RunPod:*` and can be set with environment variables (`__` separator).

| Key / Variable | Description | Default |
|----------------|-------------|---------|
| `Ashlar:RunPod:ApiKey` (`Ashlar__RunPod__ApiKey`) | RunPod API key | empty |
| `Ashlar:RunPod:BaseUrl` (`Ashlar__RunPod__BaseUrl`) | RunPod API base URL | `https://api.runpod.io` |
| `Ashlar:RunPod:PreferredGpuTier` (`Ashlar__RunPod__PreferredGpuTier`) | Preferred GPU tier for cloud jobs | `NVIDIA_A4000` |
| `Ashlar:RunPod:Timeout` (`Ashlar__RunPod__Timeout`) | Max remote job execution duration before timeout/teardown | `00:10:00` |
| `Ashlar:RunPod:PollingInterval` (`Ashlar__RunPod__PollingInterval`) | RunPod status polling interval | `00:00:02` |
| `Ashlar:RunPod:OutputStagingPath` (`Ashlar__RunPod__OutputStagingPath`) | Staged output path for remote artifacts | temp path (`ashlar-runpod`) |
| `Ashlar:RunPod:QueueDepthThreshold` (`Ashlar__RunPod__QueueDepthThreshold`) | Local queue threshold before remote routing | `4` |
| `Ashlar:RunPod:EnablePeerNetworkRouting` (`Ashlar__RunPod__EnablePeerNetworkRouting`) | Enables routing to peer Ashlar nodes | `false` |
| `Ashlar:RunPod:PreferPeerNetworkOverCloud` (`Ashlar__RunPod__PreferPeerNetworkOverCloud`) | System default preference when remote routing is required | `true` |
| `Ashlar:RunPod:PeerCapabilityId` (`Ashlar__RunPod__PeerCapabilityId`) | Capability identifier required for peer eligibility | `generation.capability-routing` |
| `Ashlar:RunPod:PeerRoutingBrickId` (`Ashlar__RunPod__PeerRoutingBrickId`) | Brick id invoked on peer nodes | `generation.capability-routing` |
| `Ashlar:RunPod:PeerRequestTimeout` (`Ashlar__RunPod__PeerRequestTimeout`) | Per-peer request timeout before failover | `00:00:30` |
| `Ashlar:RunPod:PeerDiscoveryInterval` (`Ashlar__RunPod__PeerDiscoveryInterval`) | Peer capability snapshot refresh interval | `00:00:10` |

Routing behavior:
- `CapabilityRoutingBrick` is the default generation entry point.
- `RemoteExecutionPreference` (job-level) can force or prefer peer/cloud routing (`UseSystemDefault`, `CloudOnly`, `PreferPeerNetwork`, `PeerNetworkOnly`).
- Peer execution includes candidate ranking, timeout handling, and failover across eligible peers.

See `docs/runtime/ExecutionRouting.md` for detailed execution flow and resilience behavior.

## Ephemeral Execution

| Variable | Description | Default |
|----------|-------------|---------|
| `ASHLAR_EPHEMERAL` | `1` = enable ephemeral models (Ollama in container) when supported | unset |
| `ASHLAR_EPHEMERAL_MODELS` | `1` = use ephemeral Ollama container for LLM; container removed when session ends | unset |
| `ASHLAR_EPHEMERAL_DB` | `postgres` = use ephemeral Postgres container for workflows/tests | unset |
| `ASHLAR_TEST_EPHEMERAL` | `1` = run tests in ephemeral containers (no volume mounts) | unset |

## Artifact Cleanup

| Variable | Description | Default |
|----------|-------------|---------|
| `ASHLAR_CLEAN_BEFORE_TEST` | `1` = run test-artifacts cleanup before `ashlar test local` | unset |
| `ASHLAR_CLEAN_AFTER_TEST` | `1` = run test-artifacts cleanup after `ashlar test local` | unset |
| `ASHLAR_ARTIFACT_CLEANUP_REPO_ROOT` | Repo root for cleanup; unset = auto-detect | unset |
| `ASHLAR_INCOMPLETE_BLOB_PATH` | Path to content-addressed blob storage for `incomplete-blobs` strategy | unset |
| `ASHLAR_BLOB_LIFECYCLE` | `docker` = pause Docker Desktop before incomplete-blob cleanup | unset |

## Background Agents

| Variable | Description | Default |
|----------|-------------|---------|
| `ASHLAR_BACKGROUND_AGENTS_CONFIG` | Path to background agent set JSON configuration | unset |
| `ASHLAR_AGENT_MODE_PATH` | Path to the file-based aggressiveness mode store (`{"Mode":"passive"|"semi-active"|"active"|"ambient"}`). This file is what ARMS the extender: missing file, unreadable JSON, `{}` or an unknown value all read as **Passive** (observe only, fail-closed); the effective mode is logged when it changes | `~/.ashlar/agent-mode.json` |
| `ASHLAR_OBJECTIVES_ROOT` | Objective store root (`{status}/{id}.md` + witness/proposal siblings); read by `AddBackgroundAgents` and the Runtime Studio path resolver | `<cwd>/.ashlar/runtime-studio/objectives` |
| `ASHLAR_FORGE_ROOT` | Forge change-proposal queue root | `<cwd>/.ashlar/runtime-studio/forge` |
| `ASHLAR_OBSERVATIONS_PATH` | Path to the shared `observations.jsonl` | `<cwd>/.ashlar/runtime-studio/observations.jsonl` |
| `ASHLAR_CYCLE_EVENTS_PATH` | Path to `cycles.jsonl` (absolute or cwd-relative) | `<cwd>/.ashlar/runtime-studio/cycles.jsonl` |
| `ASHLAR_DASHBOARD_AUTH_TOKEN` | Shared secret for `ashlar background-agent dashboard` (same as `--auth-token`); when set, requests need `?token=` or a Bearer header | unset (dashboard binds `127.0.0.1` only) |
| `ASHLAR_SANDBOX_ROOT` | Sandbox root for confined tool paths (`PathAllowlist`, forge propose-change) when the world snapshot carries no `SandboxRoot` | unset |
| `ASHLAR_PATH_ALLOWLIST_EXTRA` | Comma/semicolon-separated extra path prefixes appended to the confined toolbox allowlist. Widening only — the default allowlist cannot be narrowed from here | unset |
| `ASHLAR_EXTENSION_MAX_LINEAGE_DEPTH` | Extender ceiling (SX-AUDIT invariant D): max `ParentId` hops below a human-authored root an extender may sit and still extend. May only LOWER the built-in default. | 1 |
| `ASHLAR_EXTENSION_MAX_UNATTENDED_CYCLES` | Extender ceiling: extend cycles since a human last armed the agent before it holds (re-arm: restart or `RearmExtension`). May only LOWER the default. | 8 |
| `ASHLAR_EXTENSION_MAX_CYCLES_PER_HOUR` | Extender ceiling: extend cycles in any trailing hour. May only LOWER the default. | 4 |
| `ASHLAR_OBSERVATION_DEGRADED_MODE` | `1` = start observation pipeline in degraded mode | unset |
| `ASHLAR_OBSERVATION_FAIL_OPEN` | `1` = observation pipeline continues on store errors | unset |
| `BING_SEARCH_KEY` | API key for Bing web search provider | unset (falls back to mock) |

## Autonomy loop and sessions (`Ashlar:Autonomy:*`)

Bound from the `Ashlar:Autonomy` section by `AddAshlarAutonomy(configuration)` — a host-composed surface: the shipped API/CLI hosts do not call it, so the configuration is whatever the composing host passes (the first-flight spike passes an in-memory set; a host that passes `builder.Configuration` gets `appsettings.json` + `Ashlar__Autonomy__*`). See `AshlarAutonomyOptions` for the full set and `ValidateAshlarAutonomyOptions` for what refuses to boot. Nothing runs unless `Enabled=true`.

**Recommended trio for anything beyond local development:** `UseSandboxSessions=true`, `BuildCandidateInSession=true`, `ExecuteCandidateInSession=true`, with a `SessionImage` (and, once you have read a certificate, its `SessionImageDigest`). All three default to **false** so that a bare `AddAshlarAutonomy()` stays valid — but with `Enabled=true` and the execution leg off, the witness and mutation legs run model-proposed candidate and mutant code **in the host process**; `HoldAdmission` blocks the swap, not the execution. `AddAshlarAutonomy` logs a warning at composition in that state, and the standing loop (`AddAshlarAutonomyLoop`) additionally refuses to hand a proposer any "repair" for the resulting in-process refusal.

| Variable / config key | Description | Default |
|-----------------------|-------------|---------|
| `Ashlar:Autonomy:Enabled` (`Ashlar__Autonomy__Enabled`) | Master switch. Off means the standing loop's timer never starts and validation always passes | `false` |
| `Ashlar:Autonomy:UseSandboxSessions` (`Ashlar__Autonomy__UseSandboxSessions`) | Open an attested container session per iteration. The standing loop builds a `SessionSpec` ONLY when this is true; requires `SessionImage` | `false` |
| `Ashlar:Autonomy:BuildCandidateInSession` (`Ashlar__Autonomy__BuildCandidateInSession`) | Compile the candidate inside the session (`session-build` certificate input); iterations without a session then refuse fail-closed. Requires `UseSandboxSessions` | `false` |
| `Ashlar:Autonomy:ExecuteCandidateInSession` (`Ashlar__Autonomy__ExecuteCandidateInSession`) | Run the witness, determinism and mutation legs inside the session (`session-execution` input) — untrusted candidate code never runs in-process. Requires `BuildCandidateInSession` | `false` |
| `Ashlar:Autonomy:SessionImage` (`Ashlar__Autonomy__SessionImage`) | Container image proposal sessions start from; required when `UseSandboxSessions=true`. Must already be present on the engine — sessions run with `--pull never` and never fetch an image. The standing loop's own `AutonomyLoopSettings.SessionImage` takes precedence when set | unset |
| `Ashlar:Autonomy:SessionImageDigest` (`Ashlar__Autonomy__SessionImageDigest`) | Optional pin on the session image's identity: the engine image ID (`sha256:…`) that `SessionImage` must resolve to — the same value attestation records and certificates carry as their `image-digest` input, so pin by copying it from a certificate you have read. When set, a session whose image resolves to anything else refuses to start (checked before `docker run` and again at attestation); a value not of the form `sha256:…` fails validation at boot | unset (capture only) |
| `Ashlar:Autonomy:HoldAdmission` (`Ashlar__Autonomy__HoldAdmission`) | Certify fully, admit nothing without a human (`CertifiedButHeld` even at Tier 0). Enforced by the harness `AddAshlarAutonomy` composes; the standing loop reports this same value and has no dial of its own | `true` |
| `Ashlar:Autonomy:IterationCeilingSeconds` (`Ashlar__Autonomy__IterationCeilingSeconds`) | Absolute per-iteration wall-clock ceiling. The standing loop's session keepalive is this plus a 60 s margin, so the ceiling — not the container — ends a runaway iteration | `600` |
| `Ashlar:Autonomy:CadenceFloorSeconds`, `RetentionWindow`, `Watch*`, `LineageDemotionThreshold`, `ReaperSweepSeconds`, `DigestIntervalSeconds`, `ThroughputGuardFactor` | Swap cadence, rollback retention, watch-window thresholds, demotion, reaper and digest cadence — see `AshlarAutonomyOptions` | see class |

## Certification record signing (`ASHLAR_CERT_*`)

| Variable | Description | Default |
|----------|-------------|---------|
| `ASHLAR_CERT_DEV_HMAC_KEY` | HMAC key for signing **and verifying** brick and composition certification records (`CertificationRecordSigner`, `CompositionCertificationRecordSigner`, `Ashlar.Certification.Contracts.CertificationRecordSigning`). **Unset means the COMMITTED, PUBLIC dev key** `CertificationRecordSigning.DefaultDevKey`: anyone with the source can forge a record that verifies, so certificates then prove integrity against accident, not against an adversary. Both signers log a warning at construction while the dev key is in effect (`UsesDevKey`). Same-owner cross-project reuse works by sharing this value; cross-organization trust needs the Ed25519 key below or PKI | unset (dev key; warns) |
| `ASHLAR_CERT_ED25519_KEY` | Base64 Ed25519 private key; when set, records are dual-signed and carry the public key. **`CertificationVerifyOptions.Default` and `.Strict` require Ed25519 signatures** (`RequireEd25519Signature = true`) and trust-loop schema v2+ (`MinimumSchemaVersion = 2`), fail-closed; only `.Legacy` accepts HMAC-only records (see limitations 7–8 **CLOSED** 2026-09-06 in `docs/certification-evidence.md`). **Trusted-key pinning exists** via `TrustedEd25519PublicKeys` but is optional; **without pinning, a self-consistent record signed with an attacker's own keypair still verifies** (limitation 1: dev HMAC under committed key remains forgeable). Limitation 9 (composition signer discards explicit key) remains open. Production paths use Strict | unset (HMAC-only) |

## Workload scaling (`Ashlar:WorkloadScaling:*`, `ASHLAR_WORKLOAD_*`)

Kernel options (bound inside `AddAshlar` — environment variables only in the shipped hosts). See [`WorkloadScaling.md`](WorkloadScaling.md).

| Variable / config key | Description | Default |
|-----------------------|-------------|---------|
| `Ashlar:WorkloadScaling:Provider` (`Ashlar__WorkloadScaling__Provider`) | `null`, `kubernetes`/`k8s`, or `compose`/`docker-compose` | `null` (no-op scaler) |
| `Ashlar:WorkloadScaling:Enabled` (`Ashlar__WorkloadScaling__Enabled`) | Enable scaling actions | `true` |
| `ASHLAR_WORKLOAD_SCALER` | Env shortcut that overrides `Provider` after binding | unset |
| `ASHLAR_WORKLOAD_SCALING_ENABLED` | `1`/`true` or `0`/`false` — overrides `Enabled` after binding; any other value leaves it alone | unset |
| `ASHLAR_WORKLOAD_AUTOSCALE` | `1`/`true` starts `ElasticWorkloadAutoscaleService` (`Autoscale.Enabled`) | unset |

## MCP and A2A protocol surfaces (`Ashlar:Mcp:*`, `Ashlar:A2A:*`)

Host-owned options bound by Ashlar.API from `builder.Configuration` (`appsettings.json` and `Ashlar__Mcp__*` / `Ashlar__A2A__*` alike). All four surfaces are **off by default** and expose nothing until enabled AND allowlisted; the agent-server compose passes the four `Enabled` flags through as `false`. Full reference: `docs/architecture/ProtocolIntegration-MCP-A2A.md`.

| Section | Master switch | What else it needs |
|---------|---------------|--------------------|
| `Ashlar:Mcp:Server` (`AshlarMcpServerOptions`) | `Enabled` | `ExposedToolIds` allowlist (empty = zero tools), `ServerName`, `RepoRoot`/`OutputRoot`, `MaxConcurrentToolCalls`, per-tool `ArgumentOverrides` |
| `Ashlar:Mcp:Client` (`AshlarMcpClientOptions`) | `Enabled` | `Servers[]` (`Name`, `Url`, optional `ApiKeyHeader` + `ApiKeyEnvVar` — the secret lives in the named env var, never in config, `AllowedTools`), `ConnectTimeout`, `ToolListRefreshInterval` |
| `Ashlar:A2A:Server` (`AshlarA2AServerOptions`) | `Enabled` | `ExposedAgentIds` allowlist, `ExposeByCoordinationProtocol`, `PublicBaseUrl`, `PrimaryAgentId`, `AllowAnonymousAgentCard`, `DefaultExecutionTimeout` |
| `Ashlar:A2A:Transport` (`A2ATransportOptions`) | `Enabled` | `Endpoints[]` per remote URL prefix (API key env var names) |

## Ashlar.API host paths and license (`ASHLAR_DAILIES_PATH`, `ASHLAR_LICENSE_FILE`)

| Variable | Description | Default |
|----------|-------------|---------|
| `ASHLAR_DAILIES_PATH` | Directory the Director dailies endpoints read (`GET /api/director/dailies`); read from the API's configuration, so the config key `Ashlar:DailiesPath` works too | `<app base directory>/dailies` (`/data/dailies` in the agent-server compose) |
| `ASHLAR_LICENSE_FILE` | Path to the signed private-license JSON; overrides `Ashlar:PrivateLicense:LicenseFilePath`. Relative paths resolve against the content root | unset (falls back to the config key) |

## Barriers (`Ashlar__Barriers__*`)

Bound from the `Ashlar:Barriers` section (`appsettings.json` or `Ashlar__Barriers__*` environment variables).

| Variable | Description | Default |
|----------|-------------|---------|
| `Ashlar__Barriers__Levels__0`, `__1`, … | Ordered barrier levels, lowest (floor) to highest | `public, internal, confidential, restricted` in `Ashlar.API/appsettings.json` |
| `Ashlar__Barriers__RequireExplicitBarrier` | `true` = an agent invocation with no explicit barrier context is refused and surfaces as escalation / `errorCode` `BARRIER_CONTEXT_MISSING` (the response says why `0 agent(s) executed`); `false` = missing context defaults to the floor level and records a `DefaultApplied` audit event | `false` (code default, `Ashlar.API/appsettings.json`, agent-server compose, CLI daemon) |
| `Ashlar__Barriers__HostCeiling` | Highest level this host may process; unset disables the ceiling check | `confidential` in `Ashlar.API/appsettings.json` |

`Ashlar.API` does not register an HTTP barrier-context middleware, so its requests never carry an explicit barrier context: leave `RequireExplicitBarrier` at `false` there, or opt in only from a host that establishes the context itself (`IBarrierContextAccessor.Initialize`, e.g. `ashlar orchestrate --barrier <level>`). `HttpBarrierContextMiddleware` (gated by `ASHLAR_BARRIER_MIDDLEWARE_ENABLED`) exists in `Ashlar.Runtime` but is not wired into any shipped host.

## Routing & Execution

| Variable | Description | Default |
|----------|-------------|---------|
| `ASHLAR_ALLOW_MOCK` | `1` = enable mock/offline/mock-json/echo providers | unset |
| `ASHLAR_LOCAL_MODEL_PATH` | Path to local ONNX/LLamaSharp model for `local` provider | unset |
| `ASHLAR_LOCAL_QUEUE_DEPTH` | Local execution queue depth for routing decisions | unset (auto) |
| `ASHLAR_GPU_COMPUTE_CLASS` | GPU compute class label for NCR capability matching | unset |
| `ASHLAR_LOAD_PREFERENCE` | Default load balancing preference | unset |
| `ASHLAR_EXECUTION_REMOTE_URL` | Remote execution endpoint URL for hosting | unset |

`ASHLAR_LOCAL_MODEL_PATH` alone is not enough to get the `local` provider: the GGUF weights are loaded by `LLamaSharp.Backend.Cpu`, which the `Ashlar.*` libraries reference with `PrivateAssets="all"` so that no consumer inherits its four same-named CPU-variant native payloads (they collide on `dotnet publish -r <rid>` with `NETSDK1152`). A deployable host opts in with its own `<PackageReference Include="LLamaSharp.Backend.Cpu" Version="0.25.0" />` — `application/src/Ashlar.API` and `application/src/Ashlar.CLI` do, which is why the shipped images keep the capability. Missing the opt-in is never a build error, and `IsAvailable()` does not detect it — it only checks that `ASHLAR_LOCAL_MODEL_PATH` names an existing file. With the variable unset (the shipped default) routing simply never selects `local`; with it set but no backend present, the first execution fails at `LLamaWeights.LoadFromFile` and surfaces as `ModelUnavailableException` wrapping the missing native library. See [consumer-template/CONSUMING.md](../consumer-template/CONSUMING.md#local-model-inference-is-opt-in) for the consumer-side snippet, including the CPU-variant prune a RID publish needs.

## Config File

`~/.ashlar/config.json` (or path from `ASHLAR_CONFIG_PATH`):

```json
{
  "provider": "openai",
  "model": "gpt-4o-mini"
}
```

- `provider`: `openai`, `azure`, `ollama`, `local`, `video`, `mock`, `offline`, `mock-json`, `echo` (mock variants require `ASHLAR_ALLOW_MOCK=1`)
- `model`: override for the selected provider
