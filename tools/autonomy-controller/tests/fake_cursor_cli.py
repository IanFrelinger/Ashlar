#!/usr/bin/env python3
"""A stand-in for the `cursor-agent` CLI, driven by a JSON script. The tests pass
`[sys.executable, <this file>]` as the adapter's `cli`, so no real `cursor-agent` is ever invoked.

Environment:
  FAKE_CURSOR_SCRIPT    path of the JSON script (required)
  FAKE_CURSOR_ARGV_OUT  where to record what each invocation received: a directory (one
                        `<kind>-<pid>.json` per record, kind `status`, `print` or `child`) or a file path.
                        Each record is written once (to a temporary name, then renamed) and never
                        replaced: Windows refuses a rename onto a file that a polling test holds open.

Script keys:
  status_stdout  text `status` prints              (default "Not logged in\\n": the observed logged-out text)
  status_exit    exit code of `status`              (default 0)
  print_stdout   text a `-p` run prints on stdout   (default "")
  print_stream   "observed": a `-p` run then prints `observed_stream()`, the shape of the first logged-in run
                 (OBSERVED_SOURCE), which ends in a `result` event past the adapter's first 2000 characters
  print_stderr   text a `-p` run prints on stderr   (default "")
  print_exit     exit code of a `-p` run            (default 0)
  sleep_seconds  how long a `-p` run sleeps after recording its argv and before printing (default 0)
  spawn_child    when true, a `-p` run first starts a child process that sleeps as long, as the real
                 launcher starts PowerShell and then node, and records the child's pid as `child_pid`
                 in its own `child-<pid>.json`
  gate_file      when set, a `-p` run waits after recording its argv until this file exists (at most
                 GATE_TIMEOUT_SECONDS) instead of sleeping, so a test decides when the run ends

Output is written as bytes, so the files the adapter reads hold exactly the scripted text on every OS.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import time

RECORDED_ENV_PREFIXES = ("CLAUDE_CODE_", "CURSOR_")
RECORDED_ENV_NAMES = ("CLAUDECODE", "NODE_COMPILE_CACHE")
GATE_TIMEOUT_SECONDS = 60.0

# The observed shapes of the first logged-in run: run-bdc63ec68d56 of the PR 5 live run (2026-10-13),
# cursor-agent 2026.10.01-e373342, model composer-2.5. Its manifest's `status_text` and its stdout.json
# (38 events, 17324 bytes; the first 2000 characters end inside event 7). Only the shapes are copied: the
# address is a test address, the ids are made up, and the token counts and duration are the run's own.
OBSERVED_SOURCE = "run-bdc63ec68d56 (PR 5 live run, 2026-10-13)"
OBSERVED_LOGGED_IN = "\u2713 Logged in as owner@example.invalid\n"   # `cursor-agent status`, logged in
OBSERVED_SESSION_ID = "00000000-0000-4000-8000-0000000c0de5"
OBSERVED_REQUEST_ID = "00000000-0000-4000-8000-0000000e9e57"
OBSERVED_USAGE = {"inputTokens": 11983, "outputTokens": 588, "cacheReadTokens": 49476, "cacheWriteTokens": 0}
# The last event of the stream, with its keys in the observed order.
OBSERVED_RESULT_EVENT = {"type": "result", "subtype": "success", "duration_ms": 13601, "duration_api_ms": 13601,
                         "is_error": False, "result": "Added LIVE-PR5-CURSOR.md and committed it.",
                         "session_id": OBSERVED_SESSION_ID, "request_id": OBSERVED_REQUEST_ID,
                         "usage": dict(OBSERVED_USAGE)}


def observed_stream() -> str:
    """The observed event order, shortened: system/init, user, thinking deltas then completed, assistant,
    tool_call started and completed, a closing assistant message, and the `result` event last. The same
    session_id is on every event, and the lines are compact JSON, as observed."""
    sid = OBSERVED_SESSION_ID
    call = "toolu_fake_0001"
    glob = {"targetDirectory": "/fake/workspace", "globPattern": "*.md"}

    def message(role: str, text: str) -> dict:
        return {"role": role, "content": [{"type": "text", "text": text}]}

    thinking = [{"type": "thinking", "subtype": "delta", "text": "Reading the workspace before writing the file. " * 3,
                 "session_id": sid, "timestamp_ms": 1760368740000 + i} for i in range(10)]
    events = ([{"type": "system", "subtype": "init", "apiKeySource": "login", "cwd": "/fake/workspace",
                "session_id": sid, "model": "Composer 2.5", "permissionMode": "default"},
               {"type": "user", "message": message("user", "card #779"), "session_id": sid}]
              + thinking
              + [{"type": "thinking", "subtype": "completed", "session_id": sid, "timestamp_ms": 1760368741000},
                 {"type": "assistant", "message": message("assistant", "Looking."), "session_id": sid,
                  "model_call_id": "mc-fake-0001", "timestamp_ms": 1760368741001},
                 {"type": "tool_call", "subtype": "started", "call_id": call,
                  "tool_call": {"globToolCall": {"args": dict(glob)}},
                  "model_call_id": "mc-fake-0001", "session_id": sid, "timestamp_ms": 1760368741002},
                 {"type": "tool_call", "subtype": "completed", "call_id": call,
                  "tool_call": {"globToolCall": {"args": dict(glob),
                                                 "result": {"success": {"files": [], "totalFiles": 0}}}},
                  "model_call_id": "mc-fake-0001", "session_id": sid, "timestamp_ms": 1760368741003},
                 {"type": "assistant", "message": message("assistant", "Done."), "session_id": sid,
                  "model_call_id": "mc-fake-0002", "timestamp_ms": 1760368741004},
                 OBSERVED_RESULT_EVENT])
    return "".join(json.dumps(e, separators=(",", ":")) + "\n" for e in events)


def _load_script() -> dict:
    path = os.environ.get("FAKE_CURSOR_SCRIPT")
    if not path:
        sys.stderr.write("fake_cursor_cli: FAKE_CURSOR_SCRIPT is not set\n")
        sys.exit(70)
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def _record(kind: str, argv: list, **extra) -> None:
    out = os.environ.get("FAKE_CURSOR_ARGV_OUT")
    if not out:
        return
    payload = {"kind": kind, "argv": argv, "cwd": os.getcwd(), "pid": os.getpid(), "ppid": os.getppid(),
               "env": {k: v for k, v in os.environ.items()
                       if k in RECORDED_ENV_NAMES or k.startswith(RECORDED_ENV_PREFIXES)}}
    payload.update(extra)
    if os.path.isdir(out) or out.endswith(("/", "\\")):
        os.makedirs(out, exist_ok=True)
        out = os.path.join(out, f"{kind}-{os.getpid()}.json")
    tmp = out + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(payload, f, indent=2, sort_keys=True)
        f.write("\n")
    os.replace(tmp, out)   # a test polling for the file never reads half of it


def _write(stream, text: str) -> None:
    if text:
        stream.buffer.write(text.encode("utf-8"))
        stream.flush()


def main() -> int:
    argv = sys.argv[1:]
    if argv[:1] == ["--fake-child"]:
        time.sleep(float(argv[1]))
        return 0
    script = _load_script()
    if argv[:1] in (["status"], ["whoami"]):
        _record("status", argv)
        _write(sys.stdout, script.get("status_stdout", "Not logged in\n"))
        return int(script.get("status_exit", 0))
    if "-p" not in argv and "--print" not in argv:
        sys.stderr.write(f"fake_cursor_cli: unscripted invocation {argv!r}\n")
        return 64

    sleep_seconds = float(script.get("sleep_seconds", 0))
    _record("print", argv)
    if script.get("spawn_child"):
        child = subprocess.Popen([sys.executable, os.path.abspath(__file__), "--fake-child", str(sleep_seconds)],
                                 stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        _record("child", argv, child_pid=child.pid)   # its own file: print-<pid>.json is never replaced
    gate = script.get("gate_file")
    if gate:
        deadline = time.time() + GATE_TIMEOUT_SECONDS
        while not os.path.exists(gate) and time.time() < deadline:
            time.sleep(0.02)
    else:
        time.sleep(sleep_seconds)
    _write(sys.stderr, script.get("print_stderr", ""))
    _write(sys.stdout, script.get("print_stdout", ""))
    if script.get("print_stream") == "observed":
        _write(sys.stdout, observed_stream())
    return int(script.get("print_exit", 0))


if __name__ == "__main__":
    sys.exit(main())
