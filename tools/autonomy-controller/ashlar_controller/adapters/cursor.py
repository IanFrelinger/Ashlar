"""The Cursor agent CLI adapter: one headless `cursor-agent -p` per run, every run recorded on disk.

WHAT WAS OBSERVED. The CLI on the probe host is NOT logged in, so no `-p` run has produced any output.
Report `probe-cursor-report-1.json` (script `probe_cursor_runtime.py`, 2026-10-12), against the Cursor
IDE's embedded agent CLI `2026.10.01-e373342`
(`%APPDATA%\\Cursor\\User\\globalStorage\\anysphere.cursor-agent-worker\\agent-cli\\.local\\share\\cursor-agent\\versions\\<v>\\cursor-agent.cmd`),
recorded only this:

  * c0_version: `cursor-agent --version` prints `2026.10.01-e373342` (exit 0);
  * c0_status: `cursor-agent status` prints `Not logged in` (exit 0). This is the funding-route check;
  * c0_about: `cursor-agent about` prints `About Cursor CLI` with `Model Auto`, `Subscription Tier
    Unknown` and `User Email Not logged in`;
  * c0_models: logged out, `cursor-agent models` exits 1 with stderr `Error: Authentication required.
    Run 'agent login', pass --api-key/--auth-token, or set CURSOR_API_KEY/CURSOR_AUTH_TOKEN.`;
  * c1_print_json, c1b_print_stream_json, c3_edit_force, c4_unrecognised_model,
    c5_model_composer_standard, c6_cancel: logged out, every `-p` run exits 1 within about a second,
    with EMPTY stdout and the stderr `Error: Authentication required. Please run 'agent login' first, or
    set CURSOR_API_KEY environment variable.`. So no model id was checked (`composer-standard` and
    `no-such-model-0` failed the same way), and c6 had nothing to cancel.

UNVERIFIED, and therefore False in `capabilities`:
  * exact_resume: the stream's session or chat id was never seen, so `--resume` is NEVER passed. A
    request carrying `resume_session_id` launches fresh and records `resume_unsupported: true`;
  * cancel: no run lived long enough to be cancelled. `cancel` still records first and kills the tree;
  * structured_output: no schema flag in `--help`;
  * usage_observation: no turn count, cost or allowance figure was ever printed. `consumed_units` is
    always None, so the controller charges the FULL reservation (`turns_unobserved: true`), and
    `observe_usage` is an `unavailable` observation;
  * transcript_retention: where (and whether) the CLI keeps a chat was never seen;
  * turn_limit: `--help` lists no turn-limit flag, so the invocation ceiling is not enforced by the CLI.
  Also unseen: the logged-in `status` text (any other non-empty text, at exit 0, counts as logged in and
  is recorded in the manifest), the shape of `--output-format stream-json`, a successful run's exit code,
  and what `--trust`, `--workspace` and `-f` do.

So that the first logged-in run leaves evidence of the real shape, every settled run keeps the first
2000 characters of stdout (`result_text`), the `type` of every JSON line (`event_types`), any
`session_id`/`sessionId`/`chat_id`/`chatId` value seen (`session_id_candidates`) and the stderr tail.

INTEGRATOR DECISIONS (no owner row; listed for the next handoff):
  * An exit 0 with non-empty stdout is `completed` BEFORE the quota, rate-limit and transient texts are
    looked for. stdout carries the model's own text and its tool output, so a word like "network" or
    "timed out" in a finished run must not turn it into a retry. Those texts classify only a run that
    did not exit 0.
  * The launcher. `cursor-agent.cmd` runs `powershell -File cursor-agent.ps1 %*` under `setlocal
    enabledelayedexpansion`, and the script then runs `node.exe index.js $args` (read from the installed
    files, not probed). Through cmd.exe a prompt loses everything after its first line break, and `%`,
    `!`, `"`, `&`, `|`, `<`, `>` and `^` are rewritten or executed. So, as `claude.py` launches
    `claude.exe` rather than the npm shim, a `.cmd` with `node.exe` and `index.js` beside it (or in its
    newest `versions/` directory) is launched as `node.exe index.js` with the variables the launcher
    sets (`CURSOR_INVOKED_AS`, `NODE_COMPILE_CACHE`). A `.cmd` with neither is refused when an argument
    holds one of those characters. A list `cli` is used as given.
  * Refused before anything starts: a speed other than `standard` (no speed flag in `--help`), and a
    restricted `allowed_tools` (no tool-restriction flag in `--help`, and every run passes `-f`, so the
    restriction would silently widen). `effort`, `invocation_ceiling` and `agent` have no flag. They are
    recorded in the manifest under `ignored`.
  * The exit recorder. The classification rests on the exit code, and only the process that started the
    CLI can read it, while every `tick` is its own process: the tick that launches a run returns at once
    and a later tick settles it. So the CLI runs under `_exit_recorder.py`, which waits for it and writes
    its exit code to `exit_code` in the run directory. The manifest's `pid` is the recorder's, so the tree
    kill of `cancel` still takes the CLI. Once that file exists the run has finished, whatever the pid now
    names: a poll classifies it, and `cancel` neither records a cancellation nor kills anything.

Run layout, as `claude.py`: `<runs_dir>/<run_id>/manifest.json` (written BEFORE the spawn),
`stdout.json` (the stream), `stderr.log`, `exit_code` (written by the recorder when the CLI exits), and a
`workspace/` directory unless the request names one. The run id is the handle.
"""
from __future__ import annotations

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
# The generic provider texts are shared with the Claude adapter on purpose: one definition of
# "usage limit", "rate limit" and "network" for every provider.
from .claude import _QUOTA_RE, _RATE_LIMIT_RE, _TRANSIENT_RE

STATUS_TIMEOUT_SECONDS = 30
RESULT_TEXT_LIMIT = 2000
STDERR_TAIL_LIMIT = 2000
IDENTITY_PREFIX_CHARS = 200
EVENT_TYPES_LIMIT = 1000
USAGE_UNIT = "requests"          # the unit of the `cursor` pool in policy.example.json
EXIT_FILE = "exit_code"          # in the run directory, written by the exit recorder when the CLI exits
EXIT_RECORDER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_exit_recorder.py")

# Observed (c0_status): logged out, `cursor-agent status` prints exactly `Not logged in`, exit 0.
_NOT_LOGGED_IN_RE = re.compile(r"not logged in", re.IGNORECASE)
# Observed (c1..c6): the stderr of every logged-out `-p` run.
_AUTH_REQUIRED_RE = re.compile(r"authentication required", re.IGNORECASE)
# Observed (c0_models): the variables the CLI names as alternatives to a login. Either one bills a key.
API_KEY_VARS = ("CURSOR_API_KEY", "CURSOR_AUTH_TOKEN")
_SESSION_KEYS = ("session_id", "sessionId", "chat_id", "chatId")
# Characters that cmd.exe (with delayed expansion) or the PowerShell hop rewrites, executes or cuts at.
_CMD_UNSAFE = frozenset('\r\n"%!^&|<>')
# The launcher's own rule for a version directory (cursor-agent.ps1): YYYY.MM.DD[-HH-MM-SS]-<commit>.
_VERSION_DIR_RE = re.compile(r"^(\d{4})\.(\d{1,2})\.(\d{1,2})(?:-(\d{2})-(\d{2})-(\d{2}))?-[a-f0-9]+$", re.IGNORECASE)
CURSOR_AGENT_VERSIONS = ("Cursor", "User", "globalStorage", "anysphere.cursor-agent-worker", "agent-cli", ".local",
                         "share", "cursor-agent", "versions")

_WINDOWS = sys.platform.startswith("win")


def _utc_iso(t: float) -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t))


def _clean_env(env: Dict[str, str]) -> Dict[str, str]:
    """Strip the nested-session markers; keep CURSOR_* so the funding-route check can see a key."""
    return {k: v for k, v in env.items() if k != "CLAUDECODE" and not k.startswith("CLAUDE_CODE_")}


def _version_key(name: str) -> Optional[Tuple[int, ...]]:
    m = _VERSION_DIR_RE.match(name)
    if m is None:
        return None
    return tuple(int(g) if g is not None else -1 for g in m.groups())


def newest_version_dir(versions_dir: str, required: Sequence[str]) -> Optional[str]:
    """The newest `<versions_dir>/<YYYY.MM.DD[-HH-MM-SS]-commit>` directory that holds every file in
    `required`, or None. Newest by the date and build time in the name, read as numbers (as the
    launcher's `Parse-VersionString` does, so `2026.10.01` is newer than `2026.9.30`), then by the
    directory's modification time: two builds of one day share a date (`2026.10.01-14929f9` and
    `2026.10.01-e373342` on the probe host)."""
    try:
        names = os.listdir(versions_dir)
    except OSError:
        return None
    best: Optional[str] = None
    best_key: Optional[Tuple[float, ...]] = None
    for name in names:
        version = _version_key(name)
        path = os.path.join(versions_dir, name)
        if version is None or not os.path.isdir(path):
            continue
        if not all(os.path.isfile(os.path.join(path, f)) for f in required):
            continue
        try:
            key = version + (os.path.getmtime(path),)
        except OSError:
            continue
        if best_key is None or key > best_key:
            best, best_key = path, key
    return best


def resolve_cli(*, which: Optional[Callable[[str], Optional[str]]] = None, appdata: Optional[str] = None,
                home: Optional[str] = None, windows: Optional[bool] = None) -> Optional[str]:
    """Where `cursor-agent` is, or None: `cursor-agent` on PATH; else, on Windows, the newest
    `%APPDATA%\\Cursor\\User\\globalStorage\\anysphere.cursor-agent-worker\\agent-cli\\.local\\share\\cursor-agent\\versions\\*\\cursor-agent.cmd`
    (the Cursor IDE's embedded CLI, the one probed); on POSIX `~/.local/bin/cursor-agent`. The
    keyword arguments exist for tests."""
    found = (which or shutil.which)("cursor-agent")
    if found:
        return found
    if _WINDOWS if windows is None else windows:
        root = appdata if appdata is not None else os.environ.get("APPDATA")
        if not root:
            return None
        best = newest_version_dir(os.path.join(root, *CURSOR_AGENT_VERSIONS), ("cursor-agent.cmd",))
        return os.path.join(best, "cursor-agent.cmd") if best else None
    path = os.path.join(home if home is not None else os.path.expanduser("~"), ".local", "bin", "cursor-agent")
    return path if os.path.isfile(path) else None


def node_direct(cli: str) -> Optional[List[str]]:
    """`[node.exe, index.js]` for a `cursor-agent.cmd` launcher, or None. The launcher script runs the
    `node.exe` and `index.js` beside it when they are there, and otherwise those of its newest
    `versions/` directory; this picks the same pair, so the prompt never passes through cmd.exe."""
    if not cli.lower().endswith((".cmd", ".bat")):
        return None
    here = os.path.dirname(os.path.abspath(cli))
    pair = ("node.exe", "index.js")
    if all(os.path.isfile(os.path.join(here, f)) for f in pair):
        return [os.path.join(here, f) for f in pair]
    best = newest_version_dir(os.path.join(here, "versions"), pair)
    return [os.path.join(best, f) for f in pair] if best else None


def parse_stream(text: str) -> Dict[str, Any]:
    """What the stdout of a run shows, whatever its shape: the `type` of every JSON-object line, in
    order (capped at EVENT_TYPES_LIMIT), and every session or chat id seen. Lines that are not JSON
    objects are counted, never an error: the stream-json shape is unobserved."""
    out: Dict[str, Any] = {"event_types": [], "events": 0, "unparsed_lines": 0, "session_id_candidates": [],
                           "event_types_truncated": False}
    for line in text.splitlines():
        s = line.strip()
        if not s:
            continue
        try:
            obj = json.loads(s)
        except ValueError:
            obj = None
        if not isinstance(obj, dict):
            out["unparsed_lines"] += 1
            continue
        out["events"] += 1
        kind = obj.get("type")
        if isinstance(kind, str):
            if len(out["event_types"]) < EVENT_TYPES_LIMIT:
                out["event_types"].append(kind)
            else:
                out["event_types_truncated"] = True
        for key in _SESSION_KEYS:
            value = obj.get(key)
            if isinstance(value, str) and value and value not in out["session_id_candidates"]:
                out["session_id_candidates"].append(value)
    return out


def _normalise_for_identity(text: str) -> str:
    return re.sub(r"\s+", " ", text[:IDENTITY_PREFIX_CHARS]).strip().lower()


def failure_identity(text: str) -> str:
    return "cursor:" + hashlib.sha256(_normalise_for_identity(text).encode("utf-8")).hexdigest()[:12]


def _line_at(text: str, match) -> str:
    """The whole line of `text` that holds `match`, stripped."""
    start = text.rfind("\n", 0, match.start()) + 1
    end = text.find("\n", match.end())
    return text[start:end if end >= 0 else len(text)].strip()


def kill_tree(pid: int) -> None:
    """Terminate `pid` and every process it started: the exit recorder starts the CLI, a `.cmd` launcher
    starts PowerShell, which starts node, and node (launched directly or not) starts its tools.
    `taskkill /T` on Windows; on POSIX the run is its own session, so `killpg`. A process that is already
    gone is not an error."""
    if _WINDOWS:
        try:
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], stdin=subprocess.DEVNULL,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
        except (OSError, subprocess.SubprocessError):
            pass
    else:
        try:
            os.killpg(pid, signal.SIGTERM)
        except (ProcessLookupError, PermissionError, OSError):
            pass


class CursorAdapter:
    name = "cursor"
    capabilities = AdapterCapabilities(
        exact_resume=False,          # unobserved: no session or chat id was ever printed (logged out)
        cancel=False,                # unobserved: every probe run exited at once with the auth error
        structured_output=False,     # no schema flag in --help
        usage_observation=False,     # no turns, cost or allowance was ever printed
        transcript_retention=False,  # unobserved
        turn_limit=False,            # no turn-limit flag in --help
        funding_route_check=True,    # observed (c0_status): `cursor-agent status` prints `Not logged in`
        evidence="probe-cursor-report-1.json (2026-10-12): logged out; -p runs unexercised")

    def __init__(self, runs_dir: str, *, cli: Optional[Union[str, Sequence[str]]] = None,
                 env: Optional[Dict[str, str]] = None, clock=None, expected_route: str = "cursor-login"):
        self.runs_dir = os.path.abspath(runs_dir)
        self._cli = cli
        self._env_source = env
        self.clock = clock or time.time
        self.expected_route = expected_route
        # Popen objects (of the exit recorder) of runs launched by THIS process. Any other process, which
        # under the CLI is every later `tick`, has the manifest's pid and the recorder's exit file.
        self._procs: Dict[str, subprocess.Popen] = {}

    # ----- resolution --------------------------------------------------------------------------

    def _launcher(self) -> Tuple[List[str], str, Dict[str, str]]:
        """(argv prefix, launcher kind, variables the launcher would have set)."""
        cli = self._cli if self._cli is not None else resolve_cli()
        if cli is None:
            raise LaunchRefused(StopReason.AUTH_MISSING,
                                "cursor-agent CLI not found (not on PATH, no cursor-agent.cmd under the Cursor IDE's "
                                "agent-cli versions directory); install it and run `cursor-agent login`")
        if not isinstance(cli, str):
            return list(cli), "as-given", {}
        direct = node_direct(cli)
        if direct is not None:
            extra = {"CURSOR_INVOKED_AS": os.path.basename(cli)}
            local = (self._env_source if self._env_source is not None else os.environ).get("LOCALAPPDATA")
            if local:
                extra["NODE_COMPILE_CACHE"] = os.path.join(local, "cursor-compile-cache")
            return direct, "node-direct", extra
        return [cli], ("cmd-shim" if cli.lower().endswith((".cmd", ".bat")) else "executable"), {}

    def _env(self, extra: Dict[str, str]) -> Dict[str, str]:
        source = self._env_source if self._env_source is not None else os.environ
        env = _clean_env(dict(source))
        for key, value in extra.items():
            if key == "NODE_COMPILE_CACHE":
                env.setdefault(key, value)   # the launcher script keeps one that is already set
            else:
                env[key] = value             # the .cmd always sets CURSOR_INVOKED_AS
        return env

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

    # ----- funding route -----------------------------------------------------------------------

    def _check_route(self, prefix: List[str], env: Dict[str, str]) -> Dict[str, Any]:
        """`cursor-agent status`, then the route. A key in the child env bills the key whatever the
        login says (c0_models names both variables as the alternative to a login). Logged out
        (observed: `Not logged in`), a non-zero exit, or no text at all refuses `auth-missing`; any
        other text is taken as logged in and recorded, because the logged-in text is unobserved."""
        argv = prefix + ["status"]
        try:
            os.makedirs(self.runs_dir, exist_ok=True)
            proc = subprocess.run(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  env=env, cwd=self.runs_dir, timeout=STATUS_TIMEOUT_SECONDS)
        except (OSError, subprocess.SubprocessError) as e:
            raise LaunchRefused(StopReason.AUTH_MISSING,
                                f"`cursor-agent status` did not run: {e}; operator step: install the CLI and run "
                                "`cursor-agent login`")
        text = proc.stdout.decode("utf-8", "replace").strip()
        route = "api-key" if any(env.get(k) for k in API_KEY_VARS) else "cursor-login"
        if route != self.expected_route:
            raise LaunchRefused(StopReason.FUNDING_ROUTE_MISMATCH,
                                f"{route} would be billed; policy selected {self.expected_route}")
        if proc.returncode != 0 or not text or _NOT_LOGGED_IN_RE.search(text):
            shown = text or proc.stderr.decode("utf-8", "replace").strip() or "(no output)"
            raise LaunchRefused(StopReason.AUTH_MISSING,
                                f"cursor-agent is not logged in (`cursor-agent status` exit {proc.returncode}: "
                                f"{shown[:500]}); operator step: `cursor-agent login`")
        return {"route": route, "status_text": text[:500], "status_exit": proc.returncode}

    # ----- launch ------------------------------------------------------------------------------

    @staticmethod
    def _argv(prefix: List[str], request: LaunchRequest, workspace: str) -> List[str]:
        # From --help (unexercised): -p print mode, stream-json output, --trust the workspace without a
        # prompt, -f run commands unless denied. Never --resume: no session id was ever observed.
        return prefix + ["-p", "--output-format", "stream-json", "--trust", "--workspace", workspace,
                         "--model", request.model, "-f", request.prompt]

    def launch(self, request: LaunchRequest) -> LaunchResult:
        if request.speed != "standard":
            raise LaunchRefused(StopReason.LAUNCH_FAILED,
                                f"speed {request.speed} is not supported by cursor-agent (no speed flag); only standard")
        if request.allowed_tools is not None:
            raise LaunchRefused(StopReason.LAUNCH_FAILED,
                                "cursor-agent has no tool-restriction flag and every run passes -f; a restricted "
                                "tool list is refused rather than silently widened")
        prefix, launcher, extra = self._launcher()
        env = self._env(extra)
        run_id = request.run_id
        run_dir = self._run_dir(run_id)
        workspace = os.path.abspath(request.workspace) if request.workspace else os.path.join(run_dir, "workspace")
        argv = self._argv(prefix, request, workspace)
        if launcher == "cmd-shim":
            unsafe = sorted({c for a in argv[1:] for c in a if c in _CMD_UNSAFE})
            if unsafe:
                raise LaunchRefused(StopReason.LAUNCH_FAILED,
                                    f"{prefix[0]} would pass the arguments through cmd.exe and PowerShell, which "
                                    f"rewrite {json.dumps(''.join(unsafe))}, and no node.exe/index.js was found beside "
                                    "it to launch directly")
        route = self._check_route(prefix, env)

        os.makedirs(run_dir, exist_ok=True)
        os.makedirs(workspace, exist_ok=True)
        manifest: Dict[str, Any] = {
            "run_id": run_id, "task_id": request.task_id, "provider": self.name, "session_id": None,
            "resume_of": request.resume_session_id, "resume_unsupported": bool(request.resume_session_id),
            "model": request.model, "argv": argv, "cwd": workspace, "launcher": launcher,
            "route": route["route"], "status_text": route["status_text"],
            "ignored": {"effort": request.effort, "invocation_ceiling": request.invocation_ceiling,
                        "agent": request.agent},
            "started_at": _utc_iso(self.clock()), "pid": None, "exit": None, "cancelled_at": None,
            "stdout": os.path.join(run_dir, "stdout.json"), "stderr": os.path.join(run_dir, "stderr.log"),
            # `argv` is the CLI's; `pid` is the recorder's, which runs it and writes its exit code here.
            "exit_file": os.path.join(run_dir, EXIT_FILE), "exit_recorder": [sys.executable, "-I", EXIT_RECORDER],
        }
        self._write_manifest(run_id, manifest)

        out_f = open(manifest["stdout"], "wb")
        err_f = open(manifest["stderr"], "wb")
        try:
            kwargs: Dict[str, Any] = {}
            if not _WINDOWS:
                kwargs["start_new_session"] = True   # so cancel can killpg the whole tree
            try:
                # The launching `tick` exits at once; a later `tick` (another process) can read the exit
                # code only from the file the recorder writes.
                proc = subprocess.Popen(manifest["exit_recorder"] + [manifest["exit_file"], "--"] + argv,
                                        stdin=subprocess.DEVNULL, stdout=out_f, stderr=err_f, cwd=workspace,
                                        env=env, **kwargs)
            except (OSError, ValueError) as e:
                # Popen reports a failed exec only after it has reaped the child: nothing is running.
                raise RuntimeError(f"cursor-agent did not start: {e}")
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
            raise LaunchOutcomeUnknown(f"cursor-agent started (pid {proc.pid}) but the manifest could not be updated: {e}")
        return LaunchResult(native_session_id=None, native_run_handle=run_id, workspace=workspace)

    # ----- poll --------------------------------------------------------------------------------

    @staticmethod
    def _recorded_exit(manifest: Dict[str, Any]) -> Optional[int]:
        """The exit code the recorder wrote when the CLI exited, or None (still running, killed, or a run
        launched before the recorder existed)."""
        path = manifest.get("exit_file")
        if not path:
            return None
        try:
            with open(path, "r", encoding="ascii") as f:
                return int(f.read().strip())
        except (OSError, ValueError):
            return None

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
        # Another process launched the run (every `tick` is its own process). The exit file is read BEFORE
        # the pid: once the CLI has exited the run is over, and a live pid may already be someone else's.
        rc = self._recorded_exit(manifest)
        if rc is not None:
            if manifest.get("exit") is None:
                manifest["exit"] = rc
                self._write_manifest(run_id, manifest)
            return False
        pid = manifest.get("pid")
        if pid is None:
            return False
        return pid_alive(int(pid))

    def _allowance_observation(self, key: Optional[str]) -> UsageObservation:
        return UsageObservation(provider=self.name, kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit=USAGE_UNIT,
                                freshness=UsageFreshness.UNAVAILABLE, native_session_id=key,
                                observed_at=_utc_iso(self.clock()),
                                raw={"why": "cursor-agent printed no allowance, turn or cost figure in any probe"})

    @staticmethod
    def _read_text(path: Optional[str]) -> str:
        if not path or not os.path.exists(path):
            return ""
        with open(path, "rb") as f:
            return f.read().decode("utf-8", "replace")

    def poll(self, handle: str) -> RunStatus:
        run_id = handle
        manifest = self._read_manifest(run_id)
        if manifest is None:
            return RunStatus(state=RunState.FAILED, stop_reason=StopReason.NO_FINAL_RESULT,
                             result={"error": f"no manifest for run {run_id} under {self.runs_dir}"})
        if self._alive(run_id, manifest):
            return RunStatus(state=RunState.RUNNING)
        return self._classify(manifest)

    def _classify(self, manifest: Dict[str, Any]) -> RunStatus:
        run_id = manifest["run_id"]
        rc = manifest.get("exit")
        stdout = self._read_text(manifest.get("stdout"))
        stderr = self._read_text(manifest.get("stderr"))
        stream = parse_stream(stdout)
        base: Dict[str, Any] = {
            "workspace": manifest.get("cwd"), "transcript": None, "run_id": run_id, "exit": rc,
            "model": manifest.get("model"), "launcher": manifest.get("launcher"),
            "result_text": stdout[:RESULT_TEXT_LIMIT], "stderr_tail": stderr[-STDERR_TAIL_LIMIT:],
            "event_types": stream["event_types"], "event_types_truncated": stream["event_types_truncated"],
            "stream_events": stream["events"], "unparsed_lines": stream["unparsed_lines"],
            "session_id_candidates": stream["session_id_candidates"],
            # No turn count was ever observed: consumed_units is None and the controller charges the
            # full reservation, the conservative reading.
            "turns_unobserved": True,
            "resume_unsupported": bool(manifest.get("resume_unsupported")), "resume_of": manifest.get("resume_of"),
        }
        common: Dict[str, Any] = {"consumed_units": None, "usage": None, "transcript_path": None,
                                  "observations": [self._allowance_observation(run_id)]}

        def failure(reason: str, extra: Optional[Dict[str, Any]] = None, identity: Optional[str] = None) -> RunStatus:
            return RunStatus(state=RunState.FAILED, stop_reason=reason, result=dict(base, **(extra or {})),
                             failure_identity=identity, **common)

        # 1. Cancelled (poll answers RUNNING while the process lives, so this run is gone).
        if manifest.get("cancelled_at"):
            return RunStatus(state=RunState.CANCELLED, stop_reason=StopReason.CANCELLED,
                             result=dict(base, cancelled_at=manifest["cancelled_at"]), **common)
        # 2. The observed authentication error (c1..c6).
        m = _AUTH_REQUIRED_RE.search(stderr)
        if m:
            return failure(StopReason.AUTH_MISSING,
                           {"blocker": f"{_line_at(stderr, m)}; operator step: `cursor-agent login`"})
        # 3. A clean exit is the CLI's own success signal (integrator decision, module doc).
        if rc == 0:
            if stdout.strip():
                return RunStatus(state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, result=base, **common)
            return failure(StopReason.NO_FINAL_RESULT, {"blocker": "cursor-agent exited 0 and printed nothing"})
        # 4. A failed exit: the generic provider texts, stderr first.
        text = stderr + "\n" + stdout
        for pattern, reason in ((_QUOTA_RE, StopReason.QUOTA_EXHAUSTED), (_RATE_LIMIT_RE, StopReason.RATE_LIMITED),
                                (_TRANSIENT_RE, StopReason.TRANSIENT)):
            m = pattern.search(text)
            if m:
                return failure(reason, {"blocker": _line_at(text, m)[:RESULT_TEXT_LIMIT]})
        # 5. The exit code is unknown: the recorder was killed before it wrote one, or the run was launched
        #    before the recorder existed.
        if rc is None:
            return failure(StopReason.NO_FINAL_RESULT,
                           {"blocker": "the exit code of the run was not recorded; stdout and stderr are kept"})
        # 6. Anything else: a diagnosed failure with a stable identity from stderr (stdout's tail when
        #    stderr is empty).
        return failure(StopReason.TASK_FAILED,
                       identity=failure_identity(stderr if stderr.strip() else stdout[-IDENTITY_PREFIX_CHARS:]))

    # ----- cancel ------------------------------------------------------------------------------

    def cancel(self, handle: str) -> None:
        """Record the cancellation FIRST, then terminate the process tree. The workspace is never
        touched: it is the evidence an operator inspects. A run whose exit code was recorded has already
        finished: nothing is recorded and nothing is killed (a live pid then belongs to another process),
        and the next poll classifies it from its output."""
        run_id = handle
        manifest = self._read_manifest(run_id)
        if manifest is None:
            return
        if self._recorded_exit(manifest) is not None:
            return
        if not manifest.get("cancelled_at"):
            manifest["cancelled_at"] = _utc_iso(self.clock())
            self._write_manifest(run_id, manifest)
        pid = manifest.get("pid")
        if pid is None:
            return
        kill_tree(int(pid))
        proc = self._procs.get(run_id)
        if proc is not None:
            try:
                proc.wait(timeout=10)
            except subprocess.SubprocessError:
                pass

    # ----- reconcile and usage -----------------------------------------------------------------

    def reconcile(self, run_id: str, handle: Optional[str]) -> ReconcileResult:
        manifest = self._read_manifest(handle or run_id)
        if manifest is None:
            return ReconcileResult(found=False)
        return ReconcileResult(found=True, status=self.poll(handle or run_id), native_session_id=None,
                               native_run_handle=run_id)

    def observe_usage(self) -> UsageObservation:
        return self._allowance_observation(None)
