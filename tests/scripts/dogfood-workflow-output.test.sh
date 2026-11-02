#!/usr/bin/env bash
# Exercise the workflow's actual Bash blocks with hostile model/compiler text as step outputs.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
python3 - "${ROOT}" <<'PY'
import os
from pathlib import Path
import subprocess
import sys
import tempfile

workflow = (Path(sys.argv[1]) / ".github/workflows/dogfood-continuous-proof.yml").read_text()

def run_block(name):
    section = workflow.split("      - name: " + name + "\n", 1)[1].split("      - name:", 1)[0]
    for field in ("sweep_outcome", "sweep_gap", "sweep_evidence"):
        assert "${{ steps.sweep.outputs." + field in section.split("        run: |", 1)[0], "missing output environment binding"
    lines = section.split("        run: |\n", 1)[1].splitlines()
    assert all(not line or line.startswith("          ") for line in lines), "unexpected workflow block indentation"
    script = "\n".join(line[10:] for line in lines)
    assert "${{ steps.sweep.outputs." not in script, "untrusted output interpolated into shell source"
    return script

with tempfile.TemporaryDirectory(prefix="ashlar-workflow-output-") as directory:
    root = Path(directory)
    marker = root / "executed"
    # Both quote breakout and command substitution would execute if the runner inserted this
    # value into a run block. Environment expansion must preserve it as one argument instead.
    hostile = f'"; touch {marker}; # $(touch {marker}) `touch {marker}` | untrusted text'
    env = dict(os.environ, SWEEP_OUTCOME="GAP", SWEEP_GAP=hostile, SWEEP_EVIDENCE=hostile,
               GITHUB_RUN_ID="123", GITHUB_REPOSITORY="o/r", GITHUB_STEP_SUMMARY=str(root / "summary"))
    scripts = root / "scripts"
    scripts.mkdir()
    args = root / "args"
    (scripts / "dogfood-continuous-proof.sh").write_text('printf "%s\\0" "$@" > "$CAPTURE_ARGS"\n')
    env["CAPTURE_ARGS"] = str(args)
    subprocess.run(["bash", "-euo", "pipefail", "-c", run_block("Append ledger row")], cwd=root, env=env, check=True)
    captured = args.read_bytes().split(b"\0")[:-1]
    assert len(captured) == 7 and captured[4].decode() == hostile and captured[6].decode().endswith(hostile)
    assert not marker.exists(), "append-ledger step executed output text"
    subprocess.run(["bash", "-euo", "pipefail", "-c", run_block("Workflow summary")], cwd=root, env=env, check=True)
    assert hostile in (root / "summary").read_text() and not marker.exists(), "summary executed output text"
print("PASS: actual ledger and summary workflow blocks preserve hostile outputs as data")
PY
