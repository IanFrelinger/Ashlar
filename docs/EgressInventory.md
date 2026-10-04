# Egress inventory

This file lists every path by which a request or data leaves an Ashlar process, as found at master `f1f2cff` (2026-10-04). It is the written inventory for SPEC-007 PR 3. All line numbers refer to that commit.

- **The machine-checked half is `ci/egress-inventory.tsv`.** It pins a count per file and per marker, and `EgressGuardConventionTests` (cert-gate) holds those counts exactly. Every TSV row names IDs from this file. Every `EG-…` id that production code passes to the guard must have a row here.
- **What the controls are.** These are classification-style controls inside the runtime. They are not an accredited cross-domain solution.
- **PR 3 lands in two parts, and 3a routes nothing yet.** 3a ships the guard and its decision log (`IEgressGuard`, `EgressGuard`, `EgressHttp` and `EgressDecisionLog`, in `src/Ashlar.Abstractions/Security/Egress/`), `AddAshlarEgressGuard` and the logger sink it subscribes (in `src/Ashlar.Infrastructure/Egress/`), this inventory and the convention test. It changes no call site below, and no production composition calls `AddAshlarEgressGuard`. So the TSV marks every site that 3b routes `Unrouted`: a temporary reason that means "listed, not routed yet". The **Route (3b)** column says how 3b routes each path. 3b converts every `Unrouted` row and removes the reason. An `Exempt:` route is final, so the TSV carries it from 3a.
- **What PR 3 does and does not do.** Both parts are report-only: the guard decides and logs each decision, and refuses nothing. PR 4 turns enforcement on per deployment profile, with AirGapped and SecureWorkstation enforcing by default.
- **A gap PR 4 must close before it enforces: synchronous `Send` on the netstandard2.0 asset.** .NET 5–7 apps resolve the netstandard2.0 asset of `Ashlar.Abstractions`, which is compiled against a `DelegatingHandler` with no `Send` to override, so a synchronous `HttpClient.Send` or `HttpMessageInvoker.Send` through an `EgressHttp` client or handler goes to the inner handler without being evaluated; only `SendAsync` is evaluated there. No Ashlar source makes a synchronous HTTP `Send`. Before PR 4 enforces, keep synchronous callers off that asset or add a target framework that exposes `Send`.

**Profiles.**

| Code | Profile |
|---|---|
| F | Full |
| S | Server |
| E | Edge |
| AG | AirGapped |
| SW | SecureWorkstation |
| Sys | System |

Module flags and the profiles that include them (`src/Ashlar.Hosting/AshlarServiceCollectionExtensions.Deployment.cs:98-145`):

| Module | Profiles |
|---|---|
| NCR, Adaptation | F, S, AG, SW |
| Trust, BackgroundAgents, WorkflowIntegrations | F, S, SW |
| RuntimeTransport, TestingAdapters | F, S |
| Persistence | F, S, E, AG, SW |

Only one runtime check on a profile touches any path below: `AshlarDeploymentProfileEnvironment.ForbidsRemoteProtocolEgress` (`src/Ashlar.Abstractions/AshlarDeploymentProfileEnvironment.cs:56`). It is used by the MCP-client, A2A-client and A2A-server option validators. From 3a, `EgressGuard` also calls it, report-only: when the guard reads the profile from the environment, `ResolveProfile` (`src/Ashlar.Abstractions/Security/Egress/EgressGuard.cs:138`, a file 3a adds) uses it to set `EgressDecision.ProfileEnforcesByDefault`. The guard gates no path below.

**Route vocabulary (3b)**

The **Route (3b)** column below uses these routes. Except for `Exempt:` and `Unscanned:`, each one describes what 3b does; in 3a the TSV pins those sites `Unrouted`.

| Route | Meaning |
|---|---|
| `Handler(factory)` | An `IHttpClientFactory` client. `AddAshlarEgressGuard` puts the guard handler on every factory client through one `ConfigureHttpClientDefaults` call. 3b calls it in every member that calls `AddHttpClient`. |
| `Handler(raw)` | A client the code builds itself. 3b builds it with `EgressHttp.CreateClient` or `EgressHttp.Wrap`, so the guard handler sees every `SendAsync` (and, on net8.0 and later, every `Send`). |
| `Governance` | A MEAI chat target. 3b adds `EgressGuardChatClient` as the outermost middleware in `UseAshlarGovernance`. |
| `Explicit` | 3b makes the member call `guard.Evaluate(new EgressRequest(…))` before the primitive, in the same block or an enclosing one. |
| `Exempt:<reason>` | Listed and pinned, not routed. The reasons are a closed list: `LocalOnly`, `Operator`, `Inbound`, `DataStore`, `LocalDaemon`, `ConsumerSdk`, `TestDouble`, `TestSeam`. They are final, so the TSV already carries them in 3a. |
| `Unscanned:<reason>` | The source scan cannot see it: out of process, inbound server, host-configured or a dormant port. Listed so the inventory is complete. |

Two kinds of TSV pin have no row of their own here, and their `ids` cell is `-`. The guard's own raw constructions in `src/Ashlar.Abstractions/Security/Egress/` (`EgressHttp` has to build a client to hand out a guarded one) are pinned `Exempt:GuardImpl`, a reason the convention test accepts only under that folder. The shipped test double in `src/Ashlar.Agents.TestKit/StubHttpMessageHandler.cs` is pinned `Exempt:TestDouble`.

## 1. Model calls

| ID | Where | Leaves via | Destination | Payload | Gates today | Label at the site | Route (3b) |
|---|---|---|---|---|---|---|---|
| EG-MDL-01 | `src/Ashlar.AI.Pipeline/Clients/OllamaHttpChatClient.cs:48`, `:72`; client built at `:166` | `HttpClient.PostAsJsonAsync` / `SendAsync`, POST `api/chat` | `OllamaEndpointResolver.ResolveBaseUrl` (`OllamaEndpointResolver.cs:215-223`): `ASHLAR_OLLAMA_BASE_URL`, then `Ashlar:Meai:OllamaBaseUrl`, then `OLLAMA_BASE_URL`, else `http://localhost:11434`. Any host is accepted. | The whole conversation (system, user, assistant and tool observations). Sent from `MeaiBackedModel.cs:61`, which every `IModel` consumer uses, and from `application/src/Ashlar.API/Endpoints/IdeEndpoints.cs:410-421`. | Registered in every profile (Phase13b, `AshlarKernelRegistrar.Phases.cs:409-441`) unless `ASHLAR_USE_MEAI_PIPELINE=0`. PolicyGate allows every `local:*` key (`DefaultChatTargetAccessPolicy.cs:25-30`). Sanitising is `Pass` for keys that are not `cloud:`. `local:` is only a name and is not checked against the URL. | Target key and model id. The resolved URL is available. No data label. Caller identity is never wired (`AshlarGovernanceChatClientBuilderExtensions.cs:25`). | Governance. The destination is the resolved URL, so a remote Ollama is classified as an external model. |
| EG-MDL-02 | `src/Ashlar.AI.Pipeline/Clients/AwsBedrockChatClientFactory.cs:38-42` | AWS SDK: `new AmazonBedrockRuntimeClient` plus `AsIChatClient`, calling Converse or ConverseStream (SigV4). The SDK owns the HTTP. | `bedrock-runtime.<region>.amazonaws.com` | Chat text after sanitising: PII redacted, a secret blocks the call. Only text parts are inspected (`DefaultChatMessageSanitizer.cs:33`). Also the model id. | Needs `Ashlar:Meai:Bedrock:Enabled=true` and the key in `AllowedCloudTargets`. Under `AddAshlar` the allow-list appears dead (`Phases.cs:415` vs `MeaiPipelineServiceCollectionExtensions.cs:96/185`; static reading only), so `cloud:bedrock:*` is denied. No profile check. | Target key `cloud:*` | Governance |
| EG-MDL-03 | `src/Ashlar.Infrastructure/Execution/ProviderFactory.cs:327-334`, through `SendHttpWithResilienceAsync` `:183-199`; static client at `:60` | `HttpClient.SendAsync`, POST `chat/completions` | `OPENAI_BASE_URL`, else `https://api.openai.com`; or `OPENAI_COMPAT_BASE_URL` (vLLM, LiteLLM) | System and user prompt, with a Bearer key. Callers: `ProviderBackedModel.cs:33`, `OWASPScannerBrick.cs:212` (the source code under scan), `ContentGenerator.cs:41`, `ProviderGeneratorModel.cs:61`, `ProviderCompositionGeneratorModel.cs:41`, `ProviderFactoryLocalExecutor.cs:39` | `IProviderFactory` is registered in every profile (Phase15, `Phases.cs:571-607`). A provider is available whenever its API-key env var is set. `SanitizingProviderFactory` applies only with trust services (F, S, SW) and `ASHLAR_TRUST_ENABLED=1`. `AdaptiveProviderFactory` (`ASHLAR_LOAD_PREFERENCE`) can fall back from local to cloud (`AdaptiveProviderFactory.cs:40-58`). AG reaches OpenAI if `OPENAI_API_KEY` is set. | Provider name; the URL at the wire | Handler(raw). `Http` is built by `EgressHttp.CreateClient`, so the guard sees the request after the adaptive rewrite. |
| EG-MDL-04 | `ProviderFactory.cs:365-372` | Same static client, POST | `{AZURE_OPENAI_ENDPOINT}/openai/deployments/{deployment}/chat/completions` | Prompts, `api-key` header | As EG-MDL-03, plus all three `AZURE_OPENAI_*` variables | Provider name | Handler(raw), shared client at `:60` |
| EG-MDL-05 | `ProviderFactory.cs:421`, reached from `ExecuteVisionAsync` `:554-582` and `ExecuteVisionMultiFrameAsync` `:632-660` | Same static client, with no resilience wrapper | OpenAI or compat base URL | Prompts plus a base64 PNG as a data URI | As EG-MDL-03. The sanitiser never sees the image bytes (`SanitizingProviderFactory.cs:66-117`). | Provider name | Handler(raw) |
| EG-MDL-06 | `ProviderFactory.cs:465` | Same static client | Azure deployment URL | Prompts plus a base64 PNG | As EG-MDL-05 | Provider name | Handler(raw) |
| EG-MDL-07 | `src/Ashlar.Infrastructure/Execution/Ollama/OllamaProvider.cs:241` (chat) and `:86` (tags); client built at `ProviderFactory.cs:813` | `HttpClient.SendAsync` / `GetAsync` | The ephemeral container when `ASHLAR_EPHEMERAL_MODELS=1`; otherwise `ASHLAR_OLLAMA_BASE_URL`, then `OLLAMA_BASE_URL`, then `http://localhost:11434` (`ProviderFactory.cs:769-780`) | Prompts and base64 images. Warm-up and availability checks send `GET api/tags`. | The default provider unless `ASHLAR_ALLOW_MOCK=1`. No profile check. Blind to the URL. | Provider name, base URL | Handler(raw) at `:813`. `OllamaProvider` receives that client (Upstream). |
| EG-MDL-08 | `ProviderFactory.cs:731` (`ExecuteVideoAsync` `:676`) | Same static client, multipart POST | `{VIDEO_SERVICE_URL}/v1/analyze` | Prompt, system prompt, and an mp4 of every frame | Needs `VIDEO_SERVICE_URL`. No profile check. Frames are not sanitised. | None | Handler(raw) |
| EG-MDL-09 | `src/Ashlar.Infrastructure/Execution/Routing/RunPodHttpClient.cs:43`, `:91`, `:128`, `:160`, `:184` | Typed client `AddHttpClient<IRunPodClient, RunPodHttpClient>` (`RunPodCapabilityRoutingServiceCollectionExtensions.cs:43-50`) | `RunPodBrickConfig.BaseUrl`, else `https://api.runpod.io` | Dispatch body `{modelId, prompt, generationParameters, outputFormat, priority}` (`:82-89`). Without an API key, spin-up metadata still leaves, unauthenticated (`:199-209`). | NCR module: F, S, **AG, SW** (`Phases.cs:68-72`). `NcrCapabilityRouter` sends work remote on an overnight flag, VRAM shortfall or queue depth (`NcrCapabilityRouter.cs:125-154`). It checks neither `IsAirGapped` nor the profile. | None | Handler(factory) |
| EG-MDL-10 | `src/Ashlar.Infrastructure/NodeCapabilityRuntime/Backends/OllamaModelServingBackend.cs:224` (`/api/chat` `:70`, `/api/generate` `:106`/`:128`, `/api/pull` `:150`); GETs at `:43`, `:170` | Named factory client (`NodeCapabilityRuntimeServiceCollectionExtensions.cs:117-131`) | `ASHLAR_OLLAMA_BASE_URL`, then `Ashlar:NodeCapabilityRuntime:Ollama:BaseUrl`, then `OLLAMA_BASE_URL`, then `http://127.0.0.1:11434` | Live traffic is load/unload (model id, keep_alive) and health GETs. `RunInferenceAsync` would send prompts but has no product caller. `/api/pull` makes the Ollama daemon download, a second hop outside the process. | NCR module: F, S, AG, SW | Model id | Handler(factory) |
| EG-MDL-11 | `src/Ashlar.BackgroundAgents/Autonomy/OllamaProposalSource.cs:129-130` | `HttpClient.PostAsJsonAsync` on an injected client | `OllamaProposalOptions.BaseUrl` (default `http://localhost:11434`) | The objective, certification-gate feedback, and the proposer's previous source code | `[Experimental]`. Not in any product DI; only `spikes/autonomy-first-flight/FirstFlight/SweepMode.cs:141` builds it. | None | Explicit (`ProposeAsync`) |
| EG-MDL-12 | `src/Ashlar.Infrastructure/Trust/CloudAvailabilityResolver.cs:103`; client at `:97` | `HttpClient.GetAsync` | `https://api.openai.com/v1/models` | None. The GET is unauthenticated, but it reveals that this host is probing. | Runs only with `ASHLAR_AIRGAP_PROBE=1` and no `ASHLAR_AIRGAP` or config key. Registered with trust services (F, S, SW). `ICloudAvailabilityResolver` has no consumer. | n/a | Handler(raw) for the fallback client. The client injectable through the constructor is `Exempt:TestSeam`. |
| EG-MDL-13 | `src/Ashlar.Infrastructure/ModelArtifacts/OllamaRemoteLibraryModelArtifactCatalogSource.cs:52` | Named factory client (`ModelArtifactCatalogServiceCollectionExtensions.cs:43-49`) | `https://ollama.com` | None (metadata GET) | `Enabled` defaults to true (`OllamaRemoteLibraryCatalogOptions.cs:13`). NCR module, AG included. Dormant: `ListInstallableAsync` has no caller. | n/a | Handler(factory) |
| EG-MDL-14 | `OllamaTagsModelArtifactCatalogSource.cs:39`, `:52`; `DockerOllamaModelArtifactCatalogSource.cs:127`; `application/src/Ashlar.API/Endpoints/IdeEndpoints.cs:193`, with a fallback client at `:192` | Factory clients; the IDE endpoint falls back to `new HttpClient()` | Loopback by default, configurable | None (`GET api/tags`) | NCR module. The IDE endpoint is always mapped in the API. | n/a | Handler(factory); the IDE fallback is Handler(raw) |

Checked and found not to be egress:
- LLamaSharp (`local:onnx`, `LocalModelProvider`, `LlamaSharpChatClient`).
- `TokenHashEmbeddingGenerator` and BackgroundAgents' `TokenEmbeddingGenerator`.
- ffmpeg (`ProviderFactory.cs:704-716`).

No remote embedding generator and no MCP sampling call exist.

## 2. Web search

| ID | Where | Leaves via | Destination | Payload | Gates today | Label at the site | Route (3b) |
|---|---|---|---|---|---|---|---|
| EG-WEB-01 | `src/Ashlar.BackgroundAgents/WebSearch/BingWebSearchProvider.cs:56`, reached from `WebSearchTool.cs:75` | `HttpClient.GetAsync` on a client the caller supplies | `https://api.bing.microsoft.com/v7.0/search`, overridable | The query in the URL, plus `Ocp-Apim-Subscription-Key`. On failure the raw query is logged (`:63`). | The kernel registers only `MockWebSearchProvider` (`Phases.cs:342`); Bing is SDK surface. Once wired: `DataExfiltrationPolicy` refuses the web-search tool ids when `BlockWebSearch` or `RequireLocalOnly` is set (`DataExfiltrationPolicy.cs:133`, `:145-151`); an optional `ISensitiveContentFilter` applies; the domain lists filter results only. No profile check. | None on the query. The agent's clearance can be derived from the snapshot `agentId`. | Explicit, in `SearchAsync` before `GetAsync` |

## 3. Agent transports and protocol clients

| ID | Where | Leaves via | Destination | Payload | Gates today | Label at the site | Route (3b) |
|---|---|---|---|---|---|---|---|
| EG-XPT-01 | `src/Ashlar.Transport.A2A/A2AAgentTransport.cs:95`; `A2AClient` at `:81` over a per-authority `new HttpClient()` at `:168` | A2A SDK `SendMessageAsync` (JSON-RPC `message/send` over HTTP POST). Retries at `:107-116`. | The `a2a+` `TargetEndpoint` with the prefix stripped (`A2AEndpointScheme.cs:22-33`), taken from the agent spec (`Orchestrator.cs:363`) | `Payload` or `DependencyOutputs` as a DataPart and `payload["message"]` as text. Metadata: correlation id, barrier level and source, domain, goals, command chain, `ollamaModel` (`A2AInvocationMapper.cs:32-79`; `Orchestrator.cs:364-376`). API key from env. | Needs `Ashlar:A2A:Transport:Enabled`. The validator refuses AG and SW (`ValidateA2ATransportOptions.cs:27`). Reachable only through `RoutingAgentTransport`, which is registered for F and S (`Phases.cs:168-175`). Only Ashlar.API composes it (`Program.cs:157`). | Ambient `BarrierContext.Level`, which is a caller-clearance name. No data label. | Handler(raw) |
| EG-XPT-02 | `A2AAgentTransport.cs:148` (`CheckEndpointAsync`; resolver at `:147`) | `A2ACardResolver.GetAgentCardAsync`, GET `/.well-known/agent-card.json` | As EG-XPT-01 | None (API-key header only) | As EG-XPT-01. No caller. | n/a | Handler(raw), same client |
| EG-XPT-03 | `src/Ashlar.Transport.Grpc/GrpcAgentTransport.cs:62`; channel at `DefaultGrpcChannelFactory.cs:66` over `new HttpClientHandler()` at `:74` | gRPC unary `AgentTransportService/Invoke` over HTTP/2 | Any `TargetEndpoint` without the `a2a+` prefix (the fallback at `SchemeDispatchingAgentTransport.cs:55`) | `InvokeRequest` payload and metadata maps, plus barrier and correlation headers (`:178-242`) | The kernel registers it for F and S only. A host that calls the public `AddAshlarRuntimeTransport<,>` bypasses the profile. | Barrier level only | Handler(raw): `BuildHandler` returns `EgressHttp.Wrap(handler, …)` |
| EG-XPT-04 | `GrpcAgentTransport.cs:150`, driven by `src/Ashlar.Runtime/Routing/EndpointHealthMonitor.cs:81` | gRPC `CheckHealth` from a BackgroundService | Every registered endpoint | Empty | The monitor is registered unconditionally (`RuntimeServiceCollectionExtensions.cs:95`) and idles when no gRPC transport exists. | n/a | Handler(raw), same channel |
| EG-XPT-05 | `src/Ashlar.Transport.Grpc.Server/AgentTransportServiceImpl.cs:173-176` | An inbound `Invoke` is re-sent through `IAgentTransport.SendAsync` | Any endpoint string the remote caller supplies; no allowlist | The caller's payload, plus this node's freshly resolved barrier | Barrier validation only. No profile check. The host binds `127.0.0.1:5001` by default. | The inbound barrier context | Handler(raw), at the wire through EG-XPT-01 or EG-XPT-03 |
| EG-XPT-06 | `src/Ashlar.Mcp.Client/McpClientConnectionManager.cs:167-168`; transport at `:325-326` | MCP SDK `CallToolAsync` over `HttpClientTransport` (streamable HTTP/SSE). Today the SDK builds its own client. | `AshlarMcpClientOptions.Servers[].Url` (http/https only) | Tool arguments written by the model, which can carry anything in the transcript. API key from env. | `Enabled=false` by default. The validator refuses AG and SW (`ValidateAshlarMcpClientOptions.cs:30`). Tool allowlist and schema pinning apply. `DataExfiltrationPolicy` has no class for `mcp:` ids. Only Ashlar.API composes it (`Program.cs:155`). | None | Handler(raw): the transport is given an `EgressHttp` client |
| EG-XPT-07 | `McpClientConnectionManager.cs:326` (initialize), `:263` (list), `:194` (drift re-list) | MCP initialize and `tools/list` from a hosted service | As EG-XPT-06 | Client info, API-key header | As EG-XPT-06 | n/a | Handler(raw), same client |

## 4. Remote execution

| ID | Where | Leaves via | Destination | Payload | Gates today | Label at the site | Route (3b) |
|---|---|---|---|---|---|---|---|
| EG-EXE-01 | `src/Ashlar.Infrastructure/Execution/RemoteBrick.cs:90` | Default factory client, with no auth header (`AshlarFederatedBrickMeshServiceCollectionExtensions.cs:79`); POST `{host}/api/bricks/{id}/execute` | `entry.HostBaseUrl` as the remote catalog returns it, else the catalog base (`CompositeBrickRegistry.cs:45`, `:55`, `:93`). A catalog can steer execution to any host. | `BrickInput` plus `ExecutionContextDto` {AgentId, BehaviorId, IsAirGapped, AuditMode, Provider, Variables} (`:67-85`) | Adaptation module: F, S, **AG, SW** (`Phases.cs:215-224`). Active when `BrickHost:RemoteCatalogBaseUrls` is non-empty. `IsAirGapped` is forwarded, not enforced. `RemoteBrick` advertises Deterministic (`:42`), so the air-gap forcing in `BehaviorExecutor.cs:375-380` still selects it. | None | Handler(factory) |
| EG-EXE-02 | `src/Ashlar.Infrastructure/Execution/HttpRemoteBrickCatalog.cs:47`, `:76`, `:196` | Factory client with an optional API-key header (`:70-76` of the extension). Called synchronously from `GetBrick`/`GetAllBricks`. | `BrickHost:RemoteCatalogBaseUrls` | Brick id and the API key | As EG-EXE-01 | n/a | Handler(factory) |
| EG-EXE-03 | `src/Ashlar.Infrastructure/Execution/Routing/AshlarPeerBrickExecutor.cs:117`; client at `:105` | Default factory client, POST `/api/bricks/{peerBrickId}/execute` | Peers from `FileBasedInstanceDiscovery` (`ASHLAR_MESH_INSTANCES_PATH`) | Prompt, model id, output format, and context: agent, behavior, auditMode, isAirGapped, provider, variables (`:412-448`) | NCR module: F, S, AG, SW. Needs `EnablePeerNetworkRouting`. `PeerTrustPolicyResolver` (`:214`) applies. | The peer's effective TrustTier (trust in the destination) | Handler(factory) |
| EG-EXE-04 | `src/Ashlar.Infrastructure/Testing/ExecutionPlatform/RemoteExecutionPlatform.cs:37`, `:60`, `:97` | Named client `AshlarExecution` (`Phases.cs:777-781`) | `ASHLAR_EXECUTION_REMOTE_URL` or `AshlarHostingOptions.ExecutionRemoteUrl` | Build args; run argv, environment variables (which may hold secrets), volume mounts | TestingAdapters module (F, S) and the URL being set | None | Handler(factory) |
| EG-EXE-05 | `src/Ashlar.Infrastructure/MeshLab/MeshLabWorkerExecutorClient.cs:134`, `:161`, `:177`, `:219`; commercial copy at `commercial/src/…/MeshLab/MeshLabWorkerExecutorClient.cs:131-216` | Named client (`MeshLabServiceCollectionExtensions.cs:28`; `FleetServiceCollectionExtensions.cs:116`) | Director and peer URLs | Task status and brick execution inputs | Registered in every profile (`Phases.cs:854`). `Ashlar:MeshLab:WorkerExecutor:Enabled` defaults to false. | None | Handler(factory) |
| EG-EXE-06 | `src/Ashlar.Infrastructure/Execution/Sandbox/DockerSandboxedSessionRunner.cs:78`, `:134`, `:321`, `:331`, `:399`, `:410` | docker CLI, through `ProcessCommandRunner.cs:25` and `TimedProcess.cs:60` | The inherited `DOCKER_HOST` or docker context, which may be remote (`SessionFileTransfer.cs:8-9`) | LLM-generated source and built assemblies, sent as base64 argv chunks. The container runs `--network=none`, attested. | No profile gate, by design (`ValidateAshlarAutonomyOptions.cs:12-17`). `AddAshlarAutonomy` is called by no shipped host. | None | Explicit, through EG-PROC-03 |
| EG-EXE-07 | `src/Ashlar.Infrastructure/Execution/Sandbox/DockerSandboxedCommandRunner.cs:41`, `:52` | docker CLI through the same funnel | As EG-EXE-06 | `SandboxSpec` command and mounts | Registered in every profile (`Phases.cs:519`). No consumer. | None | Explicit, through EG-PROC-03 |
| EG-EXE-08 | `src/Ashlar.Infrastructure/Scaling/KubernetesWorkloadScaler.cs:36`, `:62`, `:117`; `ComposeWorkloadScaler.cs:35`, `:64`, `:114` | kubectl or docker compose, through the same funnel | The `KUBECONFIG` API server, or the docker daemon | Namespace, deployment and service names; replica counts | The provider defaults to `null`. Registered in every profile (`Phases.cs:800-801`). | None | Explicit, through EG-PROC-03 |

## 5. HTTP integrations and tools

| ID | Where | Leaves via | Destination | Payload | Gates today | Label at the site | Route (3b) |
|---|---|---|---|---|---|---|---|
| EG-HTTP-01 | `src/Ashlar.Infrastructure/Workflows/HttpWorkflowWebhookClient.cs:44`, called from `WorkflowExecutor.cs:724` | Default factory client, `PostAsJsonAsync` | `OutputNode.WebhookUrl`: any URL, no allowlist | The first upstream node's output (`WorkflowExecutor.cs:694`): brick or agent output, file contents, DB results | WorkflowIntegrations module (F, S, SW). No first-party host resolves `WorkflowExecutor`. The context hard-codes `IsAirGapped=false` (`WorkflowExecutionContext.cs:28-35`). | None | Handler(factory) |
| EG-HTTP-02 | `HttpWorkflowWebhookClient.cs:32`, called from `WorkflowExecutor.cs:183` | Default factory client, `GetAsync` | `InputNode.WebhookUrl` | The URL only, which can itself encode data | As EG-HTTP-01 | None | Handler(factory) |
| EG-HTTP-03 | `src/Ashlar.Ingress.AwsSns/SnsRsaSignatureVerifier.cs:54`; `application/src/Ashlar.API/Middleware/Ingress/AwsSnsSmsWebhook.cs:123` | Named client `ashlar-sns-signing` (`application/src/Ashlar.API/Program.cs:169`; `commercial/src/Ashlar.Commercial.Fleet.Host/Program.cs:78`) | https URLs from the inbound SNS message, restricted to `*.amazonaws.com(.cn)` | GET only. `SubscribeURL` carries the subscription token. | `EnableAwsSnsSmsWebhook` and `AwsSnsAutoConfirmSubscription` both default to false | n/a | Handler(factory) |
| EG-HTTP-04 | `src/Ashlar.Client/AshlarClient.cs:30-102` | Typed client (`src/Ashlar.Client/ServiceCollectionExtensions.cs:20`) | The Ashlar API base address the consumer configures | Agent, orchestration, validation and knowledge requests | None; the consumer configures it | None | `Exempt:ConsumerSdk`. From 3b, covered by the handler when the consumer also calls `AddAshlar` (3a: nothing calls `AddAshlarEgressGuard`). |
| EG-HTTP-05 | `application/src/Ashlar.CLI/Commands/WorkflowCommand.cs:541`; client at `:534` | `new HttpClient`, `PostAsJsonAsync api/orchestrate` | Discovered mesh peers (`--include-mesh-peers`) | The scenario prompt, `RuntimeSpecJson`, provider | CLI flag only | None | Handler(raw) |
| EG-HTTP-06 | `application/src/Ashlar.CLI/Commands/MeshCommand.cs:374`; client at `:371` | `new HttpClient`, `GetAsync <url>/health` | The operator's `--url` | None | Operator command | n/a | Handler(raw) |
| EG-HTTP-07 | `commercial/src/Ashlar.Commercial.MeshDirector/MeshDirectorCommand.cs:236`, `:342`, `:401`; client at `:414` | `new HttpClient`, `SendAsync` | `--base-url` or `ASHLAR_MESH_DIRECTOR_BASE_URL` | Fleet register, admit, revoke and list requests, with API-key and mesh-token headers | The operator's credentials | n/a | Handler(raw) |

## 6. Mesh sharing and federation

| ID | Where | Leaves via | Destination | Payload | Gates today | Label at the site | Route (3b) |
|---|---|---|---|---|---|---|---|
| EG-MESH-01 | `src/Ashlar.Manifest/Packaging/MeshStore.cs:62-63`. Callers: `application/src/Ashlar.CLI/Commands/PkgCommand.cs:384` (publish) and `:436`/`:452` (share); `src/Ashlar.BackgroundAgents.HostRunners/SelfExtendAdmissionBridge.cs:285-286` (auto-share) | File write and atomic rename. A directory sync outside the process is the transport. | `--store`, else `$ASHLAR_MESH_DIR/published`, else `~/.ashlar/mesh/published` | A sealed `.ashpkg`: the gate record with its diff and course detail, the signer and sealer public keys, and the full source of every packaged file | `TryOpen` checks integrity only. Auto-share needs `ASHLAR_MESH_AUTOSHARE=1` and a signed record (`:246`, `:254`), and also runs inside Ashlar.API (`Program.cs:218`). No profile or label check. | None. `GateRecord`, `ExtensionProposal` and `PackageFile` have no label field. | Explicit, at the three callers, before `Pack` or `Publish`. `Ashlar.Manifest` references no Ashlar project. |
| EG-MESH-02 | `PkgCommand.cs:91-92` (`Pack`, then `File.WriteAllTextAsync` to `--out`) | Local file write to any path | `--out`: removable media, a synced folder | As EG-MESH-01 | Needs `ashlar.yaml`, an operator key, and applied forge rows that match the signed claims (`:66-154`) | None | Explicit, before `Pack` |
| EG-MESH-03 | `application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshServeService.cs:228` (`ListenAnyIP`); hello `:254-266`, index `:268`, pkg `:290-314` (`Results.Stream` `:313`) | Kestrel on all interfaces. Plaintext unless `ASHLAR_MESH_SERVE_TLS_CERT` and `_KEY` are set. | Any client that reaches the port. mTLS is optional, and the client certificate is never read. | Package bytes up to 4 MiB. Hello and index return node name, fingerprint and the package list. | Only `ASHLAR_MESH_SERVE_PORT` (`BackgroundAgentDaemonCommand.cs:415-420`), so it serves under AG and SW too. Seals are not re-verified at serve time. | Only the optional mTLS client certificate, which is unread | Explicit, in the pkg handler before `Results.Stream`; the destination is the remote IP. Hello and index are listed as metadata responses and not routed in PR 3. |
| EG-MESH-04 | `application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshAutoPullService.cs:266`, `:307`; static client `:52-71` (`SocketsHttpHandler` plus mTLS) | `HttpClient.GetAsync` | `ASHLAR_MESH_PEERS`, LAN-discovered peers and tailnet peers (at most 16 per tick) | Outbound: request line, package file names and the client certificate. Packages flow inbound, which is the read side. | Env opt-in (`BackgroundAgentDaemonCommand.cs:331-346`) | n/a | Handler(raw): the handler is passed to `EgressHttp.CreateClient` |
| EG-MESH-05 | `application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshDiscoveryService.cs:259`; sender `:245`, listener `:190` | `UdpClient.SendAsync`, multicast every 15 s | `239.7.42.1:7421` | Node name, operator key fingerprint, serve port (`MeshBeacon.Encode` `:52-53`) | `ASHLAR_MESH_DISCOVERY=1` (`BackgroundAgentDaemonCommand.cs:387-392`) | n/a | Explicit in `AnnounceLoopAsync` before the send; the listener is `Exempt:Inbound` |
| EG-MESH-06 | `application/src/Ashlar.CLI/Commands/BackgroundAgent/TailnetPeerSource.cs:164-176` | `Process.Start` of `tailscale status --json` (local IPC) | Local `tailscaled` | Nothing leaves. Its peer list feeds EG-MESH-04. | `ASHLAR_MESH_TAILNET=1` | n/a | `Exempt:LocalOnly` |
| EG-MESH-07 | `src/Ashlar.Infrastructure/Adaptation/FileBasedSharedAdaptationStore.cs:66`, `:81` (`BroadcastAsync` `:50`); caller `application/src/Ashlar.CLI/Commands/ImproveCommand.cs:401` | File writes into a shared directory that peers sync | `~/.ashlar/shared-adaptations/<id>/` (improve ignores `ASHLAR_SHARED_ADAPTATIONS_PATH`) | The `AdaptationRecord`, the source peer id, and the full bytes of the promoted source file | None. It fires on every successful promotion, unsigned. | None | Explicit (`BroadcastAsync`) |
| EG-MESH-08 | `src/Ashlar.Infrastructure/Adaptation/SneakernetTransport.cs:50` (`ExportAsync` `:25`); caller `MeshCommand.cs:199` | Local file write for physical transfer | The `--to` path (`.nxpkg`) | Every shared adaptation with base64 file bytes, unsigned | None | None | Explicit (`ExportAsync`) |

## 7. Export bundles

| ID | Where | Leaves via | Destination | Payload | Gates today | Label at the site | Route (3b) |
|---|---|---|---|---|---|---|---|
| EG-FILE-01 | `application/src/Ashlar.CLI/Commands/ExportCommand.cs:158`, calling `NativeBundle.StageApp` (`NativeBundle.cs:83-121`, `File.Copy` `:210`); zip at `ExportCommand.cs:209` | Local copy and zip | `<out>/<name>-<rid>` directory or `.zip` | `ashlar.yaml`, the policy, and `.ashlar/**`, which includes gate records with diffs and the signed ledger. Also `src/**`, run scripts and `bundle.json`. Keys, `forge/`, `*.key`, `.lock` and `*.tmp` are excluded. | The project must verify (`:143-148`). No profile or label check. | None | Explicit (`StageApp`). The `dotnet publish` restore is EG-PROC-04. |
| EG-FILE-02 | `ExportCommand.cs:76`, calling `CloudBundle.Stage` (`CloudBundle.cs:33-120`), which calls `StageApp` `:37` | Local files. The upload happens out of process in the generated `deploy-aws.sh` (`docker push`, `:171`) and `deploy-azure.sh` (`az acr build`, `:259`). | ECR/ECS or ACR/ACI. Base image `ghcr.io/ianfrelinger/nexo-cli:latest` (`:28`). | The StageApp tree plus a Dockerfile and entrypoint | The project must verify (`:62-67`) | None | Explicit (`StageApp`, site EG-FILE-02). The push is `Unscanned:OutOfProcess`. |

## 8. Telemetry

| ID | Where | Leaves via | Destination | Payload | Gates today | Label at the site | Route (3b) |
|---|---|---|---|---|---|---|---|
| EG-TEL-01 | `application/src/Ashlar.API/Program.cs:244`, `:251` (`AddOtlpExporter`) | OpenTelemetry OTLP exporter, batch export owned by the SDK | `OTEL_EXPORTER_OTLP_ENDPOINT` | Ashlar metrics, ASP.NET Core and HttpClient metrics. Traces include a client span for every outbound HttpClient call, which leaks the map of all other egress. | Only the variable being set (`:237-239`). No profile check. | n/a | Explicit, decided once at registration |
| EG-TEL-02 | `src/Ashlar.Hosting/Sdk/Extensions/OpenTelemetryServiceCollectionExtensions.cs:20-29` (`AddAshlarOpenTelemetry`) | Whatever exporter an SDK host attaches | Configured by the host | As EG-TEL-01 | None | n/a | `Unscanned:HostConfigured` |

## 9. Child processes

| ID | Where | Leaves via | Destination | Payload | Gates today | Label at the site | Route (3b) |
|---|---|---|---|---|---|---|---|
| EG-PROC-01 | `src/Ashlar.Tools.Dev/DotnetRunner.cs:17-25`. Used by `dotnet.build`, `dotnet.test` and `forge.build` (`RepoFsToolboxFactory.cs:99-109`, `:124`); `DotnetRunTool` (`:42`, arbitrary args) is unregistered. | `Process.Start("dotnet", …)` with host network | NuGet feeds (api.nuget.org by default) through the implicit restore; anything the repo's MSBuild targets dial | Package ids and versions. `DotnetRunTool` can carry anything. | `BuildTestBudget`, `PathAllowlist`, governance floor. `dotnet.build` is on no `DataExfiltrationPolicy` deny-list. No profile check. | Snapshot `agentId` only | Explicit (`RunAsync`). The destination is `nuget-feeds` when the restore is implicit, otherwise `host:dotnet`. |
| EG-PROC-02 | `src/Ashlar.Infrastructure/Validation/Adapters/ValidationServiceAdapter.cs:525` (`dotnet build`), `:759` (`dotnet test` without `--no-build`) | Direct `Process.Start` | NuGet feeds | Package metadata | None | None | Explicit |
| EG-PROC-03 | `src/Ashlar.Infrastructure/Scaling/ProcessCommandRunner.cs:16-25` | The single funnel for every docker, kubectl and compose argv (EG-EXE-06/07/08) | `DOCKER_HOST` or context (unset, unix or npipe means the host); the kube API server | See EG-EXE-06/07/08 | See those rows | None | Explicit (`RunAsync`) |
| EG-PROC-04 | `ReleaseCommand.cs:58-61`, `:170` (`gh workflow run`); `WorkflowCommand.cs:336-348` (`ollama pull`); `BootstrapRuntime.cs:367` (`curl … ollama.com/install.sh \| sh`, Homebrew, apt); `DoctorRemediation.cs:156`; `ExportCommand.cs:245-268` (`dotnet publish`); `TestPortableCommand.cs:163`/`:196`; `DogfoodTestCommand.cs:100`; `CiCommand.cs:278`; `TestMultiEnvCommand.cs:369`; `SelfExtendCommand.cs:931`; `Runtime/ExecuteHandler.cs:200`/`:302`; `MultiPlatformTestCommand.cs:419-421` (`dotnet test`); the `TimedProcess.cs:60`, `:111` funnel | `Process.Start` or `TimedProcess` | GitHub API, the Ollama registry, apt, Homebrew, NuGet | Workflow name and ref, model names, package metadata | An operator runs the verb. No profile check. | n/a | `Exempt:Operator` |
| EG-PROC-05 | ffmpeg `ProviderFactory.cs:704`/`:716`; `CoreVersionManager.cs:45-51` (`git rev-parse`); `DockerDesktopLifecycleAdapter`; `DotNetRegressionTestRunner.cs:55` and `DotNetInstanceSpawner.cs:58` (`--no-build`); `OperatorDashboardBackgroundAgentCommand.cs:74` (opens a URL in the browser); `DockerSandboxSessionReaper` | Local processes | Local | Local | n/a | n/a | `Exempt:LocalOnly`. The raw-string templates at `MockScaffoldingResponder.Templates.cs:734`/`:1613` are text, not launches. |

## 10. Data stores and local daemons

| ID | Where | Leaves via | Destination | Payload | Gates today | Route (3b) |
|---|---|---|---|---|---|---|
| EG-STORE-01 | `src/Ashlar.Infrastructure/Persistence/PostgresDatabaseProvisioner.cs:92`, `:115`, `:175`, `:209`, `:311`, `:325` | `new NpgsqlConnection` | The `AdminConnectionString` host, or the ephemeral container | DDL, credentials, caller SQL batches | Persistence module (F, S, E, AG, SW). No runtime consumer. | `Exempt:DataStore` |
| EG-STORE-02 | `src/Ashlar.Ingress.DynamoDb/SmsIngressDynamoDbServiceCollectionExtensions.cs:12`; `DynamoDbSmsIngressApprovalStore.cs:47` (`PutItemAsync`) | AWS SDK DynamoDB | The configured table | Sender phone number, approval token, timestamp | Configuration only | `Exempt:DataStore` |
| EG-STORE-03 | Docker.DotNet: `DockerExecutionPlatform.cs:43` (its containers run on the default bridge with egress and read-write bind mounts, `:131-150`); `DockerService.cs:36`; `OllamaEphemeralLifecycle.cs:27` and `PostgresEphemeralLifecycle.cs:26` (ports published on every interface); `DockerOllamaModelArtifactCatalogSource.cs:49`; `DockerCommand.cs:538`, `:618`, `:679` | Local daemon socket (hard-coded; `DOCKER_HOST` ignored) | The local daemon. Image pulls and container traffic leave through the daemon. | Image names, test commands, environment | TestingAdapters (F, S); `ASHLAR_EPHEMERAL*`; CLI verbs | `Exempt:LocalDaemon` |

## 11. Inbound servers (responses leave; these are read-side decisions)

All rows in this table are `Unscanned:Inbound`. Mediating them is CanRead against the caller's clearance, at the server seam, in a later PR.

| ID | Where | What leaves | Gates today |
|---|---|---|---|
| EG-SRV-01 | MCP over HTTP: `src/Ashlar.Mcp.Server/AshlarMcpToolBridge.cs:122`; `application/src/Ashlar.API/Program.cs:154`, `:387` | Tool output, for example `repo.fs.read` file contents | Refuses AG only. SW is allowed (`ValidateAshlarMcpServerOptions.cs:31`), which contradicts `AshlarDeploymentProfileEnvironment.cs:50-55`. |
| EG-SRV-02 | MCP over stdio: `src/Ashlar.Mcp.Server.Host/Program.cs:47` | `RepoFsRead`/`RepoFsList` output to the IDE process | Refuses AG only |
| EG-SRV-03 | A2A server: `src/Ashlar.Transport.A2A.Server/AshlarA2AAgentHandler.cs:75` | Local agent output, agent cards | Refuses AG and SW (`ValidateAshlarA2AServerOptions.cs:25`); exposure allowlist |
| EG-SRV-04 | gRPC server: `AgentTransportServiceImpl.cs:177-190` | Agent output map | Barrier validation only |
| EG-SRV-05 | `commercial/src/Ashlar.Commercial.Fleet.Api/CommercialFleetEndpoints.cs:134`, `:455-465` (knowledge export) | Up to 10,000 adaptation records and observed patterns | API key |
| EG-SRV-06 | `CommercialFleetEndpoints.cs:119`, `:482-500` (task result download) | Whatever file `ResultHandle` names | API key. No path containment is visible. |

## 12. Dormant, contract-only and port-only

| ID | Where | Notes | Route (3b) |
|---|---|---|---|
| EG-LAT-01 | `commercial/src/Ashlar.Commercial.Fleet.Infrastructure/Networking/HttpKnowledgeSyncService.cs:60`; `HttpNetworkBus.cs:101` | Push knowledge chunks and agent-bus events. No production DI registers them. | Scanned like any other site (factory or raw) |
| EG-LAT-02 | `src/Ashlar.Infrastructure/Mesh/FileBasedLocalTransport.cs:43` | Writes to a peer inbox in a shared directory. No production caller. | `Unscanned:Dormant` |
| EG-LAT-03 | `src/Ashlar.Contracts/Distributed/ITaskScheduler.cs:15`; `INativeExecutionHost.cs:22` | Ports with no implementation in this repository | `Unscanned:Contract` |
| EG-LAT-04 | `commercial/…/MeshPeerKnowledgePullBackgroundService.cs:89` | Outbound GET, gated by `MeshPeerKnowledgeSync:Enabled` and `PeerBaseUrls` | Scanned like any other site |

## Searched, nothing found

- Update or version checks.
- Outbound notifications (SMTP, Slack, Teams, Twilio, SNS publish).
- Runtime NuGet API clients.
- A generic `http_fetch` agent tool.
- Remote RAG fetchers. Stores are in-memory, SQLite or in-process.
- Azure Blob, S3, Lambda, Redis and Mongo. The packages are pinned, but no csproj references them.
- `Dns` or `Ping` probes.
- A runtime `git push`.
- An in-process container push, or an AWS or Azure SDK deploy.
- HTTP calls in `extensions/ashlar-vscode`.

## What this inventory says about the profiles (input to PR 4)

- **AirGapped does not stop HTTP egress today.** It still registers RunPod (EG-MDL-09), the peer executor (EG-EXE-03), federated remote bricks (EG-EXE-01/02) and the ollama.com catalog (EG-MDL-13). The bare `ProviderFactory` cloud branches (EG-MDL-03..06) are also live there. The mesh serve, pull and beacon (EG-MESH-03/04/05) read only environment variables.
- **SecureWorkstation also registers the workflow webhook client** (EG-HTTP-01).
- **"Air-gapped" means three different things:**
  - `ASHLAR_DEPLOYMENT_PROFILE`;
  - `ASHLAR_AIRGAP`, which only `CloudAvailabilityResolver` reads, and which has no consumer;
  - `IExecutionContext.IsAirGapped`, which callers set. It is never derived from the profile.
- **No call site carries a `SecurityLabel`.** The only destination tiers named in code are the MEAI target-key prefixes (`local:`, `peer:`, `cloud:`).

## Adding an outbound path

1. **HTTP.** Get the client from `IHttpClientFactory`, in a member that also calls `AddAshlarEgressGuard`. Otherwise build it with `EgressHttp.CreateClient(family, "EG-…")` or `EgressHttp.Wrap(handler, family, "EG-…")`.
2. **Anything else.** Call `guard.Evaluate(new EgressRequest(family, "EG-…", destination))` before the primitive, in the same member.
3. **Record it.** Add a row to this file and the pins to `ci/egress-inventory.tsv`. `EgressGuardConventionTests` fails until both exist.
