#!/usr/bin/env python3
"""Turn recorded github.blockers answers into short human summaries with one or more Anthropic models.

Usage:
  ANTHROPIC_API_KEY=... dogfood_summarize.py --brick DIR --out DIR --budget-usd 3 \
      --model claude-haiku-5-5:0.10:0.50 --model claude-sonnet-5-5:2:10

DIR holds the <slug>.json payloads capture_brick.py writes. Each model gets the same system prompt and the
brick's `outputs` object, once per payload. Every summary is checked mechanically: it must state the brick's
verdict word, and every URL in it must be one the brick returned. Writes summaries.jsonl and meta.json.
The key is read from the environment, sent only in the request header, and never printed or written.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import pathlib
import re
import sys

from run import Budget, Client

SYSTEM = (
    "You turn a tool's JSON answer about a GitHub pull request or release into a summary for a busy engineer. "
    "Write 2 to 4 plain sentences. Use only facts in the JSON; add nothing. State the verdict word exactly as "
    "the JSON gives it (blocked, ready, unknown, merged or closed). Name each blocker in a few words. If something "
    "is unknown, say what and what to do next. Cite at most two evidence URLs, copied exactly from the JSON. "
    "No headings, no bullet points, no preamble."
)
URL = re.compile(r"https://[^\s)>\]\"'`]+")


def check(summary: str, outputs: dict) -> dict:
    allowed = set(outputs.get("evidence", []))
    allowed |= {b["evidence"] for b in outputs.get("blockers", []) if b.get("evidence")}
    allowed |= {n["evidence"] for n in outputs.get("notes", []) if n.get("evidence")}
    urls = [u.rstrip(".,;:") for u in URL.findall(summary)]
    return {
        "states_verdict": re.search(rf"\b{re.escape(outputs['verdict'])}\b", summary, re.IGNORECASE) is not None,
        "urls": urls,
        "invented_urls": [u for u in urls if u not in allowed],
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--brick", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--model", action="append", required=True, help="id:price_in:price_out (USD per million)")
    ap.add_argument("--budget-usd", type=float, default=3.0)
    ap.add_argument("--max-tokens", type=int, default=400)
    a = ap.parse_args()
    if not os.environ.get("ANTHROPIC_API_KEY"):
        print("ANTHROPIC_API_KEY is not set", file=sys.stderr)
        return 2

    payloads = sorted(pathlib.Path(a.brick).glob("*.json"))
    out = pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    spent, stopped, rows = 0.0, None, []
    with open(out / "summaries.jsonl", "w", encoding="utf-8") as log:
        for spec in a.model:
            model, price_in, price_out = spec.split(":")
            client = Client(model, float(price_in), float(price_out), a.budget_usd - spent)
            for f in payloads:
                outputs = json.loads(f.read_text())["outputs"]
                body = {"max_tokens": a.max_tokens, "system": SYSTEM,
                        "messages": [{"role": "user", "content": json.dumps(outputs)}]}
                # Both 5.5 models think by default and can spend the whole max_tokens doing it.
                if model.startswith("claude-sonnet"):
                    body["thinking"] = {"type": "between_tools"}
                elif model.startswith("claude-haiku"):
                    body["thinking"] = {"type": "disabled"}
                try:
                    resp = client.create(body)
                except Budget as e:
                    stopped = str(e)
                    break
                text = "".join(b.get("text", "") for b in resp["content"] if b["type"] == "text").strip()
                row = {"model": model, "target": f.stem, "verdict": outputs["verdict"], "summary": text,
                       "usage": resp.get("usage", {}), "cost_usd": client.cost(resp.get("usage", {})),
                       **check(text, outputs)}
                rows.append(row)
                log.write(json.dumps(row) + "\n")
                print(f"{model:<20} {f.stem:<45} verdict-ok={row['states_verdict']} "
                      f"invented-urls={len(row['invented_urls'])} ${row['cost_usd']:.5f}")
            spent += client.spent
            if stopped:
                break
    meta = {"models": a.model, "spent": spent, "stopped": stopped, "payloads": len(payloads),
            "finished_at": datetime.datetime.now(datetime.timezone.utc).isoformat()}
    (out / "meta.json").write_text(json.dumps(meta, indent=1))
    print(f"spent ${spent:.4f}" + (f"; stopped: {stopped}" if stopped else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
