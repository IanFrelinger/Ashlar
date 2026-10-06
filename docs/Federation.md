# Federation — hub-less peer sharing

Ashlar nodes share the extensions they've grown with each other, **peer to peer, with no hub
and no designated always-on machine**. Any nodes that can reach each other — on a LAN, across a
Tailscale/VPN tailnet, or by a configured address — find each other and pull each other's signed
extension packages, and each one re-gates everything it receives through **its own** trust root
and policy.

> This is the OSS `.ashpkg` peer federation (F1–F4), surfaced at `/mesh/v1/…`. It is a different
> thing from the commercial **director/hub** mesh (fleet placement, leases, trust tiers — see
> [`docs/commercial/mesh-add-on-sku.md`](commercial/mesh-add-on-sku.md) and the "Mesh (director/hub)"
> subsystem row). This page is only about the hub-less peer surface.

## The one invariant

**The network is transport; the seal is the trust.** A package is an Ed25519-signed `.ashpkg`
sealed by an operator key. However it arrives — a shared folder, a peer's HTTP endpoint, a
multicast-discovered address, a tailnet — it is:

1. **verified intrinsically** (the seal must check out; a tampered or unsigned package is refused
   before anything is parked),
2. **refused unless its signer is trusted** by the *receiving* node (`selfExtend.trustedSigners`
   in policy, or the operator's `keys trust` keychain — an empty trust set refuses everything,
   fail-closed), and
3. **re-decided by the receiver's own gate under its own policy** — a `proposing` node **holds**
   imported code for a human to seat; only a `self-extending` node auto-admits it (within budget —
   see [`RunningASelfExtendingNode.md`](RunningASelfExtendingNode.md)). An admitted import is
   written to the tree without the post-apply canary; that canary runs only on the node's own
   self-extend cycles, and only when a verifier is registered.

Nothing about how a package travelled changes any of that. Discovering a peer is *presence*, never
trust — a stranger's node on your LAN can announce itself and be pulled *from*, and its untrusted
packages are still refused.

## Everything is opt-in

A default node exposes nothing and pulls from nowhere. Each capability below turns on only when you
set its environment variable ([`deploy/node.yml`](../deploy/node.yml) documents them all with a
commented example).

## F1 — Serve your packages

Offer this node's published, signed packages read-only over HTTP:

```
ASHLAR_MESH_SERVE_PORT=7420
ASHLAR_NODE_NAME=study-node        # optional; defaults to the machine name
```

On the `air-gapped` and `secure-workstation` deployment profiles the node does not serve: mesh serve listens on
every interface and those profiles allow inbound listeners on loopback only (SPEC-007 PR 4.10), so with the port set
it logs the refusal at Error, binds nothing, and the rest of the daemon runs.

Three read-only endpoints:

| Endpoint | Returns |
|----------|---------|
| `GET /mesh/v1/hello` | this node's name, key fingerprint, package count |
| `GET /mesh/v1/index` | the published package list (`file`, `size`) |
| `GET /mesh/v1/pkg/{file}` | one package |

Safe by construction: everything offered is already sealed and signed (the store refuses to hold
what doesn't verify), the surface is read-only, file names are validated traversal-free and against
resolved-path containment, oversized files are excluded, and a bind failure is logged without taking
the daemon down. With `deploy/node.yml`, uncomment the matching `ports:` entry so the LAN can reach
the port.

## F2 — Pull from peers

Pull from other nodes' serve endpoints on the daemon's timer:

```
ASHLAR_MESH_PEERS=http://192.168.1.20:7420,http://100.x.y.z:7420
ASHLAR_MESH_PULL_INTERVAL_SECONDS=300     # default 300
ASHLAR_MESH_PULL_PROJECT=/data/state/project
```

A peer address can be **anything routable** — a LAN IP, a tailnet address, a hostname. Each package
a peer serves is downloaded (bounded), verified, and run through this node's trust gate. A peer that
is offline, slow, or malformed is counted and skipped — never a crash. `ASHLAR_MESH_PULL_DIR` (a
synced folder) is a fourth source that works the same way.

## F3 — Zero-config LAN discovery

Announce yourself and hear peers on the local network, with no addresses configured:

```
ASHLAR_MESH_DISCOVERY=1
```

The node multicasts `{name, fingerprint, serve port}` on `239.7.42.1:7421` and listens for others;
discovered peers feed the same pull. See who's around:

```bash
ashlar mesh lan          # peers this node has discovered, and which are keychain-trusted
```

**Environmental honesty:** multicast reaches the physical LAN from native / host-network nodes, but
Docker Desktop's bridge does not forward it to the LAN (containers on one bridge still hear each
other). Where multicast doesn't cross, `ASHLAR_MESH_PEERS` is the works-everywhere baseline and
discovery is the zero-config bonus on top.

## F4 — Beyond the LAN: tailnet and mTLS

**Tailnet (internet-wide, across NAT).** If the node (or its host) is on a Tailscale tailnet:

```
ASHLAR_MESH_TAILNET=1
ASHLAR_TAILNET_PEER_PORT=7420      # the port peers serve on
```

It reads `tailscale status --json` and pulls from every online peer's tailnet address, through the
same gate. A missing/failing `tailscale` binary just means "no tailnet peers right now."

**TLS / mTLS (a private fleet).** Give the serve endpoint a cert to encrypt the wire, and require a
client cert the fleet CA signed so only fleet members can even list or download:

```
ASHLAR_MESH_SERVE_TLS_CERT=/run/secrets/mesh-cert.pem
ASHLAR_MESH_SERVE_TLS_KEY=/run/secrets/mesh-key.pem
ASHLAR_MESH_SERVE_REQUIRE_CLIENT_CERT=1
ASHLAR_MESH_SERVE_CA=/run/secrets/fleet-ca.pem
# peers then pull over https:// presenting their own cert:
ASHLAR_MESH_CLIENT_CERT=/run/secrets/mesh-cert.pem
ASHLAR_MESH_CLIENT_KEY=/run/secrets/mesh-key.pem
ASHLAR_MESH_CA=/run/secrets/fleet-ca.pem
```

Client certs are validated against the fleet CA with **custom-root trust** — a public CA cannot mint
a fleet identity. This is defence in depth *on top of* the package seal (which still decides what
runs), and it is **fail-closed**: a half-specified TLS config (require-client-cert without a server
cert, only one of cert/key, or mTLS without a CA) makes the serve endpoint **refuse to start rather
than fall back to plaintext**. mTLS pairs naturally with configured/tailnet peers (a known roster);
LAN multicast stays the plaintext zero-config path.

## The strategy seam

Where peer addresses come from is a swappable strategy (`IPeerSource`): configured URLs, LAN
multicast, and the tailnet source all ship, and the auto-pull consumes the union of every registered
source each tick. Adding a new discovery mechanism (a rendezvous file, a DHT, a service registry) is
a new class and a DI line — never a change to how packages move or how they are trusted, because a
source only *nominates an address to pull from* and pulling is already fail-closed end to end.

## Setting up trust

Before a node will admit anything from a peer, trust that peer's signer:

```bash
# on the origin node — read its fingerprint:
ashlar keys show                       # → ed25519:abcd…

# on the receiving node — trust it (or list it in policy under selfExtend.trustedSigners):
ashlar keys trust ed25519:abcd…
ashlar keys peers                      # the trusted set + its digest
```

A node always trusts its own operator key (self-trust), so a node re-importing what it published
needs no ceremony.

## Try it: two nodes, held for review

```bash
# node A — serve, and publish something worth sharing
ASHLAR_MESH_SERVE_PORT=7420 ASHLAR_MESH_AUTOSHARE=1 ashlar background-agent daemon &

# node B — trust A's signer, pull from A
ashlar keys trust <A's fingerprint>
ASHLAR_MESH_PEERS=http://<A-host>:7420 ashlar background-agent daemon &

# on B, A's package lands in the review queue (proposing mode):
ashlar gates                           # → ! ext-…  a person seats the stone
```

A stranger node that B doesn't trust gets `refused (untrusted signer)` instead — nothing parks.

## See also

- [`RunningASelfExtendingNode.md`](RunningASelfExtendingNode.md) — what a node *does* with what it pulls, and how to arm one safely.
- [`../deploy/node.yml`](../deploy/node.yml) — every federation env var, with a commented deploy example.
