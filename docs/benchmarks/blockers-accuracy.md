# Accuracy study: "what is blocking this PR?"

Run 2026-10-25 on 44 real pull requests in 5 public repositories, against ground truth computed from GitHub's own
API at a recorded snapshot. Two arms answer the same question from the same recorded data: the `github.blockers`
brick, and Sonnet reading the raw GitHub REST API. The result is published as measured, including where the brick
is wrong. Companion to the [token benchmark](token-benchmark.md), which measured cost, not correctness.

**Result.**

- **Verdicts.** The brick's verdict (blocked, ready, merged or closed) was right on **44 of 44** pull requests.
  The native arm got 42 of 44; its two misses were answers truncated at the output cap, not wrong answers.
- **Blockers found.** The brick named **21 of 111** true blockers and the native arm **52 of 111**. Most of the
  gap is two blocker kinds the brick does not report: a missing required review (0 of 24) and required checks
  that never ran (1 of 67). On every other kind it found all of them: draft, conflict, behind, changes
  requested, and a failing required check, 20 of 20.
- **Wrong claims.** The brick made **16 overcalls**: a check that is failing or pending but not required, named
  as a blocker. These were on 10 of the 35 open pull requests. It also made 4 vague "blocked by an unidentified
  branch rule" claims. Neither arm made a fabricated claim, meaning one the snapshot contradicts. The native arm
  made 0 overcalls.
- **Cost.** The brick arm cost **$0.0063** per answer (median) and the native arm **$0.094**.

The brick misses and overcalls are product bugs, filed below under [Brick findings](#brick-findings) with the
pull request URLs and the cause. All three share one root: the brick learns which checks and approvals are
required only from branch protection, which needs admin rights, and GitHub publishes the same facts without
them.

**Scale pass.** The same 1.1.0 brick was graded brick-raw on a third snapshot of **100** pull requests in **10**
public repositories (including `IanFrelinger/Ashlar` and other .NET-heavy hosts): **100/100** verdicts, **211/211**
blockers, no wrong claims, **$0** Anthropic spend. See [blockers-accuracy-scale.md](blockers-accuracy-scale.md).

**After the fix.** `github.blockers` 1.1.0 ([#874](https://github.com/IanFrelinger/Ashlar/pull/874)) reads those
facts from the non-admin sources. It was re-run on a second snapshot of the same 44 pull requests, taken 2026-10-25
about 18:00 UTC, with the old and new bricks asked back to back:

- Verdicts were right on 44 of 44 for both bricks.
- Blockers found: **109 of 109** for 1.1.0, against 19 of 109 for 1.0.0.
- Wrong claims: none for 1.1.0, against 13 overcalls and 6 unidentified-rule claims for 1.0.0.

Read that result with its limit: the fixed brick now reads the same GitHub sources the rubric is computed from.
See [Re-run after the fix](#re-run-after-the-fix).

## Results

Model `claude-sonnet-5-5`, one run per pull request per arm (88 runs). Prices: $2 per million input tokens,
$10 per million output tokens; a cache write costs 1.25× input and a cache read 0.05× input.

| Answer graded | Verdict right | Blockers found | Overcalls | Fabricated | Unattributed | Unparsed | Cost (total) |
|---|---|---|---|---|---|---|---|
| brick, raw payload | 44 / 44 | 21 / 111 | 16 | 0 | 4 | 0 | $0 |
| brick + Sonnet | 44 / 44 | 21 / 111 | 16 | 0 | 4 | 0 | $0.2726 |
| native + Sonnet | 42 / 44 | 52 / 111 | 0 | 0 | 0 | 2 | $4.6124 |

*brick, raw payload* grades the brick's own JSON, mapped to blocker keys by its fixed reason sentences
(`score.py`, `BRICK_PATTERNS`). *brick + Sonnet* grades Sonnet's final answer after one brick call. It is
identical to the raw payload on every pull request: the model relayed the brick and added nothing.

Blockers found, by kind (the 111 true blockers on the 28 blocked pull requests):

| Kind | Truth | Brick | Native |
|---|---|---|---|
| draft | 3 | 3 | 3 |
| conflict | 7 | 7 | 5 |
| behind | 5 | 5 | 5 |
| changes_requested | 4 | 4 | 4 |
| check_failing (required) | 1 | 1 | 1 |
| review_required | 24 | 0 | 18 |
| check_pending (required) | 67 | 1 | 16 |

66 of the 67 `check_pending` blockers are required checks with **no run at all** on the head commit. They
appear in two situations: a pull request with conflicts, where `pull_request` workflows cannot run, and
workflows that wait for maintainer approval or run only in a merge queue. They sit on 13 pull requests,
and three efcore pull requests account for 36 of them, so the per-blocker totals weight them heavily. Per
pull request, every blocker was found on 3 of the 28 blocked pull requests by the brick and on 14 by the
native arm.

The native arm's two conflict misses are its two unparsed answers: [dotnet/efcore#39400] and [dotnet/efcore#39391].
In each it listed about 12 checks, hit the 2,048-token output cap before closing its JSON, and is scored as no
answer.

Per-run cost and time (median): brick $0.0063, 5.8 s, 1 tool call; native $0.094, 12.5 s, 6 tool calls. Total
API spend for this study, including a smoke run: **$5.10** (budget $6).

## Brick findings

Each finding is a product bug in `github.blockers` 1.0.0 (`src/Ashlar.Bricks.GitHub/GitHub/BlockerAnalysis.cs`),
reproduced from the recorded payloads in `tools/blockers-accuracy/fixtures/brick/`.

### F1. Non-required failing or pending checks reported as blockers (16 overcalls, 10 pull requests)

| Pull request | Checks named as blocking, none required |
|---|---|
| [dotnet/efcore#39400] | `Analyze (javascript-typescript)`, `efcore-ci (Build Helix macOS x64)` |
| [cli/cli#13013] | `govulncheck` |
| [dotnet/efcore#39399] | `Maestro auto-merge - Version.Details.props Validation Merge Policy` |
| [modelcontextprotocol/csharp-sdk#1952] | `markdown-link-check` |
| [modelcontextprotocol/python-sdk#3540] | 4 × `checks / test (3.11, …)` (the required `all-green` was also reported, correctly) |
| [dotnet/efcore#39369] | `coverage` |
| [modelcontextprotocol/csharp-sdk#1929] | `build (windows-latest, Release)` |
| [dotnet/efcore#38960] | `coverage` |
| [dotnet/efcore#39407] | `efcore-ci (Build Helix)`, `efcore-ci (Build Windows)`, `Maestro auto-merge - All Checks Successful` (pending) |
| [microsoft/semantic-kernel#14641] | `license/cla` (pending) |

**Cause.** Without admin rights the brick cannot read branch protection. When `mergeable_state` is `blocked`, it
then lists every failing or pending check as a blocker, and says so in an unknown ("every failing or pending
check is listed as a blocker"). On these pull requests the real block was a missing review, so the list was
wrong. **Fix:** read which checks are required from sources that need no admin rights:
- GraphQL `isRequired(pullRequestNumber:)` on each check context;
- the public protection summary on `GET /repos/{o}/{r}/branches/{base}` (`protection.required_status_checks`);
- `GET /repos/{o}/{r}/rules/branches/{base}` (the `required_status_checks` rules).

These three sources agreed on every pull request in this sample where more than one applied.

### F2. A missing required review is never reported as a blocker (0 of 24)

On all 24 pull requests where GitHub's `reviewDecision` was `REVIEW_REQUIRED`, the brick reported "Required
approvals" as an unknown ("Branch protection is not readable with this token… the pull request has 0
approving review(s)"), not as a blocker. Examples: [modelcontextprotocol/csharp-sdk#1934], [cli/cli#14816],
[dotnet/efcore#39405], [microsoft/semantic-kernel#14661]; the full list is in the per-PR table below. **Cause:**
the brick reads the approval count only from branch protection. **Fix:** GraphQL `reviewDecision`, or the
`pull_request` rule's `required_approving_review_count` from `rules/branches/{base}`. The native arm found 18 of
the 24 from the REST responses. On [modelcontextprotocol/csharp-sdk#1934] the missing review was the only
blocker, and the brick's whole answer was "blocked by a branch rule this read could not identify".

### F3. Required checks that never ran are missed, or reported as an unidentified rule (0 of 66 found)

On [cli/cli#14816], [dotnet/efcore#39405] and [microsoft/semantic-kernel#14661], the brick's only blocker was
"GitHub reports merging as blocked by a branch rule this read could not identify". GitHub names the rule
publicly: for example, cli/cli requires `build (macos-latest)`, `build (ubuntu-latest)` and
`build (windows-latest)`, and none had run on #14816's head commit. On the other 10 pull requests with
never-run required checks, a draft, conflict, behind or changes-requested blocker was present, so the brick
reported that and nothing about the checks. **Cause:** the brick sees only checks that reported. **Fix:**
the same public required-check list as F1, with every required check that has no run reported as "has not
reported". The brick already has that sentence, but uses it only when protection is readable.

Fixing F1–F3 needs one change to the reader: take required checks and approvals from the public sources when
`branches/{base}/protection` returns 404 or 403.

**Status: fixed in `github.blockers` 1.1.0** ([#874](https://github.com/IanFrelinger/Ashlar/pull/874)). The fix
has regression tests built from this snapshot's facts for dotnet/efcore#39369, cli/cli#14816 and
modelcontextprotocol/csharp-sdk#1934, and each finding was mutation-checked. The re-run is below.

## Re-run after the fix

A second snapshot of the same 44 pull requests was captured in CI on 2026-10-25, run `40218094404`, at about
18:00 UTC. For each pull request the capture recorded the truth facts, then asked the fixed brick (1.1.0, #874's
code), then asked the old one (1.0.0, master `3a03598e7`), back to back. Both bricks used the same token and saw the
same pull request state. Pull requests had moved since the first snapshot: the truth has 109 blockers on 28 blocked
pull requests, against 111 before. Grading uses the same rubric and `score.py`.

| Answer graded | Verdict right | Blockers found | Overcalls | Fabricated | Unattributed | Every blocker found | Cost (total) |
|---|---|---|---|---|---|---|---|
| brick 1.0.0, raw payload | 44 / 44 | 19 / 109 | 13 | 0 | 6 | 3 of 28 PRs | $0 |
| brick 1.1.0, raw payload | 44 / 44 | **109 / 109** | **0** | 0 | 0 | **28 of 28 PRs** | $0 |
| brick 1.1.0 + Sonnet | 44 / 44 | 109 / 109 | 0 | 0 | 0 | 28 of 28 PRs | $0.3287 |

By kind, brick 1.1.0 found every one: missing required review 24/24, required check with no run 66/66, conflict
6/6, behind 5/5, changes requested 4/4, draft 3/3, failing required check 1/1. Its non-required failing checks are
now notes, for example `coverage` on [dotnet/efcore#39369] and `govulncheck` on [cli/cli#13013].

**Limit on this result.** Brick 1.1.0 now reads the GitHub sources the rubric is computed from: GraphQL
`reviewDecision` and `isRequired`, and the public protection summary and rulesets. Full agreement therefore shows
two things. The brick reads and applies those facts without error, and it no longer overcalls or misses on these
44 pull requests. It is not independent evidence that GitHub's merge box would list exactly these blockers. The
native arm was not re-run; its numbers above are from the first snapshot and are not comparable with this table.

Spend for the re-run: $0.3287, against a $1 budget. The key was used as an environment variable only and unset
after the run. Raw files: `tools/blockers-accuracy/fixtures-rerun/` (truth, `brick/` for 1.1.0, `brick-old/` for
1.0.0, REST responses), `results/2026-10-25-rerun/` and `results/2026-10-25-rerun-old/`.

## Rubric

Ground truth is computed by `tools/blockers-accuracy/truth.py` from the snapshot, deterministically. Nothing is
inferred from text, and check names are compared case-insensitively.

1. **Verdict.**
   - A merged pull request is `merged`, and a closed one is `closed`, with no blockers.
   - An open pull request with one or more blockers below is `blocked`.
   - An open pull request with no blockers is `ready` if GraphQL `mergeStateStatus` is `CLEAN`, `UNSTABLE` or
     `HAS_HOOKS`, and `unknown` otherwise. If the state is `BLOCKED` with no blocker found, the blocker is
     `other`.
2. **Blockers of an open pull request:**
   - `draft`: `isDraft`.
   - `conflict`: `mergeable` is `CONFLICTING`.
   - `behind`: `mergeStateStatus` is `BEHIND`.
   - `changes_requested`: `reviewDecision` is `CHANGES_REQUESTED`.
   - `review_required`: `reviewDecision` is `REVIEW_REQUIRED`.
   - `check_failing:<name>`: a required check run concluded `FAILURE`, `TIMED_OUT`, `CANCELLED`,
     `ACTION_REQUIRED`, `STARTUP_FAILURE` or `STALE`, or a required commit status is `FAILURE` or `ERROR`.
   - `check_pending:<name>`: a required check run is not completed, a required status is `PENDING` or
     `EXPECTED`, or a required check has no run or status on the head commit.
3. **A check is required** if GraphQL `isRequired(pullRequestNumber:)` is true for it, or it is named in the
   base branch's public protection summary (`GET /branches/{base}`), or in a `required_status_checks` rule from
   `GET /rules/branches/{base}`. The last two are needed because the GraphQL rollup lists only checks that
   reported.
4. **Grading an answer.** A claimed blocker that matches a truth key is **found**; a truth key no claim matches
   is **missed**. A claim with no matching truth key is classified against the snapshot:
   - **overcall**: the fact is true but the rubric says it does not block (a non-required failing check);
   - **fabricated**: the snapshot contradicts it (a check that does not exist or passed, an approved review
     reported as missing);
   - **unattributed**: `other` when the truth has blockers the claim does not name;
   - **unsupported**: anything else.

   A verdict is right when it equals the truth verdict. An answer whose final JSON does not parse has no
   verdict and no claims.

The rubric's first version used only `isRequired`. That version scored 15 native claims as "fabricated" that were
real required checks with no run, such as cli/cli#14816's three `build` checks. Rule 3 was corrected before
any number in this document was taken.

## Method

- **Pull requests** (`tools/blockers-accuracy/targets.json`): 44, chosen on 2026-10-25 from
  `dotnet/efcore` (10), `microsoft/semantic-kernel` (9), `modelcontextprotocol/python-sdk` (9), `cli/cli` (8)
  and `modelcontextprotocol/csharp-sdk` (8). They are stratified by state:
  - blocked by failing CI 6, conflict 5, merged 5, behind 4, changes requested 4, waiting on review 4,
    ready 4, closed 4, draft 3, unstable 3, pending CI 2;
  - the category is a selection label only; grading uses the rubric.
- **One snapshot, captured in CI.** Run `40211730660`, 2026-10-25 16:28–16:34 UTC, used the workflow's
  `GITHUB_TOKEN`, which has no admin rights on these repositories, like a real user's token. For each pull
  request, back to back, `capture.py` recorded:
  - the GraphQL facts the rubric uses;
  - the brick's answer through the real `Ashlar.Mcp.Server.Host`;
  - the REST responses an agent would read: the pull request, reviews, requested reviewers, check runs,
    statuses, branch, protection, rules, issue and repository.

  The head SHA matched between the GraphQL and REST reads on all 44.
- **Same data for both arms.** The run replays the recorded fixtures.
  - The *brick* arm has one tool, `github_blockers(url)`, which returns the recorded brick payload.
  - The *native* arm has one tool, `github_api_get(path)`, which returns the recorded REST body.
  - A native path that was not recorded is fetched live once, recorded, and flagged. 8 of 44 native runs did
    this, during the run, within about 20 minutes of the snapshot. The paths were comments, compares,
    CODEOWNERS files and annotations, none of them a fact the rubric uses.
- **Same prompt.** Both arms use one system prompt, which says that a failing non-required check does not
  block, and require a final JSON answer in a fixed schema (`run.py`, `SYSTEM`). Settings: output cap 2,048
  tokens, at most 12 turns, prompt caching on. Pull requests ran in round-robin order over categories, so a
  budget stop would still leave a paired, stratified sample. None occurred.
- **Key handling.** `ANTHROPIC_API_KEY` was read from the environment only, sent only as a request header,
  masked in any printed error, and unset after the run.

## Limits

- One run per arm per pull request, and one model. Model variance is not measured.
- The snapshot is one moment, and pull requests change. Re-capturing gives different truth, so compare only
  results computed from the same fixtures.
- `review_required` comes from GraphQL `reviewDecision`. A REST-only reader can infer it from the rulesets
  endpoint (the native arm did, 18 of 24), but not from branch protection without admin rights.
- The native arm's 2,048-token cap truncated 2 answers. A larger cap would likely have made both parse. This
  is a harness limit, scored strictly.
- Verdicts are coarse. Every blocked pull request in this sample had an obvious reason GitHub reports as
  blocked, so a 44/44 verdict does not show the blocker list is right. The blocker columns do.
- The 5 repositories are public, large and well maintained, and use GitHub Actions. Private repositories and
  other CI systems are not covered.

## Reproduce

```bash
cd tools/blockers-accuracy
python3 score.py results/2026-10-25        # re-grade the recorded runs; no key, no network
python3 score.py results/2026-10-25-rerun --fixtures fixtures-rerun --brick-dir brick          # brick 1.1.0
python3 score.py results/2026-10-25-rerun-old --fixtures fixtures-rerun --brick-dir brick-old  # brick 1.0.0
```

The harness, recorded inputs and raw runs are in [`tools/blockers-accuracy/`](../../tools/blockers-accuracy/README.md).
`results/2026-10-25/per-pr.md` lists each pull request's truth keys, and every key each answer found, missed or
got wrong.

## Per pull request

Counts are found / missed / extra. "Extra" is an overcall, fabricated, unattributed or unsupported claim; there
were no fabricated or unsupported claims.

| PR | Selected as | Truth | Brick | Native |
|---|---|---|---|---|
| [cli/cli#14196] | draft | blocked: draft, conflict, review_required, check_pending ×3 | 2 / 4 / 0 | 2 / 4 / 0 |
| [dotnet/efcore#39321] | draft | blocked: draft, review_required | 1 / 1 / 0 | 2 / 0 / 0 |
| [microsoft/semantic-kernel#14658] | draft | blocked: draft, review_required, check_pending ×3 | 1 / 4 / 0 | 2 / 3 / 0 |
| [cli/cli#12942] | conflict | blocked: conflict, review_required | 1 / 1 / 0 | 1 / 1 / 0 |
| [dotnet/efcore#39400] | conflict | blocked: conflict, review_required, check_pending ×12 | 1 / 13 / 2 | unparsed · 0 / 14 / 0 |
| [modelcontextprotocol/python-sdk#3516] | conflict | blocked: conflict, review_required | 1 / 1 / 0 | 2 / 0 / 0 |
| [cli/cli#13013] | conflict | blocked: conflict, review_required | 1 / 1 / 1 | 1 / 1 / 0 |
| [dotnet/efcore#39391] | conflict | blocked: conflict, review_required, check_pending ×12 | 1 / 13 / 0 | unparsed · 0 / 14 / 0 |
| [microsoft/semantic-kernel#14659] | behind | blocked: behind, review_required, check_pending ×3 | 1 / 4 / 0 | 2 / 3 / 0 |
| [microsoft/semantic-kernel#14577] | behind | blocked: behind, review_required, check_pending ×3 | 1 / 4 / 0 | 2 / 3 / 0 |
| [microsoft/semantic-kernel#14375] | behind | blocked: behind, review_required, check_pending ×3 | 1 / 4 / 0 | 2 / 3 / 0 |
| [microsoft/semantic-kernel#14643] | behind | blocked: behind, review_required, check_pending ×3 | 1 / 4 / 0 | 2 / 3 / 0 |
| [cli/cli#13899] | changes-requested | blocked: changes_requested | 1 / 0 / 0 | 1 / 0 / 0 |
| [modelcontextprotocol/python-sdk#3681] | changes-requested | blocked: changes_requested | 1 / 0 / 0 | 1 / 0 / 0 |
| [cli/cli#13506] | changes-requested | blocked: changes_requested, check_pending ×3 | 1 / 3 / 0 | 1 / 3 / 0 |
| [modelcontextprotocol/python-sdk#1721] | changes-requested | blocked: conflict, changes_requested | 2 / 0 / 0 | 2 / 0 / 0 |
| [dotnet/efcore#39399] | blocked-failing-ci | blocked: review_required | 0 / 1 / 1 | 1 / 0 / 0 |
| [modelcontextprotocol/csharp-sdk#1952] | blocked-failing-ci | blocked: review_required | 0 / 1 / 1 | 1 / 0 / 0 |
| [modelcontextprotocol/python-sdk#3540] | blocked-failing-ci | blocked: review_required, check_failing | 1 / 1 / 4 | 2 / 0 / 0 |
| [dotnet/efcore#39369] | blocked-failing-ci | blocked: review_required | 0 / 1 / 1 | 1 / 0 / 0 |
| [modelcontextprotocol/csharp-sdk#1929] | blocked-failing-ci | blocked: review_required | 0 / 1 / 1 | 1 / 0 / 0 |
| [dotnet/efcore#38960] | blocked-failing-ci | blocked: review_required | 0 / 1 / 1 | 1 / 0 / 0 |
| [dotnet/efcore#39407] | blocked-pending-ci | blocked: review_required, check_pending | 1 / 1 / 3 | 2 / 0 / 0 |
| [microsoft/semantic-kernel#14641] | blocked-pending-ci | blocked: behind, review_required, check_pending ×3 | 1 / 4 / 1 | 2 / 3 / 0 |
| [cli/cli#14816] | blocked-review | blocked: review_required, check_pending ×3 | 0 / 4 / 1 | 3 / 1 / 0 |
| [dotnet/efcore#39405] | blocked-review | blocked: review_required, check_pending ×12 | 0 / 13 / 1 | 13 / 0 / 0 |
| [microsoft/semantic-kernel#14661] | blocked-review | blocked: review_required, check_pending ×3 | 0 / 4 / 1 | 1 / 3 / 0 |
| [modelcontextprotocol/csharp-sdk#1934] | blocked-review | blocked: review_required | 0 / 1 / 1 | 1 / 0 / 0 |
| [cli/cli#14716] | ready-clean | ready | 0 / 0 / 0 | 0 / 0 / 0 |
| [modelcontextprotocol/csharp-sdk#1949] | ready-clean | ready | 0 / 0 / 0 | 0 / 0 / 0 |
| [modelcontextprotocol/python-sdk#3680] | ready-clean | ready | 0 / 0 / 0 | 0 / 0 / 0 |
| [modelcontextprotocol/csharp-sdk#1709] | ready-clean | ready | 0 / 0 / 0 | 0 / 0 / 0 |
| [modelcontextprotocol/csharp-sdk#1788] | unstable | ready | 0 / 0 / 0 | 0 / 0 / 0 |
| [modelcontextprotocol/python-sdk#3542] | unstable | ready | 0 / 0 / 0 | 0 / 0 / 0 |
| [modelcontextprotocol/python-sdk#3541] | unstable | ready | 0 / 0 / 0 | 0 / 0 / 0 |
| [modelcontextprotocol/csharp-sdk#1955] | merged | merged | 0 / 0 / 0 | 0 / 0 / 0 |
| [modelcontextprotocol/csharp-sdk#1948] | closed | closed | 0 / 0 / 0 | 0 / 0 / 0 |
| [microsoft/semantic-kernel#14649] | merged | merged | 0 / 0 / 0 | 0 / 0 / 0 |
| [microsoft/semantic-kernel#13456] | closed | closed | 0 / 0 / 0 | 0 / 0 / 0 |
| [dotnet/efcore#39350] | merged | merged | 0 / 0 / 0 | 0 / 0 / 0 |
| [dotnet/efcore#39404] | closed | closed | 0 / 0 / 0 | 0 / 0 / 0 |
| [modelcontextprotocol/python-sdk#3758] | merged | merged | 0 / 0 / 0 | 0 / 0 / 0 |
| [modelcontextprotocol/python-sdk#3754] | closed | closed | 0 / 0 / 0 | 0 / 0 / 0 |
| [cli/cli#11926] | merged | merged | 0 / 0 / 0 | 0 / 0 / 0 |

[cli/cli#11926]: https://github.com/cli/cli/pull/11926
[cli/cli#12942]: https://github.com/cli/cli/pull/12942
[cli/cli#13013]: https://github.com/cli/cli/pull/13013
[cli/cli#13506]: https://github.com/cli/cli/pull/13506
[cli/cli#13899]: https://github.com/cli/cli/pull/13899
[cli/cli#14196]: https://github.com/cli/cli/pull/14196
[cli/cli#14716]: https://github.com/cli/cli/pull/14716
[cli/cli#14816]: https://github.com/cli/cli/pull/14816
[dotnet/efcore#38960]: https://github.com/dotnet/efcore/pull/38960
[dotnet/efcore#39321]: https://github.com/dotnet/efcore/pull/39321
[dotnet/efcore#39350]: https://github.com/dotnet/efcore/pull/39350
[dotnet/efcore#39369]: https://github.com/dotnet/efcore/pull/39369
[dotnet/efcore#39391]: https://github.com/dotnet/efcore/pull/39391
[dotnet/efcore#39399]: https://github.com/dotnet/efcore/pull/39399
[dotnet/efcore#39400]: https://github.com/dotnet/efcore/pull/39400
[dotnet/efcore#39404]: https://github.com/dotnet/efcore/pull/39404
[dotnet/efcore#39405]: https://github.com/dotnet/efcore/pull/39405
[dotnet/efcore#39407]: https://github.com/dotnet/efcore/pull/39407
[microsoft/semantic-kernel#13456]: https://github.com/microsoft/semantic-kernel/pull/13456
[microsoft/semantic-kernel#14375]: https://github.com/microsoft/semantic-kernel/pull/14375
[microsoft/semantic-kernel#14577]: https://github.com/microsoft/semantic-kernel/pull/14577
[microsoft/semantic-kernel#14641]: https://github.com/microsoft/semantic-kernel/pull/14641
[microsoft/semantic-kernel#14643]: https://github.com/microsoft/semantic-kernel/pull/14643
[microsoft/semantic-kernel#14649]: https://github.com/microsoft/semantic-kernel/pull/14649
[microsoft/semantic-kernel#14658]: https://github.com/microsoft/semantic-kernel/pull/14658
[microsoft/semantic-kernel#14659]: https://github.com/microsoft/semantic-kernel/pull/14659
[microsoft/semantic-kernel#14661]: https://github.com/microsoft/semantic-kernel/pull/14661
[modelcontextprotocol/csharp-sdk#1709]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1709
[modelcontextprotocol/csharp-sdk#1788]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1788
[modelcontextprotocol/csharp-sdk#1929]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1929
[modelcontextprotocol/csharp-sdk#1934]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1934
[modelcontextprotocol/csharp-sdk#1948]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1948
[modelcontextprotocol/csharp-sdk#1949]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1949
[modelcontextprotocol/csharp-sdk#1952]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1952
[modelcontextprotocol/csharp-sdk#1955]: https://github.com/modelcontextprotocol/csharp-sdk/pull/1955
[modelcontextprotocol/python-sdk#1721]: https://github.com/modelcontextprotocol/python-sdk/pull/1721
[modelcontextprotocol/python-sdk#3516]: https://github.com/modelcontextprotocol/python-sdk/pull/3516
[modelcontextprotocol/python-sdk#3540]: https://github.com/modelcontextprotocol/python-sdk/pull/3540
[modelcontextprotocol/python-sdk#3541]: https://github.com/modelcontextprotocol/python-sdk/pull/3541
[modelcontextprotocol/python-sdk#3542]: https://github.com/modelcontextprotocol/python-sdk/pull/3542
[modelcontextprotocol/python-sdk#3680]: https://github.com/modelcontextprotocol/python-sdk/pull/3680
[modelcontextprotocol/python-sdk#3681]: https://github.com/modelcontextprotocol/python-sdk/pull/3681
[modelcontextprotocol/python-sdk#3754]: https://github.com/modelcontextprotocol/python-sdk/pull/3754
[modelcontextprotocol/python-sdk#3758]: https://github.com/modelcontextprotocol/python-sdk/pull/3758
