#!/usr/bin/env python3
"""Run exact mutations through test-in-container.sh, preserving TRX and byte-restore evidence.

Capture complete stdout with --archive-stdout, then use
decode-spec007-mutation-evidence.py on the host. Without that flag --output must
be on an external writable mount. No --no-build or alternate runner is supported.
Fixture tests may import these functions on the host without invoking dotnet.
Run manually: python tests/scripts/spec007-mutation-runner.test.py
If expected_failures is supplied, it is a nonempty list of test-name fragments:
EVERY fragment must match at least one actual failed test (not merely any fragment).
An xUnit RunInfo Error is accepted only for its single-line [FAIL] diagnostic
when the entire test name matches an independently validated Failed result.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import signal
import stat
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET


class InvalidRun(RuntimeError):
    pass


def save_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def read_result(path, returncode, console=""):
    if returncode not in (0, 1):
        raise InvalidRun("test process did not exit normally with 0 or 1")
    if re.search(r"(?im)^.*(?:\):|:)\s*error\s+(?:CS|CA|MSB|NU|NETSDK)\d+\b", console):
        raise InvalidRun("compiler/build error is not an assertion failure")
    root = ET.parse(path).getroot()
    if root.tag.rsplit("}", 1)[-1] != "TestRun":
        raise InvalidRun("not a TRX TestRun")
    summaries = root.findall("./{*}ResultSummary")
    containers = root.findall("./{*}Results")
    if len(summaries) != 1 or len(containers) != 1:
        raise InvalidRun("missing or duplicate result summary/results")
    summary = summaries[0]
    counter_nodes = summary.findall("./{*}Counters")
    if len(counter_nodes) != 1:
        raise InvalidRun("missing or duplicate counters")
    values = {}
    for name, value in counter_nodes[0].attrib.items():
        if not re.fullmatch(r"\d+", value):
            raise InvalidRun("non-numeric or negative counter: " + name)
        values[name] = int(value)
    required = ("total", "executed", "passed", "failed", "notExecuted")
    if any(key not in values for key in required) or values["executed"] == 0:
        raise InvalidRun("missing counters or no executed tests")
    if any(value for key, value in values.items() if key not in (*required, "completed")):
        raise InvalidRun("aborted, errored, pending or incomplete counters")
    if values["executed"] != values["passed"] + values["failed"]:
        raise InvalidRun("executed counter disagrees with passes/failures")
    if values["total"] != values["executed"] + values["notExecuted"]:
        raise InvalidRun("total counter disagrees with executed/skipped tests")
    results = list(containers[0])
    if len(results) != values["total"]:
        raise InvalidRun("individual result total disagrees with counters")
    observed = {"Passed": 0, "Failed": 0, "NotExecuted": 0}
    executions, failures = set(), []
    for result in results:
        outcome = result.get("outcome")
        name = result.get("testName", "").strip()
        execution = result.get("executionId", "")
        if result.tag.rsplit("}", 1)[-1] != "UnitTestResult" or outcome not in observed:
            raise InvalidRun("unsupported or incomplete individual result")
        if not name or not result.get("testId") or not execution or execution in executions:
            raise InvalidRun("missing test identity or duplicate execution")
        executions.add(execution)
        observed[outcome] += 1
        if outcome == "Failed":
            message = result.find("./{*}Output/{*}ErrorInfo/{*}Message")
            if message is None or not (message.text or "").strip():
                raise InvalidRun("failed result has no assertion/exception message")
            failures.append(name)
    if list(observed.values()) != [values[x] for x in ("passed", "failed", "notExecuted")]:
        raise InvalidRun("individual outcomes disagree with counters")
    if summary.get("outcome") != ("Failed" if failures else "Completed"):
        raise InvalidRun("aborted or inconsistent run summary")
    for info in summary.findall("./{*}RunInfos/{*}RunInfo"):
        if info.get("outcome") in ("Passed", "Completed", "Warning"):
            continue
        # xUnit repeats ordinary assertion failures as Error RunInfos. Allow
        # only that observed diagnostic, including the complete theory case;
        # a crashed/aborted runner or an unrelated error still invalidates it.
        texts = info.findall("./{*}Text")
        diagnostic = re.fullmatch(
            r"\[xUnit\.net [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]+\][ \t]+([^\r\n]+) \[FAIL\]",
            texts[0].text or "") if len(texts) == 1 and len(texts[0]) == 0 else None
        if (info.get("outcome") != "Error" or diagnostic is None
                or diagnostic.group(1) not in failures):
            raise InvalidRun("run-level failure is not a mutation kill")
    if (returncode == 0) != (not failures):
        raise InvalidRun("process exit and test results disagree")
    return {**{key: values[key] for key in required}, "failures": failures, "exit": returncode}


def safe_relative(relative):
    if not isinstance(relative, str) or not relative or "\\" in relative or any(ord(c) < 32 for c in relative):
        raise ValueError("path must be a nonempty relative POSIX path")
    path = PurePosixPath(relative)
    if path.is_absolute() or PureWindowsPath(relative).drive or any(p in ("..", ".git") for p in path.parts):
        raise ValueError("absolute, parent or git-internal paths are forbidden")
    if str(path) != relative:
        raise ValueError("path must be canonical")
    return path


def source_path(repo, relative):
    parts = safe_relative(relative).parts
    repo = repo.resolve()
    path = repo
    for part in parts:
        path = path / part
        if path.is_symlink():
            raise ValueError("symlink sources or path components are forbidden")
    if not path.resolve().is_relative_to(repo) or not path.is_file():
        raise ValueError("source must be a regular file inside the checkout")
    tracked = subprocess.check_output(["git", "--literal-pathspecs", "ls-files", "--stage", "--error-unmatch", "--", relative], cwd=repo, text=True)
    if not re.match(r"100(?:644|755) [0-9a-f]+ 0\t", tracked) or len(tracked.splitlines()) != 1:
        raise ValueError("source must be one regular tracked file")
    return path


def clean(repo):
    status = subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=all"], cwd=repo, text=True)
    if status:
        raise InvalidRun("checkout is not clean: " + status)
    print("git status --porcelain: empty", flush=True)


def run_tests(repo, case, directory, phase, timeout=1800):
    source_path(repo, case["project"])
    result_dir = directory / phase
    result_dir.mkdir(parents=True, exist_ok=False)
    command = ["dotnet", "test", case["project"], "--framework", case["framework"],
               "--filter", case["filter"], "--nologo", "-v", "minimal",
               "--results-directory", str(result_dir), "--logger", "trx;LogFileName=result.trx"]
    save_json(result_dir / "command.json", command)
    print("COMMAND " + json.dumps(command), flush=True)
    process = subprocess.Popen(command, cwd=repo, text=True, encoding="utf-8", errors="replace",
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True)
    timed_out = interrupted = False
    try:
        output, _ = process.communicate(timeout=timeout)
    except (subprocess.TimeoutExpired, KeyboardInterrupt) as error:
        timed_out = isinstance(error, subprocess.TimeoutExpired)
        interrupted = isinstance(error, KeyboardInterrupt)
        if os.name == "posix":
            os.killpg(process.pid, signal.SIGKILL)
        else:
            process.kill()
        output, _ = process.communicate()
    (result_dir / "console.log").write_text(output, encoding="utf-8")
    save_json(result_dir / "process.json", {"exit": process.returncode, "timed_out": timed_out, "interrupted": interrupted})
    print(output, flush=True)
    if timed_out:
        raise InvalidRun("test run timed out")
    if interrupted:
        raise KeyboardInterrupt("test run interrupted after its process group was stopped")
    trx = result_dir / "result.trx"
    if not trx.is_file() or trx.is_symlink():
        raise InvalidRun("no regular TRX: compilation/discovery failure is not a mutation kill")
    counts = read_result(trx, process.returncode, output)
    save_json(result_dir / "counts.json", counts)
    for name in counts["failures"]:
        print("FAILED TEST " + name, flush=True)
    return counts


def restore_source(repo, relative, original, mode):
    # Replace a swapped leaf symlink instead of following it; refuse changed parent symlinks.
    relative = safe_relative(relative)
    parent = repo.resolve()
    for part in relative.parts[:-1]:
        parent = parent / part
        if parent.is_symlink() or not parent.is_dir():
            raise InvalidRun("source parent changed; refusing unsafe restoration")
    if not parent.resolve().is_relative_to(repo.resolve()):
        raise InvalidRun("restoration escaped checkout")
    path = parent / relative.name
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=parent, prefix=".mutation-restore-", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(original)
            stream.flush()
            os.fsync(stream.fileno())
        temporary.chmod(mode)
        os.replace(temporary, path)
        if path.is_symlink() or path.read_bytes() != original:
            raise InvalidRun("restored source bytes differ")
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def run_mutation(repo, sha, case, directory, baselines, timeout=1800):
    directory.mkdir()
    record = {"id": case["id"], "ref": sha, "case": case, "verdict": "INVALID"}
    save_json(directory / "case.json", case)
    applied = False
    try:
        clean(repo)
        path = source_path(repo, case["file"])
        original = path.read_bytes()
        mode = stat.S_IMODE(path.stat().st_mode)
        old, new = case["old"].encode("utf-8"), case["new"].encode("utf-8")
        if not old or old == new or original.count(old) != 1:
            raise InvalidRun("replacement must change exactly one source fragment")
        (directory / "original.bin").write_bytes(original)
        record["original_sha256"] = hashlib.sha256(original).hexdigest()
        selection = tuple(case[key] for key in ("project", "framework", "filter"))
        if selection not in baselines:
            baseline = run_tests(repo, case, directory, "baseline", timeout)
            record["baseline"] = baseline
            if baseline["failed"] or baseline["exit"]:
                raise InvalidRun("baseline is not green")
            baselines[selection] = baseline
        else:
            record["baseline"] = baselines[selection]
        clean(repo)
        try:
            applied = True  # Restore even if the write only partly succeeds.
            path.write_bytes(original.replace(old, new, 1))
            if path.read_bytes() != original.replace(old, new, 1):
                raise InvalidRun("mutation bytes do not match exact replacement")
            diff = subprocess.check_output(["git", "--literal-pathspecs", "diff", "--", case["file"]], cwd=repo)
            (directory / "applied.diff").write_bytes(diff)
            if not diff.strip():
                raise InvalidRun("mutation did not change tracked source")
            print(f"APPLIED {case['id']}\n{diff.decode('utf-8', errors='replace')}", flush=True)
            record["red"] = red = run_tests(repo, case, directory, "red", timeout)
            expected = case.get("expected_failures", [])
            if red["failed"] == 0:
                record["verdict"] = "SURVIVED"
            elif expected and not all(any(fragment in name for name in red["failures"]) for fragment in expected):
                record["reason"] = "not every intended assertion failed"
            else:
                record["verdict"] = "KILLED"
        finally:
            if applied:
                restore_source(repo, case["file"], original, mode)
                record["restored_sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
                clean(repo)
                record["restored_clean"] = True
    except (Exception, KeyboardInterrupt) as error:
        record["verdict"] = "INVALID"
        record["reason"] = f"{type(error).__name__}: {error}"
        record["interrupted"] = isinstance(error, KeyboardInterrupt)
    finally:
        # A survivor/invalid red still needs a fresh green rebuild after restoration.
        if applied and record.get("restored_clean") and not record.get("interrupted"):
            try:
                green = run_tests(repo, case, directory, "green", timeout)
                record["green"] = green
                clean(repo)
                if green["failed"] or green["exit"]:
                    raise InvalidRun("restored source is not green")
                baseline = record["baseline"]
                if any(green[k] != baseline[k] for k in ("total", "executed", "passed", "notExecuted")):
                    raise InvalidRun("restored discovery/counters changed from baseline")
                record["final_clean"] = True
            except (Exception, KeyboardInterrupt) as error:
                record["verdict"] = "INVALID"
                record["green_reason"] = f"{type(error).__name__}: {error}"
                record["interrupted"] = isinstance(error, KeyboardInterrupt)
        save_json(directory / "verdict.json", record)
        red, green = record.get("red"), record.get("green")
        red_text = f"failed:{red['failed']}/{red['total']}" if red else "invalid-or-not-run"
        green_text = f"passed:{green['passed']}/{green['total']}" if green else "invalid-or-not-run"
        print(f"mutation {case['id']}: {record['verdict']} red={red_text} green={green_text} ref={sha} framework={case['framework']}", flush=True)
    return record


def externally_mounted(path):
    mounts = Path("/proc/self/mountinfo")
    if not mounts.is_file():
        return False
    candidates = []
    for line in mounts.read_text().splitlines():
        left, right = line.split(" - ", 1)
        fields = left.split()
        point = Path(re.sub(r"\\([0-7]{3})", lambda m: chr(int(m[1], 8)), fields[4])).resolve()
        if path.is_relative_to(point):
            candidates.append((len(point.parts), point, right.split()[0], fields[5]))
    if not candidates:
        return False
    _, point, kind, options = max(candidates)
    return point != Path("/") and kind not in {"tmpfs", "proc", "sysfs", "devtmpfs", "cgroup", "cgroup2", "overlay"} and "rw" in options.split(",")


def emit_archive(output):
    with tempfile.TemporaryFile() as stream:
        with tarfile.open(fileobj=stream, mode="w:gz") as archive:
            archive.add(output, arcname="evidence", filter=lambda entry: entry if entry.isfile() or entry.isdir() else None)
        stream.seek(0)
        payload = stream.read()
    print(f"BEGIN ASHLAR_MUTATION_EVIDENCE sha256={hashlib.sha256(payload).hexdigest()} bytes={len(payload)}", flush=True)
    print(base64.b64encode(payload).decode("ascii"), flush=True)
    print("END ASHLAR_MUTATION_EVIDENCE", flush=True)


def validate_cases(repo, manifest, selection):
    cases = manifest["mutations"]
    ids = [case["id"] for case in cases]
    if not cases or len(ids) != len(set(ids)) or any(not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", value) for value in ids):
        raise ValueError("manifest must contain unique safe mutation IDs")
    selected = set(selection.split(",")) if selection else set(ids)
    if not selected or not selected.issubset(ids):
        raise ValueError("unknown or empty requested mutation IDs")
    cases = [case for case in cases if case["id"] in selected]
    for case in cases:
        for key in ("file", "project", "old", "new", "filter", "framework"):
            if not isinstance(case[key], str) or (not case[key] and key != "new") or "\0" in case[key]:
                raise ValueError("invalid manifest field: " + key)
        if not re.fullmatch(r"net[0-9]+\.[0-9]+", case["framework"]) or not case["project"].endswith(".csproj"):
            raise ValueError("use an explicit target framework and csproj")
        source_path(repo, case["file"])
        source_path(repo, case["project"])
        expected = case.get("expected_failures", [])
        if not isinstance(expected, list) or ("expected_failures" in case and not expected) or any(not isinstance(x, str) or not x for x in expected):
            raise ValueError("expected_failures must contain nonempty name fragments")
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--ids", help="Comma-separated subset")
    parser.add_argument("--output", help="New directory outside checkout")
    parser.add_argument("--archive-stdout", action="store_true", help="Emit recoverable evidence before container removal")
    parser.add_argument("--timeout-seconds", type=int, default=1800)
    args = parser.parse_args()
    if not Path("/.dockerenv").is_file():
        raise InvalidRun("execute through the devtest container")
    if args.timeout_seconds <= 0:
        raise ValueError("timeout must be positive")
    def interrupted(_signal, _frame):
        raise KeyboardInterrupt("termination requested")
    signal.signal(signal.SIGTERM, interrupted)
    repo = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip()).resolve()
    output = Path(args.output).resolve() if args.output else Path(tempfile.mkdtemp(prefix="ashlar-mutations-"))
    if output.is_relative_to(repo):
        raise ValueError("evidence directory must be outside checkout")
    if not args.archive_stdout and not externally_mounted(output):
        raise ValueError("ephemeral evidence requires --archive-stdout; otherwise use writable external mount")
    if args.output:
        output.mkdir(parents=True, exist_ok=False)
    batch = {"verdict": "INVALID", "records": []}
    code = 2
    try:
        clean(repo)
        sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()
        batch["ref"] = sha
        manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8-sig"))
        save_json(output / "manifest.json", manifest)
        cases = validate_cases(repo, manifest, args.ids)
        print(f"REF {sha}; EVIDENCE {output}; MUTATIONS {len(cases)}", flush=True)
        baselines = {}
        for index, case in enumerate(cases):
            record = run_mutation(repo, sha, case, output / f"{index:03d}-{case['id']}", baselines, args.timeout_seconds)
            batch["records"].append(record)
            save_json(output / "summary.json", batch)
            if not record.get("final_clean") or record.get("interrupted"):
                break
        verdicts = {record["verdict"] for record in batch["records"]}
        batch["verdict"] = "KILLED" if verdicts == {"KILLED"} and len(batch["records"]) == len(cases) else "INVALID" if "INVALID" in verdicts else "SURVIVED"
        code = {"KILLED": 0, "SURVIVED": 1, "INVALID": 2}[batch["verdict"]]
    except (Exception, KeyboardInterrupt) as error:
        batch["reason"] = f"{type(error).__name__}: {error}"
    finally:
        save_json(output / "summary.json", batch)
        if args.archive_stdout:
            emit_archive(output)
    return code


if __name__ == "__main__":
    raise SystemExit(main())
