#!/usr/bin/env python3
"""Bounded static CI workers: advisory candidates, never automatic fixes."""
import argparse
from collections import Counter, defaultdict
import hashlib
import html
import json
from pathlib import Path
import re
import subprocess
from security_rules import findings as security_findings, _csharp_code
from cost_rules import findings as cost_findings

MAX_FILE_BYTES = 1024 * 1024
MAX_TOTAL_BYTES = 64 * 1024 * 1024
MAX_FILES = 4000
MAX_FINDINGS = 100
WINDOW = 24


def git(repo, *args):
    return subprocess.check_output(["git", "-C", str(repo), *args], timeout=60)


def commit(repo, sha):
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("an immutable 40-character commit SHA is required")
    if git(repo, "rev-parse", "--verify", sha + "^{commit}").decode().strip() != sha:
        raise ValueError("commit did not resolve exactly")


def eligible(path, kind):
    workflow = path.startswith(".github/workflows/") and path.endswith((".yml", ".yaml"))
    if kind == "cost":
        return workflow
    if any(p in {"bin", "obj", "vendor", "node_modules", "generated"} or "Tests" in p for p in path.split("/")):
        return False
    if path.endswith((".g.cs", ".generated.cs", ".Designer.cs")):
        return False
    return ((path.startswith(("src/", "application/src/", "samples/templates/brick/")) and path.endswith(".cs"))
            or (kind == "security" and workflow))


def sources(repo, sha, kind):
    result, omitted, size = {}, [], 0
    for entry in git(repo, "ls-tree", "-rz", "--full-tree", sha).split(b"\0"):
        if not entry:
            continue
        metadata, raw_path = entry.split(b"\t", 1)
        path = raw_path.decode("utf-8", errors="strict")
        if not eligible(path, kind):
            continue
        mode, object_type, blob = metadata.decode().split()
        if any(ord(c) < 32 for c in path) or mode not in {"100644", "100755"} or object_type != "blob":
            omitted.append({"path": path, "reason": "not a safe regular source file"})
            continue
        length = int(git(repo, "cat-file", "-s", blob))
        if length > MAX_FILE_BYTES or size + length > MAX_TOTAL_BYTES or len(result) >= MAX_FILES:
            omitted.append({"path": path, "reason": "scan budget exceeded"})
            continue
        data = git(repo, "cat-file", "blob", blob)
        size += len(data)
        try:
            result[path] = data.decode("utf-8-sig", errors="strict")
        except UnicodeDecodeError:
            omitted.append({"path": path, "reason": "source is not UTF-8"})
    return result, omitted, size


def finding(rule, path, line, message, action, evidence, confidence="candidate"):
    identity = json.dumps([rule, path, evidence], ensure_ascii=True, separators=(",", ":"))
    return {"rule": rule, "path": path, "line": line, "message": message,
            "next_action": action, "confidence": confidence,
            "fingerprint": hashlib.sha256(identity.encode()).hexdigest()}


def redundancy(files):
    windows = defaultdict(list)
    for path, text in sorted(files.items()):
        lines = [(n, line.strip()) for n, line in enumerate(text.splitlines(), 1) if line.strip()]
        for start in range(max(0, len(lines) - WINDOW + 1)):
            selected = lines[start:start + WINDOW]
            substantive = [line for _, line in selected if not line.startswith(("//", "/*", "*", "using ", "namespace ")) and line not in {"{", "}", "};"}]
            if len(substantive) < 12:
                continue
            digest = hashlib.sha256("\n".join(line for _, line in selected).encode()).hexdigest()
            windows[digest].append((path, selected[0][0], selected[-1][0]))
    result, covered = [], defaultdict(list)
    for digest, locations in sorted(windows.items(), key=lambda item: item[1]):
        if len({path for path, _, _ in locations}) < 2:
            continue
        if any(any(a <= y and b >= x for x, y in covered[p]) for p, a, b in locations):
            continue
        locations = sorted(set(locations))
        for path, start, end in locations:
            covered[path].append((start, end))
        first, *others = locations
        row = finding("duplicate-source-window", first[0], first[1],
                      f"The same {WINDOW} nonblank source lines occur in multiple production files.",
                      "Compare ownership and independent evolution; consolidate only if semantics allow it. Run affected tests before changing either copy.",
                      [digest, sorted({path for path, _, _ in locations})])
        row["related_locations"] = [{"path": p, "line": a, "end_line": b} for p, a, b in others[:9]]
        row["locations_omitted"] = max(0, len(others) - 9)
        result.append(row)
    return result


def efficiency(files):
    rules = [
        ("sync-over-async", r"\.GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(",
         "Synchronous waiting on an asynchronous operation can block a worker thread.",
         "Inspect the caller/context; use await where possible and measure thread-pool pressure before calling this a performance defect."),
        ("repeated-materialization", r"\.(?:ToList|ToArray)\s*\(\s*\)\s*\.(?:ToList|ToArray)\s*\(\s*\)",
         "Adjacent collection materializations may allocate an avoidable copy.",
         "Check whether the second copy provides required ownership/isolation; benchmark allocation and preserve behavior if removing it."),
    ]
    result = []
    for path, text in sorted(files.items()):
        code = _csharp_code(text)
        for rule, pattern, message, action in rules:
            for match in re.finditer(pattern, code):
                line = text.count("\n", 0, match.start()) + 1
                result.append(finding(rule, path, line, message, action, text.splitlines()[line - 1].strip()))
    return result


def analyze(files, kind):
    if kind == "redundancy":
        return redundancy(files), []
    result, omitted = [], []
    for path, text in sorted(files.items()):
        try:
            if kind == "efficiency":
                result.extend(efficiency({path: text}))
                continue
            for row in (cost_findings if kind == "cost" else security_findings)(text, path):
                context = text.splitlines()[max(0, row["line"] - 2):row["line"] + 1]
                result.append(finding(row["rule"], path, row["line"], row["message"], row["next_action"],
                                      [line.strip() for line in context], row["confidence"]))
        except ValueError:
            omitted.append({"path": path, "reason": "parser could not safely inspect this file"})
    return result, omitted


def review(repo, head, base, kind):
    commit(repo, head)
    files, omitted, size = sources(repo, head, kind)
    rows, parse_omitted = analyze(files, kind)
    omitted.extend(parse_omitted)
    old_fingerprints = Counter()
    if base:
        commit(repo, base)
        old_files, old_omitted, _ = sources(repo, base, kind)
        old_rows, old_parse_omitted = analyze(old_files, kind)
        omitted.extend({**row, "revision": "base"} for row in old_omitted + old_parse_omitted)
        old_fingerprints = Counter(row["fingerprint"] for row in old_rows)
    fresh = []
    for row in sorted(rows, key=lambda r: (r["path"], r["line"], r["rule"])):
        if old_fingerprints[row["fingerprint"]]:
            old_fingerprints[row["fingerprint"]] -= 1
        else:
            fresh.append(row)
    rows = fresh
    truncated = len(rows) > MAX_FINDINGS
    tool_root = Path(__file__).resolve().parent
    tool_digest = hashlib.sha256(b"".join((tool_root / name).read_bytes() for name in ("review.py", "security_rules.py", "cost_rules.py"))).hexdigest()
    return {"schema": 1, "worker": kind, "engine": "deterministic-static-review", "tool_sha256": tool_digest,
            "source_sha": head, "base_sha": base, "scope": "new candidates" if base else "full snapshot",
            "status": "incomplete" if omitted or truncated else "completed",
            "files_scanned": len(files), "bytes_scanned": size, "candidates_found": len(rows),
            "findings": rows[:MAX_FINDINGS], "omitted": omitted, "findings_truncated": truncated,
            "limits": {"files": MAX_FILES, "file_bytes": MAX_FILE_BYTES, "total_bytes": MAX_TOTAL_BYTES, "findings": MAX_FINDINGS},
            "limitations": ["Advisory static candidates, not proof of unnecessary code, measured inefficiency, or exploitability.",
                            "Duplicate windows are textual; lexical efficiency/security rules miss aliases, interpolation bodies, reflection, data flow and runtime behavior.",
                            "Generated code, test projects and non-C# product languages are outside the source review scope.",
                            "No model calls, source execution, fixes, approvals or repository writes."]}


def markdown(report):
    safe = lambda value: re.sub(r"([\\`*_{}\[\]()#+.!|>-])", r"\\\1", html.escape(str(value), quote=True))
    lines = [f"## {safe(report['worker'])} review — {safe(report['status'])}", "",
             f"Source: `{report['source_sha']}` · {report['files_scanned']} files · {report['candidates_found']} candidates ({report['scope']}).", "",
             "Deterministic advisory worker; absence of findings is not security or performance certification.", ""]
    for row in report["findings"]:
        lines.extend([f"- **{safe(row['rule'])}** — {safe(row['path'])}:{row['line']}",
                      f"  {safe(row['message'])} {safe(row['next_action'])}"])
    if report["omitted"] or report["findings_truncated"]:
        lines.extend(["", "**Incomplete:** see JSON for omitted inputs or truncated findings. Do not interpret this run as clean."])
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--head", required=True)
    parser.add_argument("--base")
    parser.add_argument("--worker", choices=("redundancy", "efficiency", "security", "cost"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = review(args.repo, args.head, args.base, args.worker)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    (args.output / "summary.md").write_text(markdown(report))
    print(f"{args.worker}: {report['status']}; {report['files_scanned']} files; {report['candidates_found']} candidates")
    return 2 if report["status"] == "incomplete" else 0


if __name__ == "__main__":
    raise SystemExit(main())
