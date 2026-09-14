# Phase 12 — Automating remote networking gaps

This doc maps the **three remaining “go to sea” checks** to automation tiers. Phase 11 covered Docker-bridge negatives; Phase 12 closes **TLS**, **gRPC in CI**, and **remote/Tailscale** as far as GitHub-hosted runners allow.

## Summary

| Gap | Automate in CI? | What we ship |
|-----|-----------------|--------------|
| **TLS** | Local, plus CI on dispatch (weekly schedule removed 2026-09-13) | Caddy + [`mesh-lab-tls-certs.sh`](../scripts/mesh-lab-tls-certs.sh) + [`mesh-lab-verify-tls.sh`](../scripts/mesh-lab-verify-tls.sh) |
| **gRPC transport** | No PR lane - push to `master`/`main`/`cursor/**` + dispatch (`grpc-transport-gate.yml`); PrimeTime `test-prime-time` is pre-release | [`Ashlar.Tests.Transport`](../src/Ashlar.Tests.Transport) `Category=ProdStyle` + `grpc-transport-gate.yml` |
| **Two-host / Tailscale** | Partial | [`mesh-lab-verify-remote.sh`](../scripts/mesh-lab-verify-remote.sh) + self-hosted runner recipe |

---

## 1. TLS (automated)

**Idea:** Ashlar stays HTTP on `peer-a:8080`; **Caddy** terminates HTTPS on the lab bridge ([`deploy/compose/docker-compose.mesh-lab-tls.override.yml`](../deploy/compose/docker-compose.mesh-lab-tls.override.yml)).

```bash
make mesh-lab-e2e-tls
# or:
./scripts/run-mesh-lab-e2e-tls.sh
```

[`mesh-lab-verify-tls.sh`](../scripts/mesh-lab-verify-tls.sh) checks:

- `GET https://127.0.0.1:18443/health` (self-signed cert; `-k` unless `MESH_LAB_TLS_CACERT` set; HTTP/1.1 for macOS LibreSSL)
- `GET/POST /api/mesh/*` over HTTPS (register, schedule → Assigned)
- Mutating mesh without API key → **401/403**

**CI:** [`.github/workflows/mesh-lab-tls-gate.yml`](../.github/workflows/mesh-lab-tls-gate.yml) — `workflow_dispatch` only; the Tue 07:00 UTC schedule was removed 2026-09-13 after four consecutive red scheduled runs.

**Production parity:** Replace self-signed certs with real DNS + ACME ([`friend-mesh.Caddyfile.example`](config/friend-mesh.Caddyfile.example)); same curl paths, trust store from Let’s Encrypt.

**Local macOS note:** Host `curl` (LibreSSL) against Docker-published HTTPS can fail with `bad decrypt`; [`mesh-lab-tls-curl.sh`](../scripts/mesh-lab-tls-curl.sh) retries via OpenSSL `curl` in a container (`host.docker.internal`). CI on `ubuntu-latest` uses host `curl` directly.

---

## 2. gRPC (automated in test matrix)

**Idea:** [`GrpcAgentTransportTests`](../src/Ashlar.Tests.Transport/GrpcAgentTransportTests.cs) already spin a **real Kestrel** gRPC server (`GrpcServerFixture`) — not mocks.

Ensure CI runs Transport:

```bash
dotnet test src/Ashlar.Tests.Transport/Ashlar.Tests.Transport.csproj --filter "Category=ProdStyle"
# or full prime-time:
make test-prime-time
```

**Optional dedicated gate:** [`.github/workflows/grpc-transport-gate.yml`](../.github/workflows/grpc-transport-gate.yml) — fast, no Docker.

No extra hardware required.

---

## 3. Tailscale / two-host (semi-automated)

GitHub **ubuntu-latest** is a **single** host; it cannot join your production tailnet without secrets and a second node. Automate in three layers:

### Layer A — Remote URL script (any laptop)

Deploy mesh lab (or friend-mesh) on **machine A**; run worker on **machine B**. From a third machine or CI runner with routes:

```bash
export ASHLAR_MESH_DIRECTOR_BASE_URL=https://100.64.0.1:8080   # director tailnet IP
export ASHLAR_MESH_API_KEY=...
export MESH_LAB_PEER_REGISTRATION_KEY=...                    # ≠ API key
export MESH_LAB_REMOTE_WORKER_URL=http://100.64.0.2:8080    # worker tailnet IP
export ASHLAR_MESH_TLS_INSECURE=1                              # if using self-signed TLS
./scripts/mesh-lab-verify-remote.sh
```

### Layer B — Self-hosted GitHub Actions runner on tailnet

1. Install runner on a host that is **always on tailnet**.
2. Register runner with label `ashlar-tailnet`.
3. Workflow uses `runs-on: [self-hosted, ashlar-tailnet]` + [`tailscale/github-action`](https://github.com/tailscale/github-action) only if the runner is **not** already on the tailnet.
4. Job runs `mesh-lab-verify-remote.sh` against secrets:
   - `ASHLAR_MESH_DIRECTOR_BASE_URL`
   - `ASHLAR_MESH_API_KEY`
   - `MESH_LAB_REMOTE_WORKER_URL`

### Layer C — Scheduled smoke from your cloud VM

Reuse [`scripts/bootstrap-cloud-mesh-lab.sh`](../scripts/bootstrap-cloud-mesh-lab.sh) on a VM with Tailscale installed; second peer is a laptop also on tailnet. Document IPs in `.env` and run remote verify from the VM cron.

**What we do *not* fake in Docker:** true cross-NAT tailnet ACLs — use real Tailscale for that.

---

## Recommended CI schedule (networking)

| Workflow | When | Covers |
|----------|------|--------|
| `mesh-lab-gate.yml` | Manual (`workflow_dispatch`; automatic triggers removed 2026-08-11) | Bridge HTTP + negatives + persistence + Phase 13 data plane |
| `mesh-lab-stress-gate.yml` | Manual (`workflow_dispatch`; weekly schedule removed 2026-08-16) | Stress + post-stress |
| `mesh-lab-tls-gate.yml` | Dispatch only since 2026-09-13 | HTTPS director |
| `grpc-transport-gate.yml` | Push to `master`/`main`/`cursor/**` + dispatch | gRPC Kestrel round-trip |
| `friend-mesh-prefab-gate.yml` | Push to `master`/`main`/`cursor/**` + dispatch | Single-hub auth smoke |
| PrimeTime `test-prime-time` | Pre-release | Transport + full ProdStyle |

---

## Revision history

| Date | Change |
|------|--------|
| 2026-05-19 | TLS override + remote verify script + automation doc. |
