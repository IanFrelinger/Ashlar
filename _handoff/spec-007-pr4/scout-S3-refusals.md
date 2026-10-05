# Scout S3: refusals — what a refusal looks like on each route, and who sees it

Read-only pass over `$SP/pr4-base` at `8ec674d2a`. Every `file:line` is at that commit. Nothing was built or run.
Claims about third-party behaviour (HttpClient, Grpc.Net.Client, the A2A and MCP SDKs, System.CommandLine) are marked
**[needs a test]**: they are readings from memory, not code in this repo.

## 0. Summary

- **Ashlar has no refusal exception today.** `AccessDecision` (a struct) is the only refusal type in `Ashlar.Abstractions`,
  and the assembly defines no exception type at all. The refusal exceptions that exist are elsewhere: `PolicyViolationException`
  (AI.Pipeline) and the three Barrier exceptions. Each derives straight from `Exception` and carries a code.
- **A refusal's `Exception.Message` reaches people outside the trust boundary.** It goes to A2A peers, gRPC callers, MCP clients,
  IDE/API clients and the model's own context, through at least eight generic `catch (Exception) { … ex.Message … }` sites (§1.6).
  So the redaction answer to open question D has to live in `Message` itself, not in a separate "detail for the subject" field.
- **Retries, fallbacks and swallowing depend on the base type.** If the refusal derives from `HttpRequestException`, `IOException` or
  `OperationCanceledException`, `TransientClassifiers.Network` retries it, the A2A loop retries it, and Bing, SNS and MeshLab
  swallow it as "empty", "bad signature" or "skipped". A synthetic 403 is worse: `EnsureSuccessStatusCode` turns it into an
  unexplained `HttpRequestException`.
- **Some catch-alls mask a refusal whatever its type:**
  - `HotSwappableModel` falls back to the echo model, which looks like success.
  - `ToolCallingAgent.ThinkAsync` turns it into "no actions".
  - `AdaptiveProviderFactory` and `HotSwappableModel` rewrite it as "model unreachable".
  - The orchestrator's circuit breaker counts it as a failure and later reports `CIRCUIT_OPEN`.
  - Four background loops log it as "degraded" or "could not connect".
  - The IDE tags probe swallows it with no log at all.
- **Throwing is wrong at four explicit sites:**
  - Mesh serve should answer 404. It already does for every other refusal.
  - Mesh discovery: a throw stops the daemon host.
  - OTLP: a throw fails API startup.
  - Self-extend auto-share: it already has a "refused" annotation.
- **The fault path fails closed in the record but fails open in the code around it.** `Evaluate` returns `NoDecision`, which is refused.
  But the HTTP handler and the MEAI layer swallow a throwing guard and send anyway (`EgressGuardHandler.cs:81-85`,
  `EgressGuardChatClient.cs:67-70`). The `enforces` placeholder is also `false` (`EgressGuard.cs:66`).
- **Recommendations:**
  - One `EgressRefusedException : Exception`.
  - HTTP refuses by throwing, never with a synthetic response.
  - `Message` is redacted (reason category, site, class, decision number), and the full decision is a property and goes to the log.
  - A fixed generic string at the trust-boundary exits.
  - No same-destination retry. Refusals are exempt from echo fallback and from the circuit breaker.
  - A fault fails closed, with the enforcement switch resolved outside the guard.
  - Refusals are logged at Warning.

---

## 1. Facts

### 1.1 What a refusal would be built from (the guard as merged)

- **`Evaluate` never throws.** It catches everything, records only the exception's type name as `Fault`, sets
  `access = default`, and always publishes (`src/Ashlar.Abstractions/Security/Egress/EgressGuard.cs:93-101`, `:120`).
  - Its fail-closed placeholders are `destinationLabel = Public` and `current = SystemHigh`, but `enforces = false` (`EgressGuard.cs:65-76`; `:66`).
  - `ResolveProfile` is the first statement inside the try (`:80`).
- **The mode is a constant.** `ReportMode = "report"` (`EgressGuard.cs:31`). `EgressDecision.Mode` documents `report` as "the only mode
  in SPEC-007 PR 3" (`EgressDecision.cs:58`).
- **`default(AccessDecision)` reads as refused.** It is `Allowed=false` with reason `NoDecision` (`AccessDecision.cs:18`, `:42`, `:55`).
- **`Detail` is not for the refused subject.** It "names the offending level, compartments or caveats" and "is for audit logs and operators; do
  not hand it to the refused subject" (`AccessDecision.cs:44-55`; `EgressDecision.cs:11`).
  - The write-down texts name both level names and compartment and caveat tokens (`ReferenceMonitor.cs:57-79`).
- **No subject means SystemHigh.** With no subject the current label is SystemHigh with basis `no-subject` (`EgressSubject.cs:51`).
  - No production code calls `EgressSubject.Enter`: a grep of `src application commercial` outside tests finds none.
  - Every non-host refusal is therefore `SystemHighData` with the detail "the subject's current label is SystemHigh, which only a
    SystemHigh destination may receive" (`ReferenceMonitor.cs:57-63`).
- **The explicit sites all use `ProcessDefault`.** Every explicit call site uses `EgressGuard.ProcessDefault.Evaluate(...)`, never an
  injected `IEgressGuard`, and discards the result with `_ =` (§1.3 R7, grep listing).
- **The convention scanner keys on `Evaluate`.** It recognises a guard only as `.Evaluate(new EgressRequest(`
  (`src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressGuardConventionTests.Scanner.cs:668`).

### 1.2 Existing refusal types and conventions

- **The exception types:**
  - `PolicyViolationException : Exception` has `Code`, `TargetKey`, `ModelId` and `Details`. Its Details are documented as "never contains raw
    secret/PII content" (`src/Ashlar.AI.Pipeline/Governance/PolicyViolationException.cs:6-33`).
  - `BarrierCeilingExceededException`, `BarrierElevationException` and `BarrierContextMissingException` are all `: Exception`, and each
    has an `ErrorCode` string property (`src/Ashlar.Runtime/Barriers/BarrierCeilingExceededException.cs:6`, `:63`;
    `src/Ashlar.Orchestration/Barriers/BarrierElevationException.cs:4`, `:95`; `BarrierContextMissingException.cs:4`, `:17`).
  - `Ashlar.Abstractions` (including `Security/`) defines no exception type. Its only throws are argument guards plus
    `JsonException` and `FormatException` in the label converters.
- **The MEAI governance precedent:**
  - `PolicyGateChatClient` writes its own `denied` audit record, then throws `PolicyViolationException("target_denied")`
    (`PolicyGateChatClient.cs:54-76`; audit `:62-69`, throw `:71`). `SanitizingChatClient` does the same with
    `sanitization_blocked` (`SanitizingChatClient.cs:68-87`).
  - They write their own record because `AuditingChatClient` is innermost (`AshlarGovernanceChatClientBuilderExtensions.cs:26-49`) and
    never sees an outer layer's throw.
- **A specific catch gives the refusal its own code.** The orchestrator catches `BarrierContextMissingException` specifically and
  escalates under the exception's own `ErrorCode`, "so callers can see WHY nothing ran" (`Orchestrator.cs:456-469`).
- **An inbound HTTP refusal is a bare 403.** `HttpBarrierContextMiddleware` returns 403 with no body, logs a Warning and writes a
  barrier audit event (`src/Ashlar.Runtime/Barriers/Identity/HttpBarrierContextMiddleware.cs:80-93`).
- **A stranger gets 404 and never the reason.** On mesh serve, "a refusal is a 404 and never its reason: … output goes to a STRANGER"
  (`application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshServeService.cs:303-311`).
- **A tool-level denial goes back to the model.** In `ToolCallingAgent`, a policy denial becomes the message
  `"tool {id}: DENIED ({reason})"`, so the model can re-plan (`src/Ashlar.BackgroundAgents/Agents/ToolCallingAgent.cs:222-231`).
  `DotnetBuildTool` returns `ToolResult{ok=false,error}` for a sandbox rejection (`src/Ashlar.Tools.Dev/DotnetBuildTool.cs:75-80`).
  `WebSearchTool` returns a "blocked" delta (`WebSearchTool.cs:64-71`).
- **The CLI's "refused" exit code is 65.** `export` and `pkg publish` use it (`ExportCommand.cs:78-82`, `:160-164`; `PkgCommand.cs:383-384`).
  `pkg export` and `share` catch only `InvalidOperationException or ArgumentException` and exit 1 (`PkgCommand.cs:102-106`, `:469-473`).
  The CLI has no global exception handler: `application/src/Ashlar.CLI/Program.cs:87` is a bare `root.InvokeAsync(args)`.
- **Analyzer constraints on a new type in Abstractions.** The project sets `AnalysisMode=All` (`src/Ashlar.Abstractions/Ashlar.Abstractions.csproj:16`)
  and the repo sets `TreatWarningsAsErrors=true` (`Directory.Build.props:3`). The project also runs the public-API analyzer (`csproj:24`,
  `PublicAPI.Unshipped.txt`).

### 1.3 Per route: from the guard call to the caller

#### R1. Factory HTTP handler (`EgressGuardHandler` through `ConfigureHttpClientDefaults`)

- **Insertion and decision.** The handler is inserted at index 0 of `AdditionalHandlers`
  (`src/Ashlar.Infrastructure/Egress/EgressServiceCollectionExtensions.cs:64`). Retry and resilience handlers added with
  `AddHttpMessageHandler` therefore sit inside it, and a refused send never reaches them. The decision is made in a non-async
  `SendAsync` (`EgressGuardHandler.cs:51-55`).
- **To refuse,** `Report` would have to stop the send. The two choices are throwing or returning a synthetic response (§2.1). Today it
  swallows a guard's throw and counts it (`:67-86`).
- **No HTTP resilience handler is registered anywhere.** A grep finds no `AddStandardResilienceHandler`, `AddResilienceHandler`,
  `AddPolicyHandler` or `AddTransientHttpErrorPolicy`, and no `using Polly` in non-test code. The only Polly reference is a
  `PackageReference` (`src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj:65`).
- **Who catches** (factory clients):

| Site | Catch | What a thrown refusal becomes |
|---|---|---|
| `HttpRemoteBrickCatalog.cs:62-65`, `:89-92`, `:149-151` | `catch (Exception)` → Warning → `[]` / `null` / cached | The catalog is silently empty. Logged at Warning. |
| `CompositeBrickRegistry.cs:58-61` | `catch (Exception)` → Warning → `null` | "Brick not found" |
| `ModelArtifactCatalogService.cs:62-65` | `catch (Exception)` → Warning, skip source | A source is missing from the list |
| `RunPodHttpClient.cs:67-70` (and `:113`, `:145`, `:169`, `:192`) | `catch (Exception)` → `Result.Failure("runpod.*_exception", …, ex.Message)` | An explained Result, with a generic code |
| `AshlarPeerBrickExecutor.cs:185-196`, then the loop `:66-88` | `catch (Exception)` → `peer.dispatch_exception` with `ex.Message`; next peer | Each peer is decided again. The final result is `peer-routing.all_peers_failed` with every message. |
| `OllamaModelServingBackend.cs:48-53` (and siblings) | bare `catch { metrics; throw; }` | Propagates. The `ncr.ollama.*.error` counter goes up. |
| `RemoteBrick.cs:90-91` | none | Propagates |
| `HttpWorkflowWebhookClient.cs:32-33`, `:44-45` | none | Propagates |
| `RemoteExecutionPlatform.cs:40-43` | `catch (Exception)` → `false` ("not reachable") | Misleading |
| `MeshLabWorkerExecutorClient.cs:190-193` | `catch (HttpRequestException or TaskCanceledException)` → **Debug** "skipped" | Swallowed **only if** the refusal derives from `HttpRequestException` |
| `SnsRsaSignatureVerifier.cs:52-59` | `catch (HttpRequestException)` → `false` | "Bad signature" (a 403 to AWS) **if** it derives from `HttpRequestException`. Otherwise unhandled: a 500 to AWS, which AWS SNS retries **[needs a test]**. |
| `IdeEndpoints.cs:193-211` (EG-MDL-14) | bare `catch { }`, **no log** | Silent. The tags list is just shorter. |

#### R2. `EgressHttp` raw clients

- **ProviderFactory (EG-MDL-03..08).**
  - The static `Http` is built at `ProviderFactory.cs:61`.
  - OpenAI-shaped sends go through `SendHttpWithResilienceAsync` (`:184-200`), which uses `CreateLlmRetryPolicy()` with no classifier
    (`:199`), so `TransientClassifiers.Network` applies (`RetryPolicy.cs:57`). That classifier treats `OperationCanceledException`,
    `HttpRequestException`, `TimeoutException`, `SocketException` and `IOException` as transient
    (`src/Ashlar.Core.Application/Resilience/Ports/TransientClassifiers.cs:26-36`).
  - Attempts are `ASHLAR_LLM_RETRY_COUNT+1`, default 4 (`ProviderFactory.cs:165-175`; `AshlarDefaults.cs:30`), with a 2 s exponential backoff.
  - The executor rethrows a non-transient exception at once (`ResilientExecutor.cs:43-53`).
- **The Ollama provider (EG-MDL-07).**
  - The Ollama path adds `ModelUnavailableException` to the transient set (`ProviderFactory.cs:181-182`, `:286-292`).
  - `OllamaProvider` maps `HttpRequestException` to `OLLAMA_UNREACHABLE` (`OllamaProvider.cs:120-126`, `:269-275`).
    `ExecuteOllamaAsync` turns any failed Result into `ModelUnavailableException` (`ProviderFactory.cs:503-527`).
  - **So a refusal derived from `HttpRequestException` is retried 4 times and ends as "Ollama execution failed. OLLAMA_UNREACHABLE".**
    A refusal of a fresh type escapes `OllamaProvider`, which catches only `OperationCanceledException`, `HttpRequestException` and
    `JsonException` (`:263-281`), and is not retried.
- **Above ProviderFactory:**
  - `ProviderBackedModel.cs:33` has no catch.
  - `AdaptiveProviderFactory.cs:47-69` catches **every** exception per provider, tries the next one, and finally throws
    `ModelUnavailableException("No model available. Ensure local model is loaded or server is reachable.", lastEx)`. The vision path
    does the same (`:85-102`).
  - `HotSwappableModel` catches every exception from the agentic model:
    - under `prefer=deterministic` it **falls back to the echo model and returns success** (`HotSwappableModel.cs:60-71`);
    - under `ASHLAR_ALLOW_MOCK=1` it does the same (`:94-95`);
    - otherwise it throws `ModelUnavailableException("… failed … Refusing to report success over an unreachable model.", ex)` (`:84-93`).
- **A2A, gRPC and MCP** raw clients: see R4–R6.
- **MeshAutoPull (EG-MESH-04).** The client is at `MeshAutoPullService.cs:57`, `:72`.
  - `catch (Exception)` → `MeshPullSummary.Empty with { Errors = 1 }`, commented "peer offline / timeout / bad index" (`:290-293`). The
    same happens per package (`:340-343`).
  - The tick logs only the counts, at Information (`:168-174`). **The refusal's reason is never logged.** "Refused" in the summary already
    means "untrusted signer" (`:172`).
- **CLI clients** (EG-HTTP-05/06/07) and `CloudAvailabilityResolver` (EG-MDL-12) propagate, or answer "unavailable"; they are not traced
  further here.

#### R3. MEAI `EgressGuardChatClient`

- **Shape.** The client is outermost (`AshlarGovernanceChatClientBuilderExtensions.cs:26-28`). `Decide()` runs synchronously in both
  overrides (`EgressGuardChatClient.cs:37-54`) and swallows a guard's throw (`:58-71`). Its constructor has no auditor
  (`:26-31`, public API).
- **To refuse:** `Decide()` throws. Like PolicyGate, that is a synchronous throw from a `Task`-returning method, or from
  `GetStreamingResponseAsync` before enumeration (the remark at `:14-15`).
- **Who catches:**
  - **The keyed client's own `AuditingChatClient` never sees it.** It is innermost (`AshlarGovernanceChatClientBuilderExtensions.cs:45-49`).
    No audit record is written unless the guard layer writes one, as PolicyGate does (`PolicyGateChatClient.cs:62-69`).
  - **`RoutingChatClient`** has no catch and no fallback to another target (`RoutingChatClient.cs:40-110`).
  - **The default router** is wrapped in `AuditingChatClient(router, "router:default")` (`MeaiPipelineServiceCollectionExtensions.cs:122-129`).
    It classifies `PolicyViolationException` as `"denied"` with `ex.Code`, and **anything else as `"fault"`/`"fault"`**
    (`AuditingChatClient.cs:50-60`, `:94-104`). A new exception type would therefore be audited as a fault unless that class is taught it.
  - **The auditor is in memory only.** The default is `InMemoryChatInvocationAuditor` (`MeaiPipelineServiceCollectionExtensions.cs:203`),
    and no non-test code reads it.
  - **`MeaiBackedModel.CompleteAsync`** has no catch (`MeaiBackedModel.cs:61`).
  - **Then `HotSwappableModel`**, as in R2: echo fallback or a misleading `ModelUnavailableException`.
  - **Then `ToolCallingAgent`:**
    - in `ThinkAsync`, everything except `ModelUnavailableException` is caught → Warning plus a `think.error` memory record →
      `AgentActions.None` (`ToolCallingAgent.cs:108-127`). That is a refusal reported as "the model chose to do nothing", which the
      comment at `:116-121` forbids for an unavailable model;
    - in the ReAct loop, the model call (`:190-198`) falls to the outer `catch (Exception)` → `stoppedReason="error"`, Warning, and a
      `react.error` memory record with `ex.Message` (`:271-276`). `ModelUnavailableException` is rethrown (`:264-270`).
  - **`IdeEndpoints` run.** A refusal surfaces as an SSE `error` event carrying `message = ex.Message`, to the **API client**
    (`application/src/Ashlar.API/Endpoints/IdeEndpoints.cs:465-473`).

#### R4. gRPC handler (EG-XPT-03/04/05)

- **Shape.** The channel's `HttpHandler` is `EgressHttp.Wrap(ConfigureHandler(new HttpClientHandler()), …)`
  (`DefaultGrpcChannelFactory.cs:65-76`). No service config and no retry policy are set (`:67-70`).
- **What a throw turns into [needs a test].** Grpc.Net.Client converts an exception from the HTTP pipeline into
  `RpcException(StatusCode.Internal, "Error starting gRPC call. <Type>: <Message>")`, with the original as `Status.DebugException`. It
  would be `Unavailable` if the exception were an `HttpRequestException` or `IOException`.
- **Who catches:**
  - `GrpcAgentTransport.SendAsync` catches `RpcException` → Warning → `AgentResult(Success=false, ErrorMessage = ex.Status.Detail,
    ErrorCode = "GRPC_ERROR_INTERNAL")` (`GrpcAgentTransport.cs:79-92`, `:289-298`). Anything else → Error →
    `"TRANSPORT_ERROR"` with `ex.Message` (`:93-104`).
  - Health: `CheckEndpointHealthAsync` catches everything → `IsHealthy=false`, `Message = ex.Message` (`:161-169`).
    `EndpointHealthMonitor` marks the endpoint unhealthy, and after N probes logs at Warning that it "is degraded … {Diagnostic}"
    (`src/Ashlar.Runtime/Routing/EndpointHealthMonitor.cs:79-120`).
  - **Orchestrator.** `SendThroughTransportAsync` retries only `TIMEOUT` and exempts `AGENT_NOT_FOUND`. **Every other failure goes
    through the circuit breaker** (`Orchestrator.cs:547-569`). After enough failures its fallback relabels the result `CIRCUIT_OPEN`
    (`:612-640`, `:628-631`). The caller then logs at Error and escalates `"AgentExecution"` at High severity with `result.ErrorMessage`
    (`:393-408`).
  - **The gRPC server relay (EG-XPT-05).** It forwards a remote caller's `TargetEndpoint` (`AgentTransportServiceImpl.cs:173`) and returns
    `result.ErrorMessage` and `ErrorCode` **to that remote caller** (`:176-190`).

#### R5. MCP transport client (EG-XPT-06/07)

- **Shape.** `HttpClientTransport` is given `EgressHttp.CreateClient(EgressFamilies.Mcp, "EG-XPT-06")` (`McpClientConnectionManager.cs:328-333`).
  Whether the MCP SDK (2.2.0) wraps a handler exception, and whether its streamable-HTTP/SSE transport reconnects, is
  **[needs a test]**.
- **Who catches:**
  - Tool call: `catch (Exception)` → Warning → a tool failure payload `"Remote call failed: {ex.Message}"`, which **is fed to the calling
    model** (`:166-183`, `:362-363`).
  - Connect at start: `catch` → Error, "could not be connected; it contributes no tools this process lifetime" (`:84-98`). One refusal at
    host start is therefore permanent for the process.
  - Drift refresh: `catch` → Warning, "could not list server", commented "Transient unreachability is not drift" (`:186-207`). This runs
    every interval (`:337-345`).

#### R6. A2A client (EG-XPT-01/02)

- **Shape.** One per-authority `EgressHttp` client (`A2AAgentTransport.cs:158-190`), used by `A2AClient.SendMessageAsync` (`:96`).
- **The retry loop.** It retries **only `HttpRequestException`**, up to `EffectiveOptions.MaxRetries` (`:89-117`). It maps
  `HttpRequestException or A2AException` to `a2a.transport.failed` with `ex.Message` (`:118-123`), and lets everything else propagate.
  Whether A2A 1.0.0-preview2 wraps a handler exception in `A2AException` is **[needs a test]**.
- **The card probe** catches everything except `OperationCanceledException` → "Agent card fetch failed: {ex.Message}" (`:146-155`).
- **If unwrapped,** the refusal escapes to the orchestrator's per-agent `catch (Exception)`. That logs at Error and escalates
  `"AgentExecution"` with `ex.Message` and the stack (`Orchestrator.cs:471-479`).

#### R7. Explicit `Evaluate` sites

| Site | Guard call | Primitive and surrounding catch | A thrown refusal today would… |
|---|---|---|---|
| EG-MESH-01 `pkg publish` | `PkgCommand.cs:389` | `MeshStore.Publish` `:390`. `PublishAsync` has **no try** (`:355-397`). | Escape to System.CommandLine's default handler: stack trace on stderr, exit 1 **[needs a test]** |
| EG-MESH-01 `pkg share` | `PkgCommand.cs:444` | `catch (InvalidOperationException or ArgumentException)` → exit 1 (`:469-473`) | Escape unless it is an `InvalidOperationException` |
| EG-MESH-02 `pkg export` | `PkgCommand.cs:93` | the same catch (`:102-106`) | as above |
| EG-MESH-01 auto-share | `SelfExtendAdmissionBridge.cs:288` | `catch (Exception)` → Warning → `"; auto-share failed: {ex.Message}"` (`:294-300`), into the `GATE:` outcome (`:226`). A "refused" shape already exists (`:281-285`). | Be explained, but labelled "failed" |
| EG-MESH-03 serve | `MeshServeService.cs:313` | Minimal-API handler. **The stream is already open** (`:308`) and returned at `:316`. | Become a 500 from ASP.NET Core and leak the open stream |
| EG-MESH-05 discovery | `MeshDiscoveryService.cs:249` | `AnnounceLoopAsync` runs inside `Task.WhenAll(listenTask, announceTask)` (`:212-213`). The same loop also persists the peer snapshot (`:270`). | Fault `ExecuteAsync`. Under .NET's default `BackgroundServiceExceptionBehavior.StopHost` that **stops the daemon** **[needs a test]**. |
| EG-MESH-07 broadcast | `FileBasedSharedAdaptationStore.cs:57` | Caller `ImproveCommand.cs:401-404`: `catch (Exception)` → Warning. The audit then still says "Promoted" (`:406-418`). | Be explained in the console log |
| EG-MESH-08 sneakernet | `SneakernetTransport.cs:36` | Caller `MeshCommand.cs:199-202`, with no try | Escape (as `pkg publish`) |
| EG-FILE-01/02 StageApp | `NativeBundle.cs:88` | `ExportCommand.cs:74-82`, `:156-164`: catches `InvalidOperationException` → stderr → 65 | Escape unless it is an `InvalidOperationException` |
| EG-PROC-01 dotnet tools | `DotnetRunner.cs:16` | Escapes `DotnetBuildTool.InvokeAsync`. `ToolCallingAgent` catches only `OperationCanceledException` around the tool (`:233-242`), so it falls to `:271-276` and ends the whole cycle with `stopped=error`. The MCP server returns `"Tool 'x' failed: {ex.Message}"` to the **MCP client** (`ToolboxMcpToolContributor.cs:104-110`). | End the cycle; the model never sees it |
| EG-PROC-02 validate | `ValidationServiceAdapter.cs:528`, `:765` | Per-project `catch (Exception)` → `TestResult{Passed=false, Message="Error: {ex.Message}"}` (`:237-250`) | Be explained in validate's output |
| EG-PROC-03 process funnel | `ProcessCommandRunner.cs:18` | No catch. Callers already fail closed on `!Succeeded`: "never fall back to the host" (`DockerSandboxedSessionRunner.cs:78-84`). `KubernetesWorkloadScaler` turns a `GetReplicas` throw into a Warning (`:100-108`). `IsAvailableAsync` maps `!Succeeded` to "unavailable" (`:33-38`). | Propagate to callers that already fail closed |
| EG-TEL-01 OTLP | `application/src/Ashlar.API/Program.cs:247` | Before `builder.Build()`. The comment says a malformed endpoint "must not fail startup" (`:245-246`). | Fail API startup |
| EG-WEB-01 Bing | `BingWebSearchProvider.cs:56` | The guard sits **outside** the try (`:57-72`), which catches `HttpRequestException` → Warning **with the raw query** → empty results (`:64-67`). | Propagate through `WebSearchTool.InvokeAsync` (`:75`) to the agent's cycle catch. If it derived from `HttpRequestException` it would read as "no hits". |
| EG-MDL-11 proposer | `OllamaProposalSource.cs:132` | No catch. `null` already means "no proposal, a normal outcome" (`:141-142`). Caller `AutonomyLoopService.cs:256-274`: per-objective catch, commented "Explained refusal for THIS objective", Warning. | Be explained, per objective |

### 1.4 Retried, swallowed, or misleading: the full list

**Retried** if the refusal's base type is wrong:
- `TransientClassifiers.Network` (`TransientClassifiers.cs:26-36`): the ProviderFactory OpenAI path (`ProviderFactory.cs:184-200`).
- The ProviderFactory Ollama path, through `ModelUnavailableException` (`:181-182`, `:286-292`, `:503-527`).
- The A2A loop (`A2AAgentTransport.cs:108-117`).

**Retried whatever the type, against another destination:**
- `AdaptiveProviderFactory.cs:52-65`.
- `AshlarPeerBrickExecutor.cs:66-83`.

The guard decides each attempt again, so this is fallback, not a retry of the refused send.

**Re-attempted every interval:**
- MCP drift refresh (`McpClientConnectionManager.cs:337-345`).
- The gRPC health monitor (`EndpointHealthMonitor.cs:60-67`).
- Mesh auto-pull (`MeshAutoPullService.cs:154-185`).

**Retried by an external party:**
- AWS SNS redelivery after a 500 from the webhook **[needs a test]**.

**Swallowed or masked as success** (whatever the type):
- `HotSwappableModel.cs:60-71`, `:94-95`: the echo model "answers".
- `ToolCallingAgent.cs:122-126`: "no actions".
- `IdeEndpoints.cs:208-211`: a silent bare catch.
- `MeshAutoPullService.cs:290-293`, `:340-343`: counted as an error, reason dropped.

**Absorbed into an empty or false result with a Warning:**
- `HttpRemoteBrickCatalog.cs:62-65`, `:89-92`.
- `ModelArtifactCatalogService.cs:62-65`.
- `CompositeBrickRegistry.cs:58-61`.
- `RemoteExecutionPlatform.cs:40-43`.

**Swallowed only if derived from `HttpRequestException`:**
- `BingWebSearchProvider.cs:64-67`.
- `MeshLabWorkerExecutorClient.cs:190-193` (at Debug).
- `SnsRsaSignatureVerifier.cs:56-59`.
- `OllamaProvider.cs:120-126`, `:269-275`.

**Misleading text or code:**
- `HotSwappableModel.cs:88-92`: "unreachable model".
- `AdaptiveProviderFactory.cs:67-69`: "No model available".
- `GrpcAgentTransport.cs:79-92`: `GRPC_ERROR_INTERNAL`.
- `InProcessAgentTransport.cs:93-111`: `TRANSPORT_ERROR`.
- `Orchestrator.cs:628-631`: `CIRCUIT_OPEN`.
- `EndpointHealthMonitor.cs:86-97`: "degraded".
- `McpClientConnectionManager.cs:93-98`: "could not be connected".
- `AuditingChatClient.cs:56-60`: an audit `"fault"`.

### 1.5 The guard fault path today

1. **An internal fault in `Evaluate`** produces `Fault=<type name>` and `Access=default`, which is refused with `NoDecision`, and is published
   (`EgressGuard.cs:93-120`). But `enforces` stays at its placeholder `false` whenever `ResolveProfile` (`:80`) did not finish (`:66`).
2. **A host `IEgressGuard` that throws** is swallowed and counted in the HTTP handler (`EgressGuardHandler.cs:81-85`, counter `:24`,
   `:48`, internal and not exported). It is swallowed silently in the MEAI layer (`EgressGuardChatClient.cs:67-70`). The inventory records
   that MEAI faults are not counted (`docs/EgressInventory.md:13`).
3. **A host guard that returns `null`** is discarded today with `_ =`. Under enforcement, dereferencing it would throw a
   `NullReferenceException`.
4. **A guard that cannot be resolved** falls back to `ProcessDefault`, silently and uncounted (`EgressServiceCollectionExtensions.cs:76-105`;
   `AshlarGovernanceChatClientBuilderExtensions.cs:56-66`).
5. **A sink or event-listener fault** is swallowed and counted. The decision is still returned (`EgressDecisionLog.cs:50-93`).
6. **A decision made while a record is being published on the same thread** is returned but not published, only counted
   (`EgressDecisionLog.cs:52-56`).

### 1.6 Where `Exception.Message` travels (the exits that cross the trust boundary)

| Exit | Code | Recipient |
|---|---|---|
| Every in-process agent failure: `ErrorMessage = ex.Message` | `InProcessAgentTransport.cs:93-111` | Feeds the next three rows |
| The A2A server fails the task with `result.ErrorMessage`. A thrown exception is replaced by a generic text (`:101-110`). | `AshlarA2AAgentHandler.cs:81-88` | **A2A peer** |
| The gRPC server returns `ErrorMessage` and `ErrorCode` | `AgentTransportServiceImpl.cs:176-190` | **Remote gRPC caller** |
| The MCP server tool failure `"Tool 'x' failed: {ex.Message}"` | `ToolboxMcpToolContributor.cs:104-110` | **MCP client / IDE** |
| The IDE run SSE `error` with `ex.Message` | `IdeEndpoints.cs:465-473` | **API client** |
| An MCP client tool failure fed back to the model | `McpClientConnectionManager.cs:182` | **The model** (the subject) |
| Agent memory `react.error` and `think.error` | `ToolCallingAgent.cs:125`, `:275` | Agent memory, and from there prompts |
| The orchestrator's escalation text | `Orchestrator.cs:403-406`, `:474-478` | Escalation manager (operator) |
| The `GATE:` outcome string | `SelfExtendAdmissionBridge.cs:299` | Operator |
| Validate's `TestResult.Message` | `ValidationServiceAdapter.cs:249` | Operator |
| Result details from RunPod and the peer executor | `RunPodHttpClient.cs:70`; `AshlarPeerBrickExecutor.cs:195` | Callers, and from there operators |
| An unhandled API exception | no `UseExceptionHandler` or `IExceptionHandler` in `application/src/Ashlar.API` (grep) | A 500. In Development, .NET's default developer exception page shows the full exception **[needs a test]**. |

### 1.7 Telemetry today

- **EventSource.** `Ashlar-Egress` event 1, `Decision`, at Informational. Its 17 string and bool fields are a public contract: "append fields,
  never reorder or rename them" (`EgressEventSource.cs:14`, `:20`, `:56`).
- **ILogger sink.** Category `Ashlar.Egress`, event 7300, **always Debug**, outcome `would-allow` or `would-refuse`
  (`LoggerEgressDecisionSink.cs:38`, `:41`, `:62`, `:67`). It is subscribed only where `AddAshlarEgressGuard` ran. CLI verbs that
  build their own collections (for example `MeshCommand.cs:192-197`) have only the EventSource.
- **MEAI audit.** `ChatInvocationAuditRecord`, in memory only (`MeaiPipelineServiceCollectionExtensions.cs:203`).
- **Durable audit.** `IDataDecisionAuditLog` exists (`src/Ashlar.Core.Application/Trust/Ports/IDataDecisionAuditLog.cs:9-47`). It is backed
  by LiteDB when `ASHLAR_TRUST_AUDIT_DB` is set and is registered only by `AddTrustServices`
  (`src/Ashlar.BackgroundAgents/ServiceCollectionExtensions.cs:179-184`), so on F, S and SW and not on AG. It has no egress event type.
- **Barrier audit.** `IBarrierAuditLog` already records a refused barrier as `CeilingExceeded` (`HttpBarrierContextMiddleware.cs:83-91`).
- **No egress counters.** No refusal or fault metric is exported. `GuardFaults`, `SinkFaults` and `ReentrantSkips` are internal statics.

---

## 2. Analysis

### 2.1 HTTP: throw, or return a synthetic response?

- **A synthetic 403 (or another 4xx) is not explained at most callers:**
  - `EnsureSuccessStatusCode` turns it into `HttpRequestException("Response status code does not indicate success: 403 (Forbidden).")`,
    and the reason is gone. Affected: `RemoteBrick.cs:91`, `HttpWorkflowWebhookClient.cs:33`/`:45`, `BingWebSearchProvider.cs:60`,
    `OllamaProposalSource.cs:134`, `ProviderFactory.cs:337`.
  - That `HttpRequestException` is then transient (`TransientClassifiers.cs:31`), retried by A2A (`A2AAgentTransport.cs:108`), and
    swallowed by Bing, SNS and MeshLab (§1.4).
  - `OllamaProvider.cs:242-246` reads it as `OLLAMA_CHAT_HTTP_ERROR` → `ModelUnavailableException` → retried 4 times.
  - It also reads as the remote's answer, which misattributes the refusal in operator logs.
  - It breaks the handler's own guarantee, "returns the inner handler's response instance unchanged" (`EgressGuardHandler.cs:8-12`).
  - **The one place it is better is gRPC.** Grpc.Net.Client maps HTTP 403 to `PermissionDenied` **[needs a test]**. A thrown exception
    with a `Status.DebugException` check in `GrpcAgentTransport` gives the same precision.
- **A thrown exception of a fresh type keeps its message at every catch above it.** No classifier in the repo retries it, because it is
  neither transient nor `ModelUnavailableException`. The new exception does **not** change any retry decision as long as it does not
  derive from `HttpRequestException`, `IOException`, `SocketException`, `TimeoutException` or `OperationCanceledException`.
  - Recommendation: return `Task.FromException` from `SendAsync` and throw from `Send`.
  - HttpClient passes a handler's non-cancellation exceptions through unwrapped **[needs a test]**.

### 2.2 The base type

| Base | Captured by | Consequence |
|---|---|---|
| `HttpRequestException` / `IOException` | the transient classifier; the A2A retry; Bing, SNS, MeshLab and OllamaProvider | Retried and swallowed: **excluded** |
| `InvalidOperationException` | about 36 non-test catch sites (grep), including the CLI verbs' clean exit-65 paths, but also `InProcessAgentTransport.cs:52-53`, which matches on the message containing "not registered" and maps to `AGENT_NOT_FOUND` | The CLI gets the refusal for free, but generic IOE handlers capture a security refusal |
| `UnauthorizedAccessException` | 7 sites, all file-I/O (`AutonomyLoopService.cs:449` silent, `RoslynCodeAnalysisService.cs:366` returns `false`, …) | The meaning fits (security denial), but it inherits "file denied" handling |
| `Exception` (sealed) | only generic `catch (Exception)` | Matches the four in-repo refusal exceptions (§1.2). The CLI verbs need an explicit catch each (about 6 sites). |

- **A new type in `Ashlar.Abstractions` needs a build to settle the analyzers:**
  - `AnalysisMode=All` with warnings as errors will raise CA1032 (standard constructors), and possibly the serialization rules on the
    netstandard2.0 asset.
  - The repo's practice is a `#pragma` suppression with a written reason (`EgressGuard.cs:93`, `EgressGuardHandler.cs:80`). A refusal
    should always carry its decision, which argues for suppressing CA1032 rather than adding a constructor without one.

### 2.3 What each route should do under enforcement (the shape this lens recommends)

| Route / site | Mechanism | Who sees what | Catch-site changes needed |
|---|---|---|---|
| R1/R2 handler | `Task.FromException(EgressRefusedException)`; throw on `Send` | Caller code sees the exception | Rethrow refusals before the swallow in: `HttpRemoteBrickCatalog`, `ModelArtifactCatalogService`, `RemoteExecutionPlatform`, the IDE tags probe. Or log them at Warning; that is the D4 choice. |
| ProviderFactory chain | propagate | — | `AdaptiveProviderFactory` keeps the refusal as the final exception when every provider refused, or stops at the first refusal. `HotSwappableModel` rethrows refusals before the echo fallback and before wrapping. |
| R3 MEAI | `EgressGuardChatClient` writes a `denied`/`egress_refused` audit record (a constructor change) and throws the same exception type | — | `AuditingChatClient` classifies the refusal as `denied` (2 catch blocks). `ToolCallingAgent`: a refusal is neither "no actions" (`:122-126`) nor a generic error. It needs an explicit stop reason (S5 proposes `egress_refused`). |
| R4 gRPC | the handler throws, and Grpc wraps it | The remote caller (relay) | `GrpcAgentTransport` maps `Status.DebugException is EgressRefusedException` to `ErrorCode="EGRESS_REFUSED"` with the redacted text. `AgentTransportServiceImpl` passes only the generic text over the wire. |
| R5 MCP | the handler throws | The model | The tool failure payload uses the redacted message, so `ex.Message` is safe if D-b holds. Connect and drift: log as "refused", not "could not connect". |
| R6 A2A | the handler throws | — | Add `catch (EgressRefusedException)` → `Failure("a2a.egress_refused", redacted)`, ahead of the `HttpRequestException` retry. The SDK may wrap it, so a test must pin the shape. |
| Orchestrator | — | Operator escalation | Exempt `EGRESS_REFUSED` from the circuit breaker, as `AGENT_NOT_FOUND` is (`Orchestrator.cs:558-561`). Add a specific escalation code, as for `BarrierContextMissingException` (`:456-469`). |
| `InProcessAgentTransport` | — | A2A, gRPC and MCP servers downstream | Catch the refusal → `ErrorCode="EGRESS_REFUSED"`. The servers can then map it to the generic text. |
| EG-MESH-01/02/08, FILE-01/02 (CLI) | throw | The local operator | Each verb catches the refusal → stderr (redacted, plus seq) → a refusal exit code (D8) |
| EG-MESH-01 auto-share | check the decision, no throw | `GATE:` outcome | Return `"; auto-share refused: …"` in the existing shape (`SelfExtendAdmissionBridge.cs:281-285`) |
| EG-MESH-03 serve | check the decision, no throw | Peer: 404; operator: Warning | Dispose the already-open stream, then `Results.NotFound()`, as in the convention at `:303-311` |
| EG-MESH-05 discovery | check the decision, no throw | Operator Warning | Degrade to listen-only (`beacon = null`) and keep the persist loop running |
| EG-MESH-07 | throw | Console Warning | Already explained (`ImproveCommand.cs:404`) |
| EG-PROC-01 | throw at the runner; the tool returns `ToolResult{ok=false,error}` | The model, as an observation | 3 tools, or the shared runner wrapper |
| EG-PROC-02 | throw | validate output | none |
| EG-PROC-03 | throw | Callers already fail closed | Avoid a synthetic exit code: `IsAvailableAsync` would read it as "kubectl unavailable" |
| EG-TEL-01 | check the decision | Operator | Skip `AddOtlpExporter` and write a stderr line, rather than failing startup (D6) |
| EG-WEB-01 | throw | The model, as an observation | `WebSearchTool` catches and returns the "blocked" shape it already has (`:64-71`) |
| EG-MDL-11 | throw | Operator | none: `AutonomyLoopService.cs:256-274` already explains it |

**API shape.**
- Explicit sites keep `….Evaluate(new EgressRequest(…))`, so the G3 regex at `Scanner.cs:668` still matches, and chain
  `.ThrowIfRefused()` on the decision.
- A non-throwing form, `decision.IsRefused` plus a redacted text, serves the four degrade sites.
- An `Enforce(...)` helper would hide the call from G3.
- A new rule "an explicit site must not discard the decision" is a new blocking convention, and needs a `ci/cert-gate-assertions.md` row.

### 2.4 Faults under enforcement (§7, "fail closed")

- **Every fault becomes an explained refusal.** That covers an `Evaluate` fault, a host guard that throws, and a host guard that returns `null`.
  - The reason is `NoDecision`.
  - The text names the fault's **type** only (as `EgressGuard.cs:97-99` already does), for example "the egress guard could not decide
    (System.X); the egress fails closed".
- **The enforcement switch must not depend on the faulting evaluation.** Today `enforces` is computed inside the try and defaults to
  `false` (`EgressGuard.cs:66`, `:80`). If enforcement keyed off `decision.ProfileEnforcesByDefault`, a fault in `ResolveProfile` would
  fail **open**.
  - The handler and the MEAI layer also need the mode when the guard itself threw (`EgressGuardHandler.cs:81-85`), and then there is no
    decision to read it from. So the mode must be resolved separately: process level, from the profile and override.
- **A guard that cannot be resolved** may fall back to `ProcessDefault`, which still enforces. The fallback should be counted and logged,
  because it bypasses a host's stricter guard (`EgressServiceCollectionExtensions.cs:83-85`, `:101-103`).
- **Sink faults and re-entrant skips** leave the refusal enforced. The only explanation is then the exception, which is acceptable.
  A re-entrant refusal (`EgressDecisionLog.cs:52-56`) should also be counted as a refusal.

### 2.5 Redaction (open question D): three audiences

1. **Operator.** The EventSource, the ILogger sink and durable audit get the whole record: destination, labels, bases, `Detail`, profile,
   fault and seq. They already carry this. The record never holds a payload, path or query (`EgressDecision.cs:3-12`).
2. **In-process code and the local subject (the model, agent memory).** These get `Exception.Message`, which §1.6 shows **cannot be held
   inside the boundary**. It should carry reason category, site, family, destination class and `#seq`, plus `no-subject` when that is the
   basis: no level names, no compartment or caveat tokens, no `Detail`. The full `EgressDecision` rides on the exception as a property,
   for in-process loggers.
   - Under today's no-subject state, every refusal is `SystemHighData` with basis `no-subject`. Saying so in the text is what makes
     "nothing labels this flow yet" diagnosable, and it reveals nothing.
3. **Remote parties** (A2A peer, gRPC caller, MCP client, API client, mesh puller). They get a fixed generic text with only the
   correlation number, or a bare 404/403 where a status is the protocol: mesh serve (`:303-311`) and the barrier middleware (`:80-93`)
   are the precedents.
   - This needs the refusal to be recognisable after flattening, which is why `InProcessAgentTransport` should set
     `ErrorCode="EGRESS_REFUSED"`.
   - Even the reason category tells a remote party that compartmented data exists, so it stays off the wire by default.

### 2.6 Telemetry under enforcement

- **Mode and a refused flag.** Set `Mode` to `enforce`. Append a `refused` (or `enforced`) bool to event 1; the contract allows
  appending fields (`EgressEventSource.cs:14`). Optionally add **event 2, `Refused`, at Warning**, so a listener at Warning level gets
  only refusals.
- **The ILogger sink.** Refusals at **Warning** (for example event 7301 `EgressRefused`, outcome `refused`). Allowed decisions stay at
  Debug. Without this, a caller that swallows a refusal leaves only a Debug line, which is off by default: silent in practice.
- **Volume.** The periodic loops (§1.4) would log one Warning per tick. Logging the first refusal per site at Warning and repeats at
  Debug, or rate-limiting, keeps the signal.
- **MEAI audit.** A `denied`/`egress_refused` record, as PolicyGate writes one.
- **Durable audit.** An `IEgressDecisionSink` adapter onto `IDataDecisionAuditLog` covers F, S and SW. **AG has no durable store**
  (§1.7), so its record is the EventSource plus the ILogger output.
- **Counters.** A refusal counter on a meter (site, family, reason) is optional. Note that exporting it is itself EG-TEL-01.

### 2.7 What the seeded leak test should assert, from this lens

The scenario: a subject frame at, say, `Secret//C:ALPHA` sends to a factory client and to a MEAI target, under AG. The test should check:

- The inner handler or inner chat client was **never called**.
- The caller catches `EgressRefusedException` with the expected `Reason`, and `Message` contains the site and `#seq`.
- `Message` does **not** contain `ALPHA` or `Detail`.
- The published record has mode `enforce` and refused, and the ILogger entry is at Warning.
- A host guard that throws is refused with `NoDecision`.
- A refusal is not retried: one decision per send through `ProviderFactory`, and no echo output from `HotSwappableModel`.

### 2.8 Settled only by a test

- Whether HttpClient passes a handler exception through unwrapped.
- Grpc.Net.Client's mapping: status code, Detail text, `DebugException`.
- Whether the A2A 1.0.0-preview2 and MCP 2.2.0 transports wrap a handler exception, and whether either reconnects or retries.
- System.CommandLine beta4's default unhandled-exception output and exit code.
- `BackgroundServiceExceptionBehavior` in the daemon host.
- AWS SNS redelivery on a 500.
- Whether the Development developer exception page appears in Ashlar.API.
- CA1032 and the serialization rules for a new exception in `Ashlar.Abstractions` on all three TFMs.

---

## 3. Decisions for the owner

### RD1. One refusal exception type, or one per surface?

- **Options:**
  - (a) One public sealed `EgressRefusedException : Exception` in `Ashlar.Abstractions.Security.Egress`. It carries `Decision`,
    `Reason`, `Site`, `Sequence` and `ErrorCode = "EGRESS_REFUSED"`, and is thrown by every route, MEAI included.
  - (b) Per surface: an `HttpRequestException` subclass for HTTP, `PolicyViolationException("egress_refused")` for MEAI, and an
    `InvalidOperationException` for explicit and CLI sites.
  - (c) One type derived from `InvalidOperationException` (or `UnauthorizedAccessException`), so existing catches pick it up.
- **Consequences:**
  - (a) Callers have one thing to catch, and no classifier retries it. It needs about 6 CLI catch sites, 2 `AuditingChatClient`
    blocks, and catches in the A2A, gRPC and in-process transports. CA1032 must be settled by a build.
  - (b) The HTTP subclass is retried and swallowed at the sites in §1.4. PolicyViolation fits MEAI auditing, but Abstractions cannot
    reference AI.Pipeline, so every non-MEAI site needs a second type.
  - (c) The CLI handling comes free, but about 36 generic `InvalidOperationException` handlers, one of which matches on message text, can
    capture a security refusal.
- **Recommendation: (a).**

### RD2. Does an HTTP refusal throw from `SendAsync`, or return a synthetic 4xx?

- **Options:**
  - (a) A faulted task with `EgressRefusedException` (a throw on synchronous `Send`).
  - (b) A synthetic 403 with an explanation header or body.
  - (c) A throw for Ashlar's own clients and a 403 for host-application clients.
- **Consequences:**
  - (a) The message survives every catch, and nothing retries it.
  - (b) `EnsureSuccessStatusCode` strips the reason. The result is retried as an `HttpRequestException` (A2A, the transient classifier,
    the Ollama path) and swallowed by Bing, SNS and MeshLab. It reads as the remote's answer.
  - (c) Two behaviours to test, and host applications still get the problems of (b).
- **Recommendation: (a).** Host applications, if the owner enforces on them (3a Q1), see a new exception type from their `HttpClient`.
  The guard is outermost, so their resilience handlers never see it. Their outer generic catches will.

### RD3. How much does a refusal reveal by default? (open question D)

- **Options:**
  - (D-a) The full `Detail` in `Message`.
  - (D-b) `Message` = reason category + site + family + destination class + `#seq` (+ `no-subject`). The full decision is a property
    and goes to the log.
  - (D-c) D-b plus level names, never compartment or caveat tokens.
  - (D-d) Detail that depends on the profile.
- **In every option,** the trust-boundary exits (A2A server, gRPC server, MCP server, API SSE, mesh serve) send only a generic text and
  `#seq`, or a bare status.
- **Consequences:**
  - (D-a) Compartment names reach A2A, gRPC and MCP peers and the model (§1.6).
  - (D-b) The model can still re-plan on the category. Operators join on `#seq`.
  - (D-c) Level names reveal the high-water mark to whoever reads agent memory.
  - (D-d) Two texts to keep straight. Profiles are not a trust boundary.
- **Recommendation: (D-b), plus the boundary rule.** It needs the in-process transport to set `EGRESS_REFUSED` so the servers can tell a
  refusal apart.

### RD4. Are refusals retried, or allowed to fall back?

- **Options:**
  - (a) Never retried and never fallen back from: a refusal propagates through every catch-all.
  - (b) No retry to the same destination. Fallback to **another guarded** destination is allowed, and the refusal must remain the final
    error when all are refused. Echo fallback and the circuit breaker are exempt.
  - (c) Leave today's catch-alls alone; the refusal is explained only in the guard's log.
- **Consequences:**
  - (a) `AdaptiveProviderFactory` and the peer executor lose fallback to an in-process or local target that would legitimately pass.
  - (b) Changes: `HotSwappableModel` (rethrow before echo and before wrapping), `AdaptiveProviderFactory` (keep the refusal),
    `Orchestrator` (exempt it from the breaker), and `ToolCallingAgent.ThinkAsync` (not "no actions"). The periodic loops log a refusal
    once per site.
  - (c) The echo model "answers" a refused prompt (`HotSwappableModel.cs:60-71`), a refused model call reads as "nothing to do", and the
    breaker reports `CIRCUIT_OPEN`. That fails §7.
- **Recommendation: (b).**

### RD5. What does a guard fault do under enforcement?

- **Options:**
  - (a) Fail closed. An `Evaluate` fault, a host guard that throws, and a host guard that returns `null` are each refused with `NoDecision`
    and a type-name-only explanation. The enforce switch is resolved outside the guard.
  - (b) Fail open with a Warning.
  - (c) Fail closed only on AG and SW.
- **Consequences:**
  - (a) Matches §7. A buggy host guard stops egress, loudly. It needs `enforces` decoupled from `EgressGuard.cs:66`/`:80`.
  - (b) Any guard fault becomes a bypass.
  - (c) Enforcement is per profile anyway, so (c) is (a) whenever enforcement is on, and only complicates the wording.
- **Recommendation: (a).**

### RD6. Where throwing is the wrong shape: degrade with an explanation, or throw?

The sites are mesh serve, mesh discovery, OTLP and auto-share.

- **Options:**
  - (a) Degrade: mesh serve returns 404 after disposing the stream; discovery goes listen-only with a Warning; OTLP skips the exporter
    with a stderr line and a Warning at startup; auto-share returns `"; auto-share refused: …"`.
  - (b) Throw everywhere.
- **Consequences:**
  - (a) Nothing leaves, and each case is explained to the operator. The peer learns nothing. Four non-throwing code paths, each needing a
    test.
  - (b) Discovery stops the daemon (StopHost). OTLP fails API startup over telemetry configuration. Mesh serve returns a 500 and leaks the
    open stream.
- **Recommendation: (a).** If the owner wants a misconfigured OTLP endpoint on AG to fail startup, that is the one exception to (a).

### RD7. Refusal telemetry: log level and durable record

- **Options:**
  - (a) Refusals at Warning in the ILogger sink, with a new EventSource event 2 at Warning and a `refused` field appended to event 1. MEAI
    gets a `denied`/`egress_refused` audit record. An `IDataDecisionAuditLog` adapter covers F, S and SW. First-per-site Warning, repeats at
    Debug.
  - (b) Keep everything at Debug.
  - (c) (a) plus a durable egress-refusal file sink for AG.
- **Consequences:**
  - (a) A swallowed refusal still reaches a default-level log. Console-logging CLI collections print a refusal line, but only when one
    happens.
  - (b) A refusal swallowed by a caller (§1.4) is explained only in a log that is off.
  - (c) More code, plus a decision about where AG keeps it.
- **Recommendation: (a).** The owner decides whether AG needs (c).

### RD8. The CLI exit code for a refusal (minor)

- **Options:** (a) reuse 65 (EX_DATAERR, today's "does not verify"); (b) use 77 (EX_NOPERM).
- **Consequences:** (a) scripts cannot tell "bad package" from "policy refused"; (b) one new code to document.
- **Recommendation: (b).**

### RD9. What the model sees when a tool or model call is refused

- **Options:**
  - (a) A tool-level refusal (dotnet restore, web search, MCP tool) becomes a `DENIED`-style observation (`ToolCallingAgent.cs:222-231`
    shape), and the cycle continues. A refused model call ends the cycle with an explicit stop reason.
  - (b) Every refusal ends the cycle.
- **Consequences:**
  - (a) The model can re-plan (for example, stop asking for web search). It sees only the D-b text.
  - (b) Simpler, but one refused restore aborts an otherwise valid cycle as `error` (`:271-276`).
- **Recommendation: (a).**
