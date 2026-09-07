# Dogfood Ledger

**Purpose:** Dated pass/fail evidence for demos before any autonomy or design-partner marketing claim. This ledger records what actually runs end-to-end for Ashlar dogfood / shippable-demo truth. Product copy stays with Marketing/Product 3000.

For the full gate catalog and block test commands, see [`docs/DogfoodValidation.md`](DogfoodValidation.md).

For scorecard thresholds and autonomy marketing unlock criteria, see [`docs/dogfood-scorecard.md`](dogfood-scorecard.md).

## Entries

| Date | Demo | Pass/Fail | Gap | Owner | Repro |
|------|------|-----------|-----|-------|-------|
| 2026-09-06 | Strict+Ed25519 production verification (lim-7 / lim-8 close via PR #523) | **PASS** for Strict/Default requiring Ed25519 signatures (fail-closed) | **Limitation 9 remains OPEN** (CompositionCertificationRecordSigner key discard). Autonomy / design-partner marketing still **HOLD** until real dated E2E dogfood passes with repro exist — this entry is cert-path close evidence, not an E2E shippable-demo pass. | Dogfood Ledger | [PR #523](https://github.com/IanFrelinger/Ashlar/pull/523) closed limitations 7–8; merge commit `966e6bf4024ca634bdf607b376d44fc5240fd42f`; tests `Strict_RejectsRecordWithoutEd25519` and `Default_RejectsRecordWithoutEd25519` in `SchemaVersionFloorTests`; see [`docs/certification-evidence.md`](certification-evidence.md) limitations 7–8 CLOSED |
| 2026-09-06 | Continuous dogfood proof infrastructure | **INFRA ONLY** | Workflow created with canary sweep script stub. Ed25519 signature requirement (PR #523) now on master; lim-9 status TBD; autonomy marketing remains HOLD until scorecard thresholds met ~7 days + dated Strict E2E passes appear. | Dogfood Continuous | [PR #529](https://github.com/IanFrelinger/Ashlar/pull/529) adds scheduled CI workflow `.github/workflows/dogfood-continuous-proof.yml` that will run Strict extend→certify→admit on canary objectives and auto-append dated results to this ledger. |
| 2026-09-05 | Cert-gate trust signature fail-closed defaults (PR #513 partial close of limitations 7–9) | **PASS (fail-closed defaults on master)** | **Limitation 7 residual closed by PR #523 on 2026-09-06.** Ledger bootstrap only; full end-to-end shippable demos still need dated entries. Autonomy marketing remains blocked until the ledger shows real E2E passes with repro. | Dogfood Ledger | [PR #513](https://github.com/IanFrelinger/Ashlar/pull/513) landed fail-closed verification defaults; merge commit `16125e58f098826fc1a970ce609fefe77759b5d4`; `CertificationForgeAttackTests` (signature stripping / schema downgrade / composition key); see [`docs/certification-evidence.md`](certification-evidence.md) limitations 7–9 |

## Ledger Format

Each row documents one dated dogfood run or infrastructure milestone:

- **Date:** YYYY-MM-DD of the event/run
- **Demo:** Brief description of what was exercised (fixture name, PR milestone, infrastructure change)
- **Pass/Fail:** Outcome - PASS (with context), FAIL (with reason), INFRA ONLY, or GAP
- **Gap:** Known limitations, dependencies, or blockers (limitation 7/8/9 references, missing PRs, etc.)
- **Owner:** Team/component responsible (Dogfood Continuous, Cert Gate, etc.)
- **Repro:** Links to PR, commit SHA, CI run, or runbook for reproducing the result

## Continuous Proof

The scheduled workflow (`.github/workflows/dogfood-continuous-proof.yml`) runs autonomy loop sweeps on canary objectives and automatically appends rows to this ledger on completion. Rows are appended via CI automation; manual edits for infrastructure milestones are also permitted.

**Current canary:** `rgb-hex-parse` (6-digit hex color parser from `samples/autonomy-objectives/`)

**Scheduled cadence:** Weekdays (Monday-Friday) at 06:00 UTC

**Autonomy marketing unlock:** Requires passing scorecard thresholds (see `dogfood-scorecard.md`) for ~7 consecutive days. Marketing claims MUST remain HOLD until dated Strict production passes with Ed25519 appear in this ledger.
