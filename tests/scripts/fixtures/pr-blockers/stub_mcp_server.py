#!/usr/bin/env python3
"""Stand-in for Ashlar.Mcp.Server.Host over stdio, for tests/scripts/pr-blockers-action.test.sh.

STUB_MODE picks the answer: blocked, ready, structured, tool-error, rpc-error, garbage, exit, silent,
no-verdict. Every message received is appended, one JSON per line, to STUB_LOG, followed by a line
recording the exposed-brick environment variable.
"""
import json
import os
import sys
import time

mode = os.environ.get("STUB_MODE", "ready")
log_path = os.environ["STUB_LOG"]


def log(obj):
    with open(log_path, "a", encoding="utf-8") as f:
        f.write(json.dumps(obj) + "\n")


def send(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


log({"env.ExposedBrickIds__0": os.environ.get("Ashlar__Mcp__Bricks__ExposedBrickIds__0")})

OUTPUTS = {
    "blocked": {
        "verdict": "blocked",
        "blockers": [
            {"reason": "Required check build-core failed", "evidence": "https://github.com/o/r/runs/1"},
            {"reason": "Branch is behind master", "evidence": "https://github.com/o/r/pull/7"},
        ],
        "unknowns": [],
        "answer": "**Blocked** by 2 things.",
    },
    "ready": {"verdict": "ready", "blockers": [], "unknowns": [], "answer": "**Ready** to merge."},
}

for line in sys.stdin:
    msg = json.loads(line)
    log(msg)
    method = msg.get("method")
    if method == "initialize":
        send({"jsonrpc": "2.0", "method": "notifications/message", "params": {"level": "info", "data": "hi"}})
        send({"jsonrpc": "2.0", "id": msg["id"], "result": {
            "protocolVersion": msg["params"]["protocolVersion"],
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "stub", "version": "0"},
        }})
    elif method == "tools/call":
        rid = msg["id"]
        if mode == "exit":
            sys.exit(3)
        if mode == "silent":
            time.sleep(30)
            sys.exit(0)
        if mode == "garbage":
            sys.stdout.write("Building...\n")
            sys.stdout.flush()
            continue
        if mode == "rpc-error":
            send({"jsonrpc": "2.0", "id": rid, "error": {"code": -32602, "message": "Unknown tool"}})
            continue
        if mode == "tool-error":
            send({"jsonrpc": "2.0", "id": rid, "result": {
                "isError": True, "content": [{"type": "text", "text": "url is required (auditId abc)"}]}})
            continue
        if mode == "no-verdict":
            payload = {"brickId": "github.blockers", "brickVersion": "1.0.0", "auditId": "a-0", "outputs": {}}
            send({"jsonrpc": "2.0", "id": rid, "result": {"content": [{"type": "text", "text": json.dumps(payload)}]}})
            continue
        key = "blocked" if mode in ("blocked", "structured") else "ready"
        payload = {"brickId": "github.blockers", "brickVersion": "1.0.0", "auditId": f"audit-{key}",
                   "summary": key, "outputs": OUTPUTS[key]}
        result = {"content": [{"type": "text", "text": json.dumps(payload)}]}
        if mode == "structured":
            result = {"content": [{"type": "text", "text": "not json"}], "structuredContent": payload}
        send({"jsonrpc": "2.0", "id": rid, "result": result})
