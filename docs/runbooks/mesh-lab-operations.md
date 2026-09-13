# Mesh virtual lab — operations runbook

Short operator guide for the Docker mesh lab ([`MeshVirtualLab.md`](../MeshVirtualLab.md)). For production multi-org mesh, see [`ProductFleetImplementationRoadmap.md`](../ProductFleetImplementationRoadmap.md) Phase 5.

## Bring up / tear down

```bash
cp docs/config/mesh-lab.env.example .env.mesh-lab   # once
# Set ApiKey, Bearer, Basic, MESH_LAB_PEER_REGISTRATION_KEY (≠ ApiKey)
MESH_LAB_WORKERS=1 make mesh-lab-up
make mesh-lab-verify
make mesh-lab-down
```

One-shot (CI parity): `make mesh-lab-e2e-workers`, `make mesh-lab-e2e-deep`, `make mesh-lab-e2e-stress` (includes post-stress director checks).

## Director vs local `instances.json`

| Mechanism | Scope | Use when |
|-----------|--------|----------|
| **`ashlar mesh admit` / `revoke`** | Local **`instances.json`** discovery file | gRPC/capability routing on this host |
| **`POST /api/mesh/fleet/nodes/{id}/admit|revoke`** | **Director** in-memory fleet (peer-a in lab) | HTTP mesh placement only |
| **commercial mesh director CLI `admit|revoke`** | Same as HTTP above from headless hosts | CI scripts, workers without curl |

Lab verify runs **both** HTTP governance ([`mesh-lab-verify-governance.sh`](../../scripts/mesh-lab-verify-governance.sh)) and CLI ([`mesh-lab-verify-director-cli.sh`](../../scripts/mesh-lab-verify-director-cli.sh)).

## Upgrade order (lab)

1. **`make mesh-lab-down`** (or stop workers first if soaking traffic).
2. Pull / rebuild images (`make mesh-lab-up` or E2E script).
3. **Director (peer-a) before workers** — workers cache director URL; peer-a holds task registry.
4. Re-register fleet nodes (register includes **`peerRegistrationKey`** when policy is on).
5. **`make mesh-lab-verify`** then optional **`make mesh-lab-verify-deep`**.

Do not scale workers and change peer-a placement policy in the same step without re-verify.

## Split-brain and dual-writer risks

**Task state is director-local.** Each `Ashlar.API` that accepts `POST /api/mesh/tasks` has its own in-memory registry. In the lab, only **peer-a** is the director; peer-b and worker may still expose mesh routes for auth tests but must not be treated as source of truth for placement.

| Scenario | Symptom | Mitigation |
|----------|---------|------------|
| Two operators schedule against **different** directors | Duplicate task IDs possible; workers PATCH the wrong host | One canonical **`ASHLAR_MESH_DIRECTOR_BASE_URL`**; document hub DNS |
| Worker holds lease, director restarted | Tasks may show **Pending** again; stale lease on worker | Short **`leaseSeconds`**; worker re-schedule; run verify-deep |
| **Revoked** peer still running old work | Running task completes; **new** work not placed | Revoke is placement-only; drain peer before revoke in production |
| Concurrent **PATCH** status with wrong `leaseToken` | **409** on director | Expected; extend lease or reschedule |
| Trust test left only **untrusted** peers | **`placement.trust_policy_blocked`** | Re-register **Trusted** peer (verify scripts restore `mesh-lab-verify-peer`) |
| Revoked peer still in fleet with other **admitted** peers | Schedule still **200** on another peer | Governance tests isolate fleet; production: revoke + drain + verify elastic status |

There is **no distributed consensus** in the MVP director—last writer wins on status PATCH. Prefer short leases, **`migrate-for-checkpoint`** before moving work, and a single director URL in automation.

## Failure modes

| Symptom | Likely cause | Action |
|---------|----------------|--------|
| `peer-a not reachable` on host | Compose not up or wrong port | `docker compose … ps`; check `MESH_LAB_PEER_A_PUBLISH` |
| `peer-a container not running` in verify | Wrong `COMPOSE_PROJECT_NAME` | Export same project name for scripts and compose |
| Worker executor timeout | Executor disabled or director URL wrong | Check worker logs; `Ashlar__MeshLab__WorkerExecutor__*` env |
| `placement.trust_policy_blocked` | Only untrusted workers registered | Register a **Trusted** fleet node or relax `Ashlar__Mesh__Placement__PeerTrustPolicy` |
| `placement.peer_not_admitted` | Peer revoked on director | `POST …/admit` or `commercial mesh director CLI `admit <id>`` |
| Register **400** missing key | `RequirePeerRegistrationKey=true` | Set **`MESH_LAB_PEER_REGISTRATION_KEY`** / **`ASHLAR_MESH_PEER_REGISTRATION_KEY`** |
| Register **400** same as ApiKey | Policy rejects operator key as peer secret | Use a distinct registration secret |
| Stress `port is already allocated` on scale | Host port publish on workers | Stress uses `deploy/compose/docker-compose.mesh-lab-stress.override.yml` |
| Stress burst high `fail` rate | Workers still starting | Increase `MESH_LAB_STRESS_PAUSE_SEC`; check `docker logs` |
| Copilot quota verify fails | Shared tenant bucket | Restart worker container or use fresh `X-Ashlar-Tenant` |
| Director CLI verify skipped | No dotnet SDK on host | Install SDK or `MESH_LAB_SKIP_DIRECTOR_CLI_VERIFY=1` |
| Fleet/tasks empty after peer-a restart | Persistence not LiteDb or volume missing | Check `Ashlar__Mesh__Persistence__*` and `mesh_lab_peer_a_data` volume |
| Persistence verify skipped | Provider not LiteDb in env | Set `Ashlar__Mesh__Persistence__Provider=LiteDb` on peer-a |

## Network negative (automated)

[`scripts/mesh-lab-verify-network-negative.sh`](../../scripts/mesh-lab-verify-network-negative.sh) runs after trust checks in standard verify. Covers:

- Assigned peer with **unreachable** `apiBaseUrl` (director does not HTTP preflight workers)
- **Drained-only** fleet → schedule blocked
- **peer-b** `docker compose stop` while task is Running
- **peer-a** restart with persisted lease (LiteDB)

**Automated TLS:** `make mesh-lab-e2e-tls`, or **`mesh-lab-tls-gate.yml`** on manual dispatch — its weekly schedule was removed 2026-09-13 after four consecutive red runs. **gRPC:** **`grpc-transport-gate.yml`** / `make test-prime-time`.

**Tailscale / two physical hosts:** [`mesh-lab-verify-remote.sh`](../../scripts/mesh-lab-verify-remote.sh) run from a tailnet host (the `mesh-lab-remote-gate.yml` Actions wrapper was deleted 2026-08-16; it had never been dispatched). See [`MeshPhase12RemoteNetworkingAutomation.md`](../MeshPhase12RemoteNetworkingAutomation.md).

## Headless director CLI

```bash
export ASHLAR_MESH_DIRECTOR_BASE_URL=http://127.0.0.1:18081
export ASHLAR_MESH_API_KEY='…'
export ASHLAR_MESH_PEER_REGISTRATION_KEY='…'   # distinct from API key

dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director register worker-1 \
  --api-base-url http://peer-b:8080 --trust-tier Trusted --json

dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director revoke worker-1
dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director admit worker-1
```

See [`MeshPhase7EdgeAlignment.md`](../MeshPhase7EdgeAlignment.md).

## Cloud VM

```bash
./scripts/bootstrap-cloud-mesh-lab.sh --install-docker --workers --deep
```

Tunnel: `ssh -L 18081:127.0.0.1:18081 -L 18082:127.0.0.1:18082 user@vm`
