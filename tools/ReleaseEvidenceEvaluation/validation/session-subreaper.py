#!/usr/bin/env python3
"""Session-only Linux test wrapper; reap orphaned children without changing test/product code."""
import ctypes
import os
import subprocess
import sys

if len(sys.argv) < 2:
    raise SystemExit('usage: run-with-subreaper.py COMMAND [ARG ...]')
libc = ctypes.CDLL(None, use_errno=True)
if libc.prctl(36, 1, 0, 0, 0) != 0:  # PR_SET_CHILD_SUBREAPER
    raise OSError(ctypes.get_errno(), 'subreaper setup failed')
process = subprocess.Popen(sys.argv[1:])
while True:
    pid, status = os.waitpid(-1, 0)
    if pid == process.pid:
        raise SystemExit(os.waitstatus_to_exitcode(status))
    print(f'subreaper: reaped adopted child {pid}, exit {os.waitstatus_to_exitcode(status)}',
          file=sys.stderr, flush=True)
