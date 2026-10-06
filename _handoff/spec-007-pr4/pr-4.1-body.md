## Summary

SPEC-007 PR 4.1 closes the third gap PR 3b carried to PR 4: **egress records that could read Host for a remote peer** (design §2.6 gap 3, default D34). It is still report-only. Nothing refuses; records change, and one HTTP client stops following redirects.

- **A `file:` destination is never Host.** A destination name that starts with `file:` (any case) is a path: it is recorded as written and never read as a URL. A `file` URI is never inside the host boundary either. Before, `file:` plus a path spelled `//127.0.0.1/…` parsed as a URL with a loopback host and was recorded as Host (EG-MESH-07/08, and any other file site).
- **Mesh serve records `mesh-peer:<ip>`** (`mesh-peer:unknown` with no address). The name has no `://`, so the class is the family's, a network export, whatever the IP. A loopback puller was Host, but a local TLS terminator, `ssh -R` or a localhost tunnel delivers every remote puller as loopback, and forwarded headers can set the address (EG-MESH-03).
- **The MEAI layer records where the inner client dials** (EG-MDL-01/02). The destination is the inner client's `ChatClientMetadata.ProviderUri` when absolute; `local:onnx` records nothing only when the inner client is the in-process `LlamaSharpChatClient` (a type check); Bedrock keeps its region reconstruction; anything else fails closed to `meai:<key>`, an external model. Before, the key decided: a custom `local:ollama` client was recorded as the resolved Ollama URL (Host by default), and a custom `local:onnx` client was not recorded at all.
- **Ollama cloud models (D34, unconditional).** A model id that ends in `-cloud` or `:cloud` (any case) is recorded as an external model at `https://ollama.com`, because the local daemon relays it there: in `EgressGuardChatClient` (the call's `ChatOptions.ModelId`, or the inner client's default model) and in `OllamaProvider` (one more EG-MDL-07 decision before the chat, when the requested name or the name it resolves to is a cloud id: a config's `gpt-oss` that resolves to `gpt-oss:120b-cloud` counts, and so does a requested `foo-cloud` that resolves to `foo-cloud:latest`).
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
- **Public docs** (`src/Ashlar.Abstractions/Security/Egress/EgressDecision.cs`): the class remarks and `Destination` name the `file:` exception: a `file:` name is a path, recorded as written and bounded, and may hold a path. XML docs only.
- **Records:**
  - `docs/EgressInventory.md`: the "Records that can read Host for a remote peer" Known-limits bullets are removed; a "Loopback is inside the host boundary only for a service that does not relay" bullet replaces them (D40's loopback half: OTLP collector, docker daemon, `kubectl proxy`, local LLM gateways; the cloud rule and where it applies; EG-MDL-10, EG-MDL-11 and the OpenAI-compatible providers (EG-MDL-03) pointed at a local Ollama's `/v1` do not; the rule matches the model id as written, so an alias or copy of a cloud model, or a model set inside a host's inner pipeline, is recorded at the daemon); the Bedrock bullet states that the AWS MEAI client (4.0.101.8) reports no `ProviderUri`, pinned by a twin; the EG-MDL-01, EG-MDL-02, EG-MDL-07, EG-MESH-03, EG-MESH-07 and EG-MESH-08 Route cells (EG-MDL-02's now puts an absolute `ProviderUri` the inner client reports first; EG-MDL-07's also says the handler's own decision stays Host for a loopback daemon, so only the explicit cloud decision can refuse, which 4.9 must do before the send); the redirect bullet names the Ollama client; line numbers re-derived for the files this PR edits.
  - `ci/egress-inventory.tsv`: `OllamaHttpChatClient.cs` `http.new` 1 → 2 (the new `HttpClientHandler`), note updated. 85 rows, 149 occurrences (was 148), 48 guarded. No other pin moves.
  - `ci/cert-gate-assertions.md` row 64: the classifier's `file` rule, the MEAI destination rule and cloud rule, the redirect-off client, `OllamaProvider`'s cloud decision, the file doors' `//127.0.0.1` twins, the resolved-name, Bedrock-URI and AWS-client twins, and the 4.1 occurrence count (149, a measurement; the floors stay 1000/60/30).
  - `EgressGuardConventionTests` floors comment: restated for 4.1 (149 occurrences, http.new 19; no production file added).
  - `docs/governed-pipeline.md` and the `UseAshlarGovernance` XML remarks (`AshlarGovernanceChatClientBuilderExtensions.cs`): an inner client that names no URI is recorded at the configured region's endpoint (or `aws-bedrock`) under `cloud:bedrock:*`, else as `meai:<key>`.
  - `CHANGELOG.md` (`### Changed`; the PR 3b entry's "destination fixed when the keyed client is built" now says an Ollama cloud model is recorded per call; the PR 3a entry's "scheme, host and port only" sentence now notes the `file:` exception).
  - SPEC-007: the "Gaps carried to PR 4" bullet marks the third gap (records that could read Host) closed by PR 4.1, and one status line for PR 4.1, which also records the 17th explicit guard site.
- **Tests:** listed under Testing.

## Phase B: check findings fixed

The adversarial check found no blocking defect. Each finding, and what this PR does about it:

1. **[medium] The resolved-name half of the `OllamaProvider` cloud rule had no test.** New twin `OllamaProvider_records_a_short_name_that_resolves_to_a_cloud_model_at_ollama_com` (since the second check, the `gpt-oss` row of `OllamaProvider_records_the_relay_when_only_the_requested_or_only_the_resolved_name_is_a_cloud_id`): the stub daemon lists only `gpt-oss:120b-cloud`, the provider is asked for `gpt-oss`, and the twin asserts one EG-MDL-07 decision at `https://ollama.com` (an external model) and that the chat body carries `gpt-oss:120b-cloud`. Mutation M10 drops only the second operand and goes red.
2. **`EgressDecision`'s redaction docs** now name the `file:` exception, on the class and on `Destination` (XML docs only; no PublicAPI change).
3. **The `EgressGuardConventionTests` floors comment** is restated for 4.1: 149 occurrences, http.new 19 (recounted after the master merge; master moved no pin).
4. **Known limits** name EG-MDL-10 next to EG-MDL-11, and say the rule matches the model id as written (an alias or copy of a cloud model, or a model set inside a host's inner pipeline, is recorded at the daemon).
5. **Bedrock.** New twin `CloudBedrock_WithAnInnerClientThatNamesItsUri_IsRecordedThere_UnderItsSite` (a `cloud:bedrock:fast` key over an inner client reporting `https://bedrock.example`: site EG-MDL-02, that URI, ahead of the configured region); mutation M11 goes red. The AWS hedge is resolved, not left: a runtime probe showed the AWS SDK's `BedrockChatClient` (4.0.101.8) reports no `ProviderUri`, and the twin `CloudBedrock_TheAwsClientReportsNoProviderUri_SoTheRegionIsReconstructed` pins it (building the client sends nothing). The Known-limits bullet now states it.
6. **CHANGELOG.** The PR 3b entry's "a destination fixed when the keyed client is built" now adds that since PR 4.1 an Ollama cloud model is recorded at `https://ollama.com` per call.
7. **17 explicit sites, for 4.9.** No code change. `OllamaProvider`'s cloud decision (`OllamaProvider.cs:244`, `_ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(... "EG-MDL-07" ...))`) is the 17th production `_ = …Evaluate(new EgressRequest(` site (grep count 17). Under enforcement the handler's own decision names the loopback daemon (Host, allowed), so only this explicit decision can refuse a cloud chat, and 4.9 must make it refuse before `SendAsync`, not only record. The EG-MDL-07 Route cell records all of this, the Host clause included; SPEC-007's 4.1 line records only that this is the 17th explicit guard site, which 4.9 must make refuse before the send.
8. **The redirect twin's port race.** `RedirectingOllama.Start` now retries on a freshly probed port when the `HttpListener` bind fails (`HttpListenerException` or `SocketException`, up to 10 attempts, disposing each failed listener).

**Found while fixing, out of scope (not changed):** `OllamaProvider.BuildChatPayload` ends its JSON with one `}` more than it opens (its last segment, `"]}}"`, is not an interpolated string, so the brace is not an escape). Ollama's decoder reads the first value and ignores the rest, so chats work; a strict parser rejects the body. The twin's stub reads the first JSON value only, as the daemon does. Worth a small follow-up.

## Phase B: second check, findings fixed

A second adversarial check raised seven low findings. All seven were real; none needs an owner decision.

1. **Nothing pinned that the EG-MDL-07 relay decision comes before the chat is sent.** The twins counted the decisions but not their order, and a mutant that moved the decision after `SendAsync` survived. The `OllamaProvider` theory now asserts the decisions in order (daemon tags, `https://ollama.com`, daemon chat; a local model: daemon, daemon), and so does the operand theory below. Mutation M12 moves the decision block to just after `SendAsync` and goes red in all four cloud rows. 4.9 relies on this site refusing before the send.
2. **The requested-name operand was neither tested nor mutated.** The short-name twin is now a two-row theory, `OllamaProvider_records_the_relay_when_only_the_requested_or_only_the_resolved_name_is_a_cloud_id`, and each row pins one operand: `gpt-oss` → `gpt-oss:120b-cloud` (only the resolved name is a cloud id) and `foo-cloud` → `foo-cloud:latest` by the bare-name `:latest` rule (only the requested name is; the fail-closed half). Each row asserts that exactly one of the two names is a cloud id, that the chat body carries the resolved name, and one EG-MDL-07 external-model decision at `https://ollama.com`. Mutation M13 drops `IsOllamaCloudModel(requestedModel) ||` and goes red.
3. **`meai:<key>` was claimed for every inner client that names no URI.** `cloud:bedrock:*` keeps the configured region's endpoint (or `aws-bedrock`) under EG-MDL-02 (rule 3), and the AWS client reports no URI. Fixed in `docs/governed-pipeline.md`, the `UseAshlarGovernance` XML remarks, the 4.1 CHANGELOG entry and SPEC-007's 4.1 line. The EG-MDL-02 Route cell now states rule 1 first: an absolute `ProviderUri` that the inner client reports is recorded under this site. (Row 64 already said so.)
4. **CHANGELOG PR 3a entry.** "Scheme, host and port only, never its path" now adds "(since PR 4.1 a `file:` name is a path and is recorded as written)", so it agrees with the 4.1 entry.
5. **SPEC-007 "Gaps carried to PR 4".** The bullet now ends "PR 4.1 closes the third gap: those records no longer read Host", so it no longer lists that gap as open in `docs/EgressInventory.md`, whose Known-limits bullets for it this PR removes. Expected conflict: 4.2 rewrites the same bullet (synchronous `Send` closed by 4.2). Whichever merges second keeps both: the first gap closed by 4.2, the third by 4.1, and redirects open for 4.3.
6. **The loopback-relay bullet left out a route.** It now names the OpenAI-compatible providers (EG-MDL-03, with `OPENAI_COMPAT_BASE_URL` or `OPENAI_BASE_URL` pointed at a local Ollama's `/v1`) next to EG-MDL-10 and EG-MDL-11 as routes that record the daemon whatever the model. Only `EgressGuardChatClient` and `OllamaProvider` apply the rule.
7. **Wording in this body and the merge message.** 149 is the 4.1 *occurrence count*, a measurement: the floors stay 1000/60/30 (fixed above). The Host clause is in the EG-MDL-07 Route cell only; SPEC-007's 4.1 line records the 17th site and the 4.9 obligation (fixed in item 7 above). Testing now says the merge commit `d540af5` itself carries the row 64, EG-MDL-07 and SPEC-007 record edits. The merge message's "the 4.1 floor (149)" should read "the 4.1 occurrence count (149)". It was left as it is: rewriting the pushed merge would orphan the ref that M10 and M11 name, and the squash merge drops the message.

Also fixed while there: the remarks gained a line, so EG-MDL-01's citation of the `PolicyGateChatClient` construction ("caller identity is never wired") moves from `AshlarGovernanceChatClientBuilderExtensions.cs:35` to `:36`.

## Integrator decisions (phase B)

- **A `file:` name is recorded as written, a userinfo included.** A second-check finding showed that `DescribeName` returns a `file:` name verbatim before any URL redaction, so a URL-shaped `file://user:secret@host/x` would keep its userinfo, which master stripped. The PR 4 design says `file:` names are never URL-parsed, and Ashlar's own file sites pass `file:` plus a local path, where a userinfo cannot occur. So the design default stands, and the `EgressDecision` class remarks and `Destination` docs (commit `439f449e`) now state the whole exception: such a name holds whatever its text holds, a userinfo, query or fragment included. That commit changes doc comments only. `Ashlar.Abstractions` built with 0 warnings and 0 errors on netstandard2.0, net8.0 and net10.0, and the repo gates passed 26 of 26.
- **Merge order.** The phase B plan merges 4.4 first. 4.4 (#716) is still being reworked, so 4.1 merges first. They share no code. Their only common files are records: the floors comment, the SPEC-007 PR 4 lines and the CHANGELOG. 4.4 merges master in after this PR and redoes its counts.

## Records this PR touches (for the drift audit)

- `docs/EgressInventory.md`: Known limits (loopback-relay bullet, which now names EG-MDL-03 with an OpenAI-compatible base URL beside EG-MDL-10 and EG-MDL-11; Bedrock bullet; redirect bullet), Route cells of EG-MDL-01, EG-MDL-02 (rule 1 first), EG-MDL-07, EG-MESH-03, EG-MESH-07, EG-MESH-08, and line numbers in the files this PR edits (EG-MDL-01's `AshlarGovernanceChatClientBuilderExtensions.cs:36`).
- `ci/egress-inventory.tsv`: `OllamaHttpChatClient.cs` `http.new` 1 → 2. Sums: 85 rows, 149 occurrences, 48 guarded; reasons 33 `-` / 34 `Exempt` / 10 `Factory` / 4 `Upstream` / 4 `Governance`.
- `ci/cert-gate-assertions.md`: row 64 (classifier, MEAI destination and cloud rules, redirect-off client, `OllamaProvider`'s cloud decision, the twins, and the 4.1 occurrence count, 149). The Certification count paragraph is unchanged: 125 `.cs` files, 128 entries (this PR adds no file there).
- `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressGuardConventionTests.cs`: the floors comment.
- `src/Ashlar.Abstractions/Security/Egress/EgressDecision.cs`, `EgressRequest.cs`, `EgressDestinations.cs`: XML docs (no PublicAPI change).
- `docs/governed-pipeline.md` (step 0, EgressGuard) and `src/Ashlar.AI.Pipeline/Governance/AshlarGovernanceChatClientBuilderExtensions.cs` (XML remarks of `UseAshlarGovernance`): the Bedrock fallback.
- `CHANGELOG.md` Unreleased: `### Changed` (the Ollama client's redirects), the 4.1 entry (Bedrock keeps its region endpoint), the PR 3b entry's wording and the PR 3a entry's "scheme, host and port only" sentence (the `file:` exception).
- `docs/specs/SPEC-007-security-labels-and-reference-monitor.md`: the "Gaps carried to PR 4" bullet (the third gap closed by 4.1) and the "PR 4.1 (this PR)" status line (needs the PR number and merge SHA at merge), including the 17th explicit site and the Bedrock fallback.
- `docs/knowledge-graph.{json,md}`: regenerated.

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

All builds and tests ran in the Linux devtest container through `scripts/test-in-container.sh`. Nothing ran on the host. The branch is the phase A commit (`1e3e645`, on master `79e988c`), one phase B fix commit (`a6e271c`), a merge of master `5ff00a4` (#714, #715) at `d540af5`, and three commits for the second check: `91ef17a` (the Ollama twins), `5971c9a` (records) and `4449f6c` (one citation). The merge had no conflicts, but the merge commit `d540af5` also carries phase B record edits that are not in `a6e271c`: row 64, the EG-MDL-07 Route cell and SPEC-007's 4.1 line (those files differ from both parents). Read it with `git show --first-parent d540af5`. The rows marked **second check** ran at `91ef17a`/`4449f6c`; the rows marked **phase B** ran at `d540af5`; the rest are phase A's evidence at `4e170e2`/`1e3e645`, which phase B did not change except as stated.

| Check | Result |
|---|---|
| **second check** Full cert-gate (`scripts/run-cert-gate.sh`, net8.0) at `4449f6c` | **2593/2593**, 0 skipped (skip guard matches the baseline; zero-test guard expected ≥2588). 2592 + the `foo-cloud` row |
| **second check** The five egress cert-gate classes on **net10.0** at `4449f6c` | **452/452** (451 + the `foo-cloud` row) |
| **second check** `EgressRawClientTwinTests` on net8.0 at `91ef17a` (the twins alone) | **9/9** (8 + the `foo-cloud` row) |
| **second check** `scripts/ci/run-repo-gates.sh` at `4449f6c` (after `git add -A` and the knowledge-graph rebuild, which changed nothing) | **26/26** |
| **phase B** Full cert-gate (`scripts/run-cert-gate.sh`, net8.0) at `d540af5` | **2592/2592**, 0 skipped (skip guard matches the baseline; zero-test guard expected ≥2587). 2589 + the 3 new twins |
| **phase B** The five egress cert-gate classes on **net10.0** at `d540af5` | **451/451** (448 + 3) |
| **phase B** `EgressRawClientTwinTests` and `EgressGuardChatClientTwinTests` on net8.0 | 58/58 at the fix commit before the AWS twin, then `EgressGuardChatClientTwinTests` 51/51 with it |
| **phase B** `Ashlar.Abstractions` build (netstandard2.0, net8.0, net10.0) at `d540af5` | 0 warnings, 0 errors (the `EgressDecision` XML docs) |
| **phase B** `scripts/ci/run-repo-gates.sh` at `d540af5` (after `git add -A` and the knowledge-graph rebuild) | **26/26** (master's #714 added `tests/scripts/handoff-scripts.test.sh`, so 25 became 26) |
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

M1 to M9 at `4e170e2` (the phase A head `1e3e645` differs by one documentation line); M10 and M11 at `d540af5` (phase B), with the same four-class filter (257 tests there); M12 at `5971c9a` and M13 at `4449f6c` (second check; the trees differ by one documentation line), same filter (258 tests: the operand theory adds one row). Filter for M2 to M9: the four twin classes on net8.0 (`EgressGuardDecisionTests|EgressExplicitSiteTwinTests|EgressGuardChatClientTwinTests|EgressRawClientTwinTests`); M1: `EgressCliSiteTwinTests` in `Ashlar.Tests.CLI` on net10.0. Each log shows `replaced 1 occurrence`, the diff, and `porcelain=[]` after the restore.

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
| M10 (**phase B**, at `d540af5`) | `OllamaProvider`: drop only `|| IsOllamaCloudModel(validationResult.Value.Name)` | `mutation m10-provider-resolved-name-dropped: KILLED red=failed:1/257 green=passed:257/257 ref=d540af5cf518ec37e4a351c9adfce8b081fc40c8` | `OllamaProvider_records_a_short_name_that_resolves_to_a_cloud_model_at_ollama_com` (now the `gpt-oss` row of the operand theory) |
| M11 (**phase B**, at `d540af5`) | rule 1 on a Bedrock key records the key's site (`isBedrock ? BedrockSite` → `isBedrock ? NameOf(targetKey)`) | `mutation m11-bedrock-uri-site-is-key: KILLED red=failed:1/257 green=passed:257/257 ref=d540af5cf518ec37e4a351c9adfce8b081fc40c8` | `CloudBedrock_WithAnInnerClientThatNamesItsUri_IsRecordedThere_UnderItsSite` |
| M12 (**second check**, at `5971c9a`) | `OllamaProvider`: the whole cloud-decision block moved to just after `using var response = await _httpClient.SendAsync(...)` | `mutation m12-provider-relay-decision-after-send: KILLED red=failed:4/258 green=passed:258/258 ref=5971c9a2abe8c8202dd0b48f148c30967f98fa23` | `OllamaProvider_records_a_cloud_model_…` ×2 (the cloud rows) and `OllamaProvider_records_the_relay_when_only_…` ×2, each on the order assertion (`daemon, ollama.com, daemon` expected) |
| M13 (**second check**, at `4449f6c`) | `OllamaProvider`: drop only `IsOllamaCloudModel(requestedModel) ||` | `mutation m13-provider-requested-name-dropped: KILLED red=failed:1/258 green=passed:258/258 ref=4449f6c4e902b8489356e3abc4b067b721359364` | `OllamaProvider_records_the_relay_when_only_…(requested: "foo-cloud", listed: "foo-cloud:latest")` |

The design's four (restore `tcp://`, drop rule 1, rule 2 key-only, drop the `file:` rule) are M1, M2, M3 and M4; the `file` URI half (M5), the cloud rule on both routes (M6, M8), its default-model branch (M7) and the redirect change (M9) are the other behavioural changes, each checked the same way.

**Not observed red, stated.** Phase B: `CloudBedrock_TheAwsClientReportsNoProviderUri_SoTheRegionIsReconstructed` pins a third-party fact (the AWS SDK's `BedrockChatClient` reports `ProviderName=aws.bedrock` and no `ProviderUri`; checked first with a throwaway runtime probe), so there is no Ashlar mutant for it; it goes red if an SDK update starts reporting a URI. The redirect twin's port retry is test plumbing and was not made to collide. Phase A: The live mesh-serve twin runs over IPv4 loopback only; the IPv6 and IPv4-mapped shapes are pinned by the `PeerDestination` theory. The `ThrowingMetadataChatClient` path of `EgressGuardChatClient`'s default-model read (a `GetService` that throws is swallowed) is exercised by `ResolutionFaults_…` but not mutated.

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

https://claude.ai/code/session_01NNv8ZAWjwkJYwgB4yFMLBs
