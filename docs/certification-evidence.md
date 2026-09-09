# Certification evidence ledger

Falsifiable proof ledger for atom certification, general generation, composition certification, and dogfood. Each row cites how it was proven and the CI run (when applicable).

Version pin: published feed `0.1.2` (`ci/published-version`). Repo `VERSION` is not the public pin.

## Proof index

| Property | Proof mechanism | Result | CI run |
|----------|-----------------|--------|--------|
| Atom portability (spike steps 1–5) | `spikes/portability/run-portability-spike.sh` — generate, certify, pack, external consume, cross-project execute | **PASS** (all steps) | Local spike; re-run `run-portability-spike.sh` for fresh summary |
| Atom gate teeth (strong witness) | `CertificationGateTeethTests.GoodBrick_StrongWitness_Admits_WithZeroEscapeRate` | **ADMIT**, `escape_rate=0` | [Cert gate 27918340788](https://github.com/IanFrelinger/Nexo/actions/runs/27918340788) |
| Atom gate teeth (weak witness) | `CertificationGateTeethTests.WeakWitness_AllowsMutantEscapes_RejectsWithTeeth` | **REJECT**, `mutation`, `escape_rate > 0` | [Cert gate 27918340788](https://github.com/IanFrelinger/Nexo/actions/runs/27918340788) |
| General generation 4b (buggy rejects) | `GenerationSafetyTests.BuggyGeneration_StrongWitness_Rejects` | **REJECT**, `correctness` \| `mutation` | [Cert gate 27918340788](https://github.com/IanFrelinger/Nexo/actions/runs/27918340788) |
| General generation 4a (correct admits) | `GenerationSafetyTests.GoodGeneration_StrongWitness_Admits_WithZeroEscapeRate` | **ADMIT**, `escape_rate=0` | [Cert gate 27918340788](https://github.com/IanFrelinger/Nexo/actions/runs/27918340788) |
| Composition gate 4b (broken seam) | `CompositionCertificationGateTeethTests.BrokenComposition_StrongWitness_Rejects` | **REJECT**, `seam` | [Cert gate 27918340788](https://github.com/IanFrelinger/Nexo/actions/runs/27918340788) |
| Composition gate 4c (weak witness) | `CompositionCertificationGateTeethTests.CorrectComposition_WeakWitness_Rejects_WithStructuralTeeth` | **REJECT**, `mutation`, `composition_escape_rate > 0` | [Cert gate 27918340788](https://github.com/IanFrelinger/Nexo/actions/runs/27918340788) |
| Damage-resolver dogfood (honest) | `DamageResolverDogfoodTests.HonestCursorGeneration_Admits_WithZeroEscapeRate` | **ADMIT**, `escape_rate=0`, `signed=true` | [Cert gate 27918244198](https://github.com/IanFrelinger/Nexo/actions/runs/27918244198) @ `802e6d18` |
| Damage-resolver dogfood (buggy) | `DamageResolverDogfoodTests.BuggyCursorGeneration_Rejects` | **REJECT**, `correctness` \| `mutation` | [Cert gate 27918244198](https://github.com/IanFrelinger/Nexo/actions/runs/27918244198) @ `802e6d18` |
| Autonomy first flight (live engine) | `spikes/autonomy-first-flight/run-first-flight.ps1` — one real iteration: attested Docker session → full chain → Tier-0 swap → watch window | **PASS**, `AdmittedAndSwapped`, `escape_rate=0` | Local spike @ `1afac86d`; re-run the script for a fresh flight |
| Autonomy in-session build (P3) | Same flight with `-SessionBuild` — candidate compiles INSIDE the attested `dotnet/sdk:9.0` session over `ExecAsync`, offline | **PASS**, `session-build` input on the certificate | Local spike @ `d71d045f` |
| Autonomy in-session execution (P5a) | Flight with `-SessionExecute` — witness, determinism, and every mutant EXECUTE inside the session; the gate judges raw observations | **PASS**, `session-execution` input, `escape_rate=0` | Local spike @ `bf8821db` |
| Model-proposed candidate (P5b) | Flight with `-Proposed` — recorded model proposal, proposer signature in lineage, full containment; admitted only after two honest mutation REJECTs forced witness hardening | **PASS** after campaign: 2× `BudgetExhausted`, REJECT 0.16, REJECT 0.05, then `AdmittedAndSwapped` | Local spike @ `bf8821db` |
| LIVE model proposal (P6) | Flight with `-Live` — ollama `codellama:7b` called AT FLIGHT TIME, witness-blind prompt, recording committed; the gate judges each sample | **PASS on sample 4** (`AdmittedAndSwapped`, escape 0); measured acceptance 1/4 — 2 mutation REJECTs, 1 swap-host identity hold | Local spike @ `4ad4d05e`; recordings in `spikes/autonomy-first-flight/recordings/` |
| Standing loop, first sweep (S1) | `run-first-flight.ps1 -Sweep` — an objective FILE in the store drives the loop: witness + proposal loaded, attested session, in-session compile, witness judged | **REJECT at `correctness` case 0** (`expected "" got <null>`) — hold mode, nothing swapped | Local spike @ `061c4f83`; example in `samples/autonomy-objectives/` |
| Repair loop to ADMIT (S2) | S1 rejection fed back to the model as repair input; loop re-run under hold + full containment | model fixed its one defective line; **ADMIT, `escape_rate=0` → `CertifiedButHeld`** after two trust-machinery holes were closed (analyzer-dead mutants as kills; `$summary` witnessable) | Local spike @ `7cdf9e88`; sample in `samples/autonomy-objectives/` |
| Repair channel as policy (S3) | `RepairFeedbackPolicy` + ablation on codellama:7b, then the shipped loop path (5 objectives × 2-attempt budget, two temperatures) | **redaction costs nothing (3/3 vs 3/3 in ablation); through the shipped path 3/5 objectives converge within the budget at temp 0.2 and 0.7 alike**; the necessary ingredient was contract precision ("NEVER null"), and single-shot rate on a 7B model swings with formatting noise — the bounded retry is what makes it usable | Local ablation @ this PR |
| Dogfood campaign 1 (S4) | five human-authored objectives, live codellama:7b in the loop, hold mode, four campaigns | **compiled 0/5 → 1/5 → 3/5 → 2/5 as the loop was fixed; first full-chain success `door-lock-transition` CertifiedButHeld (escape_rate=0) on the FIRST proposal; text-slug held on a witness the proposer never saw**; a 7B model re-emits on repair — the loop is model-agnostic and the next lever is the model | `.ashlar/campaign/*` recordings; `samples/autonomy-objectives/door-lock-transition.proposal.json` |
| Dogfood campaign 2 (S5) | the same five objectives, witnesses, preamble and hold mode — only `ASHLAR_OLLAMA_MODEL` varies: `codellama:7b` vs `qwen2.5-coder:7b` vs `qwen3.8:27b` | **certified-held 1/5 → 2/5 → 3/5 on model swap alone; at 27B compiled 5/5 and the failures move down the pipeline** — and the survivor is a finding against the GATE: `semver-parse` passes every correctness case and is rejected at `mutation` on an **equivalent mutant** (`escape_rate=0.04`, a redundant length guard `0 => 2` that no witness case can kill), so a correct candidate can never certify. Repair re-emission is clean evidence at 7B (byte-identical on 6/6) but untested at 27B (2 of its 4 repairs had nothing to fix); every certified candidate certified on attempt 1 | `.ashlar/campaign/*-qwen*` recordings; `samples/autonomy-objectives/rgb-hex-parse.proposal.json` |

**Dogfood summary:** `honest=ADMIT`, `buggy=REJECT`, `tests_executed=19` — CI-confirmed on PR #191.

### Integration merge verification (2026-06-21)

Branch `cursor/integration-cert-tower-921c` — fast-forward merge of full tower + merge-readiness onto `origin/master` (`5bd1a103`).

Local cert-gate on integration tip (`9baf34a9`):

```
Test Run Successful. Total tests: 19, Passed: 19
cert-gate executed 19 tests (expected>=19, derived from --list-tests).
```

Dogfood on integration: `HonestCursorGeneration_Admits_WithZeroEscapeRate` PASS, `BuggyCursorGeneration_Rejects` PASS.

---

## Atom portability (spike)

Probe brick: `ErrorSummaryExtractor` (deterministic log scanner).

| Step | Description | Result |
|------|-------------|--------|
| 1 | Generate deterministic probe brick via `INewBrickGenerator` | PASS |
| 2 | Certify through S0–S2 gate (signed admission record) | PASS |
| 3 | Pack Ashlar.Brick.Contracts + Ashlar.Authoring (+ Hosting.Bundle) @ 0.1.0 | PASS |
| 4 | Consume generated brick from external template (package pins only) | PASS |
| 5 | Cross-project HTTP execute assertion | PASS |

**Step 2 detail:** Signed admission record at `spikes/portability/generated/certification-record.json` (`escape_rate=0`).

## Atom gate teeth

Strong witness on the probe brick: ADMIT with `escape_rate=0`, mutants killed by AST operators (`flip-binary-op`, `negate-condition`, `mutate-int-literal`, `remove-statement`, etc.).

Weak witness (unit test `MutationProbeBrick`, errorCount-only): REJECT with `escape_rate > 0`; survivors include AST ids such as `flip-binary-op-*` and `mutate-int-literal-*`.

## General generation

Intent: **line-substring-counter** — extract count of lines containing a given substring (plus `firstMatchingLine` output).

Independent strong witness (human-provided, not authored by generator):

| Input | Expected output |
|-------|-----------------|
| `text="FOO line one\nplain\nFOO line two\n"`, `substring="FOO"` | `matchCount=2`, `firstMatchingLine="FOO line one"` |

Model seam: `IGeneratorModel` with hermetic `FixtureGeneratorModel` (**test double**) in tests; production `ProviderGeneratorModel` (`model:ollama:isolation-enforced`) behind the sealed seam.

### Generate→certify→admit results (fixture test double)

| Case | Variant | Witness | Result | Gate detail |
|------|---------|---------|--------|-------------|
| 4a | `fixture:correct` | Strong | **ADMIT** | `escape_rate=0`, signed admission record |
| 4b | `fixture:buggy` | Strong | **REJECT** | `correctness` — `firstMatchingLine` reports last match (`FOO line two`) instead of first |
| 4c | `fixture:correct` | Weak (`matchCount` only) | **REJECT** | `mutation` — `escape_rate > 0` (AST mutants survive weak witness) |
| 4d | `fixture:dependency-leak` | Strong | **REJECT** | `dependency` — source contains forbidden token `Ashlar.Infrastructure` |

Generated manifest carries `GenerationProvenance` (e.g. `fixture:correct`) marking model/fixture origin on the artifact.

## Composition certification

Composition: **error-summary-pipeline** — certified `mutation-probe-brick` → `error-summary-formatter` with independent composition-level witness (not derived from constituent witnesses).

| Case | Setup | Witness | Result | Gate detail |
|------|-------|---------|--------|-------------|
| 4a | Correct wiring | Strong (`errorCount` + `summary`) | **ADMIT** | `composition_escape_rate=0`, signed composition record |
| 4b | Broken seam (`firstErrorMessage` → `errorCount`) | Strong | **REJECT** | `seam` — producer type `string` does not satisfy consumer `int` |
| 4c | Correct wiring | Weak (`errorCount` only) | **REJECT** | `mutation` — `composition_escape_rate > 0`; survivors include `drop-node-format`, `reorder-adjacent-0` |
| 4d | Uncertified `uncertified-brick` constituent | Strong | **REJECT** | `constituents` — no valid atom certification record |
| 4e | Volatile `nonce` output (unwitnessed) | Strong | **REJECT** | `determinism` — composition outputs differ under AuditMode |

Graph mutations only (reorder/drop/redirect/swap brick assignments); constituent brick source is sealed.

## Dogfood run (damage-resolver)

Intent: **damage-resolver** — Cursor-authored `CursorGeneratorModel` (**test double**, `cursor:honest` / `cursor:buggy`); human-authored witness in `DamageResolverDogfoodWitness.Spec` (generation blind).

### CI verification (authoritative)

| Fact | Value |
|------|-------|
| Workflow | [Cert gate run 27918244198](https://github.com/IanFrelinger/Nexo/actions/runs/27918244198) — `conclusion: success` |
| Commit | `802e6d180bcae8cb7538d0497a644f67a5153893` |
| Tests executed | **19** (TRX `Counters total="19" executed="19" passed="19"`) |
| `HonestCursorGeneration_Admits_WithZeroEscapeRate` | **PASS** (CI TRX `outcome="Passed"`, 941 ms on `runnervm7b5n9`) |
| `BuggyCursorGeneration_Rejects` | **PASS** (CI TRX `outcome="Passed"`, 63 ms) |
| Honest brick `escape_rate` | **0** |
| Honest brick `signed` | **true** |
| TRX artifact | `cert-gate-trx` uploaded by workflow (not committed to repo) |

## Composition dogfood run

honest=ADMIT, broken=REJECT, tests_reported=21

| Test | Result |
|------|--------|
| `HonestComposition_StrongWitness_Admits_WithZeroEscapeRate` | **PASS** |
| `BrokenComposition_StrongWitness_Rejects` | **PASS** |
| Honest composition `composition_escape_rate` | **0** |
| Honest composition `signed` | **true** |

Composition: **damage-resolver → health-applier** (`damage-to-health-pipeline`); witness in `CompositionDogfoodWitness.Spec` (6 end-to-end cases); broken wiring redirects `currentHealth` into `health.finalDamage` (rejects on `correctness`).

cert-gate CI: **success**, run [27922375242](https://github.com/IanFrelinger/Nexo/actions/runs/27922375242) — TRX `total="21"`, guard `cert-gate reported 21 tests (expected>=21)`.

## Phase 2: cross-project reuse

trusted-reuse=PASS, tamper-reject=PASS, forged-sig-reject=PASS, tests_reported=24

| Proof | Result |
|-------|--------|
| `HonestCertifiedBrick_ProjectB_TrustsAndRunsUntouched` | **PASS** — verifier TRUSTED, brick runs untouched (`finalDamage=40`) |
| `TamperedBrick_ProjectB_RejectsContentHashMismatch` | **PASS** — `content-hash-mismatch`, refused |
| `ForgedSignature_ProjectB_Rejects` | **PASS** — `signature-invalid`, refused |

Project B (`samples/certified-brick-reuse/ProjectB`) references only `Ashlar.Certification.Contracts`, `Ashlar.Brick.Contracts`, `Ashlar.Authoring`, and the packed `Ashlar.Certified.DamageResolver` artifact — **no generator, no gate**. B verifies signature + content hash; it does not re-certify or regenerate.

Content binding: `CertificationRecord.ContentHash` = SHA-256 (UTF-8) of canonical brick source, included in signed HMAC payload (`Ashlar.Certification.Contracts`).

Pack/export: `scripts/pack-certified-brick-reuse.sh` → local feed + `certification-record.json` sidecar.

**v0 trust model:** same-owner cross-project reuse via shared dev HMAC key (`ASHLAR_CERT_DEV_HMAC_KEY`). Cross-organization trust requires PKI (out of scope).

## Agent-composer (proposer seam → real model → acceptance rate)

Cumulative evidence from P3-S1 (controlled proposer), P3-S2 (real-model record/replay), and P3-S3 (acceptance-rate measurement). cert-gate: **41 tests** @ [run 28067778575](https://github.com/IanFrelinger/Nexo/actions/runs/28067778575) (`conclusion: success` @ `7f9cbdc3`).

### Proposer seam (P3-S1)

| Proof | Result |
|-------|--------|
| `BadProposalVariants_AreRejectedByExistingGate` (5 variants) | **REJECT** — `correctness` \| `mutation` \| `seam` \| `constituents` via unchanged `CompositionCertificationGate` |
| `TamperedConstituentCert_RejectedByConstituentIntegrity` | **REJECT** — `constituents` |
| `CorrectProposal_StrongWitness_Admits_WithZeroEscapeRate` | **ADMIT** — `composition_escape_rate=0`, signed |
| `CorrectProposal_MatchesHandAuthoredCompositionResult` | **ADMIT** — matches hand-authored `CompositionDogfoodFixtures.HonestSpec()` |
| `ProposerInput_StructurallyCannotCarryWitnessCases` | **PASS** — no witness-bearing fields on `CompositionProposerInput` |

**What this proves:** Untrusted proposer (`ICompositionProposer`) receives target I/O signature + certified-brick catalog only. Wiring traverses the **identical** composition admission gate — no agent bypass. Human witness in `CompositionDogfoodWitness.Spec`; `ControlledCompositionProposer` is a deterministic CI double.

**v0 boundary:** Controlled proposer only for rejection teeth; real model is next layer below.

CI: [run 28000451847](https://github.com/IanFrelinger/Nexo/actions/runs/28000451847) — 33 tests, 9 `CompositionProposer*` passed.

### Real-model proposer dogfood (P3-S2)

| Proof | Result |
|-------|--------|
| `RecordedRealProposal_TraversesIdenticalLoop_ReportsHonestGateVerdict` | **ADMIT** — `model:cursor:isolation-enforced`; `firstTryCertified=true` |
| `RecordedRealProposal_WhenAdmitted_MatchesHandAuthoredCompositionResult` | **ADMIT** — same result as hand-authored spec |
| `CompositionGeneratorModel_InputPathCannotCarryWitnessCases` | **PASS** |
| `RealProposerPrompt_IsBuiltFromProposerInputOnly_WithNoWitnessValues` | **PASS** — no serialized witness cases in prompt |
| S1 controlled rejection suite (8 tests) | **UNCHANGED** |

**What this proves:** `ProviderCompositionGeneratorModel` over `ICompositionGeneratorModel` builds prompts from `CompositionProposerInput` only. Cert-gate replays a **recorded** proposal — no live API. Honest first-try ADMIT on damage→health.

**v0 boundary:** Single recorded proposal (record/replay); live capture via `CompositionProposalRecorder` locally. S1 controlled rejection remains authoritative teeth.

CI: [run 28028224579](https://github.com/IanFrelinger/Nexo/actions/runs/28028224579) — 37 tests.

### Acceptance-rate measurement (P3-S3)

| Observation | Value |
|-------------|-------|
| **Measured acceptance rate** | **0.60** (3 admits / 5 proposals) — reported, not targeted |
| Protocol | N=5, temperature=0.7, provider=cursor, discards=none |
| Distinctness observed | 3 unique wiring specs (correct×3, reordered×1, dropped×1) |

| Proof | Result |
|-------|--------|
| `RecordedBatch_EachEntryReproducesRecordedVerdictOnReplay` | **PASS** — anti-forgery |
| `RecordedBatch_ComputedRateMatchesAdmitsOverTotal` | **PASS** — arithmetic integrity only |
| `ShortBatchGuard_RejectsTruncatedBatch` | **PASS** |
| `RecordedBatch_HoldsExactlyDeclaredIndependentSamples` | **PASS** — exactly N=5, no padding |
| S1 + S2 tests | **BYTE-UNCHANGED** |

**What this proves:** Raw proposer acceptance is **measured** (admits/total via unchanged `ProposeAndCertifyCompositionService`), not gated. Full batch recorded at temperature > 0 including rejects; CI replays deterministically.

**v0 boundary:** Single task (damage→health), one provider (cursor), record/replay batch.

## Physical-atom and spatial evidence (removed with the vertical, 2026-08-31)

The physical-atom certification (Phases 0-3) and spatial pose/provider (P1-P2) evidence
sections documented `applications/` products removed in the native-responsibility slim.
Their code, tests, and this ledger's full sections are preserved verbatim on the archive
branch `archive/verticals-2026-08-31` and in git history; the evidence travels with the
vertical to its own repository.

## Autonomy first flight (P2)

One real autonomous iteration, end to end, against a **live container engine** — the acceptance
step previously recorded as outstanding under known limitation 5. Flown 2026-08-14 from commit
`1afac86d` via `spikes/autonomy-first-flight/run-first-flight.ps1` (devcontainer image +
`/var/run/docker.sock` pass-through; only committed state flies).

The spike composes `AddCertificationGate` + `AddAshlarAutonomy` exactly as a host would
(`ValidateOnBuild`/`ValidateScopes`), hand-authors a Triage objective, and runs one
`AutonomousIterationHarness.RunIterationAsync` with a candidate whose source, witness, and clean
project file mirror the proven gate-teeth shape.

| Step | Result |
|------|--------|
| Objective intake (`Triage` source, touch-set: `spikes/autonomy-first-flight/generated/`) | Tier = `Tier0Autonomous` |
| Sandbox session against live daemon (`alpine:3.20`, no mounts, `NetworkAccess.None`) | Provisioned + attested: digest `sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc`, engine `29.7.2`, effective caps `mem=268435456` / `pids=64` / `nanoCpus=1000000000` (matched request — not weaker) |
| Certification chain (analyzer fence + touch-set, witness, mutation, determinism, dependency) | **ADMIT** — `signed=true`, `escape_rate=0` |
| Certificate inputs | `witness`, `image-digest`, `sandbox-spec`, `attestation`, `generation-depth` (depth 1) all recorded |
| Autonomous Tier-0 swap (`AutonomousAdmission` with lineage key) | **`AdmittedAndSwapped`** as generation 1, 4.4 s end to end |
| Post-swap serving | 3/3 invocations correct (`errorCount=1`, first message extracted) — watch window cleared |
| Session teardown | `docker ps -a` on the host daemon shows **zero** `ashlar-session-*` containers after the run |
| Digest | `AutonomyDigest` renders the swap-committed event; nothing held |

A `--dry` leg (TestKit fake runner, same wiring) also passed, with an explicit zero-leaked-sessions
assertion. The dry and real runs produced identical outcomes and identical certificate-input kinds;
only the attestation values differ (fake vs. live daemon) — which is exactly the seam's contract.

**Boundary:** the P2 flight's session was provisioning attestation only; the **P3 leg below**
closed the compilation half of that gap. The **real-proposer** leg of old limitation 5 remains
open: every flight candidate was hand-authored to the gate-teeth shape, not produced by a model.

### P3 acceptance: the candidate compiled inside the session

Re-flown 2026-08-14 from commit `d71d045f` with `-SessionBuild`: the loop's iteration required the
candidate to compile **inside** the attested session (`Ashlar:Autonomy:BuildCandidateInSession=true`),
against the pinned SDK image on the live daemon.

| Step | Result |
|------|--------|
| Session image | `mcr.microsoft.com/dotnet/sdk:9.0` @ `sha256:35048e3a81e6a07c316e7bbbd80d80d2ba705fe5f23a8ed42b6638c8f4c20d30`, engine `29.7.2`, same effective-caps verification as P2 |
| In-session compile (`SessionCandidateBuild` over `ExecAsync`: base64-chunk uploads, zero-`PackageReference` project, cleared NuGet sources, `NetworkAccess.None`) | **PASS** — the exact `CandidateSourceWrapper.Wrap` bytes every certification-path compile sees |
| Certificate inputs | Six kinds: `witness`, `image-digest`, `sandbox-spec`, `attestation`, **`session-build`**, `generation-depth` |
| Outcome | **`AdmittedAndSwapped`** as generation 1, 7.0 s end to end; 3/3 post-swap invocations; digest rendered; zero `ashlar-session-*` containers left on the daemon |

The dry `-Dry -SessionBuild` leg passed identically (fake sessions, same input kinds). In-container
test suite: 20/20 (`AutonomousIterationHarnessTests` including the in-session-build facts,
`AutonomyCompositionTests` including the `BuildCandidateInSession` misconfiguration row).

### P5: execution containment, and the recorded model proposal

Two closures flown 2026-08-14. With `ExecuteCandidateInSession=true`, the gate's witness,
determinism, and mutation legs EXECUTE candidate and mutant code inside the attested session
(`SessionExecutionBackend`: a dumb runner built in-session from embedded source, per-unit load
contexts, raw observations only — the gate judges). With both legs on, untrusted candidate code
never runs in the harness process; what remains on the host is orchestration, Roslyn compilation
of mutants, and judgment.

**Full-containment flight (hand-authored candidate):** `AdmittedAndSwapped`, seven certificate
inputs including `session-execution`, `escape_rate=0` with every mutant executed in-session —
12.3 s at the two-case witness (`3b199a67`), re-flown PASS at the hardened four-case witness
(`bf8821db`).

**The recorded model proposal** — the first candidate in this repository authored by a model
(Claude Fable 5, recorded 2026-08-14; proposer signature `model:claude-fable-5:recorded:2026-08-14`
hash-bound into the `generation-depth` input; authored from the objective and interface contract
only, never shown the witness; deliberately a different implementation shape). **The campaign it
took to admit it is the strongest teeth evidence this ledger has:**

| Flight | Commit | Outcome |
|--------|--------|---------|
| 1 | `3b199a67` | **`BudgetExhausted`** at the 600 s ceiling (R4.6 working as specced): a `mutate-int-literal` mutant turned the proposal's position-advancing loop nonterminating and hung the in-session runner |
| 2 | `80a9edc4` | **`BudgetExhausted`** again: the first fix raced the brick's *returned* task, but a synchronous brick executes inside `MethodInfo.Invoke` — the race never started. Fixed by racing the whole invoke inside `Task.Run` |
| 3 | `56ad61ae` | **REJECT `mutation`**, `escape_rate=0.16`, 3 survivors — boundary-index equivalents the two-case witness could not distinguish (no case pinned an ERROR marker at the start of the text or of a line). 23.6 s |
| 4 | `9e6776c3` | **REJECT `mutation`**, `escape_rate=0.05`, 1 survivor — the leading-newline sentinel. 23.2 s |
| 5 | `bf8821db` | **PASS** — `AdmittedAndSwapped` as generation 1, `escape_rate=0` against the hardened witness, 23.5 s, 3/3 post-swap invocations, zero sessions leaked |

Flights 3–4 are the mutation gate REJECTING a model-produced candidate on a live engine with full
session containment — and the survivors named REAL witness gaps, fixed by contract-derived
hardening cases (edge-positioned markers; leading newline), not by fitting the witness to the
implementation. Flight 1 additionally proved the budget ceiling catches nonterminating mutants,
and produced the runner's per-execution timeout: a timed-out execution is now an observation
(judged as a killed mutant or rejected candidate), and the spinning thread dies with the runner
process — an isolation move the in-process path could never afford.

In-container suites after all of it: 66/66 (gate execution-seam facts, backend orchestration
facts, teeth, campaigns, harness, composition, watch window, digest).

### P6: LIVE model proposals at flight time

The last open leg, flown 2026-08-14: `run-first-flight.ps1 -Live` calls the LOCAL provider —
**ollama `codellama:7b`**, the seam the general-generation arc named as production
(`model:ollama:isolation-enforced`) — at flight time, with a committed witness-blind prompt
(objective + interface contract only), records the raw exchange to
`spikes/autonomy-first-flight/recordings/` (committed evidence artifacts), and mounts the
recording into the flight as a read-only input. The spike extracts the source mechanically and
does no validation beyond locating a class name: **the gate is the judge**, and every `-Live`
run is a fresh acceptance sample.

| Sample | Generation | Verdict |
|--------|-----------|---------|
| 1 (`live-…121237`) | 57 s | **REJECT `mutation`** — `escape_rate=0.08`; the `IndexOf + 5` offset mutant, trim-shadowed by every existing witness message (real witness gap #3) |
| 2 (`live-…121507`) | 8 s | **REJECT `mutation`** — same survivor against the five-case witness (hardening #3, the colon-adjacent case, was necessary but not sufficient) |
| 3 (`live-…121708`) | 7 s | **CERTIFIED — signed, `escape_rate=0` — then HELD by the swap host**: the model omitted the constructor, so the compiled brick self-declared an empty Id and verify-at-load refused the identity mismatch (`brick-id-mismatch`). Defense-in-depth demonstrated on a live candidate: the gate certifies, the host independently re-verifies |
| 4 (`live-…121844`) | 10 s | **PASS — `AdmittedAndSwapped`** as generation 1 in 13.3 s: signed, `escape_rate=0` against the six-case witness, full session containment, 3/3 post-swap invocations, zero sessions leaked |

**Measured live acceptance: 1/4 (0.25) — reported, not targeted** (the P3-S3 discipline). The
campaign forced two further contract-derived witness hardenings (the colon-adjacent marker; the
marker-terminal line, which kills the trim-shadowed offset class outright), and one prompt
reshape (skeleton-completion form — the constructor arrives verbatim, the model contributes the
behavior, which is exactly the A2.3 manifest-scaffold shape).

**The proposer-diversity observation, worth keeping:** every distinct implementation shape that
traversed this loop — hand-authored, recorded model, live model — surfaced a witness gap the
previous shapes could not express. Six witness cases now exist; four were demanded by the gate
rejecting real candidates. Proposer diversity is adversarial witness-hardening.

## S1: the first standing-loop sweep (objective file drives the loop)

Flown 2026-08-15 from `061c4f83` via `run-first-flight.ps1 -Sweep`. Everything before this
hand-constructed its candidate inside a spike; this is the first run where an objective FILE
in the store drove the loop end to end.

| Stage | Result |
|-------|--------|
| Objective read from `IObjectiveStore` | `tag-scan-classifier` (source=Human, priority=10) |
| Witness loaded (human-authored, sibling file) | 5 cases |
| Proposal loaded (live ollama `codellama:7b`, recorded) | witness-blind by construction |
| Session started + attested on the live daemon | `mcr.microsoft.com/dotnet/sdk:9.0` @ `sha256:35048e3a...` |
| Candidate compiled INSIDE the session | PASS, toolchain `9.0.317` |
| Witness judged | **REJECT at `correctness`, case 0** |
| Outcome | `ExplainedFailure`, session torn down, zero leaked containers, nothing swapped |

**What the rejection caught.** The model wrote the codec's `failureCode` straight to its
output; that value is `null` on the success path (`PhysicalAtomTagBinaryCodec.cs:113`) while
the contract and the witness's first case require the empty string. A witness authored from
the contract BEFORE the proposal existed, and never shown to the proposer, caught a real
defect in model-generated code under full session containment.

**Two defects the run exposed in our own work, both fixed:**

1. The first sweep failed earlier, at the in-session build, with `CS1056 Unexpected
   character '003c'`. The proposal-recording tool unescaped `
`, `\"` and `\` but not
   `XXXX`, so the stored source carried literal escape text. The MODEL's output was fine;
   the transcription was not. The loop caught it at the earliest possible gate with an exact
   diagnostic.
2. The rejection message read `expected  got ` - both sides empty, because
   `Convert.ToString` returns `""` for null and a `JsonElement` of kind Null stringifies to
   empty. The verdict was right and the feedback was useless. Since this text is the repair
   channel a proposer reads, it now renders `expected "" got <null>`.

### S2: repair loop to ADMIT — and two holes in the trust machinery it exposed

The rejection message from S1 was fed back to the model as repair input, and the loop
re-run after each fix. Full campaign, all under hold mode with full session containment:

| Run | Verdict | What it taught |
|-----|---------|----------------|
| S1 | REJECT `correctness` case 0 (`expected "" got <null>`) | the witness catches the model's null-vs-empty defect |
| repair | model returns `failureCode ?? string.Empty` — the one line at fault, nothing else | **the repair loop works on the correctness leg with a live model** |
| S2a | correctness PASSES; REJECT `mutation`, escape 0.20, survivor `mutate-string-literal-33` | a survivor no witness could kill — see below |
| fence triage | analyzer-dead mutants now count as kills (`BrickMutationEngine` runs the fence on SURVIVORS only) | mutants that could never certify were inflating escape rates |
| S2b | still REJECT `mutation`, same survivor | that mutant was not analyzer-dead — deeper |
| `$summary` | `WitnessObservableOutput` makes the summary witnessable under a reserved key at all three judge sites | the survivor was the SUMMARY literal, invisible to any witness |
| S2c | **ADMIT, `escape_rate=0` → `CertifiedButHeld`** | the model-repaired candidate is fully certified; hold refuses the swap |

**Two genuine holes in the trust machinery, found by getting to the bottom of one survivor:**

1. **Analyzer-dead mutants counted as escapes.** Mutants were judged only by the witness; the
   analyzer fence never ran on them. So a mutant that rewrites a declared key
   (`Set("firstMatchingLine")` → `"firstMatchingLinX"`, ASHLAR0002) — which no proposer could ever
   ship — was an "escape" no behavioural witness can observe. Fixed with precedent: a
   non-compiling mutant was already "dead on arrival"; a fence-rejected one is the same case at an
   earlier gate. Triage runs on survivors only and fails toward reporting the survivor.
   **This exposed that `GoodGeneration_WeakWitness_Rejects_WithTeeth` had fake teeth** — all seven of
   its surviving mutants were analyzer-dead; not one touched the logic the weak witness actually
   fails to observe (`firstMatchingLine ??= line`). The catalog had no operator for `??=`. Added
   `degrade-coalesce-assign` (`a ??= b` → `a = b`, "keep first" → "keep last", the buggy fixture's
   exact defect class); the weak witness now rejects for the RIGHT reason.
2. **The summary was unwitnessable.** `BrickOutput.ToDictionary()` excludes `Summary`, and every
   judge compared against the dictionary alone — so a mutated summary literal was unkillable by any
   witness expressible in the language. Reserved key `$summary`, projected identically at all three
   judge sites; the contract-conformance leg reads `ToDictionary()` directly so it can never register
   as an undeclared write.

Also fixed en route: the proposal recorder did not unescape `XXXX` (CS1056 at the in-session build —
the model's output was fine, the transcription was not), and witness failure messages rendered null
and empty identically (`expected  got `), which is useless as repair feedback.

**The design tension made concrete:** useful repair feedback necessarily leaks witness values to
the proposer (`expected ""` IS a witness value). Repair trades generation-blindness for
convergence one message at a time. That was the open question at the end of S2. S3 answers it.

### S3: the repair channel made policy — and measured

The tension is now resolved by construction: `RepairFeedbackPolicy` projects every rejection into
a proposer view at a chosen disclosure level — `CheckOnly` / `OwnOutput` (default) / `Full` — from
STRUCTURED witness findings, never by re-parsing prose. The default shows the proposer the failing
check, case index, key, and its OWN observed value; expected values appear only under `Full`, which
is opt-in and documented as weakening the certificate. Repairs are bounded per objective (default
2), then held for a human. Everything is model-independent configuration; `OllamaProposalSource`
adds prompt style, temperature, tokens and preamble as dials so the same loop serves a 7B local
model or a large hosted one.

**Then it was measured — twice, because the first measurement was too clean to trust.**

Hand-built ablation on `codellama:7b`, 3 samples/arm at temperature 0.2, repairing the S1
null-vs-empty defect:

| Prompt | Expected value shown | "NEVER null" in contract | Repaired |
|--------|----------------------|--------------------------|----------|
| default `OwnOutput` | no | yes | 3/3 |
| `Full` | yes | yes | 3/3 |
| default, contract softened | no | no | 0/3 |
| (13 earlier attempts, softened contract) | mixed | no | 0/13 |

**Redacting the expected value costs nothing**; the necessary ingredient was two words in the
CONTRACT ("NEVER null"). But the SHIPPED prompt — same disclosure, same contract — went 0/3
single-shot, and ~60 further model calls could not isolate a single semantic cause: the swing
between 0/3 and 3/3 tracked formatting noise (a blank line, "; the" vs ", and the",
"valid tag" vs "tag reference"). That is a property of a 7B model at low temperature, and the
loop must be robust to it rather than tuned to it.

So the number that matters is the one users get. Through the shipped code path — default
`OwnOutput` policy, `RepairWithContractOnly`, the loop's 2-attempt budget — **3/5 independent
objectives converge, at temperature 0.2 and at 0.7 alike.** Not the 3/3 of a lucky prompt, not
the 0/3 of an unlucky one. The retry budget is what turns "sometimes" into "usually", and it is
bounded, so a proposer still cannot binary-search the witness.

Three operating lessons, all now encoded as configuration rather than lore: (1) generation-
blindness is free — keep the default; (2) when a small model will not repair, sharpen the
CONTRACT before loosening the disclosure; (3) hand small models the contract alone, not the
objective narrative (`RepairWithContractOnly`, measured 3/3 → 0/3 the other way).

Worked example (objective, witness with `$summary` pinned, and the model-REPAIRED proposal):
`samples/autonomy-objectives/`.

### S4: dogfood campaign 1 — the loop meets five objectives it has never seen

Everything before this section was mechanism with a green gate behind it. What there was no
evidence for was the standing loop meeting more than one real objective. So: five human-authored
objectives with human-authored witnesses (`samples/autonomy-objectives/`) — a classifier, a state
machine, two parsers, and `text-slug`, under-specified ON PURPOSE (its contract never addresses
diacritics; the witness pins them) so the campaign could watch the repair channel hold rather
than converge on a witness the proposer cannot see. A LIVE `codellama:7b` composed INSIDE the
loop (so the loop's own policy-projected, bounded repair is what runs), hold admission on. Every
proposal and the exact projected feedback the model was handed are recorded per attempt
(`run-first-flight.ps1 -SweepLive`).

Four campaigns in an afternoon, each ~2–5 minutes for all five objectives, each fixing what the
previous one exposed:

| Campaign | What changed | Compiled | Judged by the witness | Certified (held) |
|---|---|---|---|---|
| 1 | baseline | 0/5 | 0 | 0 |
| 2 | build failures enter the repair channel | 1/5 (a `// TODO` stub) | 1 | 0 |
| 3 | build repairs carry the whole objective; brick-API operator preamble | 3/5 | 3 | 0 |
| 4 | authoring fixes: `event`→`trigger`, Summary stated, `$summary` worded | 2/5 | 2 | **1** |

**Mechanism findings (fixed as they surfaced):**

1. **Compile failures were terminal.** A session-build failure carried no `CertificationDecision`,
   and the loop repaired only when there was one — so a small model's dominant failure mode (a
   one-line C# slip: `output.Minor = …` for `output.Set("minor", …)`, a `Set(` without its
   receiver, a missing `using System.Linq`) ended the objective after one attempt. Compiler
   diagnostics are the safest feedback there is — they describe the candidate's own text, never
   the witness — and now they ride on `IterationResult.BuildDiagnostics` in the candidate's own
   line coordinates, projected by `RepairFeedback.RenderBuildFailure` under the same policy and
   attempt cap.
2. **Repair prompts stripped what a compile fix needs.** Contract-only repair (measured right for
   the null-vs-empty case) dropped the skeleton and API notes; a build repair now carries the whole
   objective (`RepairContext.Kind`).
3. **A 7B model does not know the brick API** and cannot derive it from diagnostics. The existing
   operator preamble knob now carries it as house rules (`proposer-preamble.md`) — data the
   proposer is handed, never a witness, the same knob a deployment would set. Compiled went 1/5 → 3/5.
4. **The reserved `$summary` key was opaque to a proposer** ("output['$summary'] was not
   produced", three attempts, no repair). It is now worded as "the output Summary (output.Summary)".

**Authoring findings — the campaign is as much about objectives as about the loop:** an input
named `event` is a C# keyword (the model's natural `var event = …` can never compile, and
"; expected" cannot tell a small model why); a witness that pins `$summary` needs a contract that
states the Summary. Both were ours. The contract-precision lesson from S3, again.

**What the model did, honestly.** `codellama:7b` re-emits its previous source almost verbatim on
repair — every objective, both repair kinds, all four campaigns; identical diagnostics at identical
lines. Feedback quality is not the constraint: the projections were precise and redaction held
throughout (`text-slug` saw its own `"café-olé"` and never `"cafe-ole"`, and the loop held). At 7B
the loop reaches the witness for 2–3 of 5 shapes and repair convergence is ~0 for anything beyond
a one-token fix; results also swing between runs (`semver-parse` compiled in campaign 3, not in 4).
The loop is model-agnostic by construction (`ASHLAR_OLLAMA_MODEL`); the next lever is the model.

**And the first end-to-end success:** in campaign 4 `door-lock-transition`'s FIRST proposal —
9.4 s from a 7B model, on an objective it had never seen — built in the attested session, passed
the analyzer fence, all eight witness cases, mutation (`escape_rate=0`) and determinism, and the
loop held it: `CertifiedButHeld — certified; the operator holds admission … with full evidence on
the record`. Recorded beside the objective as `door-lock-transition.proposal.json`. Nothing was
admitted, in four campaigns and 60 attempts, and every rejection says why.

### S5: dogfood campaign 2 — the same five objectives, three proposer models

S4 ended with a claim rather than a measurement: the loop is model-agnostic by construction, so
the next lever is the model and not the prompt. S5 tests exactly that. Everything else is pinned —
the same five objectives and witnesses, the same operator preamble, hold mode, the same 2-attempt
repair budget, the same attested session — and the only variable is `ASHLAR_OLLAMA_MODEL`. Two more
proposers, one campaign each, back to back on one box (`run-first-flight.ps1 -SweepLive -Models`).

| Proposer | Compiled | Judged by the witness | Certified (held) | Where the survivors failed | Repair behaviour | s/proposal |
|---|---|---|---|---|---|---|
| `codellama:7b` (S4, campaign 4) | 2/5 | 2 | 1 | compile | re-emits almost verbatim | 9.3 |
| `qwen2.5-coder:7b` | 3/5 | 3 | **2** | 2 compile, 1 correctness | byte-identical on 6/6 repairs | 9.4 |
| `qwen3.8:27b` | **5/5** | **5** | **3** | 1 mutation, 1 correctness | byte-identical on 3/4; one real edit | 117.7 |

**The lever was real, and it moves the thing S4 could not move.** Swapping the model — no prompt
change, no loop change — took certified-held from 1/5 to 2/5 to 3/5. `qwen3.8:27b` certified
`tag-scan-classifier`, `door-lock-transition` and `rgb-hex-parse`, each on its FIRST proposal, each
with `escape_rate=0`. `rgb-hex-parse` is the first parser to go the whole way and is recorded beside
its objective as `rgb-hex-parse.proposal.json`.

**The interesting result is not the count — it is that the failures moved down the pipeline.** At
7B the dominant failure mode is writing C# that compiles against an API the model was just handed:
`codellama` and `qwen2.5-coder` lose 2–3 objectives to `error CS1061` / `CS0103` before the witness
ever runs. At 27B that failure mode disappears outright — 5/5 compiled, and the build-repair path
introduced in S4 never fired once. What is left are two failures deep in the pipeline — and running
them down is what makes this campaign worth its cost, because only one of them is the proposer's
fault:

1. **`semver-parse` passed every correctness case and was rejected at `mutation`** —
   `escape_rate=0.04, survivors=[mutate-int-literal-41]`, three attempts running. Run down, this is
   **an equivalent mutant, and therefore a soundness hole in the gate — not a weak witness and not a
   bad candidate.** Survivor ids are `mutate-int-literal-{line}`, so this is line 41,
   `if (version.Length > 0)`, and the catalog rewrites `0 => 2` (`AstMutationCatalog`), giving
   `if (version.Length > 2)`. The two differ only for inputs of length 1 or 2 — and no such input can
   be a valid semver, because `isValid` is set only when `corePart.Split('.')` yields three parts
   that each pass `IsValidNumericPart`; the sole length-≤2 string with two dots is `".."`, whose parts
   are empty and rejected. So both versions return `isValid=false, 0, 0, 0, ""` on every input that
   distinguishes them: the guard is redundant, the mutant is semantically identical, and **no witness
   case can ever kill it.** `CertificationGate` rejects on `EscapeRate > 0`, so this candidate could
   not have certified no matter what was added to the witness or how many repair attempts it got.
   `BrickMutationEngine` already discounts three classes of unkillable mutant (source-identical,
   non-compiling, analyzer-dead); semantic equivalence is a fourth it does not model, and it is
   undecidable in general. This is the first time the campaign has caught the trust machinery being
   wrong rather than the proposer, and it is the substantive finding of S5.
2. **`text-slug` failed at `correctness` on the plain cases**, not on the diacritics case it was
   authored to hold on (`"hello-world"` → `"helo-wrd"`, `"rock-roll"` → `"rock-l"` — the candidate
   eats repeated letters). So this run does not re-demonstrate S4's "held on a witness the proposer
   never saw"; it failed earlier, for an ordinary reason.

**Repair at 7B is still the weak channel; at 27B this campaign cannot say.** `qwen2.5-coder:7b`
re-emitted **byte-identical** source on 6 of 6 repair attempts — same hash, same length, with
populated feedback on the record every time — and all three of its repaired objectives had genuine,
fixable defects (two compile errors and a correctness bug), so that is clean evidence and it carries
the S4 finding across a family change. The 27B number does **not** carry the same weight, and the
equivalent-mutant finding above is why: of its 4 repair attempts, 2 were `semver-parse`, where no
edit could have helped, so re-emitting identical source was the *correct* response rather than a
failure to read the feedback. That leaves `text-slug` as the only valid test at 27B, and there it
did change its source on one of two attempts (1631 → 1474 chars, reproducing the same failure).
**One objective is not a measurement** — whether repair works at 27B is untested, not answered.
What does hold at both sizes: every certified candidate certified on attempt 1.

**Redaction held on a second and third model.** `text-slug`'s projected feedback showed the model
only its own output (`"café-ol"`); the expected `"cafe-ole"` appears in no recorded feedback
anywhere in the campaign. The S3 policy is now measured across three proposers of two families.

**Cost, honestly.** `qwen3.8:27b` is 18 GB q4 on a 12 GB card, so it runs partly on CPU: 117.7 s per
proposal against 9.4 s, and 1174.9 s of wall clock for the sweep against 171.9 s — 12.5x per
proposal, 6.8x per campaign. It needs fewer proposals (9 vs 11) because more objectives land first
try, which earns a little of that back. It also cannot be run naively: Qwen3-family models spend the
entire token budget reasoning and return an empty candidate unless `think: false` is sent, which is
what `OllamaProposalOptions.Think` and `-ThinkOff` exist for.

**Nothing was admitted.** Three campaigns, twenty proposals, hold mode throughout; every rejection
names its stage and says why.

**Replication: both models re-run end to end, same dials, nothing else changed.** The point was to
find out whether 2/5 versus 3/5 is a capability difference or a lucky draw. Every objective
reproduced its exact verdict and its exact failure stage, for both models — `qwen2.5-coder:7b` 2/5
certified (semver at `correctness`, two compile failures), `qwen3.8:27b` 3/5 (semver at `mutation`,
`text-slug` at `correctness`); 164.3 s and 1145.6 s against 171.9 s and 1174.9 s. The gap is real.

The two models get there differently, and only one of those is good news:

- **`qwen2.5-coder:7b` is very nearly deterministic at temperature 0.2** — 4 of its 5 candidates
  are **byte-identical** across independent runs (only `text-slug` differed, and it failed to
  compile either way). So repeat runs at these dials do not measure sampling variance; they mostly
  re-measure one sample. Anything about run-to-run spread needs a higher temperature, not more
  repeats. It also means S4's "results swing between runs" belongs to `codellama:7b` or to the loop
  changes between those campaigns — it is not a property of the harness at 0.2.
- **`qwen3.8:27b` is not deterministic** — 3 of its 5 candidates differ across runs — and it
  reached identical verdicts anyway. That is the stronger result: the same five outcomes from
  genuinely different code.

**And the equivalent-mutant finding replicated on a different candidate.** The 27B's second
`semver-parse` attempt is structurally different from the first (`IndexOf` + `TryParseNumericPart`
rather than a hand-rolled scan), and its survivor is a different line —
`mutate-int-literal-47`, `if (prereleaseIndex >= 0)` rewritten to `>= 2`. It is equivalent for the
same class of reason: the two differ only when `'-'` sits at index 0 or 1, and in every such case
the mutant's `corePart` retains the `'-'` so `TryParseNumericPart` rejects it, while the original's
`corePart` is too short to split into three parts — both return `isValid=false` with defaults. Two
independent candidates, two different redundant guards, both unkillable, both rejected at
`escape_rate=0.04`. This is not a quirk of one proposal: **`EscapeRate > 0` as a hard reject
systematically refuses correct implementations that happen to carry a redundant guard**, and a
redundant guard is exactly what a careful proposer writes.

## Settled decisions

- **`NetworkAccess.HostServicesOnly` stays a fail-closed refusal — permanently.** Every shipped
  backend refuses the mode rather than approximating it, and this is now the settled posture,
  not a v1 gap. The one workload that seemed to need it — package restore during the in-session
  candidate build — was solved **without** network (P3: offline restore from the SDK's installed
  packs, cleared sources), and a model server, the other imagined consumer, belongs on the
  proposer side of the boundary, not inside a certification session. The enum member stays
  declared (and `AllowedEndpoints` stays attestation-relevant) for a backend that can genuinely
  realize per-session egress rules; none is planned.

## Contract-stability gaps

- Generated brick uses Ashlar.Core.Domain.* namespaces (shipped via Ashlar.Authoring/Ashlar.Brick.Contracts but not the pinned package IDs)

## Artifacts

- `spikes/portability/generated/ErrorSummaryExtractorBrick/ErrorSummaryExtractorBrick.cs`
- `spikes/portability/generated/manifest.json`
- `spikes/portability/generated/certification-record.json`

## Known v0 limitations

1. **Dev HMAC signer, not PKI.** `CertificationRecordSigner` uses a development HMAC key, not a public-key infrastructure. This becomes more load-bearing in the composition phase because trust chains from constituent atom signatures — a forged or weak constituent record undermines the whole composition admission path. Unless `ASHLAR_CERT_DEV_HMAC_KEY` is set, the key is the COMMITTED constant `CertificationRecordSigning.DefaultDevKey`, so every record verifiable here is forgeable by anyone with the source; both signers now warn at construction while that is the case (`UsesDevKey`), and `ASHLAR_CERT_ED25519_KEY` adds a real signature on top. **Ed25519 is now required on Default/Strict verification paths** (limitations 7–8 CLOSED as of 2026-09-06), but it is still not a complete operator trust root on its own: without `TrustedEd25519PublicKeys` pinning, a self-consistent attacker-signed record can verify; HMAC remains forgeable under the committed dev key (this row); and the composition signer still discards an explicitly supplied key (limitation 9).

2. **Composition seam check is TYPE-level only.** The seam validator checks producer/consumer type compatibility (e.g. `string` vs `int`) but not semantic mismatches where types align (e.g. file path vs URL, both `string`). Graph-mutation teeth only partially compensate for this gap.

3. **cert-gate expected count is derived at runtime.** The zero-test guard compares executed count against `dotnet test --list-tests` on the cert-gate filter (see `scripts/cert-gate-config.sh`). If the filter or test discovery breaks, the guard fails closed.

4. **Session containment is opt-in and covers compilation + execution; the host keeps
   orchestration, mutant compilation, and judgment.** With `BuildCandidateInSession=true`,
   candidates compile inside the attested session (`session-build` input); with
   `ExecuteCandidateInSession=true` additionally, the witness, determinism, and mutation
   legs EXECUTE candidate and mutant code inside that session (`session-execution` input)
   and untrusted candidate code never runs in the harness process. What deliberately stays
   on the host: iteration orchestration, Roslyn compilation of mutants (trusted tooling —
   the mutant never executes here), and ALL judgment — backends return raw observations
   and the gate compares, because a backend that judged its own executions could be lied
   to once and would then lie to the certificate forever. With both flags off (the
   default), sessions remain attestation-only and the in-process path is byte-identical
   to before. The **write** surface is confined separately, by `ProposerConfinement`'s
   single-declaration tool allowlist.

   *Container hardening (M26).* Every session (and one-shot) container now runs with
   `--cap-drop ALL`, `--security-opt no-new-privileges`, `--pull never` (the image must
   already be present — a session never triggers the fetch of an unattested image), and
   `--read-only`; the spec's `ScratchPaths` come back as capped tmpfs mounts
   (`SessionScratchPaths.Default` = the candidate work dir, the execution root, `/tmp`,
   `/root` — the SDK image's toolchain home), so the in-session build and execution legs
   still work and any write outside the declared surface fails loudly. Attestation now
   records the containment the engine reports it applied — network mode, read-only rootfs,
   dropped capabilities, security options — inside the hashed attestation input, and the
   Docker backend refuses to attest a session that requested no network but does not
   report `none`. `Ashlar:Autonomy:SessionImageDigest` optionally PINS the image identity
   (refuse-to-start on mismatch; null = capture only, as before). Still on the host, and
   deliberately: the container runs as **root** — the SDK image ships no unprivileged
   user, and a numeric `--user` cannot own the tmpfs scratch the toolchain needs without
   more staging work than this pass earns (follow-up, not a gap in what is attested).
   The read-only leg was reasoned against the SDK image's write paths, not yet flown on a
   live daemon; the first `-SweepLive` flight after this lands is its proof, and a
   forgotten scratch path shows up as an explained `read-only file system` build failure,
   never as a silent host-side fallback. Note that tmpfs pages are charged to the
   session's memory cap: build output now counts toward `512m`.

5. **Model proposing: CLOSED for the mechanism; the remaining boundary is scale.** Both
   legs have now flown: a recorded model proposal (P5) and a LIVE local-model proposal
   (P6 — ollama called at flight time, admitted on sample 4 with measured acceptance
   1/4). Every stage the spec demands has real evidence: model identity hash-bound in
   lineage, witness-blind prompting, full session containment, honest gate rejections,
   a swap-host identity hold, and an autonomous Tier-0 swap of live model output. What
   remains is breadth, not mechanism: one objective and one task family so far; a
   standing proposer loop over many objectives with acceptance tracked per lineage is
   host-operations work on seams that all exist.

6. **Kernel options bind from environment variables only in the shipped hosts.** `AddAshlar` builds its own `IConfiguration` from `AddEnvironmentVariables()` (`src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs`), so `Ashlar:Meai`, `Ashlar:NodeCapabilityRuntime`, `Ashlar:WorkloadScaling` and the other kernel sections read `Ashlar__X__Y` variables and never `appsettings.json` in Ashlar.API / Ashlar.CLI (`Ashlar:Autonomy` is host-composed and reads whatever configuration the composing host passes). Documented in `docs/Configuration.md`; the fix is architectural, not a docs fix.

7. **Signature downgrade: the Ed25519 check is conditional on a field the record controls.**
   Every verification path enforces the Ed25519 signature *only when the record carries one*.
   `CertificationRecordSigner.Verify` reduces to
   `string.IsNullOrWhiteSpace(data.Ed25519Signature) || CertificationRecordEd25519.VerifySignature(data)`,
   and `CertificationTrustVerifier.Verify` guards its Ed25519 block with
   `if (!string.IsNullOrWhiteSpace(record.Ed25519Signature))`. Presence is therefore
   attacker-controlled: **deleting** the `ed25519Signature` field — not forging it — drops
   verification to HMAC alone, and per limitation 1 the HMAC key is a committed public
   constant unless `ASHLAR_CERT_DEV_HMAC_KEY` is set. Strip the field, recompute the HMAC
   from the source, and a forged record verifies through all four non-test entry points.
   `FileCertificationRecordStore` re-verifies on load through the same signer, so a record
   edited on disk this way loads as admitted.

   The conditional is not itself a mistake — it is the documented dual-write window for
   netstandard2.0 consumers that have no NSec target. What is missing is any way for a
   caller to say *this deployment requires a signature*, so the window cannot be closed even
   by a host that has keys and wants strictness.

   *Update, 2026-08-27: the mechanism now exists, opt-in.*
   `CertificationVerifyOptions.RequireEd25519Signature` turns a missing signature into a
   refusal (`ed25519-signature-required`), and `TrustedEd25519PublicKeys` pins the acceptable
   signers (`ed25519-key-not-trusted`); pinning implies the signature it pins. Both are
   threaded through **both** verification tiers, and on netstandard2.0 — which cannot evaluate
   Ed25519 at all — requesting either causes a refusal (`ed25519-verification-unavailable`)
   rather than an unchecked pass. **Every default is unchanged**, so for an unconfigured
   deployment this row is downgraded from an open hole to a *configurable* one, not closed.
   Not compiled — see the provenance note below.

   Two distinct fixes were needed, and the first is worthless without the second:

   - A require-signature mode, so a missing Ed25519 signature is a refusal rather than a
     downgrade. This closes stripping.
   - Trusted-key pinning. `CertificationRecordEd25519.VerifySignature` verifies the
     signature against `record.Ed25519PublicKey` — **the public key carried by the record
     itself** — so a self-consistent record signed with an attacker-generated keypair
     verifies. Requiring a signature without pinning the acceptable signers only forces an
     attacker to sign rather than strip, which costs them nothing. Pinning needs the trust
     root that SPEC-006 §3 defers (`~/.ashlar/keys/trusted/` is described for rotation but
     is not consulted by the certification path, which reads `ASHLAR_CERT_ED25519_KEY` — the
     two-operator-identity split — **resolved 2026-08-27 to a single identity**, see
     `_handoff/readiness/DECISION-identity-split.md`).

     **Pin against `operator.pub` only.** Do *not* enumerate `~/.ashlar/keys/trusted/`:
     `OperatorKey.Generate(rotate: true)` writes the *previous* public key into it
     (`src/Ashlar.Manifest/Signing/OperatorKey.cs:45-52`) and there is no revocation (`:28`),
     so using it as an allowlist would make `ashlar keys init --rotate` after a suspected key
     theft permanently re-authorize the stolen key — an action that reads as remediation and
     is worse than doing nothing.

   Not yet fixed. SPEC-006 is ACCEPTED as of 2026-08-27 and lists this as an unmet
   obligation under S-1: that rule covers a signature that fails verification and is silent
   about one that was removed.

   *Update, 2026-09-06: **CLOSED**.* `CertificationVerifyOptions.Default` and
   `CertificationVerifyOptions.Strict` both now set `RequireEd25519Signature = true` and
   `MinimumSchemaVersion = 2`, fail-closed by default. Production admission and load paths
   (FileCertificationRecordStore, CertifiedBrickHotSwapHost, SelfProducedBrickCertificationPolicy,
   etc.) use `Strict` and reject signature-stripped records with `ed25519-signature-required`.
   The `Legacy` option remains for backward compatibility during migration. This closes the
   signature-strip attack on all non-Legacy code paths. CI-verified in SchemaVersionFloorTests.

   *Established 2026-08-27 by reading the cited sources. No .NET SDK was available in the
   authoring environment, so nothing in this row was executed — it is a code-reading result,
   not a CI result.*

8. **Schema downgrade: the payload lane is chosen by an attacker-supplied field, and the
   legacy lane signs far less.** This **compounds limitation 7 rather than replacing it.**
   `CertificationRecordSigning.BuildPayload` opens with
   `if (record.SchemaVersion is null) return BuildLegacyPayload(record);`
   (`src/Ashlar.Certification.Contracts/CertificationRecordSigning.cs:106`).

   *Correction, 2026-08-27.* An earlier draft of this row claimed the schema downgrade was
   "strictly worse than limitation 7 and subsumes it", and that an attacker "does not need to
   strip `ed25519Signature`". **Both were wrong.**
   `CertificationRecordEd25519.VerifySignature` builds its payload with the *same*
   `BuildPayload` (`src/Ashlar.Certification.Contracts/CertificationRecordEd25519.cs:72`), so
   nulling `schemaVersion` also changes the bytes the Ed25519 signature is checked against: a
   signature left in place fails, and the record is refused
   (`CertificationTrustVerifier` returns `ed25519-signature-invalid`;
   `CertificationRecordSigner.Verify` returns false). **Stripping the signature is still
   required.**

   What the legacy lane adds is not a cheaper attack but a **wider one**. Comparing the two
   payload builders (`:109-140` versus `:144-163`), the legacy lane drops `SchemaVersion`,
   `Gate`, `GatesPassed`, `Inputs`, `Proposer`, `Attempts` **and** `Ed25519PublicKey` out of
   the signed bytes entirely. So an attacker who has already performed limitation 7's strip
   can then rewrite **the gate name and the list of gates that passed** under a valid HMAC —
   which reaches the core invariant of the trust-loop spec. A record can claim to have passed
   gates it never ran. On netstandard2.0 the Ed25519 block is compiled out entirely, so
   nothing needs downgrading there at all.

   **There was no minimum accepted schema version anywhere in the repository** when this row
   was written — a repo-wide grep for `SchemaVersion >=`, `SchemaVersion <`, `MinimumSchema`
   and `MinSchema` returned zero non-test hits. *Added 2026-08-27* as
   `CertificationVerifyOptions.MinimumSchemaVersion`, checked by both verification tiers
   before any signature, and demonstrated end to end by `SchemaVersionFloorTests`: the same
   forged record with a rewritten gate name verifies at floor 0 and is refused
   (`schema-version-below-floor`) at floor 2. **The default floor is 0**, so an unconfigured
   deployment is unchanged; raising it is the remediation. The only non-test site that *stamps* a version is
   `CertificationGate.cs:424`, and no verifier ever compares against it.

   *Update, 2026-09-06: **CLOSED**.* `CertificationVerifyOptions.Default` and
   `CertificationVerifyOptions.Strict` now set `MinimumSchemaVersion = 2` (trust-loop schema),
   fail-closed by default. Production paths reject legacy-schema records with
   `schema-version-below-floor`. Combined with Ed25519 requirement (limitation 7), this closes
   the schema-downgrade attack. Only `Legacy` option accepts schema version 0.

   The consequence for planning is the important part: **hardening a new schema version
   closes nothing on its own.** Any design that adds a stricter v3 lane while v1 and v2
   remain verifiable under the committed constant is bypassed by minting a v1 record. The
   missing control is a version floor — `SPEC-006` S-5 as of 2026-08-27 — and it is
   independent of the operator-identity question.

   *Established 2026-08-27 by reading the cited sources. No .NET SDK was available in the
   authoring environment, so nothing in this row was executed — it is a code-reading result,
   not a CI result.*

   Note also that `record.Signed` is not a backstop: it is a plain `required bool ... init`
   data field (`src/Ashlar.Certification.Contracts/CertificationRecordData.cs:26`) that the minter sets and the JSON carries
   literally (see `samples/certified-brick-reuse/Ashlar.Certified.DamageResolver/certification-record.json`). An attacker
   leaves it `true`. `FileCertificationRecordStore`'s own remarks already say the flags on a
   persisted record are a claim and not evidence; the same is true of this one.

9. **`CompositionCertificationRecordSigner` discards the signer it is given and reads the
   environment instead.** Its constructor takes a `CertificationRecordSigner? brickSigner`,
   documents it as "Unused; kept so existing composition wiring compiles unchanged", and
   drops it with `_ = brickSigner;`. It then resolves its own key from
   `ASHLAR_CERT_DEV_HMAC_KEY` or the committed constant, and computes its honesty flag as
   `CertificationRecordSigning.UsesDevKey()` — **with no argument**, so the flag can never
   report an explicitly supplied key
   (`src/Ashlar.Infrastructure/Certification/Composition/CompositionCertificationRecordSigner.cs:20,:26,:27-28,:30`).
   In practice flag and key agree, because this class resolves both the same way; the false
   statement is the class's own XML doc, which claims it "resolves its key exactly as
   `CertificationRecordSigner` does" (`:10-13`). It does not:
   `src/Ashlar.Infrastructure/Certification/CertificationRecordSigner.cs:37-41` honours an
   explicit key, and this class discards one via `_ = brickSigner;`.

   The effect is that SPEC-006 S-4's only stated migration path — supply a real key — is
   already broken for compositions: a host that passes one still mints composition records
   under the committed public constant. Since
   composition admission chains trust from constituent atom signatures (limitation 1), this
   undercuts the composition path specifically. Fixing it is a prerequisite under every
   identity option and should land on its own.

   *Established 2026-08-27 by reading the cited sources. No .NET SDK was available in the
   authoring environment, so nothing in this row was executed — it is a code-reading result,
   not a CI result.*
