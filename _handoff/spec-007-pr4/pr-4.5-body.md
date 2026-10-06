## Resume state (written on resume after the container restart, before any further change)

The previous lane agent was killed mid-flight. Its clone, five WIP commits, logs and two mutant snippets survived. Reconstructed from `git log`, `git status`, `git diff`, `logs-4.5/` and `mut-4.5/`:

**Done (committed on `claude/spec-007-pr4-4.5-producers`, base master `de41a8ac`, every commit with the Fable trailers):**
- `4ef20937` twins alone. Red at base: **45 failed of 60** under `FullyQualifiedName~EgressSubjectReadScopeTests|~EgressProducerTwinTests|~EgressSubjectProducerConventionTests`, net8.0 (`logs-4.5/red-twins-net8.log`; the 45 names are listed under "Testing").
- `04ea9cfb` the change (open read scope at SystemHigh; `ToolCallingAgent`, `RAGTool`, peer responses, self-extend). Green: the egress filter **499 of 499** on net8.0; `Ashlar.Abstractions` 0 warnings on netstandard2.0, net8.0, net10.0 (`green1-net8.log`).
- `36f2a132` records (CHANGELOG, SPEC-007 status line and decisions-log row, `docs/EgressInventory.md` producers section, row 68 and the count paragraph, knowledge graph). Floors re-measured **2,136 files, 150 occurrences, 67 docs rows**; `Ashlar.Tests.BackgroundAgents` 649 passed, 1 skipped; `Ashlar.Tests.AI.Pipeline` 107 of 107 (`floors-bga-aip-net8.log`).
- `f1ddae98` marker twins (custom level x3, a labelled tool that reports then throws, `RAGTool` behind a decorator or another toolbox), the floors comment, remarks. The egress filter **504 of 504** on net8.0 and on net10.0; Abstractions 0 warnings x3 (`filter-both-f1ddae98.log`); repo gates **26 of 26** (`repo-gates-f1ddae98.log`).
- `36ffae61` the convention test's syntax-fixture fact. **Not verified:** the full cert-gate at this head was killed by the restart (`cert-gate-36ffae61.log` ends inside the build with no summary), so this fact was never seen green.

**Half-done:** `ci/cert-gate-assertions.md` row 68 reworded to name `f1ddae98`'s and `36ffae61`'s facts, uncommitted. Inspected, kept, committed on resume. Mutation checks: two mutant snippets prepared (`mut-4.5/m13`: the streamed peer observe before `yield return` removed; `m17`: `read.Complete()` moved before the `try`), no `mutation-check.sh` run, no summary line.

**Never started:** a full cert-gate at a verified head; every mutation run; the model check (`model-4.5/` absent at resume); the adversarial inputs (`attack-4.5.md`, `records-4.5.md` absent at resume); this body's Testing, Mutations, Not observed, Model, Deviations and Records sections; the squash; the final push.

**On resume:** `36ffae61` pushed as-is first (the branch was not on the remote), then the assertions commit, the squash to one commit on master and the push.

## Summary

SPEC-007 PR 4.5, **the subject producers, report-only**. Production code now declares and feeds `EgressSubject` frames in three places, and one change to PR 4.4's read scopes carries the owner's decision of 2026-10-06. It implements row 4.5 of the PR 4 plan: design §2.2 (producer boundary, floor, Observe/report/read scopes, what counts as a read, session), defaults D13–D17 and D19, and the owner's answers to Q2 and Q8. The design and its decision ids live in `_handoff/spec-007-pr4/DESIGN-4-final.md` on the `claude/spec-007-pr4-workspace` branch; the permanent records name the rules, not the ids.

- **A read that has not ended counts as `SystemHigh`** (the owner's decision of 2026-10-06, recorded in SPEC-007's decisions log). *While a read scope is open and unreported, any egress decided on the flows inside it is decided at `SystemHigh`, for every tool, `RAGTool` included (no labelled-tool exemption).* `EgressSubject.BeginRead()` now enters a **read's frame** on the flow. Its mark is `SystemHigh` and it never names a decision, so every decision on the flows inside the read is made at `SystemHigh`, with the basis of the innermost live subject frame. `ReadScope.Dispose` first observes what the read reported, or `SystemHigh`, into the frames the read was begun in, as 4.4 already did, and only then disposes the read's frame, which takes the flow that began it back to those frames.
- **The runner declares the frame** (Q2, D19). `SelfExtendRunnerAdapter.RunAsync` enters `agent:<id>` at `new HighWaterMark(SecurityLabel.SystemHigh)` around the objective claim, the toolbox, the snapshot, the cycle, and the admission and auto-share after it. Self-extend's model calls, tool calls and admission now record `subject:agent:<id>` instead of `no-subject`, at the same `SystemHigh`.
- **Every tool call is a read** (D13, D14). `ToolCallingAgent.RunCycleAsync` wraps each invocation as `using (var read = EgressSubject.BeginRead()) { …; read.Complete(); }`. It hands a report-only `ReadReporter` (never the scope, which alone completes the read) to a tool that declares itself labelled: the new public marker `ILabelledTool` (in `Ashlar.Abstractions.Security.Egress`, recorded in `PublicAPI.Unshipped.txt`), served directly by a `CapabilityRegistry`. Any other result, and any call that throws, a labelled one's included, counts as `SystemHigh`.
- **`RAGTool` labels its hits** (D15, Q8). `RAGTool` implements `ILabelledTool`. It reports each hit's tier: trim the name, require a spelling the RAG pipeline's `TrustTierOrder` also ranks (the five canonical names and `top-secret`, compared ordinally ignoring case), `registry.GetByName`, require one of `DataSensitivityLevels.All`, then `ToDataLabel`. Anything else is `SystemHigh`, a custom level included. No hits, and the stores' own unrankable-query refusal, report "read nothing" (`Public`); any other `ArgumentException` a store throws stays unreported. A parity theory pins the mapping to `TrustTierOrder.RecordLabel` over 28 names, and a second theory pins that over six non-ASCII look-alikes `RAGTool` never labels below the pipeline.
- **Peer responses are reads** (D16). The outermost governance layer, `EgressGuardChatClient`, observes `SystemHigh` into the caller's frames when a call to an agent-backed target ends, however it ends (a faulted task or stream, a synchronous throw under the layer such as PolicyGate's deny). A streamed response observes it before each update reaches the caller, and again at the end. Agent-backed means a `peer:` target, or any governed target whose key is neither `local:` nor `cloud:`. A model's response is not a read.
- **The floor pin** (D19). `EgressSubjectProducerConventionTests` parses every production C# file with Roslyn. It pins each `EgressSubject.Enter` and `BeginRead` with its file, enclosing method and floor, requires every production `Enter` floor to be `SystemHigh`, requires each entry to be the resource of a `using` in a method that is not an iterator, finds no `using static` and no `using` alias of `EgressSubject`, and pins the production types that declare themselves labelled to exactly `RAGTool`. Its row is new in `ci/cert-gate-assertions.md` (row 68 on this branch; row 69 once 4.10's row lands first).

**Done-when, met in report mode:** the leak skeleton passes. A test runner frame is entered at `Public`, and the agent runs a real `RAGTool` over `MeaiVectorDataRagAdapter` and `VectorDataRagService`, with the model behind the governed `local:ollama` client (ExternalModel, `Internal`, EG-MDL-01). After a `Secret` hit, the next model call records `subject:agent:leak-<guid>`, `Current = Secret`, `LevelTooLow`, `Mode = report` and `Refused = false`, and the call still goes. After an `Internal` hit it would be allowed. Production self-extend records `subject:agent:<id>` at `SystemHigh`.

These are classification-style controls inside the runtime. They are not an accredited cross-domain solution.

No non-test code under `application/` changes, so this is not `[coordinated-integration]`.

### The open-read rule, and how it keeps 4.4's invariant

4.4's one-line invariant: *a flow leaves a frame only by disposing its own head while that head is undisposed, and goes back to exactly the frame it was entered under, disposed or not; every frame on a chain counts at its live mark down to a detachment.* The read's frame is a frame under exactly that rule, with three differences in code:
1. Its mark is a shared `HighWaterMark(SystemHigh)`, which observing can never change (`HighWaterMark.Observe` is a compare-and-swap join, so the shared static mark is never written).
2. `Live` skips it, so it never names a decision.
3. Its own `Dispose` observes nothing outward: its `SystemHigh` stands for what the read has not reported yet, and `ReadScope.Dispose` has already observed the read's result.

`Resolve`, `ObserveInto`, the three dispose states and the head-only restore are unchanged. So:
- the flow that ends a read in order, in a `using` block, goes back to its frames, which already hold what the read observed;
- work created and started inside a read, such as a fire-and-forget task a tool starts during its call, keeps the read's frame. It decides at `SystemHigh` for its whole life, also after the read ended (fail closed), and what it observes still reaches the frames the read was begun in;
- a read ended out of order or on another flow leaves the flow that began it inside the read's frame, at `SystemHigh`, for as long as that flow lives. This is 4.4's limit (a), fail closed;
- a read begun inside a read is a frame inside a frame: ending the inner one takes the flow back to the outer read's frame, still `SystemHigh`, never past it.

**Stricter than the decision's wording, on purpose:** "open and unreported" is implemented as "not yet ended". A report counts only once the read has completed and ended: a read that reports and then throws still counts as `SystemHigh` (4.4's completion rule). So until the scope is disposed it cannot be known to be reported. A labelled tool's egress after its report, and the agent's own code between `Complete()` and the end of the `using` block, are decided at `SystemHigh`. `ToolCallingAgent` ends the block immediately after `Complete()`. This is the shape the integrator's instruction named ("BeginRead enters a read frame pinned at SystemHigh; disposing leaves it by the no-skip rule").

**The accepted cost (owner):** under a runner floor below `SystemHigh`, a tool's own egress during its call is refused once the guard enforces, a labelled tool's included. **Consequence for the PR 4 design's §5 leak test (4.11):**
- Scenario B's web search runs inside the `web_search` tool call, so its expected reason changes from `LevelTooLow` to `SystemHighData`.
- Control C4's inner send, made by a test tool during its call, is now decided at `SystemHigh` too. That send no longer isolates monotone nesting, which 4.4's own twins pin.
- Scenario B and control C5 then share a reason; they differ by site and family (the tool's `EG-WEB-01`, WebSearch, against the model's `EG-MDL-01`, ExternalModel), which the leak test must assert.

SPEC-007 and the docs on master do not state Scenario B's reason, so only the decisions-log row records it. The storage branch's `DESIGN-4-final.md` §5 should be amended by the integrator.

### Obligation (d), checked at both production entries

| Entry | `using` on the entering flow | Disposed in order | Never spans a `yield return` | Not in an async helper whose frame outlives it | Work it reads through created and started inside |
|---|---|---|---|---|---|
| `SelfExtendRunnerAdapter.RunAsync(string, string?, string?, string?, string?, string?, CancellationToken)`: `using var subject = EgressSubject.Enter("agent:" + resolvedAgentId, new HighWaterMark(SecurityLabel.SystemHigh));` | yes: a `using` declaration in the method body, on the flow that runs `RunAsync` | yes: the method's only frame, disposed when `RunAsync` returns (also on the catch path) | yes: `RunAsync` is not an iterator | yes: entered and disposed in `RunAsync` itself, so the frame never outlives the method, and no caller relies on it (an `async` method's frame never reaches the flow that awaits it, so a frame left undisposed on an exception path could not become the registry's head either) | yes. After it: the objective claim, the toolbox (`CreateWithBuildTest`), the snapshot (scratchpad, observations, repo listing), `RunCycleAsync`, the scratchpad write, the claim release and `SelfExtendAdmissionBridge.TryRecordAsync` (admission and auto-share). Before it: only argument validation and name resolution. Work captured before the frame (an MCP transport pump created at connect time, if the SDK sends on one; the `ProviderFactory` warm-up task) stays `no-subject`, which the producers section records. |
| `ToolCallingAgent.RunCycleAsync(…)`: `using (var read = EgressSubject.BeginRead()) { … read.Complete(); }` | yes: a `using` statement in the method body, on the agent's flow | yes: the block ends before the next tool call and the next model call | yes: not an iterator | yes: begun and ended in `RunCycleAsync` itself | yes: the tool invocation (`InvokeAsync` or `InvokeLabelledAsync`) is created and awaited inside the block, and the toolbox is asked only after the read began, so a registry that refuses an unregistered id synchronously still ends the read unreported |

`EgressSubjectProducerConventionTests` checks the first three columns mechanically, for every production entry, and its fixture facts show those checks read the syntax (a bare `Enter`, an iterator, a lambda inside an iterator, a `using static`, a `using` alias).

## Changes

- **`src/Ashlar.Abstractions/Security/Egress/EgressSubject.cs`**
  - `BeginRead` enters a read's frame (`Frame.ForRead`, shared `SystemHigh` mark) and hands it to the scope.
  - `Frame.Live` skips read's frames, so the basis is the innermost live subject frame's.
  - `Frame.Dispose` of a read's frame observes nothing outward; the head-only restore is unchanged.
  - Remarks: "Current label" (a read's frame counts `SystemHigh`; "no live subject frame"), "Reads" (the open-read rule), the new "A read's frame is a frame" paragraph (limit (a) for reads, work created inside), the closing paragraph ("never inside a read that has not ended"), and the `BeginRead` summary and remarks.
- **`src/Ashlar.Abstractions/Security/Egress/ReadScope.cs`**: holds the read's frame. `Dispose` observes first, then disposes the frame. The summary and remarks state the open-read rule and what happens when the scope is disposed on another flow.
- **`src/Ashlar.Abstractions/Security/Egress/ReadReporter.cs`** (new, public, sealed): the report-only surface a labelled tool is handed: `Report(SecurityLabel)` and nothing else, an internal constructor, no way back to the scope. `ReadScope` gains `Reporter`.
- **`src/Ashlar.Abstractions/Security/Egress/ILabelledTool.cs`** (new, public): `ILabelledTool : ITool` with `InvokeLabelledAsync(ToolCall, WorldSnapshot, ReadReporter, CancellationToken)`. Implementing it is the declaration. Its remarks cover: a labelled tool reports for all of its result and never completes or disposes the scope; a decorator or a toolbox that hides the tool is not labelled; a tool that cannot label every part of its result must not implement it; its own egress during the read is decided at `SystemHigh`; only `RAGTool` implements it in production (pinned), and a host's own labelled tool is the host's trusted base.
- **`src/Ashlar.Abstractions/PublicAPI.Unshipped.txt`**: `ILabelledTool`, `ILabelledTool.InvokeLabelledAsync`, `ReadReporter`, `ReadReporter.Report`, `ReadScope.Reporter`.
- **`src/Ashlar.Runtime/CapabilityRegistry.cs`**: `public ITool? Find(string id)`, the tool `InvokeAsync` would invoke for that id. Runtime has no PublicAPI baseline; the CHANGELOG entry is the record.
- **`src/Ashlar.BackgroundAgents/Agents/ToolCallingAgent.cs`**: the read scope around each tool call, and `LabelledToolFor`, which returns `registry.Find(id) as ILabelledTool` for a `CapabilityRegistry` and `null` otherwise; the labelled tool receives `read.Reporter`, and only the agent completes the scope. The `RunCycleAsync` summary gains a paragraph on this.
- **`src/Ashlar.BackgroundAgents/RAG/RAGTool.cs`**: `RAGTool : ILabelledTool`. `InvokeAsync` and `InvokeLabelledAsync` share `InvokeCoreAsync`. The private `HitLabel` maps a tier: trim; a spelling `TrustTierOrder` ranks (`CanonicalSpellings`, ordinal ignoring case); `registry.GetByName`; one of `DataSensitivityLevels.All`; `ToDataLabel`; else `SystemHigh`. The reports cover hits, no hits and the refusal, where "read nothing" is reported only for `VectorMath.IsUnrankableQuery(ex)`. Class remarks added.
- **`src/Ashlar.BackgroundAgents/RAG/VectorMath.cs`**: the refusal's message becomes a constant, and the internal `IsUnrankableQuery(ArgumentException)` recognises it.
- **`src/Ashlar.BackgroundAgents.HostRunners/SelfExtendRunnerAdapter.cs`**: the frame, with a comment that states obligation (d).
- **`src/Ashlar.AI.Pipeline/Governance/EgressGuardChatClient.cs`**: an internal constructor taking the target key; `IsAgentBacked`; `ObservedAsync` for a response and for a stream. Both overrides stay non-`async`, so `Decide` stays eager and an inner layer's synchronous throw still leaves synchronously, and it also observes. Class remarks added (how a call may end; the target-key rule and the host's trusted base).
- **`src/Ashlar.AI.Pipeline/Governance/AshlarGovernanceChatClientBuilderExtensions.cs`**: passes the target key; remarks.
- **Tests** (cert-gate namespace):
  - `EgressProducerTwinTests` (new; {{PRODUCER_COUNT}} test cases in {{PRODUCER_METHODS}} methods): the leak skeleton (`Secret`, `Internal`); unlabelled, side-`Observe`, throwing, `OperationCanceledException`-throwing, in-call-egress and unregistered tools; a tool registered under `RAGTool`'s id; `RAGTool` parity (28 names), the non-ASCII domination theory (6), join, read-nothing (no hits; the real refusal through `RAGService` over `InMemoryVectorStore` with a zero embedding), a store's other `ArgumentException`, its store's own egress, custom levels (7 name/value pairs); a labelled tool that reports then throws; a labelled tool behind a decorator or another toolbox; peer, streamed peer, empty-stream, faulted (2), policy-denied (2), eager-decide, unknown-kind and `local:` responses; production self-extend.
  - `EgressSubjectProducerConventionTests` (new; 6 facts): the three convention facts, the implementers fact, two fixture facts.
  - `EgressSubjectReadScopeTests`: 4 new twins (in-call egress, a frame entered inside a read, work created inside a read, nested read scopes). Two pinned outcomes flip by name: `A_completed_read_with_no_report_observes_SystemHigh` (the in-scope decision was `Public`, now `SystemHigh`) and `A_read_observes_into_every_live_frame_of_its_chain_from_whatever_flow_it_ends_on` (the flow whose read another flow ended decided `LevelTooLow`, now stays at `SystemHigh`). The class remarks gain the open-read paragraph.
  - `EgressGuardConventionTests`: the floors comment (2,136 files, 150 occurrences).

## Behaviour changes, and what stays report-only

| Change | Production effect today |
|---|---|
| A decision inside a read that has not ended is made at `SystemHigh` | None in label: the only production reads run inside self-extend's `SystemHigh` frame |
| Self-extend's frame | Records change basis from `no-subject` to `subject:agent:<id>` (its model calls, EG-MDL-01 on the MEAI route; its `dotnet.build` and `dotnet.test` tool calls, EG-PROC-01; the auto-share after admission, EG-MESH-01); the label stays `SystemHigh` |
| `ToolCallingAgent` read scopes, `RAGTool` reports, the spelling check, the narrowed "read nothing" | None in label under self-extend's `SystemHigh` floor; `RAGTool` is registered in no production toolbox |
| Agent-backed responses observe `SystemHigh` | None in label inside self-extend; no other production flow has a frame, and no production composition registers a `peer:` client |
| `CapabilityRegistry.Find`, `ILabelledTool`, `VectorMath.IsUnrankableQuery` (internal) | Additive API |

Everything stays report-only: `EgressGuard.Evaluate` records and never refuses. Routes act on the mode from 4.7, and the switch is 4.11.

## Testing

All builds and tests ran in the Linux devtest container through `scripts/test-in-container.sh`. No `dotnet` command ran on the host; only the repo gates (shell and Python) did. Logs are under the lane's `logs-4.5/` and `mut-4.5/`.

{{TESTING}}

### Mutations

{{MUTATIONS}}

### Not observed failing

{{NOT_OBSERVED}}

### Model check of the open-read rule

{{MODEL}}

## Deviations and integrator decisions

{{DEVIATIONS}}

## Adversarial inputs

{{ADVERSARIAL}}

## Records (for the drift audit)

{{RECORDS}}

## Checklist

- [x] Repo gates pass locally ({{GATES}} of 26).
- [x] The cert-gate passes in the devtest container on net8.0 ({{CERT}}), and the touched classes pass on net8.0 and net10.0.
- [x] `Ashlar.Abstractions` builds with 0 warnings on netstandard2.0, net8.0 and net10.0.
- [x] Documentation updated: CHANGELOG, SPEC-007 (status, decisions log, the 4.5 plan line), `docs/EgressInventory.md`, `ci/cert-gate-assertions.md`.
- [x] No `TODO` or `NotImplementedException` in the code.
- [x] Breaking changes: none. The public API change is additive (`ILabelledTool`, Unshipped; `CapabilityRegistry.Find`). The open-read rule changes the label decided inside a read scope, which only tightens; `RAGTool`'s narrowed "read nothing" and spelling check only tighten.

## Release

- [x] Not a versioned release; skip.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_017TjtzCLb6VaYxD1MtRJ1ja
