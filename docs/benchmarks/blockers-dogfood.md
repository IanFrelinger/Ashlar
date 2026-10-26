# Dogfood: `github.blockers` on Ashlar's own pull requests and releases

Run 2026-10-25. The MCP brick host was built from master at `46d01ab14` (after #868, #872 and #874) and asked
"what is blocking this?" about 11 of Ashlar's own pull requests and 3 of its release tags. Every answer was
then checked against GitHub's own view, and two models turned each answer into a short summary for a person.

**Result.** No answer was wrong. 10 of 14 verdicts matched GitHub exactly. The other 4 were `unknown`: in each,
GitHub had not yet computed whether the pull request could merge, and the brick said so rather than guess.
In all 4 the real blocker was one the brick already knows how to report, a merge conflict (3) or a branch
behind master (1), so they point at one fix, described under [What this found](#what-this-found). No pull
request or release was called `ready` when it was not. Both models summarised all 14 answers without
dropping the verdict or inventing a link, once their default thinking was turned off; Haiku 5.5 did it for
about 1/20 of Sonnet 5.5's cost.

**Scope.** One repository, the maintainer's own, at one moment. GitHub's view was read 35 seconds after the
capture finished; one pull request (#869) got a new commit in between. This is a dogfood record, not an
accuracy study: the study over 44 pull requests in other repositories is
[#871](https://github.com/IanFrelinger/Ashlar/pull/871), run separately.

## Verdicts against GitHub

The brick ran in GitHub Actions with the job's own read-only token, which cannot read branch protection, so
it fell back to the base branch's public required-check lists, as it is designed to. "GitHub's own view" is
`gh pr view --json mergeable,mergeStateStatus,statusCheckRollup` read at 21:54:58 UTC, and for the release tags
the check runs on the tagged commit, read through the API. The required checks on master are `cert-gate`,
`build-core`, `shell-lint`, `lychee (README + docs)` and `Readiness summary`, with strict mode on.

| Target | Brick verdict | Blockers | Unknowns | GitHub's own view | Outcome |
|---|---|---|---|---|---|
| [#877](https://github.com/IanFrelinger/Ashlar/pull/877) | `merged` | 0 | 0 | merged | exact |
| [#876](https://github.com/IanFrelinger/Ashlar/pull/876) | `merged` | 0 | 0 | merged | exact |
| [#875](https://github.com/IanFrelinger/Ashlar/pull/875) | `blocked` | 4 | 1 | conflicting (DIRTY); required checks never started | exact |
| [#873](https://github.com/IanFrelinger/Ashlar/pull/873) | `blocked` | 1 | 1 | conflicting (DIRTY); `shell-lint` failed | exact |
| [#871](https://github.com/IanFrelinger/Ashlar/pull/871) | `unknown` | 0 | 1 | conflicting (DIRTY); required checks green | unknown, not wrong |
| [#870](https://github.com/IanFrelinger/Ashlar/pull/870) | `unknown` | 0 | 1 | conflicting (DIRTY); required checks green | unknown, not wrong |
| [#869](https://github.com/IanFrelinger/Ashlar/pull/869) | `blocked` | 2 | 1 | required checks running on a newer head (`de613cadd596`) | exact |
| [#853](https://github.com/IanFrelinger/Ashlar/pull/853) | `unknown` | 0 | 1 | conflicting (DIRTY); required checks green | unknown, not wrong |
| [#844](https://github.com/IanFrelinger/Ashlar/pull/844) | `unknown` | 0 | 1 | behind master (BEHIND, strict mode); required checks green | unknown, not wrong |
| [#840](https://github.com/IanFrelinger/Ashlar/pull/840) | `blocked` | 3 | 1 | behind master; `build-core`, `cert-gate`, `Readiness summary` failed | exact |
| [#839](https://github.com/IanFrelinger/Ashlar/pull/839) | `blocked` | 3 | 1 | behind master; `build-core`, `cert-gate`, `Readiness summary` failed | exact |
| [v0.3.0](https://github.com/IanFrelinger/Ashlar/releases/tag/v0.3.0) | `ready` | 0 | 0 | 33 checks on the tagged commit, none failing | exact |
| [v0.2.0](https://github.com/IanFrelinger/Ashlar/releases/tag/v0.2.0) | `ready` | 0 | 0 | 83 checks on the tagged commit, none failing | exact |
| [v0.1.2](https://github.com/IanFrelinger/Ashlar/releases/tag/v0.1.2) | `blocked` | 6 | 0 | Release workflow failed (`pack-and-publish`), perf and UAT gates failed on the tagged commit | exact |


Counts: **10 exact, 4 unknown, 0 wrong.** Of the 11 pull requests, 9 were open, and the brick reported
mergeability as not yet computed on all 9. That is GitHub's behaviour, not a read failure: GitHub computes
mergeability in a background job and returns `null` until it finishes, and every merge to master restarts that
job for every open pull request. #876 and #877 had merged within the hour before the capture.

## What a person sees

The brick's `answer` field is what the free [PR blockers Action](../../actions/pr-blockers/README.md) writes to
the job summary. Five of the fourteen, verbatim (all fourteen are in
[`results/2026-10-25-dogfood/brick/`](../../tools/token-benchmark/results/2026-10-25-dogfood/brick/)):

#### #873: `blocked`

> **What is blocking PR #873?** PR #873 is BLOCKED by 1 blocker(s); 1 unknown(s).
>
> Blockers:
> - The check `shell-lint` concluded failure. ([evidence](https://github.com/IanFrelinger/Ashlar/actions/runs/40215216321/job/121234761890))
>
> Unknowns:
> - Mergeability: GitHub had not computed it when asked (it does so in a background job, which can take a while); ask again later, and if it stays unknown, the pull request page shows GitHub's own view.
>
> Notes:
> - The check `verify` concluded failure. It is not required, so it does not block the merge. ([evidence](https://github.com/IanFrelinger/Ashlar/actions/runs/40215216284/job/121234762206))
> - The check `MCP/A2A adapter suites` concluded failure. It is not required, so it does not block the merge. ([evidence](https://github.com/IanFrelinger/Ashlar/actions/runs/40215178199/job/121234653619))
>
> Read from https://github.com/IanFrelinger/Ashlar/pull/873

#### #853: `unknown`

> **What is blocking PR #853?** PR #853: no blocker found, but it is UNKNOWN whether anything blocks it; 1 unknown(s).
>
> Unknowns:
> - Mergeability: GitHub had not computed it when asked (it does so in a background job, which can take a while); ask again later, and if it stays unknown, the pull request page shows GitHub's own view.
>
> Notes:
> - Read 53 check run(s) and 0 commit status(es) on the head commit 8b9fda6a16e3; none is failing or pending. ([evidence](https://github.com/IanFrelinger/Ashlar/commit/8b9fda6a16e3bde4b9051e1ae385e813c0df87f6))
>
> Read from https://github.com/IanFrelinger/Ashlar/pull/853

#### v0.3.0: `ready`

> **What is blocking release v0.3.0?** Release v0.3.0 is READY: nothing blocking was found.
>
> Notes:
> - Read 33 check run(s) and 0 commit status(es) on the tagged commit c0c0287e141d; none is failing or pending. ([evidence](https://github.com/IanFrelinger/Ashlar/commit/c0c0287e141d5cd173d6bc86d67f635443758f47))
>
> Read from https://github.com/IanFrelinger/Ashlar/releases/tag/v0.3.0

#### v0.1.2: `blocked`

> **What is blocking release v0.1.2?** Release v0.1.2 is BLOCKED by 6 blocker(s).
>
> Blockers:
> - The check `Readiness summary` concluded failure. ([evidence](https://github.com/IanFrelinger/Ashlar/actions/runs/33867694826/job/101011341405))
> - The check `NuGet packages / pack-and-publish` concluded failure. ([evidence](https://github.com/IanFrelinger/Ashlar/actions/runs/33867752688/job/101006550765))
> - The check `Windows — setup · discover · dry-run` concluded failure. ([evidence](https://github.com/IanFrelinger/Ashlar/actions/runs/33867694826/job/101006194923))
> - The check `perf-gate` concluded failure. ([evidence](https://github.com/IanFrelinger/Ashlar/actions/runs/33867694863/job/101006194573))
> - The check `uat cross-platform (tiers 8, 9)` concluded failure. ([evidence](https://github.com/IanFrelinger/Ashlar/actions/runs/33867694772/job/101006194261))
> - The check `uat (tiers 0-2, 4-10)` concluded failure. ([evidence](https://github.com/IanFrelinger/Ashlar/actions/runs/33867694772/job/101006194059))
>
> Read from https://github.com/IanFrelinger/Ashlar/releases/tag/v0.1.2

#### #876: `merged`

> **What is blocking PR #876?** PR #876 is already merged.
>
> Read from https://github.com/IanFrelinger/Ashlar/pull/876


## Summaries for a person

Each brick answer (its `outputs` object) was sent once to `claude-haiku-5-5` and once to `claude-sonnet-5-5`,
with the same system prompt: two to four plain sentences, the verdict word as given, each blocker in a few
words, at most two evidence URLs copied exactly, and nothing that is not in the JSON. Every summary was then
checked mechanically for the verdict word and for any URL the brick did not return, and read in full by hand.

| Model | Summaries | States the verdict | Invented or broken URLs | Cost of all 14 (USD) |
|---|---|---|---|---|
| `claude-haiku-5-5` | 14 | 14 | 0 | 0.0037 |
| `claude-sonnet-5-5` | 14 | 14 | 0 | 0.0795 |
| `claude-haiku-5-5`, default thinking | 14 | 12 | 4 | 0.0049 |

Prices: Haiku 5.5 $0.10 / $0.50 per million input / output tokens, Sonnet 5.5 $2 / $10, no caching.

**Thinking has to be off for this job.** Both 5.5 models think by default. With a 400-token limit, Haiku spent
up to all 400 tokens thinking on 6 of the 14 answers and returned a summary cut off mid-sentence (one was
empty); those are the 2 missing verdicts and 4 broken URLs in the last row, kept in
[`haiku-default-thinking.jsonl`](../../tools/token-benchmark/results/2026-10-25-dogfood/haiku-default-thinking.jsonl).
Turning thinking off (`"thinking": {"type": "disabled"}` on Haiku 5.5; Sonnet 5.5 refuses `disabled` and takes
`{"type": "between_tools"}`) fixed all of them, and the summaries got cheaper.

Reading them by hand found one sentence in 28 that goes beyond the JSON: Haiku on v0.1.2 suggests starting
with `Readiness summary` "since it may summarize the others", which is a guess. Everything else is a
restatement. The same five targets as above, both models:

**#873**

- *Haiku 5.5:* PR #873 is blocked by a failing required check, `shell-lint`, which you can inspect at https://github.com/IanFrelinger/Ashlar/actions/runs/40215216321/job/121234761890. Mergeability is still unknown because GitHub had not finished computing it, so ask again later, and if it stays unknown, check GitHub's own view on the pull request page. The failing `verify` and `MCP/A2A adapter suites` checks are not required and do not block the merge.
- *Sonnet 5.5:* PR #873 is blocked because the `shell-lint` check failed (https://github.com/IanFrelinger/Ashlar/actions/runs/40215216321/job/121234761890). Mergeability is also unknown, since GitHub had not computed it yet; ask again later, and if it stays unknown, check the pull request page for GitHub's own view. The `verify` and `MCP/A2A adapter suites` checks also failed, but neither is required, so they do not block the merge.

**#853**

- *Haiku 5.5:* The verdict for PR #853 is unknown: no blockers were found, but GitHub had not yet computed mergeability, so it is unclear whether anything blocks the merge. All 53 check runs on the head commit are passing or not pending, with none failing. To resolve the unknown, ask again later, and if it stays unknown, check GitHub's own view on the pull request page at https://github.com/IanFrelinger/Ashlar/pull/853.
- *Sonnet 5.5:* The verdict for PR #853 is unknown: no blockers were found, but GitHub had not yet computed mergeability when asked. That calculation runs as a background job, so ask again later, and if it stays unknown, check the pull request page for GitHub's own view. On the head commit, 53 check runs and 0 commit statuses were read, and none is failing or pending. Evidence: https://github.com/IanFrelinger/Ashlar/pull/853 and https://github.com/IanFrelinger/Ashlar/commit/8b9fda6a16e3bde4b9051e1ae385e813c0df87f6

**v0.3.0**

- *Haiku 5.5:* Release v0.3.0 is ready, with no blockers or unknowns found. All 33 check runs on the tagged commit c0c0287e141d are passing or complete, with none failing or pending, and there are no commit statuses to consider. The commit is at https://github.com/IanFrelinger/Ashlar/commit/c0c0287e141d5cd173d6bc86d67f635443758f47 and the release at https://github.com/IanFrelinger/Ashlar/releases/tag/v0.3.0.
- *Sonnet 5.5:* Release v0.3.0 is ready, with no blockers and nothing unknown. The tool read 33 check runs and 0 commit statuses on the tagged commit c0c0287e141d, and none is failing or pending. Evidence: https://github.com/IanFrelinger/Ashlar/releases/tag/v0.3.0 and https://github.com/IanFrelinger/Ashlar/commit/c0c0287e141d5cd173d6bc86d67f635443758f47

**v0.1.2**

- *Haiku 5.5:* Release v0.1.2 is blocked, with six failing checks: Readiness summary, NuGet packages / pack-and-publish, Windows setup/discover/dry-run, perf-gate, uat cross-platform (tiers 8, 9), and uat (tiers 0-2, 4-10). No unknowns were reported. Start with the Readiness summary failure, since it may summarize the others: https://github.com/IanFrelinger/Ashlar/actions/runs/33867694826/job/101011341405
- *Sonnet 5.5:* Release v0.1.2 is blocked by six failing checks: Readiness summary, NuGet packages / pack-and-publish, Windows setup/discover/dry-run, perf-gate, uat cross-platform (tiers 8, 9), and uat (tiers 0-2, 4-10). Nothing is listed as unknown, so the next step is to fix or rerun those failures. A good place to start is the NuGet pack-and-publish job: https://github.com/IanFrelinger/Ashlar/actions/runs/33867752688/job/101006550765. The perf-gate failure is also worth checking early: https://github.com/IanFrelinger/Ashlar/actions/runs/33867694863/job/101006194573.

**#876**

- *Haiku 5.5:* The verdict is merged: pull request #876 in IanFrelinger/Ashlar has already been merged, so there are no blockers and nothing is unknown. The tool reported no notes either. You can confirm the details at https://github.com/IanFrelinger/Ashlar/pull/876.
- *Sonnet 5.5:* PR #876 is merged, so there is nothing blocking it. The tool reported no blockers, no unknowns, and no additional notes. Evidence: https://github.com/IanFrelinger/Ashlar/pull/876


All 28 are in [`summaries.jsonl`](../../tools/token-benchmark/results/2026-10-25-dogfood/summaries.jsonl).

## What this found

1. **Mergeability is often not computed on the first read, and that hides the real blocker.** All 4 `unknown`
   verdicts are this case, and in all 4 GitHub's answer arrived seconds later: 3 merge conflicts and 1 branch
   behind master. The brick already turns `dirty` and `behind` into blockers
   (`src/Ashlar.Bricks.GitHub/GitHub/BlockerAnalysis.cs`); it reads the pull request once. GitHub's documented
   pattern is to read again after a short wait. A bounded re-read (for example two more reads, two seconds apart)
   when `mergeable` is `null` would likely have turned all 4 into the right blocker. That is a product change for
   after the 0.4.0 freeze, not something this run changed.
2. **"Blocked" on a published release means the evidence on the tagged commit is red, not that nothing
   shipped.** v0.1.2 is on nuget.org (`Ashlar.CLI` 0.1.2 published 11:29 UTC on 2026-09-04), yet the release
   workflow's `pack-and-publish` job then failed at 11:32 and its "Draft GitHub Release" step was skipped. The
   brick's six blockers are all real. For a tag that already shipped, the useful reading is "this release's
   evidence is incomplete", and copy that shows release answers should say so.
3. **Root causes the brick cannot see are reported as their symptoms.** #875's required checks "have not
   reported" because GitHub does not run them on a conflicting pull request. The verdict is right and the
   symptom is accurate; the conflict itself only appears once mergeability is computed (finding 1).
4. **A summarising model adds little risk here when thinking is off.** The brick's answer already names each
   blocker with its evidence, so the model only restates it. The one embellishment above is the kind a reviewer
   should still expect.

## Method and rerun

- Targets: [`tools/token-benchmark/dogfood-targets.json`](../../tools/token-benchmark/dogfood-targets.json).
- Capture: `capture_brick.py --targets dogfood-targets.json`, run in GitHub Actions (`ubuntu-24.04`, .NET SDK
  10.0.403) against `Ashlar.Mcp.Server.Host` built in Release from the commit above, with the audit directory
  set (since #868 the host withholds a result whose audit record cannot be written). A temporary workflow on a
  throwaway branch ran it (run 40237177037); it is not part of this change.
- Summaries:
  `ANTHROPIC_API_KEY=... python3 tools/token-benchmark/dogfood_summarize.py --brick results/2026-10-25-dogfood/brick --out results/2026-10-25-dogfood --model claude-haiku-5-5:0.10:0.50 --model claude-sonnet-5-5:2:10 --budget-usd 3`
  (run from `tools/token-benchmark/`).
- Spend on this page: $0.17 of model calls (two summary runs plus a one-call check of Haiku's thinking setting).
