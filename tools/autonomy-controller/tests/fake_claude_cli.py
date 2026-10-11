#!/usr/bin/env python3
"""A stand-in for the `claude` CLI, driven by a JSON script. The tests pass
`[sys.executable, <this file>]` as the adapter's `cli`, so no real `claude` is ever invoked.

Environment:
  FAKE_CLAUDE_SCRIPT    path of the JSON script (required)
  FAKE_CLAUDE_ARGV_OUT  where to record what this invocation received: a directory (one
                        `argv-<pid>.json` per invocation) or a file path

Script keys:
  auth          object printed by `auth status --json`      (default: {"loggedIn": true, "authMethod": "claude.ai"})
  auth_exit     exit code of `auth status`                  (default 0)
  auth_stdout   raw text printed instead of the auth object (optional; simulates a CLI that prints no JSON)
  sleep_seconds how long a `-p` run sleeps after recording its argv (default 0)
  stderr        text written to stderr by a `-p` run
  stdout        the result object printed LAST by a `-p` run (type "result"); its session_id defaults to
                the --session-id / --resume argument unless the script sets one (null: print none)
  rate_limit_info  when an object, a `rate_limit_event` line carrying it is printed after the init line
  no_init       when true, the `system/init` line is not printed
  stdout_raw    raw text printed before the result object
  exit          exit code of a `-p` run (default 0)
  persist       when true, append one line to <auth.projectsDirectory>/<slug(cwd)>/<session>.jsonl
"""
from __future__ import annotations

import json
import os
import re
import sys
import time


def _load_script() -> dict:
    path = os.environ.get("FAKE_CLAUDE_SCRIPT")
    if not path:
        sys.stderr.write("fake_claude_cli: FAKE_CLAUDE_SCRIPT is not set\n")
        sys.exit(70)
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def _record(argv: list) -> None:
    out = os.environ.get("FAKE_CLAUDE_ARGV_OUT")
    if not out:
        return
    payload = {"argv": argv, "cwd": os.getcwd(), "pid": os.getpid(),
               "env": {k: v for k, v in os.environ.items()
                       if k == "CLAUDECODE" or k.startswith("CLAUDE_CODE_") or k.startswith("ANTHROPIC_")}}
    if os.path.isdir(out) or out.endswith(("/", "\\")):
        os.makedirs(out, exist_ok=True)
        out = os.path.join(out, f"argv-{os.getpid()}.json")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(payload, f, indent=2, sort_keys=True)
        f.write("\n")


def _flag(argv: list, name: str):
    for i, a in enumerate(argv):
        if a == name and i + 1 < len(argv):
            return argv[i + 1]
    return None


def main() -> int:
    argv = sys.argv[1:]
    script = _load_script()
    if argv[:2] == ["auth", "status"]:
        if "auth_stdout" in script:
            sys.stdout.write(script["auth_stdout"])
        else:
            auth = script.get("auth", {"loggedIn": True, "authMethod": "claude.ai"})
            sys.stdout.write(json.dumps(auth, indent=2) + "\n")   # the real CLI pretty-prints
        sys.stdout.flush()
        return int(script.get("auth_exit", 0))

    _record(argv)
    session = _flag(argv, "--session-id") or _flag(argv, "--resume")
    if script.get("persist"):
        projects = (script.get("auth") or {}).get("projectsDirectory")
        if projects and session:
            slug = re.sub(r"[^A-Za-z0-9]", "-", os.getcwd())
            d = os.path.join(projects, slug)
            os.makedirs(d, exist_ok=True)
            with open(os.path.join(d, f"{session}.jsonl"), "a", encoding="utf-8", newline="\n") as f:
                f.write(json.dumps({"type": "fake", "session_id": session, "argv": argv}) + "\n")
    # The observed `--output-format stream-json --verbose` NDJSON: system/init first, then the
    # rate_limit_event (the allowance source), both BEFORE the model request (the sleep stands in
    # for it), then messages, then the result object last.
    if not script.get("no_init"):
        init = {"type": "system", "subtype": "init", "session_id": session, "model": _flag(argv, "--model"),
                "permissionMode": _flag(argv, "--permission-mode"), "apiKeySource": "none",
                "claude_code_version": "2.1.263", "cwd": os.getcwd(), "tools": ["Bash", "Edit", "Read", "Workflow"],
                "mcp_servers": [], "agents": [], "slash_commands": []}
        sys.stdout.write(json.dumps(init) + "\n")
    if isinstance(script.get("rate_limit_info"), dict):
        sys.stdout.write(json.dumps({"type": "rate_limit_event", "rate_limit_info": script["rate_limit_info"],
                                     "session_id": session}) + "\n")
    sys.stdout.flush()
    time.sleep(float(script.get("sleep_seconds", 0)))
    if script.get("stderr"):
        sys.stderr.write(script["stderr"])
        sys.stderr.flush()
    if script.get("stdout_raw"):
        sys.stdout.write(script["stdout_raw"])
    result = script.get("stdout")
    if isinstance(result, dict):
        result = dict(result)
        result.setdefault("session_id", session)
        result.setdefault("type", "result")
        sys.stdout.write(json.dumps(result) + "\n")
    sys.stdout.flush()
    return int(script.get("exit", 0))


if __name__ == "__main__":
    sys.exit(main())
