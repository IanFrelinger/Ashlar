#!/usr/bin/env bash
# Tests for scripts/ci/verify-required-runner-pins.py.
#
# WHY A TEST AND NOT JUST THE CHECK. A scanner that stops matching is indistinguishable from a
# repository with nothing to find: both print a clean line and exit 0. So each case below builds a
# throwaway tree holding the five required workflows, all pinned, and changes ONE thing the check must
# refuse - and a refusal counts only when it names its reason and the line, so a python traceback
# (also exit 1) can never pass for one. The first case proves the untouched fixture is accepted, so a
# refusal below is caused by the change and not by the fixture.
#
# The check has a strict mode (floor, required OS families) that only runs bare. To reach it on a
# fixture, a copy of the check is placed inside the fixture tree and run bare there: it locates the
# repository from its own path.
#
# Run:  bash tests/scripts/required-runner-pins.test.sh
# Pure bash + python (PyYAML): no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CHECK="${ROOT}/scripts/ci/verify-required-runner-pins.py"

# PROBE the interpreter, do not merely locate it. On Windows `command -v python3` finds an App
# Execution Alias stub that exists, is executable, and exits 49 - indistinguishable from a refusal to
# any assertion that only checks a non-zero exit.
PY_BIN=""
for candidate in python3 python py; do
  if command -v "${candidate}" >/dev/null 2>&1 && "${candidate}" -c 'import yaml' >/dev/null 2>&1; then
    PY_BIN="${candidate}"
    break
  fi
done
if [[ -z "${PY_BIN}" ]]; then
  echo "FAIL - no working python with PyYAML on PATH; this file cannot test anything and will not pretend to."
  exit 1
fi
if [[ ! -f "${CHECK}" ]]; then
  echo "FAIL - ${CHECK} does not exist; nothing here can be tested."
  exit 1
fi

# Bump when you add an assertion; the check at the bottom says why.
EXPECTED_ASSERTIONS=50

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; echo "         $2"; }

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

W=".github/workflows"

# A fresh tree with the five required workflows, every job pinned: 12 judged labels, like the real
# tree. Echoes its path. Called as $(tree), a subshell, so mktemp makes each tree its own.
tree() {
  local d
  d="$(mktemp -d "${TMP}/t.XXXXXX")"
  mkdir -p "${d}/${W}"
  cat > "${d}/${W}/cert-gate.yml" <<'YML'
name: Cert gate
on:
  pull_request:
jobs:
  cert-gate:
    name: cert-gate
    runs-on: ubuntu-24.04
    steps:
      - run: echo cert
YML
  cat > "${d}/${W}/build-gate.yml" <<'YML'
name: Build Gate
on:
  pull_request:
jobs:
  build-core:
    name: build-core
    runs-on: ubuntu-24.04
    steps:
      - run: echo build
YML
  cat > "${d}/${W}/shell-lint.yml" <<'YML'
name: Shell lint
on:
  pull_request:
jobs:
  shell-lint:
    name: shell-lint
    runs-on: ubuntu-24.04
    steps:
      - run: echo lint
YML
  cat > "${d}/${W}/docs-link-check.yml" <<'YML'
name: Docs Link Check
on:
  pull_request:
jobs:
  lychee:
    name: lychee (README + docs)
    runs-on: ubuntu-24.04
    steps:
      - run: echo lychee
YML
  cat > "${d}/${W}/full-platform-readiness-gate.yml" <<'YML'
name: Full Platform Readiness Gate
on:
  pull_request:
jobs:
  changes:
    name: "Changes"
    runs-on: ubuntu-24.04
    steps:
      - run: echo changes
  native-platform:
    name: "${{ matrix.label }} lane"
    needs: changes
    runs-on: ${{ matrix.os }}
    strategy:
      matrix:
        include:
          - os: ubuntu-24.04
            label: Linux
          - os: macos-26
            label: macOS
          - os: windows-2025-vs2026
            label: Windows
    steps:
      - run: echo native
  container-platform:
    runs-on: ubuntu-24.04
    steps:
      - run: echo container
  docker-cli-image:
    runs-on: ubuntu-24.04
    steps:
      - run: echo cli
  docker-all-images:
    runs-on: ubuntu-24.04
    steps:
      - run: echo images
  readiness-summary:
    name: "Readiness summary"
    runs-on: ubuntu-24.04
    needs: [changes, native-platform]
    steps:
      - run: echo summary
YML
  printf '%s' "${d}"
}

# Replace the first line matching $2 in fixture file $1 with $3 (literal strings, no regex), and FAIL
# the case loudly if nothing matched: a mutation that did not apply would make the case vacuous.
swap() {
  "${PY_BIN}" - "$1" "$2" "$3" <<'PY' || { echo "swap: '$2' not found in $1" >&2; return 1; }
import sys
path, old, new = sys.argv[1], sys.argv[2], sys.argv[3]
text = open(path, encoding="utf-8").read()
if old not in text:
    sys.exit(1)
open(path, "w", encoding="utf-8", newline="\n").write(text.replace(old, new, 1))
PY
}

# Echoes "<exit>|<output>" of the check in fixture mode.
check() {
  local out code
  out="$("${PY_BIN}" "${CHECK}" --root "$1" 2>&1)"; code=$?
  printf '%s|%s' "${code}" "${out}"
}

# Echoes "<exit>|<output>" of a copy of the check run BARE inside fixture $1 (strict mode).
check_strict() {
  local out code
  mkdir -p "$1/scripts/ci"
  cp "${CHECK}" "$1/scripts/ci/verify-required-runner-pins.py"
  out="$("${PY_BIN}" "$1/scripts/ci/verify-required-runner-pins.py" 2>&1)"; code=$?
  printf '%s|%s' "${code}" "${out}"
}

expect() {  # label want_rc needle got
  local label="$1" want="$2" needle="$3" got="$4"
  local code="${got%%|*}" out="${got#*|}"
  if [[ "${code}" == "${want}" ]] && grep -qF -- "${needle}" <<<"${out}"; then
    ok "${label}"
  else
    bad "${label}" "wanted exit ${want} and '${needle}'; got exit ${code}: ${out}"
  fi
}

echo "== the fixture itself is clean, so every refusal below is caused by its one change =="
expect "the untouched five-workflow fixture is accepted" 0 \
  "5 workflow(s), 10 job(s), 12 runner label(s): 12 pinned, 0 excepted, 0 refused; 0 problem(s)" \
  "$(check "$(tree)")"
expect "and accepted in strict mode too (floor and OS families met)" 0 "0 problem(s)" \
  "$(check_strict "$(tree)")"

echo "== a floating label is refused wherever a required workflow's job names it =="
d="$(tree)"; swap "${d}/${W}/cert-gate.yml" "runs-on: ubuntu-24.04" "runs-on: ubuntu-latest"
expect "ubuntu-latest on the required job itself is refused, with its line" 1 \
  "${W}/cert-gate.yml:7: job 'cert-gate' runs on 'ubuntu-latest', which is a floating -latest label" \
  "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "  changes:
    name: \"Changes\"
    runs-on: ubuntu-24.04" "  changes:
    name: \"Changes\"
    runs-on: ubuntu-latest"
expect "ubuntu-latest on a job that only FEEDS the required one (readiness 'changes') is refused" 1 \
  "${W}/full-platform-readiness-gate.yml:7: job 'changes' runs on 'ubuntu-latest'" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "- os: macos-26" "- os: macos-latest"
expect "macos-latest reached through runs-on: \${{ matrix.os }} is refused at the matrix value's line" 1 \
  "${W}/full-platform-readiness-gate.yml:19: job 'native-platform' runs on 'macos-latest'" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "- os: windows-2025-vs2026" "- os: windows-latest"
expect "windows-latest in the matrix is refused" 1 \
  "job 'native-platform' runs on 'windows-latest', which is a floating -latest label" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "    strategy:
      matrix:
        include:
          - os: ubuntu-24.04
            label: Linux" "    strategy:
      matrix:
        os: [ubuntu-24.04, ubuntu-latest]
        include:
          - os: ubuntu-24.04
            label: Linux"
expect "a floating label in a matrix-level list (not include:) is refused" 1 \
  "${W}/full-platform-readiness-gate.yml:16: job 'native-platform' runs on 'ubuntu-latest'" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/build-gate.yml" "runs-on: ubuntu-24.04" "runs-on: Ubuntu-Latest"
expect "a -latest label in another case is refused (runner labels are case-insensitive)" 1 \
  "job 'build-core' runs on 'Ubuntu-Latest', which is a floating -latest label" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/shell-lint.yml" "runs-on: ubuntu-24.04" "runs-on: macos-latest-xlarge"
expect "a -latest larger-runner label (macos-latest-xlarge) is refused" 1 \
  "runs on 'macos-latest-xlarge', which is a floating -latest label" "$(check "${d}")"

echo "== an unpinned label that does not say latest is refused too =="
d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "- os: windows-2025-vs2026" "- os: windows-2025"
expect "windows-2025 (OS only; it moved from the VS2022 to the VS2026 image) is refused" 1 \
  "runs on 'windows-2025', which names the Windows Server release but not the Visual Studio image" \
  "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/docs-link-check.yml" "runs-on: ubuntu-24.04" "runs-on: self-hosted"
expect "an unknown label (self-hosted) is refused: unknown is not pinned" 1 \
  "runs on 'self-hosted', which is not a label this check knows to be pinned" "$(check "${d}")"

# A pinned shape must match the WHOLE label. These two start like one and are not it: a larger-runner
# name, and the image name a "Set up job" step prints (`Image: macos-26-arm64`), which is no label.
d="$(tree)"; swap "${d}/${W}/cert-gate.yml" "runs-on: ubuntu-24.04" "runs-on: ubuntu-24.04-16core"
expect "a label that only starts like a pinned one (ubuntu-24.04-16core) is refused" 1 \
  "${W}/cert-gate.yml:7: job 'cert-gate' runs on 'ubuntu-24.04-16core', which is not a label this check knows to be pinned" \
  "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "- os: macos-26" "- os: macos-26-arm64"
expect "the image name macos-26-arm64 used as a matrix os is refused (it is not a runner label)" 1 \
  "${W}/full-platform-readiness-gate.yml:19: job 'native-platform' runs on 'macos-26-arm64', which is not a label this check knows to be pinned" \
  "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/docs-link-check.yml" "runs-on: ubuntu-24.04" "runs-on: [self-hosted, linux]"
expect "a label list is judged label by label" 1 \
  "runs on 'linux', which is not a label this check knows to be pinned" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/cert-gate.yml" "runs-on: ubuntu-24.04" "runs-on: \${{ inputs.runner }}"
expect "an expression it cannot evaluate is refused, not skipped" 1 \
  "runs on '\${{ inputs.runner }}', which is an expression this check cannot evaluate statically" \
  "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/cert-gate.yml" "runs-on: ubuntu-24.04" "runs-on: \${{ matrix.os }}"
expect "a matrix reference on a job with no matrix is refused" 1 \
  "(the job has no strategy.matrix)" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "runs-on: \${{ matrix.os }}" "runs-on: \${{ matrix.runner }}"
expect "a matrix key the matrix does not define is refused" 1 \
  "(the matrix defines no 'runner')" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "    strategy:
      matrix:
        include:" "    strategy:
      matrix: \${{ fromJSON(needs.changes.outputs.m) }}
      unused:
        include:"
expect "a matrix built from an expression is refused" 1 \
  "(strategy.matrix is an expression)" "$(check "${d}")"

# The matrix-level os values are all pinned, so only the include: expression can make this red: it
# may add any runner at run time.
d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "        include:
          - os: ubuntu-24.04
            label: Linux
          - os: macos-26
            label: macOS
          - os: windows-2025-vs2026
            label: Windows
" "        os: [ubuntu-24.04, macos-26, windows-2025-vs2026]
        include: \${{ fromJSON(needs.changes.outputs.extra) }}
"
expect "an include: built from an expression, beside a pinned matrix-level os, is refused" 1 \
  "${W}/full-platform-readiness-gate.yml:17: job 'native-platform' runs on '\${{ ... }} (strategy.matrix.include is an expression)'" \
  "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/build-gate.yml" "    runs-on: ubuntu-24.04
    steps:
      - run: echo build" "    uses: ./.github/workflows/reusable-build.yml"
expect "a job that calls a reusable workflow (runner not visible) is refused" 1 \
  "runs on '<calls a reusable workflow; its runner is not visible here>'" "$(check "${d}")"

echo "== every runs-on shape is judged, none is skipped =="
d="$(tree)"; swap "${d}/${W}/build-gate.yml" "    runs-on: ubuntu-24.04" "    runs-on:
      group: big-runners
      labels: [ubuntu-latest]"
expect "a runner-group mapping is judged by its labels: (ubuntu-latest there is refused)" 1 \
  "${W}/build-gate.yml:9: job 'build-core' runs on 'ubuntu-latest', which is a floating -latest label" \
  "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/build-gate.yml" "    runs-on: ubuntu-24.04" "    runs-on:
      group: big-runners"
expect "a runner-group mapping with no labels: is refused (the group decides the image)" 1 \
  "job 'build-core' runs on '<runner group without labels>'" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/docs-link-check.yml" "runs-on: ubuntu-24.04" "runs-on: []"
expect "an empty runner list is refused" 1 \
  "${W}/docs-link-check.yml:7: job 'lychee' runs on '<empty runner list>'" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/shell-lint.yml" "    runs-on: ubuntu-24.04
" ""
expect "a job with neither runs-on nor uses is refused at its key" 1 \
  "${W}/shell-lint.yml:5: job 'shell-lint' runs on '<no runs-on>'" "$(check "${d}")"

echo "== the only exception is a reasoned comment on the value's own line =="
d="$(tree)"; swap "${d}/${W}/cert-gate.yml" "runs-on: ubuntu-24.04" \
  "runs-on: ubuntu-latest  # runner-pin: allow trialling the next image on this branch only"
expect "a -latest label with a reasoned exception on its line is accepted and counted as excepted" 0 \
  "12 runner label(s): 11 pinned, 1 excepted, 0 refused; 0 problem(s)" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "- os: macos-26" \
  "- os: macos-latest  # runner-pin: allow the macOS lane tracks the newest image by decision"
expect "an exception on a matrix value's own line is accepted" 0 "1 excepted, 0 refused; 0 problem(s)" \
  "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/cert-gate.yml" "runs-on: ubuntu-24.04" "runs-on: ubuntu-latest  # runner-pin: allow"
expect "an exception with no reason is refused" 1 \
  "its \`# runner-pin: allow\` comment gives no reason" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/cert-gate.yml" "runs-on: ubuntu-24.04" \
  "runs-on: ubuntu-24.04  # runner-pin: allow left over from before the pin"
expect "an exception on a label that is already pinned is refused as stale" 1 \
  "${W}/cert-gate.yml:7: job 'cert-gate': stale exception - 'ubuntu-24.04' is already pinned" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "- os: macos-26" "- os: macos-latest"
swap "${d}/${W}/full-platform-readiness-gate.yml" "runs-on: \${{ matrix.os }}" \
  "runs-on: \${{ matrix.os }}  # runner-pin: allow macOS floats"
got="$(check "${d}")"
expect "an exception on the runs-on line, not the matrix value's, is stray" 1 \
  "${W}/full-platform-readiness-gate.yml:13: stray \`# runner-pin: allow\` comment" "${got}"
expect "... and does not excuse the matrix value it was meant for" 1 \
  "job 'native-platform' runs on 'macos-latest'" "${got}"

d="$(tree)"; swap "${d}/${W}/shell-lint.yml" "    steps:" "    # runner-pin: allow nothing in particular
    steps:"
expect "a runner-pin comment on a line that holds no label is refused as stray" 1 \
  "${W}/shell-lint.yml:8: stray \`# runner-pin: allow\` comment" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/cert-gate.yml" "runs-on: ubuntu-24.04" "runs-on: ubuntu-latest  # runner-pin: skip"
expect "a runner-pin comment in any other form is refused as malformed" 1 \
  "${W}/cert-gate.yml:7: malformed runner-pin comment" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "    strategy:
      matrix:
        include:" "    strategy:
      matrix:
        os: [ubuntu-24.04, macos-latest]  # runner-pin: allow the macOS entry floats on purpose
        include:"
expect "one exception on a flow list covers the line; its pinned neighbour does not make it stale" 0 \
  "1 excepted, 0 refused; 0 problem(s)" "$(check "${d}")"

# It must be a COMMENT. The same text inside a quoted value on the label's own line is data.
d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "          - os: macos-26
            label: macOS" "          - { os: macos-latest, label: \"macOS # runner-pin: allow not a comment at all\" }"
got="$(check "${d}")"
expect "exception text inside a quoted value on the label's line does not excuse it" 1 \
  "${W}/full-platform-readiness-gate.yml:19: job 'native-platform' runs on 'macos-latest', which is a floating -latest label" \
  "${got}"
expect "... and is refused as runner-pin text that is not a comment" 1 \
  "${W}/full-platform-readiness-gate.yml:19: \`runner-pin:\` text inside a YAML value" "${got}"

# Control for the case above: a '#' inside an earlier quoted value does not hide the real comment.
d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "          - os: macos-26
            label: macOS" "          - { os: macos-latest, label: \"mac # os\" }  # runner-pin: allow the macOS lane tracks the newest image by decision"
expect "a real exception comment after a quoted value that holds '#' is still honoured" 0 \
  "1 excepted, 0 refused; 0 problem(s)" "$(check "${d}")"

# A block scalar's content is a value (a shell comment is not a YAML one); its header line is not.
d="$(tree)"; swap "${d}/${W}/shell-lint.yml" "      - run: echo lint" "      - run: |
          echo lint  # runner-pin: allow a shell comment in a run: script"
expect "runner-pin text inside a run: | script is refused as not a comment" 1 \
  "${W}/shell-lint.yml:10: \`runner-pin:\` text inside a YAML value" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/shell-lint.yml" "      - run: echo lint" "      - run: |  # runner-pin: allow on a block header
          echo lint"
expect "a runner-pin comment on a block scalar's header line is a real comment, refused as stray" 1 \
  "${W}/shell-lint.yml:9: stray \`# runner-pin: allow\` comment" "$(check "${d}")"

echo "== scope: the five required workflows, and only those =="
d="$(tree)"; cat > "${d}/${W}/some-advisory-gate.yml" <<'YML'
name: Advisory
on:
  pull_request:
jobs:
  advisory:
    runs-on: ubuntu-latest
    steps:
      - run: echo advisory
YML
expect "a -latest label in a workflow outside the five is not this check's business" 0 \
  "5 workflow(s), 10 job(s), 12 runner label(s)" "$(check "${d}")"

d="$(tree)"; rm "${d}/${W}/shell-lint.yml"
expect "a required workflow that is missing is refused, not skipped" 1 \
  "${W}/shell-lint.yml: missing. It carries the required check 'shell-lint'" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" 'name: "Readiness summary"' 'name: "Readiness roll-up"'
expect "a required workflow whose required job was renamed is refused" 1 \
  "no job is named 'Readiness summary'" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/docs-link-check.yml" "jobs:" "jobs: ["
expect "a required workflow that does not parse is refused" 1 \
  "${W}/docs-link-check.yml: does not parse as YAML" "$(check "${d}")"

d="$(tree)"; swap "${d}/${W}/cert-gate.yml" "jobs:
  cert-gate:" "jobs: {}
unused:
  cert-gate:"
expect "a required workflow with no jobs is refused" 1 \
  "${W}/cert-gate.yml: no jobs found" "$(check "${d}")"

EMPTY="$(mktemp -d "${TMP}/empty.XXXXXX")"
got="$(check "${EMPTY}")"
expect "a tree with no workflows at all refuses instead of reporting clean" 1 "0 workflow(s), 0 job(s)" "${got}"
n_missing="$(grep -c ": missing. It carries the required check" <<<"${got#*|}")"
if [[ "${got%%|*}" == "1" && "${n_missing}" == "5" ]]; then
  ok "... and names every missing required workflow (all five)"
else
  bad "... and names every missing required workflow (all five)" "named ${n_missing}: ${got}"
fi

echo "== strict mode: the floor and the OS families =="
d="$(tree)"; swap "${d}/${W}/full-platform-readiness-gate.yml" "          - os: macos-26
            label: macOS
          - os: windows-2025-vs2026
            label: Windows
" ""
got="$(check_strict "${d}")"
expect "strict: losing the macOS and Windows lanes from the scan is refused (macos)" 1 \
  "no macos runner was judged" "${got}"
expect "strict: ... (windows)" 1 "no windows runner was judged" "${got}"

# Drop four single-label readiness jobs: 12 - 4 = 8 judged labels, under the floor of 10.
d="$(tree)"; "${PY_BIN}" - "${d}/${W}/full-platform-readiness-gate.yml" <<'PY'
import re, sys
p = sys.argv[1]
t = open(p, encoding="utf-8").read()
for job in ("container-platform", "docker-cli-image", "docker-all-images", "changes"):
    t, n = re.subn(r"\n  " + re.escape(job) + r":\n(?:    .*\n|      .*\n)+", "\n", t, count=1)
    assert n == 1, job
open(p, "w", encoding="utf-8", newline="\n").write(t)
PY
expect "strict: fewer judged labels than the floor is refused" 1 \
  "judged 8 runner label(s), expected at least 10" "$(check_strict "${d}")"

echo "== the real tree =="
# Bare, as run-repo-gates runs it: the floor and the OS families are live here and nowhere else. The
# listing is the non-vacuity control - it must show a judged label from EACH of the five files and
# from the macOS and Windows lanes, or a clean summary line means the scan reached less than it claims.
got="$("${PY_BIN}" "${CHECK}" 2>&1)"; code=$?
if [[ "${code}" == "0" ]] && grep -q "0 problem(s)" <<<"${got}"; then
  ok "every required job in this repository runs on a pinned label (${got##*: })"
else
  bad "every required job in this repository runs on a pinned label" "exit ${code}: ${got}"
fi
missing=""
for f in cert-gate.yml build-gate.yml shell-lint.yml docs-link-check.yml full-platform-readiness-gate.yml; do
  grep -q "^required-runner-pins: ${W}/${f}:[0-9]* " <<<"${got}" || missing+=" ${f}"
done
grep -q " -> macos-[^ ]* \[" <<<"${got}" || missing+=" <a macOS lane>"
grep -q " -> windows-[^ ]* \[" <<<"${got}" || missing+=" <a Windows lane>"
if [[ -z "${missing}" ]]; then
  ok "the real-tree scan judged a label in all five required workflows and in the macOS and Windows lanes"
else
  bad "the real-tree scan reached everything it must" "not reached:${missing}; output: ${got}"
fi

echo
echo "passed: ${PASS}   failed: ${FAIL}"

RAN=$((PASS + FAIL))
if [[ "${RAN}" -ne "${EXPECTED_ASSERTIONS}" ]]; then
  echo "FAIL - ran ${RAN} assertions, expected ${EXPECTED_ASSERTIONS}."
  echo "       Either this file stopped early or assertions were added without bumping"
  echo "       EXPECTED_ASSERTIONS. A partial run is not a pass."
  exit 1
fi

[[ "${FAIL}" -eq 0 ]] || exit 1
