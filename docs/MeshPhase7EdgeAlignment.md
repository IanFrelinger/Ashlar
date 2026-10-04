# Phase 7 — Edge and headless alignment (MVP)

Phase 7 closes a practical gap from the Phase 0 **capability matrix**: **PC / Mac / Linux headless workers** and automation can participate in the **HTTP mesh director** without hosting **`Ashlar.API`** in-process. **Lite / MAUI** remain thin clients; they should call the same director URLs from app code using the same headers.

**Depends on:** mesh director HTTP surface on **`Ashlar.API`** (`/api/mesh/*`), mesh security headers (`Ashlar:Security:Mesh`), and correlation id propagation where enabled.

## CLI: commercial mesh director

Subcommands **`get`**, **`post`**, **`patch`**, **`register`**, **`admit`**, and **`revoke`** perform HTTP calls against a director base URL. From a source checkout, use:

```bash
dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director <subcommand>
```

### Environment variables

| Variable | Purpose |
|----------|---------|
| **`ASHLAR_MESH_DIRECTOR_BASE_URL`** | Director root (e.g. `https://ashlar-hub.tailnet:8080`) — used when `--base-url` is omitted |
| **`ASHLAR_MESH_API_KEY`** | Optional **`X-Ashlar-Api-Key`** when the API is configured with built-in API key auth |
| **`ASHLAR_MESH_MUTATING_TOKEN`** | Optional **`X-Ashlar-Mesh-Token`** for mutating **`/api/mesh`** requests (see Phase 2) |
| **`ASHLAR_MESH_PEER_REGISTRATION_KEY`** | Per-peer fleet registration secret when the director requires **`peerRegistrationKey`** (must differ from operator API key) |

### Examples

```bash
export ASHLAR_MESH_DIRECTOR_BASE_URL=https://director.example:8080
export ASHLAR_MESH_MUTATING_TOKEN=your-long-secret

# List fleet nodes (GET is non-mutating for mesh security; token optional)
dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director get /api/mesh/fleet/nodes --json

# Register this host as a worker (includes peerRegistrationKey when director policy requires it)
export ASHLAR_MESH_PEER_REGISTRATION_KEY='long-secret-not-the-api-key'
dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director register worker-01 \
  --api-base-url https://worker-01:8080/ \
  --trust-tier Trusted

# Revoke / admit placement eligibility (Product 5.2 director governance)
dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director revoke worker-01
dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director admit worker-01

# Heartbeat with queue depth for elastic placement
dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director post /api/mesh/fleet/nodes/worker-01/heartbeat --body '{"queueDepth":2}'
```

Use **`--base-url`**, **`--api-key`**, and **`--mesh-token`** to override env for one-off runs. **`--body-file`** accepts a path for larger JSON payloads.

### Exit codes

The CLI exits **0** when the HTTP status is success (2xx), **1** otherwise (network errors or non-2xx).

## Edge / mobile

- **Thin clients / MAUI** (`Ashlar.Client`; the `Ashlar.Lite` wrapper was parked 2026-10-03): Use your HTTP client of choice against **`{director}/api/mesh/*`**, sending **`X-Ashlar-Correlation-Id`** (Phase 3), **`X-Ashlar-Mesh-Token`** on mutating mesh calls (Phase 2), and **`X-Ashlar-Api-Key`** if the hub requires it.
- **Headless CI:** Same as CLI — script **`dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director ...`** or `curl` with the same headers.

## Limitations

- No generated C# client or OpenAPI codegen in this phase — raw paths only.
- **GET** mesh routes still go through mesh security middleware on the server; if your deployment requires a mesh token for reads, extend env usage accordingly (today Phase 2 gates **mutating** mesh verbs).

## Revision history

| Date | Change |
|------|--------|
| 2026-04-22 | Initial Phase 7: commercial mesh director HTTP helper + env contract. |
| 2026-05-19 | `register` / `admit` / `revoke` + `ASHLAR_MESH_PEER_REGISTRATION_KEY`; lab verify via `mesh-lab-verify-director-cli.sh`. |
