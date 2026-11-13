# Instance ledger (SPEC-003 v1)

The instance ledger is a project's **signed, append-only, hash-chained history of what was
certified** — the record that lets `ashlar verify` say *CERTIFIED* with a real signature instead
of *VERIFIED · unsigned*. It is the read side of the same trust model the gate store writes: gate
records prove *who admitted what*; the ledger proves *this project verified, signed, in this
order*.

It is presence-activated, exactly like signing everywhere else (SPEC-006): with an operator key
(`ashlar keys init`), a successful `verify` appends a signed entry and the wall reads CERTIFIED;
with no key, nothing is written and the wall reads VERIFIED · unsigned — zero-setup keeps working.

## On disk

```
<project>/.ashlar/ledger/
  000001.json      # genesis entry, seq 1
  000002.json      # seq 2, chained to 000001
  ...
  .lock            # FileShare.None append lock (never a chain entry — no .json suffix)
```

One JSON file per entry, named by zero-padded sequence. An entry:

| field      | meaning |
|------------|---------|
| `Seq`      | 1-based position; contiguous, no gaps |
| `At`       | UTC timestamp |
| `Kind`     | `verification` (the only v1 kind) |
| `Subject`  | SHA-256 (hex) identifying the exact certified documents — `SHA256( SHA256(manifest) ‖ SHA256(policy) )` |
| `Verified` | whether the verification passed |
| `Courses`  | frozen snapshot of the course results |
| `Prev`     | SHA-256 (hex) of the **previous entry's full canonical bytes** — the chain link; `null` for genesis |
| `Sig`      | Ed25519 signature over the canonical entry with `Sig`/`Signer` nulled |
| `Signer`   | base64 raw public key of the operator that signed |

`Subject` hashes each document independently and hashes the two fixed-length digests together, so
no two distinct document pairs collide by shifting where one ends and the next begins — with no
assumption about what bytes the documents may contain.

## Integrity — what it promises, and what it does not

**Promised (v1).** Every entry is signed and carries the hash of its predecessor, so:

- **Modifying** a past entry breaks both its own signature and the next entry's `Prev` link.
- **Inserting** or **removing** an entry breaks the sequence (a gap or duplicate) and the links.
- **Reordering** entries is caught by the per-position sequence check.

Any of these makes `VerifyChain` **throw** — the same loud, fail-closed refusal a corrupt gate
record gets, on the principle that a forged history is worse than a missing one. Verification is
**intrinsic**: each entry carries its own public key, so a fresh checkout with no operator key
still validates the whole chain. Append verifies the existing chain **before** extending it, so a
fresh valid-looking entry can never be used to bury a broken one.

**Not promised (v1), stated honestly.**

- **Tail truncation / rollback.** Dropping the last *N* entries leaves a shorter but internally
  consistent chain. Detecting that needs an external, anti-rollback anchor (a persisted signed
  head, or an off-box witness) — that is **v2**. In v1 the guard is the policy's
  `truncate_ledger` never-entry: the runtime may not rewrite the ledger, and doing so is a
  governance violation, not a silently-accepted state.
- **External trust root.** The signer is the local operator key, not an org root. An actor who
  holds that key can author history; v1 does not distinguish "the operator" from "an attacker who
  stole the operator's key." Org roots, revocation, and HSM-backed keys are **v2** (SPEC-006).

## How `verify` uses it

1. Run the existing courses (`contract`, `composition`, `envelope`).
2. Add a `provenance` course that calls `VerifyChain` on the existing ledger — a broken chain
   fails verification loudly, even for a keyless reader.
3. On success, if an operator key is present, append a signed `verification` entry whose
   `Subject` is the current documents' hash, and render **CERTIFIED · signed ed25519:… · #Seq**.
   With no key, render **VERIFIED · unsigned** exactly as today.

Use `verify --read-only` to run the same courses without loading an operator private
key or appending a new entry. Stale certification remains a failure even when a
signing key is available. New native launchers use this mode. See
[read-only verification](ReadOnlyVerification.md) for compatibility and limits.

## Locking prerequisites and limits

Before either the gate store or ledger is constructed, a shared, once-per-process
self-test opens a private temporary probe with `FileShare.None` and requires a second open to
fail with a sharing/lock violation (Windows) or `EWOULDBLOCK` (Unix). Unrelated I/O errors
fail closed rather than counting as successful exclusion. Both success and failure are
cached. If exclusivity is disabled (for example by
`DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` on Unix), construction fails loudly, naming the switch; unset it or
set it to `0` and restart. Failure to create the probe also refuses construction. The same
guard precedes every write-lock acquisition. Existing records are unchanged, but store-based
reads also require successful construction. This checks runtime behavior on the temporary filesystem, not locking
semantics on every possible store filesystem.

The store leaves its `.lock` file in place after closing the handle. Deleting a shared lock
file on release would allow contenders to lock different inodes. This does **not** defend
against a same-user actor who can unlink or replace `.lock` while another writer holds it.
Store directories must be trusted and must use a filesystem that supports local exclusive
file locks; the probe does not establish a hostile-directory or network-filesystem guarantee.

## Why it mirrors the gate store

Both are admission-boundary records with the same hazards — two writers racing a sequence, a
crash mid-write, a corrupt file that must fail loud rather than vanish — so the ledger reuses the
gate store's proven shapes: a `FileShare.None` cross-process lock the OS releases on death,
write-to-temp-then-rename, and fail-closed reads. Same canonical-JSON and Ed25519 machinery
(SPEC-006) signs both.
