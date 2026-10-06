# 4.5 read-frame model check: the open read scope decides SystemHigh

*Scout deliverable, 2026-10-06, read-only against master `de41a8ac` (host checkout `/home/user/Ashlar`). Python only;
nothing in the repository was edited, no `dotnet`, no container, no git state touched. Every claim below cites
`file:line` on master as read; anything not verified by reading is marked **[unverified]**.*

Files in this folder (`scratchpad/model-4.5/`):

| File | What |
|---|---|
| `frame-model-readscope.py` | the r7 no-skip model (`ws/frame-model-wd-callback.py`, the model whose counts the 4.4 body quotes) extended with the read frame; variants `correct`, `settled`, `B1`-`B4` |
| `frame-model-pins-readscope.py` | `ws/frame-model-pins.py` extended with the read frame: re-computes the 4.4 pins (unchanged) and gives candidate exact counts for 4.5 twins |
| `logs/*.log` | every run quoted here, verbatim; `gen-report.py` builds the tables in section 4 from them |
| `REPORT.md` | this file |

## 1. The rule as modelled

Owner decision 2026-10-06, as given to this scout: *while a read scope (`EgressSubject.BeginRead`) is open and
unreported, every egress decided on the flows inside it is decided at SystemHigh, for every tool, RAGTool included.*
Scenario B's expected reason becomes `SystemHighData`: its call 2 (`web_search`) egresses inside the tool call, so inside
the open read scope (design section 5, Scenario B, "the decision is `LevelTooLow`, WebSearch / Confidential"), and under the
rule that decision is SystemHigh.

The natural implementation and what the model does, op by op (header of `frame-model-readscope.py`):

- **`BeginRead` pushes a read frame** onto the flow (`prev` = the flow's head, as `Enter` does at
  `src/Ashlar.Abstractions/Security/Egress/EgressSubject.cs:95-96`), whose mark is **pinned at SystemHigh** while the
  read is open. The `ReadScope` holds that frame instead of the bare chain it holds today
  (`src/Ashlar.Abstractions/Security/Egress/ReadScope.cs:31`, `:36-39`).
- **`Resolve`** keeps its shape (`EgressSubject.cs:238-251`: the join of every frame's mark from the head down to a
  detachment), so a decision on any flow whose chain holds an open read frame is SystemHigh, and a subject frame
  entered inside the read (its `prev` is the read frame) decides SystemHigh too.
- **`Observe` inside the scope** still raises every subject frame on the chain: `ObserveInto` walks through the read
  frame (non-null mark) to the subject frames below (`EgressSubject.cs:257-261`).
- **Forked work inherits the read frame** by the same `AsyncLocal` capture as any frame (`EgressSubject.cs:71`,
  `:20-22`): a Task, continuation or thread started inside the scope starts with the read frame as its head.
- **`ReadScope.Dispose`** computes the result exactly as today (`ReadScope.cs:83-84`: the join of the reports iff
  `Complete` was called and at least one report was made, else SystemHigh), observes it into the chain the read was
  begun on (the read frame's `prev`), marks the read frame disposed, and **leaves it by the 4.4 no-skip rule**
  (`EgressSubject.cs:275-279`, remarks `:27-31`): only the flow whose own undisposed head is the read frame goes back to
  the frame's `prev`; every other flow stays inside it.
- **A report after the end** observes straight into the frames (`ReadScope.cs:63-65`), the read frame included.
- **Two readings of a disposed read frame's mark** are modelled, because the decision's text fixes the open case only:
  - **`correct` (pinned; the rule as given to this scout):** the read frame's mark is SystemHigh for life. A flow that
    did not leave the read frame as its own head decides SystemHigh for the rest of its life.
  - **`settled` (alternative):** once the read ended, the read frame counts at `join(result, everything observed into
    it while open)`, so a flow stuck inside it decides exactly as the chain the read was begun on does (that chain
    already holds the result). While the read is open the two are identical.

The required label (the spec the code is checked against) is r7's lexical in-order semantics extended with reads: a
flow's lexical stack holds the subject frames, detachments and read frames it is inside in program order (a fork copies
it; the flow's own dispose, helper dispose or late `using` end removes an item; `RunDetached`'s return drops what was
entered inside); its scope is the items after the innermost detachment; an ended read contributes its result, which is
also joined into the ideal of the subject frames it was begun inside when it ends; an open read requires SystemHigh.

## 2. Properties checked (after every op, for every flow)

| | Property | Statement |
|---|---|---|
| P1 | no write-down (the 4.4 property restated) | `resolve(flow) >= required(flow)` for both the ideal and the live-mark reading: a decision never resolves below the join of every label that reached its flow through a read that has ended, nor below the marks of the frames it is inside. An open read contributes nothing here (P2 covers it). |
| P2 | open read = SystemHigh | if the flow's lexical scope holds a read not yet ended (begun on this flow, or on an ancestor flow before the fork), `resolve(flow) == SystemHigh`. A `Report` before `Dispose` does not lower it. |
| P3 | no-skip with read frames present | after any dispose (`X`, `Z`, `A`, `Y`), every flow's head is exactly: the disposed frame's `prev` if the disposing flow's head was that frame and it was undisposed; otherwise unchanged. Nothing else moves a flow but a push or the exact `RunDetached` bracket. |
| P4 | fail closed on a mis-disposed read | a `Z`/`Y` that is not an in-order end (the read frame is not the disposing flow's own undisposed head: out of order, on another flow, inside an awaited helper) moves no flow's head and lowers no mark: every subject frame's mark is `>=` before and the read frame's mark is SystemHigh (`correct`) or `>=` the result (`settled`). Independently, every subject-frame mark is monotone under every op. |

Ops: `E` Enter; `X` Dispose a frame (any flow); `A` Dispose inside an awaited async helper (on a copy of the context,
the restore is lost); `L` the flow's late `using` end of a frame another flow disposed; `O` Observe a fresh atom; `T`
fork; `C`/`U` `RunDetached` brackets; `R` BeginRead; `P` Report a fresh atom (any flow, before or after the end); `K`
Complete (any flow); `Z` Dispose the scope (any flow); `Y` Dispose the scope inside an awaited helper; `W` the flow's
late `using` end of a read another flow ended.

**The base is the quoted model.** With no read ops the extended model reproduces r7 exactly: `ACELO`, N=7, two flows
gives **1,900,357** sequences, the number the 4.4 body quotes (`logs/D2-base-reproduction-ACELO-N7.log`), and N=5 gives
18,324 in both. r7 itself, re-run from `ws/`, gave the body's 506,219 for `ACELOQR`, N=6, two flows.

## 3. Controls: each broken variant is CAUGHT first

All four at N=4, two flows, ops `E O R P K Z` (+X, +T), and again at N=5 with every op kind (tables in section 4). The
shortest counterexample per property, with its op count:

| Control | What is broken | Shortest counterexample | Ops | Property |
|---|---|---|---|---|
| **B1** | the open scope is not counted (master today: `BeginRead` captures the chain, `ReadScope.cs:36-39`, and the scope only observes on `Dispose`, `:78-86`) | `E@main ; R@main`: a decision inside the open read decides at the frame's mark, not SystemHigh | **2** | P2 |
| | | `T@main ; R@main ; Z0@t1 ; E@main`: a read begun with no frame and ended unreported by another flow, then a frame entered; the flow decides at that frame's mark alone (note below) | 4 | P1 |
| **B2** | disposing the scope pops the read frame even when it is not the head (a skip) | `R@main ; E@main ; Z0@main`: the frame entered inside the read is undisposed; the flow jumps below it | **3** | P3, P4 |
| **B3** | work forked inside the scope does not inherit the read frame | `E@main ; R@main ; T@main`: the task decides at the frame's mark while the read is open | **3** | P2 |
| | | `R@main ; T@main ; Z0@main ; E@t1`: after the unreported read ends, the task's later frame misses its SystemHigh | 4 | P1 |
| **B4** | `Report` before `Complete` satisfies the scope even when the read ends without `Complete` (throws) | `E@main ; R@main ; P0@main ; Z0@main`: the frame is raised to the report, not to SystemHigh | **4** | P1 (ideal) |

Note on B1's 4-op P1 catch: 4.4 pins "with no frame a read changes nothing"
(`src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressSubjectReadScopeTests.cs:336-349`). The lexical spec here is
stricter: a flow that could not leave an ended read (another flow ended it) carries the read's result into frames it
enters later. The correct rule meets the stricter spec because the flow stays inside the read frame; master does not.
It is a consequence of the new rule, not a 4.4 defect, and the 2-op P2 catch is the one to quote.

## 4. The correct rule: bounds, sequences, no violation

Every run: `python3 frame-model-readscope.py <variant> <N> <MAXF> <ops>`. "Sequences" counts every generated prefix, as
r7 does. CPU is `time.process_time()` of the run on one core (runs shared 4 cores, so wall > CPU; the logs hold both).
The largest bounds that fit the ~20 min single-core budget are N=7 on two flows with every op kind but `L`/`W`
(1,019 s CPU) and N=7 on three flows with the core read-scope ops (736 s CPU). Two runs were stopped unfinished as over
budget: N=8 on the core ops (estimated ~70 min CPU) and N=7 with `L`/`W` added (stopped after ~35 min CPU); neither is
in the table.

| Variant | Ops | Flows | N | Sequences | CPU s | Result | Log |
|---|---|---|---|---|---|---|---|
| `correct` | A C/U E K L O P R W Y Z (+X, +T) | <= 2 | 5 | 92,080 | 2.7 | no violation of P1-P4 | `B2-correct-allops-LW-2flows.log` |
| `correct` | A C/U E K L O P R W Y Z (+X, +T) | <= 2 | 6 | 1,471,354 | 57.1 | no violation of P1-P4 | `B2-correct-allops-LW-2flows.log` |
| `correct` | A C/U E K O P R Y Z (+X, +T) | <= 2 | 5 | 75,023 | 2.2 | no violation of P1-P4 | `B-correct-allops-2flows.log` |
| `correct` | A C/U E K O P R Y Z (+X, +T) | <= 2 | 6 | 1,174,311 | 48.3 | no violation of P1-P4 | `B-correct-allops-2flows.log` |
| `correct` | A C/U E K O P R Y Z (+X, +T) | <= 2 | 7 | 20,664,823 | 1019.4 | no violation of P1-P4 | `B-correct-allops-2flows.log` |
| `correct` | A C/U E L O (+X, +T) | <= 2 | 7 | 1,900,357 | 85.9 | no violation of P1-P4 | `D2-base-reproduction-ACELO-N7.log` |
| `correct` | E K O P R Z (+X, +T) | <= 2 | 6 | 306,250 | 13.9 | no violation of P1-P4 | `A-correct-EORPKZ-2flows.log` |
| `correct` | E K O P R Z (+X, +T) | <= 2 | 7 | 4,695,013 | 262.5 | no violation of P1-P4 | `A-correct-EORPKZ-2flows.log` |
| `correct` | E K O P R Z (+X, +T) | <= 3 | 6 | 641,038 | 30.2 | no violation of P1-P4 | `C-correct-3flows-and-settled.log` |
| `correct` | E K O P R Z (+X, +T) | <= 3 | 7 | 12,635,707 | 736.1 | no violation of P1-P4 | `C-correct-3flows-and-settled.log` |
| `settled` | A C/U E K O P R Y Z (+X, +T) | <= 2 | 6 | 1,174,311 | 47.0 | no violation of P1-P4 | `D-base-reproduction-controls-settled.log` |
| `settled` | E K O P R Z (+X, +T) | <= 2 | 6 | 306,250 | 13.3 | no violation of P1-P4 | `C-correct-3flows-and-settled.log` |
| `settled` | E K O P R Z (+X, +T) | <= 2 | 7 | 4,695,013 | 250.6 | no violation of P1-P4 | `C-correct-3flows-and-settled.log` |

Controls (every op kind on):

| Control | Ops | N | Sequences | Caught (shortest per property) |
|---|---|---|---|---|
| `B1` | A C/U E K O P R Y Z (+X, +T) | 5 | 58,949 | CAUGHT P2-open-read on main (2 ops): E@main ; R@main; CAUGHT P2-open-read on t1 (3 ops): E@main ; T@main ; R@t1; CAUGHT P1-write-down-ideal on main (4 ops): T@main ; R@main ; Z0@t1 ; E@main; CAUGHT P1-write-down-ideal on t1 (4 ops): T@main ; R@t1 ; Z0@main ; E@t1; CAUGHT P1-write-down-live on main (4 ops): T@main ; R@main ; Z0@t1 ; E@main; CAUGHT P1-write-down-live on t1 (4 ops): T@main ; R@t1 ; Z0@main ; E@t1 |
| `B2` | A C/U E K O P R Y Z (+X, +T) | 5 | 73,803 | CAUGHT P3-head on main (3 ops): R@main ; E@main ; Z0@main; CAUGHT P4-misdisposed-read on main (3 ops): R@main ; E@main ; Z0@main; CAUGHT P3-head on t1 (4 ops): T@main ; R@t1 ; E@t1 ; Z0@t1; CAUGHT P4-misdisposed-read on t1 (4 ops): T@main ; R@t1 ; E@t1 ; Z0@t1 |
| `B3` | A C/U E K O P R Y Z (+X, +T) | 5 | 73,249 | CAUGHT P2-open-read on t1 (3 ops): E@main ; R@main ; T@main; CAUGHT P1-write-down-ideal on t1 (4 ops): R@main ; T@main ; Z0@main ; E@t1; CAUGHT P1-write-down-live on t1 (4 ops): R@main ; T@main ; Z0@main ; E@t1 |
| `B4` | A C/U E K O P R Y Z (+X, +T) | 5 | 75,009 | CAUGHT P1-write-down-ideal on main (4 ops): E@main ; R@main ; P0@main ; Z0@main; CAUGHT P1-write-down-ideal on t1 (5 ops): E@main ; T@main ; R@main ; P0@main ; Z0@main |

**No violation of P1-P4 was found in any run of the `correct` or `settled` variant: 41,755,599
read-scope sequences for `correct` in all.** No counterexample survives the correct rule at these bounds.

## 5. Pins: does the rule change any exact frame count the 4.4 growth twins pin?

**No count that exists on master changes.** The rule adds a frame only when `BeginRead` is called, and no 4.4 growth
twin calls it (grep over `EgressSubjectNestingTests.cs`: no `BeginRead`; the two uses in `EgressSubjectDetachTests.cs`,
`:699` and `:905`, are in-order `using` blocks in twins that pin labels, not lengths). `frame-model-pins-readscope.py`
Part 1 re-computes the three `pins.py` numbers with no reads and gets the same 13 / 13 / 1,248, and the stay-outer
lengths 1, `i + 2`, 53, 53 (the detach-middle and four-frame-shapes numbers are of twins the 4.4 body says the
2,092-program twin replaced; the model keeps the old disposable detachment only to reproduce them). The growth twins on
master and their pinned counts:

| Twin (master) | Pinned count | Where |
|---|---|---|
| `A_flow_that_disposes_its_frames_out_of_order_stays_inside_the_outer_frame_it_disposed` | 1, then `i + 2` over 50 iterations, then 53, then 53 | `EgressSubjectNestingTests.cs:282`, `:309`, `:321`, `:329` |
| `Every_dispose_order_of_three_frames_on_one_flow_leaves_only_the_head_it_disposes` | 13 | `EgressSubjectNestingTests.cs:725` |
| `Every_program_of_frames_and_detached_callbacks_on_one_flow_leaves_only_the_head_it_disposes_and_never_writes_down` | 2,092 programs, 2,474 frames left | `EgressSubjectDetachTests.cs:781`, `:787` |

**Candidate exact counts for new 4.5 twins** (Part 2 of the pins model, output in `logs/pins.log`; the lane should
recompute them against its C# `FlowModel`, `EgressSubjectNestingTests.cs:1145-1233`, which needs a read-frame node):

- (a) the three-frame orders with an in-order read inside each frame leave the same **13** frames: in-order reads add
  nothing;
- (b) a read ended on another flow (or in an awaited helper) once per iteration, under one session frame: the chain is
  **`i + 2`** after iteration `i` for 50 iterations, final **51** (4.4 limit (a) for read frames);
- (c) a read ended in order once per iteration: the chain stays **1** for 50 iterations;
- (d) a read ended while a frame entered inside it is still the head: **3**, then **2** after that frame is disposed
  (the flow is left on the disposed read frame);
- (e) every program of one enclosing frame, up to two frames and up to two reads on one flow, each read ended by the
  flow in any order and every frame disposed: **7,101 programs, 17,428 frames left**.

## 6. Findings that matter for the 4.5 lane

1. **Two 4.4 twins flip by name under the rule; one of them only under the pinned reading.**
   - `EgressSubjectReadScopeTests.A_completed_read_with_no_report_observes_SystemHigh` asserts that a decision
     **inside the open scope** is `Public` ("the read has not ended yet", `EgressSubjectReadScopeTests.cs:50`). Under
     the rule it is SystemHigh. It flips under both readings; it is the twin to flip by name for the owner decision.
   - `EgressSubjectReadScopeTests.A_read_observes_into_every_live_frame_of_its_chain_from_whatever_flow_it_ends_on`
     disposes the scope inside `Task.Run` (`:169-175`): the task, whose head is the read frame it inherited, leaves it,
     but the **main flow's head stays the disposed read frame** (no-skip, `EgressSubject.cs:278-279`). Line `:179` then
     expects `LevelTooLow` on the main flow. Under `correct` (pinned) the main flow is SystemHigh for life, so the
     reason is `SystemHighData`: **flips**. Under `settled` the read frame counts at Secret and the twin stands.
   - Stand under both: `:184-247` (three reads ended off-chain leave the main flow inside three disposed read frames,
     but the twin asserts only marks and the other flows' `no-subject` basis), `:140-158` (the read is begun and ended
     inside the async helper, on the helper's flow, in order; see section 10), `:290-307`, `:310-333`, `:336-349`,
     `EgressSubjectDetachTests.cs:699-706`, and the `Read` helper `:903-908` used in a filter at `:323` (in order).
2. **Pinned versus settled is an owner call with an availability cost, not a confidentiality one.** Both readings pass
   P1-P4 at every bound. Under pinned, any read scope that is not ended as its flow's own head, that is ended on another
   flow (`Dispose` "may be called from any thread", `ReadScope.cs:26-27`), inside an awaited helper, or under a frame a
   tool entered and left, leaves the begin flow (an agent cycle, say) at SystemHigh **for the rest of its life**, and
   its chain grows by one read frame per such read (limit (a) extended to reads). Under settled it costs the chain growth
   only and decides as 4.4 does today. The 4.4 known-limits wording (`EgressSubject.cs:44-62`) covers the growth but not
   "SystemHigh from then on".
3. **`ToolCallingAgent`'s wiring is in order on one flow, so production as designed is not exposed to finding 2.**
   Tool calls run serially (`foreach` at `src/Ashlar.BackgroundAgents/Agents/ToolCallingAgent.cs:218`,
   `await tools.InvokeAsync(...)` at `:236`, the only `InvokeAsync` in the file; no `WhenAll`, `Task.Run`, `Task.Factory` or `new Thread` in it), so
   `using var read = EgressSubject.BeginRead(); ...; read.Complete();` around `:236` begins and ends each read as the
   agent flow's own head. The exposure is a tool that forks work which outlives the call (it keeps the read frame and,
   under pinned, decides SystemHigh for life) and any future parallel tool calls begun on one flow (the first to finish
   ends out of order and the agent flow is stuck).
4. **The basis of a decision inside an open read is unspecified.** `Resolve` names the innermost live frame
   (`EgressSubject.cs:240-250`). If the read frame is a `Frame` with a non-null mark, `Live` (`:224-229`) stops at it and
   the basis is whatever the read frame carries. The leak test filters by `subject:agent:<id>` for the model call
   **after** the read (design section 5), which is unaffected, but Scenario B's in-call egress record carries the read
   frame's basis. Suggest `Live` skips read frames for the basis (the nearest live subject names the decision) while
   their mark still counts; the model does not distinguish (it has labels only).
5. **A read with no frame keeps 4.4's behaviour.** A read begun with no frame pushes a read frame over nothing: inside it
   decisions are SystemHigh (they were already `no-subject`, `EgressSubject.cs:10-12`); after an in-order end the flow
   has no frame again (`With_no_frame_a_read_changes_nothing`, `:336-349`, stands).

## 7. Limitations of the model (what it does not model)

- **Timers, registrations, `System.Timers.Timer`, cold tasks:** `T` forks the flow at the op; it does not model work
  created before a frame or read and started inside it, or created inside and started after (`EgressSubject.cs:52-62`
  names the capture points). Such work keeps the frames of where it captured the context, exactly as for subject frames;
  the read frame adds no new case there, and nothing here checks it.
- **Cross-process and cross-network:** no frame crosses a process boundary (design section 2.2, "Threading facts"); not
  modelled.
- **Exception filters and the two-pass unwind:** as in r7, no op runs between a throw and the `RunDetached` restore; the
  Cecil fact on master pins that (`EgressSubjectDetachTests`).
- **Async iterators / `yield return`:** not an op; the 4.4 obligation (d) forbids a frame, and so a read, across one.
- **The basis string and the shared `HighWaterMark`** (limit (b)): labels are symbolic atoms joined as sets, one mark
  per frame; the cross-session effect of a shared mark is outside the model.
- **Concurrency is interleaving only:** ops are atomic and sequential; the three-state dispose window
  (`EgressSubject.cs:190-193`, `:263-273`) and the concurrent-`Report` CAS (`ReadScope.cs:51-61`) are not modelled.
- **Bounds:** up to N ops and up to 3 flows (section 4); fresh atoms for every `O`/`P`; `Complete` twice, `Dispose`
  twice and `Complete` after `Dispose` are generated as refused (no-ops in code).

## 8. Wording a PR body can quote

> **Read frames (owner decision 2026-10-06).** `BeginRead` pushes a read frame onto the flow whose mark is SystemHigh
> while the read is open, so every decision on that flow, and on every task, continuation or thread started inside the
> scope, is SystemHigh until the scope ends; `Observe` inside the scope still raises every subject frame on the chain.
> Disposing the scope observes its result into the chain it was begun on, exactly as before (the join of the reports if
> `Complete` was called and at least one report was made, else SystemHigh), and leaves the read frame by the 4.4 rule:
> only the flow whose own undisposed head it is goes back to the frame it was begun under; every other flow stays
> inside it (fail closed). A Python model of the rule over flows with Enter, Dispose (in order, out of order, on another
> flow, inside an awaited helper, late), Observe, fork, `RunDetached` brackets, BeginRead, Report, Complete and Dispose
> checked four properties after every op (no write-down; an open read decides SystemHigh; no-skip with read frames
> present; a mis-disposed read lowers no mark and moves no flow) over 41,755,599 sequences in all (the largest single bound: 20,664,823 sequences at N=7, up to 2 flows, ops A C/U E K O P R Y Z (+X, +T), 1019 s of CPU), and found no
> violation. With no read ops the model reproduces the 4.4 model's 1,900,357 sequences exactly. The same harness
> catches four broken variants first: the open scope not counted (master before this PR) in 2 ops (`Enter ; BeginRead`),
> a Dispose that pops a read frame that is not the head in 3, forked work that does not inherit the read frame in 3, and
> a Report before Complete that satisfies a read that then throws in 4. No exact frame count a 4.4 growth twin pins
> changes: the rule adds a frame only when `BeginRead` is called, and none of those twins does.

## 9. Owner questions

1. **Pinned or settled** for a disposed read frame a flow could not leave (section 6.2): SystemHigh for the rest of
   that flow's life (the rule as worded; more conservative; flips one more 4.4 twin), or the result the read observed
   (4.4 parity; chain growth only)?
2. **The basis** of a decision made inside an open read scope (section 6.4): the nearest live subject frame, or a read
   basis of its own?
3. **"Unreported"** in the decision: the rule modelled treats a scope as open until it is disposed, so a `Report` before
   `Dispose` does not lower an open scope. Confirm, since "open and unreported" could be read as "a reported-but-open
   scope counts at its reports".
4. Whether limit (a)'s wording gains "a read scope not ended as its flow's own head stays on the flow" (and, if pinned,
   "and that flow decides SystemHigh from then on").

## 10. Unverified

- [unverified] That `AsyncLocal` changes made inside an `async` helper before its first `await` persist across that
  helper's later awaits on the helper's own flow, which `An_awaited_read_that_throws_observes_SystemHigh`
  (`EgressSubjectReadScopeTests.cs:140-158`) relies on to keep passing with a read frame. Runtime fact; a test settles it.
- [unverified] The cert-gate time budget of a C# port of the (e) program-twin candidate (7,101 programs).
