"""The Claude Code CLI adapter: one headless `claude -p` per run, every run recorded on disk.

Everything this adapter relies on was observed live on Claude Code 2.1.263 (Windows, 2026-10-11,
model `claude-sonnet-5`, report `probe/probe-report-2.json`); the README section "Claude adapter"
records the shapes verbatim. Observed, and therefore declared in `capabilities`:

  * `--session-id` / `--resume` reuse the session id exactly; `num_turns` and `total_cost_usd` are
    PER INVOCATION (a resumed call reports its own turns and cost, not the session's total);
  * `--max-turns N` is enforced: at the limit the result is `is_error:true, subtype:"error_max_turns",
    terminal_reason:"max_turns"`;
  * a permission denial under `--permission-prompts none` COMPLETES with `is_error:false` and a
    non-empty `permission_denials:[{tool_name, tool_input, tool_use_id}]`, so denials are checked
    before a success counts as completed;
  * an unrecognised model prints `[claude-code:unrecognized_model] {...}` to stderr and returns
    `api_error_status:404` with "issue with the selected model" text;
  * terminating `claude.exe` mid-request exits without a `result` event, keeps the transcript, and a
    later `--resume` of the same id succeeds;
  * `--json-schema` returns `structured_output`; `--no-session-persistence` writes no transcript;
  * `--output-format stream-json --verbose` emits NDJSON: `system/init`, then a `rate_limit_event`
    whose `rate_limit_info.unifiedWindows.five_hour.utilization` is the subscription allowance (the
    only headless source for it), then messages, then the `result` object;
  * `--strict-mcp-config` (no `--mcp-config`) keeps the operator's claude.ai connectors out of the
    run: `mcp_servers:[]`, and the first-call cache creation drops from ~70k to ~9k tokens. Every
    launch passes it.

`total_cost_usd` is `costBasis:"list"`: an estimate of API value, never a charge on a subscription.
It is recorded as an ESTIMATED_API_VALUE observation and is never summed with the allowance.

Run layout: `<runs_dir>/<run_id>/manifest.json` (written BEFORE the spawn, so a crash between the
write and the spawn leaves a record to reconcile), `stdout.json` (the NDJSON stream), `stderr.log`,
and a `workspace/` directory unless the request names one. The run id is the handle.
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

from ..store import pid_alive
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


def locate_transcript(projects_dir: Optional[str], session_id: Optional[str], hint: Optional[str] = None) -> Optional[str]:
    """Where the CLI kept the session's transcript, or None. The slug rule (cwd with every character
    outside [A-Za-z0-9] replaced by '-') is only a hint: the live run of 2026-10-11 showed the CLI
    truncates a long slug (206 chars became 200 plus '-' and a six-character suffix), so the file is
    found by its session id, which is unique, under any project directory."""
    if hint and os.path.isfile(hint):
        return hint
    if not projects_dir or not session_id or not os.path.isdir(projects_dir):
        return None
    name = f"{session_id}.jsonl"
    try:
        for entry in os.listdir(projects_dir):
            candidate = os.path.join(projects_dir, entry, name)
            if os.path.isfile(candidate):
                return candidate
    except OSError:
        return None
    return None


def _json_object(text: str) -> Optional[Dict[str, Any]]:
    """The JSON object `claude auth status --json` printed. The real CLI pretty-prints it over many
    lines (observed 2026-10-11), so the whole text is parsed first; a single trailing object line is
    accepted as a fallback. Run output never comes through here; it goes through `parse_stream`."""
    try:
        obj = json.loads(text.strip())
        if isinstance(obj, dict):
            return obj
    except ValueError:
        pass
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


def parse_stream(text: str) -> Dict[str, Any]:
    """Read the NDJSON of `--output-format stream-json --verbose`. Returns the LAST object whose
    `type` is "result" (None when the run produced none, as a terminated run does), the last
    `rate_limit_event` seen (its `rate_limit_info`), and the `system/init` object. Lines that are not
    JSON objects are ignored."""
    out: Dict[str, Any] = {"result": None, "rate_limit_info": None, "init": None, "events": 0}
    for line in text.splitlines():
        s = line.strip()
        if not s.startswith("{"):
            continue
        try:
            obj = json.loads(s)
        except ValueError:
            continue
        if not isinstance(obj, dict):
            continue
        out["events"] += 1
        kind = obj.get("type")
        if kind == "result":
            out["result"] = obj
        elif kind == "rate_limit_event" and isinstance(obj.get("rate_limit_info"), dict):
            out["rate_limit_info"] = obj["rate_limit_info"]
        elif kind == "system" and obj.get("subtype") == "init":
            out["init"] = obj
    return out


def _normalise_for_identity(text: str) -> str:
    return re.sub(r"\s+", " ", text[:IDENTITY_PREFIX_CHARS]).strip().lower()


def failure_identity(result_text: str) -> str:
    return "claude:" + hashlib.sha256(_normalise_for_identity(result_text).encode("utf-8")).hexdigest()[:12]


class ClaudeAdapter:
    name = "claude"
    capabilities = AdapterCapabilities(
        exact_resume=True,          # observed: --resume <uuid> reused the session id, three times
        cancel=True,                # observed: terminate mid-request keeps the transcript; --resume then works
        structured_output=True,     # observed: --json-schema returns `structured_output`
        usage_observation=True,     # observed: per-invocation num_turns/total_cost_usd; rate_limit_event allowance
        transcript_retention=True,  # observed: <projectsDirectory>/<slug or truncated slug>/<session_id>.jsonl
        turn_limit=True,            # observed: --max-turns 1 -> subtype error_max_turns, terminal_reason max_turns
        funding_route_check=True,   # observed: `claude auth status --json` reports authMethod/loggedIn
        evidence="live probe of claude 2.1.263 on 2026-10-11, probe/probe-report-2.json; see README 'Claude adapter'")

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
        status = _json_object(out)
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
        # stream-json so the rate_limit_event (the allowance source) is on stdout beside the result;
        # --strict-mcp-config so the operator's claude.ai connectors never enter a controller run.
        argv = prefix + ["-p", request.prompt, "--output-format", "stream-json", "--verbose", "--strict-mcp-config",
                         "--model", request.model, "--effort", request.effort,
                         "--max-turns", str(int(request.invocation_ceiling)),
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
            "transcript_path": transcript, "projects_dir": projects_dir, "stdout": os.path.join(run_dir, "stdout.json"),
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
        return pid_alive(int(pid))

    def _allowance_observation(self, session_id: Optional[str], rate_limit_info: Optional[Dict[str, Any]] = None,
                               ) -> UsageObservation:
        """The subscription allowance. Its only headless source is the stream's `rate_limit_event`:
        `rate_limit_info.unifiedWindows.five_hour.utilization` is the fraction of the five-hour window
        used, reported here as a percentage of 100. Without an event (no stream, a run that died before
        the first request, `observe_usage` with no run) the observation is UNAVAILABLE."""
        at = _utc_iso(self.clock())
        windows = (rate_limit_info or {}).get("unifiedWindows") if isinstance(rate_limit_info, dict) else None
        five = windows.get("five_hour") if isinstance(windows, dict) else None
        utilization = five.get("utilization") if isinstance(five, dict) else None
        if isinstance(utilization, (int, float)):
            return UsageObservation(provider=self.name, kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="percent",
                                    freshness=UsageFreshness.FRESH, value=round(float(utilization) * 100, 2),
                                    total=100.0, native_session_id=session_id, observed_at=at, raw=dict(rate_limit_info))
        return UsageObservation(provider=self.name, kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="percent",
                                freshness=UsageFreshness.UNAVAILABLE, native_session_id=session_id, observed_at=at,
                                raw=dict(rate_limit_info) if isinstance(rate_limit_info, dict) else {})

    def poll(self, handle: str) -> RunStatus:
        run_id = handle
        manifest = self._read_manifest(run_id)
        if manifest is None:
            return RunStatus(state=RunState.FAILED, stop_reason=StopReason.NO_FINAL_RESULT,
                             result={"error": f"no manifest for run {run_id} under {self.runs_dir}"})
        session_id = manifest.get("session_id")
        transcript = locate_transcript(manifest.get("projects_dir"), session_id, manifest.get("transcript_path"))
        base = {"workspace": manifest.get("cwd"), "transcript": transcript}
        if manifest.get("cancelled_at"):
            # Observed: a terminated run has no result event but keeps its transcript and resumes later;
            # the allowance event it emitted before termination is still worth recording.
            stream = parse_stream(self._read_text(manifest.get("stdout")))
            return RunStatus(state=RunState.CANCELLED, stop_reason=StopReason.CANCELLED, consumed_units=None,
                             result=dict(base, cancelled_at=manifest["cancelled_at"], resume_session_id=session_id),
                             observations=[self._allowance_observation(session_id, stream["rate_limit_info"])],
                             transcript_path=transcript)
        if self._alive(run_id, manifest):
            return RunStatus(state=RunState.RUNNING)
        return self._classify(manifest)

    def _read_text(self, path: Optional[str]) -> str:
        if not path or not os.path.exists(path):
            return ""
        with open(path, "rb") as f:
            return f.read().decode("utf-8", "replace")

    def _classify(self, manifest: Dict[str, Any]) -> RunStatus:
        """Classify an exited run from its NDJSON stdout, its stderr and the manifest. The order is the
        one observed to be discriminating (module doc); `poll` has already answered CANCELLED."""
        run_id = manifest["run_id"]
        session_id = manifest.get("session_id")
        rc = manifest.get("exit")
        stdout = self._read_text(manifest.get("stdout"))
        stderr = self._read_text(manifest.get("stderr"))
        stream = parse_stream(stdout)
        obj = stream["result"]
        rate_limit_info = stream["rate_limit_info"]
        transcript = locate_transcript(manifest.get("projects_dir"), session_id, manifest.get("transcript_path"))
        base: Dict[str, Any] = {"workspace": manifest.get("cwd"), "transcript": transcript, "exit": rc, "run_id": run_id,
                                "stream_events": stream["events"], "rate_limit_info": rate_limit_info}
        observations = [self._allowance_observation(session_id, rate_limit_info)]
        # Both observations are kept apart: the allowance is the subscription's own unit, the cost is
        # an estimate of API value (costBasis "list"), never a charge. They are never summed.
        consumed: Optional[float] = None
        if obj is not None:
            # Observed: num_turns and total_cost_usd are PER INVOCATION, also on --resume, so the
            # turns are this run's consumption and the cost observation is keyed by the run id (its
            # recorded delta then equals its value).
            if isinstance(obj.get("num_turns"), (int, float)):
                consumed = float(obj["num_turns"])
            usage_raw = obj.get("usage") if isinstance(obj.get("usage"), dict) else {}
            cost = obj.get("total_cost_usd")
            observations.append(UsageObservation(
                provider=self.name, kind=UsageKind.ESTIMATED_API_VALUE, unit="usd", freshness=UsageFreshness.FRESH,
                value=float(cost) if isinstance(cost, (int, float)) else None, native_session_id=run_id,
                observed_at=_utc_iso(self.clock()),
                raw={"tokens": {k: usage_raw.get(k) for k in ("input_tokens", "output_tokens",
                                                              "cache_creation_input_tokens", "cache_read_input_tokens")},
                     "num_turns": obj.get("num_turns"), "duration_api_ms": obj.get("duration_api_ms"),
                     "model_usage": obj.get("modelUsage"), "session_id": session_id}))
        common = {"consumed_units": consumed, "usage": None, "observations": observations, "transcript_path": transcript}

        def failure(reason: str, extra: Optional[Dict[str, Any]] = None, identity: Optional[str] = None, **kw) -> RunStatus:
            merged = dict(common, **kw)
            return RunStatus(state=RunState.FAILED, stop_reason=reason, result=dict(base, **(extra or {})),
                             failure_identity=identity, **merged)

        result_text = ""
        subtype = None
        api_status: Optional[int] = None
        denials: List[Any] = []
        if obj is not None:
            rt = obj.get("result")
            result_text = rt if isinstance(rt, str) else json.dumps(rt) if rt is not None else ""
            subtype = obj.get("subtype")
            try:
                api_status = int(obj["api_error_status"]) if obj.get("api_error_status") is not None else None
            except (TypeError, ValueError):
                api_status = None
            denials = obj.get("permission_denials") or []
            base.update({"subtype": subtype, "terminal_reason": obj.get("terminal_reason"), "api_error_status": api_status,
                         "duration_ms": obj.get("duration_ms"), "result_text": result_text[:RESULT_TEXT_LIMIT],
                         "session_id": obj.get("session_id") or session_id, "stop_reason_native": obj.get("stop_reason"),
                         "permission_denials": denials, "structured_output": obj.get("structured_output")})

        # 1. Unrecognised model. Observed: the stderr tag, and a result with api_error_status 404 whose
        #    text says "issue with the selected model"; total_cost_usd 0, nothing consumed.
        if UNRECOGNIZED_MODEL_MARKER in stderr or (api_status == 404 and re.search(r"model", result_text, re.IGNORECASE)):
            return failure(StopReason.MODEL_UNRECOGNIZED, {"model": manifest.get("model"),
                                                           "stderr_tail": stderr[-STDERR_TAIL_LIMIT:]},
                           consumed_units=0.0)
        # 2. No result object. Observed for a terminated process (poll answers CANCELLED before this
        #    when the manifest says so); otherwise the worker died without saying why.
        if obj is None:
            return failure(StopReason.NO_FINAL_RESULT, {"stderr_tail": stderr[-STDERR_TAIL_LIMIT:]})
        # 3. Overage: the provider is billing paid overflow. Treated as the allowance being exhausted,
        #    never as a reason to continue on a paid route (paid inference is off).
        if isinstance(rate_limit_info, dict) and rate_limit_info.get("isUsingOverage") is True:
            return failure(StopReason.QUOTA_EXHAUSTED,
                           {"overage": True, "blocker": "the provider is billing overage; paid inference is off"})
        # 4. The turn limit. Observed: is_error true, subtype error_max_turns, terminal_reason max_turns.
        if subtype == "error_max_turns":
            return failure(StopReason.INVOCATION_CEILING, {"blocker": f"turn ceiling reached in session {session_id}"})
        # 5. API-level errors, by status then by the observed texts.
        if obj.get("is_error"):
            if api_status == 401 or _AUTH_RE.search(result_text):
                return failure(StopReason.AUTH_MISSING, {"blocker": result_text[:RESULT_TEXT_LIMIT]})
            if api_status == 402 or _QUOTA_RE.search(result_text):
                return failure(StopReason.QUOTA_EXHAUSTED, {"blocker": result_text[:RESULT_TEXT_LIMIT]})
            if api_status == 429 or _RATE_LIMIT_RE.search(result_text):
                return failure(StopReason.RATE_LIMITED, {"blocker": result_text[:RESULT_TEXT_LIMIT]})
            if api_status in TRANSIENT_STATUSES or _TRANSIENT_RE.search(result_text):
                return failure(StopReason.TRANSIENT, {"blocker": result_text[:RESULT_TEXT_LIMIT]})
        # 6. Permission denials, BEFORE success: observed, a denied run completes with is_error false
        #    and `permission_denials:[{tool_name, tool_input, tool_use_id}]`.
        if denials:
            names = []
            for d in denials:
                if isinstance(d, dict):
                    names.append(str(d.get("tool_name") or d.get("tool") or d.get("name") or json.dumps(d, sort_keys=True)))
                else:
                    names.append(str(d))
            return failure(StopReason.PERMISSION_MISSING,
                           {"blocker": "permission denied for: " + ", ".join(names), "denied_tools": names})
        # 7. Completed.
        if not obj.get("is_error"):
            return RunStatus(state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, result=base, **common)
        # 8. Anything else the model reported as an error: a diagnosed failure with a stable identity.
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
