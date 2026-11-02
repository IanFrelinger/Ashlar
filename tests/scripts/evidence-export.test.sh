#!/usr/bin/env bash
# Offline export behavior: temporary Ed25519 keys, pinned signer, retained anchor and exact inventory.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
python3 "${ROOT}/tests/scripts/evidence_export_tests.py"
