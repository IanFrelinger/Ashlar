"""The Claude Code CLI adapter: one headless `claude -p` per run, every run recorded on disk.

Everything this adapter relies on was observed on Claude Code 2.1.263 (Windows, 2026-10-11); the
README section "Claude adapter" records the shapes verbatim. What was NOT observed is marked
UNVERIFIED in the comments below and is not declared in `capabilities`:

  * cancel: `capabilities.cancel=False`. `cancel()` does terminate the process and marks the run
    cancelled, but what the CLI does on SIGTERM / taskkill (transcript flush, partial result) was
    never observed.
  * turn limit: `--max-turns N` is accepted; its effect AT the limit (the `error_max_turns` subtype
    this adapter classifies as INVOCATION_CEILING) is UNVERIFIED, so `turn_limit=False`.
  * `num_turns` on `--resume`: per-invocation or cumulative is UNVERIFIED; consumed_units takes the
    value as reported (see `_classify`).
  * `--json-schema` structured output: accepted by the CLI, never exercised live. The
    `structured_output=True` flag below follows the lane spec's declaration; treat live use as
    unverified until a run has produced a schema-shaped result.
  * `permission_denials[]` entries: only the empty list was observed; the entry shape is guessed.

Run layout: `<runs_dir>/<run_id>/manifest.json` (written BEFORE the spawn, so a crash between the
write and the spawn leaves a record to reconcile), `stdout.json`, `stderr.log`, and a `workspace/`
directory unless the request names one. The run id is the handle.
"""
from __future__ import annotations

import errno
import hashlib
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
import uuid
from typing import Any, Dict, List, Optional, Sequence, Union

from ..models import (LaunchOutcomeUnknown, LaunchRefused, LaunchRequest, LaunchResult, ReconcileResult, RunState,
                      RunStatus, StopReason, UsageFreshness, UsageKind, UsageObservation)
from .base import AdapterCapabilities

UNRECOGNIZED_MODEL_MARKER = "[claude-code:unrecognized_model]"
AUTH_STATUS_TIMEOUT_SECONDS = 30
RESULT_TEXT_LIMIT = 2000
STDERR_TAIL_LIMIT = 2000
IDENTITY_PREFIX_CHARS = 200

# Observed: `api_error_status` is an int or null; the text patterns are the fallback when it is null
# (the observed auth failure carried `api_error_status: null` with the text "Failed to authenticate:
# OAuth session expired and could not be refreshed").
_AUTH_RE = re.compile(r"not logged in|oauth|authenticat", re.IGNORECASE)
_QUOTA_RE = re.compile(r"usage limit|out of (extra )?usage|quota|limit reached|resets at", re.IGNORECASE)
_RATE_LIMIT_RE = re.compile(r"rate.?limit|too many requests", re.IGNORECASE)
_TRANSIENT_RE = re.compile(r"overloaded|timed out|ECONNRESET|ENOTFOUND|network", re.IGNORECASE)
TRANSIENT_STATUSES = (408, 500, 502, 503, 504, 529)

_WINDOWS = sys.platform.startswith("win")


def _utc_iso(t: float) -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t))


def _clean_env(env: Dict[str, str]) -> Dict[str, str]:
    """Strip the nested-session markers; keep ANTHROPIC_* so the funding-route check can see a key."""
    return {k: v for k, v in env.items() if k != "CLAUDECODE" and not k.startswith("CLAUDE_CODE_")}


def _slug(path: str) -> str:
    """The CLI's project slug: the cwd with every char outside [A-Za-z0-9] replaced by '-'."""
    return re.sub(r"[^A-Za-z0-9]", "-", path)


def _last_json_line(text: str) -> Optional[Dict[str, Any]]:
    """The LAST stdout line that starts with '{' and parses as a JSON object (observed: the result
    object is the last line of `--output-format json` output)."""
    for line in reversed(text.splitlines()):
        s = line.strip()
        if not s.startswith("{"):
            continue
        try:
            obj = json.loads(s)
        except ValueError:
            continue
        if isinstance(obj, dict):
            return obj
    return None


def _normalise_for_identity(text: str) -> str:
    return re.sub(r"\s+", " ", text[:IDENTITY_PREFIX_CHARS]).strip().lower()


def failure_identity(result_text: str) -> str:
    return "claude:" + hashlib.sha256(_normalise_for_identity(result_text).encode("utf-8")).hexdigest()[:12]


def _pid_exists(pid: int) -> bool:
    """Existence check that cannot kill. On Windows CPython's `os.kill(pid, sig)` calls TerminateProcess
    for any signal that is not a console-control event, so the POSIX idiom is NOT used there."""
    if pid is None or pid <= 0:
        return False
    if _WINDOWS:
        import ctypes
        from ctypes import wintypes
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)  # type: ignore[attr-defined]
        kernel32.OpenProcess.restype = wintypes.HANDLE
        kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        kernel32.GetExitCodeProcess.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))
        kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        STILL_ACTIVE = 259
        ERROR_ACCESS_DENIED = 5
        handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))
        if not handle:
            return ctypes.get_last_error() == ERROR_ACCESS_DENIED   # exists, but we may not open it
        try:
            code = wintypes.DWORD()
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
                return False
            return code.value == STILL_ACTIVE
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError as e:
        return e.errno == errno.EPERM
    return True


class ClaudeAdapter:
    name = "claude"
    capabilities = AdapterCapabilities(
        exact_resume=True,          # observed: --resume <uuid> reused the session; the transcript grew
        cancel=False,               # UNVERIFIED: the CLI's behaviour on termination was not observed
        structured_output=True,     # --json-schema accepted; live result shape UNVERIFIED (see module doc)
        usage_observation=True,     # observed: num_turns, total_cost_usd and usage{} in the result object
        transcript_retention=True,  # observed: <projectsDirectory>/<slug>/<session_id>.jsonl
        turn_limit=False,           # UNVERIFIED: --max-turns accepted, effect at the limit not observed
        funding_route_check=True,   # observed: `claude auth status --json` reports authMethod/loggedIn
        evidence="probe of claude 2.1.263 on 2026-10-11; see README 'Claude adapter'")

    def __init__(self, runs_dir: str, cli: Optional[Union[str, Sequence[str]]] = None,
                 expected_route: str = "claude.ai", env: Optional[Dict[str, str]] = None, clock=None,
                 permission_mode: str = "acceptEdits"):
        self.runs_dir = os.path.abspath(runs_dir)
        self._cli = cli
        self.expected_route = expected_route
        self._env_source = env
        self.clock = clock or time.time
        self.permission_mode = permission_mode
        # Popen objects of runs launched by THIS process. Polling through them reaps the child (on
        # POSIX an unreaped child is a zombie that `kill -0` reports alive); after a restart the pid
        # in the manifest is all that is left, and the exit code is unknown.
        self._procs: Dict[str, subprocess.Popen] = {}

    # ----- resolution --------------------------------------------------------------------------

    def _env(self) -> Dict[str, str]:
        source = self._env_source if self._env_source is not None else os.environ
        return _clean_env(dict(source))

    def _cli_prefix(self) -> List[str]:
        """The argv prefix that invokes the CLI. A list is used as given (tests pass an interpreter and a
        script); a string is the executable; None resolves `claude` on PATH. On Windows the npm shim
        `claude.cmd` wraps `node_modules/@anthropic-ai/claude-code/bin/claude.exe`: launch the exe."""
        cli = self._cli
        if cli is None:
            cli = shutil.which("claude")
            if cli is None:
                raise LaunchRefused(StopReason.AUTH_MISSING, "claude CLI not found on PATH; install Claude Code "
                                                             "and run `claude auth login`")
        if not isinstance(cli, str):
            return list(cli)
        if cli.lower().endswith(".cmd"):
            exe = os.path.join(os.path.dirname(cli), "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe")
            if os.path.exists(exe):
                return [exe]
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

    # ----- funding route -----------------------------------------------------------------------

    def _auth_status(self, prefix: List[str], env: Dict[str, str]) -> Dict[str, Any]:
        """`claude auth status --json`. Observed shape: {loggedIn, authMethod:"claude.ai", apiProvider,
        subscriptionType, projectsDirectory, email, orgId, orgName}."""
        argv = prefix + ["auth", "status", "--json"]
        try:
            os.makedirs(self.runs_dir, exist_ok=True)
            proc = subprocess.run(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  env=env, cwd=self.runs_dir, timeout=AUTH_STATUS_TIMEOUT_SECONDS)
        except (OSError, subprocess.SubprocessError) as e:
            raise LaunchRefused(StopReason.AUTH_MISSING, f"`claude auth status` did not run: {e}")
        out = proc.stdout.decode("utf-8", "replace")
        status = _last_json_line(out)
        if status is None:
            tail = (proc.stderr.decode("utf-8", "replace") or out)[-STDERR_TAIL_LIMIT:].strip()
            raise LaunchRefused(StopReason.AUTH_MISSING,
                                f"`claude auth status --json` gave no JSON (exit {proc.returncode}): {tail}")
        return status

    def _check_route(self, prefix: List[str], env: Dict[str, str]) -> Dict[str, Any]:
        status = self._auth_status(prefix, env)
        if not status.get("loggedIn"):
            raise LaunchRefused(StopReason.AUTH_MISSING, "claude CLI is not logged in; operator step: `claude auth login`")
        # An API key in the child env means the CLI bills the key (paid spend), whatever the login says.
        observed = "api-key" if env.get("ANTHROPIC_API_KEY") else str(status.get("authMethod") or "unknown")
        if observed != self.expected_route:
            raise LaunchRefused(StopReason.FUNDING_ROUTE_MISMATCH,
                                f"{observed} would be billed; policy selected {self.expected_route}")
        return status

    # ----- launch ------------------------------------------------------------------------------

    def _argv(self, prefix: List[str], request: LaunchRequest, session_id: str) -> List[str]:
        argv = prefix + ["-p", request.prompt, "--output-format", "json", "--model", request.model,
                         "--effort", request.effort, "--max-turns", str(int(request.invocation_ceiling)),
                         "--permission-prompts", "none", "--permission-mode", self.permission_mode, "--no-chrome"]
        if request.resume_session_id:
            argv += ["--resume", session_id]
        else:
            argv += ["--session-id", session_id]
        if request.agent:
            argv += ["--agent", request.agent]
        if request.allowed_tools is not None:
            argv += ["--tools", ",".join(request.allowed_tools)]   # an empty list is `--tools ""`: no tools
        return argv

    def launch(self, request: LaunchRequest) -> LaunchResult:
        if request.speed != "standard":
            # Observed: no speed flag exists; usage.speed is always "standard" and fast_mode_state "off".
            raise LaunchRefused(StopReason.LAUNCH_FAILED,
                                f"speed {request.speed} is not supported by the Claude CLI; only standard")
        env = self._env()
        prefix = self._cli_prefix()
        auth = self._check_route(prefix, env)

        session_id = request.resume_session_id or str(uuid.uuid4())
        run_id = request.run_id
        run_dir = self._run_dir(run_id)
        workspace = os.path.abspath(request.workspace) if request.workspace else os.path.join(run_dir, "workspace")
        os.makedirs(run_dir, exist_ok=True)
        os.makedirs(workspace, exist_ok=True)
        argv = self._argv(prefix, request, session_id)
        projects_dir = auth.get("projectsDirectory")
        transcript = os.path.join(str(projects_dir), _slug(workspace), f"{session_id}.jsonl") if projects_dir else None
        manifest: Dict[str, Any] = {
            "run_id": run_id, "task_id": request.task_id, "session_id": session_id,
            "resume_of": request.resume_session_id, "model": request.model, "argv": argv, "cwd": workspace,
            "started_at": _utc_iso(self.clock()), "pid": None, "exit": None, "cancelled_at": None,
            "transcript_path": transcript, "stdout": os.path.join(run_dir, "stdout.json"),
            "stderr": os.path.join(run_dir, "stderr.log"),
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
                # Popen reports a failed exec only after it has reaped the child: nothing is running.
                raise RuntimeError(f"claude CLI did not start: {e}")
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
            # The process IS running but the record of its pid is not: definite uncertainty.
            raise LaunchOutcomeUnknown(f"claude started (pid {proc.pid}) but the manifest could not be updated: {e}")
        return LaunchResult(native_session_id=session_id, native_run_handle=run_id, workspace=workspace)

    # ----- poll --------------------------------------------------------------------------------

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
        # After a restart: a pid check only. A reused pid would read as alive (pids recycle); the lease
        # has the same limit. The exit code of a run polled this way stays unknown.
        return _pid_exists(int(pid))

    def _allowance_observation(self, session_id: Optional[str]) -> UsageObservation:
        # No headless source for the subscription allowance exists (observed): always unavailable.
        return UsageObservation(provider=self.name, kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="turns",
                                freshness=UsageFreshness.UNAVAILABLE, native_session_id=session_id,
                                observed_at=_utc_iso(self.clock()))

    def poll(self, handle: str) -> RunStatus:
        run_id = handle
        manifest = self._read_manifest(run_id)
        if manifest is None:
            return RunStatus(state=RunState.FAILED, stop_reason=StopReason.NO_FINAL_RESULT,
                             result={"error": f"no manifest for run {run_id} under {self.runs_dir}"})
        session_id = manifest.get("session_id")
        base = {"workspace": manifest.get("cwd"), "transcript": manifest.get("transcript_path")}
        if manifest.get("cancelled_at"):
            return RunStatus(state=RunState.CANCELLED, stop_reason=StopReason.CANCELLED, consumed_units=None,
                             result=dict(base, cancelled_at=manifest["cancelled_at"]),
                             observations=[self._allowance_observation(session_id)],
                             transcript_path=manifest.get("transcript_path"))
        if self._alive(run_id, manifest):
            return RunStatus(state=RunState.RUNNING)
        return self._classify(manifest)

    def _read_text(self, path: Optional[str]) -> str:
        if not path or not os.path.exists(path):
            return ""
        with open(path, "rb") as f:
            return f.read().decode("utf-8", "replace")

    def _classify(self, manifest: Dict[str, Any]) -> RunStatus:
        run_id = manifest["run_id"]
        session_id = manifest.get("session_id")
        rc = manifest.get("exit")
        stdout = self._read_text(manifest.get("stdout"))
        stderr = self._read_text(manifest.get("stderr"))
        allowance = [self._allowance_observation(session_id)]
        transcript = manifest.get("transcript_path")
        base: Dict[str, Any] = {"workspace": manifest.get("cwd"), "transcript": transcript, "exit": rc, "run_id": run_id}

        def failed(reason: str, extra: Dict[str, Any], **kw) -> RunStatus:
            return RunStatus(state=RunState.FAILED, stop_reason=reason, result=dict(base, **extra),
                             observations=allowance, transcript_path=transcript, **kw)

        if UNRECOGNIZED_MODEL_MARKER in stderr:
            # Observed: `[claude-code:unrecognized_model] {"model":"<id>","query_source":"sdk"}`, exit 1,
            # before any request: nothing was consumed.
            return failed(StopReason.MODEL_UNRECOGNIZED, {"model": manifest.get("model"),
                                                          "stderr_tail": stderr[-STDERR_TAIL_LIMIT:]},
                          consumed_units=0.0)
        obj = _last_json_line(stdout)
        if obj is None:
            return failed(StopReason.NO_FINAL_RESULT, {"stderr_tail": stderr[-STDERR_TAIL_LIMIT:]})

        result_text = obj.get("result")
        result_text = result_text if isinstance(result_text, str) else json.dumps(result_text) if result_text is not None else ""
        subtype = obj.get("subtype")
        api_status = obj.get("api_error_status")
        try:
            api_status = int(api_status) if api_status is not None else None
        except (TypeError, ValueError):
            api_status = None
        denials = obj.get("permission_denials") or []
        base.update({"subtype": subtype, "terminal_reason": obj.get("terminal_reason"), "api_error_status": api_status,
                     "duration_ms": obj.get("duration_ms"), "result_text": result_text[:RESULT_TEXT_LIMIT],
                     "session_id": obj.get("session_id") or session_id, "stop_reason_native": obj.get("stop_reason"),
                     "permission_denials": denials})

        # num_turns is taken as reported. Observed per invocation on a fresh session; whether a
        # `--resume` run reports its own turns or the session's cumulative count is UNVERIFIED.
        consumed = float(obj["num_turns"]) if isinstance(obj.get("num_turns"), (int, float)) else None
        usage_raw = obj.get("usage") if isinstance(obj.get("usage"), dict) else {}
        cost = obj.get("total_cost_usd")
        usage = UsageObservation(provider=self.name, kind=UsageKind.ESTIMATED_API_VALUE, unit="usd",
                                 freshness=UsageFreshness.FRESH, value=float(cost) if isinstance(cost, (int, float)) else None,
                                 native_session_id=session_id, observed_at=_utc_iso(self.clock()),
                                 raw={"tokens": {k: usage_raw.get(k) for k in ("input_tokens", "output_tokens",
                                                                               "cache_creation_input_tokens",
                                                                               "cache_read_input_tokens")},
                                      "num_turns": obj.get("num_turns"), "duration_api_ms": obj.get("duration_api_ms"),
                                      "model_usage": obj.get("modelUsage")})
        common = {"consumed_units": consumed, "usage": usage, "observations": allowance, "transcript_path": transcript}

        if not obj.get("is_error") and subtype == "success":
            return RunStatus(state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, result=base, **common)

        def failure(reason: str, extra: Optional[Dict[str, Any]] = None, identity: Optional[str] = None) -> RunStatus:
            return RunStatus(state=RunState.FAILED, stop_reason=reason, result=dict(base, **(extra or {})),
                             failure_identity=identity, **common)

        if subtype == "error_max_turns":
            # UNVERIFIED subtype name: the CLI's behaviour at --max-turns was not observed (module doc).
            return failure(StopReason.INVOCATION_CEILING, {"blocker": f"turn ceiling reached in session {session_id}"})
        if api_status == 401 or _AUTH_RE.search(result_text):
            return failure(StopReason.AUTH_MISSING, {"blocker": result_text[:RESULT_TEXT_LIMIT]})
        if api_status == 402 or _QUOTA_RE.search(result_text):
            return failure(StopReason.QUOTA_EXHAUSTED, {"blocker": result_text[:RESULT_TEXT_LIMIT]})
        if api_status == 429 or _RATE_LIMIT_RE.search(result_text):
            return failure(StopReason.RATE_LIMITED, {"blocker": result_text[:RESULT_TEXT_LIMIT]})
        if api_status in TRANSIENT_STATUSES or _TRANSIENT_RE.search(result_text):
            return failure(StopReason.TRANSIENT, {"blocker": result_text[:RESULT_TEXT_LIMIT]})
        if denials:
            # UNVERIFIED entry shape (only [] was observed): take the likeliest keys, else the entry itself.
            names = []
            for d in denials:
                if isinstance(d, dict):
                    names.append(str(d.get("tool_name") or d.get("tool") or d.get("name") or json.dumps(d, sort_keys=True)))
                else:
                    names.append(str(d))
            return failure(StopReason.PERMISSION_MISSING,
                           {"blocker": "permission denied for: " + ", ".join(names), "denied_tools": names})
        return failure(StopReason.TASK_FAILED, identity=failure_identity(result_text))

    # ----- cancel ------------------------------------------------------------------------------

    def cancel(self, handle: str) -> None:
        """Record the cancellation FIRST, then terminate the process tree. The workspace and the
        transcript are never touched: they are the evidence an operator inspects."""
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
        pid = int(pid)
        if _WINDOWS:
            try:
                subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], stdin=subprocess.DEVNULL,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
            except (OSError, subprocess.SubprocessError):
                pass   # a missing process (or a missing taskkill) is not an error
        else:
            try:
                os.killpg(pid, signal.SIGTERM)
            except (ProcessLookupError, PermissionError, OSError):
                pass
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
        return ReconcileResult(found=True, status=self.poll(handle or run_id),
                               native_session_id=manifest.get("session_id"), native_run_handle=run_id)

    def observe_usage(self) -> UsageObservation:
        return self._allowance_observation(None)
