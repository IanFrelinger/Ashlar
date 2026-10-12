"""The Cursor agent CLI adapter against a scripted stand-in (tests/fake_cursor_cli.py). No test here runs
a real `cursor-agent`: the adapter is always given `cli=[sys.executable, fake_cursor_cli.py]`, and the
resolver and launcher tests only create files, never run them. Every test names, in a one-line comment,
the mutation of adapters/cursor.py it kills."""
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

from ashlar_controller.adapters import cursor as cursor_module
from ashlar_controller.adapters.cursor import CursorAdapter, failure_identity, node_direct, resolve_cli
from ashlar_controller.controller import Controller
from ashlar_controller.models import (LaunchRefused, LaunchRequest, RunState, StopReason, TaskState, UsageFreshness,
                                      UsageKind)
from ashlar_controller.store import pid_alive

from .helpers import ControllerCase, FakeClock

FAKE_CLI = str(Path(__file__).resolve().parent / "fake_cursor_cli.py")
# Observed verbatim (probe-cursor-report-1.json, c1..c6): the stderr of every logged-out `-p` run.
OBSERVED_AUTH_ERROR = ("Error: Authentication required. Please run 'agent login' first, or set CURSOR_API_KEY "
                       "environment variable.\n")
LOGGED_IN = "Logged in as fake-operator\n"   # the real logged-in text is unobserved
PROMPT = "card #779: PR 5 lane B"
# A plausible stream; its real shape is unobserved, which is why the adapter only keeps it as evidence.
STREAM = "\n".join(json.dumps(o) for o in (
    {"type": "system", "subtype": "init", "session_id": "chat-0001", "model": "composer-standard"},
    {"type": "assistant", "message": {"content": [{"type": "text", "text": "editing"}]}},
    {"type": "result", "subtype": "success", "result": "done", "session_id": "chat-0001"})) + "\nnot json at all\n"


def wait_for(predicate, timeout=20.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if predicate():
            return True
        time.sleep(0.05)
    return False


class CursorAdapterCase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="ashlar-cu-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.runs_dir = self.tmp / "runs"
        self.argv_dir = self.tmp / "argv"
        self.argv_dir.mkdir()
        self.script_path = self.tmp / "script.json"
        self.clock = FakeClock()
        self.adapters = []
        self.addCleanup(self._kill_leftovers)

    def _kill_leftovers(self):
        for a in self.adapters:
            for proc in a._procs.values():
                if proc.poll() is None:
                    cursor_module.kill_tree(proc.pid)
                    proc.kill()
                    proc.wait(timeout=10)
        for rec in self.recorded("print"):
            child = rec.get("child_pid")
            if child and pid_alive(child):
                try:
                    os.kill(child, signal.SIGTERM)
                except OSError:
                    pass

    def env(self, **extra):
        e = {k: v for k, v in os.environ.items() if k not in cursor_module.API_KEY_VARS}
        e["CLAUDECODE"] = "1"                     # the suite may run inside a Claude Code session; the
        e["CLAUDE_CODE_ENTRYPOINT"] = "cli"       # adapter must strip these from the child
        e["FAKE_CURSOR_SCRIPT"] = str(self.script_path)
        e["FAKE_CURSOR_ARGV_OUT"] = str(self.argv_dir) + os.sep
        e.update(extra)
        return e

    def write_script(self, **keys):
        self.script_path.write_text(json.dumps(keys), encoding="utf-8")

    def adapter(self, **kw):
        kw.setdefault("env", self.env())
        kw.setdefault("cli", [sys.executable, FAKE_CLI])
        a = CursorAdapter(str(self.runs_dir), clock=self.clock, **kw)
        self.adapters.append(a)
        return a

    def request(self, run_id="run-1", **kw):
        base = dict(run_id=run_id, task_id="t1", provider="cursor", model="composer-standard", effort="medium",
                    speed="standard", invocation_ceiling=20, workspace=None, prompt=PROMPT)
        base.update(kw)
        return LaunchRequest(**base)

    def recorded(self, kind):
        out = []
        for f in sorted(self.argv_dir.glob(f"{kind}-*.json")):
            out.append(json.loads(f.read_text(encoding="utf-8")))
        return out

    def manifest(self, run_id="run-1"):
        return json.loads((self.runs_dir / run_id / "manifest.json").read_text(encoding="utf-8"))

    def wait_exit(self, adapter, handle, timeout=20.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            status = adapter.poll(handle)
            if status.state != RunState.RUNNING:
                return status
            time.sleep(0.05)
        self.fail(f"run {handle} still running after {timeout}s")

    def launch_and_settle(self, run_id="run-1", request_kw=None, **script):
        script.setdefault("status_stdout", LOGGED_IN)
        self.write_script(**script)
        a = self.adapter()
        res = a.launch(self.request(run_id=run_id, **(request_kw or {})))
        return a, res, self.wait_exit(a, res.native_run_handle)

    def spy_popen(self):
        """Record every spawn (the status check goes through subprocess.run, which uses Popen too)."""
        calls = []
        real = subprocess.Popen
        manifest = self.runs_dir / "run-1" / "manifest.json"

        def spy(*args, **kwargs):
            argv = args[0] if args else kwargs.get("args")
            calls.append({"argv": list(argv), "stdin": kwargs.get("stdin"), "manifest_existed": manifest.exists()})
            return real(*args, **kwargs)

        patcher = mock.patch.object(subprocess, "Popen", spy)
        patcher.start()
        self.addCleanup(patcher.stop)
        return calls


class Argv(CursorAdapterCase):
    def test_argv_is_the_print_shape_with_the_prompt_last_and_no_resume(self):
        # kills: dropping -p/--trust/-f/--workspace/--model, a format other than stream-json, the prompt not last, a cwd other than the workspace
        a, res, status = self.launch_and_settle(print_stdout=STREAM)
        run = self.recorded("print")
        self.assertEqual(len(run), 1, run)
        workspace = str(self.runs_dir / "run-1" / "workspace")
        self.assertEqual(os.path.normcase(res.workspace), os.path.normcase(workspace))
        self.assertEqual(run[0]["argv"], ["-p", "--output-format", "stream-json", "--trust", "--workspace", res.workspace,
                                          "--model", "composer-standard", "-f", PROMPT])
        self.assertEqual(os.path.normcase(run[0]["cwd"]), os.path.normcase(workspace))
        self.assertEqual([r["argv"] for r in self.recorded("status")], [["status"]])

    def test_every_spawn_closes_stdin_and_the_manifest_precedes_the_run(self):
        # kills: removing stdin=DEVNULL from the status check or from the run's Popen; writing the manifest after the spawn
        calls = self.spy_popen()
        a, res, status = self.launch_and_settle(print_stdout=STREAM)
        self.assertEqual(status.stop_reason, StopReason.COMPLETED)
        self.assertEqual([c["argv"][-1] for c in calls], ["status", PROMPT])
        for c in calls:
            self.assertIs(c["stdin"], subprocess.DEVNULL, c["argv"])
        self.assertFalse(calls[0]["manifest_existed"])   # the route check runs before any record
        self.assertTrue(calls[1]["manifest_existed"])    # the manifest is on disk before the run starts

    def test_child_env_drops_nested_session_markers_and_keeps_the_rest(self):
        # kills: passing CLAUDECODE / CLAUDE_CODE_* through to cursor-agent
        self.write_script(status_stdout=LOGGED_IN, print_stdout=STREAM)
        a = self.adapter(env=self.env(CURSOR_TEST_MARKER="kept"))
        res = a.launch(self.request())
        self.wait_exit(a, res.native_run_handle)
        for rec in self.recorded("status") + self.recorded("print"):
            env = rec["env"]
            self.assertNotIn("CLAUDECODE", env)
            self.assertFalse([k for k in env if k.startswith("CLAUDE_CODE_")], env)
            self.assertEqual(env.get("CURSOR_TEST_MARKER"), "kept")

    def test_resume_session_id_is_never_passed_and_is_recorded(self):
        # kills: passing --resume <id> although exact resume was never observed
        a, res, status = self.launch_and_settle(print_stdout=STREAM, request_kw={"resume_session_id": "chat-0001"})
        argv = self.recorded("print")[0]["argv"]
        self.assertNotIn("--resume", argv)
        self.assertNotIn("--continue", argv)
        self.assertNotIn("chat-0001", argv)
        self.assertIsNone(res.native_session_id)
        manifest = self.manifest()
        self.assertIs(manifest["resume_unsupported"], True)
        self.assertEqual(manifest["resume_of"], "chat-0001")
        self.assertIs(status.result["resume_unsupported"], True)
        self.assertEqual(status.result["resume_of"], "chat-0001")


class Refusals(CursorAdapterCase):
    def test_logged_out_status_refuses_auth_missing_and_spawns_no_run(self):
        # kills: accepting the observed `Not logged in` status as logged in
        self.write_script(print_stdout=STREAM)    # status_stdout defaults to the observed `Not logged in`
        a = self.adapter()
        with self.assertRaises(LaunchRefused) as cm:
            a.launch(self.request())
        self.assertEqual(cm.exception.stop_reason, StopReason.AUTH_MISSING)
        self.assertIn("Not logged in", cm.exception.blocker)
        self.assertIn("`cursor-agent login`", cm.exception.blocker)
        self.assertEqual(len(self.recorded("status")), 1)
        self.assertEqual(self.recorded("print"), [])
        self.assertFalse((self.runs_dir / "run-1").exists())

    def test_status_failure_or_silence_refuses_auth_missing(self):
        # kills: ignoring the status exit code, or taking an empty status text as logged in
        for script, expect in (({"status_stdout": LOGGED_IN, "status_exit": 1}, "exit 1"),
                               ({"status_stdout": ""}, "(no output)")):
            with self.subTest(script=script):
                self.write_script(print_stdout=STREAM, **script)
                with self.assertRaises(LaunchRefused) as cm:
                    self.adapter().launch(self.request())
                self.assertEqual(cm.exception.stop_reason, StopReason.AUTH_MISSING)
                self.assertIn(expect, cm.exception.blocker)
        with self.assertRaises(LaunchRefused) as cm:
            self.adapter(cli=str(self.tmp / "no-such-cursor-agent.exe")).launch(self.request())
        self.assertEqual(cm.exception.stop_reason, StopReason.AUTH_MISSING)
        self.assertEqual(self.recorded("print"), [])

    def test_api_key_or_auth_token_in_the_env_is_a_funding_route_mismatch(self):
        # kills: not reading CURSOR_API_KEY / CURSOR_AUTH_TOKEN, the variables the CLI bills instead of the login
        for var in ("CURSOR_API_KEY", "CURSOR_AUTH_TOKEN"):
            for status_stdout in (LOGGED_IN, "Not logged in\n"):
                with self.subTest(var=var, status=status_stdout):
                    self.write_script(status_stdout=status_stdout, print_stdout=STREAM)
                    a = self.adapter(env=self.env(**{var: "test-not-a-real-key"}))
                    with self.assertRaises(LaunchRefused) as cm:
                        a.launch(self.request())
                    self.assertEqual(cm.exception.stop_reason, StopReason.FUNDING_ROUTE_MISMATCH)
                    self.assertEqual(cm.exception.blocker, "api-key would be billed; policy selected cursor-login")
        self.assertEqual(self.recorded("print"), [])
        # An operator who selected the key route explicitly gets it.
        self.write_script(status_stdout=LOGGED_IN, print_stdout=STREAM)
        a = self.adapter(env=self.env(CURSOR_API_KEY="test-not-a-real-key"), expected_route="api-key")
        res = a.launch(self.request())
        self.assertEqual(self.wait_exit(a, res.native_run_handle).stop_reason, StopReason.COMPLETED)
        self.assertEqual(self.manifest()["route"], "api-key")

    def test_unsupported_speed_or_tool_restriction_is_refused_before_anything_runs(self):
        # kills: launching a fast-speed request, or a tool-restricted one that -f would silently widen
        self.write_script(status_stdout=LOGGED_IN, print_stdout=STREAM)
        for kw, expect in (({"speed": "fast"}, "only standard"), ({"allowed_tools": ["Read"]}, "tool"),
                           ({"allowed_tools": []}, "tool")):
            with self.subTest(kw=kw):
                with self.assertRaises(LaunchRefused) as cm:
                    self.adapter().launch(self.request(**kw))
                self.assertEqual(cm.exception.stop_reason, StopReason.LAUNCH_FAILED)
                self.assertIn(expect, cm.exception.blocker)
        self.assertEqual(self.recorded("status") + self.recorded("print"), [])
        self.assertFalse(self.runs_dir.exists())


class Classification(CursorAdapterCase):
    def test_exit_zero_with_output_is_completed_with_unobserved_turns_and_the_stream_kept(self):
        # kills: reporting consumed_units as 0 (or any number) instead of None; dropping result_text or event_types
        a, res, status = self.launch_and_settle(print_stdout=STREAM)
        self.assertEqual((status.state, status.stop_reason), (RunState.FINISHED, StopReason.COMPLETED))
        self.assertIsNone(status.consumed_units)          # the controller charges the full reservation
        self.assertIsNone(status.usage)
        self.assertIs(status.result["turns_unobserved"], True)
        self.assertEqual(status.result["result_text"], STREAM)
        self.assertEqual(status.result["event_types"], ["system", "assistant", "result"])
        self.assertEqual(status.result["unparsed_lines"], 1)   # a line that is not JSON is evidence, not an error
        self.assertEqual(status.result["session_id_candidates"], ["chat-0001"])
        self.assertEqual(status.result["exit"], 0)
        self.assertIsNone(res.native_session_id)
        self.assertIsNone(status.transcript_path)
        [obs] = status.observations
        self.assertEqual((obs.provider, obs.kind, obs.unit, obs.freshness, obs.value),
                         ("cursor", UsageKind.SUBSCRIPTION_ALLOWANCE, "requests", UsageFreshness.UNAVAILABLE, None))
        long_out = "x" * 5000 + "\n"
        a, res, status = self.launch_and_settle(run_id="run-2", print_stdout=long_out)
        self.assertEqual(status.result["result_text"], long_out[:2000])
        self.assertEqual(status.result["event_types"], [])

    def test_the_observed_auth_error_of_a_run_is_auth_missing(self):
        # kills: dropping the `Authentication required` rule, which leaves the observed failure as task-failed
        a, res, status = self.launch_and_settle(print_exit=1, print_stderr=OBSERVED_AUTH_ERROR)
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.AUTH_MISSING))
        self.assertTrue(status.result["blocker"].startswith("Error: Authentication required."), status.result)
        self.assertIn("`cursor-agent login`", status.result["blocker"])
        self.assertIsNone(status.consumed_units)
        self.assertIsNone(status.failure_identity)
        self.assertEqual(status.result["result_text"], "")

    def test_other_failures_are_task_failed_with_a_stable_identity(self):
        # kills: an identity built from unstable input (run id, time) or from unnormalised text, or without the `cursor:` prefix
        stderr = "Error:   Build FAILED  CS0246 the type 'Foo' could not be found\n"
        a, res, first = self.launch_and_settle(print_exit=1, print_stderr=stderr)
        a, res, second = self.launch_and_settle(run_id="run-2", print_exit=1, print_stderr=stderr)
        self.assertEqual((first.state, first.stop_reason), (RunState.FAILED, StopReason.TASK_FAILED))
        self.assertEqual(first.failure_identity, failure_identity(stderr))
        self.assertEqual(second.failure_identity, first.failure_identity)
        self.assertTrue(first.failure_identity.startswith("cursor:"))
        self.assertEqual(len(first.failure_identity), len("cursor:") + 12)
        self.assertEqual(failure_identity("error: build failed cs0246 THE TYPE 'foo' could not be found"),
                         first.failure_identity)
        self.assertNotEqual(failure_identity("Error: CS0103"), first.failure_identity)
        self.assertIsNone(first.consumed_units)

    def test_quota_rate_limit_and_transient_texts_classify_a_failed_run(self):
        # kills: dropping the quota / rate-limit / transient patterns shared with the Claude adapter
        cases = ((dict(print_stderr="Error: You've hit your usage limit\n"), StopReason.QUOTA_EXHAUSTED),
                 (dict(print_stderr="Error: 429 Too Many Requests\n"), StopReason.RATE_LIMITED),
                 (dict(print_stderr="Error: request timed out (ECONNRESET)\n"), StopReason.TRANSIENT),
                 (dict(print_stdout='{"type":"error","message":"rate limit exceeded"}\n'), StopReason.RATE_LIMITED))
        for i, (script, expect) in enumerate(cases):
            with self.subTest(expect=expect, script=script):
                a, res, status = self.launch_and_settle(run_id=f"run-{i}", print_exit=1, **script)
                self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, expect))
                self.assertTrue(status.result["blocker"])
                self.assertIsNone(status.consumed_units)

    def test_a_clean_exit_is_completed_even_when_the_output_mentions_network_or_quota(self):
        # kills: applying the failure texts before the exit-0 success rule (model and tool text would turn a finished run into a retry)
        out = '{"type":"assistant","text":"the network test timed out; the usage limit and rate limit docs say so"}\n'
        a, res, status = self.launch_and_settle(print_stdout=out, print_stderr="warning: network slow\n")
        self.assertEqual((status.state, status.stop_reason), (RunState.FINISHED, StopReason.COMPLETED))

    def test_a_clean_exit_with_no_output_is_no_final_result(self):
        # kills: counting an exit 0 that printed nothing as completed
        a, res, status = self.launch_and_settle(print_stdout="")
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.NO_FINAL_RESULT))
        self.assertIsNone(status.consumed_units)


class CancelAndReconcile(CursorAdapterCase):
    def launch_sleeping(self, **script):
        self.write_script(status_stdout=LOGGED_IN, print_stdout=STREAM, sleep_seconds=60, **script)
        a = self.adapter()
        res = a.launch(self.request())
        self.assertTrue(wait_for(lambda: self.recorded("print")), "the fake CLI never started")
        return a, res

    def test_cancel_records_first_then_kills_the_whole_tree(self):
        # kills: killing before cancelled_at is recorded; killing only the launcher (taskkill without /T, kill instead of killpg)
        a, res = self.launch_sleeping(spawn_child=True)
        self.assertTrue(wait_for(lambda: self.recorded("print")[0].get("child_pid")), "the fake never started its child")
        child = self.recorded("print")[0]["child_pid"]
        self.assertTrue(pid_alive(child))
        self.assertEqual(a.poll(res.native_run_handle).state, RunState.RUNNING)
        marker = Path(res.workspace) / "half-done.txt"
        marker.write_text("evidence\n", encoding="utf-8")
        seen = []
        real_kill_tree = cursor_module.kill_tree

        def spy(pid):
            seen.append((pid, self.manifest()["cancelled_at"]))
            real_kill_tree(pid)

        with mock.patch.object(cursor_module, "kill_tree", spy):
            a.cancel(res.native_run_handle)
        self.assertEqual(len(seen), 1)
        self.assertEqual(seen[0][0], self.manifest()["pid"])
        self.assertIsNotNone(seen[0][1], "the process was killed before the cancellation was recorded")
        self.assertTrue(wait_for(lambda: not pid_alive(child)), "the launcher's child survived cancel: not a tree kill")
        status = a.poll(res.native_run_handle)
        self.assertEqual((status.state, status.stop_reason), (RunState.CANCELLED, StopReason.CANCELLED))
        self.assertIsNone(status.consumed_units)
        self.assertEqual(status.result["cancelled_at"], self.manifest()["cancelled_at"])
        self.assertTrue(marker.exists())
        a.cancel(res.native_run_handle)          # again, and of a run that never existed: not an error
        a.cancel("run-never-existed")

    def test_a_live_process_is_running_even_with_a_cancel_recorded_and_a_fresh_adapter_cancels_it(self):
        # kills: poll answering cancelled while the process still lives (the contract checks the pid first)
        a, res = self.launch_sleeping()
        manifest = self.manifest()
        manifest["cancelled_at"] = "2026-10-12T00:00:00Z"     # a cancel whose kill never landed
        (self.runs_dir / "run-1" / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        self.assertEqual(a.poll(res.native_run_handle).state, RunState.RUNNING)
        fresh = self.adapter()
        found = fresh.reconcile("run-1", res.native_run_handle)
        self.assertTrue(found.found)
        self.assertEqual(found.status.state, RunState.RUNNING)
        self.assertIsNone(a._procs["run-1"].poll(), "the existence check must not terminate the process")
        fresh.cancel(res.native_run_handle)
        self.assertTrue(wait_for(lambda: a._procs["run-1"].poll() is not None), "the process survived cancel")
        self.assertTrue(wait_for(lambda: fresh.poll("run-1").state != RunState.RUNNING))
        self.assertEqual(fresh.poll("run-1").stop_reason, StopReason.CANCELLED)

    def test_reconcile_classifies_a_recorded_run_and_reports_a_missing_one(self):
        # kills: reconcile answering found without a manifest, or not classifying from the manifest after a restart
        ghost = self.adapter().reconcile("run-ghost", None)
        self.assertFalse(ghost.found)
        self.assertIsNone(ghost.status)
        self.launch_and_settle(print_exit=1, print_stderr=OBSERVED_AUTH_ERROR)
        found = self.adapter().reconcile("run-1", "run-1")
        self.assertTrue(found.found)
        self.assertEqual((found.native_session_id, found.native_run_handle), (None, "run-1"))
        self.assertEqual(found.status.stop_reason, StopReason.AUTH_MISSING)
        self.assertEqual(found.status.result["exit"], 1)

    def test_capabilities_and_observe_usage_declare_only_the_observed(self):
        # kills: declaring an unobserved capability True, or an allowance figure that was never printed
        caps = CursorAdapter.capabilities
        for name in ("exact_resume", "cancel", "structured_output", "usage_observation", "transcript_retention",
                     "turn_limit"):
            self.assertIs(getattr(caps, name), False, name)
        self.assertIs(caps.funding_route_check, True)
        self.assertEqual(caps.evidence, "probe-cursor-report-1.json (2026-10-12): logged out; -p runs unexercised")
        obs = self.adapter().observe_usage()
        self.assertEqual((obs.provider, obs.kind, obs.unit, obs.freshness, obs.value, obs.total),
                         ("cursor", UsageKind.SUBSCRIPTION_ALLOWANCE, "requests", UsageFreshness.UNAVAILABLE, None, None))


class Resolution(CursorAdapterCase):
    def make_version(self, versions, name, files=("cursor-agent.cmd",), age=0.0):
        d = versions / name
        d.mkdir(parents=True)
        for f in files:
            (d / f).write_text("", encoding="utf-8")
        t = time.time() - age
        os.utime(d, (t, t))
        return d

    def test_resolver_prefers_path_then_the_newest_versions_dir(self):
        # kills: sorting version names as text, by modification time alone, or picking a directory without cursor-agent.cmd
        appdata = self.tmp / "appdata"
        versions = appdata.joinpath(*cursor_module.CURSOR_AGENT_VERSIONS)
        self.make_version(versions, "2026.9.30-aaaaaaa", age=0)            # newest mtime, older date
        self.make_version(versions, "2026.10.01-14929f9", age=3000)        # same date, older build
        expected = self.make_version(versions, "2026.10.01-e373342", age=1000)
        self.make_version(versions, "2026.12.31-abcdef0", files=())        # no launcher in it
        self.make_version(versions, "2027.01.01-zzz")                      # not a version name
        none = lambda name: None  # noqa: E731
        self.assertEqual(resolve_cli(which=none, appdata=str(appdata), windows=True),
                         str(expected / "cursor-agent.cmd"))
        self.assertEqual(resolve_cli(which=lambda name: "/opt/bin/cursor-agent", appdata=str(appdata), windows=True),
                         "/opt/bin/cursor-agent")
        self.assertIsNone(resolve_cli(which=none, appdata=str(self.tmp / "empty"), windows=True))
        home = self.tmp / "home"
        self.assertIsNone(resolve_cli(which=none, home=str(home), windows=False))
        (home / ".local" / "bin").mkdir(parents=True)
        (home / ".local" / "bin" / "cursor-agent").write_text("", encoding="utf-8")
        self.assertEqual(resolve_cli(which=none, home=str(home), windows=False),
                         str(home / ".local" / "bin" / "cursor-agent"))

    def test_a_cmd_launcher_runs_as_node_index_js_and_a_bare_cmd_refuses_cmd_unsafe_prompts(self):
        # kills: launching through cursor-agent.cmd, where cmd.exe cuts a multi-line prompt at its first line break
        version = self.make_version(self.tmp / "v", "2026.10.01-e373342", files=("cursor-agent.cmd", "node.exe", "index.js"))
        cmd = str(version / "cursor-agent.cmd")
        self.assertEqual(node_direct(cmd), [str(version / "node.exe"), str(version / "index.js")])
        root = self.tmp / "standalone"
        newest = self.make_version(root / "versions", "2026.10.01-e373342", files=("node.exe", "index.js"))
        self.make_version(root / "versions", "2026.09.28-64d2043", files=("node.exe", "index.js"))
        (root / "cursor-agent.cmd").write_text("", encoding="utf-8")
        self.assertEqual(node_direct(str(root / "cursor-agent.cmd")),
                         [str(newest / "node.exe"), str(newest / "index.js")])
        env = self.env(LOCALAPPDATA=str(self.tmp / "local"))
        env.pop("NODE_COMPILE_CACHE", None)
        a = self.adapter(cli=cmd, env=env)
        prefix, launcher, extra = a._launcher()
        self.assertEqual((prefix, launcher), ([str(version / "node.exe"), str(version / "index.js")], "node-direct"))
        env = a._env(extra)
        self.assertEqual(env["CURSOR_INVOKED_AS"], "cursor-agent.cmd")
        self.assertEqual(env["NODE_COMPILE_CACHE"], str(self.tmp / "local" / "cursor-compile-cache"))
        bare = self.tmp / "bare" / "cursor-agent.cmd"
        bare.parent.mkdir()
        bare.write_text("", encoding="utf-8")
        calls = self.spy_popen()
        with self.assertRaises(LaunchRefused) as cm:
            self.adapter(cli=str(bare)).launch(self.request(prompt="card #779: lane B\n\nHandoff: 100% done & more"))
        self.assertEqual(cm.exception.stop_reason, StopReason.LAUNCH_FAILED)
        self.assertIn("cmd.exe", cm.exception.blocker)
        self.assertEqual(calls, [])                 # refused before the status check: nothing ran


class ThroughTheController(ControllerCase):
    """End to end: the controller dispatches a cursor task through the real adapter and the fake CLI."""

    def setUp(self):
        super().setUp()
        self.argv_dir = self.tmp / "argv"
        self.argv_dir.mkdir()
        self.script_path = self.tmp / "script.json"
        self.policy_path = self.write_policy(lambda raw: raw["providers"]["cursor"].update({"enabled": True}))

    def controller(self, **script):
        self.script_path.write_text(json.dumps(script), encoding="utf-8")
        env = {k: v for k, v in os.environ.items() if k not in cursor_module.API_KEY_VARS}
        env.update({"FAKE_CURSOR_SCRIPT": str(self.script_path), "FAKE_CURSOR_ARGV_OUT": str(self.argv_dir) + os.sep})
        self.adapter = CursorAdapter(str(self.tmp / "runs"), cli=[sys.executable, FAKE_CLI], env=env, clock=self.clock)
        self.store = self.init_store()
        self.policy = self.load()
        self.ctl = Controller(self.store, self.policy, {"cursor": self.adapter}, holder_id="holder-a", host="host-a",
                              pid=4242, clock=self.clock)
        self.ctl.ensure_task("t1", workstream="budgeted-autonomy", provider="cursor", card_number=779, title="PR 5")
        return self.ctl

    def tick_until_settled(self, ctl, timeout=20.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            r = ctl.tick()
            if r.action == "settled":
                return r
            time.sleep(0.1)
        self.fail("the run never settled")

    def prints(self):
        return sorted(self.argv_dir.glob("print-*.json"))

    def test_a_completed_cursor_run_is_charged_the_full_reservation(self):
        # kills: any consumed_units other than None reaching the controller, which would charge less than the reservation
        ctl = self.controller(status_stdout=LOGGED_IN, print_stdout=STREAM)
        r = ctl.tick()
        self.assertEqual(r.action, "launched")
        reservation = r.details["reservation"]
        self.assertEqual(reservation, 20.0)
        settled = self.tick_until_settled(ctl)
        self.assertEqual(settled.settled[0]["stop_reason"], StopReason.COMPLETED)
        self.assertEqual(settled.settled[0]["charged"], reservation)
        self.assertEqual(self.task()["state"], TaskState.DONE)
        run = self.store.row("SELECT * FROM runs")
        self.assertEqual((run["state"], run["consumed"]), (RunState.FINISHED, reservation))
        self.assertIs(json.loads(run["result_json"])["turns_unobserved"], True)
        pool = self.pool()
        self.assertEqual((pool["consumed"], pool["reserved"]), (reservation, 0.0))
        argv = json.loads(self.prints()[0].read_text(encoding="utf-8"))["argv"]
        self.assertEqual(argv[argv.index("--model") + 1], "composer-standard")

    def test_a_logged_out_cli_blocks_the_task_on_auth_without_a_run(self):
        # kills: launching a run while `cursor-agent status` says `Not logged in`
        ctl = self.controller(print_stdout=STREAM)       # the observed logged-out status
        r = ctl.tick()
        self.assertEqual((r.action, r.reason), ("blocked", StopReason.AUTH_MISSING))
        t = self.task()
        self.assertEqual(t["state"], TaskState.BLOCKED_AUTH)
        self.assertIn("cursor-agent login", t["blocker"])
        self.assertEqual(self.prints(), [])
        self.assertEqual(self.pool()["consumed"], 0.0)
        for _ in range(2):
            self.assertEqual(ctl.tick().action, "idle")
        self.assertEqual(self.prints(), [])


if __name__ == "__main__":
    unittest.main()
