# Ashlar Execution Plan

Comprehensive execution plan derived from the 30/60/90 roadmap, North Star gap analysis, and current codebase state. Each work item includes what already exists, what remains, dependencies, risks, and concrete implementation tasks.

**Guiding principle:** Every item is sequenced so that earlier work de-risks or enables later work. Items within the same phase can be parallelized unless a dependency is noted.

## Completion Status

Snapshot of this plan versus the repo today. **Implemented** = code exists; **Tested** = automated coverage called out in tests/CI where known; **Remaining work** = still open per codebase review.

| Item | Status | Notes |
|------|--------|-------|
| 1.1 Secure Engineering Copilot MVP | Remaining work | Task persistence, authz, portal history, and onboarding still outstanding (see §1.1). |
| 1.2 Wire Observe → Improve Integration | Implemented | `ashlar improve --from-observation`, `--continuous`, `IPatternStore` in improve path; `ashlar ingest-failures` + `TestFailureIngestionBridge`. |
| 1.2 (tests / auto TRX) | Remaining work | Optional: bridge TRX directly from `dotnet test` tooling; broaden integration tests if desired. |
| 1.3 Mesh Trust Tiers — Routing Policy Enforcement | Partially implemented | `PeerTrustTier`, `PeerTrustPolicyResolver`, CLI `--set-trust-tier`; verify audit/tier display and policy env surfacing end-to-end. |
| 1.4 SDK and Port Stabilization v1 | Partially implemented | [SdkCompatibilityPolicy.md](SdkCompatibilityPolicy.md), `UseAdaptiveRouting()` obsolete; sample under `docs/samples/StableSdkHostSample/` (standalone csproj — add an explicit CI `dotnet build` step to gate it). |
| 1.5 SLO Evidence Pipeline Hardening | Remaining work | Broader gate aggregation, RC checklist links, baseline compare (see §1.5). |
| 2.1 Capability Component Registry Completion | Partially implemented | Stable `InputSchema`/`OutputSchema` enforced in `ComponentDescriptorValidator` ([ComponentDescriptorValidatorSchemaTests.cs](../src/Ashlar.Tests.Infrastructure/Tests/Composition/ComponentDescriptorValidatorSchemaTests.cs)); CLI filters / `compose audit` still open. |
| 2.2 Doctor `--fix` Remediation Hardening | Remaining work | See §2.2. |
| 2.3 Onboarding Reliability Gate Expansion | Remaining work | See §2.3. |
| 2.4 Release Gate Orchestration — Unified Reports | Remaining work | See §2.4. |
| 2.5 Trust Policy Pack Maturation | Remaining work | See §2.5. |
| 3.1 First Application-Suite Vertical | Remaining work | See §3.1. |
| 3.2 Multi-Instance Mesh Governance | Partially implemented | `ashlar mesh admit` / `ashlar mesh revoke` and trust tiers landed; policy propagation / audit trail depth may remain. |
| 3.3 Load/Performance Certification Lane | Partially implemented | [perf-certification.yml](../.github/workflows/perf-certification.yml) runs benchmark-scoped tests; formal harness/regression policy still expandable. |
| 3.4 External Integrator Program | Remaining work | [IntegratorGuide.md](IntegratorGuide.md) exists; extra reference integrations and compatibility matrix still open. |
| C.1 HttpBarrierContextMiddleware | Implemented | Resolves barrier context via `IBarrierIdentityResolverPipeline` when `ASHLAR_BARRIER_MIDDLEWARE_ENABLED` is `1`/`true`. |
| C.1 (tests) | Tested | [HttpBarrierContextMiddlewareTests.cs](../src/Ashlar.Tests.Infrastructure/Tests/Barriers/HttpBarrierContextMiddlewareTests.cs). |
| C.2 PerfHeadroom Policy | Implemented | Enforces per-tool cumulative time budget via `WorldSnapshot` keys. |
| C.2 (tests) | Remaining work | Confirm dedicated unit tests cover approve/reject paths. |
| C.3 CI `continue-on-error` (test caching workflow) | Implemented | Docker build/test steps are gating; summary job fails on `test-caching` failure. |
| C.4 PR / Issue Templates | Implemented | `.github/PULL_REQUEST_TEMPLATE.md` and `ISSUE_TEMPLATE/*.md` present. |
| C.5 Self-Improvement as Background Agent | Implemented | `self-improver` role in `BackgroundAgentRegistry`. |
| C.6 Trust Wiring in ImproveCommand | Implemented | `ASHLAR_TRUST_ENABLED=1` registers the trust composition (`AddTrustServices`) and `SanitizingProviderFactory` in improve DI, so the proxy has a PII content filter. Before the fail-closed change it was built with none and passed every prompt through. |
| C.6 (tests) | Tested | [ImproveCommandTrustWiringTests.cs](../application/src/Ashlar.Tests.CLI/Tests/Commands/ImproveCommandTrustWiringTests.cs). |

---

## Phase 1 — Foundation & Product Proof (Phase-30 items)

These items establish the first end-to-end product experience and close the most critical framework gaps. Complete these before moving to Phase 2.

---

### 1.1 Secure Engineering Copilot MVP

**What exists:**
- `POST /api/copilot/task` endpoint with orchestration + audit trail (`AshlarEndpoints.cs`).
- Portal UI in `wwwroot/index.html` with chat-style copilot task flow.
- Trust dashboard/pause/rules endpoints (`/api/trust/*`).
- `deploy/compose/docker-compose.agent-server.yml` for local deployment.
- Walkthrough doc (`docs/Phase1SecureCopilotWalkthrough.md`).

**What remains:**
- The copilot flow is synchronous request/response with no task persistence, correlation IDs, or execution history.
- No dedicated authz on copilot endpoints (relies on shared API-key middleware).
- Portal UI is minimal — no execution history view, no audit timeline visualization.
- No operator onboarding wizard or guided first-run experience.

**Implementation tasks:**
1. Add task persistence: store copilot task submissions and results in LiteDB with correlation IDs. Define `ICopilotTaskStore` port in `Ashlar.Core.Application`, implement in `Ashlar.Infrastructure`.
2. Add execution history endpoint: `GET /api/copilot/tasks` with pagination, filtering by status/date.
3. Add correlation ID propagation: generate a `copilotTaskId` on submission, thread it through `OrchestrateAsync` and into audit log entries so the audit trail is filterable per task.
4. Enhance portal UI: add task history sidebar, audit timeline per task, trust boundary status indicator.
5. Add operator first-run flow: detect empty state, show guided setup (API key, Ollama connection, trust pack selection).
6. Update `docs/Phase1SecureCopilotWalkthrough.md` with the full flow including history and audit drill-down.
7. Add E2E smoke test: submit task via API, verify result + audit entries + history retrieval.

**Files to modify:**
- `src/Ashlar.Core.Application/` — new port `ICopilotTaskStore`
- `src/Ashlar.Infrastructure/` — LiteDB implementation
- `application/src/Ashlar.API/Endpoints/AshlarEndpoints.cs` — history endpoint, correlation IDs
- `application/src/Ashlar.API/wwwroot/index.html` — portal UI enhancements
- `src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs` — register new services
- `docs/Phase1SecureCopilotWalkthrough.md`

**Dependencies:** None — this is the first item to start.  
**Risks:** Orchestration latency may make synchronous copilot tasks feel slow for complex requests. Consider adding a simple polling/status pattern if tasks exceed ~10s.

---

### 1.2 Wire Observe → Improve Integration

**What exists:**
- `ashlar observe` populates `IPatternStore` (LiteDB) with patterns (`repeated-edits`, `edit-then-build`).
- `ashlar improve` runs static analysis → adaptation on a fixed or user-specified path.
- `ashlar improve --self` runs `SelfImprovementLoop` which reads both `ITestFailureStore` and `IPatternStore`.
- `SelfImprovementLoop` already queries `repeated-edits` and `edit-then-build` patterns.

**What remains:**
- Default `ashlar improve` (without `--self` or `--from-observation`) still targets a fixed or explicit `--path`; only `--from-observation` / `--continuous` pull from `IPatternStore`.
- Optional: auto-ingest TRX from the same process as `dotnet test` (today: `ashlar ingest-failures` after tests produce `.trx` files).
- Optional: broader integration tests for `--from-observation` / `--continuous` and CI recipes (ingest → `--self`).

**Implementation tasks:** *(largely complete — keep for history)*
1. ~~Add `--from-observation` option to `ImproveCommand`~~ **Done.**
2. ~~Register `IPatternStore` in the `ImproveCommand` service collection~~ **Done** (as needed for improve path).
3. ~~TRX → `ITestFailureStore`~~ **`TestFailureIngestionBridge` + `ashlar ingest-failures`** **Done.**
4. ~~`ashlar improve --continuous`~~ **Done** (`--observe-minutes`, `--interval-minutes`).
5. Add integration tests validating the observe → improve → self-context pipeline with pattern-driven targeting (expand coverage as needed).
6. ~~Document the wired flow in `docs/GapAnalysis.md`~~ **Done** (keep in sync with CLI flags).

**Files to modify:**
- `application/src/Ashlar.CLI/Commands/ImproveCommand.cs` — add `--from-observation`, wire `IPatternStore`
- `src/Ashlar.Infrastructure/SelfImprovement/` — `TestFailureIngestionBridge`
- `src/Ashlar.Infrastructure/Validation/` — bridge from `TrxTestResultParser` to `ITestFailureStore`
- `src/Ashlar.Tests.Infrastructure/` — integration tests
- `docs/GapAnalysis.md`

**Dependencies:** None — can be done in parallel with 1.1.  
**Risks:** Pattern store may contain noisy or stale entries; add a staleness filter (e.g. patterns older than 7 days are excluded by default).

---

### 1.3 Mesh Trust Tiers — Routing Policy Enforcement

**What exists:**
- `PeerTrustTier` enum (`Unknown`, `Untrusted`, `Trusted`) on `PeerInfo`.
- CLI `--set-trust-tier` updates peer trust tier in `instances.json` with metadata preservation.
- `PeerTrustPolicyResolver`, `NcrCapabilityRouter`, `AshlarPeerBrickExecutor` in routing infrastructure.

**What remains:**
- Verify routing policy actually blocks execution to disallowed tiers (audit the `NcrCapabilityRouter` behavior).
- Add routing policy options surface: `trusted-only`, `trusted-preferred`, `any` — confirm these are configurable and enforced.
- Surface trust tier in `ashlar mesh` status output and API status endpoints.
- Add audit events for trust-tier routing decisions (accepted/rejected with reason).

**Implementation tasks:**
1. Audit `NcrCapabilityRouter` and `PeerTrustPolicyResolver` to confirm tier enforcement end-to-end. Document findings.
2. If routing policy options are not yet configurable via CLI/env, add config: `ASHLAR_MESH_TRUST_POLICY=trusted-only|trusted-preferred|any`.
3. Add tier display to `ashlar mesh` list output (show tier next to each peer).
4. Add audit log entries when a peer is skipped due to trust tier policy.
5. Add integration tests: mixed-tier peer snapshots, `trusted-only` blocks `untrusted`, `trusted-preferred` falls back correctly.
6. Add smoke test for policy toggle + routing outcomes.

**Files to modify:**
- `src/Ashlar.Infrastructure/Execution/Routing/` — policy enforcement, audit
- `src/Ashlar.Infrastructure/Mesh/` — status display
- `application/src/Ashlar.CLI/Commands/MeshCommand.cs` — tier display
- `src/Ashlar.Tests.Infrastructure/` and `application/src/Ashlar.Tests.CLI/` — tests

**Dependencies:** None — can be done in parallel with 1.1 and 1.2.  
**Risks:** Low. The data model and CLI surface already exist; this is mostly wiring and verification.

---

### 1.4 SDK and Port Stabilization v1

**What exists:**
- `Ashlar.Sdk` with `AshlarSdkBuilder` (thin wrapper over `Ashlar.Client`).
- `docs/sdk.md` with stable/experimental/internal classification.
- `docs/samples/StableSdkHostSample/` with `Program.cs` showing brick/agent registration.
- `Ashlar.Abstractions` contains core port definitions.

**What remains:**
- Add an explicit CI step/workflow that `dotnet build` (and optionally `dotnet run`) `docs/samples/StableSdkHostSample/StableSdkHostSample.csproj` — the sample is not in `Ashlar.sln`, so it is not built by default solution builds.
- Full public-surface audit: explicit `[Stable]` / `[Experimental]` attributes and a complete classification table in `docs/sdk.md` (policy doc exists; table may still be partial).

**Implementation tasks:**
1. ~~Add CI lane that builds and runs `StableSdkHostSample` in isolation (verify it compiles against published/local NuGet only — no project references to internals).~~ **Done** — `package-consumer/StableSdkHostSample.Package.csproj` + `scripts/verify-stable-sdk-host-sample-packages.*` + readiness gate step **Setup — verify SDK sample consumes local NuGet graph**.
2. Add `docs/SdkCompatibilityPolicy.md`: semver rules, deprecation process, breaking-change notification. **Done** — see [SdkCompatibilityPolicy.md](SdkCompatibilityPolicy.md).
3. Audit all public types in `Ashlar.Sdk`, `Ashlar.Client`, `Ashlar.Abstractions`, `Ashlar.Brick.Contracts` — classify each as Stable / Experimental / Internal. Add classification table to `docs/sdk.md`. **Ongoing.**
4. Add XML doc comments indicating stability level on key public interfaces. **Ongoing.**
5. Remove or clearly mark `UseAdaptiveRouting()` as experimental in `AshlarSdkBuilder`. **Done** — `[Obsolete]` with rationale in XML doc.

**Files to modify:**
- `.github/workflows/` — new or extended workflow for SDK sample CI
- `docs/sdk.md` — classification table
- `docs/SdkCompatibilityPolicy.md` — new
- `src/Ashlar.Hosting/Sdk/Builders/AshlarSdkBuilder.cs` — stability annotations
- `src/Ashlar.Abstractions/` — stability annotations on ports

**Dependencies:** None — can be done in parallel.  
**Risks:** Premature stabilization of APIs that may need to change. Mitigate by keeping the stable surface minimal (only `Ashlar.Sdk`, `Ashlar.Client`, `Ashlar.Brick.Contracts`).

---

### 1.5 SLO Evidence Pipeline Hardening

**What exists:**
- `RuntimeCommand` with `--emit-slo-evidence`, `--slo-warning-only`, env thresholds (`ASHLAR_RELEASE_SLO_*`).
- `RuntimeSloEvidence` / `RuntimeLaneSloEvidence` models.
- `ci release-bundle` includes runtime-gate step.
- Tests in `RuntimeCommandTests` validating SLO JSON output.

**What remains:**
- SLO evidence is runtime-gate scoped; broader release gates (cross-platform, trust, caching) don't emit SLO summaries.
- RC checklist (`docs/ReleaseCandidateChecklist-v1.md`) may not reference concrete SLO artifact paths.
- No trend comparison between releases.

**Implementation tasks:**
1. Extend `ci release-bundle` to collect SLO evidence from each gate step (not just runtime-gate).
2. Add SLO summary section to `release-bundle-report.md` output.
3. Update `docs/ReleaseCandidateChecklist-v1.md` to reference SLO artifact paths from release-bundle output.
4. Add `--compare-baseline` option to compare current SLO metrics against a stored baseline.
5. Add test for forced SLO threshold violation → promotion failure.

**Files to modify:**
- `application/src/Ashlar.CLI/Commands/CiCommand.cs` — extend release-bundle SLO collection
- `application/src/Ashlar.CLI/Commands/RuntimeCommand.cs` — baseline comparison
- `docs/ReleaseCandidateChecklist-v1.md`
- `application/src/Ashlar.Tests.CLI/` — threshold violation test

**Dependencies:** Lightweight dependency on 1.1 (copilot MVP may inform which SLO metrics matter most).  
**Risks:** Low. SLO infrastructure is already functional; this is extension and documentation.

---

## Phase 2 — Framework Maturity (Phase-60 items)

These items deepen the framework's reliability, developer experience, and operational readiness. Start after Phase 1 core items (1.1, 1.2) are complete.

---

### 2.1 Capability Component Registry Completion

**What exists:**
- `CapabilityComponentRegistry` with in-memory store, registration, lookup, `ValidateSelection`.
- `ComponentDescriptor` model with `Id`, `Capability`, `DisplayName`, `Summary`, `InputSchema`, `OutputSchema`, `Version`, `SupportLevel`, `RequiredCapabilities`, `IncompatibleWith`.
- `ComponentDescriptorValidator` enforces core fields, rejects `TBD` implementation types, validates semver.
- Seed includes real components + North Star placeholders via `PlaceholderCapabilityComponent`.

**What remains:**
- Composition-time filtering by capability/constraint is not fully exposed in CLI.
- Placeholder components need replacement with real implementations or explicit exclusion from production composition.

**Implementation tasks:**
1. ~~Add `InputSchema` / `OutputSchema` validation to `ComponentDescriptorValidator` (non-empty when `SupportLevel` is `Stable`).~~ **Done.**
2. Add `ashlar compose --filter-capability <cap>` to filter components by capability during composition.
3. Add `ashlar compose --list-components` to show registry contents with metadata completeness indicators.
4. Replace or remove `PlaceholderCapabilityComponent` entries that have real implementations.
5. Add registry audit report command: `ashlar compose audit` showing completeness per component.
6. Add unit tests for schema validation and composition filtering.

**Files to modify:**
- `src/Ashlar.Infrastructure/Composition/ComponentDescriptorValidator.cs`
- `src/Ashlar.Infrastructure/Composition/CapabilityComponentRegistry.cs`
- `application/src/Ashlar.CLI/` — compose subcommands
- `src/Ashlar.Tests.Infrastructure/`

**Dependencies:** Benefits from 1.4 (SDK stabilization clarifies which components are stable).  
**Risks:** Schema validation may break existing tests that use minimal descriptors. Add migration path with warnings before hard enforcement.

---

### 2.2 Doctor `--fix` Remediation Hardening

**What exists:**
- `DoctorCommand` with `--fix` and `--yes` flags.
- `DoctorRemediation.cs` implements remediation runner.
- `scripts/onboarding/failure-taxonomy.sh` maps failures to remediation.
- `ci release-bundle` runs `doctor --json` (read-only check).

**What remains:**
- Verify remediation coverage: which failure categories does `--fix` actually handle?
- JSON output from `--fix` may not include before/after state for all remediation actions.
- No integration test with mocked failure scenarios validating end-to-end remediation.

**Implementation tasks:**
1. Audit `DoctorRemediation.cs` — catalog which failure types are handled and which are not.
2. Add before/after state to JSON remediation output for each action.
3. Add mocked-failure integration tests: simulate missing tools, wrong SDK version, broken config — verify `--fix` resolves them.
4. Add `ashlar doctor --fix --dry-run` mode that reports what would be fixed without acting.
5. Update `docs/GettingStarted.md` with `doctor --fix` as part of the setup flow.
6. Align `failure-taxonomy.sh` categories with `DoctorRemediation` problem taxonomy.

**Files to modify:**
- `application/src/Ashlar.CLI/Commands/DoctorCommand.cs`
- `application/src/Ashlar.CLI/Commands/DoctorRemediation.cs`
- `application/src/Ashlar.Tests.CLI/` — remediation integration tests
- `scripts/onboarding/failure-taxonomy.sh`
- `docs/GettingStarted.md`

**Dependencies:** None.  
**Risks:** Auto-remediation that modifies system state is inherently risky. Keep `--yes` as opt-in; default to interactive confirmation.

---

### 2.3 Onboarding Reliability Gate Expansion

**What exists:**
- `onboarding-quickstart-gate.yml` with weekly cron schedule (`0 7 * * 1`).
- Drift signals emitted as JSON (`driftSignals.isScheduledRun`, `isRetry`).
- Native + container lanes.

**What remains:**
- Drift detection is a signal in JSON but there is no automated comparison between scheduled runs.
- No failure categorization by lane/platform/root cause in the workflow output.
- No trend dashboard or alerting on regression.

**Implementation tasks:**
1. Add run-over-run comparison: store previous scheduled run summary as a workflow artifact, diff against current run.
2. Add failure categorization step: parse test/setup outputs and classify by `failure-taxonomy.sh` categories.
3. Add summary artifact with platform × lane × failure-category matrix.
4. Add optional notification (GitHub issue or workflow annotation) when new failures appear vs. previous scheduled run.
5. Add documentation in `docs/Testing.md` explaining the drift detection mechanism.

**Files to modify:**
- `.github/workflows/onboarding-quickstart-gate.yml`
- `scripts/onboarding/failure-taxonomy.sh` — extend for workflow integration
- `docs/Testing.md`

**Dependencies:** Benefits from 2.2 (doctor `--fix` provides remediation for detected drift).  
**Risks:** Scheduled CI runs consume minutes; keep the workflow lean. Use `workflow_dispatch` for on-demand deep checks.

---

### 2.4 Release Gate Orchestration — Unified Reports

**What exists:**
- `ci release-bundle` with `--profile` (`default` | `quick`).
- Steps: verify → runtime-gate → doctor JSON.
- Outputs `release-bundle-report.json` and `release-bundle-report.md`.

**What remains:**
- Only three steps in the bundle; cross-platform, trust, and caching gates are not included.
- Report may not aggregate sub-gate pass/fail status clearly.
- No `--profile full` that runs all gates.

**Implementation tasks:**
1. Add `--profile full` that includes: verify, runtime-gate, doctor, trust-gate, cross-platform-smoke, caching-smoke.
2. For each step, capture exit code + summary + artifact paths.
3. Enhance `release-bundle-report.md` with a clear pass/fail matrix per gate.
4. Add `--fail-fast` option (default true) to stop on first failure, and `--continue` to run all gates.
5. Add integration test for pass-path and forced-failure-path.
6. Update `docs/ReleaseCandidateChecklist-v1.md` to reference `ci release-bundle --profile full`.

**Files to modify:**
- `application/src/Ashlar.CLI/Commands/CiCommand.cs` — profiles, steps, report format
- `application/src/Ashlar.Tests.CLI/` — integration tests
- `docs/ReleaseCandidateChecklist-v1.md`

**Dependencies:** Benefits from 1.5 (SLO evidence feeds into release report).  
**Risks:** Full gate profile may be slow. Mitigate with `--profile quick` as default and `full` for RC cuts.

---

### 2.5 Trust Policy Pack Maturation

**What exists:**
- Three packs: `air-gapped.json`, `internal-only.json`, `strict-enterprise.json`.
- `TrustPolicyPackRegistry` with load, activate, persist to `active-pack.json`.
- CLI `trust pack list` / `trust pack apply --id`.
- `IAccessBoundary.ApplyPolicyPack` integration.

**What remains:**
- Verify pack behavior actually enforces the described boundaries (not just metadata).
- No trust integration tests that run across all packs.
- Pack version visibility in `ashlar status` or API status output may be incomplete.

**Implementation tasks:**
1. Add trust integration tests for each pack: apply pack, attempt operations that should be blocked, verify enforcement.
2. Add pack version and active status to `GET /api/status` response.
3. Add `ashlar trust pack describe --id <id>` to show pack rules in human-readable form.
4. Add pack migration guidance: document how to transition between packs.
5. Add operator docs mapping deployment scenarios to recommended packs.

**Files to modify:**
- `src/Ashlar.Tests.Infrastructure/` — trust pack integration tests
- `application/src/Ashlar.API/Endpoints/AshlarEndpoints.cs` — status enrichment
- `application/src/Ashlar.CLI/Commands/TrustCommand.cs` — `describe` subcommand
- `docs/` — operator pack selection guide

**Dependencies:** None.  
**Risks:** Low. Infrastructure exists; this is verification and documentation.

---

## Phase 3 — Production Readiness & Vision (Phase-90 items)

These items push Ashlar toward production use and external adoption. Start after Phase 2 core items are stable.

---

### 3.1 First Application-Suite Vertical

**What exists:**
- `apps/runtime-studio/` with agent-set JSON configs and shell scripts.
- Game director variant with dedicated agent set.
- Background agent infrastructure (scheduling, registry, lifecycle).
- Copilot MVP endpoint (from 1.1).

**What remains:**
- No standalone, product-quality vertical application.
- Runtime Studio is config + scripts, not a deployable product.

**Recommended vertical: Engineering Release Manager**
- Leverages existing: `ci release-bundle`, SLO evidence, trust packs, background agents, changelog generation.
- User journey: configure release criteria → agents monitor repo → automated release readiness assessment → operator approval → release.

**Implementation tasks:**
1. Define user journeys and acceptance criteria for the release manager vertical.
2. Build release manager agent set (JSON config for background agents): repo monitor, test runner, SLO collector, report generator.
3. Add release manager API endpoints or extend copilot flow for release-specific queries.
4. Build minimal dashboard UI (extend portal or standalone page) showing release readiness status.
5. Add deployment docs: compose file, env config, operator guide.
6. Add E2E scenario test: repo change → agent detection → readiness report → approval simulation.

**Files to modify:**
- `apps/` — new `release-manager/` directory with agent configs
- `application/src/Ashlar.API/` — release-specific endpoints or views
- `src/Ashlar.BackgroundAgents/` — release monitor agent role
- `docs/` — vertical docs

**Dependencies:** 1.1 (copilot MVP), 1.5 (SLO pipeline), 2.4 (release gate orchestration).  
**Risks:** Scope creep. Keep the first vertical minimal: readiness dashboard + automated checks. Defer approval workflows and notifications to a follow-up.

---

### 3.2 Multi-Instance Mesh Governance

**What exists:**
- Mesh discovery, `ashlar mesh` commands, peer trust tiers (from 1.3).
- Shared adaptation cache, sneakernet export/import.
- `FileBasedInstanceDiscovery`, `FileBasedCapabilityAdvertisement`.

**What remains:**
- Rich admission state machine (`pending` → `admitted` → `active`) vs. boolean `admitted` flag in peer JSON.
- Policy propagation between instances and versioned policy fields on advertisements.
- Deeper governance audit trail beyond file updates and routing behavior.

**Implementation tasks:**
1. Design peer admission state machine: `pending` → `admitted` → `active` (or `rejected`). **Partial** — today: `admitted` boolean via CLI.
2. Implement admission workflow in `MeshCommand`: `ashlar mesh admit <peerId>`, `ashlar mesh revoke <peerId>`. **Done.**
3. Wire revocation into routing: revoked peers are immediately excluded from `NcrCapabilityRouter`. **Done** (verify in tests as needed).
4. Add policy version field to mesh advertisements; receiving peers can detect and warn on mismatched policy versions. **Remaining.**
5. Add governance audit trail: all admission/revocation events logged. **Remaining / extend.**
6. Add integration tests for admission → routing → revocation → routing-exclusion flow. **Ongoing.**

**Files to modify:**
- `src/Ashlar.Core.Application/Mesh/` — admission models
- `src/Ashlar.Infrastructure/Mesh/` — admission state persistence
- `src/Ashlar.Infrastructure/Execution/Routing/` — revocation enforcement
- `application/src/Ashlar.CLI/Commands/MeshCommand.cs`
- `src/Ashlar.Tests.Infrastructure/` and `application/src/Ashlar.Tests.CLI/`

**Dependencies:** 1.3 (mesh trust tiers provide the trust model that governance builds on).  
**Risks:** Distributed state consistency. Keep it simple: file-based state with eventual consistency, not distributed consensus.

---

### 3.3 Load/Performance Certification Lane

**What exists:**
- Runtime benchmarks directory (`docs/runtime/benchmarks/README.md`).
- SLO evidence with configurable thresholds.
- `dotnet test` with blame-hang timeouts.
- CI workflow [perf-certification.yml](../.github/workflows/perf-certification.yml) runs benchmark-scoped tests and uploads TRX/meta artifacts.

**What remains:**
- No formal load test harness beyond benchmark-scoped test filters.
- No repeatable workload profiles document tying CI runs to named scenarios.
- No trend retention or automated regression detection against a stored baseline.

**Implementation tasks:**
1. Define 3 representative workload profiles: single-agent simple task, multi-agent orchestration, mesh-routed execution.
2. Implement benchmark harness using BenchmarkDotNet or a custom runner that emits structured JSON results.
3. ~~Add CI workflow (`perf-certification.yml`)~~ **Done** (extend with richer profiles / baselines as needed).
4. Add regression detection: compare current run against stored baseline; fail on >10% degradation (configurable).
5. Add trend report generation: markdown summary with throughput/latency/error rates across recent runs.

**Files to modify:**
- `src/` — new `Ashlar.Benchmarks` project or extend `Ashlar.Tests.Infrastructure`
- `.github/workflows/` — `perf-certification.yml`
- `docs/runtime/benchmarks/` — workload profiles and trend docs

**Dependencies:** 1.1 (copilot MVP defines the primary workload), 2.4 (release gate can include perf lane).  
**Risks:** Benchmarks on CI runners are noisy. Use relative comparison (% change) rather than absolute thresholds. Run on dedicated runner class if available.

---

### 3.4 External Integrator Program

**What exists:**
- `docs/sdk.md` with stability tiers.
- [IntegratorGuide.md](IntegratorGuide.md) onboarding and patterns.
- `StableSdkHostSample` reference integration.
- `Ashlar.Brick.Contracts` for plugin wire format.

**What remains:**
- Only one reference integration exists.
- No external integrator feedback loop.
- No compatibility matrix across versions.

**Implementation tasks:**
1. Build 2 additional reference integrations: (a) external brick provider (HTTP catalog), (b) custom background agent using SDK.
2. Add integration CI lane: build all reference integrations against current SDK.
3. Create compatibility matrix: SDK version × reference integration version → pass/fail.
4. Add `docs/IntegratorGuide.md`: onboarding steps, common patterns, troubleshooting.
5. Add feedback triage template (GitHub issue template for integrators).
6. Conduct dry-run with a non-core contributor; document friction points.

**Files to modify:**
- `docs/samples/` — additional reference integrations
- `.github/workflows/` — SDK compatibility CI
- `.github/ISSUE_TEMPLATE/` — integrator feedback template
- `docs/IntegratorGuide.md`

**Dependencies:** 1.4 (SDK stabilization defines what integrators build against).  
**Risks:** External adoption feedback may require breaking changes. Use the compatibility policy from 1.4 to manage expectations.

---

## Cross-Cutting Items (any phase)

These are smaller items that should be addressed opportunistically alongside the phased work.

---

### C.1 Fix `HttpBarrierContextMiddleware`

**Current state:** **Implemented** — middleware injects `IBarrierIdentityResolverPipeline`, builds `BarrierResolutionContext` from `HttpContext`, runs the pipeline, and initializes `IBarrierContextAccessor` when enabled. Gated by `ASHLAR_BARRIER_MIDDLEWARE_ENABLED` (`1` or `true`).

**Tasks:** *(complete; optional test hardening)*
1. ~~Inject `IBarrierIdentityResolverPipeline`~~ **Done.**
2. ~~Build `BarrierResolutionContext` from `HttpContext`~~ **Done.**
3. ~~Call pipeline and handle results~~ **Done.**
4. Add integration test with mock pipeline if not already covered.

**Files:** `src/Ashlar.Runtime/Barriers/Identity/HttpBarrierContextMiddleware.cs`  
**Risk:** Low when flag is off (default passes through to `next`).

---

### C.2 Implement `PerfHeadroom` Policy

**Current state:** **Implemented** — reads `ToolElapsed:{toolId}` from `WorldSnapshot.Data`, tracks cumulative time per tool, rejects when over `_maxPerTool`.

**Tasks:** *(implementation done; confirm test coverage)*
1. ~~Implement time-budget tracking per tool invocation.~~ **Done.**
2. ~~Reject when cumulative tool time exceeds `_maxPerTool`.~~ **Done.**
3. Add unit tests for approval and rejection paths if not already present.

**Files:** `src/Ashlar.Policies/PerfHeadroom.cs`  
**Risk:** Low. Policy is opt-in.

---

### C.3 Fix CI `continue-on-error` in Test Caching Workflow

**Current state:** **Addressed** — Docker build and test steps are gating; `continue-on-error: true` remains only on optional publish/reporting. Summary job fails when `needs.test-caching.result` is `failure` or `cancelled`.

**Tasks:** *(complete)*
1. ~~Remove `continue-on-error: true` from build and test steps.~~ **Done.**
2. ~~Keep `continue-on-error: true` only on optional publish/reporting steps.~~ **Done.**
3. ~~Add proper failure aggregation in summary job~~ **Done.**
4. ~~Fix Windows `|| true`~~ **N/A / resolved** in current workflow layout.

**Files:** `test-caching-multi-env.yml` (workflow deleted 2026-08-16 — muted since 2026-08-11 and red on every recorded run; see `docs/CiGateInventory.md`, "Pruning")  
**Risk:** Low — failures now surface instead of being masked.

---

### C.4 Add PR Template and Issue Templates

**Current state:** **Done** — `.github/PULL_REQUEST_TEMPLATE.md` and `bug_report`, `feature_request`, `integrator_feedback` issue templates exist.

**Tasks:** *(complete)*
1. Create `.github/PULL_REQUEST_TEMPLATE.md` with sections: Summary, Changes, Testing, Checklist. **Done.**
2. Create `.github/ISSUE_TEMPLATE/bug_report.md` with reproduction steps template. **Done.**
3. Create `.github/ISSUE_TEMPLATE/feature_request.md`. **Done.**
4. Create `.github/ISSUE_TEMPLATE/integrator_feedback.md` (for 3.4). **Done.**

**Files:** `.github/PULL_REQUEST_TEMPLATE.md`, `.github/ISSUE_TEMPLATE/`  
**Risk:** None.

---

### C.5 Add Self-Improvement as Background Agent

**Current state:** **Implemented** — `BackgroundAgentRegistry` handles role `self-improver` and invokes `ISelfImprovementLoop` (with Passive/SemiActive skip behavior consistent with other roles).

**Tasks:** *(core complete; samples/docs optional)*
1. Add `self-improver` role to `BackgroundAgentRegistry.ExecuteAgentAsync`. **Done.**
2. Wire role to call `ISelfImprovementLoop.RunOnceAsync`. **Done.**
3. Add sample agent config in `apps/runtime-studio/config/`. **Optional.**
4. Document in `docs/Configuration.md`. **Optional.**

**Files:** `src/Ashlar.BackgroundAgents/Registry/BackgroundAgentRegistry.cs`, `apps/runtime-studio/config/`  
**Risk:** Self-improvement running automatically requires safety guardrails. Use `SemiActive` aggressiveness mode by default (requires approval gate).

---

### C.6 Trust Wiring in `ImproveCommand`

**Current state:** **Implemented** — when `ASHLAR_TRUST_ENABLED=1`, `ImproveCommand` registers the kernel's trust composition (`AddTrustServices`) and a `SanitizingProviderFactory` wrapping the inner `IProviderFactory`. Before the fail-closed change (`CHANGELOG.md`, Unreleased) the proxy behind that factory was built with no content filter and passed every prompt through; it now filters PII, and a proxy with no filter blocks.

**Tasks:** *(complete)*
1. Conditionally register `SanitizingProviderFactory` in `ImproveCommand`'s DI graph when trust is enabled. **Done** (`ASHLAR_TRUST_ENABLED` == `1`).
2. Add a test that validates sanitization is active when improve is configured for cloud-backed fix generation. **Done** for the DI graph: `ImproveCommandTrustWiringTests` resolves improve's provider registration and asserts that the proxy passes a clean prompt and blocks one carrying PII. No test drives a cloud provider end to end.

**Files:** `application/src/Ashlar.CLI/Commands/ImproveCommand.cs`, `application/src/Ashlar.Tests.CLI/`  
**Risk:** Low. Defensive wiring.

---

## Execution Sequence Diagram

```
Phase 1 (parallel tracks):
├── Track A: 1.1 Copilot MVP ──────────────────┐
├── Track B: 1.2 Observe→Improve ──────────────┤
├── Track C: 1.3 Mesh Trust Tiers ─────────────┤
├── Track D: 1.4 SDK Stabilization ────────────┤
└── Track E: 1.5 SLO Evidence ─────────────────┘
    + Cross-cutting: C.1–C.6 (opportunistic)    │
                                                 ▼
Phase 2 (after 1.1 complete; 1.2 observe→improve wiring largely done):
├── 2.1 Registry Completion (needs 1.4)
├── 2.2 Doctor --fix Hardening
├── 2.3 Onboarding Gate Expansion (needs 2.2)
├── 2.4 Release Gate Orchestration (needs 1.5)
└── 2.5 Trust Pack Maturation
                                                 │
                                                 ▼
Phase 3 (after Phase 2 core stable):
├── 3.1 First Vertical (needs 1.1, 1.5, 2.4)
├── 3.2 Mesh Governance (needs 1.3)
├── 3.3 Perf Certification (needs 1.1, 2.4)
└── 3.4 External Integrators (needs 1.4)
```

---

## Success Metrics

| Phase | Metric | Target |
|-------|--------|--------|
| 1 | Copilot E2E flow works in compose | User submits task, gets result with audit trail |
| 1 | Observe→improve connected | `ashlar improve --from-observation` targets pattern-affected files |
| 1 | Mesh trust enforcement | Routing blocks untrusted peers under `trusted-only` policy |
| 2 | Release bundle covers all gates | `ci release-bundle --profile full` runs ≥6 gate steps |
| 2 | Doctor fixes common issues | ≥3 failure categories have automated remediation |
| 3 | First vertical deployed | Release manager dashboard shows live readiness status |
| 3 | External integration validated | ≥2 reference integrations pass CI on current SDK |
