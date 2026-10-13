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
    return int(script.get("print_exit", 0))


if __name__ == "__main__":
    sys.exit(main())
