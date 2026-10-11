# Agent-task cards from the 2026-10-11 review of master `ac264559f`

Each card below is one GitHub issue, label `agent-task`, per `_handoff/bus/PROTOCOL.md` (claim on the bus with `About: card-<n>` before working it). Bodies cite `REVIEW-2026-10-11.md` on the storage branch `claude/spec-007-pr4-workspace`, `_handoff/spec-007-pr4/`. Create with `gh issue create --label agent-task --title "<title>" --body-file <file>`.

---

## Card 1 — Testing-strategy gate: ignore comment-only diffs under trigger paths

**Done when:** `scripts/ci/pr-testing-strategy-gate.sh` no longer demands a ProdStyle proof for a diff whose only change under a trigger path is inside comments or XML doc comments, with a test script that shows a comment-only diff passing and a one-line code change still failing; `[skip-prod-style]` stays available for real exceptions.

**Why:** #746 (`7cb132897`) added `[skip-prod-style]` after the gate went red on three lines of XML comment in `AshlarHostingOptions.cs`; the rationale was true, but the token now bypasses the gate for the whole PR. Review §5 #746 minor; §7 "[skip-prod-style]".

**Records touched:** `docs/architecture/TestingStrategyPivot-v1.md` if it describes the gate; `ci/cert-gate-assertions.md` only if a blocking test is added.

**Relies on:** no decisions-log row.

---

## Card 2 — EventId 7308 for the host opt-out startup Warning

**Done when:** `EgressGuardOptions.cs:69` (`EgressHostClientOptOut`) uses its own EventId (7308) instead of reusing 7307, SPEC-007 §4.8/4.11 event lists and `docs/Configuration.md` name it, and a twin asserts the id.

**Why:** 4.8 uses 7307 for degrade warnings at 13 sites (SPEC-007:227); 4.11 reused it for the opt-out line, so an operator filter on 7307 mixes two meanings. Review §5 #746 minor.

**Relies on:** no row.

---

## Card 3 — `WorkflowExecutor` `FailurePolicy.Continue` and a refused brick

**Done when:** the owner's answer (review §10 new (c)) is implemented: either `FailurePolicy.Continue` continues past a refused brick and records the refusal in the cluster result, or the current abort is recorded in the CHANGELOG 4.8 entry and `docs/EgressInventory.md` as the intended behaviour; twin either way.

**Why:** #743 made a refusal abort a cluster step even under `FailurePolicy != Abort` (`WorkflowExecutor.cs:384-391`), fail-closed but unrecorded. Review §5 #743 minor. **Blocked on the owner's answer.**

---

## Card 4 — One `denied/egress_refused` audit record per refusal through the unkeyed router

**Done when:** a governed MEAI refusal through the unkeyed router produces one audit record, not two (`EgressGuardChatClient.cs:214-221` plus `AuditingChatClient.cs:45-51,185-195`), with a twin counting records and a mutation that restores the duplicate.

**Why:** review §5 #742 minor (m5).

---

## Card 5 — Pre-switch wording and the `is_reported` theory name (if not folded into R3a)

**Done when:** `EgressEnforcement.cs:50` ("Not produced yet"), `EgressDecision.cs:123-124`, `EgressRedirectTwinTests.cs:132` and the theory name `An_explicit_profile_is_reported_and_does_not_change_the_decision` reflect that AirGapped and SecureWorkstation enforce by default; `ci/cert-gate-assertions.md` and every `scripts/mutations/*.json` `filter`/`expected_failures` that name the test are updated in the same change.

**Why:** review §5 #746 minor; Grok `drift-746`. R3a (opened from this phase) attempts it; close this card if R3a lands it.

---

## Card 6 — `RunPodHttpClient.cs:43` and other off-by-a-few citations in `docs/EgressInventory.md`

**Done when:** every `file:line` citation in the inventory resolves to the construct it names at master, checked by a script run once (not a gate), with the list of corrected citations in the PR body.

**Why:** review §6 row 25 pattern; records lens. Low risk, tedious.

---

## Card 7 — `DESIGN-4-final.md:605` and §5 C12 on the storage branch

**Done when:** on `claude/spec-007-pr4-workspace`, `_handoff/spec-007-pr4/DESIGN-4-final.md` §5 says Scenario B's reason is `SystemHighData`, the multi-frame sentence near line 605 matches master (a cloud multi-frame resolve on AirGapped is refused), and the C12 adaptation (the runner ends an ordinary tool exception with `error`; the test checks the next send in the same frame) is recorded, after the owner accepts it (review §10 new (d)).

**Why:** the design is the reference the next review will read. Handoff §7; review §5 #738 minor, #746 minor.

---

## Card 8 — Mutation evidence pass: all seven SPEC-007 manifests on one master SHA

**Done when:** after the manifest-hygiene PR merges, `scripts/run-spec007-mutation-batch.py` has run every entry of all seven `scripts/mutations/spec007-*.json` serially on one master SHA, every line reads `KILLED`, the lines are on the record (a `scripts/mutations/EVIDENCE.md` with the SHA, or the manifests' `note` fields) and posted on the bus as `status` `About: mutations-spec007`.

**Why:** closes 2026-10-08 B7 and review M7 (bodies of #738/#739/#740/#744/#746 quote no per-case line; Grok's post-merge runs cover hygiene, producers and redirects only). Several hours of serial container time; phase H.

---

## Card 9 — PR 4.9b: exit 77 centrally, Initiator convention, `-v` mounts, `docker exec` per the owner

**Done when:** `EgressRefusedException` maps to exit 77 for every CLI command (a System.CommandLine handler in `BuildRootCommand`, redacted message kept) with a process-level twin on one EG-PROC-09 command; a Roslyn convention fact lists the production `Initiator =` writers (`PkgCommand.cs`, `NativeBundle.cs`, `SneakernetTransport.cs`) and fails on any other production file, with its cert-gate row; `HasNetworkOffRun` rejects `-v`/`--mount` values naming the daemon socket or pipe or an absolute root (twin rows, the probe inverted); `docker exec` into an Ashlar-started `--network=none` session is either recognised as Host through a verifiable chain or recorded as a limit, per the owner's answer (review §10 new (a), (b)). Mutations in `spec007-explicit-egress.json`, lines quoted.

**Why:** review M4, M5, M6 and write-down F6/F7. **Partly blocked on the owner** (exit-77 scope; `docker exec`).

---

## Card 10 — Inbound hosts under Q6: gRPC server host and Fleet.Host

**Done when:** per the owner's Q-C answer: either `AshlarInboundListenerPolicy.CollectEndpoints` and `LoopbackListenerVerifier` are wired into `Ashlar.Transport.Grpc.Server.Host` and `Ashlar.Commercial.Fleet.Host` so a non-loopback bind on AirGapped or SecureWorkstation fails boot, with ProdStyle twins and mutations, or the Known-limit lines R3a adds stand and SPEC-007's Q6 row is amended to name the two hosts as outside the rule.

**Why:** review M8 (design §2.7 vs the Q6 row's literal text); the release notes will say "AirGapped and SecureWorkstation now refuse by default" and an operator reads that as every inbound surface. **Blocked on the owner.**

---

## Card 11 — Dependabot #728 (AWSSDK.BedrockRuntime 4.0.101.9)

**Done when:** #728's `kernel-coverage` check is green (or its failure is explained as unrelated) and the bump is merged, or the PR is closed with the reason.

**Why:** #753 lands the other nine bumps and holds #728 because its kernel-coverage check was red/cancelled. Review §7.
