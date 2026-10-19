#!/usr/bin/env bash
# A command passed to scripts/test-in-container.sh must keep its double quotes.
#
# The harness used to interpolate that command into `bash -lc "..."`. The container
# shell (and Git Bash, when it builds a Windows command line for docker.exe) then
# reparses those quotes. A command that contains `"` dies with
# `bash: -c: option requires an argument` (exit 2). GettingStarted's pipeline JSON
# is full of double quotes, so that command cannot go through the harness as one
# argument.
#
# The command is now a separate argument of the container's `bash -lc` and is run
# with `eval "$3"`. These assertions run the real script with `docker` stubbed, so
# putting the command back inside the double-quoted `-lc` string reddens them.
#
# Run:  bash tests/scripts/container-harness-quoting.test.sh
# Pure bash: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="${ROOT}/scripts/test-in-container.sh"

EXPECTED_ASSERTIONS=6

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

if [[ ! -d "${ROOT}/.git" ]]; then
  echo "FAIL — ${ROOT} is not a git clone; test-in-container.sh refuses a worktree."
  exit 1
fi

STUB_DIR="$(mktemp -d)"
ARGFILE="${STUB_DIR}/args"
trap 'rm -rf "${STUB_DIR}"' EXIT

cat > "${STUB_DIR}/docker" << EOF
#!/usr/bin/env bash
if [[ "\${1:-}" == "image" ]]; then
  exit 0
fi
if [[ "\${1:-}" != "run" ]]; then
  echo "stub docker: unexpected \$*" >&2
  exit 99
fi
printf '%s\\0' "\$@" > "${ARGFILE}"
exit 0
EOF
chmod +x "${STUB_DIR}/docker"

COMMAND='printf "%s" "quoted value"'

PATH="${STUB_DIR}:${PATH}" bash "${SCRIPT}" --repo "${ROOT}" -- "${COMMAND}" \
  >"${STUB_DIR}/out" 2>"${STUB_DIR}/err"
HARNESS_RC=$?

if [[ "${HARNESS_RC}" -eq 0 ]]; then
  ok "the harness accepts a command that contains double quotes"
else
  bad "the harness accepts a command that contains double quotes" \
      "exit ${HARNESS_RC}; $(tail -5 "${STUB_DIR}/err" | tr '\n' ' ')"
fi

if [[ ! -f "${ARGFILE}" ]]; then
  bad "docker run received the container command" "stub recorded no arguments"
  echo
  echo "passed: ${PASS}   failed: ${FAIL}"
  exit 1
fi

mapfile -d '' ARGS < "${ARGFILE}"
SCRIPT_TEXT=""
POS_CMD=""
for i in "${!ARGS[@]}"; do
  if [[ "${ARGS[$i]}" == "-lc" ]]; then
    SCRIPT_TEXT="${ARGS[$((i + 1))]:-}"
    POS_CMD="${ARGS[$((i + 5))]:-}"
    break
  fi
done

if [[ "${POS_CMD}" == "${COMMAND}" ]]; then
  ok "the command is its own argument, quotes included"
else
  bad "the command is its own argument, quotes included" \
      "argument was [$(printf '%s' "${POS_CMD}" | head -c 200)]"
fi

if [[ -n "${SCRIPT_TEXT}" && "${SCRIPT_TEXT}" != *"quoted value"* && "${SCRIPT_TEXT}" == *'eval "$3"'* ]]; then
  ok "the -lc script evals \"\$3\" and does not embed the command"
else
  bad "the -lc script evals \"\$3\" and does not embed the command" \
      "embedding the command in bash -lc \"...\" is the quoting bug"
fi

RAN_OUT="$(bash -lc 'set -e; eval "$3"' bash unused unused "${POS_CMD}" 2>"${STUB_DIR}/eval.err")"
RAN_RC=$?
if [[ "${RAN_RC}" -eq 0 && "${RAN_OUT}" == "quoted value" ]]; then
  ok "eval of that argument prints the quoted text"
else
  bad "eval of that argument prints the quoted text" \
      "rc=${RAN_RC} out=[${RAN_OUT}] err=$(tr '\n' ' ' < "${STUB_DIR}/eval.err")"
fi

# A second shape: a JSON document, which is what made the harness unusable for
# GettingStarted's pipeline template. The argument must round-trip unchanged.
JSON_CMD='printf "%s" "{\"templateId\":\"quickstart\"}"'
: > "${ARGFILE}"
PATH="${STUB_DIR}:${PATH}" bash "${SCRIPT}" --repo "${ROOT}" -- "${JSON_CMD}" \
  >"${STUB_DIR}/out2" 2>"${STUB_DIR}/err2"
mapfile -d '' ARGS < "${ARGFILE}"
POS_CMD=""
for i in "${!ARGS[@]}"; do
  if [[ "${ARGS[$i]}" == "-lc" ]]; then
    POS_CMD="${ARGS[$((i + 5))]:-}"
    break
  fi
done
JSON_OUT="$(bash -lc 'set -e; eval "$3"' bash unused unused "${POS_CMD}" 2>"${STUB_DIR}/json.err")"
JSON_RC=$?
if [[ "${JSON_RC}" -eq 0 && "${JSON_OUT}" == '{"templateId":"quickstart"}' ]]; then
  ok "a JSON command keeps its double quotes through the harness"
else
  bad "a JSON command keeps its double quotes through the harness" \
      "rc=${JSON_RC} out=[${JSON_OUT}] arg=[${POS_CMD}]"
fi

# --dirty used to be spliced into the same double-quoted string. It has to stay a
# separate argument too, or a quote in the command and the patch script break each other.
: > "${ARGFILE}"
PATH="${STUB_DIR}:${PATH}" bash "${SCRIPT}" --repo "${ROOT}" --dirty -- "${COMMAND}" \
  >"${STUB_DIR}/out3" 2>"${STUB_DIR}/err3"
mapfile -d '' ARGS < "${ARGFILE}"
PATCH_ARG=""
POS_CMD=""
for i in "${!ARGS[@]}"; do
  if [[ "${ARGS[$i]}" == "-lc" ]]; then
    PATCH_ARG="${ARGS[$((i + 4))]:-}"
    POS_CMD="${ARGS[$((i + 5))]:-}"
    break
  fi
done
if [[ "${POS_CMD}" == "${COMMAND}" && "${PATCH_ARG}" == *"git apply"* && "${PATCH_ARG}" == *"/patch/dirty.patch"* ]]; then
  ok "--dirty still passes the patch script as its own argument"
else
  bad "--dirty still passes the patch script as its own argument" \
      "patch=[${PATCH_ARG}] cmd=[${POS_CMD}]"
fi

echo
echo "passed: ${PASS}   failed: ${FAIL}"

RAN=$((PASS + FAIL))
if [[ "${RAN}" -ne "${EXPECTED_ASSERTIONS}" ]]; then
  echo "FAIL — ran ${RAN} assertions, expected ${EXPECTED_ASSERTIONS}."
  echo "       Either this file stopped early or assertions were added without bumping"
  echo "       EXPECTED_ASSERTIONS. A partial run is not a pass."
  exit 1
fi

[[ "${FAIL}" -eq 0 ]] || exit 1
