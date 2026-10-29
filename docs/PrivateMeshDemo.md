# Private-mesh demo

Three Ashlar nodes on one machine share one signed brick package. It takes about 20 seconds and needs only
Docker with the compose plugin:

```bash
bash scripts/demo/private-mesh-demo.sh
```

It prints PASS or FAIL for each check and exits 0 only when every check passes. With `KEEP=1` the nodes keep
running so you can look around, for example with `docker compose -p ashlar-private-mesh-demo exec node-b bash`.
Stop them with `docker compose -p ashlar-private-mesh-demo down -v`.

## What it shows

| Scene | What happens | How the script checks it |
|---|---|---|
| Setup | node-a seals `hello-mesh.ashpkg` with its own operator key. node-b trusts node-a's key; node-c trusts no one. All three run the same policy: `proposing` mode, bricks may be added, and `sandbox`, `tests` and `security` results are required. | node-a's signer fingerprint; `keys peers` on node-c |
| 1 | node-a serves the package. | `GET /mesh/v1/index`, called from node-b, lists it |
| 2 | node-b pulls the package, verifies both signatures and the file hashes, and checks that it trusts the sealer. Its own gate then holds the package for a person. A person approves it with `gates --admit`, the brick's file lands in node-b's project, and node-b's decision record is signed with node-b's own key. | node-b's auto-pull log shows `1 held`; the file exists; `gates --show` names node-b's key |
| 3 | node-c pulls the same package and refuses it, because it doesn't trust node-a's key. Nothing lands. | node-c's auto-pull log shows `1 refused` |
| 4 | A copy edited after sealing fails verification. `pkg show` reports that the seal does not verify, and node-b refuses the copy on its next pull. | `pkg show` exits non-zero; node-b's log shows the refusal |

## What it doesn't show

Say exactly this much when you present it:

- **The gate itself.** The script supplies the gate results inside the package, because the published image has
  no .NET SDK to run them. The admission gate is tested by the `cert-gate` workflow.
- **Re-testing on the receiving node.** node-b decides under its own policy, using node-a's signed results. It
  does not re-run the tests, and imports skip the post-apply canary (see [Federation](Federation.md)).
- **Evidence across nodes.** Each node keeps its own decisions and receipts; nothing combines them. The
  aggregated audit that `LICENSING.md` lists for the commercial tier is not built.
- **Fleet.** The commercial Fleet modules place work across nodes. They don't move brick packages and play no part
  here.
- **Quorum or distributed verification.** A package has exactly one signer. Quorum acceptance and distributed
  verification are planned, not built.
- **Running the brick.** The demo stops once the brick's file lands in node-b's project.

## Images

By default the nodes run the published 0.4.2 image, pinned by digest in
`deploy/compose/docker-compose.private-mesh-demo.yml`, the same pin as `deploy/node.yml`. To try a local build:

```bash
docker build --file .docker/Dockerfile.cli --tag ashlar-cli:local .
ASHLAR_IMAGE=ashlar-cli:local bash scripts/demo/private-mesh-demo.sh
```

The `Private mesh demo` workflow does the same on pull requests that change the mesh, packaging or gate code, so a
change that breaks sharing between nodes fails before it merges.

## Going further

[Federation](Federation.md) covers what the demo leaves out: LAN discovery, tailnet peers, mTLS between nodes,
and the egress rules on the air-gapped and secure-workstation profiles. `scripts/lab/release-federation-lab.sh`
is the older manual lab this demo grew from.
