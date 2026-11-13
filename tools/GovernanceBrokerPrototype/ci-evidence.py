#!/usr/bin/env python3
"""Retain and check ordinary contract TRXs; never run the service or an isolation probe."""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

FORMAT = "ashlar-governance-contracts/v1"
BEGIN = "ASHLAR_GOVERNANCE_CONTRACT_EVIDENCE_BEGIN"
END = "ASHLAR_GOVERNANCE_CONTRACT_EVIDENCE_END"
FRAMEWORKS = ("net8.0", "net10.0")
CLASSES = (
    "BrokerParserTests", "BrokerPersistenceTests", "BrokerFramingTests",
    "BrokerConfigurationTests", "LinuxMetadataPolicyTests", "BrokerCapacityConfigurationTests",
)
# Initial source inventory: 141 authored Fact/InlineData cases. Additional cases are
# allowed, but dropping below this inventory cannot silently leave a green check.
MINIMUM_CASES = 141
MAX_TRX = 8 * 1024 * 1024
MAX_ENCODED = 32 * 1024 * 1024


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def sha(value: str) -> str:
    require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{40}", value) is not None,
            "expected a full lowercase source SHA")
    return value


def encode_file(path: Path, limit: int) -> dict | None:
    if not path.is_file():
        return None
    require(path.stat().st_size <= limit, f"{path.name}: evidence exceeds size bound")
    data = path.read_bytes()
    return {"base64": base64.b64encode(data).decode("ascii"), "sha256": hashlib.sha256(data).hexdigest()}


def decode_file(value: dict | None, limit: int, name: str) -> bytes:
    require(isinstance(value, dict) and set(value) == {"base64", "sha256"}, f"{name}: missing/malformed payload")
    require(isinstance(value["base64"], str) and len(value["base64"]) <= (limit + 2) // 3 * 4,
            f"{name}: encoded size bound")
    data = base64.b64decode(value["base64"], validate=True)
    require(len(data) <= limit, f"{name}: decoded size bound")
    require(value["sha256"] == hashlib.sha256(data).hexdigest(), f"{name}: digest mismatch")
    return data


def envelope(results: Path, source_sha: str) -> dict:
    runs = []
    for framework in FRAMEWORKS:
        receipt = results / f"{framework}.exitcode"
        status = int(receipt.read_text().strip()) if receipt.is_file() else None
        runs.append({"framework": framework, "exit_code": status,
                     "trx": encode_file(results / f"contracts-{framework}.trx", MAX_TRX),
                     "runtimeconfig": encode_file(results / f"runtimeconfig-{framework}.json", 65536)})
    return {"format": FORMAT, "source_sha": sha(source_sha),
            "roll_forward": os.environ.get("DOTNET_ROLL_FORWARD"),
            "runtimes": encode_file(results / "runtimes.txt", 65536), "runs": runs}


def check_trx(data: bytes, framework: str) -> dict:
    root = ET.fromstring(data)
    rows = root.findall("./{*}Results/{*}UnitTestResult")
    counters = root.findall("./{*}ResultSummary/{*}Counters")
    require(len(counters) == 1, f"{framework}: expected one counter record")
    counts = {name: int(counters[0].get(name, "-1")) for name in ("total", "executed", "passed", "failed")}
    require(len(rows) >= MINIMUM_CASES, f"{framework}: fewer than {MINIMUM_CASES} executed case rows")
    require(all(row.get("outcome") == "Passed" for row in rows), f"{framework}: failed or skipped case")
    require(all(counts[name] == len(rows) for name in ("total", "executed", "passed"))
            and counts["failed"] == 0, f"{framework}: inconsistent/skipped counters {counts}")
    ids = [row.get("executionId") for row in rows]
    require(all(ids) and len(set(ids)) == len(ids), f"{framework}: missing or duplicate execution IDs")
    names = [row.get("testName", "") for row in rows]
    for test_class in CLASSES:
        require(any(f".{test_class}." in name for name in names), f"{framework}: absent class {test_class}")
    definitions = root.findall("./{*}TestDefinitions/{*}UnitTest")
    definition_ids = [item.get("id") for item in definitions]
    require(bool(definitions) and all(definition_ids) and len(set(definition_ids)) == len(definition_ids),
            f"{framework}: missing/duplicate test definitions")
    require(all(row.get("testId") in definition_ids for row in rows),
            f"{framework}: result is not linked to a test definition")
    suffix = f"/{framework}/governancebrokerprototype.tests.dll"
    require(bool(definitions) and all(item.get("storage", "").replace("\\", "/").lower().endswith(suffix)
                                     for item in definitions), f"{framework}: wrong/missing test assembly asset")
    return {"framework": framework, "counts": counts, "cases": [
        {"execution_id": row.get("executionId"), "name": row.get("testName"), "outcome": row.get("outcome")}
        for row in rows
    ]}


def validate(document: dict, expected_sha: str, output: Path | None = None) -> dict:
    require(isinstance(document, dict) and set(document) == {
        "format", "source_sha", "roll_forward", "runtimes", "runs"}, "unexpected envelope fields")
    require(document["format"] == FORMAT, "unknown evidence format")
    require(document["source_sha"] == sha(expected_sha), "source SHA mismatch")
    require(document["roll_forward"] == "LatestPatch", "runtime-family roll-forward guard absent")
    require(isinstance(document["runs"], list) and all(isinstance(run, dict) for run in document["runs"])
            and [run.get("framework") for run in document["runs"]] == list(FRAMEWORKS),
            "missing, duplicate or reordered framework receipts")
    if output is not None:
        output.mkdir(parents=True, exist_ok=True)
        (output / "envelope.json").write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    report = {"format": FORMAT, "source_sha": expected_sha, "status": "failed", "runs": [], "errors": []}
    try:
        runtimes = decode_file(document["runtimes"], 65536, "runtimes.txt")
        if output is not None:
            (output / "runtimes.txt").write_bytes(runtimes)
        for family in ("8.0", "10.0"):
            require(any(line.startswith(f"Microsoft.NETCore.App {family}.")
                        for line in runtimes.decode("utf-8").splitlines()), f"missing installed runtime {family}")
    except (ValueError, UnicodeError) as ex:
        report["errors"].append(str(ex))
    for run in document["runs"]:
        framework = run["framework"]
        try:
            require(set(run) == {"framework", "exit_code", "trx", "runtimeconfig"}, f"{framework}: malformed run receipt")
            data = decode_file(run["trx"], MAX_TRX, framework)
            if output is not None:
                (output / f"contracts-{framework}.trx").write_bytes(data)
            runtimeconfig = decode_file(run["runtimeconfig"], 65536, f"runtimeconfig-{framework}")
            if output is not None:
                (output / f"runtimeconfig-{framework}.json").write_bytes(runtimeconfig)
            options = json.loads(runtimeconfig)["runtimeOptions"]
            require(options.get("tfm") == framework, f"{framework}: runtimeconfig target mismatch")
            families = options.get("frameworks", [options.get("framework", {})])
            family = "8.0." if framework == "net8.0" else "10.0."
            require(any(item.get("name") == "Microsoft.NETCore.App"
                        and item.get("version", "").startswith(family) for item in families),
                    f"{framework}: runtimeconfig framework-family mismatch")
            require(options.get("rollForward") in (None, "Disable", "LatestPatch", "Minor", "LatestMinor"),
                    f"{framework}: runtimeconfig permits major roll-forward")
            require(type(run["exit_code"]) is int and run["exit_code"] == 0,
                    f"{framework}: dotnet test exit was {run['exit_code']!r}")
            report["runs"].append(check_trx(data, framework))
        except (ValueError, ET.ParseError) as ex:
            report["errors"].append(str(ex))
    report["status"] = "passed" if not report["errors"] else "failed"
    if output is not None:
        (output / "verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    require(not report["errors"], "; ".join(report["errors"]))
    return report


def unpack(log: str) -> dict:
    lines = log.splitlines()
    begins = [index for index, line in enumerate(lines) if line == BEGIN]
    ends = [index for index, line in enumerate(lines) if line == END]
    require(len(begins) == 1 and len(ends) == 1 and begins[0] < ends[0],
            "expected exactly one ordered evidence frame")
    encoded = "".join(lines[begins[0] + 1:ends[0]])
    require(0 < len(encoded) <= MAX_ENCODED, "evidence frame size bound")
    return json.loads(base64.b64decode(encoded, validate=True))


def self_test() -> None:
    # In-memory receipt controls, not .NET tests or OS-isolation evidence.
    def fixture(framework: str) -> bytes:
        root = ET.Element("TestRun")
        rows = ET.SubElement(root, "Results")
        definitions = ET.SubElement(root, "TestDefinitions")
        for index in range(MINIMUM_CASES):
            ET.SubElement(rows, "UnitTestResult", executionId=f"execution-{index}", testId=f"test-{index}", outcome="Passed",
                          testName=f"GovernanceBrokerPrototype.Tests.{CLASSES[index % len(CLASSES)]}.Case{index}")
            ET.SubElement(definitions, "UnitTest", id=f"test-{index}", storage=f"/repo/bin/Debug/{framework}/GovernanceBrokerPrototype.Tests.dll")
        ET.SubElement(ET.SubElement(root, "ResultSummary"), "Counters",
                      total=str(MINIMUM_CASES), executed=str(MINIMUM_CASES),
                      passed=str(MINIMUM_CASES), failed="0")
        return ET.tostring(root)
    good = fixture("net8.0")
    require(check_trx(good, "net8.0")["counts"]["passed"] == MINIMUM_CASES, "positive control")
    corruptions = (
        good.replace(b'outcome="Passed"', b'outcome="NotExecuted"', 1),
        good.replace(b'executed="141"', b'executed="0"', 1),
        good.replace(b'execution-1"', b'execution-0"', 1),
        good.replace(b".BrokerParserTests.", b".AbsentTests."),
        good.replace(b"/net8.0/", b"/net10.0/"),
        good.replace(b'testId="test-0"', b'testId="missing-definition"', 1),
        b"<TestRun/>",
    )
    for data in corruptions:
        try:
            check_trx(data, "net8.0")
        except ValueError:
            continue
        raise AssertionError("negative TRX control was accepted")
    payload = {"base64": base64.b64encode(good).decode("ascii"), "sha256": "0" * 64}
    try:
        decode_file(payload, MAX_TRX, "bad-digest")
    except ValueError:
        pass
    else:
        raise AssertionError("digest negative control was accepted")
    for log in ("", BEGIN + "\n" + END, BEGIN + "\n" + BEGIN + "\n" + END,
                END + "\n" + BEGIN + "\n" + "e30="):
        try:
            unpack(log)
        except ValueError:
            continue
        raise AssertionError("bad evidence frame was accepted")
    def payload(data: bytes) -> dict:
        return {"base64": base64.b64encode(data).decode("ascii"), "sha256": hashlib.sha256(data).hexdigest()}
    document = {
        "format": FORMAT, "source_sha": "a" * 40, "roll_forward": "LatestPatch",
        "runtimes": payload(b"Microsoft.NETCore.App 8.0.0 [/synthetic]\nMicrosoft.NETCore.App 10.0.0 [/synthetic]\n"),
        "runs": [{"framework": framework, "exit_code": 0, "trx": payload(fixture(framework)),
                  "runtimeconfig": payload(json.dumps({"runtimeOptions": {
                      "tfm": framework, "framework": {"name": "Microsoft.NETCore.App",
                      "version": "8.0.0" if framework == "net8.0" else "10.0.0"}}}).encode("utf-8"))}
                 for framework in FRAMEWORKS],
    }
    frame = BEGIN + "\n" + base64.b64encode(json.dumps(document).encode("utf-8")).decode("ascii") + "\n" + END
    report = validate(unpack(frame), "a" * 40)
    require(len(report["runs"]) == 2, "two-framework synthetic envelope control")
    try:
        validate(unpack(frame), "b" * 40)
    except ValueError:
        pass
    else:
        raise AssertionError("wrong source SHA was accepted")
    print("PASS receipt self-test: synthetic envelope round-trip; seven TRX faults, digest mismatch, four bad frames and wrong SHA rejected")



def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("pack", "check"):
        command = commands.add_parser(name)
        command.add_argument("--results", required=True, type=Path)
        command.add_argument("--expected-sha", required=True)
    decode = commands.add_parser("decode")
    decode.add_argument("--log", required=True, type=Path)
    decode.add_argument("--output", required=True, type=Path)
    decode.add_argument("--expected-sha", required=True)
    commands.add_parser("self-test")
    args = parser.parse_args()
    try:
        if args.command == "self-test":
            self_test()
        elif args.command == "pack":
            data = json.dumps(envelope(args.results, args.expected_sha), separators=(",", ":")).encode("utf-8")
            encoded = base64.b64encode(data).decode("ascii")
            require(len(encoded) <= MAX_ENCODED, "evidence frame exceeds bound")
            print(BEGIN)
            for start in range(0, len(encoded), 76):
                print(encoded[start:start + 76])
            print(END)
        elif args.command == "check":
            report = validate(envelope(args.results, args.expected_sha), args.expected_sha)
            for run in report["runs"]:
                print(f"PASS contracts {run['framework']}: {run['counts']['passed']} executed cases, no skips; source={args.expected_sha}")
        else:
            report = validate(unpack(args.log.read_text(encoding="utf-8")), args.expected_sha, args.output)
            print(f"PASS retained contract evidence: {sum(run['counts']['passed'] for run in report['runs'])} executions; source={args.expected_sha}")
    except (OSError, ValueError, ET.ParseError, TypeError, KeyError) as ex:
        print(f"contract-evidence: FAIL: {ex}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
