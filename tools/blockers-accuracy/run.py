#!/usr/bin/env python3
"""Ask Sonnet "what is blocking this PR?" in two arms, from the same recorded snapshot.

  brick   one tool, github_blockers(url), answered with the recorded brick payload (fixtures/brick)
  native  one tool, github_api_get(path), answered with recorded REST responses (fixtures/github);
          a path not recorded is fetched live once, recorded, and flagged in the run record

Both arms get the same system prompt and must end with a JSON answer in a fixed schema, which score.py
grades against truth.py. One run per PR per arm; PRs are taken in round-robin order over their selection
category, brick before native, so a budget stop leaves a stratified, paired sample.

Usage: ANTHROPIC_API_KEY=... [GITHUB_TOKEN=...] run.py --model M --price-in 2 --price-out 10 \
         --cache-write-mult 1.25 --cache-read-mult 0.05 --budget-usd 6 --out results/<date>
The key is read from the environment, sent only as a request header, and masked in any error printed.
"""
from __future__ import annotations

import argparse
import collections
import datetime
import json
import os
import pathlib
import re
import sys
import time
import urllib.error
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from fixtures import GitHubFixtures, slug  # noqa: E402

API = "https://api.anthropic.com/v1/messages"

SYSTEM = """You are a release-engineering agent. Answer: what is blocking this pull request from merging?
Read GitHub with the tool you have. Report only blockers the data shows; do not guess. A check that fails but
is not required does not block a merge.

End your reply with exactly one fenced JSON block in this schema and nothing after it:
```json
{"verdict": "blocked|ready|unknown|merged|closed",
 "blockers": [{"kind": "draft|conflict|behind|changes_requested|review_required|check_failing|check_pending|other",
               "check": "<check or status name, for check_failing and check_pending only>",
               "evidence": "<URL>"}]}
```
Use "other" for a blocker that fits no other kind. For a merged or closed pull request, use an empty list."""

TOOLS = {
    "native": [{
        "name": "github_api_get",
        "description": (
            "GET a GitHub REST API path (https://api.github.com<path>) and return the response body as "
            "received, or the status code and body on an error. Example paths: "
            "/repos/{owner}/{repo}/pulls/{number}, /repos/{owner}/{repo}/pulls/{number}/reviews, "
            "/repos/{owner}/{repo}/commits/{sha}/check-runs, /repos/{owner}/{repo}/commits/{sha}/status, "
            "/repos/{owner}/{repo}/branches/{branch}/protection, /repos/{owner}/{repo}/rules/branches/{branch}."
        ),
        "input_schema": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]},
    }],
    "brick": [{
        "name": "github_blockers",
        "description": (
            "What is blocking a pull request or release: a verdict (blocked, ready, unknown, merged, closed), "
            "each blocker with an evidence link, and every fact that could not be read, with the reason. "
            "Input: url, https://github.com/<owner>/<repo>/pull/<n>."
        ),
        "input_schema": {"type": "object", "properties": {"url": {"type": "string"}}, "required": ["url"]},
    }],
}


def mask(text: str) -> str:
    key = os.environ.get("ANTHROPIC_API_KEY", "")
    return text.replace(key, "***") if key else text


class Budget(Exception):
    pass


class Client:
    def __init__(self, a):
        self.a, self.spent = a, 0.0
        self.headers = {"x-api-key": os.environ["ANTHROPIC_API_KEY"], "anthropic-version": "2023-06-01",
                        "content-type": "application/json"}
        if os.environ.get("ANTHROPIC_WORKSPACE_ID"):
            self.headers["anthropic-workspace-id"] = os.environ["ANTHROPIC_WORKSPACE_ID"]

    def cost(self, u: dict) -> float:
        a = self.a
        return (u.get("input_tokens", 0) * a.price_in
                + u.get("cache_creation_input_tokens", 0) * a.price_in * a.cache_write_mult
                + u.get("cache_read_input_tokens", 0) * a.price_in * a.cache_read_mult
                + u.get("output_tokens", 0) * a.price_out) / 1e6

    def create(self, body: dict) -> dict:
        if self.spent >= self.a.budget_usd:
            raise Budget(f"budget ${self.a.budget_usd:.2f} reached (spent ${self.spent:.4f})")
        data = json.dumps({"model": self.a.model, **body}).encode()
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
                    time.sleep(10 * (attempt + 1))
                    continue
                raise SystemExit(f"Anthropic API HTTP {e.code}: {text}")
        raise SystemExit("Anthropic API: retries exhausted")


def with_cache_breakpoint(messages: list) -> list:
    out = json.loads(json.dumps(messages))
    for m in out:
        for block in m["content"] if isinstance(m["content"], list) else []:
            block.pop("cache_control", None)
    last = out[-1]
    if isinstance(last["content"], str):
        last["content"] = [{"type": "text", "text": last["content"]}]
    last["content"][-1]["cache_control"] = {"type": "ephemeral"}
    return out


def parse_answer(text: str) -> dict | None:
    blocks = re.findall(r"```json\s*(\{.*?\})\s*```", text, re.S)
    if not blocks:
        return None
    try:
        return json.loads(blocks[-1])
    except json.JSONDecodeError:
        return None


def run_once(client: Client, gh: GitHubFixtures, brick_dir: pathlib.Path, arm: str, url: str, max_turns: int) -> dict:
    messages = [{"role": "user", "content": f"What is blocking this pull request from merging? {url}"}]
    usage = collections.Counter()
    tool_calls, requests, cost, live = 0, 0, 0.0, []
    final_text, stop = "", None
    started = time.monotonic()
    for _ in range(max_turns):
        resp = client.create({
            "max_tokens": 2048,
            "system": [{"type": "text", "text": SYSTEM, "cache_control": {"type": "ephemeral"}}],
            "tools": TOOLS[arm],
            "messages": with_cache_breakpoint(messages),
        })
        requests += 1
        for k, v in (resp.get("usage") or {}).items():
            if isinstance(v, int):
                usage[k] += v
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
                out, was_live = gh.get(str(u["input"].get("path", "")))
                if was_live:
                    live.append(u["input"].get("path"))
            elif u["name"] == "github_blockers":
                f = brick_dir / f"{slug(str(u['input'].get('url', '')))}.json"
                out = (json.dumps(json.loads(f.read_text()), separators=(",", ":")) if f.exists()
                       else json.dumps({"error": "no recorded answer for that url"}))
            else:
                out = f"unknown tool {u['name']}"
            results.append({"type": "tool_result", "tool_use_id": u["id"], "content": out})
        messages.append({"role": "user", "content": results})
    else:
        stop = "max_turns"
    return {"arm": arm, "url": url, "requests": requests, "tool_calls": tool_calls, "usage": dict(usage),
            "cost_usd": round(cost, 6), "seconds": round(time.monotonic() - started, 1), "stop": stop,
            "live_paths": live, "answer": parse_answer(final_text), "final_text": final_text}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--price-in", type=float, required=True)
    ap.add_argument("--price-out", type=float, required=True)
    ap.add_argument("--cache-write-mult", type=float, default=1.25)
    ap.add_argument("--cache-read-mult", type=float, default=0.10)
    ap.add_argument("--budget-usd", type=float, default=6.0)
    ap.add_argument("--max-turns", type=int, default=12)
    ap.add_argument("--fixtures", default=str(HERE / "fixtures"))
    ap.add_argument("--out", required=True)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--arms", default="brick,native", help="comma-separated: brick, native")
    ap.add_argument("--brick-dir", default="brick", help="directory under --fixtures holding the brick payloads")
    a = ap.parse_args()
    if not os.environ.get("ANTHROPIC_API_KEY"):
        print("ANTHROPIC_API_KEY is not set", file=sys.stderr)
        return 2

    fx = pathlib.Path(a.fixtures)
    gh = GitHubFixtures(fx / "github", os.environ.get("GITHUB_TOKEN", ""))
    targets = json.loads((HERE / "targets.json").read_text())["prs"]
    by_cat = collections.defaultdict(list)
    for t in targets:
        by_cat[t["selected_as"]].append(t["url"])
    order = []
    while any(by_cat.values()):
        for cat in sorted(by_cat):
            if by_cat[cat]:
                order.append(by_cat[cat].pop(0))
    if a.limit:
        order = order[:a.limit]

    out = pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    done = set()
    runs_file = out / "runs.jsonl"
    if runs_file.exists():
        done = {(r["arm"], r["url"]) for r in map(json.loads, runs_file.read_text().splitlines())}
    client = Client(a)
    stopped = None
    with open(runs_file, "a", encoding="utf-8") as log:
        for url in order:
            for arm in a.arms.split(","):
                if (arm, url) in done:
                    continue
                try:
                    r = run_once(client, gh, fx / a.brick_dir, arm, url, a.max_turns)
                except Budget as e:
                    stopped = str(e)
                    break
                log.write(json.dumps(r) + "\n")
                log.flush()
                verdict = (r["answer"] or {}).get("verdict")
                print(f"{arm:<6} {url.removeprefix('https://github.com/'):<45} {verdict!s:<8} "
                      f"{r['requests']} req ${r['cost_usd']:.4f} live={len(r['live_paths'])} (spent ${client.spent:.3f})",
                      flush=True)
            if stopped:
                break
    meta = {"model": a.model, "price_in": a.price_in, "price_out": a.price_out,
            "cache_write_mult": a.cache_write_mult, "cache_read_mult": a.cache_read_mult,
            "spent_this_invocation": round(client.spent, 4), "stopped": stopped,
            "finished_at": datetime.datetime.now(datetime.timezone.utc).isoformat()}
    with open(out / "meta.jsonl", "a", encoding="utf-8") as m:
        m.write(json.dumps(meta) + "\n")
    print(json.dumps(meta))
    return 0


if __name__ == "__main__":
    sys.exit(main())
