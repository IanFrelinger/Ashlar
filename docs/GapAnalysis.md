# Gap Analysis: Current State vs North Star (historical)

> **Historical document (marked 2026-08-16).** This analysis predates the trust loop (certification gate, attested sessions, hold-mode autonomy loop) and is kept as the record of the dogfood / observe→improve program. Do not use it as the current state: that is `docs/certification-evidence.md` (the proof ledger), `docs/ProjectTiers.md` (the repo map) and `CHANGELOG.md`. Of the items below, only rows 4 and 5 of the priority table were still open when this banner was added; row 4's README ↔ IntegratorGuide / DogfoodValidation cross-links landed with the README front-door pass the same day.

**North Star:** Every capability must be used by Ashlar on itself. Each block has a dogfood gate that must pass before moving on.

**Reference:** [DogfoodValidation.md](DogfoodValidation.md)

---

## Executive Summary

| Area | Status | Gap Severity |
|------|--------|---------------|
| Dogfood gates (Blocks 1–9 + Phase F) | All implemented in tests; `make dogfood-all` passes | Low |
| CLI dogfood parity | `ashlar dogfood` exposes `block1`–`block9`, `closedloop`, `phasef`, `all` | Resolved |
| Observe → Improve integration | `ashlar improve --from-observation` reads `IPatternStore`; `ashlar improve --continuous` runs observe → improve loop | Resolved |
| Test failure → adaptation trigger | `ashlar ingest-failures` + `TestFailureIngestionBridge` write TRX failures to `ITestFailureStore`; `self-improver` background role runs the loop | Resolved |
| Trust in improve flow | When `ASHLAR_TRUST_ENABLED=1`, `ImproveCommand` registers `SanitizingProviderFactory` in its local DI graph | Resolved |
| Documentation & discoverability | [ExecutionPlan.md](ExecutionPlan.md), [IntegratorGuide.md](IntegratorGuide.md), [SdkCompatibilityPolicy.md](SdkCompatibilityPolicy.md) added; README/GettingStarted still the main onboarding surface | Low |

---

## 1. Dogfood Gates (Blocks 1–9 + Phase F)

### Current State
- All 9 blocks and Phase F (closed loop, changelog, test failure store) have passing tests.
- `make dogfood-all` runs Phase C (blocks 1–6), Phase D+E (blocks 7–9), closed loop, and Phase F.
- CI supports `scope=dogfood` in Cross-Platform Tests workflow.
- `ashlar dogfood` exposes `block1`–`block9`, `closedloop`, `phasef`, and `all`.

### Gap
- **No functional parity gap.** CLI and test runner both cover all dogfood gates.
- **Discoverability gap:** users still need clearer docs on when to use `ashlar dogfood ...` vs `make dogfood-*`.

### Recommendation
Keep CLI parity marked as resolved; prioritize documentation examples that map each `ashlar dogfood` command to the equivalent `make` and test-filter paths.

---

## 2. Observe → Analyze → Adapt Loop

### Current State
- **Observe:** `ashlar observe` watches file system and processes, detects patterns (e.g. `repeated-edits`), stores in `ashlar-patterns.db`.
- **Improve:** `ashlar improve` runs `IBrickStaticAnalyzer` on a path (default: Block 1 Observation path), then adapts from violations.
- **Self-context:** `ashlar self-context` assembles adaptations, executions, and patterns for "what did I change in 24h?"
- **Full pipeline E2E:** `observe` → `analyze bricks` → `adapt` → `improve` → `self-context` run sequentially in tests.

### Gap
- **Resolved.** `ashlar improve --from-observation` queries `IPatternStore` and targets paths derived from recent patterns (staleness/window behavior as implemented in `ImproveCommand`). `ashlar improve --continuous` runs repeated observe-then-improve iterations (`--observe-minutes`, `--interval-minutes`).

### Recommendation
- Keep [NorthStarGapAnalysis.md](NorthStarGapAnalysis.md) and dogfood docs aligned with `--from-observation` / `--continuous` as the supported integration path. Optional: add CLI help examples to README for operators who still run `observe` and `improve` as separate steps.

---

## 3. Self-Improvement Loop (Test Failures → Adaptation)

### Current State
- `ashlar improve --self` runs `ISelfImprovementLoop.RunOnceAsync()`.
- Loop reads from `ITestFailureStore`, processes failures, applies fixes, validates, promotes.
- `DogfoodPhaseFTests` validates `TestFailureStore_RecordAndQuery_ReturnsStoredFailures`.

### Gap
- **Resolved (ingestion path):** `ashlar ingest-failures --trx-path <dir>` parses TRX files and records into `ITestFailureStore` via `TestFailureIngestionBridge` (wire this after `dotnet test` or in CI that publishes `.trx` artifacts).
- **Resolved (background scheduling):** `BackgroundAgentRegistry` supports role `self-improver` (calls `ISelfImprovementLoop` when registered with runners and not skipped by aggressiveness mode). Document agent JSON/config in operator docs as needed.

### Recommendation
- Document a concrete CI recipe: `dotnet test` → TRX artifact → `ashlar ingest-failures` → `ashlar improve --self` (or a `self-improver` agent) for teams adopting the closed loop in production.

---

## 4. Trust & Information Architecture in Improve Flow

### Current State
- Trust phases 1–4 implemented: classification, sanitization, access boundary, audit dashboard.
- `SanitizingProviderFactory` wraps `IProviderFactory` when Trust is enabled via hosting registration (`AddAshlar`).
- `ImproveCommand` builds a lightweight service collection and registers `ProviderFactory` directly.
- Default `FixGenerator` paths are rule-based; cloud LLM usage in improve is optional and configuration-dependent.

### Gap
- **Resolved.** When `ASHLAR_TRUST_ENABLED=1`, `ImproveCommand` registers the kernel's trust composition (`AddTrustServices`, which supplies the PII content filter, the taxonomy and the sanitization audit log) in its CLI-local service collection and wraps `ProviderFactory` in `SanitizingProviderFactory`, so cloud-backed fix generation uses the same sanitization path as hosting when Trust is on. Until the fail-closed change (`CHANGELOG.md`, Unreleased) the wiring had no effect: improve registered `CloudSanitizationProxy` by type alone, DI built it with no content filter, and the proxy passed every prompt through. A proxy with no filter now blocks every non-air-gapped prompt instead.

### Recommendation
- **Done.** `application/src/Ashlar.Tests.CLI/Tests/Commands/ImproveCommandTrustWiringTests.cs` builds the provider registration improve uses. With trust enabled, both provider ports resolve to the one `SanitizingProviderFactory` and its proxy has a content filter (a clean prompt passes; a prompt carrying an email address is blocked for PII). With trust disabled, no sanitizer is registered. The test resolves the registration directly; it does not configure a cloud provider.

---

## 5. Composition & Mesh (Blocks 7–9)

### Current State
- Block 7: `ICompositionEngine.ComposeAsync("test Ashlar CLI")` returns a pipeline.
- Block 8: Parallel test matrix + composed test runner.
- Block 9: Instance mesh advertise/discover; local IPC between two instances.

### Gap
- **Production use:** These are validated in tests. It is unclear if `ashlar compose` and `ashlar mesh` are used in real workflows (e.g. CI, local dev) or only for validation.
- **Discoverability:** README lists `ashlar compose` and `ashlar mesh` but does not explain when to use them or how they fit the North Star.

### Recommendation
- Add a "Dogfood workflows" section to README: e.g. "Run `ashlar compose test ashlar-cli` to compose a test agent for Ashlar CLI."
- Consider a `ashlar dogfood full` that runs observe → improve → self-context → changelog as a single workflow.

---

## 6. Documentation & Discoverability

### Current State
- README lists CLI commands including `ashlar changelog`, `ashlar dogfood`, `ashlar improve`.
- DogfoodValidation.md documents gates and validation commands.
- [ExecutionPlan.md](ExecutionPlan.md), [IntegratorGuide.md](IntegratorGuide.md), and [SdkCompatibilityPolicy.md](SdkCompatibilityPolicy.md) document roadmap, external integration, and SDK semver expectations.
- GettingStarted.md may still lag on dogfood, changelog, or improve-specific walkthroughs.

### Gap
- Onboarding docs may still under-link to the new planning/integration docs above.
- The North Star principle ("every capability used by Ashlar on itself") may still be easy to miss for first-time readers.

### Recommendation
- Cross-link README / GettingStarted to ExecutionPlan, IntegratorGuide, and SdkCompatibilityPolicy where operators and integrators land first.
- Add a short "North Star & Dogfood" subsection to README with links to DogfoodValidation.md and `make dogfood-all` if not already present.
- Add "Changelog from promoted changes" to GettingStarted: `ashlar changelog --since 7d`.

---

## 7. Summary: Priority Order

| Priority | Gap | Effort | Impact |
|----------|-----|--------|--------|
| 1 | ~~Observe → improve integration~~ | — | Resolved (`--from-observation`, `--continuous`) |
| 2 | ~~Trust in improve flow~~ | — | Resolved (`ASHLAR_TRUST_ENABLED=1` + `SanitizingProviderFactory` in improve DI) |
| 3 | ~~Test failure ingestion~~ | — | Resolved (`ashlar ingest-failures`, `TestFailureIngestionBridge`; `self-improver` role) |
| 4 | Documentation cross-links (README/GettingStarted ↔ ExecutionPlan, IntegratorGuide, dogfood) | Low | Partly resolved 2026-08-16: README links `docs/IntegratorGuide.md` (Where to start, Integrate lane) and `docs/DogfoodValidation.md` (Observe / adapt / improve); `docs/DocsIndex.md` Start Here lists IntegratorGuide. ExecutionPlan is historical and is deliberately not linked from the front door. |
| 5 | Composition & mesh production workflows (see Section 5) | Medium | Medium – operational adoption beyond tests |

---

## Appendix: What’s Working Well

- All dogfood gates pass; CI supports dogfood scope.
- Observe, adapt, improve (`--from-observation`, `--continuous`), self-context, changelog, compose, mesh are implemented and usable.
- `ashlar ingest-failures` feeds `ITestFailureStore`; background `self-improver` role complements `ashlar improve --self`.
- Trust & Information Architecture phases 1–4 are implemented.
- Full pipeline E2E runs observe → analyze → adapt → improve → self-context.
- `ashlar improve --self` runs the self-improvement loop from test failures.
- Cross-platform testing (Docker, portable, multi-env) is in place.
