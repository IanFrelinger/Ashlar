# BackgroundAgents DIP Refactoring Analysis

## Current State (Before)

### BackgroundAgents Dependencies
```
Ashlar.BackgroundAgents.csproj references:
- Ashlar.Infrastructure ❌ (to be removed)
- Ashlar.Abstractions ✅
- Ashlar.Core.Application ✅
- Ashlar.Core.Domain ✅
- Ashlar.Orchestration ✅
- Ashlar.Runtime ✅
```

### Infrastructure Usages in BackgroundAgents

#### 1. Ashlar.Infrastructure.Execution
**Files**: 
- `Trust/SanitizingProviderFactory.cs`
- `Agents/ToolCallingAgent.cs`

**Types Used**:
- `IProviderFactory` - Main abstraction for LLM providers
- `ModelUnavailableException` - Exception type

**Action**: Move `IProviderFactory` interface to `Ashlar.Core.Application/Execution/Ports/IProviderFactory.cs`

#### 2. Ashlar.Infrastructure.Certification
**Files**:
- `Security/SelfProducedBrickCertificationPolicy.cs`

**Types Used**:
- `CertificationRecordMapper.ToData()` - Static mapper utility
- Already has: `ICertificationRecordStore` in Application ✅

**Action**: Move `CertificationRecordMapper` to `Ashlar.Core.Application/Certification/CertificationRecordMapper.cs` (utility)

#### 3. Ashlar.Infrastructure.Observation
**Files**:
- `Observation/ObservationPipelineService.cs`
- `ServiceCollectionExtensions.cs`

**Types Used**:
- `PatternDetector`
- `FileSystemEventSource`
- `ProcessEventSource`  
- `CompositeEventSource`
- `LiteDbPatternStore`
- `LiteDbPatternProcessedStore`

**Ports Already Exist**: ✅
- `IPatternStore` in Application.Observation.Ports
- `IPatternProcessedStore` in Application.Observation.Ports
- `IObservableEventSource` in Application.Observation.Ports

**Action**: These are concrete implementations used only in DI registration. Move registration to Hosting layer.

#### 4. Ashlar.Infrastructure.Trust
**Files**:
- `ServiceCollectionExtensions.cs`

**Types Used**:
- Extension methods: `AddUserKnowledgeLog()`, `AddAccessBoundary()`, `AddCloudAvailabilityResolver()`

**Action**: Keep these as they're SDK extensions for DI registration. BackgroundAgents can re-export or delegate.

#### 5. Ashlar.Infrastructure.Autonomy
**Files**:
- `Autonomy/AutonomyLoopService.cs`

**Types Used**:
- `AutonomousIterationHarness`

**Action**: Check if this should be an Application port or if AutonomyLoopService should move to Infrastructure.

#### 6. Ashlar.Infrastructure.Certification.HotSwap
**Files**:
- `Autonomy/AutonomyLoopService.cs`

**Types Used**:
- Types from HotSwap namespace

**Action**: Analyze and extract ports if needed.

## Target State (After)

### BackgroundAgents Dependencies
```
Ashlar.BackgroundAgents.csproj references:
- Ashlar.Abstractions ✅
- Ashlar.Core.Application ✅
- Ashlar.Core.Domain ✅
- Ashlar.Orchestration ✅
- Ashlar.Runtime ✅
```

### New Application Ports
1. `Ashlar.Core.Application/Execution/Ports/IProviderFactory.cs`
2. `Ashlar.Core.Application/Certification/CertificationRecordMapper.cs` (utility)

### Hosting Layer Changes
- DI registrations for Observation concrete types move to appropriate hosting composition root

## Blast Radius Assessment

**Low Risk**:
- Moving IProviderFactory (interface only, implementations stay in Infrastructure)
- Moving CertificationRecordMapper (pure utility, no state)

**Medium Risk**:
- Reorganizing DI registrations in ServiceCollectionExtensions
- May need to keep some registrations in BackgroundAgents for backwards compat

**High Risk** (defer if too large):
- Moving AutonomyLoopService if it has deep Infrastructure dependencies
- Large-scale refactoring of Observation pipeline registration

## Strategy

1. **Phase 1** (this PR): Extract clean ports with minimal blast radius
   - Move IProviderFactory to Application.Execution.Ports
   - Move CertificationRecordMapper to Application.Certification
   - Update BackgroundAgents to use new ports
   - Remove Infrastructure reference

2. **Phase 2** (future PR if needed): Handle remaining registrations
   - Move complex DI registrations to Hosting if blast radius was too large in Phase 1
   
## Constraints (from task)
- ✅ Do NOT weaken cert-gate, layer-boundary, dependency-boundary, or perf-gate
- ✅ Do NOT touch LiveExtender / perf-gate CI
- ✅ Do NOT touch sealed adapters / Tools cycle / #521-#525 branches
- ✅ Keep Forge isolated in BackgroundAgents — do not leak Forge into kernel
