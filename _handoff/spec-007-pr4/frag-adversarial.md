Two inputs were produced for this lane after the restart, read in full before the final push: `attack-4.5.md` (25 numbered attacks, 10 claims "[needs a test]", 11 design findings) and `records-4.5.md` (a records-and-evidence checklist with 4 owner questions). Every item is closed below or says why not.

### `attack-4.5.md`

| # | Attack | Closed by |
|---|---|---|
| 1 | A tool egresses during its own call at the pre-read mark | The read's frame. `A_tools_own_egress_during_its_call_is_decided_at_SystemHigh`, `RAGTools_own_egress_during_its_call_is_decided_at_SystemHigh`, `A_tool_that_reads_then_egresses_within_one_call_is_decided_at_SystemHigh` (basis `subject:agent:…`, never `no-subject`); the flip of `A_completed_read_with_no_report_observes_SystemHigh` by name; mutation m01. The attack's "after the report but before dispose must be the reported label" is where this PR is stricter (deviation 1). |
| 2 | The report is made on the tool's flow and never reaches the agent's | `ReadScope` is one shared object; `Report` writes it by compare-and-swap, not an `AsyncLocal` value. The leak skeleton (a report from an awaited `async` tool body makes the next model call `Secret`, `LevelTooLow`). |
| 3 | A scope disposed from another flow, or twice | The read's frame is a frame under the no-skip rule: the flow that began it stays inside at `SystemHigh` (fail closed, availability cost), pinned by the flipped `A_read_observes_into_every_live_frame_of_its_chain_from_whatever_flow_it_ends_on`, also for a frame entered afterwards; recorded as limit (a) for reads in the `EgressSubject` remarks and row 66. Disposing twice does nothing (`_ended`). |
| 4 | The toolbox throws synchronously before any task exists; `Complete()` too early | The read is begun before the toolbox is asked; `Complete()` is the last statement of the block. `A_call_for_an_unregistered_tool_counts_as_SystemHigh` (4a), `A_labelled_tool_that_reports_and_then_throws_counts_as_SystemHigh` (4b), `A_tool_that_throws_OperationCanceledException_counts_as_SystemHigh` (4c); mutation m04. |
| 5 | Nested scopes tracked as one slot | Frames chain. `Nested_read_scopes_each_keep_the_flows_inside_them_at_SystemHigh_until_their_own_end` (inner `Internal` raises the frame while the next decision is still `SystemHigh`; both reported, `Secret`). |
| 6 | The marker is checked by tool id | Checked on the instance `CapabilityRegistry.Find` returns. `A_tool_registered_under_RAGTools_id_is_not_labelled_by_the_id` (last-wins registration; the stub store is never searched). |
| 7 | A public marker lets any plugin declare itself labelled | `Only_RAGTool_declares_itself_labelled_in_production` (root-wide Roslyn scan of base lists); the producers section and the `ILabelledTool` remarks say a host's own labelled tool is the host's trusted base; mutation m22. |
| 8 | The scope is handed through a channel every tool sees | It is passed only as `InvokeLabelledAsync`'s parameter to the instance that declared itself labelled; `snapshot.Data` and the arguments never hold it (`BuildSystemPrompt` is unchanged). No twin: there is no channel to probe. |
| 9 | A wrapped labelled tool | `A_labelled_tool_behind_a_decorator_or_another_toolbox_is_not_labelled`; `ObservingTool` named in the producers section. |
| 10 | Unicode case folding diverges in the loose direction | **Real.** Fixed: `HitLabel` accepts only a spelling `TrustTierOrder` ranks under `OrdinalIgnoreCase` (deviation 7). `RAGTool_never_labels_a_hit_below_what_the_pipeline_treats_it_as` over U+0130, U+017F, U+212A, a full-width S, a zero-width space and a trailing no-break space, two-sided as the attack asks (domination always; equality where both sides agree). Mutation m24 (drop the check), m08 (drop the trim), m09 (drop the `All` check). |
| 11 | A custom level sharing a primitive's value or display name | `RAGTool_labels_a_hit_at_a_custom_level_SystemHigh_whatever_its_name_or_value` over `Restricted` (0, 1, 3), `Top Secret` (4), `UltraSecret` (5), `Open` (-1) and `ſecret` (1); mutation m09. |
| 12 | "Read nothing" on the refusal path carries a store's exception text at `Public` | **Real.** Fixed: only `VectorMath.IsUnrankableQuery(ex)` reports `Public` (deviation 6). `A_stores_other_ArgumentException_is_not_read_nothing` (a canary in the message: next call `SystemHighData`), and the real refusal through `RAGService` over `InMemoryVectorStore` with a zero embedding still reads nothing; mutation m23. |
| 13 | Zero hits versus a filtered corpus | (a) no hits → `Public` (the read-nothing twin); (b) is the same from the tool's side; (c) a sub-Public floor `Open` (-1) → `SystemHigh` (the custom-level theory). |
| 14 | The frame is entered in a helper, iterator or lambda | A `using` declaration in `RunAsync` itself; the convention fact pins the enclosing method and would show a local function or lambda; `Production_self_extend_records_its_agent_as_the_subject_at_SystemHigh` pins the model call's record. The EG-MESH-01 record is not pinned at runtime ("Not observed failing"). |
| 15 | The floor is lowered quietly | The convention pins the literal argument `new HighWaterMark(SecurityLabel.SystemHigh)` (a field, an alias or `new HighWaterMark()` is a different floor string and fails); the self-extend twin pins `Current == SystemHigh` at runtime; mutation m15 goes red on both. |
| 16 | The convention scans for a spelling, not a call | Roslyn, not text: `EgressSubject.Enter`/`BeginRead` as identifier, member access or alias-qualified; a `using static` or a `using` alias of `EgressSubject` fails the scan (new); mutation m25 plants an alias. A method group, a delegate or reflection is not seen: recorded in row 68 and the test remarks as the tripwire's limit. |
| 17 | The `using` spans a `yield return` | The convention's iterator check and its fixture (`Iterator()`, `IteratorAroundLambda()`). No production streaming runner exists. |
| 18 | Work created before the frame reads for the agent | Recorded in the producers section and deviation 12 (MCP pump, `ProviderFactory` warm-up stay `no-subject`); not tested. |
| 19 | Post-cycle steps read tool-derived data outside any scope | At `SystemHigh` the frame only attributes; the design's producer rule is recorded in the convention test remarks ("a frame below SystemHigh may wrap only code whose reads are observed") and the producers section. The sub-SystemHigh variant is the leak skeleton's second decision (`Secret`). |
| 20 | A bounded subject id; a synchronous tool renames the subject | Recorded in the producers section as attribution limits (monotone nesting keeps the label). |
| 21 | The peer observe runs after the response and a consumer stops early | Observed before each update reaches the consumer (`A_streamed_response_from_a_peer_counts_as_SystemHigh_before_the_caller_sees_it`), at the end of the stream (`A_streamed_response_with_no_updates_from_a_peer_still_counts_as_SystemHigh`), on a faulted task or stream and on PolicyGate's synchronous deny (`A_call_to_a_peer_that_fails_…`, `A_call_to_an_agent_backed_target_that_the_policy_denies_…`); mutations m12, m13, m18–m21. The denial outcome is deviation 4. |
| 22 | An async-iterator override makes `Decide` lazy | Both overrides stay non-`async`. `A_streamed_call_to_a_peer_is_decided_when_it_is_made_not_when_it_is_enumerated` (one decision, in the frame that made the call; enumerating in another frame decides nothing more). |
| 23 | "Agent-backed" cannot be recognised | Keyed on the target key, fail closed for every key that is not `local:`/`cloud:`; the host's inner client under those keys is its trusted base (producers section, `EgressGuardChatClient` remarks; deviation 3). |
| 24 | A host frame around `AgentHost`/`ThinkAsync` | Recorded: the producers section names every uninstrumented path and says a frame below `SystemHigh` around any of them is a write-down. No twin: it would pin what is not fixed. |
| 25 | The MCP server returns a tool's message with no decision | Not a 4.5 path; the producers section lists the MCP server's responses among the unobserved. Untouched. |

**B. Claims marked [needs a test].** B1 (U+0130 folding): made moot by the spelling check; the domination theory records the runtime's answer either way. B2 (where the MCP SDK sends): not tested, recorded as a limit. B3 (whether the MEAI path ever throws the known refusal): it does not (`UnrankableQuery` is thrown only by `InMemoryVectorStore` and `SqliteVectorStore`), so on the MEAI path the `Public` report is unreachable and every `ArgumentException` counts as `SystemHigh`; the refusal twin uses the legacy store. B4 (the read's state flows like a frame): it is a frame; `Work_created_inside_an_open_read_keeps_its_frame_after_the_read_ends` and 4.4's capture twins. B5 (every subject record at `SystemHigh`): the model call's record is pinned; the admission's is not ("Not observed failing"). B6 (production `RAGTool` floored to `Public`-tier records): `RAGTool` is in no production toolbox; recorded. B7 (`Decide` eager): item 22's twin. B8 (a 256-character id): recorded, not tested. B9 (`OperatorKey.TryLoad` in a test): not attempted (B5). B10 (an undisposed frame becoming the registry's head): impossible, an `async` method's frame never reaches its awaiter; the `using` declaration disposes anyway.

**C. Where the design is wrong against master.** C1, C2: deviation 2. C3: deviation 3. C4: the flips by name, the `ReadScope`/`EgressSubject` remarks and row 66 are rewritten; the Python model is the integrator's sibling workflow ("Model check" above). C5: the B/C5 site-and-family note in the decisions-log row (deviation 8). C6: deviation 12. C7: recorded in the producers section, SPEC-007 and the CHANGELOG (deviation 10). C8: deviation 6. C9, C10: stale citations in the design document, not in this PR's records. C11: the marker is in Abstractions, so `PublicAPI.Unshipped.txt` is its record.

### `records-4.5.md`

| # | Item | State |
|---|---|---|
| 1.1–1.3, 1.5, 1.6 | SPEC-007 bullet, past-tense 4.4 sentence, decisions-log intro and row | Done (Records). |
| 1.2 | 4.4's merge SHA | Left for 4.3 (deviation 11). |
| 1.4 | 4.4's "when it ends" sentence | Left as what 4.4 shipped; the 4.5 bullet states the amendment. |
| 1.7 | The 4.5 plan line says the obligation is discharged | Done ("discharged in PR 4.5: `EgressSubjectProducerConventionTests` …"). |
| 1.8 | Which rule for custom levels | `SystemHigh` (deviation 5), stated in the bullet. |
| 1.9–1.11 | Unchanged sections | Untouched. |
| 2.1–2.4 | CHANGELOG entry, past-tense 4.4 sentence, the amended `ReadScope` sentences, which records change basis | Done; the basis change is a sentence in the Added entry naming EG-MDL-01, EG-PROC-01 and EG-MESH-01, not a separate Changed bullet (no production outcome changes). |
| 3.1 | Line-numbered citations of the edited files | None of the four edited files is cited by line in `docs/EgressInventory.md`; the opening paragraph is unchanged. |
| 3.2–3.5 | The producers section, the known limits it creates, the "No call site carries a `SecurityLabel`" bullet | Done (3.5's optional per-row note not added). |
| 3.6 | Docs rows | Still 67 (prose, no `EG-` row). |
| 4.1 | TSV | Unchanged; sums re-verified (86/150/48; 33/35/10/4/4). |
| 5.1 | Row 56 | Unchanged: the leak skeleton uses explicit guards, composes neither profile and names no environment variable (`grep` over the new test files finds none of the markers). |
| 5.2–5.6, 5.8, 5.9 | Rows 64, 65, 66, the new row 68, the count paragraph | Done (Records). |
| 5.7 | Row 67 | Unchanged. |
| 6.1–6.3 | PublicAPI | `ILabelledTool` and its member in Unshipped; nothing on shipped `ITool`/`IToolbox`/`ToolResult`; Runtime/AI.Pipeline additions recorded in the CHANGELOG and this body. |
| 7.1 | Floors comment | Done, measured 2,136 / 150 / 67. |
| 8.1 | Knowledge graph | Regenerated after `git add`; the repo gates' byte compare passes. |
| 9.1 | Readiness paths | Unchanged; no new project. |
| 10.1 | `[coordinated-integration]` | Not needed. |
| 11.1 | Runbook / release notes | Unchanged; the overrides stay synchronous. |
| 12.1 | Environment convention | Not triggered (5.1). |
| 13.1 | Flip of `A_completed_read_with_no_report_observes_SystemHigh` | Done, by name, in row 66 and this body. |
| 13.2 | `CloudBedrock_DeniedByPolicyGate_…_AndThrowsAtTheCall` must not flip | Holds: both overrides stay non-`async`; the cert-gate passes. |
| 13.3, 13.4 | The `peer:` record; `new EgressGuardChatClient(` only in the builder | Hold: the record is unchanged; the construction site is the builder's (F5 passes). |
| 14 | Test files likely broken | `Ashlar.Tests.BackgroundAgents` {{BGA}}, `Ashlar.Tests.AI.Pipeline` {{AIP}}, `Ashlar.Tests.Orchestration` {{ORCH}}, the cert-gate {{CERT}}, {{OTHER}}. |
| 15 | Counts to re-measure | Re-run on the pushed head (Records); to be repeated after the master merge. |
| Owner Q1 | What "the flows inside it" means; the basis; after `Report` before `Dispose` | Answered by the implementation and the integrator's instruction: the read's frame on the flow (and on work created inside it); basis stays `subject:<id>`; `SystemHigh` until disposed (deviation 1). |
| Owner Q2 | Custom levels | `SystemHigh` (deviation 5). |
| Owner Q3 | Register `RAGTool` in self-extend | No (deviation 10). |
| Owner Q4 | Where the marker lives | Abstractions, public, Unshipped; a wrapped tool is unlabelled (deviation 2). |

### `phase-C-critic.md` (the phase C completeness critic; its 4.5 rows, §4, §5 and §6 items 1–6)

| # | Item | Closed by |
|---|---|---|
| G5 | D14 against the labelled tool itself: `ReadScope.Complete()`/`Dispose()` are public and the lane handed the whole scope to `ILabelledTool` | **Real; fixed.** `ILabelledTool.InvokeLabelledAsync` now takes a report-only `ReadReporter` (new, sealed, internal constructor, `Report` and nothing else, handed out as `ReadScope.Reporter`); the scope, and so completing or ending the read, stays with `ToolCallingAgent`. Twins: `A_labelled_tool_cannot_complete_or_end_the_read_it_is_handed` (a hostile tool invokes every parameterless public method of what it is handed, reports `Public`, throws: the surface offers nothing but `Report`, and the next model call is `SystemHighData`) and `The_surface_a_labelled_tool_is_handed_reports_and_does_nothing_else` (reflection over `ReadReporter` and the marker's parameters). Mutation m26 (a report completes the read). `PublicAPI.Unshipped.txt` carries the changed signature, `ReadReporter`, `ReadReporter.Report` and `ReadScope.Reporter`. |
| G6 | D17 "persisted state coming back is SystemHigh" is a sentence nobody can check | The producers section now says: persisted state coming back is covered by the runner's floor, and a runner that declares a floor below `SystemHigh` must observe what it loads. No code is possible in 4.5 (the floor is the mechanism). |
| G7 | Nothing pins that a sub-SystemHigh runner's snapshot carries no carry-over | The producers section and this body name the obligation the leak skeleton's test runner meets: its snapshot is built from literals only (`agentId`, `maxDataSensitivity`). |
| G8 | "No production outcome changes" is a records-diff claim | `Production_self_extend_records_its_agent_as_the_subject_at_SystemHigh` now asserts paired records: every record with the subject basis has `Current == SystemHigh` and the same `Access` (allowed, reason), destination class and label as the same request decided with no frame. |
| §4.1 | The report channel and who may call it | G5's twins; the impostor, wrapper and other-toolbox twins (attack items 6–9). |
| §4.2 | The basis string | `A_decision_inside_an_open_read_is_named_by_the_nearest_live_subject_frame_never_by_the_read` and the basis assertions of the in-call twins. |
| §4.3 | Timers, registrations, cold tasks around a read | Not tested for read frames: the read's frame is an `AsyncLocal` frame like any other, so 4.4's capture table and twins apply unchanged; `Work_created_inside_an_open_read_keeps_its_frame_after_the_read_ends` pins the `Task.Run` case. Recorded under "Not observed failing". |
| §4.4 | `AsyncLocal` persistence inside an async helper | `An_awaited_read_that_throws_observes_SystemHigh` passes on net8.0 and net10.0 in the egress filter. |
| §4.5 | Concurrency (the three-state window, the `Report` CAS) | 4.4's concurrent-report twin still passes; a `Dispose` racing a `Decide` is the `ReadScope.Dispose` ordering noted under "Not observed failing". |
| §4.6 | Exception filters around a read | `ToolCallingAgent`'s only filter inside the read is `when (!ct.IsCancellationRequested)`, which decides nothing; `A_tool_that_throws_OperationCanceledException_counts_as_SystemHigh` runs through it. |
| §4.7 | Iterators and alias spellings | The convention's fixture facts show the iterator and alias detection from the syntax; mutation m25 plants an alias in production; no production iterator entry exists to plant a `yield return` in (the fixture's `Iterator()` is the evidence). |
| §4.8 | Peer responses | Attack items 21–22's twins; records-4.5 13.2's "must not flip" holds (the cert-gate passes). |
| §4.9 | `RAGTool`'s mapping | Attack items 10–13's twins and mutations m07–m09, m23, m24. |
| §4.10 | Exact frame counts for read-frame twins | Not done; a follow-up (the C# `FlowModel` needs a read-frame node). |
| §4.11 | Model bounds | Quoted with the bounds in "Model check". |
| §4.12 | MCP transport pumps | Recorded as `no-subject` work captured before the frame; not tested. |
| §5.1 | Cross-lane conflicts | Listed under Records ("Expected conflicts"); the row the lane calls 68 is row 69 if 4.10's `AirGappedHygieneTests` row lands first; 4.3's TSV change makes "150 occurrences" 149 after its merge, to be re-measured once on the merged tree. |
| §5.2 G14 | A redirect hop followed during a tool call | The producers section says it is decided inside the read's frame, at `SystemHigh`. |
| §5.2 G18 | The internal `EgressGuardChatClient` constructor and F5 | The construction stays in `AshlarGovernanceChatClientBuilderExtensions.cs`; F5 passes in the cert-gate. |
| §6.2 | Reject attack item 1's "Internal after report" | Rejected (C2): "not yet ended" is kept and stated as stricter than the owner's words, with why (a report for part of a result followed by more reading would reopen the write-down). |
| §6.3 | O1 | Asked by the integrator; answered by the owner on 2026-10-06: D15 stands, custom levels are `SystemHigh` in PR 4, Q8's C3 normalisation is deferred to the first producer that wants custom tiers. Recorded in the 4.5 decisions-log row, the parity theory's custom-level rows and deviation 5. |
| §6.4 | Verify the flips and non-flips by name | Flipped: `A_completed_read_with_no_report_observes_SystemHigh` (line 50's `Public` inside the open scope → `SystemHigh`) and `A_read_observes_into_every_live_frame_of_its_chain_from_whatever_flow_it_ends_on` (line 179's `LevelTooLow` → `SystemHigh`, `SystemHighData`), both named in row 66 and this body. Not flipped: `EgressGuardChatClientTwinTests.CloudBedrock_DeniedByPolicyGate_StillRecordsOneExternalModelDecision_AndThrowsAtTheCall`, `AThrowingCustomGuard_NeverReachesTheCaller_OnGetResponseAsync` and `_OnStreaming`, and F5 (`EgressGuardConventionTests`), all green in the full cert-gate at the pushed head. |
| §6.5 | Records | The three "not yet" sentences, `docs/EgressInventory.md:220`, the producers section with G6/G7/C9/G14 sentences, the MCP-pump caveat and the row numbering: all done (Records). |
| §6.6 | DESIGN §5 amendment | Drafted for the integrator at `ws/design-4.5-amendment.md` (Scenario B's reason, C4, C5, a mutation note). |
