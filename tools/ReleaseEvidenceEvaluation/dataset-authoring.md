# Synthetic release-evidence dataset authoring record

Status: **30 synthetic raw-response scenarios frozen for blind review; no candidate outputs were inspected by the author.**

The author was assigned an independent corpus for the [release-evidence evaluation plan](../../docs/benchmarks/release-evidence-evaluation-plan.md). All cases are synthetic. They are not public captures, customer evidence, user research or measurements of a real repository. Every repository, PR, account, CI job and commit identity is fabricated. The repeated URL `https://github.com/eval/fixture/pull/1` is a scenario identifier, not a claim about that live URL. Separate snapshots use independent 40-character hexadecimal commit identities.

## Independence and scope

The author read the evaluation plan, `src/Ashlar.Bricks.GitHub/GitHub/GitHubRuleDtos.cs` for wire shapes, official GitHub documentation and the replay agent's endpoint contract. The author did not read production collector/decision code, existing tests, prior benchmark cases or candidate results. The baseline supplied by the coordinator was `47a7c8506b89670d977265ce342a962d35858cd5`; this dataset is not a derivative of its tests.

These files contain factual scenario intent and raw response material. They contain no expected legacy verdict, expected assurance, expected blocker list or scoring rule. The allocation category identifies the evaluation plan's scenario family. Independent reviewers must derive and freeze the expected labels from the exact responses before viewing candidate results. The author did not assign those labels.

No token values, PR bodies, review bodies, code, patches, artifact payloads or private/customer captures were retained. Adversarial strings in e27/e28 are inert synthetic metadata for rendering evaluation. Official documentation URLs are reference sources; fabricated source URLs inside API responses remain untrusted fixture data.

## Wire format and replay contract

Each `cases/*.json` file is a self-contained snapshot with:

- `schemaVersion`, `id`, `category`, `kind: "synthetic"`, factual `intent`, and `target` URL string.
- `credentialSupplied`, sanitized authorization assumptions, initial/final `identity`, documentation `sources`, and an `endpointInventory` with response types/statuses.
- A `collectionInterval` marked as a synthetic scenario clock. Its four seconds are **not** measured API, installation, build or user task duration. Replay records its actual timestamps separately.
- `routes`, each with an HTTP method, API `path`, and ordered `responses`. The first response serves the first matching request, subsequent responses advance in order, and the last response repeats.
- Responses use a numeric `status`, response `headers`, and exactly one of JSON `body`, exact `rawBody` text, or `fault: "timeout"`. `status: 0` with a timeout is a transport-fault sentinel: no HTTP status was received. Malformed/truncated JSON is deliberate only in e21 and e22.

The runner matches the complete path/query first. A route with no `?` may match that path with pagination/default query arguments. The explicit e22 page2 route must take precedence over its path-only first-page route. The rules route deliberately records `?per_page=100`. An unmatched endpoint is a replay failure and must not be silently converted into an API denial. Request/response transcripts should record unused responses and missing route requests.

The endpoint contract covers PR identity (initial and final), reviews, head-specific check runs, head-specific combined status, classic status/review policy, repository branch rules, optional branch details, and GraphQL. GraphQL uses the DTO aliases `pull_request`, `review_decision`, `status_check_rollup`, `type_name`, and `is_required`. GitHub's review decision enum has APPROVED, REVIEW_REQUIRED and CHANGES_REQUESTED; absent review decisions are null. Empty combined-status responses use GitHub's `pending` aggregate state. Their empty arrays must be interpreted in the context of the configured policy, rather than treated as an invented check.

Unbound mandatory contexts explicitly retain **both** `app_id: null` and `integration_id: null`. Bound contexts retain their synthetic app ID. Classic branch protection absence uses HTTP404 `Branch not protected`; unreadable repository/PR resources use HTTP404 `Not Found`. Policy permission denial, CI/review denial, primary rate exhaustion, authentication unavailability, malformed payloads and timeouts remain distinct.

## Scenario inventory

| Case | Allocation family | Raw scenario |
|---|---|---|
| e01 | Documented scope | No configured requirements |
| e02 | Documented scope | Successful required external CircleCI status |
| e03 | Documented scope | Two current-head approvals; GitHub APPROVED |
| e04 | Documented scope | Successful required CI with failed, neutral and pending optional CI |
| e05 | Observed blockers | Required check failure |
| e06 | Observed blockers | PR merge conflict |
| e07 | Observed blockers | Draft PR with successful required external status |
| e08 | Observed blockers | Review threshold, code-owner and last-push conditions; GitHub REVIEW_REQUIRED |
| e09 | Observed blockers | A reviewer's later changes request supersedes approval |
| e10 | Observed blockers | Behind base with strict required status policy |
| e11 | Pending/missing | Queued required check |
| e12 | Pending/missing | Missing context required by a repository ruleset |
| e13 | Pending/missing | Conflicting same-name check runs from separate apps |
| e14 | Pending/missing | Running required check without conclusion |
| e15 | Authorization | Anonymous REST with unavailable GraphQL |
| e16 | Authorization | Visible PR/checks but classic and ruleset policy denied |
| e17 | Authorization | Unreadable private PR returns Not Found |
| e18 | Authorization | Required checks and reviews unreadable |
| e19 | API/partial | Primary rate exhaustion while reading branch rules |
| e20 | API/partial | Reviews endpoint transport timeout |
| e21 | API/partial | HTTP200 with malformed policy JSON |
| e22 | API/partial | Check-run pagination with truncated second-page JSON |
| e23 | Freshness | Head changes and new-head checks are absent |
| e24 | Freshness | Protected base advances while head stays constant |
| e25 | Freshness | Approval records another commit, stale dismissal disabled |
| e26 | Freshness | CI and reviews change without head/base SHA changes |
| e27 | Hostile text | Workflow command, Markdown and unsafe check source URL |
| e28 | Hostile text | Malformed PR title, head-ref text and unsafe source URL |
| e29 | Unsupported/own check | App-bound context plus production-deployment rule |
| e30 | Unsupported/own check | Collection job's own check is mandatory |

The allocation is exactly 4 documented-scope, 6 observed-blocker, 4 pending/missing, 4 authorization, 4 API/partial, 4 freshness, 2 hostile-text and 2 unsupported/own-check scenarios, totaling **30**. Providers include synthetic GitHub Actions checks and CircleCI, Buildkite and Jenkins status contexts. Policies include classic protection and repository rulesets, review counts zero/one/two, code-owner/last-push conditions, strict base checks and app binding. PR metadata varies commit count and changed-file counts. This synthetic breadth is not a claim of representative real-world coverage.

## Time-dependent and incomplete observations

In e23/e24 the PR endpoint's second response changes head/base identity. Subsequent reads repeat the new identity. e23 additionally supplies explicit routes for the new head, preventing a transport mismatch if the client recollects.

In e26 checks, reviews and GraphQL each have an initial and later response. The final PR read has the same commit identities and a changed `updated_at`/merge state. The later response facts are scenario ground truth even if a client never rereads them. Reviewers and the results report must distinguish a defect in reported certainty from facts the request transcript never observed; no reread is invented by the fixture transport.

In e17 no PR identity is observable under the supplied authorization context, so initial/final identity is null. Other head-shaped routes in that case are defensive negative routes, not an assertion that the unreadable PR disclosed a head. In e29 no deployment response is supplied: the mandatory deployment rule is visible, while deployment satisfaction is not established by this corpus.

The e22 first page reports two total check runs and links a second page. GraphQL lists the check names and requirement flags, while the second REST page's conclusion is unavailable because its JSON truncates. A visible policy or name does not restore the missing check-result bytes.

## Documentation sources consulted

Official GitHub API and policy references were read on 2026-11-02. Per-case `sources` retain relevant URLs. No synthetic repository was queried.

- [Pull request API](https://docs.github.com/en/rest/pulls/pulls#get-a-pull-request), [check runs API](https://docs.github.com/en/rest/checks/runs#list-check-runs-for-a-git-reference), [combined commit status API](https://docs.github.com/en/rest/commits/statuses#get-the-combined-status-for-a-specific-reference), and [reviews API](https://docs.github.com/en/rest/pulls/reviews#list-reviews-for-a-pull-request).
- [Classic status protection](https://docs.github.com/en/rest/branches/branch-protection#get-status-checks-protection), [classic review protection](https://docs.github.com/en/rest/branches/branch-protection#get-pull-request-review-protection), [rules for a branch](https://docs.github.com/en/rest/repos/rules#get-rules-for-a-branch), and [branch metadata](https://docs.github.com/en/rest/branches/branches#get-a-branch).
- [Protected branch policy semantics](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches), [GraphQL review decision enum](https://docs.github.com/en/graphql/reference/enums#pullrequestreviewdecision), [pagination](https://docs.github.com/en/rest/using-the-rest-api/using-pagination-in-the-rest-api), and [rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api).

The documentation describes required status results (success/skipped/neutral), strict up-to-date requirements, app binding, stale-review handling, last-push review policy and deployment rules. It does not supply this corpus's expected Ashlar labels.

## Structural verification and frozen byte inventory

Author-only structural validation read these new files. It verified 30 parseable case documents, the required allocation, unique route keys per case, response representation exclusivity, forty-hex identity shapes, identities independent across cases, explicit null/bound provider fields, and absence of expected labels and excluded payload keys. It did not execute Ashlar or inspect its results. There are 273 routes and 307 scripted response entries, including repeated identity reads and later-fact response sequences.

The following SHA256 lines freeze the exact UTF-8 case-file bytes. The dataset digest is SHA256 over these sorted lines, each followed by LF. After this freeze, any correction must preserve the previous bytes/hash, record the reason and obtain renewed blind review for the changed case before candidate execution. No silent fixture repair is permitted.

Dataset digest: `6327defe56d0ad1d7f7d6f60e44b8d9837638e555a492920246cb6401715a6d9`.

```text
52c57a0a7a768afc8d85c64c4f5e137f0e4acb89274f1bee640bc7afb01b5402  cases/e01-no-configured-requirements.json
e57a1b04aabd3e977ffa47e718a6000bcecad18c3ce97c13b937f9979c99efbb  cases/e02-required-external-status.json
77346ac74145d689d514bc1ce26ca806a19ee42968bde39b493321fd8cd441ee  cases/e03-two-approvals.json
05523845799d6cde5173f834f406808ebb8011b69f2d5b884bc33ffee5a97a88  cases/e04-mixed-optional-ci.json
f1df5db2c45d0dd664c2dc8b56c8bcee617474e8ed592b8e8b2afa4fdc8560b9  cases/e05-required-check-failure.json
dfcd9df7ed2740c955cee3b61a8b26173405e53ed1abaf7b0186bc2ec6ca5d46  cases/e06-merge-conflict.json
50e93b3f9612731f547be661fc2fa5c5ca361f2ffc24f4fce755a15f860636ee  cases/e07-draft-pr.json
f0a26df85db34516d239f379318b787b6d7ef13491757b5ba17a7d39edfc1e44  cases/e08-review-threshold.json
c08a0799b182ad1d163d4cb635e6ba3f56ea6969d4c215ac40af2c1df0d6499a  cases/e09-changes-requested.json
1b60d1fcb7d95484bf787e7327d4b99a8179187a670c6a24bc3e851dec2524f5  cases/e10-behind-strict-base.json
1eaee85f305726b234a4e504f68237e381dcf692ce52bb275dda5f09a22f464c  cases/e11-queued-check.json
bdae216b83abec009749a5740f435ae98c65eacabb70bbbb6eb49af8faaca4aa  cases/e12-missing-rule-context.json
61ba8d11cad00a01adaf41333d3d522917052854f26c83fc93108ad1dfc4bb8c  cases/e13-conflicting-same-name.json
f52c503d9439f3c5d610e28529921b56a33cf32ed49bad4df2381797140f4f5f  cases/e14-running-no-conclusion.json
961b076d2cd3c4e143ff68fa6a4b3d8a668f1f235d606216526ed6d2c3d6d29b  cases/e15-anonymous-graphql.json
cf64726d1a4387e0caaded56327be4c83eab3e99ed7c13db26436c8928473d12  cases/e16-policy-permission-denied.json
476a706553b88a11d5ff3ab95293f1966e3d1631b6a70782a8b699a6f0f86a78  cases/e17-private-pr-not-found.json
1100d9625c8813c6022afaa6c46bec46dbb3a9721c814914856d035620cd6d3d  cases/e18-checks-and-reviews-denied.json
f7f35d6eee2cd4e768dde60572b4b889835e042d8169a13ca785fbb30b70a084  cases/e19-rate-limited-policy.json
ff9cd5dad3dfbed2c229c4cd0e59d3ecc619316588a3c08ee6d61983796f5210  cases/e20-review-transport-timeout.json
015f5eec24c28ee0e5f6410a0cc2860442fa1495b46dfac633afc870fbeff242  cases/e21-malformed-policy-json.json
b24cc09240b186d446fb460d0661a4d39637681eadc0009ad0e811dc8dc00180  cases/e22-truncated-second-page.json
f88a57d604e9c121d762c2b25fa98d0aef73b6ed41419bffc761edf02bca35c6  cases/e23-head-moves-during-read.json
2b0b91b34f6d18f05419b27456b59521fd4b2ee47f9942e7421d484583ea8fc9  cases/e24-base-moves-during-read.json
74d257e6eebd6648585be88adc2b8f3b8e7aa6e15d3d75f8e884effc4bb491b0  cases/e25-approval-on-older-commit.json
72afad0c4f020300a97e9051a2ac593bff9fd02443063a55a072e803ab54c08b  cases/e26-facts-change-same-head.json
a3e5ba93c8a8477002bf427e845331db0feefa0a762b3b4645e47724e4ec9a96  cases/e27-hostile-check-metadata.json
8bd8681f7c6a99196d4e6853a92a07bc1ee4c48848662773b4afb6a5afb23fd4  cases/e28-malformed-pr-source-metadata.json
469feda8f2a8d181297c78c89b8d67b00c84e60881239b9da546a42949823604  cases/e29-app-bound-and-deployment-rule.json
6f507bef7ed10911240a46eb4ff70f277aefe60641b4cb67f473e6e5cbcfe27a  cases/e30-required-collection-check.json
```
