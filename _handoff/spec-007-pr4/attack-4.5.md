# Attack list for SPEC-007 PR 4.5 (producers, report-only)

Read-only scout against master `de41a8ac8d0d1ae94f075c7aba2dde366c890fec` (host checkout `/home/user/Ashlar`, clean
tree). Every `file:line` below was read at that commit unless marked **[unverified]**. Design citations are to
`DESIGN-4-final.md` (§ and line in that file) and to the phase B handoff (`handoff.md`). Nothing was built or run.

**What 4.5 builds, and what master gives it to build on.** Master has the frame machinery (`EgressSubject.Enter`,
`Observe`, `BeginRead`, `ReadScope`, `RunDetached`; `src/Ashlar.Abstractions/Security/Egress/EgressSubject.cs`,
`ReadScope.cs`) and **no production caller of any of it**: the only non-test `EgressSubject.` call in the repository is
`AgentBus.cs:69` (`RunDetached`). 4.5 adds the first producers. Three facts about master shape every attack here:

- `ToolCallingAgent.RunCycleAsync` sees tools only through `IToolbox` (`ToolCallingAgent.cs:141-147`, `:236`;
  `src/Ashlar.Abstractions/IToolbox.cs:51-68`), which has `Schemas()`, `InvokeAsync(...)` and `MemoryFor(...)` and no
  way to reach a tool instance. The design's "marker that `ToolCallingAgent` checks" (DESIGN §2.2 :222-224) therefore
  needs a new seam; §C.1 below.
- `ReadScope` is held, not ambient (`ReadScope.cs:20-23`), it observes only on `Dispose` (`:24-25`, `:78-86`), and
  `EgressSubject.Resolve` walks frames only (`EgressSubject.cs:238-251`); `BeginRead` pushes nothing onto the flow
  (`:120`). The owner's 2026-10-06 rule ("while a read scope is open and unreported, every egress decided on the flows
  inside it is decided at SystemHigh") is **not expressible with master's `ReadScope`**; §C.4 below. How the lane
  implements it decides which of items 1-5 bite.
- In production the floor is SystemHigh, and `ReferenceMonitor.CanWrite(SystemHigh, x)` is refused for every
  non-SystemHigh destination exactly as `no-subject` is (`ReferenceMonitor.cs:53-64`; `EgressSubject.cs:10-12`). So
  **no production write-down is reachable in 4.5**; every production item below is about records (attribution) and
  about obligation (d). The write-downs are reachable only through a runner with a floor below SystemHigh: the leak
  skeleton (DESIGN §5 :919-924), and any future runner. The attacks are written for that runner, with
  `SelfExtendRunnerAdapter` named where its code is the path.

Record vocabulary used in the twins: `destination` is `scheme://host[:port]` or the name as recorded
(`EgressDecision.cs:78-84`); classes and labels are Host/SystemHigh, ExternalModel/Internal, WebSearch/Confidential,
NetworkExport/Internal, Unknown/Public (`EgressDestinations.cs:106-111`); a `file:` name is never Host
(`EgressDestinations.cs:188-189`). With `new EgressGuard("full")` every record is `Mode=report`,
`ModeBasis=profile:full`, `Refused=false`; with `new EgressGuard("secure-workstation")` it is `report` /
`profile:secure-workstation` until 4.11; with `new EgressGuard("secure-workstation", "enforce")` it is `enforce` /
`override`, `Refused=true` and nothing acts (`EgressGuard.cs:184-211`; `EgressDecision.cs:56`). `CurrentBasis` is
`subject:` + `EgressDestinations.Bound(id)` (`EgressSubject.cs:95`, `:68-69`), `no-subject` with no live frame.

---

## A. Numbered attack list

### A.1 The open read scope (the owner's 2026-10-06 rule)

**1. A tool egresses during its own call and is decided at the pre-read mark.**
- *Path today.* `ToolCallingAgent.RunCycleAsync` → `tools.InvokeAsync(call, snapshot, loopCt)` (`ToolCallingAgent.cs:236`)
  → `CapabilityRegistry.InvokeAsync` (`src/Ashlar.Runtime/CapabilityRegistry.cs:40-45`) → `WebSearchTool.InvokeAsync`
  (`WebSearchTool.cs:58-89`) → `BingWebSearchProvider.SearchAsync`, which evaluates
  `EgressGuard.ProcessDefault.Evaluate(new EgressRequest(WebSearch, "EG-WEB-01", uri))` **before** the send
  (`BingWebSearchProvider.cs:56`), on the tool's flow, inside the agent's frame. `Resolve` reads only the frames' marks
  (`EgressSubject.cs:238-251`); a scope begun around the call changes nothing until it is disposed (`ReadScope.cs:78-86`).
  So today, and with a scope wired the 4.4 way, the Bing decision is at the frame's mark as it was before the call.
- *Design.* Owner 2026-10-06: decided at SystemHigh for every tool. DESIGN §2.2 :219-229 (D13, D14); §5 Scenario B
  :937-946, reason now `SystemHighData`. Handoff §3 "Open questions" :74.
- *Twin.* Frame `Enter("agent:t1", new HighWaterMark(SecurityLabel.Public))`; `BeginRead()`; inside the scope (same
  flow, and again from an awaited async tool body, and again from a stub `IEmbeddingGenerator.GenerateAsync` called by a
  real `VectorDataRagService` — `VectorDataRagService.cs:109`, `:185-190` — to cover RAGTool's own pre-report egress),
  evaluate family `web-search` to `https://api.bing.example`. Expected: destination `https://api.bing.example`, class
  WebSearch, label Confidential, **Current SystemHigh**, CurrentBasis `subject:agent:t1` (pin whichever basis the PR
  records; it must not be `no-subject`, or the operator cannot tell the scope rule from a missing frame), Access.Reason
  `SystemHighData`, Mode `report`, ModeBasis `profile:full`. Then `Complete()` with no report, dispose, decide again:
  SystemHigh/`SystemHighData` (the 4.4 unreported rule). Then a second frame with a scope that `Report(Internal)`s and
  completes: a decision made *after the report but before dispose* must be `Internal` (LevelTooLow to an ExternalModel):
  "open and unreported" ends at the report, which is the "tool reads then egresses in one call" case the owner decided.
- *Flip by name.* `EgressSubjectReadScopeTests.A_completed_read_with_no_report_observes_SystemHigh` line 50 pins
  `Decide(...).Current == Public` **inside the open scope** ("the read has not ended yet"). The owner's rule makes that
  assertion false; the PR must flip it by name, as 4.4 flipped `An_Internal_subject_may_reach_…`.
- *Mutation.* Make `Resolve` ignore open scopes (restore master's loop at `EgressSubject.cs:246-248` as the whole
  answer). Red: the inside-the-scope decision above (`Public`, `LevelTooLow`/Allowed instead of `SystemHighData`), and
  the leak skeleton's Scenario B reason (`LevelTooLow` instead of `SystemHighData`).

**2. The report is made on the tool's flow and the agent's flow never learns of it.**
- *Path today.* `RAGTool.InvokeAsync` is an `async Task` (`RAGTool.cs:62`) awaited by the agent (`ToolCallingAgent.cs:236`).
  Anything the tool writes into an `AsyncLocal` *value* stays in the tool's execution context and never flows back
  (`EgressSubject.cs:20-22`). If the lane keeps "open/unreported" as the AsyncLocal's value (a counter or a flag) rather
  than as state on a shared object the AsyncLocal points at, `Report(label)` inside the tool clears the child's copy
  only; the agent's flow still counts the scope as unreported after the tool returns.
- *Design.* §2.2 :219-224: a reported scope "observes only what was reported"; the done-when (DESIGN §4 row 4.5 :855):
  after a Secret hit the next model call is `Current = Secret`, `LevelTooLow`.
- *Twin.* Frame Public; the agent-shaped loop: `BeginRead()` on the agent flow, `await` a tool whose body is an
  `async` method that `Report(Secret)`s from inside, returns; `Complete()`; dispose; the next `model.meai` decision to
  `https://remote.example` must be destination `https://remote.example`, ExternalModel/Internal, **Current Secret**,
  CurrentBasis `subject:agent:…`, Access.Reason **`LevelTooLow`**, not `SystemHighData`. Also assert the decision made
  *inside the tool after its report* is `Secret`/`LevelTooLow`.
- *Mutation.* Track the open state in `AsyncLocal<int>` (or any by-value flow state). Red: the twin above
  (`SystemHighData` where `LevelTooLow` is expected) and the leak skeleton's Scenario A expected reason.

**3. A scope disposed from another flow, or disposed twice, moves or strands the agent's flow.**
- *Path today.* `ReadScope.Dispose` "may be called from any thread" (`ReadScope.cs:26-27`), is idempotent (`:80-81`),
  and 4.4 pins disposal from a flow that never had the chain
  (`EgressSubjectReadScopeTests.A_read_ended_on_a_flow_without_its_chain_still_raises_the_chain_it_was_begun_on`,
  :184-248). If the lane implements the owner rule by pushing a scope marker onto `Active` as a head, then a `Dispose`
  from another flow either (i) cannot pop it (the no-skip rule, `EgressSubject.cs:278-279`) — the agent flow stays "inside
  an open scope" and every later decision on it is SystemHigh: fail closed, availability cost, and the runner's later
  reported reads never lower it; or (ii) pops unconditionally — a flow that did not begin the scope leaves a frame, which
  is the D1-class write-down 4.4 closed (`pr-4.4-body.md` §"Why this rule").
- *Design.* 4.4 frame rule (handoff §3 item 1); DESIGN §2.2 "Monotone nesting" :236-247.
- *Twin.* Begin on flow A inside frame Secret; `await Task.Run(scope.Dispose)`; decide on A: Current must be ≥ Secret
  (Secret or SystemHigh), never Public; `EgressSubjectNestingTests.RestorePathLength()` on A must equal what it was
  before `BeginRead` (no stranded marker) **or** the PR records the stranding as a known limit with the count pinned.
  The existing 4.4 twin at `:184-248` stays green. Also: `scope.Dispose(); scope.Dispose();` leaves the flow where one
  dispose left it.
- *Mutation.* Make the marker's `Dispose` restore `_previous` unconditionally (drop the `ReferenceEquals(Active.Value,
  this)` guard the frame has at `EgressSubject.cs:278`). Red: the cross-flow twin (a head the other flow did not own
  is popped), and 4.4's growth twin if the marker is a `Frame`.

**4. The toolbox throws synchronously, before any task exists, and the scope is begun too late or completed too early.**
- *Path today.* `CapabilityRegistry.InvokeAsync` throws `InvalidOperationException` for an unregistered id **before**
  returning a task (`CapabilityRegistry.cs:42-43`); the agent's `try` at `ToolCallingAgent.cs:234-242` only catches
  `OperationCanceledException`, so the exception reaches the outer catch at `:271-276`, which writes `react.error` with
  `ex.Message` to memory and ends the cycle `stoppedReason = "error"`. If the lane writes
  `var task = tools.InvokeAsync(...); using var read = EgressSubject.BeginRead(); result = await task;` the throw
  happens before the scope exists: nothing observes SystemHigh. If it writes `read.Complete()` in a `finally`, or
  before the `await`, a tool that throws observes its reports instead of SystemHigh (the D14 rule, `ReadScope.cs:16-18`).
- *Design.* §2.2 :225-229 (a thrown tool observes SystemHigh; its message reaches memory); D14.
- *Twin.* (a) Frame Public (`HighWaterMark mark`); call `RunCycleAsync` with a scripted model whose first turn asks for
  an unregistered tool id; after the cycle, `mark.Current == SystemHigh` and `StoppedReason == "error"`; a second
  `RunCycleAsync` in the same frame records its first model call at SystemHigh/`SystemHighData`. (b) A registered tool
  that `Report(Public)`s then throws (`InvalidOperationException`): same assertions (C12 / M15). (c) A tool that throws
  `OperationCanceledException` from the deadline token (`:238-242`): the scope must still observe SystemHigh before
  `stoppedReason = "deadline"` leaves.
- *Mutation.* Move `read.Complete()` into a `finally` (or before the `await`). Red: (b), and `An_awaited_read_that_throws_…`
  if the lane routes the twin through the agent.

**5. Nested or overlapping scopes are tracked as one slot.**
- *Path today.* Nothing nests scopes on master. A composite tool (a plugin `ITool`, or a future `ToolCallingAgent`
  wrapping another agent) can `BeginRead` inside the agent's scope. If the open state is a single reference ("the
  current scope") rather than a count or a chain, disposing the inner reported scope makes the outer unreported scope
  look closed, and an egress between the inner's dispose and the outer's is decided at the frame's mark.
- *Design.* Owner 2026-10-06 ("every egress decided on the flows inside it").
- *Twin.* Frame Public; outer `BeginRead`; inner `BeginRead`; inner `Report(Internal)`, `Complete`, dispose; decide:
  **SystemHigh** (outer still open and unreported); dispose outer unreported: next decision SystemHigh; variant with the
  outer reported `Secret` and inner `Internal`: after both close, `Secret`.
- *Mutation.* Replace the count or chain with a single `AsyncLocal<ReadScope?>` slot. Red: the inner-closed-first
  decision (`Public` where `SystemHigh` is expected).

### A.2 The labelled-tool marker and the report channel

**6. The marker is checked by tool id, so an extra tool named `rag_search` is "labelled".**
- *Path today.* `RepoFsToolboxFactory.CreateWithBuildTest` registers built-ins, then `RegisterExtraTools(tools, extraTools)`
  (`RepoFsToolboxFactory.cs:92-97`, `:176-187`); registration is last-wins by id (`CapabilityRegistry.cs:23`), which the
  factory's own remark relies on ("an extra tool with a clashing id wins", `:36-39`). `extraTools` are
  `IToolSource.GetTools()` snapshots (`SelfExtendRunnerAdapter.cs:210-212`; `src/Ashlar.Abstractions/IToolSource.cs:83-87`),
  today the MCP connection manager (`AshlarMcpClientServiceCollectionExtensions.cs:37`), whose proxies are namespaced
  `mcp:` (`McpToolProxy.cs:8-10`), but any host-registered `IToolSource` can return a tool with `Id == "rag_search"`.
  `ToolCallingAgent` never sees the tool instance (`IToolbox.cs:51-68`), so the only things it can key a marker on
  without a new seam are `call.Id` and the `ToolSchema` from `Schemas()` (`:152`), both attacker-chosen for an extra tool.
- *Design.* §2.2 :222-224: "a tool that **declares itself labelled**… a marker that `ToolCallingAgent` checks before it
  accepts a report. In PR 4 that is `RAGTool` alone." D13.
- *Twin.* A `CapabilityRegistry` holding a real `RAGTool` and then an extra `ITool` with `Id = "rag_search"` and
  `RAGTool`'s schema, whose `InvokeAsync` returns a payload holding `CANARY-<guid>` and, through whatever report channel
  the PR exposes to tools, reports `Public`. Frame Public; `RunCycleAsync` with a scripted model that calls `rag_search`
  then returns `{"tool_calls":[]}`. Expected for the second model call: Current **SystemHigh**, `SystemHighData` — the
  impostor's report is not accepted. Control: the same with the real `RAGTool` over a Secret record → `Secret`,
  `LevelTooLow`.
- *Mutation.* Accept a report when `call.Id == RAGTool.DefaultId` (or when the schema matches). Red: the impostor twin
  (`SystemHighData` expected, `Allowed` observed, and the canary in the stub's second request).

**7. The marker is a public interface or attribute, so any plugin tool is "labelled" by declaration.**
- *Path today.* If 4.5 defines `ILabelledTool` (or `[LabelledTool]`) in `Ashlar.Abstractions` or `Ashlar.BackgroundAgents`,
  every `ITool` reaching the toolbox through `IToolSource` (`SelfExtendRunnerAdapter.cs:212`) or DI (`Program.cs:167-168`
  in the API) can implement it and report `Public` for whatever it returns. The design's "RAGTool alone" is then a
  repository convention, not a property of the code.
- *Design.* §2.2 :222-224; D13; DESIGN §4 records row for 4.5 names a "labelled-tool marker" in `PublicAPI.Unshipped`
  (:878), so the marker is expected to be public API.
- *Twin.* A convention fact in the cert-gate that pins the set of production implementers of the marker to exactly
  `{src/Ashlar.BackgroundAgents/RAG/RAGTool.cs}`, scanning every tree the way
  `EgressSubjectDetachTests.RunDetached_is_called_only_at_the_listed_dispatch_points` does
  (`EgressSubjectDetachTests.cs:791-803`, `:968-1003`, root-wide less `bin`/`obj`/dot dirs/nested checkouts). And a
  records assertion: `docs/EgressInventory.md`'s new producers section says a host's own labelled tool is the host's
  trusted base (as the host's `IEgressGuard` is, DESIGN §2.1 :152-153).
- *Mutation.* Add `: ILabelledTool` to `RepoFsReadTool` (`src/Ashlar.Tools.Dev/RepoFsReadTool.cs:21`) with a `Public`
  report. Red: the implementers fact; and, if the lane also runs C5 through the real toolbox, C5 goes from
  `SystemHighData` to Allowed, which is the write-down the fact exists to prevent.

**8. The scope (or a report callback) is handed to the tool through a channel every tool can see.**
- *Path today.* `ITool.InvokeAsync(ToolCall, WorldSnapshot, CancellationToken)` (`ITool.cs:35`) has no slot for a scope.
  The two in-reach channels are `WorldSnapshot.Data` (every tool gets it; `RAGTool.cs:114` already reads a key from it)
  and `ToolCall.Arguments` (model-authored JSON). `snapshot.Data` is also serialized into the system prompt
  (`ToolCallingAgent.cs:336-339`), so a `ReadScope` object placed there is either serialized (leaking nothing but
  failing on a non-serializable graph) or must be filtered out of `BuildSystemPrompt`.
- *Design.* `ReadScope` remarks: "only code holding it can report, and its holder decides whose reports it accepts"
  (`ReadScope.cs:21-23`); pr-4.4-body "PR 4.5 decides how `RAGTool`'s label reaches `Report`".
- *Twin.* An unlabelled test tool that reads the snapshot (and the arguments) for anything of type `ReadScope` or
  delegate and calls `Report(Public)` on it: next model call must still be `SystemHighData` (C11 generalised: the
  laundering channel is the scope itself, not `Observe`). And `BuildSystemPrompt` must still serialize (no exception,
  `"payload_serialization_failed"` absent) with the real snapshot the runner builds.
- *Mutation.* Put the scope in `snapshot.Data["egress.read"]` and accept `Report` from whoever calls it. Red: the twin.

**9. The labelled tool is wrapped, so the marker is on the inner tool and the wrapper is unlabelled (fail closed), or the
wrapper forwards the marker and launders.**
- *Path today.* `ObservingTool` decorates `dotnet.build`/`dotnet.test`/forge tools (`RepoFsToolboxFactory.cs:99-109`,
  `:122-136`; `ObservingTool.cs:16-49`) and exposes only `Id`, `Schema`, `InvokeAsync`. Nothing wraps `RAGTool` today,
  and `RAGTool` is not registered in any production toolbox (no `new RAGTool(` outside tests: grep over `src`,
  `application`, `commercial`). The leak skeleton registers it directly (DESIGN §5 :913-915).
- *Design.* §2.2 :222-224.
- *Twin.* Wrap the real `RAGTool` in a pass-through decorator that does not carry the marker: a Secret hit must give
  **SystemHigh**/`SystemHighData` at the next model call (unreported), not `Secret`. If the lane ships a decorator that
  forwards the marker (an `ObservingTool`-style wrapper for RAG), pin that it forwards the *inner tool's* reports and
  cannot report on its own.
- *Mutation.* Make the marker check walk `Id` instead of the instance (same as item 6) → the wrapped case reports.

### A.3 RAGTool's labels

**10. Unicode case folding diverges between `DataSensitivityLevels.FromName` and `TrustTierOrder.TryRank`, in the loose
direction.**
- *Path today.* The RAG store normalises a known tier to canonical spelling and keeps an unknown one trimmed as written
  (`TrustTierOrder.NormalizeRecordTier`, `TrustTierOrder.cs:152-160`; `VectorDataRagService.IndexAsync` `:52`), and the
  search filter ranks an unknown stored tier at `MostRestrictive` (`:78-79`, `VectorDataRagService.cs:114`), so such a
  record is served only to a TopSecret clearance. `TryRank` matches with `StringComparer.OrdinalIgnoreCase` (`:58`,
  `:74`). The design's RAGTool mapping goes `Trim` → `registry.GetByName` → `DataSensitivityLevels.FromName`, which
  matches on `name.ToLowerInvariant()` (`DataSensitivityLevels.cs:92-100`; `DataSensitivityRegistry.cs:55`). The two
  fold differently: `OrdinalIgnoreCase` upper-cases per `char`, `ToLowerInvariant` lower-cases per `char`. A stored
  tier `"İnternal"` (U+0130) is unknown to `TryRank` (U+0130 upper-cases to itself, not to `I`) and so was filtered as
  TopSecret-only; if `ToLowerInvariant('İ') == 'i'` **[needs a test]**, `FromName` returns `Internal` and RAGTool
  labels the hit **Internal**: a record the filter treated as top-restricted is read at Internal. The other direction
  (`"ſecret"`, U+017F, upper-cases to `S`) makes `TryRank` match and `FromName` miss, which is RAGTool stricter: safe.
- *Design.* §2.2 "Canonical names only for RAG" :262-276; D15; the parity test "over case, white space and
  `top-secret`" (:275-276) does not mention non-ASCII folds.
- *Twin.* The parity test must be two-sided and must include non-ASCII inputs: for every input in {the five names in
  every ASCII case, with leading/trailing spaces and tabs, `top-secret`, `Top Secret`, `""`, `null`, `"Restricted"`,
  `"İnternal"`, `"ſecret"`, `"Konfidential"` (Kelvin sign), full-width `"Ｓecret"`, `"Secret​"`},
  assert `TrustTierOrder.RecordLabel(input).Dominates(RAGToolMapping(input))` **is false only when the two are equal**,
  i.e. RAGTool's label is equal to or **dominates** the store's label, never below it. Record the expected value for
  U+0130 once the runtime answers.
- *Mutation.* (a) Drop the `All.Contains` reference check (step 3 of the wiring, :266-271) → `"Restricted"` (custom,
  value 1) maps to Internal: red. (b) Drop the `Trim` → `" Secret "` maps to SystemHigh while `RecordLabel` says Secret:
  the equality rows go red (the lane may decide RAGTool-stricter is acceptable there; then the row must say so).
  (c) Fold with `OrdinalIgnoreCase` on the RAGTool side only: the U+0130 row flips direction.

**11. A custom level that shares a primitive's value or display name.**
- *Path today.* `DataSensitivityRegistry.Register` refuses a custom level whose `Value` is a primitive *name*
  (`DataSensitivityRegistry.cs:20-25`) but not one whose `SensitivityValue` equals a primitive's, nor one whose `Value`
  is a primitive's *display* (`"Top Secret"`). `GetByName` returns primitives first, then customs (`:49-61`).
  `ToDataLabel` keys on `SensitivityValue` alone (`DataSensitivityLabelBridge.cs:82-97`, remarks `:18-22`): a custom
  `("Restricted", 1)` maps to a bare Internal label unless the wiring's `All` check stops it. The custom level's flags
  are free (`CustomSensitivityLevel.cs:18-38`) and the bridge does not carry them (owner Q8, C1).
- *Design.* §2.2 :253 ("Anything else, custom registry levels included, is SystemHigh"); wiring :266-271; C2 and M9
  (:964, :988).
- *Twin.* Registry with `("Restricted", 1)`, `("Top Secret", 4)` and `("UltraSecret", 5)` registered; a record at each
  tier, read at a TopSecret clearance (C2's shape, :964): each hit maps to **SystemHigh**; the next model call is
  `SystemHighData`. Control: a record at `"secret"` maps to Secret → `LevelTooLow`.
- *Mutation.* M9 (`:988`): map an unknown tier to Public → C2 red while C5 stays green. Also: replace the reference
  `Contains` with `All.Any(l => l.SensitivityValue == level.SensitivityValue)` → `("Restricted", 1)` row red.

**12. The "read nothing" report on the refusal path carries the store's exception text to the model at Public.**
- *Path today.* `RAGTool.InvokeAsync` catches `ArgumentException` from `_ragService.SearchAsync` and returns a payload
  `{ Refused = true, Reason = ex.Message }` and a log line with the query (`RAGTool.cs:77-100`). The intended source is
  `VectorMath.UnrankableQuery` (`VectorMath.cs:161`), thrown **before** the corpus is touched on the legacy stores
  (`InMemoryVectorStore.cs:72-73`; `SqliteVectorStore.cs:110`). But `IRAGService` is an interface with a host-replaceable
  implementation (`src/Ashlar.BackgroundAgents/RAG/IRAGService.cs`; `MeaiVectorDataRagAdapter` is the default,
  `AshlarKernelRegistrar.Phases.cs:415-419`), and `VectorDataRagService.SearchAsync` enumerates hits into `results`
  (`VectorDataRagService.cs:118-123`): an `ArgumentException` from the collection mid-enumeration drops the hits read
  so far and surfaces its own message. Whether the in-process VectorData collection ever throws `ArgumentException`
  for a zero vector, or at all, is **[needs a test]**; on that path the catch may be dead code. Design D15 says this
  path reports "read nothing", so `ex.Message` reaches the model observation under a Public report.
- *Design.* §2.2 :230-234; D15.
- *Twin.* A stub `IRAGService` whose `SearchAsync` throws `new ArgumentException("CANARY-<guid>")`: the tool result is
  the refusal shape; assert what the PR decides, and make the decision explicit in the PR body: either the refusal
  reports Public only when `ex` is the known refusal (e.g. `ex.ParamName == "embedding"`, the `paramName`
  `UnrankableQuery` takes, `VectorMath.cs:161`) and reports **SystemHigh** otherwise (so the next model call is
  `SystemHighData` and the canary leaves at SystemHigh), or the payload stops carrying `ex.Message`. The recommended
  twin expectation: unknown `ArgumentException` → next call Current SystemHigh, `SystemHighData`.
- *Mutation.* Report Public for every `ArgumentException` → the canary twin goes Allowed.

**13. Zero hits versus a filtered corpus: "read nothing" is right only because the filter runs below the clearance.**
- *Path today.* The MEAI path floors an omitted or unknown clearance (`VectorDataRagService.cs:110-114`;
  `TrustTierOrder.cs:134-144`) and production never sets `maxDataSensitivity` in the snapshot
  (`SelfExtendRunnerAdapter.BuildSnapshot` `:335-342` sets `RepoRoot`, `OutputRoot`, `AgentName`, `agentId`,
  `selfExtendAdmission`, `Objective`, `ObjectiveId`, `ObjectiveMeta`, `RecentNotes`, `RecentObservations`,
  `RepoOverview`; S2 L12 in DESIGN §5 :917-918). So in production `RAGTool` can only ever return Public-tier records
  or nothing, and `EffectiveClearance` returns `null` (`RAGTool.cs:112-119`). The legacy path (`RAGService` over
  `InMemoryVectorStore`, when MEAI is off) resolves the clearance through the registry floor
  (`InMemoryVectorStore.cs:75`, `DataSensitivityFallbacks.cs:54-58`), which can be a custom level **below** Public
  (`:25-37`), so there a record at a custom sub-Public tier is returned and must map to SystemHigh (item 11).
- *Design.* §2.2 :230-234; §5 C2 (:964) was rewritten because of this (CT2).
- *Twin.* (a) 0 hits: `Report(Public)` and the next call at the frame's floor (an Internal frame → `Internal`,
  `LevelTooLow` to a Public destination only; Allowed to an ExternalModel). (b) A record exists above the clearance:
  still 0 hits, still `Public`. (c) Legacy path with a registry floor `("Open", -1)` and a record at `"Open"`: the hit
  maps to SystemHigh (`ToDataLabel` maps a value below 0 to Public, `DataSensitivityLabelBridge.cs:96`, but the
  wiring's `All` check must win first).
- *Mutation.* Report `Public` without the `All` check → (c) red.

### A.4 The runner frame and obligation (d)

**14. The frame is entered inside a helper, an iterator or a lambda, so `RunAsync`'s flow never holds it.**
- *Path today.* `SelfExtendRunnerAdapter.RunAsync(string, string?, string?, string?, string?, string?, CancellationToken)`
  (`SelfExtendRunnerAdapter.cs:136-315`) is the method whose flow must hold the frame: it builds the toolbox
  (`:199-212`), the agent (`:224-230`), the snapshot (`:233-240`), awaits `RunCycleAsync` (`:247-249`), appends the
  scratchpad (`:254-262`), and awaits the admission bridge (`:290-294`), whose `TryAutoShare` makes the EG-MESH-01
  decision (`SelfExtendAdmissionBridge.cs:288`). The design wants one `using` around `:247-294`. A frame entered in a
  local `async` function, in `BuildSnapshot`, or in a lambda passed to a helper never reaches `RunAsync`'s flow
  (`EgressSubject.cs:20-22`, `:83-85`), so the admission decision is `no-subject`. At SystemHigh the *decision* is the
  same; the *record* is not.
- *Design.* §2.2 :191-196 ("The frame wraps the cycle and the post-cycle admission and auto-share (`:290-294`)…
  Records go from `no-subject` to `subject:agent:<id>`"); obligation (d) (handoff §3 item 1; SPEC-007 :60-73).
- *Twin.* Run the real `SelfExtendRunnerAdapter.RunAsync(repoRoot, objective, agentName, null, null, agentId: "att-14-<guid>")`
  in a temp repo that has `ashlar.policy.yaml` (so `TryRecordAsync` runs, `SelfExtendAdmissionBridge.cs:57-61`), with a
  scripted `IModel` that proposes one `forge.propose_change` then returns `{"tool_calls":[]}`, a `SigningIdentity`
  (`OperatorKey.TryLoad`, `:138-149` — inject or set `ASHLAR_HOME` in `EnvironmentVariables`), `ASHLAR_MESH_AUTOSHARE=1`
  and a temp mesh dir (`autoShare`/`meshDir` are parameters of `TryRecordAsync` but `RunAsync` does not pass them,
  `:290-294`, so the environment is the only lever: run in `[Collection("EnvironmentVariables")]`), and an
  `EgressDecisionLog` sink filtered by `CurrentBasis == "subject:agent:att-14-<guid>"`. Expected records: the model
  calls (if the scripted `IModel` evaluates a `model.meai` request itself, or if the test wires `MeaiBackedModel` over a
  stub `IChatClient` with `ProviderUri = http://remote-<guid>.example:11434`): ExternalModel/Internal, **Current
  SystemHigh**, CurrentBasis `subject:agent:att-14-<guid>`, `SystemHighData`, `report`, `profile:…`; the EG-MESH-01
  record: family `mesh.publish`, site `EG-MESH-01`, destination `file:<meshDir>`, NetworkExport/Internal, Current
  SystemHigh, CurrentBasis `subject:agent:att-14-<guid>`, `SystemHighData`. **Both** must carry the subject basis.
- *Mutation.* Move the `Enter` into a local `async Task RunCycleAsync()` helper awaited at `:247`. Red: the EG-MESH-01
  record's basis is `no-subject`.

**15. The floor is lowered quietly: `new HighWaterMark()` is Public.**
- *Path today.* `HighWaterMark()` starts at Public (`HighWaterMark.cs:19-23`). The design's floor-pin convention
  (§2.2 :213; D19) is textual; a floor held in a field (`private static readonly SecurityLabel Floor = SecurityLabel.Public;`)
  named `SystemHighFloor`, or a `new HighWaterMark()` call, passes a scanner that looks for `Enter(`.
- *Design.* §2.2 "Floor" :207-213; D19; Q2 ("self-extend runs at SystemHigh").
- *Twin.* Item 14's runtime twin asserts `Current == SystemHigh` on every record with the subject basis; the convention
  fact asserts the literal `SecurityLabel.SystemHigh` in the `HighWaterMark(...)` argument at the pinned site and
  that production holds no `new HighWaterMark()` with no argument within the enclosing method of an `Enter`.
- *Mutation.* `new HighWaterMark(SecurityLabel.SystemHigh)` → `new HighWaterMark()`. Red: the convention fact **and**
  item 14's twin (Current Public, Access Allowed for the model call). If only the fact goes red, the runtime pin is
  missing and the fix can be faked by an indirection.

**16. The floor-pin convention scans for a spelling, not a call.**
- *Path today.* The 4.4 convention facts are textual (`EgressSubjectDetachTests.cs:968-1003`;
  `ProcessGlobalEnvironmentConventionTests.IsPruned`, `:479-492`), and 4.6's fifth fact is textual too
  (`pr-4.6-body.md` "The new convention fact is textual"). `EgressSubject.Enter` is public (D37, `EgressSubject.cs:88`).
  Spellings a `EgressSubject.Enter(` scan misses: `using static Ashlar.Abstractions.Security.Egress.EgressSubject;` then
  `Enter(...)`; `using ES = …EgressSubject;` then `ES.Enter(...)`; a delegate `Func<string, HighWaterMark, IDisposable> f
  = EgressSubject.Enter;`; `typeof(EgressSubject).GetMethod("Enter")`; a repo wrapper
  `static IDisposable EnterAgent(string id) => EgressSubject.Enter(id, new HighWaterMark())` whose callers are then
  unpinned.
- *Design.* §2.2 :213 ("pins every production `Enter` call site, with its floor and its enclosing method"); D19.
- *Twin.* The fact must pin the set of production files that *reference* `EgressSubject` at all (a `using` alias,
  `using static`, `nameof(EgressSubject)`, the identifier) and, in each, every `Enter` call with its floor expression and
  enclosing method; the scan root is the repository (as `RunDetached_is_called_only_at_the_listed_dispatch_points`,
  widened in 4.4 after its "every call site" claim was false, `pr-4.4-body.md` §"Second final re-check" item 4).
- *Mutation.* Plant each of the five spellings above, one at a time, in `src/Ashlar.BackgroundAgents.HostRunners/PlannerScratchpad.cs`.
  Each must turn the fact red. A fact that stays green for the alias or the wrapper is a fact that pins nothing.

**17. The `using` spans a `yield return`, or the producer streams.**
- *Path today.* No production consumer of the agent's model is an async iterator: `MeaiBackedModel.CompleteAsync` calls
  `GetResponseAsync` (`MeaiBackedModel.cs:61`); `HotSwappableModel`, `ProviderBackedModel` and the
  `OrchestrationRuntimeModelDecorator` are plain `Task`s (`HotSwappableModel.cs:43-109`;
  `OrchestrationRuntimeModelDecorator.cs:28-38`). The streaming clients are iterators (`RoutingChatClient.cs:52-65`,
  `AuditingChatClient.cs:65`, `SanitizingChatClient.cs:45`) and the only production streaming consumer is the IDE SSE
  endpoint (`IdeEndpoints.cs:422`), path B10, not instrumented. So the yield hazard is a convention item for 4.5, not a
  live path.
- *Design.* Obligation (d) (SPEC-007 :66-73; `EgressSubject.cs:22-27`).
- *Twin.* The convention fact asserts the enclosing member of each pinned `Enter` contains no `yield return` and is
  not a lambda or local function. Plant `yield return` in a method holding an `Enter` → red.
- *Mutation.* As the twin says; no production mutation exists until a streaming runner lands.

**18. Work created before the frame and awaited inside it reads for the agent.**
- *Path today.* Inside `RunAsync`, before `:247`: the toolbox (`:199-212`), including `_toolSources?.SelectMany(s => s.GetTools())`,
  enumerated synchronously by `RegisterExtraTools` (`RepoFsToolboxFactory.cs:183-186`); the agent (`:224`); the
  snapshot (`:233-240`), which reads the scratchpad tail and the observation store (`:365-367`, `:439-457`,
  `PlannerScratchpad.LoadTail`, `JsonlObservationStore`): disk reads under the SystemHigh floor. MCP proxies hold a
  connection whose transport was created at connect time (`McpClientConnectionManager.InvokeProxiedToolAsync`,
  `:152-184`): whether the MCP SDK sends on the caller's flow or hands the request to a pump task created at connect
  (before any frame) is **[needs a test]**; if the latter, the HTTP decision for a proxied tool call is `no-subject`,
  not `subject:agent:<id>`. The `ProviderFactory` warm-up is a `Task.Run` created at construction
  (`ProviderFactory.cs:131`), before any frame; it reads only the daemon's tag list.
- *Design.* Obligation (d): "a runner creates and starts the work it reads through inside its `using` block"
  (handoff §3 item 1; SPEC-007 :64-66); §2.2 :194-196.
- *Twin.* Item 14's twin, extended with an `IToolSource` returning an `McpToolProxy`-shaped tool over a stub transport
  whose send evaluates a `model.*` or `http.factory` request: assert the record's `CurrentBasis`; if it is `no-subject`,
  the PR body and `docs/EgressInventory.md` must list "MCP proxy sends are decided no-subject" as a known limit, and
  the design's "records go from `no-subject` to `subject:agent:<id>`" (:196) must be narrowed.
- *Mutation.* None exists in Ashlar code (the capture point is the runtime's); the deliverable is the record.

**19. The post-cycle steps read tool-derived data outside any scope.**
- *Path today.* `cycle.MergedDelta.Log` (tool log lines: `"read:<path> bytes=…"`, `"RAG search: query='…', results=N"`,
  `RAGTool.cs:102`; `RepoFsReadTool.cs:62`) → `ExtractWritePaths` (`:521-541`) → `PlannerScratchpad.Append`
  (`:254-262`, disk) and `TryRecordAsync` (`:290-294`) → signed gate record (`SelfExtendAdmissionBridge.cs:151-152`),
  forge apply (`:174-207`) and auto-share (`:238-301`), which packages forge rows (`:263-276`) and publishes them to
  the mesh store after one EG-MESH-01 decision (`:288-290`). The forge rows hold model-authored content produced
  after the model read tool results. None of this runs under a read scope; under a floor below SystemHigh it is a
  write of derived data at the frame's mark, which is correct only because every read was observed before the model
  call that produced the content.
- *Design.* The producer rule (§2.2 :203-205): post-processing "runs outside such a frame, or inside a read scope".
  At SystemHigh (4.5) it only attributes.
- *Twin.* Item 14's twin: the EG-MESH-01 record is inside the frame and at SystemHigh. For the design claim about
  sub-SystemHigh runners, a leak-skeleton variant: a Public frame, a Secret hit, then a test "post-processing" step that
  evaluates `mesh.publish` to `file:<dir>` on the same flow after the cycle: expected Current **Secret**, `LevelTooLow`
  (NetworkExport is Internal). It must never be Public.
- *Mutation.* Dispose the frame before `TryRecordAsync` (narrow the `using` to the cycle). Red: the EG-MESH-01
  record's basis flips to `no-subject`.

**20. The subject id in the record is bounded, so two agents collide, and a tool can rename the subject.**
- *Path today.* `Enter` records `subject:` + `EgressDestinations.Bound(subjectId)` (`EgressSubject.cs:95`); `Bound`
  truncates at `MaxTextLength = 256` (`EgressDestinations.cs:49`) and replaces control/format/separator/surrogate
  characters with `?` (`EgressDestinations.cs:168-186`). `agentId` comes from configuration through `BackgroundAgentRegistry`
  (`BackgroundAgentRegistry.cs:613-615`; `SelfExtendRunnerAdapter.cs:150-151`). Separately, `Enter` is public (D37), so
  a *synchronous* tool (an `ITool` whose `InvokeAsync` is not `async` and returns a completed task) runs on the agent's
  flow and can `Enter("agent:victim", new HighWaterMark())` without disposing: the frame becomes the agent flow's head,
  later records name `subject:agent:victim`, and the chain grows (known limit (a)). Monotone nesting keeps the label
  (`Resolve` joins the chain, `EgressSubject.cs:246-248`), so this is an attribution attack, not a write-down.
- *Design.* §2.2 :196 (records say `subject:agent:<id>`); D37.
- *Twin.* (a) Two ids equal up to `MaxTextLength` and different after it: both records carry the same basis; the PR
  body or the producers section says so. (b) A synchronous tool that enters `agent:victim` and returns: the next
  model call's basis is `subject:agent:victim` **and** its Current is still ≥ the agent's mark (SystemHigh after the
  unreported scope). Record it as a known limit of attribution.
- *Mutation.* None; records.

### A.5 Peer and agent-backed chat responses

**21. The peer observation runs after the response and a streaming consumer stops early.**
- *Path today.* `EgressGuardChatClient` is deliberately non-`async` (`EgressGuardChatClient.cs:14-15`); both overrides
  call `Decide(options)` then delegate (`:52-69`). It does not know the target key: the constructor takes
  `(innerClient, EgressRequest? request, IEgressGuard? guard)` (`:37-43`), and the key is consumed only by
  `MeaiEgressDestination.Resolve` at the builder (`AshlarGovernanceChatClientBuilderExtensions.cs:29-30`). A `peer:`
  key is admitted by the default policy (`DefaultChatTargetAccessPolicy.cs:25-26`) and registered only through
  `AddAshlarGovernedChatClient` (`MeaiPipelineServiceCollectionExtensions.cs:165-173`); nothing in production registers
  one (grep `peer:` over `src`, `application`). If 4.5 observes SystemHigh *after* `await base.GetResponseAsync(...)`,
  the streaming override must observe at the end of enumeration; a consumer that reads one `ChatResponseUpdate` and
  breaks never reaches it, and the chunk it holds is unobserved.
- *Design.* §2.2 table row "Responses from `peer:`…" :259 ("`EgressGuardChatClient` observes it after the response");
  D16.
- *Twin.* `AddAshlarGovernedChatClient("peer:att", sp => scriptedStreamingClient)`; frame Public; `await foreach` one
  update, `break`; dispose the enumerator; decide `model.meai` to `https://remote.example`: Current **SystemHigh**,
  `SystemHighData`. Variant: the inner throws after yielding one update: SystemHigh. Variant: the non-streaming call:
  SystemHigh after `GetResponseAsync` returns. Variant: `GetResponseAsync` throws (PolicyGate denial,
  `PolicyGateChatClient.cs:30-75`): nothing was received; either outcome is defensible, the PR body must say which.
  Recommended implementation for the lane: observe **before** delegating (it only raises, so observing before the send
  is safe and closes the early-break case by construction); then the twin's `Decide` for the peer call itself also
  records SystemHigh.
- *Mutation.* Drop the `Observe` → every variant red. Observe in a continuation queued with
  `ExecutionContext.SuppressFlow` or `ThreadPool.UnsafeQueueUserWorkItem` → the frame is not reached → red.

**22. Making the streaming override an async iterator makes `Decide` lazy.**
- *Path today.* `GetStreamingResponseAsync` returns `base.GetStreamingResponseAsync(...)` directly (`:62-69`), so
  `Decide` runs when the call is made, on the caller's flow, in the caller's frame (the remark at `:14-15`). An
  `async IAsyncEnumerable` override moves `Decide` to the first `MoveNextAsync`, on the consumer's flow.
- *Design.* `EgressGuardChatClient.cs:14-15` (kept by 4.1 and 4.6); §2.3 route table :331 ("before enumeration from
  streaming, which is PolicyGate's shape").
- *Twin.* Frame A (Secret): obtain the streaming enumerable for a remote target and do not enumerate; dispose A;
  frame B (Public): enumerate. The recorded decision must be at **Secret** (`LevelTooLow`), made when the call was
  made, not at Public. Also: a call never enumerated still records one decision.
- *Mutation.* Turn the override into an async iterator that decides before its first `yield` → red (decision at
  Public; or no decision when not enumerated).

**23. "Agent-backed" cannot be recognised; a `local:` key over an agent is unobserved.**
- *Path today.* `AddAshlarMeaiPipeline` takes `ollamaInnerFactory`/`onnxInnerFactory` (`MeaiPipelineServiceCollectionExtensions.cs:100-113`)
  and `AddAshlarGovernedChatClient` takes any factory under any key (`:165-173`); the policy allows every `local:*`
  (`DefaultChatTargetAccessPolicy.cs:25-30`). A host can register `local:ollama` over a client that is another agent.
  Nothing in `IChatClient`/`ChatClientMetadata` says "agent-backed".
- *Design.* §2.2 :259 and D16 say "`peer:` and other agent-backed chat targets". Only the prefix is decidable.
- *Twin.* Records only: the producers section and the `EgressGuardChatClient` remarks say the observation is keyed on
  the `peer:` prefix and that a host's own inner client under any other key is the host's trusted base.
- *Mutation.* None; see §C.3.

### A.6 Paths the design leaves uninstrumented, where a host-declared frame writes down

**24. A frame around `AgentHost.StepAsync` or `ToolCallingAgent.ThinkAsync`.**
- *Path today.* `AgentHost.StepAsync` calls `agent.ThinkAsync` (`AgentHost.cs:42`) then invokes tools itself
  (`:66-70`) with no scope; `ToolCallingAgent.ThinkAsync` makes one model call (`ToolCallingAgent.cs:110`) and writes
  `think.error` with `ex.Message` (`:125`). The next `StepAsync` sends the (unobserved) state again. A host that enters
  `Enter("agent:x", new HighWaterMark(Public))` around the loop writes down at the second step.
- *Design.* §2.2 :199-201 (not instrumented: B4 `AgentHost`, B5 orchestrator, B6-B12); the producer rule :203-205.
- *Twin.* A documentation twin plus a behaviour pin: with `Enter(Public)` around two `StepAsync` calls whose first
  tool returns `CANARY`, the second step's `model.meai` decision is at Public (Allowed). The test's name says it pins a
  **known limit**, and the producers section in `docs/EgressInventory.md` names `AgentHost`, the orchestrator
  (`BaseAgent.cs:267`), workflows, bricks, the autonomy loop and the API/CLI paths as unobserved, so a frame below
  SystemHigh around them is a write-down.
- *Mutation.* None; the twin is the record of what is not fixed.

**25. The MCP server path returns a tool's exception message to a remote client with no decision.**
- *Path today.* `ToolboxMcpToolContributor.InvokeAsync` returns `ex.Message` as an `isError` result (`:104-111`) and
  the payload JSON (`:93-98`) to the remote caller; no egress decision is made on the response (Q6: inbound responses
  unmediated until PR 5). Not a 4.5 path; listed because DESIGN §2.2 :225-227 cites it as the reason for D14, and a
  verifier might expect 4.5 to touch it. It must not: a scope there would observe into no frame.
- *Design.* §2.7 :619-626; Q6.
- *Twin.* None for 4.5; the producers section says the MCP server is unobserved.

---

## B. Design claims marked [needs a test] for this PR

1. **`ToLowerInvariant('İ') == 'i'`** on the runtime(s) the cert-gate runs (glibc ICU in the devtest container).
   Decides item 10's direction. Also `'K'` (Kelvin) → `'k'`, and full-width letters (expected: no fold).
2. **Where the MCP SDK (`ModelContextProtocol`) sends.** On the caller's flow (then the proxied tool's HTTP decision is
   `subject:agent:<id>`) or on a pump task created at connect (then `no-subject`). Item 18; decides the wording of
   DESIGN §2.2 :196.
3. **Whether the in-process VectorData collection ever throws `ArgumentException`** from `SearchAsync`, and whether
   `VectorDataRagService.EmbedAsync` can for an empty or punctuation-only query (`VectorDataRagService.cs:109`,
   `:185-190`; the `TokenHashEmbeddingGenerator`). If neither does, `RAGTool`'s refusal branch (`RAGTool.cs:77-100`) is
   unreachable on the default path, and D15's "its refusal" case can be exercised only on the legacy stores or a stub
   (item 12).
4. **That `ReadScope`'s open state flows through `ConfigureAwait(false)` and `Task.Run` created inside the scope and
   not into a `Thread` started without the context** — the same capture table 4.4 pinned for frames
   (`EgressSubject.cs:55-62`), now for the scope marker, whatever its shape (items 1-3).
5. **"At SystemHigh this only attributes them" / "No production outcome changes"** (DESIGN §2.2 :194-196): needs the
   records-diff twin of item 14 (every record with `subject:` basis has `Current == SystemHigh` and an `Access` equal
   to the same request decided with no frame).
6. **That production `RAGTool` searches are floored to Public-tier records** (item 13) and therefore that no production
   `RAGTool` report above Public exists in PR 4. If a production toolbox ever registers `RAGTool` (none does today),
   this changes.
7. **That `EgressGuardChatClient.Decide` stays eager** after the peer change (item 22), on both overrides.
8. **Whether a 256-character subject id is realistic from configuration** (`EgressDestinations.MaxTextLength = 256`,
   `EgressDestinations.cs:49`) for item 20; the cap itself is read, the collision needs no test, only a record.
9. **That `OperatorKey.TryLoad` can be pointed at a temp identity in a test** without touching the operator's real key
   (`SelfExtendAdmissionBridge.cs:138-149`), which item 14's auto-share twin needs; otherwise the twin passes `signer`
   by calling `TryRecordAsync` directly, inside a frame the test enters, and loses the end-to-end attribution check of
   `RunAsync`'s own `using`.
10. **`BackgroundServiceExceptionBehavior` and the daemon's call shape** (`BackgroundAgentRegistry.cs:613-615` awaits
    `RunAsync` directly): a frame the runner leaves undisposed on an exception path would be the registry's flow's head.
    `using var` inside the `try` at `:178` disposes before the catch at `:305`; the twin for item 14 should include a
    throwing `IModel` and assert `RestorePathLength() == 0` on the calling flow afterwards.

---

## C. Where the design is wrong or impossible against master

1. **"The declaration is a marker that `ToolCallingAgent` checks before it accepts a report" (DESIGN §2.2 :222-224) has
   no seam on master.** `ToolCallingAgent.RunCycleAsync` takes `IToolbox tools` (`ToolCallingAgent.cs:141-147`) and
   invokes through it (`:236`); `IToolbox` exposes schemas, invocation and memory only (`IToolbox.cs:51-68`). The tool
   instance is private to `CapabilityRegistry` (`CapabilityRegistry.cs:19`, `:40-45`). Adding a member to `IToolbox`
   changes an `Ashlar.Abstractions` public interface (PublicAPI-tracked) and breaks every implementer:
   `AgentManagementToolbox` (`src/Ashlar.BackgroundAgents/Tools/AgentManagementToolbox.cs:13`, `:42-46`) and six test
   fakes (`ToolCallingAgentReActTests.cs`, `ToolCallingAgentThinkAsyncTests.cs`, `FailClosedTests.cs`,
   `FaultInjectionTests.cs`, `ConcurrencyTests.cs`, `AdversarialScopeEscapeTests.cs`); a default interface member is
   unavailable on the `netstandard2.0` asset (SPEC-007 §4.1: Abstractions targets `netstandard2.0;net8.0;net10.0`).
   The lane must choose and record: (a) a type test `tools is CapabilityRegistry` plus a new `TryGetTool(id)` on the
   concrete class (Runtime is referenced by BackgroundAgents, `Ashlar.BackgroundAgents.csproj:25`); (b) a second,
   optional interface on the toolbox (`ILabelledToolbox`) that `CapabilityRegistry` implements; or (c) a scope passed
   through `InvokeAsync` by a new interface the tool implements, with the toolbox unwrapping it. Items 6-9 test
   whichever is chosen. The design's "RAGTool alone" is then enforceable only by a convention fact (item 7).

2. **"`ReadScope.Report(label)` from a tool" (DESIGN §2.2 :223) needs a hand-off the `ITool` contract does not have.**
   `ITool.InvokeAsync(ToolCall, WorldSnapshot, CancellationToken)` (`ITool.cs:35`); the scope is not ambient by 4.4's
   decision (`ReadScope.cs:20-23`; `pr-4.4-body.md` "The scope is held, not ambient"). The only channels without a new
   seam are `WorldSnapshot.Data` and the arguments (item 8), both wrong. `RAGTool` is in `Ashlar.BackgroundAgents`,
   which references Abstractions and Runtime (`Ashlar.BackgroundAgents.csproj:21-25`), so a new interface in either
   project is reachable.

3. **"`EgressGuardChatClient` observes it after the response" for "`peer:` and other agent-backed chat targets" (DESIGN
   §2.2 :259; D16) is half-implementable.** The client does not receive the target key (`EgressGuardChatClient.cs:37-43`;
   key used only at `AshlarGovernanceChatClientBuilderExtensions.cs:30`), so 4.5 must thread it or a flag; both overrides
   are non-`async` by design (`:14-15`), so "after the response" means an `async` override for `GetResponseAsync` and an
   iterator or a wrapping enumerable for streaming (item 21), or observing before the send instead. "Other agent-backed
   targets" have no identifying property on master (item 23); the design must narrow to the `peer:` prefix and state
   the rest as the host's trusted base. `AddAshlarGovernedChatClient` is the only production way a `peer:` client exists
   (`MeaiPipelineServiceCollectionExtensions.cs:165-173`) and no production composition calls it with `peer:`.

4. **The owner's 2026-10-06 rule contradicts three records that 4.4 just merged, which 4.5 must flip by name:**
   `ReadScope.cs:24-25` ("A scope observes only when it is disposed"); `EgressSubject.cs:17-19` ("Unless the read
   completes and reports… the scope observes SystemHigh **when it ends**"); cert-gate row 66 ("A read counts at what it
   reported only when it returned and reported"); and the twin
   `EgressSubjectReadScopeTests.A_completed_read_with_no_report_observes_SystemHigh` at line 50, which asserts `Public`
   inside the open scope with the message "the read has not ended yet". The rule also needs `Resolve` to see scopes,
   which `BeginRead() => new(Active.Value)` (`EgressSubject.cs:120`) and `Frame.Resolve` (`:238-251`) do not. Whatever
   the mechanism, the 4.4 frame-rule invariants must hold for it (items 3, 5), and the Python model checkers the
   handoff names (`frame-model-noskip.py`, `frame-model-wd-callback.py`; handoff §2) should gain a scope-open
   operation and be shown to catch a broken variant before the lane trusts them.

5. **Scenario B and control C5 now share a reason.** With the rule, B (the tool's own egress inside its open scope)
   and C5 (the next model call after an unlabelled tool) are both `SystemHighData` (DESIGN §5 :937-946, :967). They
   are still distinguishable: B's refused record is the **tool's** site (`EG-WEB-01`, family `web-search`, WebSearch /
   Confidential) and C5's is the **model's** (`EG-MDL-01`/`meai:…`, ExternalModel / Internal); the verifier must assert
   the site and family, not the reason alone, and C0's "allowed until the label was read" (`:962`) must stay at the
   first model call, before any scope exists.

6. **"Records go from `no-subject` to `subject:agent:<id>`, at the same SystemHigh" (DESIGN §2.2 :196) is not
   guaranteed for every record in the cycle.** A send made by work captured before the frame (an MCP transport pump
   created at connect, item 18; the `ProviderFactory` warm-up, `ProviderFactory.cs:131`) stays `no-subject`. The
   sentence should read "every decision made on the runner's flow and on work it creates and starts inside the frame".

7. **"`RAGTool` reports canonical tiers" is exercised only by the leak skeleton.** `RAGTool` is in no production toolbox
   (no `new RAGTool(` outside tests), and production snapshots carry no clearance (`SelfExtendRunnerAdapter.cs:335-342`),
   so the MEAI filter floors every production search to Public-tier records (`VectorDataRagService.cs:110-114`). The
   producers section should say that in PR 4 the only reads above Public a production agent can make are unreported
   ones (SystemHigh), and that this is why the production floor is SystemHigh.

8. **D15's "read nothing" on the refusal path lets a store's `ArgumentException` text reach the model at Public**
   (`RAGTool.cs:96-99`; item 12). The design assumed the refusal text is `VectorMath.UnrankableQuery`'s fixed message;
   `IRAGService` is replaceable and the MEAI path's exception sources are different (item B.3). Either narrow the
   `Public` report to the known refusal or drop `ex.Message` from the payload.

9. **The design's threading section still names `Detach()` (DESIGN §2.2 :291-294; D18).** Phase B replaced it with the
   callback-shaped internal `RunDetached(Action)` (`EgressSubject.cs:158-183`; handoff §2, §3 item 1). 4.5 records that
   cite "Detach" are stale on arrival.

10. **The design's line citations into `EgressSubject.cs` predate 4.4** (`:47-53` for `Resolve`, `:11-12`, `:23`, `:42`
    in §2.2 :286 and §1 :50, :74). On master `Resolve` is `:238-251`, the capture remarks are `:20-31` and `:55-62`.
    Harmless, but a verifier copying them into the PR body would be citing the wrong lines.

11. **Design row 4.5 puts the "labelled-tool marker" in `PublicAPI.Unshipped`** (§4 records table :878). That is right
    only if the marker lives in `Ashlar.Abstractions` (`netstandard2.0;net8.0;net10.0`, `Ashlar.Abstractions.csproj:9`;
    the only project of the three with `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`). `Ashlar.BackgroundAgents`
    and `Ashlar.Runtime` have no `PublicAPI.*.txt`, so a marker placed there is tracked by no record and must be named
    in the PR body and in the floor-pin/implementers convention fact instead.
