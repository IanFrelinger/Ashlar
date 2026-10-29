# Findings and Recommendations

This document tracks observations from building and using the PR Blocker Assistant as an Ashlar 0.3.0 consumer.

## What Ashlar 0.3.0 Does Well

### Package Consumption
- ✅ Clean nuget.org publishing - all packages restore without issues
- ✅ Central package management works as documented
- ✅ Clear separation of concerns (Authoring, Hosting, Contracts)

### Brick Pattern
- ✅ `DomainBrick` base class provides clean authoring surface
- ✅ `BrickInput`/`BrickOutput` with typed `Get<T>` and `Set` methods
- ✅ `IExecutionContext` provides necessary execution metadata

### Hosting Integration
- ✅ `AddAshlar()` + `AddAshlarBrick<T>()` pattern is straightforward
- ✅ Works well with standard Microsoft.Extensions.Hosting patterns
- ✅ `IBrickRegistry` and `IBrickExecutor` provide clean runtime access

## Framework Gaps Discovered

### 1. GitHub Integration Missing

**Gap**: No built-in GitHub API integration or bricks

**Impact**: Had to add Octokit as external dependency and implement all GitHub logic from scratch

**Recommendation for 0.4.0**:
- Consider adding `Ashlar.Bricks.GitHub` package with:
  - PR status checker brick
  - Release analyzer brick  
  - Commit status aggregator brick
  - Review status brick
- Or document integration pattern for common APIs (GitHub, GitLab, etc.)

### 2. No Built-in CLI Pattern

**Gap**: No guidance or helper for building CLI tools that execute bricks

**Impact**: Had to figure out:
- How to get `IBrickExecutor` from DI container
- How to create proper `IExecutionContext` for CLI usage
- Argument parsing and brick input mapping

**Recommendation for 0.3.x or 0.4.0**:
- Add `Ashlar.CLI.Hosting` package with:
  - CLI-specific host builder extensions
  - Argument parsing helpers that map to `BrickInput`
  - Simple `ExecutionContext` implementation for CLI scenarios
- Or add a sample showing this pattern

### 3. Limited Brick Discovery at Runtime

**Gap**: `IBrickRegistry.GetAll()` returns metadata, but finding specific capabilities requires knowing IDs upfront

**Impact**: CLI tools need to hardcode brick IDs

**Recommendation**:
- Add capability-based discovery (find bricks by category, tags, or input/output signatures)
- Add brick metadata validation at registration time

### 4. Async Result Presentation

**Gap**: No built-in formatters for brick output

**Impact**: Each consumer implements their own output formatting

**Recommendation**:
- Add common output formatters (JSON, Markdown, plain text)
- Add streaming output support for long-running bricks
- Document patterns for progress reporting

### 5. Error Handling and Diagnostics

**Gap**: Limited guidance on error handling in brick execution

**What we did**: Bare try-catch in brick with generic error messages

**Recommendation**:
- Standardize error response format in `BrickOutput`
- Add structured diagnostics support
- Document retry patterns for external API calls

## Real Usage Questions (To Be Answered)

After 10 real assessments, answer:

1. **Accuracy**: How often does the tool correctly identify blockers?
2. **Completeness**: What does it miss that humans catch?
3. **False Positives**: What does it flag that isn't actually blocking?
4. **Time Saved**: How much faster than manual checking?
5. **Reliability**: Does it handle rate limits, timeouts, auth failures gracefully?

## Recommendations Summary

### For Ashlar 0.3.x (patch/minor)
1. Add CLI hosting sample to samples/
2. Document external API integration patterns
3. Add `SimpleExecutionContext` utility class to `Ashlar.Hosting.Bundle`

### For Ashlar 0.4.0 (major)
1. `Ashlar.Bricks.GitHub` - GitHub integration bricks
2. `Ashlar.CLI.Hosting` - CLI-specific hosting helpers
3. Enhanced brick discovery and metadata
4. Standardized output formatters
5. Structured error and diagnostics support

## Notes

- This is a preliminary analysis based on initial implementation
- Real findings will emerge from the 10 real assessments
- Some "gaps" may be intentional design decisions (thin framework, avoid feature bloat)
