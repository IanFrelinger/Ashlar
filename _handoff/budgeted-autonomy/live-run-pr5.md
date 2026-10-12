# PR 5 live run: one small task through each new adapter, and one transfer (prepared 2026-10-12, not executed)

Run this from a Command Prompt that is **not** a Claude Code session; the session's classifier refuses worker
launches. Use the checkout at or after the PR 5 merge.

The ledger, the policy and the tasks are separate from the pilot's (`pr5\` beside `pilot\`). The policy is
guarded: a sixth required check never runs, so the controller never merges. You review each PR, then merge it or
close it, and the next tick records which.

Cost is one Codex run, at most one Cursor run, and one Claude run (the transfer target), plus one CI run per push.
Nothing here buys anything or enables overage: paid inference is off in the policy.

## 0. Prerequisites

```bat
codex login status
```
- **Codex:** this must print `Logged in using ChatGPT`. The controller resolves the desktop app's binary under
  `%LOCALAPPDATA%\OpenAI\Codex\bin\<hash>\codex.exe`, which has a `codex-code-mode-host.exe` beside it. It
  never resolves the bundled `%USERPROFILE%\.codex\.sandbox-bin\codex.exe`, which cannot run tools.
- **Cursor (optional):** run `cursor-agent login` once. The CLI is the Cursor IDE's embedded one, under
  `%APPDATA%\Cursor\User\globalStorage\anysphere.cursor-agent-worker\agent-cli\...`; `cursor-agent status`
  must no longer say `Not logged in`. Skip section 3 if you do not want Cursor logged in.
- **Environment:** no `OPENAI_API_KEY`, `CURSOR_API_KEY`, `CURSOR_AUTH_TOKEN` or `ANTHROPIC_API_KEY`. A key
  means the `api-key` route, which is paid spend, and the launch is refused.

## 1. Once per Command Prompt window

```bat
cd C:\Users\icfre\Documents\Codex\2026-09-12\f-2\work\ashlar-record-correction
git checkout master
git pull --ff-only
set L=%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\ashlar-controller\pr5
set ASHLAR_CONTROLLER_STATE=%L%\state\controller.db
set ASHLAR_CONTROLLER_POLICY=%L%\policy.json
set C=python tools\autonomy-controller\controller.py --holder pr5-live
%C% status
%C% tick --dry-run
```

`status` must show controller `ctl-pr5-live` with tasks `pr5-codex`, `pr5-cursor` and `pr5-transfer` queued.
The dry run must say `would-launch` for `pr5-codex`, reservation 12. The session prepared the ledger, the
policy and the tasks after the merge; if any of that is missing, tell it rather than creating them by hand.

## 2. Codex: one task through `--adapter codex`

```bat
%C% --json tick --adapter codex --gh cli
```
Repeat it every minute or two:
- `launched`: one `codex exec` started; the reservation is 12.
- `settled` with `lifecycle.action: pr-open`: the controller committed, pushed and opened the PR.
- `blocked: pr-open-blocks-dispatch`: the checks are running.

Then look at the run:

```bat
%C% report
%C% run list
```

Review the PR, then merge it pinned to its head, or close it:

```bat
gh pr merge <n> --squash --match-head-commit <head>
gh pr close <n> --delete-branch
```

The next tick records `merged` or `pr-closed-unmerged`.

**Record:** the `launched` and `settled` reports; `consumed` (the completed non-error items); the run's
`stdout.json` (`%C% evidence archive <run id>`); and the PR's outcome.

## 3. Cursor: one task through `--adapter cursor` (only after `cursor-agent login`)

```bat
%C% provider enable cursor
%C% --json tick --adapter cursor --gh cli
```

The Cursor provider counts `requests`, so this task has its own `requests` pool. **The first logged-in run is
the evidence nobody has yet.** Archive it whatever happens:

```bat
%C% evidence archive <run id>
```

The archive holds the run's `stdout.json`; its `result_text` and `event_types` are the real stream shape. If
the run settles as anything other than `completed`, that is a finding, not a failure of the pilot.

## 4. One transfer: `pr5-transfer` from Codex to Claude

`pr5-transfer` is queued on `codex`.

1. Launch it once with `%C% --json tick --adapter codex --gh cli`.
2. While it runs (`run list` shows it `running`), transfer it:

```bat
%C% --json task transfer pr5-transfer --to claude --reason "live transfer check" --adapter codex
%C% --json tick --adapter claude --adapter codex --gh cli
```

Expect `transfer_no 1 of 1`. The rest depends on whether the Codex run was still going:
- **Still running:** the transfer reports `stopped: [{how: cancelled}]` and `state queued`. The next tick then
  launches on `claude` in a fresh clone; its prompt names the Codex run it replaced.
- **Already finished** (a Codex run of this size takes under a minute): the transfer reports
  `stopped: [{how: settled, stop_reason: completed}]`. The run is proposed as usual, so a PR opens, and
  `state` is `pr-open`. A red head is then repaired through `claude`.

Both are correct. Record which one happened. A second transfer must be refused with exit 6
(`provider-transfers-exhausted`):

```bat
%C% task transfer pr5-transfer --to codex
```

## 5. At the end

```bat
%C% report > pr5-report.md
%C% --json status > pr5-status.json
%C% --json metrics > pr5-metrics.json
%C% run list
%C% evidence archive <each run id>
%C% backup %L%\backups\99-end.db
```

Send me `pr5-report.md` and the PR numbers. Anything the real `codex`, `cursor-agent` or `gh` printed that
looked unexpected is a finding for the next phase.
