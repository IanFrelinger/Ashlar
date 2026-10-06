## Summary

SPEC-007 PR 4.6, **mode plumbing with every profile still reporting**. The egress guard now resolves a mode for every decision through one resolver, and the record says what decided it, whether a route would have to refuse, and a random reference a refused party may be shown. `enforce` becomes an opt-in on every profile, so the enforcing twins of 4.7 to 4.9 can run through the real resolver. Every profile still defaults to `report`, and **nothing refuses**: no route acts on the mode until 4.7, and AirGapped and SecureWorkstation keep reporting until the switch (4.11).

- **One resolver, two bindings** (design §2.1, D1). `EgressEnforcement.ResolveMode(profile, override)` is a pure function, pinned as the full table: 6 profiles × {unset, `report`, `enforce`, junk}. The process binding is `EgressGuard.ProcessDefault`, which the 17 explicit sites call (the 17th, `OllamaProvider`'s cloud decision, came with 4.1). The composition binding is the guard `AddAshlar` now registers, `new EgressGuard(profile, override)`, for factory clients and MEAI targets. It replaces an `IEgressGuard` descriptor **only** when its instance is `ProcessDefault` or the guard an earlier `AddAshlar` composed into the same collection, so an `AddAshlarEgressGuard()` that ran before `AddAshlar` is bound, a later `AddAshlar` on the same collection rebinds, and a host's own guard is kept.
- **The opt-in** (D2, D3, D4). `ASHLAR_EGRESS_MODE` is read **once per process**, by `AddAshlar` or at the first decision of a process that never runs it. `AshlarHostingOptions.EgressMode` can only raise the mode, and is never bound from `IConfiguration`. A guard built with a profile takes its override from its constructor only and never reads the environment. A guard built without one takes the stricter of its own override and the process's, so it can raise its mode but never lower a process that enforces.
- **Fails closed** (D7). An unrecognised mode value, an unrecognised profile, and a fault while resolving the mode all give `enforce` (bases `override`, `profile:unrecognised` and `fault`). The mode is resolved in its own step before the classification, so a fault cannot leave the old `enforces = false` placeholder failing open.
- **The strictest profile noted wins** (D5), for `ProcessDefault`, the remote-protocol validators **and the guard `AddAshlar` composes**. Once AirGapped has been noted nothing lowers it, and once SecureWorkstation has been noted only AirGapped replaces it. A second `AddAshlar()` with no profile no longer turns an AirGapped process into Full, and that container's own guard and startup line say AirGapped too. On the same collection, a later `AddAshlar` that notes a stricter profile or raises the mode replaces the guard the earlier one composed, so the guard and the line move together. `ProcessDefault` reads the profile once per decision and records the value it resolved the mode from.
- **Records** (D8). `EgressDecision` gains `ModeBasis`, `Refused` (`Mode == enforce && !Access.Allowed`) and `Ref`, and the `Ashlar-Egress` event 1 appends `modeBasis`, `refused` and `ref` after `fault`, at event version 1.
- **Test hygiene** (§2.10, D41). An internal reset seam, `EgressProcessState`, snapshots and restores the noted profile and the mode latch. Every test class that composes AirGapped or SecureWorkstation now restores through it, and `ProcessGlobalEnvironmentConventionTests` treats those compositions, a raised mode and the seam as process-global writes. A fifth fact pins, file by file, every source that names the seam or a member that writes the noted profile or the latch outside their rules, since `InternalsVisibleTo` exposes them beyond `Ashlar.Abstractions` (4.4's check finding 4).
- **IVT** (D9). `Ashlar.Abstractions` grants `InternalsVisibleTo` to `Ashlar.AI.Pipeline`, for 4.7's fallback mode and synthetic `NoDecision`.
- **Startup line** (D10). A hosted activator logs one line for the guard `AddAshlar` composed: the mode, its basis, the profile, and whether the profile was defaulted. When the host's own `IEgressGuard` is the container's guard, the line does not describe it (listed under Known limits). A process whose mode is not plain `report` also writes the line to stderr, once.

These are classification-style controls inside the runtime. They are not an accredited cross-domain solution.

No non-test code under `application/` changes, so this is not `[coordinated-integration]`.

**Departures from the design, stated** (each is listed for the integrator too):
- **The stderr line is not limited to host-less CLI verbs.** It is written once per process by the first `AddAshlar`, or by the first decision of a process-bound guard, whose mode is not plain `report`. The library cannot tell whether a host will start, and CLI verbs run `AddAshlar` through a host they build lazily and never start. A hosted process with the opt-in therefore gets the stderr line and the log line.
- **D7 applies to explicit-profile guards too.** An unrecognised profile fails closed in the one resolver, whoever passes it. An override of `report` on such a profile is recorded as `override-ignored`.
- **A test seam in production code.** `EgressEnforcement.ModeResolutionProbe` (internal, `null` in production) lets the fault twin make mode resolution throw, because no input to the pure resolver can fault. It is an `AsyncLocal`, so it reaches only decisions on the flow that set it. The reset seam snapshots and restores it.
- **The convention gained a fourth fact.** Besides treating the new writes as process-global, `No_file_that_leaves_egress_state_behind_skips_the_reset_seam` enforces §2.10's "restores through the seam".
- **`AshlarHostingOptions.EgressMode` is a `string?`.** It mirrors the variable and the guard's constructor. Ashlar.Hosting has no PublicAPI baseline, so only the Abstractions API is recorded.
- **The opt-in stays out of the CHANGELOG and the public XML docs** (integrator decision 4). The CHANGELOG and the public XML docs (`AddAshlar`, `AshlarHostingOptions.EgressMode`, `EgressGuard`, `EgressDecision.ModeBasis`) say a mode setting exists without naming the variable, and no user-facing configuration doc mentions it. Internal records still name `ASHLAR_EGRESS_MODE`: `docs/EgressInventory.md`'s mode section (the one §4 asks for, marked "not a supported setting until 4.11"), SPEC-007's design text and its "PR 4.6 (this PR)" status bullet, and cert-gate row 56. The startup line also names it when the value is unrecognised.

## Phase B: check findings fixed

The adversarial check passed the lane with fixes. Each finding, and what this PR does about it:

1. **[medium] The composed guard used this call's profile, not the strictest noted** (integrator decision 3, for AirGapped and SecureWorkstation alike). `BindComposedEgressGuard` now composes with `AshlarDeploymentProfileEnvironment.ResolvedRaw ?? canonicalProfile`, after `AddAshlar` has noted its own profile, and builds `EgressModeStartup` (the log line and the stderr line) from the same value. The line says the profile was defaulted only when the composed profile is the call's own default. Twins: `A_second_AddAshlar_with_no_profile_does_not_lower_an_AirGapped_process` now resolves the second container's `IEgressGuard` and asserts `Profile == "air-gapped"` and `ModeBasis == "profile:air-gapped"`; `A_later_AddAshlar_in_an_AirGapped_container_does_not_lower_the_startup_line` is new. Both red first (below); mutations p1 and p10.
2. **`ProcessDefault` read the profile twice per decision.** `Evaluate` reads the effective profile once (`ReadProfile`), resolves the mode from it, and the record describes the same value (`DescribeProfile`); an explicit guard uses its constructor's profile. The guard no longer calls `ForbidsRemoteProtocolEgress` itself; it applies the same AirGapped-or-SecureWorkstation test to the value it read. Twin: `A_decision_names_one_profile_even_when_the_noted_profile_changes_while_it_is_made` (the probe notes AirGapped mid-decision, standing in for a concurrent `AddAshlar`). Red first; mutation p2.
3. **A null-profile guard with a constructor override never saw a raise.** It now takes the stricter of its own override and `ProcessOverride()`: the process's mode wins only when it enforces and the guard's own does not, so a tie keeps the constructor's basis. The `AshlarHostingOptions.EgressMode` doc now says what the raise reaches (`ProcessDefault`, every guard built without a profile, the guard this and every later `AddAshlar` composes) and what keeps its mode (a guard composed earlier, like any guard built with a profile); check round 2 narrows this to a container already built or another collection, since a later `AddAshlar` on the same collection now replaces the earlier guard. Twins: `A_guard_built_without_a_profile_takes_the_stricter_of_its_own_override_and_the_process_override` (the variable and the hosting option; red first) and the positive control `A_guard_built_without_a_profile_keeps_its_own_override_when_the_process_raises_nothing`; mutation p3.
4. **Untested or never-red behaviour.** `The_hosting_option_raises_the_mode_for_the_process` is a theory with a `"junk"` row (enforce, basis `override`, logged at Warning) beside `"enforce"` (Information). Mutations p4 (drop the Unrecognised raise), p5 (`Ref` = the sequence as `x16`) and p6 (drop the `ReferenceEquals(ProcessDefault)` condition) are all killed.
5. **Event 1's payload grew without a version bump.** `[Event(1, …, Version = 1)]`, through the constant `DecisionEventVersion`; the class remarks say appended fields bump it. `Event_1_carries_the_mode_basis_refused_and_the_reference` asserts `Version == 1`. Red first; mutation p7.
6. **The profile and the mode latch differently** (for 4.11). Not fixed here: listed under "Known limits / carried to 4.11".
7. **Record drift.** SPEC-007's "4.6 (this PR)" line stays a placeholder for the integrator. Cert-gate row 64 gains a sentence on `Mode`, `ModeBasis`, `Refused` and `Ref` (and what the two twin classes pin), and its Tests column names `EgressModeResolutionTests` and `EgressModeProcessBindingTests`. Integrator decision 4: the hedged section in `docs/EgressInventory.md` stays; the CHANGELOG and `AddAshlar`'s XML doc say a mode setting exists without naming the variable (and so do `AshlarHostingOptions.EgressMode`, `EgressGuard` and `EgressDecision.ModeBasis`, by the same rule). SPEC-007's 4.11 plan notes that `An_explicit_profile_is_reported_and_does_not_change_the_decision` already has an `enforce` row from 4.6 and that 4.11 flips its AirGapped and SecureWorkstation rows.
8. **The fault probe was a process-global static.** `ModeResolutionProbe` is backed by an `AsyncLocal<Action?>`; `EgressProcessStateScope.SetModeResolutionProbe` documents that it reaches only the calling flow. Twin: `The_mode_resolution_probe_does_not_reach_another_flow` (a thread started under `SuppressFlow` decides `profile:full` while the setting flow faults). Red first; mutation p8.

**From 4.4's check, finding 4.** `ProcessGlobalEnvironmentConventionTests.Only_AddAshlar_and_the_reset_seam_reach_the_process_egress_state` pins every C# file in the repository whose code (whole-line comments left out) names, as a whole word, `EgressProcessState`, `NoteResolved`, `RestoreResolved`, `ClearResolved`, `NoteHostingOption`, `CaptureLatch`, `RestoreLatch`, `ResetLatch` or `ModeResolutionProbe`. Production: only `AshlarDeploymentProfileEnvironment.cs`, `EgressEnforcement.cs`, `EgressGuard.cs` (the probe), `EgressProcessState.cs` and `AddAshlar`'s file; tests: only `Helpers/EgressProcessStateScope.cs`. The pin is exact both ways, and each direction has been seen red (p9: an unlisted caller; p14: a listed file that stops naming its member). 4.4 (which adds `InternalsVisibleTo Ashlar.Orchestration`) is not on master yet, so this pins what exists on this tree; 4.4's branch names none of these members, so it needs no new row when it lands. Mutations p9 and p14.

**Check round 2.** A second adversarial check raised three low findings:

1. **A later `AddAshlar` on the same collection moved the startup line but not the guard.** `BindComposedEgressGuard` replaced only a `ProcessDefault` descriptor, so a second `AddAshlar` on the same collection found the first call's composed guard, registered nothing, and still replaced `EgressModeStartup`: after `AddAshlar(); AddAshlarProfile(AirGapped)` the container decided under Full while the line said air-gapped, and after `AddAshlar(); AddAshlar(o => o.EgressMode = "enforce")` it decided `report` while the line said `enforce`. Fixed by the checker's option 2: `EgressModeStartup` now carries the guard it describes, and `AddAshlar` replaces a descriptor holding `ProcessDefault` **or a guard an earlier `AddAshlar` composed into the same collection** (the one that call's line names), so the stricter profile or the raise reaches the guard and the line together. A host's own guard is still kept. Twins `A_later_stricter_AddAshlar_on_the_same_collection_reaches_its_guard_and_its_startup_line` and `A_later_AddAshlar_that_raises_the_mode_on_the_same_collection_reaches_its_guard_and_its_startup_line`, red first (2/40); mutations p13 and p6b. The `EgressInventory` bullets ("Two bindings", "Startup line"), `BindComposedEgressGuard`'s remarks, the `AshlarHostingOptions.EgressMode` doc, the CHANGELOG entry, SPEC-007's 4.6 status line and row 64 now say what is replaced and what the line describes. The host's-own-guard case is listed under Known limits.
2. **The fifth convention fact's stale direction had never been seen red.** Mutation p14 drops `EgressEnforcement.ModeResolutionProbe?.Invoke();` from `EgressGuard.cs`, a listed file, and the fact fails with "`ModeResolutionProbe` is no longer named by a listed file". Both directions are now observed (p9 and p14).
3. **This body said the opt-in is named only in `docs/EgressInventory.md`.** It was false: SPEC-007's design text and its 4.6 status line, cert-gate row 56 and the startup line (for an unrecognised value) name `ASHLAR_EGRESS_MODE` too. The departure bullet above now says the variable stays out of the CHANGELOG and the public XML docs, and lists the internal records that name it.

**Also for Grok and the CHANGELOG:** after `AddAshlar(AirGapped)`, a later `AddAshlar` in the same process no longer lowers `ForbidsRemoteProtocolEgress` or `DisplayName`, and with phase B that container's own guard decides under AirGapped too. On the same collection, a later `AddAshlar` also replaces the guard the earlier one composed.

**Recounts after merging master (`bbc5d71`, PR 4.1 #717), at `d994185`.** The certification folder still holds 127 `.cs` files (130 entries): 4.1 added none, so the count paragraph stands. Row 64's TSV sums are 4.1's, which this PR does not touch: 85 rows, 149 occurrences (4.1's redirect-off `HttpClientHandler` in `OllamaHttpChatClient.cs` moved http.new from 18 to 19), 48 guarded; 33 `-`, 34 `Exempt:`, 10 `Factory`, 4 `Upstream:`, 4 `Governance`. The scan reads 2,133 files and examines 149 occurrences, measured by the floors fact in the container at `d994185` (`ScannedFiles=2133 ExaminedOccurrences=149`); row 64 and the floors comment say so (`measured 2,133, 149 and 67 at PR 4.6`). Row 56 is unaffected: 4.1 adds no environment write and leaves `ProcessGlobalEnvironmentConventionTests` alone. The merge conflicted in row 64 (4.1's record rules beside this PR's mode sentence and Tests column), the `EgressGuardConventionTests` floors comment and the knowledge graph (regenerated, not hand-merged). Resolved beside them: SPEC-007's PR 4 status now lists 4.1 as "(#717, `bbc5d71`)" and this PR as a sibling bullet, `**PR 4.6** (this PR)` (it had been a sub-bullet of the plan); `docs/EgressInventory.md`'s opening paragraph names 4.6 beside 4.1 among the PRs that re-derived the line numbers in the files they edited; this body's explicit-site count is 17, since 4.1 made `OllamaProvider`'s cloud decision a 17th `ProcessDefault` site. CHANGELOG Unreleased, `docs/EgressInventory.md` and `EgressDecision.cs` merged cleanly. No non-test code under `application/` changes against master (`git diff --name-only origin/master...HEAD`), so the PR is still not `[coordinated-integration]`. Earlier merge: `5ff00a4af`, at phase B (2,133 files and 148 occurrences then).

## Changes

- **`src/Ashlar.Abstractions/Security/Egress/`**
  - `EgressEnforcement` (new, internal static):
    - `ResolveMode`, `ParseOverride` and the basis constants (`profile:<p>`, `profile:unrecognised`, `override`, `override-ignored`, `fault`; `break-glass`, `host-opt-out` and `operator-verb` are declared for 4.9 and 4.11 and not produced);
    - the process latch: `ProcessOverride` and `NoteHostingOption`, read once and raise-only;
    - `StartupLine` and `AnnounceOnce` (stderr, once per process, only for a mode that is not plain report);
    - `NewReference` (8 random bytes from `RandomNumberGenerator`, as 16 lowercase hex digits);
    - `ModeResolutionProbe`, a test seam that is `null` in production, backed by an `AsyncLocal`.
  - `EgressProcessState` (new, internal static): the reset seam, with `Snapshot`, `Restore` and `Reset`.
  - `EgressGuard`:
    - the constructor is `EgressGuard(string? deploymentProfile = null, string? egressMode = null)`;
    - `Evaluate` reads the profile once and resolves the mode first, in its own try, falling back to `enforce`/`fault`; the classification try describes the profile it read;
    - a guard without a profile takes the stricter of its own override and the process's;
    - it stamps `Mode`, `ModeBasis` and `Ref`.
  - `EgressDecision`: `ModeBasis`, `Refused` and `Ref`, appended through the internal constructor; the `Mode` and `ProfileEnforcesByDefault` docs are updated.
  - `EgressEventSource`: event 1 appends `modeBasis`, `refused` and `ref`, at `Version = 1`.
- **`src/Ashlar.Abstractions/AshlarDeploymentProfileEnvironment.cs`**: `NoteResolved` is strictest-wins under a lock, and the internal `RestoreResolved` serves the seam.
- **`src/Ashlar.Abstractions/Ashlar.Abstractions.csproj`**: `InternalsVisibleTo Ashlar.AI.Pipeline`.
- **`src/Ashlar.Abstractions/PublicAPI.Unshipped.txt`**: `EgressDecision.ModeBasis`, `.Refused` and `.Ref`, and the guard constructor's new parameter.
- **`src/Ashlar.Hosting/`**
  - `AshlarHostingOptions.EgressMode` (new, `string?`, raise-only). Ashlar.Hosting has no PublicAPI baseline.
  - `AddAshlar`:
    - notes the profile (strictest-wins) and the hosting option;
    - calls `BindComposedEgressGuard` right after its `AddAshlarEgressGuard()`;
    - `ResolveDeploymentProfile` now reports whether the profile was defaulted.
  - `AshlarServiceCollectionExtensions.Egress.cs` (new):
    - `BindComposedEgressGuard`, which composes the strictest profile noted in the process and replaces a `ProcessDefault` descriptor or the guard an earlier `AddAshlar` composed into the same collection;
    - `EgressModeStartup` (internal record; it carries the guard it describes, which is how a later `AddAshlar` finds that guard);
    - `EgressModeStartupActivator` (internal `IHostedService`; `Ashlar.Egress`, event 7302 `EgressMode`; Warning for an unrecognised override, a break-glass or an ignored override, Information otherwise).
- **Tests** (`src/Ashlar.Tests.Infrastructure/`)
  - `Tests/Certification/EgressModeResolutionTests.cs` (new, hermetic): the 24-row table, spellings, overrides, an unrecognised profile, a blank profile, what an explicit guard records (Mode, ModeBasis, Refused under report and enforce, the host boundary never refused), a classification fault under enforce, `Ref`, and event 1.
  - `Tests/Certification/EgressModeProcessBindingTests.cs` (new, `EnvironmentVariables`), covering:
    - read-once in both directions;
    - every profile reporting through `ProcessDefault`;
    - an unrecognised mode and an unrecognised profile;
    - the fault twin;
    - D4;
    - the strictest-wins matrix and the `AddAshlar` twin;
    - the reset seam;
    - `AddAshlar` latching;
    - the hosting option raising, and failing to lower;
    - the `AddAshlarEgressGuard`-first twin, and a host guard kept;
    - the activator line at Information and at Warning, and on AirGapped after a later `AddAshlar`;
    - the stderr line;
    - phase B: the second container's own guard, one profile read per decision, a null-profile guard under a raised process, the `"junk"` hosting option, and the probe staying on its flow;
    - check round 2: a later `AddAshlar` on the same collection, stricter or raising the mode, reaching the container's guard and its line.
  - `Helpers/EgressProcessStateScope.cs` (new): reaches the seam by reflection.
  - `ProcessGlobalEnvironmentConventionTests`: the new writes, a fourth fact, `No_file_that_leaves_egress_state_behind_skips_the_reset_seam`, and a fifth, `Only_AddAshlar_and_the_reset_seam_reach_the_process_egress_state`.
  - Restore through the seam: `HostingDeploymentProfileTests`, `KernelPhaseResolutionTests`, `KernelDiCompositionProdStyleTests`, `OnboardingE2ETests` and `AirGappedProfileApiHostProdStyleTests`.
  - Flipped pins:
    - `EgressKernelFactoryTwinTests.TheGuardIsRegisteredOnce` and `EgressApiHostProdStyleTests.TheApiHost_RegistersTheGuardOnce…`: the single `IEgressGuard` is now the composed guard, not `ProcessDefault`;
    - `EgressGuardDecisionTests`: event 1's payload names gain the three fields, and the explicit-profile theory's `air-gapped-ish` row records `enforce` (an unrecognised profile fails closed).
- **Records**
  - `docs/EgressInventory.md`: a mode section, and line citations re-derived (`AshlarDeploymentProfileEnvironment.cs:81` and `:75-80`, `AshlarServiceCollectionExtensions.Deployment.cs:103-150`); the profile-check paragraph now cites `DescribeProfile` (`EgressGuard.cs:212`) and says the guard no longer calls `ForbidsRemoteProtocolEgress` itself.
  - `ci/cert-gate-assertions.md`: row 56 (the environment convention) describes the egress writes and the fourth and fifth facts, and its offender count is corrected from 22 to 20, the size of the frozen set. Row 64 gains the mode sentence, the two twin classes and the 4.6 floor measurement (2,133 files, 149 occurrences with 4.1's, 67 docs rows). The certification file count goes from 125 to 127.
  - `EgressGuardConventionTests` floors comment: 2,133 files and 149 occurrences at 4.6 (4.1's sentence kept before it).
  - `CHANGELOG.md`: an Unreleased entry, which says a mode setting exists without naming the variable, and spells out the strictest-wins behaviour change.
  - SPEC-007: the PR 4 status gains a "PR 4.6 (this PR)" bullet after 4.1's, and the 4.11 plan bullet notes the explicit-profile theory's existing `enforce` row.
  - The knowledge graph is regenerated.

## Behaviour changes, and what stays report-only

**Report-only, unchanged:** no route reads `Mode` or `Refused`. The HTTP handler, the MEAI chat client and the 17 explicit sites still discard the decision. The default mode is `report` on all six profiles, so with `ASHLAR_EGRESS_MODE` unset every record's `Mode` is `report` and `Refused` is `false`, as before.

**What does change:**
1. **Strictest-wins.** After `AddAshlar(AirGapped)`, a later `AddAshlar` in the same process with another profile no longer changes `ForbidsRemoteProtocolEgress`, `DisplayName` or `ProcessDefault`'s profile, and that later container's own guard and startup line say AirGapped. On the same collection, a later `AddAshlar` replaces the guard an earlier one composed, so a stricter profile or a raised mode reaches that container's guard. The MCP and A2A validators therefore stay on AirGapped. SecureWorkstation is replaced only by AirGapped. Among the other profiles, the last one noted still wins. Out-of-repo hosts that compose profiles twice in one process will see it.
2. **`IEgressGuard` in an `AddAshlar` container** is the composed guard, not `EgressGuard.ProcessDefault` (the last `AddAshlar` on the collection composed it). Code that compared the resolved guard to `ProcessDefault` by reference now sees a different instance, which decides by the same rules under the composed profile (the strictest noted in the process).
3. **One hosted service more per `AddAshlar` container** (`EgressModeStartupActivator`). It logs one Information line at start, in category `Ashlar.Egress`, which is not filtered to Debug like the decision records.
4. **An unrecognised profile on an explicit guard or in the profile variable** now records `Mode = enforce` (fail closed). `AddAshlar` already refuses such a profile at startup, so only a host-less process or a hand-built guard sees this, and nothing acts on it yet.
5. **Event 1 has 20 fields** (17 before) and version 1 (0 before). It is append-only, so positional readers of the first 17 are unaffected; manifest-keyed consumers (ETW, TraceEvent) see a new version.
6. **`ASHLAR_EGRESS_MODE`** is read once per process. With it set to anything but `report`, the process writes one line to stderr. It is not a supported setting until 4.11.
7. **A guard built without a profile but with an override** (`new EgressGuard(null, "report")`) now records `enforce` when the process enforces (the variable as latched, or the hosting option's raise). Before, its own `report` won. No production code builds such a guard.
8. **`ProcessDefault`'s record** names the profile its mode was resolved from, read once per decision; before, a concurrent `AddAshlar` between two reads could give `ModeBasis profile:full` with `Profile air-gapped`.

## Known limits / carried to 4.11

- **4.6 check finding 6: the profile and the mode latch differently.** Where `AddAshlar` never ran, `ProcessDefault` re-reads `ASHLAR_DEPLOYMENT_PROFILE` at every decision, so in-process code can lower AirGapped to Full with `SetEnvironmentVariable`; the mode override is read once. The design accepted the per-decision read. For 4.11: record it as a decision or a known limit; one option is that `ProcessDefault` notes AirGapped or SecureWorkstation through `NoteResolved` the first time the variable names it.
- **A composed guard keeps what it was composed with.** It is built with an explicit profile and override and never reads process state (D4). A later `AddAshlar` on the **same** collection replaces it (check round 2, finding 1), but one on another collection, or after the container was built, does not reach it: a later stricter profile, or a raise through `AshlarHostingOptions.EgressMode`, leaves a container composed earlier as it was. Every production `AddAshlar` takes its profile from the same variable, so this needs two compositions with different profiles in one process. For 4.11's D6 twin list: an earlier Full container after a later `AddAshlar(AirGapped)`.
- **The startup line does not describe a host's own guard.** `AddAshlar` keeps a host's own `IEgressGuard` (registered before it, or after it), and that guard decides for the container's factory clients and MEAI targets, but the activator still logs the line for the guard `AddAshlar` composed. The docs now say so (`docs/EgressInventory.md`, `BindComposedEgressGuard`'s remarks, `AshlarHostingOptions.EgressMode`). What the line should say there depends on 4.7's route rule (on AirGapped the route also enforces on the process profile, whatever the host's guard decides), so it is left for 4.7: suppress the line, name the host's guard, or keep it as the process's mode.
- **A later `AddAshlar` still selects its own modules.** After `AddAshlar(AirGapped)`, a later `AddAshlar()` composes an AirGapped guard but registers Full's module set. That is 4.10's AirGapped hygiene; under the switch the guard fails such a container closed.
- **The new convention fact is textual.** It sees a member's name spelled in code or in a string; a name assembled at run time, or reflection over all members, is invisible to it.

## Testing

All builds and tests ran in the Linux devtest container through `scripts/test-in-container.sh`. Nothing ran on the host.

After merging master `bbc5d71` (PR 4.1, #717), head `d994185`:

| Check | Result |
|---|---|
| Full cert-gate (`scripts/run-cert-gate.sh`, net8.0) at `d994185` | **2698/2698**, 0 skipped (the skip guard matches the baseline); the count check derived ≥2693 from `--list-tests`. 2647 (this PR) + 51 (4.1: master's 2593 − 2542). The floors fact measured `ScannedFiles=2133 ExaminedOccurrences=149` |
| Touched classes (17), net8.0 / net10.0, at `d994185` | **665/665** / **670/670** (`f0e0373`: 614 / 619; the 51 are 4.1's twins in the same classes) |
| `Ashlar.LocalDevCore.slnf` (the `build-core` command) at `d994185` | 0 errors, 2 warnings, both the harness's SourceLink warnings |
| `scripts/ci/run-repo-gates.sh` (host, shellcheck 0.9.0 on PATH) at `d994185` | **26/26**, the knowledge-graph byte-compare included |
| Mutations | Not re-run at `d994185`. 4.1 changed neither file this PR's mutants touch: p1 is in `AshlarServiceCollectionExtensions.Egress.cs` (`Ashlar.Hosting`, untouched by 4.1) and p3 in `EgressGuard.Evaluate`'s null-profile override path (4.1 did not touch `EgressGuard.cs`; its `Ashlar.Abstractions` edits are `file:` classification in `EgressDestinations.cs` and doc comments in `EgressRequest.cs` and `EgressDecision.cs`). Their twins pass at `d994185` in both runs above. Every summary line below stands at the ref it names |

Check round 2 (head `f0e0373`, master `5ff00a4af` merged in):

| Check | Result |
|---|---|
| Red first: the two same-collection twins committed alone (`ba51bd5`), before the fix, net8.0, `EgressModeProcessBindingTests` | **failed 2/40**: the raise twin (`Mode` was `report`) and the stricter-profile twin (`Profile` was `full`) |
| Green after the fix (`f0e0373`), net8.0, `EgressModeProcessBindingTests` + `EgressModeResolutionTests` + `ProcessGlobalEnvironmentConventionTests` | **108/108** |
| Full cert-gate (`scripts/run-cert-gate.sh`, net8.0) at `f0e0373` | **2647/2647**, 0 skipped (the skip guard matches the baseline); the count check derived ≥2642 from `--list-tests`. Round 2 adds the 2 twins |
| Touched classes (17), net8.0 / net10.0, at `f0e0373` | **614/614** / **619/619** (`38ccf81`: 612 / 617; the 2 twins) |
| `Ashlar.LocalDevCore.slnf` (the `build-core` command) at `f0e0373` | 0 errors, 2 warnings, both the harness's SourceLink warnings |
| `scripts/ci/run-repo-gates.sh` (host, shellcheck 0.9.0 on PATH) at `f0e0373` | **26/26**, the knowledge-graph byte-compare included |

Phase B (head `38ccf81`, master `5ff00a4af` merged in):

| Check | Result |
|---|---|
| Red first: the phase B twins committed alone (`a87251e`), before any fix, net8.0, `EgressModeProcessBindingTests` + `EgressModeResolutionTests` | **failed 7/101**, each for the reason it pins: the second container's guard (`Profile` was `full`), the startup line (`profile full, defaulted …`), the profile read once (`ModeBasis` was `profile:air-gapped`), both null-profile-guard rows (`Mode` was `report`), event 1's version (`0x00`), and the probe on another flow (`ModeBasis` was `fault`) |
| Green after the fixes (`38e7523`), net8.0, those two classes + `ProcessGlobalEnvironmentConventionTests` + `EgressGuardDecisionTests` | **243/243** |
| Full cert-gate (`scripts/run-cert-gate.sh`, net8.0) at `38ccf81` | **2645/2645**, 0 skipped (the skip guard matches the baseline); the count check derived ≥2640 from `--list-tests`. Phase A had 2637: phase B adds 8 (the startup-line twin, the null-profile guard fact and its 2-row theory, the profile-read twin, the probe twin, the `"junk"` hosting-option row, and the fifth convention fact). F6 measured `ScannedFiles=2133 ExaminedOccurrences=148` |
| Full cert-gate at `8b55af7` (the commit before) | 2644/2645: `No_unlisted_test_file_mutates_the_environment_unserialized` failed because the floors comment named the seam's file, so the convention read `EgressGuardConventionTests` as a seam user. `38ccf81` rewords the comment |
| Touched classes, cert-gate and hosting (17 classes), net8.0 | **612/612** at `38ccf81` (phase A: 604) |
| Touched classes, net10.0 | **617/617** at `38ccf81` (phase A: 609) |
| `Ashlar.LocalDevCore.slnf` (the `build-core` command), and `Ashlar.Abstractions` on netstandard2.0, net8.0 and net10.0 | build-core: 0 errors, 2 warnings, both the harness's SourceLink warnings. Abstractions: 0 warnings, 0 errors |
| `scripts/ci/run-repo-gates.sh` (host, shellcheck 0.9.0 on PATH) | **26/26** at `38ccf81` (master's #714 added the 26th, `handoff-scripts.test.sh`), the knowledge-graph byte-compare included |

Phase A (for the record):

| Check | Result |
|---|---|
| `Ashlar.Abstractions` (netstandard2.0, net8.0, net10.0), `Ashlar.Hosting` with `Ashlar.AI.Pipeline` | 0 errors. `Ashlar.LocalDevCore.slnf` at `a501c2b`: 0 errors, 2 warnings (the harness's SourceLink warnings) |
| Full cert-gate (net8.0) | **2637/2637** at `296c745`. Master had 2542: 63 (`EgressModeResolutionTests`), 31 (`EgressModeProcessBindingTests`) and 1 (the fourth convention fact) |
| Touched classes (17), net8.0 / net10.0 | 604/604 / 609/609 |
| `scripts/ci/run-repo-gates.sh` | 25/25 at `a501c2b` |

### Mutation checks, phase B

Every row ran `scripts/mutation-check.sh` at the head, `38ccf8101dc7f3a73341c0ca4909ea45f7d8966f`, one at a time, on net8.0. Each log shows "replaced 1 occurrence", the diff, the red run's failing tests, `porcelain=[]` after the restore, and a green run that passed every test. p11 and p12 re-run two phase A mutants (m10, m02) whose code phase B restructured.

| id | finding | mutation | red | what went red |
|---|---|---|---|---|
| p1 | 1 | the composed guard uses this call's profile (`var composedProfile = profile;`) | 2/38 | `A_second_AddAshlar_with_no_profile_does_not_lower_an_AirGapped_process`, `A_later_AddAshlar_in_an_AirGapped_container_does_not_lower_the_startup_line` |
| p2 | 2 | the record re-reads the profile (`DescribeProfile(ReadProfile())`) | 1/38 | `A_decision_names_one_profile_even_when_the_noted_profile_changes_while_it_is_made` |
| p3 | 3 | a null-profile guard keeps its own report (`&& !IsEnforce(own.Mode)` → `&& IsEnforce(own.Mode)`) | 2/38 | both rows of `A_guard_built_without_a_profile_takes_the_stricter_of_its_own_override_and_the_process_override` |
| p4 | 4 | the hosting option's Unrecognised value does not raise | 1/38 | `The_hosting_option_raises_the_mode_for_the_process(option: "junk", level: Warning)` |
| p5 | 4 | `Ref` is the sequence as `x16` | 1/63 | `Every_decision_carries_a_fresh_random_reference_of_16_hex_digits` |
| p6 | 4 | `BindComposedEgressGuard` drops the `ReferenceEquals(ProcessDefault)` condition | 1/38 | `AddAshlar_keeps_a_guard_the_host_registered_before_it` |
| p7 | 5 | event 1 loses `Version = DecisionEventVersion` | 1/63 | `Event_1_carries_the_mode_basis_refused_and_the_reference` |
| p8 | 8 | the probe is process-global (`AsyncLocal` → `StrongBox`) | 1/38 | `The_mode_resolution_probe_does_not_reach_another_flow` |
| p9 | 4.4 F4 | `AddAshlar`'s egress partial calls `AshlarDeploymentProfileEnvironment.ClearResolved()` | 1/5 | `Only_AddAshlar_and_the_reset_seam_reach_the_process_egress_state` |
| p10 | 1 | the startup line says "defaulted" for a profile raised by an earlier `AddAshlar` (`var defaulted = profileDefaulted;`) | 1/38 | `A_later_AddAshlar_in_an_AirGapped_container_does_not_lower_the_startup_line` |
| p11 | phase A m10, re-run | an explicit-profile guard falls back to the process latch (`_egressMode ?? EgressEnforcement.ProcessOverride()`) | 1/38 | `A_guard_built_with_a_profile_never_reads_the_environment` |
| p12 | phase A m02, re-run | `Evaluate`'s mode-fault fallback becomes `report` | 1/38 | `A_fault_while_resolving_the_mode_fails_closed_to_enforce` |

```
mutation p1-composed-guard-uses-this-calls-profile: KILLED red=failed:2/38 green=passed:38/38 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
mutation p2-record-rereads-the-profile: KILLED red=failed:1/38 green=passed:38/38 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
mutation p3-null-profile-guard-keeps-own-report: KILLED red=failed:2/38 green=passed:38/38 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
mutation p4-option-junk-does-not-raise: KILLED red=failed:1/38 green=passed:38/38 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
mutation p5-ref-is-the-sequence: KILLED red=failed:1/63 green=passed:63/63 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
mutation p6-host-guard-replaced: KILLED red=failed:1/38 green=passed:38/38 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
mutation p7-event1-version-dropped: KILLED red=failed:1/63 green=passed:63/63 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
mutation p8-probe-process-global: KILLED red=failed:1/38 green=passed:38/38 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
mutation p9-unlisted-seam-caller: KILLED red=failed:1/5 green=passed:5/5 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
mutation p10-raised-profile-reads-defaulted: KILLED red=failed:1/38 green=passed:38/38 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
mutation p11-explicit-guard-reads-latch: KILLED red=failed:1/38 green=passed:38/38 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
mutation p12-mode-fault-fails-open: KILLED red=failed:1/38 green=passed:38/38 ref=38ccf8101dc7f3a73341c0ca4909ea45f7d8966f
```

Check round 2, at the head `f0e0373707c7fc50279e137fc8b83fe8237471b9`, one at a time, net8.0. p6b re-derives p6 for the new replacement condition, and p1 is re-run because its method changed. Each log shows "replaced 1 occurrence" and `porcelain=[]`.

| id | finding | mutation | red | what went red |
|---|---|---|---|---|
| p13 | round 2, 1 | `BindComposedEgressGuard` replaces only `ProcessDefault` again (the earlier-composed condition dropped) | 2/40 | `A_later_stricter_AddAshlar_on_the_same_collection_reaches_its_guard_and_its_startup_line`, `A_later_AddAshlar_that_raises_the_mode_on_the_same_collection_reaches_its_guard_and_its_startup_line` |
| p6b | round 2, 1 (p6 re-derived) | `BindComposedEgressGuard` replaces every non-keyed `IEgressGuard` descriptor | 1/40 | `AddAshlar_keeps_a_guard_the_host_registered_before_it` |
| p14 | round 2, 2 | `EgressGuard.cs`, a listed file, no longer calls `EgressEnforcement.ModeResolutionProbe?.Invoke();` | 1/5 | `Only_AddAshlar_and_the_reset_seam_reach_the_process_egress_state` ("`ModeResolutionProbe` is no longer named by a listed file: `src/Ashlar.Abstractions/Security/Egress/EgressGuard.cs`") |
| p1 | 1, re-run | the composed guard uses this call's profile | 2/40 | `A_second_AddAshlar_with_no_profile_does_not_lower_an_AirGapped_process`, `A_later_AddAshlar_in_an_AirGapped_container_does_not_lower_the_startup_line` |

```
mutation p13-earlier-composed-guard-kept: KILLED red=failed:2/40 green=passed:40/40 ref=f0e0373707c7fc50279e137fc8b83fe8237471b9
mutation p6b-host-guard-replaced: KILLED red=failed:1/40 green=passed:40/40 ref=f0e0373707c7fc50279e137fc8b83fe8237471b9
mutation p14-listed-seam-caller-dropped: KILLED red=failed:1/5 green=passed:5/5 ref=f0e0373707c7fc50279e137fc8b83fe8237471b9
mutation p1-composed-guard-uses-this-calls-profile: KILLED red=failed:2/40 green=passed:40/40 ref=f0e0373707c7fc50279e137fc8b83fe8237471b9
```

### Mutation checks, phase A

Every row ran `scripts/mutation-check.sh` at `78f573e`, the branch's last commit before the squash; its tree is the head's (`f596ff9`), and the clone keeps the commit. Each run: it applies the replacement exactly once, runs red, restores, checks that `git status --porcelain` is empty, and runs green. All runs are on net8.0. "red a/b" counts failed of total. Each verbatim summary line and its red test names are below the table.

| id | mutation | red | what went red |
|---|---|---|---|
| m01 | resolver: an `enforce` or unrecognised override resolves to `report` | 34/94 | the table's `enforce` and junk rows; overrides; a blank profile with `enforce`; explicit-guard rows; the classification fault under enforce; event 1; 9 process-binding facts (read-once, latching, the option, D4, Warning, stderr) |
| m02 | `Evaluate`'s mode-fault fallback becomes `report` | 1/31 | `A_fault_while_resolving_the_mode_fails_closed_to_enforce` |
| m03 | `NoteResolved` last-wins (every profile equally strict) | 5/31 | 4 `The_strictest_profile_noted_in_the_process_wins` rows, `A_second_AddAshlar_with_no_profile_does_not_lower_an_AirGapped_process` |
| m04 | the process override re-reads the variable at every decision | 4/31 | `ProcessDefault_reads_the_override_once…`, `A_report_latched_first_is_not_raised…`, `AddAshlar_reads_the_override_once…`, `The_reset_seam_restores…` |
| m05 | `AddAshlar` does not replace the `ProcessDefault` descriptor | 3/33 | `AddAshlar_binds_its_composed_guard_even_when_AddAshlarEgressGuard_ran_first`, both `EgressKernelFactoryTwinTests` |
| m06 | `EgressMode = "report"` lowers the composed guard | 1/31 | `The_hosting_option_cannot_lower_the_mode` |
| m07 | the seam's `Restore` skips the latch | 1/31 | `The_reset_seam_restores_the_noted_profile_and_the_mode_latch` |
| m08 | `Refused = !Access.Allowed` (ignores the mode) | 5/200 | 3 explicit-guard report rows, `A_classification_fault_under_enforce_is_refused_and_under_report_is_not`, `EgressGuardDecisionTests.The_event_source_writes_each_decision…` |
| m09 | event 1 writes an empty `ref` | 2/200 | `Event_1_carries_the_mode_basis_refused_and_the_reference`, `The_event_source_writes_each_decision…` |
| m10 | an explicit-profile guard falls back to the process latch (breaks D4) | 1/31 | `A_guard_built_with_a_profile_never_reads_the_environment` |
| m11 | `KernelPhaseResolutionTests` (composes AirGapped and SecureWorkstation, writes no variable) loses its `[Collection]` | 1/4 | `No_unlisted_test_file_mutates_the_environment_unserialized` |
| m12 | `HostingDeploymentProfileTests` drops its seam scope | 1/4 | `No_file_that_leaves_egress_state_behind_skips_the_reset_seam` |
| m13 | the activator logs nothing | 2/31 | `The_hosted_activator_logs_one_line…`, `An_unrecognised_override_is_logged_at_Warning` |
| m14 | an unrecognised profile resolves to `report` | 4/231 | `An_unrecognised_profile_fails_closed…`, the explicit `air-gapped-ish` row in both `EgressModeResolutionTests` and `EgressGuardDecisionTests`, `An_unrecognised_profile_variable_fails_closed_to_enforce` |
| m15 | the startup line goes to stdout | 1/31 | `A_mode_other_than_plain_report_is_written_to_standard_error_once` |

Every phase A row is KILLED, and every green run passed all of its tests. The first m13 run was stopped by the session's 2-hour limit on a background task, before its red run had finished building, and m13 was run again from the start. The summary lines, verbatim:

```
mutation m01-enforce-override-reports: KILLED red=failed:34/94 green=passed:94/94 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m02-mode-fault-fails-open: KILLED red=failed:1/31 green=passed:31/31 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m03-profile-last-wins: KILLED red=failed:5/31 green=passed:31/31 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m04-override-reread: KILLED red=failed:4/31 green=passed:31/31 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m05-composed-guard-unbound: KILLED red=failed:3/33 green=passed:33/33 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m06-option-lowers: KILLED red=failed:1/31 green=passed:31/31 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m07-seam-skips-latch: KILLED red=failed:1/31 green=passed:31/31 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m08-refused-ignores-mode: KILLED red=failed:5/200 green=passed:200/200 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m09-event1-ref-dropped: KILLED red=failed:2/200 green=passed:200/200 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m10-explicit-guard-reads-latch: KILLED red=failed:1/31 green=passed:31/31 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m11-ag-composer-unserialized: KILLED red=failed:1/4 green=passed:4/4 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m12-seam-skipped: KILLED red=failed:1/4 green=passed:4/4 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m13-startup-line-silent: KILLED red=failed:2/31 green=passed:31/31 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m14-unrecognised-profile-reports: KILLED red=failed:4/231 green=passed:231/231 ref=78f573e89f5ae73855d3570179b00dc25fec361c
mutation m15-stderr-line-to-stdout: KILLED red=failed:1/31 green=passed:31/31 ref=78f573e89f5ae73855d3570179b00dc25fec361c
```

**Not red-at-base.** The 4.6 row does not ask for red-at-base twins, and most of these twins cannot compile at the base: `ModeBasis`, `Refused`, `Ref`, the seam and the two-argument constructor are all new. The mutations stand in for that check. m03, m05, m10 and m11 each restore one piece of the base behaviour (last-wins, the `ProcessDefault` registration, an explicit guard that reads the process state, a convention blind to the new writes), and each one turns its twin red.

### Testing strategy

Blast radius: `Ashlar.Abstractions` (the guard's record and resolver), `Ashlar.Hosting` (`AddAshlar`'s guard registration and one hosted service) and test hygiene. No route changes.
- [x] Focused tests for every behaviour: the mode table, both bindings, the latch, strictest-wins, the seam, the composition rule, the startup line.
- [x] The touched cert-gate and hosting classes on net8.0 and net10.0.
- [x] A prod-style check of the composition: `EgressApiHostProdStyleTests` boots the API host, and asserts the composed guard and one activator.
- [ ] `make kernel-gate` / `make test-prod-style`: not run locally; CI runs them.

## Records (for the drift audit)

This PR touches these records and docs:
- `CHANGELOG.md`, Unreleased: the 4.6 entry (a mode setting exists, the variable is not named; event 1 at version 1; `AddAshlar`'s own guard composes the strictest profile noted and replaces `ProcessDefault` or the guard an earlier `AddAshlar` registered in the same collection; the mode line does not describe a host's own guard; the strictest-wins behaviour change, `ForbidsRemoteProtocolEgress` and `DisplayName` included).
- `ci/cert-gate-assertions.md`: row 56 (`ProcessGlobalEnvironmentConventionTests`: the egress writes, the fourth and fifth facts, offender count 20); row 64 (the mode sentence, which now says a later `AddAshlar` on the same collection replaces the guard an earlier one composed; `EgressModeResolutionTests` and `EgressModeProcessBindingTests` in its Tests column; the measured floors 2,133 / 149 / 67 at 4.6, after 4.1's one occurrence); the Certification count paragraph (127 `.cs`, 130 entries).
- `ci/egress-inventory.tsv`: one note only, the `AshlarServiceCollectionExtensions.cs` row. It now cites `AddHttpClient` at `:136` and `AddAshlarEgressGuard` at `:139`; 4.6's edits had moved them from `:124` and `:127`. The merge check found this (commit `f9c30137`). No count moves.
- `docs/EgressInventory.md`: the mode section (hedged, names the opt-in as not supported until 4.11; its "Two bindings" and "Startup line" bullets say what `AddAshlar` replaces and what the line describes), the profile-check paragraph (`DescribeProfile`, `EgressGuard.cs:212`; the guard no longer calls `ForbidsRemoteProtocolEgress`), and citations `AshlarDeploymentProfileEnvironment.cs:81`, `:75-80`, `AshlarServiceCollectionExtensions.Deployment.cs:103-150`; the opening paragraph now names 4.6 beside 4.1 among the PRs that re-derived the line numbers in the files they edited.
- `docs/specs/SPEC-007-security-labels-and-reference-monitor.md`: the "PR 4.6 (this PR)" status bullet, now a sibling of 4.1's (the integrator fills the number; it names `ASHLAR_EGRESS_MODE`, and now says `AddAshlar` binds in place of `ProcessDefault` or of the guard an earlier `AddAshlar` bound in the same collection) and the 4.11 plan bullet's note on the explicit-profile theory.
- `src/Ashlar.Abstractions/PublicAPI.Unshipped.txt`: `EgressDecision.ModeBasis`, `.Refused`, `.Ref`, and the guard constructor's `egressMode` parameter (phase A; unchanged in phase B).
- `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressGuardConventionTests.cs`: the floors comment (2,133 files, 149 occurrences).
- Public XML docs: `AddAshlar`, `AshlarHostingOptions.EgressMode` (round 2: what the raise reaches on the same collection, in a container already built, in another collection, and in a host's own guard), `EgressGuard` (class remarks, constructor), `EgressDecision.Profile` and `.ModeBasis`, `EgressEventSource` remarks.
- `docs/knowledge-graph.json` and `docs/knowledge-graph.md`, regenerated (declared facts 4809 on the merged tree).

Expected conflicts with the sibling lanes still open (4.2 and 4.4; 4.1 is merged and its conflicts are resolved here), whichever merges first: SPEC-007's PR 4 status bullets; CHANGELOG Unreleased; the cert-gate count paragraph and row 64's floors; the `EgressGuardConventionTests` floors comment (4.2 adds one production file and one http.new, 4.4 one production file and no occurrence, so with all of them merged the scan should read 2,135 files and 150 occurrences: re-measure); `docs/EgressInventory.md`; `Ashlar.Abstractions.csproj`'s `InternalsVisibleTo` list (4.4 adds `Ashlar.Orchestration` beside this PR's `Ashlar.AI.Pipeline`); `EgressGuard.cs`'s class remarks (4.4 rewrites "Current label" just above this PR's "Mode" paragraph); `PublicAPI.Unshipped.txt`; the knowledge graph.

## Checklist

- [x] Repo gates pass (26/26); the cert-gate passes in the devtest container on net8.0 (2698/2698 at `d994185`, after merging 4.1).
- [x] Documentation updated: `docs/EgressInventory.md`, `ci/cert-gate-assertions.md` (rows 56 and 64), `CHANGELOG.md`, the SPEC-007 status and 4.11 plan, the knowledge graph.
- [x] Every phase B behaviour change has a twin shown red first and a killed mutation.
- [x] Public API recorded in `src/Ashlar.Abstractions/PublicAPI.Unshipped.txt`.
- [x] No `TODO` or `NotImplementedException`.
- [x] Behaviour changes named above and in the CHANGELOG.

## Release

- [x] Not a versioned release; skip.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01NNv8ZAWjwkJYwgB4yFMLBs
