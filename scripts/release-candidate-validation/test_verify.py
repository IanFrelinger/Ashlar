import json
from pathlib import Path
import tempfile
import unittest

import verify


class EvidenceChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.assets = self.root / "assets.json"
        self.version = "9.9.9-rcvalidation.g123456789abc"
        self.data = {"libraries": {
            f"Ashlar.{name}/{self.version}": {"type": "package"}
            for name in ["Hosting.Bundle"] + [f"Fixture{n}" for n in range(9)]},
            "packageFolders": {str(self.root / "packages"): {}}}

    def check_assets(self):
        self.assets.write_text(json.dumps(self.data))
        verify.assets(self.assets, self.version, self.root / "packages")

    def test_good_package_closure(self):
        self.check_assets()

    def test_stale_package_is_rejected(self):
        self.data["libraries"]["Ashlar.Stale/0.4.2"] = {"type": "package"}
        with self.assertRaisesRegex(ValueError, "Wrong version"):
            self.check_assets()

    def test_project_reference_is_rejected(self):
        self.data["libraries"]["SourceProject/1.0.0"] = {"type": "project"}
        with self.assertRaisesRegex(ValueError, "ProjectReference"):
            self.check_assets()

    def test_global_cache_is_rejected(self):
        self.data["packageFolders"]["/root/.nuget/packages"] = {}
        with self.assertRaisesRegex(ValueError, "Unexpected package caches"):
            self.check_assets()

    def test_empty_closure_is_rejected(self):
        self.data["libraries"] = {}
        with self.assertRaisesRegex(ValueError, "Incomplete"):
            self.check_assets()

    def check_trx(self, total, executed, passed, failed=0, result_count=8):
        path = self.root / "tests.trx"
        results = '<UnitTestResult outcome="Passed" />' * result_count
        path.write_text(f'<TestRun><Results>{results}</Results><Counters total="{total}" executed="{executed}" passed="{passed}" failed="{failed}" /></TestRun>')
        verify.trx(path, 8)

    def test_good_trx(self):
        self.check_trx(8, 8, 8)

    def test_zero_tests_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Wrong test counts"):
            self.check_trx(0, 0, 0, result_count=0)

    def test_skipped_tests_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Wrong test counts"):
            self.check_trx(8, 7, 7, result_count=7)

    def test_forged_counters_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "disagree"):
            self.check_trx(8, 8, 8, result_count=0)


if __name__ == "__main__":
    unittest.main()
