#!/usr/bin/env bash
# Offline evaluation arithmetic, runner status propagation, and every retained report.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
python3 -B "${ROOT}/tools/ReleaseEvidenceEvaluation/test_score.py"
python3 -B "${ROOT}/tools/ReleaseEvidenceEvaluation/test_runner_status.py"
python3 -B "${ROOT}/tools/ReleaseEvidenceEvaluation/test_verify_retained.py"
python3 -B "${ROOT}/tools/ReleaseEvidenceEvaluation/verify_retained.py"
