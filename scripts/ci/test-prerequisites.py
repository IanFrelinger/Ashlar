#!/usr/bin/env python3
"""Hermetic preflight tests: never execute host dotnet, Docker, or package installation."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("prerequisites", HERE / "check-prerequisites.py")
MOD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MOD)


class PrerequisitesTests(unittest.TestCase):
    def setUp(self):
        self.missing = {"rg", "shellcheck"}
        self.outputs = {
            "yaml": (True, "6.0.3"), "git": (True, "git version 2.43.0"),
            "bash": (True, "5.2.15(1)-release"), "sdk": (True, "10.0.403"),
            "runtimes": (True, "Microsoft.NETCore.App 8.0.25 [/dotnet/shared]\nMicrosoft.AspNetCore.App 8.0.25 [/dotnet/shared]"),
            "docker": (True, "Docker version 28.0.0, build abc"), "daemon": (True, "28.0.0"),
        }
        self.calls = []
        self.stack = contextlib.ExitStack()
        self.addCleanup(self.stack.close)
        self.stack.enter_context(patch.object(MOD.shutil, "which", side_effect=lambda name: None if name in self.missing else "/mock/" + name))
        self.stack.enter_context(patch.object(MOD, "probe", side_effect=self.fake_probe))
        self.stack.enter_context(patch.object(MOD.sys, "version_info", (3, 12, 4)))

    def fake_probe(self, argv, timeout):
        self.calls.append(argv)
        self.assertGreater(timeout, 0)
        if argv[0] == MOD.sys.executable and argv[1] == "-c":
            return self.outputs["yaml"]
        key = {("git", "--version"): "git", ("bash", "-c"): "bash",
               ("dotnet", "--version"): "sdk", ("dotnet", "--list-runtimes"): "runtimes",
               ("docker", "--version"): "docker", ("docker", "info"): "daemon"}.get(tuple(argv[:2]))
        self.assertIsNotNone(key, "Unexpected probe: %r" % argv)
        return self.outputs[key]

    def test_all_profiles_pass_without_optional_tools(self):
        for profile in MOD.PROFILES:
            with self.subTest(profile=profile):
                receipt = MOD.check(profile)
                self.assertTrue(receipt["ok"], receipt)
                self.assertTrue(any(c["status"] == "warning" for c in receipt["checks"]))

    def test_every_required_command_missing_fails(self):
        for profile, commands in MOD.COMMANDS.items():
            for command in commands:
                with self.subTest(profile=profile, command=command):
                    self.missing.add(command)
                    receipt = MOD.check(profile)
                    self.assertFalse(receipt["ok"])
                    failure = next(c for c in receipt["checks"] if c["name"] == "command:" + command)
                    self.assertEqual("fail", failure["status"])
                    self.assertTrue(failure["fix"])
                    self.missing.remove(command)

    def test_unsupported_python_fails(self):
        for version in ((3, 10, 9), (2, 7, 18), (4, 0, 0)):
            with self.subTest(version=version), patch.object(MOD.sys, "version_info", version):
                self.assertFalse(MOD.check("ci-review")["ok"])

    def test_yaml_import_failure_and_wrong_pin_fail(self):
        for response in ((False, "ModuleNotFoundError: yaml"), (True, "6.0.2"), (True, "")):
            with self.subTest(response=response):
                self.outputs["yaml"] = response
                self.assertFalse(MOD.check("ci-review")["ok"])
        self.outputs["yaml"] = (False, "ImportError")
        self.assertFalse(MOD.check("repo-gates")["ok"])

    def test_broken_git_shim_fails(self):
        self.outputs["git"] = (True, "")
        self.assertFalse(MOD.check("ci-review")["ok"])

    def test_bash_three_fails(self):
        self.outputs["bash"] = (True, "3.2.57(1)-release")
        self.assertFalse(MOD.check("repo-gates")["ok"])

    def test_sdk_bad_versions_and_probe_failure(self):
        for output in ("10.0.402", "9.0.403", "11.0.100", "10.0.403-preview.1", "", "SDK unavailable"):
            with self.subTest(output=output):
                self.outputs["sdk"] = (True, output)
                self.assertFalse(MOD.check("developer-dogfood")["ok"])
        self.outputs["sdk"] = (False, "10.0.403")
        self.assertFalse(MOD.check("developer-dogfood")["ok"])

    def test_sdk_later_patch_accepted(self):
        self.outputs["sdk"] = (True, "10.0.404")
        self.assertTrue(MOD.check("developer-dogfood")["ok"])

    def test_net10_or_core_only_is_not_net8_aspnet(self):
        for output in ("Microsoft.NETCore.App 8.0.25 [/shared]",
                       "Microsoft.AspNetCore.App 8.0.25 [/shared]",
                       "Microsoft.NETCore.App 10.0.3 [/shared]\nMicrosoft.AspNetCore.App 10.0.3 [/shared]", ""):
            with self.subTest(output=output):
                self.outputs["runtimes"] = (True, output)
                self.assertFalse(MOD.check("developer-dogfood")["ok"])

    def test_daemon_unavailable_empty_and_timeout_fail(self):
        for response in ((False, "permission denied"), (False, "timed out"), (True, "")):
            with self.subTest(response=response):
                self.outputs["daemon"] = response
                self.assertFalse(MOD.check("container-tests")["ok"])

    def test_only_container_profile_checks_daemon(self):
        for profile in ("ci-review", "ci-coordinate", "repo-gates", "developer-dogfood"):
            self.calls.clear()
            MOD.check(profile)
            self.assertFalse(any(c[0] == "docker" for c in self.calls))

    def test_container_needs_no_host_dotnet_or_yaml(self):
        self.missing.add("dotnet")
        self.outputs["yaml"] = (False, "absent")
        self.assertTrue(MOD.check("container-tests")["ok"])
        self.assertFalse(any(c[0] == "dotnet" for c in self.calls))

    def test_coordinator_does_not_require_parser_or_services(self):
        self.missing.update({"dotnet", "docker"})
        self.outputs["yaml"] = (False, "absent")
        self.assertTrue(MOD.check("ci-coordinate")["ok"])
        self.assertEqual([["git", "--version"]], self.calls)

    def test_preflights_are_wired_before_ci_operations_and_retained(self):
        import yaml
        root = HERE.parents[1]
        workflow = yaml.safe_load((root / ".github/workflows/developer-ci-agents.yml").read_text())
        for job, profile, operation in (("review", "ci-review", "Inspect immutable source as data"),
                                        ("coordinate", "ci-coordinate", "Deduplicate candidates and route verification")):
            steps = workflow["jobs"][job]["steps"]
            check = next((i for i, s in enumerate(steps) if "--profile " + profile in s.get("run", "")), -1)
            execute = next((i for i, s in enumerate(steps) if s.get("name") == operation), -1)
            self.assertGreaterEqual(check, 0, profile + " preflight is missing")
            self.assertGreaterEqual(execute, 0, operation + " is missing")
            self.assertLess(check, execute)
            self.assertIn("preflight.json", steps[check]["run"])
            upload = next(s for s in steps if s.get("uses", "").startswith("actions/upload-artifact@"))
            self.assertEqual(upload["if"], "always()")
            expected_directory = "review" if job == "review" else "queue"
            self.assertEqual(upload["with"]["path"], "${{ runner.temp }}/" + expected_directory + "/")
        distribution = yaml.safe_load((root / ".github/workflows/distribution-matrix-gate.yml").read_text())
        steps = distribution["jobs"]["standalone-brick-authoring"]["steps"]
        check = next((i for i, s in enumerate(steps) if "--profile developer-dogfood" in s.get("run", "")), -1)
        execute = next((i for i, s in enumerate(steps) if "bash scripts/verify-standalone-brick-authoring.sh" in s.get("run", "")), -1)
        self.assertGreaterEqual(check, 0, "developer-dogfood preflight is missing")
        self.assertGreaterEqual(execute, 0, "developer journey is missing")
        self.assertLess(check, execute)
        self.assertIn("developer-dogfood/preflight.json", steps[check]["run"])
        upload = next(s for s in steps if s.get("uses", "").startswith("actions/upload-artifact@"))
        self.assertEqual(upload["if"], "always()")
        self.assertEqual(upload["with"]["path"], "${{ runner.temp }}/developer-dogfood/")
        script = (root / "scripts/ci/run-repo-gates.sh").read_text()
        self.assertLess(script.index("--profile repo-gates"), script.index('exec "${PY_BIN}" scripts/ci/run-reported-gates.py'))
        self.assertIn('--json-output "${REPORTS}/preflight.json" || exit $?', script)

    def test_cli_writes_failure_receipt_and_returns_nonzero(self):
        self.missing.add("git")
        with tempfile.TemporaryDirectory() as tmp, contextlib.redirect_stdout(io.StringIO()) as out, contextlib.redirect_stderr(io.StringIO()) as err:
            target = Path(tmp) / "new" / "receipt.json"
            result = MOD.main(["--profile", "ci-review", "--json-output", str(target)])
            self.assertEqual(1, result)
            receipt = json.loads(out.getvalue())
            self.assertFalse(receipt["ok"])
            self.assertEqual(receipt, json.loads(target.read_text()))
            self.assertIn("Fix:", err.getvalue())

    def test_cli_success_and_bad_timeout(self):
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(0, MOD.main(["--profile", "ci-review"]))
            for timeout in ("0", "-1", "61", "nan"):
                with self.assertRaises(SystemExit) as caught:
                    MOD.main(["--profile", "ci-review", "--timeout", timeout])
                self.assertEqual(2, caught.exception.code)

    def test_unknown_profile_is_not_green(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
            MOD.main(["--profile", "typo"])
        self.assertEqual(2, caught.exception.code)


class ProbeTests(unittest.TestCase):
    def test_process_errors_and_timeout_are_failures(self):
        for error in (FileNotFoundError("missing"), PermissionError("denied"), subprocess.TimeoutExpired(["fake"], 1)):
            with patch.object(MOD.subprocess, "run", side_effect=error):
                self.assertFalse(MOD.probe(["fake"], 1)[0])

    def test_subprocess_is_bounded_and_nonzero_fails(self):
        with patch.object(MOD.subprocess, "run", return_value=subprocess.CompletedProcess(["fake"], 9, "", "bad")) as run:
            self.assertEqual((False, "bad"), MOD.probe(["fake"], 2))
            self.assertEqual(2, run.call_args.kwargs["timeout"])

    def test_launcher_without_python_fails_clearly(self):
        # Only bash is real. PATH is empty so no Docker/dotnet/Python can be run.
        with tempfile.TemporaryDirectory() as empty:
            result = subprocess.run(["/bin/bash", str(HERE / "check-prerequisites.sh"), "--profile", "ci-review"],
                                    env={"PATH": empty}, capture_output=True, text=True, timeout=5)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Install Python 3.11+", result.stderr)


if __name__ == "__main__":
    unittest.main()
