Task card (`agent-task`, see `_handoff/bus/PROTOCOL.md`). Bus slug: `card-<this number>`. Workstream: **budgeted-autonomy**, phase E. Claimed by Claude Code on #695.

## Context

PR 5 of the budgeted autonomous development workflow (roadmap in #755; PR 1 merged as #756 `1d8bc7448`; PR 2 as #769 `f236bd4ad`; PR 3 as #771 `d2c11b347`; PR 4 as #773 `18b5da30f` with the follow-up #774 `3d14ce0b7`). This card is the Codex and Cursor adapters and the bounded provider transfer: a task can move from one provider's CLI to another without losing its PR, its attempts or its budget pool, and only after the previous writer is stopped and reconciled.

The pilot of PR 4 ran on 2026-10-11 from the owner's terminal (PR #777 merged `f0fd9d113`, PR #778 merged `ea71db40c` for card #762; card #776 parked on budget after two capped attempts). Its findings (a resumed session is given a fresh clone, so the capped attempt's edits are discarded; a resumed invocation reported 48 turns under a 12-turn ceiling and was charged in full; the report prints a merged PR's state and outcome as "merged merged") land in a small fix PR before this card's code.

Base master at phase start: `ea71db40c7924ca9e70d6d6221cb77a76ca3b5d8`. Handoff: `claude/budgeted-autonomy-workspace`, `_handoff/budgeted-autonomy/handoff.md` (phase D, with its addendum).

## Deliverables

- `ashlar_controller/adapters/codex.py`: the Codex CLI (`codex exec --json`) headless, declaring in `AdapterCapabilities` only what a probe of the installed CLI observed (session id and resume, cancel, usage source, structured output, turn limit or its absence, funding route), with the observed shapes recorded in the README the way the Claude adapter's are.
- `ashlar_controller/adapters/cursor.py`: the Cursor agent CLI (`cursor-agent -p --output-format stream-json`) headless, on the same terms.
- The bounded provider transfer in `controller.py` / `lifecycle.py`: `task transfer <id> --to <provider>` (and the same path when a provider is disabled or parked for a reason the policy lets another provider pick up) moves the task to the new provider's adapter; the task row, its PR row, its attempts and its budget pool are unchanged; `prs.provider_transfers_used` is counted before the launch and `limits.provider_transfers_per_pr` bounds it; the previous writer is stopped (cancel) and reconciled (settled) before the new one launches; the new run's prompt is the compact handoff, never the previous transcript or session.
- `tick --adapter codex|cursor` and the policy's `codex` and `cursor` providers with ids a probe accepted (the example's `sol` and `composer-standard` are candidates until then).
- README (two adapter sections with "What was observed" and "What is unverified", the transfer, "Not in PR 5"), CHANGELOG, DocsIndex, the test floor.
- One small real task through each enabled adapter, from the owner's terminal, recorded in the handoff.

## Acceptance criteria

- A provider transfer preserves the task, its PR, its attempts and its budget pool (the same pool id, consumed and attempts before and after; the PR row's number, head and base unchanged).
- The previous writer is stopped and reconciled before the new one launches: no two active writers across providers, and a transfer with an active run cancels and settles it first (the journal shows `settled` before `launched`).
- `limits.provider_transfers_per_pr` bounds transfers; the count is written before the launch; the bound refuses with a recorded reason.
- The new run's prompt is the compact handoff and never the previous transcript or session id.
- Each adapter declares only observed capabilities; its parser handles only the shapes the probe recorded; an unrecognised model id and a missing login are refused before any request.
- One small real task goes through each enabled adapter (owner's terminal), recorded in the ledger and the handoff.
- Every behaviour above has a test observed failing (mutation red / restore / clean / green), summary lines in the PR body.

## Records this work touches

- `tools/autonomy-controller/**`, `tests/scripts/autonomy-controller.test.sh` (test floor), `CHANGELOG.md` `[Unreleased]`, `docs/DocsIndex.md`, `docs/knowledge-graph.{json,md}` if regenerated.

## Decisions this work relies on

- No owner decisions-log rows (no spec for this workstream). Phase D's owner decisions (the pilot is guarded; workers launch from the owner's terminal) stand. Open for the owner: which Codex binary the controller uses (the Codex desktop app's bundled `codex.exe` under `~/.codex/.sandbox-bin/`, logged in through ChatGPT, or a separately installed `@openai/codex`), and which Cursor binary (the IDE's embedded `cursor-agent` under `%APPDATA%\Cursor\User\globalStorage\anysphere.cursor-agent-worker\...`, or a separately installed Cursor CLI). Defaults taken are integrator decisions recorded in the PR body and the phase handoff.
