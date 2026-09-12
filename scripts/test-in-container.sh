#!/usr/bin/env bash
# Run a build/test command against this repository INSIDE the devtest container.
#
# The bash sibling of scripts/test-in-container.ps1, with one addition the PowerShell version does
# not have: it can run an ARBITRARY command in /repo, not only `dotnet test`. Most of what this is
# actually used for is a solution build, a probe script, or a loop that runs one suite ten times to
# see whether it is flaky, and shelling out to `dotnet test` with a filter cannot express those.
#
# WHY A CONTAINER
# ---------------
# Two independent reasons, and both of them bite on a developer machine rather than in CI.
#
# 1. Windows Smart App Control reputation-blocks freshly built unsigned DLLs
#    (FileLoadException 0x800711C7). Verdicts are per-file-hash, cached, and re-rolled on every
#    rebuild, and SAC has no exclusion mechanism — so host-side `dotnet test` is unreliable BY
#    DESIGN on such a box, not occasionally.
# 2. net8.0 needs a real ASP.NET Core 8 runtime. `DOTNET_ROLL_FORWARD=LatestMajor` is not a
#    substitute: it rolls net8.0 onto ASP.NET Core 10 even when 8.0 is installed, and every
#    HTTP-hosting test then fails in a way that reads as a product bug. Measured on this repo at
#    10 failed / 167 passed purely from the roll-forward. The image built by
#    scripts/ensure-devtest-image.sh carries SDK 10 AND the real 8.0 runtime.
#
# The repository is mounted READ-ONLY and re-cloned inside the container, so no Linux bin/obj ever
# lands in the host working tree, and only COMMITTED state is tested unless --dirty is passed.
# NuGet packages persist in the ashlar-nuget-packages volume the devcontainer also uses.
#
# KNOWN HARNESS ARTEFACT
# ----------------------
# Every build in this harness prints two SourceLink warnings:
#
#     warning : Source control information is not available - the generated source link is empty.
#     warning : URL of repository remote 'origin' is invalid: /src-mirror
#
# They come from the inner clone's origin being a bind-mount path. They are absent in CI, they are
# not a regression, and — this is the part that matters — a WARNING COUNT from this harness is two
# higher than the same build in CI. Grep the warning text; never compare counts.
#
# USAGE
#   scripts/test-in-container.sh                                  # cert-gate filter, net10.0
#   scripts/test-in-container.sh --filter 'FullyQualifiedName~LiteDb' --framework net8.0
#   scripts/test-in-container.sh --dirty -- 'dotnet build Ashlar.sln -c Debug --nologo'
#   scripts/test-in-container.sh --repo /path/to/other/clone -- 'bash scripts/run-cert-gate.sh'
#
# OPTIONS
#   --repo <path>       Repository to mount. Default: the repository containing this script.
#   --ref <ref>         Git ref to check out inside the container. Default: HEAD.
#   --dirty             Carry uncommitted work in (binary patch + untracked tarball).
#   --project <csproj>  Test project, repo-relative. Default: Ashlar.Tests.Infrastructure.
#   --framework <tfm>   Default: net10.0.
#   --filter <expr>     xUnit filter. Default: the cert-gate namespace filter.
#   -- <command...>     Run this in /repo instead of `dotnet test`.
#
# ASHLAR_DEVTEST_IMAGE overrides the image tag (default ashlar-devtest:local).
set -euo pipefail

REPO=""
REF="HEAD"
DIRTY=0
PROJECT="src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"
FRAMEWORK="net10.0"
FILTER="FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Certification"
CMD=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) REPO="${2:?--repo needs a path}"; shift 2 ;;
    --ref) REF="${2:?--ref needs a ref}"; shift 2 ;;
    --dirty) DIRTY=1; shift ;;
    --project) PROJECT="${2:?--project needs a path}"; shift 2 ;;
    --framework) FRAMEWORK="${2:?--framework needs a tfm}"; shift 2 ;;
    --filter) FILTER="${2:?--filter needs an expression}"; shift 2 ;;
    --) shift; CMD="$*"; break ;;
    -h|--help) sed -n '2,55p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "test-in-container: unknown argument '$1' (try --help)" >&2; exit 2 ;;
  esac
done

if [[ -z "${REPO}" ]]; then
  REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fi
if [[ ! -d "${REPO}/.git" ]]; then
  echo "test-in-container: ${REPO} is not a git repository" >&2
  exit 2
fi

if [[ -z "${CMD}" ]]; then
  CMD="dotnet test '${PROJECT}' --framework '${FRAMEWORK}' --filter '${FILTER}' --nologo -v minimal"
fi

SHA="$(git -C "${REPO}" rev-parse "${REF}")"
IMAGE="$(ASHLAR_DEVTEST_IMAGE="${ASHLAR_DEVTEST_IMAGE:-ashlar-devtest:local}" bash "${REPO}/scripts/ensure-devtest-image.sh")"

# Docker on Windows wants a Windows-shaped host path; Git Bash hands out /c/... which it rejects.
to_host_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1" | tr '\\' '/'
  else
    printf '%s' "$1"
  fi
}

REPO_MOUNT="$(to_host_path "${REPO}")"

PATCH_MOUNT=()
INNER_PATCH=""
PATCH_DIR=""
cleanup() { [[ -n "${PATCH_DIR}" ]] && rm -rf "${PATCH_DIR}"; }
trap cleanup EXIT

if [[ "${DIRTY}" == "1" ]]; then
  PATCH_DIR="$(mktemp -d)"
  # --binary so a changed image or corpus survives; staged and unstaged both. Untracked files are
  # not in `git diff` at all, hence the separate tarball.
  git -C "${REPO}" diff HEAD --binary > "${PATCH_DIR}/dirty.patch" || true
  git -C "${REPO}" ls-files --others --exclude-standard -z \
    | tar -C "${REPO}" --null -T - -czf "${PATCH_DIR}/untracked.tgz" 2>/dev/null \
    || : > "${PATCH_DIR}/untracked.tgz"
  PATCH_MOUNT=(-v "$(to_host_path "${PATCH_DIR}"):/patch:ro")
  INNER_PATCH='if [ -s /patch/dirty.patch ]; then git apply --whitespace=nowarn /patch/dirty.patch; fi;
               if [ -s /patch/untracked.tgz ]; then tar -xzf /patch/untracked.tgz -C /repo; fi;'
fi

DIRTY_LABEL=""
if [[ "${DIRTY}" == "1" ]]; then
  DIRTY_LABEL=" (+ uncommitted work)"
fi
echo "== container run: ${SHA}${DIRTY_LABEL} =="
echo "== ${CMD}"

# MSYS_NO_PATHCONV stops Git Bash rewriting the -v arguments into Windows paths mid-flight.
MSYS_NO_PATHCONV=1 docker run --rm --user root \
  -v "${REPO_MOUNT}:/src-mirror:ro" \
  -v ashlar-nuget-packages:/root/.nuget/packages \
  "${PATCH_MOUNT[@]}" \
  -e DOTNET_NOLOGO=1 \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  "${IMAGE}" \
  bash -lc "set -e; git config --global safe.directory '*'; git clone -q /src-mirror /repo; cd /repo; git checkout -q ${SHA}; ${INNER_PATCH} ${CMD}"
