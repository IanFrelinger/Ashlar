# Blockers accuracy harness

Grades answers to "what is blocking this PR?" against ground truth computed from GitHub's API at one recorded
snapshot. Method, rubric and the published result:
[`docs/benchmarks/blockers-accuracy.md`](../../docs/benchmarks/blockers-accuracy.md).

| File | What it is |
|------|------------|
| `targets.json` | The 44 pull requests and the category each was selected as |
| `capture.py` | Records the snapshot: GraphQL truth facts, the REST responses, and the brick's answer through `Ashlar.Mcp.Server.Host` (needs a built host, so it runs in CI) |
| `fixtures.py` | Recorded-response store both `run.py` and `score.py` read |
| `truth.py` | The rubric as code: verdict and blocker keys from the snapshot, and the classification of a wrong claim |
| `run.py` | Asks Sonnet in the brick and native arms from the recorded data; writes `runs.jsonl` and `meta.jsonl`; stops at `--budget-usd` |
| `score.py` | Grades the brick payload and both arms' answers; writes `score.json` and `per-pr.md` |
| `fixtures/truth/`, `fixtures/brick/`, `fixtures/github/` | The snapshot (captured 2026-10-25 16:28–16:34 UTC) |
| `results/<date>/` | Raw runs and scores of a published run |

Re-grading needs no key and no network: `python3 score.py results/2026-10-25`.

`run.py` reads `ANTHROPIC_API_KEY` from the environment, sends it only as a request header, and masks it in any
error it prints.
