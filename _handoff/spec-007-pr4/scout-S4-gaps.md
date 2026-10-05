# Scout S4-gaps: designs for the three gaps PR 4 must close before it enforces

Base: `$SP/pr4-base` at `8ec674d2a` (PR 3b merged). This pass was read-only. Nothing was built or run.
Every `file:line` below is at that commit. Some claims are about the .NET runtime or a third-party
library rather than this repo. Those are marked **[ext]**, with a note on how to settle them.

`$SP` = `/tmp/claude-0/-home-user-Ashlar/119c0f0e-4179-5553-a59e-a4fbe7cfca88/scratchpad`

## 0. Summary

- **Gap 1, synchronous `Send` on the netstandard2.0 asset.** It matters only to code outside this repo.
  - Every in-repo caller of `EgressHttp` targets net8.0 or later.
  - No Ashlar source makes a synchronous HTTP `Send`.
  - No repo document says which consumer runtimes are supported.
  - **Recommendation:** make the netstandard2.0 asset fail closed. This needs no owner decision: a handler
    that cannot override `Send` makes the runtime throw `NotSupportedException`. Then have the owner
    *state* support. Do not add an end-of-life target framework.
- **Gap 2, redirects.**
  - The only production client that turns redirects off today is `MeshAutoPullService.cs:57`.
  - No Ashlar server issues redirects, and no in-repo call site visibly depends on a cross-host redirect.
  - **Design:** turn automatic redirects off on every primary handler Ashlar builds or binds. Add an
    internal `EgressRedirectHandler`.
    - It sits **directly above the primary handler**, where the runtime's own redirect loop sat, not
      outermost.
    - It follows redirects with the runtime's rules.
    - It evaluates every hop after the first, with the same guard, family and site.
  - **The real choice for the owner:** whether cross-host hops are followed at all (parity) or returned
    as a 3xx (same-host only).
- **Gap 3, records that can read Host.** Each has a code fix with no owner decision:
  - **EG-MESH-03:** record every peer as `mesh-peer:<ip>`, which is never Host.
  - **EG-MDL-01:** take the destination from the inner client's `ChatClientMetadata.ProviderUri` and
    fail closed to `meai:<key>`.
  - **EG-MESH-07/08:** a `file:` destination is never URL-parsed and never Host. This is one rule in
    `EgressDestinations`.
  - Each fix changes existing tests and records. Section 2.3 lists them.
- **Order.** Two decision-free pre-enforcement PRs can ship before PR 4:
  - **4a-1:** the gap 3 fixes and the netstandard2.0 fail-closed change.
  - **4a-2:** the redirect mechanism at parity.
- **The owner decides:**
  - D1: which consumer runtimes are supported, and whether to add a TFM.
  - D2: the cross-host redirect policy for the clients Ashlar builds.
  - D3: whether host-app factory clients get the redirect change.

---

## 1. Facts

### 1.1 Gap 1: synchronous `Send` on the netstandard2.0 asset

| # | Fact | Citation |
|---|---|---|
| F1.1 | `Ashlar.Abstractions` targets `netstandard2.0;net8.0;net10.0`. It has `LangVersion` 12 and `AnalysisMode=All`, and RS0036/RS0037 are suppressed. | `src/Ashlar.Abstractions/Ashlar.Abstractions.csproj:9-16` |
| F1.2 | There is one `PublicAPI.Shipped.txt`/`Unshipped.txt` pair, shared by all target frameworks. There are no per-TFM API files. | `Ashlar.Abstractions.csproj:27-30` |
| F1.3 | The `Send` override is compiled only under `#if NET5_0_OR_GREATER`. The remarks say that on netstandard2.0 a synchronous `Send` reaches the inherited `DelegatingHandler.Send`, which forwards to the inner handler without evaluating. | `src/Ashlar.Abstractions/Security/Egress/EgressGuardHandler.cs:13-18`, `:57-64` |
| F1.4 | `EgressHttp` repeats the limit and says it "must be closed before SPEC-007 PR 4 enforces". | `src/Ashlar.Abstractions/Security/Egress/EgressHttp.cs:11-14` |
| F1.5 | The whole egress surface (`EgressHttp`, the guard, the decision and the request) is in `PublicAPI.Unshipped.txt`, so nothing is promised yet. The policy confirms that the egress surface is unshipped. | `src/Ashlar.Abstractions/PublicAPI.Unshipped.txt:15-54`; `docs/SdkCompatibilityPolicy.md:46` |
| F1.6 | Every project that calls `EgressHttp` or the guard targets net8.0 or later: Infrastructure, AI.Pipeline, BackgroundAgents(.HostRunners), Mcp.Client, Tools.Dev, Transport.A2A, Transport.Grpc, MeshDirector, API, CLI. None of the netstandard2.0 projects (Core.Application, Runtime, Policies, Brick.Contracts, Core.Domain, Adapters.Models, Certification.*) calls it. | the csproj `TargetFramework(s)` of each; for example `src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj` (`net8.0;net10.0`), `src/Ashlar.AI.Pipeline/Ashlar.AI.Pipeline.csproj:4` |
| F1.7 | No Ashlar source makes a synchronous HTTP `Send`. Every `.Send(` in production code is MediatR, apart from the guard's own `base.Send`. | grep at 8ec674d2a; matches `docs/EgressInventory.md:9` |
| F1.8 | No repo document says which consumer runtimes are supported. `SdkCompatibilityPolicy.md` covers tiers and API stability only. The README states only the SDK the repo builds with (.NET 10). | `docs/SdkCompatibilityPolicy.md` (whole file); `README.md:226` |
| F1.9 | The netstandard2.0 shims are described as being for **Unity 2019+**. | `Directory.Build.targets:35-45` |
| F1.10 | The repo already records that "a `net6.0` or `net7.0` consumer silently binds the `netstandard2.0` asset". | `docs/samples/CertificationTrustConsumer/README.md:94-96` |
| F1.11 | The build treats warnings as errors. The SDK is 10.0.100. `System.Text.Json` is 10.0.12, and `Ashlar.Abstractions` references it unconditionally. | `Directory.Build.props:3`; `global.json`; `Directory.Packages.props:38`; `Ashlar.Abstractions.csproj:20` |
| F1.12 | The devtest container has SDK 10 and the 8.0 runtime only. It has no 5.0, 6.0 or 7.0 runtime. | `.docker/Dockerfile.devtest:1-6`, `:41-43` |
| F1.13 | The synchronous twin test calls `invoker.Send` from a net8.0/net10.0 test project, so it only ever exercises the net8.0 or net10.0 asset. | `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressHttpHandlerTwinTests.cs:94-110`; `src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj:4` |
| F1.14 | Code under `#if NET8_0_OR_GREATER` uses `ArgumentNullException.ThrowIfNull` and falls back to a manual null check otherwise. A new net6.0 inner build would compile the fallback. | `src/Ashlar.Abstractions/Security/SecurityGuard.cs:8-11`; `Barriers/BarrierContext.cs:73`; `Barriers/BarrierHierarchy.cs:104` |
| F1.15 | Polyfills are linked only into `.NETStandard` inner builds. | `Directory.Build.targets:41-45` |
| F1.16 | The CI precedents for testing another asset: an advisory `net9-runtime-consumer` lane, which packs a package and runs a net9.0 consumer against the `lib/net8.0` asset, and a `mono-netstandard20-consumer` lane. | `docs/CiGateInventory.md:127` |
| F1.17 | Prior review already framed two options: (a) add a net6.0 TFM; (b) make the netstandard2.0 handler a plain `HttpMessageHandler`, so the runtime's base `Send` throws. | `$SP/pr3a/REVIEW-FINDINGS.md:310-314` |

### 1.2 Gap 2: redirects. Who builds which client, and who relies on redirects

| # | Fact | Citation |
|---|---|---|
| F2.1 | `EgressHttp.CreateClient(family, site[, guard])` builds `new HttpClientHandler()` with the default `AllowAutoRedirect = true`. `CreateClient(inner, …)` and `Wrap(inner, …)` take the caller's handler unchanged. | `EgressHttp.cs:49`, `:70`, `:90` |
| F2.2 | In production code, the only `AllowAutoRedirect = false` is the mesh auto-pull client. The repo states the reason itself: "an untrusted peer must not be able to bounce this node's request to an internal/link-local address". | `application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshAutoPullService.cs:48-50`, `:57` |
| F2.3 | The known-limits text says redirects run below every `DelegatingHandler`. On a 307 or 308 the method and body are re-sent and only `Authorization` is cleared. | `docs/EgressInventory.md:10` |
| F2.4 | The factory binding adds the guard through `HttpMessageHandlerBuilderActions` at index 0 of `AdditionalHandlers`. That action runs **before** each client's own configuration, so it cannot see a primary handler the client sets later. | `src/Ashlar.Infrastructure/Egress/EgressServiceCollectionExtensions.cs:18-22`, `:63-69` |
| F2.5 | No production client registration calls `ConfigurePrimaryHttpMessageHandler`. Only tests do, with stub primaries. | grep; for example `EgressFactoryDefaultsTwinTests.cs:58`, `:111`; `EgressApiHostProdStyleTests.cs:76-77` |
| F2.6 | The prod-style API test installs its stub primary through `ConfigureHttpClientDefaults`, which runs before each client's own configuration. | `src/Ashlar.Tests.Infrastructure/Tests/VirtualProduction/EgressApiHostProdStyleTests.cs:76-77` |
| F2.7 | No Ashlar server issues a redirect: there is no `UseHttpsRedirection`, `Results.Redirect` or rewriter. So Ashlar-to-Ashlar calls (mesh peers, MeshDirector, workflow peers, RemoteBrick catalogs, the MeshLab executor) never depend on one. | grep at 8ec674d2a |
| F2.8 | No production handler sets `Credentials`, `UseDefaultCredentials` or `PreAuthenticate`. | grep |
| F2.9 | `OllamaHttpChatClient` (EG-MDL-01) builds its own `new HttpClient { BaseAddress, Timeout }`. That is neither `EgressHttp` nor the factory, so the MEAI decision is made once per call and a redirect below it is never seen. | `src/Ashlar.AI.Pipeline/Clients/OllamaHttpChatClient.cs:163-170` |
| F2.10 | Bedrock (EG-MDL-02) builds `new AmazonBedrockRuntimeClient(...)` with no config object. The AWS SDK owns the HTTP. | `src/Ashlar.AI.Pipeline/Clients/AwsBedrockChatClientFactory.cs:38-42` |
| F2.11 | gRPC (EG-XPT-03) passes a `new HttpClientHandler()` to `EgressHttp.Wrap`. `ConfigureHandler` sets TLS only. | `src/Ashlar.Transport.Grpc/DefaultGrpcChannelFactory.cs:76-89` |
| F2.12 | A2A puts a per-endpoint **API key header** in `DefaultRequestHeaders`, and MCP puts one in `AdditionalHeaders`. Because only `Authorization` is cleared on a redirect (F2.3), both keys go to any redirect target today. | `src/Ashlar.Transport.A2A/A2AAgentTransport.cs:169-178`; `src/Ashlar.Mcp.Client/McpClientConnectionManager.cs:314-323`, `:330` |
| F2.13 | The SNS signing-cert fetch (EG-HTTP-03) is a factory client (`ashlar-sns-signing`). Its host check is `*.amazonaws.com` or `*.amazonaws.com.cn` and HTTPS. `GetStringAsync` follows redirects. SubscribeURL confirmation applies the same check and also follows redirects. | `application/src/Ashlar.API/Program.cs:171`; `src/Ashlar.Ingress.AwsSns/SnsRsaSignatureVerifier.cs:34-39`, `:54`, `:79-81`; `application/src/Ashlar.API/Middleware/Ingress/AwsSnsSmsWebhook.cs:72`, `:116-123` |
| F2.14 | The ollama.com catalog (EG-MDL-13) does `GET api/tags` against `https://ollama.com` by default, then `EnsureSuccessStatusCode`. | `src/Ashlar.Infrastructure/ModelArtifacts/Sdk/Extensions/ModelArtifactCatalogServiceCollectionExtensions.cs:50-56`; `OllamaRemoteLibraryModelArtifactCatalogSource.cs:52-53` |
| F2.15 | OTLP export (EG-TEL-01) is decided once at registration. The prod-style test says that when the exporter's clients come from the factory, they also record `factory:` decisions. | `application/src/Ashlar.API/Program.cs:242-259`; `EgressApiHostProdStyleTests.cs:31-33` |
| F2.16 | No in-repo HTTP call downloads from a host that typically redirects across hosts (Hugging Face, the Ollama registry, GitHub releases). Model pulls are done by the Ollama daemon process (EG-MDL-10 `/api/pull`, EG-PROC-04). | grep; `docs/EgressInventory.md` rows EG-MDL-10 and EG-PROC-04 |
| F2.17 | The two explicit-route HTTP sites that take a **caller-supplied** `HttpClient` evaluate the configured URL only: the Ollama proposer (EG-MDL-11) and Bing (EG-WEB-01). Neither is in product DI. | `docs/EgressInventory.md` rows EG-MDL-11 and EG-WEB-01; `src/Ashlar.BackgroundAgents/Autonomy/OllamaProposalSource.cs:106`; `src/Ashlar.BackgroundAgents/WebSearch/BingWebSearchProvider.cs:30` |
| F2.18 | The convention scanner counts `new HttpClientHandler(`, `new SocketsHttpHandler(` and `new HttpMessageInvoker(` as `http.new`. The TSV pins `OllamaHttpChatClient.cs http.new 1 … Governance` and `EgressHttp.cs http.new 3 … Exempt:GuardImpl`. | `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressGuardConventionTests.Scanner.cs:577-580`; `ci/egress-inventory.tsv` |

Every EgressHttp construction site in production code:
- `ProviderFactory.cs:61` (EG-MDL-03..06, 08) and `:814` (EG-MDL-07)
- `CloudAvailabilityResolver.cs:98` (EG-MDL-12)
- `A2AAgentTransport.cs:169` (EG-XPT-01/02)
- `McpClientConnectionManager.cs:330` (EG-XPT-06/07)
- `DefaultGrpcChannelFactory.cs:76` (EG-XPT-03)
- `MeshAutoPullService.cs:72` (EG-MESH-04, redirects already off)
- `MeshCommand.cs:372` (EG-HTTP-06)
- `WorkflowCommand.cs:535` (EG-HTTP-05)
- `IdeEndpoints.cs:193` (EG-MDL-14 fallback)
- `MeshDirectorCommand.cs:415` (EG-HTTP-07)
- the factory binding at `EgressServiceCollectionExtensions.cs:66`

### 1.3 Gap 3: records that can read Host for a remote peer

| # | Fact | Citation |
|---|---|---|
| F3.1 | The classifier puts a URI or a `scheme://` name in the Host class when the scheme is `unix` or `npipe` or the host is loopback. A name is URL-parsed whenever it holds `://` with a valid scheme; there is no exception for `file`. | `src/Ashlar.Abstractions/Security/Egress/EgressDestinations.cs:180-187`, `:189-208`, `:219-236`, `:131-152` |
| F3.2 | **EG-MESH-03:** mesh serve runs on `WebApplication.CreateSlimBuilder()`, listens on `ListenAnyIP`, and records `PeerDestination(http.Connection.RemoteIpAddress)`, which is `tcp://127.0.0.1` / `tcp://[::1]` for a loopback peer. | `application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshServeService.cs:217`, `:229`, `:313`, `:328-339` |
| F3.3 | The EG-MESH-03 twins pin a loopback peer as `tcp://127.0.0.1` / Host, and the classifier theory pins `tcp://127.0.0.1` and `tcp://[::1]` with family `mesh.serve` as Host. | `application/src/Ashlar.Tests.CLI/Tests/Commands/EgressCliSiteTwinTests.cs:73-85`, `:87-113`; `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressExplicitSiteTwinTests.cs:267-272` |
| F3.4 | **EG-MDL-01:** the destination comes from the target key alone. `local:onnx` gives `null`, which means no decision. `local:ollama` gives `OllamaEndpointResolver.ResolveBaseUrl(options)`. The inner client is never consulted. | `src/Ashlar.AI.Pipeline/Governance/MeaiEgressDestination.cs:69-80`; `AshlarGovernanceChatClientBuilderExtensions.cs:27-28`; `EgressGuardChatClient.cs:58-61` |
| F3.5 | The inner client is replaceable through public API: `AddAshlarMeaiPipeline(ollamaInnerFactory:, onnxInnerFactory:, bedrockInnerFactory:)`, `AddAshlarGovernedChatClient(targetKey, innerFactory)`, and the public `OllamaHttpChatClient(HttpClient, model)` constructor. | `src/Ashlar.AI.Pipeline/MeaiPipelineServiceCollectionExtensions.cs:77-83`, `:100-112`, `:165-173`; `OllamaHttpChatClient.cs:34` |
| F3.6 | Metadata is already there. `OllamaHttpChatClient` returns `ChatClientMetadata("ollama", _http.BaseAddress, model)`. `LlamaSharpChatClient` returns `("llamasharp", null, …)` and answers `GetService(typeof(LlamaSharpChatClient))` with itself. `FakeChatClient` returns `("fake", null, …)`. The three governance layers are `DelegatingChatClient`s, so `GetService` reaches the provider. | `OllamaHttpChatClient.cs:112-119`; `LlamaSharpChatClient.cs:57-63`; `FakeChatClient.cs:49-56`; `PolicyGateChatClient.cs:9`, `SanitizingChatClient.cs:9`, `AuditingChatClient.cs:11` |
| F3.7 | The product composition uses the default inner clients (`AddAshlarMeaiPipeline(configuration)`). LLamaSharp loads a local GGUF file and makes no network call. | `src/Ashlar.Hosting/AshlarKernelRegistrar.Phases.cs:441`; `LlamaSharpChatClient.cs:78-82`, `:114-116`, `:163` |
| F3.8 | The MEAI twins put **`FakeChatClient`** under `local:ollama` and expect the resolver URL (Host or ExternalModel). | `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressGuardChatClientTwinTests.cs:106-130`, `:139-167`, `:232-253`, `:256-281`; `local:onnx` records nothing at `:185-203` |
| F3.9 | **EG-MESH-07/08** record `"file:" + path` with the path as written. `_basePath` is the constructor argument, or `~/.ashlar/shared-adaptations`, and is never normalised. The `--to` argument goes straight through. | `src/Ashlar.Infrastructure/Adaptation/FileBasedSharedAdaptationStore.cs:38-40`, `:57`; `SneakernetTransport.cs:31-36`; `application/src/Ashlar.CLI/Commands/MeshCommand.cs:190-200` |
| F3.10 | The other `file:` sites record a normalised full path: `outFile.FullName`, `storeDir`, `bundleDir`. | `application/src/Ashlar.CLI/Commands/PkgCommand.cs:93`, `:389`, `:444`; `NativeBundle.cs:88`; `src/Ashlar.BackgroundAgents.HostRunners/SelfExtendAdmissionBridge.cs:288` |
| F3.11 | No test passes a `file://` name and expects Host. The existing `file:` rows (`file:/home/…`, `file:\\server\share\…`) are NetworkExport. | `EgressExplicitSiteTwinTests.cs:263-266`; `EgressGuardDecisionTests.cs:89-161` (no `file://` row) |

### 1.4 External behaviour, not verifiable in this repo [ext]

- **E1, the runtime's base `Send`.** On .NET 5 and later, `HttpMessageHandler.Send` is
  `protected internal virtual` and its base implementation throws `NotSupportedException`.
  `DelegatingHandler.Send` forwards to `InnerHandler.Send`. The test in §2.1 settles this: it runs the
  netstandard2.0 build on the 8.0 runtime.
- **E2, the runtime's redirect loop.** `SocketsHttpHandler`'s `RedirectHandler`:
  - follows 300, 301, 302, 303, 307 and 308;
  - turns a POST into a GET on 301 and 302, and turns any method except HEAD into a GET on 303;
  - keeps the method and body on 307 and 308;
  - clears `Authorization`;
  - refuses a redirect from HTTPS to HTTP;
  - stops after `MaxAutomaticRedirections` hops (default 50) and returns the last 3xx;
  - uses a non-authenticating connection for the hops when `Credentials` is set and is not a `CredentialCache`.

  The differential twin in §2.2 settles these.
- **E3, `Microsoft.Extensions.Http`.**
  - The default `PrimaryHandler` is `new HttpClientHandler()`.
  - An `IHttpMessageHandlerBuilderFilter` wraps the whole configure step, so its code after `next(builder)`
    sees the final `PrimaryHandler`.
  - A delegating handler given to the builder must have a null `InnerHandler`.
  - .NET 8 added `ConfigurePrimaryHttpMessageHandler((handler, sp) => …)`, which configures the existing
    primary.
  - Setting `AllowAutoRedirect` on a handler that has already sent throws `InvalidOperationException`.
- **E4, warnings on an end-of-life TFM.** A `net6.0` target on SDK 10 raises NETSDK1138. Prior review
  says this is an error under `TreatWarningsAsErrors` (`$SP/pr3a/REVIEW-FINDINGS.md:26`).
  `System.Text.Json` 10.x warns that it does not support `net6.0`/`net7.0` and suggests
  `SuppressTfmSupportBuildWarnings`. Only a build settles this.
- **E5, end of support.** .NET 5 ended in May 2022, .NET 7 in May 2024 and .NET 6 in November 2024.
- **E6, Starlette.** Python MCP servers built on Starlette or FastMCP can answer `/mcp` with a 307 to
  `/mcp/`. If so, MCP clients rely on same-host redirects in the field.
- **E7, ASP.NET Core forwarded headers.** With `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, the web
  defaults, slim ones included, register the forwarded-headers startup filter with `KnownNetworks` and
  `KnownProxies` cleared. A client's `X-Forwarded-For` then rewrites `RemoteIpAddress`. The 3b verifier
  also took this from memory.
- **E8, Ollama cloud models.** Since about September 2025, Ollama serves "cloud" models (names ending
  in `-cloud`) through the **local** daemon on `localhost:11434`, which relays the prompt to ollama.com.
  This affects every "local Ollama = Host" record. See §2.4.
- **E9, tenant-controlled AWS hosts.** API Gateway endpoints are
  `https://<id>.execute-api.<region>.amazonaws.com` and their tenant controls them. So the SNS
  `*.amazonaws.com` check (F2.13) admits hosts an attacker controls.

---

## 2. Analysis and designs

### 2.1 Gap 1: synchronous `Send` on the netstandard2.0 asset

**Who is exposed (F1.6, F1.7).** Only code outside this repo, and only when all three hold:
1. the app runs on .NET 5, 6 or 7 and binds the netstandard2.0 asset (F1.10);
2. it calls the public `EgressHttp` API itself;
3. it sends **synchronously**.

.NET Framework, Mono and Unity have no synchronous `HttpClient.Send`, so the netstandard2.0 asset is
sound there. Unity is the documented audience of that asset (F1.9). Within Ashlar's own composition the
gap cannot occur, because every host is net8.0 or later. It still has to be closed, because §7 says a
MUST is enforced only where it names a passing test, and the guard's guarantee is stated for the public
API.

**Options.**

| Option | Change | Cost | Consequences |
|---|---|---|---|
| **A. Add a TFM** (`net6.0`, or `net5.0` to also cover .NET 5) | `<TargetFrameworks>netstandard2.0;net6.0;net8.0;net10.0</TargetFrameworks>`. The existing `#if NET5_0_OR_GREATER` `Send` override then compiles for it. | Suppress NETSDK1138 (`CheckEolTargetFramework=false`) and the STJ unsupported-TFM warning (`SuppressTfmSupportBuildWarnings`) for that TFM (E4). Sweep `#if NET8_0_OR_GREATER` to `NET6_0_OR_GREATER` wherever `AnalysisMode=All` would flag the fallback, for example CA1510 on `SecurityGuard.cs:8-11` (F1.14; needs a build). The shared PublicAPI files suffice while the surface is identical (F1.2). A fourth inner build on every cert-gate and kernel-gate build. No CI runtime for 6 or 7 (F1.12); a consumer lane would need a new advisory job like `net9-runtime-consumer` (F1.16). | Commits Ashlar to compiling an end-of-life target until it is dropped, and dropping it later silently moves those consumers back to netstandard2.0. Packing then declares STJ 10.0.12 for net6.0, which Microsoft does not support there (E4). With `net6.0`, .NET 5 apps still bind netstandard2.0, so option C is needed anyway. |
| **B. Drop .NET 5–7** | State in `docs/SdkCompatibilityPolicy.md` that the guard's synchronous coverage needs net8.0 or later, and that the netstandard2.0 asset serves .NET Framework, Mono and Unity. **B-hard:** also ship `buildTransitive/netstandard2.0/Ashlar.Abstractions.targets` with an `<Error>` when `TargetFrameworkIdentifier=.NETCoreApp` and the version is between 5.0 and 7.x, behind an opt-out property. | Documentation only. B-hard adds the repo's first `buildTransitive` file and needs a pack-and-consume probe. | B alone changes no behaviour, so the gap stays technically open. B-hard breaks the build for every .NET 5–7 consumer of every netstandard2.0 Ashlar package, because they all depend on Abstractions. |
| **C. Fail closed on the netstandard2.0 asset** (recommended for 4a-1) | Under `#if NETSTANDARD2_0`, put a handler between the guard and the inner handler that derives from **`HttpMessageHandler`, not `DelegatingHandler`**, and forwards `SendAsync` through an owned `HttpMessageInvoker(inner, disposeHandler: true)`. On .NET 5–7, a synchronous `Send` then goes: guard (the runtime's forwarding `DelegatingHandler.Send`) → this handler (no `Send` override) → the runtime's base `HttpMessageHandler.Send`, which throws `NotSupportedException` before anything is sent (E1). Under the redirect design (§2.2) this is the `EgressRedirectHandler` itself, so it costs no extra hop. Name the type so the runtime's message explains itself, for example `…Egress.SynchronousSendRefusedOnNetstandard20Asset`. For `CreateDelegatingHandler` (the factory path), the factory sets `InnerHandler` and the handler cannot be interposed. So on netstandard2.0, when `typeof(HttpMessageHandler)` has a non-public instance `Send(HttpRequestMessage, CancellationToken)` (checked once by reflection), throw `PlatformNotSupportedException` saying "use the net8.0 asset". | About 40 lines in Abstractions. `HttpMessageInvoker` is `http.new`, so add one `Exempt:GuardImpl` TSV row (F2.18). One test harness that runs the **netstandard2.0 build on the 8.0 runtime**: a net8.0 test project with `<ProjectReference … SetTargetFramework="TargetFramework=netstandard2.0" />` and no other Ashlar reference. Running netstandard2.0 IL on 8.0 reproduces the .NET 5–7 behaviour, which depends on the compiled overrides, not on the runtime version. So no 6.0 or 7.0 runtime is needed (F1.12). | **Behaviour change, report-only too.** On .NET 5–7, a synchronous `Send` through `EgressHttp` throws instead of going out unevaluated, and `CreateDelegatingHandler` throws there. Both are on an Unshipped surface (F1.5). On .NET Framework, Mono and Unity nothing changes except one invoker hop. `Wrap` used under an outer `HttpMessageInvoker` on .NET 5–7 may double the System.Net.Http telemetry events. The refusal is not a guard decision and is not recorded; the type name is the explanation. |
| **D. Document only** | Leave as is. | None | Fails §7. PR 4 cannot claim enforcement for the public API. |

**Twin test for C.** Build with `EgressHttp.CreateClient(stub, family, site, guard)` from the
netstandard2.0 build.
- Today: `invoker.Send(request)` returns the stub's response and records 0 decisions.
- After C: it throws `NotSupportedException`, the stub sees 0 requests, and `SendAsync` still records
  exactly 1 decision.
- Also: `CreateDelegatingHandler` throws `PlatformNotSupportedException` on the 8.0 runtime.
- Mutation: route `CreateClient` back to `new EgressGuardHandler(inner, …)` without the
  `HttpMessageHandler` hop. The `Send` assertion goes red (the stub sees 1 request).

**The harness must run in a required check.** cert-gate runs `Ashlar.Tests.Infrastructure`
(`docs/SdkCompatibilityPolicy.md:40`). If the harness is a new project, it needs a lane and a row in
`ci/cert-gate-assertions.md`.

### 2.2 Gap 2: redirects

**Who relies on automatic redirects today.**

| Site(s) | Client | Evidence of reliance | Notes |
|---|---|---|---|
| EG-MDL-03..08 (OpenAI, Azure, compat, video, ProviderFactory Ollama) | `EgressHttp` (`ProviderFactory.cs:61`, `:814`) | None in the repo. Every call is a POST to an API; a 301 or 302 would turn it into a GET and the call would fail anyway. | A compat base URL behind a proxy that answers 308 (HTTP to HTTPS) would rely on it. |
| EG-MDL-12 cloud availability | `EgressHttp` (`CloudAvailabilityResolver.cs:98`) | None | GET to `api.openai.com` |
| EG-XPT-01/02 A2A, EG-XPT-06/07 MCP | `EgressHttp` (`A2AAgentTransport.cs:169`; `McpClientConnectionManager.cs:330`) | Possible same-host redirects from third-party servers (E6) | API key headers survive a redirect (F2.12). Cross-host following leaks them. |
| EG-XPT-03 gRPC | `EgressHttp.Wrap` (`DefaultGrpcChannelFactory.cs:76`) | None. A 3xx is not gRPC. | — |
| EG-HTTP-05/06/07 (mesh CLI, MeshDirector) | `EgressHttp` | None from Ashlar servers (F2.7). A Caddy front end would redirect HTTP to HTTPS on the same host. | Peers are untrusted, as `MeshAutoPullService.cs:48-50` states. |
| EG-MESH-04 auto-pull | `EgressHttp` over a `SocketsHttpHandler` | Already off (`:57`) | Precedent |
| EG-MDL-09 RunPod, EG-MDL-10/14 Ollama backends, EG-MDL-13 ollama.com, EG-EXE-01..05, EG-HTTP-01/02 webhooks, host-app clients, OTLP (if factory, F2.15) | Factory (default `HttpClientHandler`) | None visible in the repo. Webhooks and host-app clients go to arbitrary URLs. | Host-app clients may genuinely rely on redirects. |
| EG-HTTP-03 SNS cert and SubscribeURL | Factory (`Program.cs:171`) | None: SNS does not redirect. | Following is a **security liability** (E9 plus F2.13). |
| EG-MDL-01 Ollama MEAI | Raw `new HttpClient` (`OllamaHttpChatClient.cs:166`) | None: Ollama does not redirect `/api/chat` [ext] | Not covered by any `EgressHttp` change |
| EG-MDL-02 Bedrock | AWS SDK | None | Fixed AWS endpoint |
| EG-MDL-11, EG-WEB-01 | The caller's `HttpClient` (F2.17) | — | Not in product DI. The residual is the caller's. |

**Design (decision-free at parity).**

**R-a. Turn automatic redirects off on every primary Ashlar builds or binds.**
- `EgressHttp.CreateClient(family, site, guard)`: `new HttpClientHandler { AllowAutoRedirect = false }`.
- `CreateClient(inner, …)` and `Wrap(inner, …)`:
  - When `inner` (or the end of its `DelegatingHandler.InnerHandler` chain) is an `HttpClientHandler`, or
    on NET a `SocketsHttpHandler`, record its `AllowAutoRedirect` and `MaxAutomaticRedirections`, then set
    `AllowAutoRedirect = false`. Do this inside a try/catch: a handler that has already sent throws (E3).
    If it throws, leave the handler following, unevaluated, and list it as a residual.
  - An unknown handler type is passed through and is a documented residual. In-repo inner handlers are
    `SocketsHttpHandler` with redirects already off, and `HttpClientHandler` for gRPC (F2.11).
- `OllamaHttpChatClient.CreateHttpClient` gets `new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { … }`,
  with **no** follower. The EG-MDL-01 decision is per call at the MEAI layer and cannot see hops, and
  Ollama does not redirect. A 3xx then fails at `EnsureSuccessStatusCode` (`:50`, streaming `:74`).
  - Record change: the TSV `OllamaHttpChatClient.cs http.new` count goes from 1 to 2 (F2.18).
- Bedrock: `new AmazonBedrockRuntimeClient(new AmazonBedrockRuntimeConfig { RegionEndpoint = …, AllowAutoRedirect = false })`.
  Verify that `ClientConfig.AllowAutoRedirect` exists in AWSSDK.Core 4.0.100.4 (`Directory.Packages.props:127`).
- SNS: the `ashlar-sns-signing` client should **not follow at all**. Use the configure-existing overload:
  `.ConfigurePrimaryHttpMessageHandler((h, _) => { if (h is HttpClientHandler c) c.AllowAutoRedirect = false; })`.
  - Do **not** use `ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler{…})`. It runs after
    the prod-style test's defaults and would replace that test's stub primary (F2.6), so the test would
    send for real.
  - The convention fixtures that mirror `Program.cs:171` (`EgressGuardConventionTests.Controls.cs:514`,
    `.Routes.cs:1206`) may need the same edit.

**R-b. Add `EgressRedirectHandler` (internal, `src/Ashlar.Abstractions/Security/Egress/`).**
- **Placement.** It sits **innermost, directly above the primary**: where the runtime's redirect loop
  sat, below every client handler. Do not put it outside the guard.
  - With a follower outermost, a client `DelegatingHandler` that adds a Bearer token or an API key would
    run again on every hop and re-add the credential the runtime had cleared. With it innermost, inner
    handlers run once, as they do today.
  - Retry handlers keep retrying the whole exchange.
- **The hops.** The first hop is evaluated by `EgressGuardHandler`, unchanged. The redirect handler
  evaluates **every later hop** with the same guard, family and site, before sending it.
  - Under report-only this adds one record per hop. Under PR 4's enforcement it throws the guard's
    explained refusal, after disposing the 3xx.
- **Semantics: runtime parity (E2).** Run the loop only when the primary originally had
  `AllowAutoRedirect = true`, and use its own `MaxAutomaticRedirections`. Then:
  - resolve a relative `Location`, and keep the original fragment when the new one has none;
  - refuse HTTPS to HTTP;
  - refuse any scheme that is not `http` or `https`;
  - rewrite the method as the runtime does (301/302 POST to GET; 303 any non-HEAD to GET, with the
    content and `Transfer-Encoding: chunked` dropped);
  - set `Authorization = null`;
  - dispose each intermediate response;
  - mutate the same `HttpRequestMessage`, so `response.RequestMessage.RequestUri` is the final URI.
- **Overrides.** On NET it overrides both `SendAsync` and `Send`. On netstandard2.0 it derives from
  `HttpMessageHandler` over an `HttpMessageInvoker`, and that **is** gap 1 option C (§2.1).
- **Cross-host policy hook.** A cross-host hop is either followed and evaluated (parity, P1) or returned
  to the caller as the 3xx (same-host only, P2). Owner decision D2.

**R-c. Factory binding.** `AddAshlarEgressGuard` registers an `IHttpMessageHandlerBuilderFilter`
(`TryAddEnumerable`). Its post-`next(builder)` step sees the **final** `PrimaryHandler` (E3; this
addresses F2.4). For a known type it does the R-a flip and replaces `builder.PrimaryHandler` with
`EgressRedirectHandler(original, http.factory, "factory:" + builder.Name, guard)`.
- A delegating handler set as `PrimaryHandler` is allowed, because the factory does not set its inner.
- An unknown primary (every test stub, F2.5) is left alone: no flip, no follow. Existing stub tests
  therefore keep their behaviour; this still needs a run to confirm.
- **Shared primary instances.** A host may use `ConfigurePrimaryHttpMessageHandler(() => shared)`.
  Remember the original `AllowAutoRedirect` per instance (`ConditionalWeakTable`). Otherwise the second
  handler rotation reads the `false` the first rotation set, and stops following.
- **Access.** Infrastructure needs the internal type. Either add
  `<InternalsVisibleTo Include="Ashlar.Infrastructure" />` to `Ashlar.Abstractions.csproj:32-38`
  (Hosting already has it), or add a public `EgressHttp` method in `Unshipped.txt`.
  - **Recommendation: InternalsVisibleTo.** It makes no public promise.

**What changes in report-only mode (a behaviour change, even before enforcement).**
1. A redirected send records one decision per hop. The "exactly once per `SendAsync`" wording in
   `EgressGuardHandler.cs:8-9` and `EgressHttp.cs:7-8` becomes "the request once, then each hop the
   redirect handler follows, once".
2. Ashlar's code follows redirects, not the runtime's. Parity is designed in, but differences remain:
   - hop credentials: no production handler sets `Credentials` (F2.8). For a host primary that does and
     is not a `CredentialCache`, do not follow a cross-host hop, or leave that primary following and
     record it as a residual;
   - per-hop telemetry is unchanged, because DiagnosticsHandler is inside the primary.
3. `AllowAutoRedirect` reads `false` on every known primary, host-app clients included, if D3 is "all".
4. Under P2, a cross-host redirect reaches the caller as a 3xx. Most call sites then throw from
   `EnsureSuccessStatusCode`.
5. `OllamaHttpChatClient`, Bedrock and the SNS client stop following altogether.

**Twin tests, with a redirecting stub.**
- **Stub.** A test class `RedirectingStub : HttpClientHandler` overrides `SendAsync` and `Send`: 307 to
  `http://remote.example/b` for `/a`, and 200 for `/b`. Because it is an `HttpClientHandler`,
  `EgressHttp` and the filter flip it and wrap it. Because its override bypasses the base, it never
  dials.
  - **Today**, red: 1 decision, and the caller gets the 307.
  - **After:**
    - 2 decisions: `localhost` Host, then `remote.example` NetworkExport/ExternalModel;
    - the body reaches `/b` only after the second decision;
    - `stub.AllowAutoRedirect` is `false`.
- **Factory twin.** `AddHttpClient("x").ConfigurePrimaryHttpMessageHandler(() => new RedirectingStub())`
  with `AddAshlarEgressGuard` gives two `factory:x` decisions. A primary with `AllowAutoRedirect = false`
  returns the 307 with 1 decision. A non-`HttpClientHandler` stub keeps today's behaviour.
- **Differential twin against the real runtime.**
  - Use a loopback Kestrel or `HttpListener` server and a `SocketsHttpHandler` whose `ConnectCallback`
    maps `remote.example:80` to the server.
  - Send the same request twice: runtime-follow (plain `HttpClient`) and Ashlar-follow (`EgressHttp`).
  - Compare what the server saw on the final hop: method, body, `Authorization`, an API-key header, and
    the URI with its fragment.
  - Cover 301/302 with POST, 303, 307 and 308; HTTPS to HTTP; a relative `Location`; no `Location`; and
    the limit.
  - This turns the [ext] parity claims (E2) into observed facts.
- **Enforcement twin, for PR 4.** A refusing test guard stops hop 2. The server never sees `/b`, and
  the exception is the guard's explained refusal.
- **Mutations:**
  - (1) drop the flip: the real-runtime twin goes red, because the runtime follows and there is one
    decision;
  - (2) drop the per-hop evaluate: red, 1 decision instead of 2;
  - (3) keep `Authorization`: the differential twin goes red;
  - (4) drop the 303 rewrite: red;
  - (5) drop the HTTPS-to-HTTP refusal: red.

**Records touched:**
- `docs/EgressInventory.md:10` (the gap bullet is removed and replaced by a residual for unknown and
  already-started primaries, and the caller-supplied clients of EG-MDL-11 and EG-WEB-01);
- the remarks of `EgressGuardHandler`, `EgressHttp` and `EgressServiceCollectionExtensions` ("Outermost"
  still holds for the guard; add the filter);
- the TSV (`OllamaHttpChatClient.cs` http.new 1 to 2; possibly the Bedrock sdk.client count);
- cert-gate row 64's text;
- SPEC-007 lines 44-49;
- `CHANGELOG.md`.

### 2.3 Gap 3: records that read Host

#### EG-MESH-03 (mesh serve peer)

**Fix.** `PeerDestination` returns `mesh-peer:<ip>` for **every** peer (`mesh-peer:unknown` with no
address), not `tcp://<ip>`.
- The name has no `://` and no `host:` prefix, so `DescribeName` never URL-parses it and never puts it in
  Host (F3.1). The class is the family's, `mesh.serve`, which is NetworkExport and Internal.
- Keep the IPv4-mapped to IPv4 mapping for readable records.
- **Why not "Host only when loopback and not proxied":** the listener is `ListenAnyIP` (F3.2), and a local
  TLS terminator, `ssh -R` or a localhost tunnel cannot be detected from the connection. Forwarded
  headers (E7) only make the IP spoofable.
- With the fix the IP no longer affects the class, so both the proxy case and the spoofing case close.
- The record still shows the IP Kestrel reports, which forwarded headers can spoof. That affects the
  text, not the class.

**Twin.**
- `PeerDestination(127.0.0.1)` is `mesh-peer:127.0.0.1` (and `::1` gives `mesh-peer:::1`). The live
  test serves a package to a loopback client: `DestinationClass` is NetworkExport, not Host.
- Mutation: restore `tcp://` and the live twin goes red (Host). No forwarded-headers test is needed,
  because the class no longer depends on the IP.

**Records it changes:**
- `EgressCliSiteTwinTests.cs:73-85` (the expected strings) and `:87-113` (Host becomes NetworkExport);
- the classifier theory rows at `EgressExplicitSiteTwinTests.cs:267-272`. That theory claims to cover
  "every destination shape the explicit sites pass", so replace the `tcp://` rows with `mesh-peer:`
  rows;
- the EG-MESH-03 Route cell and the Known-limits bullet (`docs/EgressInventory.md:17`);
- SPEC-007 lines 44-49; `CHANGELOG.md`.
- TSV: no change, because no marker changes.

**Consequence under enforcement.** Every serve compares the current label against Internal. No subject
frame reaches Kestrel's thread, so the current label is SystemHigh (`no-subject`), and **every mesh
serve would be refused** under AirGapped or SecureWorkstation. That is fail-closed and correct. Serving
then needs the served package's label as the subject. That is gap 4 or PR 5 work (outside this lens),
not this fix.

#### EG-MDL-01 (custom inner client under `local:*`; `local:onnx` unrecorded)

**Fix.** Pass `inner` to the resolver:
`builder.Use((inner, sp) => new EgressGuardChatClient(inner, MeaiEgressDestination.Resolve(targetKey, sp, inner), …))`
(`AshlarGovernanceChatClientBuilderExtensions.cs:27-28`). `ResolveCore` then runs, never throwing:
1. Read `inner.GetService(typeof(ChatClientMetadata))` (F3.6). If `ProviderUri` is an absolute URI,
   return `EgressRequest(ModelMeai, site, providerUri)`, with site `EG-MDL-01` for `local:ollama`,
   `EG-MDL-02` for Bedrock, and `meai:<key>` otherwise.
   - For the default `OllamaHttpChatClient` this is `BaseAddress`, which the same resolver produced, so
     the default record does not change.
2. For `local:onnx`, return `null` (no decision) **only** when
   `inner.GetService(typeof(LlamaSharpChatClient)) is LlamaSharpChatClient`. That is a type check, not
   the spoofable provider-name string. The default in-process client therefore still records nothing
   (F3.7).
3. For `cloud:bedrock:*` with no `ProviderUri`, keep today's region reconstruction (`:82-89`).
4. Anything else, including `local:ollama` with no `ProviderUri` and `local:onnx` that is not
   LLamaSharp, becomes `Named(targetKey)` = `meai:local:ollama` or `meai:local:onnx`. That is
   ExternalModel and Internal: **it fails closed** (§7).

Also set `AllowAutoRedirect = false` on `OllamaHttpChatClient`'s own client (§2.2 R-a), because a
`ProviderUri` names only the first hop.

**Twins.** Each is red today and green after:
- custom inner under `local:ollama` with `ProviderUri` `http://gpu.example:11434`: records that URL as
  ExternalModel (today: `http://localhost:11434`, Host);
- custom inner under `local:onnx` with `ProviderUri` `https://api.example`: records it (today: no
  decision);
- custom inner under `local:onnx` with no metadata: `meai:local:onnx`, ExternalModel (today: none);
- default LLamaSharp: none (unchanged);
- default Ollama via the options constructor: the resolver URL (unchanged).

Mutations:
- drop step 1: the `gpu.example` twin goes red;
- make step 2 key-only: the custom-onnx twin goes red.

**Records it changes:**
- **Existing tests.** The MEAI twins that put `FakeChatClient` under `local:ollama` and expect the
  resolver URL (`EgressGuardChatClientTwinTests.cs:106-130`, `:139-167`, `:232-253`, `:256-281`; F3.8)
  would record `meai:local:ollama`.
  - They test the resolver's precedence, so move them to
    `ollamaInnerFactory: sp => new OllamaHttpChatClient(new HttpClient(stub) { BaseAddress = new Uri(OllamaEndpointResolver.ResolveBaseUrl(opts)) }, "m")`,
    or to the default inner over a stub.
  - The test at `:177` only reads `Request`.
- Docs and remarks:
  - the `MeaiEgressDestination` remarks (`:13-31`);
  - the EG-MDL-01 Route cell (`docs/EgressInventory.md:63`) and Known-limits bullet (`:18`);
  - the `UseAshlarGovernance` remark (`:17-18`);
  - `docs/governed-pipeline.md:21`;
  - cert-gate row 64 ("`local:onnx`, in process, records none" becomes "the default LLamaSharp client
    records none");
  - `CHANGELOG.md`; SPEC-007 lines 44-49.
- The production composition's records **do not change** (F3.7).
- TSV: no change from this fix; the redirect-off change is counted under §2.2.

#### EG-MESH-07 / EG-MESH-08 (`file:` path spelled `//127.0.0.1/…`)

**Fix, central (recommended).** In `EgressDestinations`:
- `DescribeName`: a name starting with `file:` (ordinal, ignore case) is returned `Bound(name)` with
  `insideHost = false`. It is never URL-parsed, so the path stays in the record like every other file
  site, and the class is the family's.
- `DescribeUri`: `insideHost` is false when `uri.Scheme` is `file`, whatever the host.

This closes `file://127.0.0.1/…` and also `file://localhost/…`, which is Host today, for every current
and future file site: EG-MESH-01, EG-MESH-02, EG-FILE-01/02 and EG-MESH-07/08.

**Optional, site-level.** Normalise `_basePath` and the export `path` with `Path.GetFullPath`, inside a
non-throwing wrapper, as the other sites do (F3.10). Records then become absolute and consistent. On
Linux, `//127.0.0.1/x` becomes `/127.0.0.1/x`. This alone would leave future raw `file:` sites exposed,
so use it **in addition to** the central rule, not instead of it.

**Twins.** Add classifier rows:
- `file.export` / `file://127.0.0.1/E$/out.nxpkg`: recorded as written, NetworkExport (today:
  `file://127.0.0.1`, Host);
- `mesh.publish` / `file://localhost/share`: NetworkExport;
- the Uri overload `new Uri("file://localhost/x")` with family `file.export`: NetworkExport;
- site twins: `SneakernetTransport.ExportAsync("//127.0.0.1/x/out")` and
  `new FileBasedSharedAdaptationStore("//127.0.0.1/share", …)` record NetworkExport. On Linux these
  write to `/127.0.0.1/…`, so the test should use a temp-rooted path with the same `//` prefix shape, or
  call only the classifier.

Mutation: drop the `file:` early return and the site twins go red (Host).

**Records it changes.** None of the existing rows change (F3.11). Remove the Known-limits bullet
(`docs/EgressInventory.md:19`), add the rule to the classifier's remarks (`EgressDestinations.cs:11-20`),
and update SPEC-007 lines 44-49 and the CHANGELOG.

### 2.4 Side findings outside the three gaps (verify; not designed here)

- **S1, Ollama cloud models (E8).** Several records treat a loopback Ollama as Host (SystemHigh):
  EG-MDL-01, 07, 10, 11 and 14. A cloud model such as `*-cloud` sent to `localhost:11434` reaches
  ollama.com. The per-call model id is visible to `EgressGuardChatClient` (`options?.ModelId`) and to
  `OllamaProvider`. A per-call rule ("a model id ending in `-cloud` or `:cloud` is `https://ollama.com`,
  ExternalModel") would close it. It needs Ollama's documented naming checked first.
- **S2, the SNS signing host check (E9, F2.13).** `*.amazonaws.com` admits tenant-controlled API Gateway
  hosts. An unauthenticated inbound SNS POST can therefore make the API GET an attacker's host before
  any signature check, and with auto-redirect bounce that GET to any HTTPS host. The cert-chain check
  still prevents forgery. Narrowing the check to `sns.<region>.amazonaws.com(.cn)` and turning redirects
  off (§2.2 R-a) fixes both. This is a static reading.
- **S3, residual redirect blind spots.** Two explicit-route sites (EG-MDL-11, EG-WEB-01) use clients
  their callers supply (F2.17); redirects there are the caller's. A handler of an unknown type is
  another blind spot: a non-`HttpClientHandler`/`SocketsHttpHandler` primary or inner handler that
  follows redirects itself, such as `WinHttpHandler` (which could be added as a known type) or a
  third-party handler. List them in Known limits.

---

## 3. Order: what can ship before PR 4 without an owner decision

**4a-1, records and netstandard2.0 (decision-free, small).**
1. `file:` is never Host. This is the central classifier rule, with optional `GetFullPath` at
   EG-MESH-07/08.
2. EG-MESH-03 records `mesh-peer:<ip>`.
3. EG-MDL-01 reads `ProviderUri`, uses the type check for LLamaSharp, and fails closed to `meai:<key>`.
   It also turns redirects off on `OllamaHttpChatClient`'s own client.
4. Gap 1 option C: the netstandard2.0 asset fails closed for a synchronous `Send`, and
   `CreateDelegatingHandler` throws `PlatformNotSupportedException` there on a runtime with a
   synchronous `Send`. C is right under every answer to D1. With A it still covers .NET 5, and with B it
   is the enforcement.

All four only correct records or fail closed. Each has a twin that is red today. Each touches the records
listed in §2.3 and §2.1. It is one PR or two, depending on review size.

**4a-2, the redirect mechanism at parity (decision-free under the 3a answer that the guard covers every
factory client, SPEC-007:335).**
- The R-a flips, `EgressRedirectHandler` with per-hop evaluation and runtime-parity following (P1), and
  the factory filter.
- SNS stops following, and Bedrock gets `AllowAutoRedirect = false`.
- In report-only mode the visible changes are the extra per-hop records and the flipped property.
  Redirect behaviour is otherwise the runtime's.
- If the owner answers D2 with P2 (same-host only) for the clients Ashlar builds, that is a one-line
  policy flag on top of 4a-2, so 4a-2 need not wait for D2.
- If the owner wants host-app clients left alone until PR 4's host-app enforcement answer (D3), the
  filter takes a name allow-list. 4a-2 can default to all clients and be narrowed later.

**Needs an owner decision:** D1 (supported runtimes and TFM), D2 (cross-host policy), D3 (host-app scope
of the redirect change). None of them blocks 4a-1.

---

## 4. Decisions for the owner

### D1. Which runtimes the guard's synchronous coverage supports: .NET 5–7 on the netstandard2.0 asset

- **Options:**
  - A: add a `net6.0` (or `net5.0`) TFM to `Ashlar.Abstractions`.
  - B: state that .NET 5–7 is unsupported (documentation), optionally with B-hard, a `buildTransitive`
    error for .NET 5–7 consumers.
  - C only: fail closed at run time, with no support statement.
  - D: document only.
- **Consequences:**
  - **A** keeps .NET 6/7 users working with full evaluation. It costs:
    - EOL-TFM suppressions (NETSDK1138 and the STJ TFM warning);
    - an analyzer sweep of the `#if NET8_0_OR_GREATER` fallbacks;
    - a fourth inner build on cert-gate and kernel-gate;
    - no CI runtime for it, and a new advisory consumer lane would be needed to test it.

    It commits Ashlar to compiling an EOL target, and .NET 5 still needs C.
  - **B** costs a paragraph.
    - B-hard breaks every .NET 5–7 consumer of every netstandard2.0 Ashlar package at build time. It is
      bypassable with a property, and is the repo's first `buildTransitive` file.
    - The netstandard2.0 asset's real audience (Unity, .NET Framework, Mono) is unaffected.
  - **C** without a statement leaves users surprised by a `NotSupportedException`.
  - **D** fails §7.
- **Recommendation:** C (in 4a-1) plus B, without B-hard. State in `docs/SdkCompatibilityPolicy.md`:
  - full egress-guard coverage needs net8.0 or later;
  - the netstandard2.0 asset is for .NET Framework, Mono and Unity;
  - on .NET 5–7 a synchronous send through `EgressHttp` is refused.

  Do not add an end-of-life TFM. .NET 5–7 have been out of support since 2022–2024, and Ashlar's own
  netstandard2.0 dependency, STJ 10, already warns on them.

### D2. Cross-host redirect policy for the clients Ashlar builds (A2A, MCP, gRPC, the mesh CLI, MeshDirector, ProviderFactory, the cloud probe)

- **Options:**
  - **P1, parity:** follow what the runtime would follow, and evaluate each hop.
  - **P2, same-host only:** a cross-host 3xx is returned to the caller. Same-host redirects (a trailing
    slash, HTTP to HTTPS) are followed and evaluated.
  - **P3, never follow.**
  - **P4, per-client opt-in** (an `EgressHttp` parameter).
- **Consequences:**
  - **P1** changes nothing a user sees. Under enforcement each hop is checked against the label, but the
    guard is not an SSRF defence: a hop to a link-local or internal address is classified by family and
    may be allowed. The A2A and MCP API-key headers still go to the redirect target (F2.12).
  - **P2** matches the repo's own reasoning (`MeshAutoPullService.cs:48-50`) and stops API-key leaks
    across hosts. It keeps E6-style trailing-slash redirects and Caddy's HTTP-to-HTTPS redirects working.
    It would break a third-party A2A or MCP server, or an OpenAI-compatible proxy, that redirects across
    hosts; none is known in the repo.
  - **P3** is the simplest, and breaks the same-host cases.
  - **P4** adds public API.
- **Recommendation:** P2 for the clients Ashlar builds itself (`EgressHttp.CreateClient` and `Wrap`),
  and P1 for factory clients (see D3). Ship the mechanism at P1 in 4a-2 and turn on P2 as a flag once
  the owner agrees.

### D3. Whether the redirect change applies to host-app factory clients

- **Options:**
  - All factory clients (the filter flips every known primary and follows at parity).
  - Ashlar-registered client names only: host-app clients keep the runtime's following, and only their
    first hop is recorded.
  - Defer until PR 4's host-app enforcement answer.
- **Consequences:**
  - **All:** records are complete, and observable redirect behaviour is preserved by parity (P1).
    Host-app handlers' `AllowAutoRedirect` now reads `false`. Already-started shared primaries and
    unknown primary types stay residuals.
  - **Ashlar-only:** host-app redirects stay unevaluated, so enforcing on host-app clients would be
    unsound later. It needs a maintained name list.
  - **Defer:** blocks nothing in 4a-1, but leaves the factory half of gap 2 open.
- **Recommendation:** all factory clients at P1. This follows the 3a answer that the guard covers every
  factory client (SPEC-007:335). Whether host-app hops are *refused* remains the separate PR 4 host-app
  enforcement decision.

---

## 5. Risks

1. **The redirect handler.** `EgressRedirectHandler` re-implements security-relevant runtime behaviour.
   A parity bug (the method rewrite, header clearing, the fragment, the limit, HTTPS-to-HTTP) is a new
   defect class. The differential twin against the real `SocketsHttpHandler` (§2.2) is the mitigation,
   and it must be mutation-checked.
2. **Flipping a primary.** Setting `AllowAutoRedirect` on a shared primary that has already sent throws
   (E3). Those primaries, and unknown handler types that follow redirects themselves, keep following
   unevaluated. List them as residuals; they are not covered.
3. **The netstandard2.0 refusal.** It is not a guard decision: it is not recorded, and its explanation is
   the runtime's generic message plus the type name. `Wrap` under an outer invoker may double the
   System.Net.Http telemetry on .NET 5–7.
4. **Test churn in the EG-MDL-01 fix.** Four MEAI twins move off `FakeChatClient`. If they are only
   re-pointed, without keeping the resolver-precedence assertions on the default inner, coverage of the
   resolver order is lost.
5. **EG-MESH-03 under enforcement.** With no subject frame, every mesh serve is refused under AirGapped
   and SecureWorkstation until the served package's label becomes the subject (gap 4 or PR 5). The
   record fix makes that visible; it does not cause it.
6. **The cost of option A.** It is likely under-estimated: the end-of-life TFM warnings, analyzer
   differences on net6.0, and maintenance for as long as the target exists.
7. **Changing the SNS registration.** If it sets its own primary handler, it overrides the prod-style
   test's stub (F2.6), and that test would send to the network. Use the configure-existing overload.
8. **Scanner and TSV.** The scanner counts `new HttpClientHandler(` and `new HttpMessageInvoker(` (F2.18),
   so `OllamaHttpChatClient.cs` goes from 1 to 2, and the netstandard2.0 hop adds an `Exempt:GuardImpl`
   row. Run F1/F2 before committing the TSV.
9. **Out-of-lens findings that could invalidate Host records.** Ollama cloud models (S1) and the SNS
   host check (S2) are external or static readings and need verification.

## 6. Claims that need a build or test to settle

- E1 (the base `HttpMessageHandler.Send` throws on .NET 5 and later): settled by the netstandard2.0-on-8.0
  harness, which is itself the C twin.
- E2 (the runtime's redirect rules): settled by the differential twin.
- E3 (the factory filter ordering, the default primary type, and the started-handler setter): settled by
  the factory twins.
- E4 (NETSDK1138 and the STJ warning under `TreatWarningsAsErrors`, and CA1510 on net6.0 fallbacks): only
  an option-A build settles them.
- Whether `ClientConfig.AllowAutoRedirect` exists in AWSSDK.Core 4.0.100.4.
- Whether the AWS MEAI adapter (AWSSDK.Extensions.Bedrock.MEAI 4.0.101.8) exposes `ChatClientMetadata.ProviderUri`.
  If it does, the same EG-MDL-01 fix also closes the Bedrock reconstruction limit
  (`docs/EgressInventory.md:12`).
- Whether the OTLP exporter 1.15.3 uses factory clients (F2.15).
- E6 (Starlette redirects), E7 (forwarded headers with the slim builder), E8 (Ollama cloud models), E9.
