Card #770 (budgeted autonomy PR 3), workstream `budgeted-autonomy`, phase C. Roadmap #755; PR 1 #756 (`1d8bc7448`), PR 2 #769 (`f236bd4ad`). Base master `f236bd4ad`.

## What changed

The controller now owns the pull request. The worker (a headless `claude -p` under `acceptEdits`) can only edit files in its workspace, so the controller does every git and `gh` step and records each one.

- **`ashlar_controller/gitops.py`** (lane B): clone the current base onto a fresh branch for the worker (`prepare_workspace`, with an optional `--reference` clone), commit as `ashlar-controller` and push a workspace (`commit_and_push`; no diff returns `None`), clone a PR head for a repair and refuse a head that moved (`checkout_pr_head` → `StaleHead`). Tested against real local git over `file://` (29 tests).
- **`ashlar_controller/prs.py`** (lane A): the `prs` ledger (`open_or_reuse_pr`, `record_head`, `record_merged`, `record_closed`, `bump_repair_round`, `latest_merge_sha`), the `gh` client (`GhCli`) and its in-memory twin (`FakeGh`), `evaluate_checks` **by head SHA** (only the check runs of `GET repos/<repo>/commits/<head>/check-runs` are read; a required check with no run on that head is pending; `success`/`skipped` green; `failure`/`cancelled`/`timed_out`/`action_required`/`stale`/`startup_failure` red), `pr_merge` with `--squash --match-head-commit <head>`, and `compact_handoff`, the repair run's entire brief (PR, branch, base, head, round N of M, per red check its identity `check:<name>:<12 hex>` and bounded evidence). It takes no transcript, session or prompt as input (41 tests).
- **`ashlar_controller/lifecycle.py`** (lane C): the PR phase of a tick. Before an implementation launch: clone the newest recorded merge SHA (else the task's base, else the branch tip) onto `auto/<task>`; the prompt carries the compact handoff (previous PR number, outcome, merge SHA). On completion: commit and push; `no-change-to-propose` is a diagnosed failure (retried once, then parked); open the PR once (an existing row or an open PR for the branch is reused). Every tick: observe each open PR by its current head; green → merge pinned to that head, record `merge_sha`, task `done` (`pr-merged`); red → one repair round, at most `limits.repair_rounds` per PR, as a **new** run (`role=readiness-fixer`, `resume_session_id=None`) in a fresh clone of the head, briefed only by the compact handoff; rounds exhausted → `parked-failure` (`repair-rounds-exhausted`); closed without merge → `outcome=closed-unmerged`, `parked-failure`, never `done`. PRs are serial (`pr-open-blocks-dispatch`). PR events deduplicated by id trigger one idempotent observation; a repeated id does nothing (22 tests).
- **Schema 2**: `prs.branch/url/title`, `tasks.branch`, `runs.pr_number`, with an in-place journalled migration from schema 1 (refused read-only). **Policy `pr` section** (`repo`, `remote_url`, `base_branch`, `branch_prefix`, `required_checks` = the five on master, `reference_checkout`) with defaults and validation (6 tests).
- **CLI**: `tick --gh cli [--workspaces]`, `pr list|show|observe`, `event ingest --gh cli`; `status` shows the PR record. Without `--gh cli` a completed run is `done` as before.
- Records: `tools/autonomy-controller/README.md` (new "PR lifecycle" section, tick steps, stop-reason rows, integrator decisions, "Not in PR 3"), `CHANGELOG.md` `[Unreleased]`, `docs/DocsIndex.md` row; test floor 190 → 290 (suite 312).

## Acceptance criteria (card #770) and where each is proven

| Criterion | Test | Mutation |
|---|---|---|
| Old green checks cannot satisfy a changed head | `test_old_green_checks_cannot_satisfy_a_changed_head`, `test_the_merge_is_bound_to_the_observed_head`, `test_a_missing_required_check_is_pending_not_green` | m1, m2, m3, m4 |
| Duplicate events create no PR or worker | `DuplicateEvents.*` (4 tests) | m6 |
| Closure without merge is not success | `test_a_pr_closed_without_a_merge_is_never_success`, `test_after_a_closed_unmerged_pr_the_next_task_does_not_use_its_head` | m5 |
| Next task uses the updated base and a compact handoff | `test_the_next_task_uses_the_merge_sha_and_a_compact_handoff` | m11 |
| Previous transcript not injected | `test_a_red_head_launches_one_repair_with_fresh_context`, the handoff test above | m7, m8 |
| Repair rounds bounded; round counted; PRs serial; no-change is not a PR; completed is proposed not done; stale head refused | `RepairRounds.*`, `SerialPrsAndTheNextBase.*`, `OpeningThePr.*`, `test_stale_expected_head_raises_and_nothing_is_pushed` | m9, m10, m12, m13, m14, m15 |

## Integrator decisions (no owner row exists; the owner can reverse any)

- **PRs are serial**: while a PR is open nothing new launches, so the next task starts from the merge SHA the open one produces. Matches the repository's serial-merge rule.
- **The worker edits; the controller commits, pushes, opens, observes, repairs and merges.** Under `acceptEdits` a worker cannot push, and the branch, commit message and push stay under the controller's record.
- `success` and `skipped` are the only green conclusions; a required check with no run on the head is pending, never green.
- A repair round is counted before its launch (a crash in between costs a round, never an extra launch). A repair that pushes no change spends its round.
- A parked task's open PR still blocks dispatch and is watched for a merge or a closure, but is not parked again and gets no launch; the operator closes or requeues.
- A repair run that stops at the turn ceiling is not resumed; the next observation of the still-red head starts a new round with a fresh session.
- `pr.required_checks` default to the five required checks on master; `pr.repo`/`remote_url` default to this repository.

## Verification

- Host: `bash tests/scripts/autonomy-controller.test.sh` → `Ran 312 tests ... OK (skipped=1)` (the one skip is the POSIX-only world-writable-policy test).
- Container: `bash scripts/test-in-container.sh -- 'bash tests/scripts/autonomy-controller.test.sh'` → `Ran 312 tests ... OK`, `312 tests passed`.
- `bash scripts/check-docs-phantom-paths.sh` → 0 missing; `bash -n` on the test wrapper; knowledge graph regenerated after `git add` (unchanged).
- Lane reports: lane A killed 10 mutants of `prs.py`, lane B 6 of `gitops.py` (manual red/restore/green in their worktrees, listed in the handoff).
- Mutation checks through `scripts/mutation-check.sh` against the committed SHA `9ab820af9` (`ASHLAR_MUTATION_RUNNER=bash tools/autonomy-controller/mutation-runner.sh`; `runner=override` means the Python suite on the host, as the runner documents; `green=passed:311/312` is 312 minus the one POSIX-only skip). All 15 KILLED, summary lines verbatim:

```
mutation m1-pending-head-counts-green: KILLED red=failed:8/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m2-match-head-commit-dropped: KILLED red=failed:1/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m3-red-conclusion-counts-green: KILLED red=failed:15/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m4-merge-current-not-observed-head: KILLED red=failed:1/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m5-closed-unmerged-is-done: KILLED red=failed:2/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m6-duplicate-event-acts-again: KILLED red=failed:1/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m7-repair-resumes-previous-session: KILLED red=failed:1/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m8-repair-prompt-carries-transcript: KILLED red=failed:1/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m9-repair-rounds-unbounded-by-one: KILLED red=failed:1/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m10-round-not-counted: KILLED red=failed:4/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m11-next-base-ignores-merge-sha: KILLED red=failed:1/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m12-prs-not-serial: KILLED red=failed:4/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m13-no-change-still-opens-pr: KILLED red=failed:2/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m14-completed-is-done-not-proposed: KILLED red=failed:21/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
mutation m15-stale-pr-head-accepted: KILLED red=failed:1/312 green=passed:311/312 ref=9ab820af93bbc0691a8e667c18463d5dac6ae3b5 runner=override
```

## Live run

The integrated branch's live run (`tick --adapter claude --gh cli` on a registered task whose worker adds one file, with a sixth required check `owner-approval (never runs: live-run guard)` in the operator policy so the controller observes the five real checks by head SHA but can never merge; then the PR is closed by hand and the controller records `closed-unmerged`) was prepared on this host (state and policy under `%LOCALAPPDATA%\ashlar-controller\`, task `live-pr3` registered, dry run `would-launch`). **The auto-mode classifier refused to launch the worker from the Claude Code session ("Create Unsafe Agents")**, so the launch is an owner step; the exact command is in the phase handoff. Until it runs, the live evidence for PR 3 is the `GhCli` argv shapes against the recording runner and real local git; the live `gh` calls are not claimed as verified.

## Not in PR 3

`phases` table unwritten; Codex/Cursor adapters (PR 5); premium escalation and provider transfer counted, not driven; the local readiness gate loop (`readiness run|evaluate`) stays operator-driven; the pilot against real cards (PR 4).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
