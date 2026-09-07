# Ashlar 30/60/90 Gap-Closure Issue Batch

Use this file to create GitHub issues in the recommended execution order.

**Status as of April 2026:** All 14 items across Phase 30, Phase 60, and Phase 90 are complete.

## Suggested labels

- `roadmap`
- `phase-30` / `phase-60` / `phase-90`
- `area:product`
- `area:trust`
- `area:mesh`
- `area:sdk`
- `area:runtime`
- `area:devex`
- `area:release`

---

## 1) [Phase 30] Product pilot: secure engineering copilot MVP — ✅ COMPLETE

**Status:** Implemented. Copilot task API (`POST /api/copilot/task`, `GET /api/copilot/tasks`), web portal with Quick chat panel, trust audit in responses, trust dashboard accessible from product flow. Docker compose launch works.

**Evidence:** See `docs/CopilotMvpWalkthrough.md` for the first-success walkthrough.

---

## 2) [Phase 30] Mesh trust tiers (trusted vs untrusted peers) — ✅ COMPLETE

**Status:** Implemented. `PeerTrustTier` on `PeerInfo`, `PeerTrustPolicyResolver`, `ashlar mesh admit` / `ashlar mesh revoke`, `--set-trust-tier` CLI support. Routing honors trust policy under all peer availability states.

**Evidence:** Tests in `DogfoodBlock9Tests`, `PeerToPeerRoutingSmokeTests`. North Star gap analysis marks this RESOLVED.

---

## 3) [Phase 30] Unified knowledge query layer — ✅ COMPLETE

**Status:** Implemented. `IKnowledgeQueryService` / `KnowledgeQueryService` aggregates adaptation log, pattern store, and user knowledge. Exposed as `GET /api/knowledge/query` in API. Supports filters, pagination, provenance.

**Evidence:** Registered in `AshlarServiceCollectionExtensions.AddAshlar()`, used by self-context and copilot flows.

---

## 4) [Phase 30] SDK and port stabilization v1 — ✅ COMPLETE

**Status:** Implemented. `AddAshlarSdk` builder, reference sample at `docs/samples/StableSdkHostSample/`, compatibility policy in `docs/SdkCompatibilityPolicy.md`. SDK sample built in CI via readiness gate workflow. Integration guide at `docs/SdkIntegrationGuide.md`.

**Evidence:** SDK sample builds and runs standalone. Three reference integration archetypes documented (CLI tool, background service, air-gapped).

---

## 5) [Phase 30] SLO evidence pipeline for release decisions — ✅ COMPLETE

**Status:** Implemented. `runtime-release-gate.yml` and `runtime-release-promotion.yml` emit `evidence.json` and `evidence.md` artifacts. CLI `ci runtime-gate` produces SLO evidence. `ci release-bundle` aggregates gate results.

**Evidence:** Workflows upload evidence artifacts; RC checklist references concrete artifact paths.

---

## 6) [Phase 60] Capability component registry completion — ✅ COMPLETE

**Status:** Implemented. `CapabilityComponentRegistry` with seed components, `ComponentDescriptorValidator` enforcing required metadata (id, capability, display name, semver, input/output schemas for Stable components). Composition engine uses registry for filtering.

**Evidence:** `CompositionRegistryValidationTests`, `ComponentDescriptorValidatorSchemaTests` in test suite.

---

## 7) [Phase 60] `doctor --fix` safe remediation mode — ✅ COMPLETE

**Status:** Implemented. `ashlar doctor --fix` with `--dry-run`, `--json`, `--yes` options. Consent-gated remediation actions. JSON output includes attempted fixes and outcomes. `DoctorRemediation.cs` implements fixable problem taxonomy.

**Evidence:** `DoctorCommandTests` in CLI test suite. Readiness gate workflow runs `doctor --fix --dry-run`.

---

## 8) [Phase 60] Onboarding reliability gate expansion — ✅ COMPLETE

**Status:** Implemented. `full-platform-readiness-gate.yml` runs setup → discovery → dry-run on 7 platforms (Linux, macOS, Windows native + Ubuntu/Alpine/Debian containers + Docker CLI image). `onboarding-quickstart-gate.yml` provides failure taxonomy on manual runs.

**Evidence:** `full-platform-readiness-gate.yml` validates all 7 platforms when dispatched (Linux, macOS, Windows, Ubuntu/Alpine/Debian containers, Docker CLI image).

---

## 9) [Phase 60] Single-shot release gate orchestration command — ✅ COMPLETE

**Status:** Implemented. `ashlar ci release-bundle --profile <default|quick|full> --output-dir <path>` runs configured gate bundle and produces unified `release-bundle-report.json` + `.md`.

**Evidence:** `CiCommand.ExecuteReleaseBundleAsync` in CLI. Documented in `docs/ReleaseCandidateChecklist-v1.md`.

---

## 10) [Phase 60] Regulated-environment trust policy packs — ✅ COMPLETE

**Status:** Implemented. Three predefined policy packs in `config/trust-packs/`: `strict-enterprise.json`, `internal-only.json`, `air-gapped.json`. CLI `ashlar trust pack list | describe | apply --id <pack>`. Active pack status visible in trust dashboard.

**Evidence:** `TrustPolicyPackRegistryTests` in test suite.

---

## 11) [Phase 90] First application-suite vertical — ✅ COMPLETE

**Status:** Implemented, then extracted 2026-09-01 to [ashlar-release-manager](https://github.com/IanFrelinger/ashlar-release-manager) (nuget.org consumer). In-tree, `ashlar dogfood campaign` runs a release-manager coordinator whose specialist sub-agents report docs-drift, regression, and developer-tool verdicts.

**Evidence:** `docs/background-agents/examples/dogfood-campaign.json`, `docs/DogfoodCampaign.md`. Specialists publish observations; silence is fail-closed.

---

## 12) [Phase 90] Multi-instance mesh governance — ✅ COMPLETE

**Status:** Core implementation done. `ashlar mesh admit` / `ashlar mesh revoke` for peer admission/revocation. `PeerTrustTier` classification. Trust tier in routing decisions. Audit events for governance actions.

**Evidence:** `MeshCommand` admit/revoke handlers, `PeerTrustPolicyResolver`, mesh routing tests.

---

## 13) [Phase 90] Load/performance certification lane — ✅ COMPLETE

**Status:** Implemented. `perf-certification.yml` workflow with threshold enforcement (95% pass rate, 0 allowed failures), machine-readable report (`perf-certification-report.json`), GitHub step summary, and 90-day artifact retention for trend analysis.

**Evidence:** Workflow artifacts include TRX results, metadata, and certification report with PASS/FAIL verdict.

---

## 14) [Phase 90] External integrator program and reference integrations — ✅ COMPLETE

**Status:** Implemented. SDK integration guide (`docs/SdkIntegrationGuide.md`) with three reference archetypes (CLI tool, background service, air-gapped). Working sample at `docs/samples/StableSdkHostSample/`. Stability tiers documented (Stable/Experimental/Internal). SDK sample built in CI readiness gate for compatibility smoke.

**Evidence:** SDK sample compiles and runs. Integration guide covers registration, key abstractions, and extension points.
