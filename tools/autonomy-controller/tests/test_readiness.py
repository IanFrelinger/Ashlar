"""Bounded readiness: the three gate parsers on the exact shapes the scripts print, identities that
survive noise and notice a different failing set, one gate run with no retry, and one fixer launch
with exactly one general-purpose fallback on a missing agent role."""
import json
import os
import re
import subprocess
import unittest
from pathlib import Path
from typing import List

from ashlar_controller.adapters.fake import FakeAdapter
from ashlar_controller.models import LaunchRefused, LaunchRequest, LaunchResult, StopReason
from ashlar_controller.readiness import (GATE_COMMANDS, MISSING_ROLE_PATTERNS, GateFailure, GateInfrastructureError,
                                         GateReport, failure_identity, fixer_request, launch_fixer, normalise,
                                         parse_container_test, parse_readiness_json, parse_repo_gates, refuted,
                                         run_gate)

IDENTITY = re.compile(r"^gate:[^:]+:[0-9a-f]{12}$")


# ---------------------------------------------------------------------------------------------------
# Canned outputs, in the shapes scripts/readiness-gate-local.sh, scripts/ci/run-repo-gates.sh and
# scripts/test-in-container.sh (dotnet test) print today. All "\n"; one test re-runs them as "\r\n".
# ---------------------------------------------------------------------------------------------------

def readiness_json(failed_tail: str = "", commit: str = "1d8bc7448abc", started: str = "2026-10-11T03:04:05Z",
                   log_dir: str = "/tmp/readiness-gate-31337", dur: int = 12) -> str:
    gates = [
        {"gate": "build", "status": "PASS", "exit_code": 0, "duration_s": dur, "log": f"{log_dir}/build.log",
         "log_tail": ""},
        {"gate": "interface", "status": "FAIL" if failed_tail else "PASS", "exit_code": 1 if failed_tail else 0,
         "duration_s": dur + 3, "log": f"{log_dir}/interface.log", "log_tail": failed_tail},
        {"gate": "daemon", "status": "PASS", "exit_code": 0, "duration_s": dur + 7, "log": f"{log_dir}/daemon.log",
         "log_tail": ""},
    ]
    return json.dumps({"layer": "application", "commit": commit, "started_at": started,
                       "failed": 1 if failed_tail else 0, "gates": gates})


INTERFACE_TAIL_A = (
    "2026-10-11T03:04:19Z apps-gate-checks: interface\n"
    "  Failed Ashlar.CLI.Tests.InterfaceTests.HelpListsEveryVerb [412 ms]\n"
    "  Error Message:\n"
    "   Assert.Contains() Failure: 'dashboard' not found\n"
    "  Stack Trace:\n"
    "     at Ashlar.CLI.Tests.InterfaceTests.HelpListsEveryVerb() in /repo/application/tests/InterfaceTests.cs:line 42\n"
    "Failed!  - Failed: 1, Passed: 17, Skipped: 0, Total: 18, Duration: 2 s - Ashlar.CLI.Tests.dll (net10.0)\n"
    "run 9f3e2c1ab77d finished at 03:04:21\n"
)
# The same failure on another day, on another machine, in another temp dir, with other timings.
INTERFACE_TAIL_A_NOISE = (
    "2026-10-12T22:41:03Z apps-gate-checks: interface\n"
    "  Failed Ashlar.CLI.Tests.InterfaceTests.HelpListsEveryVerb [1388 ms]\n"
    "  Error Message:\n"
    "   Assert.Contains() Failure: 'dashboard' not found\n"
    "  Stack Trace:\n"
    "     at Ashlar.CLI.Tests.InterfaceTests.HelpListsEveryVerb() in C:\\w\\clone\\application\\tests\\InterfaceTests.cs:line 42\n"
    "Failed!  - Failed: 1, Passed: 17, Skipped: 0, Total: 18, Duration: 11 s - Ashlar.CLI.Tests.dll (net10.0)\n"
    "run 0c77aa19beef finished at 22:41:17\n"
)
# A different failing test.
INTERFACE_TAIL_B = INTERFACE_TAIL_A.replace("HelpListsEveryVerb", "VersionPrintsSemver")


def repo_gates_output(failing: List[str] = (), gates: List[str] = None, python_tail: str = "") -> str:
    gates = gates or ["scripts/ci/test-commercial-receipts.py", "tests/scripts/autonomy-controller.test.sh",
                      "tests/scripts/mutation-check.test.sh"]
    out = [f"repo-gates: discovered {len(gates)} gate(s)"]
    for g in gates:
        out += ["", f"::group::{g}"]
        if g in failing:
            out += (python_tail or "FAIL: test_floor (tests.test_cli.Floor)\nRan 73 tests in 2.137s\n\nFAILED (failures=1)").split("\n")
            out += ["::endgroup::", f"::error::{g} failed (exit 1)"]
        else:
            out += [f"{Path(g).name}: 12 tests passed", "::endgroup::"]
    out.append("")
    if failing:
        out.append(f"repo-gates: {len(failing)} of {len(gates)} gate(s) failed:")
        out += [f"  {g}" for g in failing]
    else:
        out.append(f"repo-gates: all {len(gates)} gate(s) passed")
    return "\n".join(out) + "\n"


def dotnet_output(failed: List[str] = (), passed: int = 12, assembly: str = "Ashlar.Tests.Infrastructure",
                  tfm: str = "net10.0", ms: int = 3, path: str = "/repo/src/Ashlar.Tests.Infrastructure/bin/Debug") -> str:
    out = [f"Test run for {path}/{tfm}/{assembly}.dll ({tfm})",
           "Microsoft (R) Test Execution Command Line Tool Version 17.14.0 (x64)",
           "Starting test execution, please wait...",
           f"A total of 1 test files matched the specified pattern."]
    for i, t in enumerate(failed):
        out += [f"  Failed {t} [{ms + i} ms]", "  Error Message:", "   Assert.Equal() Failure", "  Stack Trace:",
                f"     at {t}() in {path}/../../{t.split('.')[-2]}.cs:line {10 + i}"]
    word = "Failed" if failed else "Passed"
    out.append(f"{word}!  - Failed: {len(failed)}, Passed: {passed}, Skipped: 0, Total: {len(failed) + passed}, "
               f"Duration: {ms * 7} ms - {assembly}.dll ({tfm})")
    return "\n".join(out) + "\n"


T1 = "Ashlar.Tests.Infrastructure.Cert.LeaseTests.ExpiredLeaseIsNotTakenWhileHolderLives"
T2 = "Ashlar.Tests.Infrastructure.Cert.StoreTests.CorruptStateStopsDispatch"
T3 = "Ashlar.Tests.Infrastructure.Cert.BudgetTests.ReservationSurvivesRestart"


def crlf(text: str) -> str:
    return text.replace("\n", "\r\n")


# ---------------------------------------------------------------------------------------------------
# normalise and failure_identity
# ---------------------------------------------------------------------------------------------------

class Normalise(unittest.TestCase):
    def test_drops_iso_timestamp(self):
        self.assertEqual(normalise("2026-10-11T03:04:05Z gate started"), "<ts> gate started")
        self.assertEqual(normalise("at 2026-10-11 03:04:05.123+02:00 done"), "at <ts> done")

    def test_drops_bare_clock(self):
        self.assertEqual(normalise("finished at 03:04:21"), "finished at <time>")

    def test_drops_durations(self):
        self.assertEqual(normalise("  Failed X.Y [412 ms]"), "Failed X.Y [<dur>]")
        self.assertEqual(normalise("exit=1 dur=3s"), "exit=1 dur=<dur>")
        self.assertEqual(normalise("Duration: 1 s - A.dll"), "Duration: <dur> - A.dll")
        self.assertEqual(normalise("Ran 73 tests in 2.137s"), "Ran 73 tests in <dur>")

    def test_keeps_basename_of_absolute_paths(self):
        self.assertEqual(normalise("in /repo/application/tests/InterfaceTests.cs:line 42"),
                         "in InterfaceTests.cs:line 42")
        self.assertEqual(normalise("in C:\\w\\clone\\tests\\InterfaceTests.cs:line 42"), "in InterfaceTests.cs:line 42")
        self.assertEqual(normalise("in C:/w/clone/tests/InterfaceTests.cs:line 42"), "in InterfaceTests.cs:line 42")

    def test_keeps_relative_paths(self):
        self.assertEqual(normalise("::error::scripts/ci/test-x.py failed (exit 1)"),
                         "::error::scripts/ci/test-x.py failed (exit 1)")

    def test_drops_temp_dirs(self):
        self.assertEqual(normalise("log=/tmp/readiness-gate-31337/build.log"), "log=<tmp>")
        self.assertEqual(normalise("log=C:\\Users\\x\\AppData\\Local\\Temp\\rg-9\\b.log"), "log=<tmp>")

    def test_drops_hex_ids_of_seven_or_more(self):
        self.assertEqual(normalise("run 9f3e2c1ab77d finished"), "run <hex> finished")
        self.assertEqual(normalise("commit 1d8bc74"), "commit <hex>")
        self.assertEqual(normalise("exit 1 abcdef"), "exit 1 abcdef")   # six chars: kept
        self.assertEqual(normalise("id 7c9e6679-7425-40de-944b-e07fc1f90ae7 x"), "id <id> x")

    def test_strips_cr_and_trailing_whitespace_and_collapses_runs(self):
        self.assertEqual(normalise("Failed!  - Failed: 1  \r"), "Failed! - Failed: 1")

    def test_keeps_the_signal(self):
        self.assertEqual(normalise("  Failed Ashlar.Tests.Cert.LeaseTests.Expired [3 ms]"),
                         "Failed Ashlar.Tests.Cert.LeaseTests.Expired [<dur>]")


class FailureIdentity(unittest.TestCase):
    def test_shape(self):
        ident = failure_identity("interface", ["a", "b"])
        self.assertRegex(ident, IDENTITY)
        self.assertTrue(ident.startswith("gate:interface:"))

    def test_equal_for_outputs_differing_only_in_noise(self):
        self.assertEqual(failure_identity("interface", INTERFACE_TAIL_A.split("\n")),
                         failure_identity("interface", INTERFACE_TAIL_A_NOISE.split("\n")))

    def test_different_when_the_failing_test_differs(self):
        self.assertNotEqual(failure_identity("interface", INTERFACE_TAIL_A.split("\n")),
                            failure_identity("interface", INTERFACE_TAIL_B.split("\n")))

    def test_different_when_the_gate_differs(self):
        lines = INTERFACE_TAIL_A.split("\n")
        self.assertNotEqual(failure_identity("interface", lines), failure_identity("daemon", lines))

    def test_order_duplicates_and_blank_lines_do_not_matter(self):
        self.assertEqual(failure_identity("g", ["Failed B", "Failed A"]),
                         failure_identity("g", ["", "Failed A", "Failed B", "Failed A", "   "]))

    def test_identity_is_over_normalised_lines(self):
        self.assertEqual(failure_identity("g", ["Failed A [3 ms]"]), failure_identity("g", ["Failed A [900 ms]"]))
        self.assertNotEqual(failure_identity("g", ["Failed A"]), failure_identity("g", ["Failed A, B"]))


# ---------------------------------------------------------------------------------------------------
# Parsers
# ---------------------------------------------------------------------------------------------------

class ParseReadinessJson(unittest.TestCase):
    def test_passing(self):
        r = parse_readiness_json(readiness_json())
        self.assertEqual((r.source, r.commit, r.failures, r.passed), ("readiness-local", "1d8bc7448abc", [],
                                                                      ["build", "interface", "daemon"]))
        self.assertTrue(r.ok)
        self.assertEqual(r.raw["layer"], "application")

    def test_failing(self):
        r = parse_readiness_json(readiness_json(INTERFACE_TAIL_A))
        self.assertEqual(r.passed, ["build", "daemon"])
        self.assertEqual([f.gate for f in r.failures], ["interface"])
        f = r.failures[0]
        self.assertRegex(f.identity, IDENTITY)
        self.assertIn("HelpListsEveryVerb", f.evidence)
        self.assertFalse(r.ok)

    def test_unknown_commit_is_none(self):
        self.assertIsNone(parse_readiness_json(readiness_json(commit="unknown")).commit)

    def test_identity_stable_across_noise(self):
        a = parse_readiness_json(readiness_json(INTERFACE_TAIL_A))
        b = parse_readiness_json(readiness_json(INTERFACE_TAIL_A_NOISE, commit="deadbeef0001",
                                                started="2026-10-12T22:41:00Z", log_dir="/tmp/readiness-gate-7",
                                                dur=400))
        self.assertEqual(a.failures[0].identity, b.failures[0].identity)

    def test_identity_differs_for_another_failing_test(self):
        a = parse_readiness_json(readiness_json(INTERFACE_TAIL_A))
        b = parse_readiness_json(readiness_json(INTERFACE_TAIL_B))
        self.assertNotEqual(a.failures[0].identity, b.failures[0].identity)

    def test_failure_without_tail_still_has_evidence(self):
        obj = json.loads(readiness_json())
        obj["gates"][0].update(status="FAIL", exit_code=137, log_tail="")
        r = parse_readiness_json(json.dumps(obj))
        self.assertEqual(r.failures[0].evidence, "build failed (exit 137)")

    def test_not_json_is_infrastructure(self):
        with self.assertRaises(GateInfrastructureError) as cm:
            parse_readiness_json("readiness-gate-local: jq not found; cannot write requested --json output\n")
        self.assertIn("jq not found", cm.exception.evidence)
        with self.assertRaises(GateInfrastructureError):
            parse_readiness_json(json.dumps({"layer": "application"}))

    def test_crlf(self):
        a = parse_readiness_json(readiness_json(INTERFACE_TAIL_A))
        b = parse_readiness_json(readiness_json(crlf(INTERFACE_TAIL_A)))
        self.assertEqual(a.failures[0].identity, b.failures[0].identity)


class ParseRepoGates(unittest.TestCase):
    def test_all_passed(self):
        r = parse_repo_gates(repo_gates_output())
        self.assertEqual(r.source, "repo-gates")
        self.assertEqual(r.failures, [])
        self.assertEqual(len(r.passed), 3)
        self.assertIn("tests/scripts/mutation-check.test.sh", r.passed)
        self.assertEqual(r.raw["summary"], "passed")

    def test_some_failed(self):
        r = parse_repo_gates(repo_gates_output(failing=["tests/scripts/autonomy-controller.test.sh"]))
        self.assertEqual([f.gate for f in r.failures], ["tests/scripts/autonomy-controller.test.sh"])
        self.assertEqual(r.passed, ["scripts/ci/test-commercial-receipts.py", "tests/scripts/mutation-check.test.sh"])
        f = r.failures[0]
        self.assertRegex(f.identity, IDENTITY)
        self.assertIn("::error::tests/scripts/autonomy-controller.test.sh failed (exit 1)", f.evidence)
        self.assertIn("FAIL: test_floor", f.evidence)
        self.assertEqual(r.raw["listed"], ["tests/scripts/autonomy-controller.test.sh"])

    def test_two_failed_gates_are_two_failures(self):
        r = parse_repo_gates(repo_gates_output(failing=["scripts/ci/test-commercial-receipts.py",
                                                        "tests/scripts/mutation-check.test.sh"]))
        self.assertEqual(len(r.failures), 2)
        self.assertNotEqual(r.failures[0].identity, r.failures[1].identity)

    def test_no_summary_is_infrastructure(self):
        text = ("repo-gates: discovered 3 gate(s)\n"
                "repo-gates: ERROR: discovered 3 gate(s), expected at least 11.\n"
                "            A glob has stopped matching, so this runner would pass having checked almost\n")
        with self.assertRaises(GateInfrastructureError) as cm:
            parse_repo_gates(text)
        self.assertIn("expected at least 11", cm.exception.evidence)

    def test_identity_stable_across_noise(self):
        tail_a = ("FAIL: test_floor (tests.test_cli.Floor)\n"
                  "  File \"/repo/tools/autonomy-controller/tests/test_cli.py\", line 12, in test_floor\n"
                  "Ran 73 tests in 2.137s\n\nFAILED (failures=1)")
        tail_b = ("FAIL: test_floor (tests.test_cli.Floor)\n"
                  "  File \"C:\\w\\c\\tools\\autonomy-controller\\tests\\test_cli.py\", line 12, in test_floor\n"
                  "Ran 73 tests in 9.004s\n\nFAILED (failures=1)")
        a = parse_repo_gates(repo_gates_output(failing=["tests/scripts/autonomy-controller.test.sh"], python_tail=tail_a))
        b = parse_repo_gates(repo_gates_output(failing=["tests/scripts/autonomy-controller.test.sh"], python_tail=tail_b))
        self.assertEqual(a.failures[0].identity, b.failures[0].identity)

    def test_identity_differs_when_the_failing_gate_differs(self):
        a = parse_repo_gates(repo_gates_output(failing=["tests/scripts/autonomy-controller.test.sh"]))
        b = parse_repo_gates(repo_gates_output(failing=["tests/scripts/mutation-check.test.sh"]))
        self.assertNotEqual(a.failures[0].identity, b.failures[0].identity)

    def test_identity_differs_when_the_failing_test_differs(self):
        a = parse_repo_gates(repo_gates_output(failing=["tests/scripts/autonomy-controller.test.sh"],
                                               python_tail="FAIL: test_floor (tests.test_cli.Floor)\nFAILED (failures=1)"))
        b = parse_repo_gates(repo_gates_output(failing=["tests/scripts/autonomy-controller.test.sh"],
                                               python_tail="FAIL: test_lease (tests.test_lease.Lease)\nFAILED (failures=1)"))
        self.assertNotEqual(a.failures[0].identity, b.failures[0].identity)

    def test_crlf(self):
        a = parse_repo_gates(repo_gates_output(failing=["tests/scripts/mutation-check.test.sh"]))
        b = parse_repo_gates(crlf(repo_gates_output(failing=["tests/scripts/mutation-check.test.sh"])))
        self.assertEqual([f.identity for f in a.failures], [f.identity for f in b.failures])
        self.assertEqual(a.passed, b.passed)


class ParseContainerTest(unittest.TestCase):
    def test_passing(self):
        r = parse_container_test(dotnet_output())
        self.assertEqual((r.source, r.failures, r.passed),
                         ("container-tests", [], ["Ashlar.Tests.Infrastructure.dll (net10.0)"]))
        self.assertEqual(r.raw["assemblies"]["Ashlar.Tests.Infrastructure.dll (net10.0)"]["passed"], 12)

    def test_failing_names_the_tests(self):
        r = parse_container_test(dotnet_output(failed=[T1, T2]))
        self.assertEqual(r.passed, [])
        self.assertEqual([f.gate for f in r.failures], ["Ashlar.Tests.Infrastructure.dll (net10.0)"])
        f = r.failures[0]
        self.assertRegex(f.identity, IDENTITY)
        self.assertEqual(f.evidence, f"Failed {T1}\nFailed {T2}")

    def test_identity_stable_across_order_timing_and_paths(self):
        a = parse_container_test(dotnet_output(failed=[T1, T2], ms=3))
        b = parse_container_test(dotnet_output(failed=[T2, T1], ms=900, path="/w/x/bin/Release"))
        self.assertEqual(a.failures[0].identity, b.failures[0].identity)

    def test_identity_stable_when_passing_count_grows(self):
        a = parse_container_test(dotnet_output(failed=[T1], passed=12))
        b = parse_container_test(dotnet_output(failed=[T1], passed=13))
        self.assertEqual(a.failures[0].identity, b.failures[0].identity)

    def test_identity_differs_when_the_failing_set_differs(self):
        a = parse_container_test(dotnet_output(failed=[T1, T2]))
        b = parse_container_test(dotnet_output(failed=[T1, T3]))
        c = parse_container_test(dotnet_output(failed=[T1]))
        self.assertNotEqual(a.failures[0].identity, b.failures[0].identity)
        self.assertNotEqual(a.failures[0].identity, c.failures[0].identity)

    def test_identity_differs_when_the_assembly_differs(self):
        a = parse_container_test(dotnet_output(failed=[T1]))
        b = parse_container_test(dotnet_output(failed=[T1], assembly="Ashlar.CLI.Tests"))
        self.assertNotEqual(a.failures[0].identity, b.failures[0].identity)

    def test_two_assemblies(self):
        text = dotnet_output(failed=[T1]) + dotnet_output(assembly="Ashlar.CLI.Tests", tfm="net8.0")
        r = parse_container_test(text)
        self.assertEqual([f.gate for f in r.failures], ["Ashlar.Tests.Infrastructure.dll (net10.0)"])
        self.assertEqual(r.passed, ["Ashlar.CLI.Tests.dll (net8.0)"])

    def test_failed_count_without_failed_lines_uses_the_summary(self):
        text = ("Test run for /repo/bin/A.dll (net10.0)\n"
                "Failed!  - Failed: 2, Passed: 3, Skipped: 0, Total: 5, Duration: 4 s - A.dll (net10.0)\n")
        r = parse_container_test(text)
        self.assertEqual(len(r.failures), 1)
        self.assertIn("Failed: 2", r.failures[0].evidence)

    def test_build_error_without_summary_is_infrastructure(self):
        text = ("  Determining projects to restore...\n"
                "/repo/src/Ashlar.Core/Foo.cs(12,5): error CS1002: ; expected [/repo/src/Ashlar.Core/Ashlar.Core.csproj]\n"
                "Build FAILED.\n")
        with self.assertRaises(GateInfrastructureError) as cm:
            parse_container_test(text)
        self.assertIn("error CS1002", cm.exception.evidence)

    def test_crlf(self):
        a = parse_container_test(dotnet_output(failed=[T1, T2]))
        b = parse_container_test(crlf(dotnet_output(failed=[T1, T2])))
        self.assertEqual(a.failures[0].identity, b.failures[0].identity)
        self.assertEqual(a.failures[0].evidence, b.failures[0].evidence)


# ---------------------------------------------------------------------------------------------------
# run_gate: one call, no retry
# ---------------------------------------------------------------------------------------------------

class FakeRunner:
    """Records every call; answers from a script of outputs (text or exception) in order."""

    def __init__(self, outputs, returncode: int = 1, write_json: str = None):
        self.outputs = list(outputs)
        self.returncode = returncode
        self.write_json = write_json
        self.calls = []

    def __call__(self, argv, **kwargs):
        self.calls.append((list(argv), kwargs))
        if self.write_json is not None and "--json" in argv:
            Path(argv[argv.index("--json") + 1]).write_text(self.write_json, encoding="utf-8")
        out = self.outputs.pop(0) if self.outputs else ""
        if isinstance(out, BaseException):
            raise out
        return subprocess.CompletedProcess(argv, self.returncode, stdout=out, stderr="")


class RunGate(unittest.TestCase):
    def test_repo_gates_argv_cwd_and_report(self):
        runner = FakeRunner([repo_gates_output(failing=["tests/scripts/mutation-check.test.sh"])])
        r = run_gate("repo-gates", "/work/ws", runner=runner)
        self.assertEqual(len(runner.calls), 1)
        argv, kw = runner.calls[0]
        self.assertEqual(argv, ["bash", "scripts/ci/run-repo-gates.sh"])
        self.assertEqual(kw["cwd"], "/work/ws")
        self.assertEqual(kw["stderr"], subprocess.STDOUT)      # the failed list goes to stderr
        self.assertEqual(kw["timeout"], 3600)
        self.assertEqual([f.gate for f in r.failures], ["tests/scripts/mutation-check.test.sh"])
        self.assertEqual(r.raw["argv"], argv)
        self.assertEqual(r.raw["exit_code"], 1)
        self.assertEqual(r.raw["workspace"], "/work/ws")

    def test_container_tests_argv_mounts_the_workspace(self):
        runner = FakeRunner([dotnet_output()], returncode=0)
        r = run_gate("container-tests", "/work/ws", runner=runner, timeout=99)
        self.assertEqual(runner.calls[0][0], ["bash", "scripts/test-in-container.sh", "--repo", "/work/ws"])
        self.assertEqual(runner.calls[0][1]["timeout"], 99)
        self.assertTrue(r.ok)

    def test_readiness_local_reads_the_json_file_and_removes_it(self):
        runner = FakeRunner(["== build ==\nRESULT\t1\tbuild\tPASS\texit=0 dur=3s log=/tmp/x/build.log\n"],
                            returncode=1, write_json=readiness_json(INTERFACE_TAIL_A))
        r = run_gate("readiness-local", "/work/ws", runner=runner)
        argv = runner.calls[0][0]
        self.assertEqual(argv[:5], ["bash", "scripts/readiness-gate-local.sh", "--layer", "application", "--json"])
        self.assertTrue(os.path.isabs(argv[5]))
        self.assertFalse(Path(argv[5]).exists())
        self.assertEqual([f.gate for f in r.failures], ["interface"])
        self.assertEqual(r.commit, "1d8bc7448abc")

    def test_readiness_local_without_json_file_is_infrastructure(self):
        runner = FakeRunner(["readiness-gate-local: jq not found\n"], returncode=1)
        with self.assertRaises(GateInfrastructureError) as cm:
            run_gate("readiness-local", "/work/ws", runner=runner)
        self.assertIn("jq not found", cm.exception.evidence)
        self.assertEqual(len(runner.calls), 1)
        self.assertFalse(Path(runner.calls[0][0][5]).exists())

    def test_unparsable_output_is_infrastructure_and_is_not_retried(self):
        runner = FakeRunner(["bash: scripts/ci/run-repo-gates.sh: No such file or directory\n",
                             repo_gates_output()], returncode=127)
        with self.assertRaises(GateInfrastructureError) as cm:
            run_gate("repo-gates", "/work/ws", runner=runner)
        self.assertIn("No such file", cm.exception.evidence)
        self.assertEqual(len(runner.calls), 1)
        self.assertEqual(len(runner.outputs), 1)   # the green second answer was never asked for

    def test_runner_error_is_infrastructure_and_is_not_retried(self):
        runner = FakeRunner([OSError("bash not found"), dotnet_output()])
        with self.assertRaises(GateInfrastructureError):
            run_gate("container-tests", "/work/ws", runner=runner)
        self.assertEqual(len(runner.calls), 1)

    def test_timeout_is_infrastructure(self):
        runner = FakeRunner([subprocess.TimeoutExpired(["bash"], 5), dotnet_output()])
        with self.assertRaises(GateInfrastructureError) as cm:
            run_gate("container-tests", "/work/ws", runner=runner, timeout=5)
        self.assertIn("timed out", cm.exception.evidence)
        self.assertEqual(len(runner.calls), 1)

    def test_gate_failure_is_not_retried(self):
        runner = FakeRunner([dotnet_output(failed=[T1]), dotnet_output()])
        r = run_gate("container-tests", "/work/ws", runner=runner)
        self.assertEqual(len(r.failures), 1)
        self.assertEqual(len(runner.calls), 1)

    def test_unknown_gate_calls_nothing(self):
        runner = FakeRunner([])
        with self.assertRaises(ValueError):
            run_gate("nope", "/work/ws", runner=runner)
        self.assertEqual(runner.calls, [])

    def test_gate_commands_are_the_three_scripts(self):
        self.assertEqual(sorted(GATE_COMMANDS), ["container-tests", "readiness-local", "repo-gates"])


# ---------------------------------------------------------------------------------------------------
# fixer_request and launch_fixer
# ---------------------------------------------------------------------------------------------------

class RaisingAdapter(FakeAdapter):
    """Raises the scripted errors in order, then launches; records the agent named by each call."""

    def __init__(self, errors=()):
        super().__init__()
        self.errors = list(errors)
        self.agents_seen = []

    def launch(self, request: LaunchRequest) -> LaunchResult:
        self.launch_count += 1
        self.agents_seen.append(request.agent)
        if self.errors:
            raise self.errors.pop(0)
        return LaunchResult(native_session_id="s1", native_run_handle=f"h{self.launch_count}")


def a_failure() -> GateFailure:
    return parse_container_test(dotnet_output(failed=[T1])).failures[0]


def a_request(agent="readiness-fixer") -> LaunchRequest:
    spec = {"provider": "claude", "model": "claude-sonnet-5", "effort": "medium", "speed": "standard",
            "enabled": True, "funding_pool": "claude-subscription", "pool_kind": "subscription_allowance",
            "unit": "turns"}
    return fixer_request(a_failure(), "t1", "r1", spec, 8, "/work/ws", agent=agent)


class FixerRequest(unittest.TestCase):
    def test_fields(self):
        f = a_failure()
        req = a_request()
        self.assertEqual((req.run_id, req.task_id, req.provider, req.model, req.effort, req.speed),
                         ("r1", "t1", "claude", "claude-sonnet-5", "medium", "standard"))
        self.assertEqual((req.invocation_ceiling, req.workspace, req.role, req.agent, req.allowed_tools),
                         (8, "/work/ws", "readiness-fixer", "readiness-fixer", None))
        self.assertIsNone(req.resume_session_id)
        self.assertIn(f.gate, req.prompt)
        self.assertIn(f.identity, req.prompt)
        self.assertIn(T1, req.prompt)

    def test_agent_can_be_none(self):
        self.assertIsNone(a_request(agent=None).agent)

    def test_provider_name_is_required(self):
        with self.assertRaises(ValueError):
            fixer_request(a_failure(), "t1", "r1", {"model": "m"}, 8, "/work/ws")


class LaunchFixer(unittest.TestCase):
    def test_plain_launch(self):
        adapter = RaisingAdapter()
        req = a_request()
        res = launch_fixer(adapter, req)
        self.assertEqual(res.native_run_handle, "h1")
        self.assertEqual(adapter.agents_seen, ["readiness-fixer"])
        self.assertFalse(hasattr(req, "fallback"))

    def test_missing_role_falls_back_exactly_once_to_general_purpose(self):
        adapter = RaisingAdapter([RuntimeError("claude exited 1: Unknown agent 'readiness-fixer'")])
        req = a_request()
        res = launch_fixer(adapter, req)
        self.assertEqual(res.native_run_handle, "h2")
        self.assertEqual(adapter.launch_count, 2)
        self.assertEqual(adapter.agents_seen, ["readiness-fixer", None])
        self.assertIsNone(req.agent)
        self.assertEqual(req.fallback, "general-purpose")

    def test_missing_role_twice_propagates_without_a_third_launch(self):
        adapter = RaisingAdapter([RuntimeError("unknown agent readiness-fixer"), RuntimeError("agent not found")])
        with self.assertRaises(RuntimeError) as cm:
            launch_fixer(adapter, a_request())
        self.assertEqual(str(cm.exception), "agent not found")
        self.assertEqual(adapter.launch_count, 2)

    def test_other_error_propagates_with_no_fallback(self):
        adapter = RaisingAdapter([RuntimeError("spawn failed: disk full")])
        req = a_request()
        with self.assertRaises(RuntimeError):
            launch_fixer(adapter, req)
        self.assertEqual(adapter.launch_count, 1)
        self.assertEqual(req.agent, "readiness-fixer")
        self.assertFalse(hasattr(req, "fallback"))

    def test_launch_refused_propagates_with_no_fallback(self):
        adapter = RaisingAdapter([LaunchRefused(StopReason.AUTH_MISSING, "Not logged in")])
        with self.assertRaises(LaunchRefused):
            launch_fixer(adapter, a_request())
        self.assertEqual(adapter.launch_count, 1)

    def test_fallback_error_after_missing_role_propagates(self):
        adapter = RaisingAdapter([RuntimeError("no agent named readiness-fixer"), OSError("spawn failed")])
        with self.assertRaises(OSError):
            launch_fixer(adapter, a_request())
        self.assertEqual(adapter.launch_count, 2)

    def test_no_agent_requested_means_no_fallback_even_on_missing_role_text(self):
        adapter = RaisingAdapter([RuntimeError("unknown agent")])
        with self.assertRaises(RuntimeError):
            launch_fixer(adapter, a_request(agent=None))
        self.assertEqual(adapter.launch_count, 1)

    def test_every_missing_role_pattern_triggers_the_fallback(self):
        samples = {r"unknown agent": "Unknown agent: readiness-fixer",
                   r"agent .* not found": "Agent 'readiness-fixer' was not found in .claude/agents",
                   r"no agent named": "There is no agent named readiness-fixer",
                   r"agent not found": "error: agent not found"}
        self.assertEqual(set(samples), set(MISSING_ROLE_PATTERNS))
        for pattern, text in samples.items():
            with self.subTest(pattern=pattern):
                adapter = RaisingAdapter([RuntimeError(text)])
                launch_fixer(adapter, a_request())
                self.assertEqual(adapter.agents_seen, ["readiness-fixer", None])


# ---------------------------------------------------------------------------------------------------
# refuted
# ---------------------------------------------------------------------------------------------------

class Refuted(unittest.TestCase):
    def test_same_identity_in_both_is_refuted(self):
        before = parse_container_test(dotnet_output(failed=[T1, T2]))
        after = parse_container_test(dotnet_output(failed=[T2, T1], ms=800))
        self.assertEqual([f.identity for f in refuted(before, after)], [before.failures[0].identity])

    def test_fixed_and_new_failures_are_not_refuted(self):
        before = parse_repo_gates(repo_gates_output(failing=["tests/scripts/mutation-check.test.sh"]))
        after_fixed = parse_repo_gates(repo_gates_output())
        self.assertEqual(refuted(before, after_fixed), [])
        after_other = parse_repo_gates(repo_gates_output(failing=["scripts/ci/test-commercial-receipts.py"]))
        self.assertEqual(refuted(before, after_other), [])

    def test_partial_fix_keeps_only_the_surviving_identity(self):
        before = GateReport(source="x", commit=None, failures=[GateFailure("a", "gate:a:000000000001", "e"),
                                                                GateFailure("b", "gate:b:000000000002", "e")])
        after = GateReport(source="x", commit=None, failures=[GateFailure("b", "gate:b:000000000002", "e"),
                                                               GateFailure("c", "gate:c:000000000003", "e")])
        self.assertEqual([f.gate for f in refuted(before, after)], ["b"])


if __name__ == "__main__":
    unittest.main()
