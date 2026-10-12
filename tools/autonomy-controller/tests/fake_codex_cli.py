#!/usr/bin/env python3
"""A stand-in for the `codex` CLI, driven by a JSON script. The tests pass `[sys.executable, <this file>]`
as the adapter's `cli`, so no real `codex` is ever invoked.

Environment:
  FAKE_CODEX_SCRIPT    path of the JSON script (required)
  FAKE_CODEX_ARGV_OUT  where to record what each invocation received: a directory (`argv-<pid>.json` per
                       `exec`, `login-<pid>.json` per `login status`) or a file path (exec only)

Script keys:
  login_status_stdout  text `login status` prints on stdout (optional)
  login_status_stderr  text `login status` prints on stderr (optional)
                       With neither key, `Logged in using ChatGPT` is printed on STDERR with stdout
                       empty: where codex-cli 0.162.0-alpha.17.2 printed it (probe p0).
  login_status_exit    exit code of `login status`                         (default 0)
  events               the NDJSON objects an `exec` prints, in order (default: the observed p3 shape:
                       a file edit through command_execution, then turn.completed). A `thread.started`
                       without a thread_id gets the resume id, or a fresh uuid on a fresh launch.
  sleep_seconds        how long an `exec` sleeps after printing `sleep_after` events  (default 0)
  sleep_after          how many events are printed before the sleep                  (default 1)
  stderr               text an `exec` writes to stderr after its events
  exit                 exit code of an `exec`                                          (default 0)
  write_session_file   when true, append a line to
                       <CODEX_HOME>/sessions/2026/10/12/rollout-2026-10-12T00-00-00-<thread_id>.jsonl
                       (a `session_meta` line first when the file is new); never without CODEX_HOME
  last_message_file    when true (default), write the last agent message to the `-o` path, if one was given

Recorded per invocation: argv, cwd, pid, the env keys CLAUDECODE, CLAUDE_CODE_*, CODEX_HOME (with values)
and OPENAI_* (names only, never values), and stdin: `isatty`, and `eof` (whether a read of stdin reached
end-of-file within a second, as it does at once on a closed stdin; the real CLI blocks on an open one).
"""
from __future__ import annotations

import json
import os
import sys
import threading
import time
import uuid

LOGGED_IN = "Logged in using ChatGPT"


def _load_script() -> dict:
    path = os.environ.get("FAKE_CODEX_SCRIPT")
    if not path:
        sys.stderr.write("fake_codex_cli: FAKE_CODEX_SCRIPT is not set\n")
        sys.exit(70)
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def _stdin_state() -> dict:
    state = {"isatty": None, "eof": None}
    try:
        state["isatty"] = os.isatty(0)
    except OSError:
        state["isatty"] = None
    done = []

    def drain():
        try:
            while os.read(0, 65536):
                pass
            done.append(True)
        except OSError:
            done.append(True)   # no usable stdin at all: as closed as it gets

    t = threading.Thread(target=drain, daemon=True)
    t.start()
    t.join(1.0)
    state["eof"] = bool(done)
    return state


def _record(kind: str, argv: list, stdin: dict) -> None:
    out = os.environ.get("FAKE_CODEX_ARGV_OUT")
    if not out:
        return
    env = {}
    for k, v in os.environ.items():
        if k == "CLAUDECODE" or k.startswith("CLAUDE_CODE_") or k == "CODEX_HOME":
            env[k] = v
        elif k.startswith("OPENAI_"):
            env[k] = "<set>"
    payload = {"argv": argv, "cwd": os.getcwd(), "pid": os.getpid(), "env": env, "stdin": stdin}
    if os.path.isdir(out) or out.endswith(("/", "\\")):
        os.makedirs(out, exist_ok=True)
        out = os.path.join(out, f"{kind}-{os.getpid()}.json")
    elif kind != "argv":
        return
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(payload, f, indent=2, sort_keys=True)
        f.write("\n")


def _flag(argv: list, name: str):
    for i, a in enumerate(argv):
        if a == name and i + 1 < len(argv):
            return argv[i + 1]
    return None


def _default_events() -> list:
    """The observed p3/q1 shape: an agent message, a command_execution started and completed, a final
    agent message, then turn.completed with the token usage."""
    cmd = "powershell.exe -Command \"Set-Content -LiteralPath hello.txt -Value 'hello' -Encoding ascii\""
    return [
        {"type": "thread.started"},
        {"type": "turn.started"},
        {"type": "item.completed", "item": {"id": "item_0", "type": "agent_message",
                                            "text": "I'll create hello.txt in the current directory."}},
        {"type": "item.started", "item": {"id": "item_1", "type": "command_execution", "command": cmd,
                                          "aggregated_output": "", "exit_code": None, "status": "in_progress"}},
        {"type": "item.completed", "item": {"id": "item_1", "type": "command_execution", "command": cmd,
                                            "aggregated_output": "", "exit_code": 0, "status": "completed"}},
        {"type": "item.completed", "item": {"id": "item_2", "type": "agent_message", "text": "done"}},
        {"type": "turn.completed", "usage": {"input_tokens": 36656, "cached_input_tokens": 31360,
                                             "cache_write_input_tokens": 0, "output_tokens": 64,
                                             "reasoning_output_tokens": 0}},
    ]


def _write_session(thread_id: str, argv: list) -> None:
    home = os.environ.get("CODEX_HOME")
    if not home:
        sys.stderr.write("fake_codex_cli: write_session_file needs CODEX_HOME; nothing written\n")
        return
    d = os.path.join(home, "sessions", "2026", "10", "12")
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, f"rollout-2026-10-12T00-00-00-{thread_id}.jsonl")
    new = not os.path.exists(path)
    with open(path, "a", encoding="utf-8", newline="\n") as f:
        if new:
            f.write(json.dumps({"type": "session_meta", "payload": {
                "id": thread_id, "session_id": thread_id, "cwd": os.getcwd(), "cli_version": "0.162.0-alpha.17.2",
                "model_provider": "openai", "originator": "codex_exec"}}) + "\n")
        f.write(json.dumps({"type": "fake", "argv": argv}) + "\n")


def _exec(argv: list, script: dict) -> int:
    resume_id = None
    if len(argv) > 1 and argv[1] == "resume":
        if "--skip-git-repo-check" not in argv:   # observed: exit 1, no events
            sys.stderr.write("Not inside a trusted directory and --skip-git-repo-check was not specified.\n")
            return 1
        positional = [a for a in argv[2:] if not a.startswith("-")]
        resume_id = positional[0] if positional else None
    events = script.get("events")
    if events is None:
        events = _default_events()
    scripted = [e.get("thread_id") for e in events if e.get("type") == "thread.started" and e.get("thread_id")]
    thread_id = resume_id or (scripted[0] if scripted else str(uuid.uuid4()))
    if script.get("write_session_file"):
        _write_session(thread_id, argv)
    sleep_after = int(script.get("sleep_after", 1))
    last_message = None
    for i, event in enumerate(events):
        if i == sleep_after and script.get("sleep_seconds"):
            sys.stdout.flush()
            time.sleep(float(script["sleep_seconds"]))
        obj = dict(event)
        if obj.get("type") == "thread.started" and not obj.get("thread_id"):
            obj["thread_id"] = thread_id
        item = obj.get("item")
        if obj.get("type") == "item.completed" and isinstance(item, dict) and item.get("type") == "agent_message":
            last_message = item.get("text")
        sys.stdout.write(json.dumps(obj) + "\n")
        sys.stdout.flush()
    if len(events) <= sleep_after and script.get("sleep_seconds"):
        time.sleep(float(script["sleep_seconds"]))
    out_path = _flag(argv, "-o")
    if out_path and last_message is not None and script.get("last_message_file", True):
        with open(out_path, "w", encoding="utf-8", newline="\n") as f:
            f.write(last_message)
    if script.get("stderr"):
        sys.stderr.write(script["stderr"])
        sys.stderr.flush()
    return int(script.get("exit", 0))


def main() -> int:
    argv = sys.argv[1:]
    script = _load_script()
    if argv[:2] == ["login", "status"]:
        _record("login", argv, {"isatty": None, "eof": None})
        if "login_status_stdout" not in script and "login_status_stderr" not in script:
            sys.stderr.write(LOGGED_IN + "\n")
        else:
            sys.stdout.write(script.get("login_status_stdout", ""))
            sys.stderr.write(script.get("login_status_stderr", ""))
        sys.stdout.flush()
        sys.stderr.flush()
        return int(script.get("login_status_exit", 0))
    if argv[:1] == ["exec"]:
        stdin = _stdin_state()
        if not stdin["eof"]:
            sys.stderr.write("Reading additional input from stdin...\n")   # what the real CLI says
            sys.stderr.flush()
        _record("argv", argv, stdin)
        return _exec(argv, script)
    sys.stderr.write(f"fake_codex_cli: unsupported invocation {argv!r}\n")
    return 2


if __name__ == "__main__":
    sys.exit(main())
