# PR 5 live run: pre-flight record (budgeted-autonomy phase F, 2026-10-13)

Before the owner ran `live-run-pr5.md`, a workflow checked v1 of that sheet against master `dd38c007c`. Nothing it
did launched a model worker; it worked only on copies of the ledger and with fake CLIs. The workflow (run
`wf_efab9420-6f4`, 124 agents) had:
- five lanes: commands and outputs, sequencing through the ledger, the transfer path across processes, the
  host environment, and evidence capture;
- two verifiers per finding: one reproduced it, one tried to refute it from the code;
- a completeness critic over all of it.

The verdicts are in `preflight-pr5-findings.txt` (every finding with its evidence, reproduction, verifier votes
and proposed fix) and in `preflight-pr5-critic.txt`.

## Counts

59 lane findings and 12 critic findings. Every lane finding but two was confirmed by both verifiers, almost all
with a reproduction:
- ENV-5 was refuted by both;
- ENV-9 split, and was later weakened by the critic (C10).

Many are duplicates across lanes (critic C11). By severity: 6 blocks-run, 13 wrong-outcome, 9 wastes-spend,
18 confusing, 13 minor.

## What happened to each kind

**Fixed in code: #785.** The fixes were built by three lanes in worktrees and adversarially reviewed (lane
contract `pf-fix-lanes.md`), then integrated and fixed further after the reviews.

| Finding(s) | Defect |
|---|---|
| D1 (and D1-ext, E4) | `task transfer` without the PR lifecycle settled a finished run `done`, unproposed, and re-queued it |
| SEQ-1, T3, E1 | the Cursor exit code was lost across `tick` processes; every live run would settle `no-final-result` |
| ENV-3 | the operator's Codex `service_tier = "priority"` was applied to every run |
| C8 | the operator's Codex plugins (computer-use, browser, chrome) and MCP servers reached unattended runs; `mcp_servers={}` removed nothing |
| T4 | `run cancel` without `--adapter` left the worker running |
| T5 | a reused pid made a finished run poll as running, and a transfer killed an unrelated tree |
| SEQ-5, T8 | `run settle` and `readiness evaluate` could finish a task with nothing proposed |
| E8 | a failed push or PR stranded the task as `running`, with a traceback |
| SEQ-6 | the dry run used other rules than the tick |
| REPORT-CP1252, ENV-4, SEQ-10, E10 | `report > file` crashed on a non-cp1252 character |
| C1 | the oldest check run decided a re-run check |
| ENV-7 | every provider's commit credited Claude Fable 5.1 |
| C7 | archived Codex transcripts carried the ChatGPT account ids |
| C9 | a worker's own commit was stranded |
| T10 | the fake Cursor CLI raced a test on Windows, about 12% of runs |
| E2 | the transfer's `stopped[].how` was not journalled |

**Fixed in the sheet: `live-run-pr5.md` v2.**
- PATH-CODEX, PATH-CURSOR, ENV-1, ENV-2: full paths.
- ORDER-S2, SEQ-3: stop ticking after `settled`, and one tick only to record a merge.
- ORDER-S4, SEQ-2, T7, E6, E7: the Cursor PR is merged or closed and recorded, and Cursor disabled again, before section 4.
- SEQ-4, ENV-6, T2, RUNLIST-STALE, E3, C6: the transfer runs on the same line as its launch, and checks that it launched.
- HEAD-SHA: the 40-character head from `gh pr view`.
- EXIT6-VISIBILITY, SEQ-12, E9, E12, ENV-8, T9: every command goes through `pr5.cmd`, which logs its output, time and exit code.
- ARCHIVE-ONCE, SEQ-7, E5: the session archives after the run.
- CWD-OUTPUTS: outputs go to the ledger folder.
- RED-CHECK-REPAIR, SEQ-8: the cost line.
- CONSOLE-CTRL-C, T6: two windows; never press Ctrl+C or close window 1 during a run.
- ENV-10: a Claude request before section 4.
- C2, C3, C4, C5: the sheet is redone on the fixed code; #776 is section 5; the outbox post is section 6; Cursor's `models` lists the policy's model.
- C12: `gh pr update-branch` when a PR is BEHIND.

**Not fixed, recorded:**
- **SEQ-9:** the first Claude launch is not checked against the 25 percent reserve.
- **SEQ-11:** after a transfer, `status` shows the old funding pool until the new run is reserved.
- **E11:** Cursor's `result_text` is the head of the stream, readable only from `runs.result_json` and the archive.
- **C10 / ENV-9:** the Codex sandbox under the Claude package's LocalCache has never been exercised. The sheet's
  stop-on-anything-unexpected rule covers it.
- **T5 residual:** a run that died without a terminal event and whose pid was then reused still polls running.
- **The line reader:** best effort on unusual TOML; tomllib, on Python 3.11 and later, is what runs on the owner's host.
- **notify:** the effect of `-c notify=[]` is not observed.

## Evidence that was checked and found fine

The critic and every lane listed what they checked and found correct (`checked_ok` in the findings file). Among
those items:
- the PR 5 tasks' files cannot trip the docs or KG gates;
- branch protection needs no approving review;
- the outbox CLI path works on a ledger copy with the fake poster.

The session also scanned the published PR 3 and PR 4 evidence tarballs: no emails, account ids or tokens matched.
