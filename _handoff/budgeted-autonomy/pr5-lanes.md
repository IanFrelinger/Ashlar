# PR 5 lanes: Codex and Cursor adapters, bounded provider transfer (card #779)

Base: the tip of `fix/budgeted-autonomy-pilot-findings` (the integrator names the SHA when launching the lane).
Package: `tools/autonomy-controller/ashlar_controller/`. Python 3.8+, standard library only. Every lane: one new
adapter module, one fake CLI script under `tests/`, one new test file, one commit, nothing else touched (the
integrator wires `cli.py`, `controller.py`, `lifecycle.py`, `policy.example.json`, README, CHANGELOG). Tests run
with `bash tests/scripts/autonomy-controller.test.sh` from the repo root (host; `OK (skipped=1)` on Windows is
green). Write files with LF line endings (`newline="\n"`; the repo has `core.autocrlf=true`). Never run a real
`codex`, `cursor-agent` or `claude` from a test: the adapter is always given `cli=[sys.executable, <fake>]`.

Shape to copy: `adapters/claude.py` (manifest before spawn, stdout/stderr files, `poll` classifies from the
manifest and the stream, `cancel` records first then kills the tree, `reconcile` from the manifest,
`observe_usage`), `tests/fake_claude_cli.py` (a JSON-scripted stand-in) and `tests/test_claude_adapter.py`.
The protocol is `adapters/base.py` (`ProviderAdapter`, `AdapterCapabilities`); the models are in `models.py`
(`LaunchRequest`, `LaunchResult`, `RunStatus`, `ReconcileResult`, `UsageObservation`, `StopReason`,
`LaunchRefused`, `LaunchOutcomeUnknown`). `locate_transcript` in `claude.py` is Claude-specific; each lane writes
its own locator.

**Declare only what was observed.** `AdapterCapabilities` flags are True only for behaviour the probes below
recorded; the module docstring lists each observation with its probe id, the way `claude.py` does. Anything else
is False and listed under "unverified" in the docstring, for the README.

## Lane A: `adapters/codex.py`, `tests/fake_codex_cli.py`, `tests/test_codex_adapter.py`

Observed on 2026-10-12 against `codex-cli 0.162.0-alpha.17.2` (the Codex desktop app's binary at
`%LOCALAPPDATA%\OpenAI\Codex\bin\<hash>\codex.exe`, beside its `codex-code-mode-host.exe`; probe reports
`probe-codex-report-1.json` and `-2.json`, scripts `probe_codex_runtime.py` and `_2.py`):

- `codex login status` prints `Logged in using ChatGPT` (exit 0). `~/.codex/auth.json` has `auth_mode:
  "chatgpt"`. A logged-out text is unobserved.
- `codex exec --json --skip-git-repo-check -C <cwd> -s workspace-write -c approval_policy="never"
  -c model_reasoning_effort="medium" -c mcp_servers={} -m gpt-6-astra -o <last.txt> <prompt>` from
  `cwd=<workspace>`, stdin DEVNULL, prints NDJSON on stdout, exit 0:
  `{"type":"thread.started","thread_id":"<uuid7>"}`, `{"type":"turn.started"}`, then items:
  `{"type":"item.started"|"item.completed","item":{"id":"item_N","type":"agent_message","text":...}}`,
  `{"type":"item.started","item":{"id":..,"type":"command_execution","command":"...","aggregated_output":"","exit_code":null,"status":"in_progress"}}`
  and its `item.completed` with `exit_code` and `status:"completed"|"failed"`, then
  `{"type":"turn.completed","usage":{"input_tokens","cached_input_tokens","cache_write_input_tokens","output_tokens","reasoning_output_tokens"}}`.
  The file was created by a PowerShell `Set-Content` through `command_execution`. `-o` writes the last agent
  message to the file. stderr says `Reading additional input from stdin...` when stdin is not closed: ALWAYS
  spawn with `stdin=subprocess.DEVNULL`.
- `--ignore-user-config` makes the write `Rejected("... rejected: blocked by policy")` on stderr and the model
  reports a read-only session: DO NOT pass it. The operator's `~/.codex/config.toml` stays loaded; the adapter
  overrides what matters on the command line (`-m`, `-s`, `approval_policy`, `model_reasoning_effort`,
  `mcp_servers={}` so the operator's MCP servers never enter a run).
- Unrecognised model (`-m no-such-model-0`, and the policy example's `sol`): events `thread.started`,
  `item.completed` (two agent messages may precede), `turn.started`, then
  `{"type":"error","message":"{\"type\":\"error\",\"status\":400,\"error\":{\"type\":\"invalid_request_error\",\"message\":\"The 'sol' model is not supported when using Codex with a ChatGPT account.\"}}"}`
  and `{"type":"turn.failed","error":{"message":<same>}}`; no `turn.completed`; exit 1.
  `gpt-6-astra` is accepted (`-m gpt-6-astra` explicit; the session file's `"model":"gpt-6-astra"`).
- Resume: `codex exec resume --json --skip-git-repo-check <thread_id> <prompt>` (without the flag:
  `Not inside a trusted directory and --skip-git-repo-check was not specified.`, exit 1, no events) emits the
  same shapes with the SAME `thread_id` in `thread.started`, and continued the edit (`hello.txt` gained a line).
- Cancel: `taskkill /T /F` on the process mid-run exits 1 with no `turn.completed`; the session file
  `~/.codex/sessions/<YYYY>/<MM>/<DD>/rollout-<timestamp>-<thread_id>.jsonl` exists (its first line is
  `session_meta` with `cli_version`, `cwd`, `model_provider`); `exec resume` of that thread then succeeded.
- `--output-schema <file>`: the last agent message is the JSON (`{"answer":"4"}`).
- `--ephemeral` writes no session file. `-c model_reasoning_effort="low"` is accepted.
- No turn limit flag exists (`turn_limit=False`). No allowance or cost figure is printed; `codex app-server`
  answered `initialize` but neither `account/rateLimits/read` nor `getAccountRateLimits` produced a reply, so
  the allowance is UNAVAILABLE (`usage_observation=False`; `observe_usage` returns an `unavailable` observation).
- The bundled `~/.codex/.sandbox-bin/codex.exe` (0.162.0-alpha.2) has no `codex-code-mode-host.exe` beside it:
  every tool call fails closed (`Code Mode is unavailable because failed to spawn code-mode host ...`), so the
  model reports it could not edit. The resolver must never pick it.

```python
class CodexAdapter:
    name = "codex"
    capabilities = AdapterCapabilities(exact_resume=True, cancel=True, structured_output=True, usage_observation=False,
                                       transcript_retention=True, turn_limit=False, funding_route_check=True,
                                       evidence="probe-codex-report-1.json, -2.json (2026-10-12)")
    def __init__(self, runs_dir: str, *, cli: Optional[Union[str, Sequence[str]]] = None, env: Optional[Dict[str, str]] = None,
                 clock=None, codex_home: Optional[str] = None, expected_route: str = "chatgpt",
                 sandbox: str = "workspace-write")
```
- `cli=None` resolves, in order: `shutil.which("codex")`; the newest `%LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe`
  that has `codex-code-mode-host.exe` beside it; `~/.codex/plugins/.plugin-appserver/codex.exe` on the same
  condition. A path given explicitly is used as given. `resolve_cli()` is a module function the README can cite.
- `_env`: strip `CLAUDECODE` and `CLAUDE_CODE_*`; keep `OPENAI_*`; set `CODEX_HOME` when `codex_home` is given.
- Route check before every launch: run `[cli, "login", "status"]`; stdout containing `Logged in using ChatGPT`
  is route `chatgpt`; `OPENAI_API_KEY` in the child env is route `api-key` (refuse `funding-route-mismatch`
  unless `expected_route` says so); anything else refuses `auth-missing` with the text as the blocker (the
  logged-out text is unobserved: say so in the blocker).
- `launch`: refuse `speed != "standard"` (`launch-failed`). Manifest `<runs_dir>/<run_id>/manifest.json` BEFORE
  the spawn: `run_id, task_id, provider:"codex", thread_id (None until thread.started is read), resume_of,
  model, argv, cwd, started_at, pid, codex_home, sessions_dir`. Argv as observed above; `exec resume --json
  --skip-git-repo-check <id> <prompt>` when `request.resume_session_id`. stdout to `stdout.json`, stderr to
  `stderr.log`, stdin DEVNULL. `LaunchResult(native_session_id=<thread_id or None>, native_run_handle=run_id,
  workspace=...)`: read `thread.started` from `stdout.json` for up to 10 s after the spawn (poll the file), then
  write `thread_id` into the manifest; None when not yet seen (poll fills it in later).
- `poll(handle)`: running while the pid is alive. Then parse `stdout.json`; classify in this order:
  1. the manifest has `cancelled_at`: `cancelled`;
  2. an `error`/`turn.failed` whose message (parsed as JSON when it is JSON) has `status` 400 and text
     matching `model is not supported`: `model-unrecognized`, `consumed_units=0.0`, result `model`;
  3. status 401 or text matching `not logged in|unauthori|authenticat`: `auth-missing`; 402 or text
     `usage limit|quota|limit reached|resets`: `quota-exhausted`; 429 or `rate limit|too many requests`:
     `rate-limited`; 408/5xx/529 or `overloaded|timed out|network|ECONNRESET`: `transient`;
  4. no `turn.completed` and no error event: `no-final-result`;
  5. `turn.completed` present and the last `agent_message` is not empty: `completed`; `turn.failed` with any
     other message: `task-failed`, identity `codex:<sha256 of the normalised first 200 chars>[:12]`.
  `consumed_units` = the number of `item.completed` events whose item type is not `error` (model steps: an
  integrator decision, documented; the pool unit for codex is `turns` and one completed item is one). The
  `result` dict carries `thread_id, last_message[:2000], items_by_type, usage (tokens), exit, stream_events,
  last_message_path, transcript (session file path or None)`. `observations`: one `subscription_allowance`
  UNAVAILABLE observation (raw: the token usage) keyed by thread id. `transcript_path` = the session file
  located by thread id under `<codex_home>/sessions/**/rollout-*-<thread_id>.jsonl`.
- `cancel(handle)`: write `cancelled_at` into the manifest first, then `taskkill /T /F /PID` (Windows) or
  `killpg` (POSIX). Never touches the workspace or the session file.
- `reconcile(run_id, handle)`: no manifest: `found=False`; manifest with a pid still alive: `found=True`,
  status RUNNING; otherwise `found=True` with `poll`'s classification.
- `observe_usage()`: an `unavailable` `subscription_allowance` observation (unit `turns`).

`tests/fake_codex_cli.py`: a JSON-scripted stand-in (`FAKE_CODEX_SCRIPT`, `FAKE_CODEX_ARGV_OUT` like the Claude
fake): keys `login_status_stdout` (default `Logged in using ChatGPT`), `login_status_exit`, `events` (the NDJSON
objects an `exec` prints, with `thread.started` defaulting its `thread_id` to the resume id or a fresh uuid),
`exit`, `stderr`, `sleep_seconds`, `write_session_file` (when true, writes
`<CODEX_HOME>/sessions/2026/10/12/rollout-2026-10-12T00-00-00-<thread_id>.jsonl` with a `session_meta` line),
`last_message_file` (honour `-o`). It records argv, cwd and the env keys `CLAUDECODE`, `CLAUDE_CODE_*`,
`OPENAI_*`, `CODEX_HOME`, and whether stdin was a TTY/closed.

`tests/test_codex_adapter.py` (>= 16 tests, each killing a specific mutation): the argv shape (every observed
flag, `-m`, the effort, `mcp_servers={}`, no `--ignore-user-config`, prompt last, `--session-id`-like id is NOT
passed on a fresh launch, `exec resume ... <id>` on resume); stdin is closed; `CLAUDE_CODE_*` stripped and
`OPENAI_API_KEY` kept; route refusals (not logged in -> `auth-missing`, `OPENAI_API_KEY` -> `funding-route-mismatch`,
`login status` failing -> `auth-missing`, nothing spawned, manifest absent); completed run with consumed = the
non-error completed items; model-unrecognised from the observed 400 text with consumed 0; cancel writes
`cancelled_at` then kills and poll says `cancelled`; no `turn.completed` -> `no-final-result`; resume carries the
thread id and the session file is located by it; `reconcile` on a dead pid classifies, on a missing manifest
says not found; `observe_usage` is unavailable; the resolver refuses a `codex.exe` with no code-mode host
beside it and prefers the newest app binary that has one (make temp dirs); a `Controller` tick over the adapter
(fake CLI) settles `completed` with the right charge, through `ControllerCase` with a policy whose `codex`
provider is enabled.

## Lane B: `adapters/cursor.py`, `tests/fake_cursor_cli.py`, `tests/test_cursor_adapter.py`

Observed on 2026-10-12 against the Cursor IDE's embedded agent CLI `2026.10.01-e373342`
(`%APPDATA%\Cursor\User\globalStorage\anysphere.cursor-agent-worker\agent-cli\.local\share\cursor-agent\versions\<v>\cursor-agent.cmd`,
a PowerShell launcher of `index.js` on the bundled `node.exe`; report `probe-cursor-report-1.json`):

- `cursor-agent --version` prints `2026.10.01-e373342`. `cursor-agent status` prints `Not logged in` (exit 0).
  `cursor-agent about` prints `About Cursor CLI` with `CLI Version`, `Model Auto`, `Subscription Tier Unknown`,
  `User Email Not logged in`.
- Logged out, `cursor-agent models` exits 1 with stderr `Error: Authentication required. Run 'agent login', pass
  --api-key/--auth-token, or set CURSOR_API_KEY/CURSOR_AUTH_TOKEN.`, and every `-p` run exits 1 with stderr
  `Error: Authentication required. Please run 'agent login' first, or set CURSOR_API_KEY environment variable.`
  and empty stdout.
- From `--help` only (UNEXERCISED, the owner has not logged the CLI in): `-p/--print`, `--output-format text|json|stream-json`,
  `--stream-partial-output`, `--mode plan|ask`, `--resume [chatId]`, `--continue`, `--model <m>`, `--list-models`,
  `-f/--force` (run commands unless denied), `--yolo`, `--sandbox enabled|disabled`, `--trust`, `--workspace <path>`,
  `--add-dir`, `-w/--worktree`; commands `login`, `logout`, `status|whoami`, `models`, `about`, `create-chat`
  (prints a new chat id), `resume`, `ls`. No turn-limit flag is listed.

```python
class CursorAdapter:
    name = "cursor"
    capabilities = AdapterCapabilities(exact_resume=False, cancel=False, structured_output=False, usage_observation=False,
                                       transcript_retention=False, turn_limit=False, funding_route_check=True,
                                       evidence="probe-cursor-report-1.json (2026-10-12): logged out; -p runs unexercised")
    def __init__(self, runs_dir: str, *, cli=None, env=None, clock=None, expected_route: str = "cursor-login")
```
- `cli=None` resolves `shutil.which("cursor-agent")`, else the newest
  `%APPDATA%\Cursor\User\globalStorage\anysphere.cursor-agent-worker\agent-cli\.local\share\cursor-agent\versions\*\cursor-agent.cmd`
  (on POSIX `~/.local/bin/cursor-agent`). `resolve_cli()` is a module function.
- Route check before every launch: `[cli, "status"]`; stdout `Not logged in` (or exit != 0) refuses
  `auth-missing` with the operator step `cursor-agent login`; `CURSOR_API_KEY` or `CURSOR_AUTH_TOKEN` in the env
  is route `api-key` and refuses `funding-route-mismatch` unless expected. Any other stdout is taken as logged
  in with that text recorded (the logged-in text is unobserved).
- `launch`: manifest before spawn; argv `-p --output-format stream-json --trust --workspace <ws> --model <m> -f
  [--resume <id>] <prompt>` from `cwd=<workspace>`, stdin DEVNULL, stdout/stderr files. `LaunchResult(native_session_id=None,
  native_run_handle=run_id)`; the session id is unknown until a `-p` run is observed, so `exact_resume` is False
  and the adapter never passes `--resume` (a request with `resume_session_id` set launches fresh and records
  `resume_unsupported: True` in the manifest and the result).
- `poll`: running while the pid is alive; then: manifest `cancelled_at` -> `cancelled`; stderr matching
  `Authentication required` -> `auth-missing` (observed); stderr/stdout matching the generic quota / rate-limit /
  transient patterns from `claude.py` -> those reasons; exit 0 with any stdout -> `completed` with
  `consumed_units=None` (charged at the reservation: the conservative reading, stated in the result as
  `turns_unobserved: True`) and the whole stdout (first 2000 chars) in `result["result_text"]`; exit != 0
  otherwise -> `task-failed` with identity `cursor:<sha256 of the normalised first 200 chars of stderr>[:12]`.
  When a line of stdout parses as JSON with a `type`, keep the list of types in `result["event_types"]`
  (unparsed: not an error) so the first logged-in run leaves evidence of the real shape.
- `cancel`: record `cancelled_at`, kill the tree (the cmd launcher spawns PowerShell then node: `taskkill /T`).
- `reconcile`: as the Codex lane. `observe_usage`: unavailable.

`tests/fake_cursor_cli.py`: a Python stand-in (`FAKE_CURSOR_SCRIPT`, `FAKE_CURSOR_ARGV_OUT`): keys `status_stdout`
(default `Not logged in`), `status_exit`, `print_stdout`, `print_stderr`, `print_exit`, `sleep_seconds`.
`tests/test_cursor_adapter.py` (>= 10 tests): the argv shape; stdin closed; `CLAUDE_CODE_*` stripped; the
logged-out refusal with the exact observed text and nothing spawned; `CURSOR_API_KEY` -> `funding-route-mismatch`;
a scripted logged-in status then a `-p` run exit 0 -> `completed`, `consumed_units` None, `result_text` kept;
the observed auth error on stderr of a run -> `auth-missing`; exit 1 otherwise -> `task-failed` with a stable
identity; `resume_session_id` ignored and recorded; cancel; reconcile; observe_usage unavailable; the resolver
picks the newest versions dir; a `Controller` tick over the adapter (fake CLI) charges the full reservation.

## Integrator (not a lane): the provider transfer, the CLI, policy, docs

`controller.py`: `transfer_task(task_id, to_provider) -> Dict`; `tick` never resumes a session recorded by a
different provider; `_controller` in `cli.py` registers every `--adapter` named (repeatable: `--adapter claude
--adapter codex`); `task transfer <id> --to <provider>`; `policy.example.json` gets `codex.model`
`gpt-6-astra` (observed) and keeps `cursor` disabled with `composer-standard` marked unverified; README
sections "Codex adapter", "Cursor adapter", "Provider transfer", "Not in PR 5"; CHANGELOG; DocsIndex; the floor.
