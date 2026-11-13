# Linux governance broker prototype — #480

Local experimental code only. Not installed, deployed, packaged, or wired into Ashlar commands.
The supported target is Linux x64 or arm64 (runtime validation pending).
A service process runs as a separate non-root Linux UID and owns a disposable state tree
and synthetic signing key. One configured worker UID may submit requests over an activated
Unix-domain socket. Peer identity comes from Linux SO_PEERCRED, not JSON.

This first slice deliberately **cannot admit anything**. It has no trusted course evaluator
and no separate human approval authority. Every valid submission is signed as Rejected under
a service-owned sealed policy, with empty course results. A signed rejection establishes
what the service recorded, not successful evaluation of the worker's content.
There is no decide, arbitrary signing, path selection, policy update, key administration,
ledger append, migration, or client-local fallback operation.

## Contract

One UTF-8 JSON object followed by newline, at most 65,536 bytes, one request per connection.
All names are case-sensitive; unknown/duplicate fields, unsupported operations, non-string
request values and invalid IDs are refused. IDs are nonzero UUIDs in D format.

Submit: `{"op":"submit","project":"demo","requestId":"11111111-1111-1111-1111-111111111111","kind":"brick","summary":"説明","diff":"descriptive text"}`

Read: `{"op":"read","project":"demo","requestId":"11111111-1111-1111-1111-111111111111"}`

Summary and diff are opaque text, never executable content or paths. Existing serialization
and signatures remain unchanged. Server time and peer UID supply attribution. The persisted
request ID and exact content deduplicate retries across restart; different content conflicts.
Existing GateStore locking, append-once and trust verification remain in use.

## Provisioning boundary — not performed by this prototype

An administrator would need to provision identities, trusted executable/configuration and
ancestors, an activated socket, private state/key directories and a **synthetic** keypair.
This program performs no installation, user/group creation, chmod, chown, lock replacement
or permission repair. Such deployment needs separate action-time authorization.

A configuration has exactly `project`, `root`, `socketPath`, and `workerUid`.
The service UID is the current effective UID; both UIDs must be nonzero and different.
The absolute root must contain private `keys/` and `state/` directories, and
`.prototype-only` containing exactly `ashlar-governance-prototype-v1\n`.
The marker is an explicit disposable-root guard, not proof of trusted history.

The systemd service must supply UMask=0077, one listening Unix stream socket as fd 3,
LISTEN_PID and LISTEN_FDS=1. Its socket path must match the configuration. The socket
directory and all state/config ancestors must be owned by root or the service and deny
group/other writes. The entire private root must be service-owned, have no group/other
access, no symlinks, special files or multiply-linked files. Public key material is loaded
strictly before GateStore is constructed; missing/invalid keys refuse startup. Ambient
ASHLAR_KEY_DIR is pinned only inside this dedicated process from validated config.

These are necessary preconditions, not a deployment attestation. Executable/library/service
unit integrity, namespace mappings, mount behavior, process capabilities, debugging access,
supplementary groups, MAC policy and administrator compromise are outside this prototype.
Two processes with the service identity are in the same trust domain. A worker UID does
not authenticate a human. Socket permissions must restrict access even though peer UID is
checked, to limit denial of service.

## Validation scope and remaining work

Tests under `../GovernanceBrokerPrototype.Tests` exercise application contracts with
disposable synthetic keys/records and pure metadata rules. They do not launch separate
identities, replace lock files or establish OS isolation. The previously rejected runtime
harness has not been recreated. No reference app should proceed on this evidence alone.

Run only through the repository devtest container, for each net8.0 and net10.0:

`bash scripts/test-in-container.sh --dirty --project tools/GovernanceBrokerPrototype.Tests/GovernanceBrokerPrototype.Tests.csproj --framework net8.0 --filter FullyQualifiedName~GovernanceBrokerPrototype`

No migration occurs. Reads and rejected requests must leave existing fixture record and
activation-marker bytes intact. The service is serial, caps new records at 128, and bounds each connection to five
seconds. This caps this prototype's ordinary write surface but is not a disk quota or
rate-limit guarantee; pre-existing administrator-provisioned content and resource use
need separate accounting. It has no production throughput, maintenance, crash-recovery, coordinated
key rotation or full-writer migration design. A crash after commit can yield a lost response;
retry/read with the same request ID recovers its record. Existing signed-history and ledger
tail-truncation limits remain unchanged.

The owner chose the Linux-first experimental direction on 2026-11-13. The accompanying
decision record must go through the normal records PR before production adoption. The
sealed-only API is an integrator scope reduction pending a trusted evaluator and separate
approval authority, not an owner decision to remove human approvals.

## Platform references

The implementation uses the Linux credential model and Unix peer credentials; neither
source establishes that a particular deployment is isolated.

- [Linux credentials](https://www.kernel.org/doc/html/latest/security/credentials.html)
- [Unix-domain sockets and SO_PEERCRED](https://man7.org/linux/man-pages/man7/unix.7.html)
- [statx metadata and returned-mask semantics](https://man7.org/linux/man-pages/man2/statx.2.html)

The inherited socket's reported pathname is checked against configuration, but its filesystem
inode/permissions are not independently attested against the descriptor. Startup metadata
checks rely on trusted owners not changing paths concurrently. No descriptor-relative
filesystem confinement or mounted-filesystem attestation is claimed.
