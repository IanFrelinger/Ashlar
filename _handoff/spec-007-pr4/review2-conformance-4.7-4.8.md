# Lens conformance-4.7-4.8 — #742 50efd7019 (4.7), #743 6d64ad508 (4.8) against DESIGN-4-final §2.3/2.4/2.5, D20–D29

Reviewer: Claude (subagent), 2026-10-11. Master at ac264559f, read-only clone C:/Users/icfre/Documents/Codex/2026-09-12/f-2/work/ashlar-g-review. No container run (reasons in §8).

## 0. Core types read (master ac264559f)

- `src/Ashlar.Abstractions/Security/Egress/EgressRefusedException.cs`: `public sealed class EgressRefusedException : Exception`, CA1032 suppressed with a written reason; properties Decision, Reason, Site, Sequence, Ref, ErrorCode="EGRESS_REFUSED". Message = `Egress refused by policy: {Reason}; site={Site}; family={Family}; class={DestinationClass}; ref={Ref}[; no-subject]`. No level names, no Detail, no destination host. D20 and the D-b local message: implemented.
- `EgressDecision.cs:56` `Refused = mode == enforce && !access.Allowed`; `:151 Refuses => Refused`; `:155 ThrowIfRefused()` returns `this`. Fault → `access = default` (NoDecision) → refused under enforce.
- `EgressGuardHandler.cs:75-90`: `Report(request)` runs before `base.SendAsync` inside an `async` method → faulted task; `Send` (NET5+) throws. `:128-136` post-send authority re-evaluation disposes the response and rethrows on refusal. No synthetic 4xx anywhere in the file. D21 implemented.
- `EgressGuardChatClient.cs:200-229 Decide()`: `EvaluateForRoute`, then audit `denied`/`egress_refused`, then `ThrowIfRefused()`; called before `base.GetResponseAsync`/`GetStreamingResponseAsync` (`:94`, `:118`), both non-async so the throw is synchronous.
- `EgressGuard.cs:118-186 EvaluateForRoute`: host guard throws → `fault = type name`, `EgressGuardHandler.RecordGuardFault()`, `ProcessDefault.EvaluateCore(request, fault)` (synthetic NoDecision under process mode); returns `null` → `fault = InvalidOperationException` same path. `:53-84 ResolveForRoute`: DI resolve throws → `_resolutionFaults++`, diagnose(true); null → `_resolutionMissing++`, diagnose(false).
- `LoggerEgressDecisionSink.cs`: 7301 Warning on `Refused`, windows keyed `(Site, Access.Reason)`, 5 minutes via `TimeProvider.CreateTimer`, 7305 summary on expiry and on `Dispose()` (`:161-177`). `EgressDecisionLoggerSubscription.Dispose()` disposes the sink (4.7 diff).
- `EgressRefusalWarnings.cs` (internal, Abstractions): the same window shape for the swallowing rows, event 7307 at the nine users, `Dispose()` flushes.
- `EgressRefusal.cs` (internal): `Find()` walks InnerException + AggregateException.InnerExceptions (reference-identity visited set); `RemoteMessage(ref) = "egress refused by policy (ref <ref>)"`; `SafeReference` accepts only 16 lowercase hex or `unavailable`, otherwise draws a fresh random ref.

## 1. Evidence pulled

- PR bodies: `logs-conformance-4.7-4.8/pr-742-body.md`, `pr-743-body.md` (gh pr view).
- CI cert-gate job logs for the merge commits: `ci-certgate-742.log` ("Total tests: 3066 / Passed: 3066"), `ci-certgate-743.log` ("Total tests: 3101 / Passed: 3101"). Check-runs `cert-gate` and `Readiness summary` conclusion=success for both (gh api commits/<sha>/check-runs).
- `git ls-tree -r <sha> src/Ashlar.Tests.Infrastructure/Tests/Certification`: 50efd7019 = 147 entries / 144 .cs (matches #742 body); 6d64ad508 = 150 / 147; ac264559f = 156 / 153.
- `ci/egress-inventory.tsv` at both merges: 86 rows, total=149, guarded=48; `docs/EgressInventory.md` EG- rows: 67 at both. The 2,149 production-file figure needs the C# scanner; not recomputed.
- Manifest old-text check (python, all 81 entries): spec007-refusal-surfaces.json 65/65 present exactly once on master; spec007-enforcement-routes.json 15/16; **r14-startup-claims-records-only is STALE** (its `old` text "HTTP and governed model routes honor enforcement; explicit sites are not yet enforced (SPEC-007 PR 4.7)." was replaced by #744 3023ef1ea; master `EgressEnforcement.cs:218` reads "HTTP, governed model routes and explicit sites honor enforcement; named operator file exports report unless mode resolution faults.").
- Spot-checks quoted: r2 (`EgressGuardHandler.cs:169` `EgressGuard.EvaluateForRoute(_guard ?? EgressGuard.ProcessDefault, egress).ThrowIfRefused();` → `_ = …`), s34 (`HotSwappableModel.cs:70` `EgressRefusal.ThrowIfPresent(ex);` → `_ = ex;`), s56 (`Orchestrator.cs:559` `if (EgressRefusal.IsRefused(initialResult)` → `if (initialResult.Success && …`). All three present on master.
- `scripts/run-spec007-mutation-batch.py`: requires `/.dockerenv`; `clean()` = `git status --porcelain --untracked-files=all` must be empty before baseline, before apply, after restore and after green; a baseline run per (project, framework, filter); apply = exact single-occurrence byte replacement with a non-empty `git diff`; red run parses TRX with strict counter consistency and rejects compiler errors (`error CS|CA|MSB|NU|NETSDK`) as not-a-kill; each `expected_failures` fragment must match a failed test; restore = byte-exact rewrite (temp file + os.replace) verified by `read_bytes() == original`; green run must match baseline counters; prints `mutation <id>: KILLED|SURVIVED|INVALID red=failed:F/T green=passed:P/T ref=<sha> framework=…`. The batch loop breaks after the first record that is not `final_clean`. Apply, red, restore, porcelain and green are all implemented, plus a baseline (stricter than `scripts/mutation-check.sh`).
- Manifest ids vs PR tables: the 16 ids equal the 16 rows of the #742 table (r9 listed out of order in the body); the 65 ids equal the 65 rows of the #743 table, including the renamed `s55-inner-exception-edge-lost` (the body says the original `s55-grpc-debug-edge-lost` survived and is not counted).

## 2. Design bullets, one by one (master ac264559f)

| Bullet | Status | Evidence |
|---|---|---|
| D20 exception type | implemented | EgressRefusedException.cs:6 sealed, derives from Exception; :5/:7 CA1032 with reason; properties :13-28 |
| D22 convention at the routes | implemented at the three routes | EgressGuardHandler.cs:169, EgressRedirectHandler.cs:157, EgressGuardChatClient.cs:228 all `.ThrowIfRefused()`; the cert-gate convention row itself is 4.9's |
| D21 HTTP faulted task / Send throws / never a synthetic 4xx | implemented | EgressGuardHandler.cs:75-90; no status-code construction in the handler |
| MEAI throws before the inner client, writes its own audit record | implemented | EgressGuardChatClient.cs:94,118 (non-async overrides), :209-228; auditor wired at AshlarGovernanceChatClientBuilderExtensions.cs:32-33 (`GetRequiredService<IChatInvocationAuditor>()`, registered TryAddSingleton at MeaiPipelineServiceCollectionExtensions.cs:209) |
| D23 no same-destination retry | implemented | TransientClassifiers.cs:28 `EgressRefusal.Find(exception) is null && …`; ProviderFactory.cs:181-182 `IsProviderCallTransient = Network(ex) || ex is ModelUnavailableException`, and OllamaProvider rethrows refusals unwrapped (:122-127, :284-288) so `ExecuteOllamaAsync` never wraps one in ModelUnavailableException; A2AAgentTransport.cs:99-105 refusal catch precedes the retry branch; Orchestrator.cs:559 breaker exemption |
| D23 fallback allowed, refusal is the final error | implemented | AdaptiveProviderFactory.cs:50-87, :98-128 with ProviderRefusalScope; WorkflowExecutor.cs:301,309 and :384,391; AshlarPeerBrickExecutor.cs:77,88 |
| D23 never the echo model | **weakened** (B1, B2) | HotSwappableModel.cs:70,88 rethrow only exception-borne refusals; the availability-probe path and OrchestrationHotSwappableModel.cs:56-67 are uncovered |
| D24 rows 1-16, 19-22, 24-29 (26 rows) | implemented | 1,2 HotSwappableModel.cs:70,88; 3 AdaptiveProviderFactory.cs:54,83,102,126; 4 ToolCallingAgent.cs:117-121; 5 :292-297; 6 :248-262; 7/8 Orchestrator.cs:559; 9 InProcessAgentTransport.cs:53-63; 10 GrpcAgentTransport.cs:69-76,88-94; 11 A2AAgentTransport.cs:99-105; 12 AuditingChatClient.cs:45-51,89-97,108-115; 13 HttpRemoteBrickCatalog.cs:68-72,96-100,152-156; 14 CompositeBrickRegistry.cs:64-67,106-109; 15 ModelArtifactCatalogService.cs:64-67; 16 RemoteExecutionPlatform.cs:46-50,80-84,125-129; 19 McpClientConnectionManager.cs:98-101,182-187,213-217; 20 AshlarPeerBrickExecutor.cs:177-180; 21 RunPodHttpClient.cs (5 catches); 22 EndpointHealthMonitor.cs:92-97,117-122; 24 ProviderFactory.cs:792-798; 25 AdaptiveProviderFactory.cs:153; 26 ValidationServiceAdapter.cs:237-241, 287-301 (Passed=false, no totalTestsFailed++, evidenceErrors); 27 WorkflowExecutor; 28 MeshLabWorkerExecutorBackgroundService.cs:52-55; 29 ElasticWorkloadAutoscaleService.cs:57-60 + AutonomyLoopService.cs:184-187, 267-273 (`SweepOutcome.Refused`) |
| D25 four degrade sites | 4.9, out of lens | — |
| D26 tool refusal observation, cap of 3, egress_refused | implemented | ToolCallingAgent.cs:248-262 observation `tool {id}: REFUSED by egress policy ({refusal.Message}). Adjust your plan and try again.`, `refused >= 3` → `stoppedReason = "egress_refused"` + Warning; :289 outer break; model refusal :292-297 → `egress_refused`; `AgentCycleResult.EgressRefusals`. Wording deviation only: after a refused tool the remaining calls of that iteration are skipped (`break`); the cycle continues |
| D27 exits walk the chain, fixed text + ref, seq operator-only | implemented | EgressRefusal.Find; AshlarA2AAgentHandler.cs:74-77, 96-99, 159-168 (`RemoteMessage` + metadata errorCode/egressRef); AgentTransportServiceImpl.cs:49-55, 187-190, 207-215 (`EgressRef` proto field 7, `ErrorMessage = RemoteMessage`); ToolboxMcpToolContributor.cs:101-104; `Sequence` reaches no remote surface |
| D28 Warning 7301, 5-minute window per (site, reason), 7305 summary, disposal flush | implemented for the sink and nine component windows; **row 24 not windowed** (M2) | LoggerEgressDecisionSink.cs:88-99, 131-158, 161-177; EgressRefusalWarnings.cs; ProviderFactory.cs:796 is a plain `LogWarning` |
| D29 durable audit deferred | recorded | no IDataDecisionAuditLog adapter in either diff; SPEC bullets claim none |
| §2.4 guard fault → NoDecision refused under enforce | implemented | EgressGuard.cs:253 `access = externalFault is null ? … : default`; EgressDecision.cs:56 |
| §2.4 host guard throws / returns null → synthetic NoDecision under process mode, counted | implemented | EgressGuard.cs:139-186 |
| §2.4 DI fallback Warning on throw / Debug on null | implemented | EgressGuard.cs:53-84; event 7306 in EgressServiceCollectionExtensions.cs and AshlarGovernanceChatClientBuilderExtensions.cs |
| §2.4 sink fault / re-entrant skip still enforced | implemented | EgressDecisionLog.cs `_reentrantRefusalSkips`; Decide() throws after the audit try/catch |
| §2.5 D-b redaction (Q7) | implemented | Message = reason, site, family, class, ref (+ no-subject); no Detail, no level names, no destination host; remote = fixed text; operator sinks carry Detail (sink template, event 2) |
| §2.5 ref random, in exception and record | implemented | EgressEnforcement.cs:266-275 RandomNumberGenerator; EgressDecision.Ref; EgressRefusedException.Ref; sink template `ref={Ref}`; event 2 `@ref` |
| Event 2 Refused at Warning, event 1 unchanged | implemented | EgressEventSource.cs:137-148 |

## 3. Question (1): can a refusal become an allowed outcome?

- Availability probe → same destination retried: no inside AdaptiveProviderFactory (the scope records; the first refusal is rethrown when nothing succeeds; `Real_Ollama_health_refusal_survives_provider_availability_conversion` pins that path).
- Retry classifiers: no (TransientClassifiers.Network; ProviderFactory's Ollama policy; A2A refusal catch before the retry branch; gRPC/InProcess map to EGRESS_REFUSED which the Orchestrator exempts from the breaker).
- Echo/stub returned on refusal: **yes, two paths** (B1, B2). Both are outside the 4.8 tests: `Echo_fallback_never_masks_a_direct_or_wrapped_refusal` injects the refusal as the inner IModel's exception; nothing drives one through `IsProviderAvailable`; no test references OrchestrationHotSwappableModel with a refusal.

## 4. Question (2): remote leakage of Detail / level names / destination

None found. Remote surfaces (A2A server FailAsync, gRPC InvokeResponse, MCP outcome, TransportHealth.Message) carry `EgressRefusal.RemoteMessage(ref)` only; `SafeReference` refuses to echo anything that is not 16 lowercase hex or `unavailable` (s33/s58). In-process AgentResult.ErrorMessage carries the redacted local message (reason, site, family, class, ref), which D-b permits in-process; the gRPC and A2A servers convert such results to the fixed text before replying (`IsRefused(result)` branches).

## 5. #722 majors 3 and 4 in 4.7

Both were fixed by 4.3b (#739) before 4.7 and are intact at ac264559f: EgressRedirectHandler.cs:319-322 and :338-345 return a `Decider` with `RedirectSettings(false, …)` for an immutable or started parent; `ReadAndDisable` :380-406 excludes `Credentials` primaries and sets `allow = false` when the setter throws; EgressRedirectBindingFilter.cs:52-56 gates warning 7304 on `ProfileEnforcesByDefault()` (AG/SW only). The 4.7 diff to EgressRedirectHandler changed only `Evaluate` (ThrowIfRefused) and RecordUnfollowed (dispose then rethrow). Not regressed.

## 6. Findings

### B1 (blocker by the lens rule: a refusal swallowed into an allowed outcome). The Ollama health-probe refusal outside AdaptiveProviderFactory becomes "provider not available", then the echo model.
- `ProviderFactory.cs:792-798`: `if (EgressRefusal.Find(ex) is { } refusal) { ProviderRefusalScope.Record(refusal); _logger.LogWarning(…); return false; }`.
- `ProviderRefusalScope.cs:15-19`: `var scope = Current.Value; if (scope is not null) …` — with no ambient scope the refusal is dropped. A scope exists only inside `AdaptiveProviderFactory.ExecuteLLMAsync/ExecuteVisionAsync/ExecuteVisionMultiFrameAsync` (:50, :98, :139); `AdaptiveProviderFactory.IsProviderAvailable` (:40) opens none.
- `ProviderBackedModel.cs:28-31`: `if (!_providerFactory.IsProviderAvailable(provider)) throw new InvalidOperationException($"Provider '{provider}' not available");` — this is HotSwappableModel's agentic model whenever the MEAI pipeline is off (`AshlarKernelRegistrar.Phases.cs:383-389`), over the Infrastructure `IProviderFactory` chain (:573-598).
- `HotSwappableModel.cs:64-73`: `catch (Exception ex) { EgressRefusal.ThrowIfPresent(ex); … return await _deterministic.CompleteAsync(input, ct); }` — `ThrowIfPresent` finds nothing in the InvalidOperationException, so under `ashlar.model.prefer=deterministic` with a provider directive the echo model answers; `:86-99` under `ASHLAR_ALLOW_MOCK=1` likewise; otherwise `ModelUnavailableException("Model provider 'ollama' failed … unreachable model")` reports the refusal as an outage, which #743's body says it fixes.
- Trigger: a non-loopback Ollama (`ASHLAR_OLLAMA_BASE_URL`/`OLLAMA_BASE_URL` off-host, or the ephemeral lifecycle) under enforce (AG/SW by default since #746): `GET api/tags` (EG-MDL-07) is refused, no payload leaves, but the caller gets a fabricated echo success. Design row 24 said "record the refusal on the factory"; 4.8 recorded it on an AsyncLocal that only Adaptive's execute paths read. Same shape at `BehaviorExecutor.cs:425` (the agentic implementation silently drops out of the chain) and `IdeEndpoints.cs:144` (health reports "unavailable").
- Fix: keep the last refusal on the ProviderFactory instance (or throw it from `IsProviderAvailable` when no scope is active) and rethrow it from ProviderBackedModel; twin: ProviderBackedModel over a real ProviderFactory whose Ollama health throws a refusal, under HotSwappableModel with prefer=deterministic and ASHLAR_ALLOW_MOCK=1 → the refusal, not echo text; mutation on ProviderFactory.cs:792.

### B2 (blocker by the lens rule, gated on ASHLAR_ALLOW_MOCK=1). `OrchestrationHotSwappableModel` falls back to its echo model on any exception, refusals included.
- `src/Ashlar.Orchestration/Models/OrchestrationHotSwappableModel.cs:52-67`: `try { return await _primary.CompleteAsync(input, ct); } catch (Exception ex) { if (!AllowMock()) throw new ModelUnavailableException(…, ex); _logger.LogWarning(…); return await _fallback.CompleteAsync(input, ct); }`, `_fallback = new EchoDeterministicModel()` (:24). No `EgressRefusal` reference in the file; untouched by #743.
- Wired for every orchestration agent: `AgentFactory.cs:179-183 WrapModel` → `new OrchestrationHotSwappableModel(model, logger)` around the DI `IModel` (MeaiBackedModel → EgressGuardChatClient, or ProviderBackedModel). A governed MEAI refusal reaches this catch; with `ASHLAR_ALLOW_MOCK=1` the agent receives echo output and the cycle proceeds as a success (ToolCallingAgent's row-4 catch never sees the refusal). Without the flag the ModelUnavailableException keeps the refusal as InnerException and `Find` still works downstream.
- The design's rows 1–2 named only `Ashlar.Infrastructure…HotSwappableModel`; this is the same masking shape ("echo under ASHLAR_ALLOW_MOCK=1") at a site the design missed. Not a recorded deviation; no Known limit. Fix: `EgressRefusal.ThrowIfPresent(ex)` first; twin + mutation like s34/s35.

### M1 (major: evidence that cannot be reproduced). `scripts/mutations/spec007-enforcement-routes.json` entry `r14-startup-claims-records-only` is stale on master.
- Its `old` text was replaced by #744 (`git log -S` → 3023ef1ea); master EgressEnforcement.cs:218 carries the 4.9 wording. `run_mutation` raises `InvalidRun("replacement must change exactly one source fragment")` before `applied = True`, so r14 records INVALID with no `final_clean` and `main()` breaks the batch: r15 and r16 never run, and the batch verdict on master is INVALID. #744 changed the line the control targets without updating the manifest. The #742 table itself was true at 963c5efc.

### M2 (major: a design bullet weakened without a recorded deviation). Row 24's Warning is not windowed.
- D28: "A catch-all that swallows a refusal (rows 13–15, 18, 22, 24, 28, 29) logs its own windowed Warning." ProviderFactory.cs:796 `_logger.LogWarning("Ollama health check refused by egress policy (ref {Ref})", refusal.Ref)` fires on every `IsProviderAvailable("ollama")` (every ProviderBackedModel call, PreferenceLoadPolicy resolve, IDE health probe). Every other swallowing row uses `EgressRefusalWarnings` (event 7307). Neither SPEC-007 nor the #743 body records the exception.

### M3 (major: a false statement in a record, over-conservative). `docs/EgressInventory.md:27` Known limit "MEAI guard faults are not recorded … neither fault is counted or logged, where the HTTP handler counts `GuardFaults`" is false since #742.
- `EgressGuardChatClient.Decide` → `EgressGuard.EvaluateForRoute` (EgressGuard.cs:139-186) counts (`EgressGuardHandler.RecordGuardFault()`) and publishes a synthetic NoDecision record for a throwing or null host guard; `ResolveEgressGuard` logs 7306. #742 edited this file (re-derived line numbers, added the "Route refusals" bullet) and left the 3b-era limit (written in #711). It understates enforcement rather than overstating it; it is still a Known-limit sentence readers rely on.

### m1 (minor). #743 widened `InternalsVisibleTo` on Ashlar.Abstractions by five assemblies (BackgroundAgents, Core.Application, Runtime, Transport.Grpc, Transport.Grpc.Server) and removed the ExperimentalAttribute polyfill for Core.Application (Directory.Build.targets). D9 named two friends. Neither the #743 body, SPEC-007 nor CHANGELOG mentions it; `EgressRefusal` (chain walker, `RemoteMessage`, `IsCode`) is internal, so a third-party host must match `EgressRefusedException`/`ErrorCode` itself.

### m2 (minor, unrecorded behaviour change). `WorkflowExecutor.cs:384-391`: a refusal now aborts the cluster step even when `cluster.Interface.FailurePolicy != FailurePolicy.Abort` (previously `continue`). Fail-closed and defensible; the CHANGELOG does not say a `Continue` policy no longer continues past a refused brick.

### m3 (minor, records). The certification count paragraph (`ci/cert-gate-assertions.md`) still read "144 `.cs` files" at 6d64ad508 although #743 added three (147 .cs / 150 entries); the #743 body made no count claim; master reads 152 (as of 2026-10-10) and its history line skips 147.

### m4 (minor, records). #743 did not touch `docs/EgressInventory.md`, although the design's records table (§4) assigns 4.8 a "refusal section" update; its "remain PR 4.8" sentence was later corrected by 4.9/4.11. SPEC-007 and CHANGELOG do carry 4.8.

### m5 (minor). A governed keyed call routed through the unkeyed router yields two `denied/egress_refused` audit records for one refusal (EgressGuardChatClient.Decide and the router's AuditingChatClient.EmitRefusal).

### m6 (minor). #743 carries `[skip-prod-style]` with a rationale (the gate accepted it); no ProdStyle twin for the refusal routes. Already in the handoff as debt.

## 7. Refuted (not findings)

- "The orchestrator relabels EGRESS_REFUSED as CIRCUIT_OPEN (row 8)": Orchestrator.cs:559-563 returns the refused result before `ExecuteWithCircuitBreakerAsync` (:632) runs.
- "A2A retries a refused send (row 11)": A2AAgentTransport.cs:99 catch precedes the `HttpRequestException when (attempt < attempts)` branch.
- "ProviderFactory's retry retries refusals": `IsProviderCallTransient` is `Network(ex) || ex is ModelUnavailableException`; OllamaProvider rethrows refusals rather than wrapping them in a Result, so `ExecuteOllamaAsync` never wraps one.
- "cert-gate 3,066/3,101 unverifiable": CI job logs for both merge commits show those totals.
- "ToolCallingAgent still ends the cycle with `error` (handoff D26 note)": at ac264559f the cycle ends `egress_refused` (ToolCallingAgent.cs:257, :295); the 4.11-body sentence belongs to the 4.11 lens.

## 8. Not checked / runs

- No container run: the two blockers are code-path findings with no existing test to flip; a decisive probe needs a new twin (ProviderBackedModel over a refusing ProviderFactory under HotSwappableModel; OrchestrationHotSwappableModel with ASHLAR_ALLOW_MOCK=1). Recommend the write-down lens or the follow-up PR write them.
- Not recomputed: 2,149 production files (scanner-only), .NET 10 egress-suite totals (921/956), prod-style 131, TRX hashes.
- Out of lens: rows 17/18/23, the four degrade sites, IDE SSE exit, CLI exit 77 (4.9); whether a host guard's report-mode decision may lower SW, and D6 (4.11).
