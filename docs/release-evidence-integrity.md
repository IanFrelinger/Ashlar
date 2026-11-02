# Release-evidence integrity and deployment boundaries

The open release-evidence snapshot can be exported with a signed manifest using
[`scripts/evidence-export.py`](../scripts/evidence-export.py). Verification is offline and free.
This is an optional operator step after collection; the Action does not receive a signing key.
It uses Ed25519, the same public-key algorithm and explicit trusted-key pattern as
[`CertificationRecordEd25519`](../src/Ashlar.Certification.Contracts/CertificationRecordEd25519.cs)
and [`CertificationTrustPolicy`](../src/Ashlar.Certification.Contracts/CertificationTrustPolicy.cs).
The Python tool delegates cryptography to OpenSSL 3; it does not implement Ed25519 itself.

## Export and independently retain the anchor

Requirements: Python 3.10 or later, OpenSSL 3 with Ed25519, a collected JSON pack and its
Markdown report, and an operator-controlled Ed25519 PEM private key. Keep the private key
outside the repository, model inputs, workflow artifacts and repository secrets. The tool
never writes the key to the export. Encrypted-key prompts are not supported; use a separately
controlled signing environment when custody requires another mechanism.

```bash
python3 scripts/evidence-export.py export \
  --evidence /path/to/evidence.json --report /path/to/evidence.md \
  --audit /path/to/ashlar-mcp-audit/audit.ndjson \
  --private-key /operator-controlled/signer.pem --output /path/to/export
```

`--audit` is repeatable; pass only files intended for this snapshot. Omit it to sign only the pack
and report. The command prints the SHA-256 of `manifest.json`. **Retain that expected hash outside
the export and local audit log's deletion domain before relying on the export.** For example,
the reviewer can record the hash in a separately controlled review record that the collector cannot
rewrite or delete. Retain the trusted public key independently through an authenticated channel.
A hash file beside the local log, a public key embedded in the export, or an artifact sharing the
collector's deletion permissions does not establish this independent trust.

The operator chooses retention period and permissions for the external anchor and export. The tool
does not contact a retention service or attest that independent retention occurred. A later verifier
must obtain the expected hash and intended public key from the previously trusted records, rather
than compute its expected hash from the export being checked.

```bash
python3 scripts/evidence-export.py verify --bundle /path/to/export \
  --trusted-public-key /previously-trusted/signer-public.pem \
  --expected-manifest-sha256 <hash-from-independent-review-record>
```

Exit zero means the anchor matches, the pinned Ed25519 key verifies the signature, and the entire
exported file inventory matches its signed sizes and hashes. Missing files, a truncated audit tail,
modified bytes, an unexpected file, a symlink, a wrong key or an unsupported schema fail verification.
Signature verification uses captured manifest, signature and trusted-key bytes, so replacing their
source paths during verification cannot combine one manifest's anchor with another's signature.
Keep the bundle quiescent and read-only while verifying. Data-file hashes describe bytes read during
verification; the utility does not lock the bundle or guarantee its state after those reads.
It does not evaluate release readiness, establish the collection host's identity, prove that assertions
were true, or refresh GitHub facts. Human release review and revalidation remain necessary.

## Open export format and coverage

`manifest.json` is UTF-8 JSON with `schemaVersion: "ashlar.evidence-export/1"`,
`signatureAlgorithm: "Ed25519"`, an operator-machine `exportedAt` timestamp, unknown authenticated
actor metadata, coverage text and a `files` array. Each entry contains `path`, byte `size` and lowercase
hex `sha256`. The export contains `evidence.json`, `evidence.md`, optional `audit-NNNN.ndjson` files,
the manifest and raw 64-byte `manifest.sig`. Ed25519 signs the exact manifest bytes, including the final
newline; no verifier-side JSON canonicalization is needed. The anchor is SHA-256 of those same bytes.
The timestamp is signed content, not an independently trusted timestamp.

The verifier accepts only the listed flat filenames, at most 128 data files, at most 64 MiB per data
file and 256 MiB total. Manifest size is capped at 128 KiB. It rejects schema downgrades and duplicate
paths. The manifest hash and signature protect all metadata even when a consumer does not interpret it.

The manifest fixes the bytes present **at export time**. It detects deletion or rewriting relative to
that retained snapshot, including an entire supplied audit file. It cannot detect records or sessions
already absent before export, calls outside the recorded path, or a collector that signs false facts.
Run the existing [`McpBrickAuditChain.Verify`](../src/Ashlar.Mcp.Bricks/McpBrickAuditChain.cs) separately
when checking links within supplied audit records. The signed manifest preserves that trail; it does
not replace chain verification. An audit write can fail after a read-only brick ran; absence from a
signed export does not establish that a call never happened. Reconciliation with caller-side outcomes
and independently retained anchors remains necessary.

## Supported credential and host boundaries

For the PR-blockers Action, the MCP host process receives the GitHub read credential through its
environment. The collection tool reads GitHub; no model is involved in deterministic collection.
Treat the runner and the pinned Ashlar Action/host source as trusted with that credential. Use a token
scoped to the selected repository and the documented read permissions; policy reads requiring
unavailable authorization remain unknown. Do not grant write permissions merely to improve an answer.

When a model calls the MCP host, the supported allowlist limits tools dispatched by that host and the
per-call audit records cover that dispatch path. Stdio does not authenticate a human caller. Actor labels
provided by a caller are not authenticated identities. A model process with its own network, shell or
credentials can bypass the gateway. The Action and this export utility do not provide a restricted agent
sandbox, network containment or container isolation. Naming an execution tier does not enforce it.
An enforcing host/network boundary must be deployed and tested separately before claiming that broader
control. No such bypass containment is evidenced by these export tests.

## Privacy and retention

The exporter copies only the explicitly supplied pack, report and audit files. Inspect the chosen scope
before export: source URLs, repository names, commit identities and review facts may reveal private work.
Do not supply raw API dumps, unnecessary PR text, access tokens or private keys. Signing does not redact
data. The existing audit format hashes arguments and outputs; unsalted hashes of low-entropy values can
be guessed and should not be treated as anonymization. Retain exports and external anchors under the
team's access and deletion policy, and record the point through which each anchor provides coverage.

The read-only path makes no external repository changes. Any later side-effecting path must durably
record intent before dispatch and surface an uncertain outcome if the external action may have completed
before acknowledgment or audit failure. These signatures do not supply exactly-once execution.
