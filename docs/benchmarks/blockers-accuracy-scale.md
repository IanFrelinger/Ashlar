# Accuracy scale pass: `github.blockers` 1.1.0 on ~100 PRs

Companion to the [44-PR accuracy study](blockers-accuracy.md). Same rubric (`tools/blockers-accuracy/truth.py`
and `score.py`), same brick (`github.blockers` **1.1.0**, post-[#874](https://github.com/IanFrelinger/Ashlar/pull/874),
including the #872 quoting of GitHub-sourced names). Expanded sample and brick-raw grading only.

**Result (CLEAN).**

| Metric | Value |
|---|---|
| Pull requests | **100** |
| Public repositories | **10** (was 5) |
| Verdicts right | **100 / 100** |
| Blockers found | **211 / 211** |
| Overcalls / fabricated / unattributed | **0 / 0 / 0** |
| Blocked PRs with every blocker named | **64 / 64** |
| Anthropic spend | **$0** (brick-raw only; Sonnet skipped) |

Read with the same limit as the 44-PR re-run: the brick and the rubric both read required checks and
`reviewDecision` from the public GitHub sources, so agreement shows the brick applies those facts without
error on a wider sample. It is not independent evidence that GitHub's merge box would list exactly these
blockers.

## Sample

Selected 2026-10-25. Keeps the original 44 from five repos and adds 56 more across five further repos,
including `IanFrelinger/Ashlar` and other .NET-heavy hosts. `selected_as` is a selection label only;
grading uses the rubric on the capture snapshot.

| Repository | PRs |
|---|---|
| IanFrelinger/Ashlar | 19 |
| AvaloniaUI/Avalonia | 17 |
| modelcontextprotocol/python-sdk | 11 |
| dotnet/efcore | 10 |
| microsoft/semantic-kernel | 9 |
| cli/cli | 8 |
| modelcontextprotocol/csharp-sdk | 8 |
| dotnet/aspnetcore | 8 |
| dotnet/runtime | 7 |
| App-vNext/Polly | 3 |

Selection mix (label counts): blocked-failing-ci 14, blocked-review 14, merged 12, conflict 10, behind 9,
changes-requested 9, draft 8, ready-clean 8, closed 8, unstable 6, blocked-pending-ci 2.

Snapshot truth on capture: 64 blocked, 15 ready, 13 merged, 8 closed. Blocker kinds: `check_pending` 100,
`review_required` 40, `check_failing` 28, `behind` 14, `conflict` 12, `changes_requested` 9, `draft` 8.

## Method

- **Brick.** Built from this branch's `Ashlar.Mcp.Server.Host` (Release, net10.0) with `github.blockers`
  1.1.0. Asked through `actions/pr-blockers/pr_blockers.py`, same path as CI capture.
- **Snapshot.** `tools/blockers-accuracy/capture.py` → `fixtures-scale/`, 2026-10-25 21:57–22:08 UTC.
  Token: the cloud agent's `gh` token (no admin rights on the foreign repos). Head SHA matched GraphQL and
  REST on all 100.
- **Grading.** Brick-raw only: map each blocker reason sentence to rubric keys with `score.py`
  `BRICK_PATTERNS`, compare to `truth.py`. No Sonnet arm. Brick-raw already covers the full rubric; an LLM
  answer arm would spend tokens without adding signal (on the 44-PR study, brick+Sonnet matched brick-raw
  on every PR).
- **Harness fix in this pass.** After #872, check and status names in brick reasons are one inline code
  span (`` `name` ``). `score.py` now strips a surrounding backtick pair before comparing to truth keys.
  Without that, every required-check claim scored as a fabricated name with backticks. Re-grading the
  post-#874 44-PR snapshot still yields 109/109 after the strip.

## Brick findings

None on this sample. No new product bug to file. Verdict and blocker-list agreement was complete after the
harness quote strip.

F1–F3 from the 44-PR study remain fixed in 1.1.0 ([#874](https://github.com/IanFrelinger/Ashlar/pull/874)).

## Spend

Hard budget $6. Actual Anthropic spend: **$0**. The key was exported into the environment for a possible
Sonnet arm, never sent (no `run.py` call), and unset afterward. Capture and scoring used only GitHub and
local compute.

## Reproduce

```bash
cd tools/blockers-accuracy
python3 score.py results/2026-10-25-scale --fixtures fixtures-scale --brick-dir brick
```

Raw inputs: `tools/blockers-accuracy/fixtures-scale/` (truth, brick 1.1.0, REST). Scores:
`results/2026-10-25-scale/score.json` and `per-pr.md`. Targets: `targets.json` (100 PRs).

## Limits

Same as the 44-PR study, plus:

- Brick-raw only; model variance and native-arm recall are not re-measured on this sample.
- Ten public repos with GitHub Actions (or equivalent check contexts). Private repos and other CI hosts are
  not covered.
- Snapshot is one moment (2026-10-25 ~22:00 UTC). Re-capture changes truth.
