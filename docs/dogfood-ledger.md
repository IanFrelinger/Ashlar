# Dogfood Ledger

**Purpose:** Dated pass/fail evidence for demos before any autonomy or design-partner marketing claim. This ledger records what actually runs end-to-end for Ashlar dogfood / shippable-demo truth. Product copy stays with Marketing/Product 3000.

For the full gate catalog and block test commands, see [`docs/DogfoodValidation.md`](DogfoodValidation.md).

## Entries

| Date | Demo | Pass/Fail | Gap | Owner | Repro |
|------|------|-----------|-----|-------|-------|
| 2026-09-06 | Strict+Ed25519 production verification (lim-7 / lim-8 close via PR #523) | **PASS** for Strict/Default requiring Ed25519 signatures (fail-closed) | **Limitation 9 remains OPEN** (CompositionCertificationRecordSigner key discard). Autonomy / design-partner marketing still **HOLD** until real dated E2E dogfood passes with repro exist — this entry is cert-path close evidence, not an E2E shippable-demo pass. | Dogfood Ledger | [PR #523](https://github.com/IanFrelinger/Ashlar/pull/523) closed limitations 7–8; merge commit `966e6bf4024ca634bdf607b376d44fc5240fd42f`; tests `Strict_RejectsRecordWithoutEd25519` and `Default_RejectsRecordWithoutEd25519` in `SchemaVersionFloorTests`; see [`docs/certification-evidence.md`](certification-evidence.md) limitations 7–8 CLOSED |
| 2026-09-05 | Cert-gate trust signature fail-closed defaults (PR #513 partial close of limitations 7–9) | **PASS (fail-closed defaults on master)** | **Limitation 7 residual closed by PR #523 on 2026-09-06.** Ledger bootstrap only; full end-to-end shippable demos still need dated entries. Autonomy marketing remains blocked until the ledger shows real E2E passes with repro. | Dogfood Ledger | [PR #513](https://github.com/IanFrelinger/Ashlar/pull/513) landed fail-closed verification defaults; merge commit `16125e58f098826fc1a970ce609fefe77759b5d4`; `CertificationForgeAttackTests` (signature stripping / schema downgrade / composition key); see [`docs/certification-evidence.md`](certification-evidence.md) limitations 7–9 |
