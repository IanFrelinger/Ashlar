# Scout S2-labels: where labels come from, and who the subject is

SPEC-007 PR 4 design pass. Read-only on `$SP/pr4-base` at `8ec674d2a`. Every `file:line` below is at that commit. Nothing was built or run.

**Short version.**

- No production code enters an `EgressSubject` frame or builds a `HighWaterMark`. If the guard simply starts enforcing, AirGapped and SecureWorkstation become **host-only**: every non-host decision is refused with `SystemHighData`.
- Only one production path hands an agent data *with its label attached*: RAG search results. Every other read the agent loop makes is unlabelled.
- **Any frame lowers the current label.** That makes every producer a security-relevant opening. The current API has three ways to open a leak:
  - a `Public` default floor;
  - nested frames, where the inner one hides the outer one;
  - no way for a reader to report a label into the ambient frame.
- **Recommendation:**
  - keep `no-subject = SystemHigh`;
  - instrument exactly one boundary in PR 4: the agent ReAct cycle;
  - observe RAG labels through the PR 2 bridge, and treat every other read as SystemHigh;
  - fix the nesting rule first;
  - seed the leak test with a RAG document that was classified Secret when it was indexed.
- Do not use `MaxDataSensitivity` or barrier levels as label sources. That would answer §8 Q3 and take PR 5's job.

---

## 1. Facts

### 1.1 No production subject frame (verifies brief gap 4)

| # | Fact | Citation |
|---|---|---|
| F1 | The frame API is `EgressSubject.Enter(string subjectId, HighWaterMark mark)`, which returns an `IDisposable`. The frame lives in an `AsyncLocal<Frame?>`. | `src/Ashlar.Abstractions/Security/Egress/EgressSubject.cs:23`, `:34-44` |
| F2 | `Enter` is called only from tests:<br>• `EgressGuardDecisionTests.cs:261,343,355,383,397-398,410,412,429,454,473,494,503,505,526`<br>• `EgressExplicitSiteTwinTests.cs:310`<br>• `EgressAgentSiteTwinTests.cs:160`<br>• `EgressCliSiteTwinTests.cs:418`<br>Outside tests, `EgressSubject` appears only in `EgressGuard.cs:89` (`Resolve`) and in XML docs (`EgressDecision.cs:84`, `EgressGuard.cs:15`). | grep `EgressSubject` over `src/` and `application/`; test paths under `src/Ashlar.Tests.Infrastructure/Tests/Certification/`, `src/Ashlar.Tests.BackgroundAgents/`, `application/src/Ashlar.Tests.CLI/Tests/Commands/` |
| F3 | No production code constructs a `HighWaterMark` or calls `Observe`. | grep `new HighWaterMark` / `.Observe(` outside tests: only `HighWaterMark.cs` and doc references |
| F4 | With no live frame, `Resolve` returns `(SecurityLabel.SystemHigh, "no-subject")`. | `EgressSubject.cs:47-53` |
| F5 | The guard decides `ReferenceMonitor.CanWrite(current, destinationLabel)`. | `EgressGuard.cs:89-91` |
| F6 | `CanWrite` behaves as follows:<br>• a SystemHigh receiver is always allowed;<br>• otherwise a SystemHigh source is denied with `SystemHighData`, and the detail reads "the subject's current label is SystemHigh, which only a SystemHigh destination may receive". | `src/Ashlar.Abstractions/Security/ReferenceMonitor.cs:53-63` |
| F7 | The destination labels are:<br>• Host: SystemHigh<br>• ExternalModel: Internal<br>• WebSearch: Confidential<br>• NetworkExport: Internal<br>• Unknown: Public<br>A `file:` name is not Host. It takes its family's class: `file.export`, `mesh.*` and `process` are NetworkExport. | `src/Ashlar.Abstractions/Security/Egress/EgressDestinations.cs:56-63`, `:67-95`, `:97-104`, `:180-196` |
| F8 | **So under enforcement, every non-host decision is refused with `SystemHighData`, and every Host decision is allowed.** A cert-gate test already pins this as the no-subject behaviour. | SPEC-007 `:113-114` (`EgressGuardDecisionTests.With_no_subject_a_non_host_destination_is_refused_with_SystemHighData`); "Design as merged" `:352-355` |
| F9 | Concretely, AG and SW would lose all of the following, because every one of them is evaluated outside any frame:<br>• cloud models (EG-MDL-02..08)<br>• web search: `BingWebSearchProvider.cs:56`<br>• workflow webhooks (EG-HTTP-01/02)<br>• remote bricks and peers<br>• RunPod<br>• `pkg pack --out`, publish and share: `PkgCommand.cs:93,389,444`<br>• `export` bundles: `NativeBundle.cs:88`<br>• sneakernet export: `SneakernetTransport.cs:36`<br>• the shared adaptation store: `FileBasedSharedAdaptationStore.cs:57`<br>• the self-extend admission publish: `SelfExtendAdmissionBridge.cs:288`<br>• mesh serve to non-loopback peers: `MeshServeService.cs:313`<br>• the discovery beacon: `MeshDiscoveryService.cs:249`<br>• `dotnet` restore. `DotnetRunner.RestoreDestination` names it `nuget-feeds` (not Host) unless `--no-restore` is given, even when the feed is local.<br>• OTLP to a remote collector: `Program.cs:247`<br>Loopback Ollama and a unix Docker socket stay allowed, because they are Host. | `DotnetRunner.cs:16`, `:52-59`; `docs/EgressInventory.md:208-215`; `$SP/pr3a/risks.md:15` |
| F10 | The default `HighWaterMark()` starts at `Public`, the bottom of the lattice. Entering any frame can therefore only **lower** the current label from SystemHigh. | `src/Ashlar.Abstractions/Security/HighWaterMark.cs:19-23`; `EgressSubject.cs:15-16` |
| F11 | **Nesting replaces; it does not join.** `Enter` links the previous frame only so it can be restored, and `Resolve` reads only the innermost live frame's mark. A cert-gate test pins this: an inner `Public` frame inside an `Internal` frame decides at `Public`. | `EgressSubject.cs:41`, `:47-53`; `EgressGuardDecisionTests.cs:343-362` |
| F12 | Code outside `Ashlar.Abstractions` cannot report a read into the ambient frame. `Frame` is private, `Resolve` is internal, and only the code that entered the frame holds the mark. | `EgressSubject.cs:47`, `:55` |
| F13 | `EgressSubject` and `HighWaterMark` are still in `PublicAPI.Unshipped.txt`, so their semantics can change in PR 4 without a breaking-change process. | `src/Ashlar.Abstractions/PublicAPI.Unshipped.txt:50`, `:55-60`, `:113` |

### 1.2 Inventory: where data carries a label or sensitivity today

| # | Source | What it labels | Who consumes it | Citation |
|---|---|---|---|---|
| L1 | `DataSensitivityLevels`: five primitives with four flags | levels 0-4, plus `AllowsExternalLLM`, `AllowsWebSearch`, `RequiresLocalOnly`, `AllowsNetworkExports` | registry and agent config. The guard's destination table is derived from the flags (F7). | `src/Ashlar.BackgroundAgents/DataSensitivity/DataSensitivityLevels.cs:20-80`; `IDataSensitivityLevel.cs:13-44`; `EgressDestinations.cs:28-30` |
| L2 | Custom levels | per-agent custom levels | registry | `BackgroundAgentConfig.cs:80`; `BackgroundAgentConfigLoader.cs:237-240` |
| L3 | `DataSensitivityMarker`: an object-to-level map; unmarked data is MostRestrictive | arbitrary objects | **nobody**: not registered in DI and not used outside its folder and tests | `DataSensitivityMarker.cs:58`, `:75-83`; grep `IDataSensitivityMarker` |
| L4 | `DataTaxonomy`: maps a data type to a level name. `file-paths`, `file-contents`, `terminal-output` and `behavioral-patterns` are TopSecret; `editor-events`, `git-metadata` and `process-names` are Public; unknown types are TopSecret. | unstructured context | `CloudSanitizationProxy`, which accepts it but **does not consult** it | `DataTaxonomy.json:1-15`; `IDataTaxonomy.cs:7-15`; `CloudSanitizationProxy.cs:18-19`, `:25`; DI `ServiceCollectionExtensions.cs:172` |
| L5 | BackgroundAgents RAG documents: `SensitivityLevelName` per stored document | RAG chunks, **at ingest** (classification at origin) | store filters by clearance. A re-index may not lower a document's label. | `InMemoryVectorStore.cs:20`, `:52`, `:75-82`; `RagSensitivity.cs:16-25`; `KnowledgeBaseIndexer.cs:43-72` |
| L6 | **`RAGTool` returns every result with its `SensitivityLevelName`.** This is the only production path where data reaches an agent *with its label attached*. | agent tool output | the model, as a JSON payload | `src/Ashlar.BackgroundAgents/RAG/RAGTool.cs:104`; `VectorSearchResult.cs:10` |
| L7 | AI.Pipeline RAG: `ChunkRecord.TrustTier`; the search is filtered by the caller's tier. In the kernel, `IRAGService` resolves to `MeaiVectorDataRagAdapter`, which copies `TrustTier` into `SensitivityLevelName`. So in a kernel host, L6's label string is a **trust-tier name**. | RAG chunks | `VectorDataRagService` filter; L6 | `ChunkRecord.cs:29`; `VectorDataRagService.cs:110-114`; `AshlarKernelRegistrar.Phases.cs:428-430`; `MeaiVectorDataRagAdapter.cs:38-52` |
| L8 | PR 2 bridges:<br>• `TrustTierOrder.RecordLabel` (unknown or blank → SystemHigh) and `CallerLabel` (unknown → Public)<br>• `DataSensitivityLabelBridge.ToDataLabel` (null or above 4 → SystemHigh) and `TryToClearance` | maps L1 and L7 onto `SecurityLabel`, level only | **nothing in production** (SPEC-007 `:30`). The bridge's own docs prescribe how to resolve a data label name: `registry.GetByName(name)`, then `ToDataLabel`, with `null` giving SystemHigh. | `TrustTierOrder.cs:110-111`, `:127-128`; `DataSensitivityLabelBridge.cs:39-51`, `:82-97`, `:115-135` |
| L9 | Agent config:<br>• `MaxDataSensitivity` (default `"Public"`)<br>• `AllowedDataSensitivityLevels`<br>• `RAGConfig.MaxSourceSensitivity` (default `"Internal"`)<br>• `WebSearchConfig.FilterSensitiveContent` | a **clearance-like ceiling per agent**, not a data label | narrowing validator, spec builder prompt text, exfiltration defaults | `BackgroundAgentConfig.cs:70`, `:75`; `RAGConfig.cs:49`; `WebSearchConfig.cs:31`; `AgentPolicyNarrowingValidator.cs:73-103`; `BackgroundAgentSpecBuilder.cs:46-89` |
| L10 | `ExfiltrationPolicy`: `BlockExternalLLMs`, `BlockWebSearch`, `BlockNetworkExports`, `RequireLocalOnly`, `MaxAllowedLevel`. When the section is absent, the values are derived from `MaxDataSensitivity`'s flags. | per-agent egress switches | `DataExfiltrationPolicy` | `ExfiltrationPolicy.cs:6-37`; `BackgroundAgentConfigLoader.cs:209-235` |
| L11 | `DataExfiltrationPolicy` judges **tool ids only**:<br>• it keeps deny-lists of LLM, search and export ids;<br>• `RequireLocalOnly` blocks only listed ids;<br>• an unidentified agent gets the most restrictive policy;<br>• `mcp:` ids are on no list.<br>**It never sees the agent's own model call**, which happens outside `policies.Approve`. This verifies brief defect 8. | tool calls | `PolicyEngine` in the self-extend toolbox | `DataExfiltrationPolicy.cs:29-42`, `:98-123`, `:127-152`; `ToolCallingAgent.cs:192` vs `:222`; `RepoFsToolboxFactory.cs:160-163`, `:176-187` |
| L12 | Snapshot key `maxDataSensitivity`: `RAGTool` reads it as the agent's clearance, but **no production code sets it**. Production RAG searches therefore run at the registry floor. | RAG clearance | `RAGTool` | `RAGTool.cs:112-127`; grep `maxDataSensitivity`: only `RAGTool.cs` and `RAGToolTests.cs`; `DataSensitivityFallbacks.cs:54-58` |
| L13 | Package and project manifests carry **no label**:<br>• `AshlarManifest`, `ManifestAgent`, `ManifestBrick` and `ManifestTarget`;<br>• `AshlarPolicy`;<br>• `ExtensionPackage` and its signed `GateRecord`. | - | - | `src/Ashlar.Manifest/AshlarManifest.cs:11-108`; `AshlarPolicy.cs:11-115`; `Packaging/ExtensionPackaging.cs:22-53` |
| L14 | Brick and workflow metadata carry **no label**:<br>• `BrickMetadata` holds author, license, repository, usage and date;<br>• `IExecutionContext` holds `AgentId`, `BehaviorId`, `IsAirGapped`, `AuditMode`, `Provider` and `Variables`;<br>• `WorkflowMetadata` holds viewport data;<br>• `AgentNode` holds `AgentId`, mode, overrides and parameters. | - | - | `src/Ashlar.Brick.Contracts/Authoring/Bricks/BrickMetadata.cs:6-22`; `Authoring/Execution/IExecutionContext.cs:6-37`; `src/Ashlar.Core.Domain/Workflows/WorkflowMetadata.cs:4-13`; `AgentNode.cs:10-37` |
| L15 | MEAI target keys `local:`, `peer:` and `cloud:` are a **destination tier**, not a data label. The default access policy ignores `callerIdentity`, and `UseAshlarGovernance` wires no caller-identity accessor, so the MEAI stack has **no subject identity**. `local:onnx` is never evaluated. | destinations | `PolicyGateChatClient`, `EgressGuardChatClient` | `DefaultChatTargetAccessPolicy.cs:14-44`; `AshlarGovernanceChatClientBuilderExtensions.cs:27-35`; `PolicyGateChatClient.cs:22`, `:56`; `MeaiEgressDestination.cs:71-72` |
| L16 | Barrier levels: a named, ranked hierarchy that is configurable and **not** the five SecurityLevels. They are resolved per request, from an explicit `x-ashlar-barrier` header (checked first), an API key, a PKI certificate or a JWT claim, into an AsyncLocal ambient.<br>• The HTTP middleware is defined but **registered in no production pipeline**, and it is gated by `ASHLAR_BARRIER_MIDDLEWARE_ENABLED`.<br>• The Orchestrator and the gRPC server use a barrier level (from the accessor, the runtime spec or the floor) as a no-elevation **clearance**, with `RequireExplicitBarrier` or else the floor. | caller clearance | Orchestrator, gRPC server, CLI orchestrate | `BarrierContextAmbient.cs:11-17`; `HttpBarrierContextMiddleware.cs:36-113`, `:124`; `DefaultBarrierIdentityResolverPipeline.cs:33-50`; `Orchestrator.cs:201-204`, `:334-350`; `BarrierGuard.cs:35-39`; `AgentTransportServiceImpl.cs:76-94`; grep `HttpBarrierContext` |
| L17 | `PeerTrustTier` (Trusted, Untrusted or Unknown) is a routing allow-list, not a data label. | peers | NCR router | `PeerTrustPolicyResolver.cs:25-53`; `PeerInfo.cs:18` |
| L18 | `CloudSanitizationProxy` is a PII filter. With no filter it blocks cloud calls; when air-gapped it allows. It assigns no label. | prompts | `SanitizingProviderFactory` | `CloudSanitizationProxy.cs:46-90`; `OutgoingContext.cs:6-22` |
| L19 | The inventory's own finding: "No call site carries a `SecurityLabel`." | - | - | `docs/EgressInventory.md:215` |

### 1.3 Inventory: execution boundaries where a frame could be entered

**Threading facts that hold for every row below:**

- **How the frame flows.** It flows into awaits and child tasks and never back out to the caller. A disposed frame counts nowhere: a task that outlives its frame falls back to the enclosing live frame, or to no-subject (fail-closed). `EgressSubject.cs:11-14`, `:74-89`.
- **Nothing in `src/` or `application/` stops the flow.** There is no `ExecutionContext.SuppressFlow` and no `Unsafe*` queueing (grep found none). `Task.Run` captures the context, so frames flow into `ParallelLoopKernel.cs:78`, `BehaviorExecutor.cs:109` and `IdeEndpoints.cs:279`.
- **Hosted services start from the host's startup flow, so they have no frame.** Their egress is no-subject unless a frame is entered inside each unit of work. There are 18 hosted or background services; grep `: BackgroundService|: IHostedService`.
- **Work handed through a `Channel` runs on the consumer's flow, not the producer's.** `BehaviorExecutor.cs:107-109`; `FileSystemEventSource`, `CompositeEventSource`, `FileBarrierAuditSink`.
- **The HTTP and MEAI guard routes decide on the caller's flow, at call time.** See `EgressGuardHandler.cs:51-78` and `EgressGuardChatClient.cs:37-53`. A frame entered by the caller is therefore seen for factory clients, raw `EgressHttp` clients and governed chat clients alike.
- **Kestrel runs each request on its own execution context**, and an AsyncLocal set in middleware flows into `await next(context)`. The repository already relies on this: `HttpBarrierContextMiddleware.cs:36-113` resets it in `finally`.
- **AsyncLocal does not cross a process or network boundary.** A2A, gRPC, MCP, remote bricks and peers have no frame on the far side, so a label must travel in the request. The barrier header (L16) is the precedent, and it is remote-asserted.

| # | Boundary | Entry point | Subject id available | Label information available there | Scope and notes |
|---|---|---|---|---|---|
| B1 | **Agent ReAct cycle** | `ToolCallingAgent.RunCycleAsync` `ToolCallingAgent.cs:141-276`: model call `:192`, tool invoke `:236`, result fed back `:247` | `Name` `:60`; snapshot `agentId` (`SelfExtendRunnerAdapter.cs:340`) | **Inputs** are system prompt and snapshot (`:159-164`), all unlabelled. **Reads** are tool results; `ToolResult(Delta, Payload)` has no label (`ToolResult.cs:16`), and only `rag_search` reports one (L6). | `using` at the top of the method covers every model call and tool call. Tools run one at a time (`:218`). This is the one place that sees every read before the next egress. |
| B2 | Self-extend run | `SelfExtendRunnerAdapter.RunAsync` `SelfExtendRunnerAdapter.cs:136-315` | `resolvedAgentId` `:151` | Snapshot inputs, all unlabelled: `Objective`, `ObjectiveMeta`, `RecentNotes` (scratchpad from earlier cycles), `RecentObservations` and `RepoOverview` (directory and file *names*, which the taxonomy calls `file-paths`, TopSecret) (`:335-425`). Tools: `repo.fs.*` (no sensitivity check, `RepoFsReadTool.cs:21`), dotnet build and test, forge, and **remote MCP tools** (`RepoFsToolboxFactory.cs:93-136`, `:185`). | Encloses B1 **and** the post-cycle mesh publish of the cycle's proposals (`:290-294`, which reaches `SelfExtendAdmissionBridge.cs:288`). A frame at B1 only leaves that publish at no-subject, which is fail-closed. |
| B3 | Background agent cycle (all roles) | `BackgroundAgentRegistry.ExecuteAgentAsync` `BackgroundAgentRegistry.cs:432-758`, through `TrackedExecuteAgentAsync` `:410-416` and scheduler loops `ScheduleExecutor.cs:24-66` | `instance.Config.Id` `:435` | `Config.MaxDataSensitivity`, `ExfiltrationPolicy` and `ModelProvider` (L9, L10). These are clearance-like, which is PR 5's subject. | It runs per cycle, inside the scheduler's flow. Roles: optimizer `:456`, tester `:493`, extender `:530`, self-improver `:663`, and a default that does nothing (`:710`). |
| B4 | `AgentHost.StepAsync` | `AgentHost.cs:36-73`: per-agent loop `:39-71` | `IAgent.Name` (`IAgent.cs:28`) | Nothing. The snapshot holds only `OutputRoot`, `RepoRoot` and `InputPath` (`AgentExecutorAdapter.cs:95-126`). | Used by API `POST /agent` (`AshlarEndpoints.cs:73`, `:322-336`) and by agent run, via `AgentExecutorAdapter.cs:92`. A frame must be **per agent inside the loop**, not around the step. |
| B5 | Orchestrator per-agent invocation | `Orchestrator.cs:191-475`, invoke `:330-363` | `agentId` | Barrier level (L16), a clearance. The payload is upstream agents' outputs (`:353-359`), sent to `TargetEndpoint` (`:363`). | Reached from `ashlar run` (`RunCommand.cs:48-97`, through `OrchestrateCommand`) and from `POST /orchestrate` (`AshlarEndpoints.cs:88`). Remote agents get no frame. |
| B6 | Workflow run | `WorkflowExecutor.ExecuteAsync` `WorkflowExecutor.cs:74-139`; nodes `:100-116`; bricks `:279-293`, `:364-373` | `correlationId` `:79`, `workflow.Name` | None (L14). Node outputs feed downstream nodes (`:107`), so **one mark per run** is the correct BLP unit. | Runs through `ILoopKernel` (`Task.Run`, which flows the context). Webhook egress EG-HTTP-01/02. |
| B7 | Brick execution | `Brick.ExecuteAsync(input, impl, IExecutionContext)` `Brick.cs:72-76`; `BehaviorExecutor.cs:107-250` | `IExecutionContext.AgentId` | None (L14). | It is inside B6 or B5. A frame here would **hide** the enclosing one (F11). |
| B8 | Autonomy iteration | `AutonomyLoopService` (hosted), then the harness (`AutonomousIterationHarness.cs:16-25`, `ExplainedFailure` at `:226-250`), then `OllamaProposalSource.cs:132` (EG-MDL-11) | objective id | `ObjectiveDocument` has no label (`ObjectiveDocument.cs:25-77`). | Hosted loop, so a frame is needed per iteration. |
| B9 | MEAI chat call | `EgressGuardChatClient.Decide` at call time | none at this layer (L15) | none | Not a boundary itself. It sees its caller's frame (`ashlar chat`, IDE endpoints, kernel). |
| B10 | API request | Kestrel middleware | no per-caller identity: auth is shared-credential tiers `None`, `Full` and `CopilotScoped` (`AshlarApiAuthTier.cs:6-16`; `AshlarAuthContextKeys.cs:7`) | Barrier level if that middleware were registered (L16, not registered). | A middleware frame would cover every endpoint, including `Task.Run` at `IdeEndpoints.cs:279`. |
| B11 | CLI one-shot verbs | `pkg` (`PkgCommand.cs:93`, `:389`, `:444`), `export` (`NativeBundle.cs:88`), `mesh export` (`SneakernetTransport.cs:36`), `improve` (`FileBasedSharedAdaptationStore.cs:57`), `chat` | the operator | **The package and app have no label (L13).** That is §8 Q4 and PR 5's business. | A frame per verb would be a human-operator subject whose reads are a whole project or package: unlabelled, so SystemHigh. |
| B12 | System and mesh services | Mesh serve (`MeshServeService.cs:313`, on a Kestrel request flow), discovery beacon (`MeshDiscoveryService.cs:249`), auto-pull, `EndpointHealthMonitor`, `McpClientConnectionManager`, peer and NCR pollers, MeshLab worker, OTLP (`Program.cs:247`) | none (the process) | none | **Infrastructure egress with no data subject.** Mesh serve sends package bytes, which are unlabelled. |
| B13 | Inbound transports | gRPC server (`AgentTransportServiceImpl.cs:60-94`, forwarding at `:173`), A2A server | the remote caller | A remote-asserted barrier level | A remote assertion cannot be trusted to *lower* a label. |

### 1.4 The spec text that binds PR 4's label choices

- **SystemHigh is mandatory for missing labels.** §2.1: "Unlabelled data, an unknown level name, or a label that fails to parse **MUST** be treated as `SystemHigh`" (`SPEC-007:99-102`). The no-subject egress is listed as enforced by test (`:113-114`).
- **The current label is the session's high-water mark.** §2.3 defines `current` as "the join of everything it has read in this session" (`:131-132`). "Session" is not defined.
- **Refusals must be explained.** §2.3 (`:133-135`) and §7 (`:310`).
- **The PR 4, 5 and 6 rows:**
  - PR 4 (`:278`): "Guard enforces … `AirGapped` and `SecureWorkstation` enforce by default." It is done when "a seeded leak test (an agent tries to write labelled data down) fails closed with an explained refusal".
  - PR 5 (`:279`): "Agents get a clearance; a sealed skill declares its highest level in the package manifest."
  - PR 6 (`:280`): a certified downgrade plus a human sign-off is "the only thing that lowers a label".
- **Two §8 questions this lens must not answer.** §8 Q3, where clearances are configured (`:317`). §8 Q4, labels in signed records versus the manifest (`:318-319`).
- **Decisions already merged:**
  - a bridged label carries the level only (`:334`);
  - no subject means SystemHigh (`:352-355`);
  - a clearance with no level is Public (`:356-358`).
- **Open questions that touch this lens:**
  - B, who holds SystemHigh (`:402-414`);
  - C, caveat scope, due before PR 4 (`:416-421`);
  - D, redacting refusal details for the subject (`:423-425`).

---

## 2. Analysis

### 2.1 What "enforce" means with no producers

With the guard as merged and no frames, enforcement on AG and SW is **host-only egress** (F8, F9).

- **AirGapped.** That is the profile's promise. At the egress layer it also closes brief defect 5: RunPod, the peer executor, federated bricks and the ollama.com catalog stay registered but are refused when called, as long as they are routed.
- **SecureWorkstation.** It removes cloud models, web search, webhooks, mesh share, export and remote peers. Local-model agents keep working, because loopback is Host.
- **The air-gap transfer path itself is refused on AG.** `export` and `mesh export` are classified NetworkExport (Internal, F7) while the content is SystemHigh. Under BLP that is correct: moving unlabelled content to removable media is a write-down. Moving it legitimately needs either a label on the content (PR 5) or a trusted downgrade (PR 6). The owner should know that before PR 4 merges.

### 2.2 Every producer is an opening, so it carries the review burden

A frame can only lower the current label (F10). There are four ways a frame leaks:

1. **A floor below what was read before entry.** The default floor is `Public`, the most permissive possible.
2. **An unobserved read after entry.** Today, every read except `rag_search` is unobserved and unlabelled (B1, B2).
3. **A nested frame that hides the outer subject's reads.** This is how nesting works now (F11). A brick frame inside a workflow frame (B6, B7), or an agent frame inside an orchestrator frame (B5), would decide at the inner mark alone.
4. **Reads that persist across sessions and come back unlabelled:** the scratchpad, observations and memory (B2). A per-cycle mark that does not observe them on re-entry forgets them.

A fifth way only looks like an opening:

5. **Model responses.** The destination labels are *maximums a destination may receive*, not labels for what comes back from it. If a response were observed at its destination's class label, a local Ollama response (Host, SystemHigh) would make every subject SystemHigh. So a response must not be observed as a read. The mark already covers the prompt that produced it.

### 2.3 What the agent loop actually reads, and what label it can get

- **Labelled read: RAG results (L6).** Use the PR 2 bridge exactly as its documentation prescribes: `registry.GetByName(name)` then `ToDataLabel()`. Null or unknown gives SystemHigh (`DataSensitivityLabelBridge.cs:39-51`).
  - In a kernel host the string is a trust-tier name (L7).
  - For the five canonical names both resolvers agree. Unknown names give SystemHigh in both.
  - Custom levels are understood only by the registry. Prefer the bridge through `RAGTool`'s own `_sensitivityRegistry` (`RAGTool.cs:33`, `:58`).
- **Unlabelled reads:**
  - the objective and system prompt;
  - `RecentNotes`, `RecentObservations` and `RepoOverview`;
  - `repo.fs.read` and `repo.fs.list`;
  - build and test output;
  - forge results;
  - MCP tool output;
  - web search results.

  §7 makes all of these SystemHigh. The taxonomy (L4) would give TopSecret for file contents, file paths and terminal output. That is no different in effect, because every non-host destination is Confidential or lower (F7). It would only matter for the Public types, such as editor events and git metadata.

**The practical result.** In production, a self-extend cycle observes at least one SystemHigh input at entry, so PR 4's frame changes **no production outcome** for it. Only an agent whose inputs are all labelled, or are configuration declared to be Public, can be decided below SystemHigh. That makes a PR 4 producer a low-risk opening, which is the right risk profile for the PR that switches enforcement on.

### 2.4 Where the frame belongs in PR 4

The done-criterion needs "an agent" and "labelled data". The narrowest honest boundary is **B1, `ToolCallingAgent.RunCycleAsync`**, for three reasons:

- it sees every input and every tool result before the next egress (`:192`, `:236`, `:247`);
- it covers the agent's own model call, which `DataExfiltrationPolicy` never sees (L11);
- it is generic: any `ToolCallingAgent` user gets it.

B2 and B3 can join later as *outer* frames once nesting is monotone. Until then, an outer frame there would be hidden by B1's frame (F11).

Every other boundary (B4 to B13) can stay unframed in PR 4. Unframed means no-subject, which is SystemHigh. That is fail-closed on enforcing profiles and unchanged on report-only ones. Each later producer is its own reviewed lowering.

### 2.5 What PR 4 must decide about label sources, without preempting PR 5 or §8 Q3

**PR 4 must decide:**

- the default when no frame is active;
- which boundary enters a frame;
- the nesting rule;
- how a reader reports a label (API);
- the label of an unlabelled read;
- that model responses are not reads;
- what happens to system egress;
- what a session is.

**PR 4 must not decide:**

- **An agent's clearance.** `MaxDataSensitivity`, barrier levels and the snapshot's `maxDataSensitivity` are clearances. Using any of them as a *floor* or *ceiling* for the mark would make agent config or host options the de facto clearance source. That answers §8 Q3 and does PR 5's job.
  - Floor = clearance is also *unsound today*. No-read-up is not enforced on most reads: `repo.fs.read` has no check (`RepoFsReadTool.cs:21`), and production RAG gets no clearance (L12).
- **A label on packages or gate records.** That is §8 Q4. Packages stay unlabelled, so they are SystemHigh, in PR 4.
- **Any mechanism by which configuration or an operator *lowers* a label below SystemHigh for data already held**, such as a CLI `--label`, a per-agent data floor or a site-declared payload label. That is declassification-shaped: PR 6 says only a certified downgrade with sign-off lowers a label.
  - Classification at *origin* is different. The RAG indexer's level (L5) is the existing, legitimate origin label, and PR 4 can consume it.
- **Caveats (open question C).** The bridges are level-only (`SPEC-007:334`). If the owner answers C by turning the flags into caveats, the RAG observation point would join caveats too, and the leak test could also assert `MissingCaveat`.

---

## 3. Decisions for the owner

### D1. Current label when no frame is active, under an enforcing profile

**Options:**

- (a) **SystemHigh**, as merged and as §7 requires. Every non-host decision is refused with `SystemHighData`.
- (b) A per-profile default, for example SW = Internal.
- (c) Report-only when there is no subject: enforce only decisions made inside a frame.
- (d) Require a frame at named boundaries. A convention test asserts that each named boundary enters one; at run time the outcome is (a).

**Consequences:**

- (a) is honest and fail-closed.
  - AG and SW become host-only (F9).
  - On AG, `export` and `mesh export` are refused until PR 5 or PR 6.
  - On SW, cloud models and web search are refused.
- (b) contradicts the §2.1 MUST (unlabelled = SystemHigh) and treats all unlabelled data as Internal. That is a silent declassification.
- (c) contradicts §7. It leaves every uninstrumented path open, including AG's RunPod, peer and catalog paths (brief defect 5), so "enforce" would mean almost nothing.
- (d) has the same runtime effect as (a). It adds a guard against a future boundary silently losing its frame.

**Recommendation:** (a), plus (d)'s convention test for the boundaries PR 4 instruments. Reject (b) and (c).

### D2. Which boundaries PR 4 instruments

**Options:**

- (a) None: the leak test enters its own frame.
- (b) One: the ReAct cycle (B1), with `rag_search` reporting labels.
- (c) Every agent-running boundary: B1 to B6 and B8.
- (d) (c) plus the API, the CLI and system services.

**Consequences:**

- (a) satisfies "fails closed" only in a unit sense. No production path ever labels data, so the done-criterion's "an agent" is simulated.
- (b) gives one real producer and keeps the opening minimal (§2.3).
- (c) and (d) multiply the openings PR 4 must review. They need D4 fixed first, and they need label sources that do not yet exist (L13, L14).

**Recommendation:** (b). B2 and B3 come in a later PR, after D4.

### D3. A frame's floor, and what counts as a read

**Options:**

- (a) Floor `Public`.
  - The cycle observes every input and every tool result.
  - A result labelled by its source (RAG) is observed through the PR 2 bridge.
  - **Anything unreported is SystemHigh**, as §7 requires.
  - The agent's own authored configuration (objective template and system prompt) is declared Public.
- (b) Floor = the agent's `MaxDataSensitivity`, through `ToDataLabel`.
- (c) Floor SystemHigh. This makes a frame pointless except as attribution.
- (d) Floor `Public`, with unlabelled reads labelled by `DataTaxonomy`.

**Consequences:**

- (a) is sound if the default is enforced in the loop. It changes no production outcome for self-extend (§2.3). It needs one explicit owner statement: that agent-authored prompt configuration is Public.
- (b) is unsound today, because reads are not clearance-checked (L12, `RepoFsReadTool`). It also preempts PR 5 and §8 Q3.
- (c) makes the leak test vacuous: the refusal would be `SystemHighData`, not a refusal over labelled data.
- (d) gives the same effect as (a) for every non-host destination today. It adds a dependency on a taxonomy nobody consults (L4).

**Recommendation:** (a). The taxonomy can come later.

### D4. Nesting semantics of `EgressSubject`

**Options:**

- (a) Keep replace semantics and forbid nested producers by convention.
- (b) `Resolve` returns the **join of every live frame** on the chain, and disposing an inner frame **observes its mark into the outer frame**, because outputs flow outward.
- (c) The inner mark is seeded from the outer frame's current label when it is entered; no propagation.

**Consequences:**

- (a) is fragile. B5/B6 around B1/B7 would leak (F11).
- (b) is monotone and fail-closed. It flips `EgressGuardDecisionTests.cs:355-362` on purpose, and the API is still Unshipped (F13).
- (c) misses reads the outer subject makes in parallel after the inner frame is entered, and loses the inner frame's reads on return.

**Recommendation:** (b), and land it **before** any second producer.

### D5. How a reader reports a label into the ambient subject

**Options:**

- (a) A static `EgressSubject.Observe(SecurityLabel)` that joins into the live frame's mark, and does nothing when there is no frame because no-subject is already the top.
- (b) A label field on `ToolResult`. This is a public record change in Abstractions.
- (c) Per-tool declared labels as metadata.
- (d) Pass the mark through `WorldSnapshot.Data`.

**Consequences:**

- (a) is a small, unshipped API. A tool needs no reference to the mark. It must be paired with the D3(a) rule: a tool result that reported nothing is SystemHigh.
- (b) is a wider breaking surface.
- (c) cannot label per result; RAG results carry different levels.
- (d) is stringly typed and lets a tool lower the mark by replacing it.

**Recommendation:** (a). `RunCycleAsync` observes SystemHigh for any tool invocation during which nothing was reported.

### D6. Model responses and system egress

**D6a. Is a model response a read?** Recommendation: **no**.

- The response is derived from the prompt, which the mark already covers.
- Observing it at the destination's class label would make every local-model subject SystemHigh (§2.2, point 5).

**D6b. Egress with no data subject** (beacon, health monitor, MCP list, catalog pulls, OTLP, mesh serve). Options:

- (a) Leave them at no-subject, so they are refused off-host on AG and SW.
- (b) A per-site declared payload label, as a reviewed "system subject" frame.
- (c) Exempt families.

Recommendation: (a) in PR 4. Option (b) is a declassification-shaped statement and needs the owner's approval for each site, later.

### D7. Where the seeded leak test's label comes from

**Options:**

- (a) A test-entered frame plus `Observe(Secret)`, as in `EgressGuardDecisionTests.cs:380-392`.
- (b) **A RAG document classified `Secret` when it is indexed (L5).**
  - `rag_search` returns it with its label (L6).
  - The production frame in `RunCycleAsync` observes it through the bridge (D3, D5).
  - The agent then calls web search (Confidential) or an external model (Internal).
  - The enforcing guard refuses with `LevelTooLow`, naming the destination level and the current level (`ReferenceMonitor.cs:66-71`), with basis `subject:<agentId>`.
- (c) The MEAI-side `ChunkRecord.TrustTier`, through the adapter (L7).
- (d) The agent config `MaxDataSensitivity = Secret` as the floor.

**Consequences:**

- (a) has no production producer. On its own it shows only that the guard works.
- (b) is end to end and uses an existing origin label, with no new configuration.
  - **Make it non-vacuous:** assert the same egress is *allowed before* the read and *refused after* it. Assert the reason is `LevelTooLow`, not `SystemHighData`.
  - **Mutation targets:** drop the `Observe`; floor SystemHigh; enforcement off.
  - **One caveat:** the test must put `maxDataSensitivity` in the snapshot so the Secret document is retrievable. Production never does (L12). That is existing `RAGTool` behaviour, but wiring it in production belongs to PR 5.
- (c) is the same idea, but needs a frame at an MEAI-calling boundary, which does not exist.
- (d) preempts PR 5 and §8 Q3. Its label would be an assumption about the agent, not a property of data that was read.

**Recommendation:** (b), with (a) kept as a unit twin. Ask the owner to confirm (b)'s caveat.

### D8. What a session is

The session is the lifetime of the high-water mark (§2.3 "this session").

**Options:**

- (a) One cycle, with persisted state (scratchpad, observations) re-entering as unlabelled reads, so it is SystemHigh.
- (b) The lifetime of the agent instance, held in memory.
- (c) A mark persisted with the agent's state.

**Consequences:**

- (a) is sound now and simple.
- (b) loses the mark on restart, and the persisted state comes back unlabelled anyway.
- (c) needs labelled storage. That is PR 7's territory (resources inherit labels).

**Recommendation:** (a).

---

## 4. Risks

1. **Fail-open by default floor.** `new HighWaterMark()` is `Public` (F10). A producer that misses one input or read lowers the label without evidence. Mitigation: the D3(a) "unreported read = SystemHigh" rule, enforced in the loop, not by convention.
2. **Nested frames hide outer reads today** (F11). Any second producer before D4 leaks.
3. **Unobserved read paths:** `repo.fs.read` (`RepoFsReadTool.cs:21`), remote MCP tool output (`RepoFsToolboxFactory.cs:185`), scratchpad and observations (`SelfExtendRunnerAdapter.cs:365-376`). They are safe only under D3(a).
4. **AG operator workflow breaks.** `export` and `mesh export`, the air-gap transfer path, are refused under no-subject (F9). This lasts until PR 5 (manifest level) or PR 6 (downgrade). The owner should accept this explicitly.
5. **SW availability:** cloud models, web search, mesh, webhooks and `dotnet` restore (`nuget-feeds` is not Host, `DotnetRunner.cs:52-59`) are all refused. Owner confirmation is needed that this is SecureWorkstation's intent.
6. **The leak test's realism depends on the snapshot clearance.** The `maxDataSensitivity` the test sets is never set in production (L12). This is PR 5's wiring.
7. **Two RAG label vocabularies meet at `SensitivityLevelName`** (L7). Custom registry names and trust-tier names resolve differently. Both fail closed to SystemHigh on unknown names, so this costs availability, not safety.
8. **Refusal text fed back to the planner reveals the level of what was read** (open question D). If the planner's model is remote, that text is itself an egress, which the guard would refuse; if local, it is harmless. The leak test's assertion surface depends on the answer to D.
9. **Using clearance-shaped fields as label floors would preempt PR 5 and §8 Q3,** and would be unsound because reads are not clearance-checked (L9, L12, L16).
10. **No frame crosses a process or network boundary.** Remote agents, bricks, peers, A2A, gRPC and MCP decide with no subject on the far side. A label carried in a request is remote-asserted and must never lower one (B13).
11. **Hosted services started before any frame never see one** (B12). System egress stays no-subject by construction. This is fine under D6b(a), but it must be stated, so nobody later "fixes" it with a host-wide frame, which would lower everything.
