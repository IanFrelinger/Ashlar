Card #779 (budgeted autonomy PR 5), first step: the PR 4 pilot's findings, fixed before PR 5 proper. Workstream `budgeted-autonomy`, phase E. Base master `ea71db40c` (#778, the pilot's last merge).

## What the pilot showed

The owner ran the pilot from a terminal on 2026-10-11 (ledger `ctl-pilot-pr4`, guarded: the owner merged by hand).

| Task | Runs | Outcome |
|---|---|---|
| `pilot-0` (throwaway file) | 1 run, 2 turns | PR #777 merged `f0fd9d113`, task `done` |
| `pilot-1` (card #762) | attempt 1 capped at 13 turns; attempt 2 resumed, reported 48 | PR #778 merged `ea71db40c`, task `done`, pool 61 of 36 |
| `pilot-2` (card #776) | two attempts capped at 13 turns each | `parked-budget`, 10 turns left, nothing pushed |

Three findings:

1. **A resumed session was handed a fresh clone.** Attempt 1 of `pilot-1` edited `docs/EgressInventory.md` (its workspace still shows 19 changed lines) and stopped at the ceiling. Attempt 2 resumed the same session in a new clone, found none of its edits, and redid the work. **Fixed:** `lifecycle.prepare_implementation` reuses the capped run's workspace when it is on disk with content, journals `workspace-reused`, and briefs the session that its edits are still there. A missing or empty workspace is cloned afresh, as before.
2. **The report printed a merged PR as `merged merged`.** **Fixed:** the state is printed once when the outcome is the same word.
3. **A resumed run reported `num_turns` 48 under `--max-turns 12`** and was charged in full. A probe on 2026-10-12 did not reproduce it: a resume with `--max-turns 2` stopped at `error_max_turns` with 3 turns, and the count was per invocation. That run spawned no subagents. **Recorded, not fixed:** README "What is unverified". The charge stays as reported, which is the conservative reading.

## Changes

- `tools/autonomy-controller/ashlar_controller/lifecycle.py`: `reusable_workspace`, `resume_prompt`, and the reuse path in `prepare_implementation`.
- `ashlar_controller/controller.py`: the dispatcher passes the run it resumes.
- `ashlar_controller/metrics.py`: the PR line.
- `tests/test_pr_lifecycle.py`: two tests. One covers reuse with the edit kept and pushed from that workspace. The other covers a fresh clone on an empty workspace while the session still resumes. `tests/test_metrics.py`: the report assertion.
- `tools/autonomy-controller/README.md`: the pilot's record and the unexplained figure. `CHANGELOG.md`: a Fixed block.

## Verification

- Host suite: `Ran 386 tests`, `OK (skipped=1)`. The floor is 360.
- `bash scripts/check-docs-phantom-paths.sh`: 0 missing. The knowledge graph is unchanged.
- Mutation checks against the commit, through `scripts/mutation-check.sh` with the controller's runner. The output is verbatim:

```
mutation f1-resume-clones-afresh: KILLED red=failed:1/386 green=passed:385/386 ref=e042ea7bb398faa911697fb69a9600c928beb193 runner=override
mutation f2-empty-workspace-reused: KILLED red=failed:1/386 green=passed:385/386 ref=e042ea7bb398faa911697fb69a9600c928beb193 runner=override
mutation f3-report-prints-merged-twice: KILLED red=failed:1/386 green=passed:385/386 ref=e042ea7bb398faa911697fb69a9600c928beb193 runner=override
mutation f4-resume-passes-no-run: KILLED red=failed:1/386 green=passed:385/386 ref=e042ea7bb398faa911697fb69a9600c928beb193 runner=override
```

  The first two kill the workspace reuse. The third kills the report line. The fourth kills the dispatcher handing over the resumed run.

## Integrator decisions

- A resumed session reuses its workspace only when the directory has content; otherwise it gets a fresh clone and the session still resumes.
- The 48-turn charge is left as reported until the figure is explained.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
