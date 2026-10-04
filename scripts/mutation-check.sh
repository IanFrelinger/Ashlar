#!/usr/bin/env bash
# One mutation check, run the way CLAUDE.md asks for it, in a throwaway clone of a committed SHA.
#
#   clone at REF -> apply EXACTLY ONE replacement -> prove it applied -> run the tests (RED)
#   -> restore with `git checkout -- FILE` -> require an empty `git status --porcelain`
#   -> run the tests again (GREEN) -> print one machine-readable summary line.
#
# WHY A SCRIPT. "A check that was never observed failing is not evidence", so every behavioural change
# here is mutation-checked. Done by hand the protocol has five places to go quietly wrong: the
# replacement matches nothing (or two places) and the "red" run tests the original code; the red run
# fails on a build error and is read as a kill; the filter selects no test, `dotnet test` exits 0, and
# that is read as a survivor; the restore leaves something behind and the green run tests a dirty tree;
# the counts are remembered rather than recorded. Each of those is refused below, by name.
#
# The source repository is never written to. The mutation is applied in a fresh clone of REF, so a
# worktree works as --repo (scripts/test-in-container.sh itself needs a clone), and --file must be a
# regular file at REF (a symlink is refused, so nothing is written through it). Only COMMITTED state
# is mutated and tested; the script does not commit for you. With the default --ref HEAD it warns,
# naming the files, when --repo has uncommitted or untracked changes, since a new test among them is
# not part of the run.
#
# USAGE
#   scripts/mutation-check.sh --file PATH (--old TEXT | --old-file F) (--new TEXT | --new-file F)
#       [--ref REV] [--filter EXPR] [--framework TFM] [--project CSPROJ] [--id NAME]
#       [--repo PATH] [--work-dir DIR] [--keep]
#
#   scripts/mutation-check.sh --id hwm-join-dropped \
#     --file src/Ashlar.Abstractions/Security/HighWaterMark.cs \
#     --old 'var joined = seen.Join(label);' --new 'var joined = seen;' \
#     --filter 'FullyQualifiedName~ReferenceMonitorDecisionTests|FullyQualifiedName~HighWaterMarkTests'
#   ...
#   mutation hwm-join-dropped: KILLED red=failed:16/120 green=passed:120/120 ref=<sha>
#
# OPTIONS
#   --file PATH       File to mutate: a literal path relative to the repository root, not a pattern
#                     (a leading ./ and doubled slashes are dropped). Must be a regular file at REF.
#   --old TEXT        Text to replace. It must occur EXACTLY ONCE in the file; 0 or 2+ is refused.
#   --new TEXT        Replacement. May be empty (a deletion); may not equal --old.
#   --old-file F      Read the text to replace from F, byte for byte (multi-line; a trailing newline
#   --new-file F      in F is part of the text). Exclusive with --old / --new respectively.
#   --ref REV         Commit to mutate. Default: HEAD of --repo.
#   --filter EXPR     dotnet test filter. Default: scripts/test-in-container.sh's own default.
#   --framework TFM   Default: net8.0.
#   --project CSPROJ  Test project, repo-relative. Default: scripts/test-in-container.sh's own default.
#   --id NAME         Name in the summary line (no whitespace). Default: the file's base name.
#   --repo PATH       Repository to clone. Default: the repository containing this script.
#   --work-dir DIR    Where the clone and the run logs go. Must be absent or empty. Default: mktemp -d.
#   --keep            Keep the work dir. Without it the work dir is removed on exit, whoever made it.
#
# OUTPUT. The runs stream to stdout, each preceded by a "== runner: ... ==" line naming the command
# actually run; red.log and green.log are written to the work dir. After the red run the names of its
# failing tests are listed. The LAST line is the summary. Fields are separated by exactly one space;
# the bracketed fields appear only when they apply:
#   mutation <id>: KILLED red=failed:<F>/<R> green=passed:<P>/<G> ref=<sha>[ totals=<R>/<G>][ runner=override]
#   mutation <id>: SURVIVED red=failed:0/<R> green=not-run ref=<sha>[ runner=override]
#   mutation <id>: INVALID red=<r> green=<g> ref=<sha> reason=<slug>[ totals=<R>/<G>][ runner=override]
# <R> and <G> are the red and green totals (skipped tests included); totals= appears when they differ,
# which means the mutation changed what was discovered. <r> is not-run, exit:<code> (the run printed
# no summary) or failed:<F>/<R>; <g> is not-run, exit:<code> or passed:<P>/<G>. <sha> is "unresolved"
# when --ref did not resolve. runner=override marks a run made by ASHLAR_MUTATION_RUNNER, not the
# container. <slug> is one of bad-args, clone-failed, apply-failed, red-no-tests, red-inconsistent,
# restore-failed, restore-not-clean, green-not-passing.
#
# COUNTS. Only a dotnet summary line that starts at column 0,
#   Passed!  - Failed: F, Passed: P, Skipped: S, Total: T, Duration: D - <name>.dll (<tfm>)
# (any word before the "!"), and whose <name>.dll was announced by an earlier
#   Test run for <path>/<name>.dll (<framework>)
# header is counted, and only the last such line per assembly and tfm. A summary-shaped string in test
# output (an indented one, one naming an assembly that never ran, or an echo before the real line) is
# not counted. Counts are summed over the assemblies. A run "executed no test" when F + P is 0.
#
# EXIT CODES
#   0  KILLED    the red run exited non-zero with failing tests, and the green run passed every test
#                it ran (exit 0, 0 failed, at least one passed).
#   1  SURVIVED  the red run exited 0, ran at least one test and failed none: the tests do not notice
#                the mutation.
#   2  INVALID   anything else: bad arguments, the replacement did not apply exactly once, the red run
#                failed without a failing test (a build error is not a kill), passed with a non-zero
#                exit or failed tests with exit 0 (red-inconsistent), executed no test (none selected,
#                or all skipped), the restore was not clean, or the green run did not pass.
#
# RUNNER. Each phase runs
#   bash <clone>/scripts/test-in-container.sh --repo <clone> --framework F [--dirty] [--project P] [--filter X]
# with --dirty on the red run only, since the mutation is uncommitted in the clone. If
# ASHLAR_MUTATION_RUNNER is set, it runs instead, as a bash command with two arguments appended: the
# clone path and the phase (red or green). --framework, --project and --filter are not passed to it.
# It must print the header and the summary line described under COUNTS, because the verdict is read
# from the counts and not from the exit code alone. It is how tests/scripts/mutation-check.test.sh
# drives this script with no container and no dotnet, and every summary line it produces ends in
# runner=override, so a stubbed verdict never reads as a real one.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"

usage() {
  awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "${SELF}"
}

ID=""
FILE=""
OLD=""
NEW=""
OLD_FILE=""
NEW_FILE=""
HAVE_OLD=0
HAVE_NEW=0
REF="HEAD"
FILTER=""
FRAMEWORK="net8.0"
PROJECT=""
REPO=""
WORK=""
KEEP=0
OVERRIDE="${ASHLAR_MUTATION_RUNNER:-}"

SHA="unresolved"
RED="not-run"
GREEN="not-run"
TOTALS=""
OWN_WORK=0
SRC_DIRTY=0

summary() {  # verdict [reason]
  local line="mutation ${ID:-unnamed}: $1 red=${RED} green=${GREEN} ref=${SHA}"
  if [[ -n "${2:-}" ]]; then line+=" reason=$2"; fi
  if [[ -n "${TOTALS}" ]]; then line+=" totals=${TOTALS}"; fi
  if [[ -n "${OVERRIDE}" ]]; then line+=" runner=override"; fi
  if [[ "${OWN_WORK}" -eq 1 && "${KEEP}" -eq 1 ]]; then echo "== work dir kept: ${WORK} =="; fi
  echo "${line}"
}

invalid() {  # reason message
  echo "mutation-check: $2" >&2
  summary INVALID "$1"
  exit 2
}

need_value() {  # option argc
  if [[ "$2" -lt 2 ]]; then invalid bad-args "$1 needs a value (try --help)"; fi
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --file)      need_value "$1" $#; FILE="$2"; shift 2 ;;
    --old)       need_value "$1" $#; OLD="$2"; HAVE_OLD=$((HAVE_OLD + 1)); shift 2 ;;
    --new)       need_value "$1" $#; NEW="$2"; HAVE_NEW=$((HAVE_NEW + 1)); shift 2 ;;
    --old-file)  need_value "$1" $#; OLD_FILE="$2"; HAVE_OLD=$((HAVE_OLD + 1)); shift 2 ;;
    --new-file)  need_value "$1" $#; NEW_FILE="$2"; HAVE_NEW=$((HAVE_NEW + 1)); shift 2 ;;
    --ref)       need_value "$1" $#; REF="$2"; shift 2 ;;
    --filter)    need_value "$1" $#; FILTER="$2"; shift 2 ;;
    --framework) need_value "$1" $#; FRAMEWORK="$2"; shift 2 ;;
    --project)   need_value "$1" $#; PROJECT="$2"; shift 2 ;;
    --id)        need_value "$1" $#; ID="$2"; shift 2 ;;
    --repo)      need_value "$1" $#; REPO="$2"; shift 2 ;;
    --work-dir)  need_value "$1" $#; WORK="$2"; shift 2 ;;
    --keep)      KEEP=1; shift ;;
    -h|--help)   usage; exit 0 ;;
    *) invalid bad-args "unknown argument '$1' (try --help)" ;;
  esac
done

# --- arguments ---------------------------------------------------------------------------------
[[ -n "${FILE}" ]] || invalid bad-args "--file is required (try --help)"
# --file is a literal repo-relative path. `./src//a.cs` names src/a.cs, so it is normalised here,
# once, and every later step (the lookup at REF, the write, the diff check) sees the same spelling.
while [[ "${FILE}" == *//* ]]; do FILE="${FILE//\/\//\/}"; done
while [[ "${FILE}" == ./* ]]; do FILE="${FILE#./}"; done
[[ "${FILE}" != /* ]] || invalid bad-args "--file must be relative to the repository root, got ${FILE}"
case "/${FILE}/" in
  //|*/./*|*/../*) invalid bad-args "--file must name a file with no . or .. components, got '${FILE}'" ;;
esac
[[ "${FILE}" != */ ]] || invalid bad-args "--file must name a file, not a directory, got ${FILE}"
if [[ -z "${ID}" ]]; then
  ID="$(basename "${FILE}")"
  if [[ "${ID}" == ?*.* ]]; then ID="${ID%.*}"; fi  # HighWaterMark.cs -> HighWaterMark; .editorconfig stays
fi
if [[ "${ID}" =~ [[:space:]] || -z "${ID}" ]]; then
  echo "mutation-check: --id must be non-empty with no whitespace, got '${ID}'" >&2
  ID="unnamed"
  invalid bad-args "the summary line is meant to be parsed"
fi
[[ "${HAVE_OLD}" -eq 1 ]] || invalid bad-args "give exactly one of --old / --old-file"
[[ "${HAVE_NEW}" -eq 1 ]] || invalid bad-args "give exactly one of --new / --new-file"
[[ -z "${OLD_FILE}" || -f "${OLD_FILE}" ]] || invalid bad-args "--old-file ${OLD_FILE} is not a file"
[[ -z "${NEW_FILE}" || -f "${NEW_FILE}" ]] || invalid bad-args "--new-file ${NEW_FILE} is not a file"
command -v python3 >/dev/null 2>&1 || invalid bad-args "python3 is required for the exact replacement"

if [[ -z "${REPO}" ]]; then
  REPO="$(git -C "$(dirname "${SELF}")" rev-parse --show-toplevel 2>/dev/null)" \
    || invalid bad-args "cannot find the repository containing ${SELF}; pass --repo"
fi
git -C "${REPO}" rev-parse --git-dir >/dev/null 2>&1 || invalid bad-args "${REPO} is not a git repository"
REPO="$(cd "${REPO}" && pwd)" || invalid bad-args "cannot enter --repo ${REPO}"
SHA="$(git -C "${REPO}" rev-parse --verify --quiet "${REF}^{commit}")" \
  || { SHA="unresolved"; invalid bad-args "--ref ${REF} is not a commit in ${REPO}"; }

# --- work dir ----------------------------------------------------------------------------------
# A user-supplied work dir must be absent or empty. It is removed on exit unless --keep, and removing
# a directory that held something else is not a risk worth taking for a convenience.
# shellcheck disable=SC2317  # reached through the EXIT trap, which shellcheck 0.9.0 does not follow
cleanup() {
  if [[ "${OWN_WORK}" -eq 1 && "${KEEP}" -eq 0 ]]; then
    rm -rf "${WORK}"
  fi
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# Checked before the work dir exists, so a --work-dir inside --repo cannot report the script's own files.
# The commonest protocol slip is a new killing test that was never committed: the clone at HEAD does
# not have it and the check reports SURVIVED. So any uncommitted or untracked change is named, not only
# one in FILE.
if [[ "${REF}" == "HEAD" ]]; then
  SRC_STATUS="$(git -C "${REPO}" status --porcelain --untracked-files=all 2>/dev/null)"
  if [[ -n "${SRC_STATUS}" ]]; then
    SRC_DIRTY=1
    echo "mutation-check: warning: ${REPO} has uncommitted changes. They are NOT part of ${SHA}," >&2
    echo "               the commit that is mutated and tested, so a new or edited test among them" >&2
    echo "               does not run. Commit first. Not part of the run:" >&2
    head -n 20 <<<"${SRC_STATUS}" | sed 's/^/                 /' >&2
    N_DIRTY="$(wc -l <<<"${SRC_STATUS}")"
    if [[ "${N_DIRTY}" -gt 20 ]]; then echo "                 ... and $((N_DIRTY - 20)) more" >&2; fi
  fi
fi

if [[ -z "${WORK}" ]]; then
  WORK="$(mktemp -d "${TMPDIR:-/tmp}/mutation-check.XXXXXX")" || invalid bad-args "mktemp -d failed"
elif [[ -e "${WORK}" ]]; then
  [[ -d "${WORK}" ]] || invalid bad-args "--work-dir ${WORK} exists and is not a directory"
  [[ -z "$(ls -A "${WORK}")" ]] || invalid bad-args "--work-dir ${WORK} is not empty; refusing to use it"
else
  mkdir -p "${WORK}" || invalid bad-args "cannot create --work-dir ${WORK}"
fi
# Absolute, and never empty: an empty WORK would put the clone at /repo and the texts in /.
WORK_ABS="$(cd "${WORK}" && pwd)" || invalid bad-args "cannot enter --work-dir ${WORK}"
[[ -n "${WORK_ABS}" ]] || invalid bad-args "cannot resolve --work-dir ${WORK}"
WORK="${WORK_ABS}"
OWN_WORK=1
CLONE="${WORK}/repo"
echo "== work dir: ${WORK} =="

# The two texts go through files so that --old and --old-file are one code path, and so that a
# multi-line or trailing-newline text reaches python byte for byte.
if [[ -n "${OLD_FILE}" ]]; then
  cp -- "${OLD_FILE}" "${WORK}/old.txt" || invalid bad-args "cannot copy --old-file ${OLD_FILE} into ${WORK}"
else
  printf '%s' "${OLD}" > "${WORK}/old.txt" || invalid bad-args "cannot write the --old text into ${WORK}"
fi
if [[ -n "${NEW_FILE}" ]]; then
  cp -- "${NEW_FILE}" "${WORK}/new.txt" || invalid bad-args "cannot copy --new-file ${NEW_FILE} into ${WORK}"
else
  printf '%s' "${NEW}" > "${WORK}/new.txt" || invalid bad-args "cannot write the --new text into ${WORK}"
fi

# --- 1. clone at REF ---------------------------------------------------------------------------
git clone -q --no-checkout "${REPO}" "${CLONE}" || invalid clone-failed "git clone ${REPO} failed"
if ! git -C "${CLONE}" cat-file -e "${SHA}^{commit}" 2>/dev/null; then
  git -C "${CLONE}" fetch -q "${REPO}" "${SHA}" 2>/dev/null \
    || invalid clone-failed "${SHA} is not reachable in a clone of ${REPO}"
fi
git -C "${CLONE}" checkout -q --detach "${SHA}" || invalid clone-failed "git checkout ${SHA} failed"
echo "== mutation ${ID} at ${SHA} =="

# FILE must be a regular file at SHA, looked up literally (`SHA:path` and --literal-pathspecs never
# glob). A symlink is refused before anything is written: python's open() would write through it, to
# wherever it points, and the restore would never undo that.
TYPE="$(git -C "${CLONE}" cat-file -t "${SHA}:${FILE}" 2>/dev/null)" \
  || invalid apply-failed "${FILE} is not a tracked file at ${SHA} (--file is a literal path, not a pattern)"
[[ "${TYPE}" == "blob" ]] || invalid apply-failed "${FILE} is a ${TYPE} at ${SHA}, not a file"
ENTRY="$(git -C "${CLONE}" --literal-pathspecs ls-tree "${SHA}" -- "${FILE}")"
MODE="${ENTRY%% *}"
case "${MODE}" in
  100644|100755) ;;
  120000) invalid apply-failed "${FILE} is a symlink at ${SHA}; refusing to write through it" ;;
  *) invalid apply-failed "${FILE} has mode '${MODE}' at ${SHA}; only a regular file can be mutated" ;;
esac

# --- 2. apply exactly one replacement ----------------------------------------------------------
# Bytes in, bytes out: no newline translation and no encoding round-trip, so the only change to the
# file is the one replacement. Occurrences are counted overlapping, so `aa` in `aaa` is two, not one.
python3 - "${CLONE}/${FILE}" "${WORK}/old.txt" "${WORK}/new.txt" "${FILE}" <<'PY' || invalid apply-failed "the replacement was refused (see above)"
import sys

path, old_path, new_path, label = sys.argv[1:5]
with open(path, "rb") as f:
    src = f.read()
with open(old_path, "rb") as f:
    old = f.read()
with open(new_path, "rb") as f:
    new = f.read()

if not old:
    sys.exit("mutation-check: the text to replace is empty")
if old == new:
    sys.exit("mutation-check: --old and --new are identical; that mutation changes nothing")

hits = []
i = src.find(old)
while i != -1:
    hits.append(i)
    i = src.find(old, i + 1)

if len(hits) != 1:
    lines = ", ".join(str(src.count(b"\n", 0, p) + 1) for p in hits[:10])
    msg = f"mutation-check: expected exactly one occurrence in {label}, found {len(hits)}"
    if hits:
        msg += f" (lines {lines}); make --old longer so it names one place"
    elif b"\r\n" in src and b"\n" in old and b"\r\n" not in old:
        msg += f"; {label} has CRLF line endings and the text to replace has LF"
    sys.exit(msg)

p = hits[0]
line = src.count(b"\n", 0, p) + 1
with open(path, "wb") as f:
    f.write(src[:p] + new + src[p + len(old):])
print(f"replaced 1 occurrence in {label} at line {line}")
PY

# --- 3. proof that it applied ------------------------------------------------------------------
echo "== proof the mutation applied =="
git -C "${CLONE}" diff --stat
git -C "${CLONE}" diff -U0
CHANGED=()
while IFS= read -r -d '' f; do CHANGED+=("${f}"); done < <(git -C "${CLONE}" diff --name-only -z)
if [[ "${#CHANGED[@]}" -ne 1 || "${CHANGED[0]}" != "${FILE}" ]]; then
  invalid apply-failed "expected the diff to touch exactly ${FILE}; it touches: ${CHANGED[*]:-nothing}"
fi

# --- the runner --------------------------------------------------------------------------------
RC=0
run_phase() {  # phase
  local phase="$1" log="${WORK}/$1.log" args
  if [[ -n "${OVERRIDE}" ]]; then
    echo "== runner: ASHLAR_MUTATION_RUNNER=${OVERRIDE} <clone> ${phase} (an override, not the container; --framework, --project and --filter are not passed) =="
    bash -c "${OVERRIDE} \"\$@\"" mutation-runner "${CLONE}" "${phase}" </dev/null 2>&1 | tee "${log}"
    RC=${PIPESTATUS[0]}
  else
    args=(--repo "${CLONE}" --framework "${FRAMEWORK}")
    if [[ "${phase}" == "red" ]]; then args+=(--dirty); fi
    if [[ -n "${PROJECT}" ]]; then args+=(--project "${PROJECT}"); fi
    if [[ -n "${FILTER}" ]]; then args+=(--filter "${FILTER}"); fi
    echo "== runner: bash$(printf ' %q' "${CLONE}/scripts/test-in-container.sh" "${args[@]}") =="
    bash "${CLONE}/scripts/test-in-container.sh" "${args[@]}" </dev/null 2>&1 | tee "${log}"
    RC=${PIPESTATUS[0]}
  fi
}

# parse_log counts LOG  -> "<assemblies> <failed> <passed> <skipped> <total>" (see COUNTS above)
# parse_log failing LOG -> the names of the failing tests, one per line
parse_log() {
  python3 - "$1" "$2" <<'PY'
import re, sys

mode, log = sys.argv[1:3]
ansi = re.compile(r"\x1b\[[0-9;?]*[A-Za-z]")
header = re.compile(r"Test run for (.+?\.dll) \(.*\)\s*")
summary = re.compile(
    r"\w+!\s+-\s+Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)\b"
    r".*\s-\s+([^\s/\\]+\.dll)\s+\(([^()\s]+)\)\s*")
failed = re.compile(r"\s+Failed (\S.*?) \[[^\]]*\]\s*")

announced = set()
last = {}
names = []
with open(log, encoding="utf-8", errors="replace") as fh:
    for raw in fh:
        line = ansi.sub("", raw).rstrip("\r\n")
        m = header.fullmatch(line)
        if m:
            announced.add(re.split(r"[\\/]", m[1])[-1])
            continue
        m = summary.fullmatch(line)
        if m:
            if m[5] in announced:
                last[(m[5], m[6])] = [int(m[k]) for k in (1, 2, 3, 4)]
            continue
        m = failed.fullmatch(line)
        if m and m[1] not in names:
            names.append(m[1])

if mode == "counts":
    sums = [sum(v[k] for v in last.values()) for k in range(4)]
    print(len(last), *sums)
else:
    for n in names[:50]:
        print(n)
    if len(names) > 50:
        print(f"... and {len(names) - 50} more")
PY
}

# --- 4. RED ------------------------------------------------------------------------------------
echo "== RED run (mutated) =="
run_phase red
RED_RC="${RC}"
read -r R_LINES R_FAILED R_PASSED R_SKIPPED R_TOTAL < <(parse_log counts "${WORK}/red.log")
echo "== red: exit ${RED_RC}, failed ${R_FAILED}, passed ${R_PASSED}, skipped ${R_SKIPPED}, total ${R_TOTAL} =="
if [[ "${R_LINES}" -eq 0 ]]; then
  RED="exit:${RED_RC}"
  if [[ "${RED_RC}" -eq 0 ]]; then
    invalid red-no-tests "the red run printed no dotnet test summary and exited 0: the filter selected no test, which is not a survivor"
  fi
  invalid red-no-tests "the red run failed without a dotnet test summary: a build or harness failure, which is not a kill"
fi
RED="failed:${R_FAILED}/${R_TOTAL}"
if [[ $((R_FAILED + R_PASSED)) -eq 0 ]]; then
  invalid red-no-tests "the red run executed no test (total ${R_TOTAL}, skipped ${R_SKIPPED}); nothing was tested, which is not a survivor"
fi
if [[ "${R_FAILED}" -gt 0 ]]; then
  echo "== red: failing tests =="
  parse_log failing "${WORK}/red.log" | sed 's/^/  /'
fi
if [[ "${RED_RC}" -eq 0 && "${R_FAILED}" -eq 0 ]]; then
  VERDICT=SURVIVED
elif [[ "${RED_RC}" -ne 0 && "${R_FAILED}" -gt 0 ]]; then
  VERDICT=KILLED
else
  invalid red-inconsistent "the red run exited ${RED_RC} with ${R_FAILED} failed test(s); the exit code and the counts disagree, so neither is a verdict"
fi

# --- 5. restore, 6. prove the tree is clean ----------------------------------------------------
echo "== restore =="
git -C "${CLONE}" checkout -- "${FILE}" || invalid restore-failed "git checkout -- ${FILE} failed"
PORCELAIN="$(git -C "${CLONE}" status --porcelain)"
echo "porcelain=[${PORCELAIN}]"
[[ -z "${PORCELAIN}" ]] || invalid restore-not-clean "the clone is not clean after the restore; a green run now would not test ${SHA}"

if [[ "${VERDICT}" == "SURVIVED" ]]; then
  echo "mutation-check: the tests passed with the mutation applied; the green run is skipped, it cannot change that." >&2
  if [[ "${SRC_DIRTY}" -eq 1 ]]; then
    echo "mutation-check: reminder: ${REPO} had uncommitted changes that were not tested (listed above)." >&2
  fi
  summary SURVIVED
  exit 1
fi

# --- 7. GREEN ----------------------------------------------------------------------------------
echo "== GREEN run (restored) =="
run_phase green
GREEN_RC="${RC}"
read -r G_LINES G_FAILED G_PASSED G_SKIPPED G_TOTAL < <(parse_log counts "${WORK}/green.log")
echo "== green: exit ${GREEN_RC}, failed ${G_FAILED}, passed ${G_PASSED}, skipped ${G_SKIPPED}, total ${G_TOTAL} =="
if [[ "${G_LINES}" -eq 0 ]]; then
  GREEN="exit:${GREEN_RC}"
  invalid green-not-passing "the green run printed no dotnet test summary; the red failures cannot be attributed to the mutation"
fi
GREEN="passed:${G_PASSED}/${G_TOTAL}"
if [[ "${G_TOTAL}" -ne "${R_TOTAL}" ]]; then
  TOTALS="${R_TOTAL}/${G_TOTAL}"
  echo "mutation-check: note: red ran ${R_TOTAL} test(s) and green ran ${G_TOTAL}; the mutation changed what was discovered." >&2
fi
if [[ "${GREEN_RC}" -ne 0 || "${G_FAILED}" -ne 0 || "${G_PASSED}" -eq 0 ]]; then
  invalid green-not-passing "the green run did not pass (exit ${GREEN_RC}, ${G_FAILED} failed and ${G_PASSED} passed of ${G_TOTAL}); the red failures cannot be attributed to the mutation"
fi
summary KILLED
exit 0
