"""The Codex CLI adapter: one headless `codex exec --json` per run, every run recorded on disk.

Everything this adapter relies on was observed live on `codex-cli 0.162.0-alpha.17.2` (the Codex desktop
app's binary at `%LOCALAPPDATA%\\OpenAI\\Codex\\bin\\<hash>\\codex.exe`, beside its
`codex-code-mode-host.exe`; Windows, 2026-10-12; probe reports `probe-codex-report-1.json` (p*) and
`probe-codex-report-2.json` (q*), the broken bundled binary in `probe-codex-sandbox-bin-report.json`).
Observed, and therefore declared in `capabilities` or relied on:

  * `codex login status` exits 0 and prints `Logged in using ChatGPT` (p0; the text arrived on STDERR,
    with stdout empty, so the route check reads both streams). `~/.codex/auth.json` has
    `auth_mode: "chatgpt"`;
  * `codex exec --json --skip-git-repo-check -C <cwd> -s workspace-write -c approval_policy="never"
    -c model_reasoning_effort="medium" -c mcp_servers={} -m gpt-6-astra -o <last.txt> <prompt>` prints
    NDJSON on stdout and exits 0 (p3, q1): `thread.started{thread_id}`, `turn.started`, then
    `item.started` / `item.completed` events whose `item` is `{id, type:"agent_message", text}` or
    `{id, type:"command_execution", command, aggregated_output, exit_code, status}`, then
    `turn.completed{usage:{input_tokens, cached_input_tokens, cache_write_input_tokens, output_tokens,
    reasoning_output_tokens}}`. `-o` writes the last agent message to the file (p6);
  * stderr says `Reading additional input from stdin...` when stdin is open: every spawn here passes
    `stdin=subprocess.DEVNULL`;
  * `--ignore-user-config` turns the write into `Rejected(... blocked by policy)` on stderr and the model
    reports a read-only session (recorded in the PR 5 lane contract; p1b shows the flag on a read-only
    run): it is NEVER passed. The operator's `config.toml` stays
    loaded and the command line overrides what matters (`-m`, `-s`, `approval_policy`,
    `model_reasoning_effort`, and `mcp_servers={}` so the operator's MCP servers never enter a run);
  * an unrecognised model (`-m no-such-model-0`, and `sol`) gives `thread.started`, two `item.completed`
    items of type `error` (warnings), `turn.started`, then `{"type":"error","message":<JSON text with
    status 400 and "... model is not supported when using Codex with a ChatGPT account.">}` and
    `turn.failed{error:{message:<same>}}`, no `turn.completed`, exit 1 (p4, p5);
  * `codex exec resume --json --skip-git-repo-check <thread_id> <prompt>` emits the same shapes with the
    SAME `thread_id` and continued the edit (q2); without the flag it exits 1 with no events (p2_resume);
  * `taskkill /T /F` mid-run exits 1 with no `turn.completed`; the session file
    `<CODEX_HOME>/sessions/<YYYY>/<MM>/<DD>/rollout-<timestamp>-<thread_id>.jsonl` (local date) exists and
    `exec resume` of that thread then succeeded (p8, q3);
  * `--output-schema <file>` makes the last agent message the JSON (p7); `--ephemeral` writes no session
    file; `-c model_reasoning_effort="low"` is accepted (p6);
  * the bundled `~/.codex/.sandbox-bin/codex.exe` (0.162.0-alpha.2) has no code-mode host beside it and
    every tool call fails closed (sandbox-bin report p3): `resolve_cli` never picks a `codex.exe` that
    has no `codex-code-mode-host.exe` beside it.

Unverified, and therefore False in `capabilities` or not relied on:

  * a turn limit: no flag exists (`turn_limit=False`), so a run is bounded by the task budget and the
    operator's cancel, never by the CLI;
  * the subscription allowance: nothing is printed, and `codex app-server` answered `initialize` but
    neither `account/rateLimits/read` nor `getAccountRateLimits` replied (q4), so `usage_observation=False`
    and every allowance observation is UNAVAILABLE;
  * the logged-out text of `codex login status`, and the error texts for 401/402/429/5xx: the generic
    status and text rules below are carried over from the Claude adapter, not observed on Codex;
  * whether `exec resume` honours `-m`/`-s`/`-c` overrides: only the minimal resume form was observed, so
    a resume passes exactly that form (the observed resume kept writing in the workspace);
  * a tool-restriction flag: none observed, so a request with `allowed_tools` set is refused rather than
    run unrestricted; `agent` (a Claude agent role) has no Codex equivalent and is recorded as ignored;
  * an `error` event followed by `turn.completed`: unobserved; a completed turn stands (below).

Integrator decisions (no owner row): `consumed_units` is the number of `item.completed` events whose
item type is not `error` (one completed model step is one unit of the `turns` pool; the `error` items are
warnings such as missing model metadata); a cancelled run and a run with no final result report None
(charged at the full reservation), an unrecognised model 0. A turn that completed with an empty last
agent message is `no-final-result`, not `completed`.

Run layout: `<runs_dir>/<run_id>/manifest.json` (written BEFORE the spawn), `stdout.json` (the NDJSON
stream), `stderr.log`, `last_message.txt` (the `-o` file), and a `workspace/` directory unless the request
names one. The run id is the handle.
"""
from __future__ import annotations

import glob
import hashlib
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple, Union

from ..models import (LaunchOutcomeUnknown, LaunchRefused, LaunchRequest, LaunchResult, ReconcileResult, RunState,
                      RunStatus, StopReason, UsageFreshness, UsageKind, UsageObservation)
from ..store import pid_alive
from .base import AdapterCapabilities

LOGGED_IN_CHATGPT = "Logged in using ChatGPT"
CODE_MODE_HOST = "codex-code-mode-host.exe"
LOGIN_STATUS_TIMEOUT_SECONDS = 30
THREAD_WAIT_SECONDS = 10.0
THREAD_HEAD_BYTES = 65536
LAST_MESSAGE_LIMIT = 2000
STDERR_TAIL_LIMIT = 2000
IDENTITY_PREFIX_CHARS = 200
SANDBOXES = ("workspace-write", "read-only")   # the two modes the probes exercised
ROUTES = ("chatgpt", "api-key")

# Observed (p4, p5): status 400 with "The '<model>' model is not supported when using Codex with a ChatGPT
# account." The remaining rules are the Claude adapter's, by status first and then by text: unobserved here.
_MODEL_UNSUPPORTED_RE = re.compile(r"model is not supported", re.IGNORECASE)
_AUTH_RE = re.compile(r"not logged in|unauthori|authenticat", re.IGNORECASE)
_QUOTA_RE = re.compile(r"usage limit|quota|limit reached|resets", re.IGNORECASE)
_RATE_LIMIT_RE = re.compile(r"rate.?limit|too many requests", re.IGNORECASE)
_TRANSIENT_RE = re.compile(r"overloaded|timed out|network|ECONNRESET", re.IGNORECASE)
_EFFORT_RE = re.compile(r"[A-Za-z][A-Za-z-]*")

_WINDOWS = sys.platform.startswith("win")


def _utc_iso(t: float) -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t))


# ----- resolution ------------------------------------------------------------------------------------


def has_code_mode_host(path: str) -> bool:
    """True when `codex-code-mode-host.exe` sits beside `path`. Observed: without it every tool call of
    the 0.162 alpha fails closed and the model reports it could not edit."""
    return os.path.isfile(os.path.join(os.path.dirname(os.path.abspath(path)), CODE_MODE_HOST))


def _usable(path: Optional[str]) -> bool:
    if not path or not os.path.isfile(path):
        return False
    if os.path.basename(path).lower() == "codex.exe":
        return has_code_mode_host(path)
    return True   # a launcher shim (codex.cmd, or a POSIX `codex`) is used as found


def _mtime(path: str) -> float:
    try:
        return os.path.getmtime(path)
    except OSError:
        return 0.0


def resolve_cli(*, which: Callable[[str], Optional[str]] = shutil.which, local_appdata: Optional[str] = None,
                codex_home: Optional[str] = None) -> Optional[str]:
    """The Codex executable to launch, or None. In order: `codex` on PATH; the newest (by the file's
    mtime) `%LOCALAPPDATA%\\OpenAI\\Codex\\bin\\*\\codex.exe` with `codex-code-mode-host.exe` beside it;
    `<codex home>/plugins/.plugin-appserver/codex.exe` on the same condition. A `codex.exe` without the
    host beside it is never returned, wherever it was found: that is the broken `~/.codex/.sandbox-bin`
    binary's shape."""
    found = which("codex")
    if _usable(found):
        return os.path.abspath(found)   # type: ignore[arg-type]
    lad = local_appdata if local_appdata is not None else os.environ.get("LOCALAPPDATA")
    if lad:
        candidates = [p for p in glob.glob(os.path.join(lad, "OpenAI", "Codex", "bin", "*", "codex.exe"))
                      if _usable(p)]
        if candidates:
            return os.path.abspath(max(candidates, key=lambda p: (_mtime(p), p)))
    home = codex_home or os.environ.get("CODEX_HOME") or os.path.join(os.path.expanduser("~"), ".codex")
    plugin = os.path.join(home, "plugins", ".plugin-appserver", "codex.exe")
    if _usable(plugin):
        return os.path.abspath(plugin)
    return None


def locate_session_file(sessions_dir: Optional[str], thread_id: Optional[str], hint: Optional[str] = None
                        ) -> Optional[str]:
    """The session file of `thread_id`: `<sessions_dir>/**/rollout-*-<thread_id>.jsonl`, found by the
    thread id (the date directories are the CLI's local date, so they are not computed)."""
    if hint and os.path.isfile(hint):
        return hint
    if not sessions_dir or not thread_id or not os.path.isdir(sessions_dir):
        return None
    suffix = f"-{thread_id}.jsonl"
    matches: List[str] = []
    for root, _dirs, files in os.walk(sessions_dir):
        for name in files:
            if name.startswith("rollout-") and name.endswith(suffix):
                matches.append(os.path.join(root, name))
    return sorted(matches)[0] if matches else None


# ----- the stream --------------------------------------------------------------------------------------


def _objects(text: str):
    for line in text.splitlines():
        s = line.strip()
        if not s.startswith("{"):
            continue
        try:
            obj = json.loads(s)
        except ValueError:
            continue
        if isinstance(obj, dict):
            yield obj


def parse_stream(text: str) -> Dict[str, Any]:
    """Read the NDJSON of `codex exec --json`. Lines that are not JSON objects are ignored."""
    out: Dict[str, Any] = {"thread_id": None, "events": 0, "turn_completed": False, "usage": None,
                           "turn_failed": None, "errors": [], "items_by_type": {}, "consumed_items": 0,
                           "warnings": [], "last_message": None}
    for obj in _objects(text):
        out["events"] += 1
        kind = obj.get("type")
        if kind == "thread.started":
            if out["thread_id"] is None and isinstance(obj.get("thread_id"), str) and obj["thread_id"]:
                out["thread_id"] = obj["thread_id"]
        elif kind == "turn.completed":
            out["turn_completed"] = True
            out["usage"] = obj.get("usage") if isinstance(obj.get("usage"), dict) else {}
        elif kind == "turn.failed":
            err = obj.get("error")
            msg = err.get("message") if isinstance(err, dict) else err
            out["turn_failed"] = msg if isinstance(msg, str) else json.dumps(msg)
        elif kind == "error":
            msg = obj.get("message")
            out["errors"].append(msg if isinstance(msg, str) else json.dumps(msg))
        elif kind == "item.completed":
            item = obj.get("item") if isinstance(obj.get("item"), dict) else {}
            itype = str(item.get("type") or "unknown")
            out["items_by_type"][itype] = out["items_by_type"].get(itype, 0) + 1
            if itype == "error":
                out["warnings"].append(str(item.get("message") or ""))
            else:
                out["consumed_items"] += 1
            if itype == "agent_message":
                out["last_message"] = item.get("text") if isinstance(item.get("text"), str) else ""
    return out


def _read_thread_id(path: Optional[str]) -> Optional[str]:
    """The `thread.started` id from the head of the stream (it is the first event, observed)."""
    if not path or not os.path.exists(path):
        return None
    try:
        with open(path, "rb") as f:
            head = f.read(THREAD_HEAD_BYTES).decode("utf-8", "replace")
    except OSError:
        return None
    complete = head if head.endswith("\n") else head.rsplit("\n", 1)[0] if "\n" in head else ""
    return parse_stream(complete)["thread_id"]


def error_detail(message: Optional[str]) -> Tuple[Optional[int], str]:
    """(status, text) of an `error` / `turn.failed` message. Observed: the message is itself JSON,
    `{"type":"error","status":400,"error":{"type":"invalid_request_error","message":"..."}}`."""
    if message is None:
        return None, ""
    try:
        obj = json.loads(message)
    except ValueError:
        return None, message
    if not isinstance(obj, dict):
        return None, message
    status: Optional[int] = None
    try:
        status = int(obj["status"]) if obj.get("status") is not None else None
    except (TypeError, ValueError):
        status = None
    err = obj.get("error")
    text = err.get("message") if isinstance(err, dict) else None
    if not isinstance(text, str):
        text = obj.get("message") if isinstance(obj.get("message"), str) else message
    return status, text


def _normalise_for_identity(text: str) -> str:
    return re.sub(r"\s+", " ", text[:IDENTITY_PREFIX_CHARS]).strip().lower()


def failure_identity(text: str) -> str:
    return "codex:" + hashlib.sha256(_normalise_for_identity(text).encode("utf-8")).hexdigest()[:12]


def _is_transient_status(status: Optional[int]) -> bool:
    return status is not None and (status == 408 or 500 <= status <= 599)


# ----- the adapter -------------------------------------------------------------------------------------


class CodexAdapter:
    name = "codex"
    capabilities = AdapterCapabilities(
        exact_resume=True,          # observed: `exec resume <thread_id>` kept the thread id and continued the edit
        cancel=True,                # observed: taskkill /T /F mid-run keeps the session file; resume then works
        structured_output=True,     # observed: --output-schema makes the last agent message the JSON
        usage_observation=False,    # observed: no allowance figure anywhere (exec, app-server)
        transcript_retention=True,  # observed: <CODEX_HOME>/sessions/YYYY/MM/DD/rollout-*-<thread_id>.jsonl
        turn_limit=False,           # observed: no turn-limit flag exists
        funding_route_check=True,   # observed: `codex login status` -> "Logged in using ChatGPT"
        evidence="codex-cli 0.162.0-alpha.17.2: probe-codex-report-1.json, -2.json (2026-10-12)")

    thread_wait_seconds = THREAD_WAIT_SECONDS   # how long launch reads the stream for `thread.started`

    def __init__(self, runs_dir: str, *, cli: Optional[Union[str, Sequence[str]]] = None,
                 env: Optional[Dict[str, str]] = None, clock=None, codex_home: Optional[str] = None,
                 expected_route: str = "chatgpt", sandbox: str = "workspace-write"):
        if sandbox not in SANDBOXES:
            raise ValueError(f"sandbox {sandbox!r} was not observed; use one of {', '.join(SANDBOXES)}")
        if expected_route not in ROUTES:
            raise ValueError(f"expected_route {expected_route!r} is not one of {', '.join(ROUTES)}")
        self.runs_dir = os.path.abspath(runs_dir)
        self._cli = cli
        self._env_source = env
        self.clock = clock or time.time
        self.codex_home = os.path.abspath(codex_home) if codex_home else None
        self.expected_route = expected_route
        self.sandbox = sandbox
        # Popen objects of runs launched by THIS process (polling through them reaps the child).
        self._procs: Dict[str, subprocess.Popen] = {}

    # ----- resolution ------------------------------------------------------------------------------

    def _env(self) -> Dict[str, str]:
        """Strip the nested Claude Code session markers; keep OPENAI_* so the route check can see a key."""
        source = self._env_source if self._env_source is not None else os.environ
        env = {k: v for k, v in dict(source).items() if k != "CLAUDECODE" and not k.startswith("CLAUDE_CODE_")}
        if self.codex_home:
            env["CODEX_HOME"] = self.codex_home
        return env

    def _codex_home(self, env: Dict[str, str]) -> str:
        return self.codex_home or env.get("CODEX_HOME") or os.path.join(os.path.expanduser("~"), ".codex")

    def _cli_prefix(self) -> List[str]:
        """A list is used as given (tests pass an interpreter and a script); a string is the executable
        as given; None resolves through `resolve_cli`."""
        cli = self._cli
        if cli is None:
            cli = resolve_cli(codex_home=self.codex_home)
            if cli is None:
                raise LaunchRefused(StopReason.AUTH_MISSING,
                                    "codex CLI not found: no `codex` on PATH and no Codex app binary with "
                                    f"{CODE_MODE_HOST} beside it; install the Codex app and run `codex login`")
        if not isinstance(cli, str):
            return list(cli)
        return [cli]

    def _run_dir(self, run_id: str) -> str:
        return os.path.join(self.runs_dir, run_id)

    def _manifest_path(self, run_id: str) -> str:
        return os.path.join(self._run_dir(run_id), "manifest.json")

    def _read_manifest(self, run_id: str) -> Optional[Dict[str, Any]]:
        path = self._manifest_path(run_id)
        if not os.path.exists(path):
            return None
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)

    def _write_manifest(self, run_id: str, manifest: Dict[str, Any]) -> None:
        path = self._manifest_path(run_id)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8", newline="\n") as f:
            json.dump(manifest, f, indent=2, sort_keys=True)
            f.write("\n")
        os.replace(tmp, path)

    # ----- funding route -----------------------------------------------------------------------------

    def _check_route(self, prefix: List[str], env: Dict[str, str]) -> str:
        argv = prefix + ["login", "status"]
        try:
            os.makedirs(self.runs_dir, exist_ok=True)
            proc = subprocess.run(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  env=env, cwd=self.runs_dir, timeout=LOGIN_STATUS_TIMEOUT_SECONDS)
        except (OSError, subprocess.SubprocessError) as e:
            raise LaunchRefused(StopReason.AUTH_MISSING, f"`codex login status` did not run: {e}")
        # Observed: the text arrives on stderr with stdout empty; both streams are read.
        text = (proc.stdout.decode("utf-8", "replace") + proc.stderr.decode("utf-8", "replace")).strip()
        if env.get("OPENAI_API_KEY"):
            # A key in the child env is paid API spend, whatever the login says (the conservative reading).
            observed = "api-key"
        elif proc.returncode == 0 and LOGGED_IN_CHATGPT in text:
            observed = "chatgpt"
        else:
            raise LaunchRefused(StopReason.AUTH_MISSING,
                                f"`codex login status` did not report `{LOGGED_IN_CHATGPT}` (exit {proc.returncode}): "
                                f"{text[-STDERR_TAIL_LIMIT:] or '(no output)'}; the logged-out text is unobserved; "
                                f"operator step: `codex login`")
        if observed != self.expected_route:
            raise LaunchRefused(StopReason.FUNDING_ROUTE_MISMATCH,
                                f"{observed} would be billed; policy selected {self.expected_route}")
        return observed

    # ----- launch --------------------------------------------------------------------------------------

    def _argv(self, prefix: List[str], request: LaunchRequest, workspace: str, last_message_path: str) -> List[str]:
        if request.resume_session_id:
            # Observed (q2): exactly this form; overrides on a resume are unobserved and not passed.
            return prefix + ["exec", "resume", "--json", "--skip-git-repo-check", request.resume_session_id,
                             request.prompt]
        return prefix + ["exec", "--json", "--skip-git-repo-check", "-C", workspace, "-s", self.sandbox,
                         "-c", 'approval_policy="never"', "-c", f'model_reasoning_effort="{request.effort}"',
                         "-c", "mcp_servers={}", "-m", request.model, "-o", last_message_path, request.prompt]

    def launch(self, request: LaunchRequest) -> LaunchResult:
        if request.speed != "standard":
            raise LaunchRefused(StopReason.LAUNCH_FAILED,
                                f"speed {request.speed} is not supported by the Codex CLI adapter; only standard")
        if request.allowed_tools is not None:
            raise LaunchRefused(StopReason.LAUNCH_FAILED,
                                "codex exec has no observed tool-restriction flag; a restricted launch is refused "
                                "rather than run with every tool")
        if not _EFFORT_RE.fullmatch(request.effort or ""):
            raise LaunchRefused(StopReason.LAUNCH_FAILED, f"effort {request.effort!r} is not a plain effort name")
        env = self._env()
        prefix = self._cli_prefix()
        route = self._check_route(prefix, env)

        run_id = request.run_id
        run_dir = self._run_dir(run_id)
        workspace = os.path.abspath(request.workspace) if request.workspace else os.path.join(run_dir, "workspace")
        os.makedirs(run_dir, exist_ok=True)
        os.makedirs(workspace, exist_ok=True)
        last_message_path = os.path.join(run_dir, "last_message.txt")
        argv = self._argv(prefix, request, workspace, last_message_path)
        codex_home = self._codex_home(env)
        manifest: Dict[str, Any] = {
            "run_id": run_id, "task_id": request.task_id, "provider": self.name, "thread_id": None,
            "resume_of": request.resume_session_id, "model": request.model, "effort": request.effort,
            "sandbox": self.sandbox, "route": route, "argv": argv, "cwd": workspace,
            "started_at": _utc_iso(self.clock()), "pid": None, "exit": None, "cancelled_at": None,
            "codex_home": codex_home, "sessions_dir": os.path.join(codex_home, "sessions"),
            "transcript_path": None, "stdout": os.path.join(run_dir, "stdout.json"),
            "stderr": os.path.join(run_dir, "stderr.log"),
            "last_message_path": None if request.resume_session_id else last_message_path,
            "agent_ignored": request.agent,
        }
        self._write_manifest(run_id, manifest)

        out_f = open(manifest["stdout"], "wb")
        err_f = open(manifest["stderr"], "wb")
        try:
            kwargs: Dict[str, Any] = {}
            if not _WINDOWS:
                kwargs["start_new_session"] = True   # so cancel can killpg the whole tree
            try:
                proc = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=out_f, stderr=err_f, cwd=workspace,
                                        env=env, **kwargs)
            except (OSError, ValueError) as e:
                raise RuntimeError(f"codex CLI did not start: {e}")
            except Exception as e:  # noqa: BLE001 - anything else leaves the spawn outcome unknown
                raise LaunchOutcomeUnknown(f"spawn outcome unknown after the manifest was written: {e}")
        finally:
            out_f.close()
            err_f.close()
        self._procs[run_id] = proc
        try:
            manifest["pid"] = proc.pid
            self._write_manifest(run_id, manifest)
        except OSError as e:
            raise LaunchOutcomeUnknown(f"codex started (pid {proc.pid}) but the manifest could not be updated: {e}")

        thread_id = self._wait_for_thread(proc, manifest["stdout"])
        if thread_id:
            manifest["thread_id"] = thread_id
            try:
                self._write_manifest(run_id, manifest)
            except OSError:
                pass   # the stream still holds it; poll fills the manifest in
        # Observed: a resume reports the SAME thread id, so the resumed id stands when the stream is slow.
        return LaunchResult(native_session_id=thread_id or request.resume_session_id, native_run_handle=run_id,
                            workspace=workspace)

    def _wait_for_thread(self, proc: subprocess.Popen, stdout_path: str) -> Optional[str]:
        deadline = time.monotonic() + float(self.thread_wait_seconds)
        while True:
            thread_id = _read_thread_id(stdout_path)
            if thread_id:
                return thread_id
            if proc.poll() is not None:
                return _read_thread_id(stdout_path)
            if time.monotonic() >= deadline:
                return None
            time.sleep(0.05)

    # ----- poll ----------------------------------------------------------------------------------------

    def _alive(self, run_id: str, manifest: Dict[str, Any]) -> bool:
        proc = self._procs.get(run_id)
        if proc is not None:
            rc = proc.poll()
            if rc is None:
                return True
            if manifest.get("exit") is None:
                manifest["exit"] = rc
                self._write_manifest(run_id, manifest)
            return False
        pid = manifest.get("pid")
        if pid is None:
            return False
        return pid_alive(int(pid))   # after a restart: an existence check only; the exit code stays unknown

    def _read_text(self, path: Optional[str]) -> str:
        if not path or not os.path.exists(path):
            return ""
        with open(path, "rb") as f:
            return f.read().decode("utf-8", "replace")

    def _allowance_observation(self, thread_id: Optional[str], usage: Optional[Dict[str, Any]] = None
                               ) -> UsageObservation:
        """UNAVAILABLE always: no allowance figure was observed anywhere. The token usage rides along in
        `raw` as a record, never as a figure that is summed."""
        return UsageObservation(provider=self.name, kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="turns",
                                freshness=UsageFreshness.UNAVAILABLE, native_session_id=thread_id,
                                observed_at=_utc_iso(self.clock()), raw={"tokens": dict(usage or {})})

    def poll(self, handle: str) -> RunStatus:
        run_id = handle
        manifest = self._read_manifest(run_id)
        if manifest is None:
            return RunStatus(state=RunState.FAILED, stop_reason=StopReason.NO_FINAL_RESULT,
                             result={"error": f"no manifest for run {run_id} under {self.runs_dir}"})
        if self._alive(run_id, manifest):
            if manifest.get("thread_id") is None:
                thread_id = _read_thread_id(manifest.get("stdout"))
                if thread_id:
                    manifest["thread_id"] = thread_id
                    self._write_manifest(run_id, manifest)
            return RunStatus(state=RunState.RUNNING)
        return self._classify(manifest)

    def _classify(self, manifest: Dict[str, Any]) -> RunStatus:
        """Classify an exited run from the manifest and its NDJSON stdout, in the order of the module doc."""
        run_id = manifest["run_id"]
        stdout = self._read_text(manifest.get("stdout"))
        stderr = self._read_text(manifest.get("stderr"))
        stream = parse_stream(stdout)
        thread_id = manifest.get("thread_id") or stream["thread_id"] or manifest.get("resume_of")
        if manifest.get("thread_id") is None and stream["thread_id"]:
            manifest["thread_id"] = stream["thread_id"]
            self._write_manifest(run_id, manifest)
        transcript = locate_session_file(manifest.get("sessions_dir"), thread_id, manifest.get("transcript_path"))
        if transcript and manifest.get("transcript_path") != transcript:
            manifest["transcript_path"] = transcript
            self._write_manifest(run_id, manifest)
        last_message = stream["last_message"]
        base: Dict[str, Any] = {
            "run_id": run_id, "workspace": manifest.get("cwd"), "thread_id": thread_id,
            "last_message": (last_message or "")[:LAST_MESSAGE_LIMIT], "items_by_type": stream["items_by_type"],
            "usage": stream["usage"], "exit": manifest.get("exit"), "stream_events": stream["events"],
            "last_message_path": manifest.get("last_message_path"), "transcript": transcript,
            "warnings": stream["warnings"],
        }
        observations = [self._allowance_observation(thread_id, stream["usage"])]
        counted = float(stream["consumed_items"])

        def status(state: str, reason: str, consumed: Optional[float], extra: Optional[Dict[str, Any]] = None,
                   identity: Optional[str] = None) -> RunStatus:
            return RunStatus(state=state, stop_reason=reason, consumed_units=consumed, failure_identity=identity,
                             result=dict(base, **(extra or {})), usage=None, observations=observations,
                             transcript_path=transcript)

        # 1. Cancelled: recorded before the kill. Observed: the session file survives and resumes.
        if manifest.get("cancelled_at"):
            return status(RunState.CANCELLED, StopReason.CANCELLED, None,
                          {"cancelled_at": manifest["cancelled_at"], "resume_session_id": thread_id})
        # The failure message: the turn's own, else (only when the turn did not complete) the last
        # top-level `error` event. An `error` followed by `turn.completed` is unobserved; the turn stands.
        message = stream["turn_failed"]
        if message is None and not stream["turn_completed"] and stream["errors"]:
            message = stream["errors"][-1]
        if message is not None:
            code, text = error_detail(message)
            failure = {"error_status": code, "error_message": text[:LAST_MESSAGE_LIMIT],
                       "blocker": text[:LAST_MESSAGE_LIMIT], "stderr_tail": stderr[-STDERR_TAIL_LIMIT:]}
            # 2. Unrecognised model (observed): status 400 with "model is not supported"; nothing consumed.
            if code == 400 and _MODEL_UNSUPPORTED_RE.search(text):
                return status(RunState.FAILED, StopReason.MODEL_UNRECOGNIZED, 0.0,
                              dict(failure, model=manifest.get("model")))
            # 3. API-level errors by status, then by text (carried over from the Claude adapter).
            if code == 401 or _AUTH_RE.search(text):
                return status(RunState.FAILED, StopReason.AUTH_MISSING, counted, failure)
            if code == 402 or _QUOTA_RE.search(text):
                return status(RunState.FAILED, StopReason.QUOTA_EXHAUSTED, counted, failure)
            if code == 429 or _RATE_LIMIT_RE.search(text):
                return status(RunState.FAILED, StopReason.RATE_LIMITED, counted, failure)
            if _is_transient_status(code) or _TRANSIENT_RE.search(text):
                return status(RunState.FAILED, StopReason.TRANSIENT, counted, failure)
            # 5b. Any other failure the CLI reported: diagnosed, with a stable identity.
            return status(RunState.FAILED, StopReason.TASK_FAILED, counted, failure, identity=failure_identity(text))
        # 4. No turn.completed and no error: the worker died without saying why (consumption unknown).
        if not stream["turn_completed"]:
            return status(RunState.FAILED, StopReason.NO_FINAL_RESULT, None, {"stderr_tail": stderr[-STDERR_TAIL_LIMIT:]})
        # 5. Completed, when the turn produced a final agent message.
        if last_message and last_message.strip():
            return status(RunState.FINISHED, StopReason.COMPLETED, counted)
        return status(RunState.FAILED, StopReason.NO_FINAL_RESULT, counted,
                      {"empty_last_message": True, "stderr_tail": stderr[-STDERR_TAIL_LIMIT:]})

    # ----- cancel --------------------------------------------------------------------------------------

    def _kill_tree(self, pid: int) -> None:
        if _WINDOWS:
            try:
                subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)], stdin=subprocess.DEVNULL,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
            except (OSError, subprocess.SubprocessError):
                pass   # a missing process (or a missing taskkill) is not an error
        else:
            try:
                os.killpg(pid, signal.SIGTERM)
            except (ProcessLookupError, PermissionError, OSError):
                pass

    def cancel(self, handle: str) -> None:
        """Record the cancellation FIRST, then terminate the process tree. The workspace and the session
        file are never touched: they are the evidence an operator inspects, and the thread resumes."""
        run_id = handle
        manifest = self._read_manifest(run_id)
        if manifest is None:
            return
        if not manifest.get("cancelled_at"):
            manifest["cancelled_at"] = _utc_iso(self.clock())
            self._write_manifest(run_id, manifest)
        pid = manifest.get("pid")
        if pid is None:
            return
        self._kill_tree(int(pid))
        proc = self._procs.get(run_id)
        if proc is not None:
            try:
                proc.wait(timeout=10)
            except subprocess.SubprocessError:
                pass

    # ----- reconcile and usage -----------------------------------------------------------------------

    def reconcile(self, run_id: str, handle: Optional[str]) -> ReconcileResult:
        key = handle or run_id
        if self._read_manifest(key) is None:
            return ReconcileResult(found=False)
        status = self.poll(key)
        manifest = self._read_manifest(key) or {}
        thread_id = manifest.get("thread_id") or (status.result or {}).get("thread_id") or manifest.get("resume_of")
        return ReconcileResult(found=True, status=status, native_session_id=thread_id, native_run_handle=key)

    def observe_usage(self) -> UsageObservation:
        return self._allowance_observation(None)
