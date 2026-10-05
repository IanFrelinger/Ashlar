# SPEC-007 PR 4 design pass: brief

The owner asked for a **design pass for PR 4**. Its output is decisions the owner makes before any code. This brief is shared by every agent in the pass.

`$SP` = `/tmp/claude-0/-home-user-Ashlar/119c0f0e-4179-5553-a59e-a4fbe7cfca88/scratchpad`

## Code

- **Read-only clone of master at `8ec674d2a`:** `$SP/pr4-base`. That commit is PR 3b, merged.
  - Read and grep only. Never edit it. Never run `dotnet` on the host.
- **If a claim needs a build or test** to settle, say so. Do not run one; this pass is design only.
- **Cite `file:line`** at `8ec674d2a` for every claim about the code.

## Where SPEC-007 stands

`docs/specs/SPEC-007-security-labels-and-reference-monitor.md` is the owner's spec, verbatim, plus marked additions.

**Merged:**
- PR 1: the label lattice and `ReferenceMonitor` (`src/Ashlar.Abstractions/Security/`).
- PR 2: bridges from the existing labels.
- PR 3a: the report-only egress guard, the inventory, and the convention test.
- PR 3b: every listed non-exempt outbound site routed to the guard, report-only.

**PR 4, owner's text (§5):** "Guard enforces. Switched on per deployment profile; `AirGapped` and `SecureWorkstation` enforce by default." Done when: "A seeded leak test (an agent tries to write labelled data down) fails closed with an explained refusal."

**PR 5 (next):** "Clearances on subjects. Agents get a clearance; a sealed skill declares its highest level in the package manifest." Done when: "Composing an agent with a skill above its clearance is refused."

**§7 rules:**
- Refusals are explained, never silent.
- Fail closed: when a label is missing or cannot be parsed, treat it as `SystemHigh`.
- A MUST is enforced only where it names a passing test.

**Owner questions:**
- §8 Q1–Q6.
- Open questions A–E, under "Open questions raised since §8":
  - A: a receiver-side helper and the shape of `TryParse`;
  - B: SystemHigh vs TopSecret visibility;
  - C: caveat scope; the owner deferred it, and it is due before PR 4;
  - D: redacting refusal details;
  - E: numbering.
- From 3a, owner Q1: the guard handler sits on every `IHttpClientFactory` client, host-app clients included. **PR 4 decides whether host-app clients are enforced.**

## The guard as merged

- `IEgressGuard.Evaluate(EgressRequest)` returns an `EgressDecision`. It never throws; a fault is recorded as `Fault` with `NoDecision`.
- **Destination table:** host (loopback, unix, npipe, `host:`) = SystemHigh; `model.*` = Internal; `web-search` = Confidential; network exports = Internal; unknown = Public.
- **Current label:** the `EgressSubject` AsyncLocal frame, or SystemHigh with basis `no-subject`.
- **Decision:** `ReferenceMonitor.CanWrite(current, destination)`.
- **Decisions go to** the `Ashlar-Egress` EventSource and to `EgressDecisionLog` sinks. `AddAshlarEgressGuard` adds an ILogger sink at Debug.
- **Routes:**
  - `EgressHttp` / `EgressGuardHandler`: raw clients, and the factory handler via `ConfigureHttpClientDefaults`;
  - `EgressGuardChatClient`: MEAI, outermost in `UseAshlarGovernance`;
  - explicit `_ = EgressGuard.ProcessDefault.Evaluate(...)` before each primitive, for mesh, file, process, socket, telemetry, Bing and the Ollama proposer.
- **Records:** `docs/EgressInventory.md`, `ci/egress-inventory.tsv` (85 rows) and `ci/cert-gate-assertions.md` row 64.

## Known gaps PR 4 must close before it enforces

All are recorded in `docs/EgressInventory.md` "Known limits" and in SPEC-007 "Gaps carried to PR 4".

1. **Synchronous `Send`.** The netstandard2.0 asset of `Ashlar.Abstractions` does not evaluate a synchronous `Send`. .NET 5–7 apps resolve that asset.
2. **Redirects.** Redirects that the primary handler follows are not evaluated. On a 307 or 308 the body is re-sent to the new host.
3. **Records that can read Host for a remote peer:**
   - EG-MESH-03 behind a local proxy or tunnel, or with forwarded headers;
   - EG-MDL-01 with a custom `local:` inner client; `local:onnx` is not recorded at all;
   - EG-MESH-07/08 with a `//127.0.0.1/…` path.
4. **No subject frames.** Nothing in production pushes an `EgressSubject` frame today. Verify this. If enforcement only turns on, every decision about a destination outside the host is a refusal (`SystemHighData`), because no-subject means SystemHigh. AirGapped and SecureWorkstation would then refuse all outbound traffic.

## Related defects from the 3a audit

These are static readings; verify each.

1. **gRPC open relay:** `AgentTransportServiceImpl.cs:173` forwards the caller's `TargetEndpoint`.
2. **Commercial task-result download:** possibly an arbitrary file read (`CommercialFleetEndpoints.cs:495-499`).
3. **MCP HTTP server allowed under SecureWorkstation:** `ValidateAshlarMcpServerOptions.cs:31` checks only AirGapped, against `AshlarDeploymentProfileEnvironment.cs:50-55`.
4. **Kernel MEAI cloud allow-list looks dead:** `Phases.cs:415` vs `MeaiPipelineServiceCollectionExtensions.cs:96/185`.
5. **AirGapped still registers network paths:** RunPod, the peer executor, federated remote bricks and the ollama.com catalog (`Enabled=true`). **The owner asked to fold this one into PR 4.**
6. **`AdaptiveProviderFactory` escalates silently** from local to cloud (`AdaptiveProviderFactory.cs:40-58`).
7. **`RemoteBrick` posts to a host the catalog chooses.**
8. **Agents' `RequireLocalOnly` / `BlockExternalLLMs` are never checked** against their `ModelProvider`, and `mcp:` tool ids pass `DataExfiltrationPolicy`.

## Repository rules for any later code

- Build and test only through `scripts/test-in-container.sh`.
- Mutation-check every behavioural change (`scripts/mutation-check.sh`).
- A new blocking convention test needs its row in `ci/cert-gate-assertions.md`.
- `[coordinated-integration]` for non-test changes under `application/`.
- Five required checks.
- Readiness lanes are diff-conditional.

## Prior art in the scratchpad

- `$SP/pr3a/` holds `guard_design.md`, `risks.md`, `SCOPE-3b.md` and `SCOPE-3b-wave2.md`.
- `$SP/pr3b-final-review.json` holds the final review's findings, including redirects and the Host-reading records.
