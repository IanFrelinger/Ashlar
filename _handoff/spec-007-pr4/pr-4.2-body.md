## Summary

SPEC-007 PR 4.2 closes the first of the three gaps PR 4 has to close before the guard enforces: **a synchronous `Send` on the netstandard2.0 asset of `Ashlar.Abstractions`** (PR 4 design §2.6, gap 1, option C; engineering default D31, which the owner amended on 2026-10-06: no record, the exception only).

That asset, which .NET 5–7 apps resolve, is compiled against a `DelegatingHandler` with no `Send` to override. So a synchronous `HttpClient.Send` or `HttpMessageInvoker.Send` through an `EgressHttp` client or handler reached the runtime's `DelegatingHandler.Send`, which forwarded it to the inner handler **unevaluated**. The twin shows it at the base: that `Send` returned the inner handler's response, the inner handler saw 1 request, and no decision was recorded.

After this PR, wherever that asset runs on a runtime that has a synchronous `Send` (.NET 5 or later):
- a synchronous `Send` through `EgressHttp.CreateClient` or `EgressHttp.Wrap` is **refused with `NotSupportedException` before anything is sent**: 0 requests reach the inner handler;
- `SendAsync` is still evaluated exactly once;
- **no decision is recorded** for the refusal, and none is published when a client or handler is built: the caller's exception is the refusal's only trace (the owner's decision, below);
- `EgressHttp.CreateDelegatingHandler` throws `PlatformNotSupportedException`.

`docs/SdkCompatibilityPolicy.md` now says that full egress-guard coverage needs the net8.0 or later asset, and that the netstandard2.0 asset is for .NET Framework, classic Mono and Unity. There is no `buildTransitive` error and no end-of-life target framework.

**Who is affected.** Only code outside this repository that loads the netstandard2.0 asset on .NET 5 or later (every .NET 5, 6 or 7 app; also, for example, a `netcoreapp3.1` app rolled forward or a plugin loaded by path), calls `EgressHttp` itself, and sends synchronously. .NET 5–7 are out of support. Every Ashlar host binds the net8.0 or later asset, no Ashlar source makes a synchronous HTTP `Send`, and the net8.0 and net10.0 assets do not change behaviour.

These are classification-style controls inside the runtime. They are not an accredited cross-domain solution.

## Owner decision (2026-10-06, amends D31): no build-time record

The design (§2.6 gap 1, row 4.2, D31) says "the hop publishes a `NoDecision` record … so the refusal is in the operator log". That cannot be done at the moment of the refusal: on netstandard2.0 nothing can override `HttpMessageHandler.Send` (the C# compiler emits `newslot` for a non-`override` virtual), so the runtime's `DelegatingHandler.Send` calls the hop's inherited base `Send` with no frame of this assembly on the stack. The phase A lane published the record **when the client or handler was built** instead. The phase A check found that this misleads operators: it reports a refused egress where none was attempted, every `SendAsync`-only client leaves one, and a client built per call leaves one per call.

**Owner decision (2026-10-06, amending D31): drop the record and keep only the runtime `NotSupportedException`** (S4's original option C). The phase B check pointed out that D31 is a default the owner accepted, so the owner was asked. They chose this option, the exception only, over a corrected build-time record and a process-wide first-chance-exception hook. SPEC-007's decisions log has the row, and the 4.2 status line cites it (commit `4e34ded7`). The refusal still fails closed: nothing is sent. The cost, stated: on such a runtime a refused synchronous `Send` reaches the caller but not the operator log. `EgressGuard.PublishNoDecision` and `NotEvaluatedBasis` are deleted (nothing else used them), so `EgressGuard.cs` is unchanged by this PR, and the collision with 4.6's `EgressDecision` constructor change is gone. SPEC-007's 4.2 line states the choice and its reason.

## Changes

- **`src/Ashlar.Abstractions/Security/Egress/SynchronousSendRefusedOnNetstandard20Asset.cs`** (new, `internal sealed`, compiled only under `#if NETSTANDARD2_0`):
  - It derives from `HttpMessageHandler`, not `DelegatingHandler`, and overrides only `SendAsync`, which it forwards through an owned `HttpMessageInvoker(inner, disposeHandler: true)`. The request, the token and the response instances are unchanged.
  - A synchronous `Send` therefore reaches the runtime's base `HttpMessageHandler.Send`, which throws `NotSupportedException`. Its message names the hop's type, so the refusal explains itself.
  - `RuntimeHasSynchronousSend` is read once, by reflection: does the runtime's `HttpMessageHandler` have a non-public instance `Send(HttpRequestMessage, CancellationToken)`? It is `true` on .NET 5 and later, and `false` on .NET Framework, classic Mono and Unity.
  - `Over(inner)` builds the hop; it publishes nothing.
  - **`internal HttpMessageHandler Inner`**: the handler the hop forwards to. Because the hop is not a `DelegatingHandler`, a walker of the handler chain steps through `Inner` here; 4.3's redirect flip, which walks to the primary handler, needs it (check finding 6).
- **`EgressHttp`**: `CreateClient` (both overloads) and `Wrap` build the guard handler through one private `Guarded(...)`, which puts the hop under it on netstandard2.0 only. `CreateDelegatingHandler` throws `PlatformNotSupportedException` there after its argument checks, with a message that names the net8.0 asset; its XML docs carry the new `<exception>`. The class remarks describe the refusal, say no decision is published, and say what the hop changes where nothing is refused (.NET Framework, classic Mono, Unity): each `SendAsync` takes one extra in-process step, and a `Wrap` handler's `InnerHandler` is the hop, which a walker of `DelegatingHandler.InnerHandler` cannot step through (adversarial finding A1). **`Wrap`'s remarks** say the same, and say not to replace the returned handler's `InnerHandler` (settable until the first send): on netstandard2.0 that removes the hop, so a synchronous `Send` goes out unevaluated again, and on every asset the handler then neither sends through nor owns `inner` (check finding 6).
- **`EgressGuardHandler`**: the remarks describe the refusal instead of the open gap.
- **`EgressDecision.CurrentBasis`** (doc only): names the faulted basis `not resolved: the evaluation faulted`, which 3a already emitted and the doc omitted (check finding 3). `EgressDecision.Fault`'s doc is unchanged and true again, since no record carries a non-exception `Fault`.
- **4.6's startup line** (`EgressEnforcement.StartupLine`; merge check finding 1): "Records only: nothing is refused yet (SPEC-007 PR 4)." becomes "Records only: **the guard refuses nothing yet** (SPEC-007 PR 4)." `AnnounceOnce` writes the line to standard error from the netstandard2.0 asset too, when the mode is not plain report, and in that same process a synchronous `Send` through `EgressHttp` is refused on .NET 5 or later, so "nothing is refused" was false there. `EgressEnforcement`'s remarks ("The guard refuses nothing yet", and the netstandard2.0 refusal is the runtime's and holds in every mode) and the `ReportMode` summary say the same. `EgressModeProcessBindingTests` pins the new text in its four startup-line facts (red first, M9).
- **Test harness**:
  - `Ashlar.Tests.Infrastructure.csproj` gains a second `ProjectReference` to `Ashlar.Abstractions`, with `SetTargetFramework=netstandard2.0`, `ReferenceOutputAssembly=false`, `Private=false` and `OutputItemType`.
  - A target copies that build to `abstractions-netstandard2.0/` in the output. It fails the build with a message if the build was not resolved.
  - `EgressHttpNetstandard20TwinTests` (cert-gate, 8 facts) loads it into an `AssemblyLoadContext` of its own, together with a second copy of the test assembly, so `Netstandard20Driver`'s ordinary C# calls to `EgressHttp` bind to the netstandard2.0 build.
  - So the design's fallback (a shell-lint driver) was not needed, and `run-cert-gate.sh`, `cert-gate-config.sh`, `cert-gate.yml` and both `READINESS_PATHS` copies are untouched.
  - **Event-source hygiene** (still needed: the isolated copy publishes its `SendAsync` decisions, so its own `Ashlar-Egress` `EventSource` exists beside the guard's). .NET 8 builds a source's event descriptors the first time it is enabled, and refuses to while another live source has the same name and GUID (`EventSource_EventSourceGuidInUse` in `EnsureDescriptorsInitialized`, unless the `System.Diagnostics.Tracing.EventSource.AllowDuplicateSourceNames` switch is set). The refusal is only reported out of band. So if the isolated copy published before anything had enabled the guard's own source, no listener could ever enable that source again.
    - The first full cert-gate run in phase A hit this: `EgressGuardDecisionTests.A_credential_holding_a_delimiter_reaches_no_record_field` failed.
    - A probe, 20 runs per arm: that class beside the twin failed **18/20**; alone it failed **0/20**. Filtering the decision test's listener to its own assembly did not help (**14/20** failed).
    - The harness therefore enables the guard's own source once, before it loads the isolated copy, and asserts that the source is enabled. After the fix, beside the twin, it failed **0/20**. `EgressGuardDecisionTests` is unchanged.
    - A fact pins this: with the isolated copy loaded and publishing, the guard's own source still writes every decision (mutation M6). Phase B changed only how the fact makes the isolated copy publish (an evaluation, since a build no longer publishes).
    - The same hazard exists in any process that loads two copies of `Ashlar.Abstractions`; it is now a Known-limits line in `docs/EgressInventory.md`, which also says that the Debug log subscribes only to the copy `Ashlar.Infrastructure` binds, so a second copy with no subscriber of its own and a dark event source records nothing (adversarial finding A2).
- **Records**:
  - `ci/egress-inventory.tsv`: +1 row, the hop's `new HttpMessageInvoker(` pinned `http.new` `Exempt:GuardImpl` (`src/Ashlar.Abstractions/Security/Egress/` only, as F3 requires), citing `:46`. The `EgressHttp.cs` row's line numbers are re-derived (`:57`, `:78`). On the tree merged with master `3196ba1` (4.1 added one http.new occurrence to an existing row) there are **86 rows and 150 occurrences, 48 of them guarded**; 35 `Exempt:`, 33 `-`, 10 `Factory`, 4 `Upstream:`, 4 `Governance`; http.new 20 (14 rows).
  - `docs/EgressInventory.md`: the PR 3 bullet's "nothing refuses" and the mode bullet's "(PR 4.6: plumbing only, nothing refuses)" now say the guard refuses nothing, and the mode bullet says the netstandard2.0 refusal is the runtime's and holds in every mode (merge check finding 1); the gap-1 bullet describes the refusal (no record; replacing a `Wrap` handler's `InnerHandler` removes the hop) and says that on .NET Framework, classic Mono and Unity nothing is refused but the hop is there; two new Known-limits lines: a chain walker stops at the hop on the netstandard2.0 asset (Grpc.Net.Client's handler-type detection over a `Wrap` handler, for example), and two copies of `Ashlar.Abstractions` in one process (one event source goes dark; the Debug log subscribes only to the copy `Ashlar.Infrastructure` binds).
  - `docs/SdkCompatibilityPolicy.md`: a new section, "Target frameworks and the egress guard": "evaluates every request it is handed (redirects: see `docs/EgressInventory.md`)", the refusal wherever the asset runs on .NET 5 or later, "classic Mono" (check finding 8); and that on every runtime this asset puts the hop under the guard handler, so a walker of `DelegatingHandler.InnerHandler` stops at it (adversarial finding A1).
  - `CHANGELOG.md`: the 4.6 headline ("Every profile still reports, and the guard refuses nothing") and a sentence that the netstandard2.0 refusal is the runtime's and holds in every mode; the 4.1 and 3b lead-ins say "the guard refuses nothing" (merge check finding 1). The 4.2 text is folded into the existing Unreleased `### Added` entry for `EgressHttp`, which has not shipped (it is in `PublicAPI.Unshipped.txt`); no `### Changed` bullet (check finding 9). It says nothing is refused on .NET Framework, classic Mono and Unity, but the hop is there, so a walker of `InnerHandler` stops at it (adversarial finding A1).
  - `docs/specs/SPEC-007-…md`: the "Gaps carried to PR 4" bullet says 4.2 closes the synchronous-`Send` gap (refused, no record: the owner's 2026-10-06 amendment of D31), 4.1 closed the Host-records gap, and redirects stay open for 4.3 (check finding 2, re-merged with 4.1's wording); the "**PR 4.2** (this PR)" bullet, a sibling of the "**PR 4.1** (#717, `bbc5d71`)" and "**PR 4.6** (#718, `3196ba1`)" bullets after the PR 4 plan, with the no-record choice and its reason; the 4.6 bullet's "Nothing refuses yet." becomes "The guard refuses nothing yet; the netstandard2.0 asset's synchronous-`Send` refusal (PR 4.2) is the runtime's and holds in every mode." (merge check finding 1); the decisions-log row of 2026-10-06.
  - `ci/cert-gate-assertions.md`:
    - row 64 gains the twin, the class name, the counts (86 rows, 150 occurrences, 48 guarded, 35 `Exempt:`) and the measured floors (2,134 files, 150 occurrences, 67 docs rows at PR 4.2, after 4.6's 2,133, 149 and 67; re-measured in the container at the merged head `eb88bc6`); it says no record is published at build and the inner handler is reachable through the hop's `Inner`;
    - the PR 4.2 sentence follows the end of the "The twins execute it:" list, so the recursion and `factory:x` items stay in that list and do not read as things the netstandard2.0 twin shows (merge check finding 2);
    - the "What breaks" cell gains the netstandard2.0 sentence, and its "nothing refuses; enforcement is PR 4" says "the guard refuses nothing" (merge check finding 1);
    - the `Tests/Certification` count goes from master's 127 to 128 `.cs` files (131 entries), recounted with `git ls-files` on the tree merged with `3196ba1`.
  - `EgressGuardConventionTests.cs`: the floors' doc comment gains 4.2's sentence after 4.1's and 4.6's (2,134 files, 150 occurrences, http.new 20, of which 4 are the guard's own), as it asks.
  - `docs/knowledge-graph.{json,md}`: regenerated after `git add`, on the merged tree.

No `PublicAPI.Unshipped.txt` change: no public signature changes. No change under `application/`.

## Behaviour changes, and what stays report-only

| Where | Before | After |
|---|---|---|
| netstandard2.0 asset on .NET 5 or later: synchronous `Send` through `EgressHttp.CreateClient` / `Wrap` | forwarded to the inner handler, unevaluated, no record | `NotSupportedException` naming `SynchronousSendRefusedOnNetstandard20Asset`; the inner handler sees nothing; no record |
| the same: building such a client or handler | no record | no record (unchanged) |
| the same: `EgressHttp.CreateDelegatingHandler` | built | `PlatformNotSupportedException` (after the `ArgumentNullException` checks) |
| the same: `SendAsync` | evaluated once | evaluated once; one extra in-process hop; the runtime's request telemetry may count a send twice under an outer `HttpMessageInvoker` |
| netstandard2.0 asset on .NET Framework, classic Mono, Unity | evaluated (no synchronous `Send` exists) | unchanged (`SendAsync` still evaluated once and recorded), plus the hop: one extra in-process step, and a `Wrap` handler's `InnerHandler` is the hop, so a walker of `DelegatingHandler.InnerHandler` stops at it; nothing refused, no extra record |
| net8.0 / net10.0 assets (every Ashlar host) | unchanged | unchanged |
| 4.6's startup line, every asset (log event 7302; standard error once when the mode is not plain report) | "… Records only: nothing is refused yet (SPEC-007 PR 4)." | "… Records only: the guard refuses nothing yet (SPEC-007 PR 4)." (text only; when and where it is written is unchanged) |

- **This is in every mode, not only enforce (D31).** An unevaluated send makes even report-mode records incomplete, and the exposed code is out-of-support external code.
- **Everything else stays report-only.** The guard still refuses nothing: `EgressGuard` is unchanged by this PR, and so is every route. The one refusal is the asset's, made by the runtime's base `Send`.

## Phase B fixes (the phase A check's findings)

| # | Finding | Resolution |
|---|---|---|
| 1 | [medium] the build-time record departs from the design and misleads operators | The owner's decision (2026-10-06, amends D31): the record is dropped; `PublishNoDecision` and `NotEvaluatedBasis` deleted; the twin pins "no record at build" (red first, M8) |
| 2 | [medium] SPEC-007's "Gaps carried to PR 4" bullet still calls the gap open | Rewritten: closed by 4.2 (refused, no record); after the master merge it also says 4.1 closed the Host-records gap, and redirects stay open for 4.3 |
| 3 | [medium] `EgressDecision.Fault` and `CurrentBasis` docs | `Fault` needs nothing now (no non-exception fault is published). `CurrentBasis` gains the faulted basis 3a already emitted (doc only) |
| 4 | [low] the twin did not pin six fields of the record | Moot: no record |
| 5 | [low] collision with 4.6's `EgressDecision` constructor and mode | Moot: `PublishNoDecision` is deleted, so 4.2 constructs no `EgressDecision` |
| 6 | [low] the hop is an `HttpMessageHandler`, so a chain walker stops there; replacing a `Wrap` handler's `InnerHandler` removes it | Internal `Inner` accessor, pinned by the shape fact (red first, M7); `Wrap`'s remarks forbid replacing `InnerHandler`; `docs/EgressInventory.md` says so too |
| 7 | [low] build-core not run with the new `SetTargetFramework=netstandard2.0` reference | Run in the container: `dotnet build Ashlar.LocalDevCore.slnf`, no `-f` (see Testing) |
| 8 | [low] `docs/SdkCompatibilityPolicy.md` overclaims and names the wrong population | Reworded as the check asked |
| 9 | [low] a `### Changed` entry for an API that never shipped | Folded into the `### Added` entry; the `Changed` bullet dropped |

### Phase B adversarial pass

| # | Finding | Resolution |
|---|---|---|
| A1 | [low] "on .NET Framework, classic Mono and Unity nothing changes" overclaims: `Guarded` puts the hop in unconditionally under `#if NETSTANDARD2_0`, so on every runtime that binds that asset each `SendAsync` takes one extra step and a `Wrap` handler's `InnerHandler` is the hop, which an external walker of `DelegatingHandler.InnerHandler` (Grpc.Net.Client's handler-type detection, for example) cannot step through | Confirmed. Reworded in `EgressHttp`'s class and `Wrap` remarks, the `docs/EgressInventory.md` gap-1 bullet, `docs/SdkCompatibilityPolicy.md` and CHANGELOG; a new Known-limits line in `docs/EgressInventory.md` for chain walkers on the netstandard2.0 asset. Docs only (`da5bae9`). |
| A2 | [low] the two-copies Known-limits line says each copy's decisions reach "the Debug log included", but the Debug log's only production subscription (`EgressDecisionLoggerSubscription`) binds whichever copy `Ashlar.Infrastructure` resolves | Confirmed. Reworded: a second copy with no subscriber of its own and a dark event source records its decisions nowhere. Docs only (`da5bae9`). |
| A3 | [low] this body: the behaviour-table row read as "nothing recorded"; the testing strategy put net10.0 in the required check; the expected sibling conflicts were stale | Confirmed and fixed here: the row says `SendAsync` is still evaluated and recorded, with no extra record; the strategy says cert-gate runs net8.0 only and net10.0 was a filtered run; the expected conflicts (Records, below) are re-derived against the current sibling heads, and 4.6 now includes `EgressGuardConventionTests.cs`. |

**Integrator option, not taken:** put the hop in only when `RuntimeHasSynchronousSend`, which would make "nothing changes on .NET Framework, classic Mono and Unity" literally true. The lane kept the hop unconditional, so the refusal does not depend on the reflection probe: if the probe ever missed a runtime's synchronous `Send`, a conditional hop would let that `Send` out unevaluated, while the unconditional hop still refuses it. The cost is the walker limit above. The integrator may choose otherwise; it is not an owner decision.

### Merge check (at `eb88bc6`)

| # | Finding | Resolution |
|---|---|---|
| 1 | [low] 4.6's unqualified "nothing refuses" (SPEC-007 4.6 bullet, `docs/EgressInventory.md` mode bullet, CHANGELOG 4.6 headline) sits beside 4.2's refusal; the startup line `AnnounceOnce` writes from the netstandard2.0 asset says "nothing is refused yet" in a process where a synchronous `Send` is refused | Confirmed. The three records now limit the claim to the guard and say the netstandard2.0 refusal is the runtime's and holds in every mode; the same wording went into `docs/EgressInventory.md`'s PR 3 bullet, the CHANGELOG 4.1 and 3b lead-ins, row 64's "What breaks" cell and `EgressEnforcement`'s remarks. The startup line now says "the guard refuses nothing yet": twin first (`71092b8`, red **4/40**), fix `09d344c`, M9 |
| 2 | [low] row 64: the recursion and `factory:x` items followed on inside the PR 4.2 sentence, so they read as things the netstandard2.0 twin shows | Confirmed (`EgressHttpNetstandard20TwinTests.cs` has 0 matches for `recurs`, `factory:x` or `AddAshlarEgressGuard`). The PR 4.2 sentence now follows the `factory:x` item, and the list is master's again |
| 3 | [low] this body called the no-record choice "integrator decision 1" while its own section and SPEC-007's decisions log say the owner made it | Confirmed. The section is "Owner decision (2026-10-06, amends D31)", and the summary and phase B row 1 say "the owner's decision" |
| 4 | [low] Expected conflicts said some come from 4.1 and 4.6, but 4.4 already has 4.1 | Confirmed: `bbc5d71` is an ancestor of `4de6bc7` (its `04adf42` merged #717) and `3196ba1` is not. Now "some of these come from 4.6". `git merge-tree` of `09d344c` with `4de6bc7` gives the same five conflicting files |

## Testing

All builds and tests ran in the Linux devtest container through `scripts/test-in-container.sh` and `scripts/mutation-check.sh`. Nothing ran on the host except the repo gates and the knowledge-graph builder, which are Python and shell.

| Check | Result |
|---|---|
| **Red at base (phase A).** The twin and its harness alone on the base `79e988c` (scratch commit `b01f57b`), net8.0 | **5 failed / 3 passed / 8.** For the client and for `Wrap`, the gap is observed directly: "expected `System.NotSupportedException` … (observed: returned 202, 1 inner send(s), 0 decision(s)), but found `<null>`". |
| **Red first (phase B).** The phase B twin alone (`92b02c1`, on the phase A head), net8.0 | **4 failed / 4 passed / 8**: `Building_a_client_or_handler_publishes_no_record` (found the build record), both `A_synchronous_Send_…` (their `build.records` held the record), and `Every_client_and_Wrap_handler_puts_the_hop_…` (found "`EgressGuardHandler > SynchronousSendRefusedOnNetstandard20Asset`": no `Inner` to walk). Green at the fix `9623ef8`: **8/8**. |
| Full cert-gate (`scripts/run-cert-gate.sh`) at the merged head `4082dc1`, net8.0 | **2550/2550**, 0 skipped (the skip guard matches the baseline; the zero-test guard expected at least 2545). Unchanged from phase A: the twin still has 8 facts, and master added no cert-gate test. M1's green run, over the same filter at the same commit, also passed 2550/2550. |
| Full cert-gate again at the pushed head `da5bae9` (docs-only commit: XML doc comments, Markdown, one TSV citation), net8.0 | **2550/2550**, 0 skipped; Abstractions built on netstandard2.0 and net8.0 with the new doc comments |
| build-core (`dotnet restore` + `dotnet build Ashlar.LocalDevCore.slnf --no-restore -v minimal`, no `-f`, as `build-gate.yml` runs it) at `4082dc1`: both TFMs of Tests.Infrastructure, the CLI, and Abstractions on netstandard2.0, net8.0 and net10.0 with its analyzers | **succeeded, 0 errors**; the 2 warnings are the harness's SourceLink artefact (`/src-mirror`), which `scripts/test-in-container.sh` documents |
| The twin (8), `EgressHttpHandlerTwinTests` (the net8.0-asset twin, 17) and `EgressGuardDecisionTests` (137) at `4082dc1` | **net8.0 162/162** (each mutation's green run), **net10.0 162/162** |
| Convention scan (F6, F8; measured in phase A on the same production tree, which phase B's first merge of master did not change) | 2,131 files scanned, 149 occurrences examined (http.new 19), 67 docs rows |
| `scripts/ci/run-repo-gates.sh` (shellcheck on `PATH`) at `4082dc1` and again at `da5bae9` | **26/26** both times (master's #714 added `tests/scripts/handoff-scripts.test.sh`), the knowledge-graph byte-compare included |
| **After merging master `3196ba1` (4.1 #717, 4.6 #718): head `eb88bc6`** | |
| Full cert-gate (`scripts/run-cert-gate.sh`) at `eb88bc6`, net8.0 | **2706/2706**, 0 skipped (the skip guard matches the baseline; the zero-test guard expected at least 2701) |
| The twin (8), `EgressHttpHandlerTwinTests` (17), `EgressGuardDecisionTests` (148) and `EgressGuardConventionTests` (194) at `eb88bc6` | **net8.0 367/367**, **net10.0 367/367** |
| Convention scan, measured in those runs (`ScannedFiles`/`ExaminedOccurrences`/`DocsRows`, both TFMs) | **2,134 files scanned, 150 occurrences examined (http.new 20: guarded 13, 14 files), 67 docs rows**: the numbers row 64 and the floors comment state |
| build-core (`dotnet restore` + `dotnet build Ashlar.LocalDevCore.slnf --no-restore -v minimal`, no `-f`) at `eb88bc6`, with the `SetTargetFramework=netstandard2.0` reference | **succeeded, 0 errors**; the 2 warnings are the SourceLink `/src-mirror` artefact |
| `Ashlar.Abstractions` rebuilt alone (`--no-incremental`) at `eb88bc6` on netstandard2.0, net8.0 and net10.0 | **0 warnings, 0 errors** |
| `scripts/ci/run-repo-gates.sh` (shellcheck on `PATH`) at `eb88bc6` | **26/26**, the knowledge-graph byte-compare included |
| **Merge check fixes: head `09d344c`** | |
| **Red first.** The four startup-line pins changed alone (`71092b8`, on `eb88bc6`), net8.0, `EgressModeProcessBindingTests` | **4 failed / 36 passed / 40**: the four startup-line facts (they differ at index 89, 89, 111 and 108), each between "…Records only: nothing is refused yet (SPEC-007 PR 4)." and the expected "…the guard refuses nothing yet…" |
| Full cert-gate (`scripts/run-cert-gate.sh`) at `09d344c`, net8.0 | **2706/2706**, 0 skipped (the skip guard matches the baseline; the zero-test guard expected at least 2701) |
| `EgressModeProcessBindingTests` + `EgressModeResolutionTests` at `09d344c`, net10.0 | **103/103** (net8.0: M9's green run, 103/103) |
| `scripts/ci/run-repo-gates.sh` (shellcheck on `PATH`) at `09d344c` | **26/26**, the knowledge-graph byte-compare included (the regenerated graph did not change: no file was added) |

### Mutation checks

Every row ran through `scripts/mutation-check.sh` on the committed head. Each one: clone the head, apply one replacement, prove it applied, run red, restore, check that `git status --porcelain` is empty, run green. "red a/b" is failed out of total. Every green run passed all of its tests.

All seven ran at `4082dc1` on net8.0. The head after it, `da5bae9`, changes only XML doc comments, Markdown and one TSV citation, so no mutation was re-run there. M1 ran over the full cert-gate filter, because the design asks for the mutation to "go red in cert-gate". M2, M4, M5, M7 and M8 ran over the three egress classes (`EgressHttpNetstandard20TwinTests|EgressHttpHandlerTwinTests|EgressGuardDecisionTests`). M6 ran over the twin class alone, because its red needs a process in which nothing has enabled the guard's source first. Phase A's M3 (delete the hop's record) is gone with the record; M7 and M8 are new in phase B.

| id | mutation | red | green | what went red |
|---|---|---|---|---|
| M1 | drop the hop: `Guarded` returns `new EgressGuardHandler(inner, …)` on netstandard2.0 too (the design's mutation) | **3/2550** | 2550/2550 | `Every_client_and_Wrap_handler_puts_the_hop_…`, `A_synchronous_Send_through_CreateClient_…`, `A_synchronous_Send_through_a_Wrap_handler_…` |
| M2 | invert the factory refusal: `if (!…RuntimeHasSynchronousSend)` | 1/162 | 162/162 | `CreateDelegatingHandler_is_refused_on_a_runtime_with_a_synchronous_Send_…` |
| M4 | the hop's invoker does not own the inner handler (`disposeHandler: false`) | 1/162 | 162/162 | `The_client_and_the_Wrap_handler_still_own_the_inner_handler_through_the_hop` |
| M5 | the runtime check looks for `"SendSync"` (a synchronous `Send` is never detected) | 1/162 | 162/162 | `CreateDelegatingHandler_is_refused_…` |
| M6 | the harness skips `InitializeTheGuardsEventSource()` | 1/8 | 8/8 | `With_the_isolated_copy_loaded_the_guards_own_event_source_still_writes_every_decision` |
| M7 | the hop's `Inner` is not the inner handler (`Inner = this;`) | 1/162 | 162/162 | `Every_client_and_Wrap_handler_puts_the_hop_…` |
| M8 | a build-time record comes back: `Guarded` evaluates the family and site before it builds the handler | 3/162 | 162/162 | `Building_a_client_or_handler_publishes_no_record` and both `A_synchronous_Send_…` (their `build.records`) |

M1 reddens 3 tests where phase A's reddened 4: phase A's fourth was the build-record test, and with no record a build publishes nothing whether or not the hop is there. M5 reddens 1 where phase A's reddened 4, for the same reason: `RuntimeHasSynchronousSend` now gates only the factory-handler refusal.

**M1 again at the merged head `eb88bc6`** (the done-when mutation: the hop dropped from `Guarded`, now at `EgressHttp.cs:142`), on net8.0, over `scripts/test-in-container.sh`'s default filter, the `Tests.Certification` namespace (2702 tests; the cert-gate filter adds 4 GenerationSafety and `AstMutationEngineTests` tests, none of them egress). Killed by the same 3 twin facts as at `4082dc1`: `Every_client_and_Wrap_handler_puts_the_hop_…`, `A_synchronous_Send_through_CreateClient_…` and `A_synchronous_Send_through_a_Wrap_handler_…`; the restore left `git status --porcelain` empty and the green run passed 2702/2702.

```
mutation m1-drop-the-hop-eb88bc69: KILLED red=failed:3/2702 green=passed:2702/2702 ref=eb88bc6983204b98ab65f98c212c4527d4f6d8be
```

**M9 at `09d344c`** (merge check finding 1): the startup line says "nothing is refused yet" again, in `EgressEnforcement.StartupLine`, net8.0, over `EgressModeProcessBindingTests|EgressModeResolutionTests`. Killed by the four startup-line facts (`A_later_stricter_AddAshlar_on_the_same_collection_…`, `A_later_AddAshlar_in_an_AirGapped_container_…`, `The_hosted_activator_logs_one_line_…`, `A_mode_other_than_plain_report_is_written_to_standard_error_once`); the restore left `git status --porcelain` empty.

```
mutation m9-startup-line-says-nothing-is-refused: KILLED red=failed:4/103 green=passed:103/103 ref=09d344ca2cbbdfabea0b405d861bcee65a1dff26
```

M2 to M8 were not re-run at `eb88bc6`: the merge of master changed none of the files they mutate or the twin they read (`EgressHttp.cs`, `SynchronousSendRefusedOnNetstandard20Asset.cs`, `EgressHttpNetstandard20TwinTests.cs`, `EgressGuardHandler.cs` and the test csproj are byte-identical at `4e34ded` and `eb88bc6`), and the three egress classes and the convention class passed on both TFMs there. `09d344c` changes none of those files either.

Verbatim summary lines at `4082dc1`:
```
mutation m1-drop-the-hop: KILLED red=failed:3/2550 green=passed:2550/2550 ref=4082dc1fe060f4caa601e93b65f7fcb3629f3840
mutation m2-factory-handler-not-refused: KILLED red=failed:1/162 green=passed:162/162 ref=4082dc1fe060f4caa601e93b65f7fcb3629f3840
mutation m4-hop-does-not-own-inner: KILLED red=failed:1/162 green=passed:162/162 ref=4082dc1fe060f4caa601e93b65f7fcb3629f3840
mutation m5-sync-send-not-detected: KILLED red=failed:1/162 green=passed:162/162 ref=4082dc1fe060f4caa601e93b65f7fcb3629f3840
mutation m6-no-event-source-init: KILLED red=failed:1/8 green=passed:8/8 ref=4082dc1fe060f4caa601e93b65f7fcb3629f3840
mutation m7-hop-inner-not-the-inner-handler: KILLED red=failed:1/162 green=passed:162/162 ref=4082dc1fe060f4caa601e93b65f7fcb3629f3840
mutation m8-build-time-record-back: KILLED red=failed:3/162 green=passed:162/162 ref=4082dc1fe060f4caa601e93b65f7fcb3629f3840
```

The hop is built through a static `Over(...)` on purpose, so that M1 compiles. If `EgressHttp` held the only `new SynchronousSendRefusedOnNetstandard20Asset(…)`, dropping it would leave an internal class with no instantiation, which is CA1812, an error under `TreatWarningsAsErrors`; the run would then be INVALID, not red.

**Not observed red, stated:**
- The `false` branch of `RuntimeHasSynchronousSend` (.NET Framework, classic Mono, Unity) needs a runtime the devtest image does not have; nothing is refused there by construction.
- The S4 claim that the runtime's request telemetry may count a send twice under an outer `HttpMessageInvoker` on .NET 5–7 is not tested; it is worded as "may".
- The `Wrap` remarks' warning about replacing `InnerHandler` is documentation, not a check.

### Testing strategy (blast radius)

This PR changes `Ashlar.Abstractions`. On the netstandard2.0 asset it changes behaviour; on net8.0 and net10.0 it changes docs only. It also changes the cert-gate test project's build. So it ran:
- [x] The twin of the changed asset, red at the base and green after: in the required check (cert-gate), which runs net8.0 only, and in a filtered run of the three egress classes on net10.0, which no required check runs.
- [x] The existing net8.0-asset twin (`EgressHttpHandlerTwinTests`), unchanged and green: that asset's behaviour is pinned as before.
- [x] The full cert-gate, which builds the netstandard2.0 inner build of Abstractions through the new `ProjectReference`.
- [x] build-core's own command, which builds both TFMs of the test project in one invocation with that reference, and Abstractions on all three TFMs with its analyzers.
- [ ] `make kernel-gate` / `kernel-coverage-gate`: not run locally; CI runs them.
- [ ] A real .NET 5–7 runtime: none is in the devtest image. The harness runs the netstandard2.0 IL on the 8.0 and 10.0 runtimes. What matters is which overrides were compiled, not the runtime version, and the first test proves the runtime has the synchronous `Send` that the gap is about.

## Records (for the drift audit)

This PR touches:
- `docs/specs/SPEC-007-security-labels-and-reference-monitor.md`: the "Gaps carried to PR 4" bullet (gap 1 closed by 4.2, gap 3 by 4.1, redirects open for 4.3); the "**PR 4.2** (this PR)" bullet, a sibling of the PR 4.1 and PR 4.6 bullets (fill the PR number and merge SHA at merge); the merge also turned "**PR 4.6** (#718)" into "(#718, `3196ba1`)", and the 4.6 bullet's last sentence now says the guard refuses nothing yet and the netstandard2.0 refusal is the runtime's; the decisions-log row of 2026-10-06 (D31 amended).
- `docs/EgressInventory.md`: the PR 3 bullet and the mode bullet's title ("the guard refuses nothing") and a sentence in the mode bullet (the netstandard2.0 refusal is the runtime's, in every mode); the gap-1 bullet (after the merge it sits between 4.6's mode bullet and the redirects bullet, which keeps 4.1's `OllamaHttpChatClient.cs:170`); Known limits (a chain walker stops at the hop on the netstandard2.0 asset; two copies of `Ashlar.Abstractions`). The opening paragraph's re-derivation sentence names 4.1 and 4.6 only: 4.2's edited files are cited by line only in the TSV.
- `docs/SdkCompatibilityPolicy.md`: the new "Target frameworks and the egress guard" section (including the hop on every runtime of the netstandard2.0 asset).
- `ci/egress-inventory.tsv`: +1 row (the hop, `:46`); the `EgressHttp.cs` row's line numbers (`:57`, `:78`), re-checked at the merged head.
- `ci/cert-gate-assertions.md`: row 64 (twin, class list, counts 86/150/48/35, floors 2,134/150/67 "at PR 4.2", "What breaks" including "the guard refuses nothing"; the PR 4.2 sentence after the "The twins execute it:" list); the Certification count paragraph (128 `.cs`, 131 entries, as of 2026-10-06). Row 56 is unaffected: the twin writes no environment variable and names no egress process state.
- `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressGuardConventionTests.cs`: the floors' doc comment (4.2's sentence after 4.1's and 4.6's).
- `CHANGELOG.md`: Unreleased `### Added`, the `EgressHttp` entry (including the hop on .NET Framework, classic Mono and Unity); `### Changed`, the 4.6 headline and its new last sentence, and the 4.1 and 3b lead-ins ("the guard refuses nothing").
- Runtime text: 4.6's startup line (`EgressEnforcement.StartupLine`), "the guard refuses nothing yet", pinned in `EgressModeProcessBindingTests` (four facts and the class remarks).
- XML docs: `EgressHttp` (class remarks, `Wrap` remarks, `CreateDelegatingHandler` `<exception>`), `EgressGuardHandler` remarks, `EgressDecision.CurrentBasis`, `EgressEnforcement` (internal: class remarks, `ReportMode`).
- `docs/knowledge-graph.{json,md}`: regenerated.

Merged in: master `3196ba1` (PR 4.1 #717 `bbc5d71`, PR 4.6 #718 `3196ba1`), merge commit `eb88bc6`; the merge check's fixes follow it (`71092b8` twin, `09d344c` fix), head `09d344c`. Counts to recount if another PR 4 lane merges first: row 64's TSV sums, the convention test's floors comment, and the Certification count paragraph (4.4 adds twin files).

Expected conflicts, from `git merge-tree` of `09d344c` (the same set as at `eb88bc6`) with the remaining sibling head, 4.4 (`4de6bc7`, which already has 4.1 but has not merged master `3196ba1` yet, so some of these come from 4.6): `ci/cert-gate-assertions.md`, the SPEC-007 spec, the `EgressGuardConventionTests.cs` floors comment and `docs/knowledge-graph.{json,md}`. CHANGELOG, `docs/EgressInventory.md`, `EgressGuard.cs` and `EgressGuardDecisionTests.cs` merge cleanly.

## Checklist

- [x] Repo gates pass (26/26 at `09d344c`). The cert-gate passes in the devtest container on net8.0 (2706/2706 at `09d344c`).
- [x] Documentation and records updated: `docs/EgressInventory.md`, `docs/SdkCompatibilityPolicy.md`, `ci/egress-inventory.tsv`, `ci/cert-gate-assertions.md`, CHANGELOG, SPEC-007 status, the knowledge graph.
- [x] No `TODO` or `NotImplementedException`.
- [x] Behaviour change documented (CHANGELOG `### Added`, since `EgressHttp` has not shipped). No public API change.

## Release

- [x] Not a versioned release; skip.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01NNv8ZAWjwkJYwgB4yFMLBs
