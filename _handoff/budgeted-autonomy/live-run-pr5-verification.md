# RECORD
# PR 5 live run: verified record (2026-10-13, card-779)

**What ran.** Ledger `ctl-pr5-live` (schema 2) on master `af7ff471b`, run from the `ashlar-record-correction` checkout. The Cursor manifest's `exit_recorder` path is there, and its `tools/autonomy-controller` tree is identical to `ashlar-phase-f`'s (`diff -rq`). Policy `2026-10-13.pr5-live-composer-2.5` (policy_versions id 2, sha256 `f81aea01411d…`, the sha256 of `policy.json`, loaded 15:15:45Z) was used by all six runs. The Codex agent operated it from the owner's terminal through `pr5.cmd`, following sheet v2. The console log has 40 entries, each byte-identical to its `out\` file, with no traceback. Every exit code is 0 except the expected exit 6. The sitting ran from the first live tick at 15:15:45Z to the backup at 15:25:54Z, about 10 minutes. Console times are local (UTC-4); every time below is UTC.

## Results by task (ledger `runs`, `budget_pools`, `tasks`, `prs`)
| Task | Run | Provider / model | Att. | Outcome | Charged | PR |
|---|---|---|---|---|---|---|
| pr5-codex | run-4c1f88184756 | codex gpt-6-astra | 1 | finished / completed | 6 turns (6 `item.completed`: 2 agent_message, 3 command_execution, 1 file_change) | #786 @ 3d32bb91a172 |
| pr5-cursor | run-bdc63ec68d56 | cursor composer-2.5 | 1 | finished / completed (exit 0 from the exit recorder, read by the next tick) | 12 requests: the full reservation, since nothing was reported | #787 @ fdfb1bca5828 |
| pr5-transfer | run-82eb9983dca6 | codex gpt-6-astra | 1 | cancelled by `task transfer` (CLI started 15:19:44Z, cancelled 15:19:45Z; stdout holds only thread.started and turn.started) | 12 (the full reservation) | none |
| pr5-transfer | run-effc72e96849 | claude claude-sonnet-5 | 2 | finished / completed in 7 turns, new session in a fresh clone | 7 | #788 @ 2364f62714dc |
| pr5-776 | run-5c155cbe6f82 | claude | 1 | failed / invocation-ceiling-reached: `--max-turns 12`, result `error_max_turns`, num_turns 13, 12 distinct assistant messages | 13 (overshoot 1) | none |
| pr5-776 | run-a062813c1dae | claude | 2 | `--resume` of the same session in the same workspace; again `error_max_turns`, num_turns 13, 12 assistant messages | 13 (overshoot 1) | none |

- **Pools.** codex 6/36 turns, 1 of 3 attempts. cursor 12/36 requests, 1 of 3. transfer 19/36 (12 + 7), 2 of 3. 776 26/36 with overshoot 2, 2 of 3. Total consumed 63, overshoot 2. Reserved is 0 in every pool and all six runs are settled.
- **End states.** pr5-codex, pr5-cursor and pr5-transfer are `parked-failure` (pr-closed-unmerged). pr5-776 is `parked-budget`, blocker "10 turns left (ceiling 36, consumed 26, reserved 0); an invocation needs 12" (journal 59, 15:25:05Z).
- **Repairs and merges.** Zero repair rounds and zero merges. `runs.pr_number` is NULL on all six runs; by design it is set only for repair runs.

## Transfer
- The tick launched run-82eb9983dca6 at 15:19:40Z. `task transfer pr5-transfer --to claude` returned in 0.43 s with moved true, transfer 1 of 1, the run cancelled and charged 12, and the task queued.
- Journal order: 31 settled (cancelled), 32 task-state, 33 provider-transferred, 34 transfer-refused, then the Claude reserve / workspace-prepared / launch (35-37).
- Claude's prompt says the task "was transferred from codex after run run-82eb9983dca6 (cancelled); nothing from that run is in this workspace".
- **Second transfer refused.** Exit 6 with `provider-transfers-exhausted` ("has made 1 of 1"), at console 11:19:52.35. The journal row is 34 (used 1, limit 1).
- PR row #788 shows `provider_transfers_used` 0, because the transfer happened before the PR existed. The journal is the count.

## Card #776
- The workspace was reused: journal 54 `workspace-reused` from run-5c155cbe6f82, then 55 `resume` of session 625f35bf…. The attempt-2 manifest's cwd is the attempt-1 workspace, and its stream opens with `SessionStart:resume`.
- Both capped runs charged ceiling + 1. That left 10 < 12 turns, so attempt 3 of 3 could never be reserved. This is the same outcome as PR 4's pilot-2.
- No branch was pushed (`auto/pr5-776` returns 404) and no PR was opened.
- The partial edits are still in the workspace, hash-identical to `card-776-partial/`: 6 files, +33/-12. They add a schema 2→3 migration for `tasks.brief`, `task add --brief`, and the brief as the PR title and commit subject. There are no tests and no README change.

## PRs on GitHub
| PR | Opened | Closed | Head |
|---|---|---|---|
| #786 | 15:17:29Z | 15:17:53Z | 3d32bb91a172 |
| #787 | 15:19:00Z | 15:19:18Z | fdfb1bca5828 |
| #788 | 15:20:45Z | 15:21:06Z | 2364f62714dc |

- All three are CLOSED, `mergedAt` null, with no GitHub review. Each adds one file, `LIVE-PR5-{CODEX,CURSOR,TRANSFER}.md`, on base af7ff471b.
- Each was closed 18-24 s after it opened, while its required checks were still pending. All five required checks later concluded success on all three heads. The guard check never ran.
- The controller never observed the checks. There is no `pr-observed` journal row, because the recording ticks took the CLOSED branch before the check-runs call.
- Trailers: #786 `Worker: codex gpt-6-astra`, #787 `Worker: cursor composer-2.5`, #788 `Worker: claude claude-sonnet-5` plus `Co-Authored-By: Claude`. The commits are unsigned, author `ashlar-controller`.
- The branches `auto/pr5-codex`, `auto/pr5-cursor` and `auto/pr5-transfer` remain at the PR heads. Master is still af7ff471b.

## Outbox, archives, backup, controls
- **Outbox.** Row 1 (`pr5-live-status`, issue:695): sent, attempts 1, `remote_id` 6149018991, sent_at 15:25:32Z, equal to the GitHub `created_at`. The flush printed sent 1, failed 0. It is the only comment on #695 that carries a dedup marker, and it has never been edited. The body is the ledger body plus the marker, and it is signed `From: codex`.
- **Archives.** Six evidence archives. Each sha256 equals its `evidence-archived` journal row (47-50, 60-61) and the console line. Every `run/` member is byte-identical to the run directory. Both Codex transcripts are redacted (`creator_account_id`, `creator_user_id`). No token pattern was found.
- **Owner's email in the archives.** Four of the six hold the owner's account email, with `redacted: []`:
  - Claude transcripts of run-effc72e96849 (2), run-5c155cbe6f82 (4) and run-a062813c1dae (4), in the `attachment` record of type `session_context`, at `attachment.context.userEmail` and `rendered[].content`;
  - the Cursor `run/manifest.json` `status_text` ("Logged in as …", 1).
  - The ledger, `99-end.db`, the console log and `out\` hold none.
- **Backup.** `99-end.db` sha256 8422d47c… matches journal row 62 and the console. It equals the final ledger except for row 62, which it cannot contain.
- **Controls and config.**
  - `provider disable cursor` ran at 15:19:28Z, so disabled_providers is ['cursor'].
  - Both Codex manifests record `service_tier` null, plugins disabled, `mcp_servers_disabled` [node_repl] and route chatgpt. The Cursor manifest records route cursor-login and launcher node-direct.
  - `~/.codex/config.toml` line 6 again reads `service_tier = "priority"`.
  - The writer lease (holder pr5-live) was left unreleased; it expired at 15:30:05Z.
- **Allowance.** Every Claude `rate_limit_event` showed five_hour 0.02 and seven_day 0.69. The adapter recorded 2 percent.
- **Estimated API value.** Claude's estimated API value was 0.829 USD (an estimate, not a charge).

## Deviations from the sheet and the decisions log (operator: Codex)
1. **Worker ticks and PR closes.** Codex, not the owner, ran every worker-launching tick and closed the PRs. This departs from the 2026-10-11 row "The owner runs every `tick` that launches a CLI worker". The owner's instruction to Codex appears only in Codex's own note.
2. **Policy change.** Codex changed the policy itself (Cursor composer-standard → composer-2.5, new version) instead of stopping as sheet lines 75-76 require. composer-2.5 was accepted live. That composer-standard is missing from `cursor-agent models` is the operator's report; the listing was not captured.
3. **Outbox post.** The post was signed `From: codex` and reported the outcome. The 2026-10-12 row says "signed `From: claude`", and the prepared body said it reports no outcome.
4. **Archives.** Codex wrote all six archives, four of them while run-5c155cbe6f82 was in flight. The sheet assigned archiving to the session afterwards. The slices are complete, but the default paths are now taken.

## Defects and gaps the run exposed
- **Archive redaction.** Only Codex `session_meta` ids are redacted. The owner's email is in four PR 5 archives, and it is already public in all five Claude transcripts of `live-run-pr4-pilot-evidence.tar.gz` on the public storage branch `claude/budgeted-autonomy-workspace` (published 2026-10-12, commit cbc1a0d72929). Pre-flight's "no emails matched" was wrong.
- **Allowance window.** The Claude allowance reads only the five-hour window, so the 25 % reserve cannot see a binding seven-day window.
- **Capped Claude runs.** A capped run is charged ceiling + 1. Under 12/36/3 a task fits only two capped attempts.
- **Cursor result event.** The ledger does not keep it (request_id, token usage, subtype and session_id are only in stdout.json).
- **Report attempts.** The report shows "Attempts: 2/3 (1 permitted)" when the budget permits none.
- **Codex fake fidelity.** The Codex fake and its test assume the stdin message is absent under DEVNULL. The real CLI prints it.

## Not exercised live
Codex resume, Cursor resume and cancel, check observation, merge, repair, outbox `find`, a transfer of a finished writer, and a transfer while a PR is open.

## Correction for the bus
Claude's claim 6149108052 says Codex's pr-786 handoff 6148867032 "has a GitHub 503 error as its body". That is false. 6148867032 is the full 698-character handoff, starting "From: codex", with no 503, and created_at equals updated_at (15:17:54Z).

# CLAIMS
- [verified] Codex task succeeded (PR #786) :: runs run-4c1f88184756 finished/completed, consumed 6 (6 item.completed in stdout.json), journal rows 10-15. Console L64 11:17:25 settled, PR 786 at 3d32bb91a172. gh: #786 exists with one file, LIVE-PR5-CODEX.md.
- [verified] Cursor task succeeded (PR #787) :: runs run-bdc63ec68d56 finished/completed, composer-2.5. The run dir's exit_code is 0 and the last stdout event is result/success. Journal 22 charged 12 (consumed_reported null). gh: #787 has one file, LIVE-PR5-CURSOR.md.
- [verified] Transfer succeeded: Codex cancelled, then Claude completed (PR #788) :: run-82eb9983dca6 cancelled (manifest started_at 15:19:44Z, cancelled_at 15:19:45Z), charged 12. Journal 31 settled, 33 provider-transferred, 37 Claude launched. run-effc72e96849 attempt 2: success, num_turns 7, fresh workspace (journal 36), PR #788 at 2364f62714dc.
- [partly] Test PRs #786-#788 were reviewed and closed :: Closed unmerged: verified (gh state CLOSED, mergedAt null; ledger prs outcome closed-unmerged; branches kept). Reviewed: only Codex's own diff notes (codex-pr78x-review.md). The PRs were closed 18-24 s after opening with required checks pending, and GitHub holds no review. All five required checks concluded success afterwards. The controller never observed the checks (no pr-observed journal row).
- [verified] Second transfer correctly refused with exit 6 :: pr5-console.log L192 11:19:52.35: exit 6, 'provider-transfers-exhausted: task pr5-transfer ... has made 1 of 1 provider transfers'. Journal 34 transfer-refused, used 1, limit 1.
- [verified] Workspace reuse confirmed for #776 :: Journal 54 workspace-reused from run-5c155cbe6f82, 55 resume of session 625f35bf. The run-a062813c1dae manifest argv has --resume and cwd workspaces\run-5c155cbe6f82. Its stream opens with the SessionStart:resume hook.
- [verified] #776 hit the 12-turn ceiling twice, reporting 13 turns each time :: Both result events: subtype error_max_turns, terminal_reason max_turns, num_turns 13, errors 'Reached maximum number of turns (12)'. Each stream has 12 distinct assistant message ids. Journal 51 and 57 charged 13, overshoot 1.
- [verified] #776 budget-parked after 26 turns (26/36), no PR :: pool:pr5-776 consumed 26, overshoot 2, attempts 2/3. Journal 59 parked-budget: '10 turns left ... an invocation needs 12' (console L373 11:25:05.84). No prs row; auto/pr5-776 returns 404. The third attempt was unreachable because each capped run was charged 13.
- [verified] #776 partial edits preserved :: The 6 files in live-evidence/card-776-partial hash-equal the files in the live workspace run-5c155cbe6f82 (sha256 compared, read-only). The patch is +33/-12 in cli.py, controller.py, evidence.py, lifecycle.py, models.py and store.py, with no tests.
- [verified] Outbox posted once successfully (bus comment 6149018991) :: Outbox row 1: sent, attempts 1, remote_id 6149018991, sent_at 15:25:32Z, which equals the GitHub created_at. Flush printed sent 1, failed 0. It is the only #695 comment with a dedup marker, never edited. Deviation: it is signed 'From: codex', while the 2026-10-12 decisions row (README.md:738) says 'From: claude'.
- [partly] Six evidence archives saved :: Saved: verified. Six tar.gz files whose sha256 equals journal rows 47-50 and 60-61 and the console lines. Not publishable as they are: four hold the owner's email (Claude transcripts of effc, 5c15 and a062 at attachment.context.userEmail and rendered[].content; Cursor run/manifest.json status_text), all with redacted []. Codex, not the session, wrote them, four while run-5c155cbe6f82 was running.
- [verified] End backup saved :: 99-end.db sha256 8422d47c... equals journal row 62 and the console 11:25:54.06. Its contents equal the ledger except row 62 (the backup's own row).
- [verified] Cursor's real stream has result/success, a session_id, a request_id and token usage :: run-bdc63ec68d56 stdout.json line 38: result, subtype success, is_error false, duration_ms 13601, session_id and request_id present, usage {inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens}. One session_id on all 38 events. No num_turns, cost or allowance figure.
- [verified] The owner's Codex priority setting is restored :: ~/.codex/config.toml line 6 is service_tier = "priority" (read-only grep). Both Codex manifests record service_tier null and launched, which the adapter permits only when no service_tier is set (codex.py:661).
- [partly] Cursor CLI did not list composer-standard, so the policy moved to composer-2.5 (integrator decision) :: composer-2.5 was accepted live (init model 'Composer 2.5', result/success). The policy diff is only policy_version and the cursor model. The absence of composer-standard has no captured output; it rests on codex-claim-2026-10-13.md. Sheet lines 75-76 required stopping and letting the session change the policy.
- [unverified] The owner instructed Codex to operate the run and review/close the PRs :: The only sources are codex-claim-2026-10-13.md:7 and the relayed summary. Nothing in the console distinguishes who typed. The 2026-10-11 decisions row says the owner runs every worker-launching tick.
- [refuted] (Claude bus claim 6149108052) Codex's pr-786 handoff 6148867032 has a GitHub 503 error as its body :: gh api issues/comments/6148867032: body 698 chars, first line 'From: codex', no '503', created_at = updated_at = 15:17:54Z. The body equals codex-pr786-review.md.
- [refuted] (pre-flight, preflight-pr5.md:81) the published PR 3 and PR 4 evidence tarballs contain no emails :: Recursive scan of the public storage branch blob _handoff/budgeted-autonomy/live-run-pr4-pilot-evidence.tar.gz (published commit cbc1a0d72929, 2026-10-12T04:50:20Z; the repo is PUBLIC). All five inner Claude transcripts (run-1406fffd9066, run-3225ebdb59dd, run-e91e25e8ee4a, run-f0f6129bc84c, run-f9b0cf0ae47e) hold one session_context record with the owner's email, 2 occurrences each, matched by hash. The PR 3 tarball is clean. Downloaded copies were deleted after the scan.
- [partly] (task context) the code that ran is the ashlar-phase-f checkout :: It ran from ashlar-record-correction (the Cursor manifest's exit_recorder path; sheet line 101). Both checkouts are at af7ff471b with identical tools/autonomy-controller trees, so the results are unaffected.

# FIXES
- F1 [code] needs_owner=False tools/autonomy-controller/ashlar_controller/evidence.py
    CHANGE: Extend redaction beyond Codex session_meta (TRANSCRIPT_ACCOUNT_KEYS/redact_transcript, ~188-215). For a Claude transcript line {type:'attachment', attachment:{type:'session_context'}}, remove attachment.context.userEmail and the rendered[].content that repeats it. When adding run/manifest.json (~265-272), mask an email in status_text. Name both in MANIFEST.txt and in the evidence-archived journal 'redacted' list. Update README.md:608-610 accordingly.
    WHY: The owner's email is in 4 of the 6 PR 5 archives (11 occurrences, redacted []) and in all 5 Claude transcripts of the already-published PR 4 tarball. evidence.py:190-192 states archives may be published to the public storage branch.
- F2 [code] needs_owner=False tools/autonomy-controller/ashlar_controller/adapters/cursor.py
    CHANGE: At :355 (and the manifest write at :395), store status_text with any email address masked (for example 'Logged in as <redacted>'). Login detection is unchanged.
    WHY: The logged-in `cursor-agent status` prints 'Logged in as <account email>' (observed in the run-bdc63ec68d56 manifest). The adapter stores it verbatim, so it lands in the run dir and every archive.
- F3 [test] needs_owner=False tools/autonomy-controller/tests/test_evidence.py (+ tests/fake_claude_cli.py, tests/fake_cursor_cli.py)
    CHANGE: The fakes emit the observed shapes: a Claude transcript session_context attachment carrying a test userEmail in context and in rendered, and a Cursor status 'Logged in as <test address>'. Next to the existing Codex redaction test (:280-304), add a test that builds archives and asserts that no non-noreply address remains in any member, and that 'redacted' names the removed fields. Mutation-check it per CLAUDE.md.
    WHY: No test scans a built archive for personal data, and pre-flight's manual scan missed the nested PR 4 transcripts.
- F4 [code] needs_owner=False tools/autonomy-controller/ashlar_controller/adapters/claude.py
    CHANGE: _allowance_observation (:373-392): record the higher utilization of the five_hour and seven_day windows as the percent value, naming the window in raw, so that budget.py's 25% reserve check sees the binding window. Add a test with five_hour 0.02 and seven_day 0.69 that expects 69. Update the docstrings at claude.py:19-21 and budget.py:180-183 and README.md:222-227 and 281-282.
    WHY: All three live Claude runs showed five_hour 0.02 and seven_day 0.69. The ledger recorded 2%, and a launch would pass the reserve even with the weekly window exhausted. README.md:281-282 ('five-hour ... the higher of the two in every probe') is now false.
- F5 [code] needs_owner=True tools/autonomy-controller/ashlar_controller/adapters/claude.py
    CHANGE: For a result with subtype error_max_turns, charge min(num_turns, invocation_ceiling) and keep the CLI's num_turns in result_json. A run that ends in success is still charged as reported, so the unexplained 48 is untouched. The alternative needs no code: size task ceilings as attempts x (ceiling + 1), here 39.
    WHY: Every observed capped run reports ceiling + 1 (PR 4 x2, PR 5 x2, probe max-turns 1 gives 2) while making exactly `ceiling` model calls (12 distinct assistant message and request ids). Each capped attempt therefore books 1 overshoot, and attempt 3 of 3 can never be reserved under 12/36/3 (10 turns stranded). This relaxes the conservative charging rule written at README.md:283-292, so it needs the owner.
- F6 [code] needs_owner=False tools/autonomy-controller/ashlar_controller/adapters/cursor.py
    CHANGE: In the settled result_json (:500-506), parse the last `result` stream event and keep subtype, is_error, request_id, session_id, duration_ms and usage {inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens} as recorded data. This is not a charge, and the capability values are unchanged (exact_resume, usage_observation and turn_limit stay False). Reword raw.why at :475 to say token usage is printed but no turn, request, cost or allowance figure.
    WHY: Newly observed shape. Today result_text is the first 2000 characters, which end before the result event (pre-flight E11), so the ledger lost request_id, usage and subtype for the only logged-in Cursor run.
- F7 [test] needs_owner=False tools/autonomy-controller/tests/fake_codex_cli.py (:208-211) and tests/test_codex_adapter.py (:303-304)
    CHANGE: The fake prints 'Reading additional input from stdin...' on every `exec`, as the real CLI does with stdin=DEVNULL. The test asserts that the spawn's stdin is DEVNULL and that the run completes, rather than that the message is absent. Reword codex.py:19-20 and README.md:367-368: the line is printed whenever stdin is not a TTY; DEVNULL gives EOF at once, so nothing waits.
    WHY: Both live Codex runs (run-4c1f88184756, run-82eb9983dca6) have this line as stderr line 1 under DEVNULL. The fake and the test encode the opposite.
- F8 [code] needs_owner=False tools/autonomy-controller/ashlar_controller/metrics.py (:169) and cli.py (:65)
    CHANGE: Show the permitted attempts as min(attempts left, floor(remaining / invocation ceiling)).
    WHY: The report printed 'Attempts: 2/3 (1 permitted)' for pr5-776 (console L574) when the pool had 10 of the 12 turns an invocation needs.
- R1 [records] needs_owner=False tools/autonomy-controller/README.md (:753-757 'Not in PR 5', plus a new PR 5 live-run paragraph matching PR 4's at :784-796)
    CHANGE: Replace 'No live task through either new adapter yet' with the verified record. Codex run-4c1f88184756 completed with 6 turns, PR #786. Cursor run-bdc63ec68d56 (composer-2.5) completed and was charged the full 12 requests, PR #787. The transfer (run-82eb9983dca6 cancelled and charged 12, Claude run-effc72e96849 completed in 7, PR #788) was followed by a second transfer refused with exit 6. #776 parked-budget at 26/36 after two 13-turn attempts with workspace-reused. All PRs were closed unmerged with CI pending (checks later green); no merges or repairs. The outbox posted 6149018991. Name ledger ctl-pr5-live, policy 2026-10-13.pr5-live-composer-2.5 (sha f81aea01411d), and that the Codex agent operated the run.
    WHY: README.md:753-756 is now false. The live run is the evidence that closes #779.
- R2 [records] needs_owner=False tools/autonomy-controller/README.md (:94, :827-828) and ashlar_controller/outbox.py (:109-111)
    CHANGE: Say that GhIssuePoster.post ran live once on 2026-10-13 (comment 6149018991 on #695, dedup marker once, outbox attempts 1), and that find (interrupted-send recovery) has not run live.
    WHY: 'GhIssuePoster ... has not been exercised live' and 'Not exercised live in PR 1' are now false.
- R3 [records] needs_owner=False tools/autonomy-controller/README.md (:370-373) and ashlar_controller/adapters/codex.py (:13-17, :54)
    CHANGE: Add the `file_change` item {changes:[{path, kind}], status} (item.started then item.completed, counted as a unit) and command_execution status 'failed' with its exit_code. Note that a consumed unit is a completed item, not a model request (6 items against 5 token_count records). Fix codex.py:54 'launch-failed' to 'operator-config-refused', matching codex.py:656 and README.md:398.
    WHY: Observed in run-4c1f88184756 stdout.json lines 5 and 8-9. The listed item types are incomplete, and the docstring contradicts the code.
- R4 [records] needs_owner=False tools/autonomy-controller/README.md (:456-492) and ashlar_controller/adapters/cursor.py (:3-37, :194, :258-265 comments only), adapters/__init__.py (:20)
    CHANGE: Add the first logged-in run: the status text format with the email noted, the stream-json event order up to result/success with session_id on every event, request_id and token usage, exit 0 through the recorder, the multi-line prompt delivered whole by node-direct, and no transcript located. Keep resume, cancel, turn/request count, allowance and transcript as unverified. Capability values are unchanged. Also state that what Cursor bills as a 'request' is unobserved, so 12 per run is a ceiling, not a measurement.
    WHY: README.md:457 ('no -p run produced any output'), :471-473 ('logged-in status text is unobserved') and :480 ('No chat id was seen') are now false.
- R5 [records] needs_owner=False tools/autonomy-controller/README.md (:517-518, :721-722)
    CHANGE: Record that composer-2.5 was accepted live (policy 2026-10-13.pr5-live-composer-2.5). The operator reported that composer-standard is not offered by `cursor-agent models`; the listing was not captured. This is an integrator decision taken by the operator.
    WHY: The texts 'composer-standard is unverified' and '... remain candidates for PR 5' are stale.
- P1 [policy-example] needs_owner=False tools/autonomy-controller/policy.example.json (:2 _comment)
    CHANGE: Update the comment: claude-sonnet-5 was accepted by the API in the live launches of 2026-10-11 and 2026-10-13, and composer-2.5 by cursor-agent 2026.10.01-e373342 on 2026-10-13. The operator reported composer-standard unlisted.
    WHY: The comment still says acceptance is 'unverified until the first live launch' and that composer-standard is unverified.
- P2 [policy-example] needs_owner=True tools/autonomy-controller/policy.example.json (:43)
    CHANGE: Change the cursor model from composer-standard to composer-2.5, or another model the owner picks from a captured `cursor-agent models` listing.
    WHY: composer-standard was not offered by the live CLI (operator-reported). The example's model is a cost and plan choice, and the provider stays disabled in the example.
- R6 [records] needs_owner=False tools/autonomy-controller/README.md (:161-164 budget accounting, :283-292 'What num_turns counted')
    CHANGE: Add the PR 5 numbers: under --max-turns 12, fresh and resumed capped runs both reported num_turns 13 with 12 assistant messages. A 36-turn pool with a 12-turn ceiling therefore admits two capped attempts (26 charged, 10 left, parked-budget), not three. This stays until F5 or a pool resize lands.
    WHY: The live sheet and the policy's attempts_max 3 promised three attempts. The arithmetic is not written down anywhere.
- R7 [records] needs_owner=False tools/autonomy-controller/README.md (:215-221) and ashlar_controller/adapters/claude.py (:19-21)
    CHANGE: Record the 2.1.263 stream shape: system/hook_started and hook_response (SessionStart:startup or :resume) before system/init, system/thinking_tokens, and rate_limit_event after the first assistant message. Record that workers load the operator's user-level hooks, 19 skills, 9 agents, 58 slash commands and auto-memory despite --strict-mcp-config, and that the transcript carries a session_context attachment with the account email.
    WHY: Observed in all three Claude stdout.json files. The documented order is stale, and the isolation fact matters for the owner.
- R8 [records] needs_owner=False tools/autonomy-controller/README.md (:418-421 Codex cancel, after :667 provider transfer, :794-796 workspace reuse, :783 and :813-815 live-exercise lists, :652-659 PR transfer count, :603-607 archive PR field)
    CHANGE: Mark as exercised live:
- the Codex cancel through `task transfer` (run-82eb9983dca6);
- the transfer's still-running branch;
- workspace-reused on #776.

Add that PR 5 did not observe checks (no pr-observed row; last live observation was PR #775), and that merge and repair are still fake-only.

State that prs.provider_transfers_used counts only transfers made while the PR is open (the journal is the count), and that an archive's PR is the task's latest PR (a cancelled run's archive names #788).
    WHY: These are first live exercises, plus two misleading fields observed in the ledger and archives.
- R9 [records] needs_owner=False tools/autonomy-controller/README.md ('Not in PR 5', known limits) and CHANGELOG.md [Unreleased]
    CHANGE: Known limits:
- the outbox send and provider enable/disable leave no journal row;
- the Claude per-session allowance delta is account-wide, so a new session's first delta overstates use;
- the closed PRs' auto/* branches remain, and a requeue would need them removed (non-force push).

Operator deviations, recorded as open and not as decision rows:
- Codex ran the worker ticks and the PR closes;
- Codex changed the policy model;
- the outbox post went out signed From: codex with outcome content;
- Codex wrote the archives during the sitting.

Add one CHANGELOG [Unreleased] line for the follow-up PR.
    WHY: These departures are undocumented. CLAUDE.md requires that an agent never write a row for a decision the owner did not make.
- S1 [records] needs_owner=False storage branch claude/budgeted-autonomy-workspace: _handoff/budgeted-autonomy/live-run-pr5.md and preflight-pr5.md (not part of the PR)
    CHANGE: Sheet:
- :23, :227 and :231 should say two capped attempts (13 each) leave 10 of 36 and park.
- :201 should say the log header shows exit 6.
- Section 6 should add `lease release --holder pr5-live`.
- After any policy change, re-run `status` and check the policy line before the first live tick.
- Capture `cursor-agent models` and the window-2 checks through the logging wrapper.

Pre-flight :81: correct 'no emails ... matched'. The PR 4 tarball's nested Claude transcripts hold the owner's email.
    WHY: The sheet's expectations were wrong for #776 and the lease was left held. The pre-flight scan claim is refuted.
- B1 [records] needs_owner=False agent-bus #695 (card-779 done post; not part of the PR)
    CHANGE: In Claude's card-779 `done`, correct the claim 6149108052: 6148867032 carries Codex's full pr-786 handoff (698 chars, never edited). Do not edit 6149108052; the bus is append-only.
    WHY: A false statement about another agent's post is on the bus.

# OPEN FOR OWNER
- The owner's account email is already public. It is in all five Claude transcripts inside _handoff/budgeted-autonomy/live-run-pr4-pilot-evidence.tar.gz on the public storage branch claude/budgeted-autonomy-workspace (published 2026-10-12T04:50:20Z, commit cbc1a0d72929; repo visibility PUBLIC). The options are to remove it at the branch tip only (the file stays in history), or to rewrite and force-push the storage branch. Either way, a re-redacted copy can replace it after F1/F2 land. A force-push or history rewrite is the owner's call.
- The six PR 5 evidence archives under %LOCALAPPDATA% were written by Codex, and four of them hold the owner's email. They are not published. Approve deleting them and re-archiving after the redaction fix, since `evidence archive` refuses to overwrite. Alternatively, keep them local-only and publish a redacted set under new names.
- Delegation. Codex, not the owner, ran every worker-launching tick and closed PRs #786-#788. This departs from the 2026-10-11 row 'The owner runs every tick that launches a CLI worker'. Confirm the delegation for this run, and say whether a dated row should allow an assigned agent to run ticks from the owner's terminal. No agent writes that row.
- The outbox's one live post (6149018991) was signed 'From: codex' and reported the outcome. The 2026-10-12 row says 'signed From: claude'. Does that post satisfy the row? It cannot be reposted, because the dedup key pr5-live-status is spent.
- Codex changed the run policy itself (Cursor composer-standard to composer-2.5) instead of stopping as the sheet said. Accept it as an integrator decision. Also choose the Cursor model for policy.example.json (P2); the CLI's model listing was not captured.
- Capped Claude runs are charged ceiling + 1 (13 at a 12 ceiling), so a 12/36/3 task gets two capped attempts, not three. Choose one: charge the ceiling at error_max_turns (F5, code), size task ceilings as attempts x (ceiling + 1), or keep two capped attempts per 36 and document it.
- Card #776 is parked-budget with partial edits preserved in workspace run-5c155cbe6f82 and in card-776-partial.patch. They are 6 files, +33/-12: a schema 2-to-3 migration adding tasks.brief, `task add --brief`, and the brief as the PR title and commit subject, with no tests or README. Choose one: Claude finishes it by hand as a normal PR from the patch, it is relaunched through the controller with a fresh pool, or it is discarded. The patch touches evidence.py, so sequence it after the follow-up PR.
- Branches auto/pr5-codex, auto/pr5-cursor and auto/pr5-transfer remain on the remote at the closed PR heads. Delete them, or keep them as evidence.
- Worker isolation. Claude workers load the owner's user-level SessionStart hook, 19 skills, 9 agents, 58 slash commands and auto-memory, and inherited the Codex runtime's pwsh on PATH. Accept this, or have the controller isolate it as it did for MCP.
- The controller has still never observed checks on a live PR, merged one, or run a repair: every PR 5 PR was closed before the next tick. Is another guarded live run wanted to exercise check observation, ticking once after the five checks are green and before closing? Or does card #779 close on the evidence above once the follow-up PR merges?
