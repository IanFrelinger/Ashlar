# Preregistered scoring contract

This contract was written before candidate replay outputs were available. The scorer
does arithmetic and checks the review records; it does not infer repository policy,
construct expected blockers from Ashlar output, or reuse a production decision rule.
The evaluation is AI-only independent review, not human review or customer research.

Run `python3 tools/ReleaseEvidenceEvaluation/test_score.py` for the artificial
counterexample tests. Score a retained replay with:

```sh
python3 tools/ReleaseEvidenceEvaluation/score.py \
  --ground-truth tools/ReleaseEvidenceEvaluation/review/adjudicated-ground-truth.json \
  --run-manifest PATH/TO/run-manifest.json \
  --annotations PATH/TO/output-review.json \
  --json-output PATH/TO/score.json \
  --markdown-output PATH/TO/score.md
```

The command never overwrites an existing report. Preserve the initial replay and its
score when repairing a finding; use a different run directory and report the rerun's
commit separately. A nonzero failure count is a result to disclose, not a reason to
change the labels, matching rules, or denominator.

Pre-output clarification: the initial contract's SHA256 was
`8505f0342ae2b968f5dd76faf8cf3166292f8f95b85c7d19f6257c3429e71926`.
This clarification permits a repaired candidate to be evaluated against the **same
immutable baseline labels and raw inputs**; it changes no metric or matching rule.
The original failed run remains retained. A changed code commit requires the explicit
scorer flag `--allow-candidate-rerun`, run `candidateMode: "repaired-rerun"`, and
`baselineCandidateCommit` equal to the original labels' `candidate_commit`. The run's
`candidateCommit` and annotation `candidate_commit` identify the actual repaired code.
The report records both actual and frozen baseline commits. Without the explicit
flag and matching declaration, a code-commit mismatch is rejected. Never copy or
relabel the ground truth to make it name the repaired candidate.

## Metrics fixed before output inspection

Every metric includes a numerator, denominator, unit, and case IDs. A zero denominator
has a null rate, never a claim of perfect performance. Cases with collection failures
remain in reference-label denominators and missed-blocker denominators. They do not
become synthetic `unknown` answers. Output `unknown` is counted only when an artifact
actually contains that result.

| Metric | Numerator | Denominator |
|---|---|---|
| Legacy ready against known blockers | Legacy `ready`, independently labeled readiness `blocked` | All readiness `blocked` cases |
| Legacy ready with unknown readiness | Legacy `ready`, independently labeled readiness `unknown` | All readiness `unknown` cases |
| Legacy compatibility disagreements | Legacy verdict outside the frozen `allowable_verdicts` | Cases with an applicable frozen legacy compatibility label |
| Assurance false pass, known failure | Assurance `pass`, expected assurance `fail` | All expected assurance `fail` cases |
| Assurance false pass, unknown | Assurance `pass`, expected assurance `unknown` | All expected assurance `unknown` cases |
| Assurance false pass, combined | Either false-pass class above | All expected assurance `fail` or `unknown` cases |
| Unknown results | Artifact actually says `unknown`, separately for legacy and assurance | All cases; also report among answered cases |
| Collection, harness, export failures | Explicit runner failure status or error marker | All cases, each error class separate |
| Missed blockers, separately by evidence and legacy surface | Frozen confirmed blocker IDs with no reviewed match on that surface | All frozen confirmed blocker instances |
| Incorrect blockers, separately by surface | Reported blocker annotated `incorrect` | All reported blocker instances on that surface |
| Unassessable blockers, separately by surface | Reported blocker annotated `unassessable` | All reported blocker instances on that surface |

Blocker instance metrics also have case counts. A reported evidence blocker is a
JSON finding whose literal `status` is `fail`. A reported legacy blocker is an item
in `payload.outputs.blockers`, identified by its zero-based `blocker:N` index. These
are output extraction rules, not expected-policy rules. Passing or unknown findings
are not assertions of a confirmed blocker. A ground-truth blocker may be matched by
more than one result; it is counted at most once per surface when computing misses.

Matching is a separate, explicit AI review performed after outputs exist and against
the already frozen independent labels. The annotation file must cover every reported
blocker exactly once. Each row includes `result_id`, `ground_truth_ids`,
`classification` (`matched`, `incorrect`, or `unassessable`), and a nonempty rationale.
Only `matched` rows may name ground-truth IDs; their IDs must exist in that case's
frozen `confirmed_blockers`. No text matching, status-to-policy rule, source-rubric
inference, or automatic creation of expected blockers occurs in the scorer.

## Input integrity and review schemas

Adjudicated labels contain `schema_version: 1`, `candidate_commit`,
`fixture_manifest_sha256`, `review_process` with `ai_only`,
`blinded_to_candidate_outputs`, and `adjudicated` all true, top-level `adjudication`
with `status: "resolved"` and empty `conflicts`, and `cases`. Each case has `id`,
`expected_conservative_readiness` (`ready`, `blocked`, `unknown`, or
`not-applicable`), `expected_scoped_assurance` (`pass`, `fail`, `unknown`, or
`not-applicable`), `confirmed_blockers` with unique IDs, and
`legacy_compatibility.allowable_verdicts` (empty means not applicable).

The replay manifest uses `schemaVersion: "ashlar.release-evidence-evaluation-run/1"`,
`candidateCommit`, `fixtureManifestPath`, `fixtureManifestSha256`,
`groundTruthSha256`, and `cases`. The retained manifest's actual byte hash must
match the frozen labels; its `cases` have `id` and `sha256` fields. Run input
hashes must match those frozen per-case hashes. A retained `groundTruthPath`, if
present, is also verified.
Each case has `caseId`, `status` (`answered`, `collector-error`, or `harness-error`),
`datasetPath`, `datasetSha256`, artifact paths relative to the manifest, and a
corresponding SHA256 for every artifact path (`evidenceSha256`, `markdownSha256`,
`payloadSha256`, `transcriptSha256`, or `errorSha256`). Answered cases require
evidence, Markdown, payload and transcript artifacts. Optional `exportStatus:
"error"` is counted separately. `exportStatus: "not-attempted"` is permitted on
collector/harness errors before rendering, and does not assert an exporter failure.
Errors require a nonempty error message.

The post-output annotation file contains:

```json
{
  "schema_version": 1,
  "candidate_commit": "FULL_FROZEN_COMMIT",
  "ground_truth_sha256": "SHA256_OF_LABEL_FILE",
  "run_manifest_sha256": "SHA256_OF_RUN_MANIFEST",
  "review": {
    "ai_only": true,
    "performed_after_outputs": true,
    "ground_truth_unchanged": true,
    "reviewer": "AI review identifier"
  },
  "cases": [{
    "id": "CASE_ID",
    "status": "resolved",
    "evidence_sha256": "HASH_OR_NULL_IF_NO_ARTIFACT",
    "legacy_sha256": "PAYLOAD_HASH_OR_NULL_IF_NO_ARTIFACT",
    "evidence_matches": [{
      "result_id": "FINDING_ID",
      "ground_truth_ids": ["FROZEN_BLOCKER_ID"],
      "classification": "matched",
      "rationale": "Explicit explanation against the frozen source facts."
    }],
    "legacy_matches": []
  }]
}
```

The scorer rejects altered hashes, missing/extra/duplicate case IDs, duplicate JSON
keys, duplicate blocker IDs, mismatched verdicts between artifacts, unresolved review
records, and conflicting adjudication. These checks establish consistency of the
retained records, not cryptographic proof of when a reviewer saw information. All
reports identify their source hashes and repeat that review is AI-only. Small fixed
samples do not establish general accuracy, customer outcomes, or release approval.
