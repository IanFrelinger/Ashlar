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

1. ✅ **Scorecard thresholds met:** Last-N green ≥80%, time-to-admit ≤15min, Strict rejection ≤70%, for 7 consecutive days.
2. ✅ **Strict production Ed25519 on master:** PR #523 merged; Strict paths enforce `RequireEd25519Signature=true`.
3. ✅ **Dated Strict passes in ledger:** At least 7 dated ledger rows showing PASS with Strict verification and Ed25519 signatures (Gap column empty or only notes non-blocking issues).
4. ✅ **Real hygiene PR proof:** A production-quality Ashlar PR (not fixture/sample) created via the Ashlar loop (extend → certify → admit → PR) is documented in the ledger. Fixture E2E is the floor; real dogfood PR is the framework proof.

**Current Status (as of 2026-09-06):**
- ✅ PR #523 (Strict+Ed25519) merged to master (merge commit 966e6bf4)
- ❌ No dated Strict passes in ledger yet (canary sweep script still stub; awaits real autonomy loop wiring)
- ❌ No real hygiene PR via Ashlar loop yet
- ⚠️  lim-9 status TBD (may still be open)

**Action:** Keep marketing HOLD. Monitor ledger for dated Strict passes once real autonomy loop wiring completes. Revisit unlock criteria after 7+ consecutive green days with real E2E passes.

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
