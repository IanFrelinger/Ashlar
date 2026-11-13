# Read-only project verification

Use `ashlar verify --read-only --path <project>` to check the current project without
loading the operator private key or appending a certification entry. It runs the
same project courses as ordinary `verify`, including the existing provenance
check when a signed ledger is present. A successful result says **VERIFIED** and
identifies the read-only operation; it does not claim that this command certified
the project.

## Behavior and compatibility

- Valid unsigned projects keep the existing verification semantics and may pass.
  Read-only verification does not create a ledger or generate keys.
- An existing signed history is verified using the existing intrinsic signature
  and chain rules. Historical JSON and signature bytes are neither rewritten nor
  normalized.
- Changed documents with stale certification fail even if an operator private key
  is available. Read-only verification cannot repair that failure by signing.
- Corrupt history fails under the same verifier rules. Missing, malformed or
  mismatched private key material does not affect the read-only path because that
  path does not load it.
- Ordinary `ashlar verify` retains its operator-driven certification behavior.
  Successful base courses can be re-certified with the current operator key;
  corrupt signing material still refuses rather than falling back to unsigned.

The exit codes remain 0 for successful verification, 65 for failed courses, and
nonzero for usage or read errors. JSON output remains unsupported for this
command. The read-only flag does not grant permission to change state on error.

## Generated launchers

New native bundles invoke `verify --read-only` before running a request. Their
Unix and Windows launchers stop on a nonzero verification result and never retry
ordinary `verify`. The default native export builds its CLI from the current
source. Staged bundles (`--no-runtime`) and custom `--cli-project` builds must
supply a runtime supporting `--read-only`; an older CLI must fail on the unknown
flag, never retry signing. A successful launcher banner says that verification passed; certification metadata in
`bundle.json` describes staging time, not a new startup certification.

Cloud bundle entrypoints are unchanged in this slice. They use a separately
published, optionally user-selected runtime image that may not implement the new
flag. Migrating those entrypoints requires a compatible runtime contract; there
must be no fallback to signing when read-only verification is unavailable.
No runtime image is published or deployed by this change.

## Limits of this migration slice

This separates checking from signing in the CLI and native templates. It does
not create a protected-project registry, broker transport, trusted evaluator,
authenticated human authority, or a complete worker/service custody boundary.
Managed projects must ultimately route authoritative work through their trusted
service and refuse on service failure; this flag is not that routing mechanism.

Existing trust limitations remain. Absent or empty history is still treated as
unsigned by the verifier; this option does not require prior certification,
establish external signer trust, detect complete history removal, or provide an
atomic snapshot against concurrent writers. Existing locking-support checks
remain. No claim is made that all filesystem or system activity disappears.

The application tests cover parser/handler behavior, unchanged project/key bytes,
legacy signing compatibility and native template contracts. They do not prove
OS isolation, key custody, deployment acceptance or production readiness. The
earlier blocked #480 isolation harness is not part of this validation.
