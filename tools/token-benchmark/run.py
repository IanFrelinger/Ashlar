#!/usr/bin/env python3
"""Live token benchmark: one brick call over MCP versus a native GitHub tool loop, on the Anthropic API.

Both arms get the same system prompt, the same question and the same model, with prompt caching on, and
answer from the same data:
  native  one generic tool, github_api_get(path), the pattern of an agent with `gh api` or a GitHub
          MCP server; answers come from recorded GitHub API responses (fixtures/github), recorded live
          on first use and replayed after, so every repetition sees the same data.
  brick   one tool, github_blockers(url); answers are the real Ashlar.Mcp.Server.Host payloads recorded by
          capture_brick.py (fixtures/brick), byte-for-byte the text the MCP tool returns.
  ghmcp   the official GitHub MCP server's pull request, checks, release, tag, commit and Actions read tools,
          with the server's own names, descriptions and schemas (fixtures/ghmcp/tools.json); answers are the
          server's own tool results, recorded live through GHMCP_COMMAND on first use and replayed after.
Backends: the Anthropic Messages API (default) or a local Ollama server (--backend ollama, cost 0).
Token counts are the API's own `usage` fields, summed over every request of a run. Cost is computed from
the prices passed on the command line, so the published result names the prices it used.

Usage:
  ANTHROPIC_API_KEY=... [ANTHROPIC_WORKSPACE_ID=...] [GITHUB_TOKEN=...] \
  run.py --model M --price-in 3 --price-out 15 --reps-native 2 --reps-brick 3 --budget-usd 4.5 --out DIR

The key is read from the environment, sent only in the request header, and never printed or written.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import pathlib
import re
import shlex
import subprocess
import statistics
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent
API = "https://api.anthropic.com/v1/messages"
VERDICTS = ("blocked", "ready", "unknown", "merged", "closed")

SYSTEM = (
    "You are a release-engineering agent. Answer the user's question about GitHub pull requests or "
    "releases by reading GitHub with the tools you have. Name each blocker with the evidence it rests "
    "on, and say plainly what you could not read. Do not guess. End your answer with one line per URL, "
    "exactly: VERDICT <url> <blocked|ready|unknown|merged|closed>"
)

TOOLS = {
    "native": [{
        "name": "github_api_get",
        "description": (
            "GET a GitHub REST API path (https://api.github.com<path>) and return the response body as "
            "received, or the status code and body on an error. Example paths: "
            "/repos/{owner}/{repo}/pulls/{number}, /repos/{owner}/{repo}/pulls/{number}/reviews, "
            "/repos/{owner}/{repo}/commits/{sha}/check-runs, /repos/{owner}/{repo}/commits/{sha}/status, "
            "/repos/{owner}/{repo}/branches/{branch}/protection, /repos/{owner}/{repo}/releases/tags/{tag}."
        ),
        "input_schema": {
            "type": "object",
            "properties": {"path": {"type": "string", "description": "Path starting with /, query string allowed"}},
            "required": ["path"],
        },
    }],
    "brick": [{
        "name": "github_blockers",
        "description": (
            "What is blocking a pull request or release: a verdict (blocked, ready, unknown, merged, closed), "
            "each blocker with an evidence link, and every fact that could not be read, with the reason. "
            "Input: url, https://github.com/<owner>/<repo>/pull/<n> or .../releases/tag/<tag>."
        ),
        "input_schema": {
            "type": "object",
            "properties": {"url": {"type": "string"}},
            "required": ["url"],
        },
    }],
}


def mask(text: str) -> str:
    key = os.environ.get("ANTHROPIC_API_KEY", "")
    return text.replace(key, "***") if key else text


def slug(url: str) -> str:
    return url.removeprefix("https://github.com/").replace("/", "_")


class Fixtures:
    def __init__(self, root: pathlib.Path, github_token: str):
        self.github = root / "github"
        self.brick = root / "brick"
        self.ghmcp = root / "ghmcp"
        self.github.mkdir(parents=True, exist_ok=True)
        self.token = github_token
        self.live_fetches = 0
        self.ghmcp_server = None

    def ghmcp_tools(self) -> list:
        spec = json.loads((self.ghmcp / "tools.json").read_text())
        return [{"name": t["name"], "description": t["description"], "input_schema": t["inputSchema"]}
                for t in spec["tools"]]

    def ghmcp_call(self, name: str, args: dict) -> str:
        key = json.dumps({"name": name, "arguments": args}, sort_keys=True)
        f = self.ghmcp / "calls" / f"{name}-{hashlib.sha256(key.encode()).hexdigest()[:12]}.json"
        if not f.exists():
            if self.ghmcp_server is None:
                cmd = os.environ.get("GHMCP_COMMAND")
                if not cmd:
                    return "ERROR: no recorded response for this call, and GHMCP_COMMAND is not set"
                self.ghmcp_server = McpStdio(shlex.split(cmd))
            result = self.ghmcp_server.call(name, args)
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_text(json.dumps({"request": json.loads(key), "result": result,
                                     "recorded_at": datetime.datetime.now(datetime.timezone.utc).isoformat()}))
            self.live_fetches += 1
        rec = json.loads(f.read_text())["result"]
        text = "".join(c.get("text", "") for c in rec.get("content", []) if c.get("type") == "text")
        return f"ERROR: {text}" if rec.get("isError") else text

    def github_get(self, path: str) -> str:
        if not path.startswith("/"):
            path = "/" + path
        name = re.sub(r"[^A-Za-z0-9._-]+", "_", path.strip("/"))[:150]
        name += "-" + hashlib.sha256(path.encode()).hexdigest()[:8] + ".json"
        f = self.github / name
        if not f.exists():
            req = urllib.request.Request("https://api.github.com" + path, headers={
                "Accept": "application/vnd.github+json", "User-Agent": "ashlar-token-benchmark",
                **({"Authorization": f"Bearer {self.token}"} if self.token else {})})
            try:
                with urllib.request.urlopen(req, timeout=60) as r:
                    rec = {"path": path, "status": r.status, "body": r.read().decode("utf-8", "replace")}
            except urllib.error.HTTPError as e:
                rec = {"path": path, "status": e.code, "body": e.read().decode("utf-8", "replace")}
            rec["recorded_at"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
            f.write_text(json.dumps(rec))
            self.live_fetches += 1
        rec = json.loads(f.read_text())
        return rec["body"] if rec["status"] < 400 else f"HTTP {rec['status']}\n{rec['body']}"

    def brick_answer(self, url: str) -> str:
        f = self.brick / f"{slug(url)}.json"
        if not f.exists():
            return json.dumps({"error": f"no recorded brick answer for {url}"})
        return json.dumps(json.loads(f.read_text()), separators=(",", ":"))

    def brick_verdict(self, url: str) -> str | None:
        f = self.brick / f"{slug(url)}.json"
        return json.loads(f.read_text())["outputs"]["verdict"] if f.exists() else None


class McpStdio:
    def __init__(self, command: list):
        self.proc = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, text=True)
        self.next_id = 0
        self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                    "clientInfo": {"name": "ashlar-token-benchmark", "version": "1"}})
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.proc.stdin.flush()

    def request(self, method: str, params: dict) -> dict:
        self.next_id += 1
        rid = self.next_id
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": rid, "method": method, "params": params}) + "\n")
        self.proc.stdin.flush()
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise SystemExit(f"MCP server exited during {method}")
            msg = json.loads(line)
            if "method" not in msg and msg.get("id") == rid:
                if "error" in msg:
                    return {"isError": True, "content": [{"type": "text", "text": json.dumps(msg["error"])}]}
                return msg["result"]

    def call(self, name: str, args: dict) -> dict:
        return self.request("tools/call", {"name": name, "arguments": args})


class Budget(Exception):
    pass


class Client:
    def __init__(self, model: str, price_in: float, price_out: float, budget: float,
                 write_mult: float = 1.25, read_mult: float = 0.10,
                 long_threshold: int = 0, long_price_in: float = 0.0, long_price_out: float = 0.0):
        self.model, self.price_in, self.price_out, self.budget = model, price_in, price_out, budget
        self.write_mult, self.read_mult = write_mult, read_mult
        self.long_threshold, self.long_price_in, self.long_price_out = long_threshold, long_price_in, long_price_out
        self.spent = 0.0
        self.headers = {
            "x-api-key": os.environ["ANTHROPIC_API_KEY"],
            "anthropic-version": "2023-06-01",
            "content-type": "application/json",
        }
        if os.environ.get("ANTHROPIC_WORKSPACE_ID"):
            self.headers["anthropic-workspace-id"] = os.environ["ANTHROPIC_WORKSPACE_ID"]

    def cost(self, u: dict) -> float:
        pin, pout = self.price_in, self.price_out
        prompt = sum(u.get(k, 0) or 0 for k in ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens"))
        if self.long_threshold and prompt > self.long_threshold:
            pin, pout = self.long_price_in, self.long_price_out
        return ((u.get("input_tokens", 0) or 0) * pin
                + (u.get("cache_creation_input_tokens", 0) or 0) * pin * self.write_mult
                + (u.get("cache_read_input_tokens", 0) or 0) * pin * self.read_mult
                + (u.get("output_tokens", 0) or 0) * pout) / 1e6

    def create(self, body: dict) -> dict:
        if self.spent >= self.budget:
            raise Budget(f"budget ${self.budget:.2f} reached (spent ${self.spent:.4f})")
        data = json.dumps({"model": self.model, **body}).encode()
        for attempt in range(4):
            req = urllib.request.Request(API, data=data, headers=self.headers, method="POST")
            try:
                with urllib.request.urlopen(req, timeout=300) as r:
                    resp = json.loads(r.read())
                self.spent += self.cost(resp.get("usage", {}))
                return resp
            except urllib.error.HTTPError as e:
                text = mask(e.read().decode("utf-8", "replace"))
                if e.code in (429, 500, 502, 503, 529) and attempt < 3:
                    time.sleep(8 * (attempt + 1))
                    continue
                raise SystemExit(f"Anthropic API HTTP {e.code}: {text}")
        raise SystemExit("Anthropic API: retries exhausted")


class OllamaClient:
    """Same create() shape as Client, served by a local Ollama model. Cost is 0; tokens are Ollama's counts."""

    def __init__(self, model: str, url: str, num_ctx: int):
        self.model, self.url, self.num_ctx = model, url.rstrip("/"), num_ctx
        self.spent, self.write_mult, self.read_mult = 0.0, 1.0, 1.0

    def cost(self, u: dict) -> float:
        return 0.0

    def create(self, body: dict) -> dict:
        names, msgs = {}, [{"role": "system", "content": body["system"][0]["text"]}]
        for m in body["messages"]:
            content = m["content"]
            if isinstance(content, str):
                msgs.append({"role": m["role"], "content": content})
                continue
            if m["role"] == "assistant":
                calls = []
                for b in content:
                    if b.get("type") == "tool_use":
                        names[b["id"]] = b["name"]
                        calls.append({"function": {"name": b["name"], "arguments": b["input"]}})
                text = "".join(b.get("text", "") for b in content if b.get("type") == "text")
                msgs.append({"role": "assistant", "content": text, **({"tool_calls": calls} if calls else {})})
                continue
            for b in content:
                if b.get("type") == "tool_result":
                    msgs.append({"role": "tool", "content": b["content"], "tool_name": names.get(b["tool_use_id"], "")})
                elif b.get("type") == "text":
                    msgs.append({"role": "user", "content": b["text"]})
        req = {"model": self.model, "messages": msgs, "stream": False, "think": False,
               "options": {"num_ctx": self.num_ctx, "num_predict": body.get("max_tokens", 2048), "temperature": 0},
               "tools": [{"type": "function", "function": {"name": t["name"], "description": t["description"],
                                                           "parameters": t["input_schema"]}} for t in body["tools"]]}
        r = urllib.request.Request(self.url + "/api/chat", data=json.dumps(req).encode(),
                                   headers={"content-type": "application/json"}, method="POST")
        with urllib.request.urlopen(r, timeout=3600) as resp:
            out = json.loads(resp.read())
        msg = out.get("message", {})
        blocks = [{"type": "text", "text": msg["content"]}] if msg.get("content") else []
        for i, c in enumerate(msg.get("tool_calls") or []):
            fn = c.get("function", {})
            args = fn.get("arguments") or {}
            if isinstance(args, str):
                try:
                    args = json.loads(args)
                except json.JSONDecodeError:
                    args = {}
            blocks.append({"type": "tool_use", "id": f"call_{len(names) + i}", "name": fn.get("name", ""), "input": args})
        return {"content": blocks or [{"type": "text", "text": ""}],
                "stop_reason": "tool_use" if msg.get("tool_calls") else "end_turn",
                "usage": {"input_tokens": out.get("prompt_eval_count", 0), "output_tokens": out.get("eval_count", 0),
                          "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0}}


def with_cache_breakpoint(messages: list) -> list:
    """Incremental caching, as agent loops do it: a breakpoint on the last block of the conversation."""
    out = json.loads(json.dumps(messages))
    for m in out:
        for block in m["content"] if isinstance(m["content"], list) else []:
            block.pop("cache_control", None)
    last = out[-1]
    if isinstance(last["content"], str):
        last["content"] = [{"type": "text", "text": last["content"]}]
    last["content"][-1]["cache_control"] = {"type": "ephemeral"}
    return out


def run_once(client, fx: Fixtures, arm: str, workload: dict, max_turns: int, reference: dict) -> dict:
    question = workload["question"].format(urls=" ".join(workload["urls"]))
    messages = [{"role": "user", "content": question}]
    usage = {"input_tokens": 0, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0, "output_tokens": 0}
    tool_calls, requests, cost = 0, 0, 0.0
    started = time.monotonic()
    final_text, stop = "", None
    for _ in range(max_turns):
        resp = client.create({
            "max_tokens": 2048,
            "system": [{"type": "text", "text": SYSTEM, "cache_control": {"type": "ephemeral"}}],
            "tools": fx.ghmcp_tools() if arm == "ghmcp" else TOOLS[arm],
            "messages": with_cache_breakpoint(messages),
        })
        requests += 1
        for k in usage:
            usage[k] += resp.get("usage", {}).get(k, 0) or 0
        cost += client.cost(resp.get("usage", {}))
        stop = resp.get("stop_reason")
        messages.append({"role": "assistant", "content": resp["content"]})
        uses = [b for b in resp["content"] if b.get("type") == "tool_use"]
        if not uses:
            final_text = "".join(b.get("text", "") for b in resp["content"] if b.get("type") == "text")
            break
        results = []
        for u in uses:
            tool_calls += 1
            if u["name"] == "github_api_get":
                out = fx.github_get(str(u["input"].get("path", "")))
            elif u["name"] == "github_blockers":
                out = fx.brick_answer(str(u["input"].get("url", "")))
            elif arm == "ghmcp":
                out = fx.ghmcp_call(u["name"], u["input"] if isinstance(u["input"], dict) else {})
            else:
                out = f"unknown tool {u['name']}"
            results.append({"type": "tool_result", "tool_use_id": u["id"], "content": out})
        messages.append({"role": "user", "content": results})
    else:
        stop = "max_turns"

    final_text = re.sub(r"<think>.*?</think>", "", final_text, flags=re.S).strip()
    verdicts = {}
    for m in re.finditer(r"VERDICT\s+(\S+)\s+(\w+)", final_text):
        verdicts[m.group(1).rstrip(".,")] = m.group(2).lower()
    agree = sum(1 for url in workload["urls"] if verdicts.get(url) and verdicts.get(url) == fx.brick_verdict(url))
    agree_ref = sum(1 for url in workload["urls"] if verdicts.get(url) and verdicts.get(url) == reference.get(url))
    return {
        "arm": arm, "workload": workload["id"], "requests": requests, "tool_calls": tool_calls,
        "usage": usage, "billed_input_equivalent": round(
            usage["input_tokens"] + client.write_mult * usage["cache_creation_input_tokens"]
            + client.read_mult * usage["cache_read_input_tokens"]),
        "cost_usd": round(cost, 6), "seconds": round(time.monotonic() - started, 1), "stop": stop,
        "verdicts": verdicts, "agrees_with_brick": f"{agree}/{len(workload['urls'])}",
        "agrees_with_reference": f"{agree_ref}/{len(workload['urls'])}" if reference else None,
        "final_answer": final_text,
    }


def summarize(runs: list, meta: dict) -> str:
    if meta["backend"] == "ollama":
        head = f"Local model `{meta['model']}` on Ollama (num_ctx {meta['num_ctx']}); no API cost. Medians over repetitions."
    else:
        head = (f"Model `{meta['model']}`; prices ${meta['price_in']}/MTok input, ${meta['price_out']}/MTok output, "
                f"cache write {meta['write_mult']}×, cache read {meta['read_mult']}× input"
                + (f"; prompts over {meta['long_threshold']} tokens at ${meta['long_price_in']}/${meta['long_price_out']}"
                   if meta.get("long_threshold") else "") + ". Medians over repetitions.")
    ref = any(r.get("agrees_with_reference") for r in runs)
    lines = [head, "",
             "| Workload | Arm | Runs | Requests | Tool calls | Input | Cache write | Cache read | Output | Cost (USD) | Seconds | Verdicts agreeing with brick |"
             + (" Verdicts agreeing with reference |" if ref else ""),
             "|---|---|---|---|---|---|---|---|---|---|---|---|" + ("---|" if ref else "")]
    by = {}
    for r in runs:
        by.setdefault((r["workload"], r["arm"]), []).append(r)
    med = statistics.median
    for (w, arm), rs in sorted(by.items()):
        u = lambda k: round(med([r["usage"][k] for r in rs]))  # noqa: E731
        lines.append(
            f"| {w} | {arm} | {len(rs)} | {med([r['requests'] for r in rs]):g} | {med([r['tool_calls'] for r in rs]):g} "
            f"| {u('input_tokens')} | {u('cache_creation_input_tokens')} | {u('cache_read_input_tokens')} "
            f"| {u('output_tokens')} | {med([r['cost_usd'] for r in rs]):.4f} | {med([r['seconds'] for r in rs]):.1f} "
            f"| {', '.join(r['agrees_with_brick'] for r in rs)} |"
            + (f" {', '.join(str(r.get('agrees_with_reference')) for r in rs)} |" if ref else ""))
    lines += ["", f"Total spent: ${meta['spent']:.4f}. Tool responses fetched live on first use: {meta['live_fetches']}."]
    return "\n".join(lines) + "\n"


def parse_reps(text: str) -> dict:
    out = {}
    for part in filter(None, text.split(",")):
        arm, _, n = part.partition("=")
        if arm not in ("native", "brick", "ghmcp"):
            raise SystemExit(f"unknown arm {arm}")
        out[arm] = int(n)
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--backend", choices=("anthropic", "ollama"), default="anthropic")
    ap.add_argument("--model", required=True)
    ap.add_argument("--price-in", type=float, default=0.0)
    ap.add_argument("--price-out", type=float, default=0.0)
    ap.add_argument("--cache-write-mult", type=float, default=1.25)
    ap.add_argument("--cache-read-mult", type=float, default=0.10)
    ap.add_argument("--long-threshold", type=int, default=0, help="prompt tokens above which the long prices apply")
    ap.add_argument("--long-price-in", type=float, default=0.0)
    ap.add_argument("--long-price-out", type=float, default=0.0)
    ap.add_argument("--reps", default="", help="per arm, e.g. native=3,brick=3,ghmcp=3")
    ap.add_argument("--reps-native", type=int, default=2)
    ap.add_argument("--reps-brick", type=int, default=3)
    ap.add_argument("--max-turns", type=int, default=15)
    ap.add_argument("--budget-usd", type=float, default=4.5)
    ap.add_argument("--fixtures", default=str(HERE / "fixtures"))
    ap.add_argument("--reference", default="", help="runs.jsonl whose verdicts are the reference for agreement")
    ap.add_argument("--ollama-url", default="http://127.0.0.1:11434")
    ap.add_argument("--num-ctx", type=int, default=32768)
    ap.add_argument("--out", required=True)
    ap.add_argument("--only", default="", help="comma-separated workload ids")
    a = ap.parse_args()

    fx = Fixtures(pathlib.Path(a.fixtures), os.environ.get("GITHUB_TOKEN", ""))
    if a.backend == "ollama":
        client = OllamaClient(a.model, a.ollama_url, a.num_ctx)
    else:
        if not os.environ.get("ANTHROPIC_API_KEY"):
            print("ANTHROPIC_API_KEY is not set", file=sys.stderr)
            return 2
        client = Client(a.model, a.price_in, a.price_out, a.budget_usd, a.cache_write_mult, a.cache_read_mult,
                        a.long_threshold, a.long_price_in, a.long_price_out)
    reps = parse_reps(a.reps) if a.reps else {"native": a.reps_native, "brick": a.reps_brick}
    reference = {}
    if a.reference:
        for line in open(a.reference, encoding="utf-8"):
            for url, v in json.loads(line)["verdicts"].items():
                reference.setdefault(url, v)
    workloads = json.loads((HERE / "targets.json").read_text())["workloads"]
    if a.only:
        workloads = [w for w in workloads if w["id"] in a.only.split(",")]
    for w in workloads:
        for url in w["urls"]:
            if fx.brick_verdict(url) is None:
                print(f"missing brick fixture for {url}; run capture_brick.py first", file=sys.stderr)
                return 2

    out = pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    runs, stopped = [], None
    # Interleave arms per repetition so a drift in the API over the run hits every arm alike.
    plan = [(arm, w, rep) for rep in range(max(reps.values())) for w in workloads
            for arm in ("native", "ghmcp", "brick") if rep < reps.get(arm, 0)]
    with open(out / "runs.jsonl", "a", encoding="utf-8") as log:
        for arm, w, rep in plan:
            try:
                r = run_once(client, fx, arm, w, a.max_turns, reference)
            except Budget as e:
                stopped = str(e)
                break
            r["rep"] = rep
            r["model"] = a.model
            runs.append(r)
            log.write(json.dumps(r) + "\n")
            log.flush()
            print(f"{w['id']:<22} {arm:<6} rep {rep}: {r['requests']} req, {r['tool_calls']} calls, "
                  f"${r['cost_usd']:.4f}, {r['seconds']}s, verdicts vs brick {r['agrees_with_brick']}"
                  f"{', vs reference ' + r['agrees_with_reference'] if r.get('agrees_with_reference') else ''} "
                  f"(spent ${client.spent:.4f})", flush=True)
    meta = {"backend": a.backend, "model": a.model, "price_in": a.price_in, "price_out": a.price_out,
            "write_mult": a.cache_write_mult, "read_mult": a.cache_read_mult, "long_threshold": a.long_threshold,
            "long_price_in": a.long_price_in, "long_price_out": a.long_price_out, "num_ctx": a.num_ctx,
            "spent": client.spent, "live_fetches": fx.live_fetches, "stopped": stopped,
            "finished_at": datetime.datetime.now(datetime.timezone.utc).isoformat()}
    (out / "meta.json").write_text(json.dumps(meta, indent=1))
    (out / "summary.md").write_text(summarize(runs, meta) + (f"\nStopped early: {stopped}\n" if stopped else ""))
    print((out / "summary.md").read_text())
    return 0


if __name__ == "__main__":
    sys.exit(main())
