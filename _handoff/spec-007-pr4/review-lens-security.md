# Review lens: write-down / security — master `095ba46e2` after #720, #722, #721, #723

Read-only against `scratchpad/review-master` (clean, at `095ba46e2`). Probes were built and run in my own clone
`scratchpad/review-security` (branch `review-security-probe`, two local commits on top of `095ba46e2`, **never pushed**);
logs in `scratchpad/review/logs-security/`. Every `file:line` below is at `095ba46e2` unless a commit is named.

Question: can labelled data leave without a correct decision, or can an AirGapped/SecureWorkstation host still reach a
non-loopback network or expose a non-loopback inbound surface, where the design and the owner's 2026-10-06 decisions say
it cannot?

Short answer: **yes, in four places the design or the owner closed and master leaves open, none recorded as a Known
limit**, and the three owner decisions of 2026-10-06 have no decisions-log row; one is contradicted by code, by the
records ("the owner can reverse it") and by a cert-gate test that pins the opposite rule by name.

---

## 0. Baseline on master (what every item below rests on)

- Nothing refuses. Every profile resolves `report` (`EgressEnforcement.cs:12`, `:26-38`); `EgressDecision.Refused` is
  false in report mode; the follower sends a would-refuse hop (integrator Q-A, INTEGRATION-NOTES-C). So every "OPEN"
  below is a *record* hole on Full and a *path* hole on AG/SW once 4.11 lands, except the inbound items, which are
  live now.
- 4.3 code: `EgressRedirectHandler.cs` (431 lines), `EgressGuardHandler.cs:115-142` post-send check,
  `EgressRedirectBindingFilter.cs`, `EgressHttp.Guarded` `:145-155`.
- 4.5 code: `ToolCallingAgent.cs:234-256`, `IEgressLabelledTool.cs`, `RAGTool.cs:115-159`, `SelfExtendRunnerAdapter.cs:248-252`,
  `EgressGuardChatClient.cs` peer observe, `CapabilityRegistry.Find`. **`EgressSubject.cs` and `ReadScope.cs` were not
  touched by #721** (`git show --stat 9b2ea56f4`): `ReadScope.Dispose` is the only observe point (`ReadScope.cs:24-25`,
  `:78-86`), `Resolve` walks frames only (`EgressSubject.cs:238-251`), `BeginRead() => new(Active.Value)` (`:120`).
- 4.10 code: `AshlarInboundListenerPolicy.cs` (Infrastructure), `MeshServeService.cs:154-166`, `:261-277`, `:313-316`,
  `NcrCapabilityRouter.cs`, `AdaptiveProviderFactory.cs`, four `ValidateAirGapped*Options`, `AshlarMcpHttpTransportMarker`.

---

## 1. The three owner decisions of 2026-10-06

| # | Decision | On master | Record |
|---|---|---|---|
| 1 | Open, unreported read scope ⇒ every egress on the flows inside it is decided at SystemHigh, every tool, RAGTool included; Scenario B reason → SystemHighData | **Contradicted.** `ReadScope`/`EgressSubject` unchanged; `Resolve` (`EgressSubject.cs:238-251`) never sees a scope. `EgressSubjectProducerTests.An_egress_during_the_tool_call_is_decided_at_the_pre_read_mark` (`:203-233`) asserts `mid.Current == Public` "an open scope observes only when it ends" — the opposite rule, green on master (probe run: see §5). | `docs/EgressInventory.md:28`, SPEC-007 `:139-142`, CHANGELOG (#721): "known limit … **The owner can reverse it**" — presents an already-taken decision as open. No decisions-log row (`grep 2026-10-06` in SPEC-007 finds only the 4.2 row at `:452`). |
| 2 | Q8/O1: custom `IDataSensitivityLevel` read through RAG is SystemHigh in PR 4 (D15 stands) | **Implemented.** `RAGTool.MapHitLabel` (`RAGTool.cs:139-156`): `GetByName` then `ReferenceEquals` against `DataSensitivityLevels.All`, else SystemHigh; `RAGTool_hit_labels_match_TrustTierOrder_RecordLabel` rows `SystemHigh`, `not-a-level`, `Pony`. | Inventory `:26` "A custom level, an unknown name, or an unmarked hit is `SystemHigh`". No dated decisions-log row. |
| 3 | O2: a hop from a non-Host authority to a Host authority (loopback, localhost/`*.localhost`, link-local, unix, npipe) is never followed, factory (P1) and `EgressHttp` (P2) alike; hop recorded, 3xx returned | **Absent.** `TryGetRedirect` (`EgressRedirectHandler.cs:163-191`) checks only scheme (`IsHttpOrHttps`, `IsHttpsToHttp`) and, for P2, `SameHost`. With `FollowCrossHost = true` (factory, `EgressRedirectBindingFilter.cs:42`) a remote 307 → `http://127.0.0.1:…` is followed and `ApplyRedirect` (`:193-204`) keeps method and body on 307/308. Probe §5 P1/P2. | Nothing: inventory `:15`, SPEC-007 `:100-104`, CHANGELOG, PR #722 body say "factory client follows a cross-host redirect and each hop is evaluated again" with no exception for the host boundary and no Known-limit line. No decisions-log row. The Claude lane had it: `origin/claude/spec-007-pr4-4.3-redirects` `EgressRedirectHandler.cs:342-345`, `:375-381`, with `EgressDestinations.IsInsideHost`/`IsLinkLocalHost`, twins and a decisions-log row (`state-4.3.md:75`, `:166`, `:196-197`). |

---

## 2. attack-4.3.md item by item

| Item | Status | Evidence |
|---|---|---|
| 1 factory 307 loopback→remote with body | **closed** | filter flips known primary and wraps it (`EgressRedirectBindingFilter.cs:40-50`); follower re-sends via `FollowAsync` (`EgressRedirectHandler.cs:72-91`) and `EvaluateHop` (`:139-153`) before each hop. Twin `Factory_cross_host_redirect_is_followed_and_re_evaluated` (`EgressRedirectTwinTests.cs:159`). |
| 2 P2 cross-host 3xx returned | **closed** | `_settings.FollowCrossHost == false` for `EgressHttp.Guarded` (`EgressHttp.cs:148`) → `TryGetRedirect` returns false on host mismatch (`:186-187`). Twins `EgressHttp_cross_host_redirect_is_not_followed` (`:93`), `Report_mode_P2_hop_2…` (`:113`). |
| 3 authority ≠ host (scheme/port) | **OPEN (P2 key), closed (re-eval)** | Re-evaluation is per hop whatever the authority (`EvaluateHop` always evaluates; `EgressEvaluatedAuthority.Authority` compares scheme+host+port `:362-399`). But the **P2 follow decision** is `SameHost` = `Uri.Host` only (`:224-225`): `http://h:80 → http://h:8080` and `http → https` on the same host are followed by an `EgressHttp` client. Design `:519` says authority. Probe §5 P3/P4. Not recorded (inventory `:15` says "same-host", which is literally what shipped, but the design row's "authority" is weakened silently). |
| 4 rewriting handler between guard and primary | **closed** | `EnsureEvaluated` compares the stamp (`:116-136`); twin `A_rewriter_between_guard_and_primary_is_evaluated_for_the_rewritten_host` (`:185`). |
| 5 cloning handler drops the stash | **closed** (fail closed) | no stamp ⇒ `Matches` false ⇒ evaluate (`:121-126`). No twin; code is unambiguous. |
| 6 composite primary (rewriter inside primary) | **closed by design, two shapes** | `EgressHttp`: `InsertAbovePrimary` walks to the tail and flips it (`:272-288`); factory: composite is "unknown" (`EgressRedirectBindingFilter.cs:40`, `:52-58`, warning), post-send check always on (`EgressGuardHandler.cs:115-142`). Twin `Unknown_primary_followed_authority_is_evaluated_after_the_send` (`:237`). |
| 7 unknown primary hiding the final URI | **recorded as design limit** | inventory `:15` "A primary type Ashlar cannot flip is not wrapped; if the response URI's authority differs … logs that the body may already have gone". |
| 8 started primary: setter throws | **OPEN, not recorded** | `ReadAndDisable` reads `allow = true`, the setter throws and is swallowed (`:345-349`), `Follow = true` is returned (`:355`). The primary keeps following internally (cross-host too, for an `EgressHttp` client: **P2 defeated**); only the post-send check records the final authority with a warning. Twin `A_started_handler_does_not_throw…` (`:278`) asserts only that `Wrap` does not throw. Design `:508-509` named this case; inventory Known limits (`:16-24`) have no sentence for it. No production caller passes a started handler (`MeshAutoPullService.cs:57`, `DefaultGrpcChannelFactory.cs:77` are fresh), so exposure is host code. |
| 9 subclass of `HttpClientHandler` | **closed** | `is HttpClientHandler` / `is SocketsHttpHandler` patterns (`:310`, `:315`, filter `:40`). The twins' `RedirectingStub : HttpClientHandler` is itself the subclass case. |
| 10 `Clear()` and `[0]` check | **closed** | presence scan + re-insert + move-to-front (`EgressRedirectBindingFilter.cs:60-93`); twin `Clear_still_has_one_guard_handler_and_exactly_one_decision` (`:210`). |
| 11 relative/missing/invalid `Location`, 300/304/305 | **closed** | `TryGetRedirect` resolves relative, keeps fragment (`:173-178`); `IsRedirect` = 300/301/302/303/307/308 (`:208-214`); missing header → no follow (`:169-171`); differential twins `:388`, `:409`. |
| 12 HTTPS→HTTP and non-http schemes | **closed** | `IsHttpOrHttps`, `IsHttpsToHttp` (`:180-184`, `:216-222`); `Differential_https_to_http_is_not_followed` (`:321`). |
| 13 method/body rewrite, credentials | **closed (parity) / record missing** | `ApplyRedirect` clears `Authorization` only (`:195`); `RequestRequiresForceGet` (`:227-240`); theory `:356-363`. The integrator's Known limit ("custom credential headers, the consumer SDK's `X-Ashlar-Api-Key`, follow an allowed cross-host hop") is **not in** `docs/EgressInventory.md` (grep `X-Ashlar-Api-Key` finds no Known-limit line). Probe P1 shows `X-Ashlar-Api-Key` arriving at hop 2. |
| 14 SNS never follows | **closed** | both hosts use the configure-existing overload (`application/src/Ashlar.API/Program.cs:172-179`, `commercial/…/Fleet.Host/Program.cs:79-86`); the filter runs post-`next` and reads `allow == false` → `Follow = false` (`EgressRedirectBindingFilter.cs:30-31`, `EgressRedirectHandler.cs:304-312`, `:355`). No convention fact pins the `(h, _)` form (attack 14(b)); `A_primary_that_already_refused_redirects_is_not_followed` (`:300`) covers the semantics only. |
| 15 SNS `SubscribeURL` | **closed** (same client) | as 14. |
| 16 P1 follows into Host | **OPEN — owner O2 contradicted** | §1 row 3; probe P1/P2. |
| 17 Bedrock | **closed per Q-C** | `AwsBedrockChatClientFactory.cs:40-41` sets `AllowAutoRedirect = false`; recorded inventory `:72`. |
| 18 host inner Ollama client | **records** | EG-MDL-01 row `:71` says "The default client does not follow redirects (PR 4.1)"; the host-inner-client seam is not named (minor). |
| 19 `ProviderFactory` P2 → "unavailable" escalation | **closed (P2) / record** | CHANGELOG names `EgressHttp` clients generally; the EG-MDL-07 row does not say a redirecting proxy now reads as unavailable (minor). |
| 20 ns2.0 hop | **closed** | `Properties` stash under `#else` (`:412-420`); hop stays `SynchronousSendRefusedOnNetstandard20Asset` (`EgressHttp.cs:149-150`); Known limit `:23` reworded. |
| 21 gRPC `Wrap` chain | **closed** | `DefaultGrpcChannelFactory.cs:77`; EG-XPT-03 row `:105`; gap-coverage casts updated (#722 stat). |
| 22 hop 2 guard/site/faults/`Refused` | **closed / Q-A** | same `_guard`, `_family`, `_site` (`:28-44`, `:155-161`); faults counted in `EgressGuardHandler.RecordGuardFault` (`:132`, `:149`). Under the 4.6 opt-in hop 2 is recorded `Refused = true` and still sent (Q-A). Inventory `:15` "`Refused` stays false" is true only in report mode — minor wording. |
| 23 redirect limit | **closed** | `redirects > _settings.MaxAutomaticRedirections` (`:80-81`, `:102-103`); differential twin `:409`. |
| 24 userinfo/fragment | **closed** | record is `SchemeAndServer` (`EgressDestinations.cs:196`); fragment kept (`:176-178`). |
| 25 `IdeEndpoints.cs:193` two policies | **records** | EG-MDL-14 row `:84` does not say the factory path follows cross-host and the fallback does not (minor). |
| 26 relative `RequestUri` after rewrite | **closed** | `Authority.From` guards `!IsAbsoluteUri` (`:377-380`); `Evaluate` passes the URI to `EgressRequest`, and `EgressGuard.Evaluate` records `Fault` (`EgressGuard.cs:124-138`). |

New branches the Cursor code added, checked:
- `EgressRedirectHandler.Follow` (sync) mirrors `FollowAsync` (`:94-113`) — same holes (O2, host key).
- `EgressEvaluatedAuthority.Stamp/Matches` — scheme+host+port, case-insensitive, relative → `(null,null,-1)` (`:362-410`). OK.
- `ReportIfResponseAuthorityDiffers` — after the follower stamps the final URI it matches `response.RequestMessage.RequestUri` (same instance, `SocketsHttpHandler` mutates the request), so no double decision. OK. If a primary returns `RequestMessage == null`, nothing is checked (design limit, item 7).
- `EgressRedirects.Memory` (`ConditionalWeakTable`, `:263`, `:291-300`): the **first** `followCrossHost` seen for a primary instance wins. A handler instance first bound by the factory (P1) and later wrapped by `EgressHttp.CreateClient(inner)` gets P1 under P2's family/site. No production sharing found; minor.
- `EgressRedirectBindingFilter` re-insert: guard built with `CreateDelegatingHandler` → on the ns2.0 asset this throws `PlatformNotSupportedException` inside a filter (host code on .NET 5–7 only; Infrastructure targets net8+ so unreachable). OK.
- Concurrency: the redirect loop mutates the single `HttpRequestMessage`; `response.Dispose()` before `ApplyRedirect`; the final 3xx on limit is **not** disposed (correct, returned). A faulting guard on hop 2 is swallowed and counted and the hop is still sent (report-only, consistent with 4.2/4.6).
- Exception path: if `base.SendAsync` on hop 2 throws, the exception propagates; no decision is lost (hop 2 was decided before the send).

---

## 3. attack-4.5.md item by item

| Item | Status | Evidence |
|---|---|---|
| 1 open scope, tool egresses mid-call | **OPEN — owner decision 1 contradicted** | §1 row 1. Production exposure today: none in bytes (the only production frame is `SelfExtendRunnerAdapter.RunAsync` at SystemHigh, `SelfExtendRunnerAdapter.cs:251`; `EgressSubjectFloorPinTests` pins it), so the pre-read mark equals SystemHigh and the decision is identical. The gap bites any runner with a floor below SystemHigh (the leak skeleton, any future runner), and the owner's expected Scenario B reason (`SystemHighData`) is not what master produces (`LevelTooLow`/Allowed, per the pinned test). Tools that egress during their own call today: `WebSearchTool` → `BingWebSearchProvider.cs:56-59` (EG-WEB-01; not in `RepoFsToolboxFactory`, host-registered), `McpToolProxy` → `McpClientConnectionManager.cs:150-184` over the EG-XPT-06 client created at connect (`:328-332`), the `dotnet.*`/forge tools → `DotnetRunner.cs:16` (EG-PROC-01), `RepoGitCommitTool`, and `RAGTool` over `VectorDataRagService.EmbedAsync` (`VectorDataRagService.cs:187`) — in-process `TokenHashEmbeddingGenerator` by default (`MeaiPipelineServiceCollectionExtensions.cs:152-157`), a remote embedder if a host replaces it. |
| 2 report on tool's flow | **closed** | `ReadScope` is a shared object; `Report` is a CAS on the instance (`ReadScope.cs:47-66`). |
| 3 cross-flow dispose / double dispose | **closed (unchanged 4.4)** | no marker is pushed; `Dispose` idempotent (`:80-81`). |
| 4 toolbox throws synchronously / `Complete` placement | **closed** | `using (var read = EgressSubject.BeginRead())` encloses the `try` (`ToolCallingAgent.cs:234-256`); `Complete()` is after `ReportRead`, skipped on any throw. `A_tool_that_throws_is_SystemHigh_when_the_scope_ends` (`EgressSubjectProducerTests.cs:112`). |
| 5 nested scopes | n/a | no open-scope state exists (item 1). |
| 6 impostor by id | **closed** | the lookup is `registry.Find(call.Id) is IEgressLabelledTool` on the registered instance (`ToolCallingAgent.cs:248-252`); an extra tool named `rag_search` that does not implement the marker is unlabelled. |
| 7 marker is a public interface | **OPEN — convention fact missing** | `IEgressLabelledTool` is public in `Ashlar.Abstractions` (`IEgressLabelledTool.cs:12`, `PublicAPI.Unshipped.txt`); any `ITool` reaching the toolbox through `IToolSource`/DI can implement it and `Report(Public)` for anything. No cert-gate fact pins implementers to `RAGTool.cs` (grep: only `EgressGuardConventionTests.cs:209` mentions the file, as a floors comment). Records say "in #721 the only labelled tool is RAGTool" (inventory `:26`) — true of the tree, enforced by nothing. |
| 8 scope handed through a channel every tool sees | **closed** | the scope reaches only `IEgressLabelledTool.ReportRead` (`:252`); not in `WorldSnapshot.Data`. |
| 9 wrapped labelled tool | **closed (fail closed)** | the decorator is unlabelled → SystemHigh. |
| 10 Unicode fold | **closed (probe)** | §5 P6: `FromName("İnternal")` and `TryRank` both miss → RAGTool SystemHigh, store MostRestrictive. Theory `RAGTool_hit_labels_match_TrustTierOrder_RecordLabel` has no non-ASCII row (minor). |
| 11 custom level sharing a primitive's value | **closed** | `ReferenceEquals` against `DataSensitivityLevels.All` (`RAGTool.cs:150-154`); `Pony` (value 3) row → SystemHigh. |
| 12 refusal path carries `ex.Message` at Public | **OPEN, not recorded** | `RAGTool.cs:95-101` catches **every** `ArgumentException` from a host-replaceable `IRAGService`, returns `RagRefusal(Reason: ex.Message)` and `ReportRead` reports `Public` for `RagRefusal` (`:122-124`). D15's "its refusal" meant `VectorMath.UnrankableQuery`. Records (`inventory :26`) say "the unrankable-query refusal … report Public" — narrower than the code. Exposure: a host `IRAGService` whose `ArgumentException` text carries data; `RAGTool` is in no production toolbox. |
| 13 zero hits vs filtered corpus | **closed** | `hits.Count == 0 → Public` (`:125-127`); production clearance floor unchanged. |
| 14 frame placement in `RunAsync` | **closed** | `using` inside `RunAsync` body around cycle + `TryRecordAsync` (`SelfExtendRunnerAdapter.cs:248-311`); `Production_self_extend_records_subject_agent_at_SystemHigh` (`:135`). |
| 15/16/17 floor pin convention | **closed (tripwire)** | `EgressSubjectFloorPinTests` pins file/method/floor/count, root-wide; remarks admit alias/delegate/reflection are not seen (`:16-17`). No `yield` check (minor). |
| 18 work created before the frame | **records** | not named (the MCP pump question is still **[unverified]**); design `:196` sentence not narrowed — minor. |
| 19 post-cycle steps | **closed** | inside the `using` (`:248-311`). |
| 20 subject id bounding / synchronous tool `Enter` | **records** | not named; minor. |
| 21 peer observe after response, streaming early break | **closed** | `ObserveAfterAsync` in `finally` (`EgressGuardChatClient.cs:104-114`); `ObserveStream` iterator with `finally` runs on `DisposeAsync` (an `await foreach` + `break` disposes) (`:116-127`). A consumer that abandons the enumerator without disposing never observes — inherent to "after the response", not recorded (minor). |
| 22 `Decide` stays eager | **closed** | `Decide(options)` runs before the wrapping call (`:80-81`, `:91-93`); `base.GetStreamingResponseAsync` is called eagerly and only the wrapper is lazy. |
| 23 agent-backed ≠ `peer:` | **recorded** | remarks and inventory say keyed on the `peer:` prefix (`EgressGuardChatClient.cs:19-24`). |
| 24/25 uninstrumented paths | **records** | inventory `:26` does not list `AgentHost`, orchestrator etc. as unobserved (minor; the production floor is SystemHigh). |
| G5 (integrator): report-only surface for the tool | **OPEN — integrator decision not honoured, not recorded** | `IEgressLabelledTool.ReportRead(ReadScope read, …)` hands the whole scope; a labelled tool may call `read.Complete()` then throw: the throw leaves `labelled.ReportRead` (`:252`) outside the `try`, the `using` disposes a completed-and-reported scope and observes the tool's label, not SystemHigh. Probe §5 P7. Only `RAGTool` implements it in-tree and it never calls `Complete`; the hole is a host labelled tool (item 7). |

---

## 4. attack-4.10.md item by item

| Item | Status | Evidence |
|---|---|---|
| 1 RunPod from AG | **closed** | `NcrCapabilityRouter.cs:85-90` → `Local` with the AG prefix; twin `Overnight_routing_stays_local_on_air_gapped_and_can_select_remote_on_full`. |
| 2 `PeerNetworkOnly` on AG | **closed** | throws `InvalidOperationException` (`:54-61`). |
| 3 `PreferPeerNetwork` / `Options.Create` bypass | **closed** | `!IsAirGapped &&` guard at `:67`; router branch is independent of the validator. |
| 4 `generation.runpod` brick direct | **recorded (guard backstop)** | EG-MDL-09 row `:79` "The RunPod client is still registered"; `:218`. |
| 5 LLM escalation | **closed** | `IsAirGapped ? (local ? {resolved} : empty) …` (`AdaptiveProviderFactory.cs:56-61`). |
| 6 cloud `resolved` LLM | **closed** | `Array.Empty` → `ModelUnavailableException`. |
| 7 single-image vision | **closed** | `WithoutOpenAiOrAzure` (`:98-99`, `:156-158`). |
| 8 multi-frame vision with a cloud `resolved` | **OPEN, not recorded as a limit** | `ExecuteVisionMultiFrameAsync` (`:120-139`) is unchanged: `_inner.ExecuteVisionMultiFrameAsync(resolved, …)` with no AG check. On AG with `ASHLAR_LOAD_PREFERENCE=server` and `OPENAI_API_KEY` set, `PreferenceLoadPolicy` resolves `openai` and every frame goes to `api.openai.com` (EG-MDL-05). PR #720 body line 11 says "Multi-frame stays on the resolved name only" (a description, not a claim of safety); CHANGELOG/SPEC say "does not try OpenAI or Azure … for LLM calls or single-image vision" (carefully scoped). Design §2.7 `:599` "never escalates past local on AG"; the attack list (item 8, §3.1) and the Claude lane both required a refusal here. No Known-limit sentence names multi-frame. |
| 9 video pass-through | **recorded (guard backstop)** | design silent; inventory `:218` general. Minor: not named by id. |
| 10 bare factory honours `openai` | **recorded** | `:218`. |
| 11 "local" Ollama at a LAN address | **recorded** | D40 / inventory `:21`. |
| 12 `BrickHost:RemoteCatalogBaseUrls` | **closed** | `ValidateAirGappedBrickHostOptions` + `ValidateOnStart` (`AshlarFederatedBrickMeshServiceCollectionExtensions.cs:31-35`); null list handled. |
| 13 `EnablePeerNetworkRouting` | **closed** | `RunPodCapabilityRoutingServiceCollectionExtensions.cs:36-40`. |
| 14 MeshLab worker | **closed** | `MeshLabServiceCollectionExtensions.cs:28-32`. |
| 15 Bedrock: `ValidateOnStart` cannot see a pre-built instance; host-less CLI | **half closed; integrator decision (composition-time refusal) not implemented, not recorded** | #720 moved `MeaiPipelineOptions` into the options pipeline (`MeaiPipelineServiceCollectionExtensions.cs:94-100`) so `ValidateOnStart` works for hosts that **start**. But the Bedrock tier is registered from the local snapshot (`:95` comment, `:121-123`), not from `IOptions<T>`, so a process that never starts its host and never reads `IOptions<MeaiPipelineOptions>` (the `ashlar` CLI, `application/src/Ashlar.CLI/Program.cs:56-76`; `BuildServiceProvider()` verbs) composes `cloud:bedrock:*` keyed clients on AG with `Ashlar__Meai__Bedrock__Enabled=true` and nothing refuses. Inventory `:72` says "PR 4.10 refuses `Bedrock:Enabled=true` at boot on AirGapped" — true only where boot happens. (`OllamaHttpChatClient` does read `IOptions<MeaiPipelineOptions>` at `:107`, so a CLI verb that resolves the local client trips the validator first; one that resolves the keyed Bedrock client directly does not.) |
| 16 ollama.com default | **closed** | `Configure<IOptions<AshlarResolvedDeploymentProfileOptions>>` sets `Enabled = false` on AG when the key is absent (`ModelArtifactCatalogServiceCollectionExtensions.cs:38-45`); twin exists; "disabled by default" wording correct in CHANGELOG. |
| 17 no `AddAshlar` ⇒ no profile | **by design** | `AshlarResolvedDeploymentProfileOptions` default `full` (`:9-15`); inventory `:50` says Hosting registers it. |
| 18 later Full `AddAshlar` | **value closed; module set open (4.11)** | `notedProfile = Effective(canonical) ?? canonical` captured after `NoteResolved` (`AshlarServiceCollectionExtensions.cs:131-138`). Module-set gap is the handoff's carry-in; not re-recorded here (minor). |
| 19 reverse order keeps Full | **by design, not recorded** | minor. |
| 20 host lowers the profile value | **OPEN as a Known limit, not recorded** | the value is `IOptions<T>` built through `.Configure` (`:136-138`); a host `Configure<AshlarResolvedDeploymentProfileOptions>(o => o.Profile = "full")` registered after `AddAshlar` wins and every 4.10 check reads Full. The attack list asked for this limit next to D40; no record names it. |
| 21 API Kestrel listeners | **partly closed; `http_ports` OPEN** | `CollectEndpoints` reads `urls` (`ASPNETCORE_URLS`/`DOTNET_URLS`/`--urls`) and `Kestrel:Endpoints:*:Url` only (`AshlarInboundListenerPolicy.cs:76-94`); **`http_ports`/`https_ports` (`ASPNETCORE_HTTP_PORTS`/`HTTPS_PORTS`) are not read**. With `urls` unset the list is empty and `Refusal` returns null ("host default … allowed", `:53-55`, Program.cs comment `:231-232` "Empty urls are the host default (localhost)") while Kestrel binds every interface on that port. The aspnet base image ships `ASPNETCORE_HTTP_PORTS=8080` (`tests/uat/tier0-2.sh:150-153`, `tests/uat/README.md:89`); the API image sets `ASPNETCORE_URLS=http://+:8080` (`.docker/Dockerfile.api:16`), which is refused on AG/SW, so the obvious operator fix — unset `ASPNETCORE_URLS` — silently falls back to the image's `http_ports` and binds `[::]:8080`. Probe §5 P5 pins `CollectEndpoints` ignoring `http_ports` and `Refusal == null`. The twin (`AirGappedSecureWorkstationHygieneCertificationTests.cs:206-231`) has no `http_ports` row. Post-bind check for code-configured listeners (attack part 3): absent. `*.localhost` correctly refused (`IsLoopbackEndpoint` accepts only `localhost` and loopback IPs). Bare `::1` (the `ASHLAR_MESH_SERVE_BIND` form) → probe P5: see §5. |
| 22 MCP over HTTP on SW | **closed for the API host; convention open** | marker registered only by `WithAshlarHttpTransport` (`AshlarMcpServerServiceCollectionExtensions.cs:92-97`); API uses it (`Program.cs:157`). A host calling the third-party `.WithHttpTransport()` directly registers no marker and **boots** on SW (fail-open default: no marker = stdio, `ValidateAshlarMcpServerOptions.cs:20-23`, `:47`). The attack's fail-closed row (no transport ⇒ refuse) is the opposite of what shipped. No convention fact pins "no production `.WithHttpTransport(` outside `WithAshlarHttpTransport`" (grep: only `Mcp.Server/…Extensions.cs:96` calls it). Validator reads the env var, not the resolved options (`:39-40`) — pre-existing shape. |
| 23 mesh serve | **closed (fail boot), but not the integrator's decision** | `ExecuteAsync` throws before listen when the bind (default `0.0.0.0`) is not loopback (`MeshServeService.cs:157-166`); `ListenLoopback` when it is (`:261-277`, `:313-314`). INTEGRATION-NOTES-C decided "bind loopback (`ListenLocalhost`) and keep serving" per the owner's recorded Q6 text; master instead **fails the daemon** unless the operator sets the new `ASHLAR_MESH_SERVE_BIND`. Fail closed; records are consistent with the code (inventory `:142`, PR body); the deviation from the integrator decision is unrecorded as such. Since a `BackgroundService` throw stops the host (default `BackgroundServiceExceptionBehavior.StopHost`), `Host.CreateDefaultBuilder` at `BackgroundAgentDaemonCommand.cs:166`), "boot fails" holds. |
| 24 UDP discovery `0.0.0.0:7421` | **open by scope, recorded as follow-up** | INTEGRATION-NOTES-C "Queued … UDP discovery … under Q6 (ask on the bus)"; nothing posted on the bus. Not a 4.10 finding per the owner's Q6 text. |
| 25 gRPC server host | same as 24. | |
| 26 SW outbound unchanged | **by design; recorded** | inventory `:218`; SW keeps RunPod etc. until 4.11. |
| 27 D41 hygiene | **closed** | `[Collection("EnvironmentVariables")]`, `EgressProcessStateScope` (`:30-36`). |
| 28 required check sees the evidence | **closed** | the hygiene twins are in the cert-gate project; `Program.cs` call site is not pinned by a source-reading fact (minor). |
| 29 container env | **closed** | listener twin passes explicit strings. |
| 30-38 remaining AG/SW paths | **recorded** | inventory `:218`. |

---

## 5. Probe runs (own clone, container, net8.0)

Filter: `FullyQualifiedName~ReviewSecurityProbeTests|FullyQualifiedName~An_egress_during_the_tool_call_is_decided_at_the_pre_read_mark`.
Run 1 (`logs-security/probe-run1.log`): compile error in my test helper (CS9007), fixed in commit 2. Run 2 (`probe-run2.log`):
Run 2 (`probe-run2.log`): Failed 2, Passed 6, Total 8 — the two factory O2 probes used an `https://remote` first hop and an
`http://` Location, which the **downgrade rule** (`IsHttpsToHttp`, `EgressRedirectHandler.cs:183-184`) returns before any
host test; that is an incidental closure, not O2, and it shaped the exposure statement in §6(b). Run 3 (`probe-run3.log`,
commit 3 of the probe branch): **Passed 11 / Failed 0 / Total 11**, net8.0, 280 ms:

| Probe | Result | What it shows |
|---|---|---|
| P1 theory `Probe_factory_client_follows_remote_307_into_the_host_boundary_with_the_POST_body` row `http → http://127.0.0.1:11434/api/chat` | green | stub saw 2 requests; hop 2 POST with body `SECRET-BODY` and `X-Ashlar-Api-Key` at loopback; decisions `[http://remote, http://127.0.0.1:11434]`, hop 2 `Host`, `Access.Allowed = true`, `Refused = false`. **O2 absent.** |
| row `https → https://127.0.0.1:11434/api/chat` | green | same, over TLS to loopback. |
| row `http → http://169.254.169.254/latest/meta-data/` | green | followed with body; hop 2 NetworkExport, `SystemHighData`, `Refused = false` (report). |
| row `http → http://svc.localhost/x` | green | followed; `*.localhost` is Host, allowed. |
| row `https → http://127.0.0.1:11434/api/chat`, `followed = false` | green | 307 returned, 1 request, 1 decision: closed by the downgrade rule only. |
| P3 `Probe_EgressHttp_client_follows_same_host_different_port_307` | green | `http://h/a → http://h:8080/b` followed by an `EgressHttp` client, body re-sent, decisions `[http://h, http://h:8080]`. **P2 key is host only.** |
| P4 `Probe_EgressHttp_client_follows_same_host_scheme_upgrade_307` | green | `http://h → https://h` followed; decisions `[http://h, https://h]`. |
| P5 `Probe_listener_policy_http_ports_and_bare_ipv6_rows` | green | `CollectEndpoints` with `http_ports=8080` and no `urls` returns `[]`; `Refusal(AirGapped, [])` is `null`; `IsLoopbackEndpoint("::1")` is **false** (so the PR #720 body line 15 and CHANGELOG "`::1` succeed" are false for the bare bind form `ASHLAR_MESH_SERVE_BIND=::1`; `[::1]` is true); `http://api.localhost:0` is refused (correct). |
| P6 `Probe_dotted_capital_I_fold_direction` | green | `DataSensitivityLevels.FromName("İnternal")` is null and `TrustTierOrder.TryRank` is false on the container runtime: both sides fail closed; attack-4.5 item 10's loose direction does not occur. |
| P7 `Probe_labelled_tool_that_completes_then_throws_observes_its_own_report` | green | through the real `ToolCallingAgent`, a labelled tool that `Report(Public)`, `Complete()`, then throws leaves the frame's mark at `Public` and the cycle at `stoppedReason = "error"`. **G5 not honoured.** |
| Cursor's `An_egress_during_the_tool_call_is_decided_at_the_pre_read_mark` | green | master pins the pre-read-mark rule the owner reversed. |

---

## 6. The three known gaps, stated precisely

**(a) Open-read-scope rule absent.** `ReadScope` observes only at `Dispose` (`ReadScope.cs:78-86`); `Resolve` reads frame marks only (`EgressSubject.cs:238-251`). Inside `ToolCallingAgent`'s `using (var read = EgressSubject.BeginRead())` (`ToolCallingAgent.cs:234`), a decision made by the tool on the agent's flow (or any flow it starts) is decided at the frame's mark **as it was before the call**. Production tools that egress during a call: `WebSearchTool`/`BingWebSearchProvider` (EG-WEB-01, `BingWebSearchProvider.cs:56-59`); `McpToolProxy` → MCP SDK `CallToolAsync` over EG-XPT-06; `dotnet.build/test/run`, forge and git tools → `DotnetRunner.cs:16` (EG-PROC-01); `RAGTool` with a host-supplied remote `IEmbeddingGenerator` (`VectorDataRagService.cs:187`; default is in-process `TokenHashEmbeddingGenerator`). Exposure today: zero bytes, because the only production frame is pinned at SystemHigh (`SelfExtendRunnerAdapter.cs:251`, `EgressSubjectFloorPinTests`), so the pre-read mark is already the top. Exposure under the design: any runner that declares a floor below SystemHigh (the leak skeleton; a future runner that vouches for its inputs) has every mid-call egress decided below the data the tool is reading, which is exactly the write-down the owner closed. Master pins the wrong rule by name (`An_egress_during_the_tool_call_is_decided_at_the_pre_read_mark`) and records it as a limit "the owner can reverse".

**(b) O2 absent.** `TryGetRedirect` (`EgressRedirectHandler.cs:163-191`) has no host-boundary test. Factory clients (`FollowCrossHost = true`, `EgressRedirectBindingFilter.cs:42`) follow a remote 3xx into `http://127.0.0.1:…`, `http://x.localhost/…`, `http://169.254.169.254/…`; on 307/308 the method and body go with it, and every header except `Authorization` (`:195`). Production factory clients and what they carry:
- the **unnamed default client** (`AshlarServiceCollectionExtensions.cs:146`): `RemoteBrick` POSTs `BrickInput` + `ExecutionContextDto` to `entry.HostBaseUrl` chosen by a remote catalog (EG-EXE-01), `AshlarPeerBrickExecutor` POSTs prompt/model/context to file-discovered peers (EG-EXE-03), workflow webhooks (EG-HTTP-01/02), `IdeEndpoints.cs:193` GET `api/tags`;
- `AshlarExecution` (`Phases.cs:780`, `ASHLAR_EXECUTION_REMOTE_URL`): remote execution bodies;
- `IRunPodClient` typed client (`RunPodCapabilityRoutingServiceCollectionExtensions.cs:54`): dispatch body `{modelId, prompt, generationParameters, …}` (EG-MDL-09); its `Authorization: Bearer` is cleared on the hop;
- `MeshLabWorkerExecutorClient` (`MeshLabServiceCollectionExtensions.cs:40`, `FleetServiceCollectionExtensions.cs:116`): `BaseAddress` from a director's task list, `X-Ashlar-Api-Key` header **not** cleared (EG-EXE-05);
- the consumer SDK `AddHttpClient<IAshlarClient, AshlarClient>` (`Ashlar.Client/ServiceCollectionExtensions.cs:20-27`): `X-Ashlar-Api-Key` default header follows the hop;
- `OllamaModelServingBackend`, the three model-artifact catalog clients (GETs, metadata).
- **Not** affected: the two SNS signing clients (`AllowAutoRedirect=false` → `Follow=false`), `MeshAutoPullService` (`EgressHttp` over `SocketsHttpHandler { AllowAutoRedirect = false }`), the MEAI Ollama client.
The hop into loopback is recorded as `Host`, `Access.Allowed = true` (probe P1), so even under 4.11 enforcement the label model would **allow** it: this is the local-service CSRF path `MeshAutoPullService.cs:48-50` names, which is why the owner closed it for both routes. One narrowing the probes found: an `https://remote` first hop redirected to an `http://` loopback or link-local URL is returned by the **downgrade rule** (`IsHttpsToHttp`), so the open shapes are `http → http` (every `http://` base above: `OLLAMA_BASE_URL`-style LAN hosts, `MeshLabWorkerExecutorOptions.DirectorBaseUrl` default `http://127.0.0.1:18081`, `BrickHost:RemoteCatalogBaseUrls` and peer entries written as `http://`, webhooks) and `https → https` (a TLS loopback service, rare). The `https → http://169.254.169.254` metadata shape is closed by accident of the downgrade rule, and only for an https first hop.

**(c) P2 keyed on host only.** `SameHost` compares `Uri.Host` (`:224-225`). `IsHttpsToHttp` closes the downgrade (`:220-222`), so https→http is **not** followed; what is followed under P2 for `EgressHttp` clients is a same-host hop to a **different port** (`http://h:80 → http://h:8080`, probe P3) or an **upgrade** (`http://h → https://h`, probe P4). Each hop is still decided (`EvaluateHop`) so the record is complete; the design's "authority" (scheme://host:port, `:519`) is weakened to host without a recorded deviation. Exposure: an `EgressHttp` client to a local service (`http://127.0.0.1:11434`, EG-MDL-07/EG-XPT-06/EG-XPT-01) that is redirected to another local port — both Host, both allowed; or a remote host redirecting to another port on itself — same operator, low risk. Major, not blocker.

---

## 7. Verdict

**defective.** Blockers: owner decision 1 contradicted and pinned by a test (4.5); O2 absent (4.3); `http_ports` inbound bypass on AG/SW (4.10); multi-frame vision cloud path on AG (4.10); no decisions-log rows for the three owner decisions; "the owner can reverse it" in three records. Majors: P2 host-only key; started-primary case unrecorded; credential-header limit unrecorded; G5 not honoured; `IEgressLabelledTool` implementers fact missing; RAGTool refusal generalised to every `ArgumentException`; Bedrock host-less composition gap unrecorded; host lowering of `AshlarResolvedDeploymentProfileOptions` unrecorded; MCP `WithHttpTransport` convention missing; mesh serve deviates from the integrator decision silently.
