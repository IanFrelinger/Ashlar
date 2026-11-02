# Release-evidence accuracy follow-up — 2026-11-02

The integrated candidate `359edd2a731a43c7f14f31233ca312b96a0a5bb0` recovers all **8 previously
missed evidence blockers** in the frozen 36-case study and removes the incorrect legacy provider
attribution. There are no false-passing assurance decisions in either that study or the three
additional public captures. This is a small development evaluation, not a production accuracy
guarantee, customer pilot, compliance determination or release authorization. Existing holds remain.

## Changes and supported behavior

- Brick contract `1.4.0` retains output names and verdict vocabulary. Requirements keep their own
  app bindings; observed positive integer app IDs must match. Wrong-app failures are advisory.
  Missing/conflicting bindings, malformed primitive IDs and ambiguous status origins remain unknown.
  A known failure for one context is not hidden by another context's uncertainty. Legacy status
  creator IDs identify users, not apps.
- One observed PR change triggers one full recollection, using the changed PR as its new anchor.
  Earlier checks, reviews, policy and errors are discarded. A second changed or unverifiable attempt
  returns an unknown decision, retaining individual observations under their actual head. Up to two
  mergeability polls per attempt are allowed; this is not an atomic GitHub snapshot or a promise to
  detect every same-head change. Schema 1.0 adds optional `freshness.collectionAttempts`.
- Required CI now selects the recollection suite and keeps the provider tests in the existing
  provider suite. The offline retained-run checker verifies the original and supplemental studies
  independently, including each required original baseline and every present run's frozen bytes.

The scope, permissions, latency and compatibility details are in the [evidence contract](../release-evidence.md).
No commercial boundary was moved, customer data collected, repository code executed by the collector,
or external write performed. Package `VERSION` is unchanged; this work is local, not a published release.

## Same frozen 36 cases

The original inputs, independent labels and scoring contract are unchanged. The prior candidate is
`b4b77b7f47975c64873dd8dc458329a584228d36`; both candidates use the same
`85b97df9b1b08dfe1585855b684324171f0ebec50ea3eac4902805de0db5c3e1` label bytes.

| Metric | Previous candidate | Integrated candidate |
|---|---:|---:|
| Answered / all cases | 36 / 36 | 36 / 36 |
| False-passing assurance / expected nonpassing cases | 0 / 30 | 0 / 30 |
| Legacy ready / known-blocked cases | 0 / 20 | 0 / 20 |
| Evidence missed confirmed blockers | 8 / 26 | **0 / 26** |
| Evidence incorrect blocker assertions | 0 / 30 assertions | 0 / 45 assertions |
| Evidence unassessable blocker assertions | 1 / 30 assertions | 1 / 45 assertions |
| Legacy missed confirmed blockers | 5 / 26 | 1 / 26 |
| Legacy incorrect blocker assertions | 1 / 23 assertions | **0 / 26 assertions** |
| Legacy unassessable blocker assertions | 1 / 23 assertions | 1 / 26 assertions |
| Assurance unknown / all cases | 14 / 36 | 10 / 36 |
| Legacy ready / unknown-readiness cases | 4 / 9 | 5 / 9 |

See the [complete integrated score](../../tools/ReleaseEvidenceEvaluation/results/accuracy-359edd2a7/score.md)
and [previous score](../../tools/ReleaseEvidenceEvaluation/results/final-b4b77b7f4/score.md).
The original failed baseline and first repair also remain retained. Collector, harness and export
errors are zero in this integrated run; errors in historical runs remain errors.

The recovered evidence is three app-bound failed checks in public-01, an app-bound pending check
in public-06, and four later facts in e23/e24/e26 after head/base/mergeability changes. Reviewers
checked recollection anchors and consumed response sequences against the unchanged facts.

The remaining e13 provider-specific causal assertion is unassessable: cross-app selection for the
same name is not fully established by the frozen data. Reviewers matched the separate
failed-or-conflicting required-context finding to the frozen broad GitHub merge block; this does
not establish which app caused it. The legacy surface does not name that broad block separately,
leaving its one miss. Neither surface claims a passing decision there.

Removing e29's wrong-app blocker correctly makes legacy `ready` coexist with assurance `unknown`
for its unobserved deployment requirement. This increases ready-on-unknown cases by one; **use
assurance for gating**. The four frozen legacy compatibility disagreements (e18/e20/e22/e28)
are retained as conservative unknown results, not hidden by changing labels.

## Additional public ready-state captures

Three captures across two repositories were selected using open, non-draft, clean mergeability and
successful CI metadata: cli/cli #14716 and jqlang/jq #3432/#3641. They contain 77 checks across four
pages. No token was supplied by capture code; inherited proxy upstream identity and effective scopes
were not observable. Required-policy endpoints returned 403. Those responses were retained, not
replaced with successful reads. PR bodies, diffs, code and artifact payloads were excluded.

Both initial independent AI reviews labeled all three unknown based partly on differing PR-base
and branch-tip SHAs. **Before any candidate execution**, source-based cross-review identified that
this imposed an undocumented SHA-equality requirement. Official GitHub semantics make up-to-date
enforcement conditional; repeated non-null mergeability and an unprotected branch with empty active
rules do not imply such a requirement. Both original reviews were preserved, and both reviewers
agreed to an explicit pre-output adjudication: jq cases ready/pass, cli unknown/unknown for incomplete
protected-policy discovery. The differing SHAs remain limitations; no hidden policy was invented
from jq's denied detail endpoints. This correction illustrates the limits of same-model AI agreement.

The [separate freeze](../../tools/ReleaseEvidenceEvaluation/public-ready/review/pre-execution-freeze.json)
binds the inputs and review/adjudication bytes. Adjudicated labels SHA256:
`56bb3ef07b040e87de45d3fa304e694ec4a34d43d3dbc69eb77d9abc2b5b2bc1`.
The pre-change baseline `86c6e9e18da496264d2e138efcbd18fabc1558bc` and integrated candidate both
produce **2 passing ready cases and 1 unknown assurance case**, zero errors or reported blockers,
and **0/1 false-passing assurance**. Blocker metrics have zero opportunities and null rates;
they are not perfect-accuracy claims. Scores and exact provenance are in the
[supplemental study](../../tools/ReleaseEvidenceEvaluation/public-ready/README.md).

One qualitative limitation remains: cli's unknown explanation identifies unsupported
`copilot_code_review`, whereas the oracle's material unknown concerns denied protected-policy details.
Numeric verdict agreement does not establish explanation accuracy; the blocker-only scoring contract
does not score unknown/pass explanations. The two ready public cases have no configured mandatory
branch gates and do not establish accuracy on a fully observed protected ready PR.

## User review and validation boundaries

The conversation user agreed with the e29 judgment: the required app's CI passes, the different app's
failure is not a blocker, and unobserved deployment leaves assurance unknown. The
[exact response record](../../tools/ReleaseEvidenceEvaluation/review/user-label-review-2026-11-02.json)
is **one post-output label review**, not blinded independent review, review of the other cases, or
approval of code, a pilot, marketing, autonomy or a release. The original review records' historical
human-review counts remain unchanged. New public labels are AI-reviewed, with no human-review claim.

The [engineering validation record](../../tools/ReleaseEvidenceEvaluation/accuracy-validation/README.md)
retains exact commands/environment: **3,989 .NET 8** certification tests and **235 .NET 10** focused
tests pass. The core build passes after correcting the audit environment for a nested offline restore;
the failed first attempt remains retained and is not called a pass.
Passing tests and this selected sample do not lift the [dogfood hold](../dogfood-scorecard.md),
establish operator custody, satisfy seven reviewed dated runs or produce a real human-reviewed loop PR.
No hosted CI run is implied by local tests. Current default master remains `5e0beeae…`; its available
API observations show four required checks successful while shell-lint is unverified, not a pass.

All replay packs are evaluation artifacts. Their `collectedAt` records replay execution, not a new
GitHub observation; do not use them to gate an actual release. A fresh collection is required.
