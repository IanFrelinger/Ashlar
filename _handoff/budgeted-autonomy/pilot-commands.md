# PR 4 pilot: the owner's command sheet (Command Prompt, prepared 2026-10-11)

Everything is registered: pilot ledger `ctl-pilot-pr4` with three queued tasks (pilot-0 throwaway, pilot-1 card #762,
pilot-2 card #776), guarded policy (ceilings 12/36/3, one repair round, the controller never merges), start backup
taken, claims posted on the bus. Before launching, close the other Claude Code sessions in the desktop app (or accept
one `transient` retry per launch).

## Once per Command Prompt window

```bat
cd C:\Users\icfre\Documents\Codex\2026-09-12\f-2\work\ashlar-record-correction
set ASHLAR_CONTROLLER_STATE=%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\ashlar-controller\pilot\state\controller.db
set ASHLAR_CONTROLLER_POLICY=%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\ashlar-controller\pilot\policy.json
set C=python tools\autonomy-controller\controller.py --holder pilot-pr4
%C% tick --dry-run
```

Expect `would-launch: dry-run task pilot-0`, reservation 12.

## The loop (same command, every minute or two)

```bat
%C% --json tick --adapter claude --gh cli
```

What each answer means:
- `launched`: a worker started (pilot-0 first, then pilot-1, then pilot-2 after the previous PR is merged or closed).
- `settled` with `lifecycle.action: pr-open`: the controller committed, pushed and opened the PR; note `pr_number`.
- `blocked: pr-open-blocks-dispatch`: the checks are running; each tick writes a `pr-observed` row.
- `launched` with `reason: repair`: a required check went red; one repair round is running (then `settled` → `repair-pushed`).
- `pr: parked-repair-rounds`: red again after the round; the task parks. Fix or close by hand, then tick.
- `settled` with `stop_reason: transient`: the OAuth contention; wait a minute and tick again (the task requeues itself).
- `idle: empty-queue`: all three tasks are done or parked.

## Your review and merge, per PR

When `%C% report` shows the PR's five real checks green (the guard stays pending), review the diff on GitHub, then:

```bat
gh pr merge <n> --squash --match-head-commit <head sha from report>
```

The next tick records `merged` with the merge SHA, the task is `done`, and the following task starts from that SHA.
If you reject a PR, `gh pr close <n> --delete-branch` and tick: `pr-closed-unmerged`, task parked, nothing landed.

## Controls to exercise once each (they take effect at the next tick)

```bat
%C% pause --reason "pilot: pause check"
%C% tick --adapter claude --gh cli
%C% resume
%C% provider disable claude
%C% tick --adapter claude --gh cli
%C% provider enable claude
```

## At the end

```bat
%C% report > pilot-report.md
%C% --json status > pilot-status.json
%C% --json metrics > pilot-metrics.json
%C% run list
%C% evidence archive <each run id>
%C% evidence list
%C% backup %LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\ashlar-controller\pilot\backups\99-end.db
```

Send me `pilot-report.md` (or paste it) and the merge SHAs; I post the `done` on `card-762` and `card-776` and record
the pilot in the phase E handoff. Anything the real `gh` or CLI printed that looked unexpected is a finding.
