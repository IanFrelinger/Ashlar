# Ashlar Core Runtime Audit: Admission & Self-Extend Readiness

**Audit Date:** 2026-09-05  
**Scope:** Core runtime readiness for autonomous self-extension with validation  
**Auditor:** Cloud Agent (Cursor)  

> **Dated correction, 2026-09-13 — read before acting on any limitation 9 item below.** This is a
> 2026-09-05 snapshot and its body is left as the record of what was known then. Limitation 9 has since
> closed **in part**: `CompositionCertificationRecordSigner` now honours an explicitly supplied
> `hmacKey` and computes its honesty flag from it, so descriptions below of the signer "discarding a
> supplied key" or "key injection bypass" no longer describe the code. **The consequence this report
> relies on still holds:** the injected brick signer is still discarded, `CertificationRecordSigner` exposes no key accessor, and no production registration supplies the parameter, so compositions have no operator path to a real key.
> By the M1.1 exit criterion stated here — a host passing a real key via `brickSigner` minting
> compositions under that key — M1.1 is **not met**. See `docs/certification-evidence.md` limitation 9.

**Branch:** `cursor/core-runtime-self-extend-audit-95c7`

---

## Executive Summary

Ashlar's core runtime has **four safety invariants (A-D) enforced and tested** across the self-extend path, with **cert-gate, trust log, and admission machinery production-ready**. The self-extend background-agent extender runs under **bounded autonomy** with fail-closed defaults, recursion ceilings, and policy narrowing. However, **significant gaps remain** before claiming "autonomous self-extension with validation" in production:

- **Trust infrastructure (Invariant A) has three open security holes** (signature downgrade, schema downgrade, signer bypass)
- **No dashboard UI** for trust events; APIs exist but no presentation layer
- **Self-extend runner is an adapter, not a certified loop** — missing: proposer seam, repair channel, acceptance measurement
- **Policy packs exist but have no self-extend-specific rules** — the documented `self_extend*` policies are not implemented
- **Product coupling found:** Commercial `ApprovalBridge` Discord integration outside core

**Top Risk:** False ADMIT via cryptographic bypasses (limitations 7–9 in certification-evidence.md) — an attacker can forge certificates under the committed HMAC key.

---

## 1. Production-Ready Components

### 1.1 Certification Loop (Invariants A–D)

#### ✅ **PRODUCTION-READY**

**Location:** `src/Ashlar.Infrastructure/Certification/CertificationGate.cs`, `src/Ashlar.BackgroundAgents/Security/`, `src/Ashlar.BackgroundAgents/Extending/`

**What Exists:**
- **Invariant A (Cert-gate inheritance):** `SelfProducedBrickCertificationPolicy` enforces admitted certification records on self-extend brick writes, verified via `CertificationTrustVerifier` with content-bound hashes
  - **Enforcement:** `RepoFsToolboxFactory.cs:137-140` wires the policy when `selfExtendAdmission=true`
  - **Fail-closed:** Missing store → `FailClosedCertificationRecordStore` denies all admissions
  - **Test coverage:** `SelfExtendInvariantACertGateTests` (passes)

- **Invariant B (Monotonic policy narrowing):** `AgentPolicyNarrowingValidator` enforces child ⊆ parent exfiltration envelope at registration
  - **Enforcement:** `BackgroundAgentRegistry.cs:203-206` validates machine-origin agents (authored trust roots skip)
  - **Narrowing:** `DataExfiltrationPolicy` keyed on `agentId`, requires `ParentId`, blocks capability widening
  - **Test coverage:** `SelfExtendInvariantBPolicyNarrowingTests` (passes)

- **Invariant C (Fail-closed default):** Aggressiveness mode defaults to **Passive** — extenders do not run without explicit operator arming
  - **Enforcement:** `FileBasedAggressivenessModeStore` defaults to Passive on missing/corrupt file
  - **Registry fallback:** `BackgroundAgentRegistry.cs:518` defaults to Passive when store absent
  - **SemiActive gate:** `BackgroundAgentRegistry.cs:528-535` refuses when `IApprovalGate` is null
  - **Test coverage:** `SelfExtendInvariantCHumanAdmissionTests` (passes)

- **Invariant D (Recursion ceiling):** `ExtensionCeiling` + `ExtensionLedger` enforce lineage depth, unattended cycles, and rate limits
  - **Enforcement:** `BackgroundAgentRegistry.cs:560-596` calls `TryBeginCycle` atomically (decide + reserve under one lock)
  - **Ceilings:** `MaxLineageDepth=1` (roots + children only), `MaxUnattendedCycles=8`, `MaxCyclesPerHour=4`
  - **Narrowing:** Environment and agent `Parameters` may only LOWER ceilings, never raise
  - **Test coverage:** `SelfExtendInvariantDRecursionCeilingTests` (passes, rejection case)

**Certification Gate:**
- Multi-stage: recursion check → analyzer fence → correctness witness → mutation gate → determinism → dependency
- Execution backends: in-process (legacy) or `SessionExecutionBackend` (attested container)
- Probe system: diagnostic probes attach structured findings on rejection (parent spec R3.1-3.4)
- **Evidence:** 19+ tests passing in CI (cert-gate-trx), dogfood runs in `docs/certification-evidence.md`

**Key Finding:** The certification **machinery** is production-ready; the **trust signatures** securing it are not (see §3.1).

---

### 1.2 Trust Log APIs

#### ✅ **PRODUCTION-READY (Backend only)**

**Location:** `application/src/Ashlar.API/Endpoints/AshlarEndpoints.cs`

**Endpoints:**
- `GET /trust/status` — active trust policy pack, boundary status
- `GET /trust/dashboard` — trust boundary + recent audit events
- `POST /trust/pause` — pause/resume observation boundary
- `POST /trust/rule` — update allow/deny rules for category/source

**Response Types:**
- `TrustStatusResponse` (contracts)
- `TrustDashboardResponse` (aggregates policy pack status + events)

**Storage:**
- `ITrustPolicyPackRegistry` → `TrustPolicyPackRegistry` (file-backed, `config/trust-packs/`)
- Policy packs: `internal-only.json`, `air-gapped.json`, `strict-enterprise.json`, `active-pack.json`
- No event ledger storage found (dashboard returns real-time observations, not persisted audit trail)

**Gap:** APIs exist but **no UI dashboard** — the presentation layer does not ship. Runtime Studio has operator surfaces (`RuntimeStudioOperatorDashboardSummary.cs`) but no trust-specific views.

---

### 1.3 HoldAdmission / Aggressiveness Mode

#### ✅ **PRODUCTION-READY**

**Location:** `src/Ashlar.Abstractions/BackgroundAgentAggressivenessMode.cs`, `src/Ashlar.BackgroundAgents/`

**Modes:**
- **Passive** (default) — observe only, no execution
- **SemiActive** — requires `IApprovalGate.RequestApprovalAsync` approval (timeout = deny)
- **Active** — runs immediately (still subject to Invariant D ceilings)

**Approval Gate Implementations:**
- `NoApprovalGate` (null object, for tests)
- `AlwaysApprovalGate` (always approves, for labs)
- `DenyApprovalGate` (always denies, for safety)
- `TimeoutApprovalGate` (timed approval window)
- **NOT IN CORE:** Commercial `ApprovalBridge` (Discord playtest fixes) — see §4

**Storage:**
- `FileBasedAggressivenessModeStore` (file-backed, operator-set)
- Re-arm: `BackgroundAgentRegistry.RearmExtension(agentId)` or process restart
- **Deliberate:** Re-registration does NOT re-arm (agents can re-register themselves via `UpdateAgentConfigTool`)

**Finding:** Hold-mode autonomy is production-ready. The "human admission" layer for Tier 1 artifacts (trust kernel touches) is **structural** (tier classification at `AutonomousIterationHarness`) but the **operational workflow** (how a human reviews/admits) is spike-only (`spikes/autonomy-first-flight/`).

---

## 2. Spike / Hold-Only Components

### 2.1 Self-Extend Runner (Background Agent Path)

#### ⚠️ **SPIKE / ADAPTER**

**Location:** `src/Ashlar.BackgroundAgents.HostRunners/SelfExtendRunnerAdapter.cs`, `src/Ashlar.BackgroundAgents/Extending/ISelfExtendRunner.cs`

**What Exists:**
- `ISelfExtendRunner` interface with 4 overloads (repoRoot, objective, agentName, modelProvider/Name, agentId)
- `SelfExtendRunnerAdapter` wires:
  - `RepoFsToolboxFactory` (policies: `PathAllowlist`, `MaxWriteSize`, `BuildTestBudget`, `SelfProducedBrickCertificationPolicy`, `DataExfiltrationPolicy`)
  - `BuildSnapshot` with `selfExtendAdmission=true` + `agentId`
  - `ToolCallingAgent.RunCycleAsync` (direct LLM call, no proposer seam)

**Gap Analysis:**
- ❌ **No proposer abstraction** — model calls directly embedded, not through `ICompositionProposer` seam
- ❌ **No repair channel** — no `RepairFeedbackPolicy`, no bounded retry (trust-loop spec R4.1-4.6)
- ❌ **No acceptance measurement** — no recorded proposals, no temperature control, no batch tracking
- ❌ **No objective store integration** — objectives are parameters, not tiered files from `IObjectiveStore`
- ✅ Policies enforced (A, B at registration; D at execution; C at mode gate)
- ✅ Toolbox confinement (write allowlist, max size, build budget)

**Verdict:** The adapter runs **bounded** self-extend cycles with safety invariants enforced, but it is **not the certified autonomy loop** described in `docs/trust-loop/`. The spikes in `spikes/autonomy-first-flight/` are the certified loop; they have not been merged into the background-agent path.

---

### 2.2 Certified Autonomy Loop (Spike Path)

#### ✅ **SPIKE EVIDENCE (P2–P6, S1–S5)**

**Location:** `spikes/autonomy-first-flight/`, `src/Ashlar.Infrastructure/Certification/HotSwap/AutonomousIterationHarness.cs`

**What Exists:**
- `AutonomousIterationHarness` — full loop: objective → propose (session sandbox) → fences → analyzer → mutation → certify → admit (tier gate) → swap → watch
- `SessionExecutionBackend` — in-session build + execution (attested container, `docker.sock` pass-through)
- `CertifiedBrickHotSwapHost` — generation-aware swap with rollback capability
- `RepairFeedbackPolicy` — structured feedback projection (`CheckOnly`, `OwnOutput`, `Full`), bounded retry
- `ProposalContextAssembler` — deterministic context assembly (parent spec R1.1-1.5)
- Acceptance measurement (S3, S5): recorded proposals, verdict replay, rate computation

**Evidence:**
- **P2:** First flight on live Docker daemon, admitted and swapped
- **P3:** In-session build (candidate compiles inside attested SDK container)
- **P5:** Full execution containment (witness + mutants execute in-session)
- **P6:** LIVE model proposal (`ollama codellama:7b`), measured acceptance 1/4
- **S1–S2:** Objective file drives loop, repair channel converges (after closing two trust holes)
- **S3:** Repair policy measured (redaction costs nothing; contract precision is necessary)
- **S4:** Dogfood campaign 1 (5 objectives, live model, hold mode, 1/5 certified-held)
- **S5:** Dogfood campaign 2 (same 5 objectives, 3 models, 1/5 → 2/5 → 3/5 certified-held)

**Gap:** These components are **not wired into `BackgroundAgentService`** — they are spike-only harnesses invoked from `FirstFlight/Program.cs`. The production path is `BackgroundAgentRegistry` → `SelfExtendRunnerAdapter`, which does not use the certified loop machinery.

---

### 2.3 Policy Packs (Self-Extend Specific)

#### ❌ **DOCUMENTED BUT NOT IMPLEMENTED**

**Claimed (docs/SELF-EXTEND-AUDIT.md):**
> policy packs / ashlar policy set self_extend*

**Found:**
- `ITrustPolicyPackRegistry` exists and works (loads `config/trust-packs/*.json`)
- Packs: `internal-only`, `air-gapped`, `strict-enterprise` (category/source rules only)
- **No self-extend-specific policies** in any pack:
  - No `self_extend_max_depth` rule
  - No `self_extend_max_unattended` rule
  - No `self_extend_tier_0_allowlist` rule
  - No `self_extend_kernel_boundary` rule

**Agent Configuration:**
- `apps/runtime-studio/config/agent_set.local.json` has `"self_extend"` in `Commands` array for `runtime-planner` agent
- But this is a **command name**, not a policy enforcement point

**Verdict:** Policy pack infrastructure is production-ready, but **self-extend-specific policy enforcement does not exist**. The invariants (A-D) are enforced directly in code, not via policy pack rules.

---

## 3. Requirements for "Autonomous Self-Extension with Validation"

### 3.1 Trust Signatures Must Be Production-Grade

#### ❌ **BLOCKING (Critical Security Holes)**

**Current State (from `docs/certification-evidence.md`):**

**Limitation 7: Signature Downgrade**
- `CertificationRecordSigner.Verify` checks Ed25519 **only if the record carries one**
- Attack: Delete `ed25519Signature` field → verification falls back to HMAC alone
- HMAC key is **committed constant** `CertificationRecordSigning.DefaultDevKey` unless `ASHLAR_CERT_DEV_HMAC_KEY` set
- **Impact:** Any attacker with the source can forge certificates
- Mitigation added 2026-08-27: `CertificationVerifyOptions.RequireEd25519Signature` + `TrustedEd25519PublicKeys` (opt-in, default unchanged)
- **Not compiled** (no .NET SDK in authoring environment per docs)

**Limitation 8: Schema Downgrade**
- `CertificationRecordSigning.BuildPayload` routes on `record.SchemaVersion` (attacker-supplied)
- Legacy lane drops `SchemaVersion`, `Gate`, `GatesPassed`, `Inputs`, `Proposer`, `Attempts`, `Ed25519PublicKey` from signed bytes
- Attack: Strip Ed25519 signature + null `schemaVersion` + rewrite `gate` name → valid HMAC under committed constant
- **Impact:** Forged record claims to have passed gates it never ran
- Mitigation added 2026-08-27: `CertificationVerifyOptions.MinimumSchemaVersion` (opt-in, default 0)
- **Not compiled**

**Limitation 9: Composition Signer Bypass**
- `CompositionCertificationRecordSigner` discards explicit signer: `_ = brickSigner;`
- Always reads `ASHLAR_CERT_DEV_HMAC_KEY` or committed constant, ignoring caller-supplied key
- **Impact:** A host that supplies a real key still mints composition records under the public constant
- **No mitigation** (architectural fix needed)

**Action Required:**
1. **Remove committed HMAC key** — `CertificationRecordSigning.DefaultDevKey` must not exist in source
2. **Enforce Ed25519 + key pinning** — make `RequireEd25519Signature` + `TrustedEd25519PublicKeys` the **default**, not opt-in
3. **Enforce schema floor** — refuse `SchemaVersion < 2` by default (legacy lane becomes dead code)
4. **Fix composition signer** — honor explicit keys (limitation 9)
5. **Compile and test** — limitations 7-9 mitigations were code-read only, not CI-proven

**Rationale:** Without these fixes, **any false ADMIT is trivially forgeable** — the cert-gate can be perfect and the attacker still wins.

---

### 3.2 Merge Certified Loop into Background Agent Path

#### ❌ **MISSING INTEGRATION**

**Current:**
- Production path: `BackgroundAgentRegistry` → `SelfExtendRunnerAdapter` (direct model calls, no repair, no acceptance tracking)
- Certified path: `AutonomousIterationHarness` (proposer seam, repair channel, tier gates, acceptance measurement) — spike-only

**Required:**
1. **Replace `SelfExtendRunnerAdapter` with `AutonomousIterationHarness`** wiring
2. **Wire `IObjectiveStore`** into `BackgroundAgentService` (objectives as tiered files, not config parameters)
3. **Wire `CertifiedBrickHotSwapHost`** for generation-aware swaps with rollback
4. **Enable `RepairFeedbackPolicy`** with bounded retry (default `OwnOutput`, 2 attempts)
5. **Add acceptance tracking** — record proposals, verdicts, rate computation per lineage
6. **Wire `SessionExecutionBackend`** for in-session build + execution (currently opt-in flags)

**Test Requirements:**
- End-to-end: objective file → extender cycle → certified-held (Tier 0) or admitted-swapped (Active mode)
- Rejection teeth: each gate rejects its defect class (correctness, mutation, seam, constituents, depth)
- Repair convergence: at least one objective converges within budget (measured acceptance > 0)
- Rollback: post-swap regression triggers automatic rollback + quarantine

---

### 3.3 Implement Watch Window + Rollback Automation

#### ❌ **PARTIALLY EXISTS (No Automation)**

**Current:**
- `CertifiedBrickHotSwapHost` has generation tracking, can retain N-1 for rollback
- `RuntimeObservation` system records agent actions, test results, errors
- **No watch window automation** — no threshold breach detection, no auto-rollback

**Required (trust-loop-ext spec R5.1-5.5):**
1. **Watch window:** Declared-contract conformance, error-rate delta, latency delta, resource ceiling
2. **Threshold breach** → automatic rollback (reactivate generation N)
3. **Quarantine** → revoke certificate, refuse hash permanently, triage into probe/fence/analyzer
4. **Revocation propagation** — flag certificates whose inputs include revoked artifact
5. **Demotion on repeated rollback** — default 2 rollbacks → Tier 1 (lose autonomy on evidence)

**Storage:**
- Rollback history: per-brick ledger (retained generations, rollback events)
- Quarantine list: content-hash deny-list + revoked certificate index

---

### 3.4 Dashboard UI for Trust Events

#### ❌ **MISSING**

**Current:**
- APIs exist (`/trust/dashboard`, `/trust/status`, `/trust/rule`, `/trust/pause`)
- `RuntimeObservation` captures events (agent actions, gate verdicts, extension refusals)
- **No presentation layer** — no web UI, no CLI command to render dashboard

**Required:**
1. **Web UI** in `application/src/Ashlar.API` (or Runtime Studio)
   - Recent trust events (30 days): gate passes/rejects, policy violations, extension refusals
   - Active policy pack status, rule overrides
   - Pause/resume controls
2. **CLI command** `ashlar trust dashboard` (summary table + recent events)
3. **Digest export** — JSON export of trust events for external SIEM integration

**Rationale:** Autonomous actions require visibility. Without a dashboard, "the loop did X" is invisible until something breaks.

---

### 3.5 Policy Pack Self-Extend Rules

#### ❌ **NOT IMPLEMENTED**

**Required Policy Rules:**
1. `self_extend_enabled: bool` — global kill switch (default false)
2. `self_extend_max_lineage_depth: int` — override `ExtensionCeiling.MaxLineageDepth` (policy narrows code default)
3. `self_extend_max_unattended_cycles: int` — override `ExtensionCeiling.MaxUnattendedCycles`
4. `self_extend_max_cycles_per_hour: int` — override `ExtensionCeiling.MaxCyclesPerHour`
5. `self_extend_tier_0_allowlist: string[]` — paths/namespaces eligible for autonomous swap (rest are Tier 1)
6. `self_extend_kernel_denylist: string[]` — paths that MUST NOT appear in any agent touch-set (trust kernel)

**Enforcement:**
- `TrustPolicyPackRegistry.GetActivePack()` + `ExtensionCeiling.Resolve()` merge policy + environment + agent params
- `AutonomousIterationHarness` tier classifier reads `tier_0_allowlist` + `kernel_denylist`
- Background agent registration refuses agents violating policy

**Existing Infrastructure:**
- `TrustPolicyPack` model supports arbitrary rules (JSON dictionary)
- `ITrustPolicyPackRegistry` loads and activates packs
- **Gap:** No reader/applier for self-extend rules

---

## 4. Core vs Product Boundary (Non-Extractable Components)

### 4.1 MUST Stay in Core (Trust Kernel)

The following components are **trust-load-bearing** and MUST NOT move to a separate product:

1. **Certification Gate** (`CertificationGate`, analyzer fence, mutation engine, determinism checks)
   - **Rationale:** I-1 — "the loop may never extend its own authority"; gates define acceptance
2. **Invariant Enforcement** (`SelfProducedBrickCertificationPolicy`, `AgentPolicyNarrowingValidator`, `ExtensionCeiling`, `ExtensionLedger`)
   - **Rationale:** Safety invariants are the contract; relaxing them is a security regression
3. **Trust Signatures** (`CertificationRecordSigner`, `CertificationTrustVerifier`, Ed25519 verification)
   - **Rationale:** Certificate forgery undermines all certification; trust root cannot be external
4. **Admission Tiers** (`AutonomousIterationHarness` tier classification, analyzer gate touch-set enforcement)
   - **Rationale:** Tier 0 autonomy boundary is the blast radius control; cannot be delegated
5. **Policy Engine** (`ITrustPolicyPackRegistry`, `AccessBoundary`, exfiltration narrowing)
   - **Rationale:** Policy packs constrain agent authority; an external product could not enforce narrowing on core agents
6. **Session Isolation** (`SessionExecutionBackend`, `DockerSessionProvider`, attestation)
   - **Rationale:** Containment is the last gate; a lie from the backend is a lie in the certificate

**Test:** "Can an attacker with product-only access bypass this?" If yes → must stay in core.

---

### 4.2 MAY Extract to Product (Not Trust-Load-Bearing)

The following are **operator surfaces** or **presentation layers** and may live in a separate product:

1. **Dashboard UI** (trust event rendering, policy pack editor)
   - Core provides APIs; product provides presentation
2. **CLI Commands** (`ashlar trust dashboard`, `ashlar self-extend pause`)
   - Thin wrappers over core APIs
3. **Objective Authoring Tools** (objective file editor, witness builder, tier classifier UI)
   - Core reads objective files; product helps humans write them
4. **Rollback/Quarantine UI** (generation browser, rollback button, quarantine list)
   - Core implements rollback; product triggers it
5. **Acceptance Reporting** (proposal history, acceptance rate charts, lineage graphs)
   - Core records proposals; product renders analytics

**Boundary Rule:** Product may **present, trigger, and report** — never **decide, enforce, or verify**.

---

### 4.3 Product Coupling Found in Core

#### ⚠️ **VIOLATION: Commercial `ApprovalBridge`**

**Location:** Mentioned in `docs/SELF-EXTEND-AUDIT.md:62`
> commercial `ApprovalBridge` (Discord playtest fixes)

**Found:** Not in main codebase (`Grep` and file search found no `ApprovalBridge.cs` in `src/`)  
**Inferred Location:** `commercial/` directory (not audited per scope)

**Issue:** If `ApprovalBridge` is a **product-specific approval gate** (e.g., Discord bot integration), it should be:
1. **Outside `src/`** — product code must not be in core
2. **Not referenced** by any core type — `IApprovalGate` interface is fine; `ApprovalBridge` DI wiring must be in product host

**Recommendation:** Verify `commercial/` does not leak into `src/` assembly references. If it does, extract.

---

## 5. Top 10 Gaps Ranked by Risk (False ADMIT / Slop Escape)

### 5.1 CRITICAL (Exploitable Now)

| # | Gap | Risk | Exploitability | Impact | Fix Priority |
|---|-----|------|----------------|--------|--------------|
| **1** | **Committed HMAC Key** (limitation 1) | Trivial certificate forgery | Immediate (key in source) | Complete bypass of cert-gate | **P0** |
| **2** | **Ed25519 Signature Downgrade** (limitation 7) | Attacker strips signature → HMAC-only | Immediate (delete one JSON field) | Bypass Ed25519; fall back to public HMAC | **P0** |
| **3** | **Schema Version Downgrade** (limitation 8) | Attacker nulls `schemaVersion` → legacy payload | Immediate (rewrite gate name post-HMAC) | Claim to pass gates never run | **P0** |

**Severity:** An attacker with source access can forge certificates for arbitrary code. The cert-gate is perfect and irrelevant.

**Mitigation Path:**
1. Remove `DefaultDevKey` constant (force key configuration)
2. Default `RequireEd25519Signature=true` + key pinning (opt-out, not opt-in)
3. Default `MinimumSchemaVersion=2` (legacy lane dead code)
4. **Compile and CI-test** these fixes (currently code-read only)
5. Publish new baseline version; refuse to interop with old

---

### 5.2 HIGH (Slop Escape / False Accept)

| # | Gap | Risk | Exploitability | Impact | Fix Priority |
|---|-----|------|----------------|--------|--------------|
| **4** | **Composition Signer Bypass** (limitation 9) | Composition certs always use public HMAC | Moderate (supply real key → ignored) | Multi-brick attacks easier (constituents fake) | **P1** |
| **5** | **No Watch Window Automation** | Certified-but-buggy brick runs until human notices | Low (requires bad proposal + gate miss) | Runtime regression without rollback | **P1** |
| **6** | **Equivalent Mutant Rejection** (S5 finding) | Correct code with redundant guards rejected | Low (model writes defensive code → unkillable) | False reject (correctness penalty, not safety) | **P2** |

**Explanation:**
- **#4:** Composition forgery requires forging constituents first (hard if #1-3 are fixed), but once fixed, compositions remain vulnerable
- **#5:** A brick that passes gates but regresses in production is **not a certification failure** (gates cannot predict all production behavior), but lack of auto-rollback means slop persists
- **#6:** Not a false ADMIT (it rejects); it's a false REJECT — blocks correct code due to semantic-equivalence gap in mutation engine

---

### 5.3 MEDIUM (Operational Gaps)

| # | Gap | Risk | Exploitability | Impact | Fix Priority |
|---|-----|------|----------------|--------|--------------|
| **7** | **Certified Loop Not Integrated** | Production path uses spike-grade adapter | N/A (not an attack vector) | No repair, no acceptance tracking, weaker convergence | **P1** |
| **8** | **No Dashboard UI** | Autonomous actions invisible until failure | N/A | Human cannot observe what loop did (visibility gap) | **P2** |
| **9** | **No Self-Extend Policy Rules** | Cannot enforce org-wide self-extend constraints via policy pack | Low (still enforced in code) | Policy packs claim to govern but don't | **P2** |
| **10** | **No Revocation Propagation** | Revoked brick's consumers not flagged | Low (requires #5 rollback + multi-brick dependency) | Trust chain breaks silently | **P3** |

---

### 5.4 Risk Ranking Rationale

**False ADMIT Risk = P(attacker succeeds) × Impact**

- **#1-3:** 100% success rate with source access × complete bypass = **P0**
- **#4:** Moderate (depends on #1-3 being unfixed) × constituent trust chain = **P1**
- **#5:** Low (gates usually catch bugs) × unbounded slop = **P1**
- **#6:** Low (affects convergence, not safety) × false reject penalty = **P2**
- **#7:** 0% exploit (operational only) × convergence quality = **P1** (for feature completion, not security)
- **#8-10:** Operational gaps, not attack vectors

**Slop Escape Risk = P(gate misses defect) × P(no rollback) × Runtime impact**

- **#5 (watch window):** Only escape path after cert-gate; needs automation
- **#6 (equivalent mutant):** False reject (quality loss, not escape)

---

## 6. Known Limitations Summary

From `docs/certification-evidence.md` (authoritative):

1. **Dev HMAC signer, not PKI** — committed constant, forgeable by anyone with source
2. **Composition seam check is TYPE-level only** — semantic mismatches (both `string`) pass
3. **cert-gate expected count is derived at runtime** — fragile to test discovery changes
4. **Session containment is opt-in** — `BuildCandidateInSession` + `ExecuteCandidateInSession` flags, default off
5. **Model proposing: mechanism CLOSED, scale boundary open** — P6 flight succeeded (1/4 acceptance), but one objective only
6. **Kernel options bind from environment variables only** — `appsettings.json` not read by `AddAshlar`
7. **Signature downgrade** (see §5.1 #2)
8. **Schema downgrade** (see §5.1 #3)
9. **Composition signer bypass** (see §5.2 #4)

**Additions from This Audit:**
10. **No watch window automation** (§3.3)
11. **No dashboard UI** (§3.4)
12. **No self-extend policy pack rules** (§3.5)
13. **Certified loop not integrated** (§2.1)
14. **No revocation propagation** (§5.3 #10)

---

## 7. File Pointers (Key Implementation Locations)

### 7.1 Certification & Invariants

| Component | Path | Lines |
|-----------|------|-------|
| CertificationGate (main) | `src/Ashlar.Infrastructure/Certification/CertificationGate.cs` | 1-519 |
| Invariant A (cert-gate policy) | `src/Ashlar.BackgroundAgents/Security/SelfProducedBrickCertificationPolicy.cs` | 1-92 |
| Invariant B (policy narrowing) | `src/Ashlar.BackgroundAgents/Security/AgentPolicyNarrowingValidator.cs` | 1-132 |
| Invariant C (fail-closed mode) | `src/Ashlar.Abstractions/BackgroundAgentAggressivenessMode.cs` | (enum) |
| Invariant C (registry enforcement) | `src/Ashlar.BackgroundAgents/Registry/BackgroundAgentRegistry.cs` | 518-560 |
| Invariant D (extension ceiling) | `src/Ashlar.BackgroundAgents/Extending/ExtensionCeiling.cs` | 1-124 |
| Invariant D (extension ledger) | `src/Ashlar.BackgroundAgents/Extending/ExtensionLedger.cs` | 1-159 |
| Invariant D (registry enforcement) | `src/Ashlar.BackgroundAgents/Registry/BackgroundAgentRegistry.cs` | 560-596 |

### 7.2 Self-Extend Runtime

| Component | Path | Lines |
|-----------|------|-------|
| ISelfExtendRunner (interface) | `src/Ashlar.BackgroundAgents/Extending/ISelfExtendRunner.cs` | 1-63 |
| SelfExtendRunnerAdapter (prod) | `src/Ashlar.BackgroundAgents.HostRunners/SelfExtendRunnerAdapter.cs` | 74-196 |
| BackgroundAgentService (entry) | `src/Ashlar.BackgroundAgents/Services/BackgroundAgentService.cs` | 59-91 |
| BackgroundAgentRegistry (scheduler) | `src/Ashlar.BackgroundAgents/Registry/BackgroundAgentRegistry.cs` | 394-764 |

### 7.3 Certified Autonomy Loop (Spike)

| Component | Path | Lines |
|-----------|------|-------|
| AutonomousIterationHarness | `src/Ashlar.Infrastructure/Certification/HotSwap/AutonomousIterationHarness.cs` | (full file) |
| CertifiedBrickHotSwapHost | `src/Ashlar.Infrastructure/Certification/HotSwap/CertifiedBrickHotSwapHost.cs` | (full file) |
| SessionExecutionBackend | `src/Ashlar.Infrastructure/Certification/SessionExecutionBackend.cs` | (full file) |
| RepairFeedbackPolicy | `src/Ashlar.Infrastructure/Autonomy/RepairFeedbackPolicy.cs` | (inferred) |
| First Flight spike | `spikes/autonomy-first-flight/FirstFlight/Program.cs` | (full file) |

### 7.4 Trust & Policy

| Component | Path | Lines |
|-----------|------|-------|
| ITrustPolicyPackRegistry | `src/Ashlar.Core.Application/Trust/Ports/ITrustPolicyPackRegistry.cs` | 1-25 |
| TrustPolicyPackRegistry | `src/Ashlar.Infrastructure/Trust/TrustPolicyPackRegistry.cs` | 1-151 |
| Trust API endpoints | `application/src/Ashlar.API/Endpoints/AshlarEndpoints.cs` | 208-257 |
| Policy packs | `config/trust-packs/*.json` | (JSON files) |
| Agent configs | `apps/runtime-studio/config/agent_set.local.json` | (JSON file) |

### 7.5 Trust Signatures (Security Holes)

| Component | Path | Lines |
|-----------|------|-------|
| CertificationRecordSigner | `src/Ashlar.Infrastructure/Certification/CertificationRecordSigner.cs` | 37-41 (explicit key) |
| CertificationRecordSigning (payload) | `src/Ashlar.Certification.Contracts/CertificationRecordSigning.cs` | 106-163 (legacy lane) |
| CertificationTrustVerifier | `src/Ashlar.Infrastructure/Certification/CertificationTrustVerifier.cs` | (Ed25519 conditional) |
| CompositionCertificationRecordSigner | `src/Ashlar.Infrastructure/Certification/Composition/CompositionCertificationRecordSigner.cs` | 20-30 (bypass) |
| DefaultDevKey constant | `src/Ashlar.Certification.Contracts/CertificationRecordSigning.cs` | (search for `DefaultDevKey`) |

### 7.6 Tests

| Invariant | Path |
|-----------|------|
| A (cert-gate) | `src/Ashlar.Tests.BackgroundAgents/SelfExtend/SelfExtendInvariantACertGateTests.cs` |
| B (narrowing) | `src/Ashlar.Tests.BackgroundAgents/SelfExtend/SelfExtendInvariantBPolicyNarrowingTests.cs` |
| C (hold) | `src/Ashlar.Tests.BackgroundAgents/SelfExtend/SelfExtendInvariantCHumanAdmissionTests.cs` |
| D (ceiling) | `src/Ashlar.Tests.BackgroundAgents/SelfExtend/SelfExtendInvariantDRecursionCeilingTests.cs` |
| Cert-gate teeth | `src/Ashlar.Tests.Infrastructure/Tests/Certification/CertificationGateTeethTests.cs` |

---

## 8. Recommended Next PRs

### 8.1 P0: Fix Trust Signature Security Holes

**Scope:** Close limitations 7-9 before any production deployment

**Tasks:**
1. Remove `CertificationRecordSigning.DefaultDevKey` constant (force `ASHLAR_CERT_ED25519_KEY` configuration)
2. Change `CertificationVerifyOptions` defaults:
   - `RequireEd25519Signature = true` (not false)
   - `MinimumSchemaVersion = 2` (not 0)
   - `TrustedEd25519PublicKeys = [OperatorKey.Load().PublicKey]` (pin to operator key)
3. Fix `CompositionCertificationRecordSigner` to honor explicit `brickSigner` parameter
4. Compile, test, and **CI-verify** all three fixes (currently code-read only)
5. Update `docs/certification-evidence.md` to mark limitations 1, 7, 8, 9 as CLOSED

**Exit Criteria:**
- `ASHLAR_CERT_DEV_HMAC_KEY` unset + no `DefaultDevKey` → signer construction fails (not falls back)
- Record with no Ed25519 signature → verify fails (not degrades)
- Record with `schemaVersion=1` → verify fails when floor=2
- Composition with explicit real key → record signed with that key (not environment key)
- All existing cert-gate tests still pass

---

### 8.2 P1: Integrate Certified Loop into Background Agent Path

**Scope:** Replace `SelfExtendRunnerAdapter` with `AutonomousIterationHarness` wiring

**Tasks:**
1. Extract `AutonomousIterationHarness` from `spikes/` into `src/Ashlar.Infrastructure/Autonomy/`
2. Wire `IObjectiveStore` into `BackgroundAgentService` (objectives as files, not config parameters)
3. Wire `CertifiedBrickHotSwapHost` for generation swaps (replace direct `IBrickLoader` calls)
4. Wire `RepairFeedbackPolicy` (default `OwnOutput`, 2-attempt budget, configurable)
5. Wire `SessionExecutionBackend` as default (not opt-in flags)
6. Add acceptance tracking: `ProposalRecorder` → record verdict + rate per lineage
7. E2E test: background agent extender → objective file → certified-held or admitted-swapped

**Exit Criteria:**
- `BackgroundAgentRegistry` self-extend path uses certified loop machinery (not adapter)
- At least one e2e test: Active extender reads objective, proposes, gate rejects OR admits-swaps
- Rejection test: each gate (correctness, mutation, seam, depth) rejects its defect class
- Repair test: at least one objective converges within 2-attempt budget

---

### 8.3 P1: Implement Watch Window + Auto-Rollback

**Scope:** Close autonomy loop with runtime regression detection

**Tasks:**
1. `BrickWatchWindow` class:
   - Track generation N swap timestamp, error rate baseline, latency baseline
   - Threshold breach detection (error rate +50%, latency +2σ, resource ceiling)
2. `CertifiedBrickHotSwapHost.RollbackGeneration(brickId, reason)` — reactivate N-1
3. `QuarantineLedger` — content-hash deny-list + revoked certificate index
4. `ExtensionLedger.RecordRollback(agentId)` — increment rollback count
5. Tier demotion: 2 rollbacks in 24h → demote lineage to Tier 1 (lose autonomy)
6. Revocation propagation: flag certificates whose `inputs` include revoked hash

**Exit Criteria:**
- E2E test: certified brick swapped → runtime error rate breach → auto-rollback → quarantine
- Quarantine test: re-submit same hash → refused permanently
- Demotion test: 2 rollbacks → next cycle refused at Tier 1 gate (human admission required)

---

### 8.4 P2: Dashboard UI for Trust Events

**Scope:** Visibility into autonomous actions

**Tasks:**
1. Web UI: trust event timeline (last 30 days), policy pack status, pause/resume controls
2. CLI: `ashlar trust dashboard` — table of recent events (gate verdicts, extension refusals, rollbacks)
3. Digest export: `GET /trust/events?format=json&since=...` — JSON for SIEM integration
4. Storage: persist `RuntimeObservation` events to append-only ledger (currently in-memory only)

**Exit Criteria:**
- Web UI renders last 30 trust events (gate passes/rejects, policy violations, extension refusals)
- CLI renders summary table: active policy pack, recent events (5 rows), pause status
- API export: JSON array of events with timestamp, kind, source, verdict, facts

---

### 8.5 P2: Implement Self-Extend Policy Pack Rules

**Scope:** Org-wide self-extend constraints via policy packs

**Tasks:**
1. Add `SelfExtendPolicyRules` class:
   - `enabled: bool`, `max_lineage_depth: int`, `max_unattended_cycles: int`, `max_cycles_per_hour: int`
   - `tier_0_allowlist: string[]`, `kernel_denylist: string[]`
2. `TrustPolicyPackRegistry.GetSelfExtendRules()` — read active pack
3. `ExtensionCeiling.ResolveWithPolicy(policyRules)` — merge policy + env + agent params (policy wins)
4. `AutonomousIterationHarness.ClassifyTier()` — read `tier_0_allowlist` + `kernel_denylist`
5. Add rules to `config/trust-packs/strict-enterprise.json` (demonstration pack)

**Exit Criteria:**
- Policy pack with `self_extend_enabled=false` → all extenders refuse (Passive-equivalent)
- Policy pack with `max_lineage_depth=0` → all machine-origin extenders refuse
- Policy pack with `tier_0_allowlist=["src/Ashlar.Bricks.Safe/*"]` → others classified Tier 1

---

## 9. Conclusion

### 9.1 Production Readiness Assessment

**Certification & Invariant Machinery:** ✅ **READY**  
**Trust Signatures:** ❌ **BLOCKING** (P0 security holes)  
**Self-Extend Runner:** ⚠️ **ADAPTER** (spike-grade, not certified loop)  
**Trust Log APIs:** ✅ **READY** (backend only)  
**Dashboard UI:** ❌ **MISSING**  
**Policy Packs:** ⚠️ **PARTIAL** (infra ready, self-extend rules missing)

**Overall Verdict:**  
Ashlar **cannot claim "autonomous self-extension with validation" in production** until:
1. Trust signature security holes (limitations 7-9) are CLOSED (**P0**)
2. Certified autonomy loop is integrated into background agent path (**P1**)
3. Watch window + auto-rollback are operational (**P1**)

The core machinery is **95% there** — invariants A-D are enforced and tested, cert-gate has teeth, policy narrowing works. The remaining 5% is **critical**: without fixing the signature holes, an attacker with source access trivially forges certificates and bypasses every gate.

### 9.2 What Must Stay in Core

**Trust Kernel (non-extractable):**
- Certification gate (analyzer, mutation, determinism)
- Invariant enforcement (A-D)
- Trust signatures (signer, verifier)
- Admission tiers (touch-set enforcement)
- Policy engine (narrowing, boundaries)
- Session isolation (attestation)

**Operator Surfaces (may extract):**
- Dashboard UI
- CLI commands
- Objective authoring tools
- Rollback/quarantine UI
- Acceptance reporting

**Boundary Test:** "Can an attacker with product-only access bypass this?" If yes → stays in core.

### 9.3 Product Coupling Found

**Commercial `ApprovalBridge`** (Discord integration) mentioned in `docs/SELF-EXTEND-AUDIT.md:62` — verify it lives in `commercial/` and does not leak into `src/` assemblies. If it does, extract.

---

## Appendix A: Audit Scope & Methodology

**Repository:** https://github.com/IanFrelinger/Ashlar  
**Commit:** `master` branch (latest)  
**Audit Date:** 2026-09-05  
**Duration:** 1 session (autonomous)

**Methodology:**
1. Read specification documents (`docs/SELF-EXTEND-AUDIT.md`, `docs/certification-evidence.md`, trust loop specs)
2. Trace control flow from `BackgroundAgentService` through self-extend path
3. Read source for invariant enforcement, cert-gate, trust signatures, policy packs
4. Search for dashboard APIs, trust log storage, policy pack implementations
5. Identify gaps between specification claims and implementation reality
6. Rank gaps by false-ADMIT risk (exploitability × impact)
7. Propose next PRs to close critical gaps

**Files Read:** 20+ source files, 4 specification documents  
**Lines Audited:** ~5000 lines of C# implementation + 2000 lines of docs  
**Tests Verified:** Invariant A-D test coverage (4 test files)  
**Spikes Reviewed:** `spikes/autonomy-first-flight/` (P2-P6, S1-S5 evidence)

---

## Appendix B: Glossary

- **Invariant A-D:** Four safety invariants enforced on self-extend path (cert-gate, narrowing, hold, ceiling)
- **Cert-gate:** Certification gate (mutation testing, determinism, dependency checks)
- **Trust kernel:** Components that define acceptance authority (gates, policies, tiers, signer)
- **Tier 0:** Autonomous admission (leaf bricks, no kernel touches)
- **Tier 1:** Human admission (kernel touches, cross-brick contracts)
- **Tier 2:** Human objective (changes to tiers, budgets, spec enforcement)
- **Spike-only:** Exists in `spikes/` with evidence, not wired into production path
- **False ADMIT:** A defective artifact passes gates and is certified
- **False REJECT:** A correct artifact fails gates and is refused (quality loss, not safety issue)
- **Slop escape:** A gate-passing artifact regresses in production (watch window miss)

---

**End of Audit Report**
