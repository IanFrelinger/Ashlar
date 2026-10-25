#!/usr/bin/env python3
"""Ask the Ashlar MCP brick host what is blocking a pull request, and report it to GitHub Actions.

Usage: pr_blockers.py -- <command that starts Ashlar.Mcp.Server.Host> [args...]

Environment:
  ASHLAR_PR_BLOCKERS_URL     pull request or release-tag URL (required)
  ASHLAR_GITHUB_TOKEN        token passed to the brick as githubToken (optional)
  ASHLAR_FAIL_ON_BLOCKED     "true" to exit 1 when the verdict is "blocked"
  ASHLAR_PR_BLOCKERS_TIMEOUT seconds before the server is abandoned (default 180)
  GITHUB_STEP_SUMMARY, GITHUB_OUTPUT  set by the runner; skipped when absent

Exit codes: 0 answered, 1 blocked with ASHLAR_FAIL_ON_BLOCKED=true, 2 no answer (tool error,
protocol error, timeout, server exited). An unanswered question is never reported as "ready".
"""
from __future__ import annotations

import json
import os
import queue
import subprocess
import sys
import threading

PROTOCOL_VERSION = "2025-06-18"
TOOL = "github_blockers"
BRICK_ID = "github.blockers"


class NoAnswer(Exception):
    pass


def _reader(stream, sink: "queue.Queue[str | None]") -> None:
    for line in stream:
        sink.put(line)
    sink.put(None)


class Server:
    def __init__(self, command: list[str], timeout: float) -> None:
        env = dict(os.environ)
        env["Ashlar__Mcp__Bricks__ExposedBrickIds__0"] = BRICK_ID
        self.timeout = timeout
        self.proc = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=sys.stderr,
            text=True,
            encoding="utf-8",
            env=env,
        )
        self.lines: "queue.Queue[str | None]" = queue.Queue()
        threading.Thread(target=_reader, args=(self.proc.stdout, self.lines), daemon=True).start()

    def send(self, message: dict) -> None:
        assert self.proc.stdin is not None
        self.proc.stdin.write(json.dumps(message) + "\n")
        self.proc.stdin.flush()

    def request(self, rid: int, method: str, params: dict) -> dict:
        self.send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        while True:
            try:
                line = self.lines.get(timeout=self.timeout)
            except queue.Empty:
                raise NoAnswer(f"no response to {method} within {self.timeout:g}s")
            if line is None:
                raise NoAnswer(f"server exited (code {self.proc.poll()}) before answering {method}")
            line = line.strip()
            if not line:
                continue
            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                raise NoAnswer(f"server wrote a non-JSON line to stdout: {line[:200]}")
            if "method" in message or message.get("id") != rid:
                continue
            if "error" in message:
                raise NoAnswer(f"{method} failed: {json.dumps(message['error'])}")
            return message.get("result") or {}

    def close(self) -> None:
        try:
            if self.proc.stdin:
                self.proc.stdin.close()
            self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()


def ask(command: list[str], url: str, token: str, timeout: float) -> dict:
    server = Server(command, timeout)
    try:
        server.request(1, "initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "ashlar-pr-blockers-action", "version": "1"},
        })
        server.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        arguments = {"url": url}
        if token:
            arguments["githubToken"] = token
        result = server.request(2, "tools/call", {"name": TOOL, "arguments": arguments})
    finally:
        server.close()

    text = "".join(c.get("text", "") for c in result.get("content", []) if c.get("type") == "text")
    if result.get("isError"):
        raise NoAnswer(f"{TOOL} returned a tool error: {text[:1000]}")
    payload = result.get("structuredContent")
    if not isinstance(payload, dict) or "outputs" not in payload:
        try:
            payload = json.loads(text)
        except json.JSONDecodeError:
            raise NoAnswer(f"{TOOL} returned text that is not JSON: {text[:200]}")
    if not isinstance(payload.get("outputs"), dict) or not payload["outputs"].get("verdict"):
        raise NoAnswer(f"{TOOL} returned no verdict: {json.dumps(payload)[:500]}")
    return payload


def summary_markdown(url: str, payload: dict) -> str:
    out = payload["outputs"]
    lines = [f"## Ashlar `{BRICK_ID}`: **{out['verdict']}**", "", f"Asked about {url}.", ""]
    answer = out.get("answer")
    if answer:
        lines += [answer.strip(), ""]
    else:
        for b in out.get("blockers") or []:
            lines.append(f"- Blocker: {b.get('reason')} ({b.get('evidence')})")
        for u in out.get("unknowns") or []:
            lines.append(f"- Not read: {u.get('what')}: {u.get('why')}")
        lines.append("")
    lines.append(
        f"<sub>Brick {payload.get('brickId', BRICK_ID)} {payload.get('brickVersion', '?')}, "
        f"audit id `{payload.get('auditId', '?')}`.</sub>"
    )
    return "\n".join(lines) + "\n"


def append(path_var: str, text: str) -> None:
    path = os.environ.get(path_var)
    if path:
        with open(path, "a", encoding="utf-8") as f:
            f.write(text)


def main(argv: list[str]) -> int:
    if "--" not in argv or argv.index("--") == len(argv) - 1:
        print("usage: pr_blockers.py -- <server command> [args...]", file=sys.stderr)
        return 2
    command = argv[argv.index("--") + 1:]
    url = os.environ.get("ASHLAR_PR_BLOCKERS_URL", "").strip()
    if not url:
        print("::error::No URL to ask about. Set the action's `url` input, or run it on a pull_request event.")
        return 2
    token = os.environ.get("ASHLAR_GITHUB_TOKEN", "")
    fail_on_blocked = os.environ.get("ASHLAR_FAIL_ON_BLOCKED", "false").strip().lower() == "true"
    timeout = float(os.environ.get("ASHLAR_PR_BLOCKERS_TIMEOUT", "180"))

    try:
        payload = ask(command, url, token, timeout)
    except (NoAnswer, OSError) as e:
        print(f"::error::Ashlar {BRICK_ID} gave no answer: {e}")
        append("GITHUB_STEP_SUMMARY", f"## Ashlar `{BRICK_ID}`: no answer\n\n{e}\n")
        append("GITHUB_OUTPUT", "verdict=error\n")
        return 2

    out = payload["outputs"]
    verdict = out["verdict"]
    blockers = out.get("blockers") or []
    markdown = summary_markdown(url, payload)
    print(markdown)
    append("GITHUB_STEP_SUMMARY", markdown)
    append("GITHUB_OUTPUT",
           f"verdict={verdict}\nblocker-count={len(blockers)}\naudit-id={payload.get('auditId', '')}\n")
    if verdict == "blocked" and fail_on_blocked:
        print(f"::error::{len(blockers)} blocker(s) on {url}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
