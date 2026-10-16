# SPEC-007 — Security labels and the reference monitor (Bell–LaPadula), v1

**Status: PROPOSED — 2026-10-03.** Written for Claude Code to start the work. Owner: @IanFrelinger.

**Start here:** implement **PR 1 (§4)** and stop. Everything after it is listed in §5 so the design
holds together, but each later PR waits for the one before it to merge. Ask the owner about anything
in §8 rather than guessing.

Master at the time of writing: `0960774` (#704 and #705 merged).

## Status (2026-10-13)

*Added when the spec was committed. Everything else is the owner's text of 2026-10-03, with these additions, each
marked: test names next to the §2 MUSTs (labelled **Enforced by**, as §7 asks), a status column in §5, the sections
marked (added 2026-10-04) between §8 and the starting prompt, and a one-line note at the head of the starting prompt.
The status line above and the starting prompt are the owner's, as written; the status line still reads PROPOSED.*

| PR | State | Number and merge commit |
|---|---|---|
| 1: the label lattice and reference-monitor decisions (§4) | Merged | #706, `0f9642ec2` |
| 2: bridge the existing labels | Merged | #707, `f1f2cff48` |
| 3: egress inventory and one guard, report-only | The owner split it into 3a and 3b. Both are merged | 3a: #709, `c257aa684`; 3b: #711, `8ec674d2a` |
| 4: the guard enforces | Merged. 4.1–4.10 earlier; 4.11 is #746. The owner answered its eight questions on 2026-10-05 (decisions log). | #746, `7cb132897` |
| 5: clearances on subjects | First slice merged: #748 (policy ceiling, host may only tighten, skill level on the sealed package). The composition check is a library API with no production caller yet; inbound response bodies are not mediated; receiver-side CanRead is still open. | #748, `ac264559f` |

- **PR 1** added `Ashlar.Abstractions.Security` in `src/Ashlar.Abstractions/Security/`, with 367 cert-gate tests. The
  four §4.4 mutations went red (16, 19, 11 and 49 of 367) and back to green. The §4.6 checklist is left as written;
  #706 and its agent-bus `handoff` and `done` posts on #695 report each item. PR 1's three departures from §4 are
  listed after the decisions log.
- **PR 2** added `TrustTierOrder.RecordLabel` and `CallerLabel`, and `DataSensitivityLabelBridge.ToDataLabel` and
  `TryToClearance`, with 152 cert-gate tests. Nothing calls the bridges yet, so behaviour is unchanged.
- **PR 3a** added the report-only guard in `src/Ashlar.Abstractions/Security/Egress/` and `AddAshlarEgressGuard` in
  `src/Ashlar.Infrastructure/Egress/`. It wrote the inventory down in `docs/EgressInventory.md` and
  `ci/egress-inventory.tsv` (84 rows, 51 of them `Unrouted` until 3b), and added `EgressGuardConventionTests`, which
  fails when an outbound path appears that the inventory does not list. 3a routes nothing: no production composition
  calls `AddAshlarEgressGuard`.
- **PR 3b** routed the 51 rows 3a pinned `Unrouted` and removed that reason, which the convention test now rejects:
  10 are `Factory`, 4 `Upstream:<path>` and 4 `Governance`, and the other 33 are guarded (`Wrapped`, `Precedes`, or
  `Factory` for the 8 registrations; the MeshDiscovery listener beside its guarded beacon stays `Exempt:Inbound`).
  The MCP client's `EgressHttp` client
  adds one row, so the TSV pins 85. HTTP clients go through `EgressHttp` or the factory handler, which `AddAshlar` and
  every other Ashlar member that registers a factory client install (the consumer SDK's `AddAshlarClient` is exempt,
  and Fleet.Host covers the commercial Fleet registration); MEAI targets go through `EgressGuardChatClient`, the
  outermost layer of `UseAshlarGovernance`; and the rest through explicit `Evaluate` calls. It was still report-only
  (by default until PR 4.11, #746; opt-in refusal arrived in PR 4.7, #742).
- **Gaps carried to PR 4** (recorded in #709 and `docs/EgressInventory.md`). The `netstandard2.0` asset of
  `Ashlar.Abstractions` cannot evaluate a synchronous `Send`, so on .NET 5 to 7 a synchronous `Send` went out
  unevaluated. PR 4.2 (below) closes that gap: such a `Send` is refused before anything is sent, with no record
  (the owner's 2026-10-06 amendment of D31). PR 3b found two more (`docs/EgressInventory.md`): redirects that the
  primary handler follows are not evaluated (closed by PR 4.3, #722, `904cf909a`, with the host-boundary exception
  still outstanding — owner O2, decisions log, tracked for PR 4.3b), and a few records could read Host for a
  remote peer (EG-MESH-03 behind a local proxy or tunnel, EG-MDL-01 with a custom `local:` inner client,
  EG-MESH-07/08 with a `//127.0.0.1/…` path). PR 4.1 closes the third gap: those records no longer read Host.
- **PR 4 plan** (2026-10-05). A design pass found that no production code enters an `EgressSubject` frame. Turning
  enforcement on alone would therefore make `AirGapped` and `SecureWorkstation` host-only: every decision is made at
  `SystemHigh`, and the leak test would pass without a label causing the refusal. PR 4 ships as eleven small PRs, in
  this order:
  - 4.1 the records that can read Host;
  - 4.2 the synchronous `Send` gap;
  - 4.3 redirects;
  - 4.4 frame semantics: monotone nesting, `Observe`, read scopes;
  - 4.5 subject producers at the agent runners, report-only (#721). Obligation carried from 4.4: every production
    `EgressSubject.Enter` is a `using` on the flow that enters it (never inside an async helper, whose frame does not
    reach the flow that awaits it), disposed in order, since a flow that disposes frames out of order stays inside the
    outer one (its chain grows with each repetition, and what it reads later raises the subject's shared mark). Work a
    runner creates and starts inside its frame, such as a task or a thread, keeps that frame for as long as it runs,
    also after the `using` ends, so what it reads later still raises the subject's shared mark. Work keeps the frames of
    the flow where it captured the execution context: a task, a `System.Threading.Timer`, a cancellation registration or
    a continuation where it is created, a thread or a `System.Timers.Timer` where it is started. So what is read by work
    created before the frame (a cold task, a continuation, a timer or a registration), or by a thread created inside it
    and started after the `using` ends, never reaches the frame's mark, and the runner would decide below what the work
    it started and awaited has read: a runner creates and starts the work it reads through inside its `using` block. The
    `using` block never spans a `yield return` of an async iterator: each `MoveNextAsync` runs the body on the
    consumer's flow, so after the first `yield return` the frame is gone, and the rest of the block decides at the
    consumer's frames (a write-down wherever they do not hold the subject's mark) while its reads never reach that mark.
    The code that drives the iteration enters the frame around its `await foreach`, or the body enters one for each
    stretch between two `yield return`s. Ashlar's streaming chat clients are such iterators (`GetStreamingResponseAsync`
    in seven production files, `RoutingChatClient` among them), so a runner that streams must follow this;
  - 4.6 mode plumbing, with every profile still reporting;
  - 4.7 and 4.8 the refusal surface, then the catch-alls that would hide a refusal;
  - 4.9 the explicit sites, the operator verbs and child processes;
  - 4.10 `AirGapped` and `SecureWorkstation` hygiene. It fixes two defects found while reviewing 3a: the MCP
    server's HTTP transport is allowed on `SecureWorkstation` (`ValidateAshlarMcpServerOptions` refuses only
    `AirGapped`), and `AirGapped` still registers network paths, such as RunPod as the default remote execution
    target;
  - 4.11 the switch, which carries the §5 leak test. Of the "never refuses" twins it flips,
    `EgressGuardDecisionTests.An_explicit_profile_sets_the_mode_and_does_not_change_the_access_decision` (named
    `An_explicit_profile_is_reported_and_does_not_change_the_decision` until #799 renamed it after the flip) already
    has an `enforce` row from 4.6 (an unrecognised profile fails closed); 4.11 flips its AirGapped and
    SecureWorkstation rows.

  The owner's answers are in the decisions log. Open questions C and D are answered there.
- **PR 4.1** (#717, `bbc5d71`) closes the third gap above, still report-only: mesh serve records `mesh-peer:<ip>`, never Host;
  the MEAI layer records where the inner client dials, else Bedrock's region endpoint, else the fail-closed
  `meai:<key>`; a `file:` destination is never Host; an Ollama model ending in `-cloud` or `:cloud` is recorded at
  `https://ollama.com`; and the default MEAI Ollama client stops following redirects (a behaviour change).
  `OllamaProvider`'s cloud decision is a 17th explicit guard site, which 4.9 must make refuse before the send.
- **PR 4.2** (#719, `ad3d570`): on the `netstandard2.0` asset, on a runtime that has a synchronous `Send` (.NET 5 or later),
  a synchronous `Send` through `EgressHttp` is refused with `NotSupportedException` before anything is sent, the
  factory handler is refused there, and `docs/SdkCompatibilityPolicy.md` says full guard coverage needs `net8.0`
  or later. The refusal publishes no decision record (the owner's 2026-10-06 amendment of the design's D31,
  decisions log): no Ashlar code runs on that path, short of a process-wide first-chance-exception hook, and a
  record published when a client is built would report a refused egress where none happened.
- **PR 4.3** (#722, merge `904cf909a`; hardened in PR 4.3b, #739, `9c56ecc1a`): an unstarted, credential-free known
  primary has `AllowAutoRedirect` false and an internal follower applies its original settings. `EgressHttp`
  follows only the same origin (scheme, host and port; P2); factory clients follow across origins (P1).
  On both routes, Ashlar’s follower returns an outside-to-Host or link-local redirect unfollowed and records the hop (owner O2,
  2026-10-06), including `*.localhost`, unix and npipe. HTTPS to HTTP and non-HTTP schemes are not followed.
  Every followed hop is decided again, including a same-origin hop; `Authorization` is cleared. Credentials,
  unknown primary types, already-started handlers and immutable chains keep their transport's behaviour;
  only a changed final authority can be reported afterwards. Factory binding restores a removed guard,
  mediates composite primaries and avoids duplicate followers; unknown-type warning 7304 is AG/SW-only,
  once per client/type, and a normal logging scope emits no relocation warning. The SNS clients use the
  configure-existing `NeverFollowRedirects` helper, whose named-client request flag also stops an already-shared follower without changing other clients. Bedrock's `RuntimeConfig` disables SDK redirects.
  Decisions stayed report-only by default until PR 4.11 (#746); an explicit enforcing guard could record
  `Refused`, and since PR 4.7 (#742) the route acts on it before sending the refused hop. The netstandard2.0 synchronous-send hop still refuses before the follower runs.
- **PR 4.6** (#718, `3196ba1`), mode plumbing: one resolver gives every decision a mode (`report` or `enforce`), its basis,
  `Refused` and a random `Ref`. `enforce` is an opt-in on every profile through `ASHLAR_EGRESS_MODE` (read once per
  process), `AshlarHostingOptions.EgressMode` (raise-only) or an explicit guard's constructor, and every profile
  still defaulted to `report` (until PR 4.11, #746). The strictest profile noted in the process wins, for `ProcessDefault`, the
  remote-protocol validators and the guard `AddAshlar` binds in place of `ProcessDefault` (or of the guard an
  earlier `AddAshlar` bound in the same collection); tests restore that state through a reset seam, whose callers a
  convention fact pins. The guard refused nothing then (routes act on a refusal from PR 4.7, #742); the netstandard2.0 asset's synchronous-`Send` refusal
  (PR 4.2) is the runtime's and holds in every mode.
- **PR 4.7** (#742, `50efd7019`) implements opt-in refusal at HTTP and governed MEAI routes. `EgressRefusedException` carries the
  operator decision and a redacted message; `Refuses` and `ThrowIfRefused` expose the route contract. HTTP async
  returns a faulted task, synchronous HTTP throws before sending, and MEAI throws before obtaining a task or
  stream. Host guard faults produce `NoDecision` under process policy. DI fallbacks are counted and visible
  (Warning for a throw, Debug for an absent registration). Refusals log at Warning, with five-minute counted
  summaries, EventSource event 2, and the MEAI audit record. Sink failures and re-entrant logging cannot bypass
  refusal. The netstandard2.0 asynchronous route also enforces. Every profile still defaulted to report (until
  PR 4.11, #746).
- **PR 4.8** (#743, `6d64ad508`) preserves refusals through catch-all, availability, fallback and transport paths, including nested
  aggregate exceptions and gRPC debug exceptions. Independent fallbacks may succeed; otherwise the first
  refusal remains the final error. Agent cycles distinguish refused attempts from ordinary policy denials,
  record `egress.refused`, and cap refused tool attempts at three without advancing the world. Catalog,
  health and hosted-loop degradation emits windowed Warning diagnostics (event 7307); disposal cancels
  timers and flushes suppressed counts. Validation keeps completed evidence and does not count prevented
  work as failed tests. A2A, gRPC and MCP server responses carry only fixed text and the random reference.
  The default switch is implemented in 4.11 (#746, `7cb132897`), whose verification status is recorded below.
  **PR 4.8b (#800)** closes masks 4.8 left. A refused Ollama availability probe outside `AdaptiveProviderFactory`
  was dropped, so `ProviderBackedModel` reported "not available" and `HotSwappableModel` answered with the echo
  model. `ProviderFactory` keeps nothing itself: it records the refused probe on the caller's
  `ProviderRefusalScope`, and the scope holds it. Besides `AdaptiveProviderFactory`, `ProviderBackedModel` (which
  throws it) and `BehaviorExecutor` (which reports it) now open one. `BehaviorExecutor` follows D23: a refused
  probe counts as a skipped alternative, and when every alternative of a step failed or was skipped, the first
  refusal is that step's last error event, also after a failing deterministic fallback; it is not appended again
  when the last event written already carried it (and is listed twice when a fallback failed after the refused
  attempt). D23 holds per step: under `FailurePolicy.Continue` later steps' errors follow. A
  deterministic implementation that succeeds after a refusal still completes the step at the event level,
  because it is not the echo model. `IBehaviorExecutor.ExecuteAsync` still returns `Success = false` once any
  step error was emitted; that predates 4.8b.
  Five sites rethrow a refusal instead of answering with canned or pass-through output; the design's rows 1–29
  missed all five:
  - `OrchestrationHotSwappableModel` (`src/Ashlar.Orchestration/Models/`), the wrapper around every orchestration
    agent's model, before its `ASHLAR_ALLOW_MOCK=1` echo fallback;
  - `PlanningAgent`'s canned recommendation, used by the planning, guide, assistant and wizard agents;
  - `SynthesisEngine`'s "Phased approach" fallback resolution;
  - `ModelBackedMaterialIntelligenceService`'s heuristic materials and `ModelBackedVectorMapIntelligenceService`'s
    pass-through, both opt-in (`ReplaceMapIntelligenceWithModelBackedDefaults`).
  The factory's row-24 Warning is windowed (event 7307, flushed when the factory is disposed).
  No echo model and no canned or pass-through output answers a refusal; every model or chat catch-all in `src/`
  was read on 2026-10-13 (17 unfiltered catch-alls, 5 refusal-filtered).   Residuals remain and are listed as dated
  Known limits in
  `docs/EgressInventory.md` ("Catch-alls and availability probes"):
  - status callers report a refused probe as "unavailable" (retained, card #809; Probe API exists via #802,
    but health/onboarding still use `IsProviderAvailable`);
  - `workflow stress` / `workflow optimize` typed refusal results were repaired by card #802
    (`IProviderFactory.ProbeProviderAvailability`; stress fails refused runs; optimize names the refusal);
  - workflow agent-node refusal propagation was repaired by card #803 (`StepErrorEvent` carries cause/code/ref;
    `ExecuteAgentNodeAsync` throws the refusal and honours `BehaviorCompletedEvent.Success`);
  - `BehaviorExecutor`'s outer catch (a throwing outcome recorder or semantic cache, or cancellation, which wins
    by design) ends a step's stream with its own event, not the refusal (retained, card #809);
  - a refusal may still appear twice in `IBehaviorExecutor.ExecuteAsync`'s `Errors` after a failing fallback
    (retained D23 ordering tradeoff, card #809; not #760).
  Card #809 repaired: `ProviderFactoryLocalExecutor` preserves `EGRESS_REFUSED` for wrapped refusals, and
  `adapt`/`improve` dispose their service providers so the probe Warning window flushes.
  Every 4.8b site reaches the internal `EgressRefusal` through the existing `InternalsVisibleTo` list of
  `Ashlar.Abstractions` (`Ashlar.Abstractions.csproj:33-47`, 15 friends, unchanged by 4.8b; Orchestration and
  Infrastructure were already on it). What each friend uses is recorded in `docs/EgressInventory.md`.
- **PR 4.9 (#744, `3023ef1ea`)** acts on all 17 original explicit decisions and the additional
  audited process paths. Named CLI file exports alone carry an internal operator initiator (report/operator-verb);
  public export APIs, publish/share and child execution continue to enforce. Refusal preserves mesh listening,
  local admission, local promotion and existing export bundles while suppressing the refused export. At that merge,
  only the export/package verbs exited 77. PR 4.9b (#765) extends exit 77 to every CLI invocation that observes
  a local refusal or propagates a typed refusal (`docs/EgressInventory.md`). IDE SSE uses fixed remote
  text and a random reference, and optional discovery/pull paths log redacted refusals. Working-tree execution
  is NetworkExport even with --no-build/--no-restore; Docker Host classification requires a local daemon and
  recognized network-off/no-pull run arguments. Docker API build/run/start is separately guarded. F10 requires
  action on explicit decisions; F11 confines the internal evaluated process entry. Local validation passes the
  core build, 3,174 certification tests, 131 production-style tests, 63 CLI egress/audit tests, four net10 API tests,
  the three open-mesh CLI bridge tests and all 28 repository gates, with zero test skips in these selections.
  All 77 cases in `scripts/mutations/spec007-explicit-egress.json` have an intended assertion failure, exact source
  restoration, clean checkout and green rerun; their mutation targets and intended test sources match the candidate.
  PR CI passed and the change merged. Exact-master readiness exposed a Windows Docker fixture response failure. The isolated repair merged in #745 (`d4dfbf379`) after fresh Windows and cross-platform PR readiness passed; the repaired master `d4dfbf379` reads `verified` (readiness run 38069920911, 2026-10-10T17:24:12Z).
- **PR 4.11 (#746, `7cb132897`)** switches AirGapped and SecureWorkstation to default enforcement. The SecureWorkstation report break-glass variable, `ASHLAR_EGRESS_MODE=report`, is environment-only, latched and logged at Warning. A host that registers its own `IEgressGuard` built with another profile or `report` mode still lowers SecureWorkstation's HTTP and MEAI routes, with no latch and no Warning (design D6 applies the process floor on AirGapped only; `docs/EgressInventory.md`, "AirGapped route floor"); explicit sites decide through `ProcessDefault` and keep enforcing. AirGapped ignores lowering overrides and routes apply its process floor even to host guards. Named factory exceptions use `Configure<EgressGuardOptions>` after `AddAshlar`, warn per name at event 7308 `EgressHostClientOptOut` (distinct from event 7307 degrade Warnings), and cannot name Ashlar-owned clients. The registry is checked against production registrations including commercial code. `EgressEnforcementLeakTests` covers A/B/C and C0–C12. All 28 mode, route, client-exception, leak and registry mutations have intended assertion failures, exact source restoration, clean checkouts and passing restored runs, reconciled against the candidate. Local validation passes 3,240 canonical certification tests, 1,091 net8 Egress tests, 1,099 net10 Egress tests, all 28 repository gates and the core build; these test selections have zero skips.
- **PR 4.4** (#716, `de41a8ac`) gives `EgressSubject` frames their semantics. A decision joins every frame the flow is inside,
  live or disposed, at each mark as it is then (fail closed: a parent that ends first never declassifies a task it started),
  and a disposed frame's mark reaches the frames around it; a flow leaves a frame only by disposing its own head
  while that head is undisposed, and goes back to exactly the frame it was entered under, disposed or not, so a flow
  that disposes frames out of order stays inside the outer one (fail closed, at the costs in the 4.5 obligation
  above); `EgressSubject.Observe` raises every frame on the chain;
  a `BeginRead` scope that ends unreported or by an exception counts as `SystemHigh`; and `AgentBus` subscribers run
  with no subject, inside a callback run by the internal `EgressSubject.RunDetached`, which puts the publisher's frame
  back exactly when it returns or throws, before any exception filter of the publisher runs. Work created and started
  inside the callback, such as each subscriber's `Task.Run`, keeps no subject; a task, timer, registration or
  continuation the publisher created and the callback starts or triggers keeps the publisher's frame, since each
  captures the flow where it is created, and a thread or `System.Timers.Timer` captures it where it is started.
  Report-only. Until PR 4.5 no production code entered a frame; the producers are in PR 4.5. **Amended PR 4 design:**
  its `InternalsVisibleTo` list for `Ashlar.Abstractions` (decision D9 in `_handoff/spec-007-pr4/DESIGN-4-final.md` on
  the `claude/spec-007-pr4-workspace` branch: `Ashlar.AI.Pipeline` and `Ashlar.Infrastructure`) gains
  `Ashlar.Orchestration`, for the internal `EgressSubject.RunDetached` at the `AgentBus` dispatch point. The grant
  exposes every Abstractions internal to Orchestration; 4.6's convention fact
  (`ProcessGlobalEnvironmentConventionTests.Only_AddAshlar_and_the_reset_seam_reach_the_process_egress_state`)
  reads every source file, Orchestration's included, so no new caller of the reset seam or the mode latch setters
  appears unlisted.
- **PR 4.5** (#721, `9b2ea56f4`), with **PR 4.5b** (#740, `bc1777dfc`) producer hardening, still report-only.
  `SelfExtendRunnerAdapter.RunAsync` enters `agent:<id>` at `SystemHigh` around the cycle, admission and
  auto-share because its snapshot carries unlabelled carry-over. `ToolCallingAgent` begins a read around every
  tool call. The owner’s 2026-10-06 rule is implemented: every decision inside an open read is `SystemHigh`,
  including a labelled tool’s own egress, until disposal. The read frame stays pinned at `SystemHigh` for work
  that inherited it; in-order disposal observes the result before restoring the enclosing flow. A read frame
  never supplies a subject id. Scenario B now expects `SystemHighData`; it differs from C5 by site and family.
  Only `IEgressLabelledTool` may report through `ReadReporter`, which exposes neither completion nor disposal.
  `RAGTool` is the sole production implementer. It reports the join of canonical hit labels; custom levels and
  unknown spellings are `SystemHigh`. Empty results and the stores’ typed pre-read unrankable-query refusal
  report `Public`; other store exceptions remain unreported. A governed target other than `local:` or `cloud:`
  is treated as agent-backed: completion, faults, synchronous policy denials and each streamed update observe
  `SystemHigh`. Model responses are not reads. Producer convention facts pin `Enter`/`BeginRead`, their floors,
  enclosing methods, in-order `using` shape, absence of iterators, and the labelled-tool implementer list.
- **PR 4.10** (#720, `02fa27f1d`) AirGapped and SecureWorkstation hygiene. The profile reaches Infrastructure through
  `AshlarResolvedDeploymentProfileOptions`, which `AddAshlar` registers from the resolved profile. On AirGapped,
  every remote routing reason runs locally (`AirGapped: remote execution unavailable; running locally (<reason>)`);
  an explicit peer-network-only request fails with an explained error and is not sent to a peer or to RunPod.
  `AdaptiveProviderFactory` does not try OpenAI or Azure on AirGapped, for LLM calls or single-image vision
  (PR 4.10b, #738, also refuses a cloud resolve for multi-frame vision). Boot refuses, on AirGapped
  only, a non-empty `BrickHost:RemoteCatalogBaseUrls`, `Ashlar:RunPod:EnablePeerNetworkRouting=true`,
  `Ashlar:MeshLab:WorkerExecutor:Enabled=true` and `Ashlar:Meai:Bedrock:Enabled=true`. The ollama.com catalog
  defaults off on AirGapped when `Enabled` is unset. SecureWorkstation refuses MCP over HTTP and still boots stdio;
  AirGapped still refuses an enabled MCP server whether or not the transport is HTTP. On both profiles an API
  listener or mesh serve that does not bind loopback fails boot (`127.0.0.1`, `localhost` and `[::1]` succeed).
  PR 4.10b adds bare/mapped IPv6 loopback, port-only settings and post-bind verification. Responses on inbound
  connections are not mediated until PR 5's
  CanRead at the server seams.

- **PR 4.10b** (#738, `bceb1ba21`) hardens the live AirGapped/SecureWorkstation inbound rule. Port-only `http_ports` and
  `https_ports` settings expand to wildcard listeners when `urls` is absent and fail pre-bind validation.
  The API also checks `IServerAddressesFeature` after Kestrel starts, including listeners configured in code; missing/empty published addresses fail closed on AG/SW.
  Bare `::1`, bracketed `[::1]`, and IPv4-mapped loopback are accepted; non-HTTP listener schemes are refused.
  Mesh serve keeps the fail-boot rule for a missing or non-loopback bind; an explicit loopback bind serves locally.
  AirGapped multi-frame vision refuses a nonlocal resolved provider before the inner factory receives frames.
  `PostConfigure` reasserts the strictest profile captured by `AddAshlar`, so later `Configure` callbacks cannot
  lower it. MCP validation detects the SDK's HTTP handler as well as the Ashlar wrapper marker. Certification
  conventions pin the profile-reader boundary and production HTTP MCP callers.
  **Known limits** (durable dispositions in
  [`SPEC-007-disposition-index.md`](SPEC-007-disposition-index.md), card #812): a listener added in code has a window
  between socket binding and the post-start refusal (H10-01); a container composed before a stricter process profile
  was noted keeps its captured profile (H10-02). These options remain mutable objects, and host code that replaces the
  options service or installs a later `PostConfigure` can override them (H10-03). Bedrock still refuses at
  `ValidateOnStart` or first options/keyed-client resolution, rather than during `AddAshlar`/`BuildServiceProvider`
  (H10-04). Responses on inbound connections await PR 5 (H10-05). The rule covers the API, mesh serve, gRPC server
  host and `Ashlar.Commercial.Fleet.Host` (the latter two wired 2026-10-14 for card #766, per the owner's 2026-10-13
  Q6 scope decision). The web hosts validate configured endpoints before bind and compile the same internal post-bind
  verifier from `src/Shared/LoopbackListenerVerifier.cs`.

---

## 1. Why

Ashlar is becoming **one runtime for governed agents**. Every skill follows one lifecycle:

> build → certify → seal (a DLL in-process, or a container out-of-process; source included) → share
> (mesh store; the receiver re-gates) → compose (agents from skills, on the fly) → export (offline
> native bundle or cloud) → run → provision (agents spin up containers, databases and local models
> that register themselves)

The certification gates make a skill behave the same everywhere it runs. **The security model** is what
makes an agent safe to run in an air-gapped or compartmented environment: only data cleared for a
destination may reach it. The model is [Bell–LaPadula](https://en.wikipedia.org/wiki/Bell%E2%80%93LaPadula_model),
the formal model behind the US national-security classification system:

- **No read up.** A subject reads only what its clearance covers.
- **No write down.** A subject cannot write to a destination labelled lower than what it has read.
- **Trusted subjects** (here: certified downgrade skills plus a human sign-off) are the only way data
  moves down.

Ashlar provides *classification-style controls inside the runtime*. It is **not** an accredited
cross-domain solution, and nothing in code or docs may claim it is.

## 2. The model (normative)

### 2.1 Labels

A **security label** is a triple `(Level, Compartments, Caveats)`.

| Part | Meaning | US analogue |
|---|---|---|
| `Level` | Ordered: `Public < Internal < Confidential < Secret < TopSecret` | Classification level |
| `Compartments` | A set of tokens; each one is need-to-know | SCI / SAP compartments |
| `Caveats` | A set of tokens naming dissemination restrictions (`NOWEB`, `NOFORN`, …) | Dissemination controls |

- The five levels and their numeric values (0–4) **MUST** match `DataSensitivityLevels` in
  `src/Ashlar.BackgroundAgents/DataSensitivity/`. That existing model is what this one generalises.
  - **Enforced by** (PR 1, #706, cert-gate):
    `SecurityLabelLevelParityTests.EachLevel_HasTheSensitivityValueOfItsPrimitive`,
    `SecurityLabelLevelParityTests.EachLevel_HasTheNameOfItsPrimitive`,
    `SecurityLabelLevelParityTests.All_ListsTheSameLevels_InTheSameOrder` and
    `SecurityLabelLevelParityTests.TheEnum_HasExactlyFiveMembers_ValuedZeroToFour`.
- Caveats are modelled exactly like compartments: a destination must carry every caveat the data
  carries. That turns a restriction such as "no web" into one lattice rule instead of a second
  mechanism. Allow-lists of the REL TO kind are out of scope for v1 (§8).
- Tokens are uppercase ASCII: `^[A-Z0-9][A-Z0-9_-]{0,63}$`, compared ordinally. Invalid tokens are
  refused at construction.
- **`SystemHigh`** is a distinguished top element. Unlabelled data, an unknown level name, or a label
  that fails to parse **MUST** be treated as `SystemHigh`, so the model fails closed. The existing code
  already does this: `TrustTierOrder.MostRestrictive` in `src/Ashlar.AI.Pipeline/Rag/` and
  `DataSensitivityFallbacks.MostRestrictive`.
  - **Enforced by** (cert-gate):
    - a label that fails to parse, an unknown level name among them (PR 1, #706):
      `SecurityLabelTextFormTests.TryParse_RefusesNonCanonicalText_AndFailsClosedToSystemHigh`,
      `SecurityLabelTextFormTests.ParseOrSystemHigh_MapsEveryRefusalToSystemHigh`,
      `SecurityLabelLatticeLawTests.TryParse_AcceptsOnlyCanonicalText`,
      `ReferenceMonitorDecisionTests.CanRead_UnparseableDataLabel_IsReadableOnlyWithASystemHighClearance` and
      `HighWaterMarkTests.ObservingAnUnparseableLabel_FailsClosedToSystemHigh`;
    - unlabelled or unknown data through the bridges (PR 2, #707):
      `DataSensitivityLabelBridgeTests.UnlabelledData_IsSystemHigh` and
      `TrustTierLabelBridgeTests.AnUnlabelledRecord_IsSystemHigh_AndAnUnknownCaller_IsPublic`;
    - an egress with no subject, whose current label is therefore unknown (PR 3a, #709):
      `EgressGuardDecisionTests.With_no_subject_a_non_host_destination_is_refused_with_SystemHighData`.

    These cover data. A clearance fails closed the other way, to `Public` or a refusal (see the decisions log,
    "Design as merged" and open question A).
- `Public` with no compartments and no caveats is the bottom element.

### 2.2 Order and combination

- **Dominance:** `a ≤ b` iff `a.Level ≤ b.Level` and `a.Compartments ⊆ b.Compartments` and
  `a.Caveats ⊆ b.Caveats`. `SystemHigh` dominates everything. Only `SystemHigh` dominates `SystemHigh`.
- **Join** (the high-water mark, used for derivative labelling): `(max Level, ∪ Compartments, ∪ Caveats)`.
  Anything joined with `SystemHigh` is `SystemHigh`.
- **Meet:** `(min Level, ∩, ∩)`. `x ⊓ SystemHigh = x`.

### 2.3 Rules the reference monitor enforces

- **Simple security (no read up).** `CanRead(clearance, data)` holds iff `data ≤ clearance`.
- **★-property (no write down).** `CanWrite(current, destination)` holds iff `current ≤ destination`.
  Here `current` is the subject's high-water mark: the join of everything it has read in this session.
- **Explained refusals.** Every decision names *why* it was refused: level too low, a missing compartment
  (named), a missing caveat (named), or `SystemHigh` data. These reasons later feed the harness's
  `ExplainedFailure` outcome (`IterationOutcome.ExplainedFailure`) and receipts.

- **Enforced by** (egress, cert-gate): `EgressEnforcementLeakTests` exercises the real RAG/model path (A), Bing tool path (B), factory send and loopback redirect (C). Its positive controls allow Public/Internal sends and Full-profile reporting. C0–C12 cover unknown labels, open/unreported/thrown reads, no subject, nested frames, ignored AirGapped overrides, classification/host faults and process exports. Refusals stop the transport and expose only redacted local observations; operator records retain the explained decision. C12 checks a subsequent send in the same runner frame after the actual agent cycle ends with `error`.
- **Composition is a library check, not yet wired (PR 5 first slice, #748, `ac264559f`).** `SkillComposition.Decide` takes an agent's clearance from `ashlar.policy.yaml`, which a host may only tighten, and the sealed skill's highest level from the package, and decides composing the skill as `CanRead` (`SubjectClearanceCompositionTests`). No production composition path calls it yet, so no running agent is refused on its clearance. These egress tests still supply a test clearance to the RAG filter. Responses on inbound connections are not mediated here.

### 2.4 Canonical text form

Labels need one printable form for configuration, logs and, later, receipts. The suggested form below
uses banner-like separators; change it if a better one emerges, but keep it canonical (one string per
label):

```text
Secret                         level only
Secret//C:ALPHA,BRAVO          with compartments (sorted, ordinal)
Secret//C:ALPHA//K:NOWEB       with caveats (sorted, ordinal)
Public//K:NOWEB                caveats without compartments
SystemHigh                     the top element
```

`Format` followed by `Parse` **MUST** round-trip, and two equal labels **MUST** print identically.

- **Enforced by** (PR 1, #706, cert-gate; in the code `Format` is `ToString` and `Parse` is `TryParse`):
  - the round trip: `SecurityLabelLatticeLawTests.Text_IsCanonicalAndRoundTripsThroughTryParse` (CsCheck,
    `SystemHigh` included), `SecurityLabelTextFormTests.TryParse_AcceptsCanonicalText_AndRoundTrips` and
    `SecurityLabelTextFormTests.TryParse_ReadsEachSpecExample_AsTheLabelItNames`;
  - equal labels print identically:
    `SecurityLabelTextFormTests.EqualLabels_BuiltDifferently_PrintIdentically_AndAreEqual`,
    `SecurityLabelTextFormTests.Tokens_PrintSorted_WhateverOrderTheyWereGivenIn`,
    `SecurityLabelTextFormTests.DuplicateTokens_Collapse` and
    `SecurityLabelTextFormTests.Tokens_SortOrdinally_NotByCulture`.

## 3. What already exists (read before writing)

| Area | Where | Notes |
|---|---|---|
| Five sensitivity levels with flags | `src/Ashlar.BackgroundAgents/DataSensitivity/` | `AllowsExternalLLM`, `AllowsWebSearch`, `RequiresLocalOnly`, `AllowsNetworkExports`; registry; fail-closed fallbacks |
| Retrieval labels | `src/Ashlar.AI.Pipeline/Rag/TrustTierOrder.cs` | Records ranked by tier; blank or unknown ranks as `TopSecret` |
| Egress sanitising | `src/Ashlar.AI.Pipeline/Governance/`, `src/Ashlar.BackgroundAgents/Trust/` | `SanitizingChatClient`, `CloudSanitizationProxy`, a sanitisation audit log |
| Principles | `docs/TrustAndInformationArchitecture.md` | Default restrictive, fail closed, inferred facts inherit the most restrictive label |
| Deployment profiles | `src/Ashlar.Abstractions/AshlarDeploymentProfileEnvironment.cs` | Both `AirGapped` and `SecureWorkstation` refuse the MCP client and A2A; `AirGapped` also refuses the MCP server. `SecureWorkstation` refuses MCP over HTTP and still allows stdio (PR 4.10) |
| Isolation tiers | `src/Ashlar.Abstractions/Execution/AgentExecutionIsolationLevel.cs` | `InProcess`, `OutOfProcess`, `ContainerPooled`, `ContainerPerAgent` |
| Docker sandboxes | `src/Ashlar.Infrastructure/Execution/Sandbox/` | Session runner, command runner, session reaper |
| Sealed skills | `src/Ashlar.Infrastructure/Certification/CertifiedBrickActivator.cs` | Hash check, `IlImportFence`, then `Assembly.Load` |
| Sharing | `src/Ashlar.Manifest/Packaging/` and `PkgCommand.cs` in the CLI | `.ashpkg` export, import and re-gate; `MeshStore`, one door, verify before landing |
| Export | `ExportCommand.cs` in the CLI | `export native` (self-verifying offline bundle), `aws`, `azure` |
| Out-of-process host port | `src/Ashlar.Contracts/Distributed/` | `INativeExecutionHost` has **no implementation on master** (the stub is on `archive/parked-2026-10-03`) |
| Provisioning | `src/Ashlar.Orchestration/Resources/`, `src/Ashlar.Infrastructure/Persistence/PostgresDatabaseProvisioner.cs` | `OrchestrationResourceScope` disposes provisioned resources in a defined order |

## 4. PR 1 — the label lattice and the reference-monitor decisions

Pure, additive, and no behaviour change anywhere else. This PR only introduces the types and proves their
laws.

### 4.1 Where

`src/Ashlar.Abstractions/Security/`, namespace `Ashlar.Abstractions.Security`. Abstractions is the
bottom layer every project references, so skills, agents, stores and channels can all carry a label
later. Today's levels sit in BackgroundAgents, which most of the code cannot reach.

Abstractions targets `netstandard2.0;net8.0;net10.0` with `LangVersion` 12 and nullable enabled.
Follow the patterns already in the project (records and `init` are already used there, for example
`Barriers/BarrierContext.cs`).

### 4.2 Types (suggested names)

- `enum SecurityLevel { Public = 0, Internal = 1, Confidential = 2, Secret = 3, TopSecret = 4 }`
- `sealed class SecurityLabel : IEquatable<SecurityLabel>`
  - `Level`, `Compartments` and `Caveats` (sorted, read-only), and `IsSystemHigh`.
  - Static members: `Public` (bottom) and `SystemHigh` (top).
  - Methods: `Dominates(SecurityLabel other)`, `Join(...)`, `Meet(...)`, and a static `Join(IEnumerable<SecurityLabel>)`.
  - Text form: `ToString()` canonical (§2.4), `TryParse(string?, out SecurityLabel)`, and `ParseOrSystemHigh(string?)`, which fails closed.
  - Structural equality and hash.
- `enum AccessDenialReason { None, LevelTooLow, MissingCompartment, MissingCaveat, SystemHighData }`
- `readonly struct AccessDecision`: `Allowed`, `Reason`, `Detail` (names the missing compartment or caveat).
- `static class ReferenceMonitor`: `CanRead(SecurityLabel clearance, SecurityLabel data)` and
  `CanWrite(SecurityLabel current, SecurityLabel destination)`, both returning `AccessDecision`.
- `sealed class HighWaterMark`: starts at a floor (default `Public`). `Observe(SecurityLabel)` joins a
  label in, `Current` reads it, and `CanWriteTo(SecurityLabel destination)` applies the ★-property. It is
  not thread-safe; say so in its XML doc.

**Public API:** Abstractions runs `PublicApiAnalyzers`, so every new public member goes into
`src/Ashlar.Abstractions/PublicAPI.Unshipped.txt`, or the build fails (RS0016). Unshipped means "not yet
promised", so whether this ships as stable or `[Experimental]` is decided before the next tag. If it
becomes experimental, `docs/SdkCompatibilityPolicy.md` requires a **new** diagnostic id
(`ASHLAREXP002`) with its own row and constant, not a reuse of `ASHLAREXP001`. **Decided 2026-10-13:** it ships in
0.3.0 as `[Experimental]` under `ASHLAREXP002` (decisions log). Applied in #801.

### 4.3 Tests

Put them in `src/Ashlar.Tests.Infrastructure/Tests/Certification/`, so the required `cert-gate` check
runs them. Its filter is `FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Certification`.
CsCheck is already referenced by that project; use it for the laws.

- **Lattice laws (CsCheck generators over labels, `SystemHigh` included):**
  - `Dominates` is reflexive, antisymmetric and transitive.
  - `Join` is commutative, associative and idempotent. It is an upper bound, and the least one: for all
    `c`, `a ≤ c ∧ b ≤ c ⇒ a ⊔ b ≤ c`.
  - `a ≤ b ⇔ a ⊔ b == b`.
  - `Meet` satisfies the dual laws.
- **Extremes:** `SystemHigh` dominates every label, and only itself dominates it; joining with it gives
  `SystemHigh`. `Public` is the bottom.
- **Decision tables:** `CanRead` and `CanWrite` produce each denial reason, and `Detail` names the
  offending token.
- **Text form:** `Format` then `Parse` round-trips. Tokens print sorted. `TryParse` refuses an unknown
  level, a blank token, a lowercase token and whitespace, and `ParseOrSystemHigh` maps every one of those
  refusals to `SystemHigh`.
- **Parity:** `(int)SecurityLevel.X == DataSensitivityLevels.X.SensitivityValue` for all five.
  Tests.Infrastructure already references BackgroundAgents.
- **`HighWaterMark`:** after observing `Secret//C:ALPHA`, a write to `Secret` is refused for the missing
  compartment, and a write to `TopSecret//C:ALPHA` is allowed.

### 4.4 Mutation checks (required by `CLAUDE.md`)

Commit first. Then apply each mutation and show the named assertions go red, with real counts. Restore,
prove `git status --porcelain` is empty, and re-run green. Report each one in the PR body.

1. In `Dominates`, swap `⊆` for `⊇` on compartments.
2. Change the level comparison from `≤` to `<`.
3. Make `Join` intersect caveats instead of uniting them.
4. Flip the operands in `CanWrite`.

### 4.5 Records the PR must update

- `ci/cert-gate-assertions.md`: a row for the new blocking assertion ("security labels form a lattice;
  the reference monitor refuses read-up and write-down"), in the same PR.
- `CHANGELOG.md`: an Unreleased `### Added` entry.
- `docs/knowledge-graph.{json,md}`: regenerate with `python scripts/knowledge-graph/build-knowledge-graph.py`
  **after** `git add`.
- No new project, so neither readiness path list changes.

### 4.6 Done means

- [ ] Builds on all three target frameworks inside the container: `scripts/test-in-container.sh --dirty -- 'dotnet build src/Ashlar.Abstractions/Ashlar.Abstractions.csproj -c Debug --nologo'`.
- [ ] The new tests pass on `net8.0` and `net10.0`: `scripts/test-in-container.sh --filter 'FullyQualifiedName~SecurityLabel|FullyQualifiedName~ReferenceMonitor|FullyQualifiedName~HighWaterMark' --framework net8.0`, then the same with `--framework net10.0`.
- [ ] All four mutations were observed red, with counts in the PR body.
- [ ] `scripts/ci/run-repo-gates.sh` is green, and the knowledge graph is current.
- [ ] The PR body has a Testing section. The testing-strategy gate passes on it.
- [ ] All five required checks are green: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary`.
- [ ] Agent-bus: a `handoff` on `pr-<n>` before merging and a `done` with the merge SHA after
      (`_handoff/bus/PROTOCOL.md`, issue #695).

## 5. After PR 1 (in order; each one small, each one merged before the next starts)

| PR | What | Done when | Status (added 2026-10-04, updated 2026-10-05) |
|---|---|---|---|
| 2 | **Bridge the existing labels.** Map `IDataSensitivityLevel` and `TrustTierOrder` onto `SecurityLabel`. No behaviour change. | Parity tests: same order, unlabelled maps to `SystemHigh`, every existing test green | Merged: #707, `f1f2cff48` |
| 3 | **Egress inventory and one guard, report-only.** List every outbound path: cloud model calls, web search, `MeshStore` publish and `pkg share`, the A2A and MCP clients, HTTP tools, export bundles. Route each through one `IEgressGuard` that evaluates `CanWrite`, and log its decisions. | A convention test fails when a new outbound path bypasses the guard; the inventory is written down | Split by the owner into 3a and 3b. 3a merged: #709, `c257aa684` (the guard, the inventory and the convention test; routes nothing). 3b merged: #711, `8ec674d2a` (routes every listed non-exempt site, report-only) |
| 4 | **Guard enforces.** Switched on per deployment profile; `AirGapped` and `SecureWorkstation` enforce by default. | A seeded leak test (an agent tries to write labelled data down) fails closed with an explained refusal | Merged. 4.11: #746, `7cb132897` (see the status) |
| 5 | **Clearances on subjects.** Agents get a clearance; a sealed skill declares its highest level in the package manifest. | Composing an agent with a skill above its clearance is refused | #748, `ac264559f` (first slice; not wired). Clearances live in `ashlar.policy.yaml`; a host may only tighten them; a missing clearance is `Public`. The skill's highest level is `ExtensionPackage.HighestLevel`, covered by the seal and not by the gate-record signature. `SkillComposition.Decide` refuses a skill above the clearance, but no production composition path calls it yet. Inbound response bodies stay unmediated |
| 6 | **Trusted downgrade.** A certified downgrade or redaction skill plus a gate-store human sign-off is the only thing that lowers a label, and every downgrade gets a receipt. | Downgrade without sign-off is refused; with it, a receipt verifies | Not started |
| 7 | **Provisioned resources inherit labels.** Databases, containers and local models an agent spins up carry its label and compartments, register themselves, and are torn down with the compartment. Teardown gets a receipt. | Tearing down a compartment leaves no resource behind and records the wipe | Not started |
| 8 | **Container execution host.** A real `INativeExecutionHost` built on the Docker sandbox, so a sealed skill can run out-of-process in its compartment. | The same skill gives identical results in-process and in a container | Not started |

Phase 1 cleanup runs alongside and touches none of this: re-sort the project triage against the new
vision, split the cert-gate tests into their own project, and move the commercial code to a private repo
(that move needs the owner).

## 6. Repository rules that will bite (from `CLAUDE.md`)

- **Never run `dotnet` on the host.** Use `scripts/test-in-container.sh`, which needs a clone, not a
  worktree:
  - `scripts/test-in-container.sh` runs the cert-gate filter on net10.0.
  - `scripts/test-in-container.sh --filter 'FullyQualifiedName~SecurityLabel' --framework net8.0`
  - `scripts/test-in-container.sh --dirty -- 'dotnet build Ashlar.sln -c Debug --nologo'`
- **Five required checks**, with strict branch protection. Merges are serial, and a red required check
  blocks every PR.
- `shell-lint` runs `scripts/ci/run-repo-gates.sh`, which auto-discovers `scripts/ci/verify-*` and
  `test-*` scripts and runs them with no arguments.
- **Readiness path lists.** `.github/workflows/full-platform-readiness-gate.yml` holds its path list
  twice. A new *project* needs both copies updated, and a cert-gate test checks they are equal.
- **Layer boundary.** A PR that changes non-test code under `application/` needs `[coordinated-integration]` in its body.
  PR 1 does not touch `application/`.
- **Ownership.** Claude Code writes, merges on green, and posts to the agent-bus. Grok audits each merge
  for drift and posts findings as `ask` / `drift-<pr>`.

## 7. Conventions for this workstream

- The rule from `docs/specs/SPEC-006-keys-and-signing.md` applies: a **MUST** is enforced only where it
  names a passing test. When a PR enforces a MUST from §2, add the test's name next to it here.
- Refusals are explained, never silent. Every `false` carries a reason that a person can read.
- Fail closed: when a label is missing or cannot be parsed, treat it as `SystemHigh`.

## 8. Open questions for the owner (ask; don't guess)

1. **REL TO allow-lists.** Are restriction caveats (§2.1) enough for v1, or are allow-lists needed too?
2. **Compartment names.** Free-form tokens, or registered in a policy pack?
3. **Where clearances are configured.** A policy pack, host options, or both?
4. **Labels in signed records.** Should a label go into the signed certification record (a schema
   change that moves the canonical bytes and the golden corpora), or stay in the package manifest?
5. **Compilation rule.** When does combining many low items raise an output's label, or send it to a
   human?
6. **Model mode.** "Pinned model" or "model picks only among certified skills" is chosen per app. Where
   is that recorded: in the receipt, the manifest, or both?

## Decisions log (added 2026-10-04)

The owner's decisions only, each with the PR it applied to: the ones #707, #709 and 3b list under "Owner decisions
applied", the eight PR 4 answers of 2026-10-05, the PR 4.2 and phase C answers of 2026-10-06, and the answers of
2026-10-13. Those are the next version, the tier of the public API this workstream added to `Ashlar.Abstractions`
(its `PublicAPI.Unshipped.txt`), and three answers to the 2026-10-11 review. The owner did not decide the tier of the
public members it added to other packages. None of these rows answers a §8 question; §8 stands as written. Design
choices that merged with those PRs but were not the owner's are in the next section.

| Date | Applies to | Decision |
|---|---|---|
| 2026-10-04 | PR 2 (#707) | **Out-of-range sensitivity levels fail closed per role.** As data: a value above the top level (4) is `SystemHigh`, and a value below 0 is `Public`. As a clearance: a value above 4 is `TopSecret`, and a value below 0 is refused (no label lies below `Public`). This answers #706's question on custom levels outside 0 to 4. |
| 2026-10-04 | PR 2 (#707) | **Only the level is carried.** A bridged label has no compartments and no caveats, and the four level flags (`AllowsExternalLLM`, `AllowsWebSearch`, `RequiresLocalOnly`, `AllowsNetworkExports`) do not become caveats. This is a documented gap; open question C says where it goes next. |
| 2026-10-04 | PR 3 (3a, #709) | **The guard covers every factory client (PR 3 design question 1, answer A; not §8 Q1).** The egress handler sits on every `IHttpClientFactory` client, including clients the host registers, and each decision carries the site `factory:<client name>`. |
| 2026-10-04 | PR 3 | **PR 3 is split.** 3a adds the guard, the written inventory and the convention test, and routes nothing. 3b routes every listed site through the guard. |
| 2026-10-05 | PR 3 (3b) | **The MeshDirector client is routed (3b question Q-A, answer A).** `Ashlar.Commercial.MeshDirector` takes a ProjectReference to `Ashlar.Abstractions` and builds its client with `EgressHttp`, behind a characterization test committed first, which must show the JSON request bodies and printed output byte-identical. |
| 2026-10-05 | PR 3 (3b) | **Open question C is deferred to PR 4 (3b question Q-B, answer A).** 3b merges with C open. |
| 2026-10-05 | PR 4 (Q1) | **`AirGapped` and `SecureWorkstation` enforce when the switch (4.11) merges.** On `SecureWorkstation` only, a break-glass `ASHLAR_EGRESS_MODE=report` returns to report-only. It is read once at startup, logged at Warning and stamped on every decision. `AirGapped` ignores every override. **Implemented in:** #746 (`7cb132897`). |
| 2026-10-05 | PR 4 (Q2) | **A subject's label comes from the runner.** The code that builds an agent's inputs enters a frame at the floor it can vouch for. A runner may declare a floor below `SystemHigh` only for inputs it built and can vouch for. Every tool result is observed: a labelled RAG hit counts at its tier, and an unreported read counts as `SystemHigh`. Self-extend declares `SystemHigh`. |
| 2026-10-05 | PR 4 (Q3) | **Child processes that run code an agent can write are not Host.** That covers `dotnet` build, test, run, pack and publish, `forge test`, the regression runner and the instance spawner. They are recorded as process exports. Docker counts as Host only with `--network=none`. On `AirGapped` and `SecureWorkstation` they are refused unless they run in the network-off docker sandbox. |
| 2026-10-05 | PR 4 (Q4) | **Before PR 6, an operator may move files off an `AirGapped` or `SecureWorkstation` host, and nothing else.** `pkg export --out`, `export` and `mesh export` run in report mode, recorded as an operator verb. `pkg publish` and `pkg share` are refused, because the mesh store can be a network mount. Everything an agent can reach is enforced. |
| 2026-10-05 | PR 4 (Q5) | **Every factory client is enforced, the host's own included.** This answers the question 3a left to PR 4: the 2026-10-04 PR 3 row puts the guard on host-registered clients, and PR 4 decides whether they are enforced. On `SecureWorkstation` and on opt-in profiles, a host may list named clients as report-only through `Configure<EgressGuardOptions>`. `AirGapped` ignores the list, and naming one of Ashlar's own clients fails boot. **Implemented in:** #746 (`7cb132897`). |
| 2026-10-05 | PR 4 (Q6) | **Inbound surfaces stay on loopback on `AirGapped` and `SecureWorkstation` until PR 5 mediates responses.** On `SecureWorkstation`, MCP over HTTP fails boot; stdio stays. On both profiles the API's listeners and mesh serve must bind loopback, or boot fails. |
| 2026-10-05 | PR 4 (Q7, open question D) | **A refusal names its category and nothing about the data's label.** The refused subject (the model, agent memory, the exception message) gets the reason category, site, family, destination class and a random reference. Operators get the full `Detail` and the sequence number. Remote parties get a fixed text and the reference. |
| 2026-10-05 | PR 4 (Q8, open question C) | **v1 labels carry the level only.** The four sensitivity flags are not caveats, and PR 4 maps only the five canonical level names. The first producer that labels data from a custom `IDataSensitivityLevel` applies a fail-closed normalisation: the lowest built-in level whose flags are no more permissive. `ORCON` and REL TO stay out of scope, and §8 Q1 stays open. |
| 2026-10-06 | PR 4 (4.2) | **A synchronous `Send` refused on the `netstandard2.0` asset leaves no decision record; the exception is the only signal.** This amends the PR 4 design's default D31, which wanted the hop to publish a `NoDecision` record so the refusal reaches the operator log. No Ashlar code runs when the runtime refuses that `Send`, so a record could only be published when a client is built, which would claim a refused egress where none happened, or from a process-wide `AppDomain.FirstChanceException` hook, which runs on every exception in the host. The `NotSupportedException` reaches the caller, whose own error handling logs it. The accepted cost: under `AirGapped` or `SecureWorkstation` enforcement, such a refusal never appears in Ashlar's egress log. Ashlar's own hosts bind `net8.0` or `net10.0` and are unaffected; only an app that binds the `netstandard2.0` asset on .NET 5 or later is. |
| 2026-10-06 | PR 4 (4.5) | **While a read scope is open and unreported, every egress decided on the flows inside it is decided at `SystemHigh`, for every tool, `RAGTool` included (no labelled-tool exemption).** This answers the read-scope question the PR 4.4 phase carried to PR 4.5: a scope observes its result only when it ends, so without this a tool that reads and then egresses within one call was decided at the mark from before the read. Once the scope ends, the frames it was begun in hold what it observed: the reported label if it completed and reported, else `SystemHigh`, as PR 4.4 already does. The accepted cost: under a runner floor below `SystemHigh`, a tool's own egress during its call is refused once the guard enforces. In the PR 4 design's §5 leak test, Scenario B's expected reason changes from `LevelTooLow` to `SystemHighData`; control C4's inner send during a tool call is decided at `SystemHigh` too. Scenario B and control C5 then share a reason and differ by site and family, which the leak test must assert. **Implemented in:** PR 4.5b (#740, `bc1777dfc`; #721 left the 4.4 observe-on-dispose rule and pinned it). |
| 2026-10-06 | PR 4 (Q8 clarified) | **In PR 4, RAG maps a custom `IDataSensitivityLevel` to `SystemHigh` (D15 stands); Q8's C3 normalisation ("the lowest built-in level whose flags are no more permissive") is deferred** to the first producer that labels data from a custom level on purpose. **Implemented in:** #721 (`RAGTool.MapHitLabel`: non-canonical levels → `SystemHigh`). |
| 2026-10-06 | PR 4 (4.3, O2) | **A redirect hop from outside the host boundary into it is never followed.** When the redirect follower holds a 3xx whose `Location` names the host boundary (loopback, `localhost` and `*.localhost`, `unix`, `npipe`, as a decision classifies Host, plus a link-local address) and the request came from an authority outside it, the hop is not followed, for factory (P1) clients as much as for `EgressHttp` (P2): the hop is decided and the 3xx is returned. Reason: a remote peer must not bounce a request, body included, to a local service. Cost: a host client behind a remote that legitimately redirects to a local service gets the 3xx. The rest of P1 stands. **Implemented in:** #739 (`9c56ecc1`). |
| 2026-10-13 | Next release | **The next release is 0.3.0.** It is the first tag that carries the `AirGapped` and `SecureWorkstation` enforce switch (#746). |
| 2026-10-13 | PR 1, PR 3a to PR 5 (public API, §4.2) | **The unshipped `Ashlar.Abstractions.Security` and `Ashlar.Abstractions.Security.Egress` surface ships in 0.3.0 as `[Experimental]`**, under a new diagnostic id, `ASHLAREXP002`. That is 150 lines of `src/Ashlar.Abstractions/PublicAPI.Unshipped.txt` at `4287a18a1`. As §4.2 and `docs/SdkCompatibilityPolicy.md` require, the id gets its own constant and its own policy row; it is not a reuse of `ASHLAREXP001`. It is applied before the 0.3.0 tag. |
| 2026-10-13 | PR 4.10 (#720, public API) | **`AshlarResolvedDeploymentProfileOptions` ships in 0.3.0 as `[Experimental]` under `ASHLAREXP002` too.** It is the deployment-profile type that PR 4.10 added to `Ashlar.Abstractions`: the other 10 unshipped lines (the profile name, the `full`, `air-gapped` and `secure-workstation` constants, `IsAirGapped`, `IsSecureWorkstation` and `RequiresLoopback`). It is applied before the 0.3.0 tag. |
| 2026-10-13 | Records (review Q-A) | **The 2026-10-08 D16 row and the 2026-10-10 §8 Q3 and Q4 rows were not the owner's decisions.** #740 and #748 wrote them into this log as owner decisions, citing no owner answer (`REVIEW-2026-10-11.md` §3, BL1 and BL2, on the storage branch `claude/spec-007-pr4-workspace`). They are struck from this log and kept under "Design as merged" as integrator defaults, with their merge SHAs. §8 Q3 and Q4 stay open. The code they describe is unchanged. |
| 2026-10-13 | PR 4 (Q6 scope; review Q-C, card #766) | **The loopback-or-fail-boot rule on `AirGapped` and `SecureWorkstation` also covers the gRPC server host (`Ashlar.Transport.Grpc.Server.Host`) and `Ashlar.Commercial.Fleet.Host`.** The 2026-10-05 Q6 row named the API and mesh serve only. Both hosts are wired before the 0.3.0 tag. |
| 2026-10-13 | Release (public API) | **`[Experimental]` symbols stay in `PublicAPI.Unshipped.txt` across the 0.3.0 tag.** The promotion moves only non-experimental lines into `PublicAPI.Shipped.txt`, so a Shipped line stays a promise. A symbol promoted to stable later loses its attribute and moves at the next tag. |
| 2026-10-14 | PR 4.9b (#765, review question (b)) | **Every CLI egress refusal exits 77**, not only the export and package verbs. The redacted refusal message is preserved. The owner selected this scope in the phase B chat on 2026-10-14; implemented by PR 4.9b (#765). This gives scripts one refusal exit code across commands; callers relying on another refusal code must update. |
| 2026-10-14 | PR 4.9b (#765, review question (a)) | **`docker exec` into an Ashlar-started `--network=none` session remains a documented limitation.** The owner selected this in the phase B chat on 2026-10-14. It is not recognised as Host without a verifiable chain; the existing fail-closed classification remains. Cost: under enforcement, self-extend cannot build a candidate through `exec` in its own sandbox. |
| 2026-10-15 | PR 4.11 / card #763 (review question (d)) | **The C12 adaptation in the leak test is accepted.** The runner ends an ordinary tool exception with `error`; the test checks the next send in the same frame (refused with `SystemHighData`). The owner selected accept on 2026-10-15, not a runner change to continue after an ordinary tool exception (`REVIEW-2026-10-11.md` §10 new (d) on `claude/spec-007-pr4-workspace`). Behaviour has been on master since #746 (`7cb132897`); this row records the owner's accept. Storage-branch `DESIGN-4-final.md` §5 C12 is amended under card #763. |

## Design as merged (added 2026-10-04)

These are not owner decisions. They are design choices that merged with PR 2 (#707) and PR 3a (#709), and, below
them, the integrator defaults of later PRs: the three rows struck from the decisions log on 2026-10-13 and four
defaults of PR 4.10 and 4.10b. They stand until a later PR or the owner changes them.

- **Where a decision is published** (#709). The guard itself publishes each decision to `EgressDecisionLog`, which
  writes it to the `Ashlar-Egress` EventSource and to every subscribed `IEgressDecisionSink`. The Debug log line is
  not the guard's: `AddAshlarEgressGuard` subscribes an `ILogger` sink that logs each decision at Debug under the
  `Ashlar.Egress` category (`EventId` 7300), so those lines stay hidden unless an operator turns that category on.
  Since PR 3b, `AddAshlar` and every Ashlar registration member that adds a factory client call
  `AddAshlarEgressGuard`, except the consumer SDK's `AddAshlarClient` and the commercial Fleet registration, which
  Fleet.Host covers; so every Ashlar host subscribes that sink, when it starts or when it first builds a factory client.
- **No subject means `SystemHigh`** (#709). With no active `EgressSubject` frame, the current label is `SystemHigh`
  with basis `no-subject`. This follows the owner's §7 rule: when a label is missing, treat it as `SystemHigh`. So a
  destination inside the host boundary is allowed, and every other one is decided as refused with `SystemHighData`.
  The decision is only recorded: in 3a and 3b the guard never blocks a send.
- **A clearance with no level is `Public`** (#707's mapping table, not its list of owner decisions).
  `TryToClearance` maps no level to `Public`, and `TrustTierOrder.CallerLabel` maps a blank or unknown caller to
  `Public`. Data with no level is `SystemHigh`, as §2.1 and the PR 2 row of §5 require.

Struck from the decisions log on 2026-10-13, because the owner did not make them (decisions log, "Records"):

- **D16 scope (PR 4.5b, #740 `bc1777dfc`; integrator default, 2026-10-08).** Every governed target other than
  `local:` and `cloud:` (trimmed, case-insensitive; unknown and null keys included) is treated as agent-backed and
  observes `SystemHigh` on completion, fault, synchronous throw, before each streamed update and at stream end, until
  PR 5 carries labels on responses. Host clients registered under `local:` or `cloud:` stay a trusted host boundary,
  and the legacy three-argument targetless `EgressGuardChatClient` overload keeps its non-reader behaviour. The
  integrating agent took this fail-closed, on the 2026-10-08 review's recommendation; the review's Q6 stays open.
- **Where clearances live (PR 5 first slice, #748 `ac264559f`; integrator default, 2026-10-10).** Clearances are
  read from `ashlar.policy.yaml` (`clearances.default`, `clearances.agents`; `SubjectClearance.Resolve`,
  `PolicyClearances`); the per-agent map overrides the default. A missing clearance is `Public`, `AshlarHostingOptions.Clearance` may only tighten it (the
  meet), and an unparseable host value fails closed to `Public`. No production path reads them yet. §8 Q3 ("a policy
  pack, host options, or both?") stays open.
- **Skill level on the sealed package (PR 5 first slice, #748 `ac264559f`; integrator default, 2026-10-10).**
  `ExtensionPackage.HighestLevel` is covered by the package seal, not by the gate-record signature, and a missing
  level is `SystemHigh`. §8 Q4 stays open until the owner decides whether a label enters the signed certification
  record (PR 6 downgrade receipts).

The four bullets below were added on 2026-10-13 too, by #799. Each is an integrator default that PR 4.10 or 4.10b
took where the code needed a choice; none is an owner decision, and each awaits the owner.

- **Mesh serve fails boot without a loopback bind (PR 4.10, #720 `02fa27f1d`; PR 4.10b, #738 `bceb1ba21`; integrator
  default, 2026-10-08 review question 1 open).** On AirGapped and SecureWorkstation, an unset or non-loopback
  `ASHLAR_MESH_SERVE_BIND` throws before mesh serve listens rather than being rewritten to loopback
  (`application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshServeService.cs:157-166`), the literal reading of the
  2026-10-05 Q6 row.
- **AirGapped multi-frame vision refuses a non-local resolved provider (PR 4.10b, #738 `bceb1ba21`; integrator default,
  2026-10-08 review question 4 open).** `AdaptiveProviderFactory.ExecuteVisionMultiFrameAsync` throws before the inner
  factory receives a frame when the resolved provider is not local
  (`src/Ashlar.Infrastructure/Execution/AdaptiveProviderFactory.cs:141`).
- **The resolved profile is reasserted after every `Configure` (PR 4.10b, #738 `bceb1ba21`; integrator default,
  2026-10-08 review question 5 open).** `AddAshlar` sets `AshlarResolvedDeploymentProfileOptions.Profile` in both a
  `Configure` and a `PostConfigure`, so a host's later `Configure` cannot lower it
  (`src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs:139-141`); replacing the options service or a later
  `PostConfigure` still can (4.10b Known limits).
- **Bedrock on AirGapped is refused at options validation, not at composition (PR 4.10, #720 `02fa27f1d`; recorded by
  PR 4.10b, #738 `bceb1ba21`; integrator default, awaiting the owner).** `ValidateAirGappedMeaiBedrockOptions`
  refuses `Ashlar:Meai:Bedrock:Enabled=true` at `ValidateOnStart` or the first options or keyed-client resolution,
  rather than during `AddAshlar`/`BuildServiceProvider` (`src/Ashlar.AI.Pipeline/ValidateAirGappedMeaiBedrockOptions.cs:22`);
  the 4.10 lane's composition-time refusal did not merge.

Report-only is not listed here or in the decisions log, because it is neither: it is this spec's own wording, in the
PR 3 row of §5; report-only by default held until PR 4.11 (#746), and opt-in refusal existed from PR 4.7 (#742). `EgressGuard.Evaluate` records each decision and never throws or
blocks: it returns the decision, and the routes act on a refused one before they send (`ThrowIfRefused`): HTTP and
governed MEAI routes since PR 4.7 (#742), explicit sites since PR 4.9 (#744).

## Where PR 1 departs from §4 (added 2026-10-04)

These are not owner decisions. #706 listed them under "Where this goes beyond the spec, and why", and they stand
while the API is in `PublicAPI.Unshipped.txt`.

- **`HighWaterMark.Observe` is atomic.** §4.2 says the class is not thread-safe and the XML doc should say so. A lost
  join leaves the mark below what was read, which permits a write down, and agents make parallel tool calls. So
  `Observe` joins with a compare-and-swap loop, at no cost to callers. `CanWriteTo` still judges a snapshot of the
  mark, and the docs say so. Pinned by `HighWaterMarkTests.ConcurrentObservations_AreNeverLost`.
- **`AccessDenialReason.NoDecision` is appended**, as value 5, after the five reasons §4.2 lists. It makes
  `default(AccessDecision)` a refusal that still names a reason, so `Reason == None` holds exactly when a decision
  is allowed, and a `switch` on the reason can never treat an uninitialised field as a grant. Pinned by
  `ReferenceMonitorDecisionTests.DefaultAccessDecision_FailsClosed` and
  `ReferenceMonitorDecisionTests.AccessDenialReason_ValuesArePinned_AndOnlyAppended`.
- **A label crosses JSON and `TypeDescriptor` only as its canonical text.** A public `SecurityLabelJsonConverter` and
  an internal `TypeConverter` write the §2.4 string and refuse anything else, `null` included. Without them, a
  serializer that rebuilds a label from its fields would turn `SystemHigh` into `TopSecret`. The converter is public
  so that System.Text.Json source generation can use it. Pinned by
  `SecurityLabelTextFormTests.Json_RefusesAnythingButCanonicalText`,
  `SecurityLabelTextFormTests.Json_KeepsSystemHighAsTheTopInsideAnObject` and
  `SecurityLabelTextFormTests.TypeConverter_RefusesNonCanonicalText`.

## Open questions raised since §8 (added 2026-10-04)

§8's six questions are still open. #706 raised the ones below, and #707 carried them forward unchanged. C and D were
answered on 2026-10-05 for PR 4; A, B and E are still open, and none blocked a merged PR. They are lettered so they do not collide with §8's numbers.

- **A. A receiver-side fail-closed helper, and the shape of `TryParse`.** `SystemHigh` is the safe fallback for
  data. For a clearance or a write destination it is the most permissive value: a `SystemHigh` clearance reads
  everything, and a `SystemHigh` destination receives everything. The XML docs say those must fall back to `Public`,
  but the API offers only `ParseOrSystemHigh`. The options #706 gave:
  - (a) add a receiver-side helper, for example `ParseOrPublic`, mirroring `TrustTierOrder.ResolveCaller`;
  - (b) change `TryParse` to `[NotNullWhen(true)] out SecurityLabel?`, so every caller has to choose (this needs a
    polyfill on `netstandard2.0`);
  - (c) leave it as it is and decide in PR 3.

  Both API reviewers on #706 called this the one hard-to-change item. PR 3a did not need it, because the guard
  classifies a destination by a fixed table instead of parsing it, so the question is still open. Option (a) landed
  in #748 as `SecurityLabel.ParseOrPublic` (a design choice, not an owner answer), which `SubjectClearance.Resolve`
  uses for the policy clearance (`SubjectClearance.cs:30`, its only production call). Wiring a production composition
  path, and `CanRead` at the server seams, is PR 5's remaining work.

- **B. `SystemHigh` versus today's `TopSecret` visibility.** Through the bridges, unlabelled data is `SystemHigh`,
  so only a `SystemHigh` clearance can read it, whereas today a `TopSecret` caller can. The bridges never give a
  clearance `SystemHigh`. Who, if anyone, holds `SystemHigh`? PR 2 pins the difference on both sides, the legacy
  allow and the label refusal:
  - for trust tiers, both in one test:
    `TrustTierLabelBridgeTests.TheDivergenceCell_AnUnlabelledRecord_IsServedToTopSecretToday_ButRefusedOverLabels`;
  - for data sensitivity, one test per side:
    `DataSensitivityLabelBridgeTests.IntendedDivergence_LegacyResolvesUnlabelledData_ToALevelATopSecretAgentCanRead`
    (legacy) and
    `DataSensitivityLabelBridgeTests.IntendedDivergence_BridgeLabelsUnlabelledData_SystemHigh_WhichATopSecretClearanceCannotRead`
    (labels).

  So the PR that switches reads to labels flips the legacy-side assertions on purpose, by name. That is PR 5, not
  4.11: PR 4 enforces egress only, and nothing calls the bridges yet.

- **C. The scope of caveats** (related to §8 Q1). Treating caveats like compartments is sound for eligibility
  markings such as `NOFORN` and `NOWEB`. It cannot express originator-controlled markings such as `ORCON`, and REL TO
  needs a different encoding. Whether the four level flags become caveats (see the PR 2 decision above) is a related
  question. #707 placed it with §8 Q2 (compartment names: free-form tokens or registered in a policy pack; #707
  calls it the caveat-registry question, though §8 Q2 asks about compartments), to be settled before PR 3. It was not decided before 3a or 3b merged:
  on 2026-10-05 the owner let 3b merge with it open, so it is now due before PR 4. **Answered 2026-10-05 (PR 4 Q8):
  v1 labels carry the level only; see the decisions log.**

- **D. Redacting refusal details.** A read refusal's `Detail` names the data's level and compartments to whoever
  receives it. The XML docs now say `Detail` is for audit and operators only. Should PR 3 or PR 4 give the refused
  subject a redacted explanation instead? **Answered 2026-10-05 (PR 4 Q7): the refused subject gets the reason
  category, site, family, destination class and a random reference; `Detail` stays with operators. See the decisions
  log.**

- **E. The SPEC-007 number clash.** `docs/specs/SPEC-006-keys-and-signing.md` (line 551, §5) cites "SPEC-007's
  honest sentence" for the certificate claim. That is a different SPEC-007, and no file for it is checked in. Should
  this spec keep the number, with that line in SPEC-006 reworded, or take another number?

  Until this is answered, a reader who looks up SPEC-006's citation finds this file, which has no such sentence.
  That is a known drift item, left in place on purpose because either answer changes it. `GateSignatureResidualTests` and
  `GateStoreAnchorProvenanceConventionTests` read SPEC-006, so the cert-gate has to be re-run after that line is
  edited.

---

## Starting prompt for Claude Code

*(Added 2026-10-04: historical, not a live instruction. PR 1 merged as #706; see the status block for what is next.)*

> You're working in IanFrelinger/Ashlar. Read `CLAUDE.md`, then this spec. Implement **PR 1 (§4)**
> exactly: the label lattice and reference-monitor decisions in `Ashlar.Abstractions`, plus the tests,
> the four mutation checks, the cert-gate assertion row, the CHANGELOG entry and the regenerated
> knowledge graph. Build and test only through `scripts/test-in-container.sh`. Open the PR with a
> Testing section that reports each mutation with its counts, post the agent-bus `handoff`, merge when
> all five required checks are green, post `done`, and stop before PR 2. If anything in §8 blocks you,
> ask me instead of choosing.
