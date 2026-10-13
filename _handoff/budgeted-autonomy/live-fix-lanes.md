# Phase F: live-run follow-up lanes (budgeted-autonomy, card #779)

Base: `origin/master` at `af7ff471b31ec703ba102d4f139f0a689b3b6c6b` (#785). The defects come from the verified
record of the PR 5 live run of 2026-10-13. The synthesis is
`C:\Users\icfre\AppData\Local\Temp\claude\C--Users-icfre-Documents-Codex-2026-09-12-f-2-work-ashlar-record-correction\875065c3-5d64-44f0-9643-39122a4af911\scratchpad\live-verify-synthesis.md`
(fix ids F1-F8). The run's real evidence (read-only) is in
`...\scratchpad\live-evidence\` (state\runs\<run>\ stdout.json, manifest.json; state\evidence\*.tar.gz). The
owner decided on 2026-10-13 that the owner's email in archives is redacted (decisions log, PR #789).

Every lane:
- works only in its own worktree, after `git fetch origin && git reset --hard af7ff471b31ec703ba102d4f139f0a689b3b6c6b`;
- touches only the files its section names, plus new tests or fakes it names;
- does NOT edit `tools/autonomy-controller/README.md`, `CHANGELOG.md`, `policy.example.json` or
  `tests/scripts/autonomy-controller.test.sh`; it puts the README text it wants in its final report instead;
- adds a test for every behaviour it changes, runs the whole suite (`bash tests/scripts/autonomy-controller.test.sh`;
  `python` is 3.13 and `python3` is a Store stub), and OBSERVES each new test fail on the unfixed code with its
  message, then pass;
- commits first, then runs at least one hand mutation per behaviour (apply exactly one occurrence, see the named
  test go red, `git checkout --` the file, confirm `git status --porcelain` is empty), and reports the exact old and
  new text;
- keeps Python 3.8 compatible and line endings unchanged;
- never puts a real email address, account id or token in a test or fixture: use `owner@example.invalid`;
- never launches a real CLI worker, never pushes, never touches anything under `%LOCALAPPDATA%`;
- commits on its worktree branch as `lane <X>: <summary>`, and reports the SHA, files, tests, the suite line, the
  observed-red evidence, the mutations and any deviations.

## Lane P: personal data in archives (F1, F3)

Files: `ashlar_controller/evidence.py`, `tests/test_evidence.py`, `tests/fake_claude_cli.py` (only if needed).

- **What was observed.** In the live run, every Claude transcript had one record
  `{"type": "attachment", "attachment": {"type": "session_context", "context": {..., "userEmail": "<addr>"},
  "rendered": [{"content": "...<addr>..."}...]}}` (look at a real one in an archive under `live-evidence\state\evidence`,
  and never print the address). Archives copied it verbatim.
- **Redaction.** Extend `TRANSCRIPT_ACCOUNT_KEYS` / `redact_transcript` so that the archived copy of a transcript has:
  - no `attachment.context.userEmail` (key removed);
  - every email address inside `attachment.rendered[*].content` (or wherever the rendered text repeats it) replaced
    by `<redacted>`.

  Name what was removed in `redacted` (for example `attachment.session_context.userEmail`,
  `attachment.session_context.rendered`).
- **A safety net.** After the structured redaction, mask any remaining email address (a conservative regex,
  excluding `@anthropic.com` noreply addresses and `@example.invalid`) in every TEXT member the archive writes:
  the transcript, and the `run/` files that are text (`manifest.json`, `stdout.json`, `stderr.log`,
  `last_message.txt`). Name each masked member in `redacted`. Binary or non-UTF-8 members are copied unchanged.
  The run directory on disk is never modified.
- **Tests.**
  - An archive of a run whose transcript has the session_context shape (with `owner@example.invalid`) and whose
    `run/manifest.json` holds `"status_text": "Logged in as owner@example.invalid"` contains no
    `owner@example.invalid` in ANY member.
  - `redacted` names both.
  - Non-matching lines are byte-identical.
  - A `@anthropic.com` noreply address is kept.
  - The files on disk are unchanged.

## Lane C: the Cursor adapter (F2, F6)

Files: `ashlar_controller/adapters/cursor.py`, `tests/test_cursor_adapter.py`, `tests/fake_cursor_cli.py`.

- **F2.** `cursor-agent status` printed `Logged in as <account email>`, and the adapter stores `status_text`
  verbatim in the manifest. Mask any email address in the stored `status_text` (`Logged in as <redacted>`). Login
  detection is unchanged.
- **F6, the observed result event.** The live stream's last line was
  `{"type":"result","subtype":"success","is_error":false,"duration_ms":...,"session_id":...,"request_id":...,
  "usage":{"inputTokens":...,"outputTokens":...,"cacheReadTokens":...,"cacheWriteTokens":...}}`. A real one is in
  `live-evidence\state\runs\run-bdc63ec68d56\stdout.json`. Today `result_text` is only the first 2000 characters,
  which end before that event, so the ledger loses it. Parse the LAST `result` event of the stream and keep, in
  the settled result, `result_event: {subtype, is_error, duration_ms, session_id, request_id, usage}`, missing keys
  `None`.
- **Not a charge.** Do NOT change any capability value (exact_resume, usage_observation and turn_limit stay
  False), nor the charge: a request count is still unobserved. Fix the `raw.why` text to say the stream prints
  token usage but no turn, request, cost or allowance figure.
- **Tests.** The fake emits the observed result-event shape. Assert that it is kept, that status_text is masked,
  and that the capability values are unchanged.

## Lane O: other shapes the run exposed (F4, F7, F8)

Files: `ashlar_controller/adapters/claude.py`, `ashlar_controller/budget.py` (only a docstring, if needed),
`ashlar_controller/metrics.py`, `ashlar_controller/cli.py` (only the status-format line for attempts),
`tests/test_claude_adapter.py`, `tests/fake_claude_cli.py`, `tests/fake_codex_cli.py`, `tests/test_codex_adapter.py`,
`tests/test_metrics.py`, and others as needed.

- **F4, the allowance window.**
  - What was observed: every live Claude `rate_limit_event` carried both windows, five_hour 0.02 and seven_day 0.69
    (see the stdout.json files of the three Claude runs in `live-evidence\state\runs`). The adapter records only
    the five-hour window (2 percent), so the 25 percent reserve cannot see a binding seven-day window.
  - The change: `_allowance_observation` records the HIGHER utilization of the windows present as the percent
    value, naming the window it took in `raw`, so `budget.allowance_decision` sees the binding one.
  - Tests: five_hour 0.02 with seven_day 0.69 gives 69; five_hour alone stays as before; a window missing or null
    is ignored. Keep the observation's kind, unit and freshness.
- **F7, the Codex fake.** Both live Codex runs printed `Reading additional input from stdin...` as stderr line 1,
  although stdin was DEVNULL. The fake and its test assume the line is absent. Make the fake print it on every
  `exec`, as the real CLI does. Change the test to assert that the spawn's stdin is DEVNULL and the run completes,
  not that the message is absent.
- **F8, the report's permitted attempts.** The report and `status` printed `Attempts: 2/3 (1 permitted)` for a pool
  with 10 turns left when an invocation needs 12. Show the permitted attempts as
  `min(attempts left, floor(remaining / invocation ceiling))`, where `remaining` is ceiling - consumed - reserved.
  Tests for the case `10 left, 12 needed, 1 attempt left: 0 permitted`.
