# State of lane 4.5 (SPEC-007 PR 4 row 4.5, subject producers, report-only) at 2026-10-06 ~22:40Z

Read-only reconstruction from the lane clone, its logs, mutation summaries, the scouts, the critic, the design and the
model report. `$SP` = `/tmp/claude-0/-home-user-Ashlar/a81481a5-34f6-5b00-a61f-74b03c502c7b/scratchpad`. Clone paths
below are relative to `$SP/c-4.5`. "head" = `91d3b48c`. Every claim cites a file:line or a log.

**One-line verdict.** Code, twins and records are complete for the §4 row and for every integrator instruction; the
PR body is a skeleton with its sections drafted in `ws/frag/*.md` but not merged; **nothing has been verified at head**:
the last complete cert-gate is at `4500574a` (six commits and ~190 production/test lines ago), the last complete egress
filter at `d465aa33`, the last repo gates at `f51322ea`; 8 of 26 planned mutations ran (7 KILLED, 1 INVALID), all at
`f51322ea`, and the G5 fix's red-first run never ran. Two records are stale (the floors "2,136" predates a second new
production file, `ReadReporter.cs`). The branch is unsquashed (7 WIP commits).

## 1. Branch and commits

| Item | Value |
|---|---|
| Clone | `$SP/c-4.5`, branch `claude/spec-007-pr4-4.5-producers`, working tree clean (`git status --porcelain` empty) |
| HEAD | `91d3b48cb607b0d726542885c239d18452f02fbd` |
| `origin/claude/spec-007-pr4-4.5-producers` | `91d3b48cb607b0d726542885c239d18452f02fbd` (**pushed == local**) |
| `origin/master` and merge-base | both `de41a8ac8d0d1ae94f075c7aba2dde366c890fec` (master unmoved; 4.3 and 4.10 not merged yet) |
| Diff stat `origin/master..HEAD` | 22 files, +2,036 / −75 |
| Trailers | every one of the 7 commits ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_017TjtzCLb6VaYxD1MtRJ1ja` (`git show --format=%b`) |
| Helper refs (local only, never push) | branches `twins-alone` (= tag `t-twins` = `22bfbd9d`, parent `f51322ea`) and `g5-twin-alone` (= tag `t-g5` = `c71fcc3c`, parent `a5e5ac92`); tags `t-all` = `91d3b48c`, `mut-base-f51322ea`, `wip-4.5-presquash` = `4bc3fe16` (the pre-restart WIP chain `4ef20937..4bc3fe16`, superseded by the squash) |

Commits on master (oldest first; all 2026-10-06 UTC):

| # | SHA | Time | Message | Files |
|---|---|---|---|---|
| 1 | `f51322ea` | 18:58 | SPEC-007 PR 4.5: subject producers, report-only (self-extend frame at SystemHigh, ToolCallingAgent read scopes, RAGTool labels, agent-backed responses observe SystemHigh, open reads count as SystemHigh, floor-pin convention) — the squash of the pre-restart WIP; amend chain `4500574a` → `e2ba2f1a` → `d465aa33` → `f51322ea` (the amends after `4500574a` touched only `EgressProducerTwinTests.cs` +98 lines and records) | 20 files, +1,559/−69 |
| 2 | `e4fdf582` | 19:27 | attack-list fixes (RAGTool canonical spelling, narrowed read-nothing refusal), twins, convention implementers, records (WIP) | 13 files: `EgressGuardChatClient.cs` (8), `ILabelledTool.cs` (3), `RAGTool.cs` (28), `VectorMath.cs` (16), 3 test files, records |
| 3 | `0983db60` | 20:17 | integrator decisions on the model report (pinned, basis, open until disposed, limit (a)); two twins (WIP) | `EgressSubject.cs` (7), `ReadScope.cs` (6), `EgressSubjectReadScopeTests.cs` (+59), row 66, KG |
| 4 | `a07cbf80` | 20:26 | qualify IEmbeddingGenerator in the refusal twin (WIP) | 1 test line |
| 5 | `651effb0` | 20:33 | G5 report-only ReadReporter for labelled tools; G8 paired records; G6/G7/G14 sentences (WIP) | `ReadReporter.cs` (new), `ReadScope.cs`, `ILabelledTool.cs`, `ToolCallingAgent.cs`, `RAGTool.cs`, `PublicAPI.Unshipped.txt`, twins, records, KG |
| 6 | `a5e5ac92` | 20:35 | owner decision O1 recorded (custom levels SystemHigh in PR 4); basis twin async (WIP) | SPEC-007 (2), 2 test files |
| 7 | `91d3b48c` | 20:38 | hostile labelled tool reports before it probes the surface (WIP) | `EgressProducerTwinTests.cs` (+6/−4) |

Not squashed. The brief (`ws/PHASE-C-LANE-BRIEF.md` step 7) requires one commit on master with the trailers.

## 2. Implemented vs the §4 row (`ws/DESIGN-4-final.md:860`, records row `:880`) and the owner rule of 2026-10-06

| Bullet | State | Where |
|---|---|---|
| Self-extend frame at SystemHigh (Q2, D19) | **done** | `src/Ashlar.BackgroundAgents.HostRunners/SelfExtendRunnerAdapter.cs:155-161` (`using var subject = EgressSubject.Enter("agent:" + resolvedAgentId, new HighWaterMark(SecurityLabel.SystemHigh));` with the obligation-(d) comment); twin `EgressProducerTwinTests.Production_self_extend_records_its_agent_as_the_subject_at_SystemHigh` `:539` |
| `ToolCallingAgent` read scopes (D13, D14) | **done** | `src/Ashlar.BackgroundAgents/Agents/ToolCallingAgent.cs:247-263` (`LabelledToolFor` `:312`, `using (var read = EgressSubject.BeginRead())` `:249`, `read.Reporter` `:255`, `read.Complete()` `:263` last in the block) |
| Labelled-tool marker, RAGTool alone | **done** | `src/Ashlar.Abstractions/Security/Egress/ILabelledTool.cs` (public, `InvokeLabelledAsync(ToolCall, WorldSnapshot, ReadReporter, CancellationToken)`); `ReadReporter.cs` (sealed, internal ctor, `Report` only); `ReadScope.Reporter` (`ReadScope.cs`); `CapabilityRegistry.Find` (`src/Ashlar.Runtime/CapabilityRegistry.cs:39-43`); `RAGTool : ILabelledTool` (`RAGTool.cs:29`); pinned by `EgressSubjectProducerConventionTests.Only_RAGTool_declares_itself_labelled_in_production` `:94` (`"src/Ashlar.BackgroundAgents/RAG/RAGTool.cs | RAGTool"` `:50`) |
| RAGTool canonical tiers and "read nothing" (D15, Q8) | **done** | `RAGTool.cs:82` `CanonicalSpellings`, `:89-107` `HitLabel` (trim → spelling `TrustTierOrder` ranks → `GetByName` → `DataSensitivityLevels.All` → `ToDataLabel`, else SystemHigh), `:150-151` read-nothing only for `VectorMath.IsUnrankableQuery(ex)`, `:160-162` `Public` then each hit; `VectorMath.cs` `UnrankableQueryMessage` const + `IsUnrankableQuery` |
| Trim parity vs `TrustTierOrder.RecordLabel` | **done** | theory `RAGTool_labels_a_hit_exactly_as_TrustTierOrder_RecordLabel_does` `:197` over `TierNames` `:183` (28 names per row 68); second theory `RAGTool_never_labels_a_hit_below_what_the_pipeline_treats_it_as` `:304` over 6 non-ASCII look-alikes; custom-level theory `:275` (7 rows) |
| Peer/agent-backed responses observe SystemHigh in `EgressGuardChatClient` (D16) | **done** | `src/Ashlar.AI.Pipeline/Governance/EgressGuardChatClient.cs:43-66` (internal ctor taking the target key, `_responsesAreReads`), `:82-96` and `:106-120` (both overrides non-`async`; synchronous throw observed), `:127-131` `IsAgentBacked` (neither `local:` nor `cloud:`), `:133` `ObserveResponse`, `:138-167` `ObservedAsync` (response; stream observes before each update and in `finally`); `AshlarGovernanceChatClientBuilderExtensions.cs:33` passes the key. Twins `:375-536` (peer, streamed, empty stream, faulted ×2, policy-denied ×2, eager decide, unknown kind, `local:` not a read) |
| Floor-pin convention test + cert-gate row | **done** | `EgressSubjectProducerConventionTests.cs` (6 facts: `:54`, `:67`, `:79`, `:94`, `:108`, `:138`); row 68 of `ci/cert-gate-assertions.md` (file is 95 lines) |
| Producers section (`docs/EgressInventory.md`) | **done** | `docs/EgressInventory.md:15-18` (three sub-bullets) and `:224` (the "No call site carries a `SecurityLabel` for its destination" bullet) |
| Leak skeleton done-when (report mode) | **done** | `Leak_skeleton_after_a_Secret_hit_…_LevelTooLow` `:57` and `Leak_skeleton_after_an_Internal_hit_…_allowed` `:86` (real `RAGTool` over `MeaiVectorDataRagAdapter`/`VectorDataRagService`, governed `local:ollama`) |
| **Owner rule 2026-10-06** (open, unreported read ⇒ SystemHigh for every tool) | **done, as "not yet ended"** (stricter; deviation 1) | `EgressSubject.cs:143-150` (`BeginRead` enters `Frame.ForRead`), `:228` shared `ReadMark = new(SecurityLabel.SystemHigh)`, `:257-258` `ForRead`, `:269` `Live` skips `_isRead`, `:315` a read's frame observes nothing outward; `ReadScope.cs` `Dispose` observes then `_read.Dispose()`; remarks "Reads", "A read's frame is a frame", limit (a) sentence |

Nothing in the row is partial or not started. Two parts remain **unverified at head** (see §3).

## 3. Tests and runs

Every log is in `$SP/logs-4.5/`. Heads in order of the tree: `4ef20937` (pre-restart twins alone) … `4bc3fe16` (pre-restart WIP) → squash `4500574a` → amends → `f51322ea` → `e4fdf582` → `0983db60` → `a07cbf80` → `651effb0` → `a5e5ac92` → `91d3b48c` (head).

| Run | Head | Command / filter | Verbatim result | Current? |
|---|---|---|---|---|
| Red first, producers | `4ef20937` (twins alone on 4.4 code) | `dotnet test … --framework net8.0 --filter 'FullyQualifiedName~EgressSubjectReadScopeTests\|~EgressProducerTwinTests\|~EgressSubjectProducerConventionTests'` | `Failed!  - Failed:    45, Passed:    15, Skipped:     0, Total:    60` (`red-twins-net8.log:476`; the 45 names are listed in `ws/frag/testing.md`) | valid red-first evidence for the base change |
| Green, filter | `04ea9cfb` | egress filter net8.0 + Abstractions ×3 | `Passed!  - Failed:     0, Passed:   499, Skipped:     0, Total:   499` (`green1-net8.log:89`); 0 warnings ×3 | superseded |
| Floors + other projects | `36f2a132` | `EgressGuardConventionTests.F6\|F8` detailed; `Ashlar.Tests.BackgroundAgents`; `Ashlar.Tests.AI.Pipeline` | `ScannedFiles=2136 ExaminedOccurrences=150`; `Passed!  - Failed: 0, Passed: 649, Skipped: 1, Total: 650` (BGA); `Passed!  - Failed: 0, Passed: 107, Skipped: 0, Total: 107` (AIP) (`floors-bga-aip-net8.log:3,17,19`) | **stale: re-run on 91d3b48c** (`RAGTool`, `ToolCallingAgent`, `ReadReporter` changed since; a second production file was added) |
| Filter both TFMs | `f1ddae98` | `run-filter-both.sh` | `Passed! … Passed: 504 … Total: 504` on net8.0 and on net10.0; Abstractions 0 warnings ×3 (`filter-both-f1ddae98.log:12,14`) | superseded |
| Repo gates | `f1ddae98` | `scripts/ci/run-repo-gates.sh` (host) | `repo-gates: all 26 gate(s) passed` (`repo-gates-f1ddae98.log:843`) | superseded |
| Full cert-gate | `36ffae61` | `bash scripts/run-cert-gate.sh` | **killed** by the container restart inside the build; no summary (`cert-gate-36ffae61.log`, 73 lines) | — |
| **Full cert-gate** | **`4500574a`** (first squashed head) | `bash scripts/run-cert-gate.sh` (net8.0) | `Total tests: 2817` (`cert-gate-4500574a.log:2940`); `cert-gate reported 2817 tests (expected>=2812, derived from --list-tests).`; `cert-gate skip guard: 0 skipped (total=2817 executed=2817)`; `== exit: 0` | **stale: re-run on 91d3b48c.** Since then: +98 test lines in the amend chain, then `e4fdf582`, `0983db60`, `651effb0` changed production (`EgressSubject.cs`, `ReadScope.cs`, `ReadReporter.cs`, `ToolCallingAgent.cs`, `RAGTool.cs`, `VectorMath.cs`, `EgressGuardChatClient.cs` 8 lines) |
| Filter both TFMs | `d465aa33` (an amend of the squash; same production code as `f51322ea`) | `run-filter-both.sh` | `Passed!  - Failed:     0, Passed:   510, Skipped:     0, Total:   510` net8.0 (`:14`) and net10.0 (`:16`); Abstractions 0 warnings ×3; `== exit: 0` (`filter-both-d465aa33.log`) | **stale: re-run on 91d3b48c** (last complete filter run; `An_awaited_read_that_throws_observes_SystemHigh` was in it) |
| Repo gates | `f51322ea` | host | `repo-gates: all 26 gate(s) passed` (`repo-gates-f51322ea.log:843`), `exit=0` | **stale: re-run on 91d3b48c** (records and KG changed in `e4fdf582`, `0983db60`, `651effb0`, `a5e5ac92`; KG verified current at head by a rebuild, see §5) |
| Red, attack-list twins | `f02e0bd9` (twins alone, pre-fix) | filter `RAGTool_never_labels\|A_stores_other_ArgumentException\|RAGTool_reports_read_nothing\|RAGTool_labels_a_hit_at_a_custom_level` | build error `EgressProducerTwinTests.cs(857,43): error CS0104: 'IEmbeddingGenerator' is an ambiguous reference` (`red-attack-twins-f02e0bd9.log:73`), exit 1 | superseded (fixed by `a07cbf80`) |
| Red, attack-list twins | `60174bb6` | same | build error `EgressSubjectReadScopeTests.cs(555,86): error xUnit1031` (`red-attack-twins-60174bb6.log`), exit 1 | superseded (fixed by `a5e5ac92`'s async basis twin) |
| **Red, attack-list twins** | **`24a1d2a1`** (= head minus `RAGTool.cs`'s `CanonicalSpellings` check and the `IsUnrankableQuery` narrowing, 8 lines, and minus the `91d3b48c` twin tweak) | same, net8.0 | `Failed!  - Failed:     1, Passed:    14, Skipped:     0, Total:    15` (`red-attack-twins-24a1d2a1.log:90`; `chain-a.log`). The one red: `A_stores_other_ArgumentException_is_not_read_nothing` ("Expected run.Mark.Current to be SystemHigh … but found Public", `:84`). | **Only the narrowed-refusal fix was observed failing.** The spelling-check twin (`RAGTool_never_labels…`, 6 cases), the custom-level theory (7) and the read-nothing twin passed on the pre-fix tree, so the `CanonicalSpellings` change has no red-first evidence; its planned evidence is mutations m24, m08, m09 (not run). |
| Filter both TFMs | `0983db60` | `run-filter-both.sh` | Abstractions 0 warnings ×3, then **killed** before the filter (`filter-both-0983db60.log`, 13 lines, no `Passed!`) | — |
| Filter both TFMs | `a07cbf80` | `run-filter-both.sh` | Abstractions 0 warnings ×3; `== filter net8.0`, `== filter net10.0`, `== exit: 0` but **no `Passed!`/`Failed!` line for either TFM** (`filter-both-a07cbf80.log`, 16 lines): inconclusive (the `grep … \|\| true` swallowed whatever `dotnet test` printed) | treat as not run |
| Filter both TFMs | `a5e5ac92` | `run-filter-both.sh` | Abstractions 0 warnings ×3, then **killed** (`filter-both-a5e5ac92.log`, 13 lines) | — |
| Red, G5 twin alone | `t-g5` = `c71fcc3c` | `mut-4.5/chain-b.sh` step 1 (filter `A_labelled_tool_cannot_complete\|A_labelled_tool_that_reports_and_then_throws`) | **never run** (no `red-g5-twin-*.log`) | needed |
| Filter at `t-all` | `91d3b48c` | `chain-a.sh` step 2 | **never produced a log** (no `filter-both-91d3b48c.log`) | needed |
| Other projects at head | `91d3b48c` | `mut-4.5/final-verify.sh` (cert-gate; BGA, AIP, Orchestration; `RuntimeStudioBlackBoxSmokeTests\|GovernanceFloorPolicyTests\|ToolEdgeGovernanceFloorTests`; F6/F8 detailed) | **never run** (no `cert-gate-91d3b48c.log`, no `other-projects-*.log`) | needed |
| build-core | — | — | not run; not required (no project reference or TFM changed; `ReadReporter.cs`/`ILabelledTool.cs` are new files in an existing project, and the Abstractions build ×3 covers the PublicAPI analyzer) | — |
| net10.0 | `d465aa33` | filter | 510/510 (above) | stale |

**Verified at head `91d3b48c`: nothing.** The two production-semantics facts the integrator asked to settle ("the AsyncLocal fact settled by `An_awaited_read_that_throws_observes_SystemHigh` on net8.0 and net10.0"; the non-flips) were last green at `d465aa33` / `4500574a`.

## 4. Mutations (`$SP/mut-4.5/`, runner `run-mutations.sh`, egress filter, net8.0, each via `scripts/mutation-check.sh`)

Verbatim summary lines (`SUMMARY.txt`; all at `ref=f51322ea`, **not at head**):

```
mutation m01-begin-read-does-not-enter-the-reads-frame: KILLED red=failed:7/510 green=passed:510/510 ref=f51322eaa06025b0f701b18a9fbca1a091d02f83
mutation m05-agent-never-hands-the-scope-to-a-labelled-tool: KILLED red=failed:26/510 green=passed:510/510 ref=f51322eaa06025b0f701b18a9fbca1a091d02f83
mutation m06-ragtool-does-not-report-its-hits: KILLED red=failed:31/510 green=passed:510/510 ref=f51322eaa06025b0f701b18a9fbca1a091d02f83
mutation m04-agent-completes-the-read-before-invoking: INVALID red=exit:1 green=not-run ref=f51322eaa06025b0f701b18a9fbca1a091d02f83 reason=red-no-tests
mutation m14-self-extend-enters-no-frame: KILLED red=failed:3/510 green=passed:510/510 ref=f51322eaa06025b0f701b18a9fbca1a091d02f83
mutation m15-self-extend-floor-public: KILLED red=failed:3/510 green=passed:510/510 ref=f51322eaa06025b0f701b18a9fbca1a091d02f83
mutation m02-live-does-not-skip-a-reads-frame: KILLED red=failed:3/510 green=passed:510/510 ref=f51322eaa06025b0f701b18a9fbca1a091d02f83
mutation m03-a-reads-frame-observes-outward-on-dispose: KILLED red=failed:36/510 green=passed:510/510 ref=f51322eaa06025b0f701b18a9fbca1a091d02f83
```

Red test names per run are in `batch1.log` (e.g. m01: the two in-call-egress twins, the two flipped read-scope twins, the nested-frame, in-call and background twins; m14/m15: the self-extend twin plus the two convention facts). Each log shows `== proof the mutation applied ==`, `porcelain=[]`, `== GREEN run (restored) ==`.

| Status | Ids | Notes |
|---|---|---|
| KILLED (7) at `f51322ea` | m01, m02, m03, m05, m06, m14, m15 | Re-run at the shipping tree: `EgressSubject.cs` changed after (`0983db60`, 7 lines; m01–m03's `--old` strings still match head), `RAGTool.cs` changed after (m06's string still matches), `ToolCallingAgent.cs` changed after (**m05's `--old-file` no longer matches head**: `read` became `read.Reporter`; re-author `m05.old`). `SelfExtendRunnerAdapter.cs` is unchanged since `f51322ea`, so m14/m15 are the only two whose target file is identical; the twins that kill them changed, so still re-run. |
| INVALID (1) | m04 (`read.Complete();` before the `try`) | `reason=red-no-tests`: the mutant did not compile (`ToolCallingAgent.cs(250,14): error CS1513: } expected` …, `m04-….log:97-107`). Re-author the snippet (the `--old-file`/`--new-file` pair broke the braces). |
| **Planned, never run (18)** | m07 unknown tier → Public; m08 no trim; m09 custom level accepted; m10 refusal not read-nothing; m11 no-hits not read-nothing; m12 no target agent-backed; m13 stream observed only at end; m16 frame not a `using`; m17 every target agent-backed (uses `m12.old` + `m17.new`); m18 stream end not observed; m19 response end not observed; m20/m21 synchronous throw not observed (response/stream); m22 a production tool declares itself labelled (`RepoFsReadTool`); m23 every `ArgumentException` reads nothing; m24 drop the canonical-spelling check; m25 a `using` alias of `EgressSubject` in production; m26 a report completes the read (G5) | `--old` texts verified present at head for all of these except as noted. **`m22.new` still implements the pre-G5 signature (`ReadScope read`) and would not compile at head**: re-author to `ReadReporter report`. `chain-b.sh` would run m26, m04, m23, m07, m08 after the G5 red run; it never started. |

## 5. Records

| Record | State | Where / what |
|---|---|---|
| SPEC-007 status bullet | **done**, keeps "(this PR)" (per the brief) | `docs/specs/SPEC-007-security-labels-and-reference-monitor.md:129-149` (**PR 4.5** (this PR) …); `:121` "no production code entered a frame until PR 4.5"; `:76-77` the 4.5 plan line "(discharged in PR 4.5: `EgressSubjectProducerConventionTests` pins both production entries …)" |
| SPEC-007 decisions-log row | **done**, includes O1 | `:448` `2026-10-06 \| PR 4 (4.5)`: the rule, its cost, Scenario B → `SystemHighData`, C4, B/C5 site-and-family, "**Q8 clarified:** in PR 4, RAG maps a custom `IDataSensitivityLevel` to `SystemHigh` (D15); Q8's C3 normalisation … not yet"; intro `:428` names "the PR 4.2 and PR 4.5 answers of 2026-10-06" |
| SPEC-007 4.4 bullet merge SHA | **not done** (deviation 11: left for 4.3; add only if still missing after the master merge) | `(#716)` without SHA |
| CHANGELOG | **done** | Unreleased/Added: the 4.4 entry's "no production code enters a frame or begins a read yet" → past tense; new 4.5 entry naming the rule, the three producers, EG-MDL-01/EG-PROC-01/EG-MESH-01, `RAGTool`'s mapping/spelling/narrowed refusal, no production toolbox, peer responses, the convention, the leak skeleton, `ILabelledTool`/`ReadReporter`/`ReadScope.Reporter` in Unshipped, `CapabilityRegistry.Find` |
| `docs/EgressInventory.md` | **done** | `:15-18` "Subject producers (SPEC-007 PR 4.5; still report-only)" with the G6 sentence (persisted state covered by the runner's floor), G7 (snapshot from literals), the MCP-pump/`ProviderFactory` `no-subject` caveat, the 256-char and synchronous-tool attribution limits, G14 (a redirect hop followed during a tool call is at `SystemHigh`), `RAGTool` in no production toolbox, decorator/other-toolbox, trusted base; `:224` the "for its destination" bullet. `EG-` rows: 67 (unchanged; `grep -c '^| EG-'`) |
| `ci/egress-inventory.tsv` | **unchanged, verified at head**: 86 rows, total 150, guarded 48 | becomes 149 after 4.3 merges (critic G17) |
| `ci/cert-gate-assertions.md` rows | **done** | row 64 (`:64`): current-label clause "a read's frame counting `SystemHigh`"; floors sentence "measured 2,136, 150 and 67 with PR 4.5 merged, after 2,135, 150 and 67 at PR 4.4"; row 65 Why (per `ws/frag/records.md`); row 66 (`:66`): the open-read rule, pinned mark, basis, the two flips by name; **new row 68** (`:68`): rule, tests `EgressProducerTwinTests`, `EgressSubjectProducerConventionTests`, Why; count paragraph `:70-73` "133 `.cs` files … (136 entries …) … then 131, before it read 133" — **verified at head: `git ls-files 'src/Ashlar.Tests.Infrastructure/Tests/Certification/*.cs'` = 133** |
| **Floors (row 64 and the floors comment)** | **STALE** | `EgressGuardConventionTests.cs:205-206` "SPEC-007 PR 4.5 adds one file with no outbound path, `ILabelledTool.cs`: 2,136 files, 150 occurrences" and row 64's "2,136" were measured at `36f2a132`; `651effb0` added a second production file, `src/Ashlar.Abstractions/Security/Egress/ReadReporter.cs` (`git diff --diff-filter=A`), so F6 should now print 2,137 (measure, do not compute). Not a red test (F6 pins floors of 1000/60, `:209-211`); a false record. Re-measure once at the end (critic §6 item 16) and restate both. |
| `PublicAPI.Unshipped.txt` | **done** | +5 lines: `ILabelledTool`, `ILabelledTool.InvokeLabelledAsync(… ReadReporter! report …)`, `ReadReporter`, `ReadReporter.Report`, `ReadScope.Reporter.get`; `PublicAPI.Shipped.txt` unchanged |
| `EgressGuardConventionTests` floors comment | **stale** (above) | `:203-207` |
| `docs/knowledge-graph.{json,md}` | **current at head** | regenerated in `f51322ea`, `e4fdf582`, `0983db60`, `651effb0`; the two later commits touched no new file. Verified by cloning `c-4.5` to `$SP/kg-check`, checking out `91d3b48c` and running `build-knowledge-graph.py`: `git status --porcelain` empty; `declared_facts: 4915` (`docs/knowledge-graph.json:19`, `.md:23`) = the body's `{{KG_FACTS}}` |
| Stale "not yet" sentences | **none** | the records-4.5 §15 grep over CHANGELOG, assertions, SPEC-007, EgressInventory and `Security/Egress/*.cs` returns nothing |
| `docs/DEPLOYMENT.md`, `docs/RELEASE_RUNBOOK.md`, release notes, readiness-gate paths | not relevant; unchanged (report-only; no new project; no `application/` file) | — |
| `ws/DESIGN-4-final.md` §5 (storage branch) | amendment **drafted and applied to the scratchpad copy** | `ws/design-4.5-amendment.md` (20:33); `diff ws/DESIGN-4-final.before-4.5.md ws/DESIGN-4-final.md` shows the "Amended 2026-10-06" header, Scenario B → `SystemHighData`, C4, C5 and the m01 mutation note. Publishing to `claude/spec-007-pr4-workspace` is the integrator's. |

## 6. Integrator instructions, one by one

| Instruction | State | Evidence |
|---|---|---|
| (a) commit trailer "Claude Fable 5.1" | **implemented** on all 7 commits | `git show --format=%b f51322ea..91d3b48c`; the squash commit must carry it too |
| (b) push after every commit | **implemented** | `origin/claude/spec-007-pr4-4.5-producers` = `91d3b48c` = HEAD |
| (c) `model-4.5/REPORT.md` landed: check rule parity, quote §8 and the counts | **drafted, not in the body** | `ws/frag/model.md` checks §1 op by op against `EgressSubject.cs`/`ReadScope.cs` ("the modelled rule is the implemented rule"), quotes §8 verbatim with 41,755,599 sequences, 20,664,823 at N=7, 1,900,357 reproducing 4.4, B1–B4 caught in 2–5 ops (`model-4.5/REPORT.md:124-127, 218-234`); the body still has `{{MODEL}}` |
| (d) PINNED read frame | **implemented** | shared static `ReadMark` (`EgressSubject.cs:228`), never lowered; remarks "A read's frame is a frame"; `ws/frag/deviations.md` integrator decision 1 |
| (d) flip `A_read_observes_into_every_live_frame_of_its_chain_from_whatever_flow_it_ends_on:179` → `SystemHighData` | **implemented** | `EgressSubjectReadScopeTests.cs:170` test; the diff replaces `LevelTooLow` with `stuck.Current == SystemHigh`, `Reason == SystemHighData`, plus the "frame entered afterwards" check; named in row 66 |
| (d) flip `A_completed_read_with_no_report_observes_SystemHigh:50` | **implemented** | `:51` test; the in-scope decision now `SystemHigh` ("a read that has not ended counts as SystemHigh on the flows inside it"); named in row 66 |
| (d) BASIS = nearest live subject frame (twin) | **implemented** | `Frame.Live` skips `_isRead` (`EgressSubject.cs:269`); twin `A_decision_inside_an_open_read_is_named_by_the_nearest_live_subject_frame_never_by_the_read` (async since `a5e5ac92`); basis asserts in the in-call twins |
| (d) OPEN UNTIL DISPOSED (twin) | **implemented** | `ReadScope.cs` remarks "While the read has not ended"; twin `A_report_and_Complete_do_not_lower_an_open_read_until_it_is_disposed`; third decision of `A_tool_that_reads_then_egresses_within_one_call_is_decided_at_SystemHigh` |
| (d) limit (a) wording extended | **implemented** | `EgressSubject.cs` known limits: "A read scope not ended as its flow's own head stays on the flow the same way, and that flow decides SystemHigh from then on" |
| (d) `[unverified]` AsyncLocal fact settled by `An_awaited_read_that_throws_observes_SystemHigh` on net8.0 and net10.0 | **settled at `d465aa33`** (510/510 both TFMs, read frame present) — **not re-run at head** | `filter-both-d465aa33.log:14,16`; `EgressSubjectReadScopeTests.cs:149` |
| (e) G5: report-only surface; twin (reports Public, calls Complete, throws → SystemHighData); mutation | **code and twins implemented; mutation and red-first NOT run** | `ReadReporter.cs`; `ReadScope.Reporter`; `ToolCallingAgent.cs:255`; twins `A_labelled_tool_cannot_complete_or_end_the_read_it_is_handed` `:330` (hostile tool invokes every parameterless public method of what it is handed; `tool.Invoked.Should().Equal("Report")`; `Decisions[1].Access.Reason == SystemHighData`), `The_surface_a_labelled_tool_is_handed_reports_and_does_nothing_else` `:343` (reflection: sealed, not `IDisposable`, one public method `Report`, no properties), `A_labelled_tool_that_reports_and_then_throws_counts_as_SystemHigh` `:321`. Mutation m26 prepared (`m26.old/new`), **not run**; red run of the twin against the old shape at `t-g5`=`c71fcc3c` (which hands the `ReadScope`, `ToolCallingAgent.cs:254` there) prepared in `chain-b.sh`, **not run**. |
| (e) keep "open until disposed" | **kept** | above; `ws/frag/deviations.md` deviation 1 ("stricter than the decision's wording, on purpose") |
| (e) verify by name the two flips and that `CloudBedrock_DeniedByPolicyGate_…` and `AThrowingCustomGuard_…` do NOT flip | **flips implemented; non-flips not re-verified at head** | the three tests exist unchanged at `EgressGuardChatClientTwinTests.cs:60`, `:479`, `:490` (file not in the diff); last green in the `4500574a` cert-gate; `EgressGuardChatClient.cs` changed by 8 lines in `e4fdf582` since |
| (e) F5 green | last green in the `4500574a` cert-gate; **stale** | `AshlarGovernanceChatClientBuilderExtensions.cs` is the only `new EgressGuardChatClient(` site; unchanged since |
| (e) the three "not yet" sentences | **done** | §5 grep: none |
| (e) `docs/EgressInventory.md:220` | **done** | now `:224` |
| (e) producers section with G6/G7 sentences, MCP-pump no-subject caveat, G14 | **done** | `docs/EgressInventory.md:16-17` (quoted in §5) |
| (e) G8 paired records in the self-extend twin | **done** | `EgressProducerTwinTests.cs:557-569`: `inside.Current == outside.Current`, `Access.Allowed`, `Access.Reason`, `DestinationClass`, `DestinationLabel` equal to the no-frame decision; `outside.CurrentBasis == "no-subject"` |
| (e) draft `ws/design-4.5-amendment.md` (Scenario B → `SystemHighData`, C4, C5) | **done** | `ws/design-4.5-amendment.md`; also applied to `ws/DESIGN-4-final.md` (see §5) |
| (f) O1: D15 stands, custom tiers SystemHigh in PR 4; Q8's C3 deferred; record in the 4.5 row and the parity theory | **done** | SPEC-007 `:448` "Q8 clarified"; `RAGTool_labels_a_hit_at_a_custom_level_SystemHigh_whatever_its_name_or_value` `:267-275` (7 rows incl. `Open` −1, `Top Secret` 4, `ſecret`); `TierNames` includes `Restricted`, `SystemHigh`, `Unclassified`, `Top Secret`, `top_secret`; `ws/frag/deviations.md` deviation 5 |

## 7. Attack list, records list, critic items

`ws/frag/adversarial.md` holds the full close-out tables; summary:

| Source | Closed | Open / caveats |
|---|---|---|
| `ws/attack-4.5.md` A.1–A.6, items 1–25 | 1–7, 9–17, 19, 21–23 closed by twins and code (cited in the fragment); 8, 18, 20, 24, 25 closed by recording a limit, no twin (8: no channel to probe; 18: MCP pump; 20: attribution; 24: uninstrumented host paths; 25: not a 4.5 path) | the fragment's mutation citations for 4 (m04), 7 (m22), 10 (m24, m08, m09), 11 (m09), 12 (m23), 16 (m25), 21 (m12, m13, m18–m21) name mutations **not yet run**; item 10's twin never went red (§3) |
| `ws/attack-4.5.md` B (10 claims) | B1 moot (spelling check); B3 answered (only the legacy stores throw the refusal); B4, B7 by twins; B10 argued; B2, B5 (admission record), B6, B8, B9 recorded not tested | as listed |
| `ws/attack-4.5.md` C (11 design findings) | C1–C8, C11 → deviations 2, 3, 4, 6, 8, 10, 12 and the flips; C9, C10 are stale citations in the design itself | — |
| `ws/records-4.5.md` §§1–13 | done (SPEC-007, CHANGELOG, EgressInventory, TSV unchanged, rows 64–68, PublicAPI, floors, KG, readiness, no `[coordinated-integration]`, runbook, environment convention, flips) | 1.2 (4.4's SHA) deferred to 4.3; **7.1 floors now stale** (§5); 13.2 non-flip re-run pending |
| `ws/records-4.5.md` §14 (test files likely broken) | BGA 649/650 and AIP 107/107 at `36f2a132` | **re-run at head**: `ToolCallingAgentReActTests`, `RAGToolTests` (`InvokeAsync_WhenTheStoreRefusesAnUnrankableQuery…`, `InvokeAsync_DoesNotSwallowAnUnrelatedFailure`), the `SelfExtendRunnerAdapter` tests, `GovernanceMiddlewareTests`, `EgressGuardChatClientTwinTests`, `Ashlar.Tests.Orchestration` |
| `ws/records-4.5.md` §15 (counts to re-measure) | TSV, Certification count, EG rows, KG, stale sentences re-derived at head in this file | floors, cert-gate total, repo gates: re-run; repeat all after the master merge |
| `ws/records-4.5.md` owner Q1–Q4 | answered (implementation + integrator; O1; default no for `RAGTool` in self-extend; Abstractions marker) | — |
| Critic §1 G5 | code + twins done | **m26 and the red-first run open** |
| Critic §1 G6, G7, G8 | done (`docs/EgressInventory.md:16`; `EgressProducerTwinTests.cs:557-569`) | — |
| Critic §3 (already decided) rows for 4.5 | each recorded in `ws/frag/deviations.md` with its citation | — |
| Critic §4.1–4.9, 4.11, 4.12 | closed per the fragment | §4.10 exact-count twins for read frames: **not done**, follow-up (needs a read-frame node in the C# `FlowModel`) |
| Critic §5.1, §5.2 G14, G17, G18 | conflicts listed; G14 sentence in the producers section; G18 F5 to re-run | G17: restate 149 after 4.3 |
| Critic §6 items 1–6 (4.5) | 1 code done (mutation/red pending); 2 done; 3 done (O1 recorded); 4 flips done, non-flips to re-verify; 5 done; 6 drafted | as noted |

## 8. Deviations, decisions and placeholders in the body (`ws/pr-4.5-body.md`, 21,524 bytes)

Deviations drafted in `ws/frag/deviations.md`: 12 deviations (1 "not yet ended"; 2 marker + `ReadReporter` + `CapabilityRegistry.Find`; 3 agent-backed keyed on the target key; 4 a policy-denied agent-backed call raises to SystemHigh; 5 custom levels SystemHigh = O1; 6 narrowed read-nothing; 7 canonical spelling check; 8 the three §5 amendments; 9 D17 needs no code; 10 `RAGTool` stays out of self-extend's toolbox; 11 4.4's `(#716)` without SHA; 12 work captured before the frame stays `no-subject`) plus the 5 integrator decisions (pinned; basis; open until disposed; limit (a) wording; the AsyncLocal fact settled).

Placeholders and items to fix before the PR opens:

| Item | Where |
|---|---|
| **"Resume state" section at the top** (written after the restart; cites pre-squash SHAs `4ef20937`, `04ea9cfb`, `36f2a132`, `f1ddae98`, `36ffae61`) | body lines 1–20: remove or fold into Testing |
| Top-level placeholders (11): `{{PRODUCER_COUNT}}`, `{{PRODUCER_METHODS}}`, `{{TESTING}}`, `{{MUTATIONS}}`, `{{NOT_OBSERVED}}`, `{{MODEL}}`, `{{DEVIATIONS}}`, `{{ADVERSARIAL}}`, `{{RECORDS}}`, `{{GATES}}`, `{{CERT}}` | body; the section texts exist in `ws/frag/{testing,not_observed,model,deviations,adversarial,records}.md`; the mutation table is produced by `mut-4.5/gen-table.py` from `SUMMARY.txt` |
| Fragment placeholders: `{{HEAD}}`, `{{HEAD8}}`, `{{CERT}}`, `{{ADDED}}`, `{{FILTER}}`, `{{FILTER_TOTAL}}`, `{{TALL}}`, `{{TALL8}}`, `{{TREE_NOTE}}`, `{{BGA}}`, `{{AIP}}`, `{{ORCH}}`, `{{OTHER}}`, `{{FLOORS}}`, `{{GATES}}`, `{{KG_FACTS}}` (= 4915), `{{PRODUCER_COUNT}}`, `{{RED_ATTACK}}`, `{{MUT_NOTES}}` | `ws/frag/testing.md`, `adversarial.md`, `not_observed.md`, `records.md` |
| `{{RED_ATTACK}}` must say: `Failed: 1, Passed: 14, Total: 15` at `24a1d2a1`; only `A_stores_other_ArgumentException_is_not_read_nothing` red; the spelling-check and custom-level twins passed on the pre-fix tree, so that fix rests on mutations m24/m08/m09 | `ws/frag/testing.md` "Red first, the attack-list fixes" still cites `t-twins` = `f02e0bd9` (a build-error run); update to `24a1d2a1`/`22bfbd9d` |
| `{{PRODUCER_COUNT}}`: from the source, 24 `[Fact]` + theories 28 (`TierNames`) + 6 (`NonAsciiTierNames`) + 7 (custom level) + 2 + 2 = **69 cases in 29 methods** (confirm from the run's count) | `EgressProducerTwinTests.cs` |
| Checklist boxes are pre-ticked `[x]` while their counts are placeholders | body "Checklist" |
| "row 68 on this branch (row 69 once 4.10's row lands first)" | body Summary and `ws/frag/records.md`; renumber after the merge |
| "(this PR)" in SPEC-007 `:129` | kept on purpose (brief step 3); the integrator replaces it with the PR number |
| Body's Testing section still says the floors are 2,136 | stale (§5) |

## 9. Expected merge conflicts (critic §5.1; merge order 4.3 → 4.10 → 4.5, so 4.5 merges last) and counts to restate

| File | Conflict | 4.5 must restate after 4.3 and 4.10 merge |
|---|---|---|
| `ci/cert-gate-assertions.md` row 64 (one line) | three-way (4.3: TSV 149 / sdk.client 8, floors 2,139/149/67, "one decision per authority", +2 classes; 4.10: floors 2,139/150/67; 4.5: current-label clause + floors 2,136/150/67) | TSV **86 / 149 / 48**, `sdk.client` 8; floors **measured once in the container on the merged tree** (critic estimates ~2,144 before `ReadReporter.cs`; measure, do not compute), 149 occurrences, 67 rows; keep the read's-frame clause |
| `ci/cert-gate-assertions.md` new rows after :67 | 4.10 adds `AirGappedHygieneTests`; 4.5 adds the producers row | the producers row becomes **row 69**; fix the body and `ws/frag/records.md` wording |
| `ci/cert-gate-assertions.md` count paragraph | 4.3 says 133/136, 4.10 132/135, 4.5 133/136 (each on its own tree) | **136 `.cs` files / 139 entries** if nothing else lands; recount with `git ls-files`; history "… then 131, then 133, then 135, before it read 136" or as the merge order dictates |
| `EgressGuardConventionTests.cs` floors comment `:191-207` | three-way | re-measure once; name both 4.5 files (`ILabelledTool.cs`, `ReadReporter.cs`) |
| `CHANGELOG.md` Unreleased | 4.3/4.10 insert under `### Changed`; 4.5 under `### Added` after 4.4's and rewords `:162-163` | textual only |
| `docs/EgressInventory.md` | `:3` (if touched), the per-PR section slot before `:16` (4.10's section and 4.5's producers section), Known-limits bullets | keep both sections; G14 sentence stays |
| `docs/specs/SPEC-007-…md` | all three add a bullet at the same anchor after `:127`; 4.5 and 4.10 (if O3 adds a row) both edit the intro `:405-406` | bullet order 4.3, 4.10, 4.5; intro "the PR 4.2 and PR 4.5 answers of 2026-10-06" (+4.10's if any); add 4.4's merge SHA to `(#716)` if 4.3 did not |
| `docs/knowledge-graph.{json,md}` | always | **regenerate after `git add`, never hand-merge** |
| `ci/egress-inventory.tsv` | 4.3 row 38 total 150→149; 4.5 none | the body's "150 occurrences unchanged" becomes 149 (G17) |
| `src/Ashlar.AI.Pipeline`, `src/Ashlar.Abstractions` | no file overlap with 4.3 (4.3: `AwsBedrockChatClientFactory.cs`, `EgressHttp.cs`, `EgressGuardHandler.cs`, `EgressGuard.cs`, csproj IVTs; 4.5: `EgressGuardChatClient.cs`, the builder, `EgressSubject.cs`, `ReadScope.cs`, new files, `PublicAPI.Unshipped.txt`) | re-run F5 and `EgressGuardChatClientTwinTests` after the merge (4.3 adds two Bedrock twins there; 4.5's peer twins are in `EgressProducerTwinTests`, so no test-file overlap) |

## 10. Remaining steps to a PR-ready head (in order; "run" = one `scripts/test-in-container.sh` invocation; one container at a time)

1. **Re-author three mutants** (no run): `mut-4.5/m05.old` to head's text (`result = labelled is null ? await tools.InvokeAsync(…) : await labelled.InvokeLabelledAsync(call, snapshot, read.Reporter, loopCt)…`, `ToolCallingAgent.cs:253-255`) with `m05.new` as is; `m22.new` to the `ReadReporter report` signature (`report.Report(…Public)`); `m04` to a compiling mutant (e.g. `--old '                        read.Complete();' --new ''` plus a `read.Complete();` inserted as the first statement of the `using` block, checked with a local diff before running). Also fix `ws/frag/testing.md`'s `t-twins` SHA (`f02e0bd9` → `24a1d2a1`/`22bfbd9d`).
2. **Fix the stale floors records in a commit** after step 5's measurement: `EgressGuardConventionTests.cs:205-206` and row 64 (name `ILabelledTool.cs` and `ReadReporter.cs`, the measured files count, 150 occurrences); commit with trailers; push. (Can be folded into the post-merge re-measure, step 9, if the integrator prefers one restatement; the body must then not claim 2,136.)
3. **Red-first for G5** (1 run): `bash scripts/test-in-container.sh --repo $SP/c-4.5 --ref c71fcc3c --framework net8.0 --filter 'FullyQualifiedName~EgressProducerTwinTests.A_labelled_tool_cannot_complete|FullyQualifiedName~EgressProducerTwinTests.A_labelled_tool_that_reports_and_then_throws'` → `logs-4.5/red-g5-twin-c71fcc3c.log`; expect red (the old shape hands the `ReadScope`; `t-g5`'s twin may need the pre-G5 `InvokeLabelledAsync(…, ReadScope, …)` signature, which `c71fcc3c` has). This is `chain-b.sh` step 1.
4. **Egress filter + Abstractions at head** (1 run): `bash scripts/test-in-container.sh --repo $SP/c-4.5 --ref 91d3b48c -- "<run-filter-both.sh body>"` but **without piping `dotnet test` through `grep … || true`** (the `a07cbf80` run lost its result that way): expect `Passed!` on net8.0 and net10.0 (last 510 at `d465aa33`; expect roughly 525–530 now), 0 warnings ×3; this settles the AsyncLocal fact at head.
5. **Full verification at head** (2 runs): `bash $SP/mut-4.5/final-verify.sh 91d3b48c` = the cert-gate (expect `Total tests:` ≈ 2817 + the twins added since `4500574a`, `0 skipped`) and the other-projects run (BGA, AIP, Orchestration, `RuntimeStudioBlackBoxSmokeTests|GovernanceFloorPolicyTests|ToolEdgeGovernanceFloorTests`, F6/F8 detailed → `ScannedFiles=` for step 2). Verify by name in the log: `CloudBedrock_DeniedByPolicyGate_…`, `AThrowingCustomGuard_…` ×2, F5, the two flips green.
6. **Repo gates at head** (host, no container): `PATH=$SP/sc-py/bin:$PATH bash scripts/ci/run-repo-gates.sh > $SP/logs-4.5/repo-gates-91d3b48c.log` → expect `all 26 gate(s) passed`.
7. **Mutations at the shipping tree** (each `mutation-check.sh` = a red and a green test run, ~6–15 min each per `batch1.log` timestamps): `bash $SP/mut-4.5/run-mutations.sh 91d3b48c <ids>`. Priority order: m26, m04, m23, m24, m07, m08, m09, m12, m13, m17, m18, m19, m20, m21, m16, m22, m25, m10, m11 (19 new), then re-run m01, m02, m03, m05, m06, m14, m15 at head (7) — **~26 mutation checks ≈ 52 container test runs, 3–5 hours sequential**; append every summary line to `SUMMARY.txt`, then `python3 mut-4.5/gen-table.py` for the body. If the squash (step 10) keeps the tree identical (`git rev-parse HEAD^{tree}` equal before and after), the mutation `ref` need not be re-run; otherwise re-run on the squashed SHA.
8. **Fill the body**: delete "Resume state"; substitute the six fragments and every `{{…}}` with the counts from steps 4–7 (`{{KG_FACTS}}` = 4915; `{{PRODUCER_COUNT}}` from the run); write `{{RED_ATTACK}}` honestly (1 of 15 red; the spelling fix rests on m24/m08/m09); `{{MUT_NOTES}}` for any INVALID; untick or satisfy the checklist boxes. No container run.
9. **After 4.3 and 4.10 merge** (4.5 merges last): `git merge origin/master` in `c-4.5`; resolve §9's conflicts; run the records-4.5 §15 block (TSV 86/149/48, Certification count, row renumber 68→69, SPEC-007 bullet order and intro, 4.4's SHA); `git add -A && python scripts/knowledge-graph/build-knowledge-graph.py`; commit. Then **re-measure and restate** floors/row 64/floors comment once (1 run: F6/F8 detailed) and re-run the cert-gate and the egress filter on the merged head (2 runs), repo gates on the host; re-run F5 and `EgressGuardChatClientTwinTests` (inside the cert-gate).
10. **Squash** to one commit on top of master with the two trailers (message from `f51322ea`'s body plus the attack-list, G5, O1 and integrator-decision paragraphs); `git push --force-with-lease -u origin claude/spec-007-pr4-4.5-producers`; confirm `git rev-parse HEAD^{tree}` equals the verified tree, `git status --porcelain` empty, remote == local. Never push `twins-alone`/`g5-twin-alone`.
11. Hand the integrator: the body, `ws/design-4.5-amendment.md` for the storage branch, the `model-4.5/` files for the workspace branch, and the follow-up (critic §4.10: read-frame exact-count twins in the C# `FlowModel`).

**Estimate:** 6 verification runs before the merge (steps 3, 4, 5×2) + ~52 mutation test runs (step 7) + 3–4 runs after the merge (step 9); repo gates twice on the host. Serial on one container: roughly 5–7 hours of container time.

**Blockers:** none hard. Sequencing only: the final counts (row numbering, TSV 149, floors, Certification 136/139) depend on 4.3 and 4.10 merging first; one container at a time across the lanes (4 CPUs shared); background commands die at 2 hours, so split the mutation batch into ≤ 6 checks per invocation.
