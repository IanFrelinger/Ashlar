# Lens conformance-4.5: PR #721 (squash `9b2ea56f4`) against DESIGN §4 row 4.5 / §2.2, D13–D19, Q2/Q8, the three 2026-10-06 owner decisions and obligation (d)

Read-only, 2026-10-08, in `scratchpad/review-master` at master `095ba46e2`. Nothing built or run; every claim is a file:line read at `095ba46e2` unless a PR, commit or branch is named. References: `ws/DESIGN-4-final.md` (amended for the open-read decision), `ws/handoff.md`, `ws/INTEGRATION-NOTES-C.md`, `ws/phase-C-critic.md`, `ws/attack-4.5.md`, `ws/records-4.5.md`, `ws/state-4.5.md`, `model-4.5/REPORT.md`, `origin/claude/spec-007-pr4-4.5-producers` @ `91d3b48c`.

**Verdict: defective.** The PR implements the §4 row's *mechanics* (frame at SystemHigh, read scopes, labelled marker, RAGTool mapping, peer observe, floor pin, leak skeleton in report mode) and the five required checks were green on its head `f15639d2` (cert-gate 113115584061, build-core, shell-lint, lychee ×2, Readiness summary) and master `9b2ea56f4` reads `verdict=verified lanes_ran=4 lanes_skipped=0` (check run 113133426766). But it contradicts an owner decision on master and records the contradiction as policy (B1, B2), omits both 2026-10-06 decisions-log rows (B3), and leaves two write-down paths the design closes (M1 G5, M2 streaming) plus three design-row bullets weakened without a recorded deviation (M3 D16, M4 D15 refusal, M5 stale sentences). Nothing was posted on agent-bus #695 for #721 (last comment 2026-10-06T17:28:44Z, `drift-716`), so no drift audit ran.

---

## B1 (blocker). The open-read-scope owner decision is not implemented; master pins the opposite

**Owner decision (2026-10-06, INTEGRATION-NOTES-C §1; DESIGN amended; handoff §3 question closed):** while a read scope is open and unreported, every egress decided on the flows inside it is decided at SystemHigh, for every tool, RAGTool included; Scenario B's reason becomes `SystemHighData`.

**Master:**
- `src/Ashlar.Abstractions/Security/Egress/EgressSubject.cs` and `ReadScope.cs` are unchanged since `de41a8ac` (`git log` shows only #716 and #709). `EgressSubject.cs:120`: `public static ReadScope BeginRead() => new(Active.Value);` — no frame is entered; `ReadScope.cs:31-39` holds only `_chain`. `Frame.Resolve` (`:238-251`) never sees a read.
- `EgressSubjectProducerTests.cs:203-233` `An_egress_during_the_tool_call_is_decided_at_the_pre_read_mark` **pins the anti-decision**: `:221-222 mid.Current.Should().Be(SecurityLabel.Public, "an open scope observes only when it ends, so this send is still at the floor")`, and `:224 mid.Access.Reason.Should().NotBe(AccessDenialReason.SystemHighData)`.
- `EgressSubjectReadScopeTests.cs:50` still reads `Decide(...).Current.Should().Be(SecurityLabel.Public, "the read has not ended yet")` (the flip the integrator named is not made).

**Exposure, as code paths (a runner floor below SystemHigh; today only the leak skeleton and any host runner):**
1. **A tool's own HTTP egress inside the agent's read** — `ToolCallingAgent.cs:235-256`: `using (var read = EgressSubject.BeginRead()) { result = await tools.InvokeAsync(call, snapshot, loopCt) ... }`. Anything the tool sends while `InvokeAsync` runs (a `repo.fs`/forge/MCP proxy tool through `EgressHttp`, EG-HTTP-*) is decided at the frames' current mark, i.e. the pre-read mark; the tool's result raises the frames only at `:256` (Dispose).
2. **Web search inside a tool call** — the §5 Scenario B shape: `web_search` reads Secret-derived text and queries Bing/WebSearch in the same call; decided at the pre-read mark (`LevelTooLow` only if an earlier read already raised the mark; otherwise allowed). The owner's rule makes it `SystemHighData`.
3. **RAG store egress** — `RAGTool.InvokeAsync` → `_ragService.SearchAsync` (`RAGTool.cs:77`): a remote embedding or vector store (MEAI `VectorDataRagService`, an `IEmbeddingGenerator` over HTTP) sends the model's query during the read; decided at the pre-read mark. The owner explicitly included RAGTool ("no labelled-tool exemption").
4. **Work started inside a read** — a fire-and-forget task a tool starts during its call inherits the chain *without* any read marker (no frame), so it decides at the frames' mark forever, and nothing pins it at SystemHigh.

**What implementing it requires** (the Claude lane's read frame, `origin/claude/spec-007-pr4-4.5-producers`; `git diff de41a8ac origin/claude/spec-007-pr4-4.5-producers -- EgressSubject.cs ReadScope.cs`, +103/+42):
- `EgressSubject.BeginRead()` enters `Frame.ForRead(chain)` (shared static `ReadMark = new(SecurityLabel.SystemHigh)`, `isRead: true`) and returns `new ReadScope(chain, read)`.
- `Frame.Live` skips `_isRead` frames (basis = nearest live **subject** frame; `no-subject` with none — integrator decision "basis").
- `Frame.Resolve` joins the read frame's SystemHigh; `Frame.Dispose` of a read frame observes nothing outward (`Mark is not null && !_isRead`).
- `ReadScope.Dispose` observes first (`ObserveInto(_chain, read)`), then `_read.Dispose()` — "open until disposed" (integrator decision, stricter than "open and unreported", on purpose; critic C2).
- Pinned, not settled: the read frame's mark never lowers (integrator decision; both pass the model's P1–P4, `model-4.5/REPORT.md` §4; 41,755,599 sequences, B1–B4 caught in 2–5 ops, 1,900,357 reproduce 4.4).
- The two 4.4 twins that flip by name: `EgressSubjectReadScopeTests.A_completed_read_with_no_report_observes_SystemHigh` (`:50`, in-scope decision `Public` → `SystemHigh`/`SystemHighData`) and `A_read_observes_into_every_live_frame_of_its_chain_from_whatever_flow_it_ends_on` (`:179`, `LevelTooLow` → stays `SystemHigh`, `SystemHighData`).
- Master's `An_egress_during_the_tool_call_is_decided_at_the_pre_read_mark` must be inverted (the lane's `A_tools_own_egress_during_its_call_is_decided_at_SystemHigh`, `RAGTools_own_egress_during_its_call_is_decided_at_SystemHigh`).
- Row 66 of `ci/cert-gate-assertions.md` gains the open-read rule and the two flips by name; `EgressSubject.cs` remarks "A read's frame is a frame"; limit (a) extended to reads.
- Mutation m01 (`BeginRead` not entering the read's frame): KILLED red=failed:7/510 at `f51322ea` (`state-4.5.md` §4); m02, m03 likewise.

## B2 (blocker). Merged records state the anti-decision as policy and as a reason against the owner's rule

- `docs/EgressInventory.md:28`: "**Within-call egress (known limit, PR 4.5).** A `BeginRead` scope observes only when it ends ... The owner can reverse it. Making an open scope count as `SystemHigh` would turn the leak scenario from `LevelTooLow` into `SystemHighData`" — the owner already decided this on 2026-10-06 and accepted that consequence (design §5 amended: Scenario B = `SystemHighData`, C4, C5 by site and family).
- `docs/specs/SPEC-007-security-labels-and-reference-monitor.md:133-142` (the PR 4.5 status bullet): "An open read scope still observes only when it ends (the merged 4.4 rule) ... That is a known limit ... The owner can reverse it."
- `CHANGELOG.md:22-30`: "An open read still counts only when the scope ends, which is a known limit when a runner's floor is below `SystemHigh` (the owner can reverse it)."
- PR #721 body ("This follows the merged 4.4 rule ... The owner can reverse it").
These are false claims about the decision state in a merged PR body and in three records (Grok's drift audit would flag them; it never ran).

## B3 (blocker). No 2026-10-06 decisions-log rows for the read-scope rule or for O1 (Q8 clarification)

- `docs/specs/SPEC-007-...md:442-452`: the decisions table ends at `| 2026-10-06 | PR 4 (4.2) | **A synchronous `Send` refused on the `netstandard2.0` asset leaves no decision record ...` — there is no `PR 4 (4.5)` row. Intro `:433` still reads "the eight PR 4 answers of 2026-10-05, and the PR 4.2 answer of 2026-10-06".
- INTEGRATION-NOTES-C §1 requires "each gets a dated SPEC-007 decisions-log row in the PR that implements it"; the Claude lane's row text is at `origin/claude/spec-007-pr4-4.5-producers:docs/specs/SPEC-007-...md:448` (one row covering the rule, its cost, Scenario B/C4/C5, and "**Q8 clarified:** in PR 4, RAG maps a custom `IDataSensitivityLevel` to `SystemHigh` (D15); Q8's C3 normalisation ... not yet").
- O1 matters because master's `RAGTool.MapHitLabel` (`RAGTool.cs:142-159`) does map a custom level to SystemHigh, i.e. it implements O1 silently; the owner's recorded Q8 text (`:451`, "The first producer that labels data from a custom level applies a fail-closed normalisation") is contradicted on master without the clarifying row.

## M1 (major). G5 open: `IEgressLabelledTool.ReportRead(ReadScope read, ...)` hands the whole scope; a labelled tool can defeat D14

- `IEgressLabelledTool.cs:20`: `void ReportRead(ReadScope read, ToolResult result);` — `ReadScope.Complete()` (`ReadScope.cs:72`) and `Dispose()` (`:78`) are public.
- `ToolCallingAgent.cs:249-255`: after `InvokeAsync` returned, `labelled.ReportRead(read, result); read.Complete();`.
- Path: a labelled tool's `ReportRead` does `read.Report(SecurityLabel.Public); read.Complete(); throw new X("<data>")`. The throw leaves the `using` without the agent's `Complete()`, but `ReadScope.Dispose` (`:83-85`) sees `_completed != 0 && reported is not null` and observes **Public**, not SystemHigh. The exception reaches `ToolCallingAgent.cs:285-290` (`stoppedReason = "error"`, `react.error` with `ex.Message` written to memory) and `RunCycleAsync` returns; the runner frame is still live and the post-cycle egress runs inside it: `SelfExtendRunnerAdapter.cs:297-301` → `SelfExtendAdmissionBridge.TryRecordAsync` → `TryAutoShare` → `SelfExtendAdmissionBridge.cs:288 EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.MeshPublish, "EG-MESH-01", "file:" + storeDir))`, decided at the mark the tool kept low. D14 ("a tool scope that ends by exception observes SystemHigh") is defeated by the tool itself. Production floor is SystemHigh so no production exposure today; the design's §5 leak test (floor Public) and any host runner are exposed.
- Also: `ReportRead` is called with the open scope and may `Dispose()` it before the agent does (fail closed: observes SystemHigh), or `Report`+`Complete`+`Dispose` then return normally (same as a legitimate Public report).
- Exceptions inside `ReportRead` (e.g. a custom `IDataSensitivityRegistry.GetByName` throwing from `MapHitLabel`, `RAGTool.cs:148`) end the whole cycle (`catch (Exception)` at `:285`), not just the call: availability only, fail closed.
- The lane's fix: `ReadReporter` (sealed, internal ctor, `Report` only; `origin/...:src/Ashlar.Abstractions/Security/Egress/ReadReporter.cs`), `ReadScope.Reporter`, `ILabelledTool.InvokeLabelledAsync(ToolCall, WorldSnapshot, ReadReporter, CancellationToken)`; twins `A_labelled_tool_cannot_complete_or_end_the_read_it_is_handed`, `The_surface_a_labelled_tool_is_handed_reports_and_does_nothing_else`, `A_labelled_tool_that_reports_and_then_throws_counts_as_SystemHigh` (`EgressProducerTwinTests.cs:321-356` on the lane). Changes `PublicAPI.Unshipped.txt` (master lines for `IEgressLabelledTool` + `ReportRead` would be replaced by `ILabelledTool`, `InvokeLabelledAsync`, `ReadReporter`, `ReadReporter.Report`, `ReadScope.Reporter.get`).

## M2 (major). Streamed peer responses are observed only when the stream ends

- `EgressGuardChatClient.cs:115-126` `ObserveStream`: `await foreach (var update in pending) yield return update;` then `finally { EgressSubject.Observe(SystemHigh); }`. Every update reaches the caller **before** any observe; an egress the caller makes between updates (forwarding peer chunks to a cloud model) is decided at the pre-response mark. A caller that abandons the enumerator without `DisposeAsync` is never observed.
- Design §2.2 "What counts as a read": peer responses are SystemHigh "until PR 5 ... `EgressGuardChatClient` observes it after the response" — for a stream the response is reaching the caller chunk by chunk. The lane observed before each `yield return` and in `finally` (`origin/...:EgressGuardChatClient.cs`, `ObservedAsync(IAsyncEnumerable...)`), twin `A_streamed_response_from_a_peer_counts_as_SystemHigh_before_the_caller_sees_it`.
- Master's twin `A_streamed_peer_response_is_observed_as_SystemHigh` (`EgressSubjectProducerTests.cs:284-301`) decides only after the loop, so it cannot see the window. No mutation covers streaming (PR body lists `peer-not-observed` only).

## M3 (major). D16 narrowed to `peer:` only, not recorded as a deviation

- `EgressGuardChatClient.cs:98-101`: `ObservesAgentBackedResponse => Request is not null && ... _targetKey.Trim().StartsWith("peer:", OrdinalIgnoreCase)`.
- D16 (DESIGN §3.B): "responses from `peer:` **and other agent-backed targets** observe SystemHigh"; §2.2 table: "Responses from `peer:` and other agent-backed chat targets". A governed target under any other key (`agent:*`, `mesh:*`, a host's custom key) is not observed. Critic C3 settled this as "take the lane's fail-closed rule (anything not `local:`/`cloud:`) and record it"; master took the permissive rule and the PR body/records say only "`local:` and `cloud:` are not reads", never naming the narrowing.
- Synchronous throw not observed (`:23-24` remarks; twin `A_peer_call_that_throws_synchronously_is_not_observed` `:304-318`): recorded in the body. Defensible under "no response came back" (PolicyGate denies before the inner client runs), but the design's sentence is "observes it after the response", and the lane observed on the synchronous throw too (fail closed). Minor on its own; listed here because it is the same bullet.

## M4 (major). "Read nothing" (`Public`) is reported for **any** store `ArgumentException`, not only the unrankable-query refusal

- `RAGTool.cs:79-102`: `catch (ArgumentException ex)` → returns `RagRefusal(Refused: true, Reason: ex.Message)`; `ReportRead` `:120-122`: `case RagRefusal: read.Report(SecurityLabel.Public)`. The payload carries `ex.Message` (`:98`, `:101`) and reaches the model (`FormatToolObservation`).
- D15 / §2.2: "with 0 hits, or with its unrankable-query refusal (`RAGTool.cs:96-99`), it reports 'read nothing'". A custom `IRAGService`'s other `ArgumentException` (whose message may carry what it read) is laundered to Public. The lane narrowed to `VectorMath.IsUnrankableQuery(ex)` (`origin/...:RAGTool.cs`, `VectorMath.cs` `UnrankableQueryMessage` const + `IsUnrankableQuery`), twin `A_stores_other_ArgumentException_is_not_read_nothing` (the one attack-list twin observed red first at `24a1d2a1`, state-4.5 §3). Not recorded as a deviation.

## M5 (major). Stale "not yet" sentences left false by the merge

- `CHANGELOG.md:172-173` (4.4 entry): "Still report-only, and no production code enters a frame or begins a read yet, so no recorded decision changes; the agent producers come in PR 4.5."
- `docs/specs/SPEC-007-...md:125` (4.4 bullet): "Report-only, and no production code enters a frame yet."
- Both false since `9b2ea56f4` (`SelfExtendRunnerAdapter.cs:251`). records-4.5 §1.3 and §2.2 named exactly these; the PR body claims the 4.4 "no production Enter" sentences "now name this wiring" — true only for row 65 (`ci/cert-gate-assertions.md:65` tail: "Production enters a frame only at the floor-pin sites (PR 4.5)") and row 66.

## M6 (major). Design-row content the merged PR lacks (vs the lane), without deviations recorded

| Row 4.5 / integrator item | Master `9b2ea56f4` | Lane `91d3b48c` |
|---|---|---|
| Open-read rule (owner) | not implemented (B1) | read frame, pinned, basis, open-until-disposed, 4 new read-scope twins, 2 flips |
| G5 report-only surface | `ReadScope` handed to the tool (M1) | `ReadReporter`, 3 twins, m26 prepared |
| Trim-parity theory "over case, white space and top-secret" | `RAGTool_hit_labels_match_TrustTierOrder_RecordLabel`, 14 cases (`EgressSubjectProducerTests.cs:235-255`): case, white space, `top-secret`, `SystemHigh`, unknown, custom `Pony` — present | 28 names + 6 non-ASCII look-alikes + 7 custom (name, value) rows + `CanonicalSpellings` guard |
| Convention test | `EgressSubjectFloorPinTests` (regex; file/method/floor/count; pins 1 `Enter`) | Roslyn: `Enter` **and** `BeginRead`, floor ≥ SystemHigh, `using` not in an iterator, no `using static`/alias, `Only_RAGTool_declares_itself_labelled_in_production`, 2 fixture facts |
| Producer twins | 14 methods (incl. 1 theory ×14) | 29 methods / 69 cases (impostor by id, decorator/other toolbox, OCE-throwing tool, side `Observe`, join of hits, custom levels, policy-denied agent-backed, empty stream, faulted stream, eager decide, unknown-kind key, `local:` not a read, G8 paired records) |
| Leak skeleton composition | fake model (`DecidingModel` calls `guard.Evaluate` directly), `FixedRag` fake; remarks `:31-34` admit it | real `RAGTool` over `MeaiVectorDataRagAdapter`/`VectorDataRagService`, governed `local:ollama` |
| Model check | none | `model-4.5/REPORT.md` quoted |
| Records | producers paragraph `docs/EgressInventory.md:26` + "known limit" `:28`; no G6 (persisted state = runner's floor), G7 (snapshot from literals), G14 (redirect hop inside a read at SystemHigh), MCP-pump `no-subject` caveat, attribution limit (C9), "RAGTool in no production toolbox" | all present (`origin/...:docs/EgressInventory.md:15-18`) |
| Decisions-log row | none (B3) | `:448` |
| `DESIGN-4-final.md` §5 amendment | n/a (anti-decision) | `ws/design-4.5-amendment.md` applied to `ws/DESIGN-4-final.md` |

## Minor

- m1. **PR body false counts.** Checklist: "Certification count 133 `.cs` files, 136 entries"; the merged paragraph (`ci/cert-gate-assertions.md:72-73`) says 135/138 (`git ls-tree 9b2ea56f4`: 135 `.cs`, 138 entries — correct), and at `095ba46e2` it is 136/139 (`AdmissionGateDemoTests.cs` from #644) while the paragraph still says 135 and "as of 2026-10-06" though merged 2026-10-08.
- m2. **Mutations are single-twin evidence.** All four lines read `red=failed:1/1 green=passed:1/1`: each filter selected exactly one test, so each proves one twin notices the mutant, not that the gate does; the design asks for "a mutation per rule" and there is none for the read-scope wrap itself, `Complete`-before-invoke ordering, the trim, the `All` reference check / custom → SystemHigh, the streaming observe, or a lowered floor (`new HighWaterMark()`); the first `drop-enter` was INVALID (`CS0161`). "Cert-gate was not re-run on net10.0" is honest and immaterial: `scripts/run-cert-gate.sh:25-30` is `-f net8.0` only, as is `.github/workflows/cert-gate.yml`.
- m3. **Red-first covered 6 of 14 facts.** `Failed: 6, Total: 6` on `11567292`; the empty-RAG, unrankable, during-read, parity theory and three peer facts were added with the wiring (`4013b491`) and never seen red; `An_egress_during_the_tool_call_is_decided_at_the_pre_read_mark` pins existing behaviour.
- m4. **Design/CHANGELOG mismatch on where RAGTool's trim/parity is pinned**: the design asks for the parity test; present, but only in `EgressSubjectProducerTests` (no row names it; row 68 names `EgressSubjectFloorPinTests` only; row 66 tail names `ToolCallingAgent`/`IEgressLabelledTool` under `EgressSubjectReadScopeTests`, which holds none of the producer facts).
- m5. **Unrelated test weakening bundled**: `EgressGuardDecisionTests.cs:723-728` now skips `ref` in the credential-substring scan (reasonable, Ref is random hex), a 4.6 twin change inside a 4.5 PR with no record line.
- m6. **New `InternalsVisibleTo Ashlar.Tests.Infrastructure` on `Ashlar.BackgroundAgents.csproj:18`** (for `RAGTool.MapHitLabel` internal) — not mentioned in CHANGELOG or body.
- m7. The floor-pin test pins `Enter` only (D19 letter met); `BeginRead` sites, the `using` shape, iterator bodies and aliases are not checked (obligation (d) is asserted in prose at `SelfExtendRunnerAdapter.cs:248-250`, not mechanically). `Floor()` reads the first `)` after `new HighWaterMark(`, so a floor written as `new HighWaterMark(Label())` would pin as `Label(`.
- m8. `EgressInventory.md:26` says "the toolbox is a `CapabilityRegistry` that can find it" — correct; but the design's F2 fact (RAGTool registered in no production toolbox, so no production read is ever reported below SystemHigh) is not stated anywhere on master.

## Verified OK (how)

- Self-extend frame at SystemHigh, obligation (d): `SelfExtendRunnerAdapter.cs:251-311` `using (EgressSubject.Enter("agent:" + resolvedAgentId, new HighWaterMark(SecurityLabel.SystemHigh)))` on `RunAsync`'s own flow (the method is `async Task` and both enters and disposes), inside the `try` at `:180`, disposed in order by the `using` (exception path included, before `catch (Exception ex)` at `:313`), not an iterator, not an async helper; wraps `RunCycleAsync` (`:254-256`), scratchpad (`:261`), claim release (`:278`), admission/auto-share (`:297-301`). Overloads at `:125-133` only await. Objects created before the frame (toolbox `:201`, agent `:226`, snapshot `:235`) start no `Task`/timer (`RepoFsToolboxFactory.cs` has no `Task.Run`/`Timer`/`HttpClient`), so the invocations inside the block carry the frame; MCP transport pumps created at connect time are the one caveat (unrecorded, see M6).
- `ToolCallingAgent` read scope per call: `:235-256`, `Complete()` last in the block, a throw (incl. OCE at `:241-245`) skips `Complete` → SystemHigh (D13/D14 for unlabelled and throwing tools). Labelled gate `tools is CapabilityRegistry registry && registry.Find(call.Id) is IEgressLabelledTool` (`:249-250`): a decorated or other-toolbox tool is unlabelled (fail closed); `CapabilityRegistry.Find` (`CapabilityRegistry.cs:26-33`) is the same dictionary `InvokeAsync` uses.
- RAGTool mapping (D15 letter, O1 in code): `MapHitLabel` `:142-159` trim → `GetByName` → `ReferenceEquals` with `DataSensitivityLevels.All` → `ToDataLabel`; custom (`PonyLevel`) and unknown → SystemHigh; 0 hits → Public (`:123-125`); `default` → SystemHigh (`:130-131`). Parity with `TrustTierOrder.RecordLabel` (`TrustTierOrder.cs:110-111`, `TryRank` trims + OrdinalIgnoreCase, `top-secret` alias `:67`; `FromName` `DataSensitivityLevels.cs:92-98` lower-cases, accepts `topsecret`/`top-secret`) over 14 inputs incl. `" Confidential "`, `" TOP-SECRET "`, `"SystemHigh"`, `"Pony"` (`EgressSubjectProducerTests.cs:235-255`), green in cert-gate 2785/2785.
- Peer response observed after return and after fault: `EgressGuardChatClient.cs:103-113` (`finally` on the awaited task), `UseAshlarGovernance` passes the key (`AshlarGovernanceChatClientBuilderExtensions.cs:30`); key trimmed, any case (`:101`), twin `:258-281`. Both overrides remain non-`async` (`:75-94`), so `CloudBedrock_DeniedByPolicyGate_...` and `AThrowingCustomGuard_...` do not flip (file untouched; cert-gate green). F5 single construction site holds (`grep 'new EgressGuardChatClient('` production = the builder only).
- Leak skeleton done-when in report mode: `:46-66` (Secret → `subject:agent:leak-secret`, `Current = Secret`, `LevelTooLow`, `Mode = report`, `Refused = false`), `:69-85` (Internal → allowed), `:135-172` (production self-extend → `subject:agent:planner-9`, SystemHigh, `SystemHighData`, report). Unlabelled `:88-109`, throwing `:112-132`, empty `:175-187`, unrankable `:190-200`.
- Floor-pin convention test and row: `EgressSubjectFloorPinTests.cs` (scan floor 1000, production roots, test projects pruned by csproj via `EgressGuardConventionTests.IsTestProjectRoot`, pin `SelfExtendRunnerAdapter.cs | RunAsync | SecurityLabel.SystemHigh | x1`); row 68 `ci/cert-gate-assertions.md:68` in the same change (CLAUDE.md rule met). D19 "frame below SystemHigh wraps only observed code": no production frame below SystemHigh.
- `PublicAPI.Unshipped.txt:58-59`: `IEgressLabelledTool` and `ReportRead` declared; `PublicAPI.Shipped.txt` untouched; no member added to shipped `ITool`/`IToolbox`/`ToolResult` (records-4.5 F3).
- Floors comment `EgressGuardConventionTests.cs:205-211` restated (2,145 / 150) — stated as measured; not re-measured here.
- Knowledge graph: `shell-lint` green on `f15639d2` → byte-equal at the PR head.
- Required checks green on `f15639d2`; readiness `verified` (4 lanes) on `9b2ea56f4`.
- No `[coordinated-integration]` needed: no `application/` file in the PR's 20 files.

## Not checked

- Nothing was built or run (no container run for this lens); floors 2,145/150, cert-gate 2785 and the 4 mutation lines are the PR's claims.
- `.NET` casing of U+0130 under `ToLowerInvariant` vs `OrdinalIgnoreCase` (the lane's non-ASCII parity concern); state-4.5 §3 reports the lane's 6-case twin passed on the pre-fix tree, so no write-down is shown either way.
- Whether the MCP SDK sends on a pump created before the frame (records wording only).
- Post-#723 drift of the count paragraph (136/139 at `095ba46e2`) belongs to the #723/#644 lens.

## Follow-up PR content (what the lane had that master lacks)

1. The read frame (`EgressSubject.cs`/`ReadScope.cs` diff on the lane), pinned mark, basis rule, open-until-disposed; flip `A_completed_read_with_no_report_observes_SystemHigh` and `A_read_observes_into_every_live_frame_..._from_whatever_flow_it_ends_on`; invert `An_egress_during_the_tool_call_is_decided_at_the_pre_read_mark`; the lane's 4 new read-scope twins (in-call egress, frame inside a read, work created inside a read, nested reads) and the basis/open-until-disposed twins; mutations m01–m03 at the shipping tree.
2. G5: `ReadReporter` + `ILabelledTool.InvokeLabelledAsync(..., ReadReporter, ...)` (or keep `IEgressLabelledTool` but pass a report-only surface), `ReadScope.Reporter`, `PublicAPI.Unshipped` lines, the 3 G5 twins, mutation m26.
3. Streaming observe before each update (lane `ObservedAsync(IAsyncEnumerable...)`), synchronous-throw observe, agent-backed = not `local:`/`cloud:` (or a recorded deviation), the lane's 10 peer twins.
4. RAGTool: `IsUnrankableQuery` narrowing (`VectorMath.cs`), `CanonicalSpellings` guard, the 28-name parity theory + 6 non-ASCII + 7 custom rows, join-of-hits, impostor-by-id and decorator twins.
5. Convention: Roslyn `EgressSubjectProducerConventionTests` (Enter + BeginRead, `using` not in iterator, no alias/static import, `Only_RAGTool_declares_itself_labelled_in_production`, fixture facts) or extend `EgressSubjectFloorPinTests`; row 68 updated.
6. Records: SPEC-007 decisions-log row (owner rule + O1), intro `:433`; rewrite `docs/EgressInventory.md:28`, SPEC `:133-142`, CHANGELOG `:22-30` from "known limit" to the rule; fix `CHANGELOG.md:172-173`, SPEC `:125`; producers section gains G6/G7/G14/MCP-pump/attribution/"no production toolbox" sentences; count paragraph date; publish `ws/design-4.5-amendment.md` to `claude/spec-007-pr4-workspace`; quote `model-4.5/REPORT.md` §8; post `handoff`/`done` for pr-721 and the follow-up on #695 so Grok's drift audit runs.
