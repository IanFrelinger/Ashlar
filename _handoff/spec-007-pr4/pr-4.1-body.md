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

All builds and tests ran in the Linux devtest container through `scripts/test-in-container.sh`. Nothing ran on the host. The branch is one commit on master `79e988c` (#713).

| Check | Result |
|---|---|
| Full cert-gate (`scripts/run-cert-gate.sh`, net8.0) at `4e170e2` | **2589/2589**, 0 skipped (the skip guard matches `scripts/cert-gate-skipped.baseline`; the zero-test guard expected ≥2584 from `--list-tests`) |
| The five touched cert-gate classes on **net10.0** at `4e170e2` (`EgressGuardDecisionTests`, `EgressExplicitSiteTwinTests`, `EgressGuardChatClientTwinTests`, `EgressRawClientTwinTests`, `EgressGuardConventionTests`) | **448/448** |
| `EgressGuardConventionTests` (net8.0) at the final head `1e3e645` (a one-line doc change after `4e170e2`) | **194/194**: the TSV pins (85 rows, 149 occurrences, 48 guarded), F5 confinement with the new `InProcessChatClient` helper, F8 ids |
| `Ashlar.Tests.CLI` (net10.0), filter `EgressCliSiteTwinTests|MeshServe|MeshLanParty`, at `4e170e2` | **36/36** |
| `Ashlar.Tests.AI.Pipeline`, whole project, at `4e170e2` | **net8.0 107/107**, **net10.0 107/107** (unchanged from master's 107) |
| `Ashlar.Abstractions` (netstandard2.0, net8.0, net10.0; `TreatWarningsAsErrors`, `AnalysisMode=All`, PublicAPI analyzers) | 0 warnings, 0 errors; no public API change |
| `scripts/ci/run-repo-gates.sh` at `1e3e645` (after `git add -A` and the knowledge-graph rebuild) | **25/25** |

**Renamed and rewritten tests (the flips, by name).**
- CLI (`application/src/Ashlar.Tests.CLI/Tests/Commands/EgressCliSiteTwinTests.cs`), red to green as the design names:
  - `PeerDestination_writes_the_peer_as_a_URL_host` → `PeerDestination_names_the_peer_so_it_is_never_inside_the_host_boundary` (rows now `mesh-peer:…`, plus `::ffff:127.0.0.1`, and each row's decision is a network export);
  - `A_package_served_to_a_loopback_peer_records_one_Host_decision_and_a_404_records_none` → `…_records_one_NetworkExport_decision_and_a_404_records_none` (`mesh-peer:127.0.0.1`, NetworkExport).
- Classifier rows in `EgressExplicitSiteTwinTests.Every_destination_shape_the_explicit_sites_pass_is_classified_as_expected`: the six `tcp://` mesh-serve rows are **rewritten** to five `mesh-peer:` rows (the `tcp://::ffff:10.0.0.5` row, which explained the IPv4-mapped mapping for URL parsing, has no counterpart: a `mesh-peer:` name is never parsed). Those rows are green at the base too (a `mesh-peer:` name never parsed as a URL), so they are **not** claimed as red-to-green. The two new `file://` rows in the same theory are red at the base.
- MEAI (`EgressGuardChatClientTwinTests`): the twins that put `FakeChatClient` (no `ProviderUri`) under `local:ollama` and expected the resolver URL now build the default client's shape over a stub handler (`StubOllama`: `OllamaHttpChatClient` with exactly the base address the default factory gives it), so the resolver-precedence assertions stay (`LocalOllama_IsClassifiedByTheResolvedUrl`, `LocalOllama_RecordsTheEnvironmentUrl_ThatTheDefaultClientDials`, which still compares with the real default client, `Streaming_…`, `ResolutionFaults_…`). `AGovernedTargetWithoutThePipelineOptions_StillResolvesTheOllamaEndpoint` became `LocalOllama_WithACustomInnerClientThatNamesNoUri_FailsClosedToItsKey`; `LocalOnnx_RecordsNoDecision` became `LocalOnnx_WithTheLlamaSharpClient_RecordsNoDecision` (default and host-registered LLamaSharp) and `LocalOnnx_WithACustomInnerClientThatNamesNoUri_FailsClosedToItsKey`. `ResolutionFaults_…` now faults through an inner client whose `GetService` throws (the options are no longer read for `local:ollama`) and keeps a throwing-options case on a Bedrock key. `EveryKeyedTarget_…` uses the default Ollama and LLamaSharp inner clients.
- `EgressGuardConventionTests.Controls`: one control's label now reads `(OllamaHttpChatClient.cs:166 as it was before 4.1)`; its fixture is unchanged.

### Red at base

The twins were committed alone first (`1f92e73`, on master `79e988c`; kept on the local branch `wip/4.1-twins`), run, and then the change was committed.

| Run at `1f92e73` (base code) | Result | Red, by test |
|---|---|---|
| `Ashlar.Tests.Infrastructure` net8.0, the four twin classes | **28 failed / 241** | `A_file_uri_is_never_inside_the_host_boundary` ×5 (Host); `A_file_name_is_a_path_recorded_as_written_and_never_inside_the_host_boundary` ×5 (Host; the `file:\\127.0.0.1\…` row has no `://` and passes at the base, as a fact row); `Every_destination_shape_…` ×2 (the new `file://` rows); `SneakernetTransport_records_a_loopback_spelled_export_path_…` and `FileBasedSharedAdaptationStore_records_a_loopback_spelled_shared_path_…` (recorded `file://127.0.0.1`, Host); `LocalOllama_WithACustomInnerClient_RecordsWhereThatClientDials` (recorded `http://localhost:11434`); `LocalOllama_WithACustomInnerClientThatNamesNoUri_FailsClosedToItsKey`; `LocalOnnx_WithACustomInnerClient_RecordsItsProviderUri` and `LocalOnnx_WithACustomInnerClientThatNamesNoUri_FailsClosedToItsKey` (no decision); `ResolutionFaults_…` (site `EG-MDL-01`, not `meai:local:ollama`); `AnOllamaCloudModel_IsRecordedAsAnExternalModelAtOllamaCom` ×5 and `AnOllamaCloudModel_AsTheDefaultModel_IsRecordedAtOllamaCom` (recorded `http://localhost:11434`); `TheDefaultOllamaClient_DoesNotFollowARedirect` (no exception: the 307 was followed); `OllamaProvider_records_a_cloud_model_…` ×2 (no `ollama.com` decision) |
| `Ashlar.Tests.CLI` net10.0, `EgressCliSiteTwinTests` | **8 failed / 16** | `PeerDestination_names_the_peer_…` ×7 (`tcp://…`); `A_package_served_to_a_loopback_peer_records_one_NetworkExport_decision_…` (`tcp://127.0.0.1`, Host) |

Green at the base, and kept as guards against an over-broad rule: `ALocalModel_IsRecordedAtTheDaemon` ×6, `LocalOnnx_WithTheLlamaSharpClient_RecordsNoDecision`, the rewritten `mesh-peer:` classifier rows. `TheCloudModelRule_IsTheSameOnBothOllamaRoutes` (13 rows) was added after the base run: it pins that the MEAI and `OllamaProvider` copies of the rule agree.

### Mutation checks

Every row was run with `scripts/mutation-check.sh` (commit, apply one replacement, prove it applied, red, restore, empty `git status --porcelain`, green) in the container.

All nine at `4e170e2` (the final head `1e3e645` differs by one documentation line). Filter for M2 to M9: the four twin classes on net8.0 (`EgressGuardDecisionTests|EgressExplicitSiteTwinTests|EgressGuardChatClientTwinTests|EgressRawClientTwinTests`); M1: `EgressCliSiteTwinTests` in `Ashlar.Tests.CLI` on net10.0. Each log shows `replaced 1 occurrence`, the diff, and `porcelain=[]` after the restore.

| id | mutation | summary line (verbatim) | what went red |
|---|---|---|---|
| M1 | `PeerDestination` back to `tcp://<ip>` / `tcp://[<v6>]` | `mutation m1-mesh-peer-restore-tcp: KILLED red=failed:7/16 green=passed:16/16 ref=4e170e2b197622440f2e160450022455d362eb8f` | `PeerDestination_names_the_peer_…` ×6 (all but `null`); `A_package_served_to_a_loopback_peer_records_one_NetworkExport_decision_…` |
| M2 | drop rule 1 (the `ProviderUri` pattern can never match) | `mutation m2-meai-drop-provider-uri: KILLED red=failed:21/254 green=passed:254/254 ref=4e170e2b197622440f2e160450022455d362eb8f` | `LocalOllama_WithACustomInnerClient_RecordsWhereThatClientDials`, `LocalOnnx_WithACustomInnerClient_RecordsItsProviderUri`, `LocalOllama_IsClassifiedByTheResolvedUrl` ×2, `LocalOllama_RecordsTheEnvironmentUrl_…` ×2, `Streaming_…`, `ResolutionFaults_…`, `EveryKeyedTarget_…`, `ALocalModel_IsRecordedAtTheDaemon` ×6, `AnOllamaCloudModel_IsRecordedAsAnExternalModelAtOllamaCom` ×5, `AnOllamaCloudModel_AsTheDefaultModel_…` |
| M3 | rule 2 key-only (drop the LLamaSharp type check) | `mutation m3-meai-onnx-key-only: KILLED red=failed:1/254 green=passed:254/254 ref=4e170e2b197622440f2e160450022455d362eb8f` | `LocalOnnx_WithACustomInnerClientThatNamesNoUri_FailsClosedToItsKey` |
| M4 | delete the `file:` name rule in `DescribeName` | `mutation m4-file-name-rule-dropped: KILLED red=failed:9/254 green=passed:254/254 ref=4e170e2b197622440f2e160450022455d362eb8f` | `A_file_name_is_a_path_…` ×5, `Every_destination_shape_…` ×2 (the `file://` rows), `SneakernetTransport_records_a_loopback_spelled_export_path_…`, `FileBasedSharedAdaptationStore_records_a_loopback_spelled_shared_path_…` |
| M5 | `DescribeUri` without the `file` scheme rule | `mutation m5-file-uri-rule-dropped: KILLED red=failed:5/254 green=passed:254/254 ref=4e170e2b197622440f2e160450022455d362eb8f` | `A_file_uri_is_never_inside_the_host_boundary` ×5 |
| M6 | MEAI cloud rule dropped (`IsOllamaCloudModel(null)`) | `mutation m6-meai-cloud-rule-dropped: KILLED red=failed:6/254 green=passed:254/254 ref=4e170e2b197622440f2e160450022455d362eb8f` | `AnOllamaCloudModel_IsRecordedAsAnExternalModelAtOllamaCom` ×5, `AnOllamaCloudModel_AsTheDefaultModel_…` |
| M7 | MEAI cloud rule reads only `options.ModelId` | `mutation m7-meai-default-model-ignored: KILLED red=failed:1/254 green=passed:254/254 ref=4e170e2b197622440f2e160450022455d362eb8f` | `AnOllamaCloudModel_AsTheDefaultModel_IsRecordedAtOllamaCom` |
| M8 | `OllamaProvider` cloud decision dropped | `mutation m8-provider-cloud-rule-dropped: KILLED red=failed:2/254 green=passed:254/254 ref=4e170e2b197622440f2e160450022455d362eb8f` | `OllamaProvider_records_a_cloud_model_as_an_external_model_at_ollama_com` ×2 (the cloud rows) |
| M9 | `AllowAutoRedirect = true` on the default Ollama client | `mutation m9-ollama-follows-redirects: KILLED red=failed:1/254 green=passed:254/254 ref=4e170e2b197622440f2e160450022455d362eb8f` | `TheDefaultOllamaClient_DoesNotFollowARedirect` |

The design's four (restore `tcp://`, drop rule 1, rule 2 key-only, drop the `file:` rule) are M1, M2, M3 and M4; the `file` URI half (M5), the cloud rule on both routes (M6, M8), its default-model branch (M7) and the redirect change (M9) are the other behavioural changes, each checked the same way.

**Not observed red, stated.** The live mesh-serve twin runs over IPv4 loopback only; the IPv6 and IPv4-mapped shapes are pinned by the `PeerDestination` theory. The `ThrowingMetadataChatClient` path of `EgressGuardChatClient`'s default-model read (a `GetService` that throws is swallowed) is exercised by `ResolutionFaults_…` but not mutated.

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
