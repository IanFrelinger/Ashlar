# budgeted-autonomy handoff: end of phase E (PR 5), start of phase F (close-out)

*Written 2026-10-12 ~04:45 UTC at the end of phase E. Start the next thread with the prompt in §9, or with `/start-phase budgeted-autonomy`. The storage branch is `claude/budgeted-autonomy-workspace`, folder `_handoff/budgeted-autonomy/`; list it with `bash scripts/handoff-fetch.sh --workstream budgeted-autonomy --list`.*

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`):
- end each phase with `/handoff budgeted-autonomy <phase>`;
- start each phase with `/start-phase budgeted-autonomy`.

The handoff is published to `claude/budgeted-autonomy-workspace`, sent to the owner, and the owner is notified. Push every in-flight branch first.

**The roadmap's five PRs are now all merged.** Phase F is a close-out phase, not a sixth feature PR. It covers four things:
- the owner's live run of PR 5 and any shape fix that run exposes;
- the `done` on card #779;
- the drift audits Grok has not posted yet;
- the owner decisions still open.

Nothing beyond that starts without an owner decision.

**The lane pattern (phases B to E).** The owner asked again this phase to use parallel worktrees for throughput.
1. Write a lane contract. `pr5-lanes.md` is this phase's shape: one new module, one fake CLI and one test file per lane, with name-exact signatures. It quotes the observed CLI shapes and sets the rule "declare only what was observed".
2. Launch one Agent per lane with `isolation: "worktree"`. Tell it the base SHA, and tell it to `git reset --hard` to that SHA.
3. Build the integration yourself while the lanes run. This phase that was the transfer, the CLI and the docs.
4. Cherry-pick the lane commits.
5. Verify the merged branch yourself: host suite, container suite, and a mutation batch.
6. Remove the worktrees and their branches.

Both lanes this phase landed name-exact, in about 17 minutes each. Each reported where it deviated from the contract, with reasons. One of Lane A's notes led to a further probe and a fix: a resume without overrides would inherit the operator's sandbox.

## 1. Where things stand

**Master:** `b130228eecf7a76ff38cb2cb09425d8b41ca13eb`.

**Merged in this workstream:**

| PR | What | Merge SHA |
|---|---|---|
| #756 (phase A) | PR 1: operator policy and durable controller state | `1d8bc7448` |
| #769 (phase B) | PR 2: Claude adapter and bounded readiness | `f236bd4ad` |
| #771 (phase C) | PR 3: PR lifecycle and fresh context | `d2c11b347` |
| #773 (phase D) | PR 4: operator controls and evidence retention | `18b5da30f` |
| #774 (phase D) | OAuth refresh contention is `transient` | `3d14ce0b7` |
| #777, #778 (the PR 4 pilot, opened by the controller, merged by the owner) | `pilot-0` throwaway file; `pilot-1` card #762 citations | `f0fd9d113`, `ea71db40c` |
| **#780 (phase E)** | The pilot's findings: a resumed session keeps its workspace; the report prints a merged PR once; the pilot recorded in the README | `4003ccb0a` |
| **#781 (phase E)** | PR 5: Codex and Cursor adapters, bounded provider transfer, `--adapter` repeatable | `b130228ee` |

**Readiness verdict for master's head.** `scripts/release/readiness-verdict-for-sha.sh b130228ee...` says `REFUSED (no-readiness-run)`. That is the expected answer for a tooling-only diff: no readiness path changed, so `Readiness summary` was a diff-skipped pass on both PRs. It is not platform verification and is not claimed as such.

**Agent-bus (#695).** These posts are from this phase, all allowed by the classifier on the first try:

| Kind | Slug | Comment | File on the storage branch |
|---|---|---|---|
| `done` | card-762, pr-778 | 6117172464 | `12-done-card-762.md` |
| `claim` saying `released` | card-776 | 6117172624 | `13-claim-released-card-776.md` |
| `claim` | card-779 | 6117172777 | `14-claim-card-779.md` |
| `handoff` | pr-780 | 6117374828 | `15-handoff-pr-780.md` |
| `done` | pr-780 | 6117378559 | `16-done-pr-780.md` |
| `handoff` | pr-781 | 6117917652 | `17-handoff-pr-781.md` |
| `done` | pr-781 | 6117923991 | `18-done-pr-781.md` |

Still open on the bus:
- **My claim on card-779.** Its last criterion is the live run.
- **Grok's drift audits** of #756, #769, #771, #773, #774, #780 and #781. None have posted; Grok's last post was 2026-10-11 03:55.
- **Not this workstream's:** Grok's `ask` on `drift-746` and `drift-748`, and its `status` on `land-749-753` and `mutations-spec007`. The open PRs #750 to #753 belong to other agents.

The three phase B posts were posted in phase D.

**Cards:**
- #762 is closed (pilot-1, #778).
- #779 is open, assigned, claimed. Its acceptance is met except the live run.
- #776 (`--brief`) is open and released. Its partial edits are in pilot run `run-f9b0cf0ae47e`'s workspace on the host.

**Phase E's exit criteria** (from the phase D map: "provider transfer preserves task/PR/attempts/budget; the previous writer is stopped and reconciled; one small real task through each enabled adapter"):
- **Transfer preserves task, PR, attempts and pool: met.** Tests and mutations t1, t7 and t12 cover it (§4).
- **Previous writer stopped and reconciled: met.** Tests and mutations t2 to t5 cover it.
- **One small real task through each enabled adapter: NOT met.** This session's classifier refuses worker launches, as it did in phases B, C and D. It is the owner's step: `live-run-pr5.md`, against a ledger this session prepared (§4). Codex is logged in. Cursor is **not**: the owner must run `cursor-agent login` first, or skip Cursor.

**The PR 4 pilot ran in the owner's terminal between phases D and E** (2026-10-11 22:57 to 23:50 UTC). The phase D handoff called it not done; it is done now. Guarded; ledger `ctl-pilot-pr4`.

| Task | Runs | PR | Outcome |
|---|---|---|---|
| `pilot-0` | 1 run, 2 turns | #777 | merged `f0fd9d113` |
| `pilot-1` (card #762) | capped at 13 turns, then a resume reporting 48 | #778 | merged `ea71db40c`; pool 61 of 36 |
| `pilot-2` (card #776) | capped twice at 13 | none | `parked-budget`, 10 turns left |

`pause`, `resume`, `provider disable|enable`, `report`, `metrics`, five evidence archives and `backup` were all exercised. Evidence is `live-run-pr4-pilot-evidence.tar.gz` on the storage branch: status, metrics, report, journal, the five run archives, and the end backup's sha256.

Three findings came out of the pilot, all handled in #780:
1. A resumed session was handed a fresh clone, so the capped attempt's edits were discarded. **Fixed.**
2. The report printed `merged merged`. **Fixed.**
3. A resumed run reported `num_turns` 48 under `--max-turns 12`. A probe did not reproduce it: a resume with `--max-turns 2` stopped at 3 turns, and the run spawned no subagents. **Recorded as unverified in the README; still charged as reported.**

## 2. Read these first

**In the repo (master).** Read `CLAUDE.md`, then `tools/autonomy-controller/README.md`. In the README, these sections first:
- "Codex adapter", "Cursor adapter", "Provider transfer (PR 5)", "Not in PR 5";
- "What is unverified" under "Claude adapter", including the 48-turn item;
- "Not in PR 4", which holds the pilot record.

Then the code and tests:
- `ashlar_controller/adapters/{codex,cursor,__init__}.py`;
- `controller.py`: `transfer_task`, `transfers_used`, and the resume guard in `tick`;
- `lifecycle.py`: `reusable_workspace`, `resume_prompt` and `transfer_note`;
- `tests/test_{provider_transfer,codex_adapter,cursor_adapter}.py`;
- `tests/scripts/autonomy-controller.test.sh`: floor 430; the suite has 451 tests.

**The cards:** #755 (the roadmap), #779 (PR 5), #776.

**On the storage branch, `_handoff/budgeted-autonomy/`:**
- **Handoffs:** `handoff.md` (this file) and `handoff-phase-{A,B,C,D,E}.md`.
- **The owner's next run:** `live-run-pr5.md`.
- **This phase's design:** `pr5-lanes.md` (the lane contract and the observed shapes) and `card-779-body.md`.
- **PR bodies:** `pr-fix-body.md` (#780) and `pr-781-body.md` (#781).
- **Probes:**
  - Codex: `probe-codex-report-{1,2,3,4}.json` against the working binary, and `probe-codex-sandbox-bin-report.json` against the broken bundled one;
  - Cursor: `probe-cursor-report-1.json` (logged out);
  - Claude: `probe-claude-resume-turns-report.json`;
  - the scripts: `probe_codex_runtime{,_2,_3}.py`, `probe_cursor_runtime.py` and `probe_claude_resume_turns.py`.
  - Account ids and emails are redacted from the reports.
- **Mutations:** `mutations-pr780-summary.txt`, `mutations-pr781-summary.txt`, and `mutation-logs-pr{780,781}.tar.gz`, which include the batch runners `run.sh`, `run2.sh` and `lanes.sh`.
- **Evidence:** `live-run-pr4-pilot-evidence.tar.gz`. The README on master calls this `live-run-pr4-pilot-evidence/`; the tarball holds that folder, as `live-run-pr3-evidence.tar.gz` holds the PR 3 run's.
- **Bus posts:** `12-` to `18-*.md`.

## 3. Decisions

**Owner decisions this workstream relies on.** None are rows in a log; this workstream has no spec. From phase D, recorded in that handoff's addendum:
- the pilot is **guarded**: the controller never merges, and the owner reviews and merges by hand;
- worker launches stay **on the owner's terminal**, with no allow rule.

The owner's prompt in #755 is the brief.

**Integrator decisions taken in phase E** (in the PR bodies and the README; the owner can reverse any):
- **Workspace reuse.** A resumed session reuses its capped run's workspace only when that directory has content; otherwise it gets a fresh clone, and the session still resumes.
- **The 48-turn charge** stays as reported until the figure is explained.
- **Transfers are operator-initiated only** (`task transfer`). Nothing transfers a task automatically when a provider's allowance runs out or its login is missing: that would be a fallback launch on another provider's allowance.
- **The transfer count** lives per budget pool, in the append-only journal (`provider-transferred` rows over every task that shares the pool), and is mirrored on the open PR row's `provider_transfers_used`. The bound is checked before anything is stopped.
- **Units.** A transfer to a provider counting a different unit is refused, because units are never converted. The example's Cursor provider counts `requests` and Claude and Codex count `turns`, so a task in a `turns` pool can never move to Cursor.
- **Fresh context after a transfer.** The new provider never resumes another provider's session. It gets a fresh clone plus a one-line note naming the previous provider, run and stop reason.
- **Codex:**
  - `consumed_units` is the number of completed non-error items. A cancelled run, and one with no final result, are charged the full reservation; an unrecognised model is charged 0.
  - Resolver order: `codex` on PATH, then the newest app binary that has a code-mode host beside it, then the plugin app-server copy. A `codex.exe` with no host beside it is never used.
  - Both a fresh run and a resume override the sandbox, the approval policy, the effort, `mcp_servers={}` and the model. `--ignore-user-config` is never passed.
- **Cursor:**
  - It launches `node.exe index.js`, bypassing the `.cmd` (which cuts a multi-line prompt). A bare `.cmd` is refused for a prompt with cmd metacharacters.
  - An exit 0 with output is `completed` before the failure texts are looked at.
  - A speed other than standard, and a restricted `allowed_tools`, are refused before launch.
- The decisions from phases A to D stand. See the README's "Operating policy defaults (integrator decisions)".

**Open questions for the owner:**
1. **Cursor login.** Run `cursor-agent login` so Cursor can carry a live task, or leave Cursor out of the live run. This blocks Cursor's live criterion only.
2. **Should the controller ever transfer by itself?** For example, after `waiting-quota` on one provider. The current answer is no (integrator decision); a yes is a new policy key.
3. **Should the integrator decisions of phases A to E become durable owner rows?** For example, a `docs/specs/` note or an "owner decisions" README section. This has been open since phase D.
4. **The bus credential for the controller's own outbox posts** is still unexercised.
5. **What happens to card #776** (`--brief`): by hand, or as the controller's next real task through the fixed resume?

None of these blocks the start of phase F. Question 1 decides whether Cursor's live task happens at all.

## 4. Plan and live status

**Phase map:**

| Phase | PR | Scope | Ends when | Status |
|---|---|---|---|---|
| A | 1 | Budget policy and durable state | merged; criteria mutation-proven | done (#756) |
| B | 2 | Claude adapter and bounded readiness | one verified task; retry, no-fallback, cancel and funding rules proven | done (#769) |
| C | 3 | PR lifecycle and fresh context | head-pinned checks, duplicate events, closure is not success, fresh base and handoff | done (#771) |
| D | 4 | Operator controls, evidence, pilot | status, controls, backup and restore, archives, metrics; pilot of three tasks | done (#773, #774); pilot run by the owner (#777, #778; #776 parked) |
| E | 5 | Codex and Cursor adapters | transfer preserves task, PR, attempts and pool; previous writer stopped and reconciled; one real task per enabled adapter | done (#780, #781) **except** the live run (owner) |
| **F** | none planned | **Close-out** | live run recorded and its findings fixed; card #779 closed with evidence; drift asks answered; open owner questions put to the owner | **next** |

**Work items:**
- **`fix/budgeted-autonomy-pilot-findings`** at `e042ea7bb`: PR #780, merged (squash) as `4003ccb0a` on the five required checks. The branch is deleted.
- **`feat/budgeted-autonomy-codex-cursor-adapters`** at `41a0f3440`, made of four commits:
  - `d74f5c228`: the transfer and CLI integration;
  - `2404dd7e6`: lane A, Codex, cherry-picked from `e482d594c`;
  - `519f7b464`: lane B, Cursor, cherry-picked from `cd49c6d4b`;
  - `41a0f3440`: the wiring and the Codex resume overrides.

  It became PR #781, merged (squash) as `b130228ee` on the five required checks. The branch is deleted. The lane worktrees and branches are removed.
- **No branch is in flight.** The local `bx/WS-A/*` and `bx/WS-B/*` branches without upstreams are another workstream's and are untouched.

**Verification of #781:**
- Host suite: `Ran 451 tests`, `OK (skipped=1)`. Container suite at `41a0f3440`: `Ran 451 tests`, `OK`.
- Lane A has 29 tests and observed six hand mutations red. Lane B has 22 tests and observed twelve red.

**Mutation checks** ran through `scripts/mutation-check.sh` with `ASHLAR_MUTATION_RUNNER=bash tools/autonomy-controller/mutation-runner.sh`. All are verbatim in `mutations-pr781-summary.txt` and the PR body:
- 15 against `d74f5c228`: t1 to t15, all KILLED, for example `mutation t6-bound-off-by-one: KILLED red=failed:5/399 green=passed:398/399`.
- 14 against `41a0f3440`: a1 to a7, b1 to b6 and w1, all KILLED, for example `mutation a5-codex-resume-inherits-sandbox: KILLED red=failed:1/451 green=passed:450/451`.

The transfer code did not change between those two refs; only `run_evidence`'s transcript lookup moved, and w1 covers it. For #780, four mutations, f1 to f4, are all KILLED against `e042ea7bb`.

**The live-run ledger is prepared on the host.** It is under `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\ashlar-controller\pr5\`:
- controller `ctl-pr5-live`;
- policy `2026-10-12.pr5-live`, sha `c4f540d57de2...`. It has ceilings 12/36/3 and one repair round, the guard check kept, Codex `gpt-6-astra` and Cursor `composer-standard`, both enabled in policy;
- Cursor operator-disabled with `provider disable cursor`;
- three tasks on card 779: `pr5-codex` (codex, priority 10), `pr5-cursor` (cursor, 20) and `pr5-transfer` (codex, 30);
- the start backup `pr5\backups\00-start.db`, sha256 `5b322c3c...`;
- a dry run said `would-launch pr5-codex`, reservation 12, model `gpt-6-astra`.

From this session, the adapters' resolvers return:
- Codex: `%LOCALAPPDATA%\OpenAI\Codex\bin\2e5e00daee91c61d\codex.exe`;
- Cursor: `node.exe index.js` in `...\cursor-agent\versions\2026.10.01-e373342\`.

**Phase F, in order:**
1. Run `/start-phase budgeted-autonomy`. Read §2. Check §1 against the live repo and the bus, then post a `status` on card-779 naming phase F.
2. **Ask the owner to run `live-run-pr5.md`** from a Command Prompt, and to decide open question 1 first: Cursor login or skip. Record what the real `codex`, `cursor-agent` and `gh` returned:
   - the `launched` and `settled` reports;
   - `consumed`;
   - the archives (`evidence archive <run>`);
   - the PR outcomes;
   - the transfer's `stopped[].how` (it is `settled` if the Codex run finished first, `cancelled` otherwise).

   For Cursor, the first logged-in run's `result_text`, `event_types` and `session_id_candidates` are the first evidence of the real stream shape.
3. **Fix any shape the live run exposes** in one small PR with a test, mutation-checked. The likely candidates:
   - Cursor's real stream shape. Re-declare capabilities only for what that run shows. Resume may become `True` if a chat id appears.
   - Codex's real `consumed` against its item count.
   - Whether a Codex run that finished before the transfer behaves as the sheet says.
4. Post the `done` on card-779 with the live evidence, close #779, and release the claim.
5. Answer every `drift-<pr>` `ask` Grok has posted for #756 to #781 in one follow-up PR, closing each with a `done` on its slug. If none have posted, say so; do not invent audits.
6. Put open questions 2 to 5 to the owner with AskUserQuestion. Land any owner decision as a dated row in a records PR before code that depends on it, per CLAUDE.md.
7. Run `/handoff budgeted-autonomy F`. If the roadmap is closed, say so in the handoff, with what remains queued.

**Phase F exit criteria:**
- the live run is recorded in the ledger and the handoff; if Cursor was skipped, that is recorded with the owner's reason;
- its findings are fixed and merged;
- #779 is closed with a `done` naming the evidence;
- every posted drift ask is answered;
- open questions 2 to 5 are asked.

## 5. How to do the next phase's routine work

1. Use the session checkout, which is a clone (`.git` is a directory): `git checkout master && git pull --ff-only`, then `git checkout -b fix/budgeted-autonomy-<topic>`. Leave the untracked `AGENTS.md`, `.agents/` and the `pilot-*.json` and `pilot-report.md` files alone. The pilot files are the owner's copies of the evidence now on the storage branch.
2. Verify:
   - **Host suite:** `bash tests/scripts/autonomy-controller.test.sh`, about 110 s, `OK (skipped=1)` on Windows. Floor 430.
   - **Container suite:** `bash scripts/test-in-container.sh -- 'bash tests/scripts/autonomy-controller.test.sh'`. It tests the COMMITTED tree, so commit first. About 40 s for the suite, plus the build.
   - **Docs:** `bash scripts/check-docs-phantom-paths.sh`.
3. Commit, then mutation-check against the commit:
   - Put the python3 shim on PATH **in POSIX form** (`/c/Users/...`). A Windows-form path silently falls through to the Store stub, and every mutation reports `INVALID ... apply-failed`. This bit once this phase.
   - The batch runner shape is `mutation-logs-pr781.tar.gz`: `run.sh`, plus `lanes.sh` sourced by `run2.sh`.
   - Before starting a batch, check that each mutation's old text occurs exactly once: source the list with `run()` defined as a counting function.
   - Do not leave a sourced lane file beside a running batch's runner: a running `run.sh` sources it when it reaches the end.
   - Each mutation takes about 2.5 minutes alone, about 4 with two batches running side by side.
4. `git add <paths>` by name, then `python scripts/knowledge-graph/build-knowledge-graph.py`. It was unchanged for #780 and #781.
5. `git push -u origin <branch>`, then `gh pr create --body-file`. The desktop app binds the PR; `mcp__ccd_pr__get_status` shows its checks.
6. On the bus, draft every post as a file and post with `gh issue comment 695 --body-file`. Merge with `gh pr merge <n> --squash --match-head-commit <sha>` on the five required checks green unless Grok has posted a `block`. Then post the `done` with the merge SHA.
7. Live controller runs are the owner's, from a Command Prompt; see `live-run-pr5.md`. The session may run `status`, `report`, `metrics`, `tick --dry-run`, `init`, `task add`, `provider disable|enable` and `backup` against a ledger; all were allowed this phase.

## 6. Environment facts

- **Required checks.** There are five on master: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)` and `Readiness summary`. Strict and enforce_admins are on, and merges are serial. #781 merged with mergeState `UNSTABLE`: a non-required check was still pending, and none had failed.
- **Repo gates.** There are 29. On this Windows host 27 pass. `tests/scripts/mutation-check.test.sh` and `tests/scripts/session-start-hook.test.sh` fail on master too.
- **The python3 shim.** `python3` on this host is the Store stub. The shim this phase is `/c/Users/icfre/AppData/Local/Temp/claude/C--Users-icfre-Documents-Codex-2026-09-12-f-2-work-ashlar-record-correction/56c1a737-9604-4035-910c-22e974a23997/scratchpad/pybin/python3`, which `exec`s `C:/Users/icfre/AppData/Local/Programs/Python/Python313/python.exe "$@"`. Recreate it if temp reaping removed it.
- **Host tools:** Python 3.13, `gh` as `IanFrelinger`, docker 29.8 with image `ashlar-devtest:local`, Claude Code CLI 2.1.263.
- **Codex CLIs.** These facts are new this phase; the details are in memory `ashlar-provider-clis-on-host`.
  - The working one is the desktop app's `%LOCALAPPDATA%\OpenAI\Codex\bin\2e5e00daee91c61d\codex.exe` (0.162.0-alpha.17.2), with `codex-code-mode-host.exe` beside it. A copy sits in `~/.codex/plugins/.plugin-appserver/`.
  - **The broken one is `~/.codex/.sandbox-bin/codex.exe`** (alpha.2, no host): tools fail closed.
  - Nothing named `codex` is on PATH.
  - `codex login status` prints `Logged in using ChatGPT` on **stderr**.
  - The owner's `~/.codex/config.toml` sets `sandbox_mode = "danger-full-access"`, `approval_policy = "never"` and MCP servers. The adapter overrides the sandbox, the approval policy and `mcp_servers={}` on every run and resume.
- **Cursor CLI.** It is the Cursor IDE's embedded agent CLI, `2026.10.01-e373342`, under `%APPDATA%\Cursor\User\globalStorage\anysphere.cursor-agent-worker\agent-cli\.local\share\cursor-agent\versions\`. It is **not logged in**. `cursor --help` (the IDE) has an `agent` subcommand that the adapter does not use.
- **The auto-mode classifier this phase:**
  - allowed `gh issue create --label agent-task`, `gh issue comment 695` (seven times), `gh issue close`, `gh issue edit --add-assignee`, `gh pr create`, `gh pr edit`, `gh pr merge --squash --match-head-commit`, and `git push`;
  - allowed `controller.py init|task add|provider disable|backup|status|tick --dry-run`;
  - allowed the probes' small headless `codex exec` and `claude -p` runs: nine, four and three invocations, run from scratchpad scripts. **They are probes of the CLIs, not controller worker launches.**
  - Worker launches through `tick --adapter ...` were not attempted, since they were refused deterministically in phases B to D.
- **Line endings.** The checkout has `core.autocrlf=true`: Markdown is CRLF on disk, and Python and JSON are LF. A Python patch must preserve the file's line endings.
- **Heredocs in the Bash tool.** They break on long bodies with quotes, and turn `\n` inside a Python string literal into a real newline (twice this phase). Write the script with the Write tool and run it by path, or use the Edit tool.
- **`scripts/test-in-container.sh`** tests the committed tree, not the working tree.

## 7. Queued work

- **The PR 5 live run** (owner): `live-run-pr5.md`. Then any shape fix, and the `done` on card-779.
- **Grok's drift audits** of #756, #769, #771, #773, #774, #780 and #781, when they post.
- **Card #776** (`--brief`). Released; the partial edits are in pilot workspace `run-f9b0cf0ae47e`. Taking it needs an owner choice (open question 5). It touches the schema (version 3).
- **The 48-turn resumed run** (`run-3225ebdb59dd`). The cause is unknown and the charge stands. A second sighting should be recorded with its stdout result, which the evidence archive keeps.
- **The `phases` table.** It is still unwritten. `premium_escalations_used` is counted and still not driven. `provider_transfers_used` is now driven by `task transfer`.
- **Not in PR 5** (README):
  - no automatic transfer;
  - no turn limit on Codex or Cursor, so their runs are bounded by the budget, the attempts and `run cancel`;
  - no allowance figure from either CLI;
  - Cursor's stream shape is unknown until a logged-in run.
- **Not in PR 4:**
  - nothing prunes `<state dir>/evidence/`;
  - `restore` does not stop a controller that holds the lease.
- **Owner rows for the integrator decisions:** open question 3.

## 8. Ways of working the owner has endorsed

- One phase per thread, one PR per phase (here: a findings PR, then PR 5), ending with `/handoff`.
- Claim before you build. Read the newest handoff and the bus first. Reuse the card if one exists.
- **Parallel lanes in isolated worktrees for throughput**, asked for again this phase. Write the contract first; verify the merged branch yourself.
- Every default taken without the owner is an integrator decision in the PR body and the handoff, never an owner row.
- **Declare nothing unverified as a capability.** Probe the CLI, save the report, cite the probe id in the code. When a lane says a shape was taken from the contract rather than a report, probe it before merging (this phase: the Codex resume form).
- Mutation-check every behaviour. Quote the summary lines verbatim. Say what was not observed and what only the owner's terminal can prove.
- The owner performs logins, credential steps and worker launches.
- Fix findings from a live run in a small PR, before the next feature PR.

## 9. Starting prompt for the next phase

> Execute **phase F (close-out)** of budgeted-autonomy. Run `/start-phase budgeted-autonomy`, or read this handoff in full, starting with §0, then the README sections §2 names. Check §1 against the live repo and the bus: master `b130228ee`, #780 and #781 merged, card #779 open with my claim.
>
> Ask me to run `live-run-pr5.md` from my terminal. First ask whether I will log the Cursor CLI in (`cursor-agent login`) or skip Cursor. Record what the real `codex`, `cursor-agent` and `gh` returned. Fix any shape the run exposes in one small PR with a test, mutation-checked with `scripts/mutation-check.sh`, and merged on the five required checks.
>
> Then post the `done` on card-779 with the evidence, close #779, answer any `drift-<pr>` asks Grok has posted for #756 to #781 in one follow-up PR, and ask me open questions 2 to 5 of §3. If anything I have not decided blocks you, ask instead of choosing; land a decision as a dated row before code that depends on it.
>
> Build and test only through `scripts/test-in-container.sh` for anything that needs `dotnet`; never run `dotnet` on the host. Use parallel lane agents in isolated worktrees where they help. Do not start new features. When the phase is done, run `/handoff budgeted-autonomy F` and notify me.
