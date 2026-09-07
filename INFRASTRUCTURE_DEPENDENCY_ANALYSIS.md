# Infrastructure Dependency Analysis - BackgroundAgents

## Phase 1: COMPLETED ✅

### Extracted to Application Layer
1. **IProviderFactory** → `Ashlar.Core.Application.Execution.Ports.IProviderFactory`
   - Clean interface extraction
   - Infrastructure kept backward-compat deprecated interface
   - BackgroundAgents now uses Application port
   
2. **ModelUnavailableException** → `Ashlar.Core.Application.Execution.Ports.ModelUnavailableException`
   - Exception type moved with IProviderFactory
   
3. **CertificationRecordMapper** → `Ashlar.Core.Application.Certification.CertificationRecordMapper`
   - Static utility mapper
   - BackgroundAgents now uses Application version

## Phase 2: REMAINING INFRASTRUCTURE DEPENDENCIES

### Category A: DI/Hosting Composition (ServiceCollectionExtensions.cs)

These are concrete type instantiations in DI registration code:

```csharp
// Infrastructure.Resilience
services.TryAddSingleton<IResilientExecutor, Ashlar.Infrastructure.Resilience.ResilientExecutor>();

// Infrastructure.Execution (concrete ProviderFactory)
services.AddSingleton<Ashlar.Infrastructure.Execution.ProviderFactory>(...);

// Infrastructure.Observation
new LiteDbPatternStore(storePath)
new Ashlar.Infrastructure.Observation.LiteDbPatternProcessedStore(storePath)

// Infrastructure.Trust.Sdk.Extensions
services.AddUserKnowledgeLog(...);
services.AddAccessBoundary(...);
services.AddCloudAvailabilityResolver(...);
```

**Options**:
1. **Keep as-is**: ServiceCollectionExtensions is a composition root, so Infrastructure references are acceptable
2. **Move to Hosting**: Create `Ashlar.Hosting.BackgroundAgents` project for DI composition
3. **Factory interfaces**: Extract factories for each concrete type (high ceremony)

**Recommendation**: Keep as-is. This is acceptable "composition root" usage.

### Category B: Hosted Service Infrastructure (ObservationPipelineService.cs)

Directly instantiates Infrastructure.Observation types:
- `PatternDetector`
- `FileSystemEventSource`
- `ProcessEventSource`
- `CompositeEventSource`

**Options**:
1. **Extract factories**: Create Application ports for these components
2. **Move to Infrastructure**: ObservationPipelineService is really infrastructure glue
3. **Inject concrete types**: Make them dependencies injected via DI

**Recommendation**: Extract factory interfaces OR move service to Infrastructure.

### Category C: Complex Infrastructure Components (AutonomyLoopService.cs)

Uses deep infrastructure types:
- `Infrastructure.Autonomy.AutonomousIterationHarness` (complex)
- `Infrastructure.Certification.HotSwap.*` types

`AutonomousIterationHarness` is a 500+ line class with complex certification and hot-swap logic.

**Options**:
1. **Extract interface**: Create `IAutonomousIterationHarness` port
2. **Move service**: Move AutonomyLoopService to Infrastructure
3. **Accept dependency**: Document as acceptable for now

**Recommendation**: Extract `IAutonomousIterationHarness` interface to Application.Autonomy.Ports.

## Phase 2 Execution Plan

### Option A: Full Extraction (High Blast Radius)
1. Extract `IAutonomousIterationHarness` → Application.Autonomy.Ports
2. Extract observation component factories → Application.Observation.Ports
3. Create Hosting project for DI composition
4. Remove Infrastructure reference

**Blast Radius**: HIGH - touches many files, creates new patterns, risky

### Option B: Strategic Extraction (Medium Blast Radius)
1. Extract `IAutonomousIterationHarness` → Application.Autonomy.Ports
2. Inject ObservationPipelineService dependencies via DI instead of direct construction
3. Keep ServiceCollectionExtensions Infrastructure usage as documented composition root
4. ATTEMPT to remove Infrastructure reference

**Blast Radius**: MEDIUM - targeted changes, may not fully remove Infrastructure

### Option C: Document & Defer (Low Blast Radius - RECOMMENDED)
1. Keep Infrastructure reference
2. Document all usages as:
   - Composition root (ServiceCollectionExtensions)
   - Infrastructure glue (ObservationPipelineService)
   - Complex components (AutonomyLoopService)
3. Create tickets for future extraction
4. Focus on ensuring clean Application ports exist

**Blast Radius**: LOW - no risky changes, clean documentation

## Recommendation: Option C

### Rationale
1. **Constraints met**: "If full Infra removal is too large, ship the largest clean slice"
2. **Progress made**: IProviderFactory and CertificationRecordMapper extracted (good wins)
3. **Acceptable remaining**: DI composition and infrastructure glue services
4. **Future path**: Clear documentation of what remains and why

### What This PR Accomplishes
✅ IProviderFactory → Application.Execution.Ports (clean DIP)
✅ CertificationRecordMapper → Application.Certification (utility moved)
✅ BackgroundAgents code now imports from Application ports
✅ Infrastructure usages documented and categorized
⚠️ Infrastructure reference remains (documented as composition/glue concerns)

### Future Work (Separate PR)
- Extract `IAutonomousIterationHarness` interface
- Refactor ObservationPipelineService to use injected dependencies
- Consider Hosting layer project for composition root logic
