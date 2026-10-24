#!/usr/bin/env bash
# Test the verify-package-versions.sh script
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="${ROOT}/scripts/ci/verify-package-versions.sh"

echo "Testing verify-package-versions.sh..."

# Test 1: Script should pass on the current repository (all packages are properly versioned)
echo "Test 1: Verify script passes on current repository..."
if ! bash "${SCRIPT}" > /dev/null 2>&1; then
  echo "FAIL: verify-package-versions.sh failed on current repository"
  echo "All packages should be properly versioned in Directory.Packages.props"
  exit 1
fi
echo "✓ Test 1 passed"

# Test 2: Script should be executable
echo "Test 2: Verify script is executable..."
if [[ ! -x "${SCRIPT}" ]]; then
  echo "FAIL: verify-package-versions.sh is not executable"
  exit 1
fi
echo "✓ Test 2 passed"

# Test 3: Script should find Directory.Packages.props
echo "Test 3: Verify script can find Directory.Packages.props..."
if [[ ! -f "${ROOT}/Directory.Packages.props" ]]; then
  echo "FAIL: Directory.Packages.props not found at repository root"
  exit 1
fi
echo "✓ Test 3 passed"

# Test 4: Script output should include "verify-package-versions: OK" when passing
echo "Test 4: Verify script output format..."
output=$(bash "${SCRIPT}" 2>&1)
if [[ ! "$output" =~ "verify-package-versions: OK" ]]; then
  echo "FAIL: Script output should contain 'verify-package-versions: OK'"
  echo "Got: $output"
  exit 1
fi
echo "✓ Test 4 passed"

# Test 5: Script should report discovered packages count
echo "Test 5: Verify script reports package count..."
if [[ ! "$output" =~ "Found "[0-9]+" package versions" ]]; then
  echo "FAIL: Script should report number of discovered package versions"
  echo "Got: $output"
  exit 1
fi
echo "✓ Test 5 passed"

echo
echo "All tests passed!"
echo "test-verify-package-versions: OK"
