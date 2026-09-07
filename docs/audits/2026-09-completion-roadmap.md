# Ashlar Completion Roadmap: Path to Autonomous Self-Extension

**Date:** 2026-09-05  
**Status:** CEO Briefing — Audit Report  
**Objective:** Runtime capable of autonomous self-extensions and validation with built-in agents, maintaining PRODUCT/CORE separation

---

## Executive Summary

Ashlar has **proven the technical foundation** for autonomous self-extension: certification gates enforce safety invariants, tier-based admission controls blast radius, session sandboxing contains execution, and dogfood validation demonstrates the loop end-to-end. The remaining work is **hardening, adversarial validation, product separation, and public accountability** — not fundamental research.

**Current Position:**
- ✅ Safety invariants A–D enforced in production paths
- ✅ Autonomous iteration harness proven through P6/S5 campaigns
- ✅ Tier 0 autonomous admission with rollback/quarantine cycle
- ✅ Product split architecture documented and boundary-gated
- ⚠️  Certification composition signer vulnerability remains (limitation 9; limitations 7–8 closed by PR #523)
- ⚠️  Adversarial validation coverage incomplete
- ⚠️  Product repos not extracted; Forge product surface undefined
- ⚠️  Public ledger for autonomous runs not implemented

**The Path Forward:**

Six milestones (M0–M6) progress from honesty baseline to commercial claims. Each milestone has clear **exit criteria**, **dependencies**, and **ownership**. Conservative estimate: **M0–M3 can close in the current development cycle**; M4–M6 require product-team coordination and sustained dogfood operation.

**Key Risk:** Certification composition signer vulnerability (limitation 9) is **publicly documented**; composition records ignore explicitly supplied keys. Limitations 7–8 (signature stripping, schema downgrade) were closed by PR #523. M1 MUST close limitation 9 before any commercial claim.

---

## M0: Honesty Baseline — What We Can Claim Today

**Goal:** Establish the current provable state with zero false claims.

### Proven Capabilities

| Capability | Evidence | Limitations |
|-----------|----------|-------------|
| **Autonomous brick generation & certification** | P6 live model proposals; S5 3/5 certified at qwen3.8:27b; cert-gate CI runs 27918340788, 27918244198 | Single-task family (damage→health compositions); recorded/replay model only in CI; live models local-only |
| **Safety invariants enforced** | SELF-EXTEND-AUDIT verdicts A–D all ENFORCED; SelfExtendInvariant*Tests pass | Certification composition signer vulnerability remains (limitation 9; limitations 7–8 closed); no adversarial campaign yet |
| **Tier-based admission control** | Trust-loop spec sections 3, 7; Tier 0/1/2 classifier structural | Tier definitions not adversarially validated; kernel-touch smuggling untested |
| **Session containment** | P3-P5 flights: in-session build + execution; Docker backend hardening M26 | Host retains orchestration, mutant compilation, judgment (limitation 4); read-only rootfs unflown on live daemon |
| **Rollback & quarantine** | Trust-loop spec R5.1–R5.4; swap-host generation retention | Watch-window breach → auto-rollback path unflown (mechanism exists, not proven) |
| **Dogfood blocks 1–10** | DogfoodValidation.md; all test classes pass | Covers observation→adaptation→mesh; not closed-loop autonomous extension under adversarial conditions |
| **Product split architecture** | product-split.md; dependency-boundary-gate enforces src/→products/ prohibition | Products not extracted; Forge product surface undefined |

### Current Commercial Position

**Claimable Today:**
- "Certified brick generation with autonomous admission controls"
- "Structural safety enforcement with rollback capability"
- "Tier-based admission system with human gates for kernel changes"

**NOT Claimable:**
- ❌ "Adversarially hardened autonomous extension" (no adversarial campaign)
- ❌ "Production-ready unattended operation" (limitation 9 remains exploitable; no public ledger yet)
- ❌ "Public audit trail" (no ledger yet)
- ❌ "Forge product" (not extracted, not integrated)

### Exit Criteria

- [x] **M0.1** Document current proven state in this roadmap
- [x] **M0.2** Mark all non-proven claims as blocked
- [ ] **M0.3** CEO/board briefing: "We have the mechanism; here's what's between us and claims"
- [ ] **M0.4** Publish honesty baseline externally (blog/docs)

**Owner:** Framework team (documentation); CEO (external communication)  
**Dependencies:** None  
**Completion Target:** Current state (this document closes M0.1–M0.2)

---

## M1: Core Self-Extend Hardening — Seal, Hold, Canary, Disarm

**Goal:** Close certification vulnerabilities, prove CI enforcement, add disarm mechanisms.

### Work Streams

#### M1.1: Certification Schema Hardening

**Problem:** Limitation 9 allows composition signer key injection bypass. (Limitations 7–8 already closed by PR #523.)

**Status of Limitations 7–8 (CLOSED by PR #523, commit 966e6bf4):**
- ✅ Signature stripping (limitation 7): `CertificationVerifyOptions.Default` and `.Strict` now set `RequireEd25519Signature = true`
- ✅ Schema downgrade (limitation 8): `MinimumSchemaVersion = 2` enforced fail-closed by default

**Remaining Fix Required:**

1. **Fix composition signer key injection (limitation 9)**
   - `CompositionCertificationRecordSigner` constructor honors explicitly supplied `brickSigner` instead of discarding it
   - Remove `_ = brickSigner;` discard; thread through key resolution
   - **Exit:** Host passing real key via `brickSigner` mints compositions under that key, not committed constant

**Acceptance:**
- Limitation 9 fix compiled, tested, merged
- Limitation 9 section in certification-evidence.md marked CLOSED with fix commit ref

**Owner:** Runtime team (cryptography/certification subsystem)  
**Dependencies:** None  
**Risk:** Composition path consumers must supply real keys; migration guidance needed  
**Effort:** 1–2 days (only limitation 9 remains; 7–8 already closed)

#### M1.2: Hold-Admit-Swap CI Proof

**Problem:** Tier 1 hold → human admission → swap path exercised only in local spikes, not CI.

**Acceptance Test:**
- New CI job `self-extend-admission-gate.yml`:
  - Propose a kernel-touching change (Tier 1)
  - Loop holds (no swap)
  - Simulate human approval (`IApprovalGate.ApproveAsync`)
  - Verify swap occurs post-approval
  - Passes on master pushes and PR
- **Exit:** CI green; test demonstrates Tier 1 hold → admit → swap with no false admits

**Owner:** CI/platform team  
**Dependencies:** M1.1 (limitation 9 closure)  
**Effort:** 2–3 days

#### M1.3: Canary Tier 0 Swap in CI

**Problem:** Autonomous Tier 0 swaps only flown locally; no CI proof of end-to-end admission.

**Acceptance Test:**
- CI job `autonomous-canary-gate.yml`:
  - Load a pre-approved Tier 0 objective (leaf brick: no kernel touch)
  - Run full loop (propose → certify → autonomous admit → swap)
  - Verify generation N+1 active
  - Post-swap: 3 invocations, verify correctness
  - Passes on master only (not PR — no unattended swaps on PR branches)
- **Exit:** CI green; proves Tier 0 autonomous path including swap

**Owner:** Autonomy team  
**Dependencies:** M1.1, M1.2  
**Effort:** 3–4 days

#### M1.4: Disarm Mechanisms Proven

**Problem:** Pause, rollback, quarantine exist (trust-loop spec R5, R6) but not CI-proven.

**Acceptance Tests:**

1. **Pause under load:**
   - Simulator: queue 5 autonomous objectives
   - Issue pause mid-flight
   - Verify: no swaps after pause acknowledgment; in-flight sessions terminate cleanly; resume lossless
   - Test: `DisarmPauseUnderLoadTests.PauseIssuedMidFlight_NoSwapsAfterAck_ResumeLossless`

2. **Rollback on watch-window breach:**
   - Swap generation N+1 (known-bad candidate: sleeps 10s per invocation)
   - Watch window detects latency breach
   - Verify: auto-rollback to N; N+1 hash quarantined; revocation in store
   - Test: `DisarmRollbackTests.WatchWindowBreachLatency_AutoRollback_HashQuarantined`

3. **Quarantine propagates:**
   - Quarantine a Tier 0 brick B1
   - Verify: composition C1 that includes B1 flagged suspect (R5.4)
   - Test: `DisarmQuarantinePropagationTests.ConstituentQuarantined_CompositionFlaggedSuspect`

**Exit:** All three test classes pass in CI

**Owner:** Runtime team (swap host, watch window)  
**Dependencies:** M1.3 (canary swap must exist to roll back from)  
**Effort:** 5–7 days

### M1 Exit Criteria

- [x] **M1.1a** Limitations 7–8 closed (PR #523, commit 966e6bf4)
- [ ] **M1.1b** Limitation 9 closed; certification-evidence.md updated
- [ ] **M1.2** Tier 1 hold → admit → swap proven in CI
- [ ] **M1.3** Tier 0 autonomous swap proven in CI (canary)
- [ ] **M1.4** Pause, rollback, quarantine proven in CI
- [ ] **M1.5** All M1 gates green on master for 7 consecutive days

**Owner:** Runtime + CI teams  
**Dependencies:** M0 (baseline documented)  
**Completion Target:** 15–20 engineering days from M0 sign-off

---

## M2: Adversarial Validation Pack — Injection, Smuggling, Evasion

**Goal:** Trust-loop spec §8 adversarial campaign plus self-extend spec §8 autonomous-specific attacks.

### Attack Scenarios (Required)

#### Trust-Loop Base Pack (from trust-loop-spec.md §8)

| Attack | Defense | Test |
|--------|---------|------|
| **Sandbox escape** (path traversal, out-of-root reads) | `ProposerConfinement` + session scratch paths | `AdversarialSandboxEscapeTests` |
| **Premature completion** (proposer claims done on failing artifact) | Gate verdict is authority; proposer self-reports ignored | `AdversarialPrematureCompletionTests` |
| **Malformed actions** (null args, unknown actions) | Harness validates action schema | `AdversarialMalformedActionsTests` |
| **Anchor abuse** (nonexistent/ambiguous edit anchors) | Anchored-edit verifier rejects | `AdversarialAnchorAbuseTests` |
| **Oscillation** (non-converging edit sequences) | Hash-history detects revisits; budget ceiling enforces | `AdversarialOscillationTests` |
| **Oversized outputs** (garbage/too-large artifacts) | Artifact size ceiling enforced by harness | `AdversarialOversizedOutputTests` |
| **"Ends bad" run** (proposer ships broken work) | No certificate without gate pass | `AdversarialEndsBadTests` |
| **"Recovers" run** (proposer repairs to good work) | Certified with full provenance | `AdversarialRecoversTests` |

#### Autonomous-Specific Pack (from self-extend spec §8)

| Attack | Defense | Test |
|--------|---------|------|
| **Objective injection** (instruction-shaped content in watched workflow) | Schema-constrained extraction (R1.2) | `AdversarialObjectiveInjectionTests` |
| **Kernel-touch smuggling** (indirect reference via transitive dep) | Analyzer gate + swap-host triple-check (R3.2) | `AdversarialKernelTouchSmugglingTests` |
| **Judge-growth attempt** (self-produced artifact as gate) | Human admission required (R4.3) | `AdversarialJudgeGrowthTests` |
| **Depth laundering** (reset generation_depth via fresh session) | Input-chain hashes detect (R4.1/R4.2) | `AdversarialDepthLaunderingTests` |
| **Capability re-widening** (depth-n+1 requests absent tool) | Policy narrowing validator (R4.4) | `AdversarialCapabilityReWideningTests` |
| **Regression theater** (healthy in gates, misbehaves post-swap) | Watch window + auto-rollback (R5.2–R5.4) | `AdversarialRegressionTheaterTests` |
| **Revoked-hash resubmission** (bit-identical) | Quarantine store permanent refusal | `AdversarialRevokedHashResubmissionTests` |
| **Pause theater** (pause issued, swaps continue) | Pause halts intake & swaps immediately (R6.2) | `AdversarialPauseTheaterTests` |

### Acceptance Criteria

- All 16 attack tests implemented as xUnit test classes in `src/Ashlar.Tests.Infrastructure/Tests/Adversarial/`
- CI gate `adversarial-validation-gate.yml` runs full pack on every PR touching autonomy/certification paths
- **Campaign requirement:** Zero false certificates, zero unauthorized swaps across all scenarios
- **"Ends good" proof:** One full machine-sourced objective → Tier 0 classification → certified → autonomous swap → clean watch window → digest entry (trust-loop spec §8 requirement)
- **"Ends bad" proof:** Same path terminating in quarantine with fence catalog grown by one

### Work Estimate

- **Setup:** Test harness for controlled proposer doubles (2 days)
- **Base pack:** 8 tests × 1 day each = 8 days
- **Autonomous pack:** 8 tests × 1.5 days each = 12 days
- **Integration:** CI gate + campaign orchestration (3 days)
- **Total:** 25 engineering days

**Owner:** Security + runtime teams  
**Dependencies:** M1 (hardened core must exist to validate against)  
**Completion Target:** 25 days from M1 close

### M2 Exit Criteria

- [ ] **M2.1** All 16 adversarial tests green
- [ ] **M2.2** `adversarial-validation-gate.yml` enforced on master
- [ ] **M2.3** Campaign log published: X scenarios, 0 false admits, Y fences added
- [ ] **M2.4** Trust-loop spec §8 acceptance criteria met

**Risk:** Attack surface is large; expect to discover new defenses needed during test implementation. Budget 20% contingency.

---

## M3: Forge Product Repo Scaffold + Consumes Ashlar

**Goal:** Extract Forge as a consuming product repo, demonstrating PRODUCT/CORE separation.

### Architecture (from product-split.md)

**Forge** is an **adaptive app-factory product**, NOT framework. It:
- Consumes Ashlar via NuGet packages (`Ashlar.Hosting`, `Ashlar.Contracts`)
- Provides UX for "generate an app from objective"
- Ships as a separate product with its own roadmap
- **Never** referenced by `src/Ashlar.*` (one-way rule enforced by dependency-boundary-gate)

### M3 Deliverables

#### M3.1: Forge Repo Scaffold

**Structure:**
```
github.com/IanFrelinger/ashlar-forge/
├── src/
│   ├── Ashlar.Forge/                    # Core Forge product
│   ├── Ashlar.Forge.Cli/                # CLI entrypoint
│   └── Ashlar.Forge.Contracts/          # Forge-specific contracts
├── tests/
│   └── Ashlar.Forge.Tests/
├── docs/
│   ├── README.md                         # Forge product docs
│   └── ConsumesCoreVersion.md            # Pins Ashlar core version
├── .github/workflows/
│   └── forge-gate.yml                    # Forge CI (builds, tests)
└── Ashlar.Forge.sln
```

**Key files (planned, in separate ashlar-forge repo):**
- src/Ashlar.Forge/Ashlar.Forge.csproj (not yet created):
  ```xml
  <PackageReference Include="Ashlar.Hosting" Version="0.1.2" />
  <PackageReference Include="Ashlar.Contracts" Version="0.1.2" />
  ```
- No `ProjectReference` to `../Ashlar/src/**` allowed (consumes NuGet only)

**Acceptance:**
- Forge repo created on GitHub
- `dotnet build` succeeds against published Ashlar packages
- CI green (basic build/test)
- README states: "Forge is a product built on Ashlar framework; see github.com/IanFrelinger/Ashlar"

**Effort:** 2 days (repo setup + skeleton)

#### M3.2: Forge Consumes Ashlar Trust Services

**Problem:** Forge needs certification but must not reimplement it.

**Integration Points:**

1. **Use `AddAshlar()` hosting registration:**
   ```csharp
   // Ashlar.Forge.Cli/Program.cs
   builder.Services.AddAshlar(options => {
       options.TrustEnabled = true;
       options.DeploymentProfile = "secure-workstation";
   });
   ```

2. **Consume `ICertificationGate` port:**
   ```csharp
   // Ashlar.Forge/ForgeService.cs
   public class ForgeService {
       private readonly ICertificationGate _gate; // injected from Ashlar.Hosting

       public async Task<ForgeResult> GenerateAppAsync(string objective) {
           var candidate = await _proposer.ProposeAsync(objective);
           var decision = await _gate.CertifyAsync(candidate); // Ashlar's gate
           if (decision.Outcome == CertificationOutcome.Admitted) {
               return ForgeResult.Success(candidate);
           }
           return ForgeResult.Rejected(decision.Reason);
       }
   }
   ```

3. **Forge CI calls Ashlar cert-gate:**
   - `forge-gate.yml` would include a cert-gate step (script not yet implemented)
   - Planned script would use Ashlar's `CertificationGate` via `ICertificationGate` DI

**Acceptance:**
- Forge `ForgeService.GenerateAppAsync` calls `ICertificationGate.CertifyAsync`
- Forge CI runs cert-gate on Forge-generated artifacts
- No Forge code reimplements certification logic

**Effort:** 3 days (integration + CI wiring)

#### M3.3: Publish Forge v0.1.0-alpha

**Acceptance:**
- Forge package published to NuGet: `Ashlar.Forge` v0.1.0-alpha
- Forge CLI installable: `dotnet tool install -g Ashlar.Forge.Cli --prerelease`
- Forge README documents: "This is an early product preview; uses Ashlar 0.1.2 core"

**Effort:** 1 day (packaging + publish)

### M3 Exit Criteria

- [ ] **M3.1** Forge repo scaffold exists and builds against published Ashlar
- [ ] **M3.2** Forge consumes `ICertificationGate` and `AddAshlar()` hosting
- [ ] **M3.3** Forge v0.1.0-alpha published
- [ ] **M3.4** Dependency-boundary-gate in Ashlar repo still passes (no `src/` → Forge refs)
- [ ] **M3.5** Blog post: "Introducing Forge: An Ashlar Product" (demonstrates separation)

**Owner:** Product team (Forge)  
**Dependencies:** M0 (architecture defined), Ashlar 0.1.2 published  
**Completion Target:** 6 days from M2 close (can overlap with M2 tail)

---

## M4: Forge.Verify — Cursor-Shaped Harness (Separate Product)

**Goal:** Forge consumes Ashlar's trust services to verify *Cursor-generated* apps, demonstrating that Ashlar certification is product-agnostic.

### M4 Concept

**Forge.Verify** is a Forge subcommand/module that:
- Accepts a Cursor-generated artifact (e.g., a React component, a Python script)
- Runs it through Ashlar's `CertificationGate` (witness, mutation, determinism)
- Issues a certificate if gates pass
- Demonstrates: "Ashlar certifies artifacts from ANY proposer, including Cursor"

### M4 Deliverables

#### M4.1: Cursor Artifact Adapter

**Problem:** Ashlar's gate expects bricks; Cursor generates arbitrary code.

**Adapter:**
```csharp
// Ashlar.Forge/Adapters/CursorArtifactAdapter.cs
public class CursorArtifactAdapter : IBrickCandidate {
    public CursorArtifactAdapter(string sourceCode, string language, Witness witness) {
        // Wrap Cursor source as a brick candidate
    }
}
```

**Acceptance:**
- Adapter converts Cursor source → `IBrickCandidate`
- Gate runs unchanged (witnesses are language-agnostic)

**Effort:** 2 days

#### M4.2: Forge.Verify CLI Command

```bash
$ ashlar-forge verify \
    --source cursor-output/Component.tsx \
    --witness witness.json \
    --output verification-report.json
```

**Acceptance:**
- Command loads Cursor artifact
- Runs through Ashlar `ICertificationGate`
- Outputs certificate or rejection report
- CI test: `ForgeVerifyCursorArtifactTests.CursorReactComponent_WithWitness_Certifies`

**Effort:** 3 days (CLI + integration test)

#### M4.3: Public Demo Artifacts

**Publish:**
- `examples/cursor-react-component/` (Cursor-generated React component)
- `examples/cursor-python-script/` (Cursor-generated Python script)
- Each with witness, certificate, and README explaining how it was verified

**Acceptance:**
- 2 example artifacts published in Forge repo
- README: "These were generated by Cursor and certified by Ashlar via Forge.Verify"

**Effort:** 2 days (documentation + examples)

### M4 Exit Criteria

- [ ] **M4.1** Cursor artifact adapter implemented
- [ ] **M4.2** `ashlar-forge verify` command works end-to-end
- [ ] **M4.3** 2 public Cursor-generated examples certified
- [ ] **M4.4** Blog post: "Certifying Cursor Output with Ashlar"

**Owner:** Forge product team  
**Dependencies:** M3 (Forge product exists)  
**Completion Target:** 7 days from M3 close

**Commercial Implication:** Positions Ashlar as certification infrastructure for *any* AI code generator, not just Ashlar's internal loops.

---

## M5: Dogfood Autonomous Loop on Forge Samples — Public Ledger

**Goal:** Run unattended autonomous loop against Forge sample objectives every release, with public audit trail.

### M5 Architecture

#### M5.1: Dogfood Automation

**CI Job (planned):** .github/workflows/dogfood-autonomous-loop.yml (not yet implemented)

**Trigger:** On every Ashlar release tag (e.g., `v0.1.3`)

**Steps:**
1. Load 5 Forge sample objectives from `samples/forge-objectives/`:
   - `objective-1-simple-rest-api.json`
   - `objective-2-state-machine.json`
   - `objective-3-parser.json`
   - `objective-4-composition.json`
   - `objective-5-ui-component.json`
2. For each objective:
   - Run `ashlar-forge generate --objective $obj --mode autonomous`
   - Loop: propose → certify → (Tier 0) admit & swap OR (Tier 1) hold
   - Record: attempts, verdicts, artifacts, certificates
3. Publish results to public ledger (see M5.2)

**Acceptance:**
- CI job runs successfully on `v0.1.3` release
- 5 objectives attempted
- Ledger updated with results

**Effort:** 4 days (CI job + objective authoring)

#### M5.2: Public Ledger Implementation

**Ledger Spec:**

**Storage:** `github.com/IanFrelinger/ashlar-public-ledger` (separate repo, GitHub Pages site)

**Structure:**
```
ledger/
├── index.html                           # Landing page
├── runs/
│   ├── 2026-09-15-v0.1.3/
│   │   ├── summary.json                 # Run metadata
│   │   ├── objective-1/
│   │   │   ├── proposal.json
│   │   │   ├── certificate.json         # If admitted
│   │   │   └── rejection.json           # If rejected
│   │   ├── objective-2/
│   │   └── ...
│   └── 2026-09-22-v0.1.4/
└── README.md
```

**`summary.json` schema:**
```json
{
  "run_id": "2026-09-15-v0.1.3",
  "ashlar_version": "0.1.3",
  "forge_version": "0.1.1-alpha",
  "timestamp": "2026-09-15T18:32:00Z",
  "objectives_attempted": 5,
  "tier_0_admitted": 2,
  "tier_1_held": 1,
  "rejected": 2,
  "model": "qwen3.8:27b",
  "ledger_url": "https://ianfrelinger.github.io/ashlar-public-ledger/runs/2026-09-15-v0.1.3/"
}
```

**Acceptance:**
- Ledger repo exists and is public
- CI job pushes results to ledger repo after each dogfood run
- Landing page shows: latest run summary, historical runs, link to Ashlar repo

**Effort:** 3 days (repo setup + CI integration)

#### M5.3: Forge Sample Objectives (Pre-Authored)

**Requirement:** 5 human-authored objectives with witnesses, ready for dogfood loop.

**Samples (from S4/S5 campaigns, adapted for Forge):**
1. `tag-scan-classifier` (classification)
2. `door-lock-transition` (state machine)
3. `semver-parse` (parser)
4. `damage-to-health-pipeline` (composition)
5. `rgb-hex-parse` (parser variant)

**Acceptance:**
- 5 objectives in `samples/forge-objectives/` (Forge repo or Ashlar repo)
- Each has: objective JSON, witness JSON, contract Markdown

**Effort:** 2 days (port from S5 campaign + document)

### M5 Exit Criteria

- [ ] **M5.1** Dogfood CI job runs on release tags
- [ ] **M5.2** Public ledger repo live and receiving results
- [ ] **M5.3** 5 Forge sample objectives authored
- [ ] **M5.4** First ledger entry published: `2026-09-15-v0.1.3` (example date)
- [ ] **M5.5** Blog post: "Ashlar's Autonomous Loop — Public Evidence Every Release"

**Owner:** CI/platform team + product team  
**Dependencies:** M4 (Forge.Verify exists)  
**Completion Target:** 9 days from M4 close

**Commercial Implication:** Public ledger = accountability. Shows "we dogfood our own autonomy claims with public evidence."

---

## M6: Commercial Claims Unlocked

**Goal:** All technical blockers removed; sales/marketing can claim "autonomous self-extension with public accountability."

### M6 Checklist

#### M6.1: Technical Readiness

- [ ] M1 closed: Core hardened, disarm mechanisms proven
- [ ] M2 closed: Adversarial validation pack green
- [ ] M3 closed: Product separation demonstrated (Forge scaffold)
- [ ] M4 closed: Cursor-shaped harness works (Forge.Verify)
- [ ] M5 closed: Public ledger live with at least 3 release runs

**Acceptance:** All M1–M5 exit criteria met; no open P0 security findings.

#### M6.2: Legal & Compliance

**Requirements:**
- Legal review of autonomous admission claims (liability, warranty)
- Privacy review of public ledger (no PII in published artifacts)
- Security audit of certification signature model (post-M1 fixes)

**Acceptance:**
- Legal sign-off document: "Autonomous Tier 0 claims approved for marketing"
- Privacy audit: Ledger artifacts scrubbed/anonymized
- Security audit: No critical findings; medium/low documented in known-limitations

**Owner:** Legal + security teams  
**Effort:** Parallel to M4–M5; requires 2 weeks lead time

#### M6.3: Commercial Collateral

**Deliverables:**
1. **Sales deck:** "Ashlar: The Self-Extending Runtime"
   - Technical deep-dive slides (safety invariants, tier model, rollback)
   - Public ledger as proof point
   - Forge as product case study
2. **Demo video:** "Watch Ashlar extend itself" (Tier 0 swap, post-swap watch, digest entry)
3. **Customer FAQ:** "Is autonomous admission safe?" (answer: tier model, adversarial validation, public ledger)

**Acceptance:**
- Sales deck approved by CTO + CEO
- Demo video < 5 min, published on YouTube
- FAQ published on ashlar.dev

**Owner:** Marketing + product marketing  
**Dependencies:** M5 (public ledger must exist for proof point)  
**Effort:** 5 days (collateral creation)

#### M6.4: Public Launch

**Announcement:**
- Blog post: "Ashlar 0.2.0: Autonomous Self-Extension with Public Accountability"
- Press release (if appropriate)
- HN/Reddit post with ledger link as proof

**Acceptance:**
- Public announcement published
- Ledger has ≥3 historical runs visible
- No retraction/correction needed in first 30 days (honesty check)

**Owner:** CEO + marketing  
**Dependencies:** M6.1–M6.3 (all readiness gates green)  
**Completion Target:** Launch at next major release (0.2.0)

### M6 Exit Criteria

- [ ] **M6.1** All M1–M5 closed; no P0 findings
- [ ] **M6.2** Legal, privacy, security sign-offs complete
- [ ] **M6.3** Commercial collateral ready
- [ ] **M6.4** Public launch executed
- [ ] **M6.5** First enterprise customer using autonomous Tier 0 (proof of commercial viability)

**Commercial Claims Unlocked:**
- ✅ "Autonomous self-extension with adversarially validated safety"
- ✅ "Tier-based admission control with human gates for kernel changes"
- ✅ "Public audit trail every release"
- ✅ "Certifies artifacts from any AI generator (Cursor, Forge, custom)"

---

## Dependencies & Critical Path

### Dependency Graph

```
M0 (baseline) → M1 (harden) → M2 (adversarial) → M3 (Forge scaffold)
                                                    ↓
                                                  M4 (Forge.Verify)
                                                    ↓
                                                  M5 (dogfood ledger)
                                                    ↓
                                                  M6 (commercial launch)
```

**Critical Path:** M0 → M1 → M2 → M5 → M6 (total: 59 days engineering + 2 weeks legal/security)

**Parallelizable:**
- M3 (Forge scaffold) can start during M2 (no blocking dependency)
- M4 (Forge.Verify) can overlap with M2 tail (once M3 scaffold exists)
- M6.2 (legal/security review) can start during M4–M5

### Risk & Mitigation

| Risk | Impact | Mitigation |
|------|--------|------------|
| **Adversarial tests discover new defenses needed (M2)** | +10 days | Budget 20% contingency; prioritize kernel-touch smuggling and judge-growth (highest risk) |
| **Legal blocks autonomous claims (M6.2)** | Launch delayed | Start legal review early (during M4); prepare fallback: "Autonomous with operator oversight" (Tier 1 only) |
| **Forge team unavailable (M3–M4)** | Product demo blocked | Core team can stub Forge scaffold; defer full Forge.Verify to post-launch |
| **Public ledger attracts adversarial scrutiny** | Reputation risk | Expect it; have incident-response plan; ledger shows we patch findings (not hide them) |
| **Composition signer vulnerability (limitation 9) exploited before M1 close** | **Medium** | Fix composition key injection immediately; limitations 7–8 already closed (signature/schema hardening) |

---

## Workstream Ownership

### Framework Team (Core Runtime)

**Owns:** M1 (hardening), M2 (adversarial validation)

**Responsibilities:**
- Close remaining certification vulnerability (limitation 9; 7–8 already closed)
- Implement disarm mechanisms (pause, rollback, quarantine)
- Write adversarial test pack (16 tests)
- Maintain cert-gate, kernel-gate, adversarial-gate CI

**Headcount:** 2–3 engineers (M1–M2 duration: 40 days)

### Product Team (Forge)

**Owns:** M3 (Forge scaffold), M4 (Forge.Verify)

**Responsibilities:**
- Extract Forge product repo
- Integrate Ashlar trust services (`AddAshlar`, `ICertificationGate`)
- Build Cursor artifact adapter
- Author Forge sample objectives

**Headcount:** 1–2 engineers (M3–M4 duration: 13 days)

### CI/Platform Team

**Owns:** M5 (dogfood automation + public ledger)

**Responsibilities:**
- Build dogfood CI job (every release)
- Implement public ledger repo + GitHub Pages site
- Publish ledger entries automatically

**Headcount:** 1 engineer (M5 duration: 9 days)

### Legal/Security Team

**Owns:** M6.2 (compliance review)

**Responsibilities:**
- Legal sign-off on autonomous claims
- Privacy audit of public ledger
- Security audit of post-M1 certification model

**Headcount:** Legal counsel + 1 security auditor (2 weeks, parallel to M4–M5)

### Marketing/Product Marketing

**Owns:** M6.3–M6.4 (commercial collateral + launch)

**Responsibilities:**
- Sales deck, demo video, FAQ
- Public announcement (blog, press, HN)

**Headcount:** Product marketing lead + content (5 days, after M5)

---

## Timeline Estimate

**Conservative Estimate (Sequential):**

| Milestone | Duration | Cumulative |
|-----------|----------|------------|
| M0 (baseline) | **Complete** | Day 0 |
| M1 (harden) | 15–20 days | Day 20 |
| M2 (adversarial) | 25 days | Day 45 |
| M3 (Forge) | 6 days (overlaps M2 tail) | Day 45 |
| M4 (Forge.Verify) | 7 days | Day 52 |
| M5 (dogfood) | 9 days | Day 61 |
| M6.2 (legal) | 10 days (parallel to M4–M5) | Day 61 |
| M6.3–M6.4 (launch) | 5 days | **Day 66** |

**Aggressive Estimate (Parallel):**

- M1 + M3 (harden + Forge scaffold): 20 days
- M2 + M4 (adversarial + Forge.Verify): 25 days
- M5 + M6.2 (dogfood + legal): 10 days (parallel)
- M6.3–M6.4 (launch): 5 days
- **Total: 60 days** (with 3 teams running parallel)

**Recommendation:** Plan for **70 days** (10-week sprint) to allow for contingencies. Launch at 0.2.0 milestone.

---

## Success Metrics

### M1–M2: Technical Hardening

- **Zero** exploitable certification vulnerabilities (limitation 9 closed; 7–8 already closed by PR #523)
- **Zero** false certificates in adversarial campaign (16/16 tests green)
- **100%** disarm mechanism coverage (pause, rollback, quarantine CI-proven)

### M3–M4: Product Separation

- **Zero** `src/Ashlar.*` → `Ashlar.Forge.*` references (dependency-boundary-gate enforces)
- **≥2** Cursor-generated artifacts certified via Forge.Verify

### M5: Public Accountability

- **≥3** historical dogfood runs in public ledger
- **100%** of ledger entries have: summary, proposals, verdicts, certificates/rejections

### M6: Commercial Launch

- **≥1** enterprise customer using Tier 0 autonomous admission in production
- **Zero** retractions/corrections to autonomous claims in first 30 days post-launch

---

## Recommendations

### Immediate Actions (Next 7 Days)

1. **CEO sign-off on M0 baseline** (this document)
2. **Assign owners to M1–M6** (framework, product, CI, legal teams)
3. **Begin M1.1b** (close limitation 9) — composition signer vulnerability is public and remains exploitable
4. **Schedule legal kick-off** for M6.2 (2-week lead time needed)

### Strategic Priorities

**Priority 1: M1 (harden)** — Closes remaining certification vulnerability (limitation 9); must complete before any commercial claim.

**Priority 2: M2 (adversarial)** — Proves safety model; required for "adversarially validated" claim.

**Priority 3: M5 (public ledger)** — Public accountability is our differentiator; this unlocks "we show our work" positioning.

**Priority 4: M3–M4 (Forge)** — Demonstrates PRODUCT/CORE separation; shows Ashlar is platform, not monolith.

### Open Questions for CEO Decision

1. **Aggressive vs. conservative timeline?**
   - Aggressive (60 days, 3 parallel teams) = higher risk, faster launch
   - Conservative (70 days, sequential with overlap) = safer, milestone gates enforced

2. **Public ledger scope:**
   - Option A: Full artifacts published (proposals, certificates) = maximum transparency, potential IP exposure
   - Option B: Summaries only (verdicts, metadata) = less transparency, safer
   - **Recommendation:** Option A with anonymization/scrubbing pass (privacy audit in M6.2)

3. **What happens if M2 adversarial tests fail badly?**
   - Fallback: Launch with "Tier 1 only" (human admission always) — still valuable, less bold claim
   - Recommendation: Budget 20% contingency (M2 = 30 days instead of 25); if still blocked, trigger fallback decision at Day 30

4. **Forge ownership post-M3:**
   - Forge is extractable but not extracted yet (still in `products/Ashlar.Forge`)
   - Decision: Extract to separate repo immediately (M3), or keep in-tree until 0.2.0 launch?
   - **Recommendation:** Extract immediately (M3.1) — proves architecture, unblocks Forge team independence

---

## Appendix A: Current CI Gate Inventory

**Total Gates:** 57 workflows in `.github/workflows/`

**Safety-Critical Gates (Enforced on Master):**

| Gate | Scope | Status |
|------|-------|--------|
| `cert-gate.yml` | Certification path (atom, composition, generation, dogfood) | ✅ Green |
| `kernel-gate.yml` | Kernel hardening (tiers A–E) | ✅ Green |
| `self-extend-invariant-*-tests` | Safety invariants A–D (cert-gate inheritance, policy narrowing, fail-closed, recursion ceiling) | ✅ Green |
| `ship-gate.yml` | Pre-ship verification | ✅ Green |
| `full-platform-readiness-gate.yml` | Cross-platform (Linux/macOS/Windows) | ✅ Green |
| `testing-strategy-gate.yml` | Test coverage + strategy audit | ✅ Green |
| `dependency-boundary-gate.yml` | Enforces `src/` → `products/` prohibition | ✅ Green |

**Additional Gates:**
- `mcp-a2a-gate.yml` (MCP/A2A protocols)
- `portability-gate.yml` (brick portability)
- `distribution-matrix-gate.yml` (NuGet package matrix)
- `mesh-lab-tls-gate.yml` (mesh TLS)
- `environment-setup-gate-v1.yml` (environment setup)
- `onboarding-quickstart-gate.yml` (onboarding verification)
- ... (50+ more gates)

**M1–M2 Will Add:**
- `self-extend-admission-gate.yml` (M1.2: Tier 1 hold → admit → swap)
- `autonomous-canary-gate.yml` (M1.3: Tier 0 autonomous swap)
- `adversarial-validation-gate.yml` (M2: 16 adversarial tests)

---

## Appendix B: Key Document References

| Document | Location | Relevance |
|----------|----------|-----------|
| **Architecture.md** | `docs/Architecture.md` | Framework layers, trust architecture |
| **SELF-EXTEND-AUDIT.md** | `docs/SELF-EXTEND-AUDIT.md` | Invariants A–D enforcement proof |
| **certification-evidence.md** | `docs/certification-evidence.md` | Current certification proof ledger; limitations 7–8 closed, limitation 9 open |
| **trust-loop-spec.md** | `docs/trust-loop/ashlar-trust-loop-spec.md` | Trust-loop normative spec (§8 adversarial) |
| **self-extend-spec.md** | `docs/trust-loop/trust-loop-ext-autonomous-self-extension.md` | Autonomous-specific spec (tier model, recursion discipline) |
| **product-split.md** | `docs/architecture/product-split.md` | Framework vs. product boundary |
| **ProjectTiers.md** | `docs/ProjectTiers.md` | Repo map (Tier 0 kernel, Tier 3 products) |
| **DogfoodValidation.md** | `docs/DogfoodValidation.md` | Dogfood blocks 1–10 status |

---

## Appendix C: Glossary

| Term | Definition |
|------|------------|
| **Brick** | Modular, contract-based component; Ashlar's execution primitive |
| **Certification** | Multi-gate validation (witness, mutation, determinism, analyzer) yielding signed certificate |
| **Tier 0** | Autonomous admission (leaf bricks, no kernel touch) |
| **Tier 1** | Human admission required (kernel-touching changes) |
| **Tier 2** | Human objective required (meta-changes: tier rules, budgets, spec enforcement) |
| **Trust Kernel** | Gates, policies, admission tiers, swap host, certifier — the authority perimeter |
| **Watch Window** | Post-swap observation period (contract conformance, error rate, latency, resource ceilings) |
| **Quarantine** | Revoked certificate; artifact hash permanently refused; failure triaged into fence/probe |
| **Forge** | Adaptive app-factory product (consumes Ashlar, generates apps from objectives) |
| **Public Ledger** | GitHub Pages site with dogfood run results (proposals, verdicts, certificates) every release |
| **Invariants A–D** | (A) Cert-gate inheritance, (B) Monotonic policy narrowing, (C) Fail-closed default, (D) Recursion ceiling |

---

**End of Roadmap Document**

**Status:** Ready for CEO/board review  
**Next Action:** CEO sign-off → Assign owners → Begin M1.1 (harden)  
**Questions:** Contact framework team lead or CTO
