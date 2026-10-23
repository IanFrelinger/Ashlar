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

## How `verify` uses it (next slice)

1. Run the existing courses (`contract`, `composition`, `envelope`).
2. Add a `provenance` course that calls `VerifyChain` on the existing ledger — a broken chain
   fails verification loudly, even for a keyless reader.
3. On success, if an operator key is present, append a signed `verification` entry whose
   `Subject` is the current documents' hash, and render **CERTIFIED · signed ed25519:… · #Seq**.
   With no key, render **VERIFIED · unsigned** exactly as today.

## Why it mirrors the gate store

Both are admission-boundary records with the same hazards — two writers racing a sequence, a
crash mid-write, a corrupt file that must fail loud rather than vanish — so the ledger reuses the
gate store's proven shapes: a `FileShare.None` cross-process lock the OS releases on death,
write-to-temp-then-rename, and fail-closed reads. Same canonical-JSON and Ed25519 machinery
(SPEC-006) signs both.

## Runtime file-lock prerequisite

GateStore and InstanceLedger constructors share a once-per-process runtime self-test: a
second `FileShare.None` open of a held temporary file must be refused. If exclusion is
disabled (for example by `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` on Unix), construction
fails with an explanation to remove the setting and restart. An inherited or operator-set
environment variable does not authorize operation without this integrity prerequisite.
Windows continues to work when its kernel still enforces sharing restrictions. A failed
probe is cached as well as a successful one; inability to create the probe also refuses use.

The cached probe establishes the process/runtime's behavior on the temporary filesystem,
not every store filesystem's semantics. Stores still require a trusted local directory
and cooperative lock-file lifetime: deleting/replacing `.lock` while another writer
holds it can split lock identity. The probe does not prevent that, establish network-file-
system safety, or defend against an actor controlling the runtime or arbitrary store files.
