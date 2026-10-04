#!/usr/bin/env bash
# Tests .claude/hooks/session-start.sh, the SessionStart hook that sets docker up in a Claude Code
# cloud session (dockerd, a session-local docker wrapper for the agent proxy, the devtest image).
#
# WHY THIS EXISTS. The hook only does anything when CLAUDE_CODE_REMOTE=true, so no CI lane ever
# runs it. An edit that broke it would break docker, and with it every container build and test, in
# every cloud session, while all five required checks stayed green. These assertions run the REAL
# functions, sourced from the hook (its main guard runs nothing when it is sourced), and the REAL
# hook as a script, against fakes: a docker that records its argv, a dockerd that comes up, exits or
# hangs on request, and pid files under a temporary directory.
#
# It never talks to a docker daemon, never reads or writes /var/run, and kills only the processes it
# started itself. Every run of the hook as a script gets a clean environment (env -i), so a cloud
# session's own CLAUDE_CODE_REMOTE, proxy or CLAUDE_ENV_FILE cannot leak in.
#
# Run:  bash tests/scripts/session-start-hook.test.sh [path-to-hook]
# The hook defaults to the repository's copy. Pure bash, coreutils, procps and python3: no network,
# no docker, no dotnet.

# Assertions are strings that check() evals, so their $ stay unexpanded on purpose (SC2016). The
# variables and functions set here are read by the sourced hook, which shellcheck cannot see
# (SC2034, SC2317). CLAUDE_ENV_FILE is sourced from a path made at run time (SC1090), and PATH=/x
# is a deliberate throwaway search path inside a subshell (SC2123).
# shellcheck disable=SC2016,SC2034,SC2317,SC1090,SC2123

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HOOK="${1:-${ROOT}/.claude/hooks/session-start.sh}"
SETTINGS="${ROOT}/.claude/settings.json"

EXPECTED_ASSERTIONS=123

PASS=0
FAIL=0
check() {
  if eval "$2"; then
    PASS=$((PASS + 1))
    echo "  ok   - $1"
  else
    FAIL=$((FAIL + 1))
    echo "  FAIL - $1"
    echo "         [$2]"
  fi
}

if [[ ! -f "${HOOK}" ]]; then
  echo "FAIL - no hook at ${HOOK}"
  exit 1
fi

# Whatever session runs this test, the hook must see only what each case gives it.
unset CLAUDE_CODE_REMOTE CLAUDE_ENV_FILE CLAUDE_PROJECT_DIR HTTPS_PROXY https_proxy HTTP_PROXY \
  http_proxy NO_PROXY no_proxy ALL_PROXY all_proxy
while IFS= read -r v; do
  unset "$v"
done < <(compgen -e | grep '^ASHLAR_')

T="$(mktemp -d)"
PIDS=()
cleanup() {
  local p
  for p in "${PIDS[@]}"; do
    kill "$p" 2>/dev/null
  done
  rm -rf "$T"
}
trap cleanup EXIT

BASH_BIN="$(type -P bash)"

# A PATH directory of the system tools the hook and scripts/ensure-devtest-image.sh use, and NO
# docker or dockerd: the host's real ones (a GitHub runner has both) must never be found.
SYS="$T/sys"
mkdir -p "$SYS"
for c in bash env cat chmod date dirname grep head id mkdir mktemp mv rm sleep tail timeout tr uname setsid sed touch; do
  if p="$(type -P "$c")"; then
    ln -s "$p" "$SYS/$c"
  fi
done

# The fake docker. `info` answers only while $T/up exists, printing FAKE_DAEMON_PROXY (the
# daemon's HTTPS proxy, for `info --format`). `image inspect` succeeds only while $T/has-image
# exists. Every call is logged on one line to $CALLS; anything else prints its argv, one per line.
FAKE="$T/fake"
CALLS="$T/docker-calls.log"
mkdir -p "$FAKE"
cat >"$FAKE/docker" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$*" >>"$CALLS"
case "\${1:-}" in
  info)
    [[ -e "$T/up" ]] || exit 1
    printf '%s\n' "\${FAKE_DAEMON_PROXY:-}"
    exit 0
    ;;
  image)
    if [[ "\${2:-}" == inspect ]]; then
      [[ -e "$T/has-image" ]]
      exit
    fi
    ;;
esac
printf '%s\n' "\$@"
EOF
chmod +x "$FAKE/docker"

# A logging timeout in front of the real one, so the limit each call was given can be read back.
REAL_TIMEOUT="$(type -P timeout)"
cat >"$FAKE/timeout" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$1" >>"$T/timeout.log"
exec "$REAL_TIMEOUT" "\$@"
EOF
chmod +x "$FAKE/timeout"

make_dockerd() { # $1 = up | exit | hang
  cat >"$FAKE/dockerd" <<EOF
#!/usr/bin/env bash
echo "fake dockerd starting, behaviour=$1, HTTPS_PROXY=\${HTTPS_PROXY:-unset}"
case "$1" in
  up)   sleep 2; touch "$T/up"; exec sleep 30 ;;
  exit) echo "failed to start daemon: fake error"; exit 1 ;;
  hang) exec sleep 30 ;;
esac
EOF
  chmod +x "$FAKE/dockerd"
}

# shellcheck source=/dev/null
source "${HOOK}"
set +e # the hook sets -euo pipefail; every status here is checked explicitly

export PATH="$FAKE:$PATH"
REAL_DOCKER="$FAKE/docker"
DOCKERD_LOG="$T/dockerd.log"
BUILD_LOG="$T/build.log"
mkdir -p "$T/run"
# Never let a test reach the real /var/run pid files.
DOCKERD_PIDFILE="$T/run/docker.pid"
DAEMON_PIDFILES=("$T/run/docker.pid:dockerd" "$T/run/containerd.pid:containerd")

# =================================================================================================
echo "== settings.json registers the hook so that a path with a space still launches it"
CMD="$(python3 - "$SETTINGS" <<'PY'
import json, sys
s = json.load(open(sys.argv[1]))
for group in s.get("hooks", {}).get("SessionStart", []):
    for h in group.get("hooks", []):
        if h.get("type") == "command" and "/.claude/hooks/session-start.sh" in h.get("command", ""):
            print(h["command"])
            print(h.get("timeout", 0))
PY
)"
REG_CMD="$(sed -n 1p <<<"$CMD")"
REG_TIMEOUT="$(sed -n 2p <<<"$CMD")"
check "a SessionStart command hook runs .claude/hooks/session-start.sh" '[[ -n "$REG_CMD" ]]'
check "the hook's budget (${HOOK_BUDGET}s) is under its registered timeout (${REG_TIMEOUT}s)" \
  '[[ "$REG_TIMEOUT" =~ ^[0-9]+$ ]] && (( HOOK_BUDGET < REG_TIMEOUT ))'
check "the committed hook is executable (the command runs it directly)" '[[ -x "${ROOT}/.claude/hooks/session-start.sh" ]]'
SPACED="$T/a checkout"
mkdir -p "$SPACED/.claude/hooks"
cp "${HOOK}" "$SPACED/.claude/hooks/session-start.sh"
chmod +x "$SPACED/.claude/hooks/session-start.sh"
out="$(env -i PATH="$SYS" CLAUDE_PROJECT_DIR="$SPACED" "$BASH_BIN" -c "$REG_CMD" 2>&1)"
rc=$?
check "the registered command launches the hook from a checkout path with a space (rc=$rc)" '[[ $rc == 0 && -z "$out" ]]'

# =================================================================================================
echo "== main: nothing at all happens unless CLAUDE_CODE_REMOTE is exactly true"
touch "$T/up"
for remote in unset TRUE 1; do
  : >"$CALLS"
  if [[ "$remote" == unset ]]; then
    remote_env=()
  else
    remote_env=(CLAUDE_CODE_REMOTE="$remote")
  fi
  out="$(env -i PATH="$FAKE:$SYS" HOME="$T/home-gate" ASHLAR_SESSION_DIR="$T/gate-session" \
    CLAUDE_ENV_FILE="$T/gate-env" HTTPS_PROXY=http://127.0.0.1:5555 "${remote_env[@]}" \
    "$BASH_BIN" "${HOOK}" 2>&1)"
  rc=$?
  check "CLAUDE_CODE_REMOTE=${remote}: exit 0, no output, no docker call, nothing written" \
    '[[ $rc == 0 && -z "$out" && ! -s "$CALLS" && ! -e "$T/gate-session" && ! -e "$T/gate-env" ]]'
done
out="$(env -i PATH="$SYS" HOME="$T/home-gate" CLAUDE_CODE_REMOTE=true "$BASH_BIN" "${HOOK}" 2>&1)"
rc=$?
check "remote, but no docker on PATH: exit 0 and one line that says so" \
  '[[ $rc == 0 && "$out" == "session-start: no docker CLI on PATH,"* && "$(wc -l <<<"$out")" == 1 ]]'

# =================================================================================================
echo "== main, end to end with fakes, run twice: wrapper, PATH line, build through the wrapper"
SESS="$T/session dir" # a space, so every quoting step on the way is exercised
ENVF="$T/claude-env.sh"
run_main() { # $1 = PATH for the run
  env -i PATH="$1" HOME="$T/home" CLAUDE_CODE_REMOTE=true CLAUDE_PROJECT_DIR="$ROOT" \
    CLAUDE_ENV_FILE="$ENVF" ASHLAR_SESSION_DIR="$SESS" ASHLAR_SESSION_DOCKERD_LOG="$T/main-dockerd.log" \
    ASHLAR_SESSION_BUILD_LOG="$T/main-build.log" HTTPS_PROXY=http://127.0.0.1:5555 \
    FAKE_DAEMON_PROXY=http://127.0.0.1:5555 "$BASH_BIN" "${HOOK}"
}
touch "$T/up"
rm -f "$T/has-image"
: >"$CALLS"
out1="$(run_main "$FAKE:$SYS" 2>"$T/main1.err")"
rc=$?
printf '%s\n' "$out1" | sed 's/^/    | /'
check "run 1: exit 0" '[[ $rc == 0 ]]'
check "run 1: exactly three status lines on stdout, nothing on stderr" \
  '[[ "$(grep -c "^session-start: " <<<"$out1")" == 3 && "$(wc -l <<<"$out1")" == 3 && ! -s "$T/main1.err" ]]'
check "run 1: dockerd was already up, and its proxy matches (no warning)" \
  '[[ "$(sed -n 1p <<<"$out1")" == "session-start: dockerd: running (was already up)" ]]'
check "run 1: wrapper written, wrapping the fake docker, put first on PATH" \
  '[[ "$(sed -n 2p <<<"$out1")" == "session-start: docker wrapper: written to $SESS/bin/docker (wraps $FAKE/docker); first on PATH via CLAUDE_ENV_FILE" ]]'
check "run 1: the wrapper is executable and parses" '[[ -x "$SESS/bin/docker" ]] && bash -n "$SESS/bin/docker"'
check "run 1: CLAUDE_ENV_FILE holds exactly one line" '[[ "$(wc -l <"$ENVF")" == 1 ]]'
check "run 1: the image build went through the wrapper (host network, proxy build arg)" \
  'grep -qxF "build --network host --build-arg HTTPS_PROXY -t ashlar-devtest:local -f ${ROOT}/.docker/Dockerfile.devtest ${ROOT}/.docker" "$CALLS"'
check "run 1: status says the image was built" \
  '[[ "$(sed -n 3p <<<"$out1")" == "session-start: devtest image: ashlar-devtest:local built in "*"s (log $T/main-build.log)" ]]'

# The next session start: Claude Code has sourced CLAUDE_ENV_FILE, so the wrapper is first on PATH.
PATH2="$(PATH="$FAKE:$SYS" && source "$ENVF" && printf '%s' "$PATH")"
check "sourcing CLAUDE_ENV_FILE puts the wrapper directory first on PATH" '[[ "$PATH2" == "$SESS/bin:$FAKE:$SYS" ]]'
touch "$T/has-image"
out2="$(run_main "$PATH2" 2>"$T/main2.err")"
rc=$?
printf '%s\n' "$out2" | sed 's/^/    | /'
check "run 2: exit 0, three lines, nothing on stderr" \
  '[[ $rc == 0 && "$(wc -l <<<"$out2")" == 3 && ! -s "$T/main2.err" ]]'
check "run 2: still wraps the fake docker, not itself, and the PATH line is already there" \
  '[[ "$(sed -n 2p <<<"$out2")" == "session-start: docker wrapper: written to $SESS/bin/docker (wraps $FAKE/docker); already on PATH via CLAUDE_ENV_FILE" ]]'
check "run 2: the rewritten wrapper calls the fake docker" "grep -qxF \"real_docker=\$(printf '%q' \"\$FAKE/docker\")\" \"\$SESS/bin/docker\""
check "run 2: CLAUDE_ENV_FILE still holds exactly one line" '[[ "$(wc -l <"$ENVF")" == 1 ]]'
check "run 2: status says the image is present" \
  '[[ "$(sed -n 3p <<<"$out2")" == "session-start: devtest image: ashlar-devtest:local present" ]]'
PATH3="$(PATH=/x && source "$ENVF" && source "$ENVF" && printf '%s' "$PATH")"
check "sourcing CLAUDE_ENV_FILE twice does not stack the directory" '[[ "$PATH3" == "$SESS/bin:/x" ]]'

# =================================================================================================
echo "== register_path"
WRAPPER_DIR="$T/reg dir/bin"
WRAPPER_STATUS="written"
CLAUDE_ENV_FILE="$T/reg-env.sh"
register_path
register_path
check "called twice: one line" '[[ "$(wc -l <"$CLAUDE_ENV_FILE")" == 1 ]]'
check "called twice: first appends, second finds it" \
  '[[ "$WRAPPER_STATUS" == "written; first on PATH via CLAUDE_ENV_FILE; already on PATH via CLAUDE_ENV_FILE" ]]'
WRAPPER_STATUS="written"
CLAUDE_ENV_FILE="$T/no-such-dir/env.sh"
register_path 2>/dev/null
check "an env file that cannot be written is reported, not fatal" '[[ "$WRAPPER_STATUS" == "written; could NOT write $CLAUDE_ENV_FILE"* ]]'
WRAPPER_STATUS="written"
unset CLAUDE_ENV_FILE
register_path
check "no CLAUDE_ENV_FILE: says to put the directory on PATH by hand" '[[ "$WRAPPER_STATUS" == "written; CLAUDE_ENV_FILE is not set"* ]]'

# =================================================================================================
echo "== dockerd_running without pgrep: the pid file must name a process called dockerd"
NOPG="$T/nopgrep"
mkdir -p "$NOPG"
ln -s "$(type -P tr)" "$NOPG/tr"
ln -s "$(type -P cat)" "$NOPG/cat"
fallback() { (PATH="$NOPG" && dockerd_running); }
check "the fallback really is taken (no pgrep on that PATH)" '! (PATH="$NOPG" && command -v pgrep >/dev/null)'
echo "$$" >"$DOCKERD_PIDFILE"
check "a live pid that is NOT dockerd (this shell) does not count" '! fallback'
cp "$(type -P sleep)" "$T/dockerd"
"$T/dockerd" 30 &
named_pid=$!
PIDS+=("$named_pid")
echo "$named_pid" >"$DOCKERD_PIDFILE"
check "a live process named dockerd counts" 'fallback'
kill "$named_pid" 2>/dev/null
wait "$named_pid" 2>/dev/null
check "the same pid once it has exited does not count" '! fallback'
rm -f "$DOCKERD_PIDFILE"
check "no pid file does not count" '! fallback'

# =================================================================================================
# From here on the hook believes it runs as root, so it starts the fake dockerd itself rather than
# through sudo, whatever user runs this test.
id() { if [[ "${1:-}" == -u ]]; then echo 0; else command id "$@"; fi; }
started_pid() { sed -n 's/^started (pid \([0-9]*\),.*/\1/p' <<<"$DOCKERD_STATUS"; }

echo "== ensure_dockerd A: daemon down, no dockerd process: clear stale pid files, start it, wait"
dockerd_running() { return 1; }
# docker.pid names a live process that is NOT dockerd (this shell): stale, must go.
echo "$$" >"$T/run/docker.pid"
# containerd.pid names a live process whose comm IS containerd: genuine, must stay.
cp "$(type -P sleep)" "$T/containerd"
"$T/containerd" 30 &
cpid=$!
PIDS+=("$cpid")
echo "$cpid" >"$T/run/containerd.pid"
make_dockerd up
rm -f "$T/up"
DOCKERD_WAIT=10
DOCKERD_STATUS=""
export HTTPS_PROXY=http://127.0.0.1:5555
t0=$SECONDS
ensure_dockerd
rc=$?
dt=$((SECONDS - t0))
unset HTTPS_PROXY
pid="$(started_pid)"
[[ -n "$pid" ]] && PIDS+=("$pid")
echo "    status: $DOCKERD_STATUS  (rc=$rc, ${dt}s)"
check "stale docker.pid (pid of a non-dockerd process) removed" '[[ ! -e "$T/run/docker.pid" ]]'
check "removal logged" 'grep -q "removed stale $T/run/docker.pid (pid $$ is not a running dockerd)" "$DOCKERD_LOG"'
check "genuine containerd.pid kept" '[[ "$(cat "$T/run/containerd.pid")" == "$cpid" ]]'
check "status reports when it answered" '[[ "$DOCKERD_STATUS" == *"; answering after "*s ]]'
check "returns 0 once docker info answers" '[[ $rc == 0 ]]'
check "waited for the daemon (>=2s), not past it (<=6s)" '(( dt >= 2 && dt <= 6 ))'
check "status names the started pid and the log" '[[ -n "$pid" && "$DOCKERD_STATUS" == *"log $DOCKERD_LOG"* ]]'
check "the started process is the fake dockerd (resolved from PATH)" 'tr "\0" " " </proc/$pid/cmdline | grep -q "sleep 30\|dockerd"'
check "daemon stdin is /dev/null" '[[ "$(readlink /proc/$pid/fd/0)" == /dev/null ]]'
check "daemon stdout is the log file, not the hook pipe" '[[ "$(readlink /proc/$pid/fd/1)" == "$DOCKERD_LOG" ]]'
check "daemon stderr is the log file" '[[ "$(readlink /proc/$pid/fd/2)" == "$DOCKERD_LOG" ]]'
check "daemon runs in its own session (setsid)" '[[ "$(ps -o sid= -p $pid | tr -d " ")" == "$pid" ]]'
check "daemon inherited the session proxy env" 'grep -q "HTTPS_PROXY=http://127.0.0.1:5555" "$DOCKERD_LOG"'
check "log carries the start banner" 'grep -q "session-start: starting $FAKE/dockerd" "$DOCKERD_LOG"'
kill "$pid" "$cpid" 2>/dev/null
wait "$cpid" 2>/dev/null
echo 999999 >"$T/run/containerd.pid"
clear_stale_pidfiles
check "pid file naming a dead pid removed" '[[ ! -e "$T/run/containerd.pid" ]]'

echo "== ensure_dockerd B: daemon already answers: nothing started"
touch "$T/up"
make_dockerd exit
: >"$DOCKERD_LOG"
DOCKERD_STATUS=""
ensure_dockerd
rc=$?
check "returns 0" '[[ $rc == 0 ]]'
check "status says already up" '[[ "$DOCKERD_STATUS" == "running (was already up)" ]]'
check "no dockerd was launched (log empty)" '[[ ! -s "$DOCKERD_LOG" ]]'

echo "== ensure_dockerd C: dockerd exits during startup: fail fast with the log tail"
rm -f "$T/up"
make_dockerd exit
DOCKERD_WAIT=30
DOCKERD_STATUS=""
t0=$SECONDS
ensure_dockerd
rc=$?
dt=$((SECONDS - t0))
check "returns 1" '[[ $rc == 1 ]]'
check "fails fast, not after the 30s wait" '(( dt <= 5 ))'
check "status carries the daemon error" '[[ "$DOCKERD_STATUS" == *"exited during startup"*"fake error"* ]]'

echo "== ensure_dockerd D: dockerd starts but never answers: give up after DOCKERD_WAIT"
rm -f "$T/up"
make_dockerd hang
DOCKERD_WAIT=3
DOCKERD_STATUS=""
t0=$SECONDS
ensure_dockerd
rc=$?
dt=$((SECONDS - t0))
pid="$(started_pid)"
[[ -n "$pid" ]] && PIDS+=("$pid")
check "returns 1" '[[ $rc == 1 ]]'
check "gave up after ~3s" '(( dt >= 3 && dt <= 6 ))'
check "status says not answering" '[[ "$DOCKERD_STATUS" == *"NOT answering after 3s"* ]]'
[[ -n "$pid" ]] && kill "$pid" 2>/dev/null

echo "== ensure_dockerd E: a dockerd process exists but is not answering yet: wait, start nothing"
dockerd_running() { return 0; }
rm -f "$T/up"
make_dockerd up
: >"$DOCKERD_LOG"
DOCKERD_WAIT=10
DOCKERD_STATUS=""
(sleep 2 && touch "$T/up") &
PIDS+=("$!")
ensure_dockerd
rc=$?
check "returns 0 once it answers" '[[ $rc == 0 ]]'
check "launched nothing (log empty)" '[[ ! -s "$DOCKERD_LOG" ]]'
check "status says it was already starting and then answered" '[[ "$DOCKERD_STATUS" == "a dockerd process was already starting; answering after "*s ]]'
# Not answering this time, so the call reaches the point where a missing dockerd would clear them.
echo "$$" >"$T/run/docker.pid"
rm -f "$T/up"
DOCKERD_WAIT=1
DOCKERD_STATUS=""
ensure_dockerd
rc=$?
check "pid files untouched when a dockerd process exists, even one that does not answer" \
  '[[ $rc == 1 && "$DOCKERD_STATUS" == "a dockerd process was already starting; NOT answering"* && -e "$T/run/docker.pid" ]]'

echo "== ensure_dockerd F: not root and no passwordless sudo: refuse, start nothing"
dockerd_running() { return 1; }
id() { if [[ "${1:-}" == -u ]]; then echo 1000; else command id "$@"; fi; }
sudo() { return 1; }
rm -f "$T/up"
: >"$DOCKERD_LOG"
DOCKERD_STATUS=""
ensure_dockerd
rc=$?
check "returns 1 with a status that says why" '[[ $rc == 1 && "$DOCKERD_STATUS" == "NOT running, and starting it needs root (no passwordless sudo)" ]]'
check "launched nothing" '[[ ! -s "$DOCKERD_LOG" ]]'
unset -f sudo
id() { if [[ "${1:-}" == -u ]]; then echo 0; else command id "$@"; fi; }

echo "== ensure_dockerd G: no dockerd binary to start"
DOCKERD_STATUS=""
status_g="$(PATH="$SYS" && ensure_dockerd && echo "rc=0"; echo "$DOCKERD_STATUS")"
check "reports that there is nothing to start" '[[ "$status_g" == "NOT running, and there is no dockerd binary on PATH to start" ]]'

# =================================================================================================
echo "== is_loopback_proxy"
for u in http://127.0.0.1:46419 http://127.0.0.1:46419/ 127.0.0.1:8080 http://localhost:3128 http://user:pw@127.0.0.1:1 'http://[::1]:9' http://127.1.2.3; do
  check "loopback:     $u" "is_loopback_proxy '$u'"
done
for u in "" http://proxy.corp:3128 http://127.example.com:80 http://10.0.0.1:3128 'http://[::2]:9' http://localhost.evil.com:1; do
  check "not loopback: '$u'" "! is_loopback_proxy '$u'"
done

# =================================================================================================
echo "== the generated wrapper: what it hands the real docker"
touch "$T/up"
WRAPPER_DIR="$T/wrap"
REAL_DOCKER="$FAKE/docker"
write_wrapper
rc=$?
W="$WRAPPER_DIR/docker"
check "write_wrapper returns 0" '[[ $rc == 0 ]]'
check "wrapper is executable and parses" '[[ -x "$W" ]] && bash -n "$W"'
CA="$T/ca.crt"
: >"$CA"
# A clean environment for every call: the loopback proxy, NO_PROXY, and a CA bundle that exists.
run_w() {
  env -i PATH="$FAKE:$SYS" HTTPS_PROXY=http://127.0.0.1:5555 https_proxy=http://127.0.0.1:5555 \
    NO_PROXY=localhost no_proxy=localhost ASHLAR_CONTAINER_CA_BUNDLE="$CA" "$W" "$@" 2>"$T/w.err" | tr '\n' ' '
}
INJECT="-e HTTPS_PROXY -e https_proxy -e NO_PROXY -e no_proxy -v $CA:/etc/ashlar-session/ca-bundle.crt:ro -e SSL_CERT_FILE=/etc/ashlar-session/ca-bundle.crt"
out="$(run_w run --rm img curl x)"
echo "    run -> $out"
check "run: --network host first, then proxy vars by name, CA mount, SSL_CERT_FILE, then caller args" \
  '[[ "$out" == "run --network host $INJECT --rm img curl x " ]]'
check "run: nothing on stderr" '[[ ! -s "$T/w.err" ]]'
out="$(run_w container run img)"
check "container run handled like run" '[[ "$out" == "container run --network host $INJECT img " ]]'
out="$(run_w build -t t -f f ctx)"
check "build: --network host + proxy build args, no CA mount" \
  '[[ "$out" == "build --network host --build-arg HTTPS_PROXY --build-arg https_proxy --build-arg NO_PROXY --build-arg no_proxy -t t -f f ctx " ]]'
out="$(run_w buildx build ctx)"
check "buildx build handled like build" '[[ "$out" == "buildx build --network host --build-arg"*" ctx " ]]'
out="$(run_w run --network bridge img)"
check "run on a caller's non-host network: passed through unchanged, no proxy" '[[ "$out" == "run --network bridge img " ]]'
out="$(run_w run --network=host img)"
check "run on a caller's --network=host: proxy and CA added, no second --network" '[[ "$out" == "run $INJECT --network=host img " ]]'
out="$(run_w run --net host img)"
check "run on a caller's --net host: proxy and CA added, no second --network" '[[ "$out" == "run $INJECT --net host img " ]]'
out="$(run_w build --network none ctx)"
check "build on a caller's non-host network: passed through unchanged" '[[ "$out" == "build --network none ctx " ]]'
out="$(run_w images -q)"
check "other subcommands pass through unchanged" '[[ "$out" == "images -q " ]]'
out="$(env -i PATH="$FAKE:$SYS" "$W" run img | tr '\n' ' ')"
check "no proxy at call time: run passes through unchanged" '[[ "$out" == "run img " ]]'
out="$(env -i PATH="$FAKE:$SYS" HTTPS_PROXY=http://proxy.corp:3128 "$W" run img | tr '\n' ' ')"
check "non-loopback proxy: unchanged" '[[ "$out" == "run img " ]]'
out="$(env -i PATH="$FAKE:$SYS" HTTPS_PROXY=http://127.0.0.1:5555 ASHLAR_CONTAINER_CA_BUNDLE=/nonexistent "$W" run img | tr '\n' ' ')"
check "missing CA bundle: no mount, no SSL_CERT_FILE" '[[ "$out" == "run --network host -e HTTPS_PROXY img " ]]'
out="$(env -i PATH="$FAKE:$SYS" HTTPS_PROXY=http://127.0.0.1:1111 ASHLAR_CONTAINER_CA_BUNDLE=/nonexistent "$W" run img | tr '\n' ' ')"
check "proxy read live: passed by name, so the port at call time is what docker sees" '[[ "$out" == *"-e HTTPS_PROXY "* && "$out" != *1111* ]]'

echo "== the wrapper and published ports: host networking would discard them"
for opts in "-p 8080:80" "-p8080:80" "--publish 80" "--publish=80" "-P" "--publish-all" "-dp 80:80" "-itP" "--rm -e A=1 -p 127.0.0.1:18080:8080"; do
  # shellcheck disable=SC2086 # word-splitting the option string is the point
  out="$(run_w run $opts img cmd)"
  check "run $opts: passed through unchanged" '[[ "$out" == "run $opts img cmd " ]]'
done
run_w run -p 1:1 img >/dev/null
check "run -p: one line on stderr says the container has no route to the proxy" \
  '[[ "$(wc -l <"$T/w.err")" == 1 ]] && grep -q "publishes ports.*no route to the session proxy" "$T/w.err"'

echo "== the wrapper reads run options only up to the image, never the container's command"
out="$(run_w run --rm img dotnet build -p:Configuration=Release)"
check "a -p: in the container's command is not a published port" \
  '[[ "$out" == "run --network host $INJECT --rm img dotnet build -p:Configuration=Release " ]]'
out="$(run_w run -e A=1 --name n -v a:b -w /w img sh -c x --network bridge -P)"
check "a --network and -P in the container's command change nothing" \
  '[[ "$out" == "run --network host $INJECT -e A=1 --name n -v a:b -w /w img sh -c x --network bridge -P " ]]'
out="$(run_w run --entrypoint bash -it img -p 1)"
check "an option's value (--entrypoint bash) is not taken for the image" \
  '[[ "$out" == "run --network host $INJECT --entrypoint bash -it img -p 1 " ]]'
out="$(run_w run -e -p img)"
check "a value that looks like an option (-e -p) is a value" '[[ "$out" == "run --network host $INJECT -e -p img " ]]'

# =================================================================================================
echo "== find_real_docker"
ELF="$T/elf"
SHIM="$T/shim"
mkdir -p "$ELF" "$SHIM"
cp "$(type -P sleep)" "$ELF/docker"
printf '#!/usr/bin/env bash\nexec /usr/bin/docker run --network host "$@"\n' >"$SHIM/docker"
chmod +x "$ELF/docker" "$SHIM/docker"
check "skips the wrapper and a non-ELF shim in front of the real binary" \
  '[[ "$(PATH="$WRAPPER_DIR:$SHIM:$ELF:$SYS" && find_real_docker)" == "$ELF/docker" ]]'
check "with no binary at all, the first docker that is not the wrapper (a script-only docker)" \
  '[[ "$(PATH="$WRAPPER_DIR:$FAKE:$SHIM:$SYS" && find_real_docker)" == "$FAKE/docker" ]]'
check "only the wrapper on PATH: no docker" '! (PATH="$WRAPPER_DIR:$SYS" && find_real_docker >/dev/null)'

# =================================================================================================
echo "== ensure_image"
PROJECT_DIR="$T/proj"
mkdir -p "$PROJECT_DIR/scripts"
cat >"$PROJECT_DIR/scripts/ensure-devtest-image.sh" <<EOF
#!/usr/bin/env bash
printf 'docker=%s\n' "\$(command -v docker)" >>"$T/ensure.log"
case "\${FAKE_ENSURE:-build}" in
  build) docker build -t img . ;;
  fail) exit 7 ;;
  stopped) exit 124 ;;
esac
EOF
WRAPPER_DIR="$T/wrap"
image_run() { # $1 = WRAPPER_STATUS
  WRAPPER_STATUS="$1"
  : >"$T/ensure.log"
  : >"$CALLS"
  : >"$T/timeout.log"
  IMAGE_STATUS=""
  ensure_image
}
HOOK_BUDGET=100000
BUILD_TIMEOUT=840
rm -f "$T/has-image"
export HTTPS_PROXY=http://127.0.0.1:5555
image_run "written to $WRAPPER_DIR/docker"
unset HTTPS_PROXY
check "with the wrapper written, ensure-devtest-image.sh finds the wrapper as docker" '[[ "$(cat "$T/ensure.log")" == "docker=$WRAPPER_DIR/docker" ]]'
check "and its build reaches the real docker with --network host" 'grep -qxF "build --network host --build-arg HTTPS_PROXY -t img ." "$CALLS"'
check "a missing image is reported as built" '[[ "$IMAGE_STATUS" == "ashlar-devtest:local built in "* ]]'
check "image inspect got 15s and the build the smaller of BUILD_TIMEOUT and the budget left" \
  '[[ "$(tr "\n" " " <"$T/timeout.log")" == "15 840 " ]]'
touch "$T/has-image"
image_run "not needed (no loopback HTTPS_PROXY)"
check "without a written wrapper, the build uses docker from PATH" '[[ "$(cat "$T/ensure.log")" == "docker=$FAKE/docker" ]]'
check "a present image is reported as present" '[[ "$IMAGE_STATUS" == "ashlar-devtest:local present" ]]'
HOOK_BUDGET=$((SECONDS + 100))
image_run "not needed"
allowed="$(tail -n 1 "$T/timeout.log")"
check "the build gets what is left of the budget, not BUILD_TIMEOUT (allowed ${allowed}s of 100)" \
  '[[ "$allowed" =~ ^[0-9]+$ ]] && (( allowed >= 98 && allowed <= 100 ))'
check "the build log names the limit" 'grep -q "(allowed ${allowed}s) ===" "$BUILD_LOG"'
HOOK_BUDGET=$((SECONDS + 30))
image_run "not needed"
check "under 60s of budget left: the build is not started, and the status says why" \
  '[[ ! -s "$T/ensure.log" && "$IMAGE_STATUS" == "ashlar-devtest:local not checked: under 60s"* ]]'
HOOK_BUDGET=100000
export FAKE_ENSURE=fail
image_run "not needed"
check "a failed build is reported with its exit code" '[[ "$IMAGE_STATUS" == "ashlar-devtest:local build FAILED (exit 7, "* ]]'
export FAKE_ENSURE=stopped
image_run "not needed"
unset FAKE_ENSURE
check "a build stopped by the timeout is reported as stopped at its limit" '[[ "$IMAGE_STATUS" == "ashlar-devtest:local build STOPPED at its 840s limit"* ]]'
mv "$PROJECT_DIR/scripts/ensure-devtest-image.sh" "$T/ensure.bak"
image_run "not needed"
check "no ensure-devtest-image.sh: skipped, with the path" '[[ "$IMAGE_STATUS" == "skipped: $PROJECT_DIR/scripts/ensure-devtest-image.sh not found" ]]'

# =================================================================================================
echo "== check_daemon_proxy"
touch "$T/up"
WANT="running; WARNING its HTTPS proxy is 'http://127.0.0.1:1' but the session's is 'http://127.0.0.1:5555'"
DOCKERD_STATUS="running"
export FAKE_DAEMON_PROXY=http://127.0.0.1:1
check_daemon_proxy http://127.0.0.1:5555
check "a daemon on another proxy port is warned about" '[[ "$DOCKERD_STATUS" == "$WANT"* ]]'
DOCKERD_STATUS="running"
export FAKE_DAEMON_PROXY=http://127.0.0.1:5555
check_daemon_proxy http://127.0.0.1:5555
unset FAKE_DAEMON_PROXY
check "a daemon on the session's proxy is not" '[[ "$DOCKERD_STATUS" == "running" ]]'

# =================================================================================================
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
