"""Run a provider CLI and record its exit code in a file, so that a run started by one controller process can
be classified by another. Only the process that started a child can reap it and read its exit code, and
every `tick` is its own process: the tick that launches a run returns at once, and a later tick settles it.

    python -I _exit_recorder.py <exit file> -- <argv...>

The CLI inherits stdin, stdout, stderr, the working directory and the environment unchanged. When it exits,
its exit code is written to `<exit file>` atomically (`<exit file>.tmp`, then a rename), and the recorder
exits with the same code. A CLI that cannot be started is recorded as 127, with the reason on stderr.

A recorder that is killed writes nothing. `cancel` kills the whole tree (the recorder and the CLI) after it
has recorded `cancelled_at`, which already says what happened. Standard library only, Python 3.8+: it runs
under `-I`, so no environment variable and no user site directory changes what it does."""
import os
import signal
import subprocess
import sys

EXEC_FAILED = 127   # what a POSIX shell reports for a command it could not run


def record(path: str, code: int) -> None:
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="ascii", newline="\n") as f:
        f.write(f"{code}\n")
    os.replace(tmp, path)   # a poll never reads half of it


def main(argv: list) -> int:
    if len(argv) < 3 or argv[1] != "--":
        sys.stderr.write("usage: _exit_recorder.py <exit file> -- <argv...>\n")
        return 64
    path, cmd = argv[0], argv[2:]
    # A Ctrl+C in the console the run is attached to reaches the CLI as well. The recorder ignores it and keeps
    # waiting, so the CLI's own exit code, whatever the interrupt made it, is still recorded; without this,
    # subprocess.call would kill the CLI on KeyboardInterrupt and write nothing. The CLI's handling is unchanged.
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    try:
        code = subprocess.call(cmd)   # the CLI inherits the run's stdin (closed), stdout and stderr files
    except OSError as e:
        sys.stderr.write(f"_exit_recorder: {cmd[0]!r} did not start: {e}\n")
        sys.stderr.flush()
        code = EXEC_FAILED
    record(path, code)
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
