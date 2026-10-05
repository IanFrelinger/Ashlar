# PR 4 wave A: lane results and check findings

The phase B thread must fix these (or answer each with a reason) before opening each PR.

The full lane reports (evidence and mutation lines) are in `waveA-lanes.json` beside this file.

## 4.1: branch `claude/spec-007-pr4-4.1-records` @ `1e3e645d2`

### Check verdict

PR 4.1 implements the design's §4 row as specified; I found no blocking defect. In order:

- **Classifier:** `file:` names (any case) are recorded as written and never read as a URL, and a `file` URI is never Host.
- **Mesh serve:** EG-MESH-03 records `mesh-peer:<ip>` and `mesh-peer:unknown`.
- **MEAI destination:** checks run in the design's order: ProviderUri, then a LlamaSharp type check (`local:onnx` only), then Bedrock's region rebuild, then fail-closed `meai:<key>`. Every step is wrapped so nothing throws.
- **Ollama cloud rule:** applied in `EgressGuardChatClient` (the call's ModelId, else the inner client's default model) and in `OllamaProvider`.
- **Redirects:** `OllamaHttpChatClient` now turns `AllowAutoRedirect` off, and the CHANGELOG records it as a behaviour change.

**Nothing refuses.** All new code paths catch their exceptions or call the never-throwing `EgressGuard.Evaluate`. The only behaviour change is the designed redirect one.

**Production records change only as the design says.** The defaults record what they did before:
- the default Ollama client's ProviderUri is its BaseAddress, which comes from the same resolver as before;
- the default LlamaSharp client still records nothing;
- the AWS Bedrock MEAI assembly appears not to set ProviderUri, so Bedrock keeps its region rebuild.

**Red at base is real.** At `1f92e73`: Tests.Infrastructure 28/241 failed and Tests.CLI 8/16 failed. The failure messages show the intended reasons: Host class, `file://127.0.0.1`, no decision, the followed 307, a localhost record. The CLI flips are named. The rewritten `mesh-peer:` classifier rows are correctly not claimed as red-to-green.

**Mutations.** All nine were killed at `4e170e2`, which differs from head `1e3e645` by one documentation line only (I checked the diff). The four the design asks for (M1 to M4) are among them.

**Records match the code.** I checked:
- the TSV sums: 85 rows, 149 occurrences, 48 guarded, and the reason buckets 33/34/10/4/4;
- every cited line number in the files 4.1 edited;
- row 64;
- the SPEC-007 status line;
- the Known-limits removals and the new loopback-relay limit;
- no PublicAPI change.

In a temp clone, the knowledge graph, doc-anchor and compat-policy checks pass. No remote destination is still recorded as Host on these routes. What remains is the documented loopback-relay limit (tunnels, gateways, aliases), which is design scope.

**Before merge:**
- One medium gap: the `OllamaProvider` check on the resolved model name (`|| IsOllamaCloudModel(validationResult.Value.Name)`) has no test and no mutation, and it is the only check that catches a short name like `gpt-oss` resolving to `gpt-oss:120b-cloud`.
- Low-severity drift worth fixing:
  - `EgressDecision`'s public doc still says a record never holds a path;
  - the floor comment in `EgressGuardConventionTests` still says 148 / http.new 18;
  - Known limits omit EG-MDL-10 and that the rule matches by name only (aliases);
  - the PR 3b CHANGELOG entry still says the destination is "fixed";
  - the Bedrock step-1 branch has no twin;
  - the explicit sites are now 17, which matters for 4.9;
  - the redirect twin can race for its port.

**After merge:** the SPEC-007 "PR 4.1 (this PR)" line will need the PR number and merge SHA.

Evidence I read is in `/tmp/claude-0/-home-user-Ashlar/119c0f0e-4179-5553-a59e-a4fbe7cfca88/scratchpad/pr4/logs-4.1/` and `/tmp/claude-0/-home-user-Ashlar/119c0f0e-4179-5553-a59e-a4fbe7cfca88/scratchpad/pr4/mut-4.1/`. I checked out a temp clone at `/tmp/claude-0/-home-user-Ashlar/119c0f0e-4179-5553-a59e-a4fbe7cfca88/scratchpad/review-4.1-clone`.

### Check findings

1. **[medium]** `src/Ashlar.Infrastructure/Execution/Ollama/OllamaProvider.cs:241 (twin: src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressRawClientTwinTests.cs OllamaProvider_records_a_cloud_model_as_an_external_model_at_ollama_com; mutation m8)`
   - **Problem:** Nothing tests or mutation-checks the resolved-name half of the OllamaProvider cloud rule, `|| IsOllamaCloudModel(validationResult.Value.Name)`. The twin always gives `ExecuteChatAsync` the same string the stub's /api/tags lists, so the requested name and the resolved name never differ. m8 replaced the whole condition with `IsOllamaCloudModel(null)`, so it never tested the two operands on their own. In the real case they do differ: a config asks for `gpt-oss`, `TryResolveManifestModel` resolves it by single-family prefix to `gpt-oss:120b-cloud`, and `BuildChatPayload` sends that resolved name. Only the second operand records ollama.com there. If someone deleted it, every test would stay green while a cloud chat is again recorded only as the loopback daemon (Host). The lane's claim that every change has a test and a mutation seen red is therefore false for this branch.
   - **Fix:** Add a twin row where the stub lists only `gpt-oss:120b-cloud` and the provider is asked for `gpt-oss`. Assert one EG-MDL-07 decision at https://ollama.com, and that the chat body carries the resolved name. Then run a mutation that drops only the second operand (`|| IsOllamaCloudModel(validationResult.Value.Name)` → empty) and record it red.
2. **[low]** `src/Ashlar.Abstractions/Security/Egress/EgressDecision.cs:8-9 and :67-69`
   - **Problem:** The public docs on `EgressDecision` and `EgressDecision.Destination` still say a record never contains a URL's userinfo, path, query or fragment, and that `Destination` is `scheme://host[:port]` or a name. Since 4.1 a `file:` name is recorded as written even when it is URL-shaped, for example `file://127.0.0.1/E$/out.nxpkg`. The twins assert exactly that. The lane updated the `EgressRequest` remarks and row 64 with this exception but not `EgressDecision`, so the redaction contract on the public record type is now false for `file:` names.
   - **Fix:** Add the same exception to both `EgressDecision` doc comments: a name starting with `file:` (any case) is a path, recorded as written and bounded, and may hold a path. This is XML docs only, so there is no PublicAPI change.
3. **[low]** `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressGuardConventionTests.cs:191-199`
   - **Problem:** The comment on the non-vacuity floors still says '148 occurrences (http.new 18, ...)', and it says 'Re-measure and restate when a PR moves them.' 4.1 moves them: the TSV now sums to 149 occurrences and http.new to 19, because of the new `HttpClientHandler` in `OllamaHttpChatClient`. Row 64 and the TSV were updated; this comment was not.
   - **Fix:** Restate it: PR 4.1 adds one http.new (the redirect-off HttpClientHandler in OllamaHttpChatClient.cs:170), so 149 occurrences and http.new 19.
4. **[low]** `docs/EgressInventory.md:16 (Known limits, loopback-relay bullet)`
   - **Problem:** The bullet names EG-MDL-11 as the only Ollama route that records the daemon whatever the model. EG-MDL-10 (`OllamaModelServingBackend`: `/api/chat` in `RunInferenceAsync`, and `/api/generate` load and unload with the model id) does the same and gets no cloud rule. The bullet also does not say that the rule goes by name only. A cloud model copied to a local name (`ollama cp gpt-oss:120b-cloud mymodel`) is still recorded at the daemon as Host. On the MEAI route, a model set by an options-rewriting layer inside a host's inner factory (for example `ConfigureOptions`) is invisible to the outermost guard, which reads only `ChatOptions.ModelId` and the inner `DefaultModelId`.
   - **Fix:** Name EG-MDL-10 next to EG-MDL-11. Add one sentence that the rule matches the model id as written: an alias or copy of a cloud model, or a model set inside a host's inner pipeline, is recorded at the daemon.
5. **[low]** `src/Ashlar.AI.Pipeline/Governance/MeaiEgressDestination.cs:88; docs/EgressInventory.md:12`
   - **Problem:** Two loose ends on Bedrock. (1) The step-1 branch that keeps site EG-MDL-02 for a `cloud:bedrock:*` inner client that reports a ProviderUri has no twin. Every Bedrock test uses FakeChatClient with no URI and reaches step 3, so changing that branch to `NameOf(targetKey)` would survive. (2) The new Known-limits hedge says 'whether the AWS MEAI client reports one is not verified here'. A strings scan of AWSSDK.Extensions.Bedrock.MEAI 4.0.101.8 (lib/net8.0) found no standalone `Uri` or `ServiceURL` entry, which suggests it never sets ProviderUri. If so, the default Bedrock record is unchanged, but the doc leaves that open.
   - **Fix:** Add one twin: a `cloud:bedrock:fast` key over an inner client that reports `https://bedrock.example`, asserting site EG-MDL-02 and that URI. Either confirm the AWS client sets no ProviderUri (by decompiling, or with a runtime twin over `AwsBedrockChatClientFactory.Create`) and state it, or leave the hedge.
6. **[low]** `CHANGELOG.md:196 (the PR 3b Unreleased entry)`
   - **Problem:** The PR 3b bullet still says `EgressGuardChatClient` records 'with a destination fixed when the keyed client is built'. Since 4.1 the destination is chosen per call when the model is an Ollama cloud model (from `ChatOptions.ModelId` or the inner client's default). The lane edited this same bullet for local:onnx but left the 'fixed' wording.
   - **Fix:** Change it to 'with a destination fixed when the keyed client is built (since PR 4.1, an Ollama `-cloud`/`:cloud` model is recorded at https://ollama.com per call)', or drop 'fixed'.
7. **[low]** `src/Ashlar.Infrastructure/Execution/Ollama/OllamaProvider.cs:244; the design's 4.9 row (DESIGN-4-final.md §4)`
   - **Problem:** The new explicit `EgressGuard.ProcessDefault.Evaluate(... "EG-MDL-07" ...)` makes 17 explicit `_ = ...Evaluate(new EgressRequest(` sites in production (grep count 17). The design's 4.9 row still plans for 'the 16 sites'. The cloud chat's real protection under enforcement now depends on 4.9 adding `ThrowIfRefused` here, because the handler's own decision is the loopback daemon and Host allows it. The lane disclosed this, but nothing in the repo records it.
   - **Fix:** Carry it into the 4.9 plan and handoff: 17 sites, including EG-MDL-07's cloud decision, which must refuse before `SendAsync` and not only record. Optionally add one clause to the EG-MDL-07 Route cell saying the handler's decision stays Host for a loopback daemon.
8. **[low]** `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressGuardChatClientTwinTests.cs:819-825 (RedirectingOllama.Start)`
   - **Problem:** The redirect twin finds a free port by starting a `TcpListener` on port 0, stopping it, and only then starting an `HttpListener` on that port. Another test that runs in parallel (other collections are not serialized against `EnvironmentVariables` for sockets) can take the port in between, so this cert-gate test can fail now and then for no real reason.
   - **Fix:** Retry `Start` a few times on `HttpListenerException`, or host the stub on Kestrel or `HttpListener` with a port it binds itself and then reads back.

### Lane deviations from the design

- Base: the clone was branched from GitHub master 79e988c (#713), not the host clone's origin/master. /home/user/Ashlar's local master and remotes/origin/master were stale (4480190 / 9abb491), so I fetched refs/heads/master from github.com into the clone. 79e988c is later than the required 9abb491.
- MEAI Ollama cloud rule: the design names options.ModelId. EgressGuardChatClient also uses the inner client's ChatClientMetadata.DefaultModelId when the call's ModelId is blank, because that default is the model OllamaHttpChatClient actually sends. The default is read once at construction and the read never throws. This is fail-closed and was mutation-checked (m7).
- OllamaProvider cloud rule: the design names the site but not the mechanism. It is implemented as one more explicit report-only Evaluate (model.legacy, EG-MDL-07, https://ollama.com) before a cloud model's chat. The client's guard handler still records the send to the daemon, so a cloud chat produces two decisions. This adds one explicit guard site that 4.9 must move to the positive convention, so the 16 sites become 17.
- LLamaSharp type check: the F5 convention confines the name LlamaSharpChatClient to its own file and the governed registrations, so MeaiEgressDestination.cs cannot name it. The check lives in a new internal static helper, InProcessChatClient.IsLlamaSharp, inside LlamaSharpChatClient.cs: inner.GetService(typeof(LlamaSharpChatClient)) is LlamaSharpChatClient, which is exactly the design's check. F5 is unchanged and green.
- The cloud-model rule has two copies: EgressGuardChatClient.IsOllamaCloudModel in AI.Pipeline and OllamaProvider.IsOllamaCloudModel in Infrastructure. The two assemblies share no internal helper, and the 4.1 records row allows no PublicAPI change in Abstractions. A 13-row parity theory, TheCloudModelRule_IsTheSameOnBothOllamaRoutes, pins that the copies agree. The rule matches ids ending in -cloud or :cloud, trimmed and in any case, per the design's parenthetical; a tag like 'mycloud' does not match.
- file: names are recorded as written, path included, as the design and scout say ('never URL-parsed'). The old record wording 'scheme, host and port only, never userinfo, path or query' therefore now applies to URIs and URL-shaped names only. The EgressRequest remarks, cert-gate row 64 and the CHANGELOG say so. The new decision-test theory asserts class, label and basis directly instead of using AssertRow's URL-redaction helper.
- SPEC-007 has no threat-model section. The loopback-relay Known limit (§2.6: OTLP collector, docker daemon, kubectl proxy, local LLM gateways; *.localhost trusted by spelling) went only into docs/EgressInventory.md Known limits, which also says the experimental EG-MDL-11 proposer does not apply the cloud rule. SPEC-007 got only the one '(this PR)' status line.
- File-door site twins avoid filesystem side effects. The EG-MESH-08 twin exports to a nonexistent //127.0.0.1/<guid>/ share, so the write after the decision fails. The EG-MESH-07 twin gives the entry id a NUL, so Directory.CreateDirectory throws ArgumentException after the decision and nothing is created; on Linux as root, //127.0.0.1/x would otherwise create /127.0.0.1/x.
- I ran nine mutations instead of the design's 'four'; they are a superset.
- More MEAI twins moved off FakeChatClient than the design's four (listed in the PR body). AGovernedTargetWithoutThePipelineOptions_StillResolvesTheOllamaEndpoint became LocalOllama_WithACustomInnerClientThatNamesNoUri_FailsClosedToItsKey. LocalOnnx_RecordsNoDecision was split into LocalOnnx_WithTheLlamaSharpClient_RecordsNoDecision and LocalOnnx_WithACustomInnerClientThatNamesNoUri_FailsClosedToItsKey. ResolutionFaults now faults through a throwing GetService, and keeps a throwing-options case on a Bedrock key.
- Extra record: the Bedrock Known-limit bullet in docs/EgressInventory.md now says an absolute ProviderUri reported by the inner client overrides the region reconstruction. Whether the AWS MEAI client reports one is unverified. This one line is the only difference between 4e170e2, where the mutations, the full cert-gate and the net10 runs were done, and the final head 1e3e645, where the convention class and the repo gates were re-run.

### Lane open issues

- 4.9 integration: OllamaProvider.cs now holds an explicit `_ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(...EG-MDL-07...))` (cloud models only). The positive convention (D22) and the ThrowIfRefused conversion must include it, so the explicit sites number 17.
- Expected consequence under 4.11: the mesh-serve peer is never Host now, and Kestrel's thread has no subject frame. Every mesh serve on AirGapped or SecureWorkstation will therefore be refused until the served package's label becomes the subject (gap 4 / PR 5). This PR makes that visible; it does not cause it.
- Merge conflicts are likely with the other 4.x lanes in: SPEC-007's status bullets (my line is marked '(this PR)' and needs the PR number), the top of CHANGELOG's Unreleased ### Changed, cert-gate-assertions.md row 64 (my occurrence count of 149 assumes no other TSV move; 4.2 adds an Exempt:GuardImpl occurrence), docs/EgressInventory.md Known limits and the redirect bullet (4.3), and docs/knowledge-graph.{json,md}, which must be regenerated after git add on the integrated tree.
- Not verified: whether AWSSDK.Extensions.Bedrock.MEAI reports ChatClientMetadata.ProviderUri (if it does, rule 1 records it with site EG-MDL-02); whether *.localhost reaches DNS on glibc or musl.
- Follow-up idea, not in scope: Ollama's /api/tags manifest carries remote_host and remote_model for relayed models, which would identify cloud models more precisely than the name suffix. EG-MDL-11 (the experimental proposer) does not apply the cloud rule.
- The clone has local helper branches that must not be pushed: wip/4.1-twins (1f92e73, the twins-only commit cited as red-at-base), wip/4.1-mut-ref (4e170e2, the mutation ref) and wip/4.1-pre-squash.
- The PR body's Release and Checklist sections assume no versioned release. Posting the agent-bus handoff for this PR is the integrator's job.

## 4.2: branch `claude/spec-007-pr4-4.2-syncsend` @ `25301b79a`

### Check verdict

The change is sound, with record fixes needed before the PR. Checked read-only at 25301b7; I did not run dotnet.

**Mechanism.** It is correct, and the net8.0 and net10.0 assets are byte-for-byte unchanged in behaviour. On those assets `Guarded` returns the same `new EgressGuardHandler(inner, …)` as before. `PublishNoDecision` and `NotEvaluatedBasis` compile there but nothing calls them. `CreateDelegatingHandler` only changes under `#if NETSTANDARD2_0`, so nothing refuses on any asset Ashlar itself binds.

**netstandard2.0 path.** On .NET 5 and later, a synchronous send goes `DelegatingHandler.Send` → the hop's inherited `HttpMessageHandler.Send` → `NotSupportedException`. No Ashlar frame is on that path, so the build-time record is the only feasible hook. C# emits `newslot` for a non-override virtual, which confirms deviation 1 is technically forced.

**The row is implemented.**
- The hop, the `NoDecision` record, the factory-handler refusal, the ALC twin inside the cert-gate project and the SDK-policy statement are all present.
- The done-when is pinned: the synchronous send throws `NotSupportedException` with 0 inner sends, and `SendAsync` produces exactly one decision.
- M1 (drop the hop) went red inside the full cert-gate filter: 4 of 2550 failed, then 2550 of 2550 passed.
- Extras: the event-source harness fix with its 8th fact, the `Over` factory, and `PublishNoDecision` compiled on every TFM. Each is justified, and nothing outside the row changed.

**Red at base is real.** b01f57b is 79e988c plus the identical twin file and csproj change. It went 5 of 8 red with the gap itself in the message ("returned 202, 1 inner send(s), 0 decision(s)"). The 3 that passed at base are each covered by a mutation instead: M4 for ownership, M6 for the event source.

**Mutations.** All six logs show KILLED with porcelain `[]` at ref 25301b7. The flake probe after the fix shows 0 of 20 failures.

**Records verified against the code.**
- TSV: 86 rows, 149 occurrences, 48 guarded, 35 Exempt, http.new 19.
- New TSV row cites `:47`; the `EgressHttp` row cites `:54` and `:75`. All three lines are correct.
- Certification folder: 126 .cs files, 129 entries in all.
- The cert-gate row 64 claims match the twin's assertions.
- CHANGELOG, `docs/EgressInventory.md` and `docs/SdkCompatibilityPolicy.md` describe the build-time record accurately.
- No PublicAPI change was needed: `EgressHttp` is still in Unshipped.

**Remote destination recorded as Host.** On these routes the only remaining ways are both 4.3's: redirects the primary handler follows, and URI-rewriting `DelegatingHandler`s inside `inner`. The new record's destination is `unknown` and its class is `Unknown`, never Host.

**What remains, in order:**
1. Get sign-off on the build-time-record deviation.
2. Fix two claims this merge makes false: the SPEC-007 gaps bullet and the `EgressDecision.Fault` XML doc.
3. The low items below.

### Check findings

1. **[medium]** `src/Ashlar.Abstractions/Security/Egress/SynchronousSendRefusedOnNetstandard20Asset.cs:45-50; src/Ashlar.Abstractions/Security/Egress/EgressGuard.cs:35 (NotEvaluatedBasis), :134-170 (PublishNoDecision)`
   - **Problem:** This departs from the design: design §2.6 and row 4.2 say the hop publishes the NoDecision record when it refuses a send, but the record is published when the client or handler is BUILT.
- On any runtime with a synchronous Send, every EgressHttp.CreateClient or Wrap call now publishes one allowed=False/NoDecision record with destination unknown.
- This includes clients that only ever call SendAsync, and code that builds a client per call, such as the pattern in WorkflowCommand.cs:535.
- A refused send itself leaves no record.
- Both bases read "not evaluated: the egress is refused before it can be evaluated", which tells an operator that an egress was attempted and refused when none was.
- The lane raised this in its report, but nothing in SPEC-007 or the design records it as an accepted deviation.
   - **Fix:** Get the integrator's or owner's explicit decision before merge: keep the build-time record, or follow S4's original (no record, only the runtime exception). If it is kept:
- reword NotEvaluatedBasis to say what the record is, e.g. "not evaluated: synchronous sends through this client are refused on the netstandard2.0 asset";
- record the deviation, with its reason (no Ashlar frame on the Send path; C# emits newslot), in SPEC-007 beside the 4.2 status line.
2. **[medium]** `docs/specs/SPEC-007-security-labels-and-reference-monitor.md:45-47`
   - **Problem:** The 'Gaps carried to PR 4' bullet still says the netstandard2.0 asset does not evaluate a synchronous Send, so on .NET 5 to 7 it 'goes out unevaluated' and 'has to be closed'. Once this PR merges that is false, which is exactly the kind of claim the agent-bus drift audit is aimed at. The lane listed it as an open issue instead of fixing it.
   - **Fix:** In this PR, edit the bullet to say the netstandard2.0 synchronous-Send gap is closed by PR 4.2 (refused, plus a build-time NoDecision record), and keep the redirect and Host-record gaps as open. At merge, replace '(this PR)' on line 67 with the PR number and merge SHA.
3. **[medium]** `src/Ashlar.Abstractions/Security/Egress/EgressDecision.cs:110-114 (Fault), :88 (CurrentBasis)`
   - **Problem:** Two public XML docs are now false or incomplete.
- The `Fault` doc says it is "null, or the full type name of the exception that stopped the evaluation". PR 4.2 publishes Fault = "SynchronousSendUnsupported", which is not an exception type name, so a consumer that parses Fault as a type gets it wrong on that asset.
- The `CurrentBasis` doc lists only `subject:<id>` or `no-subject`. PublishNoDecision adds the NotEvaluatedBasis text. (The faulted basis was already missing before this PR.)
   - **Fix:** Update both summaries: Fault is null, an exception's full type name, or the fixed code SynchronousSendUnsupported (netstandard2.0 asset, published at build). Add the not-evaluated and faulted bases to CurrentBasis, and to DestinationBasis if the docs enumerate bases. These are doc-only, so PublicAPI is unaffected.
4. **[low]** `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressHttpNetstandard20TwinTests.cs:528-530 (RecordSink line), :116-128`
   - **Problem:** The twin's record line does not pin six of the new record's fields:
- Current and CurrentBasis;
- DestinationClass, DestinationLabel and DestinationBasis;
- ProfileEnforcesByDefault.

A mutation that weakens the fail-closed placeholders in PublishNoDecision would survive, for example Current = Public, or DestinationClass = Host. Two paths are also untested:
- the custom-IEgressGuard → ProcessDefault profile fallback (`guard as EgressGuard ?? EgressGuard.ProcessDefault`);
- the swallowed ResolveProfile fault.
   - **Fix:** Add class, label, current and both bases to the RecordSink line and pin them in RefusalRecord. Add one BuildEveryShape case with a stub IEgressGuard and assert the record's profile comes from ProcessDefault. Optionally run a mutation that flips SystemHigh to Public in PublishNoDecision.
5. **[low]** `src/Ashlar.Abstractions/Security/Egress/EgressGuard.cs (PublishNoDecision `new EgressDecision(` with ReportMode)`
   - **Problem:** This collides with PR 4.6 when the two merge. 4.6 changes the internal EgressDecision constructor (ModeBasis, Refused, Ref) and how the mode is resolved, but PublishNoDecision hard-codes ReportMode. Under enforce, 4.7's event 2 and its windowed Warning sink would log every build-time record as a refusal: one Warning per client built on .NET 5-7.
   - **Fix:** Whichever of 4.2 and 4.6 merges second must decide the mode and basis of the build-time record, and keep it out of the refusal Warning (e.g. Refused=false, or a distinct basis). After 4.1 merges, recount the counts in row 64 and the convention-test comment.
6. **[low]** `src/Ashlar.Abstractions/Security/Egress/SynchronousSendRefusedOnNetstandard20Asset.cs:32; EgressHttp.cs Wrap`
   - **Problem:** The hop is an HttpMessageHandler, not a DelegatingHandler. On the netstandard2.0 asset this has two consequences:
- Any walker of the handler chain stops at the hop, and so never reaches the primary handler. This affects 4.3's primary walk that flips AllowAutoRedirect, and Grpc.Net.Client's handler-type detection when external .NET 5-7 code builds a GrpcChannel over a Wrap handler.
- `((DelegatingHandler)EgressHttp.Wrap(...)).InnerHandler = x` before the first send silently removes the hop (the record was already published), and synchronous sends go out unevaluated again.
   - **Fix:** Give the hop an internal `Inner` accessor and have 4.3's walker step through it, or fold the hop into EgressRedirectHandler as the design intends. Document in the Wrap remarks that replacing InnerHandler voids the guarantee, or make the hop the guard's own field rather than a settable InnerHandler.
7. **[low]** `src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj:132-147; .github/workflows/build-gate.yml:36-39`
   - **Problem:** The required build-core check runs `dotnet build Ashlar.LocalDevCore.slnf`: no -f, both TFMs of Tests.Infrastructure in parallel, with Ashlar.CLI also pulling in Abstractions. No log in 4.2-logs runs that build with the new SetTargetFramework=netstandard2.0 reference and copy target. The lane's evidence covers only:
- cert-gate's `-f net8.0` build;
- filtered twin runs on net8.0 and net10.0;
- a standalone Abstractions build.
   - **Fix:** Before opening the PR, run the build-core command through scripts/test-in-container.sh, or confirm build-core is green on the PR, before relying on the change.
8. **[low]** `docs/SdkCompatibilityPolicy.md:175-177`
   - **Problem:** The new section has two inaccuracies:
- "on them it evaluates every send" overclaims, because redirect hops are evaluated on no asset until 4.3.
- The refused population is not only ".NET 5, 6 or 7" apps: any app that binds the netstandard2.0 asset and runs on a .NET 5+ runtime is refused, e.g. a netcoreapp3.1 app rolled forward, or a plugin loaded by path. 'Mono' here means classic Mono only; Mono-based .NET 6+ (Android, iOS, WASM) has a synchronous Send.
   - **Fix:** Say "evaluates every request it is handed (redirects: see docs/EgressInventory.md)". Describe the refusal as applying wherever the netstandard2.0 asset runs on a runtime that has a synchronous HttpMessageHandler.Send (.NET 5 or later), and say 'classic Mono'.
9. **[low]** `CHANGELOG.md:148-167`
   - **Problem:** EgressHttp has not shipped: it is in PublicAPI.Unshipped.txt, no tag contains it, and it is under [Unreleased] → Added. A 'Changed' entry therefore describes a change from behaviour no consumer has ever seen.
   - **Fix:** Fold the netstandard2.0 refusal into the existing Added entry at CHANGELOG.md:86-91, which already points to it, and drop the separate Changed bullet. Or keep the bullet and say the API is new in this release.

### Lane deviations from the design

- The design says the hop publishes the NoDecision record when it refuses. That cannot be done at refusal time. On netstandard2.0 nothing can override HttpMessageHandler.Send: C# emits newslot for a non-override virtual. The runtime's DelegatingHandler.Send then calls the hop's inherited base Send, and no Ashlar frame is on that stack. The only alternative hook, a process-wide AppDomain.FirstChanceException handler, was rejected: it runs on every exception in the host, cannot attribute the refusal to a family or site, and needs re-entrancy guards. Implemented instead: the hop publishes ONE NoDecision record (Fault SynchronousSendUnsupported, destination unknown, family and site, report mode) when it is BUILT on a runtime that has a synchronous Send. Cost: a SendAsync-only client also leaves that record, and there is no record per refused send. If the integrator or owner prefers S4's original (no record, only the runtime exception), delete the two lines in the hop's constructor and the record assertions.
- Gap 2 says the hop 'is the EgressRedirectHandler itself'. That handler belongs to PR 4.3 and does not exist yet, so 4.2 ships the hop as its own internal type, SynchronousSendRefusedOnNetstandard20Asset. The name is chosen so that the runtime's NotSupportedException message explains itself. 4.3 must either fold it into EgressRedirectHandler on ns2.0 or keep it below. The twin asserts the hop's type name, and the TSV pins its file.
- The record's profile comes from the guard passed to EgressHttp when that guard is an EgressGuard; otherwise (a null or custom IEgressGuard) it comes from EgressGuard.ProcessDefault. The design did not specify this.
- Added beyond the row, because the harness needed it: the harness enables the guard's own 'Ashlar-Egress' EventSource before it loads the isolated copy, and an 8th fact plus mutation M6 pin that. Without this, the second same-named EventSource in the cert-gate process stopped EgressGuardDecisionTests' listener from ever enabling the guard's source; it failed 18/20 runs beside the twin. No production code changed for it.
- The hop is built through a static factory Over(...), not `new` in EgressHttp, so that the design's drop-the-hop mutation compiles. Otherwise CA1812 under TreatWarningsAsErrors makes the mutant a build error.

### Lane open issues

- Merge with 4.6. PR 4.6 changes EgressDecision's internal constructor (ModeBasis, Refused, Ref) and the mode resolution, so the new EgressGuard.PublishNoDecision (EgressGuard.cs, the `new EgressDecision(` call after NotEvaluatedBasis) must gain those arguments. Its Mode is hard-coded to ReportMode today. The integrator decides what mode and basis a build-time NoDecision record carries; under enforce, 4.7's event 2 would log it as Refused. PublishNoDecision compiles on every TFM, so a missed argument fails every build.
- Recount at integration, because these counts assume 4.2 alone on 79e988c and 4.1 also moves TSV rows:
- ci/cert-gate-assertions.md row 64: 86 rows, 149 occurrences, 48 guarded, 35 Exempt, and the measured floors 2,131/149/67;
- the EgressGuardConventionTests floors comment;
- the Tests/Certification count paragraph (126 .cs, 129 entries), since other lanes add files too.
- SPEC-007: I added only one '4.2 (this PR)' line under the PR 4 plan bullet. The older 'Gaps carried to PR 4' bullet (docs/specs/SPEC-007-…md:44-50) still describes the ns2.0 gap as open. A drift audit will flag it; fix it at integration or in the drift follow-up.
- A pre-existing hazard, found here and not introduced by this PR: any process that loads two copies of Ashlar.Abstractions (for example plugin AssemblyLoadContexts) has two 'Ashlar-Egress' EventSources. .NET 8 will not initialize the second one's descriptors while the first lives (EventSource_EventSourceGuidInUse), nor the first one's if it was never enabled before the second appeared. Decisions still reach EgressDecisionLog sinks; only the EventSource path goes dark. This may deserve a Known-limits line in docs/EgressInventory.md.
- Not verified by a run:
- the false branch of RuntimeHasSynchronousSend (.NET Framework, Mono, Unity), because the devtest image has no such runtime;
- S4's claim that the runtime's request telemetry may count a send twice under an outer HttpMessageInvoker on .NET 5–7, which is stated as 'may' in the hop's remarks and the PR body.
- Not run locally: build-core, kernel-gate, kernel-coverage-gate and lychee. No links were added, only backticked paths.
- Agent-bus handoff is not posted (lane rules leave PR and handoff to the integrator). The 'Records' section of the PR body lists the records it touches, for Grok's drift audit.
