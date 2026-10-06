## Summary

**Pre-merge gate, head `3de00809`.** Two focused checks of `91cf8d74`.
- **Robustness lens: no findings.** The IL twin finds the writes and the catch-all by meaning (Cecil exception handlers, `AsyncLocal` setter calls), not by offset. It passed in Debug and Release on net8.0 and net10.0, and under coverlet with Abstractions instrumented, and coverlet rewrote the handler bounds correctly. CI's only coverage run (kernel-coverage) instruments `Ashlar.Infrastructure` alone; it is green on `91cf8d74`. The moved-restore mutant goes red in Release and under coverlet.
- **Records lens: four low wording findings, fixed in `3de00809`.**
  - Limit (b) and row 65 now name the work that captures the flow where it is created: a `Task`, a `System.Threading.Timer`, a registration or a continuation. A `Thread` or `System.Timers.Timer` captures where it is started.
  - Row 67's lead-in now names what its list holds.
  - `ControlledExecution.Run` exists from .NET 7.
  - The `RunDetached` bullet below is split in two.
- **Re-run at `3de00809`:** the frame-semantics filter 451 of 451 on net8.0; Abstractions 0 warnings on netstandard2.0, net8.0 and net10.0; repo gates 26 of 26. The change is comments and records only.

**Status at `91cf8d74`** (master `ad3d570b`, #719, merged in): the third final re-check's four findings are handled, without changing the decided rule. One is a code change: `RunDetached` now detaches the flow and restores it on return inside its `try`, so an asynchronous abort cannot leave the flow detached. Two are records (they now say *created and started* where they said "started" or "created", and the 4.5 obligation gains the case of work created before the frame), and one corrects evidence from the last report. Two new twins. Cert-gate 2763 of 2763; the frame-semantics filter 451 of 451 on net8.0 and 451 of 451 on net10.0; repo gates 26 of 26. Details are under "Third final re-check (this round)".

SPEC-007 PR 4.4 gives `EgressSubject` frames the semantics enforcement needs: **monotone nesting**, **`Observe`**, and **read scopes**. It also makes `AgentBus` start every subscriber with no subject. It implements row 4.4 of the PR 4 plan: design §2.2 ("Observe, report, and read scopes", "Monotone nesting", "Threading facts") and defaults D12, D13, D14, D18 and D37. The design and its decision ids live in `_handoff/spec-007-pr4/DESIGN-4-final.md` on the `claude/spec-007-pr4-workspace` branch; the permanent records in this PR name the rules, not the ids.

The plan puts this PR first because it closes a declassification hole before any producer exists. Before this PR, `Resolve` read only the innermost live frame. So any code that entered a fresh `Public` frame inside a `Secret` one would decide `Public`, and a nested subject's reads were lost when its frame ended.

### The frame rule

1. **Subject frames: a flow leaves a frame only by disposing its own head while that head is undisposed.** Disposing restores exactly that frame's `_previous`, disposed or not. Disposing a frame anywhere else moves no flow, so a flow whose head is already disposed (out of order, or on another flow) never leaves it: limit (a). There is no skip record, no `Skips` `AsyncLocal`, no `Unwind` and no `_unwoundBy`. The rest is unchanged from the fourth round:
   - `Resolve` joins every subject frame's mark from the chain's head down to a detachment, read live when the decision is made.
   - `ObserveInto` raises every frame on the chain up to a detachment, disposed or not.
   - A frame has three states (entered, disposing, disposed), and its mark leaves before it is marked disposed.
   - `Observe` and `BeginRead` capture the chain head.
2. **Detachments are callbacks.** The internal `IDisposable EgressSubject.Detach()` is gone. In its place is `internal static void RunDetached(Action start)`. It installs a detachment as the flow's head, runs the callback, and puts back exactly the caller's head on the calling flow: at the end of the `try` when the callback returns and, when it throws, in a catch-all that restores and rethrows. Both writes, the detachment and the restore on return, are inside the `try`, so an asynchronous abort that lands next to either one still reaches the catch. There is no `finally`, so the caller's head is back before any exception filter of the caller runs, and nothing in `RunDetached` runs after one (an exception's first pass runs every filter up the stack before any `finally` block; see the re-checks below). No caller ever holds a detachment, so a detachment can never be disposed out of order, on another flow, or by a task it was handed. Work **created and started** inside the callback, such as `AgentBus`'s subscriber tasks (`Task.Run`), keeps the detachment for its whole life. A `Task`, a `System.Threading.Timer`, a cancellation registration or a continuation captures the flow where it is created, and a `Thread` or a `System.Timers.Timer` where it is started. So a task the caller created and the callback starts or triggers keeps the caller's frame, and so does a thread the callback created and the caller starts after it returns. A frame the callback enters and leaves undisposed is dropped from the calling flow on return, as in-order `using (Detach()) { var f = Enter(...); }` would leave the caller: a frame entered under a detachment never reaches the caller. `AgentBus.PublishAsync` is the single production call site.

**Why this rule.** Three rounds of *bounded unwinding*, which let a flow go past a frame it had disposed out of order, each produced a verified write-down (history below):
- **Round 2: a record on the frame (`_unwoundBy`).** Every flow whose head was the recorded frame went past the parent. That included a task handed the child scope and the flow that entered a frame another flow disposed (P1, M3, M5: red at `ef4da122`).
- **Round 3: "go past only a subject frame whose `_previous` is a subject frame".** It has a two-flow write-down at 8 operations: `E;E;E;T;X1@main;X2@task;X0@task;E@task`. This holds over 5,280,133 modelled sequences, also with `Resolve` joining from the head.
- **Rounds 3 and 4: a record that is the disposing flow's own (`Skips`).** The no-skip re-check's probes WD_A to WD_E and WD_G decided `Public` inside a live `Secret` caller at `e648ac4d`. In those probes the detachment was ended inside an awaited async helper, by an `await using` lease, by another flow, or first by a task started under it.

**No-skip** (never go past a frame the flow did not leave as its head) is write-down free for subject frames. The integrator's model checked 555,403 sequences at N=8 over several flows. It still had one class of write-down left: the D1 class. An `IDisposable` detachment disposed out of order left its flow on a disposed detachment, below the live caller frame it was inside. The one-flow form is `Enter; Detach; Enter; detachment.Dispose(); frame.Dispose(); Enter` on one flow, which decided `Public` inside a `Secret` caller at `e761b070` (the D1 probe, the only red test of 401 there). A callback cannot be ended out of order, so **the callback form closes D1 by construction**. The same multi-flow model as the re-check's, with its disposable detachment replaced by callback brackets, finds no write-down (see "Testing").

**Accepted consequences (fail closed; recorded under "Known limits (fail closed)" in the `EgressSubject` remarks, row 65, the CHANGELOG entry and the SPEC-007 4.4 line):**
- **(a) Out-of-order disposal costs availability.** A flow that disposes subject frames out of order stays inside the outer frame it disposed. A frame entered later on that flow joins that frame's mark, and in a loop the flow's chain grows by one frame per repetition. That costs availability, not confidentiality. It is pinned with exact lengths by `A_flow_that_disposes_its_frames_out_of_order_stays_inside_the_outer_frame_it_disposed` (1, then `i + 2` per iteration over 50 iterations, then 53), and by `Every_dispose_order_of_three_frames_on_one_flow_leaves_only_the_head_it_disposes`, which leaves 13 frames.
- **(b) A stuck frame raises the subject's shared mark.** Anything such a flow reads later raises the stuck frame's mark. That mark is the subject's shared `HighWaterMark`, so unrelated later work can raise another session's label. It is pinned by `What_a_flow_reads_after_an_out_of_order_dispose_raises_the_subjects_shared_mark_for_its_other_sessions`: another session of the subject decides `Confidential` (`LevelTooLow`) at what this flow's unrelated work read. Neither limit arises on the flow that entered a frame and disposed it in a `using` block, in order. Work created and started inside that frame, such as a task or a thread, still keeps it for as long as it runs, so what it reads later raises the frame's shared mark (pinned by the sibling-task twins). Work keeps the frames of the flow where it captured the execution context (a `Task`, `System.Threading.Timer`, registration or continuation where it is created, a `Thread` or `System.Timers.Timer` where it is started), so what a cold task created before the frame and started inside it reads never reaches the frame's mark.
- **Production is unaffected today.** No production code calls `Enter`, and `AgentBus`'s single detachment is the callback.
- **Carried obligation for 4.5.** Every production `Enter` is a `using` on the flow that enters it, disposed in order. It is never placed inside an async helper, whose frame does not reach the flow that awaits it. Work the runner creates and starts inside it, such as a task or a thread, keeps the frame after the `using` ends, so its later reads still raise the subject's shared mark. What is read by work created before the frame (a cold task, a continuation, a timer or a registration), or by a thread created inside it and started after the `using` ends, never reaches the frame's mark, so the runner would decide below what the work it started and awaited has read: a runner creates and starts the work it reads through inside its `using` block. (This sentence is this round's addition to obligation (d); the integrator may keep or drop it.) Its `using` block never spans a `yield return` of an async iterator: each `MoveNextAsync` runs the body on the consumer's flow, so after the first `yield return` the frame is gone, the rest of the block decides at the consumer's frames (a write-down wherever they do not hold the subject's mark), and its reads never reach that mark. The code that drives the iteration enters the frame around its `await foreach`, or the body enters one for each stretch between two `yield return`s. Ashlar's streaming chat clients are such iterators (`GetStreamingResponseAsync` in seven production files, `RoutingChatClient` among them). This is written into SPEC-007's 4.5 plan line, the `EgressSubject` remarks ("Leaving a frame"), a remark on `Enter`, row 65's Why and the CHANGELOG entry.

### What else this PR adds

- **Nesting.** A decision's current label is the join of the marks of every frame on the flow's chain, **live or disposed**, up to a detachment. That is the flow's own frame and every frame it was entered inside, each mark read as it is when the decision is made. Its basis names the innermost live subject. Disposing a frame observes its mark into every frame it was entered inside.
- **Disposed ancestors still count (fail closed).** A parent frame never declassifies the frames inside it, and what those frames read still raises it. This holds for a parent disposed first, and for a parent whose `using` ends while a fire-and-forget task it started still runs. A task started inside a parent stays inside it, in frames it enters then or later and with no frame of its own. A frame counts as live until its mark has been observed into the frames around it, so a decision made meanwhile never names an enclosing frame that does not hold the mark yet.
- **`EgressSubject.Observe(SecurityLabel)`** (public) joins a label into every frame on the chain at once. It only raises, and with no frame it does nothing.
- **`EgressSubject.BeginRead()`** (public) returns a `ReadScope` for one read. A read counts at what it reported only when it returned (`Complete()`) and reported. Otherwise it counts as `SystemHigh`, which covers both an unreported read and a read that threw. `Observe` never satisfies a scope.
- **Flipped by name:** `EgressGuardDecisionTests.An_Internal_subject_may_reach_an_external_model_across_await_and_Dispose_restores_the_previous_frame`. Its inner block pinned "inner Public decides Public". It now pins "inner Public inside Internal decides Internal, refused `LevelTooLow` to a Public destination".

**Still report-only, and no production outcome changes.** No production code enters a frame or begins a read yet; the producers are PR 4.5. Today every production decision is `no-subject`, and with no frame `Resolve`, `Observe` and a read scope behave as before. The one production call site this PR adds is `AgentBus`'s `RunDetached`, which turns a frame into no subject, and production has no frame for it to remove.

These are classification-style controls inside the runtime. They are not an accredited cross-domain solution.

No non-test code under `application/` changes, so this PR is not `[coordinated-integration]`.

## Changes

- **`src/Ashlar.Abstractions/Security/Egress/EgressSubject.cs`**
  - `Resolve` returns the join of the mark of every subject frame on the flow's chain, from its head, live or disposed, up to a detachment. Its basis is the innermost live frame's. With no live frame, or under a detachment, it returns `SystemHigh` and `no-subject`.
  - `ObserveInto` raises every subject frame on the chain, live or disposed, up to a detachment. `Observe`, `Frame.Dispose` and `ReadScope` use it.
  - `Frame.Dispose` observes the frame's mark into every frame it was entered inside, and only then marks the frame disposed: the three states. On the flow where the frame is the head, it restores exactly `_previous`. Anywhere else it moves no flow.
  - New public `Observe(SecurityLabel)` and `BeginRead()`.
  - New internal `RunDetached(Action)`. It detaches the flow and restores the caller's head on return, both inside the `try`, so an asynchronous abort cannot leave the flow detached. On a throw it restores in a catch-all that rethrows, with no `finally`, so a caller's exception filter runs in the caller's frame and nothing in `RunDetached` runs after it, while the callback's own filters and `finally` blocks run detached. A detachment is a frame with no mark and no enclosing frame, so every chain walk ends at it. Only `RunDetached` makes one, and it is never disposed. The detachment's restore target, the `Outer` indirection and every disposal path of an `IDisposable` detachment are deleted.
  - The class remarks state the rule (a flow leaves a frame only by disposing its own head while that head is undisposed, and goes back to exactly the frame it was entered under, disposed or not), "Disposed frames still count", and "Known limits (fail closed)" (a) and (b), which never arise on the flow that entered a frame and disposed it in order, though work created and started inside the frame keeps it, and what a `Task`, `System.Threading.Timer`, registration or continuation created before the frame reads never reaches its mark. They also state that a frame entered inside an async method is not on the flow that awaits it, and that a frame does not outlast a `yield return` of an async iterator. `Enter` has a remark that says both.
  - The nested `Frame` class becomes `internal` (it was `private`), so `ReadScope` can hold the chain it was begun on.
- **`src/Ashlar.Abstractions/Security/Egress/ReadScope.cs`** (new, `public sealed`, `IDisposable`):
  - `Report(SecurityLabel)` joins the reports with a compare-and-swap loop.
  - `Complete()` marks the read as returned.
  - `Dispose()` observes into every frame of the chain the scope was begun on: the reported join if it completed and reported, otherwise `SystemHigh`. A report made after the scope ended goes to the frames directly, so it is never lost. Disposing twice does nothing.
  - The scope is not ambient: only the code holding it can report.
- **`src/Ashlar.Orchestration/Communication/AgentBus.cs`**: the subscriber dispatch loop runs inside `EgressSubject.RunDetached(() => { ... })`. Each `Task.Run` is created there, so it captures a detached context. That covers the handler and its catch-and-log, for the whole life of the task. The publisher's flow is back in its own frame as soon as the callback returns.
- **`src/Ashlar.Abstractions/Ashlar.Abstractions.csproj`**: `InternalsVisibleTo Ashlar.Orchestration`, for the internal `RunDetached`. This is the D9 amendment recorded in SPEC-007.
- **`src/Ashlar.Abstractions/PublicAPI.Unshipped.txt`**: `EgressSubject.Observe`, `EgressSubject.BeginRead`, `ReadScope`, `ReadScope.Report`, `ReadScope.Complete` and `ReadScope.Dispose`. `RunDetached` is internal and not listed.
- **Tests**, all in the cert-gate namespace:
  - `EgressSubjectNestingTests` (27);
  - `EgressSubjectReadScopeTests` (12);
  - `EgressSubjectDetachTests` (18, among them the two convention facts, the four exception-filter twins, the created-outside/created-inside twin, the started-outside/started-inside thread and `System.Timers.Timer` twin, and the fact that reads `RunDetached`'s IL);
  - the flipped `EgressGuardDecisionTests` test, and `A_frame_disposed_from_another_flow_no_longer_names_this_flows_decisions` (renamed from `…_stops_counting_on_this_one`, which the rule made false: the frame still counts on the flow that entered it).

### Design points this PR had to settle

- **How a scope learns it ended by an exception: `Complete()`.** §2.2 and D14 require it, but the design names no mechanism, and a `using` block cannot see an exception. `Complete()` follows `TransactionScope`. A holder that forgets it fails closed (`SystemHigh`). A `Fail()` call forgotten on a catch path would fail open.
- **"Read nothing" is `Report(SecurityLabel.Public)`.** `Public` is the bottom, so the report satisfies the scope and raises nothing. No separate member was added.
- **The scope is held, not ambient.** Consider "`ReadScope.Report(label)` from a tool that declares itself labelled", with `ToolCallingAgent` checking the marker before it accepts a report. That works when the agent holds the scope and passes on the labelled tool's report. Code that only reaches `EgressSubject` cannot report at all. PR 4.5 decides how `RAGTool`'s label reaches `Report`.
- **The basis of a nested decision names the innermost subject.** The leak test filters by `subject:agent:<id>`, and C4's inner send records the inner subject.
- **The detachment's shape is the smallest one `AgentBus` needs:** `void RunDetached(Action)`, with no generic overload.

## Behaviour changes, and what stays report-only

| Change | Production effect today |
|---|---|
| A nested frame decides at the join of the chain, every mark read when the decision is made | None: no production code enters a frame |
| Disposing a frame raises its enclosing frames | None: no production frames |
| A flow leaves a frame only by disposing its own head; out of order it stays inside (known limits (a), (b)) | None: no production frames |
| A frame counts as live until its mark has been observed outward | None: no production frames |
| `Observe` and read scopes | None: nothing calls them yet (PR 4.5) |
| `AgentBus` subscribers run with no subject, inside `RunDetached` | None in recorded decisions: production publishers run with no subject already. One detachment allocation and one closure per publish |
| `RunDetached` puts the caller's frame back before any exception filter of the caller runs, and runs nothing after one | None: `AgentBus`'s callback only starts tasks |
| `RunDetached` detaches and restores on return inside its `try`, so an asynchronous abort (`ControlledExecution.Run`) cannot leave the flow detached | None: production never aborts a thread |

Everything stays report-only: `EgressGuard.Evaluate` records and never refuses, throws or blocks. The mode plumbing is PR 4.6 and the switch is 4.11.

## Testing

All builds and tests ran in the Linux devtest container through `scripts/test-in-container.sh`. No `dotnet` command ran on the host; only the repo gates (shell and Python) and the Python models did.

### Third final re-check (this round): four findings

Commits after `cb43f7cf`:
- `f1ab4a93` holds two twins alone.
- `91cf8d74` is the fix and the records. It is the pushed head.

Each finding, and what was done (none changes the decided rule):
1. **The restore on return left the protected region** (low). Real. Last round's fix removed the `finally`, which left `Active.Value = caller;` after the `try`, outside any handler. An asynchronous abort (`ControlledExecution.Run`, .NET 7 and later, obsolete as SYSLIB0046) that lands there leaves `RunDetached` with the flow detached. The caller's filter for the abort then decides below the caller's mark, and its reads miss the caller's frames. After the abort the flow also stays detached. The re-check's probe measured this at `cb43f7cf`: 42 of 120 and 22 of 60 aborts left the flow detached, against 0 at `db80d792` and 0 with the candidate fix. **Fixed:** `var caller = Active.Value; try { Active.Value = Frame.Detachment(); start(); Active.Value = caller; } catch { Active.Value = caller; throw; }`. Both writes are inside the `try`. There is still no `finally`, so the Z1 twin holds, and the runtime holds an abort back while a catch runs. A probe that aborts cannot sit in the cert-gate, because `ControlledExecution` crashes the test host now and then (`PAL_SEHException`). So the new fact `RunDetached_writes_the_flows_frame_only_inside_a_try_that_catches_everything_or_in_that_catch` reads the compiled method with Mono.Cecil. It requires one catch-everything clause and three writes of the flow's frame (`AsyncLocal<Frame>.Value`), each inside that clause's `try` or its catch. The `RunDetached` remarks, the catch and `try` comments, and row 67 (rule and Why) say so.
2. **"A task or thread started inside that frame keeps it" repeated the started-versus-created mistake for subject frames** (medium). Real. A `Task`, a `System.Threading.Timer`, a registration and a continuation capture the flow where they are *created*. So what a cold task created before a frame reads, even when it is started inside the frame, never reaches the frame's mark (the re-check's probe on both TFMs). **Fixed in the records:**
   - the `EgressSubject` class remarks ("Leaving a frame" and known limit (b)), row 65's Why, the CHANGELOG, SPEC-007's 4.5 obligation and this body now say *created and started* inside the frame;
   - they also state where each kind of work captures the flow, and that what a `Task`, `System.Threading.Timer`, registration or continuation created before the frame reads never reaches its mark (a `Thread` or `System.Timers.Timer` captures at `Start`);
   - the 4.5 obligation adds "a runner creates and starts the work it reads through inside its `using` block". That sentence is this round's addition to obligation (d); the integrator may keep or drop it.

   No twin was added for this subject-frame case. It pins only the runtime's capture point, and no mutation of this PR's code could turn it red.
3. **"Work created inside the callback keeps no subject" was too wide the other way** (low). Real. A `Thread` and a `System.Timers.Timer` capture the flow where they are *started*. **Fixed in the records:**
   - the `RunDetached` summary and remarks, row 67 (rule and Why: "a new site must create and start the work it hands off inside the callback"), the CHANGELOG, SPEC-007, the `EgressSubjectDetachTests` remarks and this body now say *created and started*, and name where each kind captures the flow;
   - the new twin `A_thread_or_a_timers_timer_keeps_the_frame_of_the_flow_that_starts_it_not_of_the_one_that_creates_it` pins both directions. A thread and a `System.Timers.Timer` the caller created and the callback starts decide with no subject (`SystemHigh`). One the callback created and the caller starts after it returns decides at the caller's frame (`Secret`, `LevelTooLow`).

   The decided rule's "work CREATED inside keeps the detachment" is read as scoped to `Task`, `System.Threading.Timer`, registration and continuation. Production is not exposed: `AgentBus` creates and starts each `Task.Run` inside the callback.
4. **Last round's report claimed the created-inside half went red under m-g1** (low, evidence). Real. Under m-g1 the created-outside/created-inside twin fails at its line 420, the "the callback has returned" check. That check runs before either half's decisions are examined. The PR body only said the twin went red, which is true. **Corrected** by the new mutation `m-n2`, which keeps the restore and drops the detachment (`Active.Value = Frame.Detachment();` becomes `Active.Value = caller;`). Under it the twin passes the restore check and its caller-created loop, and fails in its callback-created loop, at line 488: "Task.Run the callback created captured the detachment, wherever it was started or triggered", actual `subject:created-caller`. The thread twin fails in its started-inside loop, at line 549. The assertion stops at the first failing entry of each loop, so the other created-inside entries were not each seen red.

**Red first** at `f1ab4a93` (the twins alone, on the old code), net8.0, filter `FullyQualifiedName~EgressSubjectDetachTests`: **1 failed of 18**, the IL fact: "Expected string.Join(", ", unprotected) to be empty because an asynchronous abort next to a write of the flow's frame outside the try and its catch would leave RunDetached with the flow detached, but found "IL_0022, IL_0049"". These are the detachment before the `try` and the restore after it. The thread twin passed there, as expected, because it pins runtime behaviour. **Green** at `91cf8d74`: **451 of 451** on net8.0.

**Testing at the pushed head `91cf8d74`** (master `ad3d570b` merged, unchanged), all in the devtest container:
- the filter `FullyQualifiedName~EgressSubject|FullyQualifiedName~EgressGuard`: **451 of 451** on net8.0 and **451 of 451** on net10.0;
- the full cert-gate (`bash scripts/run-cert-gate.sh`, net8.0): **2763 of 2763**, skip guard 0;
- the repo gates (`scripts/ci/run-repo-gates.sh`): **all 26 passed**. The knowledge graph was regenerated after `git add` and holds 4874 declared facts.
- the abort probe, re-run at this head: shape 0 (empty callback, 120 planned) completed **111 aborts, all 111 back in the caller's frame**, each with the caller's filter run in the caller's frame and its read in the caller's mark, before the test host crashed with `PAL_SEHException` (the runtime's, as in the re-check). At `cb43f7cf` the same probe left 42 of 120 detached. A first run at this head crashed before printing progress. Logs: `logs-4.4-r11/abort-probe-net8-head-run2.log` in the lane's scratchpad. The probe is not committed.

**Mutations this round** (`scripts/mutation-check.sh`, net8.0, the filter above):

| # | Rule | Mutation | Summary line (verbatim) | What went red |
|---|---|---|---|---|
| m-n1b | `RunDetached` restores on return inside the `try` | the restore on return moved back after the `try`, the code at `cb43f7cf` | `mutation m-n1b-run-detached-restores-on-return-after-the-try: KILLED red=failed:1/451 green=passed:451/451 ref=91cf8d7453af193bae8848a8894c23c8657e24d7` | the IL fact, with "found "IL_0049"" |
| m-n2 | Work created and started inside the callback keeps no subject | `Active.Value = Frame.Detachment();` becomes `Active.Value = caller;` | `mutation m-n2-run-detached-does-not-detach: KILLED red=failed:11/451 green=passed:451/451 ref=91cf8d7453af193bae8848a8894c23c8657e24d7` | 11, including the created-inside half of the created-outside/created-inside twin (line 488) and the started-inside half of the thread twin (line 549), the AgentBus subscriber, both task twins, the leave-and-restore, dropped-frame, program and two filter twins, and the read-scope twin for a read ended on a flow without its chain |

Each KILLED run applied its replacement ("replaced 1 occurrence"), went red, restored with `git status --porcelain` empty, and went green again.

### Second final re-check (previous round): six findings, and master #719

Commits after `db80d792`:
- `86a33a0f` holds two twins alone.
- `26407e3a` is the fix and the records.
- `0ade086d` merges master `ad3d570b` (#719, PR 4.2).
- `cb43f7cf` rewraps one line of the `EgressSubjectDetachTests` remarks (comment only). It was that round's pushed head.

Each finding, and what was done (none changes the decided rule):
1. **Known limit (b) exempted a frame, not a flow** (medium). Real: a task started inside a frame that its flow entered and disposed in order in a `using` block keeps that frame, and its later reads raise the frame's shared mark (the PR's sibling-task twins pin this). **Fixed:** the `EgressSubject` remarks, row 65's Why and the CHANGELOG now say the limits never arise *on the flow* that entered the frame and disposed it in order, and that a task or thread started inside the frame keeps it for as long as it runs. SPEC-007's 4.5 obligation says so too.
2. **"Disposed or not" sat on the wrong frame** (low). Real: `Dispose` returns at its `CompareExchange` unless the frame is still entered, so a flow whose head is already disposed never leaves it. **Fixed** in SPEC-007, the `EgressSubject` remarks, row 65, the CHANGELOG, the `EgressSubjectNestingTests` remarks and `FlowModel` summary, and this body. A flow leaves a frame only by disposing its own head while that head is undisposed, and goes back to exactly the frame it was entered under, disposed or not. A flow whose head was already disposed never leaves it (limit (a)).
3. **A test name from the `Detach()` era** (low). Real. **Fixed:** `A_flow_that_disposes_its_own_detachment_out_of_order_never_decides_below_the_callers_frame` is now `A_callback_that_returns_with_frames_it_entered_undisposed_never_leaves_the_flow_below_the_callers_frame`, and its comment no longer mentions disposing a detachment. No record or knowledge-graph entry named it.
4. **Row 67's "every call site in the repository's C#" was false** (low). Real: the fact read nine named trees, not `tests/` (21 tracked `.cs` files), `spikes/` (8), `samples/` (7), `docs/` (5) or `assets/` (1). **Fixed by widening the scan**, the smaller change that makes the claim true. `RunDetached_is_called_only_at_the_listed_dispatch_points` now walks the repository root as `ProcessGlobalEnvironmentConventionTests` does, less build output, dot directories and nested checkouts (its existing `IsPruned`). Row 67 and the test remarks say so. Mutation `m-l` plants a call in `samples/`: it is KILLED at the head and SURVIVES on the code before the change.
5. **"Work started inside keeps the detachment" was too wide** (medium). Real: a `Task`, `Timer`, cancellation registration or continuation captures the execution context where it is created. So one the caller created and the callback starts or triggers runs in the caller's frame (the re-check's Q1a to Q1d, on both TFMs). Production is not exposed: `AgentBus` creates each subscriber task with `Task.Run` inside the callback, and the convention fact pins that. **Fixed in the records**, which now say *created* inside: the `RunDetached` summary and remarks, row 67 (whose Why adds that a new site must create the work it hands off inside the callback), the CHANGELOG, SPEC-007, the `AgentBus` comment and the `EgressSubjectDetachTests` remarks. The new twin `A_task_or_callback_the_caller_creates_keeps_the_callers_frame_when_RunDetached_starts_or_triggers_it` pins both directions, each decision awaited while the caller's frame is live:
   - a cold task, a timer, a registration and a continuation the caller created and the callback starts or triggers decide at the caller's frame (`Secret`, `LevelTooLow`);
   - `Task.Run` inside the callback decides with no subject (`SystemHigh`), and so do a cold task, timer, registration and continuation created inside it and started or triggered by the caller after it returns.

   It pins runtime behaviour the code already had, so it passed at the twin commit.
6. **The `finally` overwrote a head a caller's filter left** (low). Real: the rethrow's second pass ran `RunDetached`'s `finally` after the caller's filter, and dropped a frame that filter entered, which the flow never disposed. **Fixed:** `try { start(); } catch { Active.Value = caller; throw; } Active.Value = caller;`, with no `finally`. The twin `A_frame_a_callers_exception_filter_enters_stays_on_the_callers_flow_until_the_flow_disposes_it` is the re-check's Z1: after the catch the flow decides at the filter's frame (`Secret`, two frames on the flow), and disposing it goes back to the caller's frame.

**Red first** at `86a33a0f` (the twins alone, on the old code), net8.0, filter `FullyQualifiedName~EgressSubject|FullyQualifiedName~EgressGuard`: **1 failed of 449**, the Z1 twin. Its message was "Expected inside.CurrentBasis to be a match with the expectation because the flow never disposed the frame its filter entered, so it is still the flow's head, but it differs at index 20" (`…lter-frame-caller` where `…lter-frame` was expected). The created-outside twin passed there, as expected. **Green** at `26407e3a`: **449 of 449** on net8.0.

**Testing that round at `cb43f7cf`** (master `ad3d570b` merged), all in the devtest container:
- the filter above: **449 of 449** on net8.0 and **449 of 449** on net10.0;
- the full cert-gate (`bash scripts/run-cert-gate.sh`, net8.0): **2761 of 2761**, skip guard 0 (also 2761 of 2761 at `0ade086d`);
- `Ashlar.Tests.Orchestration` (net8.0): **302 of 302**;
- `Ashlar.Tests.BackgroundAgents` (`~Egress`, net8.0): **14 of 14**;
- `Ashlar.Abstractions` (netstandard2.0, net8.0, net10.0) and `Ashlar.Orchestration`: **0 warnings, 0 errors**;
- the floors, re-measured with `F6` and `F8` at `0ade086d` (the last commit changes only a test comment): **2,135 files, 150 occurrences, 67 docs rows**;
- the repo gates (`scripts/ci/run-repo-gates.sh`): **all 26 passed**. The knowledge graph was regenerated after `git add` and holds 4872 declared facts.

**Mutations that round** (`scripts/mutation-check.sh`, net8.0, the filter above):

| # | Rule | Mutation | Summary line (verbatim) | What went red |
|---|---|---|---|---|
| m-a (head) | A flow leaves a frame only by disposing its own head | `Dispose` restores `Live(_previous)` | `mutation m-a-restore-through-live-at-head: KILLED red=failed:15/449 green=passed:449/449 ref=cb43f7cfaa08a9444733b37dd0b79ef3d491ee9a` | the same 15 as last round: the growth, stickiness, shared-mark (b), three-frame orders, P7, child-task, handed-child, background, awaited, both raised-mark, both around-frames and every-level twins, and the program twin |
| m-i (head) | The catch restores the caller's head before the exception leaves | the catch does `GC.KeepAlive(caller)` and still rethrows | `mutation m-i-run-detached-catch-does-not-restore-at-head: KILLED red=failed:5/449 green=passed:449/449 ref=cb43f7cfaa08a9444733b37dd0b79ef3d491ee9a` | the three exception-filter twins, the Z1 twin and `RunDetached_restores_the_callers_frame_when_the_callback_throws` |
| m-k | Nothing in `RunDetached` runs after a caller's filter | the `finally { Active.Value = caller; }` re-added (the code before this round) | `mutation m-k-run-detached-restores-again-in-a-finally: KILLED red=failed:1/449 green=passed:449/449 ref=cb43f7cfaa08a9444733b37dd0b79ef3d491ee9a` | `A_frame_a_callers_exception_filter_enters_stays_on_the_callers_flow_until_the_flow_disposes_it` (Z1) |
| m-g1 (head) | `RunDetached` puts back the caller's head when the callback returns | the restore after the `try` becomes `GC.KeepAlive(caller);` | `mutation m-g1-run-detached-does-not-restore-on-return-at-head: KILLED red=failed:9/449 green=passed:449/449 ref=cb43f7cfaa08a9444733b37dd0b79ef3d491ee9a` | 9: the D1 probe, the program twin, the AgentBus subscriber and ended-parent publish twins, both task twins, the leave-and-restore, dropped-frame and created-outside/created-inside twins |
| m-l | The call-site scan reads the whole repository | a `// EgressSubject.RunDetached(() => { });` comment planted in `samples/hello-brick/HelloBrick/HelloBrick.cs` | `mutation m-l-scan-reaches-samples: KILLED red=failed:1/449 green=passed:449/449 ref=cb43f7cfaa08a9444733b37dd0b79ef3d491ee9a` | `RunDetached_is_called_only_at_the_listed_dispatch_points` ("Expected sites to be equal to {\"src/Ashlar.Orchestration/Communication/AgentBus.cs x1\"}") |
| m-l (control) | the same mutation at `db80d792`, before the change | the same | `mutation m-l-scan-reaches-samples-before-widening: SURVIVED red=failed:0/447 green=not-run ref=db80d792e43315e685c55793760f978060edadc5` | nothing: the old scan never read `samples/` |

Each KILLED run applied its replacement ("replaced 1 occurrence"), went red, restored with `git status --porcelain` empty, and went green again. m-i now fails five twins where it failed three last round: without the `finally`, the catch is the only restore on the throw path. The m-l control was first aimed at `86a33a0f`, whose baseline has the Z1 twin red, so the script marked it INVALID (`green-not-passing`; its red run failed only that twin). It was rerun at `db80d792`, the previous head, whose scan is the old one.

**Merge resolution** (`0ade086d`):
- SPEC-007 keeps master's text. Its 4.2 bullet now reads "**PR 4.2** (#719, `ad3d570`)", in the merged form of 4.1 and 4.6. 4.4's bullet follows 4.6's and stays "(#716)". The Gaps bullet keeps master's 4.1 and 4.2 wording.
- Row 64 is master's 4.2 text with 4.4's current-label clause. Its floors sentence reads "measured 2,135, 150 and 67 with PR 4.4 merged, after 2,134, 150 and 67 at PR 4.2, 2,133, 149 and 67 at PR 4.6, …; 4.1 adds one occurrence, 4.6 three files, 4.2 one file and one occurrence, and 4.4 one file".
- The `EgressGuardConventionTests` floors comment is master's statement for 4.1, 4.6 and 4.2, followed by 4.4's sentence (`ReadScope.cs`: 2,135 files, 150 occurrences). The lane's stray "SPEC-007 PR 4.1" rewording went back to master's "PR 4.1".
- The Certification count is 131 `.cs` files and 134 entries (`git ls-files`): master's 128 and 131, plus this PR's three test files.
- `ci/egress-inventory.tsv` and `docs/EgressInventory.md` are master's; 4.4 changes neither. Row 64's sums hold: 86 rows, 150 occurrences, 48 guarded, 33 with nothing unguarded, 35 `Exempt:`, 10 `Factory`, 4 `Upstream:` and 4 `Governance`.
- The knowledge graph was regenerated, not hand-merged.

### Final rule (tested at `4de6bc75`; master `bbc5d714` (#717, PR 4.1) merged in. The three re-check rounds after it move the head to `db80d792`, `cb43f7cf`, then `91cf8d74`)

Commits since the fourth round's head `e648ac4d`:
- `4484bb52` holds the no-skip twins alone.
- `e761b070` is no-skip.
- `5f8868b6` holds the `RunDetached` twins alone.
- `bcb82888` is `RunDetached`.
- `8499c433` holds the records and the knowledge graph.
- `36abb59b` pins reads after the callback returns and adds the async-helper wording.
- `04adf42e` merges master `bbc5d714` (#717, PR 4.1). Conflicts: row 64, the floors comment, the knowledge graph (see "Records").
- `dcf1dcca` holds the re-check's records: a frame's `using` never spans a `yield return`, and the re-measured floors.
- `4de6bc75` is a precision fix to that wording: after a `yield return` the consumer's frames need not hold the frame's mark.

All counts below use the filter `FullyQualifiedName~EgressSubject|FullyQualifiedName~EgressGuard`.

- **Red first, no-skip.** The twins were committed alone on the flow-local code (`4484bb52`). net8.0: **6 failed of 401**. The failing twins were:
  - the three-frame orders twin (restore-path lengths 2, 1, 0, where the rule says 3);
  - the own-flow parent twin ("Expected … Secret … but found Public");
  - the stay-inside twin ("Expected RestorePathLength() to be 1 … but found 0");
  - the task started after an out-of-order dispose (P7: "Expected late.Current to be Secret … but found Public");
  - the detachment-in-the-middle twin;
  - the four-frame shapes twin (2,843 failures).
- **No-skip alone** (`e761b070`), net8.0: **400 of 401**. The one failure is the D1 probe: "Expected late.Current.Dominates(callerLabel) to be True because a frame entered afterwards is inside the live caller frame, which this flow never disposed, so it never decides below it; it decided subject:detach-probe-late at Public, but found False". That is the write-down this round closes.
- **Red first, `RunDetached`.** The twins were committed alone on the no-skip code (`5f8868b6`). net8.0: **10 failed of 401**.
  - Eight fail with "Expected method not to be <null> because EgressSubject.RunDetached is the internal, raise-only way to run work with no subject": the member does not exist yet. Reflection-based twins compile either way, so they are red at run time instead of at compile time. The eight are the D1 probe, the program twin, the dropped-frame, throw, two task twins, the callback twin and the read-scope twin's detached case.
  - The two convention facts fail on "Expected sites to be equal to {"src/Ashlar.Orchestration/Communication/AgentBus.cs x1"}" and "Expected call to be greater than or equal to 0 … but found -1".
  - The shared-mark twin (b) pins behaviour that no-skip already had, and it passed there.
- **Green:** **401 of 401** on net8.0 at `8499c433`. At `4de6bc75`: **443 of 443** on net8.0 and **443 of 443** on net10.0 (401 before the merge; master's 4.1 adds 42 `EgressGuard*` tests under the filter).
- **The program twin agrees with an independent model.** `Every_program_of_frames_and_detached_callbacks_on_one_flow_leaves_only_the_head_it_disposes_and_never_writes_down` runs 2,092 programs, each on its own copy of the test's flow. Each program has an enclosing frame, up to two more frames and up to two callbacks, nested or in turn and never empty; every frame is disposed in any order, inside or outside any callback. After every step the twin compares the decision and the restore path with a C# model. Outside every callback, it also checks that the flow never decides with no subject or below a live frame it entered there. At the end of each program, it checks every frame's mark, and that no outside frame holds an inside frame's mark. A separate Python model of the rule (`r7/model.py` in the lane's scratch space) generates the same programs in the same order. It predicted 2,092 programs and 2,474 frames left on the flows, all of them frames entered outside the callbacks and disposed out of order. The C# twin pins both numbers and passes. Over the larger space of three frames and two callbacks (43,687 programs, 508,767 steps), the Python model finds no write-down and no crossing.
- **Multi-flow model of the final rule.** The re-check's write-down hunt (`wd.py`) was rerun with its disposable detachment `D` replaced by callback brackets: `C` opens a callback and `U` returns from it, restoring the caller's head exactly. A detachment cannot be disposed, and a task forked inside a callback keeps the detachment. The other operations are as before: `E`, `X`, `T`, `O`, `A` (dispose inside an awaited async helper), `L`, and the `R`/`Q` read scopes. It finds **no write-down** in 1,900,357 sequences (N=7, up to two flows, ops `ACELO`), 431,535 (N=6, up to three flows), 506,219 (N=6, two flows, with read scopes `QR`) and 11,413,646 (N=8, two flows, ops `CELOT`). The re-check ran the same bounds with the disposable detachment and found the D1 class at 4 to 7 operations. The required label is the in-order (lexical) one, as before, and a frame entered inside a callback stays the callback's. The same harness with a broken `U` finds write-downs within 3 to 6 operations, so the check has teeth: a `U` that does not restore finds `C@main ; E@main ; U@main`, and a `U` that restores `Live(caller)` finds `E@main ; C@main ; T@main ; X0@t1 ; U@main ; E@main`.
- **Full cert-gate** (`bash scripts/run-cert-gate.sh`, net8.0): **2643 of 2643**, skip guard 0 (matching `scripts/cert-gate-skipped.baseline`).
- **`Ashlar.Tests.Orchestration`** (net8.0 only): **302 of 302**.
- **`Ashlar.Tests.BackgroundAgents`**, filter `FullyQualifiedName~Egress`, net8.0: **14 of 14**.
- **Build:** `Ashlar.Abstractions` on netstandard2.0, net8.0 and net10.0: **0 warnings, 0 errors**. `Ashlar.Orchestration`: **0 warnings, 0 errors**.
- **Repo gates:** `scripts/ci/run-repo-gates.sh`: **all 26 passed**. The knowledge graph is current: it was regenerated after `git add` and holds 4822 declared facts.
- **Records recounted after the merge:**
  - `Tests/Certification` is 128 `.cs` files and 131 entries (`git ls-files`). Master has 125 and 128; 4.1 added no file there, and this PR adds three.
  - `ci/egress-inventory.tsv` is master's (4.1's): 85 rows, 149 occurrences, 48 guarded, 33 with nothing unguarded. Row 64 says so.
  - The `EgressGuardConventionTests` floors comment is one statement for 4.1 and 4.4, re-measured in the devtest container at `04adf42e` (`F6`/`F8` with detailed output): **2,131 files scanned, 149 occurrences** (http.new 19, http.param 22, http.register 12, sdk.client 9, socket 2, process 51, door 11, telemetry 2, store 14, chat.register 7, banned 0), **67 docs rows**. Row 64's floor sentence now ends "4.1 adds one occurrence, 149, and 4.4 one file, 2,131".

**Mutation checks for the final rule.** Each was run with `scripts/mutation-check.sh` on net8.0 with the filter above. Every one was KILLED:

| # | Rule | Mutation | Summary line (verbatim) | What went red |
|---|---|---|---|---|
| m-a (head) | A flow leaves a frame only by disposing its own head (no skip), at `4de6bc75` | `Dispose` restores `Live(_previous)` (reintroduces going past a disposed frame) | `mutation m-a-restore-through-live-at-head: KILLED red=failed:15/443 green=passed:443/443 ref=4de6bc7513d9982c394550f9b3dbbc71c2e85c96` | 15 twins, among them the growth twin (`A_flow_that_disposes_its_frames_out_of_order_stays_inside_the_outer_frame_it_disposed`), the stickiness twin (`A_parent_frame_disposed_out_of_order_on_its_own_flow_still_holds_a_task_started_inside_it`), the shared-mark twin (b), the three-frame orders twin, P7 and the program twin |
| m-a | A flow leaves a frame by going back to exactly its `_previous`, disposed or not | `Dispose` restores `Live(_previous)` | `mutation m-a-restore-through-live: KILLED red=failed:15/401 green=passed:401/401 ref=36abb59b86155479759387ae941cda9e7a77d263` | 15 twins of a flow or task that stays inside a frame it did not leave: the out-of-order stay twin (a), the shared-mark twin (b), the three-frame orders, the own-flow parent, P7, the child-task, handed-child, background, awaited, both raised-mark, both around-frames and every-level twins, and the program twin |
| m-b | `Resolve` joins from the chain's head | the join starts at `Live(chain)` | `mutation m-b-resolve-from-live-chain: KILLED red=failed:3/401 green=passed:401/401 ref=36abb59b86155479759387ae941cda9e7a77d263` | the three twins of a flow with no frame of its own that reads a later raise of a disposed frame's mark (X1, S1, S2) |
| m-c | `ObserveInto` raises every frame up to a detachment, disposed or not | the walk stops at the first disposed frame | `mutation m-c-observe-into-stops-at-a-disposed-frame: KILLED red=failed:7/401 green=passed:401/401 ref=36abb59b86155479759387ae941cda9e7a77d263` | the shared-mark twin (b), the sibling, frameless `Observe` and unreported-read twins, the three-frame orders, the every-level twin and the program twin (7) |
| m-d | A frame is marked disposed only after its mark has left (three states) | `Volatile.Write(ref _state, Disposed)` moved before `ObserveInto` | `mutation m-d-disposed-before-observing-outward: KILLED red=failed:1/401 green=passed:401/401 ref=36abb59b86155479759387ae941cda9e7a77d263` | `A_task_with_no_frame_of_its_own_never_decides_below_a_frame_while_it_is_being_disposed` (30 of 60 disposals: "named the enclosing frame before that frame held the parent's mark") |
| m-e | `Observe` captures the chain head, disposed or not | `Observe` passes `Frame.Live(Active.Value)` | `mutation m-e-observe-from-live-head: KILLED red=failed:2/401 green=passed:401/401 ref=36abb59b86155479759387ae941cda9e7a77d263` | the shared-mark twin (b) and the frameless `Observe` sibling twin |
| m-f | `BeginRead` captures the chain head, disposed or not | `BeginRead` captures `Frame.Live(Active.Value)` | `mutation m-f-begin-read-from-live-head: KILLED red=failed:1/401 green=passed:401/401 ref=36abb59b86155479759387ae941cda9e7a77d263` | `An_unreported_read_in_a_task_with_no_frame_after_the_parent_ended_still_reaches_a_sibling_task` |
| m-g1 | `RunDetached` puts back the caller's head | the `finally` does `GC.KeepAlive(caller)` instead of restoring | `mutation m-g1-run-detached-does-not-restore: KILLED red=failed:9/401 green=passed:401/401 ref=36abb59b86155479759387ae941cda9e7a77d263` | 9: every `RunDetached` twin, the D1 probe, the program twin and both AgentBus twins |
| m-g2 | `RunDetached` puts back exactly the caller's head, not the nearest live frame | the `finally` restores `Frame.Live(caller)` | `mutation m-g2-run-detached-restores-the-live-caller: KILLED red=failed:2/401 green=passed:401/401 ref=36abb59b86155479759387ae941cda9e7a77d263` | the program twin and `A_publish_from_a_task_inside_an_ended_parent_frame_keeps_the_parent_for_a_frame_entered_after_it` |
| m-h | `AgentBus` dispatches inside `RunDetached` | the dispatch block runs as `new Action(() => { … }).Invoke()` (one multi-line replacement) | `mutation m-h-agentbus-not-detached: KILLED red=failed:3/401 green=passed:401/401 ref=36abb59b86155479759387ae941cda9e7a77d263` | `An_AgentBus_subscriber_runs_with_no_subject_whatever_the_publisher_has_read` and both convention facts |
| b4 | (rerun) A scope observes into the chain it began on | `Report`'s late branch and `Dispose` call `EgressSubject.Observe(...)` (keeping `GC.KeepAlive(_chain)`) | `mutation b4-read-scope-ambient-observe: KILLED red=failed:1/401 green=passed:401/401 ref=36abb59b86155479759387ae941cda9e7a77d263` | `A_read_ended_on_a_flow_without_its_chain_still_raises_the_chain_it_was_begun_on` |
| b5 | (rerun) A late report is never lost | the `_ended` branch in `Report` deleted | `mutation b5-late-report-dropped: KILLED red=failed:2/401 green=passed:401/401 ref=36abb59b86155479759387ae941cda9e7a77d263` | `A_report_after_the_read_ended_is_never_lost`, `A_read_ended_on_a_flow_without_its_chain_still_raises_the_chain_it_was_begun_on` |

All 11 were killed at `36abb59b`; each red run listed the failing tests above, and each green run passed 401 of 401. m-a, m-b and m-e to m-f re-aim the earlier b3/c6, e1, c3 and c4 at the no-skip code (there is no `Outer` or `Unwind` any more). m-c is the earlier b2 on the head-first walk, and m-d is the reorder form of the earlier d5. The old m7 to m9, c5, d1 to d4, d6, d7 and e2 have no target: the `IDisposable` detachment, its restore target, the `Skips` record and `Unwind` are deleted. The `m-a (head)` line reran m-a at `4de6bc75`, the head before the final re-check: 443 tests under the filter after the merge, which brings 4.1's `EgressGuard*` twins, and the same 15 went red. Between `36abb59b` and `4de6bc75`, `EgressSubject.cs` changed only in XML doc comments, and `ReadScope.cs` and `AgentBus.cs` did not change, so every mutated statement was the same code there. The final re-check then added the catch in `RunDetached` (m-i and m-j below); no other mutated statement changed. Each run applied its mutation ("replaced 1 occurrence"), went red, restored with `git status --porcelain` empty, and went green again. `m-a` is the mutation the re-check asked for (`Live(Outer)` in its terms): it reintroduces a skip and is killed by the growth and stickiness twins.

### Final re-check (previous round): exception filters, the leaving rule's wording, and master #718

Commits after `4de6bc75`:
- `766427b9` holds the exception-filter twins alone.
- `0aa79155` is the fix and the records.
- `db80d792` merges master `3196ba11` (#718, PR 4.6). It was the pushed head of that round.

Each finding, and what was done (none changes the decided rule):
1. **A caller's exception filter ran at the callback's frame** (medium). Real. .NET handles an exception in two passes: the first runs every exception filter up the stack, and only the second unwinds and runs `finally` blocks. With the restore only in `RunDetached`'s `finally`, a caller's `catch … when (…)` filter ran while the flow's head was still the detachment or a frame the callback entered. That filter is the caller's own code, inside a caller frame it entered and has not disposed. **Fixed** in `0aa79155`: `catch { Active.Value = caller; throw; }` ahead of the `finally`. The catch matches every exception, so the first pass stops at `RunDetached`. The callback's own filters have run detached by then, and its `finally` and `using` blocks run detached as the stack unwinds to the catch. The caller's head is back before the rethrow's first pass reaches a filter of the caller. Neither model (the 2,092-program twin and `wd.py`, whose `C`/`U` ops bracket a callback) had a step for code that runs between a throw and the restore, which is why both came back clean. Production was not exposed at `4de6bc75`: `AgentBus`'s callback only calls `Task.Run`, and no production code enters a frame yet. Twins, committed alone at `766427b9`:
   - `A_callers_exception_filter_runs_in_the_callers_frame_when_the_callback_throws` (the re-check's P1, plus P4's nested form: a frame entered inside an outer callback, around a nested callback that throws). Each filter decides `subject:<caller>` at `Secret` (`LevelTooLow`), with exactly the caller's frames on the flow.
   - `A_read_in_a_callers_exception_filter_raises_the_callers_frame` (P2 and P2b). `Observe(Confidential)` in one filter, then a read scope reporting `Secret` in another, each raise the caller's mark, and the caller then decides `Secret`.
   - `The_callbacks_filters_and_finally_blocks_run_detached_and_only_then_the_callers_filter_in_the_callers_frame` (P7). The order is callback filter, callback `finally`, caller filter. The first two decide `no-subject`, and the caller's filter decides at the caller's frame.

   **Red first** at `766427b9`, net8.0, filter `FullyQualifiedName~EgressSubjectDetachTests`: **3 failed of 14**. The messages were "Expected callerMark.Current to be Confidential because what the caller's filter observes is the caller's read, but found Internal"; "Expected order to be equal to {"callback filter", "callback finally", "caller filter"} … but {"callback filter", "caller filter", "callback finally"} differs at index 1"; and, for P1, a basis naming the callback's frame (`…ilter-callback`) where the caller's (`…ilter-caller`) was expected. **Green** at `0aa79155`: 446 of 446 on net8.0 and on net10.0. The records now say that the caller's frame is back before any exception filter of the caller runs: the `RunDetached` summary and a new remarks paragraph, row 67 (the rule and its Why column), the `EgressSubjectDetachTests` remarks, the CHANGELOG entry and the SPEC-007 4.4 line.
2. **The leaving rule said "innermost" where the code says "head"** (medium). Real. `Dispose` moves the flow only when `ReferenceEquals(Active.Value, this)`, and the head can be a disposed frame (the stuck state of limit (a)), while the same remarks said a disposed frame is never "the innermost one". **Fixed** in `0aa79155`. That round's wording was "a flow leaves a frame only by disposing its head, the flow's own frame (which may be a disposed one, so not always its innermost live frame)"; this round's finding 2 rewords it again. It reads that way in the `EgressSubject` remarks ("Leaving a frame", "Disposed frames still count"), the `Enter` `<returns>`, the `RunDetached` remarks, row 65 (the rule and its Why column), the CHANGELOG entry, the SPEC-007 4.4 line and the `EgressSubjectNestingTests` remarks. The remarks now say "a disposed frame is never the innermost live one", and so do seven test messages in `EgressSubjectNestingTests`; two in `EgressGuardDecisionTests` say "innermost live" too. The basis wording keeps "innermost live".
3. **The drift-audit section misstated two records** (low). Real. **Fixed** under "Records" below: `PublicAPI.Unshipped.txt` gains six entries in this PR, and the Certification count paragraph moves.
4. **Row 67 misdescribed its probes** (low). Real. In the D1 probe, `inside` runs from 0 to 3, so the 24 programs dispose the three frames in every order, 0 to 3 of them before the callback returns. The generator also enumerates up to two more frames and up to two callbacks. **Fixed** in row 67 and in the `EgressSubjectDetachTests` remarks.
5. **A test name the rule made false** (low). Real. **Fixed:** `A_frame_disposed_from_another_flow_stops_counting_on_this_one` is now `A_frame_disposed_from_another_flow_no_longer_names_this_flows_decisions`. No record or knowledge-graph entry named it.

**Testing that round**, all in the devtest container:
- At `0aa79155`:
  - the filter `FullyQualifiedName~EgressSubject|FullyQualifiedName~EgressGuard`: **446 of 446** on net8.0 and on net10.0;
  - the full cert-gate (net8.0): **2646 of 2646**, skip guard 0;
  - `Ashlar.Tests.Orchestration`: **302 of 302**;
  - `Ashlar.Abstractions` on netstandard2.0, net8.0 and net10.0: 0 warnings, 0 errors.
- At `db80d792`, the pushed head, with master `3196ba11` merged:
  - the filter: **447 of 447** on net8.0 and on net10.0 (4.6 adds one `EgressGuard*` test);
  - the full cert-gate: **2751 of 2751**, skip guard 0;
  - `Ashlar.Tests.Orchestration`: **302 of 302**;
  - `Ashlar.Tests.BackgroundAgents` (`~Egress`): **14 of 14**;
  - `Ashlar.Abstractions` and `Ashlar.Orchestration`: 0 warnings, 0 errors;
  - the floors, re-measured with `F6` and `F8`: **2,134 files, 149 occurrences, 67 docs rows**.
- The repo gates passed **26 of 26**. The knowledge graph was regenerated after `git add` and holds 4862 declared facts.

**Mutations that round** (`scripts/mutation-check.sh`, net8.0, the filter above). Each was KILLED:

| # | Rule | Mutation | Summary line (verbatim) | What went red |
|---|---|---|---|---|
| m-i | `RunDetached` restores the caller's head before the exception leaves | the catch does `GC.KeepAlive(caller)` instead of restoring, and still rethrows | `mutation m-i-run-detached-catch-does-not-restore: KILLED red=failed:3/446 green=passed:446/446 ref=0aa79155673774feb4d8bd73b05bb97f47445b0e` | the three exception-filter twins |
| m-i (head) | the same, at the pushed head | the same | `mutation m-i-run-detached-catch-does-not-restore-at-head: KILLED red=failed:3/447 green=passed:447/447 ref=db80d792e43315e685c55793760f978060edadc5` | the three exception-filter twins |
| m-j | the restore happens in a catch, not only in the `finally` | the whole catch block deleted (the code before this round) | `mutation m-j-run-detached-restores-only-in-finally: KILLED red=failed:3/446 green=passed:446/446 ref=0aa79155673774feb4d8bd73b05bb97f47445b0e` | the three exception-filter twins |

Each run applied its replacement ("replaced 1 occurrence"), went red, restored with `git status --porcelain` empty, and went green again. The earlier m-g1 and m-g2 mutated the `finally`'s restore, which still runs on the normal path; `Active.Value = caller;` now occurs twice, so those two were not rerun.

**Merge resolution** (`db80d792`):
- Row 64 is master's 4.6 text (the mode paragraph and the two mode test classes), with 4.4's current-label clause. Its floors sentence now reads "measured 2,134, 149 and 67 with PR 4.4 merged, after 2,133, 149 and 67 at PR 4.6, …; 4.1 adds one occurrence, 4.6 three files and 4.4 one file".
- The `EgressGuardConventionTests` floors comment is one statement for 4.1, 4.6 and 4.4: 2,134 files and 149 occurrences.
- The Certification count is 130 `.cs` files and 133 entries (`git ls-files`): master's 127 and 130, plus this PR's three test files.
- SPEC-007 has master's "**PR 4.6** (#718)" bullet, then 4.4's. The 4.4 bullet's D9 sentence now says that 4.6's convention fact (`Only_AddAshlar_and_the_reset_seam_reach_the_process_egress_state`) reads every source file, Orchestration's included.
- `ci/egress-inventory.tsv` is master's: 4.6 moved only one note. Row 64's sums still hold: 85 rows, 149 occurrences, 48 guarded.

### Re-check of the no-skip head (an earlier round)

The re-check ran against `e648ac4d`, the PR head before this push, and against the local `36abb59b`. Each finding, and what was done:
- **The PR head still had the flow-local `Skips` rule** (two findings, high). Probes H1 and H2 decided `Public` inside an undisposed outer frame at `e648ac4d`, and WD_A to WD_E and WD_G decided `Public` under a `Secret` caller. The same H1 and H2 pass at `36abb59b` on net8.0 and net10.0. **Fixed by pushing** `4484bb52` to `36abb59b`. That range deletes `Skips`, `Skip`, `Unwind` and the stale `(Unwind)` comment. `Dispose` ends with `if (ReferenceEquals(Active.Value, this)) Active.Value = _previous;`.
- **D1: an `IDisposable` detachment disposed out of order writes down** (high). **Fixed by construction**, the re-check's option 1: the callback-shaped `RunDetached` (`bcb82888`) replaces `Detach`. Accepted consequence (2) of the integrator's decision ("a task handed a detachment scope that disposes it goes back to the caller's frame") no longer applies, because no caller holds a detachment. The records state the replacement: work created inside the callback keeps the detachment for its whole life (row 67, the `RunDetached` remarks; narrowed from "started" this round, finding 5).
- **A frame's `using` across a `yield return` of an async iterator** (medium; probes O1 and O1b at `36abb59b`, identical on net8.0 and net10.0). The finding is real. Decisions 2 and 3, still inside the runner's `using` block, were made at the consumer's frame, which lacks the runner's `R2-SECRET`. With no consumer frame, a `Secret` read after the yield never reached the subject's mark. This is not a change to the rule: `AsyncLocal` does not carry a value set inside an iterator body past a `yield return`. **Fixed in the records** (`dcf1dcca`, `4de6bc75`), next to the async-helper exclusion: the 4.5 obligation in SPEC-007, the `EgressSubject` "Leaving a frame" remark, a new `Enter` remark, row 65's Why, and the CHANGELOG entry. Each one says the `using` never spans a `yield return`, and that the frame is entered around the `await foreach` or once for each stretch between yields. WD_H and O2 (a frame entered in an async helper) stay red by design: the records exclude that pattern.
- **Records still described the flow-local rule** (medium). **Fixed** in `8499c433` and `36abb59b`: the one-line invariant, known limits (a) and (b), and the 4.5 obligation. `git grep` at the head finds no "goes past", `Skips`, `Unwind`, `_unwoundBy` or `Detach()` in the code, the tests or the records.
- **Twins pinned the opposite rule** (medium). **Fixed** in `4484bb52`. The six twins assert the no-skip outcomes. The growth twin pins lengths 1, then `i + 2` per iteration, then 53, and the label sticking. The "unwound frame" twin is now `A_frame_disposed_out_of_order_keeps_no_frame_entered_inside_it_reachable`, a GC check that is not vacuous under no-skip. Mutation `m-a` (`Active.Value = Live(_previous)`, the reintroduced skip) is killed by the growth, stickiness and shared-mark twins among 15. The KEEP mutations were rerun at the final rule's code (table below).
- **F7: a stuck frame's reach crosses flows** (medium). **Fixed** in `5f8868b6` and `8499c433`. Known limit (b) is in the class remarks, row 65, the CHANGELOG, the SPEC-007 4.5 obligation and this body. It is pinned by `What_a_flow_reads_after_an_out_of_order_dispose_raises_the_subjects_shared_mark_for_its_other_sessions`.
- **Stale live PR body** (medium). **Fixed:** this body replaces it, PATCHed after the push.
- **Not mergeable with master** (medium). **Fixed** by `04adf42e`, which merges `bbc5d714`. The floors comment is one re-measured statement: 2,131 files and 149 occurrences. The Certification count stays 128 and 131.
- **Row 64 dropped "up to a detachment"** (low). **Fixed** in the merge resolution of row 64: "…live or disposed, up to a detachment (…) or, with no live frame or under a detachment, `SystemHigh` (`no-subject`)".
- **SPEC-007 "**4.4** (this PR)" form** (low). **Fixed:** the line is a top-level "**PR 4.4** (#716)" bullet after master's "**PR 4.1** (#717)".

### History (superseded rounds, condensed)

- **Phase A** (`5f56b8f`) introduced nesting, `Observe`, read scopes and an `IDisposable` `Detach`. Twins were red first: 11 of 148 failed before the change, and 161 of 161 passed after it. Mutations m1 to m9 were all killed: the nesting join, outward observation, `Observe`'s reach, `Observe` never satisfying a scope, unreported and throwing reads at `SystemHigh`, `Detach` leaving every frame, `AgentBus` dispatching detached, and a disposed detachment never falling back. Cert-gate 2566 of 2566.
- **Phase B** (`96672663`) was the integrator's decision 2, fail closed. Disposed ancestors count in `Resolve`. `Dispose` restores exactly the frame it was entered under. `ObserveInto` raises disposed frames. The read-scope twin covers flows without the begin chain. Red first: 2 of 25, then 2 of 378. Mutations b1 to b5 were killed (b4 is the ambient read scope, b5 the dropped late report; both are rerun above). Cert-gate 2569. The side effect was that one flow's out-of-order disposal grew its chain.
- **Second check** (`ef4da122`) bounded the chain with a record on the frame (`_unwoundBy`). The third round showed that record is a write-down: any flow whose head was the recorded frame went past the parent. Cert-gate 2574; mutations c1 to c6 were killed.
- **Third round** (`bc563d0c`) moved the record to the disposing flow (`Skips`), added the three-state dispose and fixed CS3. Red first: 11 of 397. The integrator's round-3 rule ("go past only a subject frame whose `_previous` is a subject frame") was not adopted, because of its model write-down at 8 operations. Cert-gate 2588; mutations d1 to d7 were killed.
- **Fourth round** (`e648ac4d`) made `Resolve` join from the chain's head, so a flow with no frame of its own reads a later raise of a disposed frame's mark (X1, S1, S2). Red first: 3 of 400. It added the four-frame shapes twin and the disposing-window twin's basis check. Cert-gate 2591; mutations e1 and e2 were killed, with d5 re-aimed. The no-skip re-check then found detachment write-downs in the flow-local record (WD_A to WD_E and WD_G), which led to the integrator's final decision above.
- **What this round removed:** the `Skips` record and `Unwind` (no-skip, `e761b070`), and the `IDisposable` detachment with its restore target and `Outer` (`RunDetached`, `bcb82888`). Its twins went with them:
  - `A_detachment_disposed_from_another_flow_restores_nothing_there` and `A_task_started_under_Detach_that_disposes_it_goes_back_to_the_callers_frame_there_only`: neither can be written against a callback;
  - the detachment-in-the-middle twin and the four-frame shapes twin, which the 2,092-program twin replaces.
- **What it kept:** the twins that pin the subject-frame rule, now asserting no-skip outcomes. The out-of-order flow stays in the outer frame, with exact lengths. A task started after an out-of-order dispose stays inside the frame it did not dispose (P7). The other twins are unchanged.

### Records (for the drift audit)

- `ci/cert-gate-assertions.md`:
  - Row 64, merged with master's 4.1, 4.6 and 4.2 text. Its current-label clause says "live or disposed, up to a detachment" and "with no live frame or under a detachment, `SystemHigh`", as `Resolve` does. Its floors sentence reads "measured 2,135, 150 and 67 with PR 4.4 merged, after 2,134, 150 and 67 at PR 4.2, …; 4.1 adds one occurrence, 4.6 three files, 4.2 one file and one occurrence, and 4.4 one file". Its TSV sums are master's (86, 150, 48).
  - Row 65 states the frame rule: a flow leaves a frame only by disposing its own head while that head is undisposed, and goes back to exactly the frame it was entered under, disposed or not, and a flow whose head was already disposed never leaves it. It states the out-of-order outcomes: 13 frames over the six orders, one frame per loop iteration, and the shared-mark raise. Its Why column states the reason, the two known limits and the 4.5 obligation, including the async-helper and async-iterator exclusions. The limits never arise on the flow that entered a frame and disposed it in order, but work created and started inside the frame keeps it; work keeps the frames of the flow where it captured the execution context (a `Task`, `System.Threading.Timer`, registration or continuation where it is created, a `Thread` or `System.Timers.Timer` where it is started), so what work created before the frame and started inside it reads never reaches the frame's mark.
  - Row 66 says "a callback run detached" where it said "a detachment".
  - Row 67 is restated for `RunDetached`. It covers:
    - the callback, and the restore before any exception filter of the caller runs (the callback's filters and `finally` blocks stay detached);
    - a frame a caller's filter enters, which stays on the caller's flow;
    - the dropped frame;
    - work created and started inside the callback, which keeps no subject, while a task, timer, registration or continuation the caller created and the callback starts or triggers keeps the caller's frame, and a thread or `System.Timers.Timer` keeps the frame of the flow that starts it;
    - that `RunDetached`'s compiled body writes the flow's frame only inside a try that catches everything or in that catch;
    - the 24-program D1 probe and the 2,092 programs, with 2,474 frames left;
    - the convention fact, over every tree but build output, dot directories and nested checkouts.

    Its Why column explains how an `IDisposable` detachment was a write-down, why a restore only in a `finally`, or one repeated in a `finally`, would be one, that a write of the flow's frame outside that try and its catch leaves a window for an asynchronous abort, and that a new site must create and start the work it hands off inside the callback.
  - The Certification count paragraph moves from master's 128 `.cs` files and 131 entries to 131 and 134, as of 2026-10-06 (this PR adds three test files), and its history gains "then 128".
- `CHANGELOG.md`: the Unreleased/Added entry "Egress subject frames nest monotonically, and reads are scoped". It carries the leaving rule (its own head, while undisposed; back to the frame it was entered under, disposed or not) and the known limits, scoped to the flow, with work created and started inside a frame keeping it, and a `Task`, `System.Threading.Timer`, registration or continuation created before the frame never reaching its mark. It also states `RunDetached`, which restores before any exception filter of the publisher, keeps work created and started inside the callback detached, and leaves a task, timer, registration or continuation the publisher created, or a thread or `System.Timers.Timer` the publisher starts, in the publisher's frame. Finally, it says how a frame must be entered: never inside an async helper or across a `yield return` of an async iterator. The 3a entry's current-label sentence points to it.
- `docs/specs/SPEC-007-security-labels-and-reference-monitor.md`:
  - The 4.4 line is a top-level "**PR 4.4** (#716)" bullet after master's 4.1, 4.2 and 4.6 bullets. It states the frame rule and `RunDetached`, which puts the publisher's frame back when the callback returns or throws, before any exception filter of the publisher runs. Work created and started inside the callback keeps no subject; a task, timer, registration or continuation the publisher created keeps its frame, and a thread or `System.Timers.Timer` captures where it is started. The D9 amendment names `RunDetached`; 4.6's convention fact reads every source file, Orchestration's included.
  - The 4.2 line, master's, now reads "**PR 4.2** (#719, `ad3d570`)".
  - The 4.5 plan line carries the obligation. Every production `Enter` is a `using` on the entering flow, never inside an async helper, disposed in order, and never spanning a `yield return` of an async iterator (the streaming chat clients are such iterators). Work a runner creates and starts inside its frame keeps that frame and raises the subject's shared mark, and what work created before the frame (a cold task, continuation, timer or registration) or a thread started after the `using` reads never reaches the frame's mark, so a runner creates and starts the work it reads through inside its `using` block.
- `src/Ashlar.Orchestration/Communication/AgentBus.cs`: the dispatch comment (the tasks are created inside the callback; a task created before it would keep the publisher's frame).
- XML docs:
  - the `EgressSubject` class remarks: "Leaving a frame" (its own head, while undisposed; work created and started inside), "Disposed frames still count", and "Known limits (fail closed)", scoped to the flow, with where each kind of work captures the flow;
  - the `RunDetached` summary and remarks: work created and started inside, and where each kind of work captures the flow; the exceptions paragraph, with nothing running after a caller's filter and both writes inside the `try`;
  - the `Enter` `<returns>` and `<remarks>`;
  - the `Detachment`, `Dispose` and `RunDetached` try and catch code comments.
- Test-class remarks:
  - `EgressSubjectNestingTests`: "Leaving a frame", and the `FlowModel` summary;
  - `EgressSubjectDetachTests`: the filter's frame stays, work created and started inside, threads and `System.Timers.Timer`s, the IL fact, and the scan's reach;
  - `EgressSubjectReadScopeTests`: "a callback run detached".

  Two tests are renamed. `EgressGuardDecisionTests.A_frame_disposed_from_another_flow_stops_counting_on_this_one` became `A_frame_disposed_from_another_flow_no_longer_names_this_flows_decisions` last round. `EgressSubjectDetachTests.A_flow_that_disposes_its_own_detachment_out_of_order_never_decides_below_the_callers_frame` became `A_callback_that_returns_with_frames_it_entered_undisposed_never_leaves_the_flow_below_the_callers_frame` this round.
- `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressGuardConventionTests.cs`: the floors comment, master's statement for 4.1, 4.6 and 4.2 plus 4.4's sentence (2,135 files, 150 occurrences, re-measured at `0ade086d`). The floors themselves are unchanged.
- `docs/knowledge-graph.{json,md}`, regenerated after `git add`.
- `src/Ashlar.Abstractions/PublicAPI.Unshipped.txt`: this PR adds six entries, `EgressSubject.Observe`, `EgressSubject.BeginRead`, `ReadScope` and its `Report`, `Complete` and `Dispose`. `RunDetached` is internal and not listed.
- `src/Ashlar.Abstractions/Ashlar.Abstractions.csproj`: `InternalsVisibleTo Ashlar.Orchestration`.
- Unchanged by this PR: `docs/EgressInventory.md` and `ci/egress-inventory.tsv` (both as on master).

### Testing strategy (blast radius)

See [Testing strategy pivot v1](docs/architecture/TestingStrategyPivot-v1.md).

This PR changes `Ashlar.Abstractions` (one type rewritten, one added) and one statement block in `Ashlar.Orchestration`, plus tests. There are no DI composition, host or routing changes.

- [x] Focused tests in the touched area (at `91cf8d74`): the frame-semantics classes and `EgressGuardDecisionTests` on both TFMs, the full cert-gate filter on net8.0, `Ashlar.Tests.Orchestration` (net8.0, its only TFM), and the BackgroundAgents egress twins.
- [ ] `make kernel-coverage-gate`: runs in CI as `kernel-coverage`.
- [ ] `make kernel-gate`: no kernel hosting, pipeline or profile change.
- [ ] `make test-prod-style`: no production DI, API or routing change.

## Checklist

- [x] Repo gates pass locally (26 of 26). The cert-gate passes in the devtest container on net8.0 (2763 of 2763 at `91cf8d74`), and the touched classes pass on net8.0 and net10.0 (451 of 451 and 451 of 451).
- [x] Documentation updated: CHANGELOG, SPEC-007 status, and `ci/cert-gate-assertions.md`.
- [x] No `TODO` or `NotImplementedException`.
- [x] Breaking changes: none. The change is additive public API in `Ashlar.Abstractions` (Unshipped). The nesting change alters the label a nested frame decides at, which only tightens, and no production code nests frames.

## Release

- [x] Not a versioned release; skip.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01NNv8ZAWjwkJYwgB4yFMLBs
