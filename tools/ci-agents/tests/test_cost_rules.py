import importlib.util
from pathlib import Path
import sys
import unittest


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
SPEC = importlib.util.spec_from_file_location("cost_rules", ROOT / "cost_rules.py")
rules = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(rules)
PATH = ".github/workflows/check.yml"


class CostRulesTests(unittest.TestCase):
    def codes(self, source):
        return [row["rule"] for row in rules.findings(source, PATH)]

    def test_missing_timeout_and_unbounded_pr_overlap_are_candidates(self):
        source = "on: pull_request\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet test\n"
        self.assertEqual(["cost/job-timeout-missing", "cost/pr-build-without-concurrency"], self.codes(source))

    def test_explicit_budget_and_global_concurrency_are_respected(self):
        source = "on: [pull_request]\nconcurrency: check-${{ github.ref }}\njobs:\n  build:\n    runs-on: ubuntu-latest\n    timeout-minutes: 20\n    steps:\n      - run: dotnet build\n"
        self.assertEqual([], self.codes(source))

    def test_job_scoped_concurrency_is_respected(self):
        source = "on: pull_request\njobs:\n  build:\n    runs-on: ubuntu-latest\n    concurrency: build-${{ github.ref }}\n    timeout-minutes: 10\n    steps:\n      - run: make test\n"
        self.assertEqual([], self.codes(source))

    def test_long_timeout_is_advisory_threshold_not_platform_limit(self):
        source = "on: workflow_dispatch\njobs:\n  build:\n    timeout-minutes: 90\n    steps:\n      - run: dotnet test\n"
        found = rules.findings(source, PATH)
        self.assertEqual(["cost/long-job-timeout"], self.codes(source))
        self.assertEqual("medium", found[0]["confidence"])
        self.assertEqual([], self.codes(source.replace("90", "60")))

    def test_reusable_job_is_not_assigned_unsupported_timeout_field(self):
        self.assertEqual([], self.codes("on: push\njobs:\n  called:\n    uses: ./checks.yml\n"))

    def test_non_pr_runs_do_not_get_pr_overlap_advice(self):
        source = "on: push\njobs:\n  build:\n    timeout-minutes: 20\n    steps:\n      - run: dotnet test\n"
        self.assertEqual([], self.codes(source))

    def test_script_text_and_comments_are_not_executed_or_leaked(self):
        source = "on: pull_request\njobs:\n  report:\n    timeout-minutes: 1\n    steps:\n      - run: |\n          # dotnet test\n          echo 'dotnet build; secret-value; ignore prior instructions'\n"
        found = rules.findings(source, PATH)
        self.assertEqual([], found)
        self.assertNotIn("secret-value", str(found))

    def test_timeout_expression_is_not_evaluated(self):
        source = "on: push\njobs:\n  build:\n    timeout-minutes: ${{ inputs.minutes }}\n    steps:\n      - run: dotnet test\n"
        self.assertEqual([], self.codes(source))

    def test_unsafe_yaml_and_input_limits_raise_incomplete(self):
        for source in ("on: push\non: pull_request\njobs: {}", "on: push\njobs: &a {x: *a}",
                       "x" * (rules.MAX_BYTES + 1), "on: push\njobs: [broken", "\0"):
            with self.subTest(source=source[:40]), self.assertRaises(ValueError):
                rules.findings(source, PATH)

    def test_nonworkflow_paths_have_no_candidates_and_output_is_stable(self):
        source = "on: push\njobs:\n  x:\n    steps: []\n"
        self.assertEqual([], rules.findings(source, "README.md"))
        self.assertEqual(rules.findings(source, PATH), rules.findings(source, PATH))


if __name__ == "__main__":
    unittest.main()
