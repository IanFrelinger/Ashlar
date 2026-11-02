# Release-evidence evaluation score

Actual candidate: `359edd2a731a43c7f14f31233ca312b96a0a5bb0`. Frozen label baseline: `86c6e9e18da496264d2e138efcbd18fabc1558bc`. Mode: `repaired-rerun`. Cases: **3**.

AI-only independent review; no human, customer, productivity, or release-approval claim.

| Metric | Count / denominator | Rate |
|---|---:|---:|
| answered_cases | 3 / 3 cases | 100.00% |
| assurance_false_pass_combined | 0 / 1 cases | 0.00% |
| assurance_false_pass_known_fail | 0 / 0 cases | n/a |
| assurance_false_pass_unknown | 0 / 1 cases | 0.00% |
| assurance_unknown_among_answered | 1 / 3 cases | 33.33% |
| assurance_unknown_results | 1 / 3 cases | 33.33% |
| collection_failures | 0 / 3 cases | 0.00% |
| evidence_cases_with_incorrect_blockers | 0 / 0 cases | n/a |
| evidence_cases_with_missed_blockers | 0 / 0 cases | n/a |
| evidence_cases_with_unassessable_blockers | 0 / 0 cases | n/a |
| evidence_incorrect_blockers | 0 / 0 blocker instances | n/a |
| evidence_missed_blockers | 0 / 0 blocker instances | n/a |
| evidence_unassessable_blockers | 0 / 0 blocker instances | n/a |
| export_failures | 0 / 3 cases | 0.00% |
| harness_failures | 0 / 3 cases | 0.00% |
| legacy_cases_with_incorrect_blockers | 0 / 0 cases | n/a |
| legacy_cases_with_missed_blockers | 0 / 0 cases | n/a |
| legacy_cases_with_unassessable_blockers | 0 / 0 cases | n/a |
| legacy_compatibility_disagreements | 0 / 3 cases | 0.00% |
| legacy_false_ready_known_blockers | 0 / 0 cases | n/a |
| legacy_incorrect_blockers | 0 / 0 blocker instances | n/a |
| legacy_missed_blockers | 0 / 0 blocker instances | n/a |
| legacy_ready_unknown_readiness | 1 / 1 cases | 100.00% |
| legacy_unassessable_blockers | 0 / 0 blocker instances | n/a |
| legacy_unknown_among_answered | 0 / 3 cases | 0.00% |
| legacy_unknown_results | 0 / 3 cases | 0.00% |

## Every case and disclosed disagreement

### public-ready-01

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `unknown`); assurance: `unknown` (label `unknown`).

Disclosures: `legacy_ready_unknown_readiness`.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### public-ready-02

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `ready`); assurance: `pass` (label `pass`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### public-ready-03

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `ready`); assurance: `pass` (label `pass`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

## Retained input hashes

- ground_truth_sha256: `56bb3ef07b040e87de45d3fa304e694ec4a34d43d3dbc69eb77d9abc2b5b2bc1`
- run_manifest_sha256: `33a436b62813197d84ff28ddadc0d45bacca0f63bb687a5ae0149b7451ec23b9`
- annotations_sha256: `14d4a6879f5511501ecdd7f6e16b92a172ddba47de0942561f19d46a54bfcd16`
- fixture_manifest_sha256: `17e01f26dff07b58d004b43ae12c5c3959e6a58b36b0a22ef56b0aa107919035`

## Limits

- A small fixed sample does not establish general accuracy.
- Expected policy and blockers come only from frozen independent review labels.
- Artifact hashes establish retained-record consistency, not proof of reviewer blinding chronology.
- Legacy readiness and scoped assurance are separate metrics; collector errors remain errors.
