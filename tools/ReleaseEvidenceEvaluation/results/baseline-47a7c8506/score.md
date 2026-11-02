# Release-evidence evaluation score

Actual candidate: `47a7c8506b89670d977265ce342a962d35858cd5`. Frozen label baseline: `47a7c8506b89670d977265ce342a962d35858cd5`. Mode: `baseline`. Cases: **36**.

AI-only independent review; no human, customer, productivity, or release-approval claim.

| Metric | Count / denominator | Rate |
|---|---:|---:|
| answered_cases | 35 / 36 cases | 97.22% |
| assurance_false_pass_combined | 1 / 30 cases | 3.33% |
| assurance_false_pass_known_fail | 0 / 20 cases | 0.00% |
| assurance_false_pass_unknown | 1 / 10 cases | 10.00% |
| assurance_unknown_among_answered | 14 / 35 cases | 40.00% |
| assurance_unknown_results | 14 / 36 cases | 38.89% |
| collection_failures | 1 / 36 cases | 2.78% |
| evidence_cases_with_incorrect_blockers | 1 / 14 cases | 7.14% |
| evidence_cases_with_missed_blockers | 8 / 20 cases | 40.00% |
| evidence_cases_with_unassessable_blockers | 1 / 14 cases | 7.14% |
| evidence_incorrect_blockers | 2 / 30 blocker instances | 6.67% |
| evidence_missed_blockers | 9 / 26 blocker instances | 34.62% |
| evidence_unassessable_blockers | 1 / 30 blocker instances | 3.33% |
| export_failures | 0 / 36 cases | 0.00% |
| harness_failures | 0 / 36 cases | 0.00% |
| legacy_cases_with_incorrect_blockers | 1 / 17 cases | 5.88% |
| legacy_cases_with_missed_blockers | 5 / 20 cases | 25.00% |
| legacy_cases_with_unassessable_blockers | 1 / 17 cases | 5.88% |
| legacy_compatibility_disagreements | 7 / 36 cases | 19.44% |
| legacy_false_ready_known_blockers | 3 / 20 cases | 15.00% |
| legacy_incorrect_blockers | 1 / 22 blocker instances | 4.55% |
| legacy_missed_blockers | 6 / 26 blocker instances | 23.08% |
| legacy_ready_unknown_readiness | 5 / 9 cases | 55.56% |
| legacy_unassessable_blockers | 1 / 22 blocker instances | 4.55% |
| legacy_unknown_among_answered | 4 / 35 cases | 11.43% |
| legacy_unknown_results | 4 / 36 cases | 11.11% |

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

Run: `collector-error`; export: `not-attempted`. Legacy: `None` (readiness label `blocked`); assurance: `None` (label `fail`).

Runner error:

    "Object reference not set to an instance of an object."

Disclosures: `collection_failures`, `evidence_cases_with_missed_blockers`, `evidence_missed_blockers`, `legacy_cases_with_missed_blockers`, `legacy_missed_blockers`.

- evidence: missed ["required-ci-queued"]; incorrect []; unassessable [].
- legacy: missed ["required-ci-queued"]; incorrect []; unassessable [].

### e12-missing-rule-context

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `unknown` (label `fail`).

Disclosures: `evidence_cases_with_missed_blockers`, `evidence_missed_blockers`.

- evidence: missed ["required-context-missing"]; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e13-conflicting-same-name

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `fail` (label `fail`).

Disclosures: `evidence_cases_with_unassessable_blockers`, `evidence_unassessable_blockers`, `legacy_cases_with_missed_blockers`, `legacy_cases_with_unassessable_blockers`, `legacy_missed_blockers`, `legacy_unassessable_blockers`.

- evidence: missed []; incorrect []; unassessable ["blocker-1"].
- legacy: missed ["github-merge-block"]; incorrect []; unassessable ["blocker:0"].

### e14-running-no-conclusion

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `unknown` (label `fail`).

Disclosures: `evidence_cases_with_missed_blockers`, `evidence_missed_blockers`.

- evidence: missed ["required-ci-running"]; incorrect []; unassessable [].
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

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `blocked`); assurance: `unknown` (label `fail`).

Disclosures: `evidence_cases_with_missed_blockers`, `evidence_missed_blockers`, `legacy_cases_with_missed_blockers`, `legacy_compatibility_disagreements`, `legacy_false_ready_known_blockers`, `legacy_missed_blockers`.

- evidence: missed ["new-head-required-check-missing"]; incorrect []; unassessable [].
- legacy: missed ["new-head-required-check-missing"]; incorrect []; unassessable [].

### e24-base-moves-during-read

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `blocked`); assurance: `unknown` (label `fail`).

Disclosures: `evidence_cases_with_missed_blockers`, `evidence_missed_blockers`, `legacy_cases_with_missed_blockers`, `legacy_compatibility_disagreements`, `legacy_false_ready_known_blockers`, `legacy_missed_blockers`.

- evidence: missed ["strict-base-behind"]; incorrect []; unassessable [].
- legacy: missed ["strict-base-behind"]; incorrect []; unassessable [].

### e25-approval-on-older-commit

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `ready`); assurance: `pass` (label `pass`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e26-facts-change-same-head

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `blocked`); assurance: `unknown` (label `fail`).

Disclosures: `evidence_cases_with_missed_blockers`, `evidence_missed_blockers`, `legacy_cases_with_missed_blockers`, `legacy_compatibility_disagreements`, `legacy_false_ready_known_blockers`, `legacy_missed_blockers`.

- evidence: missed ["later-changes-requested", "later-required-ci-failure"]; incorrect []; unassessable [].
- legacy: missed ["later-changes-requested", "later-required-ci-failure"]; incorrect []; unassessable [].

### e27-hostile-check-metadata

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `ready`); assurance: `pass` (label `pass`).

Disclosures: none under the preregistered metrics.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e28-malformed-pr-source-metadata

Run: `answered`; export: `success`. Legacy: `ready` (readiness label `unknown`); assurance: `pass` (label `unknown`).

Disclosures: `assurance_false_pass_combined`, `assurance_false_pass_unknown`, `legacy_ready_unknown_readiness`.

- evidence: missed []; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

### e29-app-bound-and-deployment-rule

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `unknown`); assurance: `fail` (label `unknown`).

Disclosures: `evidence_cases_with_incorrect_blockers`, `evidence_incorrect_blockers`, `legacy_cases_with_incorrect_blockers`, `legacy_compatibility_disagreements`, `legacy_incorrect_blockers`.

- evidence: missed []; incorrect ["blocker-1", "required-check-10"]; unassessable [].
- legacy: missed []; incorrect ["blocker:0"]; unassessable [].

### e30-required-collection-check

Run: `answered`; export: `success`. Legacy: `blocked` (readiness label `blocked`); assurance: `unknown` (label `fail`).

Disclosures: `evidence_cases_with_missed_blockers`, `evidence_missed_blockers`.

- evidence: missed ["own-required-check-incomplete"]; incorrect []; unassessable [].
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

Disclosures: `evidence_cases_with_missed_blockers`, `evidence_missed_blockers`.

- evidence: missed ["required-check-2-pending"]; incorrect []; unassessable [].
- legacy: missed []; incorrect []; unassessable [].

## Retained input hashes

- ground_truth_sha256: `85b97df9b1b08dfe1585855b684324171f0ebec50ea3eac4902805de0db5c3e1`
- run_manifest_sha256: `32c50d15ff6d8d4ca40440439f0728f4c0feeacd9c68032e8ea9c41a1a944cd0`
- annotations_sha256: `50b2e36cf1899b4b7e7db227616837bc274367e4362a9bff98f13c281408269b`
- fixture_manifest_sha256: `7f13a41177dd6b0e639ead8dd173ab2556d2fb96683716a10d258d50b1d06ea5`

## Limits

- A small fixed sample does not establish general accuracy.
- Expected policy and blockers come only from frozen independent review labels.
- Artifact hashes establish retained-record consistency, not proof of reviewer blinding chronology.
- Legacy readiness and scoped assurance are separate metrics; collector errors remain errors.
