# Proposal-store recovery and lineage admission

`ChangeProposalStore` keeps its existing status-folder layout. A proposal ID is append-once
across every bucket, including corrupt rows. IDs must be safe non-reserved ASCII identifiers
(letters, digits, hyphen or underscore, at most 128 characters). IDs remain ordinal/case-sensitive;
on case-insensitive filesystems the filesystem also refuses aliases of an existing name.

## Failure semantics

- Add serializes into a private temporary file, flushes file data to disk, and publishes the
  complete row with a non-overwriting rename. Write/flush failures remain I/O failures, not
  misleading duplicate-ID errors. Unpublished temporary files never appear in listings.
- A transition first publishes a complete, flushed intent in `.transactions/`. This is its
  commit point. It then publishes the new status row, removes the original, and removes the
  intent. The original JSON is never rewritten in place.
- Every read and write obtains the same persistent `.store.lock` and completes outstanding
  intents first. A restart before intent publication preserves the old decision. A restart
  after publication completes the exact committed decision, preserving content and resolver.
- An exception after the commit point has an uncertain return outcome: inspect the proposal
  using `Find` before deciding whether a new operation is necessary. Retrying approval blindly
  may correctly report that the proposal is already approved.
- Invalid JSON, null/missing required content, path/status disagreement, conflicting duplicates,
  invalid journals and unreadable rows fail visibly. They are never silently omitted. Recovery
  refuses to overwrite or remove conflicting data; preserve the directory and investigate it.
- Do not unlink `.store.lock` or manually edit a store while writers are running. Cooperating
  processes depend on exclusive file sharing and same-volume atomic rename semantics. Locking
  disabled by the runtime or unsupported by the filesystem is not covered by this protocol.

`Flush(true)` persists file data. This protocol provides process-crash recovery, not a portable
power-loss guarantee: directory-entry durability depends on the filesystem and storage stack.
Orphan `.tmp-*` files are ignored and may be removed offline after confirming no writer is running.

## Lineage admission

The registry stores admitted parent edges and depth high-water marks separately from mutable
agent configurations. Parent/child configuration mutation, ID mutation and re-registration
cannot lower admitted depth. Later parent registration or deepening also constrains existing
descendants, including descendants whose replacement configuration omitted that old parent.
Cycles and traversal beyond 64 hops fail closed. Narrower admitted extension ceilings remain
in effect after re-registration. These records last for the registry lifetime, like the existing
extension-cycle ledgers; this change does not add cross-process/restart lineage persistence.

## Executable evidence

The `Lineage and proposal durability` workflow tests the exact PR head in the devtest container:

- Focused registry and on-disk store regressions on net8.0 and net10.0, with no skipped cases.
- Separate writer processes killed at twelve persistence boundaries per framework, followed by
  restart and idempotent second-read checks. The probe links actual production store source.
- Named red/restore/green mutations for lineage, budget narrowing, journal publication/recovery,
  status consistency and source cleanup, with assertion names and TRX evidence retained.

Run through `scripts/test-in-container.sh`; never run host `dotnet`. Results must be inspected
for the exact commit before claiming these checks passed. The workflow is advisory and does not
change the repository's five required checks.
