# Budgeted autonomy controller

A small controller that takes an eligible task card, reserves budget for one bounded worker launch,
records every attempt durably, and stops spending when the operator's policy says so. It is the
foundation of the budgeted autonomous development workflow (workstream `budgeted-autonomy`, card
#755). PR 1 shipped the policy and the durable state over a fake adapter. **PR 2 adds the Claude
adapter and bounded readiness**: a live launch through the installed Claude Code CLI, exact session
resume at the turn ceiling, usage observations, a funding-route check before every launch, and a
readiness fixer whose gate failures carry stable identities. **PR 3 adds the PR lifecycle and fresh
context**: the controller clones the base onto a branch for the worker, commits and pushes what the
worker edited, opens the PR once, observes the required checks by head SHA, runs at most
`limits.repair_rounds` repair rounds as new runs briefed by a compact handoff, merges on green with the
head pinned, records the merge SHA, treats closure without merge as not success, and starts the next
task from that merge SHA. The roadmap (PR 4 operator controls and pilot, PR 5 Codex and Cursor
adapters) is in the card.

Python 3.8+ and the standard library only (SQLite through `sqlite3`). Nothing here runs `dotnet`,
and nothing here runs outside the host the operator starts it on.

## Layout

| Path | What |
|---|---|
| `controller.py` | Entry point: `python3 tools/autonomy-controller/controller.py --state <db> --policy <json> <command>` |
| `ashlar_controller/policy.py` | Loads and validates the operator policy; refuses one that lives inside a checkout |
| `ashlar_controller/store.py` | The SQLite schema, integrity checks and the single-writer lease |
| `ashlar_controller/budget.py` | Budget pools, atomic reservations, settlement, usage deltas, the interactive reserve |
| `ashlar_controller/events.py` | Event deduplication (an event id is an idempotency key) |
| `ashlar_controller/outbox.py` | Milestone posts (claim / handoff / done) with duplicate-safe recovery |
| `ashlar_controller/controller.py` | The tick: settle, reconcile, then at most one launch |
| `ashlar_controller/adapters/` | The adapter protocol, declared capabilities, the fake adapter and `claude.py` (the Claude Code CLI) |
| `ashlar_controller/readiness.py` | The three gate commands, their parsers, stable failure identities, the bounded fixer launch |
| `ashlar_controller/prs.py` | PR records, the `gh` client (`GhCli`) and its in-memory twin (`FakeGh`), checks evaluated by head SHA, the compact handoff |
| `ashlar_controller/gitops.py` | The controller's git: clone the base onto a branch, commit and push a workspace, clone a PR head for a repair |
| `ashlar_controller/lifecycle.py` | The PR phase of a tick: propose, open, observe, repair, merge, park; PR events |
| `policy.example.json` | The operator policy example (copy it outside every checkout) |
| `tests/` | The unit suite (`tests/fake_claude_cli.py` stands in for the CLI); `tests/scripts/autonomy-controller.test.sh` runs it in the required `shell-lint` check |
| `mutation-runner.sh` | `ASHLAR_MUTATION_RUNNER` for `scripts/mutation-check.sh` |

## Operator setup

1. Copy `policy.example.json` to a path **outside every checkout**, for example
   `/etc/ashlar/controller-policy.json` or `~/.config/ashlar/controller-policy.json`, and make it
   `0600`. The loader refuses a policy that is inside the repository holding the controller, inside
   any registered worker checkout (`worker register --checkout <path>`), or group/world-writable.
   This is the trust boundary: a worker can edit its checkout, so nothing in a checkout may set the
   limits it runs under.
2. Create the state once: `controller.py --state ~/.local/state/ashlar/controller.db init`.
   The controller never recreates a missing database on its own; a missing or corrupt file stops
   dispatch (exit 3) rather than starting from an empty budget. Back the file up like any ledger.
3. For live launches, install Claude Code on the controller host and log in once: `claude auth login`.
   The controller never logs in, never stores a credential and never refreshes a token. A stale login
   blocks the task (`blocked-auth`) with the CLI's own message ("OAuth refresh token is no longer
   valid") until the operator repeats this step; `claude auth status --json` still says `loggedIn:
   true` in that state, so the first run after a long idle is the real check. `authMethod` must be
   `claude.ai`.
4. Register tasks (`task add --task-id <id> --card <n> --workstream <w> --provider claude ...`) and run
   `tick --adapter claude`. `tick` without `--adapter` uses the fake, for proving the state machine.

`ASHLAR_CONTROLLER_STATE` and `ASHLAR_CONTROLLER_POLICY` stand in for `--state` and `--policy`.

## Commands

| Command | Does |
|---|---|
| `status [--json]` | Owner, task, PR, state, stop reason, blocker, budget consumed/reserved/remaining, attempts remaining, funding pool, lease, latest usage per provider |
| `pause --reason <text>` / `resume` | Stop launching before the next tick; allow it again |
| `tick [--dry-run] [--adapter fake\|claude] [--gh none\|cli] [--workspaces <dir>]` | One step. `--dry-run` opens the state read-only, touches no adapter, and says what would happen. `--adapter claude` registers the Claude adapter with its run directory under `<state dir>/runs/`. `--gh cli` turns the PR lifecycle on through the `gh` executable (worker clones under `<state dir>/workspaces/`); without it a completed run is `done` and no PR is opened |
| `pr list\|show <n>\|observe <n>` | PR records; `observe` reads the PR's current head and its required checks now and merges, repairs or parks as they demand (`--gh cli`) |
| `readiness run --gate <name> --workspace <path> [--task-id <id>]` | Run one gate command (`repo-gates`, `readiness-local`, `container-tests`) in a workspace and print the parsed report; with `--task-id`, apply it to the task (see "Readiness fixer") |
| `readiness evaluate --task-id <id> --report <json>` | Apply a saved report (the JSON `readiness run` printed) to a task without running anything |
| `task add|list|show|set-state` | Task records; `--parent` makes a child that **shares** the parent's budget pool |
| `event ingest --event-id ... [--gh cli]` | Record an event once; a repeated id is reported as a duplicate and does nothing. With `--gh cli`, a **new** PR event (`pr-opened`, `checks-green`, `checks-red`, `merged`, `closed`, `pr-head`) also triggers one idempotent observation of the task's open PR |
| `run list|settle|cancel` | Run records; `settle` is the operator's manual reconciliation; `cancel` keeps the workspace |
| `lease show|release|break` | The writer lease; `break --confirm-stopped` is the operator asserting the old holder is gone |
| `outbox list|enqueue|flush` | Milestone posts; `flush --poster fake` in PR 1 (`gh` poster present, not exercised live) |
| `worker register|list` | Worker checkout roots, which the policy may never live under |
| `provider disable|enable <name>` | Take a provider out of dispatch without editing the policy |
| `policy` | Show the validated active policy and its hash |

Exit codes: 0 ok; 1 other error; 2 usage; 3 state missing or corrupt; 4 policy refused; 5 writer
lease not held; 6 budget refused.

## What one tick does

One tick does **one** thing, the first that applies, and journals it with its exact stop reason:

1. refuse if the state is missing or corrupt, report `paused` if paused, refuse (`lease-not-held`)
   if another controller holds the writer lease;
2. reconcile runs whose launch outcome is unknown (`launching` with no handle, or `unknown`),
   asking the adapter what became of them; while any stays unknown, nothing new launches;
3. poll running runs and settle the finished ones (release the reservation, charge what was
   consumed, move the task to the state its stop reason demands); a tick that settled anything
   stops here. With the PR lifecycle on, a **completed** implementation or repair run is settled by
   committing and pushing its workspace (see "PR lifecycle"), not by marking the task done;
4. if a writer is already active (one implementation or repair run, across every provider), stop
   (`writer-busy`);
5. with the PR lifecycle on, observe every open PR by its current head (merge, repair, park, or wait);
   if one acted, stop there; if any PR is still open, stop (`pr-open-blocks-dispatch`: PRs are serial);
6. pick the first eligible task, by priority then age; none means `idle: empty-queue` with **zero**
   adapter calls;
7. check the provider's latest allowance observation against the 25 percent interactive reserve;
8. reserve the invocation ceiling against the task's pool and record the run, in one transaction;
   with the PR lifecycle on, clone the current base onto the task's branch as the workspace; then
   launch, and record the handle, or the uncertainty, at once.

### Stop reasons and the states they produce

| Run stop reason | Task state afterwards | Then |
|---|---|---|
| `completed` | `done` | |
| `quota-exhausted` | `waiting-quota` | checkpoint and wait `idle.quota_wait_seconds`; the same provider is retried; **no fallback launch on another route** |
| `rate-limited`, `transient` | `waiting-rate-limit` | bounded backoff from `idle.rate_limit_backoff_seconds`, inside the task budget |
| `task-failed` with a failure identity | `queued` once, then `parked-failure` | the same diagnosed failure is retried **once** across all cycles and providers (`failure_retries` table) |
| `auth-missing`, `permission-missing` | `blocked-auth`, `blocked-permission` | the exact blocker is recorded; an operator step is required |
| `cancelled` | `cancelled` | the run row and its workspace path are preserved |
| `no-final-result` | `needs-reconcile` | process, workspace, transcript and remote state are reconciled before any retry |
| `launch-outcome-unknown` | `needs-reconcile` | the original attempt is investigated before another launch |
| `decision-missing` | `parked-decision` | the decision needed is recorded verbatim |
| `invocation-ceiling-reached` | `queued` | not a failure and no retry counter is touched; the next launch **resumes the same session** (`resume_session_id`), journalled as `resume`; the pool's attempts and task ceiling bound it |
| `model-unrecognized` | `parked-decision` | the CLI refused the model id before any request; the blocker names the id, and the policy must change |
| `funding-route-mismatch` | `blocked-auth` | the launch would have billed a route the policy did not select; nothing was started |
| `completed`, lifecycle on | `pr-open` (`pr-checks-pending`) | the workspace was committed and pushed; the PR is open (or its new head recorded); only a merge on the current head makes the task `done` |
| `no-change-to-propose` | `queued` once, then `parked-failure` | the worker completed and left no diff; a diagnosed failure through the same retry counter (`pr:no-change:<task>`) |
| `pr-merged` | `done` | every required check green on the current head; merged with the head pinned; `merge_sha` recorded |
| `pr-closed-unmerged` | `parked-failure` | the PR was closed without a merge: never success; nothing from it is the next base |
| `repair-rounds-exhausted` | `parked-failure` | the current head is red and `limits.repair_rounds` are used; operator step required |
| a refusal at launch (`LaunchRefused`) | by the same mapping | the adapter named the reason before anything started: `auth-missing` and `funding-route-mismatch` give `blocked-auth`, `model-unrecognized` gives `parked-decision`, anything else requeues as `launch-failed`; the reservation is released, the attempt stays counted |
| budget or attempts exhausted at reservation | `parked-budget` | with the remaining counts in the blocker text |

### Budget accounting

- A pool is per task and cumulative. Its ceilings are frozen when it is created; a later policy can
  only tighten them. A child task made with `--parent` uses the parent's pool, so splitting a task
  never resets an exhausted budget.
- A reservation is the invocation ceiling (20 turns in the example). Settlement charges what the
  adapter reports; unknown consumption is charged at the full reservation; consumption above the
  reservation is charged in full and recorded as `overshoot`, because a provider's turn limit is an
  estimate.
- Usage observations are stored by kind and never summed across kinds: `subscription_allowance`
  (the provider's own units), `api_grant_balance`, `paid_spend`, `estimated_api_value`. An estimated
  API dollar figure is an estimate, not a charge. Each observation carries `fresh`, `stale` or
  `unavailable`. A provider's cumulative session figure is turned into a per-run delta against the
  previous observation of the same session.
- With a fresh, quantified allowance in the pool's own unit, a launch needs
  `remaining - 25% of total >= one invocation`. With a fresh allowance in `percent` (the Claude
  CLI's window utilization), a launch needs the unused share of the window to exceed the reserve
  fraction; the invocation ceiling is not consulted, because turns cannot be compared with a
  percentage. With a stale or unavailable allowance, `funding.unobservable_allowance` decides: rely on
  the task ceilings (default) or wait.

### Single writer and recovery

- The writer lease lives in the state file with a heartbeat. An **expired** lease is not free: it is
  taken only when the previous holder released it, or ran on this host and its pid is gone, or the
  operator ran `lease break --holder <id> --confirm-stopped`.
- The database refuses a second active implementation run through a partial unique index, whatever
  the code believes.
- A run is `reserved` before launch, `launching` while the launch is in flight, `running` once a
  handle is recorded. A controller that dies in between leaves a row recovery can see; the next
  tick reconciles it before launching anything.
- The outbox writes a post with a dedup key before sending it, and a post interrupted mid-send is
  checked for on the target (by its `<!-- ashlar-controller:dedup:... -->` marker) before it is sent
  again. This is duplicate-safe recovery, not exactly-once delivery.

## Claude adapter

`ashlar_controller/adapters/claude.py` runs the installed Claude Code CLI headless. Its
`AdapterCapabilities` declare only what was observed; the few things still unverified are flagged
in the code and listed below.

### What was observed

Two probes of Claude Code 2.1.263 on Windows on 2026-10-11, the second after `claude auth login`,
with model `claude-sonnet-5` (the result's `modelUsage` names `canonicalModel` `claude-sonnet-5`).
The shapes below are what the CLI printed; the adapter parses these and nothing else.

- Executable: the npm shim `claude.cmd` wraps `.../node_modules/@anthropic-ai/claude-code/bin/claude.exe`;
  the adapter launches the exe (`cli=None` resolves `shutil.which("claude")`, and a `.cmd` resolves to
  the sibling `bin/claude.exe`).
- Headless: `claude -p <prompt> --output-format json` prints one JSON object (the last stdout line) of
  `type: "result"` with the fields `session_id`, `is_error` (bool), `subtype`, `terminal_reason`,
  `api_error_status` (int or null), `result` (text), `num_turns`, `total_cost_usd`, `duration_ms`,
  `duration_api_ms`,
  `usage{input_tokens,output_tokens,cache_creation_input_tokens,cache_read_input_tokens,service_tier,speed:"standard"}`,
  `modelUsage{}` (per-model figures; a `claude-haiku-4-5` helper call appears beside the main model),
  `permission_denials[]`, `fast_mode_state:"off"`, `stop_reason`, `uuid`.
- Success: `is_error:false, subtype:"success", terminal_reason:"completed", stop_reason:"end_turn"`,
  `result` = the text.
- `--output-format stream-json --verbose` (what the adapter uses) emits NDJSON, in order:
  `{"type":"system","subtype":"init",...}` (fields `model`, `permissionMode`, `apiKeySource:"none"`,
  `claude_code_version`, `session_id`, `cwd`, `tools:[...]`, `mcp_servers:[...]`, `agents:[...]`,
  `slash_commands:[...]`), then
  `{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":<epoch s>,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.69,"resetsAt":1791702600},"seven_day":{"utilization":0.18,"resetsAt":1792285200}}},"session_id":...}`,
  then `assistant` messages, then the `result` object (the same fields as `--output-format json`).
  The adapter takes the last line whose `type` is `result`.
- **The `rate_limit_event` is the subscription allowance source.** `utilization` is the used fraction
  of the window; the adapter records the last event seen as a `subscription_allowance` observation in
  `percent` (`five_hour.utilization * 100`, total 100, fresh, by session), or `unavailable` when no
  event was seen. `isUsingOverage: true` would mean paid overflow: the run is classified
  `quota-exhausted` with the blocker "the provider is billing overage; paid inference is off", and
  there is no fallback.
- `num_turns` and `total_cost_usd` are **per invocation**, not cumulative on the session: a first call
  reported `num_turns` 1 / cost 0.2817, and the `--resume` call on the same session `num_turns` 1 /
  cost 0.0144. `total_cost_usd` has `costBasis:"list"`: an estimate of API value, never a charge on
  the subscription.
- `--session-id <uuid>` is honoured: the result's `session_id` equalled the one passed, and it stayed
  the same across three `--resume` calls.
- `--max-turns N` is enforced. With `--max-turns 1` on a task needing a tool call and an answer:
  `is_error:true, subtype:"error_max_turns", terminal_reason:"max_turns", num_turns:2, stop_reason:"tool_use"`.
- Permission denial (`--permission-prompts none`, manual mode, a Bash call): the run **completes** with
  `is_error:false, subtype:"success"` and
  `permission_denials:[{"tool_name":"Bash","tool_input":{...},"tool_use_id":"toolu_..."}]`; the result
  text says the call was automatically denied. So `permission_denials` is checked before a success
  counts as completed.
- Unrecognised model (`claude-no-such-model-0`): stderr `[claude-code:unrecognized_model] {"model":"<id>","query_source":"sdk"}`,
  result `is_error:true, terminal_reason:"api_error", api_error_status:404, total_cost_usd:0,
  result:"There's an issue with the selected model (...). It may not exist or you may not have access to it."`,
  exit 1. Recognised ids include `sonnet`, `opus`, `haiku`, `fable`, `claude-sonnet-5`,
  `claude-sonnet-4-5`, `claude-opus-4-1`, `claude-haiku-4-5`, `claude-fable-5`, `claude-fable-5-1`,
  `claude-opus-5`; not recognised: `claude-sonnet-5-5`, `claude-opus-5-5`, `claude-haiku-5-5`.
- Cancellation: terminating `claude.exe` while the model request was in flight exited the process
  (code 1) with no `result` event; the transcript `.jsonl` was persisted (12 lines), and a later
  `--resume <same id>` succeeded with the same `session_id` (`num_turns` 1). Cancel, and resume after
  cancel, are verified.
- `--json-schema <schema>`: the result carries `structured_output: {"answer":"4"}` and `result` is the
  JSON text (`num_turns` 2). `--no-session-persistence`: no transcript file is written.
- Transcript: `<projectsDirectory>/<slug>/<session_id>.jsonl`, where `projectsDirectory` comes from
  `claude auth status --json` and `slug` is the cwd with every character outside `[A-Za-z0-9]`
  replaced by `-`.
- Tools: with `--tools default`, headless tools include `Workflow`, `Task`, `Bash`, `Edit`, `Read`,
  `Skill` and the rest. **The Workflow tool is available headless**, which answers the handoff's
  question: the converge-readiness command's Workflow tool can be driven through this adapter.
  `--tools ""` disables tools; `--tools Read,Bash` restricts them; `--agent <name>` selects a named
  agent role; `--add-dir` is accepted.
- MCP: with `--strict-mcp-config` and no `--mcp-config`, `mcp_servers` is `[]` and the first call's
  cache creation dropped from 70k tokens to 9k (cost 0.28 to 0.043). Without it, the operator's
  claude.ai connectors (Gmail, Drive, Slack, Docs) load into every worker and their tools are exposed
  to it. **Every controller launch passes `--strict-mcp-config`.**
- `--effort low|medium|high|xhigh|max` is accepted. There is no speed flag; `usage.speed` is always
  `"standard"` and `fast_mode_state` is `"off"`.
- Auth: `claude auth status --json` prints `{loggedIn, authMethod:"claude.ai", apiProvider:"firstParty",
  subscriptionType:"max", projectsDirectory, email, orgId, orgName}`. A stale login looked like this in
  the result: `is_error:true, terminal_reason:"api_error", api_error_status:null,
  result:"Failed to authenticate: OAuth session expired and could not be refreshed"` (under `--bare`:
  `result:"Not logged in · Please run /login"`), exit code 1, with the debug log saying
  `API auth_error: OAuth refresh token is no longer valid; run /login to re-authenticate`.
  **`auth status` still reported `loggedIn: true` in that state**, so the route check does not catch
  it; the run itself does, as `auth-missing`. The operator step is `claude auth login`.

### What is unverified

- The shape of a `permission_denials` entry for tools other than Bash (one Bash denial was observed).
- `num_turns` under a cancelled run (no result object is written, so nothing is charged beyond the
  reservation: the conservative reading).
- The seven-day window as a limiting factor: the adapter records the five-hour utilization, which was
  the higher of the two in every probe.

### How a launch runs

- Speed: `request.speed` must be `standard`; anything else is refused before any process starts
  (`launch-failed`, requeued).
- Funding-route check, before every launch: the adapter runs `claude auth status --json`. The route is
  `api-key` if `ANTHROPIC_API_KEY` is in the child environment, otherwise the `authMethod` the CLI
  reports. `loggedIn: false` refuses with `auth-missing`; a route other than the one the policy
  selected (`claude.ai` by default) refuses with `funding-route-mismatch` ("`<observed>` would be billed;
  policy selected `<expected>`"); the command failing to run at all refuses with `auth-missing`. An API
  key in the environment means paid spend, not the subscription, so the launch does not happen.
- Environment: `CLAUDECODE` and every `CLAUDE_CODE_*` variable are stripped from the child environment
  (a nested session changes the CLI's behaviour). `ANTHROPIC_*` variables are kept, so the route check
  can see a key rather than silently launching past it.
- Argv: `-p <prompt> --output-format stream-json --verbose --strict-mcp-config --model <M> --effort <E>
  --max-turns <ceiling> --permission-prompts none --permission-mode acceptEdits --no-chrome`, plus
  `--session-id <S>` for a new session or `--resume <S>` when the task's previous run stopped at the
  turn ceiling, plus `--agent <name>` and `--tools <list>` when the request carries them.
- Run directory `<runs_dir>/<run_id>/`: `manifest.json` is written before the spawn (run id, task id,
  session id, resume_of, argv, cwd, started time, pid, the projects directory and the expected transcript
  path, which is only a hint: the live run of 2026-10-11 showed the CLI truncates a long project slug to
  200 characters plus a six-character suffix, so the transcript is located by its session id), then stdout goes to
  `stdout.json`, stderr to `stderr.log`, the pid into the manifest. The workspace is
  `request.workspace` or `<run dir>/workspace`. A spawn that definitely failed is `launch-failed`; a
  manifest written with the spawn outcome unknown is `launch-outcome-unknown`, and the next tick
  reconciles from the manifest before anything else launches.
- Classification of a finished run, in this order: `[claude-code:unrecognized_model]` on stderr, or
  `api_error_status` 404 with the model text, gives `model-unrecognized`; no result object gives
  `no-final-result` (or `cancelled` when the manifest says so); `subtype` `error_max_turns` gives
  `invocation-ceiling-reached`; a 401 or an auth message gives `auth-missing`; a 402, a usage-limit
  message or `isUsingOverage` gives `quota-exhausted`; a 429 or a rate-limit message gives
  `rate-limited`; 408/5xx/529 or a network message gives `transient`; a non-empty `permission_denials`
  gives `permission-missing` even when `is_error` is false; `is_error` false gives `completed`;
  anything else is `task-failed` with the identity `claude:<sha256 of the normalised first 200
  characters of the result text>[:12]`.
- Accounting: `consumed_units` is `num_turns` (per invocation) and `RunStatus.usage` is `None`, so no
  dollar figure ever stands in for turns. Two observations are recorded on every run: the allowance in
  `percent` (by session) and `total_cost_usd` as an `estimated_api_value` in USD keyed by run id, so
  the recorded delta is the per-invocation cost. `status` shows the latest of each.
- Cancel writes `cancelled_at` into the manifest first, then terminates the process tree (`taskkill
  /T /F` on Windows, `killpg` on POSIX). The workspace and the transcript are never touched; the
  session can be resumed afterwards.

## PR lifecycle

`ashlar_controller/lifecycle.py`, `prs.py` and `gitops.py`; on with `--gh cli`. The worker (a headless CLI
under `acceptEdits`) can only edit files in its workspace, so the controller does every git and `gh`
step and records each one:

1. **Before an implementation launch**, the controller clones `pr.remote_url` at the current base onto
   the branch `pr.branch_prefix + <task id>` as the run's workspace (`gitops.prepare_workspace`; a
   `pr.reference_checkout` speeds the clone up). The base is the newest recorded `merge_sha`, else the
   task's `--base-sha`, else the tip of `pr.base_branch`. `tasks.base_sha` and `tasks.branch` record it.
   The prompt names the workspace, the base, the branch, and carries the **compact handoff**: the
   previous PR's number, outcome and merge SHA. Never a transcript, a session id or a previous prompt:
   the prompt is built from the task row and the PR ledger only.
2. **When the run completes**, the controller runs `git add -A`, commits (author `ashlar-controller`)
   and pushes the branch (`gitops.commit_and_push`). No diff is `no-change-to-propose`, a diagnosed
   failure retried once and then parked. Then it opens the PR **once** (`prs.open_or_reuse_pr`): an
   existing row for the task is reused with zero `gh` calls, an open PR already on GitHub for the branch
   is adopted, otherwise `gh pr create`. The task is `pr-open`.
3. **On every tick** each open PR is observed **by head SHA**: `gh pr view` gives the current head,
   `GET repos/<repo>/commits/<head>/check-runs` gives the checks for that commit alone, and
   `prs.evaluate_checks` reads `pr.required_checks` (the five on master) against them. A required check
   with no run on the head is pending; `success` or `skipped` is green; `failure`, `cancelled`,
   `timed_out`, `action_required`, `stale`, `startup_failure` is red. A green run on an earlier head
   never counts, because it is never fetched.
4. **Green**: `gh pr merge --squash --match-head-commit <head>`, so a head that moved after the
   observation refuses the merge (`merge-refused`, journalled; the PR stays open). The merge SHA comes
   from a second `gh pr view`; `prs.merge_sha`, `outcome=merged`, the task is `done` (`pr-merged`).
5. **Red**: one repair round, at most `limits.repair_rounds` per PR (the round is counted before the
   launch). The repair is a **new** run (`role=readiness-fixer`, agent `readiness-fixer` with the
   missing-role fallback of `readiness.launch_fixer`, `resume_session_id=None`) in a fresh clone of the
   PR head (`gitops.checkout_pr_head`, which refuses a head that moved). Its entire brief is
   `prs.compact_handoff`: PR, branch, base, head, round `N of M`, and per red check its name,
   conclusion, identity (`check:<name>:<12 hex>` over the check's title and summary, stable across
   heads) and bounded evidence. The repair's push is the new head, observed on the next tick. Rounds
   exhausted on a red head parks the task (`repair-rounds-exhausted`).
6. **Closed without a merge**: `outcome=closed-unmerged`, the task parks (`pr-closed-unmerged`), and
   the next task's handoff says nothing from it landed. A merge done outside the controller is recorded
   as `merged` with its SHA.
7. **PRs are serial.** While any PR is open nothing new launches (`pr-open-blocks-dispatch`), and the
   next task's base is the merge SHA the open one produces. An integrator decision (no owner row),
   matching the repository's serial-merge rule.
8. **PR events** (`event ingest --gh cli`): a new event id of a PR kind triggers one observation of
   the task's open PR, which is idempotent; a repeated id is a duplicate and does nothing. So a repeated
   `pr-opened`, `checks-green`, `merged` or `closed` creates no second PR row, no second merge and no
   second worker.

Schema 2 adds `prs.branch`, `prs.url`, `prs.title`, `tasks.branch` and `runs.pr_number`; a schema-1
ledger is migrated in place (additive columns, journalled `schema-migrated`) on its first read-write
open, and refused read-only until then.

## Readiness fixer

`ashlar_controller/readiness.py` turns the output of the three gate commands into a report whose
failures have identities that are stable across runs, so the controller can tell "the same failure
again" from "a different failure".

| Gate | Command | Parsed from |
|---|---|---|
| `repo-gates` | `bash scripts/ci/run-repo-gates.sh` | `::group::<path>` / `::error::<path> failed (exit N)` lines and the final `repo-gates: all N gate(s) passed` or `repo-gates: K of N gate(s) failed:` summary |
| `readiness-local` | `bash scripts/readiness-gate-local.sh --layer application --json <out>` | the JSON `{layer, commit, started_at, failed, gates:[{gate,status,exit_code,duration_s,log,log_tail}]}` |
| `container-tests` | `bash scripts/test-in-container.sh --repo <workspace>` | `Test run for <path>/<name>.dll (<tfm>)` headers, `Failed!  - Failed: F, Passed: P, ...` lines and `  Failed <FullyQualifiedTestName> [<ms> ms]` lines |

- A failure identity is `gate:<name>:` plus the first twelve hex characters of the SHA-256 of the
  normalised evidence. Normalisation strips timestamps, durations, absolute paths (the basename
  stays), hex strings of seven or more characters, temp directories, carriage returns and trailing
  whitespace, so two runs that differ only in those have the same identity and two runs with a
  different failing set do not.
- `run_gate` executes the command once and parses it. It never retries. A script that crashes without
  parsable output raises `GateInfrastructureError`, which the CLI reports as an error, not a verdict.
- `evaluate_readiness` (also `readiness run --task-id`, `readiness evaluate`): no failures means the
  task is `done`. Failures put the first failure's identity through the **same** retry counter as a
  diagnosed run failure: `queued` once per identity, then `parked-failure`. The counter lives in the
  task's pool, so it survives a second cycle, a restart and a task split; a failure a run reported and
  the same failure a gate reported share one retry.
- The fixer is one bounded launch (`role="readiness-fixer"`, agent `readiness-fixer`, the evidence in
  the prompt, the CLI's default tools). If the adapter refuses because the named agent role does not
  exist (`unknown agent`, `agent ... not found`, `no agent named`, `agent not found`), it is retried
  **once** as a general-purpose run and the request records `fallback="general-purpose"`. Any other
  error propagates with no fallback. `refuted(before, after)` lists the identities present in both
  reports: a fix that left its failure in place.

## Operating policy defaults (integrator decisions)

These are the conservative defaults in `policy.example.json`, taken because the owner has not
decided otherwise. They are **integrator decisions**, recorded here and in the PR body, not owner
rows in any decisions log: paid inference off; no new paid infrastructure; one implementation writer
across providers; one scoped verifier; one readiness fixer; one retry of the same diagnosed failure;
at most two PR repair rounds; at most one premium escalation and one provider transfer per PR; a
25 percent interactive reserve where allowance is observable; idle on an empty queue; checkpoint and
wait on an exhausted allowance; `claude.model` is `claude-sonnet-5`, an id the installed CLI
recognises and the API accepted in the live probe (`modelUsage.canonicalModel` `claude-sonnet-5`;
`claude-sonnet-5-5`, the PR 1 candidate, is refused before any request); the Claude adapter's
permission mode is `acceptEdits` with prompts disabled and `--strict-mcp-config` on every launch; the
expected funding route is `claude.ai` and an `ANTHROPIC_API_KEY` in the environment refuses the
launch rather than paying for it; a `percent` allowance (the CLI's window utilization) permits a
launch iff `(100 - value) / 100 - interactive_reserve_fraction > 0`, because the invocation ceiling is
in turns and cannot be compared with a percentage, so the reserve is kept and the task ceilings bound
the turns; the readiness fixer falls back to a general-purpose run exactly once, and only when the
named agent role is missing; PRs are serial (nothing new launches while one is open); the worker only
edits and the controller does every git and `gh` step (under `acceptEdits` a worker cannot push);
`success` and `skipped` are the only green check conclusions; a repair round is counted before its
launch; `pr.required_checks` default to the five required checks on master. The Codex `sol` and Cursor
`composer-standard` ids remain candidates for PR 5.

## Verification

```bash
bash tests/scripts/autonomy-controller.test.sh          # the unit suite, as shell-lint runs it
bash scripts/ci/run-repo-gates.sh                        # every discovered repo gate
ASHLAR_MUTATION_RUNNER="bash tools/autonomy-controller/mutation-runner.sh" \
  scripts/mutation-check.sh --id <name> --file tools/autonomy-controller/ashlar_controller/<f>.py \
  --old '<text>' --new '<text>'                           # red / restore / clean / green, in a clone
```

## Not in PR 3

- The `phases` table exists but nothing writes it yet.
- No Codex or Cursor adapter (PR 5). `tick --adapter` accepts `fake` and `claude` only.
- A repair run that stops at the turn ceiling requeues like any run; the next observation of the still
  red head starts a new round with a fresh session rather than resuming the fixer's session (the rounds
  bound it). Premium escalation and provider transfer per PR are counted in `prs` but not driven.
- The `GhCli` argv shapes are the ones in the module docstring; they were exercised against the fake
  runner and in the live run of the integrated branch (see the PR body), not against every error GitHub
  can return.
- One live launch has gone through `tick --adapter claude` (2026-10-11, policy ceilings 6/12/2): a
  task whose title asked for a `hello.txt` in the workspace. The run settled `completed` with 2 turns
  charged, the file was there, the manifest carried the session id and the workspace, the transcript
  was found by session id, and `status` showed the allowance (73 percent of the five-hour window) and
  the estimated API value (0.053 USD) as two observations. That run exposed three defects, fixed in
  the same PR: `auth status --json` is pretty-printed over many lines (the adapter read only the last
  line), the CLI truncates a long project slug (the computed transcript path did not exist), and
  `task set-state` could never work because its positional overwrote the global `--state` path. The
  pilot against a real card is PR 4. Everything else is proven against `tests/fake_claude_cli.py`.
- The readiness fixer loop over the three LOCAL gate commands (`readiness run` / `readiness evaluate`)
  is still operator-driven; `tick` drives repairs from the PR's required checks (PR 3).
- `GhIssuePoster` is present but has not been exercised live (the session that wrote it had no
  authenticated `gh`). Tests use the fake poster.
- Operator account steps that later PRs need, and that the controller never performs: an
  authenticated `gh` with issue write for the bus and cards; the provider CLIs installed and logged
  in on the controller host. The controller purchases nothing, enables no paid overflow, changes no
  subscription and provisions no infrastructure.
