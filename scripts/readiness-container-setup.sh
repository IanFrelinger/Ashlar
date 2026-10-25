#!/usr/bin/env bash
# One-shot, idempotent setup for the container-first readiness pipeline (v2).
# Run INSIDE the dev container (docker exec <container> bash <this file>).
# Provisions everything the overnight convergence loop needs so that the host
# never runs git or dotnet:
#   1. .NET 8 runtime alongside SDK 10 (layer test projects target net8.0;
#      CI installs SDKs 10.0.403 + 8.0.427 — mirror it).
#   2. Global git identity + safe.directory (docker exec runs as root; the
#      bind-mounted host repo and mounted worktrees have foreign ownership).
#   3. The agent clone at $CLONE — a container-native clone of the host repo
#      ($SRC, bind mount). ALL integration commits land here; the host repo
#      only ever receives them via the container/* staging ref push.
# Safe to re-run: never resets an existing clone (agent commits survive);
# prints ahead/behind so drift is visible instead of silently "fixed".
set -euo pipefail

SRC="${READINESS_SRC:-/workspaces/Nexo}"
CLONE="${READINESS_CLONE:-/workspaces/nexo-agent}"
BRANCH="${READINESS_BRANCH:-claude/recursing-franklin-cbb828}"
GIT_NAME="${READINESS_GIT_NAME:-IanFrelinger}"
GIT_EMAIL="${READINESS_GIT_EMAIL:-icfrelinger@gmail.com}"

echo "== readiness-container-setup =="

echo "-- .NET 8 runtime --"
if dotnet --list-runtimes | grep -q '^Microsoft\.NETCore\.App 8\.'; then
  echo "net8 runtime present"
else
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  # An exact --version downloads from builds.dotnet.microsoft.com; --channel resolves through aka.ms.
  bash /tmp/dotnet-install.sh --runtime aspnetcore --version 8.0.33 --install-dir /usr/share/dotnet
fi

echo "-- python stdlib (dependency-boundary gate) --"
if python3 -c 'import dataclasses' 2>/dev/null; then
  echo "python3 stdlib complete"
else
  # Image ships python3-minimal only; the boundary script needs the full stdlib.
  PYVER="$(python3 -c 'import sys; print(f"{sys.version_info.major}.{sys.version_info.minor}")')"
  apt-get update -qq && apt-get install -y -qq "libpython${PYVER}-stdlib"
  python3 -c 'import dataclasses' && echo "python3 stdlib installed"
fi

echo "-- docker socket (tier-D lanes) --"
if command -v docker >/dev/null 2>&1 && docker version >/dev/null 2>&1; then
  echo "docker reachable: tier-D lanes (prod dry run, Neo4j integration) available"
else
  echo "no docker socket — tier-D lanes unavailable (recreate the container from"
  echo "the branch .devcontainer config with the docker-outside-of-docker feature)"
fi

echo "-- git config --"
git config --global user.name "$GIT_NAME"
git config --global user.email "$GIT_EMAIL"
# Bind mount + mounted worktrees are foreign-owned from root's perspective.
if ! git config --global --get-all safe.directory 2>/dev/null | grep -qx '\*'; then
  git config --global --add safe.directory '*'
fi

echo "-- agent clone --"
if [ ! -d "$CLONE/.git" ]; then
  git clone --no-hardlinks "$SRC" "$CLONE"
else
  git -C "$CLONE" fetch origin --prune
fi
if git -C "$CLONE" show-ref --verify --quiet "refs/heads/$BRANCH"; then
  git -C "$CLONE" checkout -q "$BRANCH"
else
  git -C "$CLONE" checkout -q -b "$BRANCH" "origin/$BRANCH"
fi

echo "-- verdict --"
echo "dotnet sdk: $(dotnet --version)"
dotnet --list-runtimes | grep '^Microsoft\.NETCore\.App' || true
echo "git: $(git --version) as $(git config --global user.name) <$(git config --global user.email)>"
echo "clone: $CLONE @ $(git -C "$CLONE" rev-parse --short HEAD) on $(git -C "$CLONE" branch --show-current)"
git -C "$CLONE" rev-list --left-right --count "origin/$BRANCH...$BRANCH" 2>/dev/null \
  | awk '{print "clone vs origin/'"$BRANCH"': behind " $1 ", ahead " $2}' || true
command -v jq >/dev/null && command -v python3 >/dev/null && echo "jq+python3: ok"
echo "readiness-container-setup: OK"
