# Dogfood Validation (North Star Gates)

Ashlar's North Star: every capability must be used by Ashlar on itself. Each block has a dogfood gate that must pass before moving on.

## Block 1: Core Observation Pipeline

**Gate:** Ashlar's own development workflow is being observed before moving to Block 2.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodBlock1Tests"`
- Or `make dogfood-block1` (see Makefile)
- The test creates file events under `src/`, runs the observation pipeline, and verifies patterns (e.g. `repeated-edits`) are stored
- When not in Ashlar repo (no `Ashlar.sln`), the test skips

**Status:** Implemented. `DogfoodBlock1Tests.ObservationPipeline_WhenRunInAshlarRepo_StoresPatternsFromOwnFileEvents` validates the gate.

## Block 2: Code Analyzers

**Gate:** Analyzer has run against Block 1 code and surfaced at least one actionable improvement.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodBlock2Tests"`
- Or `make dogfood-block2`
- The test runs `IBrickStaticAnalyzer` against `RepoPathResolver.FindBlock1ObservationPath()` (Observation pipeline code)
- Passes when analyzer completes; violations (if any) are actionable improvements

**Status:** Implemented. `DogfoodBlock2Tests.StaticAnalyzer_RunAgainstBlock1ObservationCode_CompletesAndSurfacesOrConfirms` validates the gate.

## Block 3: Adaptation Engine

**Gate:** Adaptation engine has successfully improved at least one of its own bricks.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodBlock3Tests"`
- Or `make dogfood-block3`
- The test decomposes and recompiles ObservationContextBrick (a Ashlar brick from Block 1)

**Status:** Implemented. `DogfoodBlock3Tests.AdaptationEngine_DecomposeAndRecompileAshlarBrick_Succeeds` validates the gate.

## Block 4: Inheritance / Promote Fixes

**Gate:** Promote one Ashlar fix via inheritance.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodBlock4Tests"`
- Or `make dogfood-block4`
- The test uses IAdaptationPromoter to record a promoted fix for observation.context

**Status:** Implemented. `DogfoodBlock4Tests.PromoteAshlarFix_ViaInheritance_RecordStoredWithPromotedTrue` validates the gate.

## Block 5: Autonomy Controls

**Gate:** Use autonomy controls on Ashlar dev workflow.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodBlock5Tests"`
- Or `make dogfood-block5`
- The test logs an AdaptationAuditEntry with autonomy level for a Ashlar file

**Status:** Implemented. `DogfoodBlock5Tests.AutonomyControls_LogAshlarDevWorkflowAdaptation_EntryStoredAndQueryable` validates the gate.

## Block 6: Self-Context (24h Question)

**Gate:** SelfContextAssembler answers "what did I change in 24h?".

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodBlock6Tests"`
- Or `make dogfood-block6`
- The test seeds adaptations, executions, patterns and asserts AssembleAsync(24h) returns the summary

**Status:** Implemented. `DogfoodBlock6Tests.SelfContextAssembler_With24hLookback_ReturnsWhatChangedIn24h` validates the gate.

## Block 7: Composition Engine

**Gate:** Compose an agent from capability components for a Ashlar-related problem.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodBlock7Tests"`
- Or `make dogfood-block7`
- The test runs ICompositionEngine.ComposeAsync("test Ashlar CLI") and asserts a pipeline is returned

**Status:** Implemented. `DogfoodBlock7Tests.CompositionEngine_ComposeForTestAshlarCli_ReturnsPipeline` validates the gate.

## Block 8: Parallel Test Matrix

**Gate:** Run parallel test matrix against Ashlar tests.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodBlock8Tests"`
- Or `make dogfood-block8`
- The test spawns 1 instance with DogfoodBlock1Tests filter, aggregates results

**Status:** Implemented. `DogfoodBlock8Tests.ParallelTestMatrix_RunAgainstAshlarTests_CompletesAndAggregates` validates the gate.

### Phase D: Composition-Driven Testing (Block 7–8)

**Gate:** Test agents are composed from components; Ashlar's suite runs through composed agents.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodBlock8ComposedTests"`
- Or `make dogfood-block8-composed`
- The test uses IComposedTestRunner to run Ashlar tests via composition (test-discovery → test-execution → result-aggregation)

**Status:** Implemented. `DogfoodBlock8ComposedTests.ComposedTestRunner_RunAshlarTestsViaComposition_CompletesAndAggregates` validates the gate.

## Block 9: Instance Mesh

**Gate:** Instance discovery and capability advertisement for Ashlar.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodBlock9Tests"`
- Or `make dogfood-block9`
- The test advertises ashlar-cli/ashlar-dogfood capabilities, discovers peers, finds by capability

**Status:** Implemented. `DogfoodBlock9Tests.InstanceMesh_AdvertiseAndDiscover_ReturnsAshlarPeer` validates the gate.

### Phase E: Local IPC Mesh (Block 9)

**Gate:** Two Ashlar instances on the same machine share a capability via local IPC. One instance requests, the other fulfills, artifact is transferred and validated.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodBlock9LocalIpcTests"`
- Or `make dogfood-block9-ipc`
- The test spawns two in-process instances (fulfiller and requester), fulfiller registers a handler, requester requests the capability, artifact is transferred and validated

**Status:** Implemented. `DogfoodBlock9LocalIpcTests.TwoInstances_LocalIpc_RequestFulfilledAndArtifactValidated` validates the gate.

## Phase F: Continuous Self-Improvement Loop

**Gate:** The improve flow (observe → analyze → adapt) runs on Ashlar.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodClosedLoopTests"`
- Or `make dogfood-closedloop`
- The test runs IBrickStaticAnalyzer.AnalyzeSourceAsync on Block 1 Observation path (same services as `ashlar improve`)

**CI:** Use scope `dogfood` to run all DogfoodBlock* and DogfoodClosedLoop tests:
- `gh workflow run "Cross-Platform Tests" -f scope=dogfood`

**Status:** Implemented. `DogfoodClosedLoopTests.ImproveFlow_AnalyzeBlock1Path_Completes` validates the gate.

### Phase F (continued): Changelog and Test Failure Store

**Gate:** Changelog generated from promoted changes; test failures stored for adaptation trigger.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodPhaseFTests"`
- Or `make dogfood-phasef`
- Run `ashlar changelog --since 7d` to generate changelog from promoted adaptation records

**Status:** Implemented. `DogfoodPhaseFTests.ChangelogGenerator_WithPromotedRecords_GeneratesMarkdown` and `DogfoodPhaseFTests.TestFailureStore_RecordAndQuery_ReturnsStoredFailures` validate the gate.

## Automated dogfood campaign (release manager + specialists)

**Gate:** A release-manager coordinator dispatches specialist sub-agents for documentation drift, regressions, and developer-tool usability. Every specialist must report back; silence is a blocker.

**Validation:**
- Run `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~DogfoodCampaignTests"`
- Or `make dogfood-campaign` / `bash scripts/run-dogfood-campaign.sh` (enters the dev/test container; the SDK lives there)
- Specialists: `docs-drift` (docs-auditor), `regression` (tester), `dev-tool` (dev-tool-auditor). Agent set: `docs/background-agents/examples/dogfood-campaign.json`. Operator page: [DogfoodCampaign.md](DogfoodCampaign.md).

**Status:** Implemented. `DogfoodCampaignTests.Campaign_specialists_report_to_the_release_manager_against_this_repo` validates the gate.
