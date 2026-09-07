# North Star Gap Analysis (historical)

> **Historical document (marked 2026-08-16).** This analysis predates the trust loop (certification gate, attested sessions, hold-mode autonomy loop), which is now the mechanism behind "autonomous capability extension" and "all generated output is auditable". Do not use it as the current state: that is `docs/certification-evidence.md` (the proof ledger), `docs/ProjectTiers.md` (the repo map) and `CHANGELOG.md`. The only rows still marked MISSING when this banner was added are the Layer 9 application suite (Document Editor / Spreadsheet).

**North Star:** Private, traceable AI computing with autonomous capability extension. Modular components with standardized contracts compose into execution pipelines and are generated autonomously under configurable policy constraints. Data remains on operator-controlled infrastructure. Trust enforcement is structural. All generated output is auditable, standards-compliant, and extractable. Capabilities federate across trusted .NET peers with policy-controlled routing.

**Last Updated:** Reconciliation with codebase — several items previously marked MISSING or PARTIAL are now implemented.

**Reference:** [DogfoodValidation.md](DogfoodValidation.md)

---

## Status Legend

| Code | Meaning |
|------|---------|
| EXISTS | Implemented and tested |
| PARTIAL | Exists but incomplete or not fully wired |
| MISSING | Not yet built |
| RESOLVED | Previously a gap; now implemented (reconciliation) |

---

## Layer 1: Immutable Core

| Component | Status | Notes |
|-----------|--------|-------|
| Observation Pipeline | RESOLVED | `AddObservationPipeline` called from `AddAshlar()` by default (disabled only when `DisableObservationPipeline = true`). |
| PathAllowlist | RESOLVED | Unit tests in [PathAllowlistTests.cs](../src/Ashlar.Tests.Infrastructure/Tests/Policies/PathAllowlistTests.cs). |
| MaxWriteSize | RESOLVED | Unit tests in [MaxWriteSizeTests.cs](../src/Ashlar.Tests.Infrastructure/Tests/Policies/MaxWriteSizeTests.cs). |
| Immutability Tests | RESOLVED | [ImmutableCoreAdaptationTests.cs](../src/Ashlar.Tests.Infrastructure/Tests/Adaptation/ImmutableCoreAdaptationTests.cs) and [ImmutableCoreTests.cs](../src/Ashlar.Tests.Infrastructure/Tests/Adaptation/ImmutableCoreTests.cs) prove adaptation cannot target core components. |
| Adversarial Scope Escape | RESOLVED | [AdversarialScopeEscapeTests](../src/Ashlar.Tests.Infrastructure/Tests/Adaptation/AdversarialScopeEscapeTests.cs) prove PolicyEngine validates each call; no batch skip. |

---

## Layer 2: Domain Layer

| Component | Status | Notes |
|-----------|--------|-------|
| Domain Knowledge Store | RESOLVED | Unified read model via `IKnowledgeQueryService` / `KnowledgeQueryService` (adaptation log, pattern store, user knowledge); registered from hosting and self-context. |
| Bricks (Adaptive Behavior Library) | EXISTS | BrickCatalog, BrickRegistry, demo bricks, OWASPScannerBrick, RemoteBrick. |
| Agents | EXISTS | AgentCard, BaseAgent, domain template agents; `BackgroundAgentRegistry` runs dog-food roles including `optimizer`, `tester`, `extender`, and `self-improver`. |

---

## Layer 3: Agent Layer

| Component | Status | Notes |
|-----------|--------|-------|
| Observe → Improve Path | RESOLVED | SelfImprovementLoop queries IPatternStore for `repeated-edits` and `edit-then-build`; [SelfImprovementLoopPatternTests](../src/Ashlar.Tests.Infrastructure/Tests/SelfImprovement/SelfImprovementLoopPatternTests.cs) validates. |
| Holdout Test Set | RESOLVED | HoldoutTestOptions, `ashlar improve --self --holdout-filter`, [SelfImprovementLoopHoldoutTests](../src/Ashlar.Tests.Infrastructure/Tests/SelfImprovement/SelfImprovementLoopHoldoutTests.cs). |
| Aggressiveness Dial | RESOLVED | Four modes with distinct behavior: Passive (skip), SemiActive (approval gate), Active (run), Ambient (run silently). See [IApprovalGate](../src/Ashlar.Core.Application/Trust/Ports/IApprovalGate.cs). |

---

## Layer 4: Runtime Capability Switching

| Component | Status | Notes |
|-----------|--------|-------|
| Hot-Swap Depth | RESOLVED | BehaviorExecutor checks `IStepExecutionMode.GetMode(step.Id)` before step execution. |
| Provider Abstraction | EXISTS | ProviderFactory with multiple backends: openai, azure, ollama, local (ONNX/LLamaSharp), video (SmolVLM2), plus mock/offline/echo for testing. |

---

## Layer 5: Hexagonal Architecture & SDK

| Component | Status | Notes |
|-----------|--------|-------|
| Port Definitions | RESOLVED | Core ports in `Ashlar.Abstractions`; breaking-change and stability expectations documented in [SdkCompatibilityPolicy.md](SdkCompatibilityPolicy.md) alongside [sdk.md](sdk.md). |
| SDK & External Registration | RESOLVED | `AddAshlarSdk`, [sdk.md](sdk.md); reference host `docs/samples/StableSdkHostSample/`; `AshlarSdkBuilder.UseAdaptiveRouting()` marked `[Obsolete]` (experimental). The sample is built by the **Full Platform Readiness Gate** workflow when run manually. |

---

## Layer 6: Testing Framework

| Component | Status | Notes |
|-----------|--------|-------|
| Unit Tests (PathAllowlist, MaxWriteSize) | RESOLVED | Both have comprehensive unit tests. |
| Immutability Tests | RESOLVED | ImmutableCoreAdaptationTests, ImmutableCoreTests. |
| CLI Dogfood Parity | RESOLVED | `ashlar dogfood block1`–`block9`, `closedloop`, `phasef`, `all` exposed. |
| Adversarial Scope Escape Tests | RESOLVED | [AdversarialScopeEscapeTests](../src/Ashlar.Tests.Infrastructure/Tests/Adaptation/AdversarialScopeEscapeTests.cs). |
| Air-Gapped Test Mode | RESOLVED | `--no-network` wired in TestMultiEnvCommand; `make test-multi-env-no-network`; [test-air-gapped-no-network.yml](../.github/workflows/test-air-gapped-no-network.yml) CI workflow. |

---

## Layer 7: Runtime Agent Composition

| Component | Status | Notes |
|-----------|--------|-------|
| Composition Engine | EXISTS | CompositionEngine, ComposedTestRunner, `ashlar compose`. |
| Capability Component Registry | RESOLVED | `CapabilityComponentRegistry` / `ComponentDescriptorValidator`; `InputSchema` and `OutputSchema` required when `SupportLevel` is Stable; seed descriptors carry schemas (see [SeedComponentLibraryAudit.md](SeedComponentLibraryAudit.md)). |
| Seed Component Library | RESOLVED | [SeedComponentLibraryAudit.md](SeedComponentLibraryAudit.md); placeholder descriptors in CapabilityComponentRegistry. |

---

## Layer 8: Instance Mesh

| Component | Status | Notes |
|-----------|--------|-------|
| Shared Adaptation Cache | RESOLVED | FileBasedSharedAdaptationStore, `ashlar mesh sync`, SharedAdaptationCacheTests, DogfoodBlock10SharedAdaptationTests. |
| Sneakernet CLI | RESOLVED | `ashlar mesh export --to <path>`, `ashlar mesh import <path>`. |
| Mesh Capabilities | RESOLVED | `ashlar mesh capabilities` subcommand exists. |
| Instance Discovery | EXISTS | `ashlar mesh`, FileBasedCapabilityAdvertisement. |

---

## Layer 9: Application Suite

| Component | Status | Notes |
|-----------|--------|-------|
| Engineering Release Manager | RESOLVED | Extracted 2026-09-01 to [ashlar-release-manager](https://github.com/IanFrelinger/ashlar-release-manager). In-tree dogfood campaign (`ashlar dogfood campaign`) keeps a release-manager coordinator with specialist sub-agents. |
| Copilot MVP | RESOLVED | Task submission → execution → audit trail via API + web portal. See `docs/CopilotMvpWalkthrough.md`. |
| Document Editor, Spreadsheet, etc. | MISSING | Future work. Framework is the foundation. |

---

## Remaining Gaps (Prioritized)

### P0 — Safety-Critical
- (Resolved: Adversarial scope escape tests added.)

### P1 — Core Loop
- **Runtime mode switch:** RESOLVED. `FileBasedAggressivenessModeStore` persists to ~/.ashlar/agent-mode.json. CLI and background agent (separate processes) share the file; mode changes take effect on next execution cycle without restart.

### P2 — Product Completeness
- (Resolved: Seed library audited; air-gapped CI workflow added.)

### P3 — Vision Completion
- **Application suite:** Future work.
- **Inter-instance trust tiers:** RESOLVED — `PeerTrustTier`, `PeerTrustPolicyResolver`, mesh routing; `ashlar mesh admit` / `ashlar mesh revoke` for peer admission state.

---

## Build Order (Recommended)

All four phases are complete. See `docs/IssueBatch_30-60-90_Roadmap.md` for detailed evidence.

1. ~~**Phase 1 — Safety:** Adversarial scope escape tests.~~ **Done.**
2. ~~**Phase 2 — Agent Completeness:** SemiActive approval gate, Ambient silent implementation.~~ **Done.**
3. ~~**Phase 3 — Framework:** Seed component library audit, air-gapped test E2E.~~ **Done.**
4. ~~**Phase 4 — Product:** Application suite (Copilot MVP, Release Manager vertical).~~ **Done.**
