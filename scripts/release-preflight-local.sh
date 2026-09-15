#!/usr/bin/env bash
# One command before you cut a release: graph alignment + NuGet consumer sample (isolated cache).
# Usage: bash scripts/release-preflight-local.sh 1.2.3
# Optional: ASHLAR_RELEASE_PREFLIGHT_TRIGGER_GATE=1  →  gh workflow run "Runtime Release Gate" --ref <branch>
#           ASHLAR_RELEASE_PREFLIGHT_REF=master     (default: current branch from git)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VER="${1:?usage: release-preflight-local.sh <semver>   example: bash scripts/release-preflight-local.sh 1.2.3}"
VER="${VER#v}"

echo "== 1/2 Pack graph vs MSBuild (Ashlar.Hosting) =="
python3 "${ROOT}/scripts/verify-pack-ashlar-hosting-graph-alignment.py"

echo "== 2/2 NuGet consumer sample (local feed + isolated cache) =="
export ASHLAR_SDK_PACKAGE_VERSION="${VER}"
bash "${ROOT}/scripts/verify-stable-sdk-host-sample-packages.sh"

if [[ "${ASHLAR_RELEASE_PREFLIGHT_TRIGGER_GATE:-}" == "1" ]]; then
  REF="${ASHLAR_RELEASE_PREFLIGHT_REF:-$(git -C "${ROOT}" rev-parse --abbrev-ref HEAD)}"
  if command -v gh >/dev/null 2>&1; then
    echo "== Optional: triggering Runtime Release Gate on origin/${REF} =="
    gh workflow run "Runtime Release Gate" --ref "${REF}" || {
      echo "::warning::gh workflow run failed (auth or repo?). Continue without CI gate."
    }
  else
    echo "::notice::ASHLAR_RELEASE_PREFLIGHT_TRIGGER_GATE=1 but gh not installed; skip."
  fi
fi

# Staleness is advisory and never blocks: whether a tree is "too far" past its tag is a
# judgement call. It runs here because this is the last human checkpoint before a tag, and
# release.yml's tag-vs-VERSION guard only fires AFTER the tag exists.
bash "$(dirname "${BASH_SOURCE[0]}")/check-version-staleness.sh" || true

echo ""
echo "Preflight OK for version ${VER}."
echo "Next (pick one):"
echo "  • Ship GHCR + NuGet:  git tag v${VER} && git push origin v${VER}"
echo "  • Open Actions:       gh workflow run Release --ref \$(git rev-parse --abbrev-ref HEAD) -f version=${VER}"
echo "  • Same preflight:     dotnet run --project application/src/Ashlar.CLI -- release preflight ${VER}"
echo "  • Dispatch CI:        dotnet run --project application/src/Ashlar.CLI -- release dispatch ${VER}   (or: make release-dispatch VERSION=${VER})"
echo "  • Docs:               docs/RELEASE_RUNBOOK.md  docs/PUBLISHING.md  docs/GitHubRepoVariables.md"
