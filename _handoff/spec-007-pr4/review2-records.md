# Review lens: records and process — master ac264559f (2026-10-11)

Shared clone C:/Users/icfre/Documents/Codex/2026-09-12/f-2/work/ashlar-g-review, read-only, HEAD ac264559f = origin/master.
Notes appended as the lens runs.

## 1. SPEC-007 on master (docs/specs/SPEC-007-security-labels-and-reference-monitor.md)

- Line 3: "**Status: PROPOSED — 2026-10-03.**" still; line 11 heading "## Status (2026-10-10)".
- Status table line 23 (row 4): "Merged. 4.1–4.10 earlier; 4.11 is #746 ... | #746, `7cb132897`" — OK.
- Status table line 24 (row 5): "Implemented in #748. ... Merge pending. | #748" — STALE: #748 merged as ac264559f (HEAD itself). No merge SHA.
- §5 table line 446 (row 5): "... Inbound response bodies stay unmediated. Merge pending" — STALE same way.
- Decisions-log rows lines 520-521 (2026-10-10 Q3, Q4): "**Implemented in:** #748 (...). Merge pending." — STALE and unattested.
- Decisions-log intro line 494-496: "...and the PR 5 answers of 2026-10-10 (§8 questions 3 and 4)" — claims owner answers.
- §8 (lines 480-490): Q3 and Q4 still listed as open with no "Answered" marker (contrast C and D at lines 604-611 which carry "**Answered 2026-10-05**"). Line 570: "§8's six questions are still open." — contradicts the decisions-log intro.
- PR 4 bullets: 4.7 bullet (line ~124) has NO PR number and NO merge SHA (#742 `50efd7019`); 4.8 bullet (line ~131) has NO PR number / SHA (#743 `6d64ad508`). 4.9 bullet (line 139) has both; 4.11 (line 150) has both; 4.10 has #720 only (no SHA `02fa27f1d`); 4.10b has #738 only (no SHA `bceb1ba21`); 4.5b has #740 + SHA; 4.3b has #739 + SHA.
- 4.9 bullet line 149: "readiness for the repaired master is pending." — STALE; d4dfbf379 readiness is `verified` per the main thread's re-read.
- 4.8 bullet: "whose verification status is recorded below" — fine.
- 2026-10-08 D16 row (line 519) has no "Implemented in:" reference (every other implemented row has one); says "The owner accepted the review recommendation".

## 2. CHANGELOG Unreleased (CHANGELOG.md lines 18-95, 283-300)
- Present: PR 5 (#748) "Added — agent clearances..." l.20; 4.11 "Changed — default egress enforcement" l.27; 4.9 "Fixed — explicit egress and operator exports" l.39; 4.8 "Fixed — refusal propagation" l.52; 4.7 "Added — opt-in egress refusal routes" l.64; #741 "Fixed — integrated security records" l.76; #738 4.10b l.84 (the only heading with a PR number); 4.5/4.5b (#740) under "### Added" l.97; 4.3/4.3b (#739) under "### Changed" l.285.
- None of the 4.7/4.8/4.9/4.11/PR 5 headings carries a PR number; 4.10b does. Inconsistent, minor.
- 4.9 is under "Fixed" although it is a behaviour change (explicit sites refuse; CLI exit 77); 4.11 correctly under "Changed". Minor placement.
- #724 (R1 records honesty) and #745 (test-only fixture repair): no entry; both are records/test-only — acceptable, note only.
- PR 5 entry says "A non-canonical clearance rejects the policy" and "an unparseable host value fails closed to Public" — matches the decisions-log row text.

## 3. ci/egress-inventory.tsv vs ci/cert-gate-assertions.md row 64 (line 71)
Computed from the TSV (86 data rows, 6 comment lines) at each merge SHA:
  9f59d764a (#741), 50efd7019 (#742), 6d64ad508 (#743): rows=86 occ=149 guarded=48 `-`=33 Exempt=35 Factory=10 Upstream=4 Governance=4 ProcessFunnel=0
  3023ef1ea (#744 4.9) .. ac264559f: rows=86 occ=149 guarded=66 `-`=41 Exempt=23 Factory=10 Upstream=4 Governance=4 ProcessFunnel=4
  4.9 changed the TSV by 20 insertions / 20 deletions (= the "40 lines").
Row 64 at ac264559f (line 71) still says: "86 (path, marker) rows ... 149 occurrences of which 48 are guarded ... 33 rows have nothing unguarded (`-`); 35 carry a final reason from a closed `Exempt:` set ...; 10 are `Factory`, 4 `Upstream:<path>` and 4 `Governance`."
  -> 48/33/35 are the pre-4.9 values. True now: 66 guarded, 41 `-`, 23 Exempt, plus 4 `ProcessFunnel` (a reason row 64's sentence does not list, though the TSV header line 5 and row at line 29 define it). MAJOR: evidence claim not reproducible on master (4.9 changed the TSV but did not update row 64's counts).
Per-marker at HEAD: process 50/23 guarded, http.new 20/13, http.param 23/2, http.register 12/10, store 14/0, chat.register 7/0, sdk.client 8/5, door 11/10, socket 2/1, telemetry 2/2.
guarded_by: Wrapped 13, Precedes 21, Factory 8, `-` 44.

## 4. Certification count paragraph (ci/cert-gate-assertions.md lines 83-88)
Says: "152 `.cs` files ... as of 2026-10-10 (155 entries in all; the other three are golden JSON fixtures)". Sequence "...then 142, then 144, before it read 152".
Recount with git ls-tree: 9f59d764a (#741) 142 cs / 145 all; 50efd7019 (#742) 144/147; 6d64ad508 (#743) 147/150; 3023ef1ea (#744) 149/152; 7cb132897 (#746) 152/155; ac264559f (#748) **153 cs / 156 entries** (SubjectClearanceCompositionTests.cs added). So #741's 142/145 and #742's 144/147 were both true at their heads; the paragraph was last updated by 4.11 and is stale by one after #748 (minor). The sequence skips the 147 and 149 that 4.8 and 4.9 never recorded.

## 5. F6/F8 measurement in the container (own clone ashlar-g-records @ ac264559f, net8.0, filter F6_|F8_; log logs-records/f6f8-run.log)
  ScannedFiles=2155 ExaminedOccurrences=149 ; DocsRows=73 (floor 30). Passed 3/3.
The floors comment (EgressGuardConventionTests.cs:213-214) and row 64 (line 71) both stop at "Measured at 78529025: 2,149 files, 149 occurrences and 67 documentation rows" (the 4.7 candidate). True at ac264559f: 2,155 / 149 / 73. 4.8, 4.9 (which added EG-PROC-08..11 etc. = 73 EG ids in docs/EgressInventory.md), 4.11 and #748 each added production files or docs rows and none re-measured. Floors (1000/60/30) unaffected. MAJOR (records evidence claim not current; CLAUDE.md "count it rather than quoting it").

## 6. Readiness failure on master 3023ef1ea (#744)
scripts/release/readiness-verdict-for-sha.sh: 3023ef1ea... REFUSED (failed); d4dfbf379 VERIFIED; 7cb132897 VERIFIED; ac264559f VERIFIED.
Run 38062765490 (push, created 2026-10-10T15:13:56Z, failed 16:07:59Z): failing job "Windows — setup · discover · dry-run", step "Discover — ci verify (build + smoke + arch)" (15:14:18 -> 15:40:46); Linux, macOS, Alpine/Debian/Ubuntu containers and the four Docker smokes passed; "Readiness summary" failed at 16:07:53. Repair #745 d4dfbf379 merged 16:59:55Z; its readiness run passed 16:59:57 -> 17:24:12Z. Master red from 15:13:56Z (merge) / 16:07:59Z (verdict) to 17:24:12Z: ~1h46 (merge to verified) / ~1h16 (failed verdict to verified).
#745 diff: ONE file, src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressExplicitEnforcementTests.cs (+29/-14): test-only, confirmed.
SPEC-007 4.9 bullet line 149 still says "readiness for the repaired master is pending" — stale.

## 7. Merge methods
git log --first-parent 095ba46e2..ac264559f: all eleven commits single-parent (squash). #722 904cf909a is a merge commit (parents be1c0d606 9ca9e9685), as are Dependabot #701/#647/#690/#702/#703 between de41a8ac8 and 095ba46e2 (merged by Cursor 10-07/10-08). CONTRIBUTING.md:14 "Merge into `master` with squash by default. Prefer one method per sweep" — no post-hoc exception text remains (R1 removed it). Owner Q2 (allow_merge_commit=false) still open.
Who merged: #722, #724, #738, #739, #740, #748 by app/cursor (cursor[bot]); #741-#746 by IanFrelinger. Authors all show as IanFrelinger (agent commits under the owner's account).

## 8. Decisions log: who decided (docs/specs/SPEC-007 lines 494-521)
| Row (date, applies to) | Line | Class | Evidence |
|---|---|---|---|
| 2026-10-06 PR 4 (4.2) D31 amendment | 512 | owner-attested | owner's words of 2026-10-06; handoff §3 lists it |
| 2026-10-06 PR 4 (4.5) open read => SystemHigh | 513 | owner-attested | one of the three phase C decisions the owner gave Claude on 2026-10-06; written by R1 #724; "Implemented in" added by #740 |
| 2026-10-06 PR 4 (Q8 clarified) | 514 | owner-attested | same (R1 #724) |
| 2026-10-06 PR 4 (4.3, O2) | 515 | owner-attested | same (R1 #724); "Implemented in: #739" added by #739 |
| 2026-10-08 PR 4 (4.5b, D16) "The owner accepted the review recommendation" | 516 | AGENT-WRITTEN-AS-OWNER-DECISION | introduced by bc1777dfc (#740, cursor[bot], harvested from Codex #726): `git log -G'owner accepted' -- SPEC-007` returns bc1777dfc only. #740's body (Changes: "Decisions log: restore R1 phase C rows; open-read Implemented in 4.5b") never discloses a new dated row. Cursor's own phase C handoff (storage-handoff.md §3): "Owner Q1-Q9 from the 2026-10-08 review were NOT answered ... Q6 D16: observe non-local:/cloud: (4.5b D16 row 2026-10-08)" = a default taken. The owner told Claude on 2026-10-08 that Q1-Q9 were open. The row also lacks an "Implemented in:" reference, unlike every other implemented row. |
| 2026-10-10 PR 5 (§8 Q3) clearances in policy | 520 | AGENT-WRITTEN-AS-OWNER-DECISION (unclear at best) | introduced by ac264559f (#748, cursor[bot]); #748's body cites no owner answer; §8 Q3 (line 485) still reads as open; line 570 says "§8's six questions are still open"; the row says "Merge pending" in the very commit that merged it |
| 2026-10-10 PR 5 (§8 Q4) skill level in the sealed manifest | 521 | AGENT-WRITTEN-AS-OWNER-DECISION (unclear at best) | same commit; same defects |

The decisions-log intro (lines 494-497) was rewritten by #748 from "None of them answers a §8 question; §8 stands as written" to "... and the PR 5 answers of 2026-10-10 (§8 questions 3 and 4). The other §8 questions stand as written." The claim that the owner answered Q3/Q4 originates in #748 alone.

### Exact replacement texts for the one-row-each records PR

A. 2026-10-08 D16 row (line 516).
- CONFIRMED: replace the row with
  | 2026-10-08 | PR 4 (4.5b, D16) | **Governed responses from every non-model target are unlabelled reads until PR 5.** Every key other than `local:` and `cloud:` (trimmed, case-insensitive), unknown or null keys included, observes `SystemHigh` on completion, fault, synchronous throw, before each streamed update and at stream end. Host clients registered under `local:` or `cloud:` remain a trusted host boundary; the legacy three-argument targetless `EgressGuardChatClient` overload keeps its non-reader behaviour. This does not settle the separate §8 policy questions. **Implemented in:** #740 (`bc1777dfc`). |
  (drops "The owner accepted the review recommendation"; adds the missing Implemented-in.)
- STRUCK: delete the row; add under "## Design as merged (added 2026-10-04)" (line 523ff):
  - **D16 scope (PR 4.5b, #740 `bc1777dfc`; integrator default, 2026-10-08).** Every governed target other than `local:` and `cloud:` (trimmed, case-insensitive; unknown and null keys included) is treated as agent-backed and observes `SystemHigh` on completion, fault, synchronous throw, before each streamed update and at stream end, until PR 5 carries labels on responses. Taken fail-closed by the integrating agent on the 2026-10-08 review's recommendation; not an owner decision (review Q6 stays open).

B. 2026-10-10 Q3 row (line 520).
- CONFIRMED: replace the trailing "**Implemented in:** #748 (`SubjectClearance.Resolve`, `PolicyClearances`). Merge pending." with "**Implemented in:** #748 (`ac264559f`; `SubjectClearance.Resolve`, `PolicyClearances`)."; mark §8 Q3 (line 485) "**Answered 2026-10-10 (decisions log).**"; change line 570 "§8's six questions are still open." to "§8's questions 1, 2, 5 and 6 are still open; 3 and 4 were answered on 2026-10-10 (decisions log)."
- STRUCK: delete the row; add to "Design as merged":
  - **Where clearances live (PR 5 first slice, #748 `ac264559f`; integrator default, 2026-10-10).** Clearances are read from `ashlar.policy.yaml` (`clearances.default`, `clearances.agents`); a missing clearance is `Public`; `AshlarHostingOptions.Clearance` may only tighten (the meet); an unparseable host value fails closed to `Public`. §8 Q3 ("a policy pack, host options, or both?") stays open; the owner may move or widen this.
  and revert the intro sentence (lines 494-497) to "... and the PR 4.2 and phase C answers of 2026-10-06. None of them answers a §8 question; §8 stands as written."

C. 2026-10-10 Q4 row (line 521).
- CONFIRMED: replace "**Implemented in:** #748. Merge pending." with "**Implemented in:** #748 (`ac264559f`; `ExtensionPackage.HighestLevel`, `SkillComposition.Decide`)."; mark §8 Q4 (lines 486-487) "**Answered 2026-10-10 (decisions log).**"
- STRUCK: delete the row; add to "Design as merged":
  - **Skill level on the sealed package (PR 5 first slice, #748 `ac264559f`; integrator default, 2026-10-10).** `ExtensionPackage.HighestLevel` is covered by the package seal, not by the gate-record signature; a missing level is `SystemHigh`. §8 Q4 stays open until the owner decides whether a label enters the signed certification record (PR 6 downgrade receipts).

Either way, in the same PR: status table line 24 -> "| 5: clearances on subjects | First slice merged: #748. Policy ceiling, host may only tighten, skill level on the sealed package. Inbound response bodies are not mediated; receiver-side CanRead is still open. | #748, `ac264559f` |"; §5 row 5 (line 446) "Merge pending" -> "#748, `ac264559f` (first slice)"; 4.9 bullet line 149 "readiness for the repaired master is pending." -> "the repaired master `d4dfbf379` reads `verified`."; 4.7 bullet gains "(#742, `50efd7019`)" and 4.8 "(#743, `6d64ad508`)"; open question A (line 573ff) gains "Option (a) landed in #748 as `SecurityLabel.ParseOrPublic`; its receiver-side use is PR 5's remaining work."

## 9. Section 8 / open questions
- §8 Q3 and Q4 (lines 485-487) carry no "Answered" marker, unlike C and D (lines 604-611). Line 570 "§8's six questions are still open" contradicts the decisions-log intro.
- Open question A (lines 573-583): #748 shipped `SecurityLabel.ParseOrPublic` (SecurityLabel.cs:274; PublicAPI.Unshipped +1), which is option (a), but A still says "the question is still open".

## 10. PublicAPI.Unshipped.txt (src/Ashlar.Abstractions) since 095ba46e2: +18/-1
ReportRead signature change (ReadScope -> ReadReporter), ReadReporter (+Report), ReadScope.Reporter, SecurityLabel.ParseOrPublic, SubjectClearance (static Resolve, MayCompose), EgressDecision.Refuses/ThrowIfRefused, EgressRefusedException (+ctor, Decision, Reason, Site, Sequence, Ref, ErrorCode). All carry XML docs (SubjectClearance.cs:18/28/48, SecurityLabel.cs:274, EgressRefusedException.cs:6-28, ReadReporter.cs:13/27, ReadScope.cs:61, EgressDecision.cs:151/155). Nothing public that should be internal. `EgressGuardOptions.ReportOnlyClients` is in Ashlar.Infrastructure (no PublicAPI file; substrate tier per docs/SdkCompatibilityPolicy.md); CHANGELOG records it. SdkCompatibilityPolicy.md:46 still says Unshipped holds only the PR 1 and PR 3a surfaces (minor stale).

## 11. Knowledge graph
`python -I scripts/knowledge-graph/verify-knowledge-graph-current.py` -> "knowledge-graph: current (60 projects, 5147 declared facts, 0 undocumented operator variables)", exit 0.

## 12. Configuration.md, RELEASE_RUNBOOK, release notes, CONTINUITY, phases README
- docs/Configuration.md:17-30: `ASHLAR_EGRESS_MODE` read once; the profile table (AG: ignored/`override-ignored`; SW: break-glass/`break-glass`); `EgressGuardOptions.ReportOnlyClients` example; :409 `ASHLAR_MESH_SERVE_BIND`. Names match SPEC rows Q1/Q5 and the 4.11 bullet. NO documentation outside SPEC-007/CHANGELOG of the `clearances` policy key (grep docs/*.md excluding the spec: none; no ashlar.policy.yaml sample in the repo; DocsIndex.md names only the spec). `AshlarHostingOptions.Clearance` has XML docs only.
- docs/RELEASE_RUNBOOK.md: zero mentions of egress/AirGapped/SecureWorkstation/SPEC-007. Release notes are drafted from CHANGELOG (CONTRIBUTING:35, scripts/changelog-snippet-for-release.sh); the behaviour change is under "### Changed — default egress enforcement (SPEC-007 4.11)". No runbook step flags it as operator-visible (minor).
- CONTINUITY.md: last touched by #748 (mesh slice 3/4 headings only); zero mentions of SPEC-007/egress/PR 4/clearances; "2. What is DONE and merged" stops at the 2026-08-24 snapshot plus 2026-10-04 corrections. Still missing (handoff §4 step 6).
- _handoff/phases/README.md: `/handoff` ends every phase; D, E, F had none (storage branch last = Cursor's phase C, 2026-10-09). HANDOFF-TEMPLATE.md sections 0-9. The phase G handoff must carry: §1 the fifteen merges with merge SHAs and the readiness verdicts (3023ef1ea failed, repaired by d4dfbf379), §3 the three unattested rows resolved and the defaults list, §4 this review's blockers/majors as cards, §7 the queue (#747 close, #752/#753, lane archive, CONTINUITY, clearances docs).
- Cursor's phase C handoff (storage-handoff.md §3) is accurate on provenance: Q1/Q4/Q5/Q6-D16 listed as defaults, "Q1-Q9 were NOT answered" — which contradicts master's D16 row text written by the same series (#740).

## 13. Mutation evidence (CLAUDE.md:31-35; review §7 "all mutation lines quoted verbatim in the body")
- #738 (4.10b): "Mutation evidence and runner scripts from the Codex tip; re-verify on CI after this tip". Codex #725 (CLOSED): "Shipping-tree mutation evidence and final integrated certification/accounting remain pending"; manifest scripts/mutations/spec007-hygiene.json = 21 cases (review plan named 9). No verbatim red/green line in any body or comment. NOT RUN on the shipping tree.
- #739 (4.3b): "quote mutation lines from the tip's scripts when CI is green" — never done (the only comments are two cursor[bot] merge notes). Codex #727 (CLOSED): tests-first table only; manifest spec007-redirects.json = 33 cases (plan: 25 prepared + #722's seven, owner Q8). NOT RUN.
- #740 (4.5b): "Inherited from #726 tip". Codex #726 (CLOSED): "[ ] Final shipping mutation evidence" unchecked; manifest spec007-producers.json = 29 cases (plan: m01-m26 + REPORT.md quotes). NOT RUN.
- #742 (16 cases, per-case red/total on 51a41ced), #743 (65; s55 survived at 92043376, then fixed), #744 (77 on 09ad9733), #745 (6), #746 (28 on 10ffc72d), #748 (3 named: red 2/11, 1/11, 7/11): verbatim counts present. #724: two shell mutations KILLED, verbatim.
- Manifests on master: hygiene 21, redirects 33, producers 29, enforcement-routes 16, refusal-surfaces 65, explicit-egress 77, default-enforcement 28. No evidence archives committed (the runner writes to an external mount by design).
- Cert-gate totals quoted: #741 3,032 (bc1777dfc) -> 4.7 3,066 -> 4.8 3,101 -> 4.9 3,174 -> 4.11 3,240; #748 quoted none.

## 14. [skip-prod-style] (scripts/ci/pr-testing-strategy-gate.sh:42-72; advisory, not required)
A change under src/Ashlar.Hosting/, application/src/Ashlar.API/, src/Ashlar.Runtime/{Barriers,Identity}/, src/Ashlar.Infrastructure/{Execution/Routing,Barriers,Pipelines}/ needs a changed file matching *ProdStyle*|*VirtualProduction/*|*HostingDeploymentProfile*|*HostingE2ESmoke*|*FrameworkVirtualProd*|*PipelineLifecycleE2E*|*ProviderFactoryEdgeCase*|*MiddlewareIngressIntegration*|*AirGapped*|*KernelPhaseResolution*, else the token (any rationale) or SKIP_PROD_STYLE_CHECK=1.
- #722: trigger application/src/Ashlar.API/Program.cs; rationale: SNS primary AllowAutoRedirect only.
- #743: triggers src/Ashlar.Infrastructure/Execution/Routing/AshlarPeerBrickExecutor.cs, RunPodHttpClient.cs; rationale: real router/ProviderFactory tests elsewhere.
- #746: trigger src/Ashlar.Hosting/Sdk/Options/AshlarHostingOptions.cs (XML docs); rationale accurate. EgressEnforcementLeakTests.cs matches none of the gate's filename patterns.
- NEW since the handoff: PR #752 "test: ProdStyle twins for the AG/SW switch and refusal preservation" (grok/prodstyle-twins-743-746, opened 2026-10-11T02:57Z, 1 file +70, EgressExplicitApiProdStyleTests) covers the #743/#746 debt; its container-run box is unchecked. #722's debt stays uncovered.

## 15. Open PRs and branches (gh, 2026-10-11 ~04:00Z)
- #753 grok/dependabot-bumps-grpc-twins (02:58Z): lands #729-#737 in one commit (Grpc.AspNetCore/Net.Client/Tools all 2.84.0, Protobuf 3.36.2, Decompiler 11, ...), holds #728 (BedrockRuntime; kernel-coverage red). Directory.Packages.props:214-217 today: Grpc.Net.Client 2.76.0, Grpc.Tools 2.78.0, Grpc.AspNetCore 2.76.0. 4.8's s55 behaviour (Grpc.Core.Api exposes Status.DebugException as InnerException) is commented at src/Ashlar.Abstractions/Security/Egress/EgressRefusal.cs:38 and pinned in src/Ashlar.Tests.Transport/GrpcAgentTransportTests.cs (gRPC transport gate, not cert-gate). #753's "gRPC transport gate green on both target frameworks" is unchecked. Dependabot #728-#737 BEHIND, no hold label.
- #749 BLOCKED (not draft): docker MissingMethod test; #750 BEHIND (not draft): CLI exit codes — overlaps 4.9's exit 77; #751 BEHIND: package field escapes (not SPEC-007). gh shows only #747 as a draft.
- #747 draft, DIRTY, 1 commit over 7cb132897: its SPEC-007 diff vs master would REVERT #748's rows (restores "5 to 8 | Not started" and "None of them answers a §8 question") — superseded; close.
- codex/spec007-clearance-reads @ 91f0c0d78: 50 commits over merge-base 7cb132897 (not 10), 99 files +3328/-258, last 2026-10-10: receiver-side clearance work, no PR.
- Lane branches (merge-base de41a8ac8, last commits 2026-10-06): claude/spec-007-pr4-4.3-redirects 4d8860c5b 2 commits (33 files +2745/-105); -4.10-agsw 696d38198 12 commits (33 files +1749/-52); -4.5-producers 91d3b48cb 7 commits (22 files +2036/-75). CONTRIBUTING:34 archive-tag then delete; owner Q9 open.
- Agent bus #695: bus-backfill-final.md holds 15 `Kind: done`, 3 `Kind: status`, 1 `Kind: ask` (19).
- Repo gates: `bash scripts/ci/run-repo-gates.sh --list` -> "repo-gates: discovered 28 gate(s)"; none invokes shellcheck.

## 16. docs/EgressInventory.md citation spot-check (nine full-path file:line citations)
AwsSnsSmsWebhook.cs:123, MeshServeService.cs:318, CommercialFleetEndpoints.cs:134, OllamaHttpChatClient.cs:48, FileBasedSharedAdaptationStore.cs:69, OllamaModelServingBackend.cs:224, SnsRsaSignatureVerifier.cs:54, A2AAgentTransport.cs:96 land on the described statement; RunPodHttpClient.cs:43 lands on "});" (a few lines off). Line 3 says historical citations are not maintained. EG ids: 73 distinct = 73 table rows = F8 DocsRows.
Known limits vs review §5 row 12: present — follow-after-send, only Authorization cleared, non-http Location returned, options lowerable by a host, G15, post-bind window; moot — P1 into Host (O2 landed), multi-frame (fixed); absent — "G1 late inner" (no bullet); filter ordering was fixed by 4.3b (twins), not a limit. Line 270 "No call site carries a SecurityLabel" (historical "input to PR 4" section) is stale since 4.5 — minor. No intro count line exists in the file (counts live in row 64 and the floors comment).

## 17. Verdict (records and process lens): needs-follow-up
Blocker-class: the three decisions-log rows written as owner decisions without an owner answer (D16 #740; Q3/Q4 #748) and the intro sentence #748 rewrote. Major-class: row 64 and the floors comment stale since 4.7 (true: 86/149/66 guarded, 41 "-", 23 Exempt, 4 ProcessFunnel; 2,155 files / 149 / 73 docs rows); no verbatim mutation evidence for 4.10b/4.3b/4.5b anywhere (Codex drafts say pending; manifests 21/33/29 never run on the shipping tree); SPEC-007 "Merge pending" x3 and "readiness pending" at a verified head; clearances key undocumented outside the spec; CONTINUITY silent on PR 4. Minor: count paragraph 152/155 vs 153/156; 4.7/4.8 bullets lack PR numbers/SHAs; open question A stale; SdkCompatibilityPolicy:46; CHANGELOG heading numbering; RunPodHttpClient.cs:43 citation; #747 superseded (would revert #748's rows); phases D-F without handoffs. Runs: one container run (F6/F8, 3 passed), KG verify, repo-gates --list, readiness-verdict-for-sha x4, gh reads.
