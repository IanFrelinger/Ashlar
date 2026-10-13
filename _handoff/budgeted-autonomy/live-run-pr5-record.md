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

Source: the verification workflow `wf_1bf8c9b6-1b7` (five readers over the ledger, the console log, the archives, GitHub and the declarations, then a synthesis), run read-only on a snapshot of the ledger folder. The bus correction it names was posted as 6149566946.

Since then (2026-10-13): the PR 4 pilot tarball on the storage branch was replaced by a redacted copy and the branch history rewritten (bus 6151763815); #791 redacts archives in code. The six PR 5 archives stay local.
