import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import review
import coordinate


class ReviewerTests(unittest.TestCase):
    def test_duplicates_are_candidates_with_locations_and_coalesced_windows(self):
        code = "\n".join(f"value += operation{i}();" for i in range(40))
        rows = review.redundancy({"src/a.cs": code, "src/b.cs": code})
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["line"], 1)
        self.assertEqual(rows[0]["related_locations"][0]["path"], "src/b.cs")
        self.assertEqual(rows[0]["confidence"], "candidate")
        self.assertNotIn(code, json.dumps(rows))
        self.assertEqual(review.redundancy({"src/a.cs": code}), [])

    def test_new_duplicate_location_changes_identity(self):
        code = "\n".join(f"value += operation{i}();" for i in range(24))
        before = review.redundancy({"src/a.cs": code, "src/b.cs": code})
        after = review.redundancy({"src/a.cs": code, "src/b.cs": code, "src/c.cs": code})
        self.assertNotEqual(before[0]["fingerprint"], after[0]["fingerprint"])

    def test_efficiency_patterns_and_clean_alternatives(self):
        code = "var x = items.ToList().ToArray();\nwork.GetAwaiter().GetResult();"
        self.assertEqual({r["rule"] for r in review.efficiency({"src/a.cs": code})}, {"repeated-materialization", "sync-over-async"})
        self.assertEqual(review.efficiency({"src/a.cs": "var x = items.ToList(); await work;"}), [])

    def test_security_parser_failure_is_incomplete_not_clean(self):
        rows, omitted = review.analyze({".github/workflows/a.yml": "jobs: ["}, "security")
        self.assertEqual(rows, [])
        self.assertEqual(len(omitted), 1)

    def test_baseline_does_not_hide_an_added_identical_occurrence(self):
        old = "task.GetAwaiter().GetResult();"
        with patch.object(review, "commit"), patch.object(review, "sources", side_effect=[
            ({"src/a.cs": old + "\n" + old}, [], 80), ({"src/a.cs": old}, [], 40)]):
            report = review.review(Path("."), "a" * 40, "b" * 40, "efficiency")
        self.assertEqual(report["candidates_found"], 1)
        self.assertEqual(report["findings"][0]["line"], 2)

    def test_finding_truncation_is_incomplete(self):
        with patch.object(review, "commit"), patch.object(review, "sources", return_value=(
            {"src/a.cs": "task.GetAwaiter().GetResult();"}, [], 40)), patch.object(review, "MAX_FINDINGS", 0):
            report = review.review(Path("."), "a" * 40, None, "efficiency")
        self.assertEqual(report["status"], "incomplete")
        self.assertTrue(report["findings_truncated"])

    def test_summary_escapes_untrusted_paths(self):
        row = review.finding("r", "<script>`.cs", 1, "message", "action", "evidence")
        report = {"worker": "security", "status": "completed", "source_sha": "a" * 40,
                  "files_scanned": 1, "candidates_found": 1, "scope": "snapshot", "findings": [row], "omitted": [], "findings_truncated": False}
        rendered = review.markdown(report)
        self.assertNotIn("<script>", rendered)
        self.assertIn("\\`", rendered)
        self.assertEqual(review.efficiency({"src/a.cs": '// work.GetAwaiter().GetResult();\nvar x = "items.ToList().ToArray()";'}), [])

    def test_source_reader_uses_committed_blobs_and_refuses_symlinks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def git(*args):
                return subprocess.check_output(["git", "-C", directory, *args]).decode().strip()
            git("init", "-q")
            git("config", "user.email", "fixture@example.invalid")
            git("config", "user.name", "Fixture")
            (root / "src").mkdir()
            (root / "src/a.cs").write_text("items.ToList().ToArray();")
            (root / "src/link.cs").symlink_to("/etc/passwd")
            git("add", ".")
            git("commit", "-qm", "fixture")
            sha = git("rev-parse", "HEAD")
            (root / "src/a.cs").write_text("changed working tree")
            report = review.review(root, sha, None, "efficiency")
            self.assertEqual(report["status"], "incomplete")
            self.assertEqual(report["candidates_found"], 1)
            self.assertEqual(report["omitted"][0]["path"], "src/link.cs")
            self.assertEqual(review.review(root, sha, sha, "efficiency")["findings"], [])
            with patch.object(review, "MAX_FILE_BYTES", 1):
                self.assertEqual(review.review(root, sha, None, "efficiency")["files_scanned"], 0)
            with self.assertRaises(ValueError):
                review.review(root, "HEAD", None, "efficiency")


class CoordinatorTests(unittest.TestCase):
    def receipt(self, worker, sha="a" * 40):
        return {"schema": 1, "worker": worker, "source_sha": sha, "status": "completed", "findings": [],
                "engine": "deterministic-static-review", "tool_sha256": "a" * 64,
                "base_sha": None, "scope": "full snapshot", "omitted": [], "candidates_found": 0, "findings_truncated": False}

    def test_missing_worker_and_wrong_sha_never_report_complete(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.assertEqual(coordinate.coordinate(root, "a" * 40)["status"], "incomplete")
            for worker in coordinate.WORKERS:
                (root / worker).mkdir()
                (root / worker / "report.json").write_text(json.dumps(self.receipt(worker)))
            result = coordinate.coordinate(root, "a" * 40)
            self.assertEqual(result["status"], "completed")
            self.assertFalse(result["autonomous_provider"]["enabled"])
            (root / "security/report.json").write_text(json.dumps(self.receipt("security", "b" * 40)))
            with self.assertRaises(ValueError):
                coordinate.coordinate(root, "a" * 40)

    def test_deduplication_keeps_security_priority_and_actionable_plan(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            candidate = review.finding("same-rule", "src/a.cs", 5, "fixed message", "verify boundary", "evidence")
            for worker in coordinate.WORKERS:
                receipt = self.receipt(worker)
                if worker in {"cost", "security"}:
                    receipt["findings"] = [candidate]
                    receipt["candidates_found"] = 1
                (root / worker).mkdir()
                (root / worker / "report.json").write_text(json.dumps(receipt))
            result = coordinate.coordinate(root, "a" * 40)
            self.assertEqual(len(result["items"]), 1)
            item = result["items"][0]
            self.assertEqual(item["priority"], "security-triage")
            self.assertEqual(item["reported_by"], ["cost", "security"])
            self.assertEqual(item["state"], "needs-human-validation")
            self.assertIn("named failing test", item["regression_plan"][1])

    def test_malformed_or_contradictory_receipts_are_refused(self):
        for changes in ({"findings": {}}, {"omitted": ["scan failed"]}, {"candidates_found": 1},
                        {"findings_truncated": True}, {"base_sha": "b" * 40}, {"engine": "fake"}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                coordinate.validate_report({**self.receipt("security"), **changes}, "a" * 40)

    def test_mixed_baseline_scopes_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for worker in coordinate.WORKERS:
                receipt = self.receipt(worker)
                if worker == "security":
                    receipt.update(base_sha="a" * 40, scope="new candidates")
                (root / worker).mkdir()
                (root / worker / "report.json").write_text(json.dumps(receipt))
            with self.assertRaises(ValueError):
                coordinate.coordinate(root, "a" * 40)


if __name__ == "__main__":
    unittest.main()
