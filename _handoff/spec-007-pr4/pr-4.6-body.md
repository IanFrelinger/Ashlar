## Summary

SPEC-007 PR 4.6, **mode plumbing with every profile still reporting**. The egress guard now resolves a mode for every decision through one resolver, and the record says what decided it, whether a route would have to refuse, and a random reference a refused party may be shown. `enforce` becomes an opt-in on every profile, so the enforcing twins of 4.7 to 4.9 can run through the real resolver. Every profile still defaults to `report`, and **nothing refuses**: no route acts on the mode until 4.7, and AirGapped and SecureWorkstation keep reporting until the switch (4.11).

- **One resolver, two bindings** (design §2.1, D1). `EgressEnforcement.ResolveMode(profile, override)` is a pure function, pinned as the full table: 6 profiles × {unset, `report`, `enforce`, junk}. The process binding is `EgressGuard.ProcessDefault`, which the 16 explicit sites call. The composition binding is the guard `AddAshlar` now registers, `new EgressGuard(profile, override)`, for factory clients and MEAI targets. It replaces an `IEgressGuard` descriptor **only** when its instance is `ProcessDefault`, so an `AddAshlarEgressGuard()` that ran before `AddAshlar` is bound, and a host's own guard is kept.
- **The opt-in** (D2, D3, D4). `ASHLAR_EGRESS_MODE` is read **once per process**, by `AddAshlar` or at the first decision of a process that never runs it. `AshlarHostingOptions.EgressMode` can only raise the mode, and is never bound from `IConfiguration`. A guard built with a profile takes its override from its constructor only and never reads the environment.
- **Fails closed** (D7). An unrecognised mode value, an unrecognised profile, and a fault while resolving the mode all give `enforce` (bases `override`, `profile:unrecognised` and `fault`). The mode is resolved in its own step before the classification, so a fault cannot leave the old `enforces = false` placeholder failing open.
- **The strictest profile noted wins** (D5). Once AirGapped has been noted nothing lowers it, and once SecureWorkstation has been noted only AirGapped replaces it. A second `AddAshlar()` with no profile no longer turns an AirGapped process into Full.
- **Records** (D8). `EgressDecision` gains `ModeBasis`, `Refused` (`Mode == enforce && !Access.Allowed`) and `Ref`, and the `Ashlar-Egress` event 1 appends `modeBasis`, `refused` and `ref` after `fault`.
- **Test hygiene** (§2.10, D41). An internal reset seam, `EgressProcessState`, snapshots and restores the noted profile and the mode latch. Every test class that composes AirGapped or SecureWorkstation now restores through it, and `ProcessGlobalEnvironmentConventionTests` treats those compositions, a raised mode and the seam as process-global writes.
- **IVT** (D9). `Ashlar.Abstractions` grants `InternalsVisibleTo` to `Ashlar.AI.Pipeline`, for 4.7's fallback mode and synthetic `NoDecision`.
- **Startup line** (D10). A hosted activator logs one line: the mode, its basis, the profile, and whether the profile was defaulted. A process whose mode is not plain `report` also writes the line to stderr, once.

These are classification-style controls inside the runtime. They are not an accredited cross-domain solution.

No non-test code under `application/` changes, so this is not `[coordinated-integration]`.

**Departures from the design, stated** (each is listed for the integrator too):
- **The stderr line is not limited to host-less CLI verbs.** It is written once per process by the first `AddAshlar`, or by the first decision of a process-bound guard, whose mode is not plain `report`. The library cannot tell whether a host will start, and CLI verbs run `AddAshlar` through a host they build lazily and never start. A hosted process with the opt-in therefore gets the stderr line and the log line.
- **D7 applies to explicit-profile guards too.** An unrecognised profile fails closed in the one resolver, whoever passes it. An override of `report` on such a profile is recorded as `override-ignored`.
- **A test seam in production code.** `EgressEnforcement.ModeResolutionProbe` (internal, `null` in production) lets the fault twin make mode resolution throw, because no input to the pure resolver can fault. The reset seam snapshots and restores it.
- **The convention gained a fourth fact.** Besides treating the new writes as process-global, `No_file_that_leaves_egress_state_behind_skips_the_reset_seam` enforces §2.10's "restores through the seam".
- **`AshlarHostingOptions.EgressMode` is a `string?`.** It mirrors the variable and the guard's constructor. Ashlar.Hosting has no PublicAPI baseline, so only the Abstractions API is recorded.
- **The opt-in is named in `docs/EgressInventory.md` (the mode section §4 asks for) and the CHANGELOG**, both marked "not a supported setting until 4.11". No user-facing configuration doc mentions it.

## Changes

- **`src/Ashlar.Abstractions/Security/Egress/`**
  - `EgressEnforcement` (new, internal static):
    - `ResolveMode`, `ParseOverride` and the basis constants (`profile:<p>`, `profile:unrecognised`, `override`, `override-ignored`, `fault`; `break-glass`, `host-opt-out` and `operator-verb` are declared for 4.9 and 4.11 and not produced);
    - the process latch: `ProcessOverride` and `NoteHostingOption`, read once and raise-only;
    - `StartupLine` and `AnnounceOnce` (stderr, once per process, only for a mode that is not plain report);
    - `NewReference` (8 random bytes from `RandomNumberGenerator`, as 16 lowercase hex digits);
    - `ModeResolutionProbe`, a test seam that is `null` in production.
  - `EgressProcessState` (new, internal static): the reset seam, with `Snapshot`, `Restore` and `Reset`.
  - `EgressGuard`:
    - the constructor is `EgressGuard(string? deploymentProfile = null, string? egressMode = null)`;
    - `Evaluate` resolves the mode first, in its own try, falling back to `enforce`/`fault`; the classification try is unchanged;
    - it stamps `Mode`, `ModeBasis` and `Ref`.
  - `EgressDecision`: `ModeBasis`, `Refused` and `Ref`, appended through the internal constructor; the `Mode` and `ProfileEnforcesByDefault` docs are updated.
  - `EgressEventSource`: event 1 appends `modeBasis`, `refused` and `ref`.
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
    - `BindComposedEgressGuard`;
    - `EgressModeStartup` (internal record);
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
    - the activator line at Information and at Warning;
    - the stderr line.
  - `Helpers/EgressProcessStateScope.cs` (new): reaches the seam by reflection.
  - `ProcessGlobalEnvironmentConventionTests`: the new writes, and a fourth fact, `No_file_that_leaves_egress_state_behind_skips_the_reset_seam`.
  - Restore through the seam: `HostingDeploymentProfileTests`, `KernelPhaseResolutionTests`, `KernelDiCompositionProdStyleTests`, `OnboardingE2ETests` and `AirGappedProfileApiHostProdStyleTests`.
  - Flipped pins:
    - `EgressKernelFactoryTwinTests.TheGuardIsRegisteredOnce` and `EgressApiHostProdStyleTests.TheApiHost_RegistersTheGuardOnce…`: the single `IEgressGuard` is now the composed guard, not `ProcessDefault`;
    - `EgressGuardDecisionTests`: event 1's payload names gain the three fields, and the explicit-profile theory's `air-gapped-ish` row records `enforce` (an unrecognised profile fails closed).
- **Records**
  - `docs/EgressInventory.md`: a mode section, and four line citations re-derived (`AshlarDeploymentProfileEnvironment.cs:81` and `:75-80`, `EgressGuard.cs:204`, `AshlarServiceCollectionExtensions.Deployment.cs:103-150`).
  - `ci/cert-gate-assertions.md`: the environment-convention row describes the egress writes and the fourth fact. Its offender count is corrected from 22 to 20, the size of the frozen set. The certification file count goes from 125 to 127.
  - `CHANGELOG.md`: an Unreleased entry.
  - SPEC-007: the PR 4 status gains a 4.6 line.
  - The knowledge graph is regenerated.

## Behaviour changes, and what stays report-only

**Report-only, unchanged:** no route reads `Mode` or `Refused`. The HTTP handler, the MEAI chat client and the 16 explicit sites still discard the decision. The default mode is `report` on all six profiles, so with `ASHLAR_EGRESS_MODE` unset every record's `Mode` is `report` and `Refused` is `false`, as before.

**What does change:**
1. **Strictest-wins.** After `AddAshlar(AirGapped)`, a later `AddAshlar` in the same process with another profile no longer changes `ForbidsRemoteProtocolEgress`, `DisplayName` or `ProcessDefault`'s profile. The MCP and A2A validators therefore stay on AirGapped. SecureWorkstation is replaced only by AirGapped. Among the other profiles, the last one noted still wins.
2. **`IEgressGuard` in an `AddAshlar` container** is the composed guard, not `EgressGuard.ProcessDefault`. Code that compared the resolved guard to `ProcessDefault` by reference now sees a different instance, which decides by the same rules under the composed profile.
3. **One hosted service more per `AddAshlar` container** (`EgressModeStartupActivator`). It logs one Information line at start, in category `Ashlar.Egress`, which is not filtered to Debug like the decision records.
4. **An unrecognised profile on an explicit guard or in the profile variable** now records `Mode = enforce` (fail closed). `AddAshlar` already refuses such a profile at startup, so only a host-less process or a hand-built guard sees this, and nothing acts on it yet.
5. **Event 1 has 20 fields** (17 before). It is append-only, so positional readers of the first 17 are unaffected.
6. **`ASHLAR_EGRESS_MODE`** is read once per process. With it set to anything but `report`, the process writes one line to stderr. It is not a supported setting until 4.11.

## Testing

All builds and tests ran in the Linux devtest container through `scripts/test-in-container.sh`. Nothing ran on the host.

| Check | Result |
|---|---|
| `Ashlar.Abstractions` (netstandard2.0, net8.0, net10.0), `Ashlar.Hosting` with `Ashlar.AI.Pipeline` | 0 errors. `Ashlar.LocalDevCore.slnf` (the `build-core` check) at `a501c2b`: 0 errors, 2 warnings, both the harness's SourceLink "no source control information" warnings |
| Full cert-gate (`scripts/run-cert-gate.sh`, net8.0) | **2637/2637**, 0 skipped (the skip guard matches the baseline) at `296c745`, whose source tree equals the head's; the later commits are the knowledge graph and the squash. Master had 2542: this adds 63 (`EgressModeResolutionTests`), 31 (`EgressModeProcessBindingTests`) and 1 (the fourth convention fact) |
| Touched classes, cert-gate and hosting (17 classes), net8.0 | **604/604** at `14c0d2a` (no file under `src/` changes after it) |
| Touched classes, net10.0 | **609/609** at `296c745` |
| `scripts/ci/run-repo-gates.sh` | **25/25** at `a501c2b`, the knowledge-graph byte-compare included |

### Mutation checks

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

Every row is KILLED, and every green run passed all of its tests. The first m13 run was stopped by the session's 2-hour limit on a background task, before its red run had finished building, and m13 was run again from the start. The summary lines, verbatim:

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

## Checklist

- [x] Repo gates pass; the cert-gate passes in the devtest container on net8.0.
- [x] Documentation updated: `docs/EgressInventory.md`, `ci/cert-gate-assertions.md`, `CHANGELOG.md`, the SPEC-007 status, the knowledge graph.
- [x] Public API recorded in `src/Ashlar.Abstractions/PublicAPI.Unshipped.txt`.
- [x] No `TODO` or `NotImplementedException`.
- [x] Behaviour changes named above and in the CHANGELOG.

## Release

- [x] Not a versioned release; skip.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01GYPVuHoik1sH5U6gWtSVRT
