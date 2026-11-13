#!/usr/bin/env python3
"""Verify retained ordinary contract mutations; never execute dotnet or the broker."""
import argparse
from collections import Counter
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from types import SimpleNamespace

sys.dont_write_bytecode = True
MANIFEST = "tools/GovernanceBrokerPrototype/mutations.json"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def phase_counts(runner, directory, case):
    process = read_json(directory / "process.json")
    require(process.get("timed_out") is False and process.get("interrupted") is False,
            f"{directory.name}: interrupted or timed out")
    command = read_json(directory / "command.json")
    require(isinstance(command, list) and len(command) == 14,
            f"{directory.name}: missing complete test command")
    expected = ["dotnet", "test", case["project"], "--framework", case["framework"],
                "--filter", case["filter"], "--nologo", "-v", "minimal",
                "--results-directory", command[11], "--logger", "trx;LogFileName=result.trx"]
    require(command == expected and isinstance(command[11], str),
            f"{directory.name}: test command differs from selected contract")
    counts = runner.read_result(directory / "result.trx", process["exit"],
                                (directory / "console.log").read_text(encoding="utf-8"))
    require(counts == read_json(directory / "counts.json"),
            f"{directory.name}: stored counters differ from actual TRX")
    require(counts["notExecuted"] == 0, f"{directory.name}: skipped tests")
    return counts


def load_runner(repo):
    spec = importlib.util.spec_from_file_location(
        "spec007_mutation_runner", repo / "scripts/run-spec007-mutation-batch.py")
    runner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(runner)
    return runner


def verify(evidence, repo, expected_sha):
    require(re.fullmatch(r"[0-9a-f]{40}", expected_sha), "expected full commit SHA")
    require(subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()
            == expected_sha, "checkout differs from expected SHA")
    require(not evidence.is_relative_to(repo), "evidence must be outside checkout")
    runner = load_runner(repo)
    runner.clean(repo)
    manifest = read_json(repo / MANIFEST)
    require(read_json(evidence / "manifest.json") == manifest, "retained manifest differs")
    cases = runner.validate_cases(repo, manifest, None)
    summary = read_json(evidence / "summary.json")
    report = verify_records(evidence, repo, expected_sha, runner, cases, summary)
    (evidence / "verified-summary.json").write_text(json.dumps(report, indent=2) + "\n",
                                                    encoding="utf-8")
    print(f"PASS: {report['cases']} KILLED; {report['baseline_selections']} green baselines; "
          f"{report['red_trxs']} red and {report['green_trxs']} restored-green TRXs; "
          f"ref={expected_sha}; frameworks={json.dumps(report['frameworks'], sort_keys=True)}")
    return report


def verify_records(evidence, repo, expected_sha, runner, cases, summary):
    require(cases, "no selected mutation cases")
    require(summary.get("ref") == expected_sha and summary.get("verdict") == "KILLED",
            "batch is not KILLED on expected SHA")
    records = summary.get("records", [])
    require(len(records) == len(cases), "incomplete mutation batch")
    baselines = {}
    for index, (case, record) in enumerate(zip(cases, records)):
        directory = evidence / f"{index:03d}-{case['id']}"
        require(read_json(directory / "case.json") == case and record.get("case") == case,
                f"{case['id']}: case differs from manifest")
        require(read_json(directory / "verdict.json") == record,
                f"{case['id']}: per-case verdict differs from summary")
        require(record.get("id") == case["id"] and record.get("ref") == expected_sha
                and record.get("verdict") == "KILLED", f"{case['id']}: wrong case/ref/verdict")
        require(record.get("restored_clean") is True and record.get("final_clean") is True,
                f"{case['id']}: no clean restoration")
        original = (directory / "original.bin").read_bytes()
        require(original == runner.source_path(repo, case["file"]).read_bytes(),
                f"{case['id']}: original bytes differ from same-SHA checkout")
        digest = hashlib.sha256(original).hexdigest()
        require(record.get("original_sha256") == digest == record.get("restored_sha256"),
                f"{case['id']}: restoration digest mismatch")
        old, new = case["old"].encode("utf-8"), case["new"].encode("utf-8")
        require(old and old != new and original.count(old) == 1,
                f"{case['id']}: replacement is not exact")
        require((directory / "applied.diff").read_bytes().strip(),
                f"{case['id']}: missing applied diff")
        selection = tuple(case[key] for key in ("project", "framework", "filter"))
        if selection not in baselines:
            baselines[selection] = phase_counts(runner, directory / "baseline", case)
        baseline = baselines[selection]
        red = phase_counts(runner, directory / "red", case)
        green = phase_counts(runner, directory / "green", case)
        require(record.get("baseline") == baseline and record.get("red") == red
                and record.get("green") == green, f"{case['id']}: record differs from TRXs")
        require(baseline["exit"] == 0 and baseline["failed"] == 0
                and green == baseline, f"{case['id']}: baseline/restored run is not green")
        require(red["exit"] == 1 and red["failed"] > 0
                and red["total"] == baseline["total"], f"{case['id']}: red is not an assertion kill")
        expected = case.get("expected_failures")
        require(isinstance(expected, list) and expected
                and all(any(fragment in name for name in red["failures"]) for fragment in expected),
                f"{case['id']}: not every intended assertion failed")
    report = {"ref": expected_sha, "verdict": "KILLED", "cases": len(cases),
              "frameworks": dict(sorted(Counter(case["framework"] for case in cases).items())),
              "baseline_selections": len(baselines), "red_trxs": len(cases), "green_trxs": len(cases),
              "scope": "ordinary application and pure metadata contracts; no OS isolation proof"}
    return report


def self_test():
    # Synthetic receipts only. This does not execute .NET or prove isolation.
    repo = Path(__file__).resolve().parents[2]
    actual_runner = load_runner(repo)
    sha = "a" * 40

    def fixture(root):
        source = root / "source.cs"
        source.write_bytes(b"return false;\n")
        case = {"id": "fixture", "file": "source.cs", "project": "Contract.Tests.csproj",
                "old": "return false;", "new": "return true;", "framework": "net8.0",
                "filter": "FullyQualifiedName~Contract", "expected_failures": ["Contract.Reject"]}
        directory = root / "000-fixture"
        directory.mkdir()
        (directory / "original.bin").write_bytes(source.read_bytes())
        (directory / "applied.diff").write_text("-return false;\n+return true;\n")
        digest = hashlib.sha256(source.read_bytes()).hexdigest()
        record = {"id": case["id"], "ref": sha, "case": case, "verdict": "KILLED",
                  "original_sha256": digest, "restored_sha256": digest,
                  "restored_clean": True, "final_clean": True}
        for phase, failed in (("baseline", False), ("red", True), ("green", False)):
            phase_dir = directory / phase
            phase_dir.mkdir()
            trx = ('<TestRun><Results><UnitTestResult executionId="execution-1" '
                   'testId="test-1" testName="Contract.Reject" outcome="'
                   + ("Failed" if failed else "Passed") + '">'
                   + ('<Output><ErrorInfo><Message>Assert.True failed</Message></ErrorInfo></Output>'
                      if failed else "")
                   + '</UnitTestResult></Results><ResultSummary outcome="'
                   + ("Failed" if failed else "Completed") + '"><Counters total="1" executed="1" '
                   + f'passed="{0 if failed else 1}" failed="{1 if failed else 0}" notExecuted="0"/>'
                   + '</ResultSummary></TestRun>')
            (phase_dir / "result.trx").write_text(trx)
            (phase_dir / "console.log").write_text("")
            command = ["dotnet", "test", case["project"], "--framework", case["framework"],
                       "--filter", case["filter"], "--nologo", "-v", "minimal",
                       "--results-directory", str(phase_dir), "--logger", "trx;LogFileName=result.trx"]
            process = {"exit": int(failed), "timed_out": False, "interrupted": False}
            counts = actual_runner.read_result(phase_dir / "result.trx", process["exit"])
            for name, value in (("command.json", command), ("process.json", process),
                                ("counts.json", counts)):
                (phase_dir / name).write_text(json.dumps(value))
            record[phase] = counts
        return {"root": root, "directory": directory, "source": source, "case": case,
                "record": record, "summary": {"ref": sha, "verdict": "KILLED", "records": [record]}}

    def write_phase(data, phase, source_phase):
        directory = data["directory"]
        for filename in ("result.trx", "counts.json", "process.json", "console.log"):
            (directory / phase / filename).write_bytes((directory / source_phase / filename).read_bytes())
        data["record"][phase] = dict(data["record"][source_phase])

    controls = [
        ("wrong SHA", lambda d: d["summary"].update(ref="b" * 40)),
        ("incomplete batch", lambda d: d["summary"].update(records=[])),
        ("survivor summary", lambda d: d["summary"].update(verdict="SURVIVED")),
        ("missing clean restore", lambda d: d["record"].update(restored_clean=False)),
        ("wrong restored hash", lambda d: d["record"].update(restored_sha256="0" * 64)),
        ("changed source bytes", lambda d: d["source"].write_bytes(b"different\n")),
        ("missing intended failure", lambda d: d["case"].update(expected_failures=["Absent.Assertion"])),
        ("surviving red", lambda d: write_phase(d, "red", "baseline")),
        ("failed restored green", lambda d: write_phase(d, "green", "red")),
        ("missing red TRX", lambda d: (d["directory"] / "red/result.trx").unlink()),
        ("build failure", lambda d: (d["directory"] / "red/console.log").write_text("source.cs: error CS1002")),
        ("tampered counters", lambda d: (d["directory"] / "red/counts.json").write_text("{}")),
    ]
    for name, mutate in [("valid fixture", None), *controls]:
        with tempfile.TemporaryDirectory(prefix="ashlar-mutation-parser-") as temporary:
            data = fixture(Path(temporary))
            if mutate is not None:
                mutate(data)
            (data["directory"] / "case.json").write_text(json.dumps(data["case"]))
            (data["directory"] / "verdict.json").write_text(json.dumps(data["record"]))
            # Only the git-tracked source lookup is substituted; the real TRX
            # parser and all case/verdict/restoration checks run on fixture bytes.
            runner = SimpleNamespace(read_result=actual_runner.read_result,
                                     source_path=lambda _repo, _relative: data["source"])
            try:
                verify_records(data["root"], repo, sha, runner, [data["case"]], data["summary"])
            except (ValueError, OSError, actual_runner.InvalidRun):
                if mutate is None:
                    raise
            else:
                require(mutate is None, f"negative control accepted: {name}")
    print(f"PASS mutation evidence self-test: valid synthetic fixture and {len(controls)} rejected controls")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", type=Path)
    parser.add_argument("--repo", type=Path)
    parser.add_argument("--expected-sha")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    elif args.evidence is not None and args.repo is not None and args.expected_sha:
        verify(args.evidence.resolve(), args.repo.resolve(), args.expected_sha)
    else:
        parser.error("--evidence, --repo and --expected-sha are required")


if __name__ == "__main__":
    main()
