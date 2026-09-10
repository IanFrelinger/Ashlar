# kernel-coverage gate: why it never completed

**Status: CLOSED.** The gate runs Domain, **Infrastructure**, and Core.Application, and passes. Infrastructure line coverage has been measured for the first time: **80.3%**, from the first complete run of that suite (1,764 passed / 1 skipped / 1,765 total). Floor set to **80%** as a ratchet; target remains 83. A later round of intermittent instrumentation-load timeouts is documented at the end ("2026-08-13: intermittent timeouts under instrumentation").

Five defects had to be fixed to get a number, and **each was invisible until the one in front of it was fixed**:

| # | defect | why it hid the next one |
|---|---|---|
| 1 | Collectible-`AssemblyLoadContext` crash in the certification mutation engine | killed the process in ~21s |
| 2 | DI cycle hanging API host startup (`registry → self-extend → registry`) | wedged 2 of 2 parallel slots |
| 3 | `AddAshlarFederatedBrickMesh` recursing into its own registration | a test never completed, so the host never exited |
| 4 | `ProviderFactory` doing blocking network I/O in its constructor | stalled a 1,000-iteration stress test |
| 5 | Certification records held in a non-durable in-memory store | `adapt` could never find an admitted brick |

The single most expensive lesson is recorded in the method note at the end: **three conclusions in earlier versions of this document were drawn from runs that had already been truncated, and all three were wrong.**

This document keeps the original investigation below, corrected inline, because the sequence of wrong turns is the useful part.

> **THREE defects, stacked.** Everything below the "Symptom" heading was written when
> only the first was known, and parts of it are wrong — corrected inline.
>
> Each one was invisible until the one in front of it was fixed. That is the real
> lesson of this document: a failure that kills the process early hides everything
> behind it, and every "we ruled that out" conclusion drawn from a truncated run is
> worthless.
>
> 1. **The collectible-`AssemblyLoadContext` crash — FIXED.** The certification
>    mutation engine tore down overlapping collectible load contexts; finalizing them
>    killed the process. Fixed in `BrickMutationEngine` (single owner, serialised
>    teardown, unconditional unload). Proven crash-free across ~2 hours of CI, where
>    the process previously could not survive half a minute.
>
> 2. **A dependency cycle that hung API host startup — FIXED.** Not a deadlock and not
>    environmental:
>
>    ```
>    IBackgroundAgentRegistry factory  -> sp.GetService<ISelfExtendRunner>()
>    ISelfExtendRunner factory         -> sp.GetRequiredService<SelfExtendRunnerAdapter>()
>    SelfExtendRunnerAdapter ctor      -> IBackgroundAgentRegistry   (back to the top)
>    ```
>
>    A full dump caught ~298 repetitions of that pair across 8,340 stack lines, on one
>    thread, with `WebApplicationFactory.get_Services()` blocked on host startup and no
>    exception ever thrown. **Microsoft.Extensions.DependencyInjection cannot detect
>    this**: its circular-dependency check inspects constructor-injected graphs at
>    validation time, and the loop is laundered through two factory lambdas, so the
>    graph passes `ValidateOnBuild` and then recurses at *resolution* time. The
>    parameter being optional does not help — the registry is registered, so DI
>    re-enters the factory rather than passing null. Fixed by deferring the registry
>    behind `Lazy<T>`, registered beside `IBackgroundAgentRegistry` itself so the two
>    can never be wired apart. `ApiDevelopmentHostDiTests` went from unbounded to 1s.
>
>    *Follow-up (not done):* `SelfExtendRunnerAdapter` never calls a single method on
>    the registry — it passes it straight through to `RepoFsToolboxFactory`. That is
>    accidental coupling; segregating the slice the toolbox actually needs would remove
>    the cycle structurally rather than deferring it.
>
> 3. **A process-lifetime leak — OPEN, newly discovered, and the current blocker.**
>    `InfrastructureRoutingGapCoverageTests` passes **7/7 in 588ms** and then the test
>    host **never exits**. `coverlet.msbuild` writes its report only after the host
>    exits, so the coverage step waits forever — no crash, no failing test, no output.
>    Three capped runs died exactly this way, all with **zero** crash signatures:
>    30m20s, 60m20s, 45m21s. **Raising the cap cannot help**, because the process never
>    exits at all. A single trivial test from the same assembly exits cleanly, so this
>    is test-specific rather than assembly-wide. Suspect: the federated-mesh path
>    (`RemoteCatalogBaseUrls: https://peer.example`) starting something with a
>    non-background thread. This is also the cause of the orphaned `testhost` processes
>    that lock output DLLs and silently make `--no-build` runs execute stale binaries.
>
> Infrastructure coverage is therefore still unmeasured, and **no trustworthy
> coverage number for it exists**: every historical run was truncated by the crash at
> a different point, so the 83% floor in the table below was never measured against a
> complete run.

## Symptom

The `kernel-coverage` job runs until GitHub's 6-hour job cap and is auto-cancelled.
Observed on `master` (2026-07-13, 2026-07-14), on `dependabot/*` branches
(2026-08-01), and on every feature branch that reaches the second step. It has
never produced a verdict.

## Root cause

`scripts/ci/kernel-coverage-gate.sh` runs three coverage steps in order:

| Step | Target | Threshold | Behaviour |
|---|---|---|---|
| 1 | `Ashlar.Tests.Domain` → `[Ashlar.Core.Domain]` | 100% line | completes, ~1 min |
| 2 | `Ashlar.Tests.Infrastructure -f net9.0` → `[Ashlar.Infrastructure]` | 83% line | **never returns** |
| 3 | `Ashlar.Tests.Application` → `[Ashlar.Core.Application]` | 67% line | never reached |

Step 2 reproduced locally. The tests themselves are fine:

```
Passed!  - Failed: 0, Passed: 572, Skipped: 0, Total: 572, Duration: 50 s
The active test run was aborted. Reason: Test host process crashed
Test Run Aborted.
```

> **CORRECTION — the paragraph below is wrong on both counts.** It is not a teardown
> crash, and 572 was never the total.
>
> The crash fires **mid-run**, from the finalizer thread after a GC, so where it lands
> depends on GC timing rather than on reaching the end of the suite. Every "total" it
> reported was a truncation artifact: across runs on both frameworks the counts were
> **52, 77, 182, 199, 217, 243 and 461**. 572 was simply how far one run happened to
> get before dying, and it was read as a complete pass.
>
> This mattered. "All tests pass, then it crashes at teardown" framed the problem as
> cosmetic — a cleanup issue after the real work succeeded — when in fact most of the
> suite was never running at all, and a second defect was hiding behind it.

**All 572 tests pass in 50 seconds, then the test host crashes during teardown**
and the run is marked Aborted. The crash is the long-standing
`System.Reflection.LoaderAllocatorScout.Finalize` / `0x80131506` "Internal CLR
error" seen whenever this suite runs, with or without coverage.

The consequence specific to this gate: **coverlet never receives the results, so no
coverage report is written**. Verified — `CoverageReports/infra*` is absent after
the run. The gate therefore cannot produce a verdict no matter how long it waits.

Until now the job had no `timeout-minutes`, so it inherited the 6-hour cap.

## What was ruled out

- ~~**Not a slow or hanging test.** Execution finishes in 50s; the crash is in
  teardown, after the last test.~~
  **WRONG, and this is the entry that cost the most.** There *is* a hanging test. The
  50-second figure came from a run that died partway through, so "execution finishes"
  described a truncated run, and the conclusion ruled out the very thing that is now
  the remaining blocker. Ruling a cause out on evidence from a crashed run is what
  kept the second defect invisible.
- **Not the `xunit.runner.json` copy-directive trap** that made `Ashlar.Tests.CLI`
  silently parallel. This project has the same missing directive, but its stale
  `bin\` copy and its source are behaviourally identical (the only delta is an
  explicit `parallelizeTestCollections: true`, which is also the default). The
  directive was added anyway as hygiene; it changes nothing here.
- **Not solely the collectible-`AssemblyLoadContext` certification path.**
  `MutantAssemblyLoadContext` is the obvious `LoaderAllocator` suspect, and it does
  call `Unload()` correctly. Excluding `AstMutationEngineTests` entirely still
  crashes (432/432 pass, host still dies, still no report). So either another test
  loads assemblies collectibly — `TestRunnerAdapter` uses `Assembly.LoadFrom`, and
  the `UnitTestBridge` path loads assemblies at runtime — or the crash is not
  ALC-specific at all.

## What was changed here

Bounded and low-risk only:

1. `timeout-minutes: 30` on the job. Converts a 6-hour burn into a 30-minute
   failure. Does **not** fix the crash.
2. `<Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />`
   in `Ashlar.Tests.Infrastructure.csproj` — hygiene against config drift.

## Resolution applied

**Option 2 (swap the collector) was tried first and rejected.** Added
`coverlet.collector` plus a runsettings with `Format=cobertura` and
`Include=[Ashlar.Infrastructure]*`, mirroring the old `-p:Include` exactly:

```
Passed! - Failed: 0, Passed: 452, Skipped: 1, Total: 453, Duration: 58 s
The active test run was aborted. Reason: Test host process crashed
Test Run Aborted.
-> no coverage.cobertura.xml produced; command still had to be killed at 560s
```

Collecting out-of-process does not help, because the run is **aborted** — VSTest
discards the collector's attachments exactly as it discarded coverlet's. Both
routes fail identically:

| Route | Tests | Outcome |
|---|---|---|
| `coverlet.msbuild` instrumentation | 572/572 pass | host crashes, **no report** |
| `XPlat Code Coverage` collector | 452/453 pass | host crashes, **no report** |

The crash — not the instrumentation method — is the blocker.

**Option 3 (drop Infrastructure) is what shipped.** The gate now runs Domain
(100%) and Core.Application (67%) only, both of which complete, so it returns a
real pass/fail instead of hanging. `timeout-minutes: 30` is kept as a backstop.

## Resolution: the crash is fixed

Root cause was the certification mutation engine, and it was **not** the leaked,
never-unloaded context everyone assumed. `Unload()` was always being called on the
happy path. The defect was **concurrent teardown of several collectible contexts**:
unload was requested while the caller still held the mutant instance and assembly,
the loop immediately loaded the next mutant into a new collectible context, and
callers ran in parallel because xunit executes collections concurrently. Finalizing
those overlapping `LoaderAllocator`s is what killed the process.

Evidence: dumping `AssemblyLoadContext.All` showed the only collectible contexts in
the process were the unnamed ones holding `MutantBrick_*` assemblies, churning
2 → 3 → 2 as they piled up mid-unload. Making the context non-collectible made
crashing runs clean, which established causality.

Fixed in `BrickMutationEngine`: per-mutant work confined to a `NoInlining` frame that
lets nothing ALC-typed escape, the reflectively-invoked witness fully awaited inside
that frame, `Unload()` moved to an unconditional `finally`, collection driven until
the context is actually released, and a **process-wide** semaphore held across the
whole load/execute/unload/collect sequence so at most one context exists anywhere.
Serialising within a single loop was tried first and was **not** sufficient —
concurrent callers still overlapped. The context stays collectible; making it
non-collectible would swap the crash for an unbounded assembly leak.

Two genuine leaks were fixed alongside: `Unload()` ran only on the success path, so a
throwing witness leaked the context outright; and the temp directory was deleted
while the DLL was still memory-mapped, so the delete silently failed and those
directories accumulated for the life of the process.

## Resolution: all five defects fixed, coverage measured

**Defect 3 — mesh registration recursed into itself.** `AddAshlarFederatedBrickMesh`
resolved `IBrickRegistry` as a fallback for the local registry, but that registration
*is* the last `IBrickRegistry` descriptor, so it re-entered itself. The
`InvalidOperationException` written three lines below to report a missing local
registry was unreachable; resolution recursed instead, with no exception and no stack
overflow. A test therefore never completed, so xunit never signalled
assembly-finished, so the test host never exited, so coverlet never wrote a report —
which is what actually starved this gate. Fixed by resolving only the concrete
`BrickRegistry`.

**Defect 4 — `ProviderFactory` blocked in its constructor.** It warmed the Ollama
manifest with `GetAwaiter().GetResult()`, so *constructing* the factory — and hence
every `IProviderFactory` resolution, host startup included — performed a synchronous
HTTP round trip against a machine that may not be listening. The warm-up is a
deliberate, documented latency optimisation and is kept; it now runs fire-and-forget,
off the calling thread. An earlier attempt deleted it outright, which threw away a
considered design decision to fix a placement bug.

**Defect 5 — certification records were not durable.** `AddCertificationInfrastructure`
registered `InMemoryCertificationRecordStore` unconditionally, so every process began
with an empty admission catalogue and nothing certified earlier could ever be found.
Harmless for a single-process host that certifies as it goes; fatal for the CLI, where
each invocation is a fresh process — `ashlar adapt --store-path ...` could never locate
an admitted brick, and the command's own error text advertises a flow that could not
work. `FileCertificationRecordStore` already existed and was wired nowhere.

*Correction, 2026-08-27: the fix was incomplete.*
`AddCertificationInfrastructure(recordStorePath)` did select the durable store, but the
in-memory fallback was still registered with `AddSingleton` and `AddCertificationGate()`
forwarded no path at all. Because last-registration-wins for `GetRequiredService`, any host
that composed durability and *then* added the gate was reverted to in-memory with no error
and no log line — so defect 5 remained live for every `AddCertificationGate()` caller from
`f43ffcd5` until it was closed by making the fallback `TryAdd` and giving the gate its own
`recordStorePath`.

Durability did not weaken admission, which matters because these records decide whether
generated code may run:

- Records are written signed and **re-verified on load**. The HMAC covers the record
  with the signature field cleared, so editing the admission flags *or* the
  `ContentHash` invalidates it — a mutated record reads as uncertified.
- A record failing verification is reported **absent**, not as an untrusted record, so
  callers refuse the brick and tampering fails **closed**.
- `IsAdmitted`'s flag checks now run only on cryptographically vouched records.
  Previously those flags were the *only* gate, so a hand-edited file claiming
  `Admitted/Signed/PASS` would have been believed — a hole that existed independently
  of persistence.

## Measured coverage and the floor

| assembly | line | branch | method |
|---|---|---|---|
| `Ashlar.Core.Domain` | 100% | 73.36% | 100% |
| **`Ashlar.Infrastructure`** | **80.3%** | 64.48% | 84.92% |
| `Ashlar.Core.Application` | 68.31% | 64.84% | 40.37% |

Job: 13m53s, Infrastructure step 11m45s — comfortably inside the 30-minute cap, which
is therefore kept unchanged.

**Floor set to 80%, as a ratchet: raise it, never lower it.** The old 83% was never
measured against a complete run — it was an aspiration recorded as though it were a
baseline, which is exactly what a floor nobody can check degenerates into. 80 sits just
below the measured figure so ordinary variation does not fail the build. The target
remains 83, and branch coverage at 64.48% shows the headroom is real.

## Tracked follow-ups

- **`SelfExtendRunnerAdapter` interface segregation.** It never calls a method on
  `IBackgroundAgentRegistry` — it hands it straight to `RepoFsToolboxFactory`.
  Segregating that slice would remove the cycle *structurally* rather than deferring it
  behind `Lazy<T>`.
- ~~**`OllamaProvider` still blocks in its constructor** (`RefreshModelsAsync(...)
  .GetAwaiter().GetResult()`). Reached only when something genuinely wants a provider,
  and changing when its manifest populates would alter `IsAvailable`/`Manifest`
  semantics, so it was left alone here.~~ **Fixed in #567:** the constructor performs no
  I/O. Callers load the manifest through `OllamaProvider.InitializeAsync`, and
  `ProviderFactory`'s background warm-up is bounded to 5 s.
- **`TestRunnerAdapter.ExecuteTestAsync`** abandons its `runTask` on the per-test
  timeout path. ~~Latent — never executed today.~~ **No longer latent:** the path
  executed on 2026-08-13 (run 31665068194, attempt 1) and its side effect is worse
  than "abandons" suggests — the given-up-on work keeps running and consuming the
  saturated machine while later tests execute against that load. The per-test timeout
  has been resized so the path is rare again (see the 2026-08-13 section), but the
  abandonment itself is unfixed: in-process CPU-bound work cannot be killed, so the
  structural fix would be out-of-process execution.

## Superseded: the process-lifetime leak framing

Infrastructure coverage is **still not measured**, now for a third reason.

The hang that followed the crash was diagnosed and fixed (the DI cycle above). What
remains is different in kind: the suite *passes*, and the process simply never exits.

| cap | outcome | crash signatures |
|---|---|---|
| 30 min | cancelled at 30m20s | 0 |
| 60 min | cancelled at 60m20s | 0 |
| 45 min | cancelled at 45m21s | 0 |

Local reproduction, in under a second:

```
dotnet test --filter FullyQualifiedName~InfrastructureRoutingGapCoverageTests
Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7, Duration: 588 ms
-> process still alive; killed externally after 180s
```

A single trivial test from the same assembly exits cleanly, so this is **test-specific,
not assembly-wide**. Something that test starts holds a foreground thread. The likely
area is the federated-mesh registry resolution it performs with
`RemoteCatalogBaseUrls: https://peer.example`.

Two consequences worth stating plainly:

- **No cap can fix it.** `coverlet.msbuild` writes its report after the host exits, and
  the host never exits. Raising the limit only changes how long the runner burns.
- **It has been corrupting local runs all along.** The orphaned `testhost` processes
  that hold output DLLs — making builds fail and `--no-build` silently execute *stale*
  binaries — are this leak. Results produced that way looked authoritative and were
  meaningless.

**CORRECTED — "process-lifetime leak" was the wrong shape for the evidence.** There was
no leaked thread at all. The dump showed 14 of 15 managed threads were background, and
the single foreground thread was `testhost.Main` doing exactly its job. The process
stayed alive because a **test never completed** (the mesh self-recursion above), so
xunit never signalled assembly-finished and `Main` never returned.

The methodological error is worth keeping: I dumped the *live idle process*, which
showed only the aftermath — everything patiently waiting for a signal nobody would
send. That is consistent with a dozen causes and identifies none. The cause was named
only by a `--blame-hang` dump taken **at the stall**, while the offending test was
still executing.

## Method note

Three separate conclusions in this document were drawn from runs that had already been
truncated by an earlier defect, and all three were wrong: "all 572 tests pass", "not a
slow or hanging test", and the 83% floor. Evidence gathered after a process dies early
describes the prefix, not the system.

**There is no trustworthy Infrastructure coverage number, and the 83% floor was never
measured.** Every historical run was truncated by the crash at a different point, so
restoring 83 would be restoring a figure nobody ever verified. The real floor should
be set from the first complete run, not inherited.

Related tracked item: `TestRunnerAdapter.ExecuteTestAsync` abandons its `runTask` on
the per-test timeout path (no longer latent — see the follow-ups list and the
2026-08-13 section below).

## 2026-08-13: intermittent timeouts under instrumentation

The gate completes now, but went intermittently red on branch pushes while master
stayed green. Two runs failed on attempt 1 and passed on a manual re-run — both
recorded conclusions are attempt 2, i.e. a human pressing retry:

- **31657562601** (`cursor/trust-loop-cert-schema`) — failed on the **identical
  commit** that run 31657543917 had already passed, which is what proves flakiness
  rather than a code defect.
- **31665068194** (`cursor/trust-loop-context-assembler`).

Three failures across the two attempt-1 logs:

| run (attempt 1) | test | bound hit | observed |
|---|---|---|---|
| 31665068194 | bridge case `RoslynAnalyzeToolTests` | `TestRunnerAdapter` 60s per-test timeout | "Test timed out after 60s", reported at 1m58s suite elapsed |
| 31665068194 | bridge case `BehaviorExecutorNcrEscalationTests` | 240s `[Theory(Timeout)]` cap | killed at the cap; its case started seconds after the case above failed |
| 31657562601 | `FileSystemEventSourceTests.SubscribeAsync_FileCreated_EmitsEvent` | its own 20s internal cancellation token | `OperationCanceledException` out of `File.WriteAllTextAsync`, test duration **6m17s** |

**One cause family, not three.** Under coverlet instrumentation on the 2-core runner
the suite intermittently saturates CPU and the thread pool badly enough that queued
work items execute **minutes** late — the 6m17s figure is a file write that sat
queued while a 20s token expired behind it. Every fixed bound sized to healthy
duration then converts a slow-but-progressing run into a red build. This is the
exact failure mode the `TestTimeouts` doctrine warns about, observed at three
different bounds in one gate.

The 60s per-test timeout also **amplifies the load it mismeasures**: its timeout
path abandons `runTask`, so the instrumented Roslyn compile it gave up on kept
burning both cores while the next bridge case ran — which is plausibly why that next
case blew through a 240s cap that normally has minutes of headroom.

**What changed** — all three bounds resized as hang nets that clear the worst
observed stall (~6m20s) with margin, per the sizing rule in `TestTimeouts`:

- `TestTimeouts.HostTouching`: 240s → **480s** (covers the bridge theory cap and the
  other ProdStyle hang nets).
- `TestRunnerAdapter.DefaultPerTestTimeout`: 60s → **480s**.
- `FileSystemEventSourceTests` internal token: 20s → `TestTimeouts.HostTouching`.

**Considered and rejected:**

- **Excluding `RoslynAnalyzeToolTests` / `BehaviorExecutorNcrEscalationTests` from
  the bridge matrix.** Deletes the proof and the coverage: the Infrastructure floor
  has 0.3pt of margin (80.3% measured against a floor of 80), and these suites cover
  `RoslynAnalyzeTool` and the NCR escalation path in `[Ashlar.Infrastructure]`.
- **Scaling timeouts only under instrumentation (env var).** Two timing regimes and
  a hidden coupling, for no benefit: the doctrine already says a bound sized to
  healthy duration is wrong on any sufficiently loaded machine, instrumented or not.
- **Auto-retrying the job once.** Institutionalizes the flake — both cited runs are
  attempt-2 passes, so the retry was already happening by hand, and the point of
  this gate is that red means something.

**Determinism claim, stated honestly:** 480s clears the worst stall observed so far
by ~100s. If a future run stalls longer, this gate goes red again and this table
gets a new row — like the coverage floor, the number is sized from observation, not
proven against a bound.
