#!/usr/bin/env bash
# Build the dev/test image if it is not already present, and print its tag.
#
# The container scripts need a .NET 10 SDK *and* the real ASP.NET Core 8 runtime; see
# .docker/Dockerfile.devtest for why rolling forward is not a substitute. Docker layer-caches
# the build, so the first call costs a runtime download and every later call is instant.
#
# Usage:  IMAGE="$(bash scripts/ensure-devtest-image.sh)"
#         docker run --rm "$IMAGE" ...
#
# Set ASHLAR_DEVTEST_IMAGE to override the tag, or ASHLAR_DEVTEST_REBUILD=1 to force a rebuild.
set -euo pipefail

IMAGE="${ASHLAR_DEVTEST_IMAGE:-ashlar-devtest:local}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# docker.exe does not understand a /c/... path. Git Bash usually rewrites those on the way out,
# but a caller that has set MSYS_NO_PATHCONV=1 — which this repo's own docs recommend, because
# the rewriting corrupts container-side paths — turns that off, and the build context then fails
# with "path not found". Resolve to a Windows path ourselves so both callers work.
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    ROOT="$(cd "$ROOT" && pwd -W 2>/dev/null || printf '%s' "$ROOT")"
    ;;
esac

need_build=0
if [[ "${ASHLAR_DEVTEST_REBUILD:-0}" == "1" ]]; then
  need_build=1
elif ! docker image inspect "$IMAGE" >/dev/null 2>&1; then
  need_build=1
fi

if [[ "$need_build" == "1" ]]; then
  # Progress goes to stderr so the tag on stdout stays machine-readable.
  echo "ensure-devtest-image: building $IMAGE (first build downloads the ASP.NET Core 8 runtime)..." >&2
  docker build -t "$IMAGE" -f "${ROOT}/.docker/Dockerfile.devtest" "${ROOT}/.docker" >&2
fi

printf '%s\n' "$IMAGE"
