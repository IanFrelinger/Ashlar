#!/usr/bin/env python3
"""Combine advisory worker receipts into a bounded, issue-ready verification queue."""
import argparse
import html
import json
from pathlib import Path, PurePosixPath
import re

WORKERS = {"redundancy", "efficiency", "security", "cost"}
REQUIRED_GATES = ["cert-gate", "build-core", "shell-lint", "lychee (README + docs)", "Readiness summary"]


def validate_report(report, sha):
    if not isinstance(report, dict) or report.get("schema") != 1 or report.get("source_sha") != sha:
        raise ValueError("report source identity is invalid")
    if report.get("engine") != "deterministic-static-review" or not re.fullmatch(r"[0-9a-f]{64}", report.get("tool_sha256", "")):
        raise ValueError("report engine identity is invalid")
    base = report.get("base_sha")
    if base is not None and not re.fullmatch(r"[0-9a-f]{40}", base):
        raise ValueError("report base identity is invalid")
    if report.get("scope") != ("new candidates" if base else "full snapshot"):
        raise ValueError("report scope disagrees with its base")
    rows, omitted = report.get("findings"), report.get("omitted")
    count, truncated = report.get("candidates_found"), report.get("findings_truncated")
    if (not isinstance(rows, list) or len(rows) > 100 or not isinstance(omitted, list)
            or type(count) is not int or count < len(rows) or type(truncated) is not bool
            or truncated != (count > len(rows))):
        raise ValueError("report counts or collections are invalid")
    expected_status = "incomplete" if omitted or truncated else "completed"
    if report.get("status") != expected_status:
        raise ValueError("report completeness disagrees with evidence")
    for row in rows:
        if not isinstance(row, dict) or not re.fullmatch(r"[0-9a-f]{64}", row.get("fingerprint", "")):
            raise ValueError("finding identity is invalid")
        for key in ("rule", "path", "message", "next_action", "confidence"):
            value = row.get(key)
            if not isinstance(value, str) or not value or len(value) > 4096 or any(ord(c) < 32 for c in value):
                raise ValueError("finding text is invalid")
        path = PurePosixPath(row["path"])
        if path.is_absolute() or ".." in path.parts or "\\" in row["path"] or str(path) != row["path"]:
            raise ValueError("finding path is invalid")
        if type(row.get("line")) is not int or row["line"] < 1:
            raise ValueError("finding line is invalid")
    return (base, report["scope"], report["engine"], report["tool_sha256"])


def coordinate(directory, sha):
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("immutable source SHA required")
    reports, queue, identity = {}, {}, None
    for path in sorted(directory.glob("*/report.json")):
        if path.is_symlink() or path.parent.is_symlink() or not path.is_file() or path.stat().st_size > 1024 * 1024:
            raise ValueError("report is not a bounded regular file")
        report = json.loads(path.read_text())
        current_identity = validate_report(report, sha)
        if identity is not None and current_identity != identity:
            raise ValueError("worker source scopes or tool identities disagree")
        identity = current_identity
        worker = report.get("worker")
        if worker not in WORKERS or worker in reports or report.get("source_sha") != sha or report.get("schema") != 1:
            raise ValueError("worker receipt identity does not match this run")
        reports[worker] = report
        for row in report["findings"]:
            if not re.fullmatch(r"[0-9a-f]{64}", row.get("fingerprint", "")):
                raise ValueError("finding identity is invalid")
            key = row["fingerprint"]
            if key not in queue:
                queue[key] = {**row, "reported_by": [],
                              "priority": "security-triage" if worker == "security" else "maintenance-triage",
                              "route": "security reviewer" if worker == "security" else "cost/reliability reviewer" if worker == "cost" else "maintenance reviewer",
                              "state": "needs-human-validation",
                              "regression_plan": ["Reproduce the claimed defect; if it is intentional, record the reason instead of changing it.",
                                                  "Establish a named failing test or measured benchmark before proposing a fix.",
                                                  "Restore the baseline and prove green; run affected suites and a meaningful mutation check after the fix."],
                              "release_plan": ["Review compatibility, documentation and dependency impact.",
                                               "Require all repository merge gates on the final fix SHA; do not use this advisory report as approval."]}
            queue[key]["reported_by"].append(worker)
            if worker == "security":
                queue[key]["priority"] = "security-triage"
                queue[key]["route"] = "security reviewer"
    missing = sorted(WORKERS - set(reports))
    incomplete = missing + [worker for worker, report in reports.items() if report["status"] != "completed"]
    return {"schema": 1, "source_sha": sha, "base_sha": identity[0] if identity else None,
            "scope": identity[1] if identity else None, "tool_sha256": identity[3] if identity else None,
            "coordinator": "deterministic",
            "status": "incomplete" if incomplete else "completed", "incomplete_workers": incomplete,
            "items": sorted(queue.values(), key=lambda row: (row["priority"] != "security-triage", row["path"], row["line"], row["rule"])),
            "roles": {"coordinator": "Deduplicated advisory candidates and proposed routing; no issues assigned or created.",
                      "regression": "Verification plans only; no product test was written or executed by this coordinator.",
                      "release-readiness": "Not verified here; compatibility/docs/dependencies need review and final-SHA CI evidence.",
                      "dogfooding": "The Distribution Matrix standalone-brick job executes the developer edit/test journey and uploads TRX receipts."},
            "required_merge_gates": REQUIRED_GATES,
            "autonomous_provider": {"enabled": False, "reason": "No operator-approved CI provider credentials or spending policy configured."},
            "authority": "Advisory artifacts only: no source writes, comments, issues, approvals, merges or releases."}


def markdown(report):
    escape = lambda text: re.sub(r"([\\`*_{}\[\]()#+.!|>-])", r"\\\1", html.escape(str(text), quote=True))
    lines = ["## Developer review queue", "", f"Source: `{report['source_sha']}` · {report['status']} · {len(report['items'])} candidates.", "",
             "No issues or fixes were created. Model-driven agents are disabled. Regression/release roles provide verification plans, not approval.", ""]
    for item in report["items"]:
        lines.append(f"- **{escape(item['priority'])}: {escape(item['rule'])}** — {escape(item['path'])}:{item['line']}; route: {escape(item['route'])}.")
        lines.append(f"  {escape(item['next_action'])}")
    if report["incomplete_workers"]:
        lines += ["", "**Incomplete workers:** " + ", ".join(report["incomplete_workers"]) + ". Missing evidence is not a clean result."]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reports", type=Path, required=True)
    parser.add_argument("--head", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = coordinate(args.reports, args.head)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "queue.json").write_text(json.dumps(report, indent=2) + "\n")
    (args.output / "summary.md").write_text(markdown(report))
    print(f"Coordinator: {report['status']}; {len(report['items'])} candidates")
    return 2 if report["status"] == "incomplete" else 0


if __name__ == "__main__":
    raise SystemExit(main())
