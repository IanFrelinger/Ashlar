# Dogfood Ledger

**Purpose:** Dated pass/fail evidence for demos before any autonomy or design-partner marketing claim. This ledger records what actually runs end-to-end for Ashlar dogfood / shippable-demo truth. Product copy stays with Marketing/Product 3000.

For the full gate catalog and block test commands, see [`docs/DogfoodValidation.md`](DogfoodValidation.md).

## Entries

| Date | Demo | Pass/Fail | Gap | Owner | Repro |
|------|------|-----------|-----|-------|-------|
| 2026-09-06 | Ed25519 signature-strip and schema-downgrade attacks closed (PR #523 closes limitations 7–8) | **PASS (strict verification on master)** | **Limitation 9 remains OPEN:** Composition signer discards explicitly supplied keys. Autonomy marketing remains HOLD until limitation 9 is closed and ledger shows real E2E autonomous passes with repro. Design-partner engagement allowed with explicit "experimental, hold mode" disclosure. | Dogfood Ledger | [PR #523](https://github.com/IanFrelinger/Ashlar/pull/523) commit `966e6bf4`; `CertificationVerifyOptions.Default` and `.Strict` now require Ed25519 signature + schema version ≥2; see [`docs/certification-evidence.md`](certification-evidence.md) limitations 7–8 marked CLOSED; limitation 9 remains exploitable |
| 2026-09-05 | Cert-gate trust signature fail-closed defaults (PR #513 partial close of limitations 7–9) | **PASS (fail-closed defaults on master)** | Superseded by PR #523 (2026-09-06) which fully closed limitations 7–8. | Dogfood Ledger | [PR #513](https://github.com/IanFrelinger/Ashlar/pull/513) landed fail-closed verification defaults; merge commit `16125e58f098826fc1a970ce609fefe77759b5d4`; see 2026-09-06 entry for current status |
