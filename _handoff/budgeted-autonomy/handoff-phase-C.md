# budgeted-autonomy handoff: end of phase C (PR 3), start of phase D (PR 4)

*Written 2026-10-11 ~16:18 UTC at the end of phase C. Start the next thread with the prompt in §9, or with `/start-phase budgeted-autonomy`. Storage branch `claude/budgeted-autonomy-workspace`, folder `_handoff/budgeted-autonomy/` (`bash scripts/handoff-fetch.sh --workstream budgeted-autonomy --list`).*

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`): end each phase with `/handoff budgeted-autonomy <phase>`; start each phase with `/start-phase budgeted-autonomy`. One phase is one of the five roadmap PRs (the owner's prompt of 2026-10-11, quoted in #755). The handoff is published to `claude/budgeted-autonomy-workspace`, sent to the owner, and the owner is notified. Push every in-flight branch to GitHub before writing the handoff. Do not implement more than one PR per thread.

**Throughput pattern (phases B and C):** split the PR into lanes with a written interface contract (`pr3-lanes.md` on the storage branch is the phase C shape), make one shared base commit first (schema, names, policy keys) and push it, launch one agent per lane in an isolated worktree (Agent tool, `isolation: "worktree"`) telling it the base SHA and to `git reset --hard` to it, build the integration lane yourself against the contract while they run, cherry-pick the lane commits, then verify the merged branch yourself (suite, container, mutation batch). In phase C both lanes landed name-exact; the integration tests needed two fixes of my own code (a pending blocker that did not follow the head, and a parked task's PR being parked again every tick), none of theirs.

## 1. Where things stand

- **Master:** `d2c11b347d1759215b87bc98c94efced62f3b6d8`.
- **Merged in this workstream:**

| PR | What | Merge SHA |
|---|---|---|
| #756 (phase A) | PR 1: operator policy and durable controller state | `1d8bc7448` |
| #769 (phase B) | PR 2: Claude adapter and bounded readiness | `f236bd4ad` |
| #771 (phase C) | PR 3: PR lifecycle and fresh context (`lifecycle.py`, `prs.py`, `gitops.py`, schema 2, policy `pr` section, CLI `--gh cli`) | `d2c11b347` |

- **Readiness verdict for master's head:** `scripts/release/readiness-verdict-for-sha.sh <full sha>` → `REFUSED (no-readiness-run)`: the diff matches no readiness path, so Full Platform Readiness did not push-trigger; the newest push-triggered master run that succeeded is still `9faefe46c` (#749). PR 3 is a tooling-only diff (no readiness path), so the PR's `Readiness summary` was a diff-skipped pass; that is not platform verification and is not claimed as such.
- **Agent-bus (#695):** phase C posted `claim` card-770 (the classifier allowed `gh issue comment` this time) and `handoff` pr-771 and `done` pr-771/card-770 (comments 6110678422, 6111038026, 6111046805), all three allowed by the classifier. **The three phase B posts are still unposted** (`01-claim-card-768.md`, `02-handoff-pr-769.md`, `03-done-pr-769.md` on the storage branch); the owner asked to be asked, and was asked in the phase C final message. Open items not this workstream's: Grok's `ask` on `drift-746`, `drift-748`; `status` on `land-749-753`, `mutations-spec007`. Grok's drift audits of #756, #769 and #771 have not posted; phase D answers them with a follow-up PR and `done` on the slug.
- **Phase C exit criteria** (card #770): old green checks cannot satisfy a changed head; duplicate events create no PR or worker; closure without merge is not success; the next task uses the updated base and a compact handoff; the previous transcript is not injected; every behaviour mutation-red. **Met in tests and mutations** (§4). **The live run of the integrated branch did not happen in this thread**: the auto-mode classifier refused to launch the worker from the Claude Code session ("Create Unsafe Agents"). It is prepared on the host and is the first step of phase D, or an owner step before it (§5, §6).

## 2. Read these first

- **In the repo (master):** `CLAUDE.md`; `tools/autonomy-controller/README.md` in full, especially "PR lifecycle", "What one tick does" (eight steps now), the stop-reason table, "Operating policy defaults (integrator decisions)" and "Not in PR 3"; `ashlar_controller/{lifecycle,prs,gitops,controller,cli}.py`; `tests/test_pr_lifecycle.py` (the acceptance tests over `FakeGh` and the in-test `FakeGit`), `tests/test_prs.py`, `tests/test_gitops.py` (real local git); `tests/scripts/autonomy-controller.test.sh` (floor 290; suite 312); `_handoff/bus/PROTOCOL.md`; `_handoff/phases/README.md`.
- **The cards:** #755 (the roadmap), #770 (PR 3; close it with the merge SHA if not yet closed), #768, and the PR 4 card once created.
- **On the storage branch, `_handoff/budgeted-autonomy/`:** `handoff.md` (this), `handoff-phase-{A,B,C}.md`, `pr-771-body.md`, `pr3-lanes.md`, `mutations-pr3-summary.txt`, `mutation-logs-pr3.tar.gz`, `04-claim-card-770.md`, `05-handoff-pr-771.md`, `06-done-pr-771.md`, `live-run-pr3.md` (the prepared live-run procedure), plus phase B's files.

## 3. Decisions

- **Owner decisions this workstream relies on:** none recorded (no spec, no decisions log). The owner's prompt in #755 is the brief.
- **Integrator decisions taken in phase C** (PR body of #771 and the README; the owner can reverse any):
  - PRs are serial: while a PR is open nothing new launches (`pr-open-blocks-dispatch`), so the next task starts from the merge SHA the open one produces; matches the repository's serial-merge rule.
  - The worker only edits; the controller clones, commits (author `ashlar-controller`), pushes, opens, observes, repairs and merges. Under `acceptEdits` a worker cannot push.
  - Checks are read by head SHA from `repos/<repo>/commits/<head>/check-runs`; `success` and `skipped` are the only green conclusions; a required check with no run on the head is pending, never green; the merge passes `--match-head-commit <head>`.
  - A repair round is counted before its launch; a repair that pushes no change spends its round; a repair stopped at the turn ceiling is not resumed (the next observation of the still-red head starts a new round with a fresh session); rounds are bounded by `limits.repair_rounds` per PR.
  - A parked task's open PR still blocks dispatch and is watched for a merge or a closure, but is not parked again and gets no launch.
  - `pr.required_checks` default to the five required checks on master; `pr.repo`/`pr.remote_url` default to this repository; the worker branch is `auto/<task id>`.
  - Phase B's decisions stand (model `claude-sonnet-5`, `acceptEdits`, `--strict-mcp-config`, route `claude.ai`, the `percent` allowance rule, the fixer's single general-purpose fallback).
- **Open questions for the owner (none blocks phase D, but the first changes how it starts):**
  1. **Launching workers from a Claude Code session.** The classifier refuses `tick --adapter claude --gh cli` ("Create Unsafe Agents"). Options: the owner runs the controller from their own terminal (the pilot's natural shape anyway: the controller is an operator process, not a Claude Code session), or an allow rule in the project's `.claude/settings.json` for the controller command. Decide before phase D's pilot.
  2. Whether the integrator decisions above and those of phases A and B become durable owner rows (a short `docs/specs/` note or an "owner decisions" section in the README).
  3. The bus credential for the controller's own posts (phase B Q2): still unexercised; `gh issue comment` from a Claude Code session was allowed in phase C, refused in phase B. Not deterministic; do not rely on it.
  4. Whether the pilot (PR 4) may let the controller **merge** to master. Phase C's prepared live run uses a sixth, never-running required check as a guard so the controller can only observe, never merge.

## 4. Plan and live status

**Phase map (one PR per phase):**

| Phase | PR | Scope | Ends when | Status |
|---|---|---|---|---|
| A | 1 | Budget policy and durable controller state | merged on green; criteria mutation-proven | done (#756) |
| B | 2 | Claude adapter and bounded readiness | one task verified with a recoverable handoff; retry/no-fallback/cancel/funding rules proven | done (#769) |
| C | 3 | PR lifecycle and fresh context | old green cannot satisfy a changed head; duplicate events create no PR or worker; closure without merge is not success; next task uses updated base and compact handoff; transcript not injected | **done (#771)**, live run pending (§1) |
| **D** | **4** | **Operator controls, evidence retention, pilot** | status view, pause/cancel/resume/provider-disable, backups and restore, LLM-free status reports, evidence archives, metrics; **three bounded real tasks** through the full lifecycle | **next** |
| E | 5 | Codex and Cursor adapters | provider transfer preserves task/PR/attempts/budget; previous writer stopped and reconciled; one small real task through each enabled adapter | |

**Work items:** `feat/budgeted-autonomy-pr-lifecycle` @ `9ab820af9` (base `7b69b7493` shared commit, `2a6e36734` lane A, `3d5578b28` lane B, `9ab820af9` lane C), PR #771, **merged** (squash) as `d2c11b347` on the five required checks green, `mergeStateStatus CLEAN`, no `block`; branch deleted by GitHub; card #770 closed with the merge SHA. Lane worktrees and branches removed after cherry-pick.

**Mutation checks** (`scripts/mutation-check.sh` with `ASHLAR_MUTATION_RUNNER=bash tools/autonomy-controller/mutation-runner.sh`, ref `9ab820af9`), verbatim:

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

Lane A's manual kills (prs.py; mutation → test): `if verdict.red:` → `and not verdict.pending` (test_red_beats_pending); `GREEN_CONCLUSIONS` without `skipped` (test_skipped_counts_green); FakeGh merge ignoring the head (test_green_on_a_does_not_merge_b); `open_or_reuse_pr` existing-row return removed (test_three_calls_make_one_row_and_one_create); truncation marker removed (two truncation tests); `latest_merge_sha` `rowid ASC` (ordering test); `--match-head-commit` dropped (argv test); identity without summary lines (five tests); first-run-wins (latest-run test); journal kind renamed (record_head test). Lane B's (gitops.py): `GIT_TERMINAL_PROMPT=1`; stale-head check disabled (test_stale_expected_head_raises_and_nothing_is_pushed); porcelain test inverted (10 red); `--reference` disabled; non-empty path check disabled; author name changed.

**Phase D (PR 4), spelled out, in order:**
1. `/start-phase budgeted-autonomy`; read §2; create the PR 4 card as an `agent-task` issue from the PR 4 row above (acceptance criteria: the operator can see state, remaining permitted attempts, funding pool and blocker without an LLM; pause/cancel/resume/provider-disable take effect at the next tick; a backup can be restored and the restored ledger refuses nothing it should accept; evidence archives hold manifest, stdout, stderr, transcript path and the PR record per run; metrics are counts from the ledger; three bounded real tasks go through the full lifecycle); post `claim` on the bus (try `gh issue comment 695 --body-file`; if refused, draft and ask).
2. **Run the prepared live run of PR 3 first** (§5 step 9 and `live-run-pr3.md`): it is the integrated branch's missing live evidence and PR 4's first pilot rehearsal. Record what the real `gh` returned: the `pr-observed` journal rows, the five check names and conclusions by head SHA, and the `closed-unmerged` record after the by-hand close. Fix any shape the live `gh` exposes in a small PR before PR 4 proper (phase B found three such shapes).
3. Build PR 4: `status` as the operator view (already most of it; add the PR record and the evidence paths), `evidence archive <run>` (tar of the run dir plus the ledger rows), `backup`/`restore` (SQLite backup API; restore refuses a schema it does not know), `metrics` (counts and sums from the ledger, no LLM), `cancel` for a running repair, and the pilot: three real cards, each through clone → edit → push → PR → observe → merge or park, with the owner's answer to Q4 (§3) deciding whether the controller merges.
4. Keep the evidence rules: mutation-check every behaviour; summary lines verbatim in the PR body; `scripts/test-in-container.sh` for anything with `dotnet`.
5. PR body per §5; `handoff` on the bus; merge on green; `done` with the merge SHA; `/handoff budgeted-autonomy D`.

## 5. How to do the next phase's routine work

1. Clone or use the session checkout (it is a clone: `.git` is a directory). `git checkout master && git pull --ff-only`, then `git checkout -b feat/budgeted-autonomy-<topic>`. Never run `scripts/test-in-container.sh` from a worktree.
2. Parallel lanes: write the contract first (`pr3-lanes.md` shape), commit and push a shared base (schema, names, policy keys), launch one agent per lane with `isolation: "worktree"` telling it the base SHA and to `git reset --hard` to it, build the integration yourself against the contract meanwhile, cherry-pick with `git stash -u -- <your paths>` around the cherry-pick, then verify the merged branch yourself.
3. Verify: `bash tests/scripts/autonomy-controller.test.sh` (host; `OK (skipped=1)` on Windows; ~50 s, the gitops module is ~35 s of it); `bash scripts/test-in-container.sh -- 'bash tests/scripts/autonomy-controller.test.sh'` (container; 312/312); `bash scripts/check-docs-phantom-paths.sh`; `bash -n` on shell files. `bash scripts/ci/run-repo-gates.sh` on this host has the two known Windows failures (§6); `shell-lint` on ubuntu is the check that counts.
4. Commit, then mutation-check against the commit with the python3 shim first on PATH (§6): the batch runner is `mutations/run.sh` on the storage branch's `pr3-lanes.md` sibling (shape: one `run <id> <file> <old> <new>` per mutation; it appends the summary lines to one file). Quote each `mutation <id>: ...` line verbatim in the PR body. Each mutation is about two minutes (two suite runs plus a clone), so start the batch in the background right after the commit.
5. `git add <paths>` (named paths, never `-A`) then `python scripts/knowledge-graph/build-knowledge-graph.py`; commit the graph if it changed (unchanged for PR 3).
6. `git push -u origin <branch>`; `gh pr create --body-file`; `gh pr edit --body-file` to add the mutation lines after the batch; the desktop app binds the PR (`mcp__ccd_pr__get_status` shows the checks).
7. Bus: draft every post as a file; try `gh issue comment 695 --body-file` once; if refused, name the file and ask the owner. Merge (squash) on the five required checks green unless Grok posted `block`; `done` with the merge SHA; `gh issue close <card> --comment` works.
8. Read the readiness verdict for the merge SHA; for a tooling-only diff it says `REFUSED (no-readiness-run)`, the expected answer.
9. **Live run of the controller** (owner or a terminal outside a Claude Code session; the session's classifier refuses the launch): state `%LOCALAPPDATA%\ashlar-controller\state\controller.db` (schema 2, task `live-pr3` registered, dry run `would-launch`), policy `%LOCALAPPDATA%\ashlar-controller\policy.json` (ceilings 8/16/3, `repair_rounds` 1, `reference_checkout` = this clone, a sixth required check `owner-approval (never runs: live-run guard)` so the controller can observe but never merge). Note the desktop app virtualizes `%LOCALAPPDATA%` to `...\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\`; a terminal outside the app sees the real `%LOCALAPPDATA%`, so copy the two files there first or re-run `init` and `task add` (commands in `live-run-pr3.md`). Then from the repo root:
   `python tools/autonomy-controller/controller.py --state <db> --policy <policy> --holder live-pr3 --json tick --adapter claude --gh cli` (launches: clone of master onto `auto/live-pr3`, one `claude -p` with `--max-turns 8`), repeat `tick` until `settled` (the controller commits, pushes and opens the PR; `pr list` shows it), then `tick` again: `blocked: pr-open-blocks-dispatch` while the five real checks run, journal `pr-observed` rows carry `green`/`pending` by head SHA; `pr observe <n>` on demand. When the five are green the guard keeps it pending: close the PR by hand (`gh pr close <n> --delete-branch`), `tick` → `pr: pr-closed-unmerged`, task `parked-failure`. Keep `status --json`, `pr show <n>` and the journal as evidence; the cost is one worker run (about 0.05 USD-estimate at the phase B rate).

## 6. Environment facts

- Five required checks on master: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary`; strict + enforce_admins; merges serial. #771's checks: `cert-gate` pass, `build-core` pass, `shell-lint` pass, `lychee (README + docs)` pass, `Readiness summary` pass (diff-skipped); 9 passing, 4 skipped, 0 failing at merge.
- Repo gates: 29. On this Windows host 27 pass; `tests/scripts/mutation-check.test.sh` (symlink fixture) and `tests/scripts/session-start-hook.test.sh` (docker wrapper) fail on master too. `scripts/mutation-check.sh` calls `python3`, which on this host is the Store stub: put a directory first on PATH (POSIX form, `/c/Users/...`; a `C:/...` PATH entry is ignored by Git Bash) holding an executable `python3` that `exec`s `C:/Users/icfre/AppData/Local/Programs/Python/Python313/python.exe "$@"`.
- Host: Python 3.13, `gh` authenticated as `IanFrelinger` (scopes gist/read:org/repo/workflow), docker 29.8, image `ashlar-devtest:local` (Python 3.12 with `sqlite3`, git, no `claude`). Claude Code CLI 2.1.263, `auth status --json` says `loggedIn: true`, `authMethod: claude.ai`, `subscriptionType: max` (checked this phase; a stale login looks the same, see phase B).
- **The auto-mode classifier** (this phase): allowed `gh issue comment 695 --body-file`, `gh pr create`, `git push`, `python controller.py init|task add|tick --dry-run`; refused `python controller.py tick --adapter claude --gh cli` ("Create Unsafe Agents"), both alone and inside a compound command. Phase B saw the opposite on `gh issue comment`. Treat every external write as "try once, then ask".
- Python text-mode writes on this host produce CRLF, which `core.autocrlf=true` then shows as modified; write with `newline="\n"` or restore with `git checkout -- <file>`. Git Bash heredocs inside the Bash tool failed with "unexpected EOF" on a long Python body: write the script with the Write tool and run it by path.
- `gh pr list --head <branch>` and `gh pr merge --match-head-commit` are the two `gh` features the lifecycle relies on; both exist in the installed `gh` but were exercised only through the recording runner this phase.
- The repository's `.claude/hooks/session-start.sh` does nothing unless `CLAUDE_CODE_REMOTE=true`; the adapter strips `CLAUDE_CODE_*` from the worker env, so a worker clone of this repo does not run the docker setup.
- `gh pr view --json headRefOid` is the full 40-character sha and `gitops.checkout_pr_head` compares it exactly; never pass an abbreviation.

## 7. Queued work

- The live run of PR 3 (§5 step 9), then any shape fix it exposes.
- Post the three phase B bus files (owner).
- `drift-756` / `drift-769` / `drift-771` from Grok, when they post.
- The `phases` table is still unwritten; `premium_escalations_used` and `provider_transfers_used` are counted in `prs` but nothing drives them (PR 5 territory).
- `Controller.status()` `last_run` tiebreak now uses `rowid` for the PR row but the run row still orders by `run_id DESC` on a tied second; harmless, small follow-up.
- README "What is unverified" from phase B still stands (permission_denials for non-Bash tools; `num_turns` under cancel; the seven-day window).
- Owner rows for the integrator decisions (§3 Q2).

## 8. Ways of working the owner has endorsed

- One phase per thread, one PR per phase, ending with `/handoff`.
- Claim before you build; read the newest handoff and the bus first; reuse the card if one exists (phase C found #770 already created by an interrupted thread).
- Parallel lanes in isolated worktrees with a written contract and a shared base commit; verify the merged branch yourself.
- Every default taken without the owner is an integrator decision in the PR body and the handoff, never an owner row.
- Mutation-check every behaviour; quote the summary lines verbatim; say what was not observed failing and what only CI proves; a live run of the integrated branch is part of the phase, and when the session cannot perform it, say so and hand the exact command to the owner rather than claiming it.
- Declare nothing unverified as a capability; keep the four usage kinds separate; an estimated API dollar figure is not a charge.
- The owner performs logins, credential steps and (now) worker launches the session's classifier refuses.

## 9. Starting prompt for the next phase

> Execute **phase D (PR 4: operator controls, evidence retention, pilot)** of budgeted-autonomy. Run `/start-phase budgeted-autonomy`, or read this handoff in full starting with §0, then `tools/autonomy-controller/README.md` ("PR lifecycle" and "Not in PR 3" first). Check §1 against the live repo (master `d2c11b347d1759215b87bc98c94efced62f3b6d8`, #771 merged as `d2c11b347`, card #770 closed; the bus: three phase B posts may still be unposted; ask me). Create the PR 4 card as an `agent-task` issue and claim it. Then, before building, run the prepared live run of PR 3 (§5 step 9) or ask me to run it from my terminal, and record what the real `gh` and CLI returned. Then build PR 4: the operator status view, pause/cancel/resume/provider-disable at the next tick, backup and restore, LLM-free status reports, per-run evidence archives, metrics from the ledger, and the pilot of three bounded real tasks through the full lifecycle; ask me before the controller merges anything to master. Use parallel lane agents in isolated worktrees where it helps, but verify the merged branch yourself. Build and test only through `scripts/test-in-container.sh` for anything that needs `dotnet`; never run `dotnet` on the host. Mutation-check every behaviour with `scripts/mutation-check.sh`. If anything I have not decided blocks you, ask instead of choosing. Land PR 4 on the five required checks, post the bus `handoff` and `done` (or draft them and ask me if the classifier refuses), then run `/handoff budgeted-autonomy D` and notify me. Do not start PR 5.
