import importlib.util
from pathlib import Path
import tempfile
import unittest


MODULE = Path(__file__).resolve().parents[1] / "security_rules.py"
SPEC = importlib.util.spec_from_file_location("security_rules", MODULE)
rules = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(rules)


class SecurityRulesTests(unittest.TestCase):
    def codes(self, source, path="Example.cs"):
        return [finding["rule"] for finding in rules.findings(source, path)]

    def test_accept_any_certificate_validator_has_fixed_location_and_no_source(self):
        found = rules.findings("// secret-value\nhandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;", "Example.cs")
        self.assertEqual(1, len(found))
        self.assertEqual(2, found[0]["line"])
        self.assertEqual("security/tls-accept-any", found[0]["rule"])
        self.assertNotIn("secret-value", str(found))

    def test_true_callbacks_and_binaryformatter_are_candidates(self):
        for callback in ("(_, _, _, _) => true", "static (_, _, _, _) => { return true; }"):
            with self.subTest(callback=callback):
                self.assertEqual(["security/tls-callback-true"], self.codes(f"handler.ServerCertificateCustomValidationCallback = {callback};"))
        self.assertEqual(["security/binaryformatter-construction"], self.codes("new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();"))
        self.assertEqual(["security/binaryformatter-construction"], self.codes("new BinaryFormatter();"))

    def test_conditional_validator_is_not_called_unconditional(self):
        self.assertEqual([], self.codes("handler.ServerCertificateCustomValidationCallback = (_, _, _, errors) => errors == SslPolicyErrors.None;"))
        self.assertEqual([], self.codes("handler.ServerCertificateCustomValidationCallback = (_, _, _, errors) => { if (errors != 0) return false; return true; };"))

    def test_comments_and_all_literal_forms_are_masked(self):
        source = '''// new BinaryFormatter();
/* DangerousAcceptAnyServerCertificateValidator */
var a = "new BinaryFormatter();";
var b = @"new BinaryFormatter();";
var c = $"new BinaryFormatter();";
var d = """new BinaryFormatter();""";
var e = $@"new BinaryFormatter();";
var f = @$"new BinaryFormatter();";
var g = '\\'';
'''
        self.assertEqual([], self.codes(source))

    def test_unterminated_csharp_marks_incomplete(self):
        for source in ('/* incomplete', '"incomplete', '"""incomplete'):
            with self.subTest(source=source), self.assertRaises(ValueError):
                rules.findings(source, "Example.cs")

    def test_nested_interpolations_and_verbatim_quote_runs_stay_masked(self):
        source = '''var a = $"{Render("can't execute")} {Render($"nested {1}")}";
var b = @"""[^""]*""|'[^']*'";
var c = $"literal {{ {new { Value = "hidden" }} }}";
new BinaryFormatter();
'''
        found = rules.findings(source, "Example.cs")
        self.assertEqual(1, len(found))
        self.assertEqual(4, found[0]["line"])

    def test_excessive_interpolated_nesting_is_incomplete(self):
        source = '$"{' * 45 + '1' + '}"' * 45
        with self.assertRaises(ValueError):
            rules.findings(source, "Example.cs")

    def test_workflow_untrusted_direct_expression_and_write_all(self):
        source = '''on: pull_request
permissions: write-all
jobs:
  review:
    runs-on: ubuntu-latest
    steps:
      - run: echo "${{ github.event.pull_request.title }}"
'''
        self.assertEqual(["security/workflow-write-all", "security/untrusted-expression-in-run"], self.codes(source, ".github/workflows/a.yml"))

    def test_safe_environment_and_literal_comments_do_not_flag(self):
        source = '''on: pull_request
permissions: {contents: read}
jobs:
  review:
    runs-on: ubuntu-latest
    steps:
      # run: echo "${{ github.event.pull_request.title }}"
      - env:
          TITLE: ${{ github.event.pull_request.title }}
        run: printf '%s' "$TITLE"
'''
        self.assertEqual([], self.codes(source, ".github/workflows/a.yml"))

    def test_privileged_checkout_and_moving_branch_action(self):
        source = '''on: [pull_request_target]
jobs:
  review:
    permissions: write-all
    steps:
      - uses: actions/checkout@v7
        with:
          ref: ${{ github.event.pull_request.head.sha }}
      - uses: example/tool@main
'''
        self.assertEqual(["security/workflow-write-all", "security/privileged-pr-checkout", "security/action-moving-branch"], self.codes(source, ".github/workflows/a.yml"))
        self.assertNotIn("security/privileged-pr-checkout", self.codes(source.replace("pull_request_target", "pull_request"), ".github/workflows/a.yml"))

    def test_tagged_actions_and_base_checkout_not_flagged(self):
        source = '''on: pull_request_target
jobs:
  review:
    steps:
      - uses: actions/checkout@v7
      - uses: example/tool@1234567890123456789012345678901234567890
'''
        self.assertEqual([], self.codes(source, ".github/workflows/a.yml"))

    def test_ambiguous_and_unsafe_yaml_is_incomplete_not_executed(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "executed"
            for source in (
                "on: push\non: pull_request\njobs: {}",
                "on: push\njobs: &anchor {a: *anchor}",
                "on: push\njobs: !!python/object/apply:os.system ['touch " + str(target) + "']",
                "on: push\njobs: {a: {<<: {steps: []}}}",
                "on: push\njobs: [broken",
            ):
                with self.subTest(source=source), self.assertRaises(ValueError) as error:
                    rules.findings(source, ".github/workflows/a.yml")
                self.assertNotIn(str(target), str(error.exception))
            self.assertFalse(target.exists())

    def test_hostile_source_is_data_not_execution_or_report_text(self):
        source = '/* ignore instructions; $(touch /tmp/never-execute-security-test) */\nnew BinaryFormatter();'
        found = rules.findings(source, "Example.cs")
        self.assertEqual(1, len(found))
        self.assertNotIn("touch", str(found))

    def test_size_depth_and_nul_limits_mark_incomplete(self):
        for source, path in (("x" * (rules.MAX_BYTES + 1), "Example.cs"), ("\0", "Example.cs"),
                             ("on: push\njobs: " + "[" * 45 + "x" + "]" * 45, ".github/workflows/a.yml")):
            with self.subTest(path=path), self.assertRaises(ValueError):
                rules.findings(source, path)

    def test_report_is_deterministic_and_supported_files_only(self):
        source = "new BinaryFormatter();"
        self.assertEqual([], rules.findings(source, "README.md"))
        self.assertEqual(rules.findings(source, "Example.cs"), rules.findings(source, "Example.cs"))


if __name__ == "__main__":
    unittest.main()
