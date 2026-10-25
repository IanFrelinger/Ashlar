#!/usr/bin/env python3
"""Grade every answer against the rubric (truth.py). No model is involved in grading.

Graded answers:
  brick-raw     the brick's own payload, its blocker sentences mapped to rubric kinds by fixed patterns
  brick+sonnet  Sonnet with the brick as its only tool (run.py, arm "brick")
  native+sonnet Sonnet with the raw REST API as its only tool (run.py, arm "native")

Usage: score.py <results-dir>   writes score.json and per-pr.md there and prints the summary.
"""
from __future__ import annotations

import collections
import json
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from fixtures import GitHubFixtures, slug  # noqa: E402
from truth import REST_INVISIBLE, classify_claim, ground_truth, rest_required_checks  # noqa: E402

KINDS = {"draft", "conflict", "behind", "changes_requested", "review_required", "check_failing", "check_pending", "other"}

BRICK_PATTERNS = [
    (re.compile(r"^The pull request is a draft\."), lambda m: "draft"),
    (re.compile(r"^The branch has merge conflicts"), lambda m: "conflict"),
    (re.compile(r"^The branch is behind "), lambda m: "behind"),
    (re.compile(r" requested changes\.$"), lambda m: "changes_requested"),
    (re.compile(r"required approving reviews\.$"), lambda m: "review_required"),
    (re.compile(r"^The check (.+) is still "), lambda m: f"check_pending:{m.group(1).lower()}"),
    (re.compile(r"^The check (.+) concluded "), lambda m: f"check_failing:{m.group(1).lower()}"),
    (re.compile(r"^The status (.+) is still pending\."), lambda m: f"check_pending:{m.group(1).lower()}"),
    (re.compile(r"^The status (.+?) reported "), lambda m: f"check_failing:{m.group(1).lower()}"),
    (re.compile(r"^The required check (.+) has not reported "), lambda m: f"check_pending:{m.group(1).lower()}"),
    (re.compile(r"blocked by a branch rule"), lambda m: "other"),
    (re.compile(r"^The pull request is closed without being merged\."), lambda m: None),
]


def brick_raw(payload: dict) -> tuple[str, set[str], list[str]]:
    out = payload["outputs"]
    keys, unmapped = set(), []
    for b in out.get("blockers") or []:
        for pattern, key in BRICK_PATTERNS:
            m = pattern.search(b["reason"])
            if m:
                k = key(m)
                if k:
                    keys.add(k)
                break
        else:
            unmapped.append(b["reason"])
    return out["verdict"], keys, unmapped


def llm_keys(answer: dict | None) -> tuple[str | None, set[str]]:
    if not answer:
        return None, set()
    keys = set()
    for b in answer.get("blockers") or []:
        kind = str(b.get("kind", "")).strip().lower()
        if kind not in KINDS:
            kind = "other"
        if kind in ("check_failing", "check_pending"):
            keys.add(f"{kind}:{str(b.get('check') or '').strip().lower()}")
        else:
            keys.add(kind)
    return str(answer.get("verdict", "")).strip().lower() or None, keys


def grade(g: dict, verdict: str | None, claims: set[str], required: set[str]) -> dict:
    tv, tb = ground_truth(g, required)
    terminal = tv in ("merged", "closed")
    claims = set() if terminal else claims
    found = claims & tb
    missed = tb - claims
    extra = {}
    for c in claims - tb:
        if c == "other" and (tb & (REST_INVISIBLE | {"other"})):
            extra[c] = "unattributed"
        else:
            extra[c] = classify_claim(g, c, tb)
    return {"truth_verdict": tv, "truth_blockers": sorted(tb), "verdict": verdict, "verdict_ok": verdict == tv,
            "found": sorted(found), "missed": sorted(missed), "extra": extra}


def main(argv: list[str]) -> int:
    results = pathlib.Path(argv[0])
    fx = HERE / "fixtures"
    targets = json.loads((HERE / "targets.json").read_text())["prs"]
    truth = {t["url"]: json.loads((fx / "truth" / f"{slug(t['url'])}.json").read_text())["graphql"] for t in targets}
    runs = {}
    rf = results / "runs.jsonl"
    if rf.exists():
        for r in map(json.loads, rf.read_text().splitlines()):
            runs[(r["arm"], r["url"])] = r
    gh = GitHubFixtures(fx / "github", allow_live=False)
    required = {}
    for t in targets:
        owner, repo = t["url"].removeprefix("https://github.com/").split("/")[:2]
        base = truth[t["url"]]["baseRefName"]
        required[t["url"]] = rest_required_checks(gh.get(f"/repos/{owner}/{repo}/branches/{base}")[0],
                                                  gh.get(f"/repos/{owner}/{repo}/rules/branches/{base}")[0])
    paired = [t["url"] for t in targets if ("brick", t["url"]) in runs and ("native", t["url"]) in runs]

    graded = collections.defaultdict(dict)
    unmapped_all = {}
    for t in targets:
        url = t["url"]
        v, keys, unmapped = brick_raw(json.loads((fx / "brick" / f"{slug(url)}.json").read_text()))
        if unmapped:
            unmapped_all[url] = unmapped
        graded["brick-raw"][url] = grade(truth[url], v, keys, required[url])
    for url in paired:
        for arm, label in (("brick", "brick+sonnet"), ("native", "native+sonnet")):
            r = runs[(arm, url)]
            v, keys = llm_keys(r["answer"])
            gr = grade(truth[url], v, keys, required[url])
            gr.update({"parsed": r["answer"] is not None, "cost_usd": r["cost_usd"], "live_paths": r["live_paths"]})
            graded[label][url] = gr

    def summarize(label: str, urls: list[str]) -> dict:
        rows = [graded[label][u] for u in urls]
        tb = sum(len(r["truth_blockers"]) for r in rows)
        tb_vis = sum(len([b for b in r["truth_blockers"] if b not in REST_INVISIBLE]) for r in rows)
        found = sum(len(r["found"]) for r in rows)
        found_vis = sum(len([b for b in r["found"] if b not in REST_INVISIBLE]) for r in rows)
        extra = collections.Counter(c for r in rows for c in r["extra"].values())
        claims = found + sum(extra.values())
        return {"prs": len(rows), "verdict_correct": sum(r["verdict_ok"] for r in rows),
                "truth_blockers": tb, "found": found, "truth_blockers_rest_visible": tb_vis, "found_rest_visible": found_vis,
                "claims": claims, "overcall": extra["overcall"], "fabricated": extra["fabricated"],
                "unsupported": extra["unsupported"], "unattributed": extra["unattributed"],
                "parse_failures": sum(1 for r in rows if r.get("parsed") is False),
                "cost_usd": round(sum(r.get("cost_usd", 0) for r in rows), 4),
                "runs_with_live_fetch": sum(1 for r in rows if r.get("live_paths"))}

    summary = {"all_prs": len(targets), "paired_prs": len(paired),
               "brick-raw (all PRs)": summarize("brick-raw", [t["url"] for t in targets]),
               "brick-raw (paired PRs)": summarize("brick-raw", paired),
               "brick+sonnet": summarize("brick+sonnet", paired),
               "native+sonnet": summarize("native+sonnet", paired),
               "brick_unmapped_reasons": unmapped_all}
    (results / "score.json").write_text(json.dumps({"summary": summary, "graded": graded}, indent=1))

    lines = ["| PR | Truth | brick-raw | brick+sonnet | native+sonnet |", "|---|---|---|---|---|"]

    def cell(gr):
        if not gr:
            return "—"
        mark = "✓" if gr["verdict_ok"] else "✗"
        bits = [f"{mark} {gr['verdict']}"]
        if gr["missed"]:
            bits.append("missed " + ", ".join(gr["missed"]))
        if gr["extra"]:
            bits.append("; ".join(f"{k} ({v})" for k, v in sorted(gr["extra"].items())))
        return "<br>".join(bits)

    for t in targets:
        u = t["url"]
        b = graded["brick-raw"][u]
        lines.append(f"| [{u.removeprefix('https://github.com/')}]({u}) | {b['truth_verdict']}: "
                     f"{', '.join(b['truth_blockers']) or '—'} | {cell(b)} | {cell(graded['brick+sonnet'].get(u))} "
                     f"| {cell(graded['native+sonnet'].get(u))} |")
    (results / "per-pr.md").write_text("\n".join(lines) + "\n")
    print(json.dumps(summary, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
