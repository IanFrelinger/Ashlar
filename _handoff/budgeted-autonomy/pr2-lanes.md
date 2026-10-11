# PR 2 (budgeted-autonomy phase B): lane spec

Branch base: `feat/budgeted-autonomy-claude-adapter` @ `530ad7f0a` (master `1d8bc7448` + two shared commits).
Package: `tools/autonomy-controller/` (Python 3.8+ stdlib only; `sqlite3`; no third-party imports).
Run the suite with `bash tests/scripts/autonomy-controller.test.sh` from the repo root. On this Windows host
three tests fail on untouched master too (host artefacts; ignore them, do not "fix" them):
`test_expired_lease_with_dead_pid_on_this_host_is_taken`, `test_restart_mid_launch_reconciles_before_launching`,
`test_world_writable_policy_is_refused`. Every other test must pass. Never run `dotnet`. Never push.
Commit on your worktree branch with the trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## Observed facts about the installed Claude Code CLI (2.1.263, Windows, 2026-10-11)

Declare ONLY these as verified; everything else is unverified and must be flagged so in code comments and
in `AdapterCapabilities`.

- Executable: npm shim `claude.cmd` wraps `.../node_modules/@anthropic-ai/claude-code/bin/claude.exe`; launch the exe.
- Headless: `claude -p <prompt> --output-format json` prints ONE JSON object (last stdout line) of
  `type: "result"` with fields: `session_id`, `is_error` (bool), `subtype` (`"success"` even on auth failure),
  `terminal_reason` (`"api_error"` observed), `api_error_status` (int or null), `result` (text),
  `num_turns`, `total_cost_usd`, `duration_ms`, `duration_api_ms`, `usage{input_tokens,output_tokens,
  cache_creation_input_tokens,cache_read_input_tokens,service_tier,speed:"standard"}`, `modelUsage{}`,
  `permission_denials[]`, `fast_mode_state:"off"`, `stop_reason`, `uuid`.
- `--output-format stream-json --verbose` emits NDJSON: `{"type":"system","subtype":"init",...}` first
  (carries `tools`, `model`, `permissionMode`, `session_id`), then messages, then the `result` object.
- `--session-id <uuid>` is honoured: the result's `session_id` equalled the one we passed.
- `--resume <uuid>` is accepted and reused the same session id; the transcript file grew.
- Transcript: `<projectsDirectory>/<slug>/<session_id>.jsonl` where `projectsDirectory` comes from
  `claude auth status --json` and `slug` = cwd with every char not `[A-Za-z0-9]` replaced by `-`.
  `--no-session-persistence` suppresses it.
- `--max-turns N` is accepted (hidden from `--help`). Its effect at the limit is UNVERIFIED.
- `--model`: recognised ids include `sonnet`, `opus`, `haiku`, `fable`, `claude-sonnet-5`, `claude-sonnet-4-5`,
  `claude-opus-4-1`, `claude-haiku-4-5`, `claude-fable-5`, `claude-fable-5-1`, `claude-opus-5`. NOT recognised:
  `claude-sonnet-5-5`, `claude-opus-5-5`, `claude-haiku-5-5`. An unrecognised id prints to stderr
  `[claude-code:unrecognized_model] {"model":"<id>","query_source":"sdk"}` and exits 1 before any request.
- `--effort low|medium|high|xhigh|max` accepted. There is NO speed flag; `usage.speed` is always `"standard"`
  and `fast_mode_state` is `"off"`. The adapter accepts only `speed == "standard"` and refuses anything else.
- `--permission-prompts none` + `--permission-mode manual|acceptEdits|...`: anything that would prompt is denied;
  denials appear in `permission_denials` (shape unverified beyond the empty list).
- `--tools ""` disables tools; `--tools Read,Bash` restricts; `--agent <name>` selects a named agent role;
  `--add-dir`, `--json-schema <schema>` (structured output; UNVERIFIED live).
- Auth: `claude auth status --json` -> `{loggedIn, authMethod:"claude.ai", apiProvider:"firstParty",
  subscriptionType:"max", projectsDirectory, email, orgId, orgName}`. Observed auth failure in the result:
  `is_error:true, terminal_reason:"api_error", api_error_status:null,
  result:"Failed to authenticate: OAuth session expired and could not be refreshed"` and under `--bare`
  `result:"Not logged in · Please run /login"`; the debug log says
  `API auth_error: OAuth refresh token is no longer valid; run /login to re-authenticate`. Exit code 1.
- No headless source for the subscription allowance exists: `subscription_allowance` is `unavailable`.
- Environment: strip `CLAUDECODE` and every `CLAUDE_CODE_*` from the child env (a nested session changes
  behaviour). Keep `ANTHROPIC_*` so the funding-route check can SEE a key; an `ANTHROPIC_API_KEY` in the env
  means route `api-key` (paid spend), not the subscription.

## Lane A: `ashlar_controller/adapters/claude.py` + `tests/test_claude_adapter.py` + `tests/fake_claude_cli.py`

```python
class ClaudeAdapter:
    name = "claude"
    capabilities = AdapterCapabilities(exact_resume=True, cancel=False, structured_output=True, usage_observation=True,
                                       transcript_retention=True, turn_limit=False, funding_route_check=True,
                                       evidence="probe of claude 2.1.263 on 2026-10-11; see README 'Claude adapter'")
    def __init__(self, runs_dir: str, cli: Optional[str] = None, expected_route: str = "claude.ai",
                 env: Optional[Dict[str, str]] = None, clock=None, permission_mode: str = "acceptEdits"): ...
```
- `cli=None` resolves `shutil.which("claude")`; on Windows a `.cmd` resolves to the sibling `bin/claude.exe`.
  `cli` may also be a list prefix (tests pass `[sys.executable, "<path>/fake_claude_cli.py"]`).
- `launch(request: LaunchRequest) -> LaunchResult`:
  1. `request.speed != "standard"` -> `raise LaunchRefused(StopReason.LAUNCH_FAILED, "speed <x> is not supported by the Claude CLI; only standard")`.
  2. Funding route check: run `<cli> auth status --json` (timeout 30s). Route = `"api-key"` if `ANTHROPIC_API_KEY`
     is in the child env, else `authMethod` from the JSON. `loggedIn` false -> `LaunchRefused(StopReason.AUTH_MISSING, ...)`.
     Route != `expected_route` -> `LaunchRefused(StopReason.FUNDING_ROUTE_MISMATCH, "<observed> would be billed; policy selected <expected>")`.
     The command failing to run at all -> `LaunchRefused(StopReason.AUTH_MISSING, "<error>")`.
  3. session id: `request.resume_session_id` or a fresh `uuid4()`; argv:
     `[-p, prompt, --output-format, json, --model, M, --effort, E, --max-turns, str(ceiling), --permission-prompts, none,
      --permission-mode, <mode>, --no-chrome]` plus `--session-id S` (new) or `--resume S` (resume), plus
     `--agent <name>` when `request.agent`, plus `--tools <comma-joined>` when `request.allowed_tools is not None`
     (an empty list -> `--tools ""`).
  4. run dir `<runs_dir>/<run_id>/`: write `manifest.json` BEFORE spawning (run_id, task_id, session_id, resume_of,
     argv, cwd, started_at, pid=None, transcript_path=<expected path or null>), then `Popen` with stdout ->
     `stdout.json`, stderr -> `stderr.log`, cwd = `request.workspace or runs_dir/<run_id>/workspace` (create it),
     env = cleaned env; on POSIX `start_new_session=True`. Update manifest with pid. If Popen raises -> re-raise as
     `RuntimeError` (definite launch failure). If the manifest was written but the spawn outcome is unknown
     (e.g. OSError after fork), raise `LaunchOutcomeUnknown`.
  5. return `LaunchResult(native_session_id=S, native_run_handle=run_id, workspace=<cwd>)`.
- `poll(handle) -> RunStatus`: read manifest; `cancelled_at` set -> `RunStatus(CANCELLED, StopReason.CANCELLED,
  consumed_units=None, result={"workspace":..., "transcript": ...})`. pid alive (`os.kill(pid,0)` on POSIX;
  on Windows use `ctypes`/`OpenProcess` or `tasklist`-free approach: `subprocess` handle is not available after a
  restart, so use `psutil`-free check via `os.kill(pid, 0)` which works on Windows for existence) -> `RunStatus(RUNNING)`.
  Exited: parse the LAST line of stdout.json that starts with `{` as JSON. Classify, in this order:
  - stderr contains `[claude-code:unrecognized_model]` -> FAILED, `StopReason.MODEL_UNRECOGNIZED`, result={"model": M}.
  - no JSON result -> FAILED, `StopReason.NO_FINAL_RESULT`, result={"exit": rc, "stderr_tail": last 2000 chars}.
  - `is_error` false and `subtype == "success"` -> FINISHED, `COMPLETED`.
  - `subtype == "error_max_turns"` -> FAILED, `StopReason.INVOCATION_CEILING`.
  - auth: `api_error_status == 401` or result text matches /not logged in|oauth|authenticat/i -> `AUTH_MISSING`,
    result["blocker"] = the result text.
  - quota: `api_error_status == 402` or text matches /usage limit|out of (extra )?usage|quota|limit reached|resets at/i -> `QUOTA_EXHAUSTED`.
  - rate limit: `api_error_status == 429` or /rate.?limit|too many requests/i -> `RATE_LIMITED`.
  - transient: `api_error_status in (408, 500, 502, 503, 504, 529)` or /overloaded|timed out|ECONNRESET|ENOTFOUND|network/i -> `TRANSIENT`.
  - `permission_denials` non-empty -> `PERMISSION_MISSING`, result["blocker"] lists tool names.
  - otherwise -> `TASK_FAILED`, `failure_identity = "claude:" + sha256(normalised first 200 chars of result text)[:12]`.
  Always: `consumed_units = float(num_turns)` when present (per-invocation; cumulative-on-resume is UNVERIFIED,
  say so in a comment); `usage = UsageObservation(provider="claude", kind=UsageKind.ESTIMATED_API_VALUE, unit="usd",
  freshness=FRESH, value=total_cost_usd, native_session_id=S, raw={tokens...})`; `observations=[UsageObservation(
  provider="claude", kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="turns", freshness=UNAVAILABLE, native_session_id=S)]`;
  `transcript_path` from the manifest; `result` also carries `exit`, `subtype`, `terminal_reason`, `api_error_status`,
  `duration_ms`, `result_text` (first 2000 chars).
- `cancel(handle)`: write `cancelled_at` into the manifest FIRST, then terminate: Windows `taskkill /PID <pid> /T /F`;
  POSIX `os.killpg(pid, SIGTERM)`; a missing process is not an error. Never touch the workspace or the transcript.
- `reconcile(run_id, handle) -> ReconcileResult`: manifest missing -> `found=False`; else `found=True`,
  `native_session_id`, `native_run_handle=run_id`, `status=self.poll(run_id)`.
- `observe_usage() -> UsageObservation`: the UNAVAILABLE allowance observation (no headless source).
- `tests/fake_claude_cli.py`: a stand-alone script the tests use as `cli`. It reads a JSON "script" from the env var
  `FAKE_CLAUDE_SCRIPT` (path). Modes: `auth status --json` prints the script's `auth` object; `-p` run: sleeps
  `sleep_seconds`, writes `stderr`, prints `stdout` (the result JSON, with `session_id` taken from `--session-id`/`--resume`
  unless the script overrides it), writes the argv it received to `<FAKE_CLAUDE_ARGV_OUT>` and exits `exit`.
  It must also create `<projectsDirectory>/<slug>/<session>.jsonl` (append a line) when `persist` is true, so the
  transcript path logic is exercised.
- Tests (unittest, same style as `tests/test_controller.py`; temp dirs via `tempfile.mkdtemp`): one per stop reason
  above; session id passthrough; resume passes `--resume` and not `--session-id`; `--agent`/`--tools` plumbing;
  model/effort/max-turns on argv; speed refusal; route mismatch (script auth `authMethod:"console"`, and separately
  `ANTHROPIC_API_KEY` set in the adapter env); `loggedIn:false` -> AUTH_MISSING refusal, no process spawned
  (assert no argv file written); cancel of a sleeping run preserves the run dir, manifest and a file in the
  workspace, and poll afterwards says CANCELLED; reconcile found/not found; a stub that exits without JSON ->
  NO_FINAL_RESULT; unrecognised-model stderr -> MODEL_UNRECOGNIZED. Also an end-to-end test through
  `Controller` (helpers.make_controller with `adapters={"claude": ClaudeAdapter(...)}` and a policy whose
  `claude` provider is enabled with model `claude-sonnet-5`): quota script -> task `waiting-quota`, exactly one
  launch, no second argv file after further ticks; auth script -> `blocked-auth`, one launch.

## Lane B: `ashlar_controller/readiness.py` + `tests/test_readiness.py`

Pure functions over the three gate commands' outputs; one bounded fixer launch; stable failure identities.
```python
@dataclass
class GateFailure: gate: str; identity: str; evidence: str      # identity is stable across runs/cycles
@dataclass
class GateReport: source: str; commit: Optional[str]; failures: List[GateFailure]; passed: List[str]; raw: Dict[str, Any]

def parse_readiness_json(text: str) -> GateReport        # scripts/readiness-gate-local.sh --json output: {layer, commit, started_at, failed, gates:[{gate,status,exit_code,duration_s,log,log_tail}]}
def parse_repo_gates(text: str) -> GateReport            # scripts/ci/run-repo-gates.sh stdout: "::group::<path>" ... "::error::<path> failed (exit N)" ... "repo-gates: all N gate(s) passed" | "repo-gates: K of N gate(s) failed:" + "  <path>" lines
def parse_container_test(text: str) -> GateReport        # scripts/test-in-container.sh: "Test run for <path>/<name>.dll (<tfm>)" headers and "Failed!  - Failed: F, Passed: P, Skipped: S, Total: T, Duration: D - <name>.dll (<tfm>)" lines, plus "  Failed <FullyQualifiedTestName> [<ms> ms]" lines
def failure_identity(gate: str, evidence_lines: Iterable[str]) -> str   # "gate:<name>:" + sha256 of the NORMALISED evidence[:12]
def normalise(line: str) -> str   # strip timestamps (ISO and HH:MM:SS), durations ("[12 ms]", "dur=3s", "Duration: 1 s"), absolute paths (keep basename), hex >= 7 chars, temp dirs, trailing whitespace, CR
GATE_COMMANDS = {"repo-gates": ["bash", "scripts/ci/run-repo-gates.sh"],
                 "readiness-local": ["bash", "scripts/readiness-gate-local.sh", "--layer", "application", "--json", "<out>"],
                 "container-tests": ["bash", "scripts/test-in-container.sh", "--repo", "<workspace>"]}
def run_gate(name: str, workspace: str, runner=subprocess.run, timeout: int = 3600) -> GateReport   # executes and parses; never retries; a crash of the script itself raises GateInfrastructureError(evidence)
MISSING_ROLE_PATTERNS = (r"unknown agent", r"agent .* not found", r"no agent named", r"agent not found")
def fixer_request(failure: GateFailure, task_id: str, run_id: str, provider_spec: Dict[str, Any], invocation_ceiling: int, workspace: str, agent: Optional[str] = "readiness-fixer") -> LaunchRequest   # role="readiness-fixer", prompt built from the evidence, allowed_tools None
def launch_fixer(adapter, request) -> LaunchResult   # tries request; if the adapter raises an error whose text matches MISSING_ROLE_PATTERNS, retries ONCE with agent=None (general-purpose) and records `fallback="general-purpose"` on the request; any other error propagates (no fallback)
def refuted(before: GateReport, after: GateReport) -> List[GateFailure]   # identities present in both = the fix was refuted
```
Tests: the three parsers on canned outputs in the exact shapes above (passing and failing); identities equal across
two outputs that differ only in timestamps/durations/paths; identities differ when the failing test set differs;
`run_gate` with a fake runner (asserts the argv, the cwd, no retry); infrastructure error when the script exits
with no parsable output; `launch_fixer` falls back exactly once on a missing-role error and never on any other
error (use `FakeAdapter` subclass raising); `refuted` logic. Use `from .helpers import ...` only if needed.

## Lane C: controller/CLI integration, records, docs

1. `controller.py`:
   - `_apply_outcome`: record every `status.observations` via `budget.record_usage` (deltas by session); new branch for
     `StopReason.INVOCATION_CEILING` -> task `QUEUED` with `blocker="turn ceiling reached; next attempt resumes session <id>"`
     (no failure_retry bump; the pool bounds it); `StopReason.MODEL_UNRECOGNIZED` -> `PARKED_DECISION` with the
     blocker naming the model id; `StopReason.FUNDING_ROUTE_MISMATCH` -> `BLOCKED_AUTH` with the blocker.
   - `tick`: catch `LaunchRefused` from `adapter.launch` -> settle the run FAILED with its stop_reason, consumed 0,
     task state by the same mapping (AUTH_MISSING -> BLOCKED_AUTH, FUNDING_ROUTE_MISMATCH -> BLOCKED_AUTH,
     MODEL_UNRECOGNIZED -> PARKED_DECISION, else QUEUED with LAUNCH_FAILED) and return `TickReport("blocked", stop_reason, ...)`.
   - `tick`: when the task's latest run ended with `INVOCATION_CEILING` and has a `native_session_id`, set
     `LaunchRequest.resume_session_id` to it (exact resume); journal `"resume"`.
   - `tick`: pass `workspace=task["base_sha"] and None` -> replace with a real `workspace` column use: `tasks.workspace`
     is not in the schema; add nothing to the schema. Instead let the adapter choose (`request.workspace=None`).
   - `evaluate_readiness(task_id, report: GateReport) -> Dict`: no failures -> task DONE (`completed`); failures ->
     apply the task-failed path with `failure_identity=failures[0].identity` (same retry counter: QUEUED once per
     identity, then PARKED_FAILURE); returns what it did. Journal `readiness-evaluated`.
2. `cli.py`: `tick --adapter {fake,claude}` (default fake) building `{"fake": FakeAdapter(), "claude": ClaudeAdapter(runs_dir=<state dir>/runs)}`;
   new `readiness run --gate <name> --workspace <path> [--task-id <id>]` (runs `readiness.run_gate`, prints the report,
   and when `--task-id` is given calls `evaluate_readiness`) and `readiness evaluate --task-id <id> --report <json file>`.
3. `policy.example.json`: `claude.model` -> `"claude-sonnet-5"` (recognised by the CLI; API acceptance unverified) and
   update the `_comment`. Keep everything else.
4. `README.md` (tools/autonomy-controller): new sections "Claude adapter" (what was observed, verbatim shapes from
   the facts above, what is unverified, the operator step `claude auth login`, env stripping, the funding-route
   check), "Readiness fixer" (the three gate commands, failure identities, the one retry, the missing-role fallback
   rule), updated command table, updated "Not in PR 2" (PR 3: PR lifecycle; PR 5: Codex/Cursor). Keep the
   "integrator decisions" section accurate: add "model id claude-sonnet-5 (CLI-recognised; API unverified)".
5. `CHANGELOG.md` `[Unreleased]`: an "Added — budgeted autonomy controller, PR 2 (Claude adapter and bounded readiness)"
   block above the PR 1 block. `docs/DocsIndex.md:51`: extend the controller row to mention PR 2.
6. `tests/scripts/autonomy-controller.test.sh`: raise `MINIMUM_TESTS` to 110 (lanes A and B add tests; adjust to
   the real count minus a margin of 10 after integration).
7. Tests in `tests/test_controller.py` (or a new `tests/test_integration_pr2.py`): LaunchRefused mapping (three
   reasons); INVOCATION_CEILING requeues and the next launch carries `resume_session_id`; observations recorded;
   `evaluate_readiness` retry-once-then-park with the identity surviving a second cycle and a restart.
Lane C may stub `ClaudeAdapter`/`readiness` imports behind `try/except ImportError` only in `cli.py`; the tests for
Lane C must not depend on Lanes A/B.

## ADDENDUM (2026-10-11, after `claude auth login`): live observations, model `claude-sonnet-5`, report `probe/probe-report-2.json`

These supersede the "UNVERIFIED" marks above where they overlap.
- Success: `is_error:false, subtype:"success", terminal_reason:"completed", stop_reason:"end_turn"`, `result` = the text.
- `num_turns` and `total_cost_usd` are PER INVOCATION, not session-cumulative: first call num_turns 1 / cost 0.2817, the `--resume` call on the same session num_turns 1 / cost 0.0144. The result's `session_id` equalled the id passed with `--session-id`, and with `--resume` (three times).
- `--max-turns 1` on a task needing a tool call + answer: `is_error:true, subtype:"error_max_turns", terminal_reason:"max_turns", num_turns:2, stop_reason:"tool_use"`. The turn limit is enforced.
- Permission denial (`--permission-prompts none`, manual mode, Bash): the run COMPLETES with `is_error:false, subtype:"success"` and `permission_denials:[{"tool_name":"Bash","tool_input":{...},"tool_use_id":"toolu_..."}]`; the result text says the tool call was automatically denied. So check `permission_denials` BEFORE treating a success as completed.
- Unrecognised model (`claude-no-such-model-0`): stderr `[claude-code:unrecognized_model] {...}`, result `is_error:true, terminal_reason:"api_error", api_error_status:404, total_cost_usd:0, result:"There's an issue with the selected model (...). It may not exist or you may not have access to it."`, exit 1.
- Cancellation: `terminate()` on `claude.exe` while the model request was in flight: process exited (code 1), no `result` event, the transcript `.jsonl` was persisted (12 lines), and a later `--resume <same id>` succeeded with the same `session_id` (num_turns 1). Cancel and resume-after-cancel are verified.
- `--json-schema`: result carries `structured_output: {"answer":"4"}` and `result` = the JSON text; `num_turns:2`. `--no-session-persistence`: no transcript file is written.
- stream-json emits, in order: `{"type":"system","subtype":"init",...}` (fields: `model`, `permissionMode`, `apiKeySource:"none"`, `claude_code_version`, `session_id`, `cwd`, `tools:[...]`, `mcp_servers:[...]`, `agents:[...]`, `slash_commands:[...]`), then `{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":<epoch s>,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.69,"resetsAt":1791702600},"seven_day":{"utilization":0.18,"resetsAt":1792285200}}},"session_id":...}`, then `assistant` messages, then the `result` object (same fields as `--output-format json`). THE RATE LIMIT EVENT IS THE SUBSCRIPTION ALLOWANCE SOURCE: utilization is a fraction of the window; `isUsingOverage` true would mean paid overflow.
- With `--tools default`, headless tools include `Workflow`, `Task`, `Bash`, `Edit`, `Read`, `Skill`, etc. (the converge-readiness command's Workflow tool IS available headless). With `--strict-mcp-config` and no `--mcp-config`, `mcp_servers:[]` and the first-call cache creation dropped from 70k tokens (the operator's claude.ai MCP connectors: Gmail, Drive, Slack, Docs) to 9k; cost 0.043 vs 0.28. Every controller launch MUST pass `--strict-mcp-config`.
- `modelUsage` lists per-model figures (a `claude-haiku-4-5` helper call appears beside the main model); `total_cost_usd` is `costBasis:"list"` = an ESTIMATE of API value, never a charge on a subscription.

Consequences for the lanes:
- Lane A: launch with `--output-format stream-json --verbose --strict-mcp-config`; stdout is NDJSON: take the LAST line whose JSON has `type=="result"`; the `rate_limit_event` (last one seen) becomes a `UsageObservation(kind=SUBSCRIPTION_ALLOWANCE, unit="percent", freshness=FRESH, value=round(five_hour.utilization*100, 2), total=100.0, native_session_id=S, observed_at=<now>, raw=rate_limit_info)`; if no rate_limit_event was seen, the allowance observation is UNAVAILABLE. Put the allowance observation in `RunStatus.usage`?? NO: `usage` is the one whose delta can stand in for consumption; set `consumed_units=float(num_turns)` (per-invocation, verified) and `usage=None`; put BOTH the allowance observation and the `ESTIMATED_API_VALUE` observation (`unit="usd"`, `value=total_cost_usd`, `native_session_id=run_id` so the recorded delta equals the per-invocation value) into `observations`. Classification order: unrecognised-model stderr or `api_error_status==404` with the model text -> MODEL_UNRECOGNIZED; no result -> NO_FINAL_RESULT (or CANCELLED when the manifest says so); `subtype=="error_max_turns"` -> INVOCATION_CEILING; `is_error` auth/quota/rate/transient as specified; `permission_denials` non-empty -> PERMISSION_MISSING even when `is_error` is false; `is_error:false` -> COMPLETED; else TASK_FAILED. `rate_limit_info.isUsingOverage == true` in the stream -> treat the run as `StopReason.PAID_ROUTE_DISABLED`-class evidence: put `result["overage"]=True` and classify as QUOTA_EXHAUSTED with blocker "the provider is billing overage; paid inference is off" (no fallback). Capabilities: exact_resume=True, cancel=True, structured_output=True, usage_observation=True, transcript_retention=True, turn_limit=True, funding_route_check=True.
- Lane C: `budget.allowance_decision` must handle an observation whose `unit` is `"percent"` (total 100): permitted iff `(100 - value)/100 - interactive_reserve_fraction > 0` (the invocation ceiling is in turns and cannot be compared to a percentage; say so in the docstring and the README, as an integrator decision). Keep the existing arithmetic for observations in the pool's own unit. README: the Workflow tool IS available headless; the allowance comes from `rate_limit_event`; `--strict-mcp-config` is mandatory and why (cost and exposure of the operator's connectors); per-invocation figures; the exact shapes above.
