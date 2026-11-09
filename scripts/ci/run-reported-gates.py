#!/usr/bin/env python3
"""Run repository gates with deadlines and retained, fail-closed diagnostics."""
import argparse
import hashlib
import html
import json
import math
import os
from pathlib import Path
import signal
import subprocess
import sys
import time


def write_report(directory, rows):
    """Replace atomically so interruption cannot turn a partial run into a pass."""
    report = {"schema": 1, "status": "passed" if rows and all(r["status"] == "passed" for r in rows) else "incomplete",
              "checks": rows}
    if any(r["status"] in {"failed", "timed_out", "error", "interrupted"} for r in rows):
        report["status"] = "failed"
    temporary = directory / "report.json.tmp"
    temporary.write_text(json.dumps(report, indent=2) + "\n")
    temporary.replace(directory / "report.json")
    lines = ["## Repository gate diagnostics", "", f"Overall: **{report['status']}**", "",
             "Download the `repository-gate-diagnostics` artifact for logs and exact reproduction commands.", ""]
    for row in rows:
        name = html.escape(row["gate"]).replace("`", "&#96;")
        lines.append(f"- `{name}`: **{row['status']}** ({row.get('elapsed_seconds', 0):.2f}s). {row.get('reason', '')}")
    lines += ["", "A running or not-run check is missing evidence, never a pass. A timeout needs investigation, not an automatic retry."]
    (directory / "summary.md").write_text("\n".join(lines) + "\n")


def stop_process(process):
    """Stop descendants as well as a stuck shell/test process on CI's POSIX runner."""
    if os.name == "posix":
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        try:
            process.wait(timeout=1)
        except subprocess.TimeoutExpired:
            pass
        # The leader may exit while a descendant ignores TERM. Still kill its group.
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    else:
        process.kill()
    process.wait(timeout=5)


def run_one(row, directory, timeout):
    started = time.monotonic()
    row.update(status="running", timeout_seconds=timeout)
    process = None
    try:
        # File-backed output also survives an interrupted parent and cannot fill a pipe.
        with (directory / row["log"]).open("wb") as output:
            environment = {**os.environ, "PYTHONUNBUFFERED": "1", "PYTHONDONTWRITEBYTECODE": "1"}
            process = subprocess.Popen(row["command"], stdout=output, stderr=subprocess.STDOUT,
                                       start_new_session=os.name == "posix", env=environment)
            with (directory / row["log"]).open("rb") as stream:
                heartbeat = started + 15
                group_stopped = False
                while True:
                    chunk = stream.read(65536)
                    if chunk:
                        sys.stdout.write(chunk.decode("utf-8", errors="replace"))
                        sys.stdout.flush()
                    code = process.poll()
                    now = time.monotonic()
                    if now - started >= timeout and (code is None or chunk):
                        stop_process(process)
                        row.update(status="timed_out", exit_code=124,
                                   reason="Deadline exceeded; inspect the last active test in the retained log and reproduce the command in report.json.")
                        break
                    if code is not None and not group_stopped:
                        # An exited shell can leave background writers alive. Stop those
                        # before draining, so output cannot outlive the command deadline.
                        stop_process(process)
                        group_stopped = True
                    if code is not None and not chunk:
                        row.update(status="passed" if code == 0 else "failed", exit_code=code,
                                   reason="" if code == 0 else "Check failed; inspect the retained log and reproduce the command in report.json.")
                        break
                    if now >= heartbeat:
                        print(f"repo-gates: still running {row['gate']} ({now - started:.0f}s; limit {timeout:.0f}s)", flush=True)
                        heartbeat = now + 15
                    if not chunk:
                        time.sleep(0.01)
    except (KeyboardInterrupt, SystemExit):
        if process is not None:
            stop_process(process)
        row.update(status="interrupted", exit_code=130, reason="Runner interrupted; this check did not complete. Inspect its partial log.")
        raise
    except OSError as error:
        if process is not None:
            stop_process(process)
        row.update(status="error", exit_code=127, reason=f"Could not run check: {error}")
    finally:
        row["elapsed_seconds"] = round(time.monotonic() - started, 3)
    return row["exit_code"]


def positive(value):
    result = float(value)
    if not math.isfinite(result) or result <= 0:
        raise argparse.ArgumentTypeError("must be a positive finite number of seconds")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reports", type=Path, required=True)
    parser.add_argument("--timeout", type=positive, default=60)
    parser.add_argument("--budget", type=positive, default=180)
    parser.add_argument("gates", nargs="+")
    args = parser.parse_args()
    args.reports.mkdir(parents=True, exist_ok=True)
    rows = [{"gate": gate, "command": [sys.executable if gate.endswith(".py") else "bash", gate],
             "status": "not_run", "reason": "Awaiting execution; no passing evidence.",
             "log": hashlib.sha256(gate.encode()).hexdigest()[:16] + ".log"} for gate in args.gates]
    started = time.monotonic()
    write_report(args.reports, rows)
    print(f"repo-gates: diagnostics at {args.reports}", flush=True)
    # GitHub sends TERM on cancellation. Persist the interrupted state while possible;
    # an uncatchable kill still leaves the prewritten running receipt and partial log.
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(130))
    interrupted = False
    try:
        for row in rows:
            remaining = args.budget - (time.monotonic() - started)
            if remaining <= 0:
                row["reason"] = "Overall run budget exhausted; this check was not executed. Reproduce the command in report.json."
                print(f"::error::{row['gate']} not run (overall deadline exceeded)", flush=True)
                write_report(args.reports, rows)
                continue
            print(f"\n::group::{row['gate']}", flush=True)
            row.update(status="running", reason="Check started but has not yet completed.")
            write_report(args.reports, rows)
            try:
                code = run_one(row, args.reports, min(args.timeout, remaining))
            finally:
                write_report(args.reports, rows)
                print("::endgroup::", flush=True)
            if code:
                print(f"::error::{row['gate']} failed (exit {code}); {row['status']}; log: {row['log']}", flush=True)
    except (KeyboardInterrupt, SystemExit):
        interrupted = True
    finally:
        write_report(args.reports, rows)
    failed = [row for row in rows if row["status"] != "passed"]
    if failed:
        print(f"repo-gates: {len(failed)} of {len(rows)} gate(s) failed or did not complete:")
        for row in failed:
            print(f"  {row['gate']}: {row['status']}")
        return 130 if interrupted else 1
    print(f"repo-gates: all {len(rows)} gate(s) passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
