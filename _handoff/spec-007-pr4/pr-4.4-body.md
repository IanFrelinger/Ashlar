## Summary

SPEC-007 PR 4.4 gives `EgressSubject` frames the semantics enforcement needs: **monotone nesting**, **`Observe`**, and **read scopes**. It also makes `AgentBus` start every subscriber with no subject. It implements row 4.4 of the PR 4 plan: design §2.2 ("Observe, report, and read scopes", "Monotone nesting", "Threading facts") and defaults D12, D13, D14, D18 and D37.

The plan puts this PR first because it closes a declassification hole before any producer exists. Before this PR, `Resolve` read only the innermost live frame. So any code that entered a fresh `Public` frame inside a `Secret` one would decide `Public`, and a nested subject's reads were lost when its frame ended.

- **Nesting.** A decision's current label is the join of every live frame's mark on the chain, and its basis names the innermost subject. Disposing a frame observes its mark into every enclosing live frame.
- **`EgressSubject.Observe(SecurityLabel)`** (public) joins a label into every live frame at once. It only raises, and with no frame it does nothing.
- **`EgressSubject.BeginRead()`** (public) returns a `ReadScope` for one read. A read counts at what it reported only when it returned (`Complete()`) and reported. Otherwise it counts as `SystemHigh`, which covers both an unreported read and a read that threw. `Observe` never satisfies a scope.
- **`EgressSubject.Detach()`** (internal, raise-only) leaves every frame. `AgentBus.PublishAsync` dispatches its subscribers under it, so a subscriber is never decided at the publisher's mark.
- **Flipped by name:** `EgressGuardDecisionTests.An_Internal_subject_may_reach_an_external_model_across_await_and_Dispose_restores_the_previous_frame`. Its inner block pinned "inner Public decides Public". It now pins "inner Public inside Internal decides Internal, refused `LevelTooLow` to a Public destination".

**Still report-only, and no production outcome changes.** No production code enters a frame or begins a read yet; the producers are PR 4.5. Today every production decision is `no-subject`, and with no frame `Resolve`, `Observe` and a read scope behave as before. The one production call site this PR adds is `AgentBus`'s detachment, which turns a frame into no subject, and production has no frame for it to remove.

These are classification-style controls inside the runtime. They are not an accredited cross-domain solution.

No non-test code under `application/` changes, so this PR is not `[coordinated-integration]`.

## Changes

- **`src/Ashlar.Abstractions/Security/Egress/EgressSubject.cs`**
  - `Resolve` returns the join of every live subject frame's mark, with the innermost frame's basis.
  - `Frame.Dispose` observes the frame's mark into every enclosing live frame, then restores the previous frame as before.
  - New public `Observe(SecurityLabel)` and `BeginRead()`.
  - New internal `Detach()`. It pushes a *detachment*: a frame with no mark and no enclosing frame, so every chain walk ends at it. Disposing it restores the caller's frame on the flow it detached, and only there; a dispose from another flow changes nothing, as for a frame. A task started under it keeps no subject after the caller is restored, because a disposed detachment has nothing behind it.
  - The nested `Frame` class becomes `internal` (it was `private`), so `ReadScope` can hold the chain it was begun on.
- **`src/Ashlar.Abstractions/Security/Egress/ReadScope.cs`** (new, `public sealed`, `IDisposable`):
  - `Report(SecurityLabel)` joins the reports with a compare-and-swap loop.
  - `Complete()` marks the read as returned.
  - `Dispose()` observes into every live frame of the chain the scope was begun on: the reported join if it completed and reported, otherwise `SystemHigh`.
  - A report made after the scope ended goes to the frames directly, so it is never lost. Disposing twice does nothing.
  - The scope is not ambient: only the code holding it can report.
- **`src/Ashlar.Orchestration/Communication/AgentBus.cs`**: the subscriber dispatch loop runs inside `using (EgressSubject.Detach())`. Each `Task.Run` therefore captures a detached context. That covers the handler and its catch-and-log, and it holds for the whole life of the task.
- **`src/Ashlar.Abstractions/Ashlar.Abstractions.csproj`**: `InternalsVisibleTo Ashlar.Orchestration`, for the internal `Detach`.
- **`src/Ashlar.Abstractions/PublicAPI.Unshipped.txt`**: `EgressSubject.Observe`, `EgressSubject.BeginRead`, `ReadScope`, `ReadScope.Report`, `ReadScope.Complete` and `ReadScope.Dispose`.
- **Tests**, all in the cert-gate namespace:
  - `EgressSubjectNestingTests` (8);
  - `EgressSubjectReadScopeTests` (10);
  - `EgressSubjectDetachTests` (6, among them the convention facts);
  - the flipped `EgressGuardDecisionTests` test.
- **Records:**
  - `ci/cert-gate-assertions.md`: three new rows (nesting, read scope, `Detach`). Row 64's "current is the `EgressSubject` frame's high-water mark" now says it is the join of every live frame's mark. The `Tests/Certification` count goes from 125 to 128 `.cs` files (131 entries).
  - `CHANGELOG.md`: an Unreleased "Added" entry.
  - SPEC-007: one line under the PR 4 status bullet.
  - `docs/knowledge-graph.{json,md}`, regenerated after `git add`.
  - `docs/EgressInventory.md` and `ci/egress-inventory.tsv` are unchanged: no outbound site moves, and `EgressGuardConventionTests` stays green.

### Design points this PR had to settle

- **How a scope learns it ended by an exception: `Complete()`.** §2.2 and D14 require it, but the design names no mechanism, and a `using` block cannot see an exception. `Complete()` follows `TransactionScope`. A holder that forgets it fails closed (`SystemHigh`); a `Fail()` call forgotten on a catch path would fail open.
- **"Read nothing" is `Report(SecurityLabel.Public)`.** `Public` is the bottom, so the report satisfies the scope and raises nothing. No separate member was added.
- **The scope is held, not ambient.** "`ReadScope.Report(label)` from a tool that declares itself labelled", with `ToolCallingAgent` checking the marker before it accepts a report, works when the agent holds the scope and passes on the labelled tool's report. Code that only reaches `EgressSubject` cannot report at all. PR 4.5 decides how `RAGTool`'s label reaches `Report`.
- **The basis of a nested decision names the innermost subject.** The leak test filters by `subject:agent:<id>`, and C4's inner send records the inner subject.

## Behaviour changes, and what stays report-only

| Change | Production effect today |
|---|---|
| A nested frame decides at the join of the chain | None: no production code enters a frame |
| Disposing a frame raises its enclosing frames | None: no production frames |
| `Observe` and read scopes | None: nothing calls them yet (PR 4.5) |
| `AgentBus` subscribers run with no subject | None in recorded decisions: production publishers run with no subject already. One `Detach` allocation per publish |

Everything stays report-only: `EgressGuard.Evaluate` records and never refuses, throws or blocks. The mode plumbing is PR 4.6 and the switch is 4.11.

## Testing

All builds and tests ran in the Linux devtest container through `scripts/test-in-container.sh`. Nothing ran on the host.

- **Red at base.** The twins that compile against master were committed alone first (`cb64de5`, on `79e988c`). Run on net8.0 with the filter `FullyQualifiedName~Certification.EgressSubject|FullyQualifiedName~Certification.EgressGuardDecisionTests`: **11 failed of 148**. Each failed for the intended reason:
  - the four nesting twins (for example, "Expected decision.Current to be Secret … but found Public");
  - the flipped test ("Expected inner.Current to be Internal … but found Public");
  - the AgentBus twin (the subscriber's basis was `subject:bus-publisher`);
  - the three `Detach` twins (no such member);
  - the two convention facts (no site, no detached block).

  The control `A_frame_disposed_out_of_order_leaves_the_chain` passed at base, as it should. The tests that need the new API (`Observe`, `BeginRead`) cannot compile at base and were added with the change.
- **Green after:** the same filter: **161 of 161** on net8.0 and **161 of 161** on net10.0, at the squashed commit.
- **Builds:** `Ashlar.Abstractions` on netstandard2.0, net8.0 and net10.0, with 0 warnings and 0 errors (`TreatWarningsAsErrors`, `AnalysisMode=All`, PublicAPI analyzers). `Ashlar.Orchestration` (net8.0 and net10.0) with 0 warnings and 0 errors.
- **Full cert-gate** (`bash scripts/run-cert-gate.sh`, net8.0): **2566 of 2566**, 0 skipped (the skip baseline matches; `--list-tests` expected at least 2561).
- **`Ashlar.Tests.Orchestration`** (it targets net8.0 only): **302 of 302**, the existing `AgentBusTests` included.
- **`Ashlar.Tests.BackgroundAgents`**, filter `FullyQualifiedName~Egress` (the agent-site twins that enter a frame): **14 of 14** on net8.0.
- **Repo gates:** `scripts/ci/run-repo-gates.sh` passes all 25 gates, with the knowledge graph current.

**Mutation checks.** Each was run with `scripts/mutation-check.sh` at `8a4c295`, on net8.0, with the filter above (161 tests). The script clones the commit, applies exactly one replacement, proves it applied, runs the tests red, restores the file, requires an empty `git status --porcelain`, and runs them green.

| # | Rule | Mutation | Result | What went red |
|---|---|---|---|---|
| m1 | Nesting joins the chain | `Resolve` reads only the innermost frame (the join line becomes `_ = frame.Mark.Current;`) | `KILLED red=failed:3/161 green=passed:161/161` | `A_Public_frame_inside_a_Secret_frame_decides_Secret`, `Every_live_frame_on_the_chain_is_joined_across_await`, and the flipped `An_Internal_subject_may_reach_an_external_model_across_await_and_Dispose_restores_the_previous_frame` |
| m2 | A disposed frame raises its enclosing frames | `Frame.Dispose` no longer observes its mark outward | `KILLED red=failed:2/161 green=passed:161/161` | `Disposing_a_frame_observes_its_mark_into_every_enclosing_live_frame`, `A_frame_entered_in_a_child_task_reaches_the_parent_frame_when_it_is_disposed` |
| m3 | `Observe` joins into every live frame | `Observe` raises only the innermost frame | `KILLED red=failed:2/161 green=passed:161/161` | `Observe_joins_into_every_live_frame_on_the_chain_at_once`, `Observe_reaches_an_enclosing_flow_while_the_nested_frame_is_still_running` |
| m4 | `Observe` never satisfies a read scope | DESIGN-4's rule: `BeginRead` records the open scope in an `AsyncLocal`, and `Observe` reports into it | `KILLED red=failed:1/161 green=passed:161/161` | `Observe_alone_does_not_satisfy_a_read_scope` |
| m5 | An unreported read is `SystemHigh` | the fallback observes `Public` instead of `SystemHigh` | `KILLED red=failed:4/161 green=passed:161/161` | `A_completed_read_with_no_report_observes_SystemHigh`, `Observe_alone_does_not_satisfy_a_read_scope`, `A_read_that_throws_observes_SystemHigh_even_after_it_reported`, `An_awaited_read_that_throws_observes_SystemHigh` |
| m6 | A read that throws is `SystemHigh` | `Dispose` honours the report without `Complete()` | `KILLED red=failed:2/161 green=passed:161/161` | `A_read_that_throws_observes_SystemHigh_even_after_it_reported`, `An_awaited_read_that_throws_observes_SystemHigh` |
| m7 | `Detach` leaves every frame | `Detach` builds the detachment but does not make it current | `KILLED red=failed:4/161 green=passed:161/161` | `An_AgentBus_subscriber_runs_with_no_subject_whatever_the_publisher_has_read`, `Detach_leaves_every_frame_until_disposed_and_then_restores_the_callers`, `A_task_started_under_Detach_keeps_no_subject_after_the_caller_is_restored`, `A_detachment_disposed_from_another_flow_restores_nothing_there` |
| m8 | AgentBus dispatches detached | `using (EgressSubject.Detach())` becomes `using ((IDisposable?)null)` | `KILLED red=failed:3/161 green=passed:161/161` | `An_AgentBus_subscriber_runs_with_no_subject_whatever_the_publisher_has_read`, `Detach_is_called_only_at_the_listed_dispatch_points`, `The_AgentBus_dispatch_starts_every_subscriber_inside_the_detachment` |
| m9 | A disposed detachment never falls back to the caller | the detachment's enclosing frame is the caller's frame | `KILLED red=failed:3/161 green=passed:161/161` | `An_AgentBus_subscriber_runs_with_no_subject_whatever_the_publisher_has_read`, `A_task_started_under_Detach_keeps_no_subject_after_the_caller_is_restored`, `A_detachment_disposed_from_another_flow_restores_nothing_there` |

The first m1 attempt replaced the join with `break;`. That made the loop's increment unreachable (CS0162, an error under `TreatWarningsAsErrors`), and the script reported it as `INVALID … reason=red-no-tests`, not as a kill. It was re-run with the replacement above.

m9 also turns the AgentBus twin red: `AgentBus` disposes its detachment before its subscriber tasks run, so with the mutation they fall back to the publisher's frame.

The mutations ran at `8a4c295`. The squashed commit differs from it only in `docs/knowledge-graph.{json,md}` (`git diff 8a4c295 <head> --stat`).

### Testing strategy (blast radius)

See [Testing strategy pivot v1](docs/architecture/TestingStrategyPivot-v1.md).

This PR changes `Ashlar.Abstractions` (one type rewritten, one added) and one statement block in `Ashlar.Orchestration`, plus tests. No DI composition, host or routing changes.

- [x] Focused tests in the touched area: the frame-semantics classes and `EgressGuardDecisionTests` on both TFMs, the full cert-gate filter on net8.0, `Ashlar.Tests.Orchestration` (net8.0, its only TFM), and the BackgroundAgents egress twins.
- [ ] `make kernel-coverage-gate`: runs in CI as `kernel-coverage`.
- [ ] `make kernel-gate`: no kernel hosting, pipeline or profile change.
- [ ] `make test-prod-style`: no production DI, API or routing change.

## Checklist

- [x] Repo gates pass locally. The cert-gate passes in the devtest container on net8.0, and the touched classes on net10.0.
- [x] Documentation updated: CHANGELOG, SPEC-007 status, and `ci/cert-gate-assertions.md`.
- [x] No `TODO` or `NotImplementedException`.
- [x] Breaking changes: none. The change is additive public API in `Ashlar.Abstractions` (Unshipped). The nesting change alters the label a nested frame decides at, which only tightens, and no production code nests frames.

## Release

- [x] Not a versioned release; skip.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01GYPVuHoik1sH5U6gWtSVRT
