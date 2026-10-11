# budgeted-autonomy handoff: end of phase D (PR 4), start of phase E (PR 5)

*Written 2026-10-11 ~22:05 UTC at the end of phase D. Start the next thread with the prompt in §9, or with `/start-phase budgeted-autonomy`. Storage branch `claude/budgeted-autonomy-workspace`, folder `_handoff/budgeted-autonomy/` (`bash scripts/handoff-fetch.sh --workstream budgeted-autonomy --list`).*

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`): end each phase with `/handoff budgeted-autonomy <phase>`; start each phase with `/start-phase budgeted-autonomy`. One phase is one of the five roadmap PRs (the owner's prompt of 2026-10-11, quoted in #755). The handoff is published to `claude/budgeted-autonomy-workspace`, sent to the owner, and the owner is notified. Push every in-flight branch to GitHub before writing the handoff. Do not implement more than one PR per thread.

**Throughput pattern (phases B, C and D):** split the PR into lanes with a written interface contract (`pr4-lanes.md` on the storage branch is the phase D shape: one new module and one new test file per lane, name-exact signatures, the integrator owns `cli.py`, `controller.py`, README and CHANGELOG), launch one agent per lane in an isolated worktree (Agent tool, `isolation: "worktree"`) telling it the base SHA and to `git reset --hard` to it, build the integration lane yourself while they run, cherry-pick the lane commits, then verify the merged branch yourself (suite on the host, suite in the container, mutation batch). In phase D both lanes landed name-exact and the integration needed one test fix (a lane A test asserted the base's `status` had no `evidence` key, which lane C added). No shared base commit was needed because the lanes shared only names.

## 1. Where things stand

- **Master:** `3d14ce0b76bed1c8b9b6e864cb97687c5ec5f2ca`.
- **Merged in this workstream:**

| PR | What | Merge SHA |
|---|---|---|
| #756 (phase A) | PR 1: operator policy and durable controller state | `1d8bc7448` |
| #769 (phase B) | PR 2: Claude adapter and bounded readiness | `f236bd4ad` |
| #771 (phase C) | PR 3: PR lifecycle and fresh context | `d2c11b347` |
| #773 (phase D) | PR 4: operator controls and evidence retention (`metrics.py`, `evidence.py`, `status` evidence paths, `report`, `metrics`, `backup`, `restore`, `evidence archive|list`, controls proven at the next tick) | `18b5da30f` |
| #774 (phase D follow-up) | the live-run finding: OAuth refresh contention is `transient`; README and CHANGELOG record the PR 3 live run | `3d14ce0b7` |

- **Readiness verdict for master's head:** `scripts/release/readiness-verdict-for-sha.sh <full sha>` says ``REFUSED (no-readiness-run)` for both `18b5da30f` and `3d14ce0b7``. PR 4 is a tooling-only diff (no readiness path), so the PR's `Readiness summary` was a diff-skipped pass; that is not platform verification and is not claimed as such. The newest push-triggered master run that succeeded is still `9faefe46c` (#749).
- **Agent-bus (#695):** phase D posted `claim` card-772 (comment 6112885950), `handoff` pr-773 (6113119610) and `done` pr-773/card-772 (6114252973; plus `handoff` pr-774 (6114275255) and `done` pr-774); all allowed by the classifier on the first try. **The three phase B posts are still unposted** (`01-claim-card-768.md`, `02-handoff-pr-769.md`, `03-done-pr-769.md` on the storage branch); the owner asked to be asked. Open items not this workstream's: Grok's `ask` on `drift-746`, `drift-748`; `status` on `land-749-753`, `mutations-spec007`. Grok's drift audits of #756, #769, #771 and #773 have not posted; the next phase answers them with a follow-up PR and `done` on the slug.
- **Phase D exit criteria** (card #772): the operator can see state, remaining permitted attempts, funding pool, blocker, PR record and evidence paths without an LLM; pause, cancel, resume and provider-disable take effect at the next tick; a backup can be restored and the restored ledger refuses nothing it should accept; an evidence archive holds manifest, stdout, stderr, transcript path and the PR record; metrics are counts from the ledger; every behaviour mutation-red. **Met in tests and mutations** (§4). **The live run of PR 3 was performed by the owner from a Command Prompt on 2026-10-11 21:18 to 21:39 UTC** (the session's classifier refused the worker launch for the third phase running): attempt 1 `run-1ced8587a0bb` died at once on OAuth refresh contention with the desktop app's Claude Code processes (1 turn charged, filed `transient` once #774's rule was in the checkout, requeued by hand); attempt 2 `run-561ad9689fe5` completed in 3 turns (0.0825 USD-estimate, five-hour window 32 percent), the controller committed as `ashlar-controller`, pushed `auto/live-pr3`, opened PR #775 at `a84e0e9360c13e9e17700d096e317af81a1cbc47` on base `d2c11b347`, observed it by head SHA (`pr-observed` rows: three green and two pending; then five green and the guard pending, verdict `pending`, no merge), and after the hand close recorded `pr-closed-unmerged`, task `parked-failure`. Pool: 4 of 16 turns, 2 of 3 attempts. Evidence: `live-run-pr3-evidence/` on the storage branch (status.json, metrics.json, report.md, journal.txt, pr-775.txt) and, on the host, the two archives under `<state dir>/evidence/` and the backup `backups/live-pr3-end.db` (sha256 `0de627046df5...`). Findings: (1) the OAuth refresh-contention text was classified `auth-missing`; fixed in #774 (`transient`); (2) the PR title and commit subject are the whole task title (cosmetic, queued). **Not met: the pilot of three bounded real tasks**, prepared for the owner's terminal (`pilot-pr4.md`); the first step of phase E, or an owner step before it. The `claude` provider is left DISABLED on the live ledger (`provider enable claude` to use it again).

## 2. Read these first

- **In the repo (master):** `CLAUDE.md`; `tools/autonomy-controller/README.md` in full, especially "Operator controls and evidence (PR 4)", "PR lifecycle", "What one tick does", the stop-reason table, "Operating policy defaults (integrator decisions)", "Not in PR 4" and "Not in PR 3"; `ashlar_controller/{metrics,evidence,controller,cli,lifecycle,prs,gitops}.py`; `ashlar_controller/adapters/{base,fake,claude}.py` (phase E adds adapters beside them); `tests/{test_operator_controls,test_cli_operator,test_metrics,test_evidence,test_pr_lifecycle,test_claude_adapter}.py`; `tests/scripts/autonomy-controller.test.sh` (floor 360; suite 383); `_handoff/bus/PROTOCOL.md`; `_handoff/phases/README.md`.
- **The cards:** #755 (the roadmap), #772 (PR 4; closed with the merge SHA), and the PR 5 card once created.
- **On the storage branch, `_handoff/budgeted-autonomy/`:** `handoff.md` (this), `handoff-phase-{A,B,C,D}.md`, `pr-773-body.md`, `pr4-lanes.md`, `live-run-pr3-evidence.tar.gz` (status.json, metrics.json, report.md, journal.txt, pr-775.txt), `mutations-pr4-summary.txt`, `mutation-logs-pr4.tar.gz`, `mutations-pr4-run.sh` and `mutations-pr4-run-lanes.sh` (the batch runner), `live-run-pr3.md` and `pilot-pr4.md` (the owner's procedures), `07-claim-card-772.md`, `08-handoff-pr-773.md`, `09-done-pr-773.md`, `10-handoff-pr-774.md`, `11-done-pr-774.md`, plus the phase A to C files.

## 3. Decisions

- **Owner decisions this workstream relies on:** none recorded (no spec, no decisions log). The owner's prompt in #755 is the brief.
- **Integrator decisions taken in phase D** (PR body of #773 and the README; the owner can reverse any):
  - A cancelled repair run leaves the task `cancelled` (the operator took over), not `pr-open`; the PR is still watched for a merge or a closure (a merge by hand is recorded and makes the task `done`), and `task set-state <id> pr-open` hands it back to the controller.
  - `restore` refuses a `schema_version` it does not know, including a newer one; `1` is accepted because `Store.open` migrates it in place. Every refusal exits 3.
  - Archives live under `<state dir>/evidence/`, are written once per run, never overwritten and never pruned.
  - `restore`'s recorded sha256 is the source file's (the restored file changes the moment `restored` is journalled into it).
  - Phases A to C's decisions stand (serial PRs; the worker only edits; `success` and `skipped` the only green conclusions; `claude-sonnet-5`, `acceptEdits`, `--strict-mcp-config`, route `claude.ai`; the `percent` allowance rule).
- **Open questions for the owner (the first two gate the pilot, none gates building PR 5):**
  1. **Launching workers from a Claude Code session.** Refused three phases running. Options: the owner runs the controller from their own terminal (the pilot's natural shape: the controller is an operator process), or an allow rule for the controller command in the project's `.claude/settings.json`. Until decided, every live run is an owner step.
  2. **Whether the pilot may let the controller merge to master** (`pilot-pr4.md` §0: guarded with a sixth never-running required check so the owner merges by hand and the controller records it, or unguarded).
  3. Whether the integrator decisions of phases A to D become durable owner rows (a `docs/specs/` note or an "owner decisions" README section).
  4. The bus credential for the controller's own posts: still unexercised.
  5. Which three cards the pilot uses (`pilot-pr4.md` §2 names candidates #762, #761, #757, or three throwaway tasks).

## 4. Plan and live status

**Phase map (one PR per phase):**

| Phase | PR | Scope | Ends when | Status |
|---|---|---|---|---|
| A | 1 | Budget policy and durable controller state | merged on green; criteria mutation-proven | done (#756) |
| B | 2 | Claude adapter and bounded readiness | one task verified with a recoverable handoff; retry/no-fallback/cancel/funding rules proven | done (#769) |
| C | 3 | PR lifecycle and fresh context | old green cannot satisfy a changed head; duplicate events create no PR or worker; closure without merge is not success; next task uses updated base and compact handoff; transcript not injected | done (#771); live run still pending (owner) |
| D | 4 | Operator controls, evidence retention, pilot | status view, pause/cancel/resume/provider-disable, backups and restore, LLM-free reports, evidence archives, metrics; three bounded real tasks through the full lifecycle | **done (#773)** for everything but the pilot, which is prepared for the owner (§1) |
| **E** | **5** | **Codex and Cursor adapters** | provider transfer preserves task/PR/attempts/budget; the previous writer is stopped and reconciled; one small real task through each enabled adapter | **next** |

**Work items:** `fix/budgeted-autonomy-oauth-refresh-contention` @ `8948edf60` (fix `9031946ae`, docs `1cce2fa6c`, master merged in), PR #774, **merged** (squash) as `3d14ce0b7` on the five required checks green, no `block`. `feat/budgeted-autonomy-operator-controls` @ `1f186b8ab` (lane C `7d198b140`, lane B `c7911d3e6`, lane A `596d1f188`, integration `1f186b8ab`), PR #773, **merged** (squash) as `18b5da30f` on the five required checks green, no `block`; branch deleted; card #772 closed with the merge SHA. Lane worktrees and branches removed after the cherry-picks.

**Mutation checks** (`scripts/mutation-check.sh` with `ASHLAR_MUTATION_RUNNER=bash tools/autonomy-controller/mutation-runner.sh`, ref `1f186b8ab`), verbatim:

```
mutation c1-pause-ignored-by-tick: KILLED red=failed:6/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c2-disabled-provider-still-repairs: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c3-cancelled-task-may-launch-repair: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c4-cancel-skips-the-adapter: KILLED red=failed:3/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c5-evidence-transcript-not-located: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c6-evidence-archive-reported-when-absent: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c7-status-last-run-by-run-id: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation c8-restore-replaces-without-flag: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation a1-consumed-total-includes-estimate: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation a2-active-states-drop-running: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation a3-estimate-caveat-dropped: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation a4-pr-outcomes-counted-by-state: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation a5-report-drops-evidence: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b1-unknown-schema-accepted: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b2-stale-sidecars-kept: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b3-restore-ignores-replace: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b4-archive-overwrites: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b5-ledger-drops-the-task-pr: KILLED red=failed:1/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b6-transcript-claimed-when-absent: KILLED red=failed:3/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
mutation b7-backup-overwrites: KILLED red=failed:2/383 green=passed:382/383 ref=1f186b8ab2492aaa41cedbcc1cbfcc04c9dcd817 runner=override
```

Lane observations by hand (each lane applied three mutations and watched its named test fail; the full text is in the PR body): lane A, `metrics.py`: `ACTIVE_RUN_STATES` without `running` (`0.0 != 20.0`), `consumed_total` plus the estimated API value (`3.28 != 3.0`), the estimate caveat line deleted (two tests red); lane B, `evidence.py`: `_remove_sidecars(dest_path)` replaced by `pass` (a planted stale WAL replayed, `'yes' is not None`), `KNOWN_SCHEMA_VERSIONS` with `"99"` (the refusal moved from the pre-write guard to `Store.open` after the file was in place), the task-level PR fallback in `_ledger_slice` disabled (`ledger["pr"]` None). Live-run finding fixed in the follow-up #774 (`fix/budgeted-autonomy-oauth-refresh-contention`, merged as `FIX_18b5da30f`): the CLI's OAuth refresh-contention text is `transient`, test observed red `('failed', 'auth-missing') != ('failed', 'transient')`; it also carries the README and CHANGELOG lines about the live run. Lane A also found and fixed, before committing, an active-reservation query that hard-coded `IN (?,?,?)`, so its constant and SQL could not drift.

**Phase E (PR 5), spelled out, in order:**
1. `/start-phase budgeted-autonomy`; read §2; create the PR 5 card as an `agent-task` issue from the PR 5 row above (acceptance criteria from #755: a provider transfer preserves the task, its PR, its attempts and its budget pool; the previous writer is stopped and reconciled before the new one launches; `limits.provider_transfers_per_pr` bounds it; one small real task through each enabled adapter); post `claim` on the bus.
2. **Run, or ask the owner to run, the PR 3 live run and the PR 4 pilot** (`live-run-pr3.md`, `pilot-pr4.md`); record what the real `gh` and the CLI returned; fix any shape they expose in a small PR with a test before PR 5 proper.
3. Probe the Codex and Cursor CLIs the way phase B probed Claude (`probe_claude_runtime.py` on the storage branch is the shape): headless invocation, result shape, turn limit, session resume, cancel, usage source, auth status. Declare only what was observed in each adapter's `AdapterCapabilities`; the policy ids `sol` and `composer-standard` are candidates until a probe accepts them.
4. Build PR 5 in lanes: `adapters/codex.py`, `adapters/cursor.py`, the transfer in `controller.py`/`lifecycle.py` (stop and reconcile the previous writer; `prs.provider_transfers_used` counted before the launch; the task, PR row and pool unchanged; the new run's prompt is the compact handoff, never the previous transcript), the CLI (`tick --adapter codex|cursor`, `task transfer`), README, CHANGELOG, DocsIndex, the floor.
5. Keep the evidence rules: mutation-check every behaviour; summary lines verbatim in the PR body; `scripts/test-in-container.sh` for anything with `dotnet`.
6. PR body per §5; `handoff` on the bus; merge on green; `done` with the merge SHA; `/handoff budgeted-autonomy E`.

## 5. How to do the next phase's routine work

1. Clone or use the session checkout (it is a clone: `.git` is a directory). `git checkout master && git pull --ff-only`, then `git checkout -b feat/budgeted-autonomy-<topic>`. Never run `scripts/test-in-container.sh` from a worktree. Leave the untracked `AGENTS.md` and `.agents/` alone (a Codex mirror of CLAUDE.md the owner keeps uncommitted).
2. Parallel lanes: write the contract first (`pr4-lanes.md` shape), launch one agent per lane with `isolation: "worktree"` telling it the base SHA and to `git reset --hard` to it, build the integration yourself meanwhile, cherry-pick the lane commits, then verify the merged branch yourself. Remove the worktrees and their branches after the cherry-picks (`git worktree remove --force`, `git branch -D`).
3. Verify: `bash tests/scripts/autonomy-controller.test.sh` (host; `OK (skipped=1)` on Windows; about 70 s); `bash scripts/test-in-container.sh -- 'bash tests/scripts/autonomy-controller.test.sh'` (container; it tests the COMMITTED tree, so commit first); `bash scripts/check-docs-phantom-paths.sh`; `bash -n` on shell files. `bash scripts/ci/run-repo-gates.sh` on this host has the two known Windows failures (§6); `shell-lint` on ubuntu is the check that counts.
4. Commit, then mutation-check against the commit with the python3 shim first on PATH (§6): the batch runner is `mutations-pr4-run.sh` plus `mutations-pr4-run-lanes.sh` on the storage branch (shape: one `run <id> <file> <old> <new>` per mutation; `bash run.sh <sha> [ids...]`; it appends the summary lines to `summary.txt`). Quote each `mutation <id>: ...` line verbatim in the PR body. Each mutation is about two and a half minutes (two suite runs plus a clone), so start the batch in the background right after the commit.
5. `git add <paths>` (named paths, never `-A`) then `python scripts/knowledge-graph/build-knowledge-graph.py`; commit the graph if it changed (unchanged for PR 3 and PR 4).
6. `git push -u origin <branch>`; `gh pr create --body-file`; `gh pr edit --body-file` to add the mutation lines after the batch; the desktop app binds the PR (`mcp__ccd_pr__get_status` shows the checks).
7. Bus: draft every post as a file; try `gh issue comment 695 --body-file` once; if refused, name the file and ask the owner. Merge (squash) on the five required checks green unless Grok posted `block`; `done` with the merge SHA; `gh issue close <card> --comment` works.
8. Read the readiness verdict for the merge SHA; for a tooling-only diff it says `REFUSED (no-readiness-run)`, the expected answer.
9. **Live runs of the controller** (owner, or a terminal outside a Claude Code session): `live-run-pr3.md` (state `%LOCALAPPDATA%\ashlar-controller\state\controller.db`, policy `%LOCALAPPDATA%\ashlar-controller\policy.json`, task `live-pr3` queued, dry run `would-launch`, reservation 8) and `pilot-pr4.md` (a separate `pilot/` state and policy, three tasks, the controls exercised once each, the evidence commands). From this session's Git Bash the real `%LOCALAPPDATA%` and the app's `...\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\` show the same files; the owner checks which one holds them. `report`, `metrics` and `status --json` work against that ledger already (exercised in phase D with zero runs).

## 6. Environment facts

- Five required checks on master: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary`; strict + enforce_admins; merges serial. #773's checks at merge: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary` all pass (the last diff-skipped), `mergeStateStatus CLEAN`; #774 the same at `8948edf60`.
- Repo gates: 29. On this Windows host 27 pass; `tests/scripts/mutation-check.test.sh` (symlink fixture) and `tests/scripts/session-start-hook.test.sh` (docker wrapper) fail on master too. `scripts/mutation-check.sh` calls `python3`, which on this host is the Store stub: put a directory first on PATH (POSIX form) holding an executable `python3` that `exec`s `C:/Users/icfre/AppData/Local/Programs/Python/Python313/python.exe "$@"`; the phase B shim at `/c/Users/icfre/AppData/Local/Temp/claude/C--Users-icfre-Documents-Codex-2026-09-12-f-2-work-ashlar-record-correction/bacf9380-784e-43f2-a8be-bb8d20a25b64/scratchpad/pybin/python3` still worked in phase D (temp reaping may remove it; recreate if gone).
- Host: Python 3.13, `gh` authenticated as `IanFrelinger` (scopes gist/read:org/repo/workflow), docker 29.8, image `ashlar-devtest:local` (Python 3.12 with `sqlite3`, git, no `claude`). Claude Code CLI 2.1.263.
- **The auto-mode classifier** (this phase): allowed `gh issue create --label agent-task`, `gh issue comment 695 --body-file` (three times), `gh pr create`, `git push`, `python controller.py status|report|metrics|tick --dry-run`; refused `python controller.py tick --adapter claude --gh cli` ("Create Unsafe Agents"), as in phases B and C. Treat the worker launch as deterministically refused from a session; do not spend a try on it unless the owner has added a permission rule.
- Files on this host: the checkout has `core.autocrlf=true`, so Markdown files are CRLF on disk and LF in git; a Python patch must preserve the file's own line ending (read with `newline=""`, replace, write with `newline=""`), and new `.py` files are written LF. Git Bash heredocs inside the Bash tool fail with "unexpected EOF" on a long body with quotes: write the script with the Write tool and run it by path (`python -I <script> <target>`).
- `scripts/test-in-container.sh` tests the committed tree, not the working tree: an uncommitted test fix shows as red in the container while green on the host.
- The repository's `.claude/hooks/session-start.sh` does nothing unless `CLAUDE_CODE_REMOTE=true`; the adapter strips `CLAUDE_CODE_*` from the worker env.

## 7. Queued work

- The pilot of PR 4 (§5 step 9, `pilot-pr4.md`), then any shape fix it exposes. The live run of PR 3 is done; its one shape fix is #774.
- Post the three phase B bus files (owner).
- `drift-756` / `drift-769` / `drift-771` / `drift-773` from Grok, when they post.
- The `phases` table is still unwritten; `premium_escalations_used` and `provider_transfers_used` are counted in `prs` but nothing drives them (PR 5 territory).
- `Controller.status()` `last_run` now orders by `rowid`; the `prs` row too. Done.
- README "What is unverified" from phase B still stands (permission_denials for non-Bash tools; `num_turns` under cancel; the seven-day window).
- Nothing prunes `<state dir>/evidence/`; `restore` does not stop a controller holding the lease on the replaced file (documented in "Not in PR 4").
- Owner rows for the integrator decisions (§3 Q3).

## 8. Ways of working the owner has endorsed

- One phase per thread, one PR per phase, ending with `/handoff`.
- Claim before you build; read the newest handoff and the bus first; reuse the card if one exists.
- Parallel lanes in isolated worktrees with a written contract; verify the merged branch yourself.
- Every default taken without the owner is an integrator decision in the PR body and the handoff, never an owner row.
- Mutation-check every behaviour; quote the summary lines verbatim; say what was not observed failing and what only CI proves; a live run of the integrated branch is part of the phase, and when the session cannot perform it, say so and hand the exact command to the owner rather than claiming it.
- Declare nothing unverified as a capability; keep the four usage kinds separate; an estimated API dollar figure is not a charge.
- The owner performs logins, credential steps and worker launches the session's classifier refuses.

## 9. Starting prompt for the next phase

> Execute **phase E (PR 5: Codex and Cursor adapters)** of budgeted-autonomy. Run `/start-phase budgeted-autonomy`, or read this handoff in full starting with §0, then `tools/autonomy-controller/README.md` ("Operator controls and evidence (PR 4)", "PR lifecycle", "Claude adapter", "Not in PR 4" first). Check §1 against the live repo (master `3d14ce0b76bed1c8b9b6e864cb97687c5ec5f2ca`, #773 merged as `18b5da30f`, card #772 closed; the bus: three phase B posts may still be unposted; ask me). Create the PR 5 card as an `agent-task` issue and claim it. Before building, ask me to run the PR 3 live run and the PR 4 pilot from my terminal (`live-run-pr3.md`, `pilot-pr4.md`) and record what the real `gh` and CLI returned; tell me the two decisions they need (launching from a session; whether the controller merges). Then probe the Codex and Cursor CLIs as phase B probed Claude, build PR 5 (the two adapters declaring only observed capabilities, the bounded provider transfer that preserves task, PR, attempts and budget and stops and reconciles the previous writer, `tick --adapter codex|cursor`, docs), one small real task through each enabled adapter when I can run it. Use parallel lane agents in isolated worktrees where it helps, but verify the merged branch yourself. Build and test only through `scripts/test-in-container.sh` for anything that needs `dotnet`; never run `dotnet` on the host. Mutation-check every behaviour with `scripts/mutation-check.sh`. If anything I have not decided blocks you, ask instead of choosing. Land PR 5 on the five required checks, post the bus `handoff` and `done` (or draft them and ask me if the classifier refuses), then run `/handoff budgeted-autonomy E` and notify me. Do not start anything beyond PR 5.

## Addendum (2026-10-11 ~22:30 UTC, after the first publish)

The owner accepted the recommendations: the pilot is **guarded** (the controller never merges; the owner reviews and merges by hand), worker launches stay on the owner's terminal (no allow rule), and the pilot's tasks are one throwaway plus two real cards: **#762** (citation lines in `docs/EgressInventory.md`) and **#776** (new card: an optional `--brief` short title for controller-opened PRs; schema 3). #761 was left out (it touches `src/` and depends on R3a). Prepared on the host (paths with forward slashes; the app's redirected folder is where the files physically are): pilot ledger `ctl-pilot-pr4` at `%LOCALAPPDATA%/Packages/Claude_pzs8sxrjxfjjc/LocalCache/Local/ashlar-controller/pilot/state/controller.db` with policy `.../pilot/policy.json` (version `2026-10-11.pilot-pr4`, ceilings 12/36/3, `repair_rounds` 1, the guard check kept), tasks `pilot-0`, `pilot-1` (card 762), `pilot-2` (card 776) queued, the first with `--base-sha 3d14ce0b7`, dry run `would-launch pilot-0`, start backup `.../pilot/backups/00-start.db`. Claims posted on the bus for `card-762, card-776` (comment 6114583103); both cards self-assigned. The three phase B bus posts are now posted (6114577979, 6114578105, 6114578310). The owner's command sheet is `pilot-commands.md` beside this file. Phase E starts after the pilot's PRs are merged or closed and the `done` posts are made on `card-762` and `card-776`.
