# Compose stacks

All Docker Compose deployment stacks live here. Run them **from the repository root** so CWD-relative arguments (like `--env-file`) resolve as documented:

```bash
docker compose -f deploy/compose/docker-compose.portal.yml up -d --build
```

Build contexts and repo bind mounts inside these files point at the repository root (`../..`; relative host paths resolve against the compose file's directory, not the CWD), so images build and mounts resolve identically regardless of the invocation directory. Compose v2 also loads `.env` from this directory (`deploy/compose/.env`), not the repo root — use `--env-file <path>` for a file kept elsewhere. Override files are passed as a second `-f` after their base file.

| Stack | Purpose | Docs / entry point |
|-------|---------|--------------------|
| `docker-compose.portal.yml` | Portal + API ("Try" lane) | `README.md` Quick Start |
| `docker-compose.agent-server.yml` | Self-hosted agent server | `docs/SelfHostedAgentServer.md` |
| `docker-compose.agent-server.local.yml` | Override: local agent-set mounts for the agent server | `docs/SelfHostedAgentServer.md` |
| `docker-compose.mesh-lab.yml` | Mesh virtual lab (peers + workers) | `docs/MeshVirtualLab.md` |
| `docker-compose.mesh-lab-tls.override.yml` | Override: TLS fronting (Caddy) for the mesh lab | `docs/MeshVirtualLab.md` |
| `docker-compose.mesh-lab-stress.override.yml` | Override: stress-test profile for the mesh lab | `docs/MeshVirtualLab.md` |
| `docker-compose.friend-mesh.yml` | Friend-mesh prefab | `docs/FriendMeshPrefab.md` |
| `docker-compose.private-mesh-demo.yml` | Private-mesh demo: three nodes share one signed package (run it with `scripts/demo/private-mesh-demo.sh`) | `docs/PrivateMeshDemo.md` |
| `docker-compose.ollama.yml` | Local Ollama model runtime | `docs/Configuration.md` |
| `docker-compose.ollama.gpu.override.yml` | Override: GPU acceleration for Ollama | `docs/Configuration.md` |
| `docker-compose.cloud-multi-tenant.yml` | Cloud multi-tenant deployment shape | `docs/DistributionModels.md` |
| `docker-compose.private-single-tenant.yml` | Private single-tenant deployment shape | `docs/DistributionModels.md` |
| `docker-compose.ephemeral.yml` | Disposable Ollama (+ optional Postgres, `--profile db`) plus a `ashlar` CLI service built from `.docker/Dockerfile.cli` (`run --rm ashlar ...`) | `docs/DistributionModels.md` |
| `docker-compose.test.yml` | Cached test-runner image | `.docker/Dockerfile.test-caching` |

> **Looking for a node to deploy?** None of these is one. `deploy/node.yml` is THE node — pinned by digest, state on a named volume, restart-durable. The stacks here are labs, demos and development stacks.
