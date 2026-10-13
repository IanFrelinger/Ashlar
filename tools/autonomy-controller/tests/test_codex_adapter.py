"""The Codex CLI adapter against a scripted stand-in (tests/fake_codex_cli.py). No test here runs a real
`codex`: the adapter is always given `cli=[sys.executable, fake_codex_cli.py]`, and the resolver tests only
build files in temp directories. Each test names, in a one-line comment, the mutation it kills."""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

from ashlar_controller.adapters import codex as codex_module
from ashlar_controller.adapters.codex import CodexAdapter, failure_identity, locate_session_file, resolve_cli
from ashlar_controller.controller import Controller
from ashlar_controller.models import (LaunchRefused, LaunchRequest, RunState, StopReason, TaskState, UsageFreshness,
                                      UsageKind)
from ashlar_controller.store import pid_alive

from .helpers import ControllerCase, FakeClock

FAKE_CLI = str(Path(__file__).resolve().parent / "fake_codex_cli.py")
THREAD = "01a12e26-f397-73f0-a7fc-b3877d7c6a77"
PROMPT = "card #779: PR 5"
UUID_RE = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")


def compact(obj):
    return json.dumps(obj, separators=(",", ":"))


def thread_started(thread_id=THREAD):
    return {"type": "thread.started", "thread_id": thread_id}


TURN_STARTED = {"type": "turn.started"}


def agent(item_id, text):
    return {"type": "item.completed", "item": {"id": item_id, "type": "agent_message", "text": text}}


def warning(item_id, text):
    return {"type": "item.completed", "item": {"id": item_id, "type": "error", "message": text}}


def command(item_id, exit_code=0):
    cmd = "powershell.exe -Command \"Set-Content -LiteralPath hello.txt -Value 'hello'\""
    started = {"type": "item.started", "item": {"id": item_id, "type": "command_execution", "command": cmd,
                                                "aggregated_output": "", "exit_code": None, "status": "in_progress"}}
    done = {"type": "item.completed", "item": {"id": item_id, "type": "command_execution", "command": cmd,
                                               "aggregated_output": "", "exit_code": exit_code,
                                               "status": "completed" if exit_code == 0 else "failed"}}
    return [started, done]


def turn_completed(output_tokens=64):
    return {"type": "turn.completed", "usage": {"input_tokens": 36656, "cached_input_tokens": 31360,
                                                "cache_write_input_tokens": 0, "output_tokens": output_tokens,
                                                "reasoning_output_tokens": 0}}


def api_failure(text, status=None):
    """The observed failure tail: an `error` event, then `turn.failed`, both carrying the same message,
    which is itself compact JSON when the API answered with a status (p4, p5)."""
    message = text if status is None else compact({"type": "error", "status": status,
                                                   "error": {"type": "invalid_request_error", "message": text}})
    return [{"type": "error", "message": message}, {"type": "turn.failed", "error": {"message": message}}]


def observed_unsupported_model(model="sol", thread_id="01a12e26-b48c-7c00-8a9c-71a054355129"):
    """probe-codex-report-1.json p5, event for event."""
    return ([thread_started(thread_id),
             warning("item_0", f"Configured service tier `priority` is not advertised as supported for model `{model}` "
                               f"and will be omitted from requests."),
             warning("item_1", f"Model metadata for `{model}` not found. Defaulting to fallback metadata; this can "
                               f"degrade performance and cause issues."),
             TURN_STARTED]
            + api_failure(f"The '{model}' model is not supported when using Codex with a ChatGPT account.", 400))


class CodexAdapterCase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="ashlar-cx-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.runs_dir = self.tmp / "runs"
        self.argv_dir = self.tmp / "argv"
        self.argv_dir.mkdir()
        self.codex_home = self.tmp / "codex-home"
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
        e = {k: v for k, v in os.environ.items() if not k.startswith("OPENAI_") and k != "CODEX_HOME"}
        e["CLAUDECODE"] = "1"                 # the suite may run inside a Claude Code session; the
        e["CLAUDE_CODE_ENTRYPOINT"] = "cli"   # adapter must strip these from the child
        e["FAKE_CODEX_SCRIPT"] = str(self.script_path)
        e["FAKE_CODEX_ARGV_OUT"] = str(self.argv_dir) + os.sep
        e.update(extra)
        return e

    def write_script(self, **script):
        self.script_path.write_text(json.dumps(script), encoding="utf-8")

    def adapter(self, **kw):
        kw.setdefault("env", self.env())
        kw.setdefault("codex_home", str(self.codex_home))
        a = CodexAdapter(str(self.runs_dir), cli=[sys.executable, FAKE_CLI], clock=self.clock, **kw)
        self.adapters.append(a)
        return a

    def request(self, run_id="run-1", **kw):
        base = dict(run_id=run_id, task_id="t1", provider="codex", model="gpt-6-astra", effort="medium",
                    speed="standard", invocation_ceiling=20, workspace=None, prompt=PROMPT)
        base.update(kw)
        return LaunchRequest(**base)

    def exec_records(self):
        return sorted(self.argv_dir.glob("argv-*.json"))

    def login_records(self):
        return sorted(self.argv_dir.glob("login-*.json"))

    def recorded(self):
        files = self.exec_records()
        self.assertEqual(len(files), 1, files)
        return json.loads(files[0].read_text(encoding="utf-8"))

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

    def wait_for_exec(self, timeout=20.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.exec_records():
                return
            time.sleep(0.05)
        self.fail("the fake CLI never started")

    def launch_and_settle(self, request=None, **script):
        self.write_script(**script)
        a = self.adapter()
        res = a.launch(request or self.request())
        return a, res, self.wait_exit(a, res.native_run_handle)

    def assert_refused(self, adapter, reason, request=None):
        with self.assertRaises(LaunchRefused) as cm:
            adapter.launch(request or self.request())
        self.assertEqual(cm.exception.stop_reason, reason, cm.exception.blocker)
        self.assertEqual(self.exec_records(), [])                    # nothing spawned
        self.assertFalse((self.runs_dir / "run-1").exists())        # no manifest, no run dir
        return cm.exception.blocker

    def config_path(self):
        return self.codex_home / "config.toml"

    def write_config(self, text):
        """The operator's `<CODEX_HOME>/config.toml`, written byte for byte (LF unless the text says otherwise)."""
        self.codex_home.mkdir(parents=True, exist_ok=True)
        self.config_path().write_bytes(text.encode("utf-8"))

    def start_unrelated_process(self):
        """A harmless sleeping python child of the test: the process Windows gave a finished run's pid to."""
        kwargs = {} if sys.platform.startswith("win") else {"start_new_session": True}   # so a killpg reaches it
        victim = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"], stdin=subprocess.DEVNULL,
                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **kwargs)

        def stop():
            if victim.poll() is None:
                victim.kill()
            victim.wait(timeout=10)

        self.addCleanup(stop)
        return victim

    def point_manifest_at(self, pid, run_id="run-1"):
        path = self.runs_dir / run_id / "manifest.json"
        manifest = json.loads(path.read_text(encoding="utf-8"))
        manifest["pid"] = pid
        path.write_bytes((json.dumps(manifest, indent=2, sort_keys=True) + "\n").encode("utf-8"))


class Argv(CodexAdapterCase):
    def test_fresh_argv_overrides_model_effort_sandbox_approval_and_plugins(self):
        # Mutation killed: dropping `-c features.plugins=false` (phase F finding C8: the operator's plugins, among
        # them computer-use, browser and chrome, were offered to every unattended run; `mcp_servers={}` removed none).
        a, res, status = self.launch_and_settle()
        argv = self.recorded()["argv"]
        self.assertEqual(argv[:3], ["exec", "--json", "--skip-git-repo-check"])
        workspace = str(self.runs_dir / "run-1" / "workspace")
        for flag, value in (("-C", workspace), ("-s", "workspace-write"), ("-m", "gpt-6-astra"),
                            ("-o", str(self.runs_dir / "run-1" / "last_message.txt"))):
            self.assertIn(flag, argv)
            self.assertEqual(os.path.normcase(argv[argv.index(flag) + 1]), os.path.normcase(value), flag)
        overrides = [argv[i + 1] for i, x in enumerate(argv) if x == "-c"]
        self.assertEqual(sorted(overrides), sorted(['approval_policy="never"', 'model_reasoning_effort="medium"',
                                                    "features.plugins=false", "notify=[]"]))
        self.assertNotIn("mcp_servers={}", argv)          # observed to remove nothing; it is not claimed any more
        self.assertEqual(os.path.normcase(self.recorded()["cwd"]), os.path.normcase(workspace))
        self.assertEqual((self.manifest()["plugins"], self.manifest()["mcp_servers_disabled"]), ("disabled", []))

    def test_every_mcp_server_the_config_declares_is_disabled_on_a_run_and_a_resume(self):
        # Mutation killed: `isolation_overrides` without the per-server loop (the config's `node_repl`, a REPL with
        # browser access on the probed host, stayed enabled: `-c mcp_servers={}` does not remove it).
        self.write_config('model = "gpt-6-astra"\n\n[mcp_servers.node_repl]\ncommand = "node_repl.exe"\n\n'
                          '[mcp_servers.node_repl.env]\nX = "1"\n\n[mcp_servers.docs-2]\ncommand = "docs"\n')
        for name, reader in ServiceTier.readers(self):
            with self.subTest(reader=name), mock.patch.object(codex_module, "_tomllib", reader):
                self.assertEqual(codex_module.configured_mcp_servers(str(self.config_path()))["names"],
                                 ["docs-2", "node_repl"])
        a, res, status = self.launch_and_settle()
        argv = self.recorded()["argv"]
        for name in ("node_repl", "docs-2"):
            self.assertTrue(any(argv[i:i + 2] == ["-c", f"mcp_servers.{name}.enabled=false"]
                                for i in range(len(argv) - 1)), name)
        self.assertEqual(self.manifest()["mcp_servers_disabled"], ["docs-2", "node_repl"])

    def test_an_mcp_server_name_that_cannot_be_disabled_is_refused(self):
        # Mutation killed: dropping the refusal of an unsafe name (a quoted key the CLI's -c handling is not known to
        # accept: the server would stay enabled for the run).
        self.write_config('[mcp_servers."two words"]\ncommand = "x"\n')
        self.write_script()
        blocker = self.assert_refused(self.adapter(), StopReason.LAUNCH_FAILED)
        self.assertIn("['two words']", blocker)
        self.assertEqual(self.login_records(), [])

    def test_fresh_argv_never_ignores_user_config_names_no_thread_and_ends_with_the_prompt(self):
        # Mutation killed: passing `--ignore-user-config` (observed: every write is then rejected by policy).
        a, res, status = self.launch_and_settle()
        argv = self.recorded()["argv"]
        self.assertNotIn("--ignore-user-config", argv)
        self.assertNotIn("--ephemeral", argv)          # the session file is the transcript
        self.assertNotIn("resume", argv)
        self.assertNotIn("--session-id", argv)
        self.assertEqual([x for x in argv if UUID_RE.search(x)], [])   # a fresh launch names no thread
        self.assertEqual(argv[-1], PROMPT)

    def test_effort_comes_from_the_request(self):
        # Mutation killed: a hard-coded `model_reasoning_effort="medium"`.
        a, res, status = self.launch_and_settle(request=self.request(effort="low"))
        argv = self.recorded()["argv"]
        self.assertIn('model_reasoning_effort="low"', argv)
        self.assertNotIn('model_reasoning_effort="medium"', argv)

    def test_resume_runs_exec_resume_with_the_thread_id_and_reports_it(self):
        # Mutation killed: a fresh `exec` (a new thread) when the request names a thread to resume, and a resume
        # without the overrides (observed, probe-codex-report-4.json r5: without `-c sandbox_mode` and
        # `mcp_servers={}` a resume would inherit the operator's config.toml sandbox and MCP servers).
        a, res, status = self.launch_and_settle(request=self.request(resume_session_id=THREAD))
        argv = self.recorded()["argv"]
        self.assertEqual(argv[:4], ["exec", "resume", "--json", "--skip-git-repo-check"])
        self.assertEqual(argv[-2:], [THREAD, PROMPT])
        for pair in (["-c", 'sandbox_mode="workspace-write"'], ["-c", 'approval_policy="never"'],
                     ["-c", "features.plugins=false"], ["-c", "notify=[]"], ["-m", self.request().model]):
            self.assertTrue(any(argv[i:i + 2] == pair for i in range(len(argv) - 1)), pair)
        self.assertIn("-o", argv)
        self.assertNotIn("-s", argv)                      # `exec resume` has no -s; the sandbox is a -c override
        self.assertEqual(res.native_session_id, THREAD)
        self.assertEqual(status.result["thread_id"], THREAD)
        manifest = self.manifest()
        self.assertEqual((manifest["resume_of"], manifest["thread_id"]), (THREAD, THREAD))

    def test_stdin_is_closed(self):
        # Mutation killed: dropping `stdin=subprocess.DEVNULL` from the spawn (the CLI then reads stdin).
        spawned = []
        real_popen = subprocess.Popen

        def recording_popen(*args, **kwargs):
            spawned.append((list(args[0]) if args else list(kwargs.get("args") or []), kwargs.get("stdin")))
            return real_popen(*args, **kwargs)

        self.write_script()
        a = self.adapter()
        with mock.patch.object(subprocess, "Popen", recording_popen):
            res = a.launch(self.request())
        status = self.wait_exit(a, res.native_run_handle)
        self.assertEqual(status.stop_reason, StopReason.COMPLETED)
        execs = [stdin for argv, stdin in spawned if "exec" in argv]
        self.assertEqual(execs, [subprocess.DEVNULL])
        self.assertTrue(all(stdin is subprocess.DEVNULL for argv, stdin in spawned), spawned)   # login status too
        self.assertIs(self.recorded()["stdin"]["eof"], True)
        self.assertNotIn("Reading additional input from stdin",
                         (self.runs_dir / "run-1" / "stderr.log").read_text(encoding="utf-8"))

    def test_child_env_strips_claude_markers_keeps_openai_and_sets_codex_home(self):
        # Mutation killed: stripping OPENAI_* from the child env (the route check could no longer see a key).
        self.write_script()
        a = self.adapter(env=self.env(OPENAI_API_KEY="sk-test-not-a-real-key"), expected_route="api-key")
        res = a.launch(self.request())
        self.wait_exit(a, res.native_run_handle)
        for record in (self.recorded(), json.loads(self.login_records()[0].read_text(encoding="utf-8"))):
            env = record["env"]
            self.assertNotIn("CLAUDECODE", env)
            self.assertFalse([k for k in env if k.startswith("CLAUDE_CODE_")], env)
            self.assertEqual(env.get("OPENAI_API_KEY"), "<set>")
            self.assertEqual(os.path.normcase(env["CODEX_HOME"]), os.path.normcase(str(self.codex_home)))
        self.assertEqual(self.manifest()["route"], "api-key")


class Route(CodexAdapterCase):
    def test_logged_in_text_on_stderr_is_the_chatgpt_route(self):
        # Mutation killed: reading only stdout of `login status` (observed: the text is on stderr).
        a, res, status = self.launch_and_settle()
        self.assertEqual(status.stop_reason, StopReason.COMPLETED)
        self.assertEqual(len(self.login_records()), 1)       # checked before the launch
        self.assertEqual(self.manifest()["route"], "chatgpt")

    def test_logged_out_status_refuses_auth_missing_and_spawns_nothing(self):
        # Mutation killed: taking any `login status` that exits 0 as logged in.
        self.write_script(login_status_stdout="Not logged in\n")
        blocker = self.assert_refused(self.adapter(), StopReason.AUTH_MISSING)
        self.assertIn("Not logged in", blocker)
        self.assertIn("unobserved", blocker)
        self.assertIn("codex login", blocker)

    def test_failing_login_status_is_auth_missing(self):
        # Mutation killed: ignoring the exit code of `login status`.
        self.write_script(login_status_stderr="Logged in using ChatGPT\n", login_status_exit=1)
        blocker = self.assert_refused(self.adapter(), StopReason.AUTH_MISSING)
        self.assertIn("exit 1", blocker)
        missing = CodexAdapter(str(self.runs_dir), cli=str(self.tmp / "no-such-codex.exe"), env=self.env(),
                               codex_home=str(self.codex_home))
        self.assertIn("did not run", self.assert_refused(missing, StopReason.AUTH_MISSING))

    def test_openai_api_key_is_a_funding_route_mismatch(self):
        # Mutation killed: ignoring OPENAI_API_KEY in the child env (the key, paid spend, would be billed).
        self.write_script()
        a = self.adapter(env=self.env(OPENAI_API_KEY="sk-test-not-a-real-key"))
        blocker = self.assert_refused(a, StopReason.FUNDING_ROUTE_MISMATCH)
        self.assertEqual(blocker, "api-key would be billed; policy selected chatgpt")

    def test_speed_and_a_tool_restriction_are_refused_before_any_process(self):
        # Mutation killed: dropping the speed check (a fast launch would run at standard speed, unrecorded).
        self.write_script()
        a = self.adapter()
        self.assertIn("only standard", self.assert_refused(a, StopReason.LAUNCH_FAILED, self.request(speed="fast")))
        self.assertIn("tool-restriction", self.assert_refused(a, StopReason.LAUNCH_FAILED,
                                                              self.request(allowed_tools=[])))
        self.assertEqual(self.login_records(), [])
        self.assertFalse(self.runs_dir.exists())


PROFILE_WHERE = "[profiles.fast], which the top-level `profile` selects"


class ServiceTier(CodexAdapterCase):
    """ENV-3: the operator's `config.toml` set `service_tier = "priority"` (the Codex app's Fast mode), and the
    adapter keeps the user config loaded, so every controller run would have used it at speed standard."""

    def readers(self):
        """The two ways `read_service_tier` reads a file: the line reader (Python 3.8-3.10, or a file tomllib
        cannot parse) and tomllib (3.11+)."""
        out = [("line reader", None)]
        if codex_module._tomllib is not None:
            out.append(("tomllib", codex_module._tomllib))
        return out

    def test_an_unset_service_tier_launches_and_the_manifest_records_it(self):
        # Mutation killed: leaving `service_tier` out of the manifest (the run's own evidence would not show it).
        a, res, status = self.launch_and_settle()
        self.assertEqual(status.stop_reason, StopReason.COMPLETED)
        manifest = self.manifest()
        self.assertIn("service_tier", manifest)
        self.assertIsNone(manifest["service_tier"])
        self.assertEqual(os.path.normcase(manifest["codex_config"]), os.path.normcase(str(self.config_path())))
        self.write_config('model = "gpt-6-astra"\n# service_tier = "priority"\napproval_policy = "on-request"\n')
        res2 = a.launch(self.request(run_id="run-2"))
        self.assertEqual(self.wait_exit(a, res2.native_run_handle).stop_reason, StopReason.COMPLETED)
        self.assertIsNone(self.manifest("run-2")["service_tier"])

    def test_a_top_level_priority_tier_is_refused_before_any_process_starts(self):
        # Mutation killed: dropping the refusal (every controller run would use the priority tier at speed standard).
        self.write_config('model = "gpt-6-astra"\nmodel_reasoning_effort = "high"\n'
                          'service_tier = "priority"   # the Codex app\'s Fast mode\n\n[profiles.slow]\nmodel = "x"\n')
        self.write_script()
        blocker = self.assert_refused(self.adapter(), StopReason.LAUNCH_FAILED)
        self.assertEqual(self.login_records(), [])            # not even `codex login status` ran
        path = str(self.config_path())
        self.assertIn(f"{path} sets service_tier (line 3 (top level): service_tier = \"priority\")", blocker)
        self.assertIn(f"comment out service_tier in {path} for the controller's runs", blocker)

    def test_a_tier_inside_a_profiles_table_is_not_top_level_and_does_not_count(self):
        # Mutation killed: the line reader ignoring `[table]` headers (a tier the CLI does not apply would refuse).
        self.write_config('model = "gpt-6-astra"\n\n[profiles.fast]\nservice_tier = "priority"\n')
        self.write_script()
        for name, reader in self.readers():
            with self.subTest(reader=name), mock.patch.object(codex_module, "_tomllib", reader):
                shutil.rmtree(self.runs_dir, True)
                a, res, status = self.launch_and_settle()
                self.assertEqual(status.stop_reason, StopReason.COMPLETED)
                self.assertIsNone(self.manifest()["service_tier"])

    def test_a_tier_in_the_profile_the_config_selects_is_refused(self):
        # Mutation killed: reading only the top level (a top-level `profile = "fast"` applies [profiles.fast]).
        self.write_config('profile = "fast"\n\n[profiles.fast]\nservice_tier = "priority"\n')
        self.write_script()
        for name, reader in self.readers():
            with self.subTest(reader=name), mock.patch.object(codex_module, "_tomllib", reader):
                blocker = self.assert_refused(self.adapter(), StopReason.LAUNCH_FAILED)
                self.assertIn(f"line 4 ({PROFILE_WHERE}): service_tier = \"priority\"", blocker)

    def test_a_config_tomllib_cannot_parse_is_read_line_by_line(self):
        # Mutation killed: treating a file tomllib rejects as setting no tier (the CLI's own reader may differ).
        self.write_config('service_tier = "priority"\nthis line is not toml\n')
        self.write_script()
        blocker = self.assert_refused(self.adapter(), StopReason.LAUNCH_FAILED)
        self.assertIn("(line 1 (top level): service_tier = \"priority\")", blocker)

    def test_a_config_that_cannot_be_read_is_refused(self):
        # Mutation killed: treating an unreadable config.toml as absent (the CLI would still load whatever it holds).
        self.config_path().mkdir(parents=True)            # opening a directory fails, and not as "not found"
        self.write_script()
        blocker = self.assert_refused(self.adapter(), StopReason.LAUNCH_FAILED)
        self.assertIn(f"{self.config_path()} could not be read to check its service_tier", blocker)

    def test_the_line_reader_and_tomllib_find_the_same_tiers_on_the_same_lines(self):
        # Mutation killed: a line reader that is not a TOML reader at all (a grep for `service_tier`).
        top = "top level"
        cases = (
            ('service_tier = "priority"\n', "priority", [(top, 1, "priority")]),
            ("model = 'x'\nservice_tier='flex'  # the app's setting\n", "flex", [(top, 2, "flex")]),
            ('"service_tier" = "priority"\n', "priority", [(top, 1, "priority")]),
            ('service_tier = "priority"\r\nmodel = "x"\r\n', "priority", [(top, 1, "priority")]),
            ('﻿service_tier = "priority"\n', "priority", [(top, 1, "priority")]),
            ('model = "x"\n\n[profiles.fast]\nservice_tier = "priority"\n', None, []),
            ('profile = "fast"\n\n[profiles.fast]\nservice_tier = "flex"\n', "flex", [(PROFILE_WHERE, 4, "flex")]),
            ('profile = "fast"\nservice_tier = "priority"\n[profiles.fast]\nservice_tier = "flex"\n', "flex",
             [(top, 2, "priority"), (PROFILE_WHERE, 4, "flex")]),
            ('profile = "fast"\nprofiles.fast.service_tier = "flex"\n', "flex", [(PROFILE_WHERE, 2, "flex")]),
            ('profile = "my.p"\n[profiles."my.p"]\nservice_tier = "flex"\n', "flex",
             [("[profiles.my.p], which the top-level `profile` selects", 3, "flex")]),
            ('[profiles.fast]\nservice_tier = "flex"\n[other]\nprofile = "fast"\n', None, []),
            ('notes = """\nservice_tier = "priority"\n"""\n', None, []),
            ('[[skills]]\nservice_tier = "priority"\n', None, []),
            ('# service_tier = "priority"\n', None, []),
            ("", None, []),
        )
        path = self.tmp / "config-case.toml"
        for name, reader in self.readers():
            for text, value, sources in cases:
                with self.subTest(reader=name, text=text), mock.patch.object(codex_module, "_tomllib", reader):
                    path.write_bytes(text.encode("utf-8"))
                    info = codex_module.read_service_tier(str(path))
                    self.assertIsNone(info["error"])
                    self.assertEqual(info["value"], value)
                    self.assertEqual([(s["where"], s["line"], s["value"]) for s in info["sources"]], sources)
        self.assertEqual(codex_module.read_service_tier(str(self.tmp / "absent.toml"))["sources"], [])


class Classification(CodexAdapterCase):
    def test_completed_run_charges_the_non_error_completed_items(self):
        # Mutation killed: counting `error` items (warnings) in consumed_units.
        events = ([thread_started(), warning("item_0", "Configured service tier `priority` is not advertised"),
                   warning("item_1", "Model metadata for `gpt-6-astra` not found."), TURN_STARTED,
                   agent("item_2", "I'll create hello.txt.")] + command("item_3")
                  + [agent("item_4", "done"), turn_completed(output_tokens=64)])
        a, res, status = self.launch_and_settle(events=events)
        self.assertEqual((status.state, status.stop_reason), (RunState.FINISHED, StopReason.COMPLETED))
        self.assertEqual(status.consumed_units, 3.0)   # two agent messages and one command; not the two warnings
        self.assertEqual(status.result["items_by_type"], {"error": 2, "agent_message": 2, "command_execution": 1})
        self.assertEqual(res.native_session_id, THREAD)
        self.assertEqual(self.manifest()["thread_id"], THREAD)
        self.assertEqual((status.result["thread_id"], status.result["last_message"], status.result["exit"]),
                         (THREAD, "done", 0))
        self.assertEqual(status.result["usage"]["output_tokens"], 64)
        self.assertEqual(status.result["stream_events"], 9)
        self.assertEqual(Path(status.result["last_message_path"]).read_text(encoding="utf-8"), "done")
        self.assertEqual(len(status.result["warnings"]), 2)
        self.assertIsNone(status.usage)                # nothing stands in for consumption
        self.assertEqual(len(status.observations), 1)
        obs = status.observations[0]
        self.assertEqual((obs.provider, obs.kind, obs.unit, obs.freshness, obs.value, obs.native_session_id),
                         ("codex", UsageKind.SUBSCRIPTION_ALLOWANCE, "turns", UsageFreshness.UNAVAILABLE, None, THREAD))
        self.assertEqual(obs.raw["tokens"]["input_tokens"], 36656)

    def test_observed_400_model_not_supported_is_model_unrecognized_and_consumes_nothing(self):
        # Mutation killed: classifying the observed 400 "model is not supported" as task-failed.
        a, res, status = self.launch_and_settle(request=self.request(model="sol"), events=observed_unsupported_model(),
                                                exit=1)
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.MODEL_UNRECOGNIZED))
        self.assertEqual(status.consumed_units, 0.0)
        self.assertEqual(status.result["model"], "sol")
        self.assertEqual(status.result["error_status"], 400)
        self.assertIn("model is not supported", status.result["blocker"])
        self.assertIsNone(status.failure_identity)
        self.assertEqual(self.recorded()["argv"][self.recorded()["argv"].index("-m") + 1], "sol")

    def test_a_400_with_other_text_is_task_failed_not_model_unrecognized(self):
        # Mutation killed: reading status 400 alone as an unrecognised model.
        text = "Invalid 'input': string too long."
        a, res, status = self.launch_and_settle(events=[thread_started(), TURN_STARTED] + api_failure(text, 400), exit=1)
        self.assertEqual(status.stop_reason, StopReason.TASK_FAILED)
        self.assertEqual(status.failure_identity, failure_identity(text))

    def test_task_failed_identity_is_stable_and_normalised(self):
        # Mutation killed: an identity over the raw text (whitespace or case would defeat the retry counter).
        text = "Build failed:   CS0246 the type 'Foo'  could not be found"
        events = [thread_started(), TURN_STARTED, agent("item_0", "building")] + api_failure(text)
        a, res, status = self.launch_and_settle(events=events, exit=1)
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.TASK_FAILED))
        self.assertEqual(status.consumed_units, 1.0)
        self.assertEqual(status.failure_identity, failure_identity(text))
        self.assertTrue(status.failure_identity.startswith("codex:"))
        self.assertEqual(len(status.failure_identity), len("codex:") + 12)
        self.assertEqual(failure_identity("build failed: cs0246 THE TYPE 'foo' could not be found"), status.failure_identity)
        self.assertNotEqual(failure_identity("Build failed: CS0103"), status.failure_identity)

    def test_api_errors_map_by_status_then_by_text(self):
        # Mutation killed: dropping the status rules (a bare 429 or 503 would park the task as task-failed).
        cases = ((401, "nope", StopReason.AUTH_MISSING), (None, "Unauthorized: token expired", StopReason.AUTH_MISSING),
                 (402, "payment required", StopReason.QUOTA_EXHAUSTED),
                 (None, "You've hit your usage limit. Resets at 3pm", StopReason.QUOTA_EXHAUSTED),
                 (429, "slow down", StopReason.RATE_LIMITED), (None, "Too many requests", StopReason.RATE_LIMITED),
                 (503, "x", StopReason.TRANSIENT), (408, "x", StopReason.TRANSIENT),
                 (None, "stream disconnected: network error", StopReason.TRANSIENT))
        for code, text, reason in cases:
            with self.subTest(status=code, text=text):
                shutil.rmtree(self.runs_dir, True)
                a, res, status = self.launch_and_settle(events=[thread_started(), TURN_STARTED] + api_failure(text, code),
                                                        exit=1)
                self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, reason))
                self.assertEqual(status.result["blocker"], text)
                self.assertEqual(status.consumed_units, 0.0)

    def test_exit_without_turn_completed_is_no_final_result(self):
        # Mutation killed: reading an exited stream with no turn.completed as completed.
        events = [thread_started(), TURN_STARTED, agent("item_0", "I'll write the story into story.txt.")]
        a, res, status = self.launch_and_settle(events=events, exit=1, stderr="worker died\n")
        self.assertEqual((status.state, status.stop_reason), (RunState.FAILED, StopReason.NO_FINAL_RESULT))
        self.assertIsNone(status.consumed_units)   # charged at the full reservation
        self.assertIn("worker died", status.result["stderr_tail"])
        self.assertEqual(status.result["thread_id"], THREAD)

    def test_a_completed_turn_without_an_agent_message_is_no_final_result(self):
        # Mutation killed: calling a turn with an empty final agent message completed.
        a, res, status = self.launch_and_settle(events=[thread_started(), TURN_STARTED, turn_completed()])
        self.assertEqual(status.stop_reason, StopReason.NO_FINAL_RESULT)
        self.assertIs(status.result["empty_last_message"], True)

    def test_a_completed_turn_stands_over_an_earlier_error_event(self):
        # Mutation killed: classifying top-level `error` events before the turn's own outcome.
        events = ([thread_started(), TURN_STARTED, {"type": "error", "message": "Reconnecting... network hiccup"},
                   agent("item_0", "done"), turn_completed()])
        a, res, status = self.launch_and_settle(events=events)
        self.assertEqual(status.stop_reason, StopReason.COMPLETED)
        self.assertEqual(status.consumed_units, 1.0)


class CancelResumeReconcile(CodexAdapterCase):
    def test_cancel_records_cancelled_at_before_killing_and_poll_says_cancelled(self):
        # Mutation killed: killing before `cancelled_at` is recorded (a crash between them reads as no-final-result).
        self.write_script(sleep_seconds=60, write_session_file=True)
        a = self.adapter()
        res = a.launch(self.request())
        self.assertTrue(UUID_RE.fullmatch(res.native_session_id or ""), res.native_session_id)
        self.assertEqual(a.poll(res.native_run_handle).state, RunState.RUNNING)
        marker = Path(res.workspace) / "half-done.txt"
        marker.write_text("evidence\n", encoding="utf-8")
        seen = []
        real_kill = a._kill_tree

        def spy(pid):
            seen.append(self.manifest()["cancelled_at"])
            real_kill(pid)

        a._kill_tree = spy
        a.cancel(res.native_run_handle)
        self.assertEqual(len(seen), 1)
        self.assertIsNotNone(seen[0])
        self.assertIsNotNone(a._procs["run-1"].poll(), "the process survived cancel")
        status = a.poll(res.native_run_handle)
        self.assertEqual((status.state, status.stop_reason), (RunState.CANCELLED, StopReason.CANCELLED))
        self.assertIsNone(status.consumed_units)
        self.assertEqual(status.result["resume_session_id"], res.native_session_id)
        self.assertTrue(marker.exists())                             # the workspace is evidence
        self.assertTrue(os.path.isfile(status.transcript_path))      # and so is the session file
        a.cancel("run-never-existed")                                # not an error

    def test_a_thread_seen_after_launch_returned_is_filled_in(self):
        # Mutation killed: never reading `thread.started` after launch (a slow stream loses the thread to resume).
        self.write_script(sleep_seconds=1.5, sleep_after=0)
        a = self.adapter()
        a.thread_wait_seconds = 0.2
        res = a.launch(self.request())
        self.assertIsNone(res.native_session_id)
        self.assertIsNone(self.manifest()["thread_id"])
        status = self.wait_exit(a, res.native_run_handle)
        self.assertEqual(status.stop_reason, StopReason.COMPLETED)
        self.assertTrue(UUID_RE.fullmatch(status.result["thread_id"] or ""))
        self.assertEqual(self.manifest()["thread_id"], status.result["thread_id"])
        self.assertEqual(a.reconcile("run-1", "run-1").native_session_id, status.result["thread_id"])

    def test_resume_locates_the_session_file_by_thread_id(self):
        # Mutation killed: taking any rollout file (or one found by date or run id) instead of the thread's own.
        decoy_dir = self.codex_home / "sessions" / "2026" / "10" / "12"
        decoy_dir.mkdir(parents=True)
        decoy = decoy_dir / "rollout-2026-10-12T00-00-00-00000000-0000-0000-0000-000000000000.jsonl"
        decoy.write_text("{}\n", encoding="utf-8")
        a, res, status = self.launch_and_settle(write_session_file=True)
        thread = res.native_session_id
        self.assertIsNotNone(status.transcript_path)
        self.assertTrue(status.transcript_path.endswith(f"-{thread}.jsonl"), status.transcript_path)
        self.assertEqual(status.result["transcript"], status.transcript_path)
        res2 = a.launch(self.request(run_id="run-2", resume_session_id=thread))
        status2 = self.wait_exit(a, res2.native_run_handle)
        self.assertEqual(res2.native_session_id, thread)
        self.assertEqual(os.path.normcase(status2.transcript_path), os.path.normcase(status.transcript_path))
        self.assertEqual(len(Path(status2.transcript_path).read_text(encoding="utf-8").splitlines()), 3)
        sessions = str(self.codex_home / "sessions")
        self.assertIsNone(locate_session_file(sessions, "11111111-1111-1111-1111-111111111111"))
        self.assertIsNone(locate_session_file(str(self.tmp / "nowhere"), thread))

    def test_reconcile_after_restart_classifies_and_a_missing_manifest_is_not_found(self):
        # Mutation killed: reconcile answering found=True with no manifest (a launch that never happened would settle).
        a, res, status = self.launch_and_settle(events=[thread_started(), TURN_STARTED, agent("item_0", "done"),
                                                        turn_completed()])
        fresh = self.adapter()
        found = fresh.reconcile("run-1", "run-1")
        self.assertTrue(found.found)
        self.assertEqual((found.native_session_id, found.native_run_handle), (THREAD, "run-1"))
        self.assertEqual(found.status.stop_reason, StopReason.COMPLETED)
        ghost = fresh.reconcile("run-ghost", None)
        self.assertFalse(ghost.found)
        self.assertIsNone(ghost.status)

    def test_a_fresh_adapter_sees_a_live_run_as_running_then_cancels_it(self):
        # Mutation killed: classifying from the stream while the pid is still alive (a live run would settle).
        self.write_script(sleep_seconds=60)
        a = self.adapter()
        res = a.launch(self.request())
        self.wait_for_exec()
        fresh = self.adapter()
        found = fresh.reconcile("run-1", res.native_run_handle)
        self.assertTrue(found.found)
        self.assertEqual(found.status.state, RunState.RUNNING)
        self.assertEqual(found.native_session_id, res.native_session_id)
        self.assertIsNone(a._procs["run-1"].poll(), "the existence check must not terminate the process")
        fresh.cancel(res.native_run_handle)
        deadline = time.time() + 20
        while a._procs["run-1"].poll() is None and time.time() < deadline:
            time.sleep(0.05)
        self.assertIsNotNone(a._procs["run-1"].poll(), "cancel from a fresh adapter did not reach the process")
        self.assertEqual(self.wait_exit(fresh, "run-1").stop_reason, StopReason.CANCELLED)

    def test_cancel_of_a_finished_turn_under_a_reused_pid_kills_nothing_and_records_nothing(self):
        # Mutation killed: cancel writing `cancelled_at` and killing the manifest pid's tree although the stream
        # already holds the turn's outcome (T5: a reused pid's unrelated tree killed, completed work settled cancelled).
        finished = (
            ("turn.completed", [thread_started(), TURN_STARTED, agent("item_0", "done"), turn_completed()], 0,
             RunState.FINISHED, StopReason.COMPLETED),
            ("turn.failed", [thread_started(), TURN_STARTED, agent("item_0", "building")] + api_failure("Build failed"),
             1, RunState.FAILED, StopReason.TASK_FAILED),
        )
        for name, events, code, state, reason in finished:
            with self.subTest(stream=name):
                shutil.rmtree(self.runs_dir, True)
                a, res, status = self.launch_and_settle(events=events, exit=code)
                self.assertEqual(status.stop_reason, reason)
                victim = self.start_unrelated_process()
                self.point_manifest_at(victim.pid)
                self.assertTrue(pid_alive(victim.pid))       # the finished run's pid now belongs to a live process
                fresh = self.adapter()                         # a later tick: no Popen held
                fresh.cancel("run-1")
                time.sleep(0.3)
                self.assertIsNone(victim.poll(), "cancel killed the process that now holds the finished run's pid")
                self.assertIsNone(self.manifest()["cancelled_at"])
                again = fresh.poll("run-1")
                self.assertEqual((again.state, again.stop_reason), (state, reason))
                self.assertIsNone(victim.poll())

    def test_a_finished_turn_under_a_reused_pid_polls_from_its_stream(self):
        # Mutation killed: `_alive` answering from the pid alone after a restart (a completed run polls as running,
        # and the transfer then cancels it).
        a, res, status = self.launch_and_settle(events=[thread_started(), TURN_STARTED, agent("item_0", "done"),
                                                        turn_completed()])
        victim = self.start_unrelated_process()
        self.point_manifest_at(victim.pid)
        self.assertTrue(pid_alive(victim.pid))
        found = self.adapter().reconcile("run-1", "run-1")
        self.assertEqual((found.status.state, found.status.stop_reason, found.status.consumed_units),
                         (RunState.FINISHED, StopReason.COMPLETED, 1.0))
        self.assertIsNone(victim.poll())

    def test_observe_usage_is_an_unavailable_allowance_in_turns(self):
        # Mutation killed: reporting an allowance figure (none was observed, in exec or the app-server).
        obs = self.adapter().observe_usage()
        self.assertEqual((obs.provider, obs.kind, obs.unit, obs.freshness, obs.value, obs.total),
                         ("codex", UsageKind.SUBSCRIPTION_ALLOWANCE, "turns", UsageFreshness.UNAVAILABLE, None, None))

    def test_capabilities_declare_only_the_observed(self):
        # Mutation killed: declaring turn_limit or usage_observation (neither was observed).
        caps = CodexAdapter.capabilities
        self.assertEqual({n: getattr(caps, n) for n in ("exact_resume", "cancel", "structured_output",
                                                        "usage_observation", "transcript_retention", "turn_limit",
                                                        "funding_route_check")},
                         {"exact_resume": True, "cancel": True, "structured_output": True, "usage_observation": False,
                          "transcript_retention": True, "turn_limit": False, "funding_route_check": True})
        self.assertIn("probe-codex-report-1.json", caps.evidence)
        self.assertIn("0.162.0-alpha.17.2", caps.evidence)


class Resolver(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="ashlar-cxr-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.lad = self.tmp / "LocalAppData"
        self.home = self.tmp / "home" / ".codex"

    def make(self, path, mtime=None, host=False):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"not a real codex\n")
        if host:
            (path.parent / "codex-code-mode-host.exe").write_bytes(b"not a real host\n")
        if mtime is not None:
            os.utime(path, (mtime, mtime))
        return path

    def test_resolver_refuses_a_codex_exe_without_its_host_and_prefers_the_newest_app_binary(self):
        # Mutation killed: picking the newest codex.exe without checking for codex-code-mode-host.exe beside it.
        bins = self.lad / "OpenAI" / "Codex" / "bin"
        self.make(bins / "aaaa" / "codex.exe", mtime=1_000_000, host=True)
        newest_with_host = self.make(bins / "bbbb" / "codex.exe", mtime=2_000_000, host=True)
        self.make(bins / "cccc" / "codex.exe", mtime=3_000_000, host=False)
        sandbox_bin = self.make(self.home / ".sandbox-bin" / "codex.exe", host=False)   # the broken bundled one
        got = resolve_cli(which=lambda name: str(sandbox_bin), local_appdata=str(self.lad), codex_home=str(self.home))
        self.assertEqual(os.path.normcase(got), os.path.normcase(str(newest_with_host)))

    def test_resolver_falls_back_to_the_plugin_appserver_only_with_its_host(self):
        # Mutation killed: dropping the code-mode-host condition on the plugin-appserver fallback.
        plugin = self.make(self.home / "plugins" / ".plugin-appserver" / "codex.exe", host=False)
        none = lambda name: None  # noqa: E731
        self.assertIsNone(resolve_cli(which=none, local_appdata=str(self.lad), codex_home=str(self.home)))
        (plugin.parent / "codex-code-mode-host.exe").write_bytes(b"host\n")
        got = resolve_cli(which=none, local_appdata=str(self.lad), codex_home=str(self.home))
        self.assertEqual(os.path.normcase(got), os.path.normcase(str(plugin)))
        shim = self.make(self.tmp / "npm" / "codex.cmd")   # a launcher shim on PATH is taken as found
        got = resolve_cli(which=lambda name: str(shim), local_appdata=str(self.lad), codex_home=str(self.home))
        self.assertEqual(os.path.normcase(got), os.path.normcase(str(shim)))


class ThroughTheController(ControllerCase):
    """End to end: the controller dispatches a codex task through the real adapter and the fake CLI."""

    def setUp(self):
        super().setUp()
        self.argv_dir = self.tmp / "argv"
        self.argv_dir.mkdir()
        self.script_path = self.tmp / "script.json"
        self.policy_path = self.write_policy(
            mutate=lambda raw: raw["providers"]["codex"].update({"enabled": True, "model": "gpt-6-astra"}))

    def test_a_tick_over_the_codex_adapter_settles_completed_with_the_item_charge(self):
        # Mutation killed: consumed_units None on a completed run (the pool would be charged the full reservation).
        events = ([thread_started(), TURN_STARTED, agent("item_0", "I'll create hello.txt.")] + command("item_1")
                  + [agent("item_2", "done"), turn_completed()])
        self.script_path.write_text(json.dumps({"events": events}), encoding="utf-8")
        env = {k: v for k, v in os.environ.items() if not k.startswith("OPENAI_") and k != "CODEX_HOME"}
        env.update({"FAKE_CODEX_SCRIPT": str(self.script_path), "FAKE_CODEX_ARGV_OUT": str(self.argv_dir) + os.sep})
        adapter = CodexAdapter(str(self.tmp / "runs"), cli=[sys.executable, FAKE_CLI], env=env, clock=self.clock,
                               codex_home=str(self.tmp / "codex-home"))
        self.store = self.init_store()
        self.policy = self.load()
        ctl = Controller(self.store, self.policy, {"codex": adapter}, holder_id="holder-a", host="host-a", pid=4242,
                         clock=self.clock)
        ctl.ensure_task("t1", workstream="budgeted-autonomy", provider="codex", card_number=779, title="PR 5")
        self.assertEqual(ctl.tick().action, "launched")
        deadline = time.time() + 20
        settled = None
        while time.time() < deadline:
            r = ctl.tick()
            if r.action == "settled":
                settled = r
                break
            time.sleep(0.1)
        self.assertIsNotNone(settled, "the run never settled")
        self.assertEqual(settled.settled[0]["stop_reason"], StopReason.COMPLETED)
        self.assertEqual(settled.settled[0]["charged"], 3.0)
        self.assertEqual(self.task()["state"], TaskState.DONE)
        run = self.store.row("SELECT * FROM runs")
        self.assertEqual((run["state"], run["consumed"], run["native_session_id"]), (RunState.FINISHED, 3.0, THREAD))
        self.assertEqual(self.pool()["consumed"], 3.0)
        argv = json.loads(sorted(self.argv_dir.glob("argv-*.json"))[0].read_text(encoding="utf-8"))["argv"]
        self.assertEqual(argv[argv.index("-m") + 1], "gpt-6-astra")
        obs = self.store.row("SELECT * FROM usage_observations WHERE provider='codex'")
        self.assertEqual((obs["kind"], obs["unit"], obs["freshness"]),
                         (UsageKind.SUBSCRIPTION_ALLOWANCE, "turns", UsageFreshness.UNAVAILABLE))


if __name__ == "__main__":
    unittest.main()
