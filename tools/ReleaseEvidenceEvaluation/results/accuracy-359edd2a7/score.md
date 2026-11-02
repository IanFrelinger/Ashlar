# Release-evidence evaluation score

Actual candidate: `359edd2a731a43c7f14f31233ca312b96a0a5bb0`. Frozen label baseline: `47a7c8506b89670d977265ce342a962d35858cd5`. Mode: `repaired-rerun`. Cases: **36**.

AI-only independent review; no human, customer, productivity, or release-approval claim.

| Metric | Count / denominator | Rate |
|---|---:|---:|
| answered_cases | 36 / 36 cases | 100.00% |
| assurance_false_pass_combined | 0 / 30 cases | 0.00% |
| assurance_false_pass_known_fail | 0 / 20 cases | 0.00% |
| assurance_false_pass_unknown | 0 / 10 cases | 0.00% |
| assurance_unknown_among_answered | 10 / 36 cases | 27.78% |
| assurance_unknown_results | 10 / 36 cases | 27.78% |
| collection_failures | 0 / 36 cases | 0.00% |
| evidence_cases_with_incorrect_blockers | 0 / 20 cases | 0.00% |
| evidence_cases_with_missed_blockers | 0 / 20 cases | 0.00% |
| evidence_cases_with_unassessable_blockers | 1 / 20 cases | 5.00% |
| evidence_incorrect_blockers | 0 / 45 blocker instances | 0.00% |
| evidence_missed_blockers | 0 / 26 blocker instances | 0.00% |
| evidence_unassessable_blockers | 1 / 45 blocker instances | 2.22% |
| export_failures | 0 / 36 cases | 0.00% |
| harness_failures | 0 / 36 cases | 0.00% |
| legacy_cases_with_incorrect_blockers | 0 / 20 cases | 0.00% |
| legacy_cases_with_missed_blockers | 1 / 20 cases | 5.00% |
| legacy_cases_with_unassessable_blockers | 1 / 20 cases | 5.00% |
| legacy_compatibility_disagreements | 4 / 36 cases | 11.11% |
| legacy_false_ready_known_blockers | 0 / 20 cases | 0.00% |
| legacy_incorrect_blockers | 0 / 26 blocker instances | 0.00% |
| legacy_missed_blockers | 1 / 26 blocker instances | 3.85% |
| legacy_ready_unknown_readiness | 5 / 9 cases | 55.56% |
| legacy_unassessable_blockers | 1 / 26 blocker instances | 3.85% |
| legacy_unknown_among_answered | 5 / 36 cases | 13.89% |
| legacy_unknown_results | 5 / 36 cases | 13.89% |

## Every case and disclosed disagreement

### e01-no-configured-requirements

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `ready`); assurance: `pass` (label `pass`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e02-required-external-status

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `ready`); assurance: `pass` (label `pass`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e03-two-approvals

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `ready`); assurance: `pass` (label `pass`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e04-mixed-optional-ci

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `ready`); assurance: `pass` (label `pass`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e05-required-check-failure

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e06-merge-conflict

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e07-draft-pr

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e08-review-threshold

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e09-changes-requested

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e10-behind-strict-base

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e11-queued-check

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e12-missing-rule-context

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e13-conflicting-same-name

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: `evidence_cases_with_unassessable_blockers`, `evidence_unassessable_blockers`, `legacy_cases_with_missed_blockers`, `legacy_cases_with_unassessable_blockers`, `legacy_missed_blockers`, `legacy_unassessable_blockers`.

- evidence: missed []; incorrect []; unassessable ["blocker-1"].
- legacy: missed ["github-merge-block"]; incorrect []; unassessable ["blocker:0"].

### e14-running-no-conclusion

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e15-anonymous-graphql

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `unknown`); assurance: `unknown` (label `unknown`).

Disclosures: `legacy_ready_unknown_readiness`.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e16-policy-permission-denied

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `unknown`); assurance: `unknown` (label `unknown`).

Disclosures: `legacy_ready_unknown_readiness`.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e17-private-pr-not-found

Run: `answered`; export: `success`. Legacy: `unknown` (readiness label `unknown`); assurance: `unknown` (label `unknown`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e18-checks-and-reviews-denied

Run: `answered`; export: `success`. Legacy: `unknown` (readiness label `unknown`); assurance: `unknown` (label `unknown`).

Disclosures: `legacy_compatibility_disagreements`.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e19-rate-limited-policy

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `unknown`); assurance: `unknown` (label `unknown`).

Disclosures: `legacy_ready_unknown_readiness`.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e20-review-transport-timeout

Run: `answered`; export: `success`. Legacy: `unknown` (readiness label `ready`); assurance: `unknown` (label `unknown`).

Disclosures: `legacy_compatibility_disagreements`.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e21-malformed-policy-json

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `unknown`); assurance: `unknown` (label `unknown`).

Disclosures: `legacy_ready_unknown_readiness`.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e22-truncated-second-page

Run: `answered`; export: `success`. Legacy: `unknown` (readiness label `unknown`); assurance: `unknown` (label `unknown`).

Disclosures: `legacy_compatibility_disagreements`.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e23-head-moves-during-read

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e24-base-moves-during-read

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e25-approval-on-older-commit

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `ready`); assurance: `pass` (label `pass`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e26-facts-change-same-head

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e27-hostile-check-metadata

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `ready`); assurance: `pass` (label `pass`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e28-malformed-pr-source-metadata

Run: `answered`; export: `success`. Legacy: `unknown` (readiness label `unknown`); assurance: `unknown` (label `unknown`).

Disclosures: `legacy_compatibility_disagreements`.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e29-app-bound-and-deployment-rule

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `unknown`); assurance: `unknown` (label `unknown`).

Disclosures: `legacy_ready_unknown_readiness`.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e30-required-collection-check

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### public-01

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### public-02

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### public-03

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### public-04

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### public-05

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### public-06

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

## Retained input hashes

- ground_truth_sha256: `85b97df9b1b08dfe1585855b684324171f0ebec50ea3eac4902805de0db5c3e1`
- run_manifest_sha256: `154943f901097d014531b1b5cc1d60eb3fe1e44bf86997f47830aca4da7de30e`
- annotations_sha256: `b5520243fe6cd427bed75b8d3c10a3e1f177d124df9b9c2dece505107b5b77b8`
- fixture_manifest_sha256: `7f13a41177dd6b0e639ead8dd173ab2556d2fb96683716a10d258d50b1d06ea5`

## Limits

- A small fixed sample does not establish general accuracy.
- Expected policy and blockers come only from frozen independent review labels.
- Artifact hashes establish retained-record consistency, not proof of reviewer blinding chronology.
- Legacy readiness and scoped assurance are separate metrics; collector errors remain errors.
