# budgeted-autonomy handoff: end of phase B (PR 2), start of phase C (PR 3)

*Written 2026-10-11 ~07:40 UTC at the end of phase B. Start the next thread with the prompt in §9, or with `/start-phase budgeted-autonomy`. Storage branch `claude/budgeted-autonomy-workspace`, folder `_handoff/budgeted-autonomy/` (`bash scripts/handoff-fetch.sh --workstream budgeted-autonomy --list`).*

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`): end each phase with `/handoff budgeted-autonomy <phase>`; start each phase with `/start-phase budgeted-autonomy`. One phase is one of the five roadmap PRs (the owner's prompt of 2026-10-11, verbatim in #755). The handoff is published to `claude/budgeted-autonomy-workspace`, sent to the owner, and the owner is notified. Push every in-flight branch to GitHub before writing the handoff. Do not implement more than one PR per thread.

**Throughput pattern the owner asked for in phase B and that worked:** split the PR into lanes with a written interface contract, run one agent per lane in an isolated git worktree (Agent tool, `isolation: "worktree"`), cherry-pick the lane commits onto the feature branch, then verify the merged branch yourself (suite, container, repo gates, `scripts/mutation-check.sh`). Two cautions: a lane worktree was handed to each agent at master, not at the feature branch (each agent had to `git reset --hard <base>`; tell them the base SHA and to check it), and the lane stubs hid three real-CLI shapes that only the live run exposed (§6), so a live run of the integrated branch is part of the phase, not an optional extra.

## 1. Where things stand

- **Master:** `f236bd4ade44caef0718a1fe77e2b00e9bff632a`.
- **Merged in this workstream:**

| PR | What | Merge SHA |
|---|---|---|
| #756 (phase A) | PR 1: operator policy and durable controller state | `1d8bc7448` |
| #769 (phase B) | PR 2: Claude adapter and bounded readiness; three PR 1 defects fixed (`task set-state`, Windows pid check, POSIX-only test skip) | `f236bd4ad` |

- **Readiness verdict for master's head:** `scripts/release/readiness-verdict-for-sha.sh f236bd4ad…` returns `REFUSED (no-readiness-run)`: the diff matches no readiness path, so Full Platform Readiness did not push-trigger. The PR's `Readiness summary` was a diff-skipped pass, which is not platform verification and is not claimed as such. The newest push-triggered master run that succeeded is `9faefe46c` (#749, 2026-10-11T04:24Z). #756 and #769 are tooling-only diffs.
- **Agent-bus (#695):** **three phase B posts are drafted but NOT posted** (the auto-mode classifier refuses `gh issue comment`; `gh issue create`, `gh issue close --comment`, `gh pr create`, `gh pr edit`, `gh pr merge`, `git push` all went through). They are on the storage branch as `01-claim-card-768.md`, `02-handoff-pr-769.md`, `03-done-pr-769.md`. The owner posts them with `gh issue comment 695 --body-file <file>` (one per file, in order), or the next thread asks the owner in chat for a yes. Open items that are not this workstream's: Grok's `ask` on `drift-746` and `drift-748`, its `status` on `land-749-753` and `mutations-spec007` (spec-007-pr4). Grok's drift audit of #756 (`drift-756`) and #769 (`drift-769`) have not posted; phase C answers them with a follow-up PR and `done` on the slug.
- **Phase B exit criteria** (from the phase A handoff: one assigned task produces verified output and a recoverable handoff; refutation permits one retry and another cycle cannot refresh the counter; quota/auth cause no fallback launch; cancellation preserves workspace and run record; the funding route is verified): **met**, with 15 mutation checks KILLED at `d2f8b3336` (lines verbatim in the PR body, `pr-769-body.md` on the storage branch) and one live task through `tick --adapter claude` (§6). Card #768 closed with the merge SHA.

## 2. Read these first

- **In the repo (master):** `CLAUDE.md`; `tools/autonomy-controller/README.md` in full (now carries the observed CLI shapes under "Claude adapter", the fixer under "Readiness fixer", the stop-reason table, the integrator decisions and "Not in PR 2"); `ashlar_controller/{controller,budget,store,policy,readiness}.py`, `adapters/{base,claude,fake}.py`, `cli.py`; `tests/fake_claude_cli.py` (the NDJSON replay the tests drive); `tests/scripts/autonomy-controller.test.sh` (floor 190; suite 212); `_handoff/bus/PROTOCOL.md`; `_handoff/phases/README.md`.
- **The cards:** #755 (the roadmap), #768 (PR 2, closed).
- **On the storage branch, `_handoff/budgeted-autonomy/`:** `handoff.md` (this), `handoff-phase-A.md`, `handoff-phase-B.md`, `pr-769-body.md`, `mutations-pr2-summary.txt` and `mutation-logs-pr2.tar.gz`, `probe-report-2.json` + `probe_claude_runtime.py` (the live CLI probes and the script that made them), `pr2-lanes.md` (the lane contract with the verified addendum), the three unposted bus posts.

## 3. Decisions

- **Owner decisions this workstream relies on:** none recorded (no spec, no decisions log). The owner's prompt in #755 is the brief. Two owner actions this phase: `claude.cmd auth login` on this host (the CLI's stored session had expired; see §6) and the instruction to raise throughput with parallel worktrees.
- **Integrator decisions taken in phase B** (in the PR body of #769 and the README; the owner can reverse any):
  - `claude.model` is `claude-sonnet-5` (the CLI refuses `claude-sonnet-5-5` locally; `claude-sonnet-5` was accepted by the API in the probes and the live run); permission mode `acceptEdits` with `--permission-prompts none`; `--strict-mcp-config` on every launch (without it the operator's claude.ai connectors load into every worker: 70k cache tokens and Gmail/Drive/Slack tools exposed); expected funding route `claude.ai`, and an `ANTHROPIC_API_KEY` in the environment refuses the launch (`funding-route-mismatch`) rather than paying;
  - a `percent` allowance (the CLI's five-hour window utilization from `rate_limit_event`) permits a launch iff `(100 - value)/100 - interactive_reserve_fraction > 0`; turns cannot be compared with a percentage, so the task ceilings bound the turns;
  - a run that stops at the turn ceiling requeues and resumes its session exactly, without touching the same-failure counter (the pool's attempts bound it); an unrecognised model parks the task for a decision; auth and route mismatch block it;
  - the readiness fixer falls back to a general-purpose run exactly once and only on a recognised missing-role error; `run_gate` never retries; failure identities are `gate:<name>:<sha256[:12]>` of normalised, sorted evidence lines.
- **Open questions for the owner (none blocks phase C):**
  1. Whether the integrator decisions above (and phase A's) become durable owner rows somewhere (a short `docs/specs/` note or an "owner decisions" section in the README).
  2. The bus credential: the controller's `GhIssuePoster` is still unexercised live. On this host `gh` is authenticated (account `IanFrelinger`, scopes gist/read:org/repo/workflow) but the auto-mode classifier refuses a new comment on an existing issue, so posting through the controller from a Claude Code session needs either an allow rule for `gh issue comment` in the project's `.claude/settings.json` or the owner running the flush. Decide before PR 3 wires milestone posts.
  3. Phase A's Q1 (devtest image `sqlite3`) is **moot**: the image runs the suite (79/79 then 212/212). Nothing to pay.

## 4. Plan and live status

**Phase map (one PR per phase):**

| Phase | PR | Scope | Ends when | Status |
|---|---|---|---|---|
| A | 1 | Budget policy and durable controller state | merged on green; criteria mutation-proven | done (#756) |
| B | 2 | Claude adapter and bounded readiness | one task verified with a recoverable handoff; one retry per refuted fix and no counter refresh; no fallback on quota/auth; cancel preserves state; funding route verified | **done (#769)** |
| **C** | **3** | **PR lifecycle and fresh context** | old green checks cannot satisfy a changed head; duplicate events create no PR or worker; closure without merge is not success; next task uses updated base and compact handoff; previous transcript not injected | **next** |
| D | 4 | Operator controls, evidence retention, pilot | status view, pause/cancel/resume/provider-disable, backups and restore, LLM-free status reports, evidence archives, metrics; three bounded real tasks | |
| E | 5 | Codex and Cursor adapters | provider transfer preserves task/PR/attempts/budget; previous writer stopped and reconciled; one small real task through each enabled adapter | |

**Work items:** `feat/budgeted-autonomy-claude-adapter` @ `d2f8b3336`, PR #769, merged `f236bd4ad`, branch deleted. No branch is in flight. The three lane worktrees and branches were removed after their commits were cherry-picked.

**Phase C (PR 3), spelled out, in order:**
1. `/start-phase budgeted-autonomy`; read §2; create the PR 3 card as an `agent-task` issue from the PR 3 section of #755 (copy the acceptance criteria above; none exists yet); post `claim` on the bus (or draft it and ask the owner to post, §1).
2. Read what PR 2 left for PR 3 in the README's "Not in PR 2": the readiness fixer loop is not driven by `tick` (`readiness run|evaluate` are operator commands, `launch_fixer` is a function); the `prs` and `phases` tables exist but nothing writes them; `GhIssuePoster` is unexercised.
3. Build the PR lifecycle in the controller: open the PR from the worker's branch (`gh pr create`, which the classifier allows), record `prs.head_sha` and `base_sha`; observe checks **by head SHA** (`gh pr view --json headRefOid` + `gh pr checks` or the checks API) so a green run on an old head never satisfies a new one; one bounded repair round per `limits.repair_rounds` driven by the readiness fixer (`readiness.run_gate` → `evaluate_readiness` → `launch_fixer` with `role="readiness-fixer"`), each repair a new run that resumes nothing (fresh context) and carries only a compact handoff (the failure identities and evidence, not the transcript); merge on the five required checks through `gh pr merge --squash`; record `merge_sha`; closure without merge is `outcome=closed-unmerged`, never success; the next task's base is the merge SHA.
4. Event dedup for PR events: `event ingest` already keys on event id; prove that a repeated `pr-opened` / `checks-green` / `merged` event creates no second PR and no second worker (test with the fake adapter and a fake `gh`).
5. Keep the evidence rules: `scripts/test-in-container.sh` for anything with `dotnet`; `scripts/mutation-check.sh` with `ASHLAR_MUTATION_RUNNER=bash tools/autonomy-controller/mutation-runner.sh` for the Python suite (the `python3` shim note in §6); summary lines verbatim in the PR body; one live run of the integrated branch.
6. Acceptance (from the roadmap): old green checks cannot satisfy a changed head; duplicate events create no PR or worker; closure without merge is not success; the next task uses the updated base and a compact handoff; the previous transcript is not injected.
7. PR body per §5; `handoff` on the bus; merge on green; `done` with the merge SHA; `/handoff budgeted-autonomy C`.

## 5. How to do the next phase's routine work

1. Clone or use the session checkout (it is a clone: `.git` is a directory). `git checkout master && git pull --ff-only`, then `git checkout -b feat/budgeted-autonomy-<topic>`. One agent per clone or worktree; never run `scripts/test-in-container.sh` from a worktree.
2. Parallel lanes: write the contract first (shape of `pr2-lanes.md`), launch one agent per lane with `isolation: "worktree"`, tell each the base SHA and to `git reset --hard` to it if the worktree is at master, cherry-pick their commits, then verify the merged branch yourself.
3. Verify: `bash tests/scripts/autonomy-controller.test.sh` (host; now `OK (skipped=1)` on Windows); `bash scripts/test-in-container.sh -- 'bash tests/scripts/autonomy-controller.test.sh'` (container; 212/212); `bash scripts/ci/run-repo-gates.sh` (27 of 29 on this host; the two failures are the Windows artefacts in §6); `bash scripts/check-docs-phantom-paths.sh`; `bash -n` on shell files.
4. Commit, then mutation-check against the commit: `ASHLAR_MUTATION_RUNNER="bash tools/autonomy-controller/mutation-runner.sh" scripts/mutation-check.sh --id <name> --file <f> --old '<text>' --new '<text>'`, with a `python3` shim first on PATH on this host (§6); a batch runner that writes the summary lines to one file is `mutations/run.sh` in this phase's scratchpad (shape: one `run <id> <file> <old> <new>` per mutation). Quote each `mutation <id>: ...` line verbatim in the PR body.
5. `git add <paths>` (named paths, never `-A`: `.claude/worktrees/` is untracked and unignored) then `python scripts/knowledge-graph/build-knowledge-graph.py`; commit the graph if it changed (it did not change for PR 2's files).
6. Push with `git push -u origin <branch>`; `gh pr create --body-file`; `gh pr edit --body-file` to add the mutation lines after the batch; the desktop app binds the PR (`mcp__ccd_pr__get_status` shows the five checks).
7. Bus: draft every post as a file; try `gh issue comment 695 --body-file` once; if refused, name the file and ask the owner. Merge (squash) on the five required checks green unless Grok posted `block`; `done` with the merge SHA; `gh issue close <card> --comment` works.
8. Read the readiness verdict for the merge SHA (`scripts/release/readiness-verdict-for-sha.sh <full sha>`); for a tooling-only diff it says `REFUSED (no-readiness-run)`, which is the expected answer, not a failure.

## 6. Environment facts

- Five required checks on master: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary`; strict + enforce_admins; merges serial. #769's checks matched.
- Repo gates: **29**. On this Windows host 27 pass; `tests/scripts/mutation-check.test.sh` (symlink fixture) and `tests/scripts/session-start-hook.test.sh` (docker wrapper) fail on master too. `shell-lint` on ubuntu is the check that counts. `scripts/mutation-check.sh` calls `python3`, which on this host is the Store stub: put a directory with an executable `python3` that `exec python "$@"` first on PATH.
- Host: Python 3.13, `gh` authenticated as `IanFrelinger` (the cloud session's token was invalid; this host's is fine), `jq` 1.8, docker 29.8, image `ashlar-devtest:local` (Python 3.12 **with** `sqlite3`, `jq`, no `claude`). The controller therefore runs on the host; only `dotnet` work goes into the container.
- **Claude Code CLI 2.1.263** (`bin/claude.exe` behind the npm `claude.cmd`; launch the exe). Facts the adapter is built on, all observed on 2026-10-11 (`probe-report-2.json`): `--session-id` honoured and `--resume` reuses it; `--max-turns` enforced (`error_max_turns`); a denied tool call ends `is_error:false` with `permission_denials` populated; unrecognised model → stderr tag + `api_error_status:404`, no cost; terminate mid-request keeps the transcript and `--resume` works; `--json-schema` → `structured_output`; `num_turns` and `total_cost_usd` are per invocation; the subscription allowance is only in the stream-json `rate_limit_event` (`unifiedWindows.five_hour.utilization`, `isUsingOverage`); `Workflow` is in the default headless tool set; `--strict-mcp-config` is mandatory. Recognised model ids include `claude-sonnet-5`, `claude-sonnet-4-5`, `claude-opus-5`, `claude-fable-5-1` and the aliases; `claude-sonnet-5-5` is refused.
- **Stale login looks logged in.** At phase start `claude auth status --json` said `loggedIn: true` while every request failed with "OAuth session expired and could not be refreshed" (debug log: "OAuth refresh token is no longer valid"). The owner ran `claude.cmd auth login` (the `.ps1` shim is blocked by PowerShell's execution policy). The adapter maps the symptom to `auth-missing` and blocks the task; the README names the operator step.
- **Three shapes the stubs hid and the live run exposed:** `auth status --json` is pretty-printed over many lines; the CLI truncates a long project slug (206 chars → 200 + `-kzvbwl`), so transcripts are located by session id; `task set-state` could never work (argparse positional named `state` overwrote `--state`). All fixed in #769 with tests and mutations.
- **CPython on Windows: `os.kill(pid, 0)` calls `TerminateProcess`.** PR 1's lease takeover check could kill the holder it probed; `store.pid_alive` now uses `OpenProcess`/`GetExitCodeProcess` on Windows. The adapter's own existence check is the same function.
- Live run evidence (`tick --adapter claude`, policy ceilings 6/12/2): run `run-c8b7f95536d1`, session `5ecc6e81-…`, settled `completed`, 2 turns charged, allowance observation 73 percent, estimated API value 0.0530 USD, `hello.txt` present in `<state>/runs/<run>/workspace`. The first attempt was refused `auth-missing` by the pretty-print defect; the pool therefore shows 2 attempts used.
- A lane worktree handed to an agent sits at master, not the feature branch (three of three this phase). Known pre-existing .NET failures (from the spec-007-pr4 handoffs) are untouched and unrelated.

## 7. Queued work

- Post the three bus files (§1) and, if the owner wants the controller to post, add the `gh issue comment` allow rule or decide on a REST poster with a token from the environment (§3 Q2).
- `drift-756` / `drift-769` from Grok, when they post: fix in a follow-up PR, `done` on the slug.
- `Controller.status()` still picks `last_run` by `ORDER BY reserved_at DESC, run_id DESC` (a random tiebreak; the resume path was fixed to `rowid`); harmless until two runs share a second. Small follow-up.
- The README's "What is unverified" list: `permission_denials` for tools other than Bash; `num_turns` under a cancelled run; the seven-day window as the limiting factor. The pilot (PR 4) will observe them.
- Owner rows for the integrator decisions (§3 Q1).
- Cloud-cost review stays the workstream after this one; runtime evidence so far: the live run's first call costs about 0.04 USD-estimate with `--strict-mcp-config` and 0.28 without; the probes used eight one-turn calls.

## 8. Ways of working the owner has endorsed

- One phase per thread, one PR per phase, ending with `/handoff`.
- Claim before you build; read the newest handoff and the bus first; create the card if none exists.
- Parallel lanes in isolated worktrees for throughput, with a written contract and your own verification of the merged branch (asked for in phase B).
- Every default taken without the owner is an integrator decision in the PR body and the handoff, never an owner row.
- Mutation-check every behaviour with `scripts/mutation-check.sh`; quote the summary lines verbatim; say what was not observed failing and what only CI proves; a live run of the integrated branch is part of the phase.
- Declare nothing unverified as a capability; keep the four usage kinds separate; an estimated API dollar figure is not a charge.
- The owner performs logins and credential steps themselves (phase B: `claude auth login` in their terminal).

## 9. Starting prompt for the next phase

> Execute **phase C (PR 3: PR lifecycle and fresh context)** of budgeted-autonomy. Run `/start-phase budgeted-autonomy`, or read this handoff in full starting with §0, then `tools/autonomy-controller/README.md` ("Not in PR 2" first). Check §1 against the live repo (master `f236bd4ad`, #769 merged, the bus: three phase B posts may still be unposted; ask me to post them). Create the PR 3 card as an `agent-task` issue from the PR 3 section of #755 and claim it. Then build the PR lifecycle in the controller: open the PR from the worker's branch, observe checks by head SHA so an old green cannot satisfy a changed head, one bounded repair round through the readiness fixer with a compact handoff and no transcript injection, merge on the five required checks, record the merge SHA, treat closure without merge as not success, and start the next task from the updated base. Prove with tests that duplicate PR events create no second PR or worker. Use parallel lane agents in isolated worktrees where it helps, but verify the merged branch yourself, including one live run. Build and test only through `scripts/test-in-container.sh` for anything that needs `dotnet`; never run `dotnet` on the host. Mutation-check every behaviour with `scripts/mutation-check.sh`. If anything I have not decided blocks you, ask instead of choosing. Land PR 3 on the five required checks, draft the bus `handoff` and `done` (post them if the classifier now allows it, otherwise ask me), then run `/handoff budgeted-autonomy C` and notify me. Do not start PR 4.
