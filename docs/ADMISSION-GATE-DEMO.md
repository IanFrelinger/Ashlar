# Admission Gate Demo

**Location:** This repository demonstrates Ashlar's artifact certification and admission gates.

**Purpose:** Show how Ashlar REJECTS weak artifacts and ADMITS strong ones, with verifiable proof.

## What This Proves

✓ **Certification gates exist** and enforce mutation + witness testing  
✓ **Weak artifacts are REJECTED** with signed proof of failure  
✓ **Strong artifacts are ADMITTED** with verifiable records  
✓ **Content hashes + signatures** provide an audit trail  

## What This Does NOT Prove

- Pre-execution admission for Copilot chat tasks (that integration is a follow-up)
- Full autonomy unlock (requires provider setup + learning mode)
- Production-ready self-extending agents (see CLOSING-PLAN.md Phase 3-4)

## Two Ways to Run the Demo

### Option 1: Test Suite (Fastest)

Run the admission gate demo through the certification test suite:

```bash
# From repository root
dotnet test src/Ashlar.Tests.Infrastructure \
  --filter "FullyQualifiedName~AdmissionGateDemoTests" \
  --logger "console;verbosity=normal"
```

**What you'll see:**
- Phase 1: Weak witness → REJECT (escape rate > 0)
- Phase 2: Strong witness → ADMIT (escape rate = 0)
- Phase 3: Correlation proof (same artifact, different outcomes)

Each test prints human-readable output with:
- Brick IDs and content hashes
- Escape rates and mutation counts
- Signed records with signatures
- Plain English explanations of why each verdict was reached

### Option 2: Standalone Script

Run the full demo as a standalone script (builds artifact, runs gate, shows records):

```bash
# From repository root
bash scripts/demo-admit-reject.sh
```

**Requirements:**
- .NET SDK 8.0+ (for building the CLI)
- `jq` (for JSON parsing in the output)
- Ashlar.CLI built: `dotnet build application/src/Ashlar.CLI/Ashlar.CLI.csproj`

**What the script does:**
1. Creates a demo MutationProbeBrick (log scanner)
2. Runs it through the gate with a WEAK witness → REJECT
3. Runs it again with a STRONG witness → ADMIT
4. Shows both records side-by-side with correlation proof

**Output includes:**
- Full brick source code (created in `.demo-admission/`)
- Witness specifications (weak vs strong)
- Certification command output
- Rejection and admission records (JSON)
- Correlation proof (same ID, same hash, different outcomes)

## Demo Artifact: MutationProbeBrick

The demo uses a simple log scanner brick that:
- **Input:** Raw log text
- **Outputs:**
  - `errorCount` — number of ERROR lines found
  - `firstErrorMessage` — message from the first ERROR line

### Weak Witness (Incomplete)

Only checks `errorCount`:

```json
{
  "brick": "mutation-probe-brick",
  "cases": [{
    "input": {
      "logText": "2024-01-01 ERROR First failure: connection reset\n..."
    },
    "expectedOutput": {
      "errorCount": 2
    }
  }]
}
```

**Result:** REJECT  
**Why:** Mutations that break `firstErrorMessage` extraction go undetected → survivors escape → escape rate > 0

### Strong Witness (Complete)

Checks `errorCount` AND `firstErrorMessage`:

```json
{
  "brick": "mutation-probe-brick",
  "cases": [{
    "input": {
      "logText": "2024-01-01 ERROR First failure: connection reset\n..."
    },
    "expectedOutput": {
      "errorCount": 2,
      "firstErrorMessage": "First failure: connection reset"
    }
  }]
}
```

**Result:** ADMIT  
**Why:** All mutations to both outputs are detected → all mutants killed → escape rate = 0

## Understanding the Records

### Rejection Record (Weak Witness)

```json
{
  "brickId": "mutation-probe-brick",
  "contentHash": "sha256:...",
  "escapeRate": 0.33,
  "totalMutants": 12,
  "killedMutants": 8,
  "survivingMutants": 4,
  "signed": true,
  "signature": "...",
  "status": "FAIL",
  "stage": "mutation"
}
```

**Key fields:**
- `escapeRate > 0` → Gate REJECTS
- `survivingMutants: 4` → Weak witness let these escape
- `signed: true` → Record is verifiable (not forged)

### Admission Record (Strong Witness)

```json
{
  "brickId": "mutation-probe-brick",
  "contentHash": "sha256:...",
  "escapeRate": 0.0,
  "totalMutants": 12,
  "killedMutants": 12,
  "survivingMutants": 0,
  "signed": true,
  "signature": "...",
  "status": "PASS",
  "gatesPassed": [
    {"name": "analyzer-gate", "version": "1"},
    {"name": "correctness-witness", "version": "1"},
    {"name": "mutation-gate", "version": "1"},
    {"name": "determinism", "version": "1"}
  ]
}
```

**Key fields:**
- `escapeRate = 0` → Gate ADMITS
- `survivingMutants: 0` → Strong witness killed all mutants
- `gatesPassed` → Record shows which gates were passed
- Both records share the same `brickId` and `contentHash` → correlation proof

## Correlation Proof

Both records certify the **SAME artifact**:
- Same `brickId`: `mutation-probe-brick`
- Same `contentHash`: `sha256:...` (identical source code)
- Different outcomes based on witness strength

This proves:
1. The gate is not a rubber stamp — same artifact, different witnesses → different verdicts
2. Records are correlated by ID and hash → audit trail is verifiable
3. Signatures prevent forgery → records cannot be tampered with

## Next Steps

### For Testers
- Run the demo test suite: `dotnet test ... --filter AdmissionGateDemoTests`
- Inspect the records in `.demo-admission/` after running the script
- Try modifying the witness specs to see how escape rate changes

### For Integrators
- See **docs/certification-evidence.md** for all proven ADMIT/REJECT decisions
- See **docs/trust-loop/ashlar-trust-loop-spec.md** for gate invariants
- See **samples/hello-brick/README.md** to author your own certifiable bricks

### For Contributors
- Certification tests: `src/Ashlar.Tests.Infrastructure/Tests/Certification/`
- Gate implementation: `src/Ashlar.Infrastructure/Certification/CertificationGate.cs`
- CLI entry point: `application/src/Ashlar.CLI/Commands/CertifyCommand.cs`

## Honest Positioning

**This demo proves artifact admission gates work.**

What it does NOT prove:
- Pre-execution admission for Copilot chat tasks — that integration is tracked in CLOSING-PLAN.md Phase 3-4
- Full autonomy unlock — requires provider wizard, learning mode, and operator policy configuration
- Production-ready self-extending agents — shipped in hold mode by default (see docs/RunningASelfExtendingNode.md)

The Copilot task path currently records AFTER execution. Wiring the admission gate into `/api/copilot/task` is a separate product change.

## Related Documentation

- **CLOSING-PLAN.md** — Autonomy phases and admission gate roadmap
- **docs/certification-evidence.md** — Ledger of all proven ADMIT/REJECT decisions
- **docs/trust-loop/ashlar-trust-loop-spec.md** — Gate invariants and certification contract
- **docs/AuthoringBricks.md** — How to write bricks the gate can certify
- **ci/cert-gate-assertions.md** — What the cert-gate CI check enforces
