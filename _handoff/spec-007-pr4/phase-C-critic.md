# Phase C completeness critic: 4.3 redirects, 4.5 producers, 4.10 AG/SW hygiene

Read-only, 2026-10-06, against master `de41a8ac8d0d1ae94f075c7aba2dde366c890fec` in `/home/user/Ashlar`
(`git status --porcelain` empty). Inputs: `DESIGN-4-final.md` (owner answers `:1156-1172` override §3.A; phase B
superseded D9, D31 and `Detach()`), `handoff.md` §3/§4/§6, `pr-4.4-body.md`, `pr-4.6-body.md`, the seven scout files,
and the three lane drafts (`pr-4.3-body.md`, `pr-4.5-body.md`, `pr-4.10-body.md`, read only to find cross-lane
conflicts; nothing in a lane branch was read). Every `file:line` is master as I read it today. **[unverified]** marks a
claim I could not settle by reading. Gaps are numbered **G1…G19** (11 uncovered requirements in §1, 8 cross-lane interactions in §5.2; the nine
contradictions in §2 are C1…C9); the structured summary counts the 19 gaps.

Counts I recomputed on master, which the scouts quote correctly: TSV 86 rows / 150 occurrences / 48 guarded, reasons
33 `-` / 35 `Exempt` / 10 `Factory` / 4 `Upstream` / 4 `Governance` (`awk` over `ci/egress-inventory.tsv`);
Certification folder 131 `.cs` / 134 entries (`git ls-files`); 67 `| EG-` rows in `docs/EgressInventory.md`; floors
comment 2,135 / 150 / 67 (`EgressGuardConventionTests.cs:203-205`); knowledge graph declared facts 4874
(`docs/knowledge-graph.md:23`); production `EgressSubject.Enter(` call sites: none; `RAGTool` named in production only
by `src/Ashlar.BackgroundAgents/RAG/RAGTool.cs`.

---

## 1. Design requirements of each §4 row with no attack item and no records item

The scouts cover the §4 rows well; the uncovered requirements are at the edges, and two of them matter.

### 4.3 (DESIGN §4 row `:853`; §2.6 Gap 2 `:503-565`)

| # | Requirement | Where it is in the design | Coverage | What a verifier should add |
|---|---|---|---|---|
| **G1** | `CreateClient(inner)`/`Wrap(inner)` "walk to the end of the chain and flip" when the chain's tail is a `DelegatingHandler` whose `InnerHandler` is still `null` at wrap time (set later by the caller) | §2.6 R-a `:507-509`; `Wrap` remarks forbid replacing `InnerHandler` only (`EgressHttp.cs:93-100`) | No attack item (items 6, 8, 9 cover composite, started and subclass primaries), no records item | A twin: `EgressHttp.Wrap(new PassThrough { InnerHandler = null })`, then the caller sets `InnerHandler = new HttpClientHandler()`; expect fail closed (treated as unknown: post-send check fires, or `Wrap` refuses a chain with no primary). Not a production shape today (`DefaultGrpcChannelFactory.cs:76` wraps a configured `HttpClientHandler`), so a Known-limit sentence is acceptable |
| G2 | "dispose intermediate responses" (runtime parity) | §2.6 R-b `:530` | No item asserts an intermediate response was disposed (items 11-13 assert status, method, body, headers) | A twin with a stub whose `HttpResponseMessage.Content` records disposal; a leaked content stream on a 30-hop chain is availability, not confidentiality, so a records sentence suffices |
| G3 | Cancellation between hops: a token cancelled after hop 1 must not send hop 2 (and whether hop 2's decision is recorded before the cancellation check) | Not stated; implied by "follow as the runtime would" | None | A twin: cancel in the stub after hop 1; expect `OperationCanceledException`, the stub saw 1 request, decisions = 1 or 2 but the PR body says which |
| G4 | "A redirect between two external hosts leaves the label and the verdict the same; only the recorded host is wrong" (`docs/EgressInventory.md:15`) becomes false wording once hop 2 can be `Host` (item 16) or `WebSearch`/`Confidential` | records-4.3 R13 rewrites `:15` but does not name this sentence | Partly covered | Make R13 drop or invert that sentence: after 4.3 a cross-host hop can change the class (remote → loopback is `Host`, `ReferenceMonitor.cs:53-54`) |

Covered well: R-a for every primary (items 1, 2, 8, 9, 14, 17, 18), per-authority re-evaluation (3, 4, 5, 26),
innermost placement and the gRPC chain (21), the `Clear()` re-insert and the `[0]` question (10, §3.3), unknown
primaries and the post-send check (6, 7), P1/P2 (2, 13, 16, 19, 25), parity (11, 12, 13, 23, 24), the ns2.0 hop
(20, R14, R15, R24, R53), both SNS sites (14, 15, R18, R19, R48), IVT (§3.7, R4), records (R1-R61).

### 4.5 (DESIGN §4 row `:855`; §2.2 `:180-297`)

| # | Requirement | Where | Coverage | What a verifier should add |
|---|---|---|---|---|
| **G5** | D14 must hold against the labelled tool itself: "a tool scope that ends by exception observes SystemHigh". `ReadScope.Complete()` and `Dispose()` are public (`ReadScope.cs:72`, `:78`; `PublicAPI.Unshipped.txt:59-60`). The lane hands the whole `ReadScope` to `ILabelledTool.InvokeLabelledAsync` (`pr-4.5-body.md`, Changes). A labelled tool that calls `read.Complete()` itself, then throws, leaves a completed-and-reported scope: the agent's `using` observes the reports, not SystemHigh | §2.2 `:225-229`; D14 `:813` | attack-4.5 item 4(b) tests "Report(Public) then throw" **without** the tool calling `Complete`; item 9 covers wrappers; the model's B4 control (`REPORT.md` §3) is about `Report` before `Complete`, not a tool-side `Complete` | **Pull forward (§6).** Either hand the tool a report-only surface (an interface with `Report(label)` only), or have the agent track completion in a field it owns and ignore the scope's `_completed`, and add the twin: a labelled tool that reports `Public`, calls `Complete()`, throws → next model call `SystemHighData`. Mutation: let the scope's own `Complete` count |
| G6 | D17 "a session is one runner run; persisted state coming back is SystemHigh" | §2.2 Session `:278-281`; D17 `:816` | No attack or records item. On master `ToolCallingAgent` only writes memory (`ToolCallingAgent.cs:226`, `:275`; no read), so the only persisted-state path is the runner's snapshot (`SelfExtendRunnerAdapter.cs:233-240`), which sits under the SystemHigh floor | No code is possible in 4.5 (the floor is the mechanism); the producers section must say "persisted state is covered by the runner's floor; a runner below SystemHigh must observe what it loads", otherwise D17 is a sentence nobody can check |
| G7 | §2.2 table row "Snapshot and system prompt: the runner's declared floor" and the Q2 statement "a runner may declare a floor below SystemHigh only for inputs it built and can vouch for" | `:250-257`; owner Q2 (`SPEC-007:418`) | records-4.5 1.8 quotes Q2; nothing pins that the leak skeleton's snapshot carries no carry-over | The leak skeleton (4.11) should build its snapshot from literals only; name this in the 4.5 body as the obligation the test runner meets |
| G8 | "No production outcome changes. Records go from `no-subject` to `subject:agent:<id>` at the same SystemHigh" is a records-diff claim | `:194-196` | attack-4.5 B.5 names the twin shape; the lane body claims it ("Production self-extend records `subject:agent:<id>`") | The verifier must see the paired records (every record with the subject basis has `Current == SystemHigh` and the same `Access` as the no-frame decision), not only a basis assertion |

Covered well: the open-read rule (items 1-5, C.4; the model), the marker (6-9, C.1-C.2), RAG labels (10-13, C.7, C.8),
the frame and obligation (d) (14-20), peers (21-23, C.3), the uninstrumented paths (24, 25), records (§§1-15 of
records-4.5, with the three "not yet" sentences and the row 66 flip).

### 4.10 (DESIGN §4 row `:860`; §2.7 `:587-626`)

| # | Requirement | Where | Coverage | What a verifier should add |
|---|---|---|---|---|
| G9 | "Infrastructure never reads `Effective` directly" is a compile-time fact today only because Abstractions does not grant `InternalsVisibleTo Ashlar.Infrastructure` (`Ashlar.Abstractions.csproj:33-39`; `AshlarDeploymentProfileEnvironment` is internal, records-4.10 F2). **4.3 grants that IVT** (D9; `pr-4.3-body.md` Changes). After 4.3 merges, the rule is a convention with no test | §2.7 `:614-616` | records-4.10 F2 gives the `git grep` as a copy-paste check (§16) but no cert-gate fact | A convention fact (the `EgressGuardConventionTests` shape) pinning that no file under `src/Ashlar.Infrastructure/`, `application/src/Ashlar.API/` or `application/src/Ashlar.CLI/` names `AshlarDeploymentProfileEnvironment.` or `ASHLAR_DEPLOYMENT_PROFILE` outside a doc comment; needs its own `ci/cert-gate-assertions.md` row (CLAUDE.md) |
| G10 | The done-when "ollama.com is disabled" vs the mechanism "`Enabled` defaults to false on AG" | `:613`, `:860` | attack-4.10 §3.11 names the overstatement | The 4.10 body and SPEC-007 bullet must say "off by default"; the lane's body already does |
| G11 | Q6's "boot fails" for the API when the listener is added **in code** after `Build()` | `:621-623`; done-when `:860` | attack-4.10 item 21 part 3 covers the post-bind verifier; §3.8 names the window | Covered; the accepted window (bound, then `StartedAsync` fails) must be a Known-limit sentence |

Everything else in §2.7 has an item: routing (1-4), the adaptive factory (5-11), the four validators (12-15), the
catalog default (16), the profile value (17-20), inbound (21-25), SW (26), hygiene and evidence placement (27-29),
the remaining paths (30-38), and the records (records-4.10 §§1-16, incl. the four non-SPEC docs at 11.1-11.3).

---

## 2. Attack items that contradict the design or each other

| # | Contradiction | Who is right, and why |
|---|---|---|
| **C1** | attack-4.3 item 22(c) and records-4.3 Q-A: "the follower does not follow a `Refused` hop and returns the 3xx" (fail closed in 4.3) **vs** DESIGN D21 `:820` "HTTP refuses with a faulted task, or a throw on `Send`; never a synthetic 4xx" and the seven merged records "no route acts on the mode until PR 4.7" (`EgressGuard.cs:31-33`, `EgressDecision.cs:144-148`, `docs/EgressInventory.md:9`, `SPEC-007:106`, row 64, `pr-4.6-body.md` Summary) | The design is internally inconsistent: the 4.3 done-when's "enforcement twin: hop 2 refused, the server never sees `/b`" (`:559`, `:853`) needs 4.7's exception type (`:857`). Returning an unfollowed 3xx is a third refusal shape D21 forbids by implication (a response where a refusal happened), and it would make the "report-only" records false on master before 4.7. The lane's reading (record `Refused = true` before the send, send, name both twins for 4.7's list) is the only one consistent with D2, D21 and the merged 4.6 records. **Not an owner question** (§3) |
| C2 | attack-4.5 item 1: "a decision made *after the report but before dispose* must be `Internal`: 'open and unreported' ends at the report" **vs** the model's rule (`REPORT.md` §1, §9 Q3: a `Report` before `Dispose` does not lower an open scope) and the lane ("'open and unreported' is implemented as 'not yet ended'", `pr-4.5-body.md`) | The attack scout's reading requires `Report` to observe into the frames immediately (today it observes only after `_ended`, `ReadScope.cs:63-65`), and it reopens exactly the write-down the owner closed: a tool reports for part of its result, reads more, egresses at the reported label. The model's B4 control shows the family of bugs (`REPORT.md` §3). The stricter reading is the fail-closed default (handoff §8). The verifier must **reject** item 1's "Internal after report" expectation and expect `SystemHigh` until the scope ends |
| C3 | attack-4.5 item 23 and C.3: "narrow 'agent-backed' to the `peer:` prefix only; a host's own inner client under any other key is the host's trusted base" **vs** the lane: "agent-backed means a `peer:` target, or any governed target whose key is neither `local:` nor `cloud:`" (`pr-4.5-body.md`) | D16 `:815` says "`peer:` and other agent-backed targets". Nothing on master identifies "agent-backed" (`EgressGuardChatClient.cs:37-43` takes no key; `DefaultChatTargetAccessPolicy.cs:25-30` admits `local:` and `peer:` by prefix). The lane's rule is the fail-closed reading of D16 (an unknown key observes SystemHigh: availability cost only, since observing raises). The scout's rule is the permissive one. **Integrator decision; take the lane's and record it**: the host's trusted base is the host's *guard* (§2.1 `:152-153`), not its chat targets |
| C4 | attack-4.3 item 16 says an `https` first hop redirecting to `http://127.0.0.1:…` is followed under P1; the lane's twin found it is a **downgrade the follower refuses at parity** (`pr-4.3-body.md`, Q-R1: "the limit is narrower than the attack list says") | The lane is right by the design's own parity rule (`:527`, "refuse HTTPS → HTTP"). The limit holds only for a plain-`http` first hop (RunPod and ollama.com are `https`, `AshlarDefaults.cs:161`, `ModelArtifactCatalogServiceCollectionExtensions.cs:53` [S4]); the Known-limit sentence must say so |
| C5 | attack-4.10 item 21: "the Q6 loopback check must refuse `*.localhost`" **vs** `EgressDestinations.IsLoopbackHost` which calls `*.localhost` loopback (`EgressDestinations.cs:148-151`) and D40's "trusted by spelling" (`docs/EgressInventory.md:21`) | Both are right for their own purpose: the *egress* classifier trusts `*.localhost` by spelling (an outbound destination); a Kestrel *bind* to `api.localhost` listens on every interface **[needs a test; attack §2.2]**. The lane's `LoopbackListenerPolicy` must not reuse `IsLoopbackHost`; the lane body lists the `api.localhost` row as refused. Consistent once stated; the records must not describe the two predicates as one |
| C6 | attack-4.10 §3.5 treats DESIGN `:621` "every Ashlar inbound listener" as possibly a rule wider than Q6 | Q6's text (`SPEC-007:422`) names "the API's listeners and mesh serve", and §2.7's own sentence continues "This covers the API's Kestrel URLs and mesh serve" (`:622`). The owner chose the recommended option, which is that paragraph. The design's "every" is the recommended option's paraphrase, not a wider rule. The gRPC host and Fleet.Host were never in the design. **Decided: the two named listeners; the rest is a Known limit and a follow-up ask** |
| C7 | attack-4.5 C.1/C.2 says the marker "has no seam on master" and the lane must choose; records-4.5 F3 says a new member on shipped `ITool`/`IToolbox` breaks 30+ implementers | Agree with each other; the lane chose `ILabelledTool : ITool` (a separate type) plus `CapabilityRegistry.Find` and a type test on the toolbox, which is C.1's option (a)+(c). Consistent with the design's records row (`:878`, the marker in `PublicAPI.Unshipped`). No contradiction left, but G5 applies to the shape chosen |
| C8 | records-4.3 R29(viii) asks row 64 to state "the `RedirectingStub : HttpClientHandler` 307 to `remote.example` gives 1 decision at base and 2 after" **vs** attack-4.3 item 2 / §3.2: for an `EgressHttp` client with a cross-host 307 the answer after is **1** (P2) | Item 2 is right (D33 `:534-536`). R29(viii) copied the design's twin line (`:555`). Row 64's sentence must say "2 after through a factory client (P1); an `EgressHttp` client returns the 3xx with 1". The lane body already states the three-way split |
| C9 | attack-4.5 item 20(b) says a synchronous tool can `Enter("agent:victim", new HighWaterMark())` and leave it undisposed (attribution attack); the lane's floor-pin convention pins production `Enter` sites only | Both true: the convention pins Ashlar's code; a host or plugin tool is the host's code. Record it as the attribution Known limit (D37 keeps `Enter` public, `:835`). No contradiction, but the producers section must carry it |

---

## 3. Owner questions: genuine vs already decided

Rule applied (handoff §8): ask only for policy, security posture, compatibility, or a default the owner accepted;
otherwise take the fail-closed default and record it in the PR body.

### Genuine (the integrator asks these, and only these)

| # | Question (as the lane will hit it) | Why it is the owner's |
|---|---|---|
| **O1** | **4.5 / Q8:** the owner's Q8 answer (`SPEC-007:424`) says "the first producer that labels data from a custom `IDataSensitivityLevel` applies a fail-closed normalisation: the lowest built-in level whose flags are no more permissive". `RAGTool` is that first producer and can see a custom level through `DataSensitivityRegistry.GetByName` (`RAGTool.cs:109`; `DataSensitivityRegistry.cs:50-61`). The design (D15 `:814`, §2.2 table `:253`) and the lane map every non-canonical name to **SystemHigh** instead, and the lane's extra "a spelling `TrustTierOrder` ranks" step makes a custom level unreachable (`TrustTierOrder.cs:58-74` ranks the five names and `top-secret` only). Stricter than the owner's words, with no flag table. **Ask:** accept SystemHigh for custom levels in PR 4 (and leave Q8's normalisation to the first producer that *wants* custom tiers), or implement the normalisation now? Cost today is nil: no production toolbox registers `RAGTool` (grep; records-4.5 F2) | Contradicts an owner answer, in the stricter direction |
| **O2** | **4.3 / D33, item 16:** under P1 a factory client follows a plain-`http` remote first hop *into* the host boundary (`307 Location: http://127.0.0.1:11434/…` or link-local) with the body, and the label model allows it (`Host` is SystemHigh, `ReferenceMonitor.cs:53-54`). The repository already names this an attack for mesh pull (`MeshAutoPullService.cs:48-50`, `:57`). The design has no rule and no Known limit. Options: keep P1 and record the limit (what the lane ships, with the twin as the flip target); add "never follow into Host from a non-Host hop"; make factory clients P2 too. `https` first hops are not exposed (C4) | Security posture not in the design; non-blocking (the default is implemented and recorded) |
| **O3** | **4.10 / Q6, mesh serve:** Q6 as recorded (`SPEC-007:422`) says "the API's listeners and mesh serve must bind loopback, or boot fails". Mesh serve has no bind-address setting (`BackgroundAgentDaemonCommand.cs:432-437` [scout]) and binds every interface (`MeshServeService.cs:229`). The lane ships a third thing: refuse to serve, daemon stays up (`pr-4.10-body.md` deviation 1), following the class contract "a failed bind never takes the daemon down" (`MeshServeService.cs:113`, `:164-170`), which the owner did not write into Q6. **Recommendation before asking:** implement the owner's text literally, `ListenLocalhost` on AG/SW (one line), which needs no question; ask only if the integrator wants to keep the refusal. Either way SPEC-007 `:422` needs no new row if the text is followed, and a dated row if it is not | The owner's recorded text is being deviated from; the deviation is the integrator's to avoid or to put to the owner |

### Already decided (do not ask; record in the PR body, cite the decision)

| Scout question | Decision that settles it |
|---|---|
| 4.3 Q-A / item 22c (enforce a `Refused` hop in 4.3?) | D21 `:820` (HTTP refuses with a faulted task, never another shape), D2 `:800` (4.6 lands before any route acts), the merged 4.6 record "no route acts on the mode until 4.7" (`pr-4.6-body.md`; `EgressDecision.cs:147`). 4.3 records `Refused = true` before the send and sends; the enforcement twin moves to 4.7's list (and 4.11's Scenario C(ii), `:954-958`). See C1 |
| 4.3 Q-B (fold the ns2.0 hop into the follower?) | Integrator. The owner's 2026-10-06 D31 amendment (`SPEC-007:425`: no record from the hop) argues for keeping the hop as is; the lane keeps it and narrows Known limit `docs/EgressInventory.md:23`. Record |
| 4.3 Q-C / item 17 (Bedrock `AllowAutoRedirect`) | The design's own instruction "Bedrock, if AWSSDK.Core exposes the setting" (`:512`); the lane found it does and set it. Routing the SDK's HTTP through `EgressHttp` is outside the 4.3 row (scope), a follow-up, not a question |
| 4.3 item 13 / Q-R2 (clear `X-Api-Key`-style headers on a P1 hop?) | Runtime parity is the design's rule, "clear `Authorization`" (`:529`, D32). Record the consequence as a Known limit; the consumer SDK's header (`src/Ashlar.Client/ServiceCollectionExtensions.cs:26` [scout]) following a cross-host hop that the guard allowed is report-only today and refused on AG/SW at 4.11 whenever the label forbids it |
| 4.3 Q-D, Q-E (`GuardFiles`, a new row) | Integrator; the lane kept six files and no new row (every new fact is a runtime twin under row 64) |
| 4.5 "basis inside an open read scope" (attack-4.5 Q1, records-4.5 Q1, REPORT Q2) | The owner's rule fixes the label only. The basis is a record-shape default: the lane's `Live` skips read frames so the nearest live subject frame names the decision (`pr-4.5-body.md` "The open-read rule"), which the model report also suggests (`REPORT.md` §6.4). Record it; 4.11's Scenario B filters on `subject:agent:<id>` and is unaffected |
| 4.5 "pinned or settled" (REPORT Q1) | Both pass P1-P4 (`REPORT.md` §4); the difference is availability. Handoff §8: take the fail-closed default (pinned) and record it. It flips `EgressSubjectReadScopeTests…from_whatever_flow_it_ends_on:179` by name (the lane names it); `ToolCallingAgent` is in order on one flow (`ToolCallingAgent.cs:218`, `:236`), so production is not exposed |
| 4.5 "reported but undisposed" (REPORT Q3, attack item 1) | The stricter reading ("not yet ended") is the fail-closed default and the only one that keeps D14 without immediate observation; see C2. State in the body "stricter than the decision's wording, on purpose", as the lane does. The owner can object |
| 4.5 "peer: only or anything not local:/cloud:" (attack Q2) | D16 `:815` plus the fail-closed default; see C3. Observe-before-vs-after is an implementation choice; the lane observes before each streamed update reaches the caller and at the end, and keeps both overrides non-`async` (records-4.5 13.2 must still be run and quoted) |
| 4.5 refusal path text at `Public` (attack Q3) | D15's "read nothing" was written for `VectorMath.UnrankableQuery`; the lane narrowed `Public` to that refusal and reports nothing (SystemHigh) for any other `ArgumentException`. Fail closed; record |
| 4.5 marker location / wrapped tool (attack Q4, records Q4) | The design's records row puts the marker in `PublicAPI.Unshipped` (`:878`), i.e. Abstractions; the lane did that. A wrapped labelled tool is unlabelled (fail closed); the lane pins it |
| 4.5 "should self-extend register `RAGTool`?" (records Q3) | Not in the 4.5 row; the design's production story is "self-extend declares SystemHigh" (Q2, `SPEC-007:418`). Default no. Record that in PR 4 the labelled path runs only in the leak skeleton |
| 4.5 attribution via a public `Enter` (attack Q6) | D37 `:835` (`Enter` stays public). Known limit; record |
| 4.5 limit (a) wording for read frames (REPORT Q4) | Records; the lane rewrote the `EgressSubject` remarks |
| 4.10 Q6 scope: UDP discovery, gRPC host, Fleet.Host (attack Q, records Q2) | Q6 text names the API and mesh serve (`SPEC-007:422`); see C6. Known limits in `docs/EgressInventory.md` plus a follow-up ask on the bus, not a 4.10 question |
| 4.10 container consequence (records Q3) | A consequence of Q6, not a decision. State it in `docs/DEPLOYMENT.md` and the release notes (the lane did); inform the owner in the PR body |
| 4.10 defect 5 on SW now (attack Q) | §2.7 scopes defect 5 to AG (`:589`, `:599`, `:606`), D35 `:833`, accepted by the owner (defaults stood). SW's outbound paths wait for 4.11; the lane pins it. FYI, not a question |
| 4.10 video under "never escalates" (attack Q) | §2.7 names LLM and vision only (`:599-604`); `:613` "the guard is the backstop for everything else". Record EG-MDL-08 in the AG section as left to the guard |
| 4.10 Bedrock composition-time refusal (lane question 6) | Integrator decision, stricter than the design's `ValidateOnStart` (which cannot bind to a pre-built `Options.Create` instance, `MeaiPipelineServiceCollectionExtensions.cs:94`). Record; the owner can object |

---

## 4. What the model agent did not model, which a verifier must test by hand

From `REPORT.md` §7 and §10, plus what the model could not see by construction:

1. **The report channel and who may call it.** The model has `P` (Report) as an op anyone may issue; it does not model
   *which code holds the scope*. G5 (a labelled tool calling `Complete()`/`Dispose()` on the scope it is handed) and
   attack items 6-9 (impostor by id, wrapper, snapshot/arguments channel) are outside the model. Test by hand: the
   impostor `rag_search`, the wrapper, the tool-side `Complete`-then-throw.
2. **The basis string.** Labels are atoms; `Live` skipping read frames (`REPORT.md` §6.4) is unmodelled. Test: a
   decision inside an open scope carries `CurrentBasis == subject:agent:<id>`, never a read basis and never
   `no-subject` with a live subject frame.
3. **Timers, registrations, cold tasks and `System.Timers.Timer`** (`EgressSubject.cs:52-62` capture table): `T` forks
   at the op only. Test: a `Task` created before `BeginRead` and started inside it, and a `Thread` created inside and
   started after `Dispose`, as 4.4's twins did for frames.
4. **`AsyncLocal` persistence inside an async helper before its first await** (`REPORT.md` §10, [unverified]): decides
   whether `An_awaited_read_that_throws_observes_SystemHigh` (`EgressSubjectReadScopeTests.cs:140-158`) keeps passing
   with a read frame pushed on the helper's flow. Run the class.
5. **Concurrency**: the three-state dispose window (`EgressSubject.cs:190-193`, `:263-273`) and the `Report` CAS
   (`ReadScope.cs:51-61`) are interleaving-only in the model. Test: 4.4's concurrent-report twin (`:310-333`) and a
   `Dispose` racing a `Decide` on another flow.
6. **Exception filters and the two-pass unwind** around a read scope: not an op. Test: a tool that throws under a
   `catch … when (filter)` in the agent, where the filter decides (the 4.4 Cecil fact covers `RunDetached` only).
7. **Async iterators**: forbidden by obligation (d), not modelled. The convention fact must be seen red on a planted
   `yield return` (attack-4.5 item 17) and on the five alias spellings (item 16).
8. **Peer responses** (D16) are not in the model at all: observe timing, early-break streaming, a faulted stream, a
   synchronous PolicyGate throw (attack items 21-22; records 13.2's "must not flip").
9. **RAGTool's mapping** (D15): no model. Parity two-sided over non-ASCII folds (item 10; `ToLowerInvariant('İ')`
   [needs a test]); the `All` reference check against a custom `("Restricted", 1)` (item 11); zero-vector refusal on
   the legacy store vs the MEAI path (item 12-13; attack B.3 [needs a test]).
10. **Exact frame counts for new twins**: the model gives candidates (13, `i + 2` → 51, 1, 3 → 2, 7,101 programs /
    17,428 frames, `REPORT.md` §5) that the C# `FlowModel` (`EgressSubjectNestingTests.cs:1145-1233` [scout]) must
    reproduce with a read-frame node before a twin pins them; the 7,101-program twin's cert-gate budget is
    [unverified].
11. **Bounds**: N ≤ 7 ops, ≤ 3 flows; a counterexample needing 8 ops or 4 flows is unmodelled. The 4.4 body's precedent
    (D1 found at 3-6 ops) makes this acceptable, but the verifier should not quote "no counterexample" without the
    bounds.
12. **Cross-process and MCP transport pumps** (attack item 18, [needs a test]): whether the MCP SDK sends on the
    caller's flow decides the wording of "records go from `no-subject` to `subject:agent:<id>`" (DESIGN `:196`).

---

## 5. Cross-lane conflicts

### 5.1 Files two or three lanes edit (expected merge conflicts; merge order 4.3 → 4.10 → 4.5 per handoff §4)

| File | 4.3 | 4.10 | 4.5 | Conflict |
|---|---|---|---|---|
| `ci/cert-gate-assertions.md` **row 64 (one line)** | TSV sums → **149** / sdk.client 8; floors 2,139 / 149 / 67; "one decision per authority"; Tests column +2 classes | floors 2,139 / **150** / 67 (false once 4.3 is in) | current-label clause (+ open read scope); floors 2,136 / 150 / 67 | **Certain three-way conflict in one line.** Final text after all three: TSV 86 / 149 / 48, sdk.client 8; floors re-measured once in the container on the merged tree (expected about 2,135 + 4 + 4 + 1 = **2,144** files, **149** occurrences, 67 rows; measure, do not compute) |
| `ci/cert-gate-assertions.md` **new rows after :67** | none | one row (`AirGappedHygieneTests`) | one row (floor-pin convention; the lane calls it "row 68") | Both claim the slot after 67 → renumber; the 4.5 body's "row 68" wording becomes "row 69" if 4.10 merges first |
| `ci/cert-gate-assertions.md` **count paragraph** (`:69-75`) | 133 / 136 | 132 / 135 | 131 + k | Each computed on its own tree; the final is 131 + 2 (4.3) + 1 (4.10) + k (4.5, two classes named: `EgressProducerTwinTests`, `EgressSubjectProducerConventionTests`) = **136 / 139** if nothing else lands; recount with `git ls-files` after each merge |
| `EgressGuardConventionTests.cs` floors comment (`:191-207`) | "2,139 / 149" | "2,139 / 150" | "2,136 / 150" | Three-way; re-measure once at the end |
| `CHANGELOG.md` Unreleased | `### Changed` entry after 4.6's (`:261-301`) | `### Changed` entry after 4.6's | `### Added` entry after 4.4's (`:127-166`) and a reword of `:162-163` | 4.3 and 4.10 insert at the same point under `### Changed`; textual |
| `docs/EgressInventory.md` | `:3` (+4.3); `:15` rewritten; Known limits +4 bullets, `:23` narrowed; route vocabulary `:54-55`; rows EG-MDL-01 `:67`, **EG-MDL-02 `:68`**, EG-MDL-14, EG-HTTP-03 `:126`; "Adding an outbound path" `:224` | `:3` (+4.10); new section before `:16`; Known limits +4 bullets; `:46` rewritten; rows **EG-MDL-02 `:68`**, MDL-03 `:69`, MDL-09 `:75`, MDL-13 `:79`, EXE-01/02/03/05, MESH-03 `:138`, SRV-01 `:183`; `:214`, `:216-219` | `:3`?; producers section before `:16`; Known limits +3 bullets; `:220` | Three-way at `:3`, at the per-PR section slot before `:16`, and in the Known-limits list; **EG-MDL-02's row cell is edited by both 4.3 (route column: `RuntimeConfig`, `:47-53`) and 4.10 (AG gate)** |
| `ci/egress-inventory.tsv` | rows 9, 34 (notes), **38 (`sdk.client 3` → `2`, total 150 → 149)**, 42 (note) | rows 14 and 51 (notes only; "no count changes") | none | 4.10's row-64 sentence and floors ("150 occurrences unchanged") are false after 4.3; row 51's note (`:136`/`:139` → `:144`/`:147`) is 4.10's and holds only if 4.3 does not touch `AshlarServiceCollectionExtensions.cs` (it does not) |
| `docs/specs/SPEC-007-…md` | `:48-51` gap paragraph; `:108` gains `de41a8a`; a `**PR 4.3**` bullet after `:127` | `:249`; a `**PR 4.10**` bullet after `:127`; maybe a decisions row (O3) and the intro `:405-406` | `:114-115`, `:120` reworded; a `**PR 4.5**` bullet; a decisions row after `:425` and the intro `:405-406` | All three add a bullet at the same anchor; 4.5 and (if O3 is asked) 4.10 both edit the intro sentence `:405-406` |
| `docs/knowledge-graph.{json,md}` | regenerated | regenerated | regenerated | Never hand-merge; regenerate after `git add` on every master merge (CLAUDE.md) |
| `application/src/Ashlar.API/Program.cs` | `:171` same-line `.NeverFollowRedirects()` | +19 lines after `:262`, a hosted verifier registration | none | No textual conflict (different regions). Citations: TSV row 9 (`:171`, `:232`) and `docs/EgressInventory.md` EG-TEL-01 (`:247`, `:252`, `:259`) are above `:262` and hold; `MapAshlarMcpEndpoint` `:395` → `:414` is 4.10's to re-derive. Both PRs carry `[coordinated-integration]` |
| `src/Ashlar.AI.Pipeline` | `AwsBedrockChatClientFactory.cs`; `Ashlar.AI.Pipeline.csproj` (IVT `Ashlar.Tests.Infrastructure`) | none | `EgressGuardChatClient.cs`, `AshlarGovernanceChatClientBuilderExtensions.cs` | No file overlap. **Test file overlap:** 4.3 adds two Bedrock tests to `EgressGuardChatClientTwinTests.cs`; records-4.5 13.3 suggests 4.5 put its `peer:` twin there too (the lane put them in `EgressProducerTwinTests` instead, so probably none) |
| `src/Ashlar.Abstractions` | `EgressHttp.cs`, `EgressGuardHandler.cs`, `EgressGuard.cs` (internal `ProfileEnforcesByDefault()`), `SynchronousSendRefusedOnNetstandard20Asset.cs` (remarks), `Ashlar.Abstractions.csproj` (IVT Infrastructure); `PublicAPI.Unshipped.txt` unchanged | none | `EgressSubject.cs`, `ReadScope.cs`, `ILabelledTool.cs` (new), `PublicAPI.Unshipped.txt` (+2 lines) | No file overlap |
| `src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj` | none | `ProjectReference` → `Ashlar.Mcp.Server` | none | None |
| `src/Ashlar.Hosting` | none | `AshlarServiceCollectionExtensions.cs` (XML doc +8 lines, one call), `…AirGapped.cs` (new), `Phases.cs` (same-line) | none | None |

### 5.2 Semantics one lane changes that another relies on

| # | Interaction | Consequence |
|---|---|---|
| **G12** | **4.3's IVT grant to `Ashlar.Infrastructure` dissolves 4.10's compile-time guarantee** that Infrastructure cannot read `AshlarDeploymentProfileEnvironment` (records-4.10 F2 rests on `Ashlar.Abstractions.csproj:33-39` lacking Infrastructure). After both merge, "Infrastructure never reads `Effective` directly" (DESIGN `:616`) is only a grep | See G9: a convention fact and its row, in 4.10 or a follow-up; at minimum the 4.10 body must say the rule is a convention once 4.3 is in |
| **G13** | **4.3's AG/SW startup Warning 7304 names every unknown primary type** (`EgressRedirectFilter`, `pr-4.3-body.md`). 4.10's `AirGappedHygieneTests` composes `AddAshlar(AirGapped)` with stub primaries (`HttpMessageHandler` subclasses). After 4.3 merges, every such twin logs 7304 | Harmless unless a 4.10 twin asserts on the log or on hosted-service counts; the integrator runs 4.10's class after merging 4.3 in. Also 4.3's post-send check adds a decision only on an authority mismatch, so 4.10's "0 decisions at the RunPod site" absences hold if its stubs set `response.RequestMessage` (the repo's stubs do, records-4.3 R61) |
| G14 | **4.5's read frame vs 4.3's follower**: a redirect followed during a tool call (a tool's HTTP client, or the web-search client) is decided on the tool's flow inside the open read frame → `SystemHigh` by the owner's rule. 4.3's twins all run with no frame (`Current = SystemHigh`, `no-subject`), so nothing in 4.3 moves; but 4.3's `CheckAfterSend` decides *after* the send, and if 4.5 ends the scope between a tool's send and the check (impossible within one `SendAsync` awaited inside the tool) the label could differ. No conflict today; state in 4.5's producers section that hop decisions inside a tool call are at SystemHigh |
| G15 | **4.10's profile value vs 4.6's strictest-wins** (`AshlarServiceCollectionExtensions.Egress.cs:47-49`; `AshlarDeploymentProfileEnvironment.NoteResolved`): 4.10 builds `ResolvedDeploymentProfile` from `ResolvedRaw`, consistent with D5. But `EgressGuard.ProcessDefault` re-reads the profile per decision (`docs/EgressInventory.md:10`, handoff §7 "4.6 finding 6"), so in a process that composed Full before AG was noted, explicit-site records say `profile:air-gapped` while the 4.10 value and the composed guard say Full (attack-4.10 item 19). Recorded by the lane as a Known limit; 4.11 inherits it |
| G16 | **4.10's Bedrock composition-time refusal inside `AddAshlar`** reads the collection for an `IOptions<MeaiPipelineOptions>` with `Bedrock.Enabled`. **4.3's Bedrock twins** build the runtime client configuration through `AwsBedrockChatClientFactory.RuntimeConfig` without `AddAshlar(AirGapped)`, so they are unaffected; but any later test that composes `AddAshlar(AirGapped)` with Bedrock on (4.11's C7 shape) now throws at composition, not at start. 4.11's design must know |
| G17 | **4.3 says the TSV total is 149 and `sdk.client` 8** (one `new AmazonBedrockRuntimeClient(` fewer, `AwsBedrockChatClientFactory.cs:38-40`). 4.10 and 4.5 both carry "150" in row 64, the floors comment and their bodies | Whoever merges after 4.3 restates 149 (handoff §5 step 3 says recount; this names the number) |
| G18 | **4.5's `EgressGuardChatClient` gains an internal constructor taking the target key** and `AshlarGovernanceChatClientBuilderExtensions` passes it; **F5 of `EgressGuardConventionTests`** pins `new EgressGuardChatClient(` to that one production file (records-4.5 13.4). 4.3 does not touch either file | No conflict; the verifier re-runs F5 after 4.5 |
| G19 | **4.10's `LoopbackListenerVerifier` is an `IHostedLifecycleService`**; 4.6's `EgressModeStartup`/activator and `EgressDecisionLoggerActivator` are hosted services counted by `ImplementationType` in `EgressKernelFactoryTwinTests.TheGuardIsRegisteredOnce` (records-4.3 R55) | No flip (type-filtered); run the class after 4.10 |

---

## 6. Pull forward into a lane's brief NOW

Ordered by what blocks a clean merge or a true PR body.

**4.5**
1. **G5 (D14 against the labelled tool):** hand `ILabelledTool` a report-only surface, or make the agent own completion; add the twin (reports `Public`, calls `Complete()`, throws → `SystemHighData`) and the mutation. Do this before the PR opens: it changes the public marker's signature, which is in `PublicAPI.Unshipped.txt`.
2. Reject attack-4.5 item 1's "Internal after report" expectation (C2); keep "not yet ended", and say in the body that it is stricter than the owner's words and why.
3. Ask **O1** (Q8 vs SystemHigh for custom levels) before the body is final; it decides one sentence in the decisions-log row and the parity theory's custom-level rows.
4. Verify by name: `A_completed_read_with_no_report_observes_SystemHigh:50` and `A_read_observes_into_every_live_frame…:179` flipped and recorded in row 66 and the body; `EgressGuardChatClientTwinTests.CloudBedrock_DeniedByPolicyGate_…:81-84` and `AThrowingCustomGuard_…:479/:490` **not** flipped (records-4.5 13.2); F5 (13.4) green.
5. Records: the three "not yet" sentences (`CHANGELOG.md:162-163`, row 65 last sentence, row 66 last sentence, `SPEC-007:120`), `docs/EgressInventory.md:220`, the producers section with G6/G7/C9 sentences ("persisted state is the runner's floor"; "a host's labelled tool is the host's trusted base"; the attribution limit), the MCP-pump "no-subject" caveat (item 18), the row numbering after 4.10 (§5.1).
6. Amend `DESIGN-4-final.md` §5 on the storage branch now (Scenario B reason `SystemHighData`; C4's inner send no longer isolates nesting; B and C5 differ by site and family), so 4.11's lane does not inherit a stale design (`pr-4.5-body.md` says this; nobody owns it yet).

**4.3**
7. Run the 22-23 prepared mutations (0 run at resume) and `MeshLanPartyTests` (cheap control, `application/src/Ashlar.Tests.CLI/Tests/Commands/MeshLanPartyTests.cs:252-290`); the body's `{{MUTATIONS}}` is empty.
8. Make Q-A's resolution explicit for 4.7: list both enforcement twins (`Under_an_enforcing_guard_…`, stub and differential) in a "for 4.7" paragraph the 4.7 brief will copy; the leak test's Scenario C(ii) (`DESIGN:954-958`) is the 4.11 end state.
9. Record **O2** as a Known limit with the twin as the flip target and the C4 narrowing (`http` first hop only); do not block on it.
10. State G4 (the `docs/EgressInventory.md:15` sentence about "only the recorded host is wrong") and G1 (a `Wrap` over a chain with no primary yet) in Known limits or as twins.
11. The new `InternalsVisibleTo Ashlar.Tests.Infrastructure` on `Ashlar.AI.Pipeline.csproj` is a record (the CHANGELOG is AI.Pipeline's only record); name it.

**4.10**
12. **O3:** implement Q6's text (`ListenLocalhost` on AG/SW) unless the integrator decides to put the refusal to the owner; the lane's `MeshServeLoopbackProfileTests` rows flip either way.
13. Never-run items at resume: mutation checks, `build-core` (a `ProjectReference` was added), the full cert-gate on the final head, `924df514`/`34f0883d` in a container.
14. After 4.3 merges: restate 149 (G17), re-run `AirGappedHygieneTests` for Warning 7304 side effects (G13), and add the G9/G12 convention fact or the sentence that the Infrastructure rule is now a convention.
15. Keep the done-when wording honest: "ollama.com off by default" (G10); "fail boot, and fail the first resolution in a process with no boot" (attack §3.4); the post-bind window (G11).

**Integrator**
16. The row-64 line, the floors comment, the count paragraph and the knowledge graph conflict on every merge (§5.1); plan one re-measure in the container after the **last** merge and restate all three records once, rather than three times.
17. SPEC-007 bullet order after `:127` (4.3, 4.10, 4.5) and the decisions-log intro `:405-406` ("the PR 4.2 and PR 4.5 answers of 2026-10-06", plus 4.10's if O3 adds a row).
18. Only O1-O3 go to the owner; everything in §3's second table is answered in a PR body with its citation.

---

## 7. Unverified, in this file

1. The runtime facts the scouts marked and I did not read: `ToLowerInvariant('İ')`, Kestrel's `*.localhost` bind, `IStartupValidator` ordering, the MCP SDK's send flow, `AllowAutoRedirect` on a started handler (the lane reports it throws), AWSSDK.Core's `AllowAutoRedirect` (the lane reports it exists).
2. `BackgroundAgentDaemonCommand.cs:432-437` (`MeshServeSettings` has no bind address) and `MeshServeService.cs:113` wording: from records-4.10 and the lane body; I read `MeshServeService.cs:105-175` and `:229`, which agree.
3. `EgressSubjectNestingTests.cs:1145-1233` (`FlowModel`) and the cert-gate budget of a 7,101-program twin: from `REPORT.md`.
4. The three lane bodies' counts (cert-gate 2818 for 4.3; 499/504 egress-filter for 4.5; 2763 base for 4.10) are the lanes' claims, not read from logs.
5. Whether the 4.5 lane's `EgressProducerTwinTests` or `EgressGuardChatClientTwinTests` holds the `peer:` twins (affects the §5.1 test-file overlap row).
6. `src/Ashlar.Client/ServiceCollectionExtensions.cs:26` (`X-Ashlar-Api-Key` default header): from attack-4.3.
