#!/usr/bin/env python3
"""Check the native readiness sweep's commercial TRXs, without running another suite.

This lens supports the flat xUnit/VSTest receipts these projects currently write. It proves
nonempty, successful, internally consistent results associated with each project and TFM;
it does not prove that every discoverable test was selected. A fresh workflow checkout and
exactly one TRX per project avoid silently choosing among old or repeated runs.
"""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
COUNTERS = ("total executed passed failed error timeout aborted inconclusive passedButRunAborted "
            "notRunnable notExecuted disconnected warning completed inProgress pending").split()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def one(parent, name):
    matches = parent.findall(NS + name)
    require(len(matches) == 1, f"expected exactly one {name}")
    return matches[0]


def normalized(path):
    return path.replace("\\", "/").lower()


def inspect_receipt(path, suite):
    root = ET.parse(path).getroot()
    require(root.tag == NS + "TestRun", "not a VSTest TestRun")
    summary = one(root, "ResultSummary")
    require(summary.get("outcome") in ("Completed", "Passed"), "run did not complete successfully")
    counters = one(summary, "Counters")
    require(set(counters.attrib) == set(COUNTERS), "missing or unknown summary counters")
    require(all(value.isdecimal() for value in counters.attrib.values()), "invalid summary counter")
    counts = {key: int(value) for key, value in counters.attrib.items()}
    results = one(root, "Results")
    rows = list(results)
    require(rows and all(row.tag == NS + "UnitTestResult" for row in rows), "empty or unsupported result layout")
    require(len(results.findall(".//" + NS + "UnitTestResult")) == len(rows), "nested result layout requires an explicit accounting rule")
    require(all(row.get("outcome") in ("Passed", "NotExecuted") for row in rows), "failed or incomplete test result")
    passed = sum(row.get("outcome") == "Passed" for row in rows)
    require(passed > 0, "no test executed successfully (empty or all skipped)")
    expected = dict.fromkeys(COUNTERS, 0)
    expected.update(total=len(rows), executed=passed, passed=passed, notExecuted=len(rows) - passed)
    require(counts == expected, "summary counters do not reconcile with result rows")

    definitions = list(one(root, "TestDefinitions"))
    require(all(item.tag == NS + "UnitTest" for item in definitions), "unsupported test definition")
    by_id = {item.get("id"): item for item in definitions}
    require(all(by_id) and len(by_id) == len(definitions), "missing or duplicate definition ID")
    require({row.get("testId") for row in rows} == set(by_id), "result/definition test IDs differ")
    executions = [row.get("executionId") for row in rows]
    require(all(executions) and len(set(executions)) == len(rows), "missing or duplicate result execution ID")
    project_dir = Path(suite["project"]).parent.as_posix()
    suffix = normalized(f"/{project_dir}/bin/Debug/{suite['framework']}/{suite['assembly']}.dll")
    for row in rows:
        definition = by_id[row.get("testId")]
        require(one(definition, "Execution").get("id") == row.get("executionId"), "result/definition execution IDs differ")
        method = one(definition, "TestMethod")
        require(normalized(definition.get("storage", "")).endswith(suffix)
                and normalized(method.get("codeBase", "")).endswith(suffix),
                "receipt belongs to a different project, assembly, configuration or framework")
        require(row.get("testName") and row.get("testName") == definition.get("name"), "result/definition names differ")

    entries = list(one(root, "TestEntries"))
    expected_entries = {(row.get("testId"), row.get("executionId")) for row in rows}
    require(all(entry.tag == NS + "TestEntry" for entry in entries)
            and len(entries) == len(rows)
            and {(entry.get("testId"), entry.get("executionId")) for entry in entries} == expected_entries,
            "result/test entry IDs differ")
    return {"project": suite["project"], "framework": suite["framework"], "passed": passed,
            "skipped": len(rows) - passed, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def verify(root, output):
    suites = json.loads((root / "ci/commercial-test-suites.json").read_text(encoding="utf-8"))
    require(isinstance(suites, list) and len(suites) == 3, "expected the three registered commercial suites")
    require(len({suite["project"] for suite in suites}) == len(suites), "duplicate suite project")
    receipts = []
    for suite in suites:
        project = root / suite["project"]
        require(project.is_file() and project.resolve().is_relative_to((root / "commercial/tests").resolve()),
                f"missing or invalid commercial project: {project}")
        files = sorted((project.parent / "TestResults").rglob("*.trx"))
        require(len(files) == 1, f"{suite['project']}: expected one TRX, found {len(files)}")
        try:
            receipt = inspect_receipt(files[0], suite)
        except (ValueError, ET.ParseError, OSError) as error:
            raise ValueError(f"{files[0]}: {error}") from error
        receipts.append(receipt)
        output.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(files[0], output / (suite["assembly"] + ".trx"))
        print(f"{suite['project']} [{suite['framework']}]: {receipt['passed']} passed, {receipt['skipped']} skipped")
    (output / "receipts.json").write_text(json.dumps(receipts, indent=2) + "\n", encoding="utf-8")
    print(f"Commercial receipt check: {len(receipts)} suites verified")
    return receipts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        verify(args.root, args.output)
    except (ValueError, KeyError, TypeError, OSError, ET.ParseError) as error:
        print(f"Commercial receipt check failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
