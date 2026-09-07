# Ashlar Testing Guide

**Strategy (layered proof, coverage ratchets, ProdStyle-first):** [Testing strategy pivot v1](architecture/TestingStrategyPivot-v1.md) · [Tracking checklist](architecture/TestingStrategyTracking-v1.md) · [Review guide](architecture/TestingReviewGuide-v1.md)

## Test Guard Rails (Anti-Hang)

Ashlar uses multiple mechanisms to prevent tests from hanging indefinitely and keep CI/dev loops responsive.

### Timeout Policy

| Scope | blame-hang-timeout | Per-test timeout | Notes |
|-------|--------------------|------------------|-------|
| prime-time | 300s | varies | **`Ashlar.PrimeTime.slnf`** — ProdStyle then full (`make test-prime-time` / `make test-prime-time-full`) |
| prod-style | 120s | varies | **Category=ProdStyle** — Infrastructure-only (`make test-prod-style`) |
| smoke | 30s | — | BaseFrameworkSmokeTests; local `make test` |
| integration | 60s | 15s (Integration tests) | Category=Integration |
| persistence | 60s | — | InMemoryPersistenceTests |
| adaptation | 90s | — | Category=Adaptation (LiteDB, Roslyn, MSBuild) |
| e2e | 90s | — | Category=E2E |
| trust | 60s | — | Trust tests (Infrastructure + BackgroundAgents) |
| full | 120s | — | All Ashlar.Tests.Infrastructure tests |

### Where Timeouts Are Applied

- **Makefile `make test`**: `--blame-hang-timeout 30s --blame-hang-dump-type none` (avoids 6GB+ hang dumps)
- **Makefile `test-all-platforms`**: Local 30s; Docker runs 60s
- **CI (cross-platform-tests.yml)**: Per-scope as above
- **TestCommandRunner**: 10 min global timeout for `ashlar test local`
- **TestRunnerAdapter**: 480s per-test hang net for TestBase-based tests (sized to survive coverlet-instrumented CI runs; see `docs/production-readiness/KernelCoverageGate-Findings.md`)
- **MultiPlatformTestCommand**: 5 min for native platform runs
- **WorkflowExecutorIntegrationTests**: `[Fact(Timeout = 15000)]` on each test

### Adding New Integration Tests

1. Add `[Collection("Integration")]` and `[Trait("Category", "Integration")]` to the test class when appropriate.
2. For suites that exercise production DI graphs or **`Host`** wiring, also add **`[Trait("Category", "ProdStyle")]`** so they participate in **`make test-prod-style`** / **`ashlar ci verify`** ordering.
3. Add `[Fact(Timeout = 15000)]` (or appropriate value) to async/I/O tests
4. Integration tests run sequentially (DisableParallelization) to avoid file watcher and temp dir contention

## Running Tests

### Kernel gate (pre-application)

Run before building product features on top of the kernel. Builds `Ashlar.Runtime.sln`, runs hosting phase/profile resolution tests, and pipeline tests.

```bash
make kernel-gate              # Tier A: runtime build + hosting matrix + pipeline lifecycle
make kernel-gate-tier-b       # Tier B: CLI validate/run/fallback + LiteDB cross-process resume
make kernel-gate-tier-c       # Tier C: ProdStyle + workflow + gRPC transport + air-gapped profile
make kernel-gate-tier-d       # Tier D: pack graph alignment + NuGet consumer sample
make kernel-gate-full         # Tier A + B + C + D
make bootstrap-mesh-lab-env   # create .env.mesh-lab from example (gitignored)
KERNEL_GATE_MESH_E2E=1 make kernel-gate-tier-c   # adds ~2min Docker mesh E2E
make mesh-lab-e2e .env.mesh-lab                   # or run mesh E2E directly
# Skip tiers on full run:
make kernel-gate-tier-e       # Tier E: OpenTelemetry + perf tests + prod Compose dry run
KERNEL_GATE_CHAOS_LITE=1 make kernel-gate-tier-e   # optional mesh network-negative
KERNEL_GATE_SKIP_TIER_E=1 make kernel-gate-full   # skip Docker prod dry run
```

See **`docs/production-readiness/KernelHardeningPlan-v1.md`**, **`docs/architecture/KernelPhaseMatrix.md`**, and track sign-off in **`docs/production-readiness/KernelReadiness-v1.md`**.

### Application gate (after kernel)

Validates `application/Ashlar.Application.sln` (CLI, API, optional agent-server Compose). Assumes `make kernel-gate-full` has passed (or set `APPLICATION_GATE_REQUIRE_KERNEL=1` on full run).

```bash
make application-gate-tier-a    # build product sln + CLI smoke (runs kernel-gate unless APPLICATION_GATE_SKIP_KERNEL=1)
make application-gate-tier-b    # focused CLI tests + doctor --json
make application-gate-tier-c    # in-process API WebApplicationFactory tests
make application-gate-tier-d    # agent-server prod dry run (Docker)
make application-gate-full      # A–D (skips re-running kernel by default)
APPLICATION_GATE_SKIP_TIER_D=1 make application-gate-full   # skip Docker agent-server
```

See **`docs/production-readiness/ApplicationHardeningPlan-v1.md`** and **`docs/production-readiness/ApplicationReadiness-v1.md`**.

### Products gate (extractable scaffolds)

Path-filtered advisory workflow (`.github/workflows/products-gate.yml`). Run the same commands locally when you change `products/**` or `src/Ashlar.Contracts/Distributed/**`:

```bash
dotnet test products/Ashlar.Products.sln
dotnet test src/Ashlar.Tests.Contracts/Ashlar.Tests.Contracts.csproj \
  --filter FullyQualifiedName~DistributedContractTests
```

See [`architecture/product-split.md`](architecture/product-split.md) and [`../products/README.md`](../products/README.md). `dependency-boundary` (not this gate) enforces cloud → kernel `ProjectReference` refusal.

### Composition & mesh gate

Pipeline composition (fan-out/fan-in, agentic stages) and async clustered mesh tasks:

```bash
make composition-mesh-gate-tier-a    # pipeline validator/decomposer/orchestrator/lifecycle
make composition-mesh-gate-tier-b    # CLI pipeline + mesh command suites
make composition-mesh-gate-tier-c    # mesh fleet placement/execution (in-process)
make composition-mesh-gate-tier-d    # Docker mesh lab with workers (schedule→placement)
make composition-mesh-gate-full
COMPOSITION_MESH_GATE_SKIP_TIER_D=1 make composition-mesh-gate-full   # in-process only
COMPOSITION_MESH_GATE_STRESS=1 make composition-mesh-gate-full        # workers + stress ramp
```

See **`docs/production-readiness/CompositionMeshHardeningPlan-v1.md`**.

### Ship gate

Production CLI flows, `ci verify`, release preflight, release bundle:

```bash
make ship-gate-full
SHIP_GATE_SKIP_TIER_B=1 make ship-gate-full   # skip heavy ProdStyle ci verify
```

See **`docs/production-readiness/ShipHardeningPlan-v1.md`**.

### Ops & dogfood gate

Self-improvement dogfood blocks, optional mesh chaos, oh-shit demo:

```bash
make ops-gate-full
OPS_GATE_MESH_DEEP=1 make ops-gate-tier-d    # mesh checkpoint/migrate E2E
make ashlar-ready-gate                         # full stack
ASHLAR_READY_SKIP_DOCKER=1 make ashlar-ready-gate   # skip Docker tiers (~faster)
```

See **`docs/production-readiness/OpsHardeningPlan-v1.md`**.

### Security & trust gate

Trust boundary, API auth, mesh security, supply chain, air-gapped:

```bash
make security-gate-tier-a    # trust core + audit + policy packs
make security-gate-tier-b    # API security middleware
make security-gate-tier-c    # trust CLI surfaces
make security-gate-tier-d    # dotnet list package --vulnerable / --deprecated (artifacts in .ashlar/security-gate/)
make security-gate-tier-e    # air-gapped + safety
make security-gate-full
SECURITY_GATE_STRICT_SUPPLY_CHAIN=1 make security-gate-tier-d
SECURITY_GATE_AIRGAPPED_CONTAINER=1 make security-gate-tier-e
```

See **`docs/production-readiness/SecurityHardeningPlan-v1.md`**.

**Prime-time (whole automated framework slice):**  

```bash
make test-prime-time          # Category=ProdStyle across Ashlar.PrimeTime.slnf (seven test projects)
make test-prime-time-full    # ProdStyle gate then full slice excluding Category=ProdStyle
```

**Faster Infrastructure-only ProdStyle:** `make test-prod-style`

**Production-shaped containers (Linux dry run):** `make prod-dry-run` or `make prod-dry-run-agent-server` — see **`docs/prod-dry-run.md`** (Compose + published API image, `/health` + `/api/status`).

**Multi-node mesh (Docker bridge):** `make mesh-lab-e2e` or `make mesh-lab-e2e-workers` — see **`docs/MeshVirtualLab.md`** (peer-a / peer-b / optional worker, scripted HTTP + mesh API checks). Deep checkpoint/migrate: `make mesh-lab-verify-deep` or `MESH_LAB_VERIFY_DEEP=1 make mesh-lab-e2e-workers`. Network failure modes: **`mesh-lab-verify-network-negative.sh`** (included in standard verify); see **`docs/MeshPhase11NetworkNegative.md`**.

**Same checks via dotnet (optional):** `make test-mesh-lab` or `ASHLAR_RUN_MESH_LAB=1 dotnet test … --filter Category=MeshLab` — runs **`MeshLabDockerE2ETests`** (Compose + `mesh-lab-verify.sh` / deep script). Skipped by default; set **`ASHLAR_MESH_LAB_SKIP_DEEP=1`** for standard verify only.

**Stress (workers scale + health bursts):** `make mesh-lab-e2e-stress` locally (verify + deep + ramp + post-stress director checks); CI via **`mesh-lab-stress-gate.yml`** (`workflow_dispatch` only; the weekly schedule was removed 2026-08-16). See **`docs/MeshPhase10LabStressHardening.md`**.

**Entitlements (CopilotScoped + hourly quota):** included in `make mesh-lab-verify` when workers are up; standalone `make mesh-lab-verify-entitlements` (requires `Ashlar__Security__CopilotScopedApiKey` in `.env.mesh-lab`).

`ashlar` command note:
- Commands shown as `ashlar ...` assume the CLI tool is installed on your PATH.
- If you have not installed the global tool, use the equivalent project invocation:
  - `dotnet run --project application/src/Ashlar.CLI -- <subcommand>`
- Example:
  - `ashlar validate`
  - `dotnet run --project application/src/Ashlar.CLI -- validate`

### What CI proves (run these first)

No pull-request lane runs `dotnet test Ashlar.sln`. The PR-triggered workflows run these exact commands; reproduce them locally instead of the whole solution:

```bash
bash scripts/ci/kernel-coverage-gate.sh   # kernel-coverage-gate.yml — Domain 100%, Infrastructure -f net10.0 --filter "FullyQualifiedName!~RuntimeStudioBlackBoxSmokeTests&Category!=External" (80% floor), Core.Application 67%
bash scripts/run-cert-gate.sh             # cert-gate.yml — Certification + GenerationSafety + AstMutationEngine, -f net8.0, zero-test guard
make kernel-gate                          # kernel-gate.yml — tier A (tier-b..e / kernel-gate-full also dispatchable)
make application-gate-tier-a              # application-gate.yml — tier-c = in-process Ashlar.API WebApplicationFactory tests
make testing-strategy-gate                # testing-strategy-gate.yml — PR diff rules (gap freeze, ProdStyle wiring)
make ci-verify                            # `ashlar ci verify` — build + C#-driven checks used by ship-gate
```

`make test` (`dotnet test Ashlar.sln`) still exists for a full local sweep, but treat a green result with care: it includes suites that need Docker, Ollama, Mapbox or the mesh lab, which are **opt-in by environment variable** and are reported as **Skipped** until enabled (see below).

### Opt-in external suites (Skipped, not silently Passed)

Tests that need an external dependency use `[OptInFact("<ENV>", "<dependency>")]` from `Ashlar.Tests.Infrastructure.Helpers` instead of `if (!enabled) return;`. Without the variable the test shows up as **Skipped** with a reason naming the switch (xunit 2.x evaluates it at discovery time). Host-heavy tests that must not run on GitHub runners use `[NotOnCiFact("<reason>")]` (skipped when `CI` / `GITHUB_ACTIONS` is `true`).

| Variable | Enables | Also needs |
|----------|---------|------------|
| `ASHLAR_TEST_REAL_VISION=1` | `VisionModelIntegrationTests` (Ollama vision model) | Ollama at `OLLAMA_BASE_URL` |
| `ASHLAR_TEST_REAL_NCR_OLLAMA=1` | `NcrEngineOllamaIntegrationTests.LiveOllama_*` | Ollama at `ASHLAR_NODECAP_OLLAMA_URL` / `OLLAMA_BASE_URL` |
| `ASHLAR_TEST_MAPBOX_TILES=1` | `MapboxTilesBlackBoxTests`, `MapboxTilesWhiteBoxRealDataTests` (`Category=External`) | `MAPBOX_ACCESS_TOKEN` for the valid-token tests (skipped with a reason when empty) |
| `ASHLAR_RUN_DYNAMODB_CONTAINER=1` | `DynamoDbSmsIngressDockerTests` (Testcontainers) | Docker |
| `ASHLAR_RUN_MESH_LAB=1` | `MeshLabDockerE2ETests` (`Category=MeshLab`; `make test-mesh-lab`) | Docker, bash, python3 |

Still using the soft `return` (no discovery-time switch exists, so they cannot become Skips without changing what they gate on): `LocalModelProviderIntegrationTests` (downloads a GGUF or uses `ASHLAR_LOCAL_MODEL_PATH`), the OS-specific cases in `CodeAnalysisPlatformCompatibilityTests`, and the post-opt-in runtime guards inside the suites above (for example Ollama probed but unreachable). Prefer the attribute for any new dependency-gated test.

```bash
# Full local sweep (see caveat above)
make test

# Or directly (add --blame-hang-dump-type none to avoid 6GB dumps on hangs)
dotnet test --blame-hang-timeout 30s --blame-hang-dump-type none

# ashlar validate: high-signal check, but can fail in some host environments
# due to platform-specific test/runtime constraints.
# Prefer running after setup scripts and initial CLI smoke checks.
ashlar validate
# equivalent:
dotnet run --project application/src/Ashlar.CLI -- validate

# Dogfood (always the repo's dev/test container — SDK lives there)
make dogfood-block2
make dogfood-campaign
bash scripts/run-dogfood-campaign.sh
# Already inside the container (or after `bash scripts/run-in-devcontainer.sh`):
ashlar dogfood block2 --verbose
dotnet run --project application/src/Ashlar.CLI -- dogfood campaign --verbose

# Integration tests only
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj \
  --filter "Category=Integration" --blame-hang-timeout 60s --blame-hang-dump-type none

# Adaptation tests only (local)
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj \
  --filter "Category=Adaptation" --blame-hang-timeout 90s --blame-hang-dump-type none

# Adaptation tests across all Docker environments (linear)
make test-adaptation-all-envs
# Or: ashlar test multi-env --suite adaptation --all
# equivalent:
dotnet run --project application/src/Ashlar.CLI -- test multi-env --suite adaptation --all

# Cross-platform (CI)
make test-cross-platform SCOPE=integration

# Adaptation scope in CI
gh workflow run "Cross-Platform Tests" -f scope=adaptation

# Dogfood (North Star gates)
make dogfood-all
make dogfood-campaign
make dogfood-campaign-full
```

## Execution Routing Smoke + Stress Tests

The routing stack (NCR local + peer network + RunPod cloud) has targeted smoke coverage in:

- `CapabilityRoutingBrickTests`
- `PeerToPeerRoutingSmokeTests`

Run them with:

```bash
# Peer smoke + stress scenarios (fallback, timeout, outage, burst concurrency)
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj \
  -f net8.0 --filter "FullyQualifiedName~PeerToPeerRoutingSmokeTests"

# Routing regression (peer smoke + core capability router)
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj \
  --filter "FullyQualifiedName~PeerToPeerRoutingSmokeTests|FullyQualifiedName~CapabilityRoutingBrickTests"
```

What these stress tests simulate:
- concurrent burst traffic to peers
- intermittent HTTP failures and socket resets
- latency spikes that trigger per-peer timeout and failover
- recovery with success-rate thresholds under degraded network conditions

Note: `src/Ashlar.Tests.Infrastructure/scripts/copy-assemblies.cs` is lock-tolerant for transient file-copy races during test builds (retry/backoff + safe lock skips when outputs already exist), so transient assembly lock contention should not surface as `MSB3073` copy-script warnings.

## Safe Validation (Avoid Memory Explosion)

Running multiple validation commands in parallel (e.g. from Cursor's integrated terminal) can cause severe memory pressure and freeze the machine. Each `dotnet test` run buffers output and spawns test hosts; running 5+ in parallel can exceed 100GB RAM.

**Never run** `make ci-verify`, `ashlar dogfood all`, and multiple `dotnet test` commands **in parallel** from Cursor's integrated terminal.

**Prefer:**
- Run validation in an external terminal (iTerm, Terminal.app) or CI
- If using Cursor, run **one command at a time** and wait for completion before starting the next

**Quick sanity check:** `make ci-verify` alone (sequential: build → smoke → validate) is the minimal gate. Run it first; if it passes, consider running `ashlar dogfood all` in a separate terminal.

**Lightweight alternative:** `make validate-safe` or `bash scripts/validate-safe.sh` — equivalent to ci-verify but via shell script; use when ci-verify causes high memory usage. Run dogfood separately: `make dogfood-all`.

### Memory Mitigations (Built-in)

Test projects use `xunit.runner.json` to limit parallelism and reduce memory usage:

- **maxParallelThreads: 2** — limits concurrent test execution (default: CPU count)
- **parallelAlgorithm: conservative** — starts fewer tests at once, lower memory pressure
- **parallelizeAssembly: false** — prevents cross-assembly parallelism

To further reduce memory (e.g. on very constrained machines), set `parallelizeTestCollections: false` in `xunit.runner.json` to run all tests sequentially within each assembly.

## Test Artifacts & Cleanup

Tests can leave artifacts that consume disk space:

| Artifact | Size | Location |
|----------|------|----------|
| Hang dumps (`.dmp`) | ~6 GB each | `src/*/TestResults/*/` |
| Sequence XML | ~88 KB | `src/*/TestResults/*/` |
| TRX results (`.trx`) | ~20 KB each | `TestResults/`, `test-results/` |
| Coverage (`.coverage`) | varies | `TestResults/` |

**To prevent accumulation:** Local `make test` and CLI use `--blame-hang-dump-type none` so hang dumps are not written.

**To clean existing artifacts:**
```bash
# Via CLI (programmatic)
ashlar maintenance clean --strategy test-artifacts

# Or via Makefile
make clean-test-artifacts
```
Removes all `TestResults/` dirs under `src/` and the root `test-results/` folder.

**Procedural cleanup (before/after test runs):** Set `ASHLAR_CLEAN_BEFORE_TEST=1` or `ASHLAR_CLEAN_AFTER_TEST=1` to run test-artifacts cleanup automatically when using `ashlar test local`.

## Full Platform Readiness Gate

The `full-platform-readiness-gate.yml` workflow runs setup → discovery → dry-run on all target platforms:

- **Native:** Ubuntu, macOS, Windows (setup scripts, CLI build, `ashlar doctor`, `ashlar bootstrap check`, `ci verify`, smoke tests)
- **Container:** Ubuntu, Alpine, Debian (Docker test images with CLI + test projects pre-built)
- **Docker CLI image:** build from `Dockerfile.cli` + smoke + discover
- **SDK sample:** `docs/samples/StableSdkHostSample/` built as compatibility smoke check

Trigger manually: `make test-readiness-gate` or `gh workflow run "Full Platform Readiness Gate" --ref <branch>`. Most repo workflows are **manual-first**; see `.github/workflows/README.md`.

## Docker Test Images

Three Docker test images are maintained under `.docker/`:

| Image | Base | Purpose |
|-------|------|---------|
| `Dockerfile.test-caching` | Ubuntu + .NET 10 SDK | Framework, trust, and smoke tests |
| `Dockerfile.test-caching-alpine` | Alpine + .NET 10 SDK | Alpine-specific portability tests |
| `Dockerfile.test-caching-debian` | Debian + .NET 10 SDK | Debian-specific portability tests |

All three include the .NET 8 runtime (so the `net8.0` inner build of the multi-target test projects runs on the real 8.0 runtime) and pre-build test projects + CLI (`net10.0`) during image build.

## E2E Test Coverage

E2E tests are tagged with `[Trait("Category", "E2E")]` and must have explicit `[Fact(Timeout = N)]` (enforced by `TimeoutConventionTests`).

Key E2E test files:

| File | Tests | Coverage |
|------|-------|----------|
| `StrictModeE2ETests` | 8 | Strict mode flags, DI, config fail-fast |
| `AshlarDefaultsTests` | 13 | Golden tests for all centralized constants |
| `ConfigurationAdapterEdgeCaseTests` | 8 | ASHLAR_CONFIG_PATH, malformed JSON, strict/permissive |
| `ProviderFactoryEdgeCaseTests` | 11 | Provider selection, mock gating, concurrency |
| `PipelineLifecycleE2ETests` | 9 | Pipeline lifecycle, resume, fan-in, concurrency |
| `HostingDeploymentProfileTests` | 10 | All deployment profiles, env var resolution |
| `BackgroundAgentLifecycleE2ETests` | 10 | Mode transitions, approval gates, audit bounds |
| `HostingE2ESmokeTests` | 8 | AddAshlar kernel resolution, validation, observation |
