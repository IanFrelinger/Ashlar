#!/usr/bin/env python3
"""Offline smoke for the actual supported stdio host (no GitHub requests are needed).

Run after building: python3 real_host_scope_smoke.py -- dotnet /path/to/Ashlar.Mcp.Server.Host.dll
Run in a disposable container with networking disabled for independent bypass evidence.
This checks the host tool path only, not control over a client with its own network access.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time

PROTOCOL = "2025-06-18"
TOKEN = "ashlar-host-smoke-fake-token"
SECRETS = (TOKEN, "caller-forged-token")


class Host:
    def __init__(self, command, directory):
        self.directory = Path(directory)
        self.stderr = (self.directory / "stderr.log").open("w+", encoding="utf-8")
        self.env = {k: v for k, v in os.environ.items()
                    if not k.startswith(("Ashlar__Mcp__", "ASHLAR_GITHUB_"))}
        self.env.update({"Ashlar__Mcp__Bricks__ExposedBrickIds__0": "github.blockers",
                         "Ashlar__Audit__File__Directory": str(self.directory / "audit"),
                         "ASHLAR_GITHUB_TOKEN": TOKEN,
                         "ASHLAR_GITHUB_REPOSITORY": "allowed/repository"})
        self.command = command
        self.proc = None

    def start(self):
        self.proc = subprocess.Popen(self.command, env=self.env, stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=self.stderr,
                                     text=True, encoding="utf-8")
        self.lines = queue.Queue()
        def read():
            for line in self.proc.stdout:
                self.lines.put(line)
            self.lines.put(None)
        threading.Thread(target=read, daemon=True).start()

    def send(self, value):
        self.proc.stdin.write(json.dumps(value) + "\n")
        self.proc.stdin.flush()

    def request(self, rid, method, params):
        self.send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        deadline = time.monotonic() + 30
        while True:
            line = self.lines.get(timeout=max(0.001, deadline - time.monotonic()))
            assert line is not None, f"host exited before {method}"
            value = json.loads(line)
            assert not any(secret in line for secret in SECRETS), "host response exposed a credential"
            if value.get("id") == rid:
                return value

    def close(self):
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait()
        self.stderr.flush()
        self.stderr.seek(0)
        logs = self.stderr.read()
        assert not any(secret in logs for secret in SECRETS), "host logs exposed a credential"
        self.stderr.close()
        for path in (self.directory / "audit").rglob("*"):
            if path.is_file():
                text = path.read_text(encoding="utf-8")
                assert not any(secret in text for secret in SECRETS), "audit exposed a credential"


def refused(message):
    return "error" in message or message.get("result", {}).get("isError") is True


def main(command):
    with tempfile.TemporaryDirectory(prefix="ashlar-host-scope-") as root:
        host = Host(command, root)
        host.start()
        try:
            init = host.request(1, "initialize", {"protocolVersion": PROTOCOL, "capabilities": {},
                                "clientInfo": {"name": "ashlar-offline-scope-smoke", "version": "1"}})
            assert init.get("result", {}).get("protocolVersion") == PROTOCOL, "unsupported protocol negotiation"
            host.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            catalog = host.request(2, "tools/list", {})["result"]["tools"]
            assert [tool["name"] for tool in catalog] == ["github_blockers"], "allowlist widened"
            assert "githubToken" not in catalog[0]["inputSchema"]["properties"], "model schema asks for a token"
            for rid, arguments in [(3, {"url": "https://github.com/other/repository/pull/1"}),
                                   (4, {"url": "https://github.com/other/repository/pull/1",
                                        "githubToken": "caller-forged-token"})]:
                result = host.request(rid, "tools/call", {"name": "github_blockers", "arguments": arguments})
                assert refused(result), "off-scope call passed"
                assert "outside" in json.dumps(result), "call failed without reaching the repository scope guard"
            result = host.request(5, "tools/call", {"name": "github_ci_status", "arguments": {}})
            assert refused(result), "unallowlisted tool passed"
        finally:
            host.close()

    # A supplied credential without its scope must fail at startup, before serving tools.
    with tempfile.TemporaryDirectory(prefix="ashlar-host-partial-") as root:
        host = Host(command, root)
        host.env.pop("ASHLAR_GITHUB_REPOSITORY")
        host.start()
        try:
            assert host.proc.wait(timeout=30) != 0, "partial credential configuration did not fail startup"
        finally:
            host.close()
    print("PASS: real stdio host protocol, allowlist, hidden token, scoped refusals, caller-token bypass refusal, partial-config startup refusal, secret-free logs/audit")


if __name__ == "__main__":
    assert len(sys.argv) > 2 and sys.argv[1] == "--", "usage: real_host_scope_smoke.py -- <host command> [args]"
    main(sys.argv[2:])
