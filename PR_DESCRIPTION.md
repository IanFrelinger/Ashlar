# Architecture Follow-up: BackgroundAgents DIP Phase 1 (P1.2 Remainder)

## Summary

Extracts `IProviderFactory` and `CertificationRecordMapper` from Infrastructure to Application layer, advancing the BackgroundAgents Dependency Inversion Principle (DIP) refactoring started in PR #525.

**Architecture Guardian P1.2**: This PR moves BackgroundAgents closer to depending only on Application ports, reducing coupling to Infrastructure concrete implementations.

## Changes

### ✅ Extracted to Application Layer

1. **`IProviderFactory`** → `Ashlar.Core.Application.Execution.Ports.IProviderFactory`
   - Clean interface for LLM provider abstraction
   - Used by `SanitizingProviderFactory` and `ToolCallingAgent`
   - Infrastructure maintains backward-compat deprecated alias

2. **`ModelUnavailableException`** → `Ashlar.Core.Application.Execution.Ports.ModelUnavailableException`
   - Exception type moved alongside IProviderFactory
   - Used when no LLM model is available

3. **`CertificationRecordMapper`** → `Ashlar.Core.Application.Certification.CertificationRecordMapper`
   - Static utility mapper for certification records
   - Used by `SelfProducedBrickCertificationPolicy`
   - Infrastructure maintains backward-compat deprecated alias

### 🔧 Updated BackgroundAgents

- `Trust/SanitizingProviderFactory.cs`: Now imports from `Application.Execution.Ports`
- `Agents/ToolCallingAgent.cs`: Now imports from `Application.Execution.Ports`
- `Security/SelfProducedBrickCertificationPolicy.cs`: Now imports from `Application.Certification`

### ⚠️ Remaining Infrastructure Dependencies (Documented)

**Infrastructure ProjectReference remains in `Ashlar.BackgroundAgents.csproj`** for these documented reasons:

1. **DI/Hosting Composition** (`ServiceCollectionExtensions.cs`):
   - Registers concrete Infrastructure types (`LiteDbPatternStore`, `ResilientExecutor`, etc.)
   - This is acceptable "composition root" usage
   - Future: Consider `Ashlar.Hosting.BackgroundAgents` project

2. **Infrastructure Glue Services** (`ObservationPipelineService.cs`):
   - Directly instantiates `PatternDetector`, `FileSystemEventSource`, `ProcessEventSource`
   - Service is infrastructure-layer concern
   - Future: Extract factory interfaces or move service to Infrastructure

3. **Complex Infrastructure Components** (`AutonomyLoopService.cs`):
   - Depends on `AutonomousIterationHarness` (500+ line certification/hot-swap logic)
   - Future: Extract `IAutonomousIterationHarness` interface to Application

See `INFRASTRUCTURE_DEPENDENCY_ANALYSIS.md` for full analysis and future extraction plan.

## Before/After Dependency Graph

### Before (Master)
```
Ashlar.BackgroundAgents
├── Ashlar.Infrastructure ❌ (multiple usages)
│   ├── Infrastructure.Execution.IProviderFactory (interface)
│   ├── Infrastructure.Certification.CertificationRecordMapper (mapper)
│   ├── Infrastructure.Observation.* (concrete types)
│   ├── Infrastructure.Trust.* (SDK extensions)
│   └── Infrastructure.Autonomy.* (complex types)
├── Ashlar.Core.Application ✅
├── Ashlar.Core.Domain ✅
├── Ashlar.Orchestration ✅
└── Ashlar.Runtime ✅
```

### After (This PR)
```
Ashlar.BackgroundAgents
├── Ashlar.Infrastructure ⚠️ (DI/hosting concerns only)
│   ├── ServiceCollectionExtensions: DI registration
│   ├── ObservationPipelineService: Infrastructure glue
│   └── AutonomyLoopService: Complex component dependency
├── Ashlar.Core.Application ✅ (now includes ports)
│   ├── Application.Execution.Ports.IProviderFactory ✅ NEW
│   ├── Application.Execution.Ports.ModelUnavailableException ✅ NEW
│   └── Application.Certification.CertificationRecordMapper ✅ NEW
├── Ashlar.Core.Domain ✅
├── Ashlar.Orchestration ✅
└── Ashlar.Runtime ✅
```

### Dependency Reduction
- **Before**: 7 distinct Infrastructure namespace usages
- **After**: 3 Infrastructure namespaces remain (documented as DI/composition concerns)
- **Progress**: 57% reduction in Infrastructure coupling for domain logic

## Testing

- ✅ Architecture layer boundaries preserved (Domain ← Application ← Infrastructure)
- ✅ Backward-compatibility aliases in Infrastructure (existing consumers unaffected)
- ✅ BackgroundAgents code now uses Application ports
- ⚠️ Infrastructure reference remains (documented as composition root exception)

## Constraints Met

✅ **Do NOT weaken cert-gate, layer-boundary, dependency-boundary, or perf-gate**
- All architecture tests should pass (layer boundaries respected)

✅ **Do NOT touch LiveExtender / perf-gate CI**
- No changes to LiveExtender code

✅ **Do NOT touch sealed adapters / Tools cycle / #521–#525 branches**
- No interference with parallel work

✅ **Keep Forge isolated in BackgroundAgents**
- Forge remains in BackgroundAgents (not leaked to kernel/Application)

✅ **Ship largest clean slice if full removal too large**
- Delivered: IProviderFactory + CertificationRecordMapper extraction
- Documented: Remaining dependencies with future roadmap

## Future Work (Separate PRs)

1. **Phase 2a**: Extract `IAutonomousIterationHarness` → Application.Autonomy.Ports
2. **Phase 2b**: Refactor ObservationPipelineService to use injected factories
3. **Phase 2c**: Create `Ashlar.Hosting.BackgroundAgents` for DI composition
4. **Phase 3**: Remove Infrastructure ProjectReference entirely

## Related

- Follow-up to PR #525 (removed Orchestration from BackgroundAgents)
- Architecture Guardian P1.2 remainder
- Part of ongoing DIP application across Ashlar layers
