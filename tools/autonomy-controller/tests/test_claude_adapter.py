"""The Claude CLI adapter against a scripted stand-in (tests/fake_claude_cli.py). No test here runs a
real `claude`: the adapter is always given `cli=[sys.executable, fake_claude_cli.py]`."""
import json
import os
import shutil
import sys
import tempfile
import time
import unittest
from pathlib import Path

from ashlar_controller.adapters.claude import ClaudeAdapter, failure_identity
from ashlar_controller.controller import Controller
from ashlar_controller.models import (LaunchRefused, LaunchRequest, RunState, StopReason, TaskState, UsageFreshness,
                                      UsageKind)

from .helpers import ControllerCase, FakeClock

FAKE_CLI = str(Path(__file__).resolve().parent / "fake_claude_cli.py")


def result_object(**overrides):
    """The observed `--output-format json` result shape (claude 2.1.263), with overrides."""
    obj = {"type": "result", "subtype": "success", "is_error": False, "terminal_reason": None,
           "api_error_status": None, "result": "done", "num_turns": 3, "total_cost_usd": 0.0123,
           "duration_ms": 1234, "duration_api_ms": 1000,
           "usage": {"input_tokens": 10, "output_tokens": 20, "cache_creation_input_tokens": 0,
                     "cache_read_input_tokens": 5, "service_tier": "standard", "speed": "standard"},
           "modelUsage": {}, "permission_denials": [], "fast_mode_state": "off", "stop_reason": "end_turn",
           "uuid": "11111111-2222-3333-4444-555555555555"}
    obj.update(overrides)
    return obj


def api_error(text, status=None, **overrides):
    """The observed failure shape: is_error true, subtype still "success", terminal_reason api_error."""
    return result_object(is_error=True, terminal_reason="api_error", api_error_status=status, result=text,
                         num_turns=1, **overrides)


class ClaudeAdapterCase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="ashlar-cl-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.runs_dir = self.tmp / "runs"
        self.argv_dir = self.tmp / "argv"
        self.argv_dir.mkdir()
        self.projects_dir = self.tmp / "projects"
        self.script_path = self.tmp / "script.json"
        self.clock = FakeClock()
        self.adapters = []
        self.addCleanup(self._kill_leftovers)

    def _kill_leftovers(self):
        for a in self.adapters:
            for proc in a._procs.values():
                if proc.poll() is None:
                    proc.kill()
                    proc.wait(timeout=10)

    def env(self, **extra):
        e = {k: v for k, v in os.environ.items() if k != "ANTHROPIC_API_KEY"}
        e["CLAUDECODE"] = "1"                     # the suite may run inside a Claude Code session; the
        e["CLAUDE_CODE_ENTRYPOINT"] = "cli"       # adapter must strip these from the child
        e["FAKE_CLAUDE_SCRIPT"] = str(self.script_path)
        e["FAKE_CLAUDE_ARGV_OUT"] = str(self.argv_dir) + os.sep
        e.update(extra)
        return e

    def write_script(self, stdout=None, exit=0, stderr="", sleep_seconds=0, persist=False, auth=None, **more):
        script = {"auth": auth if auth is not None else {"loggedIn": True, "authMethod": "claude.ai",
                                                           "projectsDirectory": str(self.projects_dir)},
                  "stdout": stdout, "exit": exit, "stderr": stderr, "sleep_seconds": sleep_seconds,
                  "persist": persist}
        script.update(more)
        self.script_path.write_text(json.dumps(script), encoding="utf-8")

    def adapter(self, **kw):
        kw.setdefault("env", self.env())
        a = ClaudeAdapter(str(self.runs_dir), cli=[sys.executable, FAKE_CLI], clock=self.clock, **kw)
        self.adapters.append(a)
        return a

    def request(self, run_id="run-1", **kw):
        base = dict(run_id=run_id, task_id="t1", provider="claude", model="claude-sonnet-5", effort="medium",
                    speed="standard", invocation_ceiling=20, workspace=None, prompt="card #755: PR 2")
        base.update(kw)
        return LaunchRequest(**base)

    def argv_files(self):
        return sorted(self.argv_dir.glob("argv-*.json"))

    def recorded(self):
        files = self.argv_files()
        self.assertEqual(len(files), 1, files)
        return json.loads(files[0].read_text(encoding="utf-8"))

    def wait_exit(self, adapter, handle, timeout=20.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            status = adapter.poll(handle)
            if status.state != RunState.RUNNING:
                return status
            time.sleep(0.05)
        self.fail(f"run {handle} still running after {timeout}s")

    def launch_and_settle(self, stdout=None, **script):
        self.write_script(stdout=stdout, **script)
        a = self.adapter()
        res = a.launch(self.request())
        return a, res, self.wait_exit(a, res.native_run_handle)


class Argv(ClaudeAdapterCase):
    def test_argv_carries_model_effort_max_turns_and_headless_flags(self):
        a, res, status = self.launch_and_settle(result_object())
        argv = self.recorded()["argv"]
        self.assertEqual(argv[:2], ["-p", "card #755: PR 2"])
        for flag, value in (("--output-format", "stream-json"), ("--model", "claude-sonnet-5"), ("--effort", "medium"),
                            ("--max-turns", "20"), ("--permission-prompts", "none"),
                            ("--permission-mode", "acceptEdits"), ("--session-id", res.native_session_id)):
            self.assertIn(flag, argv)
            self.assertEqual(argv[argv.index(flag) + 1], value, flag)
        self.assertIn("--no-chrome", argv)
        self.assertIn("--verbose", argv)             # stream-json needs it
        self.assertIn("--strict-mcp-config", argv)   # the operator's connectors never enter a run
        self.assertNotIn("--resume", argv)
        self.assertNotIn("--agent", argv)
        self.assertNotIn("--tools", argv)

    def test_session_id_is_a_fresh_uuid_and_the_result_carries_it(self):
        a, res, status = self.launch_and_settle(result_object())
        self.assertEqual(len(res.native_session_id), 36)
        self.assertEqual(status.result["session_id"], res.native_session_id)
        self.assertEqual(status.observations[0].native_session_id, res.native_session_id)

    def test_resume_passes_resume_and_not_session_id(self):
        self.write_script(stdout=result_object())
        a = self.adapter()
        res = a.launch(self.request(resume_session_id="aaaaaaaa-0000-0000-0000-000000000001"))
        self.wait_exit(a, res.native_run_handle)
        self.assertEqual(res.native_session_id, "aaaaaaaa-0000-0000-0000-000000000001")
        argv = self.recorded()["argv"]
        self.assertEqual(argv[argv.index("--resume") + 1], "aaaaaaaa-0000-0000-0000-000000000001")
        self.assertNotIn("--session-id", argv)
        manifest = json.loads((self.runs_dir / "run-1" / "manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["resume_of"], "aaaaaaaa-0000-0000-0000-000000000001")

    def test_agent_and_tools_are_plumbed_including_an_empty_tool_list(self):
        self.write_script(stdout=result_object())
        a = self.adapter()
        res = a.launch(self.request(agent="readiness-fixer", allowed_tools=["Read", "Bash"]))
        self.wait_exit(a, res.native_run_handle)
        argv = self.recorded()["argv"]
        self.assertEqual(argv[argv.index("--agent") + 1], "readiness-fixer")
        self.assertEqual(argv[argv.index("--tools") + 1], "Read,Bash")
        for f in self.argv_files():
            f.unlink()
        res2 = a.launch(self.request(run_id="run-2", allowed_tools=[]))
        self.wait_exit(a, res2.native_run_handle)
        argv = self.recorded()["argv"]
        self.assertEqual(argv[argv.index("--tools") + 1], "")
        self.assertNotIn("--agent", argv)

    def test_child_env_has_no_nested_session_markers(self):
        a, res, status = self.launch_and_settle(result_object())
        env = self.recorded()["env"]
        self.assertNotIn("CLAUDECODE", env)
        self.assertFalse([k for k in env if k.startswith("CLAUDE_CODE_")], env)

    def test_workspace_defaults_to_the_run_dir_and_the_child_runs_in_it(self):
        a, res, status = self.launch_and_settle(result_object())
        expected = str(self.runs_dir / "run-1" / "workspace")
        self.assertEqual(os.path.normcase(res.workspace), os.path.normcase(expected))
        self.assertEqual(os.path.normcase(self.recorded()["cwd"]), os.path.normcase(expected))
        self.assertTrue((self.runs_dir / "run-1" / "stdout.json").exists())
        self.assertTrue((self.runs_dir / "run-1" / "stderr.log").exists())

    def test_transcript_path_follows_the_projects_directory_slug_rule(self):
        self.write_script(stdout=result_object(), persist=True)
        a = self.adapter()
        res = a.launch(self.request())
        status = self.wait_exit(a, res.native_run_handle)
        self.assertIsNotNone(status.transcript_path)
        self.assertTrue(os.path.exists(status.transcript_path), status.transcript_path)
        self.assertEqual(status.result["transcript"], status.transcript_path)
        self.assertTrue(status.transcript_path.endswith(f"{res.native_session_id}.jsonl"))


class Refusals(ClaudeAdapterCase):
    def test_non_standard_speed_is_refused_before_any_process(self):
        self.write_script(stdout=result_object())
        a = self.adapter()
        with self.assertRaises(LaunchRefused) as cm:
            a.launch(self.request(speed="fast"))
        self.assertEqual(cm.exception.stop_reason, StopReason.LAUNCH_FAILED)
        self.assertIn("only standard", cm.exception.blocker)
        self.assertEqual(self.argv_files(), [])
        self.assertFalse(self.runs_dir.exists())

    def test_not_logged_in_refuses_auth_missing_without_spawning(self):
        self.write_script(stdout=result_object(), auth={"loggedIn": False})
        a = self.adapter()
        with self.assertRaises(LaunchRefused) as cm:
            a.launch(self.request())
        self.assertEqual(cm.exception.stop_reason, StopReason.AUTH_MISSING)
        self.assertIn("claude auth login", cm.exception.blocker)
        self.assertEqual(self.argv_files(), [])
        self.assertFalse((self.runs_dir / "run-1").exists())

    def test_console_auth_method_is_a_funding_route_mismatch(self):
        self.write_script(stdout=result_object(), auth={"loggedIn": True, "authMethod": "console"})
        a = self.adapter()
        with self.assertRaises(LaunchRefused) as cm:
            a.launch(self.request())
        self.assertEqual(cm.exception.stop_reason, StopReason.FUNDING_ROUTE_MISMATCH)
        self.assertEqual(cm.exception.blocker, "console would be billed; policy selected claude.ai")
        self.assertEqual(self.argv_files(), [])

    def test_an_api_key_in_the_env_is_the_api_key_route_even_when_logged_in(self):
        self.write_script(stdout=result_object())
        a = self.adapter(env=self.env(ANTHROPIC_API_KEY="sk-ant-test-not-a-real-key"))
        with self.assertRaises(LaunchRefused) as cm:
            a.launch(self.request())
        self.assertEqual(cm.exception.stop_reason, StopReason.FUNDING_ROUTE_MISMATCH)
        self.assertIn("api-key would be billed", cm.exception.blocker)
        self.assertEqual(self.argv_files(), [])

    def test_pretty_printed_auth_status_is_accepted(self):
        # The real `claude auth status --json` spreads the object over many lines (observed 2026-10-11).
        # A last-line-only parser read it as "no JSON" and blocked every launch as auth-missing.
        pretty = json.dumps({"loggedIn": True, "authMethod": "claude.ai", "apiProvider": "firstParty",
                             "projectsDirectory": str(self.tmp / "projects"), "subscriptionType": "max"}, indent=2) + "\n"
        self.write_script(stdout=result_object(), auth_stdout=pretty)
        a = self.adapter()
        res = a.launch(self.request())
        status = self.wait_exit(a, res.native_run_handle)
        self.assertEqual(status.state, RunState.FINISHED)
        self.assertEqual(len(self.argv_files()), 1)

    def test_auth_status_without_json_is_auth_missing(self):
        self.write_script(stdout=result_object(), auth_stdout="claude: command not understood\n", auth_exit=2)
        a = self.adapter()
        with self.assertRaises(LaunchRefused) as cm:
            a.launch(self.request())
        self.assertEqual(cm.exception.stop_reason, StopReason.AUTH_MISSING)
        self.assertIn("exit 2", cm.exception.blocker)

    def test_a_cli_that_cannot_run_is_auth_missing(self):
        self.write_script(stdout=result_object())
        a = ClaudeAdapter(str(self.runs_dir), cli=str(self.tmp / "no-such-claude.exe"), env=self.env())
        with self.assertRaises(LaunchRefused) as cm:
            a.launch(self.request())
        self.assertEqual(cm.exception.stop_reason, StopReason.AUTH_MISSING)
        self.assertEqual(self.argv_files(), [])


class Classification(ClaudeAdapterCase):
    def test_success_is_completed_with_per_invocation_turns_and_a_cost_observation(self):
        a, res, status = self.launch_and_settle(result_object(num_turns=4, total_cost_usd=0.5))
        self.assertEqual((status.state, status.stop_reason), (RunState.FINISHED, StopReason.COMPLETED))
        self.assertEqual(status.consumed_units, 4.0)       # num_turns, per invocation (observed)
        self.assertIsNone(status.usage)                    # no delta stands in for consumption
        self.assertEqual([o.kind for o in status.observations],
                         [UsageKind.SUBSCRIPTION_ALLOWANCE, UsageKind.ESTIMATED_API_VALUE])
        allowance, cost = status.observations
        self.assertEqual((allowance.unit, allowance.freshness, allowance.value), ("percent", UsageFreshness.UNAVAILABLE, None))
        self.assertEqual((cost.unit, cost.freshness, cost.value), ("usd", UsageFreshness.FRESH, 0.5))
        # Keyed by the run id, so the recorded delta of this per-invocation figure equals its value.
        self.assertEqual(cost.native_session_id, "run-1")
        self.assertEqual(cost.raw["session_id"], res.native_session_id)
        self.assertEqual(cost.raw["tokens"]["output_tokens"], 20)
        self.assertEqual(status.result["exit"], 0)
        self.assertEqual(status.result["subtype"], "success")
        self.assertEqual(status.result["duration_ms"], 1234)

    def test_rate_limit_event_is_the_allowance_observation(self):
        info = {"status": "allowed", "resetsAt": 1791702600, "rateLimitType": "five_hour", "overageStatus": "rejected",
                "overageDisabledReason": "org_level_disabled", "isUsingOverage": False,
                "unifiedWindows": {"five_hour": {"utilization": 0.69, "resetsAt": 1791702600},
                                   "seven_day": {"utilization": 0.18, "resetsAt": 1792285200}}}
        a, res, status = self.launch_and_settle(result_object(), rate_limit_info=info)
        self.assertEqual(status.stop_reason, StopReason.COMPLETED)
        allowance = status.observations[0]
        self.assertEqual((allowance.kind, allowance.unit, allowance.freshness, allowance.value, allowance.total),
                         (UsageKind.SUBSCRIPTION_ALLOWANCE, "percent", UsageFreshness.FRESH, 69.0, 100.0))
        self.assertEqual(allowance.native_session_id, res.native_session_id)
        self.assertEqual(allowance.raw, info)
        self.assertEqual(status.result["rate_limit_info"]["unifiedWindows"]["seven_day"]["utilization"], 0.18)

    def test_overage_is_quota_exhausted_even_on_a_successful_result(self):
        info = {"status": "allowed", "isUsingOverage": True, "overageStatus": "allowed",
                "unifiedWindows": {"five_hour": {"utilization": 1.0, "resetsAt": 1791702600}}}
        a, res, status = self.launch_and_settle(result_object(), rate_limit_info=info)
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.QUOTA_EXHAUSTED))
        self.assertIs(status.result["overage"], True)
        self.assertEqual(status.result["blocker"], "the provider is billing overage; paid inference is off")
        self.assertEqual(status.observations[0].value, 100.0)
        self.assertEqual(status.consumed_units, 3.0)

    def test_max_turns_subtype_is_the_invocation_ceiling(self):
        a, res, status = self.launch_and_settle(result_object(subtype="error_max_turns", is_error=True, num_turns=2,
                                                              terminal_reason="max_turns", stop_reason="tool_use"))
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.INVOCATION_CEILING))
        self.assertEqual(status.consumed_units, 2.0)
        self.assertEqual(status.result["terminal_reason"], "max_turns")
        self.assertIn(res.native_session_id, status.result["blocker"])

    def test_observed_oauth_failure_is_auth_missing(self):
        text = "Failed to authenticate: OAuth session expired and could not be refreshed"
        a, res, status = self.launch_and_settle(api_error(text), exit=1)
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.AUTH_MISSING))
        self.assertEqual(status.result["blocker"], text)
        self.assertEqual(status.result["exit"], 1)
        self.assertEqual(status.result["terminal_reason"], "api_error")

    def test_401_is_auth_missing_whatever_the_text(self):
        a, res, status = self.launch_and_settle(api_error("nope", status=401), exit=1)
        self.assertEqual(status.stop_reason, StopReason.AUTH_MISSING)

    def test_usage_limit_text_is_quota_exhausted(self):
        a, res, status = self.launch_and_settle(api_error("You've hit your usage limit. Resets at 3pm"), exit=1)
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.QUOTA_EXHAUSTED))
        self.assertIn("usage limit", status.result["blocker"])
        self.assertEqual(status.consumed_units, 1.0)

    def test_402_is_quota_exhausted(self):
        a, res, status = self.launch_and_settle(api_error("payment required", status=402), exit=1)
        self.assertEqual(status.stop_reason, StopReason.QUOTA_EXHAUSTED)

    def test_429_and_rate_limit_text_are_rate_limited(self):
        a, res, status = self.launch_and_settle(api_error("slow down", status=429), exit=1)
        self.assertEqual(status.stop_reason, StopReason.RATE_LIMITED)
        shutil.rmtree(self.runs_dir)
        a, res, status = self.launch_and_settle(api_error("Too many requests"), exit=1)
        self.assertEqual(status.stop_reason, StopReason.RATE_LIMITED)

    def test_5xx_and_network_text_are_transient(self):
        for status_code in (408, 500, 502, 503, 504, 529):
            with self.subTest(status=status_code):
                shutil.rmtree(self.runs_dir, True)
                a, res, status = self.launch_and_settle(api_error("x", status=status_code), exit=1)
                self.assertEqual(status.stop_reason, StopReason.TRANSIENT)
        shutil.rmtree(self.runs_dir, True)
        a, res, status = self.launch_and_settle(api_error("request to api.anthropic.com timed out"), exit=1)
        self.assertEqual(status.stop_reason, StopReason.TRANSIENT)

    def test_permission_denial_on_a_successful_result_is_permission_missing(self):
        # Observed: under --permission-prompts none a denied run COMPLETES with is_error false.
        obj = result_object(is_error=False, subtype="success", result="The Bash call was automatically denied.",
                            permission_denials=[{"tool_name": "Bash", "tool_input": {"command": "git status"},
                                                 "tool_use_id": "toolu_01"}])
        a, res, status = self.launch_and_settle(obj, exit=0)
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.PERMISSION_MISSING))
        self.assertEqual(status.result["denied_tools"], ["Bash"])
        self.assertIn("Bash", status.result["blocker"])
        self.assertEqual(status.consumed_units, 3.0)

    def test_404_with_model_text_is_model_unrecognized_without_the_stderr_tag(self):
        text = "There's an issue with the selected model (claude-no-such-model-0). It may not exist or you may not have access to it."
        a, res, status = self.launch_and_settle(api_error(text, status=404, total_cost_usd=0), exit=1)
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.MODEL_UNRECOGNIZED))
        self.assertEqual(status.consumed_units, 0.0)
        self.assertEqual(status.result["model"], "claude-sonnet-5")

    def test_other_errors_are_task_failed_with_a_stable_identity(self):
        text = "Build failed:   CS0246 the type 'Foo'  could not be found"
        a, res, status = self.launch_and_settle(result_object(is_error=True, result=text), exit=1)
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.TASK_FAILED))
        self.assertEqual(status.failure_identity, failure_identity(text))
        self.assertTrue(status.failure_identity.startswith("claude:"))
        self.assertEqual(len(status.failure_identity), len("claude:") + 12)
        # Whitespace and case do not change the identity; different text does.
        self.assertEqual(failure_identity("build failed: cs0246 THE TYPE 'foo' could not be found"), status.failure_identity)
        self.assertNotEqual(failure_identity("Build failed: CS0103"), status.failure_identity)

    def test_exit_without_json_is_no_final_result_with_the_stderr_tail(self):
        a, res, status = self.launch_and_settle(None, exit=3, stderr="segfault-ish\n", stdout_raw="partial text\n")
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.NO_FINAL_RESULT))
        self.assertEqual(status.result["exit"], 3)
        self.assertIn("segfault-ish", status.result["stderr_tail"])
        self.assertIsNone(status.usage)
        self.assertEqual(status.consumed_units, None)

    def test_unrecognised_model_stderr_is_model_unrecognized(self):
        stderr = '[claude-code:unrecognized_model] {"model":"claude-sonnet-5-5","query_source":"sdk"}\n'
        a, res, status = self.launch_and_settle(None, exit=1, stderr=stderr)
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.MODEL_UNRECOGNIZED))
        self.assertEqual(status.result["model"], "claude-sonnet-5")
        self.assertEqual(status.consumed_units, 0.0)

    def test_the_last_result_event_wins_and_other_lines_are_ignored(self):
        earlier = json.dumps(result_object(is_error=True, result="stale result from an earlier line"))
        raw = '{"type":"assistant","message":{"content":[{"type":"text","text":"working"}]}}\nnot json\n' + earlier + "\n"
        a, res, status = self.launch_and_settle(result_object(), stdout_raw=raw)
        self.assertEqual(status.stop_reason, StopReason.COMPLETED)
        self.assertEqual(status.result["stream_events"], 4)   # init, assistant, stale result, result

    def test_exit_without_a_result_event_but_with_an_init_line_is_no_final_result(self):
        a, res, status = self.launch_and_settle(None, exit=1)
        self.assertEqual(status.stop_reason, StopReason.NO_FINAL_RESULT)
        self.assertEqual(status.result["stream_events"], 1)
        self.assertEqual(status.observations[0].freshness, UsageFreshness.UNAVAILABLE)


class CancelAndReconcile(ClaudeAdapterCase):
    def wait_for_argv(self, timeout=20.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.argv_files():
                return
            time.sleep(0.05)
        self.fail("the fake CLI never started")

    def test_cancel_preserves_the_run_dir_manifest_and_workspace(self):
        self.write_script(stdout=result_object(), sleep_seconds=60)
        a = self.adapter()
        res = a.launch(self.request())
        self.wait_for_argv()
        self.assertEqual(a.poll(res.native_run_handle).state, RunState.RUNNING)
        marker = Path(res.workspace) / "half-done.txt"
        marker.write_text("evidence\n", encoding="utf-8")
        a.cancel(res.native_run_handle)
        status = a.poll(res.native_run_handle)
        self.assertEqual((status.state, status.stop_reason), (RunState.CANCELLED, StopReason.CANCELLED))
        self.assertIsNone(status.consumed_units)
        self.assertEqual(os.path.normcase(status.result["workspace"]), os.path.normcase(res.workspace))
        self.assertTrue(marker.exists())
        manifest = json.loads((self.runs_dir / "run-1" / "manifest.json").read_text(encoding="utf-8"))
        self.assertIsNotNone(manifest["cancelled_at"])
        self.assertIsNotNone(manifest["pid"])
        self.assertTrue((self.runs_dir / "run-1" / "stdout.json").exists())
        deadline = time.time() + 20
        while a._procs[res.native_run_handle].poll() is None and time.time() < deadline:
            time.sleep(0.05)
        self.assertIsNotNone(a._procs[res.native_run_handle].poll(), "the process survived cancel")
        self.assertEqual(a.poll(res.native_run_handle).stop_reason, StopReason.CANCELLED)

    def test_cancel_of_a_finished_or_missing_run_is_not_an_error(self):
        a, res, status = self.launch_and_settle(result_object())
        a.cancel(res.native_run_handle)
        a.cancel("run-never-existed")
        self.assertEqual(a.poll(res.native_run_handle).stop_reason, StopReason.CANCELLED)

    def test_a_fresh_adapter_sees_the_running_process_then_cancels_it(self):
        """After a controller restart only the manifest's pid is left: the existence check must say
        alive without killing, and cancel must still reach the process."""
        self.write_script(stdout=result_object(), sleep_seconds=60)
        a = self.adapter()
        res = a.launch(self.request())
        self.wait_for_argv()
        fresh = self.adapter()
        found = fresh.reconcile("run-1", res.native_run_handle)
        self.assertTrue(found.found)
        self.assertEqual(found.native_session_id, res.native_session_id)
        self.assertEqual(found.status.state, RunState.RUNNING)
        self.assertIsNone(a._procs["run-1"].poll(), "the existence check must not terminate the process")
        fresh.cancel(res.native_run_handle)
        deadline = time.time() + 20
        while a._procs["run-1"].poll() is None and time.time() < deadline:
            time.sleep(0.05)
        self.assertIsNotNone(a._procs["run-1"].poll())
        self.assertEqual(fresh.poll("run-1").stop_reason, StopReason.CANCELLED)

    def test_reconcile_after_restart_classifies_the_recorded_run(self):
        a, res, status = self.launch_and_settle(api_error("You've hit your usage limit"), exit=1)
        fresh = self.adapter()
        found = fresh.reconcile("run-1", "run-1")
        self.assertTrue(found.found)
        self.assertEqual((found.native_session_id, found.native_run_handle), (res.native_session_id, "run-1"))
        self.assertEqual(found.status.stop_reason, StopReason.QUOTA_EXHAUSTED)

    def test_reconcile_without_a_manifest_is_not_found(self):
        a = self.adapter()
        found = a.reconcile("run-ghost", None)
        self.assertFalse(found.found)
        self.assertIsNone(found.status)

    def test_launch_failure_of_the_process_is_a_definite_runtime_error(self):
        self.write_script(stdout=result_object())
        a = self.adapter()
        # The auth check runs the fake; the run argv names an executable that does not exist, so the
        # spawn itself fails and nothing is running.
        a._argv = lambda prefix, request, session_id: [str(self.tmp / "no-such-claude.exe"), "-p", request.prompt]
        with self.assertRaises(RuntimeError) as cm:
            a.launch(self.request())
        self.assertIn("did not start", str(cm.exception))
        manifest = json.loads((self.runs_dir / "run-1" / "manifest.json").read_text(encoding="utf-8"))
        self.assertIsNone(manifest["pid"])
        self.assertEqual(self.argv_files(), [])

    def test_observe_usage_is_the_unavailable_allowance(self):
        obs = self.adapter().observe_usage()
        self.assertEqual((obs.provider, obs.kind, obs.unit, obs.freshness, obs.value),
                         ("claude", UsageKind.SUBSCRIPTION_ALLOWANCE, "percent", UsageFreshness.UNAVAILABLE, None))

    def test_capabilities_declare_only_the_observed(self):
        caps = ClaudeAdapter.capabilities
        for name in ("exact_resume", "cancel", "structured_output", "usage_observation", "transcript_retention",
                     "turn_limit", "funding_route_check"):
            self.assertTrue(getattr(caps, name), name)
        self.assertIn("2.1.263", caps.evidence)
        self.assertIn("probe-report-2.json", caps.evidence)

    def test_cancelled_run_keeps_the_allowance_it_streamed_and_names_the_session_to_resume(self):
        info = {"isUsingOverage": False, "unifiedWindows": {"five_hour": {"utilization": 0.4, "resetsAt": 1}}}
        self.write_script(stdout=result_object(), sleep_seconds=60, rate_limit_info=info)
        a = self.adapter()
        res = a.launch(self.request())
        self.wait_for_argv()
        deadline = time.time() + 20
        while (self.runs_dir / "run-1" / "stdout.json").stat().st_size == 0 and time.time() < deadline:
            time.sleep(0.05)
        a.cancel(res.native_run_handle)
        status = a.poll(res.native_run_handle)
        self.assertEqual(status.stop_reason, StopReason.CANCELLED)
        self.assertEqual(status.result["resume_session_id"], res.native_session_id)
        self.assertEqual((status.observations[0].freshness, status.observations[0].value), (UsageFreshness.FRESH, 40.0))


class ThroughTheController(ControllerCase):
    """End to end: the controller dispatches a claude task through the real adapter and the fake CLI."""

    def setUp(self):
        super().setUp()
        self.argv_dir = self.tmp / "argv"
        self.argv_dir.mkdir()
        self.script_path = self.tmp / "script.json"
        self.policy_path = self.write_policy(lambda raw: raw["providers"]["claude"].update({"model": "claude-sonnet-5"}))

    def controller(self, stdout, exit=1):
        self.script_path.write_text(json.dumps({"auth": {"loggedIn": True, "authMethod": "claude.ai"},
                                                "stdout": stdout, "exit": exit}), encoding="utf-8")
        env = {k: v for k, v in os.environ.items() if k != "ANTHROPIC_API_KEY"}
        env.update({"FAKE_CLAUDE_SCRIPT": str(self.script_path), "FAKE_CLAUDE_ARGV_OUT": str(self.argv_dir) + os.sep})
        self.adapter = ClaudeAdapter(str(self.tmp / "runs"), cli=[sys.executable, FAKE_CLI], env=env, clock=self.clock)
        self.store = self.init_store()
        self.policy = self.load()
        self.ctl = Controller(self.store, self.policy, {"claude": self.adapter}, holder_id="holder-a", host="host-a",
                              pid=4242, clock=self.clock)
        self.ctl.ensure_task("t1", workstream="budgeted-autonomy", provider="claude", card_number=755, title="PR 2")
        return self.ctl

    def tick_until_settled(self, ctl, timeout=20.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            r = ctl.tick()
            if r.action == "settled":
                return r
            time.sleep(0.1)
        self.fail("the run never settled")

    def argv_files(self):
        return sorted(self.argv_dir.glob("argv-*.json"))

    def test_quota_script_waits_for_quota_after_exactly_one_launch(self):
        ctl = self.controller(api_error("You've hit your usage limit. Resets at 3pm"))
        r = ctl.tick()
        self.assertEqual(r.action, "launched")
        settled = self.tick_until_settled(ctl)
        argv = json.loads(self.argv_files()[0].read_text(encoding="utf-8"))["argv"]
        self.assertEqual(argv[argv.index("--model") + 1], "claude-sonnet-5")
        self.assertEqual(argv[argv.index("--max-turns") + 1], "20")
        self.assertEqual(settled.settled[0]["stop_reason"], StopReason.QUOTA_EXHAUSTED)
        t = self.task()
        self.assertEqual(t["state"], TaskState.WAITING_QUOTA)
        self.assertIn("usage limit", t["blocker"])
        for _ in range(3):
            self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(len(self.argv_files()), 1)
        run = self.store.row("SELECT * FROM runs")
        self.assertEqual((run["state"], run["stop_reason"]), (RunState.FAILED, StopReason.QUOTA_EXHAUSTED))
        self.assertEqual(self.pool()["consumed"], 1.0)   # num_turns, per invocation

    def test_auth_script_blocks_on_auth_after_exactly_one_launch(self):
        ctl = self.controller(api_error("Failed to authenticate: OAuth session expired and could not be refreshed"))
        self.assertEqual(ctl.tick().action, "launched")
        self.tick_until_settled(ctl)
        t = self.task()
        self.assertEqual(t["state"], TaskState.BLOCKED_AUTH)
        self.assertIn("OAuth session expired", t["blocker"])
        for _ in range(3):
            self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(len(self.argv_files()), 1)


if __name__ == "__main__":
    unittest.main()
