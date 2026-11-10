#!/usr/bin/env python3
"""Run hermetic CI reviewer/regression fixtures; discovered by run-repo-gates.sh."""
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
raise SystemExit(subprocess.call([sys.executable, "-m", "unittest", "discover", "-s", str(root / "tools/ci-agents/tests"), "-v"], cwd=root))
