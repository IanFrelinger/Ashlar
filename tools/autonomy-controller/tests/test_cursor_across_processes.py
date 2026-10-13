"""A Cursor run is launched by one `tick` process and settled by a later one. Only the process that started
a child can reap it and read its exit code, and the classification of a Cursor run rests on that exit code
(an exit 0 with output is `completed` before any failure text is looked for). So the CLI runs under
`adapters/_exit_recorder.py`, which writes the exit code to `<run dir>/exit_code` for whichever process
polls next.

These tests launch with one adapter and never poll it there (a launching `tick` returns right after the
launch), let the run exit, and then poll with a fresh `CursorAdapter(runs_dir)`, built as
`cli.build_adapters` builds it. One test launches from a separate Python process that exits before the run
does. No test runs a real `cursor-agent`: the CLI is tests/fake_cursor_cli.py. Every test names, in a
one-line comment, the mutation it kills."""
import dataclasses
import json
import os
import subprocess
import sys
import unittest
from pathlib import Path
from unittest import mock

from ashlar_controller.adapters import cursor as cursor_module
from ashlar_controller.adapters.cursor import CursorAdapter, failure_identity
from ashlar_controller.models import RunState, StopReason

from .test_cursor_adapter import FAKE_CLI, LOGGED_IN, STREAM, CursorAdapterCase, wait_for

SUITE_ROOT = Path(__file__).resolve().parent.parent
# Located here rather than imported, so that this module still loads on an adapter without the recorder.
RECORDER = SUITE_ROOT / "ashlar_controller" / "adapters" / "_exit_recorder.py"
# The words the shared quota, rate-limit and transient texts look for, in a finished run's own output.
WORDY_STDOUT = ('{"type":"assistant","text":"the network test timed out; the rate limit and usage limit docs '
                'say so"}\n')
# Arguments a wrapper could lose on the way: a line break, quotes, cmd.exe and shell metacharacters, a
# backslash before a quote, a trailing backslash after a space, and text outside ASCII and cp1252.
AWKWARD_PROMPT = ('card #779: line one\nline two "quoted" 100% done! a & b | c < d > e ^ f; $HOME `x` '
                  'say \\"hi\\" caf\u00e9 \u2192 \u4e2d C:\\dir with space\\')
# A launching `tick` in miniature: build the adapter as cli.build_adapters does, launch, print the handle
# and exit without polling. The environment (the fake's script and record directory) is the process's own.
LAUNCHER = (
    "import json, sys\n"
    "from ashlar_controller.adapters.cursor import CursorAdapter\n"
    "from ashlar_controller.models import LaunchRequest\n"
    "args = json.loads(sys.argv[1])\n"
    "res = CursorAdapter(args['runs_dir'], cli=args['cli']).launch(LaunchRequest(**args['request']))\n"
    "print(json.dumps({'handle': res.native_run_handle}))\n")


class CursorAcrossProcesses(CursorAdapterCase):
    def launch_and_let_exit(self, **script):
        """Launch with one adapter, never poll it, wait for the run to end (as the OS reaps an orphan
        once the launching `tick` has exited), and return a fresh adapter that holds no process."""
        script.setdefault("status_stdout", LOGGED_IN)
        self.write_script(**script)
        launcher = self.adapter()
        res = launcher.launch(self.request())
        launcher._procs[res.native_run_handle].wait(timeout=20)
        return CursorAdapter(str(self.runs_dir)), res

    def unrelated_process(self):
        """A live process that is not the run: what a reused pid points at."""
        proc = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"], stdin=subprocess.DEVNULL,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

        def stop():
            if proc.poll() is None:
                proc.kill()
            proc.wait(timeout=10)
        self.addCleanup(stop)
        return proc

    def point_manifest_pid_at(self, pid, run_id="run-1"):
        path = self.runs_dir / run_id / "manifest.json"
        manifest = json.loads(path.read_text(encoding="utf-8"))
        manifest["pid"] = pid
        path.write_text(json.dumps(manifest), encoding="utf-8")

    # ----- B1: the exit code outlives the process that launched the run -------------------------------------

    def test_a_clean_exit_polled_by_another_process_is_completed_with_its_exit_code(self):
        # kills: launching the CLI without the exit recorder, or a poll without a Popen not reading the exit file
        fresh, res = self.launch_and_let_exit(print_stdout=STREAM)
        status = fresh.poll(res.native_run_handle)
        self.assertEqual((status.state, status.stop_reason, status.result.get("exit")),
                         (RunState.FINISHED, StopReason.COMPLETED, 0), status.result.get("blocker"))
        self.assertEqual(status.result["event_types"], ["system", "assistant", "result"])
        manifest = self.manifest()
        self.assertEqual(os.path.normcase(manifest["exit_file"]),
                         os.path.normcase(str(self.runs_dir / "run-1" / "exit_code")))
        self.assertEqual(manifest["exit"], 0)          # the poll keeps what it read in the manifest

    def test_a_clean_exit_mentioning_network_rate_limit_and_usage_limit_is_completed_across_processes(self):
        # kills: the failure texts deciding a run whose exit code was lost (the model's words turn it into a retry)
        fresh, res = self.launch_and_let_exit(print_stdout=WORDY_STDOUT)
        status = fresh.poll(res.native_run_handle)
        self.assertEqual((status.state, status.stop_reason, status.result.get("exit")),
                         (RunState.FINISHED, StopReason.COMPLETED, 0), status.result.get("blocker"))

    def test_a_failed_exit_keeps_its_code_across_processes(self):
        # kills: a failed exit losing its code across processes (a diagnosed failure read as no-final-result)
        stderr = "Error: Build FAILED CS0246 the type 'Foo' could not be found\n"
        fresh, res = self.launch_and_let_exit(print_exit=3, print_stderr=stderr)
        status = fresh.poll(res.native_run_handle)
        self.assertEqual((status.state, status.stop_reason, status.result.get("exit")),
                         (RunState.FAILED, StopReason.TASK_FAILED, 3), status.result.get("blocker"))
        self.assertEqual(status.failure_identity, failure_identity(stderr))

    def test_the_exit_code_outlives_the_process_that_launched_the_run(self):
        # kills: an exit code that only the launching process can see (every `tick` is its own process)
        gate = self.tmp / "release-the-run"
        self.write_script(status_stdout=LOGGED_IN, print_stdout=STREAM, gate_file=str(gate))
        self.addCleanup(self._release_and_stop, gate)
        request = dataclasses.asdict(self.request())
        args = {"runs_dir": str(self.runs_dir), "cli": [sys.executable, FAKE_CLI], "request": request}
        done = subprocess.run([sys.executable, "-c", LAUNCHER, json.dumps(args)], cwd=str(SUITE_ROOT), env=self.env(),
                              stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
        self.assertEqual(done.returncode, 0, done.stderr.decode("utf-8", "replace"))
        handle = json.loads(done.stdout.decode("utf-8").strip().splitlines()[-1])["handle"]
        fresh = CursorAdapter(str(self.runs_dir))
        # The launching process is gone and the run is not: it waits for the gate.
        self.assertEqual(fresh.poll(handle).state, RunState.RUNNING)
        gate.write_text("go\n", encoding="utf-8")
        status = self.wait_exit(fresh, handle)
        self.assertEqual((status.state, status.stop_reason, status.result.get("exit")),
                         (RunState.FINISHED, StopReason.COMPLETED, 0), status.result.get("blocker"))

    def _release_and_stop(self, gate):
        """Let a gated run end, and kill it if it does not: a failed test must not leave it behind. Its
        launcher is gone, so nothing in this process holds it (and on POSIX nothing here reaps it)."""
        try:
            gate.write_text("go\n", encoding="utf-8")
        except OSError:
            pass
        run_dir = self.runs_dir / "run-1"
        if not (run_dir / "manifest.json").exists():
            return
        pid = json.loads((run_dir / "manifest.json").read_text(encoding="utf-8")).get("pid")
        ended = lambda: (run_dir / "exit_code").exists() or not cursor_module.pid_alive(pid)  # noqa: E731
        if pid and not wait_for(ended, 10.0):
            cursor_module.kill_tree(pid)

    def test_the_cli_runs_under_the_recorder_with_every_argument_unchanged(self):
        # kills: a recorder that joins, re-splits or drops arguments; a manifest pid that is the CLI's own (no recorder)
        self.write_script(status_stdout=LOGGED_IN, print_stdout=STREAM)
        a = self.adapter()
        res = a.launch(self.request(prompt=AWKWARD_PROMPT))
        status = self.wait_exit(a, res.native_run_handle)
        self.assertEqual(status.stop_reason, StopReason.COMPLETED, status.result.get("blocker"))
        manifest = self.manifest()
        [run] = self.recorded("print")
        self.assertEqual(run["argv"][-1], AWKWARD_PROMPT)
        self.assertEqual(run["argv"], manifest["argv"][2:])   # the manifest keeps the CLI's argv, not the recorder's
        if sys.prefix == sys.base_prefix:
            self.assertEqual(run["ppid"], manifest["pid"])    # the CLI is the recorder's child: one tree to kill
        else:
            # In a venv, sys.executable is a launcher that starts the real interpreter as its child, so the
            # recorder is the CLI's grandparent; it is still not this test process.
            self.assertNotEqual(run["ppid"], os.getpid())
        self.assertNotEqual(run["pid"], manifest["pid"])

    def test_the_recorder_ignores_ctrl_c_while_it_waits_for_the_cli(self):
        # kills: dropping `signal.signal(SIGINT, SIG_IGN)` (a Ctrl+C in the run's console made subprocess.call
        # kill the CLI and the recorder write no exit code; the run then settled as if the recorder had been killed)
        import importlib.util
        import signal
        spec = importlib.util.spec_from_file_location("_exit_recorder_under_test", str(RECORDER))
        recorder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(recorder)
        seen = []
        before = signal.getsignal(signal.SIGINT)
        self.addCleanup(signal.signal, signal.SIGINT, before)
        with mock.patch.object(recorder.subprocess, "call", lambda cmd: seen.append(signal.getsignal(signal.SIGINT)) or 0):
            self.assertEqual(recorder.main([str(self.tmp / "exit_code"), "--", "cli"]), 0)
        self.assertEqual(seen, [signal.SIG_IGN])
        self.assertEqual((self.tmp / "exit_code").read_text(encoding="ascii").strip(), "0")

    def test_the_recorder_mirrors_the_exit_code_and_inherits_the_output(self):
        # kills: a recorder that writes or returns another code, writes it non-atomically, or captures the CLI's output
        exit_file = self.tmp / "exit_code"
        child = "import sys; sys.stdout.write('out-text'); sys.stderr.write('err-text'); sys.exit(5)"
        done = subprocess.run([sys.executable, "-I", str(RECORDER), str(exit_file), "--", sys.executable, "-c", child],
                              stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
        self.assertEqual(done.returncode, 5, done.stderr.decode("utf-8", "replace"))
        self.assertEqual(exit_file.read_text(encoding="ascii").strip(), "5")
        self.assertEqual((done.stdout, done.stderr), (b"out-text", b"err-text"))
        self.assertFalse(Path(str(exit_file) + ".tmp").exists())
        self.assertEqual(os.path.normcase(cursor_module.EXIT_RECORDER), os.path.normcase(str(RECORDER)))

    def test_a_cli_that_cannot_start_is_recorded_as_127(self):
        # kills: a recorder that dies on a failed exec and leaves no exit code (the run would read as no-final-result)
        exit_file = self.tmp / "exit_code"
        missing = str(self.tmp / "no-such-cursor-agent.exe")
        done = subprocess.run([sys.executable, "-I", str(RECORDER), str(exit_file), "--", missing, "-p", "x"],
                              stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
        self.assertEqual(done.returncode, 127, done.stderr.decode("utf-8", "replace"))
        self.assertEqual(exit_file.read_text(encoding="ascii").strip(), "127")
        self.assertIn("no-such-cursor-agent.exe", done.stderr.decode("utf-8", "replace"))

    # ----- B3 and the reused pid: a finished run is never polled as running, and never cancelled -------------

    def test_a_finished_run_whose_pid_now_belongs_to_another_process_polls_finished(self):
        # kills: checking the pid before the exit file (a reused pid makes a finished run poll as running)
        fresh, res = self.launch_and_let_exit(print_stdout=STREAM)
        other = self.unrelated_process()
        self.point_manifest_pid_at(other.pid)
        status = fresh.poll(res.native_run_handle)
        self.assertEqual((status.state, status.stop_reason, status.result.get("exit")),
                         (RunState.FINISHED, StopReason.COMPLETED, 0), status.result.get("blocker"))
        self.assertIsNone(other.poll())

    def test_cancel_of_a_finished_run_records_nothing_and_kills_nothing(self):
        # kills: cancel writing cancelled_at or killing the manifest pid after the run's exit code was recorded
        fresh, res = self.launch_and_let_exit(print_stdout=STREAM)
        other = self.unrelated_process()
        self.point_manifest_pid_at(other.pid)
        killed = []
        real_kill_tree = cursor_module.kill_tree

        def spy(pid):
            killed.append(pid)
            real_kill_tree(pid)

        with mock.patch.object(cursor_module, "kill_tree", spy):
            fresh.cancel(res.native_run_handle)
        self.assertEqual(killed, [], "cancel killed the tree of a pid the finished run no longer owns")
        self.assertIsNone(other.poll(), "the unrelated process was killed")
        self.assertIsNone(self.manifest()["cancelled_at"])
        status = fresh.poll(res.native_run_handle)
        self.assertEqual((status.state, status.stop_reason), (RunState.FINISHED, StopReason.COMPLETED))


if __name__ == "__main__":
    unittest.main()
