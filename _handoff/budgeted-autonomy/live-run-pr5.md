# PR 5 live run: one task through Codex, one through Cursor, one transfer, and card #776 (v2, 2026-10-13)

This is v2 of the sheet. v1 (2026-10-12) was never run. A pre-flight against master found defects in the
controller and in v1 itself, and **#785 (`af7ff471b`)** fixed the defects in the code. v2 assumes master at or after #785's
merge. The decisions behind it are rows in the controller README's decisions log (#784): Cursor is logged in,
everything runs in one sitting, the outbox's `gh` poster is exercised once, and #776 is the fourth task.

**Where.** Run it from a Command Prompt opened from the Start menu (Win+R, `cmd`), not from a Claude Code
session: the session's classifier refuses worker launches.

**Use two windows.**
- **Window 1** runs only `pr5` commands. While a run is going, never press Ctrl+C in it and never close it: the
  worker is attached to that console and would die with it.
- **Window 2** is for `gh`, reading files, and anything else.

**It is guarded.** The policy lists a sixth required check, `owner-approval (never runs: live-run guard)`, which
never runs, so the controller never merges. You review each PR and then merge or close it. The next tick records
which.

**Cost:**
- **Codex:** two runs, `pr5-codex` and `pr5-transfer`'s first run.
- **Cursor:** one run, counted in requests.
- **Claude:** the transfer target, plus card #776. #776 may take up to 3 attempts of 12 turns each. Its task
  budget is 36 turns.
- **Repairs:** if a required check on a PR's head ends red (failure, cancelled, timed out), the next tick
  launches **one repair run** on that PR's provider. That takes 12 more from the task's budget and one attempt.
  Close the PR first if you do not want that run.
- **CI:** one run per push.

Paid inference is off in the policy. Nothing here buys anything or enables overage.

Everything each `pr5` command prints also goes to `%L%\pr5-console.log`, with its time and exit code. The
session reads the ledger, the run folders and that log directly afterwards, so you do not need to copy output
anywhere.

## 0. Prerequisites (window 2)

**Codex.** `codex` is not on PATH. Use the desktop app's binary, which is the one the controller uses:

```bat
"%LOCALAPPDATA%\OpenAI\Codex\bin\2e5e00daee91c61d\codex.exe" --version
"%LOCALAPPDATA%\OpenAI\Codex\bin\2e5e00daee91c61d\codex.exe" login status
```

The first must print `codex-cli 0.162.0-alpha.17.2`. The second must print `Logged in using ChatGPT` (it prints
on stderr). If the app has updated, `dir /b "%LOCALAPPDATA%\OpenAI\Codex\bin"` shows the new folder.

**Codex's speed.** Your Codex config sets the priority tier ("Fast: 2x speed, increased usage"), and the policy
asks for standard. The controller now refuses a Codex launch while that line is set.

```bat
findstr /n service_tier "%USERPROFILE%\.codex\config.toml"
```

If it prints `service_tier = "priority"`, put a `#` in front of that line for the run (`notepad
"%USERPROFILE%\.codex\config.toml"`), and take it out again after section 6. **Do this before any Codex tick.**
The Codex app may rewrite the file, so run the `findstr` again just before sections 2 and 4. If a Codex tick is
refused anyway (`blocked` / `operator-config-refused`), nothing was charged, but one of the task's 3 attempts is
used and the task now waits. Fix the line, then put the task back in the queue, for example
`%P% task set-state pr5-codex queued`, and tick again.

Your Codex plugins (computer-use, browser, chrome and the others) and your `node_repl` MCP server no longer
reach the controller's Codex runs: the controller now turns them off for each run. You do not need to change
anything in the app for that.

**Cursor.** `cursor-agent` is not on PATH either. Log in once, then check the status:

```bat
set CA=%APPDATA%\Cursor\User\globalStorage\anysphere.cursor-agent-worker\agent-cli\.local\share\cursor-agent\versions\2026.10.01-e373342\cursor-agent.cmd
"%CA%" login
"%CA%" status
"%CA%" models
```

`status` must no longer say `Not logged in`. `models` must list `composer-standard`, the model the policy
names. If it does not, stop and tell the session, which changes the policy to a listed model first. If Cursor has updated, `dir /b` the `versions` folder and use the
newest, which is the one the controller picks.

**Claude** is the transfer target and runs #776:

```bat
claude auth status
```

It must show `"loggedIn": true` and `"authMethod": "claude.ai"`. That alone does not prove the session still
works, because an expired login still says logged in. So send one real request just before section 4:
`claude -p "Reply with the single word ok."`. It must answer `ok`.

**No API keys.** These must all be unset: `OPENAI_API_KEY`, `CURSOR_API_KEY`, `CURSOR_AUTH_TOKEN`,
`ANTHROPIC_API_KEY`. A key means the paid `api-key` route, and the launch is refused.

```bat
set | findstr /i "OPENAI_API_KEY CURSOR_API_KEY CURSOR_AUTH_TOKEN ANTHROPIC_API_KEY"
```

This must print nothing.

## 1. Set up window 1

```bat
cd C:\Users\icfre\Documents\Codex\2026-09-12\f-2\work\ashlar-record-correction
git checkout master
git pull --ff-only
set L=%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\ashlar-controller\pr5
set P="%L%\pr5.cmd"
%P% status
%P% tick --dry-run --adapter codex --gh cli
```

`git log -1` must show #785 or later.

`status` must show:
- controller `ctl-pr5-live`;
- cursor in `disabled providers`;
- four tasks queued: `pr5-codex` (codex), `pr5-cursor` (cursor), `pr5-transfer` (codex) and `pr5-776` (claude).

The dry run must say `would-launch: dry-run task pr5-codex` with reservation 12.0. If anything differs, stop
and tell the session; do not fix the ledger by hand.

When one step says "tick until", run the same line again every minute or so. Window 1 keeps its history, so the
up arrow repeats the line.

## 2. Codex: `pr5-codex`

```bat
%P% --json tick --adapter codex --gh cli
```

1. The first tick prints `"action": "launched"` (reservation 12.0).
2. **Tick until** it prints `"action": "settled"` with `settled[0].lifecycle.action` set to `pr-open`. Until then
   it prints `blocked` / `writer-busy`. The controller has then committed, pushed and opened the PR, and the PR
   number is in `settled[0].lifecycle.pr_number`.
3. **Stop ticking now.** Every tick from here on prints `blocked` / `pr-open-blocks-dispatch` until you merge or
   close. The five real checks can go green, but the guard check stays pending by design.

Review the PR in window 2. Take the full 40-character head; the 12 characters in `status` are refused:

```bat
gh pr view <n> --json headRefOid -q .headRefOid
gh pr merge <n> --squash --match-head-commit <the 40-character head>
```

Merge it, or close it with `gh pr close <n>`. Do not use `--delete-branch`. Then run **one** tick in window 1:
it must print `"action": "pr"` with reason `merged` (or `pr-closed-unmerged`). That tick only records the outcome.
The next tick would launch the next task, which is section 3's job.

If `gh pr merge` says the branch is not up to date (something else merged to master meanwhile), run
`gh pr update-branch <n>` and wait for the checks on the new head. Then take the new 40-character head and merge
with that. The controller observes the new head by itself. Do not merge other PRs during the sitting.

The PR's commit ends with a `Worker: codex gpt-6-astra` line, and the PR body names the worker. Only a Claude
run credits Claude.

## 3. Cursor: `pr5-cursor`

```bat
%P% provider enable cursor
%P% --json tick --adapter cursor --gh cli
```

Then the same as section 2: `launched`; **tick until** `settled`; stop; review; merge or close; then one tick
(`--adapter cursor --gh cli`) to record it. Then take Cursor out of dispatch again:

```bat
%P% provider disable cursor
```

This is the first logged-in Cursor run anyone has made. Whatever it settles as, it is evidence. A settle other
than `completed` (for example `no-final-result`, `auth-missing`, `task-failed` or `waiting-rate-limit`) is a
finding about Cursor's real behaviour: write it down, disable Cursor as above, and go on to section 4. Do not
`run settle` anything by hand.

## 4. One transfer: `pr5-transfer`, from Codex to Claude

First send the Claude check from section 0 (`claude -p ...`) in window 2, and run section 0's `findstr` once
more. Then, in window 1, launch the task on Codex and transfer it straight away. Run this as **one line**, so
the transfer catches the Codex run while it is still going and Claude really takes the task over:

```bat
call %P% --json tick --adapter codex --gh cli & call %P% --json task transfer pr5-transfer --to claude --reason "live transfer check" --adapter codex --gh cli
```

The tick's part must print `launched` for `pr5-transfer`. If it does not (for example `blocked`), the transfer
after it is refused or moves a task that never ran. Stop and tell the session. The transfer may take a few
seconds, because it waits for a stopped run to exit. The transfer must print `"transfer_no": 1`, `"of": 1`
and `"moved": true`. What else it prints depends on whether the Codex run had finished:

- **Still running:** `stopped: [{"how": "cancelled", ...}]`, and `state` is `queued`. The Codex run is charged
  its full reservation.
- **Already finished:** `stopped: [{"how": "settled", "stop_reason": "completed", ...}]`. The controller has
  committed and pushed the Codex run's work and opened a PR, so `state` is `pr-open` and `pr_number` is set.

Both are correct. The session reads which one happened from the ledger.

Then prove the bound: a second transfer must be refused.

```bat
%P% task transfer pr5-transfer --to codex --gh cli
```

It must end with `[pr5: exit 6]` and `provider-transfers-exhausted`. Run it now, before the task's PR merges: on a
`done` task the answer is exit 4 instead.

Then let Claude carry the task:

```bat
%P% --json tick --adapter claude --gh cli
```

- **Still-running branch:** `launched` on `claude`, in a fresh clone. Its prompt names the Codex run it
  replaced. **Tick until** `settled` with `pr-open`, then review, merge or close, and record it with one tick, as
  in section 2.
- **Already-finished branch:** the PR is Codex's work. The tick prints `blocked` / `pr-open-blocks-dispatch`.
  Review, merge or close, and record it with one tick. If a required check goes red first, the repair runs on
  `claude`.

## 5. Card #776 on Claude: `pr5-776`

```bat
%P% --json tick --adapter claude --gh cli
```

1. The first tick prints `launched` for `pr5-776`. **Tick until** it settles.
2. If it settles with stop reason `invocation-ceiling-reached`, the run stopped at its 12-turn ceiling and the task is
   queued again. Tick once more: the next attempt resumes the same session **in the same workspace**. The
   journal shows `workspace-reused`; that is the #780 fix being exercised. Then tick until it settles again.
   There are at most 3 attempts and 36 turns.
3. When it settles with `pr-open`, review the PR (it adds `task add --brief`), merge or close it, and record it
   with one tick.

If the task ends `parked-budget` (36 turns used without finishing), that is a result too. Note it and go on.

## 6. At the end

First make sure the last PR outcome is recorded: `%P% pr list` must show no PR `open`. If one is, merge or close
it, then run one `--gh cli` tick. Then, in window 1:

```bat
%P% outbox enqueue --key pr5-live-status --kind status --target issue:695 --slug card-779 --body-file "%L%\outbox-status.md"
%P% outbox flush --poster gh --repo IanFrelinger/Ashlar
%P% outbox list
```

`flush` must print `"sent": 1` (and `"failed": 0`), and `outbox list` must show nothing pending. That is one comment on the
agent-bus issue (#695), sent through your `gh` login: the outbox's first live post. Run `flush` once only; if it
fails, leave it and tell the session.

```bat
%P% --json run list
%P% report
%P% --json metrics
%P% backup "%L%\backups\99-end.db"
```

You need not archive anything: the session archives every run from the ledger afterwards, once each run's outcome
is final. Take the `#` out of `%USERPROFILE%\.codex\config.toml` again if you put one in. Then tell the session
"done". It reads `%L%`, including `pr5-console.log`, and records everything.

**If a tick prints `propose-failed`** (a completed run whose commit, push or PR failed) **or `blocked` /
`propose-failed-blocks-dispatch`,** stop and tell the session. Nothing else launches until it is resolved, and
the branch, or even the PR, may exist on GitHub already.

**If something looks wrong** (a Python traceback, a command that exits non-zero when the sheet does not expect
it, or a tick that does something this sheet does not describe), stop ticking and tell the session. Leave the
window as it is.
