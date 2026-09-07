# Audit: Built-In Agents & Validation Paths

**Date:** 2026-09-05  
**Scope:** Background agents, orchestration, dogfood gates, observe/adapt/improve loops, MCP/A2A surfaces, CLI doctor/report/disarm commands, and current validation approaches  
**Status:** Audit only — no feature implementation  

---

## Executive Summary

Ashlar implements a sophisticated multi-agent system with autonomous self-extension capabilities, bounded by certification gates and fail-closed safety invariants. The validation architecture spans five tiers: unit tests, certification gates (only required CI check), dogfood validation (10 blocks + closed-loop), adversarial corpus (fixtures for every breach), and production-style integration tests.

**Key Finding:** The infrastructure for safe autonomous operation exists and is enforced (4 safety invariants proven in `SELF-EXTEND-AUDIT.md`), but the validation surface has a **concentration risk**: `cert-gate` is the only required status check on master. Eight workflows were deleted after going red, and a single branch-protection toggle disables all merge-blocking conventions simultaneously.

**Recommendation:** Distribute validation responsibility across multiple required checks with always-report jobs, or formalize `cert-gate`'s role as the single-point-of-trust and harden its ownership/review process.

---

## 1. Agent & Capability Map

### 1.1 Background Agent System

Ashlar's background agents are **persistent, scheduled processes** that run autonomously based on configuration files. The system is built around three core components:

| Component | Location | Responsibility |
|-----------|----------|----------------|
| `BackgroundAgentService` | `src/Ashlar.BackgroundAgents/Services/` | Hosted service; loads configs, creates agents, schedules execution |
| `BackgroundAgentRegistry` | `src/Ashlar.BackgroundAgents/Registry/` | Central registry; tracks instances, enforces safety invariants, manages lifecycle |
| `BackgroundAgentConfigLoader` | `src/Ashlar.BackgroundAgents/Configuration/` | Loads agent definitions from JSON configs |

**Agent Roles** (from config schema):

- **`monitor`** — observe-only; health checks, metrics collection, no write access
- **`tester`** — execute test suites, report results, no production writes
- **`extender`** — self-extend path; can propose code changes through certification gate
- **`optimizer`** — tune performance parameters within bounds
- **Custom roles** — user-defined via configuration

### 1.2 Agent Discovery & Configuration

Agents are defined in JSON configuration files matching this schema:

```json
{
  "BackgroundAgents": {
    "Agents": [
      {
        "Id": "unique-agent-id",
        "Name": "Human-readable name",
        "Role": "monitor | tester | extender | optimizer | custom",
        "ModelProvider": "deterministic | ollama | openai | azure",
        "Commands": ["list", "of", "allowed", "commands"],
        "Schedule": {
          "Type": "Interval | Cron",
          "Interval": "HH:MM:SS",
          "CronExpression": "0 0 * * *"
        },
        "Enabled": true,
        "MaxDataSensitivity": "Public | Internal | Confidential"
      }
    ]
  }
}
```

**Example agents** (from `docs/background-agents/examples/`):

1. **`minimal-agent.json`** — Health monitor with 5-minute interval, deterministic provider
2. **`dogfood-extender.json`** — Self-extender with 2-hour interval
3. **`dogfood-tester.json`** — Test runner for CI validation
4. **`dogfood-optimizer.json`** — Performance tuning agent
5. **`air-gapped-deterministic.json`** — Offline-only monitor (no network)
6. **`full-agent-with-rag-and-websearch.json`** — Advanced agent with RAG and web search

### 1.3 Safety Front Doors (Operator Controls)

Four primary CLI commands control autonomous behavior:

#### 1.3.1 `ashlar background-agent mode`

Controls the **aggressiveness mode** for all self-extend agents:

```bash
# Get current mode
ashlar background-agent mode get [--json]

# Set mode
ashlar background-agent mode set --value <passive|semi-active|active|ambient>
```

**Modes:**

- **`Passive`** (default, fail-closed) — Extenders observe but never run; monitors/testers continue
- **`SemiActive`** — Extenders propose changes for human approval (requires `IApprovalGate`)
- **`Active`** — Extenders auto-admit changes within budget (see Extension Ceiling below)
- **`Ambient`** — Reserved for future context-aware scheduling

**Implementation:** `FileBasedAggressivenessModeStore` reads/writes a mode file checked every cycle. Missing/corrupt file defaults to Passive. Re-arm requires explicit operator action.

#### 1.3.2 `ashlar background-agent disarm`

**Emergency stop** — immediately sets mode to Passive:

```bash
ashlar background-agent disarm [--reason "why"] [--json]
```

- Logs warning with reason
- Fail-closed: partial write still results in Passive mode
- No restart required; extenders stop at next cycle
- Re-arm: `ashlar background-agent mode set --value active`

**Location:** `application/src/Ashlar.CLI/Commands/BackgroundAgent/ModeBackgroundAgentCommand.cs:90-129`

#### 1.3.3 `ashlar background-agent report`

**Overnight operator dashboard** — answers "what did the node do while I was away?"

```bash
ashlar background-agent report [--since-hours 24] [--project .] [--json]
```

Joins two durable, append-only logs:

1. **Cycle events** (`cycles.jsonl`) — execution count, success/fail, tools executed/denied
2. **Admission gate records** (`.ashlar/gates/`) — proposals held/admitted/rejected with reasons

**Read-only** — safe to run against live nodes. Fail-closed on corrupt gate records (reports error, never silently summarizes as empty).

**Location:** `application/src/Ashlar.CLI/Commands/BackgroundAgent/ReportBackgroundAgentCommand.cs`

#### 1.3.4 `ashlar background-agent daemon`

Runs the background agent scheduler:

```bash
ashlar background-agent daemon [--duration 10m] [--project .]
```

- Loads configs from `.ashlar/background-agents/` or `ASHLAR_BACKGROUND_AGENTS_PATH`
- Respects aggressiveness mode file
- Enforces extension ceiling (see Section 1.4)
- Logs all cycles to `cycles.jsonl`

### 1.4 Extension Ceiling (Runaway Prevention)

`ExtensionCeiling` enforces three hard limits on autonomous cycles (Invariant D, proven in `SELF-EXTEND-AUDIT.md`):

| Ceiling | Default | Environment Override | Agent Override |
|---------|---------|---------------------|----------------|
| `MaxLineageDepth` | 1 (root + children only) | `ASHLAR_EXTENSION_MAX_LINEAGE_DEPTH` | `Parameters.MaxLineageDepth` |
| `MaxUnattendedCycles` | 8 since last human re-arm | `ASHLAR_EXTENSION_MAX_UNATTENDED_CYCLES` | `Parameters.MaxUnattendedCycles` |
| `MaxCyclesPerHour` | 4 in trailing hour | `ASHLAR_EXTENSION_MAX_CYCLES_PER_HOUR` | `Parameters.MaxCyclesPerHour` |

**Enforcement:** `BackgroundAgentRegistry` checks `ExtensionCeiling.TryBeginCycle()` *before* `ISelfExtendRunner.RunAsync()`. Decision and spend happen under one lock (no check-then-act race). Overrides can only *lower* defaults.

**Re-arm:** `BackgroundAgentRegistry.RearmExtension(agentId)` or process restart. Clears unattended count but *not* trailing-hour rate.

**Location:** `src/Ashlar.BackgroundAgents/Registry/BackgroundAgentRegistry.cs` (extender branch)

### 1.5 Orchestration Layer

The `Orchestration` subsystem coordinates multi-agent workflows:

| Component | Location | Role |
|-----------|----------|------|
| `OrchestrationArchitect` | `src/Ashlar.Orchestration/` | Designs multi-step plans |
| `AgentFactory` | `src/Ashlar.Orchestration/Agents/` | Creates agent instances from specs |
| `ToolCallingAgent` | `src/Ashlar.Orchestration/Agents/` | Executes ReAct loops with tool calling |
| `HotSwappableModel` | `src/Ashlar.Orchestration/Models/` | Runtime model swapping for certified updates |

**Runtime specs** define orchestration behavior:

- `docs/runtime/specs/small_task.orchestration.runtime-spec.json`
- `docs/runtime/specs/medium_task.orchestration.runtime-spec.json`
- `docs/runtime/specs/large_task.orchestration.runtime-spec.json`

### 1.6 MCP & A2A Protocol Surfaces

Ashlar exposes two industry-standard agent protocols (feature-flagged off by default):

#### MCP (Model Context Protocol)

**Server** (Ashlar as tool provider):

- **Stdio:** `src/Ashlar.Mcp.Server.Host/` — local AI clients (Claude Desktop, IDEs)
- **HTTP:** `/api/mcp` endpoint in `Ashlar.API` (behind API key auth)
- **Allowlist:** Only tools in `Ashlar:Mcp:Server:ExposedToolIds` are callable
- **Argument overrides:** Operator can pin arguments (e.g., `repo.fs.read` root path)

**Client** (Ashlar consuming external MCP servers):

- Connects to HTTP MCP servers at startup
- Tools surface as `ITool` with `mcp:{server}:{tool}` IDs
- **Drift faults:** Re-lists tools periodically; changed definitions mark tool faulted
- **Secrets:** API keys via env vars (`ApiKeyEnvVar` per server)

#### A2A (Agent2Agent)

**Server** (external agents calling Ashlar):

- Endpoints: `/api/a2a/{agentId}` + `/.well-known/agent-card.json`
- **Allowlist:** `ExposedAgentIds` + opt-in `ExposeByCoordinationProtocol`
- **Execution:** Mirrors gRPC facade; bounded budget

**Client** (Ashlar calling remote agents):

- Scheme: `a2a+https://peer.example.com/api/a2a/agent`
- Capability routing, health filtering work unchanged
- Correlation/span context propagates as protocol metadata

**Status:** All four directions landed (PRs #266, #268, #269, #270). Feature flags default **off** (`Ashlar:Mcp:*`, `Ashlar:A2A:*`). Refused under `AirGapped` and `SecureWorkstation` profiles.

**Location:** `docs/architecture/ProtocolIntegration-MCP-A2A.md`

### 1.7 Observe → Adapt → Improve Loop

The self-improvement engine consists of three phases:

#### 1.7.1 Observe

**`ObservationContextBrick`** monitors:

- File system events (edits, creates, deletes)
- Execution traces (agent runs, tool calls)
- Pattern recognition (repeated edits, test failures)
- Test results and CI outcomes

**Storage:** `IObservationStore` (file-based by default: `.ashlar/observations/`)

#### 1.7.2 Adapt

**`AdaptationEngine`** processes observations:

1. **Static analysis** (`IBrickStaticAnalyzer`) — scans code for issues
2. **Pattern matching** — identifies recurring problems
3. **Proposal generation** — suggests improvements
4. **Promotion** (`IAdaptationPromoter`) — marks validated fixes

**Gate:** Every adaptation goes through certification (see Section 2)

#### 1.7.3 Improve

**`IImprover`** applies approved adaptations:

- Recompiles bricks with improvements
- Runs full certification gate
- Hot-swaps on admit, holds on reject
- Stores outcomes in `IAdaptationStore`

**Dogfood status:** All 10 blocks implemented (Block 1–9 + closed-loop). See Section 3.2.

---

## 2. Validation End-to-End for Autonomous Loops

### 2.1 The Certification Gate (`cert-gate`)

**Only required status check on `master`** — all other gates are advisory.

**Test filter:**
```bash
FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Certification
|FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Adaptation.GenerationSafety
|FullyQualifiedName~AstMutationEngineTests
```

**What it proves:**

1. **Atom portability** — generate, certify, pack, consume, execute (5 steps)
2. **Gate teeth** — strong witness admits with `escape_rate=0`, weak witness rejects
3. **General generation** — correct code admits, buggy code rejects
4. **Composition certification** — correct wiring admits, broken seams reject
5. **Dogfood campaigns** — honest admits, buggy rejects (19 tests)

**Evidence ledger:** `docs/certification-evidence.md` — every row cites CI run URL

### 2.2 Four Safety Invariants (All Enforced)

Proven in `docs/SELF-EXTEND-AUDIT.md` with dedicated test suites:

| ID | Invariant | Test Suite | Enforcement Point |
|----|-----------|------------|-------------------|
| **A** | Cert-gate inheritance | `SelfExtendInvariantACertGateTests` | `SelfProducedBrickCertificationPolicy` verifies admitted record via `CertificationTrustVerifier` |
| **B** | Monotonic policy narrowing | `SelfExtendInvariantBPolicyNarrowingTests` | `AgentPolicyNarrowingValidator` at `RegisterAsync`; machine-origin agents require `ParentId` and narrowed envelope |
| **C** | Fail-closed default | `SelfExtendInvariantCHumanAdmissionTests` | `FileBasedAggressivenessModeStore` defaults to Passive on missing/corrupt file |
| **D** | Recursion ceiling | `SelfExtendInvariantDRecursionCeilingTests` | `ExtensionCeiling` + `ExtensionLedger.TryBeginCycle()` before runner call |

**Control flow trace:** `BackgroundAgentService.ExecuteAsync` → `BackgroundAgentRegistry.ExecuteAgentAsync` → mode gate → ceiling gate → `ISelfExtendRunner.RunAsync` → `ToolCallingAgent.RunCycleAsync` → policy engine per tool call.

### 2.3 Admission Gate Stages

Every self-extend proposal flows through:

1. **Analyzer fence** — static analysis (no forbidden patterns)
2. **Witness (correctness)** — human-authored test cases never shown to proposer
3. **Mutation testing** — witness must kill mutants (`escape_rate` threshold)
4. **Determinism check** — repeated execution produces identical output
5. **Signature & storage** — signed admission record or rejection reason

**Gate store:** `.ashlar/gates/` — append-only, durable, read by `ashlar background-agent report`

**States:** `Held` (awaiting review), `Admitted` (certified + applied), `Rejected` (reason logged)

### 2.4 Post-Apply Canary (Auto-Rollback)

After an admitted change is applied:

1. **Canary verification** runs automatically
2. **Failure → revert** — change is rolled back, marked rejected in report
3. **Success → persists** — change stays in codebase

**Evidence:** Post-apply reverts show as "post-apply canary failed" in rejection reasons. Admission gate record stays `Admitted` (decision was made before canary).

### 2.5 Hold Mode vs. Auto-Admit

**Hold mode** (`HoldAdmission=true` default):

- Proposals fully certified (all gates pass)
- State: `CertifiedButHeld`
- Operator reviews with `ashlar background-agent proposals list`
- Manual admit: `ashlar background-agent proposals admit <id>`

**Auto-admit** (Active mode + `HoldAdmission=false`):

- Certified proposals auto-apply within budget
- Extension ceiling enforces bounds
- Post-apply canary gates actual execution
- Operator audit trail in `report` command

### 2.6 Adversarial Corpus

Every breach or near-miss is a fixture: `tests/adversarial-corpus/fixtures/<id>/`

**Fixture schema:**
```json
{
  "id": "fixture-id",
  "class": "A (judged≠shipped) | B (author code in certifier) | C (mutation) | D (drift)",
  "item": "Round-10 item owning the fence",
  "phase": "load (loader throws) | certify (gate verdict)",
  "expect": "refuse | admit",
  "reasonContains": "substring in exception/reason"
}
```

**Test:** `AdversarialCorpusTests` replays all fixtures in `cert-gate`. Unintended verdict change = regression.

**Ledger:** `tests/adversarial-corpus/ledger.json` — one row per proven attack

**Notable fixtures:**

- `a3-author-nuget-config` — malicious config injection (refused at load)
- `b2-stackalloc-exhaust` — stack overflow attempt (refused by analyzer)
- `b2-file-write` — unauthorized I/O (refused by policy)
- `a8-gate-emitted-hash-bind` — hash collision attack (refused by signature check)

### 2.7 Test Ownership Convention

**Prevents silent test omission** (how `Ashlar.Commercial.Tests.Fleet.Host` failed for 10 days):

1. Every test project must have one row in `ci/test-ownership.tsv`
2. Every row must point at existing project
3. No `UNOWNED` row past expiry
4. Enforced by `TestOwnershipConventionTests` in `cert-gate`

**Location:** `ci/cert-gate-assertions.md:26`

---

## 3. Gaps Between "Agents Exist" and "Safe Autonomous Self-Extension"

### 3.1 Current State Assessment

**What works today:**

✅ Four safety invariants enforced and tested  
✅ Certification gate proven with evidence ledger  
✅ Fail-closed defaults (Passive mode, missing files)  
✅ Extension ceiling prevents runaway  
✅ Post-apply canary with auto-rollback  
✅ Operator front doors (mode, disarm, report, daemon)  
✅ Adversarial corpus with 30+ fixtures  
✅ Dogfood validation (10 blocks + closed-loop)  
✅ MCP/A2A surfaces (feature-flagged)  

**Hold-mode status:**

⚠️ In-process autonomy loop is **spike-grade**  
⚠️ Ships in hold mode (`HoldAdmission=true` default)  
⚠️ Evidence is local spike runs, not prod deployments  
⚠️ Ledger records one equivalent-mutant soundness gap (S5)  

**Production status:**

✅ Operator-governed self-extend path (A0–A5) is **supported**  
✅ Background-agent extender proposes against policy  
✅ Every proposal faces full admission gate  
✅ In-process build course (non-compiling proposals rejected)  
✅ Post-apply canary auto-rolls-back failures  
✅ Ships **sealed** — fresh nodes change nothing after deploy  
✅ Dial raised deliberately: `passive` → `proposing` → `active`  
✅ Two safety front doors: `report` (what ran) + `disarm` (emergency stop)  

### 3.2 Dogfood Validation Status

All 10 blocks implemented + closed-loop integration:

| Block | Gate | Test | Status |
|-------|------|------|--------|
| 1 | Core observation on self | `DogfoodBlock1Tests.ObservationPipeline_...` | ✅ PASS |
| 2 | Analyzers on self | `DogfoodBlock2Tests.StaticAnalyzer_...` | ✅ PASS |
| 3 | Adaptation engine improves own bricks | `DogfoodBlock3Tests.AdaptationEngine_...` | ✅ PASS |
| 4 | Promote fixes via inheritance | `DogfoodBlock4Tests.PromoteAshlarFix_...` | ✅ PASS |
| 5 | Autonomy controls on dev workflow | `DogfoodBlock5Tests.AutonomyControls_...` | ✅ PASS |
| 6 | Self-context answers "24h changes?" | `DogfoodBlock6Tests.SelfContextAssembler_...` | ✅ PASS |
| 7 | Composition engine composes for self | `DogfoodBlock7Tests.CompositionEngine_...` | ✅ PASS |
| 8 | Parallel test matrix on self | `DogfoodBlock8Tests.ParallelTestMatrix_...` | ✅ PASS |
| 8.1 | Composed test runner | `DogfoodBlock8ComposedTests...` | ✅ PASS |
| 9 | Instance mesh discovery | `DogfoodBlock9Tests.InstanceMesh_...` | ✅ PASS |
| 9.1 | Local IPC mesh | `DogfoodBlock9LocalIpcTests...` | ✅ PASS |
| Closed | Improve flow end-to-end | `DogfoodClosedLoopTests.ImproveFlow_...` | ✅ PASS |

**Enforcement:** `make dogfood-*` targets run specific blocks. CI: `gh workflow run "Cross-Platform Tests" -f scope=dogfood`

**Location:** `docs/DogfoodValidation.md`

### 3.3 Identified Gaps

#### 3.3.1 Concentration Risk

**Finding:** `cert-gate` is the **only required status check** on `master`. All other gates (kernel-gate, layer-boundary, coverage, etc.) are **advisory**.

**Impact:**

- One branch-protection toggle disables all merge-blocking conventions
- Eight workflows deleted after going red (see `docs/CiGateInventory.md`)
- Single point of failure for trust enforcement

**Evidence:**

```bash
gh api repos/IanFrelinger/Ashlar/branches/master/protection
# → required_status_checks.contexts == ["cert-gate"]
```

**History:** Earlier revisions of `CiGateInventory.md` listed 15 required contexts. That was never the repository setting.

**Why others aren't required:** Path-filtered workflows only report when filters match. A PR not touching those paths gets no status → blocks merge forever. Adding requires:

1. Always-report job (no path filter, reports success when filtered job skips), OR
2. Drop path filter (pay run cost on every PR), OR
3. Move filter inside job (`dorny/paths-filter` or `git diff` step)

**Location:** `docs/CiGateInventory.md:10-26`, `ci/cert-gate-assertions.md`

#### 3.3.2 Equivalent Mutant Problem

**Finding:** Campaign S5 (`semver-parse` objective) passes all correctness cases but is rejected at mutation gate with `escape_rate=0.04` due to an equivalent mutant (redundant length guard `0 => 2` that no witness can kill).

**Impact:** Correct candidates can never certify when witness cannot distinguish equivalent mutants.

**Mitigation strategies:**

1. **Mutant filtering** — detect equivalent mutants via symbolic execution or SMT solver
2. **Witness strengthening** — require observable behavior for all branches
3. **Human override** — operator can admit after manual review
4. **Acceptance threshold** — low escape rates (<5%) admitted with warning

**Location:** `docs/certification-evidence.md:29` (Campaign S5 row)

#### 3.3.3 Model Re-Emission at 7B

**Finding:** 7B models (codellama, qwen2.5-coder) re-emit identical bytes on repair (6/6 in testing), suggesting limited working memory or overfitting to repair pattern.

**Impact:** Repair loop effectiveness depends on model capacity. Single-shot success rate swings with formatting noise.

**Mitigation:** Campaign S3 shows bounded retry (2-attempt budget) makes 7B models usable. Contract precision ("NEVER null") more important than model size for convergence.

**Location:** `docs/certification-evidence.md:27` (Campaign S3 row)

#### 3.3.4 No Durable A2A Tasks

**Finding:** A2A server uses synchronous, in-memory task store. Tasks survive only process lifetime.

**Impact:** External agents lose task results on Ashlar restart.

**Status:** Deferred. Listed in `docs/architecture/ProtocolIntegration-MCP-A2A.md:206-210`.

**Required for production A2A:**

- Durable task store (SQLite or file-based)
- SSE streaming for long tasks
- Push notifications for async completion

#### 3.3.5 MCP Client Stdio Not Supported

**Finding:** MCP client only supports HTTP servers. Stdio child processes require command allowlist design.

**Impact:** Cannot consume local MCP servers run as subprocesses (e.g., `claude mcp add local-tool -- python script.py`).

**Status:** Deferred. Listed in `ProtocolIntegration-MCP-A2A.md:206`.

**Required:**

- Command allowlist configuration
- Process lifecycle management
- Stdio pipe handling with backpressure

#### 3.3.6 AppendOnly Write Ceiling Not Automated

**Finding:** Production has 7 known `File.AppendAllText` / `AppendAllLines` / `AppendText` call sites, frozen in allowlist by `AppendOnlyWriterConventionTests`. New appenders fail `cert-gate`.

**Impact:** Prevents unbounded log growth, but requires manual allowlist updates for legitimate new appenders.

**Mitigation:** `CLOSING-PLAN.md` Phase 5 bounds these at write path. Convention stops count from growing.

**Location:** `ci/cert-gate-assertions.md:33`

#### 3.3.7 Forge Write Floor Separate from Package Import

**Finding:** `ForgeApplier` and `PackageImport` had divergent governance floors. Package import used `Path.Combine(repoRoot, path)` with no containment, allowing `../../x` escapes and governance path writes (`Directory.Build.targets` → RCE on next `dotnet build`).

**Status:** **Fixed**. Both now route through `MediatedWritePath.Refuse()` (shared floor). Tests: `ForgeApplierGovernanceTests`, `SharedAdaptationGovernanceTests`.

**Location:** `ci/cert-gate-assertions.md:38-39`

---

## 4. Proposed Validation Matrix

### 4.1 Five-Tier Validation Architecture

| Tier | What | Where | Frequency | Blocking? |
|------|------|-------|-----------|-----------|
| **1. Unit** | xUnit tests, fast hermetic checks | `src/Ashlar.Tests.*` (39 projects) | Every `dotnet build` | Advisory |
| **2. Cert-Gate** | Certification + conventions + adversarial | `cert-gate.yml` (19+ tests) | Every PR, every push to master | ✅ **Required** |
| **3. Dogfood** | Self-application (10 blocks + closed-loop) | `make dogfood-*` targets | On-demand, scheduled | Advisory |
| **4. Adversarial** | Breach replays (30+ fixtures) | `AdversarialCorpusTests` in cert-gate | Every cert-gate run | ✅ **Required** (via cert-gate) |
| **5. ProdStyle** | Production wiring, virtual API stack | `[Trait("Category", "ProdStyle")]` | `make test-prod-style` | Advisory |

### 4.2 Proposed Required Checks (Guarded Expansion)

**Current:** Only `cert-gate` is required.

**Proposed Phase 1** (add always-report jobs to these):

1. **`cert-gate`** ✅ (already required)
2. **`layer-boundary`** — uses `paths: "**"` (effectively unfiltered), could be required today
3. **`kernel-coverage`** — add always-report job, then require

**Proposed Phase 2** (after Phase 1 proven stable):

4. **`uat-gate`** — no path filter (deliberate), safe to require
5. **`dependency-boundary`** — add always-report job

**Rationale:** Gradual expansion with monitoring. Each new required check needs:

- Always-report job or no path filter
- 30-day green streak before requiring
- Rollback plan documented in PR description

### 4.3 Adversarial Injection Testing

**Current process:**

1. Breach discovered or hypothesized
2. Fixture created in `tests/adversarial-corpus/fixtures/<id>/`
3. `expect.json` written with desired verdict (refuse/admit + reason)
4. Row added to `ledger.json`
5. `AdversarialCorpusTests` runs all fixtures in cert-gate
6. Fix lands if test transitions from fail → pass

**Proposed additions:**

#### 4.3.1 Mutation-Based Injection

Generate fixtures automatically from mutation operators:

- **Governance mutations** — flip `./` to `../`, backslash to forward slash, case changes
- **Signature mutations** — rewrite gate names, modify signed bytes, flip public keys
- **Dependency mutations** — inject forbidden dependencies, swap trust roots
- **Schema mutations** — downgrade record versions, omit required fields

**Implementation:** <planned: scripts/generate-adversarial-fixtures.sh>

#### 4.3.2 Fuzz Testing

Apply fuzzing to:

- **Config loader** — malformed JSON, missing fields, type mismatches
- **Gate store** — corrupt records, duplicate IDs, out-of-order timestamps
- **Mode file** — invalid mode values, partial writes, encoding attacks

**Tools:** AFL++, libFuzzer, or C# SharpFuzz

#### 4.3.3 Red Team Campaign

Quarterly red team exercises:

1. **Goal:** Breach one safety invariant (A, B, C, or D)
2. **Constraints:** Public repository, no social engineering
3. **Outcome:** Successful breach → new fixture + fix in same PR

### 4.4 Test Ownership Expansion

**Current:** `ci/test-ownership.tsv` tracks 39 test projects, one row each.

**Proposed:**

1. **Gate ownership** — assign owner team per CI workflow
2. **Fixture ownership** — track adversarial fixtures by class (A/B/C/D)
3. **Expiry audits** — quarterly review of `UNOWNED` rows

**Enforcement:** Expand `TestOwnershipConventionTests` to cover gates and fixtures.

### 4.5 Dogfood Expansion

**Implemented (10 blocks + closed-loop):**

- ✅ Blocks 1–9 (observation through mesh)
- ✅ Closed-loop (improve flow end-to-end)
- ✅ Composed test runner (Block 8.1)

**Proposed additions:**

#### Phase G: Multi-Model Certification

- Run cert-gate with 3+ models (codellama:7b, qwen3.8:27b, GPT-4)
- Compare admission rates, rejection reasons
- Test: `DogfoodPhaseGMultiModelTests`

#### Phase H: Long-Running Autonomy

- 72-hour unattended run with active mode
- Extension ceiling at defaults (depth=1, unattended=8, rate=4/hr)
- Operator report after 3 days
- Test: `DogfoodPhaseHLongRunTests` (slow, CI opt-in)

#### Phase I: Adversarial Dogfood

- Inject malicious objectives (exfiltration, privilege escalation)
- Verify all rejected by invariants A/B/C/D
- Test: `DogfoodPhaseIAdversarialTests`

---

## 5. Core vs. Separate Repo Architecture

### 5.1 Current Repository Structure

**Single monorepo:** `github.com/IanFrelinger/Ashlar` (63 projects in `Ashlar.sln`)

| Path | Contents | License |
|------|----------|---------|
| `src/` | Kernel spine, runtime, SDK, transport, ingress | Apache-2.0 |
| `application/` | CLI, API hosts | Apache-2.0 |
| `products/` | Extractable product scaffolds | Apache-2.0 |
| `commercial/` | Fleet, MeshDirector | **Commercial** (not Apache-2.0) |
| `docs/` | Architecture, operations, samples | Apache-2.0 |
| `tests/` | Adversarial corpus, UAT | Apache-2.0 |
| `.github/workflows/` | 57 CI workflows | Apache-2.0 |

**Enforcement:** `dependency-boundary.yml` verifies no open projects depend on commercial projects.

### 5.2 Extraction Status

**Completed:**

✅ **`ashlar-release-manager`** — extracted 2026-09-01 as first out-of-tree consumer  
✅ **Product scaffolds** — `products/Ashlar.Products.sln` (workstation, cluster, cloud, native)  

**Scheduled:**

⏳ **Runtime Studio** — `apps/runtime-studio` extraction to separate repo (scheduled, not started)

### 5.3 What Belongs in Core (This Repo)

**Keep in `github.com/IanFrelinger/Ashlar`:**

#### 5.3.1 Kernel Spine (Tier 0)

- `Ashlar.Abstractions` — contracts, interfaces
- `Ashlar.Core.*` — domain, application use cases
- `Ashlar.Hosting` — DI composition root
- `Ashlar.Contracts` — external contracts

**Rationale:** These define the product. Moving them breaks every consumer.

#### 5.3.2 Background Agents (Tier 1)

- `Ashlar.BackgroundAgents` — registry, scheduler, safety invariants
- `Ashlar.BackgroundAgents.HostRunners` — CLI/API integration

**Rationale:** Safety invariants (A/B/C/D) are core trust properties. Extraction would require duplicating invariant enforcement.

#### 5.3.3 Orchestration (Tier 1)

- `Ashlar.Orchestration` — architect, agents, coordination
- `Ashlar.Runtime` — execution engine

**Rationale:** Multi-agent coordination is a core capability, not a product feature.

#### 5.3.4 Certification Infrastructure (Tier 1)

- Cert-gate tests (`Ashlar.Tests.Infrastructure.Tests.Certification`)
- Adversarial corpus (`tests/adversarial-corpus`)
- Gate store, admission records
- Mutation engine, witness framework

**Rationale:** Trust enforcement is non-negotiable. External cert-gate would break fail-closed invariants.

#### 5.3.5 Protocol Adapters (Tier 2)

- `Ashlar.Mcp.*` — MCP server/client
- `Ashlar.Transport.A2A.*` — A2A server/client
- `Ashlar.Transport.Grpc.*` — gRPC transport

**Rationale:** Protocol integration is infrastructure, not product. Moving creates version skew risk.

#### 5.3.6 Distribution Packages (Tier 2)

- `Ashlar.Client` — HTTP client SDK
- `Ashlar.Sdk` — embedder SDK
- `Ashlar.Hosting.Bundle` — composition bundle
- `Ashlar.Authoring`, `Ashlar.Brick.Contracts` — brick authoring

**Rationale:** Published to NuGet as part of core distribution.

#### 5.3.7 Core Documentation

- `docs/architecture/` — system design, trust model
- `docs/SELF-EXTEND-AUDIT.md`, `docs/certification-evidence.md` — trust proofs
- `ci/cert-gate-assertions.md`, `ci/test-ownership.tsv` — CI contracts

**Rationale:** Trust documentation must live with code it describes.

### 5.4 What Belongs in Separate Repos

#### 5.4.1 Application Products (Extract)

**Candidates:**

- `apps/runtime-studio` → `github.com/IanFrelinger/ashlar-runtime-studio` (scheduled)
- `products/ashlar-workstation` → `github.com/IanFrelinger/ashlar-workstation`
- `products/ashlar-cluster` → `github.com/IanFrelinger/ashlar-cluster`

**Benefits:**

- Independent release cadence
- Smaller CI surface (don't retrigger core tests on UI changes)
- Separate issue tracking
- Product-specific contributors don't need core access

**Dependency:** All depend on published NuGet packages from core repo.

**Precedent:** `ashlar-release-manager` successfully extracted 2026-09-01.

#### 5.4.2 Example Applications (Extract)

- `docs/demos/Ashlar.Demos.*` → `github.com/IanFrelinger/ashlar-demos`

**Rationale:** Demo code doesn't belong in kernel CI.

#### 5.4.3 Extended Tooling (Extract)

- `tools/devlog-ghost-publish` → `github.com/IanFrelinger/ashlar-devlog` (if used beyond core)
- `extensions/ashlar-vscode` → `github.com/IanFrelinger/ashlar-vscode` (once workstation extracted)

**Rationale:** Tooling has different release cycles and maintainer teams.

#### 5.4.4 Commercial Verticals (Already Separate Licensing)

**Current state:**

- `commercial/` subdirectory in monorepo
- Separate license (not Apache-2.0, see `LICENSING.md`)
- Enforced boundary (`dependency-boundary.yml`)

**Recommendation:** Move to `github.com/IanFrelinger/ashlar-commercial-fleet` (private repo).

**Benefits:**

- Clear licensing boundary
- Private issues for paying customers
- No risk of accidental Apache-2.0 merge

**Blocker:** Needs org/team decision. Technically ready.

### 5.5 Proposed Repository Structure (Target State)

```
github.com/IanFrelinger/Ashlar (monorepo, Apache-2.0)
├── src/                         # Kernel spine, orchestration, agents, transport
├── application/                 # CLI, API
├── docs/                        # Architecture, trust proofs, operations
├── tests/adversarial-corpus/    # Breach replays
├── .github/workflows/           # Core CI (cert-gate, kernel, coverage)
└── consumer-template/           # NuGet consumption template

github.com/IanFrelinger/ashlar-runtime-studio (separate, Apache-2.0)
└── Depends on: Ashlar.Client, Ashlar.Sdk from NuGet

github.com/IanFrelinger/ashlar-workstation (separate, Apache-2.0)
└── Depends on: Ashlar.Hosting.Bundle, Ashlar.Client

github.com/IanFrelinger/ashlar-demos (separate, Apache-2.0)
└── Depends on: Ashlar.Client

github.com/IanFrelinger/ashlar-commercial-fleet (private, commercial)
└── Depends on: Ashlar.Hosting.Bundle, Ashlar.Sdk, MeshDirector components
```

### 5.6 Migration Checklist (Per Extraction)

For each repo to extract:

- [ ] Create new repo with clean history (or subtree split)
- [ ] Set up separate CI (GitHub Actions, use core `cert-gate` as dependency check)
- [ ] Update NuGet dependencies to published versions (no project references)
- [ ] Configure separate issue tracking
- [ ] Update CODEOWNERS, CONTRIBUTING.md
- [ ] Add extraction notice in core repo ("`runtime-studio` now at [link]")
- [ ] Announce in CHANGELOG.md
- [ ] Update main README.md with repo links

---

## 6. Recommended Work Packages

### 6.1 Immediate (Q4 2026)

#### WP-1: Distribute Validation Responsibility

**Goal:** Reduce `cert-gate` concentration risk.

**Tasks:**

1. Add always-report jobs to `kernel-coverage`, `dependency-boundary`
2. 30-day green streak monitoring
3. Update branch protection to require both (total 3 required checks)
4. Document rollback process

**Effort:** 2 weeks  
**Risk:** Low (always-report pattern is GitHub-documented)

#### WP-2: Equivalent Mutant Filtering

**Goal:** Fix S5 rejection (correct code blocked by equivalent mutants).

**Tasks:**

1. Implement mutant classifier (`IEquivalentMutantDetector`)
2. Integrate with `MutationEngine`
3. Add escape-rate threshold config (default 5%)
4. Add human override command: `ashlar gate admit --override <id>`

**Effort:** 3 weeks  
**Risk:** Medium (SMT solver complexity)

#### WP-3: Adversarial Fixture Generation

**Goal:** Automate breach hypothesis testing.

**Tasks:**

1. Implement mutation-based fixture generator
2. Add fuzzing harness for config loader
3. Integrate into `cert-gate` as optional step
4. Document fixture authoring guide

**Effort:** 2 weeks  
**Risk:** Low (tooling only)

### 6.2 Short-Term (Q1 2027)

#### WP-4: Durable A2A Tasks

**Goal:** Production-ready A2A server.

**Tasks:**

1. Implement SQLite task store (`IA2ATaskStore`)
2. Add SSE streaming endpoint
3. Implement push notifications
4. Update A2A tests for durability

**Effort:** 4 weeks  
**Risk:** Medium (protocol compliance)

#### WP-5: MCP Client Stdio Support

**Goal:** Consume local MCP tools.

**Tasks:**

1. Design command allowlist config schema
2. Implement process lifecycle manager
3. Add stdio pipe handling with backpressure
4. Test with `claude mcp` ecosystem

**Effort:** 3 weeks  
**Risk:** Medium (process management edge cases)

#### WP-6: Extract Runtime Studio

**Goal:** First product extraction (precedent: release-manager).

**Tasks:**

1. Create `github.com/IanFrelinger/ashlar-runtime-studio`
2. Migrate `apps/runtime-studio` with git subtree split
3. Convert project references to NuGet packages
4. Set up separate CI pipeline
5. Update core repo links

**Effort:** 2 weeks  
**Risk:** Low (precedent exists)

### 6.3 Medium-Term (Q2 2027)

#### WP-7: Long-Running Autonomy Dogfood (Phase H)

**Goal:** Prove 72-hour unattended operation.

**Tasks:**

1. Set up dedicated test node
2. Configure Active mode with default ceilings
3. Run 3-day campaign with logging
4. Analyze operator reports
5. Document findings

**Effort:** 1 week setup + 3 days run + 1 week analysis  
**Risk:** Low (monitoring only)

#### WP-8: Multi-Model Certification (Phase G)

**Goal:** Test admission rates across models.

**Tasks:**

1. Configure 3+ model providers (codellama, qwen, GPT-4)
2. Run cert-gate with each model
3. Compare admission/rejection stats
4. Document model-specific quirks

**Effort:** 2 weeks  
**Risk:** Low (observational study)

#### WP-9: Adversarial Dogfood (Phase I)

**Goal:** Prove safety invariants against malicious objectives.

**Tasks:**

1. Author 10+ adversarial objectives (exfiltration, privilege escalation)
2. Run through cert-gate
3. Verify all rejected by invariants A/B/C/D
4. Add to adversarial corpus

**Effort:** 3 weeks  
**Risk:** Medium (needs creative red team)

### 6.4 Long-Term (Q3-Q4 2027)

#### WP-10: Extract Commercial Fleet

**Goal:** Separate commercial licensing from core repo.

**Tasks:**

1. Create private `ashlar-commercial-fleet` repo
2. Migrate `commercial/` subtree
3. Update dependency boundary enforcement
4. Configure separate customer issue tracking
5. Update licensing docs

**Effort:** 4 weeks  
**Risk:** High (requires org/legal sign-off)

#### WP-11: Extract Workstation & Cluster Products

**Goal:** Complete product split (per `docs/architecture/product-split.md`).

**Tasks:**

1. Extract `products/ashlar-workstation`
2. Extract `products/ashlar-cluster`
3. Update NuGet dependencies
4. Set up product-specific CI
5. Update core repo architecture docs

**Effort:** 6 weeks (2 products × 3 weeks each)  
**Risk:** Medium (coordination across repos)

#### WP-12: Quarterly Red Team Campaigns

**Goal:** Continuous adversarial testing.

**Tasks:**

1. Establish red team rotation (3 engineers × 4 quarters)
2. Q3 2027 campaign: target Invariant A
3. Q4 2027 campaign: target Invariant B
4. Document breach attempts and defenses

**Effort:** 1 week per quarter  
**Risk:** Low (educational, not blocking)

---

## 7. Appendices

### Appendix A: File Locations Reference

| Component | Path |
|-----------|------|
| Background agents core | `src/Ashlar.BackgroundAgents/` |
| Agent registry | `src/Ashlar.BackgroundAgents/Registry/BackgroundAgentRegistry.cs` |
| CLI commands | `application/src/Ashlar.CLI/Commands/BackgroundAgent/` |
| Report command | `.../ReportBackgroundAgentCommand.cs` |
| Mode/disarm command | `.../ModeBackgroundAgentCommand.cs` |
| Self-extend audit | `docs/SELF-EXTEND-AUDIT.md` |
| Cert-gate assertions | `ci/cert-gate-assertions.md` |
| Certification evidence | `docs/certification-evidence.md` |
| Adversarial corpus | `tests/adversarial-corpus/` |
| Dogfood validation | `docs/DogfoodValidation.md` |
| CI gate inventory | `docs/CiGateInventory.md` |
| MCP/A2A integration | `docs/architecture/ProtocolIntegration-MCP-A2A.md` |
| Governed pipeline | `docs/governed-pipeline.md` |
| Testing model | `docs/architecture/TestingModel.md` |

### Appendix B: Safety Invariant Proofs

**Invariant A (Cert-gate inheritance):**

- Enforcement: `SelfProducedBrickCertificationPolicy.Approve` @ `RepoFsToolboxFactory.cs:137-140`
- Test: `src/Ashlar.Tests.BackgroundAgents/SelfExtend/SelfExtendInvariantACertGateTests.cs`
- Evidence: Missing store → `FailClosedCertificationRecordStore` denies all

**Invariant B (Monotonic policy narrowing):**

- Enforcement: `AgentPolicyNarrowingValidator.ValidateOrThrow` @ `BackgroundAgentRegistry.cs:203-206`
- Test: `src/Ashlar.Tests.BackgroundAgents/SelfExtend/SelfExtendInvariantBPolicyNarrowingTests.cs`
- Evidence: Machine-origin agents require `ParentId` or registration fails

**Invariant C (Fail-closed default):**

- Enforcement: `FileBasedAggressivenessModeStore` defaults to Passive @ lines 33-34, 41-44
- Test: `src/Ashlar.Tests.BackgroundAgents/SelfExtend/SelfExtendInvariantCHumanAdmissionTests.cs`
- Evidence: Missing/corrupt mode file → Passive, SemiActive without approval gate → denied

**Invariant D (Recursion ceiling):**

- Enforcement: `ExtensionCeiling` + `ExtensionLedger.TryBeginCycle` before runner call
- Test: `src/Ashlar.Tests.BackgroundAgents/SelfExtend/SelfExtendInvariantDRecursionCeilingTests.cs`
- Evidence: Decision + spend under one lock, overrides can only lower, ledger survives re-registration

### Appendix C: Operator Command Reference

```bash
# Mode control
ashlar background-agent mode get [--json]
ashlar background-agent mode set --value <passive|semi-active|active|ambient>

# Emergency stop
ashlar background-agent disarm [--reason "why"] [--json]

# Overnight report
ashlar background-agent report [--since-hours 24] [--project .] [--json]

# Run daemon
ashlar background-agent daemon [--duration 10m] [--project .]

# List agents
ashlar background-agent list [--json] [--status running] [--role extender]

# Show agent details
ashlar background-agent show <agent-id> [--json]

# Start/stop/restart
ashlar background-agent start <agent-id>
ashlar background-agent stop <agent-id>
ashlar background-agent restart <agent-id>

# Proposals
ashlar background-agent proposals list [--status held|admitted|rejected]
ashlar background-agent proposals admit <proposal-id>

# Stats
ashlar background-agent stats [--json] [--since-hours 24]

# Re-arm after disarm
ashlar background-agent mode set --value active
```

### Appendix D: CI Workflow Summary

**57 total workflows** in `.github/workflows/`

**Required checks (1):**
- `cert-gate` — only merge-blocking check on `master`

**Advisory PR-triggered (14):**
- `layer-boundary`, `application-gate`, `dependency-boundary`, `distribution-matrix-gate`, `docs-link-check`, `kernel-coverage-gate`, `kernel-gate`, `security-gate`, `shell-lint`, `testing-strategy-gate`, `uat-gate`, `products-gate`, `portability-gate`, plus `release-staging-on-label` (label-driven)

**Push/schedule-driven (20):**
- Path-filtered on `master`/`main`/`cursor/**` branches, plus `workflow_dispatch`

**Manual-only (17):**
- `workflow_dispatch` only (mesh labs, multi-env, ship/ops/perf, release)

**Tag/release (2):**
- `release.yml` (`v*.*.*` tags), `devlog-ghost-release.yml` (release published)

**Scheduled (5):**
- `distribution-matrix-gate` (Mon 10:00), `full-platform-readiness-gate` (Mon 06:00), `onboarding-quickstart-gate` (Mon 07:00), `rc-gate` (1st of month 06:00), `mesh-lab-tls-gate` (Tue 07:00)

---

## 8. Conclusion

Ashlar's built-in agent system and validation architecture represent a **production-grade implementation** of autonomous software engineering with fail-closed safety. The four enforced invariants (cert-gate inheritance, policy narrowing, fail-closed defaults, recursion ceiling) provide defense-in-depth against runaway or malicious self-modification.

**Key strengths:**

1. ✅ Safety invariants proven and tested
2. ✅ Operator front doors (mode, disarm, report)
3. ✅ Certification gate with evidence ledger
4. ✅ Adversarial corpus for regression prevention
5. ✅ Dogfood validation (10+ blocks)
6. ✅ Protocol integration (MCP/A2A)

**Key risk:**

⚠️ **Concentration risk** — `cert-gate` is the only required check. One toggle disables all trust enforcement.

**Primary recommendation:**

Expand required checks incrementally (Phase 1: +2 checks with always-report jobs, Phase 2: +2 more after 30-day green streak) or formalize `cert-gate`'s role as single-point-of-trust with enhanced ownership/review process.

**Maturity assessment:**

- **Operator-governed path (A0–A5):** Production-ready with hold mode default
- **In-process autonomy loop:** Spike-grade, suitable for research and operator-supervised runs
- **Full auto-admit:** Requires Phase H (long-running dogfood) + Phase I (adversarial dogfood) completion

The architecture is sound. The validation is comprehensive. The risk is operational: maintaining trust enforcement as the only required gate in a repo with 57 workflows and a history of muting red checks.

---

**End of Audit**
