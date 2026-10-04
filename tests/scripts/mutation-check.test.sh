#!/usr/bin/env bash
# Tests for scripts/mutation-check.sh, with a stub runner standing in for the container.
#
# WHY A TEST. mutation-check.sh is what turns "the tests catch this" into a recorded verdict, so a
# defect in it reads as evidence. The shapes that matter are the ones that would print a confident
# wrong answer: a replacement that matched nothing or two places, a red run that selected no test
# (dotnet test exits 0) or skipped every test, read as a survivor; a build error or a host abort read
# as a kill; a restore that left the tree dirty; a green run that did not pass; a summary-shaped string
# in test output counted as a summary. Each is driven here with an input that must make the script
# refuse, and the KILLED and SURVIVED cases are there so that "it always refuses" cannot pass too.
#
# THE STUB. ASHLAR_MUTATION_RUNNER replaces the container run. The stub gets the clone path and the
# phase, prints a dotnet-shaped header and summary line, and fails when the kill marker is in the
# mutated file. It also logs "<phase> marker|no-marker", so the test can see that the red run saw the
# mutation and the green run saw the restored file, rather than trusting the script's word for it.
#
# THE REAL RUNNER. One case leaves ASHLAR_MUTATION_RUNNER unset, so the script runs the
# scripts/test-in-container.sh committed in the fixture repository, exactly as it runs the real one.
# That fake logs its arguments and, like the real one, sees uncommitted work only when given --dirty.
# Without this case the container branch, the only one that produces real evidence, would be untested.
#
# Run:  bash tests/scripts/mutation-check.test.sh
# Bash, git and python3 (the script's own replacement step): no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="${ROOT}/scripts/mutation-check.sh"

# Bump when you add an assertion; the check at the bottom says why.
EXPECTED_ASSERTIONS=66

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; echo "         $2"; }

# Non-vacuity control. A missing script makes `bash <missing>` exit 127, and every "must refuse" case
# below asserts a specific exit code and message, so none of them would pass, but the reason would be
# far from obvious.
if [[ ! -f "${SCRIPT}" ]]; then
  echo "FAIL - ${SCRIPT} does not exist; nothing here can be tested."
  exit 1
fi

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

# Hermetic git: no system or user config (signing, hooks, templates), and a fixed identity, so the
# fixture builds the same on a CI runner, a laptop and inside the devtest container.
export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null
export GIT_AUTHOR_NAME=fixture GIT_AUTHOR_EMAIL=fixture@example.invalid
export GIT_COMMITTER_NAME=fixture GIT_COMMITTER_EMAIL=fixture@example.invalid
# Each case chooses its runner (see mc); one left over in the caller's shell must not choose for it.
unset ASHLAR_MUTATION_RUNNER

# --- fixture: a two-commit repository -----------------------------------------------------------
FIX="${TMP}/fixture"
mkdir -p "${FIX}/src" "${FIX}/scripts"
git -C "${FIX}" init -q
write_calc() {  # version
  cat > "${FIX}/src/calc.cs" <<EOF
// version: $1
// twice twice
int Add(int a, int b)
{
    return a + b;
}
EOF
}
write_calc one
# A tracked symlink to a file OUTSIDE the repository: a write through it would land there.
echo 'secret a + b' > "${TMP}/outside.cs"
ln -s "${TMP}/outside.cs" "${FIX}/src/link.cs"
# The fake container runner, committed where mutation-check.sh looks for the real one.
cat > "${FIX}/scripts/test-in-container.sh" <<'FAKE'
#!/usr/bin/env bash
# Fake scripts/test-in-container.sh. Like the real one, it tests the COMMITTED tree unless it is given
# --dirty, so a red run without --dirty does not see the (uncommitted) mutation.
echo "argv $*" >> "${STUB_LOG}"
repo=""; dirty=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) repo="$2"; shift 2 ;;
    --dirty) dirty=1; shift ;;
    --framework|--project|--filter|--ref) shift 2 ;;
    *) echo "fake test-in-container: unknown argument '$1'" >&2; exit 2 ;;
  esac
done
if [[ "${dirty}" -eq 1 ]]; then src="$(cat "${repo}/src/calc.cs")"; else src="$(git -C "${repo}" show HEAD:src/calc.cs)"; fi
echo "Test run for /repo/bin/Debug/net8.0/Fixture.dll (.NETCoreApp,Version=v8.0)"
if grep -qF -- "${STUB_KILL_MARKER}" <<<"${src}"; then
  echo "saw marker" >> "${STUB_LOG}"
  echo "  Failed Fixture.CalcTests.Adds [1 ms]"
  echo "Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 9 ms - Fixture.dll (net8.0)"
  exit 1
fi
echo "saw no-marker" >> "${STUB_LOG}"
echo "Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 9 ms - Fixture.dll (net8.0)"
exit 0
FAKE
git -C "${FIX}" add -A && git -C "${FIX}" commit -q -m one
SHA1="$(git -C "${FIX}" rev-parse HEAD)"
write_calc two
git -C "${FIX}" commit -q -am two
SHA2="$(git -C "${FIX}" rev-parse HEAD)"
if [[ -z "${SHA1}" || -z "${SHA2}" || "${SHA1}" == "${SHA2}" ]] \
   || [[ "$(git -C "${FIX}" ls-files -s src/link.cs | cut -d' ' -f1)" != 120000 ]] \
   || [[ -n "$(git -C "${FIX}" status --porcelain)" ]]; then
  echo "FAIL - arrange failed: the fixture repository is not two clean commits with a tracked symlink."
  exit 1
fi

# --- the stub runner ----------------------------------------------------------------------------
STUB_LOG="${TMP}/stub.log"
cat > "${TMP}/stub.sh" <<'STUB'
#!/usr/bin/env bash
clone="$1"; phase="$2"
state=no-marker
if grep -qF -- "${STUB_KILL_MARKER}" "${clone}/src/calc.cs"; then state=marker; fi
echo "${phase} ${state}" >> "${STUB_LOG}"
header()    { echo "Test run for /x/bin/Debug/net8.0/$1.dll (.NETCoreApp,Version=v8.0)"; }
pass_line() { header "$2"; echo "Passed!  - Failed:     0, Passed:     $1, Skipped:     0, Total:     $1, Duration: 10 ms - $2.dll (net8.0)"; }
fail_line() { header "$4"; echo "  Failed $4.CalcTests.Adds(a: 1) [3 ms]"
              echo "Failed!  - Failed:     $1, Passed:     $2, Skipped:     0, Total:     $3, Duration: 12 ms - $4.dll (net8.0)"; }
none_line() { header Fixture; echo "$1!  - Failed:     0, Passed:     0, Skipped:     $2, Total:     $2, Duration: 1 ms - Fixture.dll (net8.0)"; }
case "${STUB_MODE}" in
  nomatch)       echo "No test matches the given testcase filter \`FullyQualifiedName~Nothing\` in /x/Fixture.dll"; exit 0 ;;
  build-error)   echo "src/calc.cs(5,20): error CS1002: ; expected"; exit 1 ;;
  litter)        if [[ "${phase}" == red ]]; then echo stray > "${clone}/stray.txt"; fi ;;
  abort)         pass_line 7 Fixture; exit 1 ;;
  fail-exit0)    fail_line 2 5 7 Fixture; exit 0 ;;
  total0)        none_line Passed 0; exit 0 ;;
  skipped)       none_line Skipped 5; exit 0 ;;
  green-fails)   if [[ "${phase}" == green ]]; then fail_line 1 6 7 Fixture; exit 1; fi ;;
  green-abort)   if [[ "${phase}" == green ]]; then pass_line 7 Fixture; exit 1; fi ;;
  green-skipped) if [[ "${phase}" == green ]]; then none_line Skipped 7; exit 0; fi ;;
  more-green)    if [[ "${phase}" == green ]]; then pass_line 8 Fixture; exit 0; fi ;;
  two-dlls)      if [[ "${state}" == marker ]]; then fail_line 2 5 7 Fixture; pass_line 3 Other; exit 1; fi
                 pass_line 7 Fixture; pass_line 3 Other; exit 0 ;;
  echo-fixture)  pass_line 3 Other
                 if [[ "${state}" == marker ]]; then
                   header Fixture
                   # Summary-shaped strings that test output can carry (this repo's own test fixtures
                   # hold several). None of them is a summary, and none may be counted.
                   echo "Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 1 ms - Embedded.dll (net8.0)"
                   echo "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 1 s"
                   echo "Failed!  - Failed:     9, Passed:     0, Skipped:     0, Total:     9, Duration: 5 ms - Fixture.dll (net8.0)"
                   echo "        Failed!  - Failed:     4, Passed:     0, Skipped:     0, Total:     4, Duration: 8 ms - Other.dll (net8.0)"
                   fail_line 2 5 7 Fixture; exit 1
                 fi
                 pass_line 7 Fixture; exit 0 ;;
esac
if [[ "${state}" == marker ]]; then fail_line 2 5 7 Fixture; exit 1; fi
pass_line 7 Fixture
exit 0
STUB
export STUB_LOG
# Every summary line of a stubbed run must say so.
OVR=" runner=override"

# mc <stub-mode> <kill-marker> <script args...>: sets RC, OUT (stdout+stderr), LAST (the summary line)
# and STUB (what the runner saw). Mode "real" runs with ASHLAR_MUTATION_RUNNER unset, so the script
# uses the fixture's committed scripts/test-in-container.sh; every other mode uses the stub.
mc() {
  local mode="$1" marker="$2"; shift 2
  local runner=(env "ASHLAR_MUTATION_RUNNER=bash ${TMP}/stub.sh")
  if [[ "${mode}" == real ]]; then runner=(env -u ASHLAR_MUTATION_RUNNER); fi
  : > "${STUB_LOG}"
  OUT="$(STUB_MODE="${mode}" STUB_KILL_MARKER="${marker}" "${runner[@]}" bash "${SCRIPT}" --repo "${FIX}" "$@" 2>&1)"; RC=$?
  LAST="$(printf '%s\n' "${OUT}" | tail -n 1)"
  STUB="$(cat "${STUB_LOG}")"
}

expect_last() {  # label want_rc want_last_line
  if [[ "${RC}" == "$2" && "${LAST}" == "$3" ]]; then
    ok "$1"
  else
    bad "$1" "wanted exit $2 and last line '$3'; got exit ${RC} and '${LAST}'"
  fi
}

expect_out() {  # label want_rc needle
  if [[ "${RC}" == "$2" ]] && grep -qF -- "$3" <<<"${OUT}"; then
    ok "$1"
  else
    bad "$1" "wanted exit $2 and '$3' in the output; got exit ${RC}, last line '${LAST}'"
  fi
}

expect_stub() {  # label want
  if [[ "${STUB}" == "$2" ]]; then
    ok "$1"
  else
    bad "$1" "the stub saw [$(tr '\n' '|' <<<"${STUB}")], wanted [$(tr '\n' '|' <<<"$2")]"
  fi
}

work_dir_of() { sed -n 's/^== work dir: \(.*\) ==$/\1/p' <<<"${OUT}" | head -n 1; }

echo "== a mutation the tests catch is KILLED =="
mc normal 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id add-sub
expect_last "KILLED: exit 0 and the summary line carries both runs' counts and the commit" 0 \
  "mutation add-sub: KILLED red=failed:2/7 green=passed:7/7 ref=${SHA2}${OVR}"
expect_stub "the red run saw the mutation and the green run saw the restored file" $'red marker\ngreen no-marker'
expect_out "the proof of the mutation is printed as a diff (removed line)" 0 "-    return a + b;"
expect_out "the proof of the mutation is printed as a diff (added line)" 0 "+    return a - b;"
expect_out "the restore is shown clean" 0 "porcelain=[]"
expect_out "the runner actually used is printed, and an override says it is one" 0 \
  "== runner: ASHLAR_MUTATION_RUNNER=bash ${TMP}/stub.sh <clone> red (an override, not the container"
if [[ "$(grep -A1 -xF '== red: failing tests ==' <<<"${OUT}" | sed -n 2p)" == "  Fixture.CalcTests.Adds(a: 1)" ]]; then
  ok "the red run's failing tests are listed by name"
else
  bad "the red run's failing tests are listed by name" "no '== red: failing tests ==' line followed by '  Fixture.CalcTests.Adds(a: 1)'"
fi
WD="$(work_dir_of)"
if [[ -n "${WD}" && ! -e "${WD}" ]]; then
  ok "the default (mktemp) work dir is removed on exit"
else
  bad "the default (mktemp) work dir is removed on exit" "work dir '${WD}' still exists, or was never printed"
fi
if [[ -z "$(git -C "${FIX}" status --porcelain)" ]] && grep -qF 'return a + b;' "${FIX}/src/calc.cs"; then
  ok "the source repository is never written to"
else
  bad "the source repository is never written to" "${FIX} is dirty or its file changed"
fi

echo "== the real runner: the committed scripts/test-in-container.sh, ASHLAR_MUTATION_RUNNER unset =="
mc real 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id real --framework net9.0 \
  --project tests/Fixture.Tests.csproj --filter 'FullyQualifiedName~A|FullyQualifiedName~B' --work-dir "${TMP}/realwd"
expect_last "KILLED through scripts/test-in-container.sh, and the summary line has no runner=override" 0 \
  "mutation real: KILLED red=failed:1/5 green=passed:5/5 ref=${SHA2}"
RED_ARGV="$(sed -n 's/^argv //p' <<<"${STUB}" | sed -n 1p)"
GREEN_ARGV="$(sed -n 's/^argv //p' <<<"${STUB}" | sed -n 2p)"
if [[ "$(grep '^saw ' <<<"${STUB}")" == $'saw marker\nsaw no-marker' ]]; then
  ok "the red run saw the uncommitted mutation and the green run the restored file"
else
  bad "the red run saw the uncommitted mutation and the green run the restored file" "the fake saw [$(tr '\n' '|' <<<"${STUB}")]"
fi
if [[ -n "${GREEN_ARGV}" && " ${RED_ARGV} " == *" --dirty "* && " ${GREEN_ARGV} " != *" --dirty "* ]]; then
  ok "--dirty is passed to the red run only"
else
  bad "--dirty is passed to the red run only" "red argv [${RED_ARGV}], green argv [${GREEN_ARGV}]"
fi
for want in "--repo ${TMP}/realwd/repo" "--framework net9.0" "--project tests/Fixture.Tests.csproj" \
            "--filter FullyQualifiedName~A|FullyQualifiedName~B"; do
  if [[ " ${RED_ARGV} " == *" ${want} "* && " ${GREEN_ARGV} " == *" ${want} "* ]]; then
    ok "both runs are given ${want%% *}"
  else
    bad "both runs are given ${want%% *}" "wanted '${want}'; red argv [${RED_ARGV}], green argv [${GREEN_ARGV}]"
  fi
done
expect_out "the runner line is the container command, in full" 0 \
  "== runner: bash ${TMP}/realwd/repo/scripts/test-in-container.sh --repo ${TMP}/realwd/repo --framework net9.0 --dirty --project"

echo "== a mutation the tests miss SURVIVES =="
mc normal 'a - b' --file src/calc.cs --old 'int Add' --new 'int Sum'
expect_last "SURVIVED: exit 1, and the default id is the file's base name" 1 \
  "mutation calc: SURVIVED red=failed:0/7 green=not-run ref=${SHA2}${OVR}"
expect_stub "a survivor is not re-run green: the verdict cannot change" "red no-marker"

echo "== the replacement must name exactly one place =="
mc normal 'a - b' --file src/calc.cs --old 'a * b' --new 'a - b'
expect_out "0 occurrences is refused with exit 2" 2 "expected exactly one occurrence in src/calc.cs, found 0"
expect_out "...and reported INVALID with a reason" 2 "reason=apply-failed"
expect_stub "...before the runner is ever called" ""
mc normal 'a - b' --file src/calc.cs --old 'twice' --new 'once'
expect_out "2 occurrences is refused with exit 2, naming the lines" 2 "found 2 (lines 2, 2)"
expect_stub "...before the runner is ever called" ""
mc normal 'a - b' --file src/calc.cs --old 'a + b' --new 'a + b'
expect_out "a replacement identical to the original is refused" 2 "identical; that mutation changes nothing"
mc normal 'a - b' --file src/missing.cs --old 'a + b' --new 'a - b'
expect_out "a file not tracked at the commit is refused" 2 "src/missing.cs is not a tracked file"

echo "== --file is a literal path to a regular file =="
mc normal 'a - b' --file ./src//calc.cs --old 'a + b' --new 'a - b'
expect_last "./src//calc.cs names src/calc.cs: it applies, the diff check agrees, and it is KILLED" 0 \
  "mutation calc: KILLED red=failed:2/7 green=passed:7/7 ref=${SHA2}${OVR}"
mc normal 'a - b' --file 'src/*.cs' --old 'a + b' --new 'a - b'
if [[ "${RC}" == 2 ]] && grep -qF "src/*.cs is not a tracked file at ${SHA2} (--file is a literal path, not a pattern)" <<<"${OUT}" \
   && ! grep -q Traceback <<<"${OUT}"; then
  ok "a glob is not expanded: it names no file and is refused cleanly, with no traceback"
else
  bad "a glob is not expanded: it names no file and is refused cleanly, with no traceback" "exit ${RC}, last line '${LAST}'"
fi
mc normal 'a - b' --file src --old 'a + b' --new 'a - b'
expect_out "a directory is refused" 2 "src is a tree at ${SHA2}, not a file"
mc normal 'a - b' --file src/../src/calc.cs --old 'a + b' --new 'a - b'
expect_out "a path with .. in it is refused" 2 "with no . or .. components"
mc normal 'a - b' --file src/link.cs --old 'a + b' --new 'a - b'
expect_out "a tracked symlink is refused" 2 "src/link.cs is a symlink at ${SHA2}; refusing to write through it"
expect_stub "...before the runner is ever called" ""
if [[ "$(cat "${TMP}/outside.cs")" == "secret a + b" ]]; then
  ok "...and the file it points to, outside the clone, is unchanged"
else
  bad "...and the file it points to, outside the clone, is unchanged" "${TMP}/outside.cs now reads '$(cat "${TMP}/outside.cs")'"
fi

echo "== multi-line text through --old-file / --new-file =="
printf '    return a + b;\n}\n' > "${TMP}/old.txt"
printf '    return a - b;\n}\n' > "${TMP}/new.txt"
if [[ "$(wc -l < "${TMP}/old.txt")" -eq 2 ]]; then
  mc normal 'a - b' --file src/calc.cs --old-file "${TMP}/old.txt" --new-file "${TMP}/new.txt" \
    --id multi --keep --work-dir "${TMP}/kept"
  expect_last "a two-line replacement applies and is KILLED" 0 \
    "mutation multi: KILLED red=failed:2/7 green=passed:7/7 ref=${SHA2}${OVR}"
else
  bad "a two-line replacement applies and is KILLED" "arrange failed: the old text is not two lines"
fi
if [[ -s "${TMP}/kept/red.log" && -s "${TMP}/kept/green.log" ]] \
   && grep -qF 'return a + b;' "${TMP}/kept/repo/src/calc.cs"; then
  ok "--keep keeps the work dir: both run logs and the restored clone"
else
  bad "--keep keeps the work dir: both run logs and the restored clone" "${TMP}/kept is missing a log or the restored file"
fi
expect_out "...and says where it is" 0 "== work dir kept: ${TMP}/kept =="
mc normal 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --work-dir "${TMP}/given"
if [[ "${RC}" == 0 && ! -e "${TMP}/given" ]]; then
  ok "a --work-dir given without --keep is removed on exit"
else
  bad "a --work-dir given without --keep is removed on exit" "exit ${RC}; ${TMP}/given exists: $([[ -e "${TMP}/given" ]] && echo yes || echo no)"
fi

echo "== --ref mutates the commit named, not HEAD =="
mc normal 'version: uno' --file src/calc.cs --old 'version: one' --new 'version: uno' --id at-ref --ref HEAD~1
expect_last "--ref HEAD~1 mutates text that exists only there, and reports that commit" 0 \
  "mutation at-ref: KILLED red=failed:2/7 green=passed:7/7 ref=${SHA1}${OVR}"
mc normal 'version: uno' --file src/calc.cs --old 'version: one' --new 'version: uno'
expect_out "...while the same text at HEAD is not found" 2 "found 0"

echo "== a red run is only a kill if tests failed, and only a survivor if tests ran =="
mc build-error 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id build
expect_last "a red run that fails with no test summary (a build error) is INVALID, not KILLED" 2 \
  "mutation build: INVALID red=exit:1 green=not-run ref=${SHA2} reason=red-no-tests${OVR}"
mc nomatch 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id nomatch
expect_last "a red run that selected no test and exited 0 is INVALID, not SURVIVED" 2 \
  "mutation nomatch: INVALID red=exit:0 green=not-run ref=${SHA2} reason=red-no-tests${OVR}"
expect_out "...and says why" 2 "the filter selected no test"
mc total0 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id total0
expect_last "a red summary of Total: 0 is INVALID, not SURVIVED" 2 \
  "mutation total0: INVALID red=failed:0/0 green=not-run ref=${SHA2} reason=red-no-tests${OVR}"
mc skipped 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id skipped
expect_last "a red run in which every selected test was skipped is INVALID, not SURVIVED" 2 \
  "mutation skipped: INVALID red=failed:0/5 green=not-run ref=${SHA2} reason=red-no-tests${OVR}"
expect_out "...and says nothing was executed" 2 "the red run executed no test (total 5, skipped 5)"
mc abort 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id abort
expect_last "a red run that passed every test but exited non-zero (a host abort) is INVALID, not KILLED" 2 \
  "mutation abort: INVALID red=failed:0/7 green=not-run ref=${SHA2} reason=red-inconsistent${OVR}"
mc fail-exit0 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id fail-exit0
expect_last "a red run that failed tests but exited 0 is INVALID, not KILLED" 2 \
  "mutation fail-exit0: INVALID red=failed:2/7 green=not-run ref=${SHA2} reason=red-inconsistent${OVR}"
mc two-dlls 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id two
expect_last "counts are summed over every test assembly's summary line" 0 \
  "mutation two: KILLED red=failed:2/10 green=passed:10/10 ref=${SHA2}${OVR}"

echo "== only a real summary line is counted =="
mc echo-fixture 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id echo
expect_last "summary-shaped strings in test output (indented, unannounced, no assembly, an early echo) are not counted" 0 \
  "mutation echo: KILLED red=failed:2/10 green=passed:10/10 ref=${SHA2}${OVR}"
mc more-green 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id more
expect_last "a red/green total mismatch is a field of the summary line, not only a note on stderr" 0 \
  "mutation more: KILLED red=failed:2/7 green=passed:8/8 ref=${SHA2} totals=7/8${OVR}"

echo "== restore and green =="
mc litter 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id litter
expect_last "a clone left dirty after the restore is INVALID" 2 \
  "mutation litter: INVALID red=failed:2/7 green=not-run ref=${SHA2} reason=restore-not-clean${OVR}"
expect_out "...and the porcelain that proves it is printed" 2 "?? stray.txt"
expect_stub "...and the green run never happens on a dirty tree" "red marker"
mc green-fails 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id gf
expect_last "a green run that does not pass is INVALID: the red failures are not the mutation's" 2 \
  "mutation gf: INVALID red=failed:2/7 green=passed:6/7 ref=${SHA2} reason=green-not-passing${OVR}"
mc green-abort 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id ga
expect_last "a green run that passed every test but exited non-zero is INVALID" 2 \
  "mutation ga: INVALID red=failed:2/7 green=passed:7/7 ref=${SHA2} reason=green-not-passing${OVR}"
mc green-skipped 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --id gs
expect_last "a green run that executed no test (all skipped) is INVALID" 2 \
  "mutation gs: INVALID red=failed:2/7 green=passed:0/7 ref=${SHA2} reason=green-not-passing${OVR}"

echo "== uncommitted work in --repo is named, because it is not what gets tested =="
mkdir -p "${FIX}/tests" && echo '// a new killing test, not committed yet' > "${FIX}/tests/NewTests.cs"
echo '// edited, not committed' >> "${FIX}/src/calc.cs"
mc normal 'a - b' --file src/calc.cs --old 'int Add' --new 'int Sum' --id uncommitted
expect_last "the run is of the committed SHA, so the uncommitted test does not kill the mutation" 1 \
  "mutation uncommitted: SURVIVED red=failed:0/7 green=not-run ref=${SHA2}${OVR}"
expect_out "the warning says to commit first" 1 "has uncommitted changes. They are NOT part of ${SHA2},"
expect_out "...and names an untracked new test file" 1 "?? tests/NewTests.cs"
expect_out "...and names an edited tracked file that is not --file's" 1 " M src/calc.cs"
expect_out "...and a survivor repeats it next to the verdict" 1 "reminder: ${FIX} had uncommitted changes"
rm -rf "${FIX}/tests"
git -C "${FIX}" checkout -q -- src/calc.cs
if [[ -n "$(git -C "${FIX}" status --porcelain)" ]]; then
  echo "FAIL - could not restore the fixture after the uncommitted-work case."
  exit 1
fi

echo "== arguments =="
mc normal 'a - b'
expect_out "no --file is a usage error, not a pass" 2 "--file is required"
mc normal 'a - b' --file src/calc.cs --old 'a + b' --old-file "${TMP}/old.txt" --new 'a - b'
expect_out "--old and --old-file together are refused" 2 "exactly one of --old / --old-file"
mc normal 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --bogus
expect_out "an unknown argument is refused" 2 "unknown argument '--bogus'"
mkdir -p "${TMP}/occupied" && echo keep-me > "${TMP}/occupied/sentinel"
mc normal 'a - b' --file src/calc.cs --old 'a + b' --new 'a - b' --work-dir "${TMP}/occupied"
expect_out "a non-empty --work-dir is refused" 2 "is not empty; refusing to use it"
if [[ "$(cat "${TMP}/occupied/sentinel" 2>/dev/null)" == "keep-me" ]]; then
  ok "...and what was in it is left alone"
else
  bad "...and what was in it is left alone" "${TMP}/occupied/sentinel was removed or changed"
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
