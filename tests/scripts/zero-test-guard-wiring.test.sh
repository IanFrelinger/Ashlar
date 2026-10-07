#!/usr/bin/env bash
# Tests for scripts/ci/verify-zero-test-guard-wiring.py.
#
# WHY A TEST AND NOT JUST THE CHECK. A scanner that stops matching is indistinguishable from a
# repository with nothing to find: both print a clean line and exit 0. So each case below builds a
# throwaway tree containing one shape it MUST refuse - and a refusal counts only when it names its
# reason, so a python traceback (also exit 1) can never pass for one. Only after those does the clean
# result on the real tree, the last case, mean anything.
#
# Every script fixture that is meant to be ACCEPTED starts with `set -euo pipefail`, as every gate
# script does: without it a failing guard does not stop the script, and the check refuses that too.
#
# Run:  bash tests/scripts/zero-test-guard-wiring.test.sh
# Pure bash + python: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CHECK="${ROOT}/scripts/ci/verify-zero-test-guard-wiring.py"

# PROBE the interpreter, do not merely locate it. On Windows `command -v python3` finds an App
# Execution Alias stub that exists, is executable, and exits 49 - indistinguishable from a refusal to
# any assertion that only checks a non-zero exit.
PY_BIN=""
for candidate in python3 python py; do
  if command -v "${candidate}" >/dev/null 2>&1 && "${candidate}" -c 'print(1)' >/dev/null 2>&1; then
    PY_BIN="${candidate}"
    break
  fi
done
if [[ -z "${PY_BIN}" ]]; then
  echo "FAIL - no working python on PATH; this file cannot test anything and will not pretend to."
  exit 1
fi
if [[ ! -f "${CHECK}" ]]; then
  echo "FAIL - ${CHECK} does not exist; nothing here can be tested."
  exit 1
fi

# Bump when you add an assertion; the check at the bottom says why.
EXPECTED_ASSERTIONS=46

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; echo "         $2"; }

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

# A fresh tree with a single-target and a multi-target project in it. Echoes its path. It is called
# as $(tree), a subshell, so a counter would never advance: mktemp makes each tree its own.
tree() {
  local d
  d="$(mktemp -d "${TMP}/t.XXXXXX")"
  mkdir -p "${d}/scripts/ci" "${d}/.github/workflows" "${d}/src/Single" "${d}/src/Multi" \
    "${d}/deploy/compose" "${d}/tests/uat"
  printf '<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>\n' \
    > "${d}/src/Single/Single.csproj"
  printf '<Project><PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup></Project>\n' \
    > "${d}/src/Multi/Multi.csproj"
  printf '%s' "${d}"
}

# Echoes "<exit>|<output>".
check() {
  local out code
  out="$("${PY_BIN}" "${CHECK}" --root "$1" 2>&1)"; code=$?
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

echo "== a pinned filter must be guarded, over its own TRX =="
d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
PROJ="src/Single/Single.csproj"
dotnet test "$PROJ" -f net8.0 --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
SH
expect "an unguarded pinned filter is refused" 1 "is not followed by" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
PROJ="src/Single/Single.csproj"
dotnet test "$PROJ" -f net8.0 --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
bash scripts/ci/zero-test-guard.sh out/foo.trx
SH
expect "the same filter followed by the guard over its TRX is accepted" 0 "1 guarded" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
bash scripts/ci/zero-test-guard.sh out/bar.trx
SH
expect "a guard over some OTHER TRX (the copy-paste slip) is refused" 1 "naming foo.trx" "$(check "${d}")"

# The slip above passes only because bar.trx does not CONTAIN foo.trx. Real names nest in one unit
# (api-security.trx holds security.trx; kernel-gate-hosting.trx holds hosting.trx), so the name must
# be an argument's whole basename, not a substring of the line.
d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
bash scripts/ci/zero-test-guard.sh out/barfoo.trx
SH
expect "a guard over a TRX whose name only CONTAINS this one (barfoo.trx) is refused" 1 \
  "is not followed by \`bash scripts/ci/zero-test-guard.sh\` naming foo.trx" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
bash scripts/ci/zero-test-guard.sh out/other.trx  # foo.trx
SH
expect "a guard that names this TRX only in a trailing comment is refused" 1 \
  "is not followed by \`bash scripts/ci/zero-test-guard.sh\` naming foo.trx" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
bash scripts/ci/zero-test-guard.sh out/foo.trx
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
SH
expect "a guard that runs BEFORE the test is refused" 1 "is not followed by" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "Category=Smoke" --no-build
SH
expect "a pinned filter that writes no TRX is refused" 1 "writes no TRX of its own" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
bash scripts/cert-gate-zero-test-guard.sh out/foo.trx
SH
expect "cert-gate's own guard script is not this guard" 1 "is not followed by" "$(check "${d}")"

echo "== the guard must be able to fail its lane =="
d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
bash scripts/ci/zero-test-guard.sh out/foo.trx || true
SH
expect "a guard neutralised by || true is refused" 1 \
  "cannot fail: it shares its line with \`||\`" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
bash scripts/ci/zero-test-guard.sh out/foo.trx || echo "zero-test guard complained"
SH
expect "a guard neutralised by || echo is refused" 1 \
  "cannot fail: it shares its line with \`||\`" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
bash scripts/ci/zero-test-guard.sh out/foo.trx && echo "guarded"
SH
expect "a guard in an && list (exempt from set -e) is refused" 1 \
  "cannot fail: it shares its line with \`&&\`" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
echo "bash scripts/ci/zero-test-guard.sh out/foo.trx"
SH
expect "a line that only mentions the guard (echo) is refused" 1 \
  "cannot fail: it is not a command of its own" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
bash scripts/ci/zero-test-guard.sh out/foo.trx
echo "gate: PASS"
SH
expect "a guard in a script without set -e is refused" 1 \
  "cannot fail: \`set -e\` is not in force" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
set +e
bash scripts/ci/zero-test-guard.sh out/foo.trx
SH
expect "a guard after set +e is refused" 1 "cannot fail: \`set -e\` is not in force" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
cat > out/later.sh <<'EOF'
bash scripts/ci/zero-test-guard.sh out/foo.trx
EOF
SH
expect "a guard that exists only as text inside a heredoc body is refused" 1 \
  "cannot fail: it is text inside a heredoc body (opened at line 4)" "$(check "${d}")"

# Control for the case above: a heredoc must CLOSE on its delimiter. If it did not, every guard after
# the first heredoc in a file would read as text, and the `exit 0` inside this body would read as a
# command that ends the script.
d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
cat > out/note.sh <<'EOF'
bash scripts/ci/zero-test-guard.sh out/foo.trx
exit 0
EOF
bash scripts/ci/zero-test-guard.sh out/foo.trx
SH
expect "a heredoc that closes before the real guard hides neither the guard nor anything after it" 0 \
  "1 guarded" "$(check "${d}")"

echo "== nothing may end the unit with success between the test and its guard =="
d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
exit 0
bash scripts/ci/zero-test-guard.sh out/foo.trx
SH
expect "an exit 0 between the test and its guard is refused" 1 \
  "can end before its guard over foo.trx at scripts/gate.sh:5 runs: \`exit 0\` at scripts/gate.sh:4" \
  "$(check "${d}")"

d="$(tree)"; cat > "${d}/.github/workflows/w.yml" <<'YML'
jobs:
  a:
    steps:
      - run: |
          dotnet test src/Single/Single.csproj \
            --filter "FullyQualifiedName~Foo" \
            --logger "trx;LogFileName=foo.trx" \
            --results-directory test-results
          if [ -n "${SKIP_GUARD:-}" ]; then exit; fi
          bash scripts/ci/zero-test-guard.sh test-results/foo.trx
YML
expect "a conditional bare exit between the test and its guard in a run block is refused" 1 \
  "\`exit\` at .github/workflows/w.yml:9 ends the run block at line 4 with success first" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
[ -f out/foo.trx ] || exit 1
bash scripts/ci/zero-test-guard.sh out/foo.trx
SH
expect "an exit 1 between the test and its guard (it fails the lane) is accepted" 0 "1 guarded" "$(check "${d}")"

d="$(tree)"; printf 'gate:\n\tdotnet test src/Single/Single.csproj --filter "Category=Smoke" --logger "trx;LogFileName=smoke.trx"\n\t@if [ -n "$${SKIP:-}" ]; then exit 0; fi\n\tbash scripts/ci/zero-test-guard.sh smoke.trx\n' \
  > "${d}/Makefile"
expect "an exit 0 on its own Make recipe line (a shell of its own) is accepted" 0 "1 guarded" "$(check "${d}")"

echo "== one TRX per invocation =="
d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Multi/Multi.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo.trx" --results-directory out
bash scripts/ci/zero-test-guard.sh out/foo.trx
SH
expect "a multi-target project run without -f is refused" 1 "targets several frameworks" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Multi/Multi.csproj -f net8.0 --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo-net8.0.trx" --results-directory out
dotnet test src/Multi/Multi.csproj -f net10.0 --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=foo-net10.0.trx" --results-directory out
bash scripts/ci/zero-test-guard.sh out/foo-net8.0.trx out/foo-net10.0.trx
SH
expect "the same project split per framework, one TRX each, is accepted" 0 "2 guarded" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" \
  --logger "trx;LogFileName=same.trx" --results-directory out
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Bar" \
  --logger "trx;LogFileName=same.trx" --results-directory out
bash scripts/ci/zero-test-guard.sh out/same.trx
SH
expect "two invocations sharing one LogFileName are refused" 1 "is also used at line" "$(check "${d}")"

echo "== solution-wide runs =="
d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test All.sln --filter "FullyQualifiedName~Perf" --logger "trx;LogFileName=perf.trx"
bash scripts/ci/zero-test-guard.sh --solution perf.trx
SH
expect "a solution run with one LogFileName is refused" 1 "needs LogFilePrefix" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test All.sln --filter "FullyQualifiedName~Perf" --logger "trx;LogFilePrefix=perf"
bash scripts/ci/zero-test-guard.sh perf-results/*__perf_*.trx
SH
expect "a solution run guarded outside --solution mode is refused" 1 "--solution mode" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test All.sln --filter "FullyQualifiedName~Perf" --logger "trx;LogFilePrefix=perf"
bash scripts/ci/zero-test-guard.sh --solution perf-results/*__perf_*.trx
SH
expect "a solution run with LogFilePrefix and a --solution guard is accepted" 0 "1 guarded" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
set -euo pipefail
dotnet test All.sln --filter "FullyQualifiedName~Perf" --logger "trx;LogFilePrefix=perf"
bash scripts/ci/zero-test-guard.sh --solution perf-results/*__perf-certification_*.trx
SH
expect "a --solution guard over another prefix that CONTAINS this one is refused" 1 \
  "is not followed by \`bash scripts/ci/zero-test-guard.sh\` naming perf" "$(check "${d}")"

echo "== what counts as pinned =="
d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName!~Bridge&Category!=Slow" --no-build
SH
expect "an exclusion-only filter is out of scope" 0 "0 pinned invocation(s)" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
dotnet test src/Single/Single.csproj --filter "${CALLER_FILTER}" --no-build
SH
expect "a filter the check cannot resolve counts as pinned (fail closed)" 1 "writes no TRX of its own" "$(check "${d}")"

d="$(tree)"; cat > "${d}/scripts/gate.sh" <<'SH'
dotnet test "$SOMEWHERE" -f net8.0 --filter "FullyQualifiedName~Foo" --logger "trx;LogFileName=foo.trx"
bash scripts/ci/zero-test-guard.sh foo.trx
SH
expect "a project the check cannot resolve is refused" 1 "cannot resolve which project" "$(check "${d}")"

d="$(tree)"; cat > "${d}/tests/uat/tier.sh" <<'SH'
dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Foo" --logger "trx;LogFileName=uat.trx"
SH
expect "tests/**/*.sh is in the scan (uat-gate runs tests/uat/*.sh)" 1 \
  "tests/uat/tier.sh:1: pinned filter 'FullyQualifiedName~Foo' is not followed by" "$(check "${d}")"

echo "== workflows and the Makefile =="
d="$(tree)"; cat > "${d}/.github/workflows/w.yml" <<'YML'
jobs:
  a:
    steps:
      # dotnet test src/Single/Single.csproj --filter "FullyQualifiedName~Commented" (a comment)
      - run: |
          dotnet test src/Multi/Multi.csproj \
            -f net8.0 \
            --filter "FullyQualifiedName~Foo" \
            --logger "trx;LogFileName=foo-${{ matrix.os }}.trx" \
            --results-directory test-results
          bash scripts/ci/zero-test-guard.sh test-results/foo-${{ matrix.os }}.trx
YML
expect "a workflow step with a matrix-named TRX and its guard is accepted; comments are not lanes" 0 \
  "1 pinned invocation(s): 1 guarded" "$(check "${d}")"

d="$(tree)"; cat > "${d}/.github/workflows/w.yml" <<'YML'
jobs:
  a:
    steps:
      - run: |
          dotnet test src/Multi/Multi.csproj \
            -f net8.0 \
            --filter "FullyQualifiedName~Foo" \
            --logger "trx;LogFileName=foo-${{ matrix.os }}.trx" \
            --results-directory test-results
          bash scripts/ci/zero-test-guard.sh test-results/foo-${{ matrix.label }}.trx
YML
expect "a guard over a TRX named by a DIFFERENT GitHub expression is refused" 1 \
  "naming foo-\${{ matrix.os }}.trx" "$(check "${d}")"

d="$(tree)"; cat > "${d}/.github/workflows/w.yml" <<'YML'
jobs:
  a:
    steps:
      - run: |
          dotnet test src/Single/Single.csproj \
            --filter "FullyQualifiedName~Foo" \
            --logger "trx;LogFileName=foo.trx" \
            --results-directory test-results
YML
expect "an unguarded workflow step is refused" 1 "is not followed by" "$(check "${d}")"

d="$(tree)"; cat > "${d}/.github/workflows/w.yml" <<'YML'
jobs:
  a:
    steps:
      - name: test
        run: |
          dotnet test src/Single/Single.csproj \
            --filter "FullyQualifiedName~Foo" \
            --logger "trx;LogFileName=foo.trx" \
            --results-directory test-results
      - name: guard
        continue-on-error: true
        run: |
          bash scripts/ci/zero-test-guard.sh test-results/foo.trx
YML
expect "a guard in another workflow step is refused" 1 "is guarded only in another unit" "$(check "${d}")"

d="$(tree)"; cat > "${d}/.github/workflows/w.yml" <<'YML'
jobs:
  a:
    steps:
      - run: |
          dotnet test src/Single/Single.csproj \
            --filter "FullyQualifiedName~Foo" \
            --logger "trx;LogFileName=foo.trx" \
            --results-directory test-results
          set +e
          bash scripts/ci/zero-test-guard.sh test-results/foo.trx
          echo done
YML
expect "a guard after set +e in a run block is refused" 1 "a \`set +e\` earlier in its run block" "$(check "${d}")"

d="$(tree)"; printf 'gate:\n\tdotnet test src/Single/Single.csproj --filter "Category=Smoke" --logger "trx;LogFileName=smoke.trx"\n' \
  > "${d}/Makefile"
expect "an unguarded Make recipe is refused" 1 "Makefile:2" "$(check "${d}")"

d="$(tree)"; printf 'gate:\n\tdotnet test src/Single/Single.csproj --filter "Category=Smoke" --logger "trx;LogFileName=smoke.trx"\n\tbash scripts/ci/zero-test-guard.sh smoke.trx\n' \
  > "${d}/Makefile"
expect "the same recipe followed by the guard is accepted" 0 "1 guarded" "$(check "${d}")"

d="$(tree)"; printf 'gate:\n\tdotnet test src/Single/Single.csproj --filter "Category=Smoke" --logger "trx;LogFileName=smoke.trx"\n\t-bash scripts/ci/zero-test-guard.sh smoke.trx\n' \
  > "${d}/Makefile"
expect "a guard line with make's - prefix (error ignored) is refused" 1 \
  "cannot fail: make ignores its exit status" "$(check "${d}")"

d="$(tree)"; printf 'gate:\n\tdotnet test src/Single/Single.csproj --filter "Category=Smoke" --logger "trx;LogFileName=smoke.trx"\n\t@bash scripts/ci/zero-test-guard.sh smoke.trx; true\n' \
  > "${d}/Makefile"
expect "a guard followed by ; true on its recipe line is refused" 1 \
  "cannot fail: it shares its line with \`;\`" "$(check "${d}")"

d="$(tree)"; printf 'gate:\n\tdotnet test src/Single/Single.csproj --filter "Category=Smoke" --logger "trx;LogFileName=smoke.trx"\n\nother:\n\tbash scripts/ci/zero-test-guard.sh smoke.trx\n' \
  > "${d}/Makefile"
expect "a guard in another Make target is refused" 1 "is guarded only in another unit" "$(check "${d}")"

echo "== compose services: the guard follows the line that brings them up =="
compose_file() {  # dir project
  cat > "$1/deploy/compose/c.yml" <<YML
services:
  t:
    image: x
    command: >
      bash -lc "dotnet test /workspace/$2
      --filter 'FullyQualifiedName~Foo'
      --logger 'trx;LogFileName=compose-foo.trx'
      --results-directory /workspace/test-results"
YML
}
d="$(tree)"; compose_file "${d}" "src/Single/Single.csproj"
cat > "${d}/.github/workflows/w.yml" <<'YML'
jobs:
  a:
    steps:
      - run: |
          docker compose -f deploy/compose/c.yml up --abort-on-container-exit --exit-code-from t t
          bash scripts/ci/zero-test-guard.sh test-results/compose-foo.trx
YML
expect "a compose service's pinned filter, guarded after the step brings it up, is accepted" 0 \
  "1 pinned invocation(s): 1 guarded" "$(check "${d}")"

d="$(tree)"; compose_file "${d}" "src/Single/Single.csproj"
cat > "${d}/.github/workflows/w.yml" <<'YML'
jobs:
  a:
    steps:
      - run: |
          docker compose -f deploy/compose/c.yml up --abort-on-container-exit --exit-code-from t t
          test -f test-results/compose-foo.trx
YML
expect "a compose service brought up with no guard after it is refused" 1 \
  "brought up at .github/workflows/w.yml:5, is not followed by" "$(check "${d}")"

d="$(tree)"; compose_file "${d}" "src/Single/Single.csproj"
expect "a compose service nothing brings up is refused" 1 \
  "no workflow, script or Make target brings up" "$(check "${d}")"

d="$(tree)"; compose_file "${d}" "src/Multi/Multi.csproj"
cat > "${d}/.github/workflows/w.yml" <<'YML'
jobs:
  a:
    steps:
      - run: |
          docker compose -f deploy/compose/c.yml up --abort-on-container-exit --exit-code-from t t
          bash scripts/ci/zero-test-guard.sh test-results/compose-foo.trx
YML
expect "a compose service running a multi-target /workspace project without -f is refused" 1 \
  "/workspace/src/Multi/Multi.csproj targets several frameworks" "$(check "${d}")"

echo "== the real tree =="
# Bare, as run-repo-gates runs it: the floor, the required lanes and the stale-exemption checks are
# all live here and nowhere else.
got="$("${PY_BIN}" "${CHECK}" 2>&1)"; code=$?
if [[ "${code}" == "0" ]] && grep -q "0 problem(s)" <<<"${got}"; then
  ok "every pinned filter in this repository is guarded (${got##*: })"
else
  bad "every pinned filter in this repository is guarded" "exit ${code}: ${got}"
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
