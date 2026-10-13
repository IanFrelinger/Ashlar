# PR 5 provider transfer — summary

`task transfer <id> --to <provider>` (`Controller.transfer_task`) is an operator-only command that
moves a task from one provider's CLI to another; nothing triggers a transfer automatically. It checks
things in a fixed order so that any refusal leaves the system unchanged.

First it validates the target: the provider must be named in the policy, enabled, not disabled, not
already the task's provider, and must share the same budget unit as the task's pool (units are never
converted). A `done` task cannot be transferred, and an invalid target exits 4 as a `PolicyError`.

Second, it checks the transfer bound: the number of transfers already made for the task's pool, counted
from the append-only journal across every task sharing that pool, must stay under
`limits.provider_transfers_per_pr`. Exceeding it refuses with exit 6 and is journalled as
`transfer-refused`.

Third, it stops and reconciles the previous writer. Any uncertain launch for that task is reconciled
through its own provider's adapter; a run that stays uncertain, or whose provider lacks a registered
adapter, refuses the transfer. Every active run is polled once. A completed implementation or repair run
is committed, pushed and proposed only when `--gh cli` is given — otherwise the transfer is refused, to
avoid silently settling work as `done` without ever proposing it. A still-running run is cancelled via
its adapter and given up to five seconds to exit before being settled. If this settlement leaves the task
`done`, or a proposal fails, the transfer ends there with nothing moved and nothing counted.

Fourth, the move itself happens as one transaction: the task's provider field changes, the PR's transfer
counter increments, and a `provider-transferred` journal entry records the full detail, guaranteeing
there is never a second concurrent writer.

The task keeps its identity, PR, and budget pool, but starts fresh on the new provider — a new clone,
no resumed session, and only a compact handoff brief, never the old transcript. No live task has yet run
through the new Codex or Cursor adapters; that remains operator-initiated future work.
