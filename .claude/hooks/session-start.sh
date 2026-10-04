#!/usr/bin/env bash
# SessionStart hook: make the devtest container usable in a Claude Code CLOUD session.
#
# CLAUDE.md says to build and test only in the devtest container, through
# scripts/test-in-container.sh. A cloud session cannot do that out of the box, for three reasons,
# and this hook deals with each one:
#
#   1. dockerd is not running after the session's machine is recycled. The hook starts it in the
#      background, logging to /tmp/ashlar-session-dockerd.log, and waits up to 60 s for it to answer,
#      first removing pid files the previous machine left behind if their pid now belongs to some
#      other process. It never stops or restarts a daemon that is already there.
#   2. Containers reach the network only through the session's agent proxy, which listens on
#      loopback (HTTPS_PROXY=http://127.0.0.1:<port>, and the port rotates) and re-terminates TLS
#      with its own CA (/root/.ccr/ca-bundle.crt; see /root/.ccr/README.md). A container on the
#      default bridge network cannot reach the host's loopback and does not trust that CA. So the
#      hook writes a session-local `docker` wrapper, $HOME/.ashlar-session/bin/docker, and puts its
#      directory first on PATH through $CLAUDE_ENV_FILE. On `docker run` the wrapper adds
#      --network host, passes the proxy variables through, and mounts the CA bundle read-only with
#      SSL_CERT_FILE pointing at it; on `docker build` it adds --network host and the proxy build
#      args. It reads the proxy from its own environment at every call, so a rotated port is
#      picked up, and it bakes nothing into any image. A run that publishes ports, or that names a
#      network other than host, is passed through unchanged; the wrapper's own header says why.
#   3. The devtest image may not exist yet. scripts/ensure-devtest-image.sh builds it, through the
#      wrapper, when it is missing. A first build pulls a few GB, which is why the hook's timeout in
#      .claude/settings.json is 900 s. The build gets what is left of an 870 s budget, so the hook
#      reports a slow build and exits before that timeout would kill it.
#
# Synchronous on purpose: a session that starts before dockerd answers would see its first
# container run fail. Idempotent and non-interactive. Outside a cloud session
# (CLAUDE_CODE_REMOTE != true) it does nothing at all. A step that fails is reported and the hook
# still exits 0, because a broken docker setup must not cost the session itself.
#
# Status goes to stdout, which Claude Code adds to the session's context, so it is kept to three
# lines. Build and daemon output go to log files under /tmp.
#
# Overrides:
#   ASHLAR_CONTAINER_CA_BUNDLE    CA bundle the wrapper mounts (default /root/.ccr/ca-bundle.crt)
#   ASHLAR_DEVTEST_IMAGE          image tag, as in scripts/ensure-devtest-image.sh
#   ASHLAR_SESSION_DIR            where the wrapper lives (default $HOME/.ashlar-session)
#   ASHLAR_SESSION_DOCKERD_WAIT   seconds to wait for dockerd (default 60)
#   ASHLAR_SESSION_BUILD_TIMEOUT  most seconds the image build may take (default 840)
#   ASHLAR_SESSION_HOOK_BUDGET    seconds the whole hook may take (default 870, under the 900 s
#                                 timeout in .claude/settings.json). The build is allowed the
#                                 smaller of BUILD_TIMEOUT and what is left of this budget, and is
#                                 not started with less than 60 s left, so a slow build is
#                                 reported rather than killed.
#
# Tested by tests/scripts/session-start-hook.test.sh, with fakes; it never touches a real daemon.
set -euo pipefail

SESSION_DIR="${ASHLAR_SESSION_DIR:-${HOME:-/root}/.ashlar-session}"
WRAPPER_DIR="${SESSION_DIR}/bin"
WRAPPER_MARKER="ashlar-session-docker-wrapper"
DOCKERD_LOG="${ASHLAR_SESSION_DOCKERD_LOG:-/tmp/ashlar-session-dockerd.log}"
BUILD_LOG="${ASHLAR_SESSION_BUILD_LOG:-/tmp/ashlar-session-devtest-build.log}"
DOCKERD_WAIT="${ASHLAR_SESSION_DOCKERD_WAIT:-60}"
BUILD_TIMEOUT="${ASHLAR_SESSION_BUILD_TIMEOUT:-840}"
HOOK_BUDGET="${ASHLAR_SESSION_HOOK_BUDGET:-870}"
DOCKERD_PIDFILE=/var/run/docker.pid

REAL_DOCKER=""
DOCKERD_STATUS=""
WRAPPER_STATUS=""
IMAGE_STATUS=""

say() { printf 'session-start: %s\n' "$*"; }

# Run "$@" under coreutils timeout when there is one; $1 is the limit in seconds.
with_timeout() {
  local limit="$1"
  shift
  if command -v timeout >/dev/null 2>&1; then
    timeout "$limit" "$@"
  else
    "$@"
  fi
}

# True when $1 is a proxy URL whose host is loopback: 127.0.0.0/8, localhost or [::1].
# The wrapper carries a copy of this function (written with `declare -f`), so the hook and the
# wrapper cannot disagree about what counts as loopback.
is_loopback_proxy() {
  local hostport="${1:-}"
  [[ -n "$hostport" ]] || return 1
  hostport="${hostport#*://}"
  hostport="${hostport%%/*}"
  hostport="${hostport##*@}"
  case "$hostport" in
    "[::1]" | "[::1]:"*) return 0 ;;
  esac
  hostport="${hostport%:*}"
  [[ "$hostport" == localhost || "$hostport" =~ ^127\.[0-9]+\.[0-9]+\.[0-9]+$ ]]
}

# The `docker` the wrapper is to call: the first ELF binary named docker on PATH, or failing that
# the first docker of any kind, and never this hook's own wrapper. Skipping the wrapper stops a
# second run, with the wrapper already first on PATH, from writing a wrapper that calls itself.
# Preferring a binary stops it from wrapping some other wrapper script, such as a session shim that
# adds --network host as well; docker refuses that run ("network "host" is specified multiple
# times"). A docker that only exists as a script (podman-docker's) is still found.
find_real_docker() {
  local dir candidate first=""
  local -a dirs=()
  IFS=: read -r -a dirs <<<"${PATH:-}"
  for dir in "${dirs[@]}"; do
    [[ -n "$dir" ]] || continue
    candidate="${dir}/docker"
    [[ -f "$candidate" && -x "$candidate" ]] || continue
    if [[ "$(LC_ALL=C head -c 4 "$candidate" 2>/dev/null | tr -d '\000')" == $'\x7fELF' ]]; then
      printf '%s\n' "$candidate"
      return 0
    fi
    if LC_ALL=C grep -qF "$WRAPPER_MARKER" "$candidate" 2>/dev/null; then
      continue
    fi
    [[ -n "$first" ]] || first="$candidate"
  done
  [[ -n "$first" ]] || return 1
  printf '%s\n' "$first"
}

daemon_answers() { with_timeout 15 "$REAL_DOCKER" info >/dev/null 2>&1; }

# True when pid file $1 holds the pid of a live process whose name (/proc/<pid>/comm) is $2. A
# live pid alone proves nothing: /run is on the persistent root disk in a cloud session's VM, so a
# pid file can outlive the daemon that wrote it, and after a recycle its pid can belong to some
# other process.
pidfile_names() {
  local pid
  pid="$(tr -dc '0-9' 2>/dev/null <"$1" || true)"
  [[ -n "$pid" && "$(cat "/proc/${pid}/comm" 2>/dev/null || true)" == "$2" ]]
}

dockerd_running() {
  if command -v pgrep >/dev/null 2>&1; then
    pgrep -x dockerd >/dev/null 2>&1
  else
    pidfile_names "$DOCKERD_PIDFILE" dockerd
  fi
}

# file:process-name pairs. A stale docker.pid makes dockerd refuse to start ("process with PID N
# is still running"), and a stale containerd.pid makes it take that process for its containerd.
DAEMON_PIDFILES=("${DOCKERD_PIDFILE}:dockerd" /var/run/docker/containerd/containerd.pid:containerd)

# Remove each pid file whose pid is not a live process of the name it should have. Called only
# when no dockerd process exists, so it can never pull a file out from under a running daemon.
clear_stale_pidfiles() {
  local entry file name pid
  for entry in "${DAEMON_PIDFILES[@]}"; do
    file="${entry%:*}"
    name="${entry##*:}"
    [[ -f "$file" ]] || continue
    if pidfile_names "$file" "$name"; then
      continue
    fi
    pid="$(tr -dc '0-9' <"$file" 2>/dev/null || true)"
    if rm -f "$file"; then
      printf '%s session-start: removed stale %s (pid %s is not a running %s)\n' \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$file" "${pid:-none}" "$name" >>"$DOCKERD_LOG" 2>/dev/null || true
    fi
  done
}

# Start dockerd in the background unless one is already running, then wait for it to answer.
# Returns 0 once `docker info` answers, 1 if it never does. Sets DOCKERD_STATUS either way.
# Called from an `if`, where `set -e` does not apply, so every failure is handled explicitly.
ensure_dockerd() {
  local dockerd_bin pid="" deadline started
  local -a launcher=()

  if daemon_answers; then
    DOCKERD_STATUS="running (was already up)"
    return 0
  fi

  if dockerd_running; then
    DOCKERD_STATUS="a dockerd process was already starting"
  else
    dockerd_bin="$(command -v dockerd 2>/dev/null || true)"
    if [[ -z "$dockerd_bin" ]]; then
      DOCKERD_STATUS="NOT running, and there is no dockerd binary on PATH to start"
      return 1
    fi
    if [[ "$(id -u)" != "0" ]]; then
      if command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
        launcher=(sudo -n -E)
      else
        DOCKERD_STATUS="NOT running, and starting it needs root (no passwordless sudo)"
        return 1
      fi
    fi
    # setsid: a new session, so the daemon outlives the hook's process group. stdin and both
    # output streams are redirected so the daemon holds none of the hook's pipes; a daemon that
    # kept the hook's stdout open would keep Claude Code waiting for it until the hook timed out.
    if command -v setsid >/dev/null 2>&1; then
      launcher=(setsid ${launcher[@]+"${launcher[@]}"})
    fi
    if ! printf '\n=== %s session-start: starting %s ===\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$dockerd_bin" >>"$DOCKERD_LOG"; then
      DOCKERD_STATUS="NOT running; cannot write its log $DOCKERD_LOG"
      return 1
    fi
    clear_stale_pidfiles
    ${launcher[@]+"${launcher[@]}"} "$dockerd_bin" </dev/null >>"$DOCKERD_LOG" 2>&1 &
    pid=$!
    DOCKERD_STATUS="started (pid ${pid}, log ${DOCKERD_LOG})"
  fi

  started=$SECONDS
  deadline=$((SECONDS + DOCKERD_WAIT))
  while ((SECONDS < deadline)); do
    if daemon_answers; then
      DOCKERD_STATUS="${DOCKERD_STATUS}; answering after $((SECONDS - started))s"
      return 0
    fi
    if [[ -n "$pid" ]] && ! kill -0 "$pid" 2>/dev/null; then
      DOCKERD_STATUS="NOT running: dockerd exited during startup; last lines of ${DOCKERD_LOG}: $(tail -n 3 "$DOCKERD_LOG" 2>/dev/null | tr '\n' ' ')"
      return 1
    fi
    sleep 1
  done
  DOCKERD_STATUS="${DOCKERD_STATUS}; NOT answering after ${DOCKERD_WAIT}s (see ${DOCKERD_LOG})"
  return 1
}

# Write the wrapper atomically: a temporary file in the same directory, then a rename, so a
# `docker` call made while the hook runs sees either the old wrapper or the new one, never half.
write_wrapper() {
  local tmp
  mkdir -p "$WRAPPER_DIR" || return 1
  tmp="$(mktemp "${WRAPPER_DIR}/.docker.XXXXXX")" || return 1
  if ! {
    printf '#!/usr/bin/env bash\n'
    printf '# %s: written by .claude/hooks/session-start.sh at every session start; edits are lost.\n' "$WRAPPER_MARKER"
    cat <<'HEADER'
#
# When HTTPS_PROXY (or https_proxy) points at loopback, which is the Claude Code cloud session's
# agent proxy, a container reaches that proxy only on the host network. So `docker run` gets
# --network host, the proxy variables (passed by name, so docker reads their CURRENT values) and the
# CA bundle mounted read-only with SSL_CERT_FILE pointing at it; `docker build` gets --network host
# and the proxy build args. Everything else, and every call made without a loopback proxy, goes to
# the real docker unchanged. The proxy is read at each call because its port rotates.
#
# Exceptions:
#   - A run or build that names its own network (--network/--net) keeps it. On host it still gets
#     the proxy and the CA. On any other network 127.0.0.1 is not the proxy, so the call is passed
#     through unchanged, and an HTTPS call from that container to a sibling by name is not sent to
#     a dead proxy address.
#   - A run that publishes ports (-p, --publish, -P, --publish-all) is passed through unchanged.
#     Host networking discards published ports, so it stays on the default bridge network, with no
#     route to the proxy, and the wrapper says so on stderr.
# For `docker run` only the options before the image are read, so a `-p:Name=Value` or a
# `--network` in the container's own command line is not taken for one.
#
# Limits: only a subcommand in the first position is recognised, so `docker --context x run` and
# `docker compose` pass through as they are. Containers on the host network share the host's ports,
# so two that bind the same fixed port at the same time collide. `docker build` gets no CA, so a
# RUN step that fetches over HTTPS from a host the proxy intercepts fails certificate verification.
# Dockerfile.devtest has no such step: apt uses plain http, and dockerd pulls the base images.
HEADER
    printf 'set -euo pipefail\n'
    printf 'real_docker=%q\n' "$REAL_DOCKER"
    declare -f is_loopback_proxy
    cat <<'BODY'

if ! is_loopback_proxy "${HTTPS_PROXY:-${https_proxy:-}}"; then
  exec "$real_docker" "$@"
fi

kind=""
head=()
case "${1:-}" in
  run | build)
    kind="$1"
    head=("$1")
    ;;
  container | image | builder | buildx)
    case "${1}:${2:-}" in
      container:run) kind=run ;;
      image:build | builder:build | buildx:build) kind=build ;;
    esac
    if [[ -n "$kind" ]]; then
      head=("$1" "$2")
    fi
    ;;
esac
if [[ -z "$kind" ]]; then
  exec "$real_docker" "$@"
fi
shift "${#head[@]}"

# The network the caller named (empty for none) and whether a run publishes ports. `docker run`
# stops reading options at the image, so this scan does too. Options that take no value are
# listed; any other option is taken to have one, in the same word (--opt=v, -p8080:80) or the next.
# A no-value option missing from the list is the scan's blind spot: it swallows the next word as its
# value. Past the image that is harmless, but directly before -p it eats the -p, the scan stops at the
# port spec, and the run gets --network host with its ports discarded. Keep the list current.
network=""
publishes=0
args=("$@")
i=0
while ((i < ${#args[@]})); do
  arg="${args[i]}"
  case "$arg" in
    --network=* | --net=*) network="${arg#*=}" ;;
    --network | --net)
      i=$((i + 1))
      network="${args[i]:-}"
      ;;
    *)
      if [[ "$kind" == run ]]; then
        case "$arg" in
          --) break ;;
          --publish=* | --publish-all | --publish-all=*) publishes=1 ;;
          --publish)
            publishes=1
            i=$((i + 1))
            ;;
          --*=*) ;;
          --rm | --detach | --interactive | --tty | --init | --privileged | --read-only | \
            --no-healthcheck | --oom-kill-disable | --sig-proxy | --quiet | --use-api-socket | \
            --disable-content-trust | --help) ;;
          --*) i=$((i + 1)) ;;
          -?*)
            # A cluster of short options: -it, -dp 80:80, -p8080:80. d, i, t, q and P take no
            # value; any other letter takes the rest of the word, or the next word.
            j=1
            while ((j < ${#arg})); do
              case "${arg:j:1}" in
                d | i | t | q) ;;
                P) publishes=1 ;;
                *)
                  if [[ "${arg:j:1}" == p ]]; then
                    publishes=1
                  fi
                  if ((j + 1 == ${#arg})); then
                    i=$((i + 1))
                  fi
                  break
                  ;;
              esac
              j=$((j + 1))
            done
            ;;
          *) break ;;
        esac
      fi
      ;;
  esac
  i=$((i + 1))
done

extra=()
on_host=0
case "$network" in
  "")
    if [[ "$publishes" == 0 ]]; then
      on_host=1
      extra+=(--network host)
    else
      printf '%s\n' "ashlar-session docker wrapper: this run publishes ports, which host networking would discard, so it stays on the default bridge network and has no route to the session proxy" >&2
    fi
    ;;
  host) on_host=1 ;;
esac
if [[ "$on_host" == 0 ]]; then
  exec "$real_docker" "${head[@]}" "$@"
fi
for var in HTTPS_PROXY https_proxy HTTP_PROXY http_proxy NO_PROXY no_proxy; do
  if [[ -n "${!var:-}" ]]; then
    if [[ "$kind" == run ]]; then
      extra+=(-e "$var")
    else
      extra+=(--build-arg "$var")
    fi
  fi
done
if [[ "$kind" == run ]]; then
  ca_bundle="${ASHLAR_CONTAINER_CA_BUNDLE:-/root/.ccr/ca-bundle.crt}"
  if [[ -f "$ca_bundle" ]]; then
    extra+=(-v "${ca_bundle}:/etc/ashlar-session/ca-bundle.crt:ro"
            -e SSL_CERT_FILE=/etc/ashlar-session/ca-bundle.crt)
  fi
fi
exec "$real_docker" "${head[@]}" ${extra[@]+"${extra[@]}"} "$@"
BODY
  } >"$tmp"; then
    rm -f "$tmp"
    return 1
  fi
  if ! bash -n "$tmp" || ! chmod 755 "$tmp" || ! mv -f "$tmp" "${WRAPPER_DIR}/docker"; then
    rm -f "$tmp"
    return 1
  fi
}

# Put the wrapper first on PATH for the rest of the session. The line is guarded, so sourcing it
# twice does not stack the directory, and it is appended only once, so the hook can run again.
register_path() {
  local line
  if [[ -z "${CLAUDE_ENV_FILE:-}" ]]; then
    WRAPPER_STATUS="${WRAPPER_STATUS}; CLAUDE_ENV_FILE is not set, so put ${WRAPPER_DIR} first on PATH yourself"
    return 0
  fi
  printf -v line "case \":\$PATH:\" in *:%q:*) ;; *) export PATH=%q:\"\$PATH\" ;; esac" "$WRAPPER_DIR" "$WRAPPER_DIR"
  if [[ -f "$CLAUDE_ENV_FILE" ]] && grep -qxF -- "$line" "$CLAUDE_ENV_FILE"; then
    WRAPPER_STATUS="${WRAPPER_STATUS}; already on PATH via CLAUDE_ENV_FILE"
  elif printf '%s\n' "$line" >>"$CLAUDE_ENV_FILE"; then
    WRAPPER_STATUS="${WRAPPER_STATUS}; first on PATH via CLAUDE_ENV_FILE"
  else
    WRAPPER_STATUS="${WRAPPER_STATUS}; could NOT write ${CLAUDE_ENV_FILE}, so put ${WRAPPER_DIR} first on PATH yourself"
  fi
}

# Warn, never fix: the daemon pulls base images through ITS OWN proxy setting, fixed when it
# started. A daemon started before the proxy port rotated pulls through a dead port.
check_daemon_proxy() {
  local session_proxy="$1" daemon_proxy
  daemon_proxy="$(with_timeout 15 "$REAL_DOCKER" info --format '{{.HTTPSProxy}}' 2>/dev/null || true)"
  if [[ "$daemon_proxy" != "$session_proxy" ]]; then
    DOCKERD_STATUS="${DOCKERD_STATUS}; WARNING its HTTPS proxy is '${daemon_proxy}' but the session's is '${session_proxy}', so image pulls may fail until dockerd is restarted with the session's environment"
  fi
}

ensure_image() {
  local image="${ASHLAR_DEVTEST_IMAGE:-ashlar-devtest:local}"
  local script="${PROJECT_DIR}/scripts/ensure-devtest-image.sh"
  local path_for_build="$PATH" had_image=0 started rc limit
  if [[ ! -f "$script" ]]; then
    IMAGE_STATUS="skipped: ${script} not found"
    return 0
  fi
  if [[ "$WRAPPER_STATUS" == written* ]]; then
    path_for_build="${WRAPPER_DIR}:${PATH}"
  fi
  if with_timeout 15 "$REAL_DOCKER" image inspect "$image" >/dev/null 2>&1; then
    had_image=1
  fi
  # What is left of the hook's budget, and never more than BUILD_TIMEOUT, so however long the
  # daemon took to answer the hook still ends before the harness's timeout would kill it.
  limit=$((HOOK_BUDGET - SECONDS))
  if ((limit > BUILD_TIMEOUT)); then
    limit=$BUILD_TIMEOUT
  fi
  if ((limit < 60)); then
    IMAGE_STATUS="${image} not checked: under 60s of the hook's ${HOOK_BUDGET}s budget was left; scripts/test-in-container.sh will build it if it is missing"
    return 0
  fi
  started=$SECONDS
  printf '\n=== %s session-start: ensure %s (allowed %ss) ===\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$image" "$limit" >>"$BUILD_LOG" 2>/dev/null || true
  if PATH="$path_for_build" with_timeout "$limit" bash "$script" </dev/null >>"$BUILD_LOG" 2>&1; then
    if [[ "$had_image" == 1 ]]; then
      IMAGE_STATUS="${image} present"
    else
      IMAGE_STATUS="${image} built in $((SECONDS - started))s (log ${BUILD_LOG})"
    fi
  else
    rc=$?
    if [[ "$rc" == 124 ]]; then
      IMAGE_STATUS="${image} build STOPPED at its ${limit}s limit (see ${BUILD_LOG}); scripts/test-in-container.sh will retry it"
    else
      IMAGE_STATUS="${image} build FAILED (exit ${rc}, after $((SECONDS - started))s; see ${BUILD_LOG}); scripts/test-in-container.sh will retry it"
    fi
  fi
}

main() {
  if [[ "${CLAUDE_CODE_REMOTE:-}" != "true" ]]; then
    exit 0
  fi

  PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

  if ! REAL_DOCKER="$(find_real_docker)"; then
    say "no docker CLI on PATH, so the devtest container (scripts/test-in-container.sh) is unavailable in this session."
    exit 0
  fi

  local daemon_up=0 session_proxy="${HTTPS_PROXY:-${https_proxy:-}}"
  if ensure_dockerd; then
    daemon_up=1
    if [[ -n "$session_proxy" ]]; then
      check_daemon_proxy "$session_proxy"
    fi
  fi

  if is_loopback_proxy "$session_proxy"; then
    if write_wrapper; then
      WRAPPER_STATUS="written to ${WRAPPER_DIR}/docker (wraps ${REAL_DOCKER})"
      register_path
    else
      WRAPPER_STATUS="could NOT be written to ${WRAPPER_DIR}/docker; containers will not reach the network"
    fi
  else
    WRAPPER_STATUS="not needed (no loopback HTTPS_PROXY)"
  fi

  if [[ "$daemon_up" == 1 ]]; then
    ensure_image
  else
    IMAGE_STATUS="skipped, because dockerd is not answering"
  fi

  say "dockerd: ${DOCKERD_STATUS}"
  say "docker wrapper: ${WRAPPER_STATUS}"
  say "devtest image: ${IMAGE_STATUS}"
  exit 0
}

# Sourcing the file (for a test of one function) defines everything and runs nothing.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
