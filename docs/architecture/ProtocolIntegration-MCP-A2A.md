# Protocol Integration: MCP and A2A

Ashlar speaks the two industry-standard agent protocols through Tier-2 adapter projects:

- **MCP (Model Context Protocol)** — AI clients (Claude, IDEs, other agents) discover and call
  tools. Ashlar acts as an **MCP server** (exposing allowlisted Ashlar tools) and as an
  **MCP client** (consuming external MCP servers' tools as `ITool`s).
- **A2A (Agent2Agent)** — peer agents discover each other via agent cards and delegate tasks.
  Ashlar acts as an **A2A server** (external agents delegate to allowlisted Ashlar agents) and as
  an **A2A client** (`A2AAgentTransport` behind the existing endpoint routing).

**Status (2026-08-16):** all four directions and the `Ashlar.API` wiring have landed (PRs #268,
#266, #269, #270): `Program.cs` registers `AddAshlarMcpServer(...).WithHttpTransport()`,
`AddAshlarMcpClient`, `AddAshlarA2AServer`, `AddAshlarA2ATransport` and maps `/api/mcp` and
`/api/a2a/{agentId}` behind the API auth middleware and per-IP rate limits, with `IngressCatalog`
rows `McpServer` and `A2AAgents`. Every surface is still **feature-flagged off by default**
(`Ashlar:Mcp:*`, `Ashlar:A2A:*` in `application/src/Ashlar.API/appsettings.json`). What remains
deferred is listed at the end of this document.

Both protocols use the official SDKs (`ModelContextProtocol` 2.x, stable; `A2A` 1.0 preview) —
the hand-rolled JSON-RPC endpoint in the commercial Game Director vertical predates this layer
and is unaffected.

## Design rules (all phases)

1. **Kernel spine stays clean.** `Ashlar.Abstractions`, `Ashlar.Core.*`, `Ashlar.Contracts`, and
   `Ashlar.Hosting` never reference protocol SDK packages. Hosts opt in by calling the
   `AddAshlar*` extensions from the adapter projects (same pattern as
   `Ashlar.Transport.Grpc.Server.Host`). This also keeps preview packages out of the
   `Ashlar.Hosting` NuGet pack graph.
2. **Fail-closed everywhere.** Every surface has `Enabled=false` defaults, explicit allowlists
   that default to empty, `ValidateOnStart` options validation, and a hard refusal to enable
   MCP **client** and A2A (client and server) under `AirGapped` **and** `SecureWorkstation`.
   MCP **server** is refused under `AirGapped`; under `SecureWorkstation` it is refused over HTTP
   (`.WithHttpTransport()`, SPEC-007 PR 4.10) and local IDE stdio stays allowed. Validators honor the profile `AddAshlar` recorded, not only the
   `ASHLAR_DEPLOYMENT_PROFILE` env var.
3. **Existing seams, not parallel plumbing.** Tools flow through `ITool`/`ToolSchema`
   (`src/Ashlar.Abstractions`), agents will flow through `IAgentTransport` + endpoint routing,
   and HTTP exposure lands behind the Ashlar API middleware chain under `/api/...`.

## Phase 1 (this phase): Ashlar as MCP server

Projects:

| Project | Role |
|---|---|
| `src/Ashlar.Mcp.Server/` | Protocol-agnostic bridge: catalog, policy gate, handlers over the official SDK |
| `src/Ashlar.Mcp.Server.Host/` | Standalone **stdio** host for local AI clients (Claude Desktop, Claude Code, IDEs) |
| `src/Ashlar.Mcp.Server.Tests/` | Unit tests |

### How exposure works

Tools become callable over MCP only when **both** are true:

1. The host registers the tool in DI: `services.AddSingleton<ITool, RepoFsReadTool>();`
2. The operator allowlists its id: `Ashlar__Mcp__Server__ExposedToolIds__0=repo.fs.read`

Every `tools/call` then flows through `AshlarMcpToolBridge`, which applies in order:

1. **Catalog routing** — only allowlisted tools exist; unknown names are JSON-RPC
   `invalid params` protocol errors.
2. **Argument overrides** — operator-pinned arguments replace caller-supplied ones
   (`Ashlar__Mcp__Server__ArgumentOverrides__repo.fs.read__root=/srv/repo`). Use this for the
   `repo.fs.*` tools, whose `root` argument would otherwise let a remote caller point them at an
   arbitrary directory.
3. **Policy gate** — `IMcpInvocationGate` (default: every DI-registered `IPolicy` must approve,
   evaluated against the canonical tool id, mirroring `AgentHost`'s approve+audit pair, which MCP
   traffic otherwise bypasses). Denials come back as `isError` tool results.
4. **Concurrency ceiling** — `MaxConcurrentToolCalls` (default 8); Ashlar tools were written for a
   single agent loop and are not assumed reentrant.
5. **Audit logging** — caller principal, canonical id, duration, and outcome.

Startup validation (`McpCatalogStartupValidator`) fails the host boot when the surface is enabled
but the catalog is invalid: allowlisted id with no registered tool, an input schema that is not a
JSON object schema, or two ids collapsing to the same sanitized MCP name (`repo.fs.read` →
`repo_fs_read`; MCP-strict clients reject dots).

### Running the stdio host

```bash
dotnet run --project src/Ashlar.Mcp.Server.Host -- \
  Ashlar__Mcp__Server__ExposedToolIds__0=repo.fs.read \
  Ashlar__Mcp__Server__ExposedToolIds__1=repo.fs.list \
  Ashlar__Mcp__Server__RepoRoot=/srv/repo
```

(Values can equally come from environment variables or `appsettings.json`; `Enabled` defaults to
`true` in this host only — running the binary is the opt-in — and all logging goes to stderr
because stdout carries the protocol stream.)

Claude Code registration example:

```bash
claude mcp add ashlar -- dotnet run --project src/Ashlar.Mcp.Server.Host
```

The host pre-registers only the read-only repo tools (`repo.fs.read`, `repo.fs.list`) for
allowlisting; mutating tools (`repo.fs.write`, `repo.git.commit`, `dotnet.run`, …) must be added
by the operator explicitly in their own host.

### HTTP exposure

`AddAshlarMcpServer(configuration).WithHttpTransport()` + `MapAshlarMcpEndpoint()` (defaults to
`/api/mcp`, streamable HTTP). `Ashlar.API` calls both (see "Ashlar.API wiring" below); the
mapping helper deliberately refuses the commercial endpoint's `AllowAnonymous()` pattern and maps
nothing at all while disabled.

## Phase 2 (this phase): Ashlar as MCP client

Projects: `src/Ashlar.Mcp.Client/` (+ `src/Ashlar.Mcp.Client.Tests/`, whose round-trip suite runs a
real `McpClient` against the Phase-1 server bridge over in-memory pipes).

External MCP servers are configured under `Ashlar:Mcp:Client` (streamable HTTP only in v1) and
their tools surface as `ITool`s through the `IToolSource` seam:

1. **Connect + pin at startup.** `McpClientConnectionManager` (a hosted service) dials each
   configured server, lists tools (optionally narrowed by a per-server `AllowedTools` list), and
   pins each definition — name, description, raw JSON schema — for the process lifetime.
2. **Namespaced ids.** Proxies register as `mcp:{server}:{tool}`, so a remote server can never
   shadow a native tool id (`CapabilityRegistry` registration is last-wins by id).
3. **Drift faults, never follows.** A periodic re-list compares against the pins; a changed or
   vanished definition marks the tool faulted (withdrawn from toolboxes, calls fail with the
   reason) until a restart re-pins. Remote servers do not get to redefine an agent's tools
   mid-flight.
4. **Failures degrade, not crash.** An unreachable server contributes zero tools; remote
   `isError` results and transport failures come back as error payloads (repo tool convention),
   visible to the calling model.
5. **Secrets via environment.** Per-server API keys are named by env var
   (`ApiKeyHeader`/`ApiKeyEnvVar` pair); a referenced-but-unset variable fails startup.

Toolboxes pick proxies up through `Ashlar.Abstractions.IToolSource`:
`RepoFsToolboxFactory.CreateMinimal/CreateWithBuildTest` accept `extraTools`, and
`SelfExtendRunnerAdapter` folds all DI-registered `IToolSource`s in per cycle. Hosts wire it with
`services.AddAshlarMcpClient(configuration)`.

```bash
Ashlar__Mcp__Client__Enabled=true
Ashlar__Mcp__Client__Servers__0__Name=github
Ashlar__Mcp__Client__Servers__0__Url=https://mcp.example.com/mcp
Ashlar__Mcp__Client__Servers__0__ApiKeyHeader=Authorization
Ashlar__Mcp__Client__Servers__0__ApiKeyEnvVar=GITHUB_MCP_TOKEN
Ashlar__Mcp__Client__Servers__0__AllowedTools__0=search_issues
```

## Phase 3 (this phase): A2A server core + client transport

Projects: `src/Ashlar.Transport.A2A/` (client transport), `src/Ashlar.Transport.A2A.Server/`
(server core), test satellites for both (the server suite drives the real client transport
through the really-mapped endpoints over an ASP.NET TestServer). Built on the official `A2A` +
`A2A.AspNetCore` SDK (1.0.0-preview2, A2A v1.0 spec) — both projects are `IsPackable=false`
until a stable SDK ships.

### A2A client (`A2AAgentTransport : IAgentTransport`)

Peers are ordinary routing endpoints with an **`a2a+` scheme prefix**
(`a2a+https://peer.example.com/api/a2a/agent`): capability routing, health filtering, and
barrier levels on `EndpointDescriptor` work unchanged, and no kernel public API moved. The
runtime's `AddAshlarRuntimeTransport` now wraps the remote side in a scheme-dispatching composite
whenever `AgentTransportSchemeRegistration`s exist in DI — with none registered the composition
is byte-for-byte the old gRPC-only behavior. Hosts opt in with
`services.AddAshlarA2ATransport(configuration)` **before** `AddAshlar()`
(`Ashlar:A2A:Transport:Enabled=true`; refused under AirGapped and SecureWorkstation).

Correlation, span, and ambient barrier context propagate as protocol metadata
(`ashlar.correlationId`, `ashlar.barrier`, …) on both the message and the request — the A2A
counterpart of the gRPC transport's `x-ashlar-*` headers, with the same audit-log event. Remote
task states map to typed results (`a2a.task.failed`, `a2a.timeout`, …); transport-level
`HttpRequestException`s honor `MaxRetries`, task-level failures are never retried. Per-endpoint
API keys are env-var named (`Ashlar:A2A:Transport:Endpoints:0:ApiKeyHeader/ApiKeyEnvVar`).

### A2A server (`src/Ashlar.Transport.A2A.Server/`)

- **Exposure**: explicit `ExposedAgentIds` allowlist, plus opt-in
  `ExposeByCoordinationProtocol` for agents whose domain card declares the `a2a` coordination
  protocol. Deny-by-default; enabled-with-zero-agents refuses to boot.
- **Execution**: `AshlarA2AAgentHandler` deliberately mirrors the gRPC facade — agent identity is
  fixed at construction from the allowlisted descriptor and execution flows through
  `IAgentTransport.SendAsync` with a bounded budget (`AgentConstraints.MaxExecutionTime`, else
  `DefaultExecutionTimeout`). It never touches the reflection-scanning
  `RunAgentCommand`/`AgentExecutorAdapter` path.
- **Cards**: `IAshlarA2ACardProjector` builds spec cards (behaviors → skills,
  `streaming=false, pushNotifications=false` in the synchronous v1). The domain's own
  `Ashlar.Core.Domain.Agents.AgentCard` is untouched; the colliding spec type never leaves the
  adapter. Hosts implement `IAshlarA2AAgentCatalog` over their agent registry (the adapter cannot
  reference the domain model per transport layering rules).
- **Mapping**: `MapAshlarA2AEndpoints()` maps, per exposed agent, a JSON-RPC endpoint + card at
  `/api/a2a/{agentId}`, plus the **primary** agent's card at `/.well-known/agent-card.json`
  (explicit `PrimaryAgentId` required when several agents are exposed). RPC and card endpoint
  builders are returned separately so hosts can auth-gate them differently
  (`AllowAnonymousAgentCard` is the public-discovery opt-out, default off).
- **Tasks**: synchronous terminal tasks in an in-memory per-agent store — `tasks/get` works
  within the process lifetime; durable tasks/streaming are deferred.

## Ashlar.API wiring (landed — PR #269, ProdStyle coverage in #270)

- `/api/mcp` + `/api/a2a/{agentId}` (+ `/.well-known/agent-card.json` for the primary agent) are
  mapped by `application/src/Ashlar.API/Program.cs` behind `AshlarApiKeyAuthMiddleware` (all verbs)
  and the `ashlar-mcp` / `ashlar-a2a` per-IP rate-limit policies; nothing is mapped while a surface
  is disabled.
- `IngressCatalog` rows `McpServer` and `A2AAgents`; per-tenant capability allowlists via
  `Ashlar:MiddlewareIngress:TenantCapabilityAllowlists`; `AgentRegistryA2ACatalog` is the
  `IAshlarA2AAgentCatalog` adapter over `IAgentRegistry`.
- Compose stacks under `deploy/compose/` set the MCP/A2A ingress explicitly off.

## Deliberately deferred

Durable A2A tasks + SSE streaming + push notifications; MCP client stdio child processes
(needs a command allowlist design); dynamic tool-set mutation (restart to pick up remote tool
changes); MEAI `AIFunction` bridging; mesh `ICapabilityAdvertisement`-based A2A peer discovery;
migration of the commercial Game Director MCP endpoint onto this layer (breaking for its
clients; needs consumer sign-off).
