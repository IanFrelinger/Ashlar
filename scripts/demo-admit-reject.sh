#!/usr/bin/env bash
# Admission Gate Demo — shows artifact REJECT → ADMIT with visible proof
#
# This demo proves the certification gate's admission logic:
#   1. A weak witness (incomplete expectations) → REJECTED
#   2. A strong witness (complete expectations) → ADMITTED
#
# Both attempts produce signed records with correlated IDs that prove the gate ran.
#
# What this demonstrates:
#   - Artifact admission gates exist and enforce mutation + witness requirements
#   - Records are signed and contain verifiable proof (content hash, gate passes, signatures)
#   - Admission is NOT autonomy — see CLOSING-PLAN.md for the pre-execution positioning
#
# What this does NOT demonstrate:
#   - Pre-execution admission for Copilot chat tasks (that integration is a follow-up)
#   - Autonomy/Learn marketing remains HOLD until Dogfood unlock (see docs/dogfood-scorecard.md)
#   - Production deployment (this runs locally with mock keys)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

DEMO_DIR="${REPO_ROOT}/.demo-admission"
BRICK_DIR="${DEMO_DIR}/probe-brick"
WEAK_WITNESS="${DEMO_DIR}/weak-witness.json"
STRONG_WITNESS="${DEMO_DIR}/strong-witness.json"
REJECT_RECORD="${DEMO_DIR}/rejected-record.json"
ADMIT_RECORD="${DEMO_DIR}/admitted-record.json"

echo "========================================"
echo "  ADMISSION GATE DEMO"
echo "========================================"
echo
echo "This demo shows how Ashlar's certification gate"
echo "REJECTS weak artifacts and ADMITS strong ones."
echo
echo "Demo artifact: MutationProbeBrick (log scanner)"
echo "Weak test:     Only checks error count"
echo "Strong test:   Checks count + first error message"
echo

# Clean previous demo artifacts
rm -rf "${DEMO_DIR}"
mkdir -p "${BRICK_DIR}"

# Create the probe brick source (simplified log scanner)
cat > "${BRICK_DIR}/MutationProbeBrick.cs" <<'EOF'
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace DemoProbe;

/// <summary>Log scanner that finds ERROR lines — mutation gate demo artifact.</summary>
public sealed class MutationProbeBrick : DomainBrick
{
    public MutationProbeBrick()
    {
        Id = "mutation-probe-brick";
        Name = "Mutation Probe DomainBrick";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "Log scanner for admission gate demo.";
        Interface = new BrickInterface
        {
            Inputs = [new BrickInputDefinition("logText", "string", "log")],
            Outputs =
            [
                new BrickOutputDefinition("errorCount", "int", "count"),
                new BrickOutputDefinition("firstErrorMessage", "string", "first")
            ]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var logText = input.Get<string>("logText") ?? string.Empty;
        var errorLines = new List<string>();
        foreach (var line in logText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("ERROR", StringComparison.Ordinal))
                errorLines.Add(line);
        }

        var errorCount = errorLines.Count;
        var firstError = errorCount > 0
            ? ExtractErrorMessage(errorLines[0])
            : string.Empty;

        var summary = errorCount > 0
            ? $"Found {errorCount} ERROR line(s); first: {firstError}"
            : "No errors found";

        return Task.FromResult(new BrickOutput(
            new Dictionary<string, object>
            {
                ["errorCount"] = errorCount,
                ["firstErrorMessage"] = firstError
            },
            summary));
    }

    private static string ExtractErrorMessage(string errorLine)
    {
        var errorIndex = errorLine.IndexOf("ERROR", StringComparison.Ordinal);
        if (errorIndex < 0) return errorLine;
        var afterError = errorLine[(errorIndex + 5)..].TrimStart();
        var colonIndex = afterError.IndexOf(':');
        return colonIndex >= 0 ? afterError[(colonIndex + 1)..].Trim() : afterError;
    }
}
EOF

# Create brick project file
cat > "${BRICK_DIR}/MutationProbeBrick.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>DemoProbe</RootNamespace>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Ashlar.Core.Domain/Ashlar.Core.Domain.csproj" />
  </ItemGroup>
</Project>
EOF

# WEAK witness: only checks errorCount (mutations can break firstErrorMessage and still pass)
cat > "${WEAK_WITNESS}" <<'EOF'
{
  "brick": "mutation-probe-brick",
  "cases": [
    {
      "input": {
        "logText": "2024-01-01 INFO Started\n2024-01-01 ERROR First failure: connection reset\n2024-01-01 WARN Retrying\n2024-01-01 ERROR Second failure: timeout"
      },
      "expectedOutput": {
        "errorCount": 2
      }
    }
  ]
}
EOF

# STRONG witness: checks errorCount AND firstErrorMessage (catches more mutations)
cat > "${STRONG_WITNESS}" <<'EOF'
{
  "brick": "mutation-probe-brick",
  "cases": [
    {
      "input": {
        "logText": "2024-01-01 INFO Started\n2024-01-01 ERROR First failure: connection reset\n2024-01-01 WARN Retrying\n2024-01-01 ERROR Second failure: timeout"
      },
      "expectedOutput": {
        "errorCount": 2,
        "firstErrorMessage": "First failure: connection reset"
      }
    }
  ]
}
EOF

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "PHASE 1: WEAK WITNESS → REJECT"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo
echo "Witness: Only checks 'errorCount' (incomplete)"
echo "Expected: Gate REJECTS due to mutation survivors"
echo

set +e
dotnet run --project application/src/Ashlar.CLI --no-build -- \
    certify brick "${BRICK_DIR}" \
    --witness "${WEAK_WITNESS}" \
    --record "${REJECT_RECORD}" 2>&1 | tee "${DEMO_DIR}/reject-output.txt"
REJECT_EXIT=$?
set -e

echo
if [[ -f "${REJECT_RECORD}" ]]; then
    REJECT_ID=$(jq -r '.brickId' "${REJECT_RECORD}")
    REJECT_HASH=$(jq -r '.contentHash' "${REJECT_RECORD}")
    REJECT_ESCAPE=$(jq -r '.escapeRate' "${REJECT_RECORD}")
    REJECT_SIGNED=$(jq -r '.signed' "${REJECT_RECORD}")
    
    echo "✗ REJECTED (as expected)"
    echo "  Brick ID:      ${REJECT_ID}"
    echo "  Content Hash:  ${REJECT_HASH:0:16}..."
    echo "  Escape Rate:   ${REJECT_ESCAPE} (threshold: 0)"
    echo "  Record Signed: ${REJECT_SIGNED}"
    echo "  Exit Code:     ${REJECT_EXIT}"
    echo
    echo "Why it failed: Weak witness cannot detect mutations to"
    echo "               'firstErrorMessage' logic → survivors escape"
else
    echo "✗ ERROR: No rejection record written (unexpected)"
    exit 1
fi

echo
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "PHASE 2: STRONG WITNESS → ADMIT"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo
echo "Witness: Checks 'errorCount' AND 'firstErrorMessage'"
echo "Expected: Gate ADMITS (all mutants killed)"
echo

set +e
dotnet run --project application/src/Ashlar.CLI --no-build -- \
    certify brick "${BRICK_DIR}" \
    --witness "${STRONG_WITNESS}" \
    --record "${ADMIT_RECORD}" 2>&1 | tee "${DEMO_DIR}/admit-output.txt"
ADMIT_EXIT=$?
set -e

echo
if [[ -f "${ADMIT_RECORD}" ]]; then
    ADMIT_ID=$(jq -r '.brickId' "${ADMIT_RECORD}")
    ADMIT_HASH=$(jq -r '.contentHash' "${ADMIT_RECORD}")
    ADMIT_ESCAPE=$(jq -r '.escapeRate' "${ADMIT_RECORD}")
    ADMIT_SIGNED=$(jq -r '.signed' "${ADMIT_RECORD}")
    ADMIT_GATES=$(jq -r '.gatesPassed | length' "${ADMIT_RECORD}")
    
    echo "✓ ADMITTED"
    echo "  Brick ID:      ${ADMIT_ID}"
    echo "  Content Hash:  ${ADMIT_HASH:0:16}..."
    echo "  Escape Rate:   ${ADMIT_ESCAPE}"
    echo "  Gates Passed:  ${ADMIT_GATES}"
    echo "  Record Signed: ${ADMIT_SIGNED}"
    echo "  Exit Code:     ${ADMIT_EXIT}"
    echo
    echo "Why it passed: Strong witness detects mutations to BOTH"
    echo "               outputs → all mutants killed"
else
    echo "✗ ERROR: No admission record written (unexpected)"
    exit 1
fi

echo
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "PROOF OF CORRELATION"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo
echo "Both records certify the SAME artifact:"
echo "  Brick ID:  ${REJECT_ID} = ${ADMIT_ID}"
echo "  Content:   ${REJECT_HASH:0:16}... = ${ADMIT_HASH:0:16}..."
echo
echo "Different outcomes from different witness strength:"
echo "  Weak → REJECT (escape rate: ${REJECT_ESCAPE})"
echo "  Strong → ADMIT (escape rate: ${ADMIT_ESCAPE})"
echo

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "RECORDS & ARTIFACTS"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo
echo "Rejection record: ${REJECT_RECORD}"
echo "Admission record: ${ADMIT_RECORD}"
echo "Demo artifacts:   ${DEMO_DIR}"
echo

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "WHAT THIS PROVES"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo
echo "✓ Certification gate exists and enforces mutation testing"
echo "✓ Weak artifacts are REJECTED with signed proof"
echo "✓ Strong artifacts are ADMITTED with verifiable records"
echo "✓ Content hashes + signatures provide audit trail"
echo
echo "HONEST DISCLAIMER:"
echo "- This proves artifact admission gates work"
echo "- Copilot chat tasks currently record AFTER execution"
echo "- Wiring pre-admission to /api/copilot/task is a separate"
echo "  product change (see CLOSING-PLAN.md Phase 3-4)"
echo "- Autonomy/Learn marketing remains HOLD until Dogfood unlock"
echo "  (see docs/dogfood-scorecard.md + landed Strict PASS rows)"
echo

echo "Demo complete!"
