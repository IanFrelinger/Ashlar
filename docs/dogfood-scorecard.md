# Dogfood Scorecard

**Purpose:** Define measurable thresholds for autonomy marketing claims. Autonomy and design-partner marketing MUST remain on HOLD until these thresholds are met for ~7 consecutive days AND dated Strict production Ed25519 passes appear in the dogfood ledger.

## Thresholds (Proposed Defaults)

These are **proposed** defaults for Project Manager / Marketing review. Adjust based on campaign data and risk tolerance.

### 1. Last-N Green Pass Rate

**Metric:** Percentage of successful (PASS) runs in the last N scheduled dogfood sweeps.

**Proposed Threshold:**
- **N = 10** (last 10 scheduled runs, ~2 weeks on weekday-only schedule)
- **Threshold: ≥ 80%** pass rate

**Rationale:** Allows for occasional transient failures (network, proposer timeout, model variance) while ensuring the loop is fundamentally stable. 8/10 pass rate means the system handles the canary objective reliably.

**Measurement:** Count PASS vs. FAIL/GAP rows in `docs/dogfood-ledger.md` for the last 10 dated entries with "Continuous dogfood proof" or canary objective name in Demo column.

### 2. Mean Time-to-Admit

**Metric:** Average elapsed time from objective seed to `CertifiedAndAdmitted` outcome across successful runs.

**Proposed Threshold:**
- **≤ 15 minutes** mean time-to-admit (single-objective canary)

**Rationale:** Demonstrates the loop converges efficiently. A 15-minute ceiling for a small parser (rgb-hex-parse) leaves headroom for larger objectives while proving the pipeline is not hung or looping indefinitely.

**Measurement:** Parse workflow logs or structured output (JSONL) for elapsed time from objective seed to final admission. Average over successful runs in threshold window.

**Note:** If `HoldAdmission=true` is used (certify but do not admit), measure time-to-`CertifiedButHeld` instead, as admission is intentionally blocked for safety.

### 3. Strict Rejection Rate

**Metric:** Percentage of proposals rejected by `CertificationVerifyOptions.Strict` on the correctness/mutation/determinism legs.

**Proposed Threshold:**
- **≤ 70%** rejection rate (i.e., ≥ 30% of proposals reach `CertifiedButHeld` or `CertifiedAndAdmitted`)

**Rationale:** The witness exists before the proposal, so rejections are expected and healthy (witness catching defects is the system working). However, a >70% rejection rate may indicate:
  - Proposer is under-constrained or poorly-prompted
  - Witness cases are too strict or misaligned with contract
  - Canary objective is pathologically hard

A 30% certification rate for a simple parser proves the loop can produce viable bricks from model output.

**Measurement:** Count proposals that reach any `Certified*` state vs. proposals rejected at correctness/mutation/determinism. Parse workflow logs or JSONL output.

**Note:** PR #523 (Strict+Ed25519) merged to master. Strict production paths now enforce `RequireEd25519Signature=true`.

### 4. Consecutive Days Hold

**Metric:** Duration (in days) that all thresholds remain continuously met.

**Proposed Threshold:**
- **≥ 7 consecutive days**

**Rationale:** Proves stability over time, not just a lucky one-off pass. Covers weekday-only runs (7 calendar days ≈ 5 scheduled runs), enough to catch regressions or drift.

**Measurement:** Manual review of ledger + threshold metrics. Reset the counter if any threshold is breached.

## Unlock Criteria for Autonomy Marketing

Autonomy and design-partner marketing claims (e.g., "Ashlar autonomously proposes and certifies bricks") are **HOLD** until ALL of the following are true:

1. **Scorecard thresholds (not yet established for a current window):** Last-N green ≥80%, time-to-admit ≤15min, Strict rejection ≤70%, for 7 consecutive days.
2. ✅ **Strict production Ed25519 on master:** PR #523 merged; Strict paths enforce `RequireEd25519Signature=true`.
3. **Split, because the original wording asked CI for a property CI cannot hold.** It read: *"At least 7 dated ledger rows showing PASS with Strict verification and Ed25519 signatures (Gap column empty)."* Nine dated passes would have satisfied that on its face while verifying nothing — see the note below. It is now two claims, because one is about the pipeline and the other is about custody:

   **3a — Pipeline (every scheduled run; ≥ 7 dated rows).** The sweep persists its certification record to a durable store, the run re-reads that record and verifies it under `CertificationVerifyOptions.Strict`, and the row cites the record file and its signer fingerprint rather than a console line. This is a claim about loop mechanics, and a CI-generated key is acceptable **provided the row says so.**

   **3b — Custody (attended; N = 1).** One dated pass whose certification record is Ed25519-signed by an operator key, verified with a `Strict` options instance whose `TrustedEd25519PublicKeys` contains that operator's public key **alone** — never the contents of `trusted/`, which `OperatorKey.Generate(rotate: true)` fills with superseded keys. The row records the fingerprint. Custody needs one credible instance, not repetition.

   **Why pinning is the load-bearing half.** `CertificationVerifyOptions.Strict` leaves `TrustedEd25519PublicKeys` null, so `PinningEnabled` is false and verification runs against the key the record itself carries — the options file says in its own words that requiring a signature without pinning is close to worthless. The static preset alone does not pin a signer. Current hosts can apply `CertificationTrustPolicy` with `ASHLAR_CERT_TRUSTED_ED25519_KEYS` and require it with `ASHLAR_CERT_PINNING_REQUIRED=true`. An unpinned record can show pipeline mechanics under 3a; it does not establish operator custody under 3b.
4. **Real hygiene PR proof (not met):** A production-quality Ashlar PR (not fixture/sample) created via the Ashlar loop (extend → certify → admit → PR) is documented in the ledger. Fixture E2E is the floor; real dogfood PR is the framework proof.

**Current status (reviewed 2026-11-01 at `5e0beeae3b037508f23b475d98d13cc7e64c25de`): HOLD.**

- Strict/Default Ed25519 requirements and the composition signer's injected key holder ship. These are certification-path properties, not evidence that the autonomy criteria passed.
- The nine September fixture rows remain historical loop passes without persisted Strict verification. Their recorded gaps remain valid for those runs.
- Persistence and configurable pinning now ship: `CertificationEvidenceArchive` writes the record, reads the bytes back and verifies with `CertificationTrustPolicy.Strict`; `AutonomousIterationHarness` invokes it **before** the hold. The archive is separate from the admission store. `SweepMode` configures the directory, and the workflow mints a temporary CI Ed25519 key. The prior status text saying these mechanisms did not exist was stale.
- The [2026-10-30 run](https://github.com/IanFrelinger/Ashlar/actions/runs/41050179155) reports a held fixture pass, 18.0 seconds, with persisted/re-read Strict verification. Its downloaded record matches the cited SHA-256; it discloses `pinned=false`, `dev-hmac=true` and an ephemeral CI signer. This establishes an observed pipeline instance, not custody. See [the dated baseline](development-baseline-2026-11-01.md) for artifact identity and verification limits.
- Criterion 3a still needs **at least seven reviewed, landed dated records**; scheduled artifacts do not become ledger entries automatically. No current N=10 pass-rate/rejection-rate window or seven-day threshold window was established by this review.
- Criterion 3b remains open: the operator identity bridge and an attended record with the intended operator public key alone pinned have not been demonstrated. Configurable pinning is available; a CI-generated key does not establish operator custody.
- Criterion 4 remains open: the ledger contains no production-quality, human-reviewed Ashlar PR produced through the full loop. The `rgb-hex-parse` canary is a fixture.

**Action:** Keep autonomy and design-partner marketing HOLD. The commercial plan's offer and pricing do not revise this gate. Review and retain dated pipeline artifacts, complete the operator identity path and attended pinned-key pass, and produce the real PR with human review. Keep `HoldAdmission=true`; never enable admission to obtain verification. Keep long-lived operator private keys out of repository secrets. Historical September rows are not retroactively upgraded by newer code.

## Self-Apply Bar

**Principle:** Ashlar's own development must use Ashlar before we claim it works.

**Floor:** Fixture E2E (canary objectives like `rgb-hex-parse`) prove the loop mechanics.

**Framework Proof:** A real Ashlar hygiene PR—linting, refactoring, gap test addition, or small feature—produced via the full loop (propose → certify → open PR with cert artifact) and merged after human review.

**Documented in Ledger:** When this occurs, add a dated row to `docs/dogfood-ledger.md` with:
- **Demo:** "Real Ashlar hygiene PR via autonomy loop"
- **Pass/Fail:** PASS (with PR link)
- **Gap:** Empty (or only non-blocking notes)
- **Owner:** Ashlar Autonomy
- **Repro:** Link to the merged PR, its cert record, and the loop log showing proposal→admit flow

Until this appears, autonomy marketing remains blocked by principle, not just by thresholds.

## Reporting

Scorecard metrics should be computed from:
- **Ledger rows:** `docs/dogfood-ledger.md` (Pass/Fail outcomes, dates)
- **Workflow logs:** GitHub Actions artifacts from `dogfood-continuous-proof.yml` runs
- **JSONL output (future):** `docs/dogfood-ledger.jsonl` if machine-readable log is added

A weekly dashboard or summary script (future work) could auto-generate threshold status. For now, manual review suffices.

## Revising Thresholds

These are proposals. Adjust based on:
- **Campaign 1-4 data** from `spikes/autonomy-first-flight` runs (model variance, typical rejection rates)
- **Proposer model selection** (codellama:7b vs. qwen3.8:27b vs. future models)
- **Canary objective complexity** (rgb-hex-parse is small; door-lock-transition is more complex)
- **Risk tolerance** (tighter thresholds for public-facing claims, looser for internal dogfood)

Document threshold changes as dated rows in the ledger or as amendments to this file.
