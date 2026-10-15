#!/usr/bin/env bash
# Drives scripts/ci/pr-testing-strategy-gate.sh: comment-only diffs under ProdStyle trigger
# paths must pass; a one-line code change must still fail; [skip-prod-style] still skips.
#
# Run: bash tests/scripts/pr-testing-strategy-gate.test.sh
# Pure bash + git: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SOURCE_GATE="${ROOT}/scripts/ci/pr-testing-strategy-gate.sh"
EXPECTED_ASSERTIONS=4

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; echo "         $2"; }

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

if [[ ! -f "${SOURCE_GATE}" ]]; then
  echo "FAIL - missing ${SOURCE_GATE}"
  exit 1
fi

make_repo() {
  local dir="$1"
  mkdir -p "${dir}/src/Ashlar.Hosting" "${dir}/scripts/ci"
  # Gate computes ROOT from BASH_SOURCE; copy it so the sandbox is the repo under test.
  cp "${SOURCE_GATE}" "${dir}/scripts/ci/pr-testing-strategy-gate.sh"
  git -C "${dir}" init -q
  git -C "${dir}" config user.email "test@example.com"
  git -C "${dir}" config user.name "test"
  cat > "${dir}/src/Ashlar.Hosting/Sample.cs" <<'CS'
namespace Ashlar.Hosting;

public sealed class Sample
{
    public int Value => 1;
}
CS
  git -C "${dir}" add src/Ashlar.Hosting/Sample.cs scripts/ci/pr-testing-strategy-gate.sh
  git -C "${dir}" commit -qm base
}

run_gate() {
  local dir="$1"
  local body="${2:-}"
  (
    cd "${dir}"
    PR_BODY="${body}" bash scripts/ci/pr-testing-strategy-gate.sh HEAD~1
  )
}

echo "== comment-only XML doc under Hosting =="
REPO="${TMP}/comment-only"
make_repo "${REPO}"
cat > "${REPO}/src/Ashlar.Hosting/Sample.cs" <<'CS'
namespace Ashlar.Hosting;

/// <summary>Host sample for the gate twin.</summary>
public sealed class Sample
{
    public int Value => 1;
}
CS
git -C "${REPO}" add src/Ashlar.Hosting/Sample.cs
git -C "${REPO}" commit -qm 'docs: xml comment only'
if out="$(run_gate "${REPO}" 2>&1)"; then
  ok "comment-only Hosting diff passes the ProdStyle demand"
else
  bad "comment-only Hosting diff passes the ProdStyle demand" "${out}"
fi

echo "== one-line code change under Hosting =="
REPO="${TMP}/code-change"
make_repo "${REPO}"
cat > "${REPO}/src/Ashlar.Hosting/Sample.cs" <<'CS'
namespace Ashlar.Hosting;

public sealed class Sample
{
    public int Value => 2;
}
CS
git -C "${REPO}" add src/Ashlar.Hosting/Sample.cs
git -C "${REPO}" commit -qm 'fix: change Value'
if out="$(run_gate "${REPO}" 2>&1)"; then
  bad "one-line Hosting code change fails without ProdStyle delta" "gate exited 0: ${out}"
else
  echo "${out}" | grep -q 'Production-wiring paths changed without ProdStyle' \
    && ok "one-line Hosting code change fails without ProdStyle delta" \
    || bad "one-line Hosting code change fails without ProdStyle delta" "missing ERROR text: ${out}"
fi

echo "== [skip-prod-style] still skips a code change =="
REPO="${TMP}/skip-token"
make_repo "${REPO}"
cat > "${REPO}/src/Ashlar.Hosting/Sample.cs" <<'CS'
namespace Ashlar.Hosting;

public sealed class Sample
{
    public int Value => 3;
}
CS
git -C "${REPO}" add src/Ashlar.Hosting/Sample.cs
git -C "${REPO}" commit -qm 'fix: change Value with skip'
if out="$(run_gate "${REPO}" '[skip-prod-style] rationale for twin' 2>&1)"; then
  echo "${out}" | grep -q 'ProdStyle check skipped' \
    && ok "[skip-prod-style] still skips a real code change" \
    || bad "[skip-prod-style] still skips a real code change" "missing skip note: ${out}"
else
  bad "[skip-prod-style] still skips a real code change" "${out}"
fi

echo "== gate script is the one under test =="
ok "scripts/ci/pr-testing-strategy-gate.sh exists and is copied into each sandbox (control)"

echo
echo "passed: ${PASS}   failed: ${FAIL}"
ran=$((PASS + FAIL))
if [[ "${ran}" -ne "${EXPECTED_ASSERTIONS}" ]]; then
  echo "FAIL - ran ${ran} assertions, expected ${EXPECTED_ASSERTIONS}."
  exit 1
fi
[[ "${FAIL}" -eq 0 ]] || exit 1
exit 0
