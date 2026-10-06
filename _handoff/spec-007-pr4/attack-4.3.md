# Attack list for SPEC-007 PR 4.3 (redirects), for the verifier

Scout, read-only, against master `de41a8ac8` (host checkout `/home/user/Ashlar`). Every `file:line` below was read at that
commit. **[unverified]** marks a claim about the .NET runtime, a NuGet package or filter ordering that I could not settle by
reading this repository (no NuGet cache exists on the host, so no package XML docs were available). **[needs a test]** marks
the same where the design already says so. Design citations are `DESIGN §/line` into
`scratchpad/ws/DESIGN-4-final.md`; the owner answers at its end (`:1156-1172`) override its §3.A, and phase B superseded D9
(IVT list also has `Ashlar.Orchestration`), D31 (no record for the ns2.0 refusal) and §2.2's `Detach()`.

A new owner decision of 2026-10-06 binds 4.5 and shapes every twin here that uses a frame: while a read scope
(`EgressSubject.BeginRead`) is open and unreported, every egress decided on the flows inside it is decided at SystemHigh.
So a 4.3 twin that wants a `Current` below SystemHigh must enter a frame and hold **no** open read scope across the send
(or `Report` + `Complete` it before the send). All twins below default to the no-frame case, `Current = SystemHigh`,
`CurrentBasis = no-subject` (`EgressGuard.cs:15-18`, `EgressSubject` as merged by 4.4).

---

## 0. Baseline facts on master the attacks rest on

**The guard handler evaluates one URI per send and stashes nothing.**
- `EgressGuardHandler.SendAsync` calls `Report(request)` then `base.SendAsync` (`EgressGuardHandler.cs:52-57`); `Send` the
  same under `#if NET5_0_OR_GREATER` (`:59-66`). `Report` reads `request.RequestUri` only, builds an `EgressRequest` with
  the handler's family and site, and discards the decision: `_ = (_guard ?? EgressGuard.ProcessDefault).Evaluate(egress)`
  (`:76-80`). A guard that throws is swallowed and counted (`:82-87`). The class remarks promise "exactly once, before
  handing the request on" (`:8-9`); `EgressHttp`'s remarks repeat it (`EgressHttp.cs:7-8`). DESIGN `:565` says this wording
  changes in 4.3.
- Nothing refuses: `EgressGuard` remarks "Nothing refuses yet" (`EgressGuard.cs:29-32`); `EgressDecision.Refused` is
  `Mode == enforce && !Access.Allowed` and "until SPEC-007 PR 4.7 no route acts on it" (`EgressDecision.cs:56`, `:144-148`).
- Every profile resolves `report` with basis `profile:<canonical>`; a blank profile is `full` (`EgressEnforcement.cs:121-123`,
  `:136-140`); an `enforce` override gives `(enforce, override)` (the 4.6 table, `pr-4.6-body.md:5-7`).

**What the primaries do today.**
- `EgressHttp.CreateClient(family, site[, guard])` builds `new HttpClientHandler()` with every default, so
  `AllowAutoRedirect = true` (`EgressHttp.cs:57`). `CreateClient(inner, …)` and `Wrap(inner, …)` pass `inner` straight into
  `Guarded` and touch nothing on it (`:72-80`, `:101-107`, `:138-147`). The `Wrap` remarks forbid replacing `InnerHandler`
  (`:97-99`).
- On the netstandard2.0 asset `Guarded` puts `SynchronousSendRefusedOnNetstandard20Asset.Over(inner)` under the guard
  (`EgressHttp.cs:140-143`). That hop is an `HttpMessageHandler` (not a `DelegatingHandler`) that forwards `SendAsync`
  through an owned `HttpMessageInvoker` (`SynchronousSendRefusedOnNetstandard20Asset.cs:32`, `:46`, `:70-71`) and exposes
  the caller's handler as `internal HttpMessageHandler Inner` (`:64`); its remarks say a chain walker "steps through
  `Inner` here" (`:29-30`). The record for this hop is the owner's 2026-10-06 amendment: **none** (`SPEC-007:425`;
  `docs/EgressInventory.md:14`).
- The factory binding inserts `EgressHttp.CreateDelegatingHandler(HttpFactory, "factory:" + name, guard)` at
  `AdditionalHandlers` index 0 through one `ConfigureHttpClientDefaults` + `ConfigureAll<HttpClientFactoryOptions>` action
  (`EgressServiceCollectionExtensions.cs:63-69`). There is no `IHttpMessageHandlerBuilderFilter` on master and the primary
  is never read or touched. The guard is resolved from DI per build, falling back to `null` (= `ProcessDefault`) on any
  failure (`:76-105`). The remarks already say the factory's own logging-scope handler sits **outside** the guard (`:19-22`).
- The redirect gap is written down: redirects "are not evaluated, because the redirect loop runs below every
  `DelegatingHandler`. On a 307 or 308 the method and body are re-sent and only `Authorization` is cleared, so a first hop
  recorded as Host (a loopback URL) can deliver its body to a remote host that no decision names"
  (`docs/EgressInventory.md:15`; `SPEC-007:48-49`). The 4.1 Known limit "a chain walker stops at the synchronous-send hop"
  is `docs/EgressInventory.md:23`.

**Records.** `Destination` is `scheme://host[:port]` only, from `UriComponents.SchemeAndServer`
(`EgressDestinations.cs:196`; `EgressDecision.cs:78-84`). A relative URI makes `Classify` throw, which `Evaluate` records as
`Fault` with `default(AccessDecision)` (`EgressDestinations.cs:192-193`; `EgressGuard.cs:130-138`). Host class: `unix`,
`npipe`, `localhost`, `*.localhost`, loopback IP (`EgressDestinations.cs:140-161`, `:195`); never `file`. Family decides
the rest (`:76-104`): `http`, `http.factory`, `a2a`, `grpc`, `mcp`, `mesh.*` → NetworkExport (Internal); `model.*` →
ExternalModel (Internal). `CanWrite(SystemHigh, X)` is `SystemHighData` unless `X` is SystemHigh (`ReferenceMonitor.cs:53-64`);
a higher current than destination level is `LevelTooLow` (`:66-72`).

**The production clients 4.3 touches.** `EgressHttp` sites (all get R-a's flip and D33's P2):
`DefaultGrpcChannelFactory.cs:76` (Wrap over a configured `HttpClientHandler`), `McpClientConnectionManager.cs:330`,
`A2AAgentTransport.cs:169`, `ProviderFactory.cs:61` (EG-MDL-03, static) and `:814` (EG-MDL-07),
`CloudAvailabilityResolver.cs:98`, `MeshCommand.cs:372`, `MeshAutoPullService.cs:72` (over its own
`SocketsHttpHandler { AllowAutoRedirect = false }`, `:57`), `WorkflowCommand.cs:535`, `IdeEndpoints.cs:193` (fallback only),
`MeshDirectorCommand.cs:415`. Factory registrations (all get R-c and D33's P1): `AshlarServiceCollectionExtensions.cs:136`
(the unnamed default client: remote bricks, peer executor, webhooks per TSV row 51), `AshlarKernelRegistrar.Phases.cs:778`
(`AshlarExecution`), `RunPodCapabilityRoutingServiceCollectionExtensions.cs:50-56` (typed RunPod, default
`https://api.runpod.io`), `NodeCapabilityRuntimeServiceCollectionExtensions.cs:136-140`,
`ModelArtifactCatalogServiceCollectionExtensions.cs:38-55` (three; the third defaults to `https://ollama.com`, `:53`),
`MeshLabServiceCollectionExtensions.cs:36` and `FleetServiceCollectionExtensions.cs:116` (`MeshLabWorkerExecutorClient`,
whose `BaseAddress` comes from a director's task list, `MeshLabWorkerExecutorClient.cs:155-156`, `:228-230`),
`src/Ashlar.Client/ServiceCollectionExtensions.cs:20-27` (consumer SDK, adds `X-Ashlar-Api-Key` as a default header, `:26`),
and the two SNS signing clients `application/src/Ashlar.API/Program.cs:171` and
`commercial/src/Ashlar.Commercial.Fleet.Host/Program.cs:79`.

---

## 1. Numbered attack list

Each item: (1) the exact call path today; (2) what the design says and where; (3) the twin; (4) the mutation that exposes a
fake fix and the assertion that goes red. "Decision fields" name `EgressDecision` members. Twins run in the cert-gate
project (`src/Ashlar.Tests.Infrastructure/Tests/Certification/`), subscribe with `EgressDecisionLog.Subscribe(sink)`
(`PublicAPI.Unshipped.txt`), and should pass an explicit guard, `new EgressGuard("full")` or `new EgressGuard("full",
"enforce")`, so no test needs the `EnvironmentVariables` collection (D4: an explicit-profile guard never reads the
environment, `EgressGuard.cs:180-182`, `:188-189`).

### 1. The baseline hole: a factory client's primary follows a 307 from loopback to a remote host with the body

1. **Today.** `IHttpClientFactory.CreateClient("x")` → pipeline `[EgressGuardHandler, …host handlers…, primary]`
   (`EgressServiceCollectionExtensions.cs:63-69`). The guard evaluates `http://127.0.0.1:P/a` → Host, allowed (`EgressGuardHandler.cs:76-80`;
   `EgressDestinations.cs:195`). The default primary (`HttpClientHandler`/`SocketsHttpHandler`, `AllowAutoRedirect = true`)
   receives `307 Location: http://remote.example/b`, re-sends method and body to `remote.example` below every
   `DelegatingHandler`. One decision, naming the loopback (`docs/EgressInventory.md:15`).
2. **Design.** R-c flips the known primary and wraps it in `EgressRedirectHandler` (DESIGN `:538-541`); R-b evaluates every
   hop whose authority differs (`:519-523`); D33 P1 for factory clients: follow and evaluate each hop (`:537`, `:831`).
   Twin list: "a factory twin", "a differential twin against the real `SocketsHttpHandler` over loopback Kestrel" (`:556-557`).
3. **Twin (factory, real primary).** Loopback Kestrel A answers `/a` with `307 Location: http://127.0.0.1:Q/b`
   (Kestrel B, a second loopback port, stands in for the remote; the record still proves hop 2 was evaluated because its
   destination names port Q). Register `services.AddSingleton<IEgressGuard>(new EgressGuard("full"))` **before**
   `AddAshlarEgressGuard()` (TryAdd keeps it, `EgressServiceCollectionExtensions.cs:59`), `AddHttpClient("x")` with the
   default primary, POST a body to `/a`. Expect: B sees exactly 1 request at `/b` with the body (runtime parity on 307);
   exactly 2 decisions, both `Family = http.factory`, `Site = factory:x`; hop 1 `Destination = http://127.0.0.1:P`,
   `DestinationClass = Host`, `Access.Allowed = true`; hop 2 `Destination = http://127.0.0.1:Q`, Host, allowed; both
   `Current = SystemHigh`, `CurrentBasis = no-subject`, `Mode = report`, `ModeBasis = profile:full`, `Refused = false`.
   For a remote-shaped hop use a stub primary (item 9) with `Location: http://remote.example/b`: hop 2 `Destination =
   http://remote.example`, `DestinationClass = NetworkExport`, `DestinationLabel = Internal`, `Access.Reason = SystemHighData`,
   `Access.Allowed = false`, `Refused = false` under report.
4. **Mutation.** Drop the flip in R-c (the primary keeps `AllowAutoRedirect = true`). With the real primary the runtime
   follows internally: B still sees `/b`, but the decision count is 1 → the "2 decisions, hop 2 names port Q" assertion goes
   red. A second mutation, the follower not following when the primary originally followed, leaves B at 0 requests and the
   caller holding a 307 → the "B sees 1 request" assertion goes red.

### 2. P2 vs the design's own stub twin: a cross-host 3xx through an `EgressHttp` client must come back, not be followed

1. **Today.** `EgressHttp.CreateClient(stub, family, site, guard)` with a `RedirectingStub : HttpClientHandler` whose
   `SendAsync` override returns `307 Location: http://remote.example/b` (the design's twin shape, DESIGN `:555`): the guard
   evaluates hop 1; the stub's override returns the 307 without following; 1 decision, 307 to the caller.
2. **Design.** D33 P2: "for `EgressHttp`-built clients: a cross-host 3xx is returned to the caller" (`:534-536`). But the
   twin line says "today 1 decision, after 2" (`:555`). Both cannot hold for an `EgressHttp` client with a cross-host 307.
3. **Twin.** (a) `EgressHttp.CreateClient(new RedirectingStub(cross-host 307), EgressFamilies.Http, site, new EgressGuard("full"))`:
   expect the response status `307`, `stub.Sends == 1`, exactly **1** decision (hop 1 only); the `Location` header is
   intact on the returned response. (b) The same stub behind a factory client: expect 2 decisions and `stub.Sends == 2`
   (P1). (c) `EgressHttp` client, **same-authority** 302 (`Location: /b` relative): expect the follower to follow,
   `stub.Sends == 2`, still **1** decision (same authority is not re-evaluated, R-b `:519`), final status 200.
4. **Mutation.** Make the follower follow cross-host for `EgressHttp` clients (P2 → P1): (a) goes red on "status 307,
   1 decision" (it sees 200 and 2 decisions). Mutation 2, follower never follows at all: (c) goes red on `stub.Sends == 2`.

### 3. "Authority" is not "destination": a scheme downgrade or a port change on the same host is not re-evaluated

1. **Today.** Not applicable: nothing re-evaluates. After the PR the compare key decides what is skipped.
2. **Design.** "Every send whose authority differs from the last one evaluated is re-evaluated" (`:519`). The record's
   destination is `scheme://host[:port]` (`EgressDestinations.cs:196`). `Uri.Authority` is `host[:port]` with no scheme, and
   a compare on `Uri.Host` alone drops the port.
3. **Twin.** Through a factory client over a stub primary: evaluated `https://remote.example:8443/a`, then a rewriting
   handler between the guard and the redirect handler (item 4's shape) sets `RequestUri = http://remote.example:8443/a`
   (same `Authority`, different scheme). Expect a second decision with `Destination = http://remote.example:8443`.
   Second row: `https://remote.example/a` → `https://remote.example:8443/a` (same `Host`, different port): second decision
   `Destination = https://remote.example:8443`. Third row: HTTPS→HTTP via a **redirect** on the same host:port must not be
   followed at all (item 12), so this row is only reachable through the rewriter.
4. **Mutation.** Compare `Uri.Authority` (or `Uri.Host`) instead of `GetComponents(SchemeAndServer)`: rows 1 (and 2) go red
   with 1 decision instead of 2.

### 4. A host `DelegatingHandler` between the guard and the primary rewrites `RequestUri` (service discovery, base-address rewriters)

1. **Today.** `AddHttpClient("x").AddHttpMessageHandler(() => new Rewriter())` runs **inside** the guard
   (`EgressServiceCollectionExtensions.cs:18-22`): the guard evaluates `http://svc.localhost/a` (Host via `*.localhost`,
   `EgressDestinations.cs:148-150`), the rewriter sets `request.RequestUri = http://10.0.0.5/a` (a private address is not
   Host, DESIGN `:64`) and the primary sends it. One decision, Host, allowed. No redirect is involved.
2. **Design.** R-b: the innermost placement "also covers a `DelegatingHandler` between the guard and the primary that
   rewrites `RequestUri`" (`:522-523`); D32; twin "a URI-rewriting inner-handler twin (a rewrite to a remote host is
   evaluated)" (`:560`); SE6 (`:1089`).
3. **Twin.** Factory client; stub primary; `AddHttpMessageHandler` rewriter `http://svc.localhost/a` → `http://remote.example/a`.
   Expect 2 decisions: hop 1 `Destination = http://svc.localhost`, Host, allowed; hop 2 `Destination = http://remote.example`,
   NetworkExport/Internal, `Access.Reason = SystemHighData`, `Refused = false` (report). The stub sees exactly 1 request
   whose `RequestUri` is `http://remote.example/a`.
4. **Mutation.** Skip the pre-send compare in `EgressRedirectHandler` (evaluate only on a 3xx): the rewrite twin goes red
   with 1 decision.

### 5. A cloning handler between the guard and the redirect handler drops the stash

1. **Today.** Hedging and retry handlers commonly build a fresh `HttpRequestMessage` per attempt (headers copied, `Options`
   not). On master nothing reads `Options`, so nothing breaks.
2. **Design.** "The guard handler stashes the evaluated authority in `request.Options`. Before each send, the redirect
   handler compares" (`:520-521`). The design does not say what a missing stash means.
3. **Twin.** Factory client; `AddHttpMessageHandler` cloner that sends `new HttpRequestMessage(request.Method,
   request.RequestUri)` (no `Options` copied) to a stub primary; request to `http://remote.example/a`. Expect the redirect
   handler to treat a missing stash as "never evaluated" and evaluate: 2 decisions, both `http://remote.example`. Second
   row: the cloner also rewrites to `http://other.example/a`: hop 2 names `http://other.example`. (A duplicate decision for
   the same authority is the fail-closed cost; the PR body states it.)
4. **Mutation.** Treat "no stash" as "already evaluated" (skip): both rows go red with 1 decision; row 2 sends
   `other.example` unevaluated.

### 6. A composite primary: the rewriter is *inside* the primary, below the redirect handler

1. **Today.** `ConfigurePrimaryHttpMessageHandler(() => new Rewriter { InnerHandler = new SocketsHttpHandler() })` is a
   legal primary. `builder.PrimaryHandler` is then a `DelegatingHandler`, not a known type.
2. **Design.** "For a known primary type, it flips the type and wraps it in `EgressRedirectHandler`" (`:541`); "Placement:
   innermost, directly above the primary" (`:517`). A rewriter inside the primary runs below the redirect handler, so R-b
   never sees its rewrite. "Unknown primary types … after the send, the guard handler compares
   `response.RequestMessage.RequestUri` with the evaluated authority" (`:546-548`).
3. **Twin.** Composite primary `Rewriter { InnerHandler = StubPrimary }`, rewrite `http://127.0.0.1:P/a` →
   `http://remote.example/b`; the stub sets `response.RequestMessage = request` (what `SocketsHttpHandler` does
   **[needs a test]**). Expect: (a) the walker treats a composite primary as **unknown** even though its tail is a known
   type (or walks to the tail and flips it, but still applies the post-send check, since the rewrite is below the redirect
   handler); (b) a post-send decision `Destination = http://remote.example`, NetworkExport, `SystemHighData`, plus one
   Warning log line ("the body may already have gone", `:548`); (c) on AG/SW a startup Warning naming the primary type
   (`:549`), pinned through `AddAshlar(AirGapped)` in the `EnvironmentVariables` collection with the reset seam.
4. **Mutation.** Walk to the tail, flip it, and skip the post-send check because the tail is known: the twin goes red on
   the missing second decision.

### 7. An unknown primary that follows internally and hides the final URI defeats the post-send check (design limit)

1. **Today.** Any `HttpMessageHandler` subclass as primary (every test stub is one: `EgressFactoryDefaultsTwinTests.cs:374`,
   `EgressKernelFactoryTwinTests.cs:167`, `EgressHttpHandlerTwinTests.cs:465`). Such a primary that performs its own
   redirect loop with a cloned request leaves `response.RequestMessage` at the original request, or `null`.
2. **Design.** The post-send compare (`:546-548`) and SE6's rejection of boot failure for unknown primaries (`:1089`). It is
   advisory: nothing can evaluate a hop the primary hid.
3. **Twin (pins the limit, not a fix).** Unknown primary that, on a 307 from loopback, re-sends internally to
   `remote.example` and returns the final 200 with `RequestMessage = <original request>`. Expect exactly 1 decision and
   **no** Warning: this is the recorded Known limit for `docs/EgressInventory.md` and the SPEC-007 4.3 line. Also pin the
   positive: with `RequestMessage` set to the mutated request, the post-send decision appears (item 6).
4. **Mutation.** None turns this red; list it as "not observed failing: a design limit, recorded". The verifier checks
   the Known-limit sentence exists.

### 8. A started primary: the `AllowAutoRedirect` setter throws and the flip is silently lost

1. **Today.** `EgressHttp.CreateClient(inner, …)` accepts any handler, started or not. `HttpClientHandler.AllowAutoRedirect`
   and `SocketsHttpHandler.AllowAutoRedirect` setters throw `InvalidOperationException` once the handler has sent
   **[needs a test]** (DESIGN `:508-509`, Appendix B `:1144`).
2. **Design.** "The flip is inside a try: the setter throws on a started handler" (`:508-509`). It does not say what the
   handler then does: if it records "flipped" or "originally false", the primary keeps following and the follower stays
   inert → redirects go back to being unevaluated.
3. **Twin.** `var primary = new SocketsHttpHandler(); using (var raw = new HttpClient(primary, false)) await
   raw.GetAsync(loopbackA + "/ok");` then `EgressHttp.CreateClient(primary, EgressFamilies.Http, site, guard)` and GET
   `/a` on loopback A, which 307s to loopback B. Expect fail-closed: either the flip is reported as failed and the handler
   is treated as **unknown** (post-send check fires, B's hop is recorded, Warning), or `CreateClient` throws an explained
   exception. Not acceptable: 200 from B with 1 decision and no Warning.
4. **Mutation.** Catch the setter exception and continue as if flipped: the twin goes red (1 decision, B saw `/b`, no
   Warning). This is also the mutation that proves the `try` is observed failing (CLAUDE.md's rule).

### 9. "Known primary type" by exact type misses subclasses (the design's own `RedirectingStub : HttpClientHandler` is one)

1. **Today.** `HttpClientHandler` is not sealed; `SocketsHttpHandler` is. `DefaultGrpcChannelFactory.ConfigureHandler`
   returns a plain `HttpClientHandler` (`DefaultGrpcChannelFactory.cs:78-88`), but a host may pass a subclass to
   `Wrap`/`CreateClient(inner)` or as a factory primary.
2. **Design.** "flip `HttpClientHandler` or `SocketsHttpHandler`" (`:507-508`); "For a known primary type" (`:541`).
3. **Twin.** `class Sub : HttpClientHandler { }` (no override) over loopback Kestrel A (307 → B), through
   `EgressHttp.CreateClient(new Sub(), …)`: expect `AllowAutoRedirect == false` on the instance after construction, B sees 0
   requests on a cross-host-shaped hop (use a `Location` to a second **hostname**: `http://localhost:Q/b` vs
   `http://127.0.0.1:P/a` differ in authority and are both Host, so P2 returns the 307 and the record stays at 1), and the
   returned status is 307.
4. **Mutation.** `GetType() == typeof(HttpClientHandler)` instead of `is HttpClientHandler`: the twin goes red
   (`AllowAutoRedirect` reads true; B sees `/b`; status 200).

### 10. `ConfigureAdditionalHttpMessageHandlers((h, _) => h.Clear())` removes the guard; and the `[0]` check vs filter ordering

1. **Today.** The guard is inserted at index 0 by an options action (`EgressServiceCollectionExtensions.cs:64-65`). A
   client's own action registered **after** `AddAshlarEgressGuard` runs after it and can `Clear()` the list: zero decisions.
   (A `Clear()` registered before runs first and the guard survives.)
2. **Design.** "It checks that the guard handler is still `AdditionalHandlers[0]` … If the guard handler is missing, the
   filter re-inserts it and logs a Warning" (`:542-544`); twin "a `Clear()` twin" (`:561`); M12 (`:991`).
3. **Twin (Clear).** `AddAshlarEgressGuard(); AddHttpClient("x").ConfigureAdditionalHttpMessageHandlers((h, _) => h.Clear())
   .ConfigurePrimaryHttpMessageHandler(() => stub)`: expect exactly 1 decision per send with `Site = factory:x` and one
   Warning. **Twin (ordering).** On a fresh collection call `AddAshlarEgressGuard()` **before** any `AddHttpClient()`, then
   `AddHttpClient("x")` over a stub: expect exactly 1 decision per send and exactly one `EgressGuardHandler` in the built
   pipeline. Reason: the factory's `LoggingHttpMessageHandlerBuilderFilter` inserts `LoggingScopeHttpMessageHandler` at
   index 0 in its post-`next` step, and filters registered earlier run their post-step **later** **[unverified]**: if
   Ashlar's filter is registered first, `AdditionalHandlers[0]` is the logging scope handler when Ashlar's post-step runs, a
   `[0]` check misfires, and a re-insert gives two guards and two decisions per send. The master remarks already know the
   logging scope handler sits outside the guard (`EgressServiceCollectionExtensions.cs:21-22`). Whether
   `ConfigureHttpClientDefaults` itself calls `AddHttpClient(services)` (which would register the logging filter before
   Ashlar's) is **[unverified]**; the twin settles it either way.
4. **Mutation.** Drop the re-insert: the Clear twin goes red with 0 decisions. Mutation 2: check `[0]` instead of
   `Any(h => h is EgressGuardHandler)`: the ordering twin goes red with 2 decisions, if the ordering is as described; if
   it is not, the twin is still the evidence.

### 11. Relative, scheme-relative, dot-segment, missing and invalid `Location`; 300/304/305

1. **Today.** The runtime resolves `Location` against the request URI, ignores a missing or unparsable `Location` (returns
   the 3xx), and follows only 301, 302, 303, 307, 308 **[needs a test]** (DESIGN `:525-526`, Appendix B `:1144`).
2. **Design.** "resolve a relative `Location`; keep the fragment" (`:526`); the differential twin lists "relative and
   missing `Location`" (`:558`).
3. **Twin (differential, loopback Kestrel vs the follower).** For each of: `Location: /b`; `Location: ../b`;
   `Location: //127.0.0.1:Q/b` (scheme-relative: resolves to another authority → P2 returns it for `EgressHttp`, P1
   evaluates it for factory); `Location:` absent on a 302; `Location: ::not a uri::`; status 300, 304, 305 with a
   `Location`. Run the same server once under a raw `HttpClient(new SocketsHttpHandler())` and once under the guarded
   factory client; expect identical final status, identical request sequence at the server, and a decision per authority
   the follower sent to. The scheme-relative row must produce a decision naming `http://127.0.0.1:Q` (factory) or a 3xx
   returned with 1 decision (`EgressHttp`).
4. **Mutation.** Compare authorities **before** resolving `Location` (compare the raw header text's authority, or compare
   `Location` as given): the scheme-relative row goes red (treated as same-authority, followed with no decision). Mutation
   2: follow when `Location` is missing (re-send to the same URI): the "server sees exactly 1 request" assertion goes red.

### 12. HTTPS→HTTP and non-http(s) `Location` schemes

1. **Today.** The runtime refuses an HTTPS→HTTP redirect and returns the 3xx **[needs a test]**; it also ignores
   non-http(s) schemes.
2. **Design.** "refuse HTTPS → HTTP, and any scheme other than `http`/`https`" (`:527`).
3. **Twin.** Loopback Kestrel with a dev certificate (`https://127.0.0.1:P`) answering `307 Location: http://127.0.0.1:P/b`
   (same host and port, scheme only); and rows with `Location: file:///etc/passwd`, `unix:///var/run/x`, `ftp://x/`.
   Expect the 307 returned, B/`/b` never hit, **1** decision. The same-host:port downgrade row is why item 3's compare key
   must include the scheme: if the follower did follow it, the record would have to name `http://…`, not `https://…`.
4. **Mutation.** Drop the scheme check: the downgrade row goes red (`/b` served, status 200).

### 13. Method and body rewrite on 301/302/303 vs 307/308, and which credentials survive a hop

1. **Today.** The runtime turns POST into GET and drops the body on 301/302 and on 303, keeps method and body on 307/308,
   and clears `Authorization` **[needs a test]** (which hosts and when is Appendix B `:1144`). Custom credential headers are
   not cleared: the consumer SDK sets `X-Ashlar-Api-Key` as a default header (`Ashlar.Client/ServiceCollectionExtensions.cs:26`;
   a factory client → P1 follows cross-host); A2A adds a configured API-key header (`A2AAgentTransport.cs:178`; `EgressHttp`
   → P2 returns cross-host); MCP puts a secret in `AdditionalHeaders` (`McpClientConnectionManager.cs:320-323`; P2); RunPod
   uses `Authorization: Bearer` (`RunPodHttpClient.cs:203-205`; factory → P1, but `Authorization` is cleared).
2. **Design.** "rewrite the method on 301/302 POST and on 303; clear `Authorization`; dispose intermediate responses;
   mutate the same `HttpRequestMessage`" (`:528-531`). F2.12: P2 "stops their API-key headers following a cross-host hop"
   (`:536`) — for `EgressHttp` clients only.
3. **Twin.** Factory client with `DefaultRequestHeaders.Add("X-Api-Key", "k")` and `Authorization: Bearer t`; Kestrel A
   (`/a`, 307) → Kestrel B (`/b`). Expect at B: method POST, body present, `X-Api-Key: k` present (runtime parity, and the
   PR body must say so), `Authorization` absent. Rows for 301/302/303: method GET at B, no body. Then the same over an
   `EgressHttp` client with A on one hostname and B on another: B sees nothing (P2). Record: 2 decisions (factory), 1
   (`EgressHttp`).
4. **Mutation.** Keep `Authorization` on the follower's re-send: the "Authorization absent at B" assertion goes red.
   Mutation 2: keep POST on 303: the 303 row goes red. (These pin runtime parity; without them a "fix" that forwards every
   header would re-send bearer tokens cross-host under P1 in report mode.)

### 14. Both SNS signing clients: P1 would follow a redirect off `*.amazonaws.com` to an attacker's PEM

1. **Today.** `httpClientFactory.CreateClient("ashlar-sns-signing")` (`AwsSnsSmsWebhook.cs:72`) →
   `SnsRsaSignatureVerifier.IsAuthenticAsync` (`:74`), which checks `SigningCertURL` is `https` and its host ends with
   `.amazonaws.com`/`.amazonaws.com.cn` (`SnsRsaSignatureVerifier.cs:34-39`, `:79-81`), then `httpClient.GetStringAsync(uri)`
   (`:54`) and `X509Certificate2.CreateFromPem(pemText)` (`:61`). The client is registered with a default primary
   (`application/src/Ashlar.API/Program.cs:171`; `commercial/…/Fleet.Host/Program.cs:79`), so today a
   `302 Location: https://attacker.example/cert.pem` from any `*.amazonaws.com` host (an S3 bucket website redirect, for
   example; the suffix check admits every AWS-hosted name, DESIGN `:1048`) is followed by the runtime, the attacker's PEM is
   parsed, and only `AmazonCertificateChains.TryValidateSnsSigningCertificate` (`:62`) stands between a forged signature and
   `Results.Ok`. The guard records the first hop only (`factory:ashlar-sns-signing`, `https://sns.<region>.amazonaws.com`).
   After R-c alone, P1 **follows and evaluates** hop 2: NetworkExport/Internal, `SystemHighData`, `Refused = false` in report
   mode on every profile today, so the PEM still arrives.
2. **Design.** "Both SNS signing clients never follow" through the configure-existing overload
   `ConfigurePrimaryHttpMessageHandler((h, _) => …)` (`:513-515`); the follower follows "only when the primary originally
   followed" (`:524`); the records table marks 4.3 `[coordinated-integration]` for the SNS sites (`:876`).
3. **Twin (commercial and API, one each).** Compose the host (`EgressFleetHostTwinTests.cs:48` already builds the Fleet
   host with a stub primary; an API-host sibling exists in `EgressApiHostProdStyleTests`, `src/Ashlar.Tests.Infrastructure/Tests/VirtualProduction/EgressApiHostProdStyleTests.cs:41`), but keep the **real** primary for
   `ashlar-sns-signing` and point `SigningCertURL` at loopback Kestrel A with a hostname the verifier accepts via a hosts
   override — if that is not possible in the container, drive `SnsRsaSignatureVerifier` directly with the factory's
   `CreateClient("ashlar-sns-signing")` and a `SigningCertURL` the test shortcuts past the host check by resolving
   `sns.test.amazonaws.com` to loopback through a `SocketsHttpHandler.ConnectCallback` on the configured primary (set in
   the test's own `ConfigurePrimaryHttpMessageHandler((h, _) => …)` **after** Program.cs's, so the flip stays). A answers
   `302 Location: http://127.0.0.1:Q/cert.pem`; B serves a valid PEM. Expect: B sees **0** requests; `IsAuthenticAsync`
   returns `false` (`GetStringAsync` throws `HttpRequestException` on the 3xx, caught at `:56-58`); exactly **1** decision,
   `Site = factory:ashlar-sns-signing`, `Family = http.factory`, `Destination = https://sns.test.amazonaws.com[:P]`,
   NetworkExport/Internal, `SystemHighData`, `Mode = report`. Also assert, by resolving the typed handler from the built
   factory, that the primary's `AllowAutoRedirect` is `false` and that the Ashlar filter recorded it as **originally false**
   (never follow), not as its own flip.
4. **Mutation.** (a) The follower ignores the original value and follows whenever it is "the" follower: B sees 1 request
   → red. (b) The SNS registration uses the **replace** overload `ConfigurePrimaryHttpMessageHandler(() => new
   SocketsHttpHandler { AllowAutoRedirect = false })`: the twin stays green but `EgressFleetHostTwinTests.cs:48`'s own stub
   replacement is then order-dependent; add a fact that greps both `Program.cs` files for the `(h, _)` form (the
   convention test style of `EgressGuardConventionTests.Routes.cs`). (c) R-c reads the primary's `AllowAutoRedirect`
   **before** the client's actions ran (pre-`next`) and sees the default `true`: the follower then follows for SNS → red.

### 15. The SNS `SubscribeURL` confirmation GET uses the same client (SSRF with redirect)

1. **Today.** `HandleConfirmationAsync` GETs `SubscribeURL` with `ResponseHeadersRead` when
   `AwsSnsAutoConfirmSubscription` is on and the host ends with `.amazonaws.com` (`AwsSnsSmsWebhook.cs:114-124`, `:168-170`).
   It runs **after** signature verification (`:74-80`), so the URL is attacker-controlled only if item 14 already fell. A
   redirect from the confirm host is followed by the runtime today; the response is discarded.
2. **Design.** Covered by "never follow" on this client (`:513-515`); nothing else names the confirm GET.
3. **Twin.** Same composition as item 14; a signed-or-skipped (`AwsSnsSkipSignatureVerification` in `Testing`, `:73`)
   `SubscriptionConfirmation` whose `SubscribeURL` is loopback A with an accepted hostname, A answering `307` to B.
   Expect B sees 0 requests; 1 decision for A; the endpoint still returns its confirmation result (it ignores the status).
4. **Mutation.** As item 14 (a).

### 16. P1 follows a remote first hop *into* the host boundary (SSRF to loopback and link-local), and the label model allows it

1. **Today.** Any factory client to a remote host (RunPod `https://api.runpod.io`, the ollama.com catalog client, MeshLab
   peers whose `BaseAddress` the director chose, `MeshLabWorkerExecutorClient.cs:155-156`; RemoteBrick hosts the catalog
   chose, DESIGN defect 7 `:1044`; workflow webhooks) that answers `307 Location: http://127.0.0.1:11434/api/chat` or
   `http://169.254.169.254/latest/meta-data/` is followed by the runtime with the body. The repo names this threat itself
   for mesh pull: "an untrusted peer must not be able to bounce this node's request to an internal/link-local address"
   (`MeshAutoPullService.cs:48-50`), and turns redirects off there (`:57`).
2. **Design.** P1 for factory clients: "follow and evaluate each hop" (`:537`). Hop 2 to loopback is Host = SystemHigh, so
   `CanWrite` **allows** it at every label (`ReferenceMonitor.cs:53-54`); hop 2 to `169.254.169.254` is NetworkExport/Internal
   (a private address is not Host, DESIGN `:64`), refused only at SystemHigh current under enforce. The design has no rule
   "never follow into Host from non-Host" and no Known-limit line for it.
3. **Twin (pins what the design does; owner question below).** Factory client; stub primary; first hop
   `https://remote.example/a` (stub returns `307 Location: http://127.0.0.1:11434/api/chat`), hop 2 served by the stub.
   Expect under the design as written: 2 decisions; hop 2 `Destination = http://127.0.0.1:11434`, `DestinationClass = Host`,
   `Access.Allowed = true`; the stub saw `/api/chat` with the body. If the owner picks "never follow into Host" (or P2 for
   factory clients too), expect instead: the 307 returned, 1 decision, stub saw 1 request.
4. **Mutation.** Whichever rule the owner picks, the opposite behaviour is the mutation; the twin's "stub saw N requests"
   assertion goes red.

### 17. Bedrock: the AWS SDK's own `HttpClient` follows redirects and the guard never sees any hop

1. **Today.** `AwsBedrockChatClientFactory.Create` builds `new AmazonBedrockRuntimeClient(...)` and `AsIChatClient`
   (`AwsBedrockChatClientFactory.cs:37-42`); "The SDK owns the HTTP" (`docs/EgressInventory.md:68`). The MEAI record is the
   reconstructed `https://bedrock-runtime.<region>.amazonaws.com` (`MeaiEgressDestination.cs:97-106`; Known limit
   `docs/EgressInventory.md:17`). No `EgressGuardHandler` is in that pipeline, so neither hop 1 nor any redirect is seen by
   the HTTP route; `AllowAutoRedirect` on `ClientConfig` defaults to following **[unverified]**.
2. **Design.** "Bedrock, if AWSSDK.Core 4.0.100.4 exposes the setting [needs a test]" (`:512`; Appendix B `:1146`).
   `Directory.Packages.props:127` pins AWSSDK.Core 4.0.100.4, `:128-129` BedrockRuntime 4.0.101.4 and the MEAI adapter
   4.0.101.8.
3. **Twin.** If the setting exists: build the runtime with `new AmazonBedrockRuntimeConfig { AllowAutoRedirect = false,
   ServiceURL = loopbackA, HttpClientFactory = <AWS factory over a stub> }` **[needs a test]**; A answers 307 → B; expect B
   sees 0 requests and the SDK surfaces an error. Better, and it closes the Known limit at `docs/EgressInventory.md:17` as
   well: supply an AWS `HttpClientFactory` whose client is `EgressHttp.CreateClient(inner, EgressFamilies.ModelMeai,
   "EG-MDL-02", guard)`, so every SDK send (and, under P2, no cross-host hop) is recorded at the host the SDK dials. The twin
   then expects an `EG-MDL-02` HTTP-route decision naming loopback A beside the MEAI-route record.
4. **Mutation.** Drop the `AllowAutoRedirect = false` (or the factory): B sees `/b` → red. If the setting does not exist,
   record it as a Known limit with the version number, and the verifier checks that sentence.

### 18. A host-supplied Ollama inner client follows redirects below the MEAI record

1. **Today.** `AddAshlarMeaiPipeline(..., ollamaInnerFactory)` lets a host replace the default inner client
   (`MeaiPipelineServiceCollectionExtensions.cs:81`, `:100-101`, `:107`). The MEAI route records only
   `ChatClientMetadata.ProviderUri` (`MeaiEgressDestination.cs:84-90`), so an inner client over a redirect-following
   `HttpClient` re-sends the conversation wherever `Location` points, and nothing records it. The default
   `OllamaHttpChatClient` already has `AllowAutoRedirect = false` (`OllamaHttpChatClient.cs:163-176`; CHANGELOG `:201-204`;
   `SPEC-007:92`).
2. **Design.** R-a lists `OllamaHttpChatClient` (`:510-511`) as if still to do; it is 4.1's. Nothing names the inner-factory
   seam.
3. **Twin.** Pin the existing default (`EgressGuardChatClientTwinTests.TheDefaultOllamaClient_DoesNotFollowARedirect`,
   `:715-725`, with `RedirectingOllama` `:860-957`) and add the limit's statement to `docs/EgressInventory.md`'s EG-MDL-01 row
   (`:67`): a host inner client's own redirects are not seen. No new behaviour is testable without a host client.
4. **Mutation.** None; records only. The verifier checks the 4.3 PR does **not** re-claim the Ollama flip as new.

### 19. `ProviderFactory`'s Ollama and OpenAI-compatible clients: P2 turns a redirecting proxy into "unavailable", which can escalate

1. **Today.** `EgressHttp.CreateClient(ModelLegacy, "EG-MDL-07")` (`ProviderFactory.cs:814`) feeds `OllamaProvider`, which
   checks `IsSuccessStatusCode` (`OllamaProvider.cs:95-97`, `:256-257`) and catches `HttpRequestException` (`:129`, `:284`);
   the static EG-MDL-03 client (`:61`) serves the OpenAI-compatible providers. A redirecting proxy in front of Ollama works
   today because the runtime follows.
2. **Design.** R-a + P2 (`:505-509`, `:534-536`): a cross-host 3xx comes back; a same-host 3xx is followed by Ashlar's
   follower. The CHANGELOG line (`:511`, `:563-565`) must say the legacy path changes too. DESIGN row 24 (`:380`):
   `IsProviderAvailable("ollama")` runs the EG-MDL-07 health check and catches everything; `AdaptiveProviderFactory` then
   escalates on Full — so on Full in report mode a redirecting local proxy now causes **more** egress (to `openai`/`azure`),
   not less. 4.10's AG no-escalate (`:599-603`) does not help Full.
3. **Twin.** `ProviderFactory` with `OLLAMA_BASE_URL` at loopback A that 307s cross-host (another loopback hostname) for
   `api/tags`: expect one EG-MDL-07 decision naming A, no request at B, `IsProviderAvailable("ollama") == false`. Same-host
   relative redirect (`/api/tags` → `/v1/api/tags`): expect followed, still 1 decision, available. The CHANGELOG sentence is
   a record check.
4. **Mutation.** Follow cross-host (P1 for `EgressHttp`): the first row goes red (B saw the request).

### 20. The netstandard2.0 hop as the redirect handler: `Options`, the type name the 4.2 twin pins, and what the Known limit becomes

1. **Today.** The hop is `SynchronousSendRefusedOnNetstandard20Asset` (`#if NETSTANDARD2_0`, `:1`, `:32`), overriding only
   `SendAsync` via an owned invoker (`:46`, `:70-71`); its name is the explanation the caller sees (`:18`). The 4.2 twin pins
   that name (`EgressHttpNetstandard20TwinTests.cs:48`, `HopTypeName`), the chain shape `Guard > Hop > HttpClientHandler`
   (`:132-147`), and walks it by that name through `Inner` (`:533-538`). TSV row 43 pins `http.new 1` in that file.
   `HttpRequestMessage.Options` does not exist on netstandard2.0; only `Properties` does **[unverified]**.
2. **Design.** "The hop is the `EgressRedirectHandler` itself (gap 2)" (`:482`); "on netstandard2.0 it is the gap 1 hop"
   (`:532`); the stash lives in `request.Options` (`:520`). Carry-in: the walker "must step through it (or fold it into
   `EgressRedirectHandler` on ns2.0)"; the 4.1 Known limit "closes or narrows" (`handoff.md:108`).
3. **Twin.** Keep the ALC harness. Expect: a synchronous `Send` is still refused with `NotSupportedException` **naming the
   merged type** before anything is sent, and `SendAsync` still records exactly 1 decision (the 4.2 twins `:92-112`); the
   stash on ns2.0 uses `Properties` and the rewrite twin (item 4) passes on the ns2.0 build through the harness; the shape
   twin (`:132-147`) and `Next()` (`:533-538`) are updated to the new name. **The Known limit does not close:** external
   walkers (Grpc.Net.Client on a ns2.0 consumer) still stop at a non-`DelegatingHandler` hop; `docs/EgressInventory.md:23`
   is reworded ("Ashlar's own walker steps through; external code still stops"), not deleted.
4. **Mutation.** Fold the follower in but let the merged hop override a `Send`-shaped member: impossible on ns2.0 (no such
   member), so instead mutate the harness-visible fact: make the hop a `DelegatingHandler` on ns2.0 → the sync-`Send`
   refusal twin goes red (the inherited `DelegatingHandler.Send` forwards unevaluated on .NET 5–7; on the net8.0 test host
   the runtime still has `Send`, so the ALC twin observes it).

### 21. The gRPC `Wrap` chain: the inserted redirect handler flips two transport tests and changes what Grpc.Net.Client walks

1. **Today.** `BuildHandler()` is `EgressHttp.Wrap(ConfigureHandler(new HttpClientHandler()), Grpc, "EG-XPT-03")`
   (`DefaultGrpcChannelFactory.cs:76`), with the comment that Grpc.Net.Client finds the handler type through
   `DelegatingHandler.InnerHandler` (`:73-74`). `DefaultGrpcChannelFactoryGapCoverageTests` casts
   `(HttpClientHandler)guarded.InnerHandler!` (`src/Ashlar.Tests.Transport/DefaultGrpcChannelFactoryGapCoverageTests.cs:116`,
   `:150`); `EgressHttpHandlerTwinTests.The_plain_client_is_the_guard_over_an_HttpClientHandler_and_every_call_shape_builds`
   pins `InnerHandler.Should().BeOfType<HttpClientHandler>()` and `BeOfType<StubHandler>()` (`:368-389`).
2. **Design.** R-b places `EgressRedirectHandler` directly above the primary for `Wrap` too (`:505-508`, `:517`); gRPC is
   under P2 (`:535`).
3. **Twin.** Rewrite the two transport casts and the handler twin to walk one more `DelegatingHandler`; add a fact that the
   chain is `EgressGuardHandler > EgressRedirectHandler > HttpClientHandler` and that `AllowAutoRedirect` is `false` on the
   tail. Pin that `GrpcChannel.ForAddress` still accepts the chain (it walks `DelegatingHandler`s **[unverified]**) and that
   `EgressRawClientTwinTests.Grpc_channel_handler_records_EG_XPT_03` (`:166`) still records exactly 1 decision per call
   (HTTP/2 never redirects, so the follower is inert; `AllowInsecure` sets the HTTP/2 unencrypted switch, `:60-63`).
4. **Mutation.** Skip the redirect handler for `Wrap` (only `CreateClient` gets it): the chain fact goes red; and a 307
   through a `Wrap`-built HTTP/1.1 client is then followed by the primary if the flip is also skipped — pair it with a
   `Wrap` differential twin.

### 22. Hop 2's decision: same guard instance, same family and site, fault counting, and `Refused` under the 4.6 opt-in

1. **Today.** The guard handler resolves the host's `IEgressGuard` once per factory build (`EgressServiceCollectionExtensions.cs:97-104`),
   swallows and counts a throwing guard (`EgressGuardHandler.cs:82-87`), and never acts on `Refused`.
2. **Design.** The redirect handler "decides with the same guard, family and site" (`:521`); faults as §2.4; 4.3's
   done-when includes "an enforcement twin: hop 2 refused, the server never sees `/b`" (`:559`, `:853`) while "no route
   acts on the mode until 4.7" (`EgressGuard.cs:29-30`).
3. **Twin.** (a) A custom `IEgressGuard` that records its calls: factory client, stub primary, 307 → remote: expect the
   custom guard called twice with the same `Family`/`Site` and the two URIs (hop 2 is **not** decided by `ProcessDefault`).
   (b) A guard that throws on the second call: expect the send still goes ahead in report mode and `GuardFaults` rises by
   exactly 1 (the counter is `EgressGuardHandler.GuardFaults`, `:50`; the redirect handler must count into the same
   counter or its own, and the PR body says which). (c) **Enforce, 4.3-compatible:** `new EgressGuard("full", "enforce")`
   as the factory's `IEgressGuard`; stub primary; loopback-shaped hop 1 → `307 Location: http://remote.example/b`. Expect
   hop 1 `Mode = enforce`, `ModeBasis = override`, `Access.Allowed = true`, `Refused = false`; hop 2 `Destination =
   http://remote.example`, `Access.Reason = SystemHighData`, `Refused = true`; and, since no exception type exists yet, the
   follower **does not follow a refused hop and returns the 3xx to the caller** (nothing sent, the stub saw 1 request).
   That is fail closed without a new exception type; 4.7 upgrades it to a faulted task. (d) The rewrite path under enforce:
   the redirect handler cannot "return a 3xx", so in 4.3 it sends and records `Refused = true`; the PR body states this
   and 4.7's twin list names it.
4. **Mutation.** (a) Decide hop 2 with `EgressGuard.ProcessDefault`: the custom-guard call count goes red (1, not 2).
   (c) Follow the refused hop: "the stub saw 1 request" goes red (2).

### 23. Redirect loops and the follower's limit

1. **Today.** `MaxAutomaticRedirections` defaults to 50; on exceeding it the runtime returns the last 3xx **[needs a test]**.
2. **Design.** "Follow only when the primary originally followed, with its own limit" (`:524`).
3. **Twin.** Kestrel A `/a` → `/b` → `/a` …; with the primary's `MaxAutomaticRedirections = 3`: expect the follower stops
   after 3 hops and returns the last 3xx; the server saw exactly 4 requests; decisions: 1 (same authority throughout). Then
   A ↔ B (two authorities): decisions = hops + 1 (each authority change is evaluated), bounded by the limit. This also
   bounds the decision-log cost a malicious peer can cause per send.
4. **Mutation.** Ignore the primary's limit (use 50 when the primary says 3): "4 requests" goes red.

### 24. `userinfo` and fragment in `Location`

1. **Today.** The record strips userinfo (`EgressDestinations.cs:196`); `HttpClient` does not send URI userinfo as
   credentials.
2. **Design.** "keep the fragment" (`:526`); records never carry userinfo (`EgressDecision.cs:8-9`).
3. **Twin.** `Location: http://user:pw@127.0.0.1:Q/b#frag` through a factory client: expect hop 2 `Destination =
   http://127.0.0.1:Q` (no userinfo), the request B received has no `Authorization`, and `request.RequestUri.Fragment` is
   `#frag` after the follow. Also `Location: /b` with the original `#orig` fragment: the fragment is carried over
   (runtime parity **[needs a test]**).
4. **Mutation.** Record `uri.ToString()` for hop 2: the userinfo assertion goes red.

### 25. One site, two policies: `IdeEndpoints.cs:193`

1. **Today.** `services.GetService<IHttpClientFactory>()?.CreateClient() ?? EgressHttp.CreateClient(Http, "EG-MDL-14")`:
   the factory's unnamed client (`Site = factory:`, P1) or the `EgressHttp` fallback (`Site = EG-MDL-14`, P2), for the same
   GET of `<ollamaBase>/api/tags` (`:188-193`).
2. **Design.** D33 splits by construction, not by site (`:533-537`). The TSV row 7 and `docs/EgressInventory.md`'s EG-MDL-14
   row describe one site.
3. **Twin.** Not needed; a record check: the EG-MDL-14 row and the Known limits say the IDE tags call follows cross-host
   redirects through the factory and not through the fallback. Or make the fallback the only path (a one-line change under
   `application/`, `[coordinated-integration]`).
4. **Mutation.** None.

### 26. A relative `RequestUri` after a rewrite must not crash the compare

1. **Today.** A relative `RequestUri` reaching the guard makes `Classify` throw and the record carry `Fault`
   (`EgressDestinations.cs:192-193`; `EgressGuard.cs:130-138`); the primary then throws its own `InvalidOperationException`.
2. **Design.** Silent on relative URIs in the follower.
3. **Twin.** Rewriter sets `request.RequestUri = new Uri("/b", UriKind.Relative)` below the guard: expect the redirect
   handler's compare to treat it as a mismatch and evaluate (a `Fault` record, `Access.Reason = NoDecision`,
   `Refused = true` under enforce), never an unhandled exception from the compare itself (`Uri.Authority` throws on a
   relative URI).
4. **Mutation.** Compare with `request.RequestUri.Authority` unguarded: the twin's "exactly one Fault record" goes red
   with an `InvalidOperationException` from the handler.

---

## 2. Design claims marked [needs a test] for this PR (and the ones the design should have marked)

Marked in the design:
- The `AllowAutoRedirect` setter throws on a started handler (`:508-509`; Appendix B `:1144`). Item 8.
- AWSSDK.Core 4.0.100.4 exposes `AllowAutoRedirect` (`:512`; `:1146`). Item 17.
- The runtime's redirect rules, which the differential twin settles (`:524-531`; `:1144`): status set, relative resolution,
  fragment, HTTPS→HTTP, method rewrite, `Authorization` cleared, limit behaviour. Items 11, 12, 13, 23, 24.
- Factory filter ordering (`:1144`): item 10.
- `HttpClient` passes a handler's exception through unwrapped (`:330`; `:1136`): 4.7, not 4.3, but item 22(c)'s
  "return the 3xx" avoids depending on it.

Not marked in the design but only a run settles them:
- `IHttpClientBuilder.ConfigurePrimaryHttpMessageHandler(Action<HttpMessageHandler, IServiceProvider>)` exists in
  Microsoft.Extensions.Http 10.0.12 (`Directory.Packages.props:19`) **[unverified]**. Item 14.
- Whether `ConfigureHttpClientDefaults` registers the logging filter before Ashlar's (`AddHttpClient(services)` inside it)
  **[unverified]**, and that an earlier-registered filter's post-`next` runs later **[unverified]**. Item 10.
- `SocketsHttpHandler` sets `response.RequestMessage` to the (mutated) request after an internal redirect; a stub that
  returns `new HttpResponseMessage()` leaves it `null` **[needs a test]**. Items 6, 7.
- `HttpClientHandler.AllowAutoRedirect` can be set on a subclass whose `SendAsync` override never started the underlying
  handler (the design's `RedirectingStub` shape) **[needs a test]**. Items 2, 9.
- Grpc.Net.Client 2.76.0 (`Directory.Packages.props:213`) walks `DelegatingHandler.InnerHandler` through the new redirect
  handler to find `HttpClientHandler` **[unverified]**. Item 21.
- The MCP (`HttpClientTransport`, `McpClientConnectionManager.cs:328-333`) and A2A SDK clients surface a returned 3xx as
  an error and do not follow it themselves **[unverified]**. Item 2's consumers.
- `HttpRequestMessage.Options` is .NET 5+ only; the netstandard2.0 asset must stash in `Properties` **[unverified]**. Item 20.

---

## 3. Where the design is wrong or impossible against master

1. **The "enforcement twin: hop 2 refused, the server never sees `/b`" cannot throw in 4.3.** No route acts on `Refused`
   until 4.7 (`EgressGuard.cs:29-32`; `EgressDecision.cs:144-148`; `EgressEnforcement.cs:18-21`), `EgressRefusedException`
   does not exist (`PublicAPI.Unshipped.txt` has no such type), and the route table puts the faulted-task behaviour in 4.7
   (DESIGN `:330`, `:857`). The 4.3 row still lists the enforcement twin (`:853`; `handoff.md:100`). What 4.3 **can** do fail
   closed with no new type: the follower does not follow a hop whose decision is `Refused` and returns the 3xx (item 22c).
   The rewrite path has no such escape; in 4.3 it sends and records `Refused = true`, and the PR body must say so.

2. **The stub twin "today 1 decision, after 2" (`:555`) contradicts D33 P2 (`:534-536`) for an `EgressHttp` client with a
   cross-host 307.** After the PR such a client returns the 307 with 1 decision; 2 decisions arise only through a factory
   client (P1) or a same-authority redirect that the follower follows (and that one is still 1 decision, since the
   authority did not change). The verifier should expect the three-way split in item 2 and reject a PR body that claims
   "after 2" for an `EgressHttp` client.

3. **"It checks that the guard handler is still `AdditionalHandlers[0]`" (`:542`) is the wrong check.** The master remarks
   already state that the factory's logging-scope handler legitimately sits outside the guard
   (`EgressServiceCollectionExtensions.cs:21-22`), and a host may `Insert(0, …)` its own outer handler. The check must be
   presence (`Any(h => h is EgressGuardHandler)`), and the twin in item 10 settles the ordering **[unverified]**.

4. **The ns2.0 hop already exists under another name and with no record.** DESIGN `:482` ("the hop is the
   `EgressRedirectHandler` itself") and `:484-485` ("the hop publishes a `NoDecision` record") describe gap 1 before 4.2
   merged. On master the hop is `SynchronousSendRefusedOnNetstandard20Asset` (`:32`), and the owner's 2026-10-06 amendment
   says **no record** (`SPEC-007:425`; `docs/EgressInventory.md:14`; `handoff.md:64`). 4.3 must not reintroduce a record from
   the hop, and if it folds the follower into the hop it changes a type name pinned by `EgressHttpNetstandard20TwinTests.cs:48`
   and `:536`, by TSV row 43, by `docs/EgressInventory.md:14` and `:23`, and by the `EgressGuardHandler` remarks (`:17`) and
   `EgressHttp` remarks (`:11-22`). Those are red-to-green flips and record edits to name in the PR body, not new evidence.

5. **The 4.1 Known limit "a chain walker stops at the hop" narrows; it does not close.** The hop stays a non-`DelegatingHandler`
   on ns2.0 (there is nothing else it can be and still refuse `Send`, `SynchronousSendRefusedOnNetstandard20Asset.cs:11-18`), so
   external code that walks only `InnerHandler` still stops there (`docs/EgressInventory.md:23`). Ashlar's own walker steps
   through `Inner` (`:64`). Reword the limit; do not remove it.

6. **`OllamaHttpChatClient` is listed under R-a as new work (`:510-511`).** 4.1 shipped it (`OllamaHttpChatClient.cs:163-176`;
   CHANGELOG `:201-204`; `SPEC-007:92`; `EgressGuardChatClientTwinTests.cs:715`). 4.3's CHANGELOG entry must not re-claim it.
   What remains open on the MEAI route is the host inner-client seam (item 18), which the design does not name.

7. **R-c needs `InternalsVisibleTo Ashlar.Infrastructure` (`:550-551`, D9 `:807`), which master does not grant.**
   `Ashlar.Abstractions.csproj:32-38` lists AI.Pipeline, Hosting, Mcp.Client, Mcp.Server, Orchestration, A2A and A2A.Server.
   `EgressGuardHandler` is `internal sealed` (`EgressGuardHandler.cs:24`), so the filter cannot recognise it by type without
   the grant (or a public marker). The 4.6 convention fact `Only_AddAshlar_and_the_reset_seam_reach_the_process_egress_state`
   pins files naming the seam members, not the IVT list, so the grant itself needs no new row; but
   `ProcessGlobalEnvironmentConventionTests` and the fifth fact should be re-run after Infrastructure gains internals.

8. **The "never follow" for SNS depends on *when* R-c reads the primary.** The client's `ConfigurePrimaryHttpMessageHandler((h, _) => h.AllowAutoRedirect = false)`
   runs inside `next(builder)`; R-c's "remember the original values" (`:509`) must read the primary in the post-`next` step
   (where F2.4 says it sees the final primary, `:540`) and treat `false` as "never follow". A pre-`next` read sees the default
   `true` and the follower undoes the SNS setting (item 14, mutation c). Also, the design's "configure-existing overload"
   silently fails to flip a test's stub primary that a **later** replace-overload installs
   (`EgressFleetHostTwinTests.cs:48`); that stub does not follow, so no hole, but the twin must not read the stub's absence of
   the property as a flip.

9. **D33 P1 follows a remote first hop into the host boundary (item 16).** The label model allows Host at every label
   (`ReferenceMonitor.cs:53-54`), so a redirect from an untrusted peer to `http://127.0.0.1:…` is followed with the body and
   recorded as allowed. The repository already treats that as an attack for mesh pull (`MeshAutoPullService.cs:48-50`). The
   design has no rule and no Known-limit line; this needs the owner (below).

10. **The design's "seven mutations" (`:562`) are not enumerated.** The verifier should expect at least: the primary not
    flipped (item 1), compare by authority not destination (3), rewrite not re-evaluated (4), no-stash treated as evaluated
    (5), P2 → P1 for `EgressHttp` (2), follower ignoring the original value (14), guard not re-inserted after `Clear()` (10),
    post-send check removed (6), scheme check dropped (12), exact type match (9), setter exception swallowed (8), refused hop
    followed under the opt-in (22c). Each with a `scripts/mutation-check.sh` summary line quoted verbatim, and each
    naming the red test.

11. **Records the PR must move, which the design's table (`:876`) understates.** `ci/egress-inventory.tsv`: row 42 (`EgressHttp.cs
    http.new 3`) changes if `:57` becomes an object initializer only if the count changes (it does not: the scanner matches
    `new HttpClientHandler {` too, `Scanner.cs:578`); a new `EgressRedirectHandler.cs` adds a row only if it constructs an
    `HttpClient`, handler or `HttpMessageInvoker` (`:576-580`), `Exempt:GuardImpl` (TSV `:5`); the floors in
    `EgressGuardConventionTests.cs:204` ("2,135 files, 150 occurrences") and cert-gate row 64's floor sentence move by +1 file
    (and +1 occurrence if the follower owns an invoker). Row 64's text "`EgressHttp.Wrap` records exactly one decision per
    send" and the "exactly once" remarks (`EgressGuardHandler.cs:8-9`; `EgressHttp.cs:7-8`) become "once per authority the
    send reaches". `docs/EgressInventory.md:15` (the redirect gap) is rewritten, `:23` reworded, the EG-MDL-01 (`:67`),
    EG-MDL-02 (`:68`) and EG-MDL-14 rows gain the limits in items 18, 17 and 25; TSV rows 9 and 34 (the SNS registrations)
    cite new lines; `DefaultGrpcChannelFactory.cs:73-74`'s comment and TSV row 91 cite the new chain. SPEC-007's 4.4 line
    still lacks its merge SHA `de41a8ac` (`handoff.md:149`): phase C's first PR adds it.

---

## 4. Red-to-green and green-to-red flips the verifier should expect by name

- `EgressHttpHandlerTwinTests.The_plain_client_is_the_guard_over_an_HttpClientHandler_and_every_call_shape_builds` (`:368-389`):
  `InnerHandler` is no longer the primary.
- `DefaultGrpcChannelFactoryGapCoverageTests` (`src/Ashlar.Tests.Transport/…:116`, `:150`): the `(HttpClientHandler)` casts.
- `EgressHttpNetstandard20TwinTests.Every_client_and_Wrap_handler_puts_the_hop_between_the_guard_and_the_inner_handler`
  (`:132-147`) and its `Next()` walker (`:533-538`) if the hop's type changes.
- `EgressGuardConventionTests` F1/F2/F6 on the TSV counts and floors (item 3.11).
- `EgressKernelFactoryTwinTests.EveryKernelMemberThatInstallsTheGuard_LeavesOneHandler` (`:75`): if the filter adds a second
  Ashlar handler (the redirect handler) to `AdditionalHandlers` rather than wrapping the primary, "one handler" is false;
  the design wants the follower around the primary (`:517`), so this test should stay green, and that is itself evidence.
- Nothing in `EgressGuardChatClientTwinTests` should move (the MEAI route is untouched by 4.3).

---

## 5. Owner questions this lane will hit

1. **Follow into the host boundary from a non-Host first hop (item 16, §3.9).** Keep P1 as designed (recorded as a Known
   limit: a remote peer can bounce a factory client's body to loopback or link-local, allowed by the label model), add
   "never follow a redirect whose destination is Host when the first hop was not" to the follower, or make factory clients
   P2 as well (return every cross-host 3xx; hosts that need following do it outside the guard, which is what
   `docs/EgressInventory.md:15` already recommends).
2. **What 4.3 does with a `Refused` hop under the 4.6 opt-in before 4.7 exists (item 22c, §3.1):** return the 3xx
   unfollowed (recommended, fail closed, no new type), or send and only record until 4.7.
3. **Bedrock (item 17):** set `AllowAutoRedirect = false` on the SDK config only, or also route the SDK's HTTP through
   `EgressHttp` via AWS's `HttpClientFactory` seam, which would retire the "Bedrock's host is reconstructed" Known limit
   (`docs/EgressInventory.md:17`) in 4.3 rather than later.
4. **Credential headers on P1 hops (item 13):** runtime parity clears `Authorization` only, so `X-Api-Key`-style headers
   (`Ashlar.Client/ServiceCollectionExtensions.cs:26`) follow a cross-host hop the guard allows. Accept as parity, or clear
   every header the client set as a default on an authority change.
