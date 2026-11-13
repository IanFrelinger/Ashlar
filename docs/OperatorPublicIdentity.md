# Public operator identity metadata

The public metadata slice for #480 adds `OperatorKey.TryReadPublicIdentity(keyDir)`.
It reads and validates only `operator.pub`, returning its public key and SPEC-006
fingerprint. It does not load a private seed, discover historical/peer keys, create
files, sign data, activate signing, or write gate/ledger records.

A missing public file or directory returns null. Malformed public data and other
read errors remain errors at the kernel API. Node metadata reports unavailable
identity as null; the mesh hello fingerprint uses `(unknown)`. Neither means that
gate decisions are unsigned. A valid public key is metadata, not proof that this
process possesses the private key, can sign, or is an authenticated trusted peer.

The heartbeat, discovery registration and mesh hello announcement use this lookup.
Discovery remains presence only; package trust and admission checks are unchanged.
Explicit signing and diagnostic paths, including `keys show`, still use the existing
keypair checks where required. A corrupt or mismatched private key is not repaired
or accepted for signing by the metadata API. Historical signed records and each
consumer's trust rules are unchanged.

This is one bounded migration step, not protected-project routing. There is no new
worker-selected protected mode, local-write fallback, service installation or key
transfer. Production verification/import/launch writers still need an authenticated
service route. The separate sealed broker prototype stays draft; it is not connected
to these production writers. Human approval, trusted evaluation, full custody
migration and deployment acceptance remain outstanding.

Validation uses disposable metadata fixtures. The CLI tests invoke discovery
registration and the real registered hello handler in memory, without starting a
host or listener. Kernel cases cover public validation, independence from private
material, missing-public refusal, raw byte preservation and unchanged signing/trust
behavior. Same-SHA mutation evidence is required before describing validation as
complete. These application cases do not prove OS or container isolation. The earlier
security-filter-blocked isolation harness must not be retried or reconstructed.
