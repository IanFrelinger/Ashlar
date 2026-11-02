# Release-evidence evaluation — 2026-11-02

Status: evaluation executed, findings repaired and the unchanged-corpus final run scored.
The final candidate has no false-passing assurance or `ready` verdict on known-blocked cases
in this sample. Blocker coverage remains incomplete, and legacy `ready` still occurs when
policy is unknown. The original candidate did **not** meet the safety target; its results remain retained.
Marketing, design-partner pilot and autonomy holds are unchanged.

This is a 36-case development evaluation with two separately contextualized AI reviewers,
not a human study, customer pilot, regulatory certification, or estimate of production accuracy.

## Frozen design and independent review

Candidate: `47a7c8506b89670d977265ce342a962d35858cd5`. The corpus contains **30 synthetic
snapshots** in the allocation preregistered by the [evaluation plan](release-evidence-evaluation-plan.md),
plus **6 real public PR captures across 3 repositories**. Public capture used a single HTTP
transport, retained actual statuses/pagination and bracketed head/base identity, and excluded
bodies, patches, code, artifact payloads and unnecessary profile data. Both required-policy
subresources returned 403 in all six public cases; successful GraphQL and rule reads do not
establish the caller's identity or full token scopes. No Authorization header was supplied by
the capture code; proxy upstream authentication was not observable.

The two reviewers read raw facts and GitHub's documented semantics without reading the
collector, existing unit-test answers, candidate outputs or each other's labels. Their original
files were frozen before cross-review. They agreed on 36/36 operational-readiness labels and
35/36 assurance labels. One disagreement, a timed-out review inventory with an independently
approved GitHub review decision, was resolved to operational `ready` / evidence assurance
`unknown`; both original rationales remain. Other fact/denominator clarifications are recorded.
The resolved truth has 7 ready, 20 blocked and 9 unknown cases; 6 pass, 20 fail and 10 unknown
assurance cases; and 26 distinct confirmed blocker instances.

Only after adjudication were producer outputs generated. A separate post-output review maps
actual blocker assertions to the already frozen labels; the scorer does arithmetic and hash
validation, not policy inference. Same-model-family correlation remains possible. Hashes
establish retained-byte consistency, not independent proof of reviewer blinding chronology.

Pre-execution evidence is committed at `99072a4af`; the failing baseline at `1b8c36ba9`.
See the [input manifest](../../tools/ReleaseEvidenceEvaluation/input-manifest.json),
[pre-output freeze](../../tools/ReleaseEvidenceEvaluation/review/pre-execution-freeze.json),
[adjudication](../../tools/ReleaseEvidenceEvaluation/review/adjudication.json),
[scoring contract](../../tools/ReleaseEvidenceEvaluation/SCORING.md), and
[public provenance](../../tools/ReleaseEvidenceEvaluation/public-capture.md).
Frozen adjudicated labels SHA256:
`85b97df9b1b08dfe1585855b684324171f0ebec50ea3eac4902805de0db5c3e1`.

## Original and final results

Original candidate: `47a7c8506b89670d977265ce342a962d35858cd5`.
Final candidate: `b4b77b7f47975c64873dd8dc458329a584228d36`.

| Metric | Original | Final |
|---|---:|---:|
| Answered / all cases | 35 / 36 | 36 / 36 |
| False-passing assurance / expected nonpassing cases | 1 / 30 | 0 / 30 |
| Legacy `ready` / known-blocked cases | 3 / 20 | 0 / 20 |
| Legacy `ready` / unknown-readiness cases | 5 / 9 | 4 / 9 |
| Collector errors / all cases | 1 / 36 | 0 / 36 |
| Assurance unknown / all cases | 14 / 36 | 14 / 36 |
| Missed confirmed blockers, evidence fail surface | 9 / 26 | 8 / 26 |
| Incorrect blocker assertions, evidence surface | 2 / 30 assertions | 0 / 30 assertions |
| Unassessable blocker assertions, evidence surface | 1 / 30 assertions | 1 / 30 assertions |
| Missed confirmed blockers, legacy surface | 6 / 26 | 5 / 26 |
| Incorrect blocker assertions, legacy surface | 1 / 22 assertions | 1 / 23 assertions |
| Unassessable blocker assertions, legacy surface | 1 / 22 assertions | 1 / 23 assertions |

The [baseline score](../../tools/ReleaseEvidenceEvaluation/results/baseline-47a7c8506/score.md)
and [final score](../../tools/ReleaseEvidenceEvaluation/results/final-b4b77b7f4/score.md)
retain every case, unknown, error and disagreement; their JSON counterparts contain denominators
and case IDs. There were no harness or export failures in either run. Collector errors remain
in denominators and never become synthetic unknown answers.

Observed failures include a valid queued-check response causing an Octokit null-timestamp
exception; malformed API-owned PR identity metadata receiving passing assurance; stale initial
GitHub summaries remaining `ready` after observed PR changes; and a failed optional provider
being asserted as the blocker for a requirement bound to another provider. Pending/missing
requirements and unavailable later facts also expose evidence-coverage gaps. An unknown finding
can safely prevent a release gate from passing while still missing a confirmed blocker under
the preregistered literal-fail-assertion coverage metric; these are separate outcomes.

## Repairs, remaining gaps and unchanged-corpus reruns

The nullable-timestamp failure was isolated to Octokit 13.0.1's full check-run deserializer.
The collector now reads a narrow nullable DTO through the same connection, with bounded,
repository/commit-bound pagination, duplicate-run and wrong-head detection, and explicit completeness.
Malformed PR identity cannot pass assurance; a stale positive becomes legacy `unknown`.
Unverified providers cannot establish failed requirements in scoped assurance. Proven required
pending or missing checks fail when policy, origin and completeness are established.

The [first repaired run](../../tools/ReleaseEvidenceEvaluation/results/repaired-7bf31595b/score.md)
at `7bf31595b7b67940f62441ecb29d132c1561d0ec` is also retained. Post-output review found a legacy
absence claim after a later page failed. The final small fix reports that absence as unknown
and preserves observed blockers. Both reviewers checked the final outputs again: only e22 changed
semantically from the first repair. Original labels, raw facts, baseline outputs and scoring
definitions are unchanged. Every rerun records its actual commit, explicit repaired-rerun mode
and new post-output matching against the same labels.
The original wrapper needed a post-output ownership/status-handling correction when a collector
returned nonzero; its amendment and regression proof are separate from producer accuracy.

The eight remaining evidence misses are three app-bound failures in public-01, one app-bound
pending check in public-06, and four later blockers in e23/e24/e26 after observed head, base or
mergeability changes. The collector retains the original snapshot and asks for revalidation;
it does not recollect later-head checks/reviews or independently verify required apps. All five
affected cases have nonpassing assurance. Global provider-known tracking also loses precision
when only some required contexts have app restrictions. These are coverage gaps, not passes.

The legacy surface still incorrectly attributes e29's optional-provider failure to a requirement
bound to another app. Its name-based semantics remain for compatibility; **use assurance for gating**.
Four unknown-policy cases still say legacy `ready` with assurance `unknown`. e13's provider-specific
failure attribution is unassessable on both surfaces; the evidence report separately identifies
GitHub's generic merge block. More generally, when policy is unreadable and GitHub cannot establish
mergeability, an observed optional failure can still be treated as a blocker. This inherited
false-fail risk was found in source review outside this fixed sample and is not counted as a
measured case. Provider-aware classification and bounded recollection are the next useful accuracy work.

## Timing observations

A fresh application-cache run of the real source-built MCP host and Action client produced both
artifacts for a new public Ashlar PR #900 recollection in **82.668 seconds**, including container
startup, local clone, restore, build, collection and teardown. Inside that run: restore 21.479s,
build 40.219s, client/host collection 5.822s; the pack's API interval was 4.908s. This timing-only
recollection is outside the frozen accuracy corpus. Its observed verdict/assurance were blocked/fail.

The SDK and Docker image were already installed/cached; SDK/image download time is not measured.
The NuGet package and container HTTP caches were new and empty, but remote proxy/CDN and OS
caches, machine sharing and scheduling were uncontrolled. A separate cold-profile build observed
22.370s restore plus 79.304s build. These are limited observations, not customer productivity
savings or a general installation-time promise. Exact records, logs and instrumentation are in
[measurements](../../tools/ReleaseEvidenceEvaluation/measurements/README.md).

## Reproduce and limits

Use the repository's Linux devtest container, .NET 10.0.403, and the commands in the
[replay README](../../tools/ReleaseEvidenceEvaluation/README.md). Build the actual frozen
production commit and overlay only the harness/inputs. All three recorded accuracy runs used
a session Docker wrapper adding `--network none --memory 6g --security-opt no-new-privileges`;
the replay helper alone does not set those flags. See the README's offline recipe to reproduce that restriction.
Do not substitute live calls for missing routes. The harness preserves request order, actual
Octokit deserialization, source result bytes, pack/renderer behavior and route coverage; it
elides mergeability sleeps. Replay durations are not API latency. A full source/transport probe
and ten transport self-tests precede the verdict run. Rendering and pack schema are checked
separately. The capture and replay sources make privacy omissions and authorization limits explicit.

The corpus is small and deliberately selected, mostly synthetic, with six public PRs all labeled
blocked; it does not establish performance on real fully observed ready PRs, customer workflows,
GitHub Enterprise or every rule/provider. No randomized-population confidence interval or
zero-error guarantee follows. Replayed packs' `collectedAt` marks replay execution, not a fresh
GitHub read; they must not be used to approve a real release.

This phase does not revise the [dogfood hold](../dogfood-scorecard.md), establish operator custody,
complete elapsed reviewed-run criteria, or supply a real human-reviewed loop PR. Durable task
recovery and framework integration remain separate engineering work.
