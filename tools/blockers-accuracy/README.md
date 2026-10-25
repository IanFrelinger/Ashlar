# Blockers accuracy harness

Grades answers to "what is blocking this PR?" against ground truth computed from GitHub's API at one recorded
snapshot. Method, rubric and the published results:
[`docs/benchmarks/blockers-accuracy.md`](../../docs/benchmarks/blockers-accuracy.md) (44 PRs) and
[`docs/benchmarks/blockers-accuracy-scale.md`](../../docs/benchmarks/blockers-accuracy-scale.md) (100 PRs, brick-raw).

| File | What it is |
|------|------------|
| `targets.json` | The pull requests under study and the category each was selected as (100 for the scale pass) |
| `capture.py` | Records the snapshot: GraphQL truth facts, the REST responses, and the brick's answer through `Ashlar.Mcp.Server.Host` (needs a built host) |
| `fixtures.py` | Recorded-response store both `run.py` and `score.py` read |
| `truth.py` | The rubric as code: verdict and blocker keys from the snapshot, and the classification of a wrong claim |
| `run.py` | Asks Sonnet in the brick and native arms from the recorded data; writes `runs.jsonl` and `meta.jsonl`; stops at `--budget-usd` |
| `score.py` | Grades the brick payload and both arms' answers; writes `score.json` and `per-pr.md`. Strips #872 inline-code quotes on check names when mapping brick reasons |
| `fixtures/truth/`, `fixtures/brick/`, `fixtures/github/` | The first snapshot (captured 2026-10-25 16:28–16:34 UTC) |
| `fixtures-rerun/` | The second snapshot (2026-10-25, about 18:00 UTC), after the fix in #874: truth, `brick/` (1.1.0), `brick-old/` (1.0.0), REST responses |
| `fixtures-scale/` | The scale snapshot (2026-10-25 21:57–22:08 UTC): 100 PRs, 10 repos, brick 1.1.0 |
| `results/<date>/` | Raw runs and scores (`2026-10-25-rerun*` grade the second snapshot; `2026-10-25-scale` grades `fixtures-scale`) |

Re-grading needs no key and no network: `python3 score.py results/2026-10-25`. `score.py --fixtures DIR --brick-dir NAME`
grades another snapshot or brick build; `run.py --arms brick` runs one arm only; `capture.py --extra NAME=COMMAND`
asks a second host build right after the first.

`run.py` reads `ANTHROPIC_API_KEY` from the environment, sends it only as a request header, and masks it in any
error it prints.
