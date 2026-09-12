#!/usr/bin/env bash
# Refuse workflow files that reference a context GitHub does not provide where they use it.
#
# Why this exists
# ---------------
# `${{ runner.temp }}` in a JOB-level `env:` block is not an error you can see. GitHub rejects the
# whole workflow file at dispatch time: the run is created, it is marked failed, it has ZERO jobs,
# and its name in the API is the file path rather than the workflow's own `name:`. No check run is
# posted to the pull request, so `gh pr checks` does not list it and a PR looks clean. An advisory,
# path-filtered gate in that state is invisible twice over — and one in this repository sat like
# that from the day it was written, with three CHANGELOG entries describing what it executed.
#
# The context availability rules (GitHub docs, "Contexts" → availability):
#   jobs.<id>.env            github, needs, strategy, matrix, vars, secrets, inputs
#   jobs.<id>.if             the same set
#   jobs.<id>.steps.*.env    ...plus runner, env, job, steps, secrets
#   jobs.<id>.outputs        evaluated after the steps run, so `steps.*` is legal there
# So `runner`, `steps`, `job` and `env` are usable from a STEP and nowhere above it, with job
# outputs as the documented exception.
#
# This checks exactly that: no `runner.`, `steps.`, `job.` or `env.` reference anywhere in a job
# except inside `steps:`, and none in the workflow-level `env:` either. It is deliberately narrow —
# it encodes the rule that has actually silenced a gate here, not every rule actionlint knows.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

# python3 on a developer's Windows/Git-Bash box is often the Microsoft Store stub; `python` is the
# real one there. CI is always python3.
PY_BIN="python3"
if ! "${PY_BIN}" -c "import sys" >/dev/null 2>&1; then
  PY_BIN="python"
fi

"${PY_BIN}" - <<'PY'
import glob
import re
import sys

try:
    import yaml
except ImportError:  # pragma: no cover - CI images carry PyYAML
    print("verify-workflow-context-availability: PyYAML is not installed (pip install pyyaml)", file=sys.stderr)
    raise SystemExit(2)

# Usable from a step and nowhere above it.
STEP_ONLY = re.compile(r"\$\{\{[^}]*?\b(runner|steps|job|env)\.")

failures = []


def strings(node, path):
    """Every string in the tree, with the key path that reached it."""
    if isinstance(node, str):
        yield path, node
    elif isinstance(node, dict):
        for key, value in node.items():
            yield from strings(value, f"{path}.{key}")
    elif isinstance(node, list):
        for index, value in enumerate(node):
            yield from strings(value, f"{path}[{index}]")


def check(node, path):
    for where, text in strings(node, path):
        match = STEP_ONLY.search(text)
        if match:
            failures.append((where, match.group(1), text.strip()))


files = sorted(glob.glob(".github/workflows/*.yml") + glob.glob(".github/workflows/*.yaml"))
if not files:
    print("verify-workflow-context-availability: no workflow files found", file=sys.stderr)
    raise SystemExit(2)

for path in files:
    with open(path, encoding="utf-8") as handle:
        try:
            doc = yaml.safe_load(handle)
        except yaml.YAMLError as error:
            failures.append((path, "yaml", str(error).splitlines()[0]))
            continue

    if not isinstance(doc, dict):
        continue

    check(doc.get("env"), f"{path}:env")

    jobs = doc.get("jobs")
    if not isinstance(jobs, dict):
        continue

    for job_id, job in jobs.items():
        if not isinstance(job, dict):
            continue
        # Everything the job declares EXCEPT its steps and its outputs. A step may use all four,
        # and `jobs.<id>.outputs` is evaluated AFTER the steps run, so reading `steps.<id>.outputs`
        # there is both legal and the only way job outputs are ever written.
        above_steps = {k: v for k, v in job.items() if k not in ("steps", "outputs")}
        check(above_steps, f"{path}:jobs.{job_id}")

print(f"verify-workflow-context-availability: scanned {len(files)} workflow files")

if failures:
    print("")
    for where, context, text in failures:
        print(f"::error::{where}: '{context}' is not available here — it is usable from a step and nowhere above one.")
        print(f"    {text}")
    print("")
    print("A workflow file GitHub cannot validate produces a run with ZERO jobs and posts no check")
    print("to the pull request, so this does not surface anywhere else. Move the value into a step:")
    print("")
    print("    - name: Probe workdir")
    print("      shell: bash")
    print('      run: echo "MY_WORKDIR=${RUNNER_TEMP}/my-probe" >> "$GITHUB_ENV"')
    print("")
    raise SystemExit(1)

print("verify-workflow-context-availability: PASS")
PY
