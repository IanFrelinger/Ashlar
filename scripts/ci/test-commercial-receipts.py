#!/usr/bin/env python3
"""Hermetic file controls for the commercial receipt checker; no SDK or test host needed."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

SCRIPT = Path(__file__).with_name("verify-commercial-receipts.py")
SPEC = importlib.util.spec_from_file_location("receipts", SCRIPT)
CHECK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECK)
SUITES = json.loads((Path(__file__).resolve().parents[2] / "ci/commercial-test-suites.json").read_text())
N = CHECK.NS


def fixture(suite, windows=False, skipped=False):
    root = ET.Element(N + "TestRun")
    results = ET.SubElement(root, N + "Results")
    definitions = ET.SubElement(root, N + "TestDefinitions")
    entries = ET.SubElement(root, N + "TestEntries")
    binary = f"/checkout/{Path(suite['project']).parent.as_posix()}/bin/Debug/{suite['framework']}/{suite['assembly']}.dll"
    if windows:
        binary = "D:" + binary.replace("/", "\\")
    for index, outcome in enumerate(["Passed", "NotExecuted"] if skipped else ["Passed"]):
        attributes = {"testId": f"test-{index}", "executionId": f"exec-{index}"}
        name = f"Commercial.Sample.Case{index}"
        ET.SubElement(results, N + "UnitTestResult", **attributes, testName=name, outcome=outcome)
        definition = ET.SubElement(definitions, N + "UnitTest", id=attributes["testId"], name=name, storage=binary.lower())
        ET.SubElement(definition, N + "Execution", id=attributes["executionId"])
        ET.SubElement(definition, N + "TestMethod", codeBase=binary)
        ET.SubElement(entries, N + "TestEntry", **attributes)
    summary = ET.SubElement(root, N + "ResultSummary", outcome="Completed")
    counts = dict.fromkeys(CHECK.COUNTERS, "0")
    counts.update(total=str(2 if skipped else 1), passed="1", executed="1", notExecuted=str(int(skipped)))
    ET.SubElement(summary, N + "Counters", **counts)
    return root


class ReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.path = self.root / "receipt.trx"

    def tearDown(self):
        self.temp.cleanup()

    def inspect(self, document):
        ET.ElementTree(document).write(self.path, encoding="utf-8", xml_declaration=True)
        return CHECK.inspect_receipt(self.path, SUITES[0])

    def test_successful_windows_and_unix_receipts_with_optional_skips(self):
        for windows in (False, True):
            for skipped in (False, True):
                with self.subTest(windows=windows, skipped=skipped):
                    result = self.inspect(fixture(SUITES[0], windows, skipped))
                    self.assertEqual((result["passed"], result["skipped"]), (1, int(skipped)))
                    self.assertEqual(len(result["sha256"]), 64)

    def test_bad_evidence_is_refused(self):
        def changed(path, attribute, value):
            document = fixture(SUITES[0])
            document.find(path, {"t": N[1:-1]}).set(attribute, value)
            return document

        cases = {}
        for outcome in ("Failed", "Aborted", "InProgress"):
            cases["summary " + outcome] = changed("t:ResultSummary", "outcome", outcome)
        for outcome in ("Failed", "Aborted", "Timeout", "Error", "NotExecuted", "Unknown"):
            cases["result " + outcome] = changed("t:Results/t:UnitTestResult", "outcome", outcome)
        for counter in CHECK.COUNTERS:
            cases["counter " + counter] = changed("t:ResultSummary/t:Counters", counter, "9")
        for value in ("-1", "garbage"):
            cases["invalid counter " + value] = changed("t:ResultSummary/t:Counters", "total", value)
        cases["unknown counter"] = changed("t:ResultSummary/t:Counters", "newCounter", "0")
        cases["wrong test ID"] = changed("t:Results/t:UnitTestResult", "testId", "other")
        cases["wrong execution ID"] = changed("t:TestDefinitions/t:UnitTest/t:Execution", "id", "other")
        cases["wrong name"] = changed("t:TestDefinitions/t:UnitTest", "name", "other")
        cases["wrong entry"] = changed("t:TestEntries/t:TestEntry", "testId", "other")
        for attribute, path in (("storage", "t:TestDefinitions/t:UnitTest"),
                                ("codeBase", "t:TestDefinitions/t:UnitTest/t:TestMethod")):
            for before, after in (("net8.0", "net10.0"), ("Debug", "Release"),
                                  (SUITES[0]["assembly"], "OtherSuite"), ("commercial/tests", "other/tests")):
                document = fixture(SUITES[0])
                node = document.find(path, {"t": N[1:-1]})
                node.set(attribute, node.get(attribute).replace(before.lower() if attribute == "storage" else before, after))
                cases[attribute + before] = document
        for section in ("Results", "ResultSummary", "TestDefinitions", "TestEntries"):
            document = fixture(SUITES[0])
            document.remove(document.find(N + section))
            cases["missing " + section] = document
        for section in ("Results", "TestDefinitions", "TestEntries"):
            document = fixture(SUITES[0])
            parent = document.find(N + section)
            parent.append(copy.deepcopy(parent[0]))
            cases["duplicate " + section] = document
        document = fixture(SUITES[0])
        document.find(N + "Results").clear()
        cases["empty results"] = document
        document = fixture(SUITES[0])
        document.find(N + "ResultSummary/" + N + "Counters").attrib.pop("total")
        cases["missing counter"] = document
        document = fixture(SUITES[0])
        document.tag = "TestRun"
        cases["wrong XML namespace"] = document
        document = fixture(SUITES[0])
        row = document.find(N + "Results/" + N + "UnitTestResult")
        ET.SubElement(row, N + "InnerResults").append(copy.deepcopy(row))
        cases["nested results"] = document
        document = fixture(SUITES[0], skipped=True)
        document.find(N + "Results")[1].set("outcome", "Failed")
        cases["failed row disguised as skipped in counters"] = document
        document = fixture(SUITES[0])
        definitions = document.find(N + "TestDefinitions")
        unused = copy.deepcopy(definitions[0])
        unused.set("id", "unused-definition")
        definitions.append(unused)
        cases["unused definition"] = document
        for name, document in cases.items():
            with self.subTest(name=name):
                with self.assertRaises(ValueError):
                    self.inspect(document)

    def test_consistent_all_skipped_and_empty_runs_are_refused(self):
        for empty in (False, True):
            with self.subTest(empty=empty):
                document = fixture(SUITES[0])
                counts = document.find(N + "ResultSummary/" + N + "Counters")
                counts.set("passed", "0")
                counts.set("executed", "0")
                if empty:
                    counts.set("total", "0")
                    for section in ("Results", "TestDefinitions", "TestEntries"):
                        document.find(N + section).clear()
                else:
                    counts.set("notExecuted", "1")
                    document.find(N + "Results/" + N + "UnitTestResult").set("outcome", "NotExecuted")
                with self.assertRaises(ValueError):
                    self.inspect(document)

    def test_malformed_file_is_refused(self):
        self.path.write_text("<TestRun>")
        with self.assertRaises(ET.ParseError):
            CHECK.inspect_receipt(self.path, SUITES[0])

    def test_command_checks_all_three_projects_and_writes_receipts(self):
        (self.root / "ci").mkdir()
        (self.root / "ci/commercial-test-suites.json").write_text(json.dumps(SUITES))
        paths = []
        for suite in SUITES:
            project = self.root / suite["project"]
            project.parent.mkdir(parents=True)
            project.write_text("<Project />")
            results = project.parent / "TestResults"
            results.mkdir()
            path = results / "result.trx"
            ET.ElementTree(fixture(suite)).write(path, encoding="utf-8")
            paths.append(path)
        command = [sys.executable, str(SCRIPT), "--root", str(self.root), "--output", str(self.root / "out")]
        completed = subprocess.run(command, text=True, capture_output=True, check=False)
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIn("3 suites verified", completed.stdout)
        self.assertEqual(len(list((self.root / "out").glob("*.trx"))), 3)
        self.assertEqual(len(json.loads((self.root / "out/receipts.json").read_text())), 3)
        paths[0].unlink()
        missing = subprocess.run(command, text=True, capture_output=True, check=False)
        self.assertNotEqual(missing.returncode, 0)
        self.assertIn("expected one TRX, found 0", missing.stderr)
        paths[0].write_text("<broken")
        malformed = subprocess.run(command, text=True, capture_output=True, check=False)
        self.assertNotEqual(malformed.returncode, 0)
        self.assertIn("failed:", malformed.stderr)
        paths[0].write_bytes(paths[1].read_bytes())
        wrong = subprocess.run(command, text=True, capture_output=True, check=False)
        self.assertNotEqual(wrong.returncode, 0)
        self.assertIn("different project", wrong.stderr)
        ET.ElementTree(fixture(SUITES[0])).write(paths[0], encoding="utf-8")
        paths[0].with_name("second.trx").write_bytes(paths[0].read_bytes())
        duplicate = subprocess.run(command, text=True, capture_output=True, check=False)
        self.assertNotEqual(duplicate.returncode, 0)
        self.assertIn("expected one TRX, found 2", duplicate.stderr)


if __name__ == "__main__":
    unittest.main()
