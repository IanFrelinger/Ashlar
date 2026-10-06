# Attack list for SPEC-007 PR 4.10: AirGapped and SecureWorkstation hygiene

*Read-only scout, 2026-10-06. Every `file:line` below was read on master `de41a8ac` in `/home/user/Ashlar`. Design
citations are `DESIGN :line` into `DESIGN-4-final.md` (this folder); the owner answers at its end (`:1156-1171`)
override §3.A. The lane's draft PR body (`pr-4.10-body.md`) is quoted only as the lane's claim; it is not evidence,
and nothing in the lane branch was read. Anything not settled by reading is marked **[unverified]** or
**[needs a test]**.*

## 0. How to read this, and what every twin should pin

**Scope of the PR under test.** DESIGN §2.7 (`:587-626`), §4 row 4.10 (`:860`), D35 (`:833`), D5 (`:803`), D41
(`:839`, §2.10 `:680-697`), owner answer Q6 (`:1167`; SPEC-007 decisions log `:422`). 4.10 is a configuration and
routing PR: the guard still refuses nothing (`EgressGuard.cs:29-32`; every profile resolves `report`,
`EgressEnforcement.cs:136-142`), so **a route that is still registered on AG still reaches the network after 4.10**.
The design's own position is that the guard is the backstop for everything §2.7 does not name (`DESIGN :613`), and
that backstop acts only from 4.7/4.11. Each item below therefore says whether the path is (a) something 4.10 must
close, (b) something the design leaves to the guard and the PR body must say so, or (c) something the design claims
and master cannot deliver as written.

**What a record looks like on AG today, for any non-Host destination.** With `AddAshlar(AirGapped)` composed and
no `ASHLAR_EGRESS_MODE`, every decision on master carries: `Mode = "report"`, `ModeBasis = "profile:air-gapped"`
(`EgressEnforcement.cs:116-143`, canonical name from `AshlarDeploymentProfileEnvironment.cs:95-104`),
`Profile = "air-gapped"`, `ProfileEnforcesByDefault = true` (`EgressGuard.cs:214-216`), `Current = SystemHigh`,
`CurrentBasis = "no-subject"` (no production frame exists before 4.5), `Access.Allowed = false`,
`Access.Reason = SystemHighData` for anything outside the host boundary, `Refused = false`
(`EgressDecision.cs:56`). The destination class follows the family (`EgressDestinations.cs:76-104`): `http.factory`,
`mesh.*`, `telemetry` → `NetworkExport` / `Internal`; `model.*` → `ExternalModel` / `Internal`; loopback, `unix`,
`npipe`, `host:` → `Host` / `SystemHigh` (`:140-161`, `:190-209`). Factory clients record site `factory:<client
name>` (`EgressServiceCollectionExtensions.cs:40`, `:63-69`). With the 4.6 opt-in `ASHLAR_EGRESS_MODE=enforce`,
`Mode = "enforce"`, `ModeBasis = "override"`, `Refused = true`, and still nothing stops.

**So the strongest evidence for a 4.10 twin is an absence**: zero requests at a stub primary handler on the named
client, and zero decisions at that client's `factory:` site, after the AG composition is driven down the path that
produces one on Full. A twin that asserts only on a record proves a request was *recorded*, not that it was not made.
Pair every "never reaches" claim with a Full control that shows the same path producing exactly one request and one
record with the fields above.

**Hygiene every twin must have (D41).** `AddAshlar(AirGapped)` and `AddAshlar(SecureWorkstation)` note a profile the
process keeps (`AshlarDeploymentProfileEnvironment.cs:17-31`), so the class sits in `[Collection("EnvironmentVariables")]`
and holds an `EgressProcessStateScope` disposed in `Dispose` (`Helpers/EgressProcessStateScope.cs:20-72`; the two
existing AG composers do this: `HostingDeploymentProfileTests.cs:24,29`, `KernelDiCompositionProdStyleTests.cs:28,36`,
and the pre-existing `AirGappedProfileApiHostProdStyleTests.cs:27,34`). `ProcessGlobalEnvironmentConventionTests`
scans every test project (`:413-420`) for the compose marker plus `AshlarDeploymentProfile.AirGapped` /
`SecureWorkstation` (`:88-96`, `:357-366`); the new twin files must carry the collection attribute or cert-gate goes
red for the wrong reason.

---

## 1. Numbered attack list

Format per item: **(1) path today** with `file:line`; **(2) design**; **(3) twin** (shape, inputs, expected record
fields); **(4) mutation** that exposes a fake fix and the assertion that goes red.

### A. Defect 5: remote execution routing on AirGapped

#### 1. The headline: RunPod is reached from AG with no configuration (overnight, VRAM, compute, queue)

1. `CapabilityRoutingBrick.ExecuteAsync` → `_router.ResolveExecutionTarget(requirements)` (`CapabilityRoutingBrick.cs:77`,
   `:131`; `overnight` comes off the wire at `:195`). `NcrCapabilityRouter.ResolveExecutionTarget`
   (`NcrCapabilityRouter.cs:46-72`) → `ResolveRemoteReason` (`:130-154`: overnight `:132-135`, VRAM `:137-140`,
   compute class `:142-145`, queue depth `:147-151`) → `ResolveRemoteTarget` (`:74-128`) → with peer routing off
   (default `false`, `RunPodBrickConfig.cs:53`) `return new ExecutionTarget.Remote(_runPodBrick, baseReason)` (`:126-127`)
   → `RunPodBrick.ExecuteAsync` (`RunPodBrick.cs:87-116`) → `RunPodHttpClient.SpinUpInstance` POST `/v2/instances`
   (`RunPodHttpClient.cs:29-43`) on the typed client whose base address defaults to `https://api.runpod.io`
   (`RunPodCapabilityRoutingServiceCollectionExtensions.cs:50-57`; `RunPodBrickConfig.cs:49`; `AshlarDefaults.cs:161`),
   with no `Authorization` header when `ApiKey` is empty (`RunPodHttpClient.cs:199-209`). Registered on AG because
   `IncludeNodeCapabilityRuntime` is `true` there (`AshlarServiceCollectionExtensions.Deployment.cs:115-116`;
   `AshlarKernelRegistrar.Phases.cs:69-72`). Inventory row EG-MDL-09 (`docs/EgressInventory.md:75`) says exactly this.
2. DESIGN §2.7 "Defect 5" (`:589-597`): every remote reason returns `ExecutionTarget.Local(_localExecutor,
   "AirGapped: remote execution unavailable; running locally (<reason>)")`. D35 (`:833`). §4 row 4.10 done-when
   (`:860`): "overnight routing chooses Local with the AG reason".
3. Twin (cert-gate, `EnvironmentVariables`, scope disposed): `AddAshlar(o => o.DeploymentProfile = AirGapped)`;
   replace the typed client's primary handler with a recording stub (`ConfigurePrimaryHttpMessageHandler` on the
   `IRunPodClient` typed client, or a handler-builder filter) and subscribe an `IEgressDecisionSink`. Resolve
   `ICapabilityRouter`; call with `JobRequirements { IsOvernightOrBackground = true }`, then with a stub
   `INCRCapabilitySnapshot` giving `AvailableVramBytes = 0` and `MinimumVramBytes = 1`, then `ComputeClass` below,
   then `CurrentQueueDepth > QueueDepthThreshold`. Expect `ExecutionTarget.Local` four times, `Reason` starting
   `AirGapped: remote execution unavailable; running locally (` and ending with the master reason text (e.g.
   `Overnight/background job forces remote execution.)`). Then drive `CapabilityRoutingBrick.ExecuteAsync` with the
   overnight input and a local executor stub: expect 0 requests at the RunPod stub and 0 decisions whose
   `Destination == "https://api.runpod.io"`. Full control: the same input gives `Remote`, 1 request, 1 decision with
   `Destination = "https://api.runpod.io"`, `DestinationClass = NetworkExport`, `DestinationLabel = Internal`,
   `Family = "http.factory"`, `Site = "factory:IRunPodClient"` **[needs a test: the typed client's registered name]**,
   `Current = SystemHigh`, `CurrentBasis = "no-subject"`, `Access.Reason = SystemHighData`, `Mode = "report"`,
   `ModeBasis = "profile:full"`.
4. Mutation: in the router, make the AG branch fall through to `ResolveRemoteTarget` for `IsOvernightOrBackground`
   only (a partial fix) → the overnight row goes red (`Remote`, reason without the prefix) while the three others stay
   green: that is why the twin must cover all four reasons. A second mutation: keep `Local` but drop the prefix →
   the reason assertion goes red. A third: pass `null` for the profile where `AshlarKernelRegistrar`/DI builds the
   router → every AG row red, Full unchanged.

#### 2. Explicit `PeerNetworkOnly` on AG

1. `RemoteExecutionPreference.PeerNetworkOnly` short-circuits at `NcrCapabilityRouter.cs:48-51` into
   `ResolveRemoteTarget`, which at `:89-105` returns `Remote(_peerExecutor, …)` whether peer routing is enabled or
   not (`:103-105` "peer routing is disabled" still returns the peer executor). `AshlarPeerBrickExecutor.ExecuteAsync`
   then POSTs `/api/bricks/{peerBrickId}/execute` to every eligible peer (`AshlarPeerBrickExecutor.cs:49-70`;
   EG-EXE-03, `docs/EgressInventory.md:113`).
2. DESIGN `:591`: "An explicit `PeerNetworkOnly` is refused with an explained failure." The lane says it throws
   `InvalidOperationException` (body, deviation 5: `ExecutionTarget` has two cases and a private constructor).
3. Twin: AG + `PeerNetworkOnly` → throws; message names AirGapped and `PeerNetworkOnly`; 0 peer requests. Also
   `PeerNetworkOnly` with `EnablePeerNetworkRouting = true` **on Full** → `Remote(peer executor)` control. Check that
   `CapabilityRoutingBrick.ExecuteAsync(BrickInput…)` (`CapabilityRoutingBrick.cs:123-155`) surfaces the throw as a
   failed output rather than swallowing it into a fallback; the API's `ExecuteBrickAsync` returns `Success=false`
   with `ex.Message` (`AshlarEndpoints.cs:849-857`), which is the explained failure Q7 allows for an operator-facing
   surface; make sure the message carries no destination path.
4. Mutation: route `PeerNetworkOnly` through the Local fallback of item 1 instead of refusing → "throws" goes red
   (and silently runs the job locally, which the design rejects); remove the refusal → `Remote` and the peer stub sees
   a POST.

#### 3. `PreferPeerNetwork` with peers, and a validator defeated by a pre-built options instance

1. `PreferPeerNetwork` is decided **before** `ResolveRemoteReason` (`NcrCapabilityRouter.cs:53-61`): with
   `EnablePeerNetworkRouting` and an eligible peer it goes remote at once. On AG the validator for
   `Ashlar:RunPod:EnablePeerNetworkRouting=true` (`DESIGN :608`) is meant to stop this at boot. But `IOptions<T>`
   validation runs only through `OptionsFactory`; a host that registers `services.AddSingleton(Options.Create(new
   RunPodBrickConfig { EnablePeerNetworkRouting = true }))` after `AddAshlar` replaces the descriptor and no
   `IValidateOptions<RunPodBrickConfig>` ever runs. The peer snapshot comes from `FileBasedInstanceDiscovery`
   (`ASHLAR_MESH_INSTANCES_PATH`, `RunPodCapabilityRoutingServiceCollectionExtensions.cs:38-48`), so the peer list is
   a file an operator or agent can write.
2. DESIGN `:589-590` "Every remote reason … returns Local"; the lane's body says "a preferred peer" is covered by the
   AG branch. Both branches (validator and router) must hold independently.
3. Twin: AG + `IOptions<RunPodBrickConfig>` replaced by `Options.Create` with peer routing on + stub
   `IPeerCapabilitySnapshot` with one eligible candidate + `PreferPeerNetwork` → `Local` with the AG prefix, 0 peer
   requests. Separate twin for the validator proper: AG + env `Ashlar__RunPod__EnablePeerNetworkRouting=true` (the
   kernel reads env only: `AshlarServiceCollectionExtensions.cs:141-143`, `Phases.cs:63-67`) → host start throws
   `OptionsValidationException` naming the setting; and in a host-less composition the first
   `IOptions<RunPodBrickConfig>.Value` read (the router ctor reads it, `NcrCapabilityRouter.cs:40-42`) throws.
4. Mutation: handle `PreferPeerNetwork` only inside `ResolveRemoteTarget` and not at `:53-61` → the twin goes red
   (`Remote(peer)`). Validator mutation: validate `PreferPeerNetworkOverCloud` instead of `EnablePeerNetworkRouting`
   → the boot twin boots.

#### 4. The router is bypassed: `generation.runpod` is a brick of its own (remaining path; design leaves it to the guard)

1. `RunPodBrick` is registered as a singleton and added to the brick registry's additional types
   (`RunPodCapabilityRoutingServiceCollectionExtensions.cs:77`, `:85-96`), id `generation.runpod`
   (`RunPodBrick.cs:43`). The API maps `POST /api/bricks/{brickId}/execute` (`AshlarEndpoints.cs:196`), resolves the
   brick by id from `IBrickRegistry` (`:836`) and calls `brick.ExecuteAsync(input, implementation, context, …)`
   (`:849`) → `RunPodBrick.ExecuteAsync(BrickInput…)` → `ExecuteAsync(payload, requirements, context)` →
   `_runPodClient.SpinUpInstance` (`RunPodBrick.cs:87-116`). No router, no profile. The same brick is reachable from
   any workflow or behaviour that names it (`BehaviorExecutor`, `WorkflowExecutor` fallback chains: DESIGN §2.3 row 27).
2. DESIGN §2.7 fixes `ResolveExecutionTarget` only (`:589-597`); "the guard is the backstop for everything else"
   (`:613`). The lane body lists "a `RunPodBrick` or peer executor invoked directly as a brick" as not covered.
3. Twin (documents the gap; it must be stated in `docs/EgressInventory.md` EG-MDL-09 and in the PR body): AG +
   `IBrickRegistry.GetBrick("generation.runpod")` is non-null; executing it with a stub RunPod handler produces 1
   request to `https://api.runpod.io/v2/instances` and 1 decision: `Site = "factory:IRunPodClient"` [needs a test],
   `Destination = "https://api.runpod.io"`, `NetworkExport`, `Internal`, `Current = SystemHigh`, `no-subject`,
   `Access.Reason = SystemHighData`, `Mode = "report"`, `ModeBasis = "profile:air-gapped"`, `Refused = false`.
   If the lane closes it (e.g. `RunPodBrick` refuses on AG, or AG does not add it to `AdditionalBrickTypes`), the
   twin flips to 0 requests and `GetBrick` null or a refusal.
4. Mutation (only if the lane closes it): remove the AG check in the brick or the registry filter → the request
   reappears.

#### 5. `AdaptiveProviderFactory` escalates an LLM call from `ollama`/`local` to `openai`/`azure`

1. `ExecuteLLMAsync` ignores the caller's `provider` and resolves through `ILoadPolicy` (`AdaptiveProviderFactory.cs:40`);
   with `resolved ∈ {ollama, local}` the candidates are `{ resolved, "openai", "azure" }` (`:47-49`); the loop skips
   unavailable providers (`:54-55`) and calls `_inner.ExecuteLLMAsync(p, …)` (`:58`). `openai` is available when
   `OPENAI_API_KEY` is set (`ProviderFactory.cs:216`), `azure` with the three `AZURE_OPENAI_*` variables (`:219-221`).
   The bare factory sends on the static `EgressHttp` client (`ProviderFactory.cs:61`; EG-MDL-03/04,
   `docs/EgressInventory.md:69-70`). The adaptive wrapper is on when `ASHLAR_LOAD_PREFERENCE` is set or
   `UseAdaptiveLoadBalancing` (`Phases.cs:510-511`, `:587-593`).
2. DESIGN `:599-601`: on AG `providersToTry` becomes `{ resolved }` when `resolved` is local. D35 `:833`; critic
   finding X12 (`:716`) and the defects table (`:1043`).
3. Twin: AG + adaptive on (`UseAdaptiveLoadBalancing = true` through the hosting options, so no env write) + a stub
   `ILoadPolicy` registered before `AddAshlar` (`TryAddSingleton` at `Phases.cs:555` keeps the earlier one) returning
   `"ollama"` + a recording inner `IProviderFactory` (register the concrete `ProviderFactory` seam or wrap
   `IProviderFactory` after `AddAshlar`) where `IsProviderAvailable` is true for all four and `ExecuteLLMAsync("ollama")`
   throws → expect tried = `["ollama"]` exactly and a `ModelUnavailableException`; never `openai`, never `azure`.
   Full control: tried = `["ollama","openai","azure"]`. With real HTTP instead of a stub: 0 requests at a stub
   handler for `api.openai.com`, and no decision with `Family = "model.legacy"` and `Destination =
   "https://api.openai.com"` (the Full control shows one: `ExternalModel`, `Internal`, `Site = "EG-MDL-03"`,
   `Access.Reason = SystemHighData`).
4. Mutation: restore `{ resolved, "openai", "azure" }` for AG → the tried-list assertion goes red with
   `["ollama","openai","azure"]`.

#### 6. A cloud `resolved` on AG (`ASHLAR_LOAD_PREFERENCE=server`) — the design's rule does not cover it

1. `PreferenceLoadPolicy.ResolveProvider` with `server` returns `"openai"` when `OPENAI_API_KEY` is set
   (`PreferenceLoadPolicy.cs:21`, `:36-37`, `:52-64`). `AdaptiveProviderFactory.cs:47-49` then yields
   `{ "openai", "ollama", "local" }` and tries `openai` first.
2. DESIGN `:600` only says the list "becomes `{ resolved }` when `resolved` is local". For a cloud `resolved` the
   design is silent, so a literal implementation still tries `openai` on AG. The lane's integrator decision 2 drops
   `openai`/`azure` whatever `resolved` is. This is the right reading of "never escalates past local" (`:599`), and
   the verifier must make sure it is what shipped.
3. Twin: AG + stub `ILoadPolicy` → `"openai"`, inner records → tried excludes `openai` and `azure`, includes
   `ollama`/`local` in that order; Full control tries `openai` first.
4. Mutation: implement the design's literal rule (`resolved` local → `{resolved}`; otherwise today's list) → red
   with `["openai", …]`.

#### 7. Single-image vision escalates unconditionally

1. `ExecuteVisionAsync` builds `{ resolved, "ollama", "openai", "azure" }` unconditionally (`AdaptiveProviderFactory.cs:85`)
   and loops (`:87-100`); the inner cloud paths are EG-MDL-05/06 (`ProviderFactory.cs:422`, `:466` per
   `docs/EgressInventory.md:71-72`), with the image bytes as a base64 data URI and no sanitiser in between.
2. DESIGN `:602-603`: on AG it drops `openai` and `azure`; critic CT6 (`:1068`) made single-image vision an explicit
   twin.
3. Twin: as item 5 for `ExecuteVisionAsync`, both with `resolved = "ollama"` and `resolved = "openai"`; expected
   tried ⊆ `{ollama, local}`.
4. Mutation: restore `:85` → red.

#### 8. Multi-frame vision with a cloud `resolved` — the design is wrong here

1. `ExecuteVisionMultiFrameAsync` calls `_inner.ExecuteVisionMultiFrameAsync(resolved, …)` (`AdaptiveProviderFactory.cs:114-120`).
   With `resolved = "openai"` (item 6) that is a cloud call carrying every frame (EG-MDL-05 reaches
   `ExecuteVisionMultiFrameAsync :633-661`, `docs/EgressInventory.md:71`).
2. DESIGN `:604`: "The multi-frame path (`:118-125`) already uses `resolved` only; it is catch-all row 25" (`:381`).
   That treats the multi-frame path as already safe, which holds only if `resolved` is local, and nothing on AG
   makes it so. The lane added a refusal (`ModelUnavailableException` on a cloud resolve, body decision 2).
3. Twin: AG + stub `ILoadPolicy` → `"openai"` → `ExecuteVisionMultiFrameAsync` throws before calling the inner (inner
   records 0 calls); AG + `"ollama"` → inner called with `ollama`; Full + `"openai"` → inner called with `openai`.
4. Mutation: drop the refusal → inner receives `openai` on AG → red.

#### 9. Video analysis passes straight through to `VIDEO_SERVICE_URL` (not in the design)

1. `ExecuteVideoAsync` is a pure pass-through (`AdaptiveProviderFactory.cs:129-135`); the bare factory's `video`
   provider is available whenever `VIDEO_SERVICE_URL` is set (`ProviderFactory.cs:224`) and POSTs the prompt and an
   mp4 of every frame to `{VIDEO_SERVICE_URL}/v1/analyze` on the shared static client (EG-MDL-08,
   `docs/EgressInventory.md:74`: "No profile check. Frames are not sanitised.").
2. DESIGN §2.7 names LLM, single-image and multi-frame vision only (`:599-604`). Nothing about video. By `:613` it is
   left to the guard.
3. Twin (documents): AG + `VIDEO_SERVICE_URL=http://203.0.113.9:8000` (through a seam if one exists; otherwise this
   is an env write, so `EnvironmentVariables`) + stub handler → 1 request; record `Family = "model.legacy"`,
   `Site = "EG-MDL-03"` (the shared client records one site for MDL-04/05/06/08, `docs/EgressInventory.md:20`),
   `Destination = "http://203.0.113.9:8000"`, `ExternalModel`, `Internal`, `Access.Reason = SystemHighData`,
   `Mode = "report"`. The verifier should ask the integrator whether "never escalates past local" was meant to
   include video; if the lane adds the check, the twin flips to 0 requests.
4. Mutation (if added): remove it → request reappears.

#### 10. The bare `ProviderFactory` honours a caller's `openai` (remaining path; acknowledged)

1. With no load preference and trust off, `IProviderFactory` is the bare `ProviderFactory` (`Phases.cs:574-595`,
   path C). `ExecuteLLMAsync("openai", …)` honours the name (`ProviderFactory.cs:237-238` defaults only when null)
   → EG-MDL-03. `openai_compat` with `OPENAI_COMPAT_BASE_URL` is an arbitrary URL (`:217-218`).
2. Not in §2.7; guard backstop (`:613`). The lane body lists it under "What these do not cover".
3. Twin (documents): AG + bare factory + `OPENAI_API_KEY` + stub → 1 request to `https://api.openai.com`; record as
   in item 5's Full control but with `ModeBasis = "profile:air-gapped"`. Record this in the inventory's AG section.
4. No mutation; records check only.

#### 11. "Local" Ollama at a remote address is a network path the no-escalate rule calls local

1. The ollama provider dials `OLLAMA_BASE_URL` (EG-MDL-07 client, `ProviderFactory.cs:814` per the TSV `:59-60`);
   the NCR backend client dials `ASHLAR_OLLAMA_BASE_URL`, then `Ashlar:NodeCapabilityRuntime:Ollama:BaseUrl`, then
   `OLLAMA_BASE_URL` (`NodeCapabilityRuntimeServiceCollectionExtensions.cs:136-152`; EG-MDL-10,
   `docs/EgressInventory.md:76`); the MEAI client dials the resolved Ollama base URL (EG-MDL-01). With
   `OLLAMA_BASE_URL=http://10.0.0.5:11434`, "ollama" is a LAN host carrying every prompt.
2. §2.7 treats `ollama`/`local` as local by name (`:600`). The guard classifies a non-loopback Ollama as
   `ExternalModel` (`EgressDestinations.cs:81-82`, `:190-197`), so it is the backstop after 4.11, and D40 (`:838`)
   records the loopback-relay limit.
3. Twin (documents, cert-gate-safe with a seam): AG + a non-loopback Ollama base URL + stub handler → 1 request;
   record `Destination = "http://203.0.113.5:11434"`, `ExternalModel`, `Access.Reason = SystemHighData`. The PR body's
   "never escalates to a cloud model" wording should not read as "never leaves the host".
4. No mutation; wording and records check.

### B. The four AG boot validators and the ollama.com default

#### 12. `BrickHost:RemoteCatalogBaseUrls` on AG (federated remote bricks, EG-EXE-01/02)

1. `AddAshlarFederatedBrickMesh(configuration)` runs under `IncludeAdaptation` (`Phases.cs:216-225`; AG `true`,
   `Deployment.cs:119`). It binds `BrickHostOptions` from the section `BrickHost` (`AshlarFederatedBrickMeshServiceCollectionExtensions.cs:30`;
   `BrickHostOptions.cs:9`, `:12` default `[]`) and replaces `IBrickRegistry` with a factory that, when the trimmed,
   non-blank URL list is non-empty (`:35-39`, `:53-54`), builds an `HttpRemoteBrickCatalog` per URL on the unnamed
   factory client (`:66-77`) and a `CompositeBrickRegistry` (`:79`). The first `GetAllBricks` (API `GET /api/bricks`,
   `AshlarEndpoints.cs:185`, `:795`) fetches `GET /api/bricks` from each remote (`HttpRemoteBrickCatalog.cs:44-66`),
   and a resolved `RemoteBrick` POSTs execution to `entry.HostBaseUrl` (`CompositeBrickRegistry.cs:55`, `:93`;
   EG-EXE-01 `docs/EgressInventory.md:111`: "A catalog can steer execution to any host"). The kernel's
   `IConfiguration` is environment variables only (`AshlarServiceCollectionExtensions.cs:141-143`), so the key is
   `BrickHost__RemoteCatalogBaseUrls__0`; the API's `appsettings.json` is never consulted for it (`Program.cs:224-229`
   passes no configuration to `AddAshlar`).
2. DESIGN `:606-607`: an `IValidateOptions<BrickHostOptions>`-style validator on the `ValidateOnStart` pattern refuses
   a non-empty list under AG.
3. Twin: AG + env (or the kernel's configuration seam if the lane added one) `BrickHost:RemoteCatalogBaseUrls:0 =
   http://203.0.113.7:8080` → `IHost.StartAsync` throws `OptionsValidationException` naming `BrickHost` and
   `RemoteCatalogBaseUrls`; in a host-less composition `IOptions<BrickHostOptions>.Value` throws at first read; 0
   requests to the catalog stub. Full control: boots, and `GetAllBricks` makes 1 request; record
   `Site = "factory:"` (the unnamed client, `EgressServiceCollectionExtensions.cs:40`), `Destination =
   "http://203.0.113.7:8080"`, `NetworkExport`, `Access.Reason = SystemHighData`. Edge rows: (a) a whitespace-only
   entry (the mesh filters it at `:35-39`; the validator may refuse or allow it, but the twin must pin which and the
   choice must never be "allow a non-blank URL"); (b) `services.Configure<BrickHostOptions>(o => o.RemoteCatalogBaseUrls
   = ["http://…"])` registered **after** `AddAshlar` (and `AddAshlarBrickHostOptions(configure)`,
   `BrickHostServiceCollectionExtensions.cs:16-23`) is still refused, because the options pipeline merges it; (c) a
   `null` list set by a host does not NRE the validator; (d) `IOptions<BrickHostOptions>` replaced by `Options.Create`
   after `AddAshlar` is **not** validated: pin it as a known limit (item 3 has the same shape).
4. Mutation: validate `UseAuth` instead of the list, or test `is null` instead of `Count > 0` → the boot twin boots
   and the catalog stub sees a request → red.

#### 13. `Ashlar:RunPod:EnablePeerNetworkRouting=true` on AG

Covered in item 3. Add the first-resolution rows: `NcrCapabilityRouter` ctor (`:40-42`), `PeerCapabilitySnapshotPoller`
ctor (`:30-38`), `AshlarPeerBrickExecutor` ctor (`:34-44`) and `RunPodHttpClient.CreateRequest` (`:199-201`) all read
`.Value`; the lane body reports its D5 twin first failed inside `RunPodCapabilityRoutingServiceCollectionExtensions.cs:64`
(`GetServices<IHostedService>().OfType<NCRCapabilityPoller>().First()`), which is where a validator exception
surfaces when the pollers are built. Make sure the exception that reaches the operator is the
`OptionsValidationException` text, not a DI wrapper that hides it (the twin should assert the message, not the
type). Design citation `DESIGN :608` names `RunPodBrickConfig.cs:24`, which is `SectionName`; the property is at
`:53`.

#### 14. `Ashlar:MeshLab:WorkerExecutor:Enabled=true` on AG (EG-EXE-05)

1. `AddAshlarMeshLabWorkerExecutor(configuration)` runs on every profile (`Phases.cs:857`). It always binds the
   options (`MeshLabServiceCollectionExtensions.cs:27-28`; section `Ashlar:MeshLab:WorkerExecutor`,
   `MeshLabWorkerExecutorOptions.cs:9`), reads `…:Enabled` from configuration at registration (`:30-32`) and only then
   registers the named client, the guard call and the hosted service (`:36-40`). The background service re-reads
   `IOptionsMonitor<T>.CurrentValue.Enabled` every loop (`MeshLabWorkerExecutorBackgroundService.cs:27-36`) and the
   client GETs `{DirectorBaseUrl}/api/mesh/tasks` with `X-Ashlar-Api-Key` (`MeshLabWorkerExecutorClient.cs:219`,
   `:226-241`); `DirectorBaseUrl` defaults to `http://127.0.0.1:18081` (`MeshLabWorkerExecutorOptions.cs:15`) but is
   any URL.
2. DESIGN `:608`.
3. Twin: AG + `Ashlar__MeshLab__WorkerExecutor__Enabled=true` → start throws naming the setting; the named client
   (`MeshLabWorkerExecutorClient.HttpClientName`) stub records 0 requests. Full control: hosted service registered,
   first poll makes 1 request; record `Site = "factory:" + HttpClientName`, `Destination` the director,
   `NetworkExport`. Also pin: AG + `Enabled=false` boots and registers no `MeshLabWorkerExecutorBackgroundService`
   (the disabled path is unchanged, `:33-34`). Also: a host that sets `Enabled` through `Configure<T>` after
   `AddAshlar` while the env says false — the hosted service is not registered (registration-time check), and the
   validator still refuses at start (options pipeline): pin both so a later refactor that moves the `Enabled` check
   into the hosted service does not open a gap.
4. Mutation: validate `ExecuteBrickOnAssignedPeer` instead of `Enabled` → boots → red.

#### 15. `Ashlar:Meai:Bedrock:Enabled=true` on AG — the `ValidateOnStart` pattern cannot bind to it, and host-less processes skip the start

1. `AddAshlarMeaiPipeline(configuration)` (`Phases.cs:441`; MEAI defaults on, `MeaiPipelineServiceCollectionExtensions.cs:69-70`)
   builds a `MeaiPipelineOptions` by hand (`:86-92`; `Bedrock:Enabled` read at `:308-314`), adds the three Bedrock
   keys to `AllowedCloudTargets` (`:93`, `:258-276`), **registers it as a pre-built instance**
   `services.AddSingleton(Options.Create(options))` (`:94`), and when `Bedrock.Enabled` registers the three keyed
   `cloud:bedrock:*` chat clients (`:115-118`, `:216-256`) over `AwsBedrockChatClientFactory` →
   `AmazonBedrockRuntimeClient` (`AwsBedrockChatClientFactory.cs:37-40`; EG-MDL-02). `LocalFirstChatRouter` names the
   three Bedrock keys as candidates (`LocalFirstChatRouter.cs:99-101`, from grep), and the default access policy
   allows whatever is in `AllowedCloudTargets` (`:185-200`).
2. DESIGN `:606`, `:609-610`: "They bind to the options types … on the `ValidateOnStart` pattern … `Ashlar:Meai:Bedrock:Enabled=true`
   … protects AG from the later defect 4 fix." `IValidateOptions<MeaiPipelineOptions>` and `ValidateOnStart` act
   through `OptionsFactory`/`OptionsManager`; a descriptor whose implementation is an `Options.Create` instance never
   goes through them. The lane acknowledges this (body: "a start-time validator that reads every registered
   `IOptions<MeaiPipelineOptions>` instance").
3. Twin A (host start): AG + `Ashlar__Meai__Bedrock__Enabled=true` + `Region` → `StartAsync` throws naming
   `Ashlar:Meai:Bedrock:Enabled`; the keyed `cloud:bedrock:balanced` client is never created (register a stub
   `IBedrockChatClientFactory` **before** `AddAshlar`: `TryAddSingleton` at `:98` keeps it; assert 0 `Create` calls).
   Twin B (a second registration): after `AddAshlar(AirGapped)` a host calls
   `services.AddAshlarMeaiPipeline(cfg, configure: o => o.Bedrock.Enabled = true)` — a second `IOptions` instance
   (last wins) and a second Bedrock tier → start must still throw. Twin C (host-less, **this is the hole**): the same
   composition with `BuildServiceProvider()` and **no** `StartAsync` — exactly how the `ashlar` CLI runs
   (`application/src/Ashlar.CLI/Program.cs:56-73` builds the host lazily and uses `Host.Value.Services` at `:76`
   without starting it) — then resolve the keyed `IChatClient` for `cloud:bedrock:balanced` or send through the
   router with the local targets unavailable: the stub factory is reached and, with real clients, the MEAI route
   records `Family = "model.meai"`, `Site = "EG-MDL-02"`, `Destination = "https://bedrock-runtime.<region>.amazonaws.com"`
   (`MeaiEgressDestination.cs:97-104`), `ExternalModel`, `Access.Reason = SystemHighData`, `Mode = "report"`. Expected
   after the PR as designed: Twin C still reaches Bedrock, because nothing runs at start. The verifier should ask
   the integrator for a composition-time refusal (inside `AddAshlar`, which has the AG profile and the configuration
   in hand at `AshlarServiceCollectionExtensions.cs:117-145`) or a refusal inside the keyed client factory on AG; either
   makes Twin C green and is mutation-checkable.
4. Mutation for A/B: have the start-time check read only `GetService<IOptions<MeaiPipelineOptions>>()` (the last
   descriptor) → Twin B variant where the host's second registration comes **first** and `AddAshlar`'s comes last
   still passes, but a variant with the Bedrock-enabled instance registered before `AddAshlar` goes red; so Twin B
   needs both orders. Mutation for C (if a composition-time refusal is added): remove it → the stub factory sees a
   `Create`.

   SW note: the four validators are AG-only (`DESIGN :606` "Each refuses under AG"). On SW, Bedrock enabled is a
   cloud model from a SecureWorkstation with no refusal until 4.11. The PR body should say so.

#### 16. The ollama.com catalog default on AG (EG-MDL-13)

1. `OllamaRemoteLibraryCatalogOptions.Enabled` defaults `true` (`OllamaRemoteLibraryCatalogOptions.cs:13`); the source
   reports available (`OllamaRemoteLibraryModelArtifactCatalogSource.cs:40-41`) and `ListAsync` GETs
   `https://ollama.com/api/tags` on the named client (`:44-52`; `ModelArtifactCatalogServiceCollectionExtensions.cs:50-56`,
   registered under NCR on AG via `AshlarServiceCollectionExtensions.NodeCapabilityRuntime.cs:47`). The inventory marks
   it dormant (no caller of `ListInstallableAsync`, `docs/EgressInventory.md:79`), which is why a default, not a
   refusal, is the design's choice.
2. DESIGN `:613`: "`Enabled` defaults to false on AG"; D35.
3. Twin: AG → `IOptionsMonitor<OllamaRemoteLibraryCatalogOptions>.CurrentValue.Enabled == false` and
   `IsAvailableAsync == false`, `ListAsync` returns `[]` with 0 requests at a stub on `HttpClientName`; AG + explicit
   `Ashlar:ModelArtifactCatalog:OllamaRemoteLibrary:Enabled=true` → `true` (it is a default); Full → `true`. Ordering
   row: a host's `Configure<T>(o => o.Enabled = true)` registered **before** `AddAshlar` still ends `true` (the lane
   inserts its default at index 0 so it runs first; if the lane used `PostConfigure` instead, this row flips to
   `false` and the body's "an explicit `Enabled=true` still turns it on" is false): pin whichever shipped.
4. Mutation: remove the inserted `IConfigureOptions` → AG reads `true` → red.

### C. How the profile reaches Infrastructure (D5, the options value, the later `AddAshlar`)

#### 17. A composition that never calls `AddAshlar` gets no profile at all (by design; must be written down)

1. `AddRunPodCapabilityRouting(configuration)` is public (`RunPodCapabilityRoutingServiceCollectionExtensions.cs:28`),
   as are `AddLoadPolicy` (`AdaptiveProviderFactoryExtensions.cs:19-23`, documented "for a custom host … without
   AddAshlar") and `AdaptiveProviderFactory`'s constructor (`AdaptiveProviderFactory.cs:19-27`). Under
   `ASHLAR_DEPLOYMENT_PROFILE=air-gapped` such a composition has no `ResolvedDeploymentProfile` (the lane's type) so
   the router and factory take their optional parameter as `null` and route exactly as today (items 1, 5).
2. DESIGN `:614-616`: the profile reaches Infrastructure only as a value Hosting registers; "Infrastructure never
   reads `Effective` directly." So this gap is the design's intent. Only `EgressGuard.ProcessDefault`, which reads the
   environment per decision (`EgressGuard.cs:177-178`), sees AG there.
3. Twin (documents): `AddRunPodCapabilityRouting` + stubs, env `air-gapped`, overnight → `Remote`; the record from
   the RunPod stub carries `Profile = "air-gapped"`, `ModeBasis = "profile:air-gapped"` (ProcessDefault read the
   variable) while the routing ignored it. Make the PR body and `docs/EgressInventory.md` say the hygiene is an
   `AddAshlar` property.
4. No mutation.

#### 18. A later `AddAshlar()` (Full) in another collection keeps AG for the value but registers Full's module set (carry-in)

1. `NoteResolved` keeps AG (`AshlarDeploymentProfileEnvironment.cs:17-31`); `BindComposedEgressGuard` composes the
   strictest (`AshlarServiceCollectionExtensions.Egress.cs:47-49`); the lane builds `ResolvedDeploymentProfile` from
   the strictest too (body). But `GetModuleSelection(deploymentProfile)` uses the **call's own** profile
   (`AshlarServiceCollectionExtensions.cs:117`, `:132`), so the second container gets Full's modules: runtime
   transport (`Phases.cs:169-175`), background agents and web search (`:286-300`), workflow webhooks (`:652-660`;
   EG-HTTP-01), and the `AshlarExecution` client when `ASHLAR_EXECUTION_REMOTE_URL` is set (`:772-787`; EG-EXE-04).
   Handoff §4 carry-in: "a later `AddAshlar` still selects its own module set (4.11 territory)".
2. D5 (`:803`) is about the value, not the modules; §2.1 `:116-119` describes the strictest-wins note.
3. Twin (the lane's "D5 on another collection" twin, plus the gap row): `AddAshlar(AirGapped)` on A, then
   `AddAshlar()` on B → B's `ResolvedDeploymentProfile.IsAirGapped == true`, B's `ICapabilityRouter` routes Local with
   the AG prefix, **and** B resolves `IWorkflowWebhookClient` and `GrpcAgentTransport` (pin the gap so 4.11 inherits
   it as a named fact). Record: B's composed guard stamps `ModeBasis = "profile:air-gapped"`, `Profile = "air-gapped"`.
4. Mutation: build the value from the call's own profile → B routes `Remote` → red.

#### 19. Reverse order: a container composed Full before AG was noted keeps Full (accepted; pin it)

1. Container A composed by `AddAshlar()` before any AG note holds a Full value and a guard composed `full`
   (`Egress.cs:47-49`); a later `AddAshlar(AirGapped)` elsewhere changes neither (`:30-32` of the same file's remarks;
   lane body deviation 4). `EgressGuard.ProcessDefault`, used by the explicit sites, flips to AG per decision
   (`EgressGuard.cs:177-178`), so in such a process the factory-client records say `profile:full` and the explicit-site
   records say `profile:air-gapped` at the same moment.
2. Design: the value is "registered from the resolved profile" (`:614-615`); nothing says it tracks later notes.
3. Twin: A (Full) then B (AG) → A routes `Remote` and its guard records `Profile = "full"`; the operator-facing
   consequence (disagreeing records in one process) belongs in the Known limits.
4. No mutation; records.

#### 20. A host replaces the profile value after `AddAshlar` (last wins)

1. Any singleton (or `IOptions<T>` the design asked for) registered after `AddAshlar` wins DI's last-registration
   rule. The lane names it a known limit.
2. D6 (`:804`) forbids a host guard lowering the **mode** on AG; nothing in the design forbids a host lowering the
   **routing profile**, and the type is in Infrastructure, outside `AddAshlar`'s control.
3. Twin: `AddAshlar(AirGapped)` then `services.AddSingleton(new ResolvedDeploymentProfile(Full))` (or whatever the
   public construction is) → `Remote`; pin it, and make `docs/EgressInventory.md` Known limits carry it next to D40's
   "AG is as strong as its profile source". If the lane gave the type an internal constructor so only Hosting can
   make one, the twin changes to "a host cannot construct it" and the limit shrinks to "a host can unregister it".
4. No mutation; records.

### D. Inbound surfaces (Q6)

#### 21. The API's Kestrel listeners come from configuration nothing checks

1. `WebApplication.CreateBuilder(args)` (`application/src/Ashlar.API/Program.cs:90`) binds whatever `urls`
   (`ASPNETCORE_URLS`, `DOTNET_URLS`, `--urls`), `Kestrel:Endpoints:*:Url`, or `ASPNETCORE_HTTP_PORTS` /
   `HTTPS_PORTS` say; nothing between `:90` and `app.Run()` (`:419`) looks at them. The shipped images and compose
   files bind every interface: `.docker/Dockerfile.api:16` `ASPNETCORE_URLS=http://+:8080`; `deploy/compose/*.yml`
   `http://+:8080`; the aspnet base image sets `ASPNETCORE_HTTP_PORTS=8080` (`tests/uat/tier0-2.sh:120-123`,
   `tests/uat/README.md:89`) **[unverified beyond those comments]**. `AshlarSecurityOptions.ExposureProfile` "does
   NOT enforce network policy" (`Program.cs:20-23`).
2. DESIGN `:621-623`: "On AG and SW, every Ashlar inbound listener binds loopback, or boot fails … the API's Kestrel
   URLs". Q6 (`:1167`; SPEC-007 `:422`).
3. Twin, part 1 (cert-gate, pure): the lane's `LoopbackListenerPolicy.ConfiguredAddresses(IConfiguration, …)` and
   `IsLoopback` over a table. Non-loopback rows that must refuse: `http://0.0.0.0:0`, `http://+:0`, `http://*:0`,
   `http://[::]:0`, `https://0.0.0.0:0`, `http://ashlar.internal:0` (Kestrel binds a DNS name to every interface
   **[needs a test]**), **`http://api.localhost:0`** (Kestrel treats any host other than exactly `localhost` as
   every interface **[needs a test]**, while `EgressDestinations.IsLoopbackHost` calls `*.localhost` loopback at
   `:148-151`: if the lane reused that predicate, this row passes pre-bind and binds the LAN), `http://localhost.:0`
   (trailing dot; decide and pin), `http://127.0.0.1:0;http://0.0.0.0:0` (a list with one bad address refuses),
   `http_ports=8080` with `urls` unset, `https_ports=8443`, `Kestrel:Endpoints:Lan:Url=http://0.0.0.0:0` with a
   loopback `urls` (over-refuse, body deviation 6). Loopback rows that must pass: `http://127.0.0.1:0`,
   `http://[::1]:0`, `http://localhost:0`, `http://unix:/tmp/a.sock`, `http://pipe:/x` if named pipes are accepted.
   Part 2 (net10 VirtualProduction, real Kestrel): AG and SW + `urls=http://0.0.0.0:0` → `StartAsync` throws with the
   explained message before anything is bound (assert no socket on the port family, or that the exception type is
   the lane's and not a Kestrel bind error); `urls=http://127.0.0.1:0` starts; Full + `0.0.0.0` starts. Part 3
   (post-bind): a test host that calls `builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(0))` after `AddAshlar`
   → `IServerAddressesFeature.Addresses` shows `http://[::]:<port>` **[needs a test]** → `StartedAsync` throws →
   `Run` throws and the host disposes (socket closed) **[needs a test]**. Pin the accepted window: between Kestrel's
   `StartAsync` and the verifier's `StartedAsync` the listener answers; the body must say so.
   Part 4 (boot order) **[needs a test]**: `IStartupValidator` runs inside `Host.StartAsync` before any
   `IHostedService` on net8/net10, so the AG validators and the MCP validator fail before `GenericWebHostService`
   binds; twin: a validator failure plus a recording hosted service → the hosted service never started.
4. Mutations: (a) `IsLoopback("0.0.0.0") == true` → the table row and part 2 go red; (b) drop `http_ports` handling →
   the `http_ports` row goes red and the real host binds `:8080` under the devtest image's env; (c) remove the
   pre-bind call from `Program.cs` → **which cert-gate assertion goes red?** If none (the table tests the policy, not
   its call), that is an evidence gap: add a `Certification` test that reads `application/src/Ashlar.API/Program.cs`
   and pins the call (the `EgressGuardConventionTests` shape), or accept that the Q6 behaviour is guarded only by
   diff-conditional lanes (item 29).

#### 22. MCP over HTTP is allowed on SW today; the refusal needs a transport marker the validator cannot see by itself

1. `Program.cs:156` `AddAshlarMcpServer(builder.Configuration).WithHttpTransport()` unconditionally; `:395`
   `MapAshlarMcpEndpoint` maps `/api/mcp` when `Enabled` (`AshlarMcpEndpointRouteBuilderExtensions.cs:31-40`);
   `repo.fs.read`/`repo.fs.list` are registered for allow-listing (`:167-168`). The validator refuses AG only
   (`ValidateAshlarMcpServerOptions.cs:29-36`) and is an `IValidateOptions<T>` with no service provider; it is
   registered `TryAddEnumerable` (`AshlarMcpServerServiceCollectionExtensions.cs:39-40`) and `ValidateOnStart`
   (`:36-38`). The intent "Local MCP server (IDE stdio) stays allowed on SecureWorkstation" is written at
   `AshlarDeploymentProfileEnvironment.cs:75-77`; EG-SRV-01 (`docs/EgressInventory.md:183`) records the contradiction.
   The stdio host enables the server by default and calls `AddAshlar()` (`src/Ashlar.Mcp.Server.Host/Program.cs:26-32`,
   `:39`, `:47`).
2. DESIGN `:618-620`: "MCP over HTTP fails boot on SW. The check is a marker service that `.WithHttpTransport()`
   registers; stdio stays." Owner Q6 (`:1167`). The marker type the lane queries through `IServiceProviderIsService`
   is `ModelContextProtocol.AspNetCore.StreamableHttpHandler` (ModelContextProtocol.AspNetCore 2.2.0,
   `Directory.Packages.props:232`) **[unverified: the package source is not in the repository]**.
3. Twins: SW + `Ashlar:Mcp:Server:Enabled=true` + `.WithHttpTransport()` → start throws naming SecureWorkstation and
   HTTP; SW + `Enabled=true` + `.WithStdioServerTransport()` → boots; AG + either → refused (existing behaviour, the
   pre-existing `AirGappedProfileApiHostProdStyleTests.cs:71-88` already pins the AG API refusal); Full + HTTP →
   boots. Fail-closed rows: SW + `Enabled=true` + **no** transport at all → refused (the lane says "cannot tell →
   assumes HTTP"); SW + stdio under a container without `IServiceProviderIsService` → refused (parameterless ctor
   assumes HTTP) and the body says so. Attack row (host code, documents a limit): SW + `Enabled=false` +
   `.WithHttpTransport()` + the host calling `app.MapMcp("/mcp")` directly instead of `MapAshlarMcpEndpoint`: the
   validator returns success at `ValidateAshlarMcpServerOptions.cs:18-22` before any transport check, and the
   Ashlar list/call handlers are registered unconditionally (`AshlarMcpServerServiceCollectionExtensions.cs:64-73`),
   so MCP over HTTP answers on SW with the catalog `ExposedToolIds` allows (empty by default). Only item 21's
   loopback rule stands between that and the network. Pin it as a limit; the fix cannot be "check the marker when
   `Enabled=false`" because the API registers HTTP unconditionally and SW must boot with MCP off.
4. Mutations: (a) check `IsAirGapped` instead of `IsSecureWorkstation || IsAirGapped` → the SW HTTP twin boots →
   red; (b) query the wrong marker type → with the lane's fail-closed default the stdio twin goes red (good: that is
   the designed signal), and the verifier must see it go red, not just read that it would; (c) make the
   `IServiceProviderIsService` ctor return "stdio" when the marker is absent **and** the marker name is wrong → the
   HTTP twin boots → red.

#### 23. Mesh serve listens on every interface; the lane refuses to serve instead of failing boot (deviation)

1. `MeshServeService.BuildApp` → `k.ListenAnyIP(_settings.Port, …)` (`MeshServeService.cs:229`), registered by the
   daemon when `ASHLAR_MESH_SERVE_PORT` is set (`BackgroundAgentDaemonCommand.cs:415-439`); the daemon composes
   `AddAshlar` on the same collection (`:218-224`). `ExecuteAsync` already refuses to serve on a half-configured TLS
   setup and keeps the daemon up (`:149-156`), and a failed bind "never takes the daemon down" (`:164-170`, class
   remarks `:113`). The served bytes are EG-MESH-03 (`:313`, `:316`; `docs/EgressInventory.md:138`).
2. DESIGN `:621-622`: "every Ashlar inbound listener binds loopback, or boot fails … mesh serve, which uses
   `ListenAnyIP`"; Q6: "mesh serve must bind loopback". There is no bind-address setting on `MeshServeSettings`
   (`:18-24`). The lane's deviation 1: log at Error, bind nothing, daemon continues.
3. Twin (Tests.CLI, net10): AG and SW → `MeshServeService.ProfileError(settings, profile)` non-null, `StartAsync`
   binds nothing (a `TcpClient` connect to the port is refused), the log carries the explained refusal at Error;
   Full → serves `/mesh/v1/hello`. Also: with `ASHLAR_MESH_DISCOVERY=1` the beacon still announces `serving :<port>`
   (`BackgroundAgentDaemonCommand.cs:401-402` → `MeshDiscoverySettings.ServePort`; `MeshDiscoveryService.cs:255-259`)
   for a port nothing serves: harmless, but pin or fix. The verifier should put the deviation to the integrator: the
   owner's words were "bind loopback or boot fails"; the lane neither binds loopback nor fails boot. Binding loopback
   is one line (`ListenLocalhost`), and "fail boot" is a throw in `TryAddMeshServe`. The class contract (`:113`,
   `:164-170`) is the only reason to prefer the refusal; the owner did not write that contract into Q6.
4. Mutation: `ProfileError` returns `null` on AG → the bind happens → the connect succeeds → red.

#### 24. The mesh discovery UDP listener binds `0.0.0.0:7421` on AG and SW (not covered)

1. `MeshDiscoveryService.ExecuteAsync` binds `new IPEndPoint(IPAddress.Any, 7421)` and joins the multicast group
   (`MeshDiscoveryService.cs:186-199`, bind at `:191-194`) when `ASHLAR_MESH_DISCOVERY=1`
   (`BackgroundAgentDaemonCommand.cs:388-408`). Strangers' beacons are written into `mesh-peers.json` and fed to
   auto-pull (`:237-239`; `MulticastPeerSource`, `:407`). The listener is `Exempt:Inbound` in the TSV (`:13`).
2. DESIGN `:621` says **every** Ashlar inbound listener; owner Q6 names the API and mesh serve. The lane: not
   covered (body, deviation 7).
3. Twin (documents): AG + discovery on → the socket is bound on every interface **[needs a test on a box that allows
   UDP multicast]**. The verifier should raise it as an owner question, because the design and the answer differ in
   scope: is a UDP presence listener an "inbound listener" under Q6?
4. No mutation unless the lane closes it.

#### 25. The gRPC server host honours `urls` and relays to any endpoint (not covered)

1. `src/Ashlar.Transport.Grpc.Server.Host/Program.cs:14-17` defaults to `http://127.0.0.1:5001` only when `urls` is
   unset; `docs/GrpcHost.md:14` tells operators to widen to `0.0.0.0` in containers; it composes `AddAshlar()` (`:27`)
   so it is an Ashlar host that can be AG. `AgentTransportServiceImpl` re-sends an inbound `Invoke` to whatever
   `TargetEndpoint` the caller names (`:165-176`; EG-XPT-05 `docs/EgressInventory.md:103`; EG-SRV-04 `:186`).
2. DESIGN `:621` "every Ashlar inbound listener"; the lane: not covered (body, deviation 7); the open relay itself is
   out of PR 4 (DESIGN `:1034-1053`, handoff §7 "Defect 1").
3. Twin (documents) or, if the integrator agrees, reuse of the lane's `LoopbackListenerPolicy` in that `Program.cs`
   with the same twin shape as item 21. Note the commercial Fleet host also composes `AddAshlar`
   (`commercial/src/Ashlar.Commercial.Fleet.Host/Program.cs:121`) behind `ASPNETCORE_URLS=http://+:8080`
   (`.docker/Dockerfile.fleet-host:17`); out of the open-source scope but the same shape.
4. No mutation unless closed.

### E. SecureWorkstation is outside defect 5 (design narrower than the problem)

#### 26. On SW, RunPod is still the default remote target and the factory still escalates

1. SW has `IncludeNodeCapabilityRuntime: true` (`Deployment.cs:140`), so items 1–11 apply to SW word for word; the
   inventory already said so (`docs/EgressInventory.md:75` "**AG, SW**").
2. DESIGN §2.7 scopes the routing fix, the no-escalate and the four validators to AG (`:589`, `:599`, `:606`); SW
   appears only in the inbound paragraph (`:618-623`). §1 (`:67-72`) lists cloud models and RunPod among what
   enforcement must refuse on both profiles, so the design relies on 4.11 for SW.
3. Twin: `AddAshlar(SecureWorkstation)` + overnight → **`Remote`** (the design's expectation), and the RunPod stub
   sees 1 request with `Profile = "secure-workstation"`, `ModeBasis = "profile:secure-workstation"`,
   `Access.Reason = SystemHighData`, `Refused = false`. Pin it so the PR body cannot say "SecureWorkstation hygiene"
   means local routing, and so 4.11's leak test knows SW reaches `api.runpod.io` by default until the switch.
4. No mutation; wording and records. If the integrator extends defect 5 to SW (it costs one predicate), the twin
   flips and item 1's mutations apply.

### F. Test hygiene and evidence placement

#### 27. D41 for the new twin files

1. The convention test scans every test project (`ProcessGlobalEnvironmentConventionTests.cs:413-420`) for the compose
   marker plus `AshlarDeploymentProfile.AirGapped`/`SecureWorkstation` (`:88-96`, `:357-366`) and requires
   `[Collection(…)]` on a serializing collection (`:195-214`) and a restore through the seam (`:265-290`).
2. DESIGN §2.10 (`:680-697`), D41 (`:839`).
3. Check: the new cert-gate class, the net10 VirtualProduction class (the pre-existing
   `AirGappedProfileApiHostProdStyleTests.cs:27-36` already qualifies) and the Tests.CLI mesh-serve class carry
   `[Collection("EnvironmentVariables")]` and dispose an `EgressProcessStateScope`. A twin that constructs
   `new NcrCapabilityRouter(…, profile)` or `new MeshServeService(…, profile)` directly notes nothing and may run in
   parallel; a twin that composes `AddAshlar(AirGapped)` may not. A twin that sets `ASHLAR_DEPLOYMENT_PROFILE` through
   `EnvironmentVariableScope` (as `AirGappedProfileApiHostProdStyleTests.cs:55` does) must still be in the serialized
   collection; the convention catches the direct `SetEnvironmentVariable` marker, so check such files by hand.
4. Mutation: remove the `[Collection]` attribute from the new cert-gate class → `No_unlisted_test_file_mutates_the_environment_unserialized`
   (or the egress-state leak check) goes red; this proves the convention sees the new file.

#### 28. The required check sees only part of 4.10's evidence

1. The five required checks run tests only in `cert-gate`, whose filter is
   `FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Certification|…GenerationSafety|…AstMutationEngineTests` on
   net8.0 (`scripts/cert-gate-config.sh:6`; `scripts/run-cert-gate.sh:29-32`; `.github/workflows/README.md:44-45`).
   The lane's real-Kestrel listener twins live in `Tests/VirtualProduction` (net10; run by `application-gate.yml:9`
   and the readiness lanes only when the diff matches), the mesh-serve twin in `application/src/Ashlar.Tests.CLI`
   (`production-readiness-gate-v1.yml:17`, `security-gate.yml:29`, readiness paths `full-platform-readiness-gate.yml:69`,
   `:221`), and the MCP twins partly in `src/Ashlar.Mcp.Server.Tests` (readiness `:102`, `:251`). CLAUDE.md: a skipped
   lane counts as a pass.
2. DESIGN §4 (`:846-848`): "Readiness lanes are diff-conditional, so cert-gate tests are the evidence."
3. Check: for each Q6 behaviour, name the cert-gate assertion that fails if the production code regresses. Expected
   gaps: the real bind (`IServerAddressesFeature`), the real `StartedAsync` failure, and mesh serve's `ListenAnyIP`
   can only be exercised on net10 outside cert-gate. For those, the cert-gate must at least pin the **call sites**
   by reading the source (as `EgressGuardConventionTests` pins guards): `Program.cs` calls the pre-bind policy and
   registers the verifier; `MeshServeService.ExecuteAsync` calls `ProfileError` before `BuildApp`. Otherwise a
   later edit removes the check and nothing required goes red.
4. Mutation: delete the pre-bind call in `Program.cs` (or the `ProfileError` call) and run **only** the cert-gate
   filter: if it stays green, the body's "mutation-checked" claim for that behaviour rests on a non-required lane and
   must say so.

#### 29. The twin must not pass because of the container's environment

1. The devtest image sets `ASPNETCORE_HTTP_PORTS=8080` (lane body; `tests/uat/tier0-2.sh:120-123`); under
   `WebApplicationFactory` (TestServer, no Kestrel) the API twin reads `http_ports` from the process environment via
   `WebApplication.CreateBuilder`'s `ASPNETCORE_` prefix **[needs a test]**, so an "AG refuses a non-loopback
   listener" twin could be going red-then-green on the image's variable rather than on its own `UseSetting`.
2. §4 `:846` and CLAUDE.md: a check never observed failing is not evidence.
3. Check: the refusal twin sets `http_ports` (or `urls`) explicitly through `UseSetting`
   (`AirGappedProfileApiHostProdStyleTests.cs:38-50` shows the pattern) and the boot twin sets `urls` loopback
   explicitly; run both with `ASPNETCORE_HTTP_PORTS` unset and set, same result.
4. Mutation: run the twin with the variable unset in the container; if the refusal twin goes green-for-the-wrong-
   reason or the boot twin changes outcome, the twin is reading the environment.

### G. Remaining network paths registered on AG or SW after the PR as designed (guard backstop; each must be written down)

For completeness; none is closed by §2.7, and each becomes a 4.7/4.9/4.11 route. The verifier checks that the PR
body's "What these do not cover" and `docs/EgressInventory.md`'s AG section name them, with the right profile.

| # | Path | Where (master) | Profiles | Record a twin would see today |
|---|---|---|---|---|
| 30 | OTLP export when `OTEL_EXPORTER_OTLP_ENDPOINT` is set | `Program.cs:242-260`; decided once with `ProcessDefault` at `:247` (EG-TEL-01) and then the SDK exports | API on any profile | `Family = "telemetry"`, `Site = "EG-TEL-01"`, `NetworkExport`, `SystemHighData`; reaches the EventSource only (`docs/EgressInventory.md:19`) |
| 31 | Mesh auto-pull from `ASHLAR_MESH_PEERS`, LAN or tailnet peers | `MeshAutoPullService.cs:53-75` (EgressHttp client), registered `BackgroundAgentDaemonCommand.cs:234-237` (EG-MESH-04) | daemon, any profile | `Family = "mesh.pull"`, `Site = "EG-MESH-04"`, `NetworkExport` |
| 32 | Peer executor to file-discovered peers | item 3; EG-EXE-03 | AG refused by validator; **SW allowed** | `factory:`, `NetworkExport` |
| 33 | Workflow webhooks | `Phases.cs:652-660` (`IncludeWorkflowIntegrations`, SW `true` `Deployment.cs:149`); EG-HTTP-01 `docs/EgressInventory.md:124` | **SW** | `factory:`, `NetworkExport` |
| 34 | Web search (Bing) when configured | background agents module, SW `true` (`Deployment.cs:145`; `Phases.cs:286-300`) | **SW** | `Family = "web-search"`, `WebSearch`/`Confidential` |
| 35 | Cloud-availability probe to `api.openai.com` | `CloudAvailabilityResolver.cs:96-110`, trust module (SW), `ASHLAR_AIRGAP_PROBE=1` (EG-MDL-12 `docs/EgressInventory.md:78`) | **SW** | `Family = "http"`, `Site = "EG-MDL-12"` |
| 36 | SNS signing-certificate fetch, DynamoDB approval store | `Program.cs:140-145`, `:171-172`; EG-HTTP-03 `docs/EgressInventory.md:126` | API, when ingress is enabled | `factory:ashlar-sns-signing` |
| 37 | `ASHLAR_EXECUTION_REMOTE_URL` | `Phases.cs:772-787`, **inside** `IncludeTestingAdapters`, which is `false` on AG and SW (`Deployment.cs:126`, `:150`) | **not** AG/SW on a pure composition; only via item 18 | `factory:AshlarExecution` |
| 38 | `IExecutionContext.IsAirGapped` supplied on the wire | `AshlarEndpoints.cs:843` → `BrickCatalogWireMapper.ToExecutionContext`; forwarded, not derived from the profile (`docs/EgressInventory.md:219`); `BehaviorExecutor.cs:375` forcing; `CloudSanitizationProxy` skip [S1, `DESIGN :1049`] | API | n/a (it is an input, not an egress) |

Row 37 corrects the lane body, which lists `ASHLAR_EXECUTION_REMOTE_URL` as "not covered on AirGapped": on a pure
AG composition the client is never registered; the gap exists only through a later Full `AddAshlar` (item 18).

---

## 2. Design claims marked [needs a test] for this PR

Each is a runtime or library fact the design (or the lane's reading of it) relies on; none can be settled by reading
this repository. Appendix B of the design (`:1134-1154`) lists none of these, so they are new.

1. `IStartupValidator` runs in `Host.StartAsync` **before** any `IHostedService.StartAsync` on net8.0 and net10.0, so
   `ValidateOnStart` validators (the four AG opt-ins, the MCP validator, `AshlarMiddlewareIngressOptions`) fail before
   `GenericWebHostService` binds Kestrel. (On net6/7 the equivalent was a hosted service, and order would have
   mattered.)
2. Kestrel address parsing: `localhost` binds both loopbacks; an IP literal binds that IP; **any other host name,
   `*.localhost` included, binds every interface** (`AnyIPListenOptions`); `+` and `*` bind every interface. And
   `IServerAddressesFeature.Addresses` after `ListenAnyIP` shows `http://[::]:<port>` (or `http://0.0.0.0:<port>`
   where IPv6 is off), after `urls=http://localhost:<p>` shows the original string.
3. `WebApplication.CreateBuilder` maps `ASPNETCORE_URLS`, `DOTNET_URLS` and `--urls` to `IConfiguration["urls"]`, and
   `ASPNETCORE_HTTP_PORTS`/`HTTPS_PORTS` to `["http_ports"]`/`["https_ports"]`; `builder.WebHost.UseUrls(...)` lands
   in the same configuration key under the minimal-hosting builder.
4. An exception from `IHostedLifecycleService.StartedAsync` fails `Host.StartAsync`, `WebApplication.Run()` rethrows
   it, and the host is disposed on the way out so Kestrel's sockets close; the window between bind and that failure
   is the accepted limit.
5. `ModelContextProtocol.AspNetCore` 2.2.0 `WithHttpTransport()` registers a service of type
   `ModelContextProtocol.AspNetCore.StreamableHttpHandler` and `WithStdioServerTransport()` registers nothing of that
   type; `IServiceProviderIsService.IsService(typeof(…))` answers true only in the HTTP case.
6. `IServiceProviderIsService` is registered by the default `ServiceCollection.BuildServiceProvider` and by
   `WebApplicationFactory`'s container, and MS DI picks the validator's two-parameter constructor when it is.
7. The typed RunPod client's name, and so its site, is `factory:IRunPodClient` (`AddHttpClient<IRunPodClient,
   RunPodHttpClient>` names the client after `TClient`).
8. MS DI supplies an optional constructor parameter from the container when the type is registered and `null`
   otherwise (`ResolvedDeploymentProfile? profile = null` on `NcrCapabilityRouter`, `AdaptiveProviderFactory`,
   `MeshServeService`).
9. `IOptions<T>.Value` throws `OptionsValidationException` at the first read when an `IValidateOptions<T>` fails
   (OptionsManager path), and **never** for a descriptor whose implementation is an `Options.Create` instance
   (item 15).
10. xUnit 2.9.3 runs `DisableParallelization` collections after the parallel ones (handoff §6 asserts it; the D41
    reasoning depends on it).
11. A UDP bind on `0.0.0.0:7421` with multicast join succeeds in the devtest container (item 24's documenting twin).
12. The daemon's `Host.CreateDefaultBuilder()` host and the CLI's lazy host (`Program.cs:56-73`) never call
    `StartAsync` for ordinary verbs, so no `ValidateOnStart` validator runs there; only first-resolution
    `IValidateOptions` do.

---

## 3. Where the design is wrong or impossible against master

1. **§2.7 `:604` (multi-frame vision) is not a fix.** "The multi-frame path already uses `resolved` only" assumes
   `resolved` is local on AG. `PreferenceLoadPolicy.cs:52-64` returns `openai`/`azure` for `ASHLAR_LOAD_PREFERENCE=server`
   when their keys are set, and `AdaptiveProviderFactory.cs:114-120` then sends every frame to the cloud. Row 25
   (`:381`) is about masking a refusal, not about not sending. The lane's refusal is required, not optional (item 8).
2. **§2.7 `:600` (LLM) is under-specified for a cloud `resolved`.** "becomes `{ resolved }` when `resolved` is local"
   leaves `{ openai, ollama, local }` in place when it is not (`AdaptiveProviderFactory.cs:47-49`). The design's
   own sentence "never escalates past local on AG" (`:599`) needs the lane's stronger rule (item 6).
3. **§2.7 `:606` "on the `ValidateOnStart` pattern" cannot bind to `MeaiPipelineOptions`.** It is registered as a
   pre-built instance (`MeaiPipelineServiceCollectionExtensions.cs:94`), outside `OptionsFactory`; neither
   `IValidateOptions<T>` nor `ValidateOnStart` ever sees it. The lane's start-time check is the only shape the
   pattern allows, and it leaves host-less compositions open (item 15). A composition-time refusal inside `AddAshlar`
   would close both.
4. **`ValidateOnStart` protects hosts that start.** The `ashlar` CLI builds its host lazily and never starts it
   (`application/src/Ashlar.CLI/Program.cs:56-76`); many verbs build bare providers (`MeshCommand.cs:147-150` and the
   other `BuildServiceProvider()` sites). For three of the four opt-ins the first-resolution `IValidateOptions` path
   carries the refusal; the design does not say this, and the done-when "the opt-in validators fail boot" (`:860`)
   should read "fail boot, and fail the first resolution in a process with no boot".
5. **§2.7 `:621` "every Ashlar inbound listener" is wider than Q6 and than the lane.** The owner answered for the
   API and mesh serve (`:1167`; SPEC-007 `:422`). Master has two more Ashlar listeners that compose `AddAshlar`: the
   UDP discovery listener (`MeshDiscoveryService.cs:191-194`) and the gRPC server host
   (`Grpc.Server.Host/Program.cs:14-17`, `:27`). Either the design's sentence is a rule (then both are 4.10 scope) or
   it is a paraphrase of Q6 (then the sentence must be narrowed in the records). Ask.
6. **§2.7 `:622` "mesh serve … binds loopback, or boot fails" has no bind-address setting to act on**, and
   `MeshServeService`'s contract is that serving failures never take the daemon down (`:113`, `:164-170`). "Bind
   loopback" is implementable (`ListenLocalhost`), "boot fails" contradicts the class. The lane chose a third thing
   (refuse to serve). This is an owner-facing deviation, not an engineering default; it should be put to the owner,
   not recorded silently (handoff §8).
7. **`:597` and `:608` cite lines that moved or point at the wrong member.** `RunPodHttpClient.cs:203-205` is
   `:199-209` (`CreateRequest`, key optional at `:201-206`); `RunPodBrickConfig.cs:24` is `SectionName`, the property
   is `:53`. Harmless, but the PR body's "re-derived line numbers" should not inherit them.
8. **§4 row 4.10 "a non-loopback listener on AG or SW fails boot" is only half achievable pre-bind.** A listener
   added in code (`ConfigureKestrel(k => k.Listen…)`) is invisible to configuration; Kestrel's code-backed listen
   options are internal, so the only check is after the bind (item 21, part 3). The design should say "refuses a
   configured non-loopback address before binding, and fails the start after a non-loopback bind".
9. **§2.7 treats SecureWorkstation as inbound-only.** Items 26 and 32-35: SW keeps RunPod, peers (if enabled),
   webhooks, web search, and the cloud probe, and the four validators are AG-only. The design's §1 (`:67-72`) says
   enforcement must stop these on both profiles; §2.7 leaves SW to 4.11. Not wrong as a plan, but the done-when
   "Full is unchanged" (`:860`) should be joined by "SecureWorkstation's outbound paths are unchanged by 4.10", so
   the body cannot imply otherwise.
10. **`:614-616` "an options value" was not what shipped**, and the plain singleton is the better fail-closed shape
    (a `Configure<T>` could lower an `IOptions<T>`). Record as the lane's deviation 3; no design fix needed beyond
    the Known limit in item 20.
11. **`:613` "ollama.com `Enabled` defaults to false on AG" is a default, so `Enabled=true` still reaches ollama.com
    on AG.** That is the design's choice (the path is dormant), but the done-when "ollama.com is disabled" (`:860`)
    overstates it; "disabled by default" is what is true.
12. **The body's "not covered: `ASHLAR_EXECUTION_REMOTE_URL` on AirGapped" is false for a pure AG composition** (row
    37): the client is registered only under `IncludeTestingAdapters` (`Phases.cs:772`), which AG and SW exclude
    (`Deployment.cs:126`, `:150`). The real exposure is item 18.

---

## 4. Owner questions this lane will hit

- Q6 scope: do the UDP discovery listener and the gRPC server host count as "Ashlar inbound listeners" (design
  `:621`) or only the API and mesh serve (the answer as written)?
- Mesh serve: the owner said "bind loopback or boot fails"; the lane refuses to serve and keeps the daemon up. Which
  of the three does the owner want?
- Bedrock on AG in a process that never starts a host (the CLI): accept the gap until 4.11, or add a
  composition-time refusal in `AddAshlar`?
- Should defect 5 (local routing, no-escalate, the four validators) also apply to SecureWorkstation now, or stay
  AG-only until the switch?
- Video analysis (`VIDEO_SERVICE_URL`) under "never escalates past local": in or out of 4.10?
