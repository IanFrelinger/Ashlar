## Summary

SPEC-007 PR 4.2 closes the first of the three gaps PR 4 has to close before the guard enforces: **a synchronous `Send` on the netstandard2.0 asset of `Ashlar.Abstractions`** (PR 4 design §2.6, gap 1, option C; engineering default D31, which the owner left standing on 2026-10-05).

That asset, which .NET 5–7 apps resolve, is compiled against a `DelegatingHandler` with no `Send` to override. So a synchronous `HttpClient.Send` or `HttpMessageInvoker.Send` through an `EgressHttp` client or handler reached the runtime's `DelegatingHandler.Send`, which forwarded it to the inner handler **unevaluated**. The twin shows it at the base: that `Send` returned the inner handler's response, the inner handler saw 1 request, and no decision was recorded.

After this PR, on that asset:
- a synchronous `Send` through `EgressHttp.CreateClient` or `EgressHttp.Wrap` is **refused with `NotSupportedException` before anything is sent**: 0 requests reach the inner handler;
- `SendAsync` is still evaluated exactly once;
- building such a client or handler publishes **one `NoDecision` record** (`Fault` `SynchronousSendUnsupported`) naming its family and site, so the refusal reaches the operator log;
- `EgressHttp.CreateDelegatingHandler` throws `PlatformNotSupportedException`.

`docs/SdkCompatibilityPolicy.md` now says that full egress-guard coverage needs the net8.0 or later asset, and that the netstandard2.0 asset is for .NET Framework, Mono and Unity. There is no `buildTransitive` error and no end-of-life target framework.

**Who is affected.** Only code outside this repository that runs on .NET 5, 6 or 7, calls `EgressHttp` itself, and sends synchronously. .NET 5–7 are out of support. Every Ashlar host binds the net8.0 or later asset, no Ashlar source makes a synchronous HTTP `Send`, and the net8.0 and net10.0 assets do not change.

These are classification-style controls inside the runtime. They are not an accredited cross-domain solution.

**One deviation from the design, stated (see "Deviation" below):** the design says the hop publishes the `NoDecision` record when it refuses. No Ashlar code runs on that synchronous path, so the hop publishes the record **when it is built** on a runtime that has a synchronous `Send`.

## Changes

- **`src/Ashlar.Abstractions/Security/Egress/SynchronousSendRefusedOnNetstandard20Asset.cs`** (new, `internal sealed`, compiled only under `#if NETSTANDARD2_0`):
  - It derives from `HttpMessageHandler`, not `DelegatingHandler`, and overrides only `SendAsync`, which it forwards through an owned `HttpMessageInvoker(inner, disposeHandler: true)`. The request, the token and the response instances are unchanged.
  - A synchronous `Send` therefore reaches the runtime's base `HttpMessageHandler.Send`, which throws `NotSupportedException`. Its message names the hop's type, so the refusal explains itself.
  - `RuntimeHasSynchronousSend` is read once, by reflection: does the runtime's `HttpMessageHandler` have a non-public instance `Send(HttpRequestMessage, CancellationToken)`? It is `true` on .NET 5 and later, and `false` on .NET Framework, Mono and Unity.
  - `Over(inner, family, site, guard)` builds the hop. Where `RuntimeHasSynchronousSend` is true, the hop publishes the refusal record through the guard it was given. When that guard is an `EgressGuard`, the record carries its profile; otherwise it carries `ProcessDefault`'s.
- **`EgressHttp`**: `CreateClient` (both overloads) and `Wrap` build the guard handler through one private `Guarded(...)`, which puts the hop under it on netstandard2.0 only. `CreateDelegatingHandler` throws `PlatformNotSupportedException` there after its argument checks, with a message that names the net8.0 asset. Its XML docs carry the new `<exception>`. The class remarks no longer call the gap open.
- **`EgressGuard.PublishNoDecision(family, site, fault)`** (new, internal) publishes a record with these fields:
  - destination `unknown`, class `Unknown`;
  - `Access` `default` (`NoDecision`), current `SystemHigh`;
  - the basis "not evaluated: the egress is refused before it can be evaluated";
  - the guard's profile.

  It never throws. It compiles on every asset, so a change to the record's shape fails every build, not only the netstandard2.0 one.
- **`EgressGuardHandler`**: the remarks describe the refusal instead of the open gap.
- **Test harness**:
  - `Ashlar.Tests.Infrastructure.csproj` gains a second `ProjectReference` to `Ashlar.Abstractions`, with `SetTargetFramework=netstandard2.0`, `ReferenceOutputAssembly=false`, `Private=false` and `OutputItemType`.
  - A target copies that build to `abstractions-netstandard2.0/` in the output. It fails the build with a message if the build was not resolved.
  - `EgressHttpNetstandard20TwinTests` (cert-gate) loads it into an `AssemblyLoadContext` of its own, together with a second copy of the test assembly, so `Netstandard20Driver`'s ordinary C# calls to `EgressHttp` bind to the netstandard2.0 build.
  - So the design's fallback (a shell-lint driver) was not needed, and `run-cert-gate.sh`, `cert-gate-config.sh`, `cert-gate.yml` and both `READINESS_PATHS` copies are untouched.
  - **Event-source hygiene.** The isolated copy brings a second `Ashlar-Egress` `EventSource` into the cert-gate process. .NET 8 builds a source's event descriptors the first time it is enabled, and it refuses to while another live source has the same name and GUID (`EventSource_EventSourceGuidInUse` in `EnsureDescriptorsInitialized`, unless the `System.Diagnostics.Tracing.EventSource.AllowDuplicateSourceNames` switch is set). The refusal is only reported out of band. The effect was that if the isolated copy published before anything had enabled the guard's own source, no listener could ever enable that source again.
    - The first full cert-gate run hit this: `EgressGuardDecisionTests.A_credential_holding_a_delimiter_reaches_no_record_field` failed.
    - A probe, 20 runs per arm: that class beside the twin failed **18/20**; alone it failed **0/20**. Filtering the decision test's listener to its own assembly did not help (**14/20** failed), which ruled out the listener as the cause.
    - The harness therefore enables the guard's own source once, before it loads the isolated copy, and asserts that the source is enabled. After the fix, beside the twin, it failed **0/20** (144/144 passed in each run). `EgressGuardDecisionTests` is unchanged.
    - A new fact pins this: with the isolated copy loaded, the guard's own source still writes every decision. It is mutation M6.
- **Records**:
  - `ci/egress-inventory.tsv`: +1 row, the hop's `new HttpMessageInvoker(` pinned `http.new` `Exempt:GuardImpl` (`src/Ashlar.Abstractions/Security/Egress/` only, as F3 requires). The `EgressHttp.cs` note's line numbers are re-derived (`:54`, `:75`). There are now 86 rows and 149 occurrences, 48 of them guarded.
  - `docs/EgressInventory.md`: the gap-1 bullet now describes the refusal.
  - `docs/SdkCompatibilityPolicy.md`: a new section, "Target frameworks and the egress guard".
  - `CHANGELOG.md`: a new `### Changed` entry under Unreleased. The 3a `### Added` entry no longer calls the synchronous `Send` unevaluated.
  - `docs/specs/SPEC-007-…md`: one status line for 4.2, under the PR 4 plan.
  - `ci/cert-gate-assertions.md`:
    - row 64 gains the twin, the class name, the new counts (86 rows, 149 occurrences, 35 `Exempt:`) and the measured floors (2,131 files, 149 occurrences, 67 docs rows);
    - the "What breaks" cell gains the netstandard2.0 sentence;
    - the `Tests/Certification` count goes from 125 to 126 `.cs` files (129 entries).
  - `EgressGuardConventionTests.cs`: the floors' doc comment restates the measured counts, as it asks.
  - `docs/knowledge-graph.{json,md}`: regenerated after `git add`.

No `PublicAPI.Unshipped.txt` change: no public signature changes. No change under `application/`.

## Behaviour changes, and what stays report-only

| Where | Before | After |
|---|---|---|
| netstandard2.0 asset on .NET 5–7: synchronous `Send` through `EgressHttp.CreateClient` / `Wrap` | forwarded to the inner handler, unevaluated, no record | `NotSupportedException` naming `SynchronousSendRefusedOnNetstandard20Asset`; the inner handler sees nothing |
| the same: building such a client or handler | no record | one `NoDecision` record, `Fault` `SynchronousSendUnsupported`, family and site |
| the same: `EgressHttp.CreateDelegatingHandler` | built | `PlatformNotSupportedException` (after the `ArgumentNullException` checks) |
| the same: `SendAsync` | evaluated once | evaluated once; one extra in-process hop; the runtime's request telemetry may count a send twice under an outer `HttpMessageInvoker` |
| netstandard2.0 asset on .NET Framework, Mono, Unity | evaluated (no synchronous `Send` exists) | unchanged, plus the hop; nothing refused or recorded |
| net8.0 / net10.0 assets (every Ashlar host) | unchanged | unchanged |

- **This is in every mode, not only enforce (D31).** An unevaluated send makes even report-mode records incomplete, and the exposed code is out-of-support external code.
- **Everything else stays report-only.** The guard still refuses nothing: `EgressGuard.Evaluate` and every route are unchanged. The one refusal is the asset's, made by the runtime's base `Send`. The decision mode is still `report`, and the new record carries it.

## Deviation from the design

The design (§2.6 gap 1, row 4.2) says "the hop publishes a `NoDecision` record … so the refusal is in the operator log". **That cannot be done at the moment of the refusal.**
- On netstandard2.0 nothing can override `HttpMessageHandler.Send`. The C# compiler emits `newslot` for a non-`override` virtual, so a same-named method does not take the slot.
- The runtime's `DelegatingHandler.Send` then calls the hop's inherited base `Send` directly. No frame of this assembly is on that stack.

The only hook would be a process-wide `AppDomain.FirstChanceException` handler. It was rejected:
- it would run in every host process for every exception;
- it cannot attribute the refusal to a family, site or request;
- it needs re-entrancy guards.

**What was built instead:** the hop publishes the record **when it is built** on a runtime that has a synchronous `Send`. The record names the family and site whose synchronous sends are refused. The cost is that a client that only ever uses `SendAsync` also leaves one such record, and the record is not one per refused send.

The other option is to drop the record and keep only the runtime's exception (S4's original option C). That is a one-line change in the hop's constructor, and its twin assertions would go with it. This is for the integrator or owner to choose.

## Testing

All builds and tests ran in the Linux devtest container through `scripts/test-in-container.sh` and `scripts/mutation-check.sh`. Nothing ran on the host except the repo gates and the knowledge-graph builder, which are Python and shell.

| Check | Result |
|---|---|
| **Red at base.** The twin and its harness alone on the base `79e988c` (`ad90d84`, the final test file; earlier `fb25fc4`, the same result), net8.0 | **5 failed / 2 passed / 7.** For the client and for `Wrap`, the gap is observed directly: "expected `System.NotSupportedException` … (observed: returned 202, 1 inner send(s), 0 decision(s)), but found `<null>`". The shapes show `EgressGuardHandler > HttpClientHandler`, there is no record, and `CreateDelegatingHandler` threw nothing. The harness proof and the ownership test pass at the base, as they should. |
| The twin (8 facts), `EgressHttpHandlerTwinTests` (the net8.0-asset twin, 17) and `EgressGuardDecisionTests` (137) at the head `HEADSHA` | **net8.0 162/162, net10.0 162/162** |
| Event-source probe: `EgressGuardDecisionTests` plus the twin, 20 runs per arm, net8.0 | before the fix, 18/20 runs failed (alone, 0/20); with the decision test's listener filtered to its own assembly, 14/20 failed; after the fix, **0/20** failed |
| `Ashlar.Abstractions` (netstandard2.0, net8.0, net10.0; `TreatWarningsAsErrors`, `AnalysisMode=All`, the PublicAPI analyzers) | 0 warnings, 0 errors |
| Full cert-gate (`scripts/run-cert-gate.sh`), net8.0 | **CERTGATE_FINAL** |
| Convention scan (F6, F8) | 2,131 files scanned, 149 occurrences examined (http.new 19), 67 docs rows |
| `scripts/ci/run-repo-gates.sh` (shellcheck on `PATH`) | **25/25**, the knowledge-graph byte-compare included |

### Mutation checks

Every row ran through `scripts/mutation-check.sh` on the committed head. Each one: clone the head, apply one replacement, prove it applied, run red, restore, check that `git status --porcelain` is empty, run green. "red a/b" is failed out of total. Every green run passed all of its tests.

MUTATION_TABLE

### Testing strategy (blast radius)

This PR changes `Ashlar.Abstractions`. On the netstandard2.0 asset it changes behaviour; on net8.0 and net10.0 it adds an internal method and changes docs. It also changes the cert-gate test project's build. So it ran:
- [x] The twin of the changed asset, red at the base and green after, in the required check (cert-gate), on net8.0 and net10.0.
- [x] The existing net8.0-asset twin (`EgressHttpHandlerTwinTests`), unchanged and green: that asset's behaviour is pinned as before.
- [x] The full cert-gate, which builds the netstandard2.0 inner build of Abstractions through the new `ProjectReference`.
- [x] Abstractions built on all three TFMs with its analyzers.
- [ ] `make kernel-gate` / `kernel-coverage-gate`: not run locally; CI runs them.
- [ ] A real .NET 5–7 runtime: none is in the devtest image. The harness runs the netstandard2.0 IL on the 8.0 and 10.0 runtimes. What matters is which overrides were compiled, not the runtime version, and the first test proves the runtime has the synchronous `Send` that the gap is about.

## Checklist

- [x] Repo gates pass (25/25). The cert-gate passes in the devtest container on net8.0.
- [x] Documentation and records updated: `docs/EgressInventory.md`, `docs/SdkCompatibilityPolicy.md`, `ci/egress-inventory.tsv`, `ci/cert-gate-assertions.md`, CHANGELOG, SPEC-007 status, the knowledge graph.
- [x] No `TODO` or `NotImplementedException`.
- [x] Behaviour change documented (CHANGELOG `### Changed`). No public API change.

## Release

- [x] Not a versioned release; skip.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01GYPVuHoik1sH5U6gWtSVRT
