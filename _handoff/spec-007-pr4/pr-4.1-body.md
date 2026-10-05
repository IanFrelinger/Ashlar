## Summary

SPEC-007 PR 4.1 closes the third gap PR 3b carried to PR 4: **egress records that could read Host for a remote peer** (design §2.6 gap 3, default D34). It is still report-only. Nothing refuses; records change, and one HTTP client stops following redirects.

- **A `file:` destination is never Host.** A destination name that starts with `file:` (any case) is a path: it is recorded as written and never read as a URL. A `file` URI is never inside the host boundary either. Before, `file:` plus a path spelled `//127.0.0.1/…` parsed as a URL with a loopback host and was recorded as Host (EG-MESH-07/08, and any other file site).
- **Mesh serve records `mesh-peer:<ip>`** (`mesh-peer:unknown` with no address). The name has no `://`, so the class is the family's, a network export, whatever the IP. A loopback puller was Host, but a local TLS terminator, `ssh -R` or a localhost tunnel delivers every remote puller as loopback, and forwarded headers can set the address (EG-MESH-03).
- **The MEAI layer records where the inner client dials** (EG-MDL-01/02). The destination is the inner client's `ChatClientMetadata.ProviderUri` when absolute; `local:onnx` records nothing only when the inner client is the in-process `LlamaSharpChatClient` (a type check); Bedrock keeps its region reconstruction; anything else fails closed to `meai:<key>`, an external model. Before, the key decided: a custom `local:ollama` client was recorded as the resolved Ollama URL (Host by default), and a custom `local:onnx` client was not recorded at all.
- **Ollama cloud models (D34, unconditional).** A model id that ends in `-cloud` or `:cloud` (any case) is recorded as an external model at `https://ollama.com`, because the local daemon relays it there: in `EgressGuardChatClient` (the call's `ChatOptions.ModelId`, or the inner client's default model) and in `OllamaProvider` (one more EG-MDL-07 decision before the chat).
- **Behaviour change: the default MEAI `OllamaHttpChatClient` no longer follows redirects.** Its record names only the first hop; a 307 or 308 would re-send the whole conversation to wherever `Location` points, unrecorded. A 3xx now fails the call. CHANGELOG `### Changed`.

The production compositions record what they did: the default inner clients report the same destinations as before, and `local:onnx`'s default LLamaSharp client still records nothing.

`[coordinated-integration]`: this PR changes non-test code under `application/` (`Ashlar.CLI`'s `MeshServeService.PeerDestination`) together with `src/`. The CLI twin that pins the new peer name (`EgressCliSiteTwinTests`, in `application/src/Ashlar.Tests.CLI`) and the classifier rows in the cert-gate project (`EgressExplicitSiteTwinTests`) describe the same record; splitting them would leave one side asserting a name the other no longer produces.

These are classification-style controls inside the runtime. They are not an accredited cross-domain solution.

## Changes

- **Classifier** (`src/Ashlar.Abstractions/Security/Egress/EgressDestinations.cs`): `DescribeName` returns a `file:` name (ordinal, ignore case) bounded and as written, never URL-parsed, never Host; `DescribeUri` puts no `file` URI inside the host boundary. The remarks of `EgressDestinations` and `EgressRequest` say so. No public API change (`PublicAPI.Unshipped.txt` untouched).
- **Mesh serve** (`application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshServeService.cs`): `PeerDestination` returns `mesh-peer:<ip>` (IPv4-mapped as IPv4) or `mesh-peer:unknown`.
- **MEAI** (`src/Ashlar.AI.Pipeline/`):
  - `MeaiEgressDestination.Resolve(targetKey, services, inner)`: the four ordered rules above; it never throws and a fault gives `meai:<key>`. `UseAshlarGovernance` passes the inner client.
  - `InProcessChatClient.IsLlamaSharp` (internal, in `LlamaSharpChatClient.cs`): the type check. It lives in that file because the F5 convention confines the `LlamaSharpChatClient` name to its own file and the governed registrations.
  - `EgressGuardChatClient`: the Ollama cloud rule, with the default model read once from the inner client's metadata (never throwing).
  - `OllamaHttpChatClient(IOptions<…>)`: `new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)`.
- **Legacy provider** (`src/Ashlar.Infrastructure/Execution/Ollama/OllamaProvider.cs`): before a chat with a cloud model (requested or resolved name), `EgressGuard.ProcessDefault.Evaluate(new EgressRequest(model.legacy, "EG-MDL-07", https://ollama.com))`. Its guard handler still records the send to the daemon.
- **Records:**
  - `docs/EgressInventory.md`: the "Records that can read Host for a remote peer" Known-limits bullets are removed; a "Loopback is inside the host boundary only for a service that does not relay" bullet replaces them (D40's loopback half: OTLP collector, docker daemon, `kubectl proxy`, local LLM gateways; the cloud rule and where it applies; EG-MDL-11 does not); the EG-MDL-01, EG-MDL-07, EG-MESH-03, EG-MESH-07 and EG-MESH-08 Route cells; the redirect bullet names the Ollama client; line numbers re-derived for the files this PR edits.
  - `ci/egress-inventory.tsv`: `OllamaHttpChatClient.cs` `http.new` 1 → 2 (the new `HttpClientHandler`), note updated. 85 rows, 149 occurrences (was 148), 48 guarded. No other pin moves.
  - `ci/cert-gate-assertions.md` row 64: the classifier's `file` rule, the MEAI destination rule and cloud rule, the redirect-off client, `OllamaProvider`'s cloud decision, the file doors' `//127.0.0.1` twins, and the occurrence count.
  - `docs/governed-pipeline.md`, `CHANGELOG.md` (`### Changed`), and one SPEC-007 status line for PR 4.1.
- **Tests:** listed under Testing.

## Behaviour changes, and what stays report-only

| Change | Before | After | Report-only? |
|---|---|---|---|
| Default `OllamaHttpChatClient` and a 3xx | Followed (307/308 re-send the body) | Returned; `EnsureSuccessStatusCode` throws `HttpRequestException` | **No: a behaviour change** (CHANGELOG). An Ollama behind a redirecting proxy needs its final URL. The `HttpClient` constructor is unchanged. |
| `file:` name / `file` URI with a loopback host | Host (`SystemHigh`), recorded `file://127.0.0.1` | Family's class (network export), recorded as written | Yes |
| Mesh serve peer | `tcp://<ip>`; loopback = Host | `mesh-peer:<ip>`; network export | Yes |
| MEAI custom inner client under `local:ollama` | Resolved Ollama URL | Its `ProviderUri`, else `meai:local:ollama` | Yes |
| MEAI custom inner client under `local:onnx` | No decision | Its `ProviderUri`, else `meai:local:onnx` | Yes (one more decision per call) |
| Ollama `-cloud`/`:cloud` model | Recorded at the daemon (Host when local) | External model at `https://ollama.com` (MEAI: instead of the daemon; `OllamaProvider`: one more decision) | Yes |

Under the switch (4.11), every mesh serve on AirGapped or SecureWorkstation is refused until the served package's label becomes the subject, because the peer is no longer Host. That is fail-closed and was always the intent; this PR makes it visible rather than causing it.

## Testing

All builds and tests ran in the Linux devtest container through `scripts/test-in-container.sh`. Nothing ran on the host.

TESTING_TABLE

### Red at base

The twins were committed alone first (TWINS_SHA, on master `79e988c`), run, then the change was committed.

RED_BASE

### Mutation checks

Every row was run with `scripts/mutation-check.sh` (commit, apply one replacement, prove it applied, red, restore, empty `git status --porcelain`, green) in the container.

MUTATION_TABLE

### Testing strategy (blast radius)

See [Testing strategy pivot v1](docs/architecture/TestingStrategyPivot-v1.md). This PR changes a classifier rule in `Ashlar.Abstractions`, the MEAI governance layer, the legacy Ollama provider and one CLI record, so it ran:
- [x] Focused tests in every touched area: the full cert-gate, `Ashlar.Tests.AI.Pipeline` on net8.0 and net10.0, and the CLI's egress twins on net10.0.
- [x] `Ashlar.Abstractions` built for netstandard2.0, net8.0 and net10.0.
- [ ] `make kernel-coverage-gate`, `make kernel-gate`, `make test-prod-style`: not run locally; CI runs them.

## Checklist

- [x] Repo gates pass (`scripts/ci/run-repo-gates.sh`), the knowledge-graph byte-compare included.
- [x] Documentation updated: `docs/EgressInventory.md`, `docs/governed-pipeline.md`, the records, SPEC-007 and the CHANGELOG.
- [x] No `TODO` or `NotImplementedException`.
- [x] Behaviour change documented: the Ollama client's redirects (CHANGELOG `### Changed`).

## Release

- [x] Not a versioned release; skip.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01GYPVuHoik1sH5U6gWtSVRT
