# Scout S1: deployment profiles, and what each one lets out

SPEC-007 PR 4 design pass. This is a static reading of `$SP/pr4-base` at `8ec674d2a`. Nothing was built or run. Every
`file:line` points at that tree. Where a claim needs a build or a test to settle it, the text says so.

The lens: the profiles, what each one registers, what reaches the network under each, and what already refuses
egress by profile or by policy.

---

## Part 1. Facts

### 1.1 The profiles, and how a host selects one

- **There are six profiles**: `Full=0`, `Server=1`, `Edge=2`, `AirGapped=3`, `System=4` and `SecureWorkstation=5`
  (`src/Ashlar.Hosting/Sdk/Options/AshlarDeploymentProfile.cs:13-65`).
- **How the profile is chosen** (`src/Ashlar.Hosting/AshlarServiceCollectionExtensions.Deployment.cs:13-34`):
  1. `AshlarHostingOptions.DeploymentProfile`, or `AddAshlarProfile(profile)`
     (`AshlarServiceCollectionExtensions.cs:77-86`).
  2. Otherwise the `ASHLAR_DEPLOYMENT_PROFILE` environment variable. Case, `-` and `_` fold.
  3. Otherwise `Full`. An unrecognised value throws at startup (`…Deployment.cs:31-33`).
- **Accepted spellings** come from one parser, `AshlarDeploymentProfileEnvironment.TryParseKnown`
  (`src/Ashlar.Abstractions/AshlarDeploymentProfileEnvironment.cs:62-84`). `workstation` is an alias for
  SecureWorkstation, and `core` is an alias for System.
- **`AddAshlar` records the profile it resolved, process-wide**, with `NoteResolved`
  (`AshlarServiceCollectionExtensions.cs:110-119` → `AshlarDeploymentProfileEnvironment.cs:10-18`).
  - `Effective(raw)` prefers that recorded value over the variable (`:22-23`).
  - The protocol validators and the egress guard read the profile through `Effective` (`EgressGuard.cs:126-139`).
  - The value is static and last-wins. It is cleared only by `ClearResolved` (`:20`), which only tests call.
- **No first-party host hard-codes a profile.** Every host calls a bare `AddAshlar`, so the profile comes from the
  environment variable:
  - Ashlar.API: `application/src/Ashlar.API/Program.cs:224`
  - the CLI: `application/src/Ashlar.CLI/Program.cs:100`
  - the daemon: `BackgroundAgentDaemonCommand.cs:216`
  - the MCP stdio host: `src/Ashlar.Mcp.Server.Host/Program.cs:39`
  - the gRPC host: `src/Ashlar.Transport.Grpc.Server.Host/Program.cs:27`
  - Fleet.Host: `commercial/src/Ashlar.Commercial.Fleet.Host/Program.cs:121`
  - the consumer template: `consumer-template/host/Program.cs:39`
- **The kernel's configuration is environment variables only.** `AddAshlar` builds its own `IConfiguration` from
  `AddEnvironmentVariables()` (`AshlarServiceCollectionExtensions.cs:128-130`) and passes it to every phase. Every
  opt-in switch below is therefore an `Ashlar__…` environment variable. `appsettings.json` does not reach them
  (`docs/Configuration.md:9`).

### 1.2 What each profile registers

The registrations are `ModuleSelection`, eleven flags (`…Deployment.cs:70-148`), consumed by the phases in
`src/Ashlar.Hosting/AshlarKernelRegistrar.Phases.cs`.

| Module (phase, line) | F | S | E | AG | SW | Sys |
|---|---|---|---|---|---|---|
| NodeCapabilityRuntime: NCR, RunPod routing, peer executor, model catalogs (P01 `:69-73`) | ✓ | ✓ | – | ✓ | ✓ | – |
| RuntimeTransport: gRPC and the A2A scheme composition (P05 `:169-176`) | ✓ | ✓ | – | – | – | – |
| Persistence and the Postgres provisioner (P06 `:187-191`) | ✓ | ✓ | ✓ | ✓ | ✓ | – |
| Adaptation and the **federated brick mesh** (P07 `:216-225`) | ✓ | ✓ | – | ✓ | ✓ | – |
| PipelineComposition (P10 `:275-278`) | ✓ | ✓ | ✓ | ✓ | ✓ | – |
| BackgroundAgents, RAG, mock web search (P11 `:290-307`, P12 `:341-344`) | ✓ | ✓ | – | – | ✓ | – |
| ObservationPipeline (P12 `:323-336`) | ✓ | ✓ | – | – | ✓ | – |
| TrustServices (P15 `:526-536`; sanitising also needs `ASHLAR_TRUST_ENABLED=1`, `:508-509`) | ✓ | ✓ | – | – | ✓ | – |
| WorkflowIntegrations: webhooks, DB, PDF (P16 `:652-660`) | ✓ | ✓ | – | – | ✓ | – |
| TestingAdapters: remote and docker execution (P19 `:772-800`) | ✓ | ✓ | – | – | – | – |

**Registered in every profile, whatever the flags say:**

| What | Where |
|---|---|
| The default factory client and `AddAshlarEgressGuard` | `AshlarServiceCollectionExtensions.cs:124-127` |
| MEAI chat: `local:ollama`, `local:onnx`, optional Bedrock | P13b `:410-442`, on unless `ASHLAR_USE_MEAI_PIPELINE=0` |
| `IProviderFactory` (OpenAI, Azure, Ollama, video) and `AdaptiveProviderFactory` when `ASHLAR_LOAD_PREFERENCE` is set | P15 `:507-608` |
| `DockerSandboxedCommandRunner` and `ProcessCommandRunner` | P15 `:519-520` |
| Ephemeral Ollama and Postgres lifecycles when `ASHLAR_EPHEMERAL*` is set | P14 `:460-469` |
| `IValidationService`, whose `dotnet build` restores packages | P18 `:744-752` |
| The workload scaler | P19 `:803-804` |
| The MeshLab worker executor | P20 `:857` |
| Orchestration | P05 `:168` |

**SecureWorkstation versus AirGapped.**
- SecureWorkstation (SW) is AirGapped plus: background agents, RAG, observation, trust and workflow integrations.
  Neither has runtime transport or testing adapters (`…Deployment.cs:110-121` and `:134-145`).
- The documents disagree about what SW is for:
  - The enum's own comment calls SW an "Air-gapped workstation / IDE daemon" (`AshlarDeploymentProfile.cs:58`).
  - `docs/architecture/product-split.md:52-54` says "Cloud providers remain opt-in via trust/provider configuration,
    not a profile kill-switch".
  - `docs/Configuration.md:28` says SW is "not a synonym for air-gapped".

### 1.3 The only profile checks at runtime today

1. `ForbidsRemoteProtocolEgress`, which is true for AG and SW (`AshlarDeploymentProfileEnvironment.cs:56-60`). Three
   validators use it, through `ValidateOnStart`, so a misconfigured host fails at boot:
   - the MCP client (`src/Ashlar.Mcp.Client/ValidateAshlarMcpClientOptions.cs:29-36`);
   - the A2A client transport (`src/Ashlar.Transport.A2A/ValidateA2ATransportOptions.cs:26-33`);
   - the A2A server (`src/Ashlar.Transport.A2A.Server/ValidateAshlarA2AServerOptions.cs:24-31`).
2. `IsAirGapped` alone, used by the MCP server validator (`src/Ashlar.Mcp.Server/ValidateAshlarMcpServerOptions.cs:29-36`).
3. The egress guard **reports** `ProfileEnforcesByDefault`, using the same predicate (`EgressGuard.cs:126-139`). It
   refuses nothing.

Nothing else reads the profile.
- `IExecutionContext.IsAirGapped` is set by callers. It is never derived from the profile:
  - `WorkflowExecutionContext.cs:32` hard-codes `false`;
  - `SanitizingProviderFactory.cs:51` hard-codes `false`;
  - the API takes it from the inbound wire DTO (`application/src/Ashlar.API/BrickCatalogWireMapper.cs:92`).
- `ASHLAR_AIRGAP` is read only by `CloudAvailabilityResolver.cs:45`, which has no consumer.

### 1.4 Defect 5 verified: AirGapped still registers network paths

Every path below is registered under AG, and each is callable once the stated condition holds. Each maps to its row
in `docs/EgressInventory.md`.

| Path | EG id | Registered under AG by | What makes it leave the host under AG | Status |
|---|---|---|---|---|
| **RunPod** | EG-MDL-09 | `AddRunPodCapabilityRouting`, P01 `:71` (`RunPodCapabilityRoutingServiceCollectionExtensions.cs:50-57`, `:77-82`) | **No configuration at all.** See below. | **Confirmed, and worse than the brief says: on by default** |
| Peer executor | EG-EXE-03 | Same extension, `:68-69` | Peers listed in `~/.ashlar/instances.json` or `ASHLAR_MESH_INSTANCES_PATH` (`FileBasedInstanceDiscovery.cs:26`, `:35`), and either `Ashlar__RunPod__EnablePeerNetworkRouting=true` or a `PeerNetworkOnly` requirement. A `PeerNetworkOnly` requirement goes to the peer executor even when peer routing is disabled (`NcrCapabilityRouter.cs:89-105`). | Confirmed |
| Federated remote bricks | EG-EXE-01, EG-EXE-02 | P07 `:224` (`AshlarFederatedBrickMeshServiceCollectionExtensions.cs:32-80`) | `BrickHost__RemoteCatalogBaseUrls__0` set. The catalog then picks the execute host (`CompositeBrickRegistry.cs:45`, `:93`; that is defect 7), and `IsAirGapped` is forwarded to the remote but not enforced (`RemoteBrick.cs:80`). | Confirmed |
| ollama.com catalog | EG-MDL-13 | `AddModelArtifactCatalog`, from `RegisterNodeCapabilityRuntime` (`AshlarServiceCollectionExtensions.NodeCapabilityRuntime.cs:47`; `ModelArtifactCatalogServiceCollectionExtensions.cs:50-61`) | `Enabled` defaults to true (`OllamaRemoteLibraryCatalogOptions.cs:13`), but `ListInstallableAsync` (`ModelArtifactCatalogService.cs:30-31`) has no caller anywhere in `src/`, `application/` or `commercial/`. | **Dormant**: registered, but nothing reaches it |
| MeshLab worker | EG-EXE-05 | P20 `:857`, unconditional | `Ashlar__MeshLab__WorkerExecutor__Enabled=true` (`MeshLabServiceCollectionExtensions.cs:30-34`), which then runs as a hosted service | Not in the brief's list; confirmed |
| Cloud LLMs through `ProviderFactory` | EG-MDL-03, -04, -05, -06 | P15 `:572-608`, unconditional | `OPENAI_API_KEY` or the three `AZURE_OPENAI_*` variables (`ProviderFactory.cs:216-221`), plus a caller that asks for `openai` or `azure`. AG has no trust module, so these prompts go out **unsanitised**. | Not in the brief's list; confirmed |
| Adaptive local-to-cloud fallback | EG-MDL-03, -04 | P15 `:587-593`, whenever `ASHLAR_LOAD_PREFERENCE` is set | An Ollama failure falls through to openai, then azure (`AdaptiveProviderFactory.cs:47-65`) | Defect 6, applies under AG |
| MEAI Ollama at a remote URL | EG-MDL-01 | P13b `:441` | `ASHLAR_OLLAMA_BASE_URL`, `Ashlar__Meai__OllamaBaseUrl` or `OLLAMA_BASE_URL` set to a host that is not this machine | Confirmed (by environment) |
| Video service | EG-MDL-08 | P15 | `VIDEO_SERVICE_URL` | Confirmed (by environment) |
| Bedrock | EG-MDL-02 | P13b, when `Ashlar__Meai__Bedrock__Enabled=true` (`MeaiPipelineServiceCollectionExtensions.cs:115-118`) | Registered, but PolicyGate denies it because the allow-list is dead (defect 4) | Registered; refused today |
| Workload scaler | EG-EXE-08 | P19 `:803-804` | `ASHLAR_WORKLOAD_SCALER=kubernetes` or `compose` (`WorkloadScalingServiceCollectionExtensions.cs:24-26`, `:55-59`) | Confirmed (by environment) |
| OTLP export | EG-TEL-01 | Ashlar.API, outside the profile | `OTEL_EXPORTER_OTLP_ENDPOINT` (`application/src/Ashlar.API/Program.cs:242-259`) | Profile-blind |
| Mesh serve, pull and beacon | EG-MESH-03, -04, -05 | The daemon, outside the profile | `ASHLAR_MESH_SERVE_PORT`, `ASHLAR_MESH_PEERS`, `ASHLAR_MESH_DISCOVERY=1` | Profile-blind |
| `dotnet build` restore | EG-PROC-02 | P18 | Normal use of validation | Profile-blind |
| Ollama and Postgres pulls through the docker daemon | EG-STORE-03 (exempt) | P14 `:460-469` | `ASHLAR_EPHEMERAL*=1` | A second hop, outside the process |
| MCP client and A2A | EG-XPT-06, -07, -01, -02 | Composed only by Ashlar.API (`Program.cs:157-159`) | Refused at boot under AG by the validators | Already enforced |
| gRPC transport and relay | EG-XPT-03, -04, -05 | P05, only when RuntimeTransport is on | AG has no `IAgentTransport`, so `AgentTransportServiceImpl` (`AgentTransportServiceImpl.cs:30-38`) cannot be built | Not reachable under AG |

**How RunPod is reached with no configuration:**
1. `RunPodBrick` and `CapabilityRoutingBrick` are added to the brick registry
   (`RunPodCapabilityRoutingServiceCollectionExtensions.cs:85-96`, consumed at
   `AdaptationServiceCollectionExtensions.cs:125-133`).
2. Both can be called through `POST /api/bricks/{brickId}/execute` (`application/src/Ashlar.API/Endpoints/AshlarEndpoints.cs:196`,
   `:820-836`), which sits behind `MeshSecurityMiddleware.cs:46-47`.
3. An input `overnight=true` (`CapabilityRoutingBrick.cs:195`) makes the router choose remote execution
   (`NcrCapabilityRouter.cs:132-134`). So does a VRAM shortfall or a deep queue (`:137-151`).
4. By default the remote target is RunPod (`:126-127`), because `EnablePeerNetworkRouting` defaults to false
   (`RunPodBrickConfig.cs:53`).
5. RunPod's base URL defaults to `https://api.runpod.io` (`src/Ashlar.Core.Domain/AshlarDefaults.cs:161`).
6. The request goes out even with no API key; only the auth header is skipped (`RunPodHttpClient.cs:203-205`).

**Nothing in this path reads the profile or `IsAirGapped`.** Grep finds no `IsAirGapped` in
`src/Ashlar.Infrastructure/Execution/Routing/` except the peer executor forwarding it as a property
(`AshlarPeerBrickExecutor.cs:441`).

### 1.5 Defects 3, 4, 6 and 8, and known gap 4

**Defect 3 is confirmed, as a scoping gap.**
- `ValidateAshlarMcpServerOptions.cs:31` refuses only AirGapped.
- The intent is narrower. `AshlarDeploymentProfileEnvironment.cs:50-55` says "Local MCP server (IDE stdio) stays
  allowed on SecureWorkstation". `product-split.md:64-66` says the same: allowed "for an IDE stdio tool surface".
- The validator cannot tell stdio from HTTP. Ashlar.API composes the HTTP transport (`Program.cs:156`,
  `.WithHttpTransport()`), so an HTTP MCP server (EG-SRV-01, which serves `repo.fs.read` output) boots under SW.
- This is inbound (`Unscanned:Inbound`). An enforcing egress guard would not see it.

**Defect 4 (the dead allow-list) is confirmed statically. A test is needed to settle it.**
- Phase 13b first calls `RegisterGovernanceDefaults(services)` with no options (`Phases.cs:416`; the brief says
  `:415`). That reaches `MeaiPipelineServiceCollectionExtensions.cs:177-178`, then `:185`, a `TryAddSingleton<IChatTargetAccessPolicy>`
  with `options: null`, so `AllowedCloudTargets` is empty.
- Later, `AddAshlarMeaiPipeline` (`Phases.cs:441`) calls `RegisterGovernanceDefaults(services, options)` (`:96`).
  The `TryAdd` at `:185` is now a no-op. The configured `AllowedCloudTargets`, and the Bedrock keys that
  `ApplyBedrockAllowListDefaults` adds (`:258-276`), never reach the policy.
- **Effect: it fails closed.** `DefaultChatTargetAccessPolicy.cs:32-41` denies every `cloud:` key, and
  `LocalFirstChatRouter` offers no cloud candidate because `IsAllowed` is false for all three Bedrock keys
  (`LocalFirstChatRouter.cs:32-33`, `:94-110`).
- **Fixing it opens Bedrock in every profile, AG included,** unless something profile-aware refuses it.
- To settle it: an `AddAshlar` test with `Ashlar__Meai__AllowedCloudTargets__0=cloud:bedrock:fast` that asserts the
  resolved policy's `AllowedCloudTargets` is empty.

**Defect 6 is confirmed.**
- `AdaptiveProviderFactory.ExecuteLLMAsync` ignores the `provider` it is given. It resolves one from the load policy
  (`AdaptiveProviderFactory.cs:40`), then tries `{resolved, "openai", "azure"}` when the resolved provider is local
  (`:47-49`).
- Each failure is logged at Warning and the next provider is tried (`:60-64`). Nothing checks the profile, the
  agent's policy or the guard.
- It is wired in any profile when `ASHLAR_LOAD_PREFERENCE` is set (`Phases.cs:510-511`, `:587-593`).
- "Silent" means there is no refusal and no policy check. There is a Warning log.

**Defect 8 is confirmed.**
- `RequireLocalOnly` and `BlockExternalLLMs` are read in only four places:
  - `DataExfiltrationPolicy.cs:127-152` (tool-id deny-lists);
  - `AgentPolicyNarrowingValidator.cs:45-66` (a child agent may not relax them);
  - the config loader (`BackgroundAgentConfigLoader.cs:221-232`);
  - the CLI's display code.
- The agent's `ModelProvider` (`BackgroundAgentConfig.cs:40`) flows to the model unchecked:
  `BackgroundAgentRegistry.cs:611-614` → `SelfExtendRunnerAdapter.cs:224-230` → `ToolCallingAgent.cs:347-348`,
  where it becomes the `ashlar.model.provider=` directive.
- `mcp:<server>:<tool>` ids are on none of the three deny-lists (`DataExfiltrationPolicy.cs:29-42`), so they reach
  `return true` with reason "OK" (`:154-155`).
- Also: `_sensitivityRegistry` is stored (`:76`) and never read, so `MaxAllowedLevel` is never enforced.

**Known gap 4 (no subject frames) is confirmed.**
- `EgressSubject.Enter` has 18 call sites, and all are in test files: `EgressGuardDecisionTests`,
  `EgressExplicitSiteTwinTests`, `EgressAgentSiteTwinTests` and `EgressCliSiteTwinTests`.
- No production code constructs a `HighWaterMark`.
- The only production reader is `EgressGuard.cs:89`, so every production decision has basis `no-subject` at
  `SystemHigh` (`EgressSubject.cs:47-53`).

### 1.6 What "enforce" would refuse today, given no subject frame

- Only Host-class destinations would pass:
  - loopback, `localhost` and `*.localhost`;
  - `unix` and `npipe` URIs;
  - `host:` names (`EgressDestinations.cs:131-151`, `:180-205`).
- **A private-LAN address is not Host.** `10.x` and `192.168.x` addresses are classified by family (`:62`, `:66-95`).
- `file:` exports and mesh publishes have an empty URI host, so they are classified by family as NetworkExport
  (`:78-91`) and refused.

The default destinations that would pass:

| Destination | EG id | Default |
|---|---|---|
| MEAI Ollama | EG-MDL-01 | `http://localhost:11434` |
| Legacy Ollama | EG-MDL-07 | `localhost` |
| NCR Ollama | EG-MDL-10 | `127.0.0.1` |
| `api/tags` listings | EG-MDL-14 | loopback |
| `dotnet test --no-build` | EG-PROC-02 | `host:dotnet` |
| docker with no `DOCKER_HOST` | EG-PROC-03 | `host:docker` |
| a mesh beacon that only listens | EG-MESH-05 | `host:listen-only` |

**Everything else is refused**, including:
- cloud models, RunPod, peers, webhooks and OTLP;
- `dotnet build` with an implicit restore (EG-PROC-01 and EG-PROC-02 record `nuget-feeds`, even when the feed is
  local);
- `pkg publish` and `pkg pack`, sneakernet export, shared adaptations and bundle export (EG-MESH-01, -02, -07, -08;
  EG-FILE-01, -02);
- an Ollama on the enclave LAN.

---

## Part 2. The matrix: EG id × profile → reachable today?

"Reachable" means registered and callable, without the guard, which is report-only today.

**How to read a cell:**
- **on**: live by default, with no opt-in.
- **opt**: registered, and leaves once the named switch is set or a caller asks.
- **dorm**: registered, but nothing calls it.
- **PG✗**: refused today by PolicyGate.
- **boot✗**: refused at boot by a profile validator.
- **–**: not registered by `AddAshlar` in this profile.
- **host**: composed by a specific host or CLI verb, independent of the profile.
- **ex**: exempt.

**The last column** is the verdict an enforcing guard would give today, with no subject frame and the default
destination: **pass** (Host class) or **refuse**.

| EG id | F | S | E | AG | SW | Sys | Gate or switch (cite) | Enforced, no subject |
|---|---|---|---|---|---|---|---|---|
| MDL-01 MEAI Ollama | on | on | on | on | on | on | P13b `Phases.cs:432-441`; URL from `OllamaEndpointResolver` | pass (localhost); refuse if the URL is remote |
| MDL-02 Bedrock | PG✗ | PG✗ | PG✗ | PG✗ | PG✗ | PG✗ | `Bedrock:Enabled`; dead allow-list `Phases.cs:416` | refuse |
| MDL-03..06 OpenAI/Azure | opt | opt | opt | opt | opt | opt | API-key env `ProviderFactory.cs:216-221`; adaptive `AdaptiveProviderFactory.cs:47-49` | refuse |
| MDL-07 OllamaProvider | on | on | on | on | on | on | default provider `ProviderFactory.cs:237` | pass (localhost) |
| MDL-08 video | opt | opt | opt | opt | opt | opt | `VIDEO_SERVICE_URL` | refuse |
| MDL-09 RunPod | **on** | **on** | – | **on** | **on** | – | NCR; `overnight`, VRAM or queue sends it remote, `NcrCapabilityRouter.cs:126-153` | refuse |
| MDL-10 NCR Ollama | on | on | – | on | on | – | NCR `NodeCapabilityRuntimeServiceCollectionExtensions.cs:136-152` | pass (127.0.0.1) |
| MDL-11 proposer | – | – | – | – | – | – | spike only | refuse |
| MDL-12 cloud probe | dorm/opt | dorm/opt | – | – | dorm/opt | – | `ASHLAR_AIRGAP_PROBE=1`; no consumer | refuse |
| MDL-13 ollama.com | dorm | dorm | – | dorm | dorm | – | `Enabled=true`; no caller of `ListInstallableAsync` | refuse |
| MDL-14 tags | on | on | – | on | on | – | NCR; the IDE endpoint is in the API host | pass (loopback) |
| WEB-01 Bing | – | – | – | – | – | – | kernel registers only the mock, `Phases.cs:341-344` | refuse |
| XPT-01/02 A2A | opt | opt | – | boot✗ | boot✗ | – | `Ashlar:A2A:Transport:Enabled`, `ValidateA2ATransportOptions.cs:27` | refuse |
| XPT-03/04 gRPC | on | on | – | – | – | – | P05 `:169-176` | refuse (pass if the endpoint is loopback) |
| XPT-05 relay | on | on | – | – | – | – | gRPC host; destination chosen by the caller | depends on the target |
| XPT-06/07 MCP client | opt | opt | opt | boot✗ | boot✗ | opt | `ValidateAshlarMcpClientOptions.cs:30` | refuse |
| EXE-01/02 federated | opt | opt | – | opt | opt | – | `BrickHost:RemoteCatalogBaseUrls` | refuse |
| EXE-03 peers | opt | opt | – | opt | opt | – | instances file, plus `EnablePeerNetworkRouting` or `PeerNetworkOnly` | refuse |
| EXE-04 remote exec | opt | opt | – | – | – | – | `ASHLAR_EXECUTION_REMOTE_URL`, `Phases.cs:772-788` | refuse |
| EXE-05 MeshLab | opt | opt | opt | opt | opt | opt | `MeshLab:WorkerExecutor:Enabled`, `Phases.cs:857` | refuse |
| EXE-06 session runner | – | – | – | – | – | – | `AddAshlarAutonomy`, which no host calls | via PROC-03 |
| EXE-07 command runner | dorm | dorm | dorm | dorm | dorm | dorm | `Phases.cs:520`, no consumer | via PROC-03 |
| EXE-08 scaler | opt | opt | opt | opt | opt | opt | `ASHLAR_WORKLOAD_SCALER` | kubectl refuse; compose on the local daemon pass |
| HTTP-01/02 webhooks | dorm | dorm | – | – | dorm | – | `Phases.cs:652-660`; no host resolves `WorkflowExecutor` | refuse |
| HTTP-03 SNS | host/opt | host/opt | host/opt | host/opt | host/opt | host/opt | API and Fleet flags, default false | refuse |
| HTTP-04 SDK | ex | ex | ex | ex | ex | ex | consumer SDK | – |
| HTTP-05/06/07 CLI | host | host | host | host | host | host | operator verbs (raw `EgressHttp`) | refuse unless loopback |
| MESH-01 publish/share | host | host | host | host | host | host | CLI; auto-share needs BackgroundAgents (F, S, SW) and `ASHLAR_MESH_AUTOSHARE=1` | refuse |
| MESH-02 pack `--out` | host | host | host | host | host | host | CLI | refuse |
| MESH-03/04/05 serve, pull, beacon | host | host | host | host | host | host | daemon environment variables | refuse (03 behind a local proxy records Host: known gap) |
| MESH-06 tailscale | ex | ex | ex | ex | ex | ex | LocalOnly | – |
| MESH-07/08 shared and sneakernet | host | host | host | host | host | host | CLI | refuse (a `//127.0.0.1/` path records Host: known gap) |
| FILE-01/02 export | host | host | host | host | host | host | CLI | refuse |
| TEL-01 OTLP | host/opt | host/opt | host/opt | host/opt | host/opt | host/opt | API `Program.cs:242-247` | refuse unless loopback |
| TEL-02 | n/a | | | | | | Unscanned:HostConfigured | – |
| PROC-01 dotnet tools | opt | opt | – | – | opt | – | self-extend toolbox, through BackgroundAgents | refuse (`nuget-feeds`) unless `--no-restore` |
| PROC-02 validation | on | on | on | on | on | on | P18 | build refuse; test pass |
| PROC-03 funnel | on | on | on | on | on | on | P15 | docker pass by default; kubectl refuse |
| PROC-04/05 | ex | ex | ex | ex | ex | ex | Operator, LocalOnly | – |
| STORE-01/02/03 | ex | ex | ex | ex | ex | ex | DataStore, LocalDaemon | – |
| SRV-01 MCP over HTTP (inbound) | opt | opt | opt | boot✗ | **opt** | opt | defect 3 | not egress |
| SRV-02 MCP over stdio (inbound) | on | on | on | boot✗ | on | on | `ValidateAshlarMcpServerOptions.cs:31` | not egress |
| SRV-03 A2A server (inbound) | opt | opt | opt | boot✗ | boot✗ | opt | `ValidateAshlarA2AServerOptions.cs:25` | not egress |
| SRV-04 gRPC server (inbound) | on | on | – | – | – | – | needs `IAgentTransport` | not egress |
| SRV-05/06 (commercial) | host | | | | | | Fleet | not egress |
| LAT-01..04 | dorm | | | | | | | – |

**What the matrix says about AG.** Five things leave an AG host today:
1. **RunPod (MDL-09) needs no opt-in at all.**
2. Six paths need a single environment variable or config key:
   - the cloud LLMs (MDL-03..06), unsanitised;
   - remote MEAI Ollama (MDL-01);
   - federated bricks (EXE-01/02);
   - peers (EXE-03);
   - MeshLab (EXE-05);
   - OTLP (TEL-01).
3. The kubectl scaler (EXE-08).
4. Every host-composed mesh and CLI path.
5. `dotnet build` restores (PROC-02).

The validators already block the remote protocols. The ollama.com catalog (MDL-13) is dormant.

---

## Part 3. Mechanisms that already refuse egress, and how each meets an enforcing guard

| # | Mechanism | Scope | Where | Overlap or conflict with an enforcing guard |
|---|---|---|---|---|
| 1 | `ModuleSelection` | Structural, by profile | `…Deployment.cs:70-148` | Complementary: a path that is not registered is never evaluated. It covers only the eleven flags; P13b, P14, P15, P18, P19 and P20 are profile-blind. |
| 2 | `ForbidsRemoteProtocolEgress` validators: MCP client, A2A client, A2A server | Boot, AG and SW | §1.3 | Redundant with the guard for XPT-01/02/06/07, and harmless: the validator fires first, at boot, so on AG and SW the guard never sees those calls. Edge and System are not covered (the MCP client is allowed there). |
| 3 | MCP server validator, AG only | Boot, inbound | `ValidateAshlarMcpServerOptions.cs:31` | No overlap. Inbound is out of the guard's scope. Defect 3. |
| 4 | PolicyGate (`DefaultChatTargetAccessPolicy`) | Per MEAI call, by target key | `DefaultChatTargetAccessPolicy.cs:25-44`; ordering `AshlarGovernanceChatClientBuilderExtensions.cs:27-35` | **The guard is outermost** (`:26-28`), so under enforcement its refusal pre-empts PolicyGate's, and the explanation is the guard's. PolicyGate allows any `local:` and `peer:` key whatever the URL. The guard classifies by URL, so it is stricter for a remote "local" Ollama. The allow-list is dead (defect 4); fixing it opens Bedrock, and the guard becomes the only refusal on AG. |
| 5 | Sanitisers: MEAI `SanitizingChatClient`; `SanitizingProviderFactory` with `CloudSanitizationProxy` (trust, `ASHLAR_TRUST_ENABLED=1`) | Payload transform, PII and secret blocks | `CloudSanitizationProxy.cs:46-71`; `SanitizingProviderFactory.cs:46-58` | Orthogonal: they change content, the guard decides the destination. **One latent conflict:** `CloudSanitizationProxy.cs:50-51` *skips* sanitising when `IsAirGapped`, which assumes nothing leaves. That holds only once the guard enforces. Moot today, because `SanitizingProviderFactory.cs:51` hard-codes `false` and AG has no trust module. |
| 6 | `DataExfiltrationPolicy` | Tool approval, per agent, deny-list by tool id | `DataExfiltrationPolicy.cs:89-156` | Same intent at a different layer. It refuses at tool approval, before any send, with its own reason; it fails closed for agents it cannot identify (`:98-119`). Gaps: `mcp:` and other unlisted ids pass; `MaxAllowedLevel` is unused; `ModelProvider` is never checked (defect 8). **Link:** the guard's destination bases are the same level flags (`EgressDestinations.cs:28-30`), so an agent's configured sensitivity level, through PR 2's `DataSensitivityLabelBridge.ToDataLabel`, is the natural floor for a subject frame. |
| 7 | `AgentPolicyNarrowingValidator` | Composition | `AgentPolicyNarrowingValidator.cs:45-66` | No conflict. |
| 8 | `IsAirGapped` on the execution context: deterministic only (`BehaviorExecutor.cs:375-380`, `:423-425`); NCR `PrivacyBoundary.LocalOnly` (`NcrAgenticBrickEngine.cs:56-58`, `ModelScoringService.cs:26`, `NodeCapabilityRuntime.cs:83-94`); `ImplementationChainResolver.cs:89` | Routing preference | as listed | Complementary, but it is the second of three meanings of "air-gapped" (profile, `ASHLAR_AIRGAP`, context). It is never set from the profile, and a remote caller can set it (`BrickCatalogWireMapper.cs:92`). |
| 9 | `PeerTrustPolicyResolver` | Trust in the destination peer | `NcrCapabilityRouter.cs:39-42`, `:156-189` | Orthogonal: it answers "do I trust the destination", the guard answers "may this data go down". A future source of per-peer destination labels. |
| 10 | Feature flags (MeshLab, MCP, A2A, remote catalogs, RunPod peer routing, ollama.com `Enabled`) | Opt-in | as listed | AG ignores all of them except MCP and A2A. RunPod has no flag (§1.4). |
| 11 | SNS host allow-list | Destination allow-list | inventory EG-HTTP-03 | No conflict. |
| 12 | Barrier validation (gRPC) | Caller-clearance names | `AgentTransportServiceImpl.cs` | A parallel label vocabulary. The relay (XPT-05, defect 1) forwards to any endpoint. |
| 13 | Trust packs (`config/trust-packs/air-gapped.json`) | Observation capture rules | `TrustPolicyPackRegistry.cs` | **Not egress**, despite the name. Worth a sentence in the docs so nobody takes it for an egress control. |
| 14 | Resilience and catch-all sites | Not a control, but they decide whether a refusal surfaces | see the next paragraph | **This is a real conflict.** |

**Item 14 in detail.**
- An enforcing refusal raised as `HttpRequestException` or `IOException` is classified transient by
  `TransientClassifiers.cs:26-36`. `ProviderFactory`'s retry policy would then retry it (`ProviderFactory.cs:181-200`).
- These sites would absorb a refusal into a Warning, a fallback or an empty result, breaking §7's rule that a refusal
  is "explained, never silent":
  - `AdaptiveProviderFactory.cs:60-64`, which tries the next provider;
  - `HttpRemoteBrickCatalog.cs:62-66`, `:89`, `:149`, which return empty;
  - `ModelArtifactCatalogService.cs:62-65`, which skips the source;
  - `CompositeBrickRegistry.cs:58-61`, which logs a Warning;
  - `AshlarPeerBrickExecutor.cs:185-196`, which returns `peer.dispatch_exception` with `ex.Message` in the detail.

---

## Part 4. Analysis

1. **"Enforce" with no producer is a loopback-only switch.**
   - With no subject frame in production (§1.5), enforcing on AG or SW today allows only the Host-class
     destinations in §1.6.
   - For AG that is close to the promise in `AshlarDeploymentProfile.cs:40-48`, but too blunt for two kinds of
     legitimate offline traffic:
     - enclave LAN infrastructure, which is not Host-class;
     - physical-transfer exports (sneakernet, `pkg pack`/`publish`, bundles), which classify as NetworkExport.
   - For SW it removes cloud models, which `product-split.md:52-54` says SW keeps as an opt-in. It also removes
     self-extend's `dotnet build` restores (PROC-01).
2. **The guard is the only mechanism that covers every row.**
   - The profile validators cover four rows. `ModuleSelection` covers the module-flagged ones.
   - The guard sits on every factory client, every raw `EgressHttp`, every MEAI target and 16 explicit sites
     (counted by grep).
   - So it should be the backstop. It should not be the *primary* refusal for paths whose correct AG behaviour is
     "do the work locally": RunPod and adaptive escalation. There, an egress refusal turns an overnight job into a
     failure when it should have stayed local.
3. **The switch has to be process-wide.**
   - Explicit sites call `EgressGuard.ProcessDefault.Evaluate`, not a DI-resolved guard; the 16 sites above,
     for example `ValidationServiceAdapter.cs:528` and `Program.cs:247`.
   - `AddAshlarEgressGuard` registers `ProcessDefault` itself (`EgressServiceCollectionExtensions.cs:59`).
   - So the mode has to be resolved where `ProcessDefault` already resolves the profile (`EgressGuard.cs:126-139`),
     through `AshlarDeploymentProfileEnvironment.Effective`. That is the same value the validators use, so the two
     can never disagree.
4. **Two risks come from the statics.**
   - `ResolvedRaw` is process-wide and last-wins. A test that builds a Full host after an AG host in the same process
     flips enforcement. The existing `DeploymentProfileEnvironmentCollection` test collections exist for this reason.
   - `NoteResolved` runs inside `AddAshlar`, at registration time. Anything evaluated before `AddAshlar` in a host
     falls back to the variable. EG-TEL-01 is safe: it runs after `AddAshlar`, at `Program.cs:247` versus `:224`.
5. **Two existing fail-closed accidents would flip if "fixed" without enforcement:**
   - the dead allow-list (defect 4);
   - the sanitiser's skip under `IsAirGapped` (`CloudSanitizationProxy.cs:50-51`).
6. **The documents disagree about SW**: compare `AshlarDeploymentProfile.cs:58` with `product-split.md:52-54` and
   `Configuration.md:28`. PR 4 forces the choice.
7. **Side finding, docs drift.** `docs/Configuration.md:168` says mesh knowledge sync is active when `AddAshlar`
   registers adaptation (Full, Server, AirGapped). No file under `src/` or `application/` references `KnowledgeSync`;
   only commercial Fleet does (EG-LAT-04), and Fleet.Host composes it regardless of profile.

---

## Part 5. Decisions for the owner

### D1. What does "AirGapped enforces" mean?

- **(a)** The star property with today's destination table. Only Host-class destinations pass. Every other one is
  refused while no subject frame exists.
- **(b)** (a), plus an operator-declared destination-label table: host, CIDR or path, mapped to a label. Enclave
  infrastructure (a LAN Ollama, peer hosts, removable-media export directories) is labelled explicitly, for example
  SystemHigh for "inside the enclave". The table ships empty, so the default is (a).
- **(c)** A per-family kill-list on AG (no `model.*`, `http*`, `a2a`, `grpc`, `mcp`, `telemetry` or `mesh.serve/pull`),
  with file exports allowed, and labels ignored.

**Consequences.**
- **(a)** is simplest and keeps §7 (missing label = SystemHigh). It refuses enclave-LAN hosts
  (`EgressDestinations.cs:131-151` knows only loopback) and every offline transfer verb (EG-MESH-01, -02, -07, -08;
  EG-FILE-01, -02). It also refuses `dotnet build` restores from an offline feed, because EG-PROC-02 is recorded as
  `nuget-feeds` by argv.
- **(b)** is the "per-destination label config" PR 3a deferred to PR 4 (`$SP/pr3a/guard_design.md:234-238`). It
  keeps the BLP semantics and leaves each allowance recorded and explained. It costs a config surface (environment
  variables only, `AshlarServiceCollectionExtensions.cs:128-130`), tests, and a risk that an operator mislabels.
- **(c)** bypasses the label model and does not satisfy §5's "write labelled data down" test.

**Recommendation: (b)**, shipped with an empty table, so the out-of-the-box behaviour is (a).

### D2. What does "SecureWorkstation enforces" mean?

- **(a)** The same rule as AG, (b) above. Cloud models, webhooks, OTLP, mesh publish and restores are refused unless
  a subject frame or a destination label allows them.
- **(b)** SW enforces only on flows that carry a subject frame. Calls with no subject are reported, not refused.
- **(c)** SW stays report-only for model and HTTP calls until PR 5 lands clearances. It enforces only what the
  validators already refuse.

**Consequences.**
- **(a)** with no producer means SW loses cloud models. That contradicts `product-split.md:52-54`, and it also
  blocks self-extend `dotnet build` (PROC-01). It is coherent only if PR 4 ships at least one subject producer: the
  background-agent cycle, with its mark floored at the agent's configured sensitivity through PR 2's
  `DataSensitivityLabelBridge.ToDataLabel`. SW cloud use then works for agents whose level the destination dominates.
- **(b)** breaks §7's fail-closed rule: every path without a subject leaks silently.
- **(c)** does not meet §5 ("SW enforces by default").

**Recommendation: (a).**
- PR 4 should ship the agent-cycle producer.
- Update `product-split.md:52-54` and `AshlarDeploymentProfile.cs:58` to say what SW now refuses.
- If the owner will not take a producer in PR 4, choose AG enforcing and SW report-only in PR 4, and record that
  this departs from §5.

### D3. Defect 5: should AirGapped stop registering network paths, or keep them and let the guard refuse?

- **(a)** Strip them under AG: no RunPod or peer executor, no federated mesh, no ollama.com, MeshLab or cloud
  providers.
- **(b)** Keep the registrations and let the enforcing guard refuse at the call.
- **(c)** Keep the DI shape, and add three things:
  - **Two profile-aware decision points:**
    - `NcrCapabilityRouter` never picks a remote target on AG, and returns Local with an explained reason;
    - `AdaptiveProviderFactory` never escalates past local on AG.
  - **Boot validators for AG opt-ins**, on the existing `ForbidsRemoteProtocolEgress` pattern: refuse
    `BrickHost:RemoteCatalogBaseUrls`, `Ashlar:RunPod:EnablePeerNetworkRouting`, `MeshLab:WorkerExecutor:Enabled`
    and `Meai:Bedrock:Enabled`, and flip the ollama.com `Enabled` default to false on AG.
  - **The guard as the backstop** for everything else.

**Consequences.**
- **(a)** needs null objects:
  - `NcrCapabilityRouter`'s constructor requires `RunPodBrick` and `IPeerExecutor` (`NcrCapabilityRouter.cs:24-30`);
  - `CapabilityRoutingBrick` is the default `IBrickExecutor` (`RunPodCapabilityRoutingServiceCollectionExtensions.cs:82`).

  It changes the DI shape that `HostingDeploymentProfileTests` and `KernelDiCompositionProdStyleTests` pin. It covers
  only the named paths, not MEAI remote URLs, OTLP or the CLI.
- **(b)** is one mechanism. But an overnight job on AG then *fails* with an egress refusal instead of running locally:
  RunPod is the default remote target with no config (§1.4). And a misconfigured opt-in surfaces only at the first
  call.
- **(c)** is more code and a visible behaviour change (jobs stay local). It fails a misconfigured host at boot, the
  same way MCP and A2A already do, and keeps the guard as the single backstop.

**Recommendation: (c).** Each change needs its own mutation check.

### D4. What do Full, Server, Edge and System do?

- **(a)** Report-only by default, with an explicit opt-in to enforce: an environment variable such as
  `ASHLAR_EGRESS_MODE=enforce`, plus a hosting option.
- **(b)** Report-only, with no opt-in in PR 4.
- **(c)** Enforce everywhere.

**Consequences.**
- **(c)** turns every Full host into a loopback-only host today, because nothing produces a subject frame.
- **(b)** is least work, but no Server deployment can adopt early.
- **(a)** adds one switch and a test row per profile.

**Recommendation: (a).** Every decision record already carries `Mode` and `ProfileEnforcesByDefault`
(`EgressDecision.cs:58`, `:108`), so the mode in force is visible in each record.

### D5. Can an operator turn AirGapped or SecureWorkstation enforcement off?

- **(a)** AG cannot be turned off. Its escape hatch is the destination table (D1b). SW can be set to report-only by
  an explicit override, logged at Warning at startup and recorded in every decision.
- **(b)** Both can be overridden.
- **(c)** Neither can.

**Consequences.**
- **(b)** makes "AG enforces" a default rather than a property.
- **(c)** leaves an SW install with no cloud models (D2a) no way out but switching profile.
- **(a)** matches what each profile name promises.

**Recommendation: (a).**

### D6. Defect 3: the MCP server over HTTP boots on SecureWorkstation

- **(a)** Fold the fix into PR 4: refuse the HTTP transport on SW and keep stdio. The validator needs a marker that
  `WithHttpTransport()` registers, since it cannot see the transport today.
- **(b)** Allow HTTP on SW only when it is bound to loopback.
- **(c)** Defer to the read-side (inbound) PR.

**Consequences.**
- **(a)** is small and closes the stated promise (`AshlarDeploymentProfileEnvironment.cs:50-55`;
  `product-split.md:64-66`). It touches `Ashlar.API` only through configuration, but it widens PR 4 beyond what the
  owner folded in.
- **(b)** requires reading Kestrel's bindings.
- **(c)** leaves `repo.fs.read` reachable over the network on SW in the meantime.

**Recommendation: (a)**, as a separate commit with its own test row.

### D7. Defect 4: the dead MEAI cloud allow-list

- **(a)** Leave it in PR 4 and record it. It fails closed.
- **(b)** Fix it in PR 4, together with an AG/SW boot refusal of `Bedrock:Enabled` and the enforcing guard.
- **(c)** Fix it in a separate PR after PR 4.

**Consequences.** Fixing it opens `cloud:bedrock:*` in every profile, including AG, wherever Bedrock is enabled. It
is safe only once the guard enforces, or a validator refuses it on AG. Leaving it keeps Full hosts' Bedrock
configuration silently ineffective.

**Recommendation: (c)**, sequenced after PR 4. The D3c validator for `Bedrock:Enabled` under AG lands in PR 4 so the
later fix cannot open AG.

### D8. How does a refusal surface? (It decides whether AG and SW refusals are "explained, never silent".)

- **(a)** A dedicated, non-transient exception type that is not `HttpRequestException` or `IOException`, carrying the
  decision. The catch-all sites in Part 3, item 14 rethrow it.
- **(b)** Throw `HttpRequestException` from the handler.

**Consequences.**
- **(b)** is retried by `ProviderFactory` (`TransientClassifiers.cs:31`). It is also absorbed by
  `AdaptiveProviderFactory.cs:60-64`, `HttpRemoteBrickCatalog.cs:62-66`, `ModelArtifactCatalogService.cs:62-65` and
  `CompositeBrickRegistry.cs:58-61`.
- **(a)** needs about five catch sites changed, each with a test that the refusal propagates.

**Recommendation: (a).**

### D9. Defects 6 and 8: fold them into PR 4, or rely on the guard and PR 5?

- **(a)** Fold both:
  - check `ModelProvider` against `RequireLocalOnly` and `BlockExternalLLMs` when the agent is registered;
  - give `DataExfiltrationPolicy` an `mcp:` class;
  - make `AdaptiveProviderFactory` consult the guard before it escalates.
- **(b)** Rely on the enforcing guard plus the agent-cycle subject frame (D2a) for AG and SW. Leave the policy gaps
  to PR 5 (clearances on subjects).
- **(c)** Defer both entirely.

**Consequences.**
- **(b)** fails closed on AG and SW, but the leak stays open on Full and Server, which are report-only.
- **(a)** widens PR 4.
- **(c)** leaves the per-agent `RequireLocalOnly` promise unenforced on every profile.

**Recommendation: (b)**, with the D3c "AG never escalates" change carried in PR 4. Record defect 8 for PR 5.

### D10. Where is the enforcement mode resolved?

- **(a)** Process-wide, inside `EgressGuard.ResolveProfile`, from `AshlarDeploymentProfileEnvironment.Effective`
  plus the D4 and D5 override.
- **(b)** Per container, through an injected `IEgressGuard` with options.

**Consequences.**
- **(b)** misses every explicit site, because they call `ProcessDefault` (Analysis 3).
- **(a)** inherits the last-wins static (Analysis 4), so tests must run in the existing profile-environment
  collections.

**Recommendation: (a).**

---

## Part 6. Risks

- **The worst outcome on SW.** With no subject producer, SW enforcement is loopback-only. If PR 4 ships enforcement
  without the agent-cycle producer, SW users lose cloud models, webhooks, OTLP, mesh publish and `dotnet` restores in
  one merge.
- **Enclave hosts.** LAN addresses are not Host-class, so AG enforcement refuses enclave hosts unless the destination
  table (D1b) ships.
- **RunPod is reachable from an AG host with no configuration** (§1.4). Under report-only that holds until PR 4
  merges.
- **Absorbed refusals.** Swallowed or retried refusals (D8) would make enforcement look like flaky providers rather
  than explained refusals.
- **The `ResolvedRaw` static.** It is process-wide and last-wins, so profile-dependent tests that are not in the
  profile-environment collection can flip enforcement.
- **Fixing defect 4 before PR 4 enforces** opens Bedrock on AG.
- **`IsAirGapped` versus the profile.** `IsAirGapped` can be set by a remote caller (`BrickCatalogWireMapper.cs:92`),
  and `CloudSanitizationProxy.cs:50-51` skips sanitising when it is set. Neither should be read as a profile
  guarantee.
- **No AG evidence today.** `test-air-gapped-no-network.yml` has never been green (`docs/CiGateInventory.md:221`), so
  there is no existing proof that an AG host runs with no network. A seeded AG test in PR 4 would be the first.
- **Static readings that need a test to settle:**
  - the dead allow-list (defect 4);
  - AG reaching RunPod through `POST /api/bricks/generation.capability-routing/execute` with `overnight=true`,
    including whether `MeshSecurityMiddleware` admits it by default;
  - which existing profile tests pin a DI shape that D3 would change.
