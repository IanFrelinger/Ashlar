# InfrastructurePipelinesGapCoverageTests Fix Demonstration

## Problem Statement

Two tests in `InfrastructurePipelinesGapCoverageTests` were failing in CI run 34288712315:
- `DefaultAgenticStageExecutionAdapter_executes_stage` 
- `DefaultDeterministicStageExecutionAdapter_executes_stage`

**Error:** `Expected boolean to be False, but found True`  
**Location:** Line 25 and 41 of InfrastructurePipelinesGapCoverageTests.cs

## Root Cause Analysis

### The Adapter Implementation

Both `DefaultAgenticStageExecutionAdapter` and `DefaultDeterministicStageExecutionAdapter` have dual behavior:

```csharp
// From DefaultAgenticStageExecutionAdapter.cs, lines 34-52
var allowMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
if (string.Equals(allowMock, "1", StringComparison.Ordinal))
{
    // MOCK MODE: Return success for CI perf gates
    return Task.FromResult(new PipelineStageExecutionResult
    {
        Succeeded = true,  // ← Tests expect false, but get this
        // ...
    });
}

// PLACEHOLDER MODE: Return honest failure
return Task.FromResult(new PipelineStageExecutionResult
{
    Succeeded = false,  // ← What tests expect
    // ...
});
```

### The CI Environment

The Full Platform Readiness Gate workflow sets `ASHLAR_ALLOW_MOCK=1` globally:

```yaml
# .github/workflows/full-platform-readiness-gate.yml, line 57
env:
  ASHLAR_ALLOW_MOCK: 1
```

### The Test Expectation

The tests were written to verify **placeholder behavior** (honest failure):

```csharp
// Line 25
result.Succeeded.Should().BeFalse();  // Expects placeholder: false
```

### The Mismatch

**Tests run in CI** → `ASHLAR_ALLOW_MOCK=1` is set → adapters return `Succeeded=true` (mock mode)  
**Tests expect** → Placeholder behavior → `Succeeded=false`  
**Result** → Test failure: "Expected False, but found True"

## The Fix

Isolate tests from the CI environment by explicitly clearing `ASHLAR_ALLOW_MOCK` during test execution:

```csharp
[Fact]
public async Task DefaultAgenticStageExecutionAdapter_executes_stage()
{
    // CI may set ASHLAR_ALLOW_MOCK=1 for perf gates; clear it to test the placeholder path.
    var prevMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
    try
    {
        Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", null);
        
        // Test the adapter - now guaranteed to take placeholder path
        var adapter = new DefaultAgenticStageExecutionAdapter(...);
        var result = await adapter.ExecuteAsync(...);
        
        result.Succeeded.Should().BeFalse();  // ✓ Now passes
        // ...
    }
    finally
    {
        Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prevMock);
    }
}
```

## Why This Pattern?

This follows the established environment isolation pattern used throughout the codebase:
- `RemoteExecutionSafetyTests.cs` (lines 87, 99)
- `OnboardingE2ETests.cs` (multiple locations)
- ~20+ other test classes in Ashlar.Tests.Infrastructure

## Verification Logic

### Before Fix (with ASHLAR_ALLOW_MOCK=1)

```
Environment: ASHLAR_ALLOW_MOCK=1
    ↓
Adapter.ExecuteAsync()
    ↓
Check: allowMock == "1" ? YES
    ↓
Return: Succeeded = true (mock mode)
    ↓
Test Assert: Succeeded.Should().BeFalse()
    ↓
Result: ❌ FAIL - Expected False, got True
```

### After Fix (ASHLAR_ALLOW_MOCK cleared in test)

```
Test Setup: Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", null)
    ↓
Adapter.ExecuteAsync()
    ↓
Check: allowMock == "1" ? NO (null)
    ↓
Return: Succeeded = false (placeholder mode)
    ↓
Test Assert: Succeeded.Should().BeFalse()
    ↓
Result: ✓ PASS - Expected False, got False
```

## Impact Analysis

**Blast Radius:** Test-only change  
**Production Code:** No changes to adapter implementations  
**CI Workflows:** No changes needed - mock mode still available for perf gates  
**Test Coverage:** Tests now correctly verify placeholder behavior in all environments

## Contract Verification

The fix ensures tests verify the documented contract:

> "The default adapter is a placeholder that performs no work. It must report FAILURE,
> not fabricated success — otherwise `ashlar pipeline run` claims stages ran when they
> did not."

## Compatibility

- ✓ Mock mode (`ASHLAR_ALLOW_MOCK=1`) still works for CI perf gates
- ✓ Placeholder mode now correctly tested in all environments
- ✓ Tests are environment-independent and deterministic
- ✓ No breaking changes to adapter interfaces or behavior
