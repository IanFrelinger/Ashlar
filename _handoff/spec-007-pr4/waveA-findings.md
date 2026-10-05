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

## 4.4: branch `claude/spec-007-pr4-4.4-frames` @ `5f56b8ff4`

### Lane result

- **Base:** `79e988c` (#713). One squashed commit. The PR body draft is `pr-4.4-body.md`.
- **What it does:** the chain join (every live frame's mark, with the innermost frame's basis), propagation into enclosing frames on dispose, `EgressSubject.Observe` into every live frame, `EgressSubject.BeginRead`/`ReadScope` (an unreported or thrown scope observes SystemHigh), and an internal `EgressSubject.Detach` at the single AgentBus dispatch point.
- **Red at base:** the base-compilable twins were committed alone at `cb64de5` on `79e988c`: net8.0 11 of 148 failed, each for the intended reason. The twins that need the new API cannot compile at base.
- **Green at `5f56b8f`:**
  - targeted filter: net8.0 161/161 and net10.0 161/161;
  - full cert-gate (net8.0): 2566/2566, skip guard 0, against the expected 2561 or more;
  - Ashlar.Tests.Orchestration 302/302 (net8.0 only; the project has no net10.0 target);
  - Ashlar.Tests.BackgroundAgents `~Egress` 14/14.
- **Builds:** Ashlar.Abstractions on netstandard2.0, net8.0 and net10.0: 0 warnings, 0 errors. Ashlar.Orchestration: 0 warnings, 0 errors.
- **Repo gates:** all 25 passed, before and after the squash.
- **Mutations:** 9 of 9 KILLED through `scripts/mutation-check.sh` at `8a4c295`, which differs from the head only in the knowledge graph:
  - m1 innermost only, 3/161;
  - m2 no propagation on dispose, 2/161;
  - m3 Observe innermost only, 2/161;
  - m4 Observe satisfies a read scope, 1/161;
  - m5 an unreported read observes nothing, 4/161;
  - m6 a thrown read keeps its report, 2/161;
  - m7 Detach does not detach, 4/161;
  - m8 AgentBus not detached, 3/161;
  - m9 the detachment forwards the chain, 3/161.

### Lane deviations (the check judged each reasonable and fail-closed)

- `ReadScope.Complete()` was added, on the TransactionScope pattern: a scope disposed without `Complete()` observes SystemHigh even after a report. This is one public member more than the row lists.
- "Read nothing" is `ReadScope.Report(SecurityLabel.Public)`. No separate member.
- A `ReadScope` belongs to whoever begins it; it is not ambient. Only the holder can `Report`, so `Observe` cannot satisfy it by construction. **4.5** must carry RAGTool's label from the labelled-tool marker to `ToolCallingAgent`, which then calls `read.Report(...)` and `read.Complete()`.
- `InternalsVisibleTo Ashlar.Orchestration` was added to Ashlar.Abstractions.csproj for `Detach`. This is outside D9; see check finding 4.
- `Detach` is a detachment frame (no mark and no enclosing frame), not a null frame. A dispose from another flow restores nothing there, and m9 shows why the detachment must not forward the chain.
- The SPEC-007 line is "**4.4** (this PR)" under #713's PR 4 plan bullet. Cert-gate row 64 was revised and the Certification count paragraph moved to 128 `.cs` files and 131 entries.

### Check verdict

Approve with changes: fix finding 1 before merge, or record it explicitly as a known limit. Production behaviour is unchanged:
- no production code calls `Enter` or `BeginRead`, so every decision stays `no-subject`;
- the only new production call is `Detach` in AgentBus;
- the guard still only reports.

The check read all nine mutation logs and found them real. The records match the code: three cert-gate rows, the counts re-counted with `git ls-files`, the knowledge graph's 3 files and 24 facts, the CHANGELOG under Unreleased/Added, and the TSV untouched.

### Check findings

1. **[medium] An ancestor disposed out of order is a write-down.**
   - **Where:** `src/Ashlar.Abstractions/Security/Egress/EgressSubject.cs:139-151` (`Frame.Resolve` walks ancestors through `Live()`); pinned as intended by `EgressSubjectNestingTests.cs:83-104` (`A_frame_disposed_out_of_order_leaves_the_chain`) and `ci/cert-gate-assertions.md:65`.
   - **Problem:** inside `Enter("parent", Secret)`, a fire-and-forget `Task.Run` enters `Enter("child", new HighWaterMark())`. While the parent is live, the child decides Secret. Once the parent's `using` ends, the child's chain skips the disposed parent, so it decides Public and is allowed, although its closures may hold Secret data. Without the inner frame the same task would fall back to SystemHigh. This contradicts the class remark at `:24` and D12's aim of closing the declassification hole.
   - **Fix (phase B, integrator's call: fail closed, as D12 intends):**
     - keep `Live(chain)` for the innermost frame;
     - for the ancestors of the innermost live frame, walk `_previous` directly and join every subject frame's mark, live or disposed, stopping at a detachment;
     - flip `A_frame_disposed_out_of_order_leaves_the_chain` to expect Secret and LevelTooLow;
     - add the child-task twin;
     - reword row 65 and the class remarks;
     - mutation-check it (ancestors walked through `Live`) and watch it go red.

     Today's behaviour is kept only if the owner chooses it. Then it moves out of the pinned properties into a SPEC-007 known limit.
2. **[low] The rule that a scope observes into the chain it began on is not pinned.**
   - **Where:** `EgressSubjectReadScopeTests.cs:159-180`; `ReadScope.cs:35`, `:77`.
   - **Problem:** the only cross-flow twin ends the scope inside `Task.Run`, which inherits the same chain. A mutant that captures nothing at `BeginRead` and calls `EgressSubject.Observe(read)` at Dispose passes every twin, and fails open under `SuppressFlow`, a detachment or a frameless thread. The late-Report rule has a test but no mutation run.
   - **Fix:** add a twin that ends the scope on a flow without the begin chain (`ExecutionContext.SuppressFlow()` with `Task.Run`, a new Thread, or a `Detach`) and asserts the begin-chain marks rise. Mutation-check that mutant, and a late-report mutant (drop the `_ended` branch in `Report`).
3. **[low] The guard's class doc is stale.**
   - **Where:** `src/Ashlar.Abstractions/Security/Egress/EgressGuard.cs:15-16`.
   - **Problem:** it still says the current label is "the active EgressSubject frame's high-water mark".
   - **Fix:** say it is the join of every live frame's mark on the flow's chain, with basis `subject:<innermost id>`, or SystemHigh (`no-subject`) with none.
4. **[low] The new `InternalsVisibleTo Ashlar.Orchestration` is outside D9.**
   - **Where:** `src/Ashlar.Abstractions/Ashlar.Abstractions.csproj:36`.
   - **Problem:** the grant exposes every Abstractions internal to Orchestration: `AshlarDeploymentProfileEnvironment.NoteResolved`/`ClearResolved`, the internal `EgressDecision` constructor, `EgressSubject.Frame`, and from 4.6 the mode latch and reset seam. D3, D5 and D41 assume few callers can reach those. The amendment is recorded only in the commit message.
   - **Fix:** record the D9 amendment in SPEC-007's PR 4 notes. In 4.6, or when 4.4 and 4.6 are integrated, pin the callers of the reset seam and the latch setters with a convention fact.
5. **[low, design-level; for 4.5] A read scope observes only when it ends.**
   - **Where:** `ReadScope.cs:77-85`, against design §2.2's producer rule and D19.
   - **Problem:** a tool that reads and then egresses within the same call has that egress decided at the pre-read mark. This is safe while every production frame is SystemHigh (4.5's self-extend floor). The leak test's Public-floor runner, and any later low-floor runner, are exposed.
   - **Fix:** in 4.5, record it as a known limit for runners whose floor is below SystemHigh. The stricter alternative (an open, unreported scope counts as SystemHigh for decisions on its chain) is an **owner call**: it changes Scenario B's expected reason from LevelTooLow to SystemHighData.

### Lane open issues

- Fill the PR number into the SPEC-007 "**4.4** (this PR)" line. The other lanes add bullets at the same place, so expect a trivial conflict.
- The `ci/cert-gate-assertions.md` count paragraph (128 `.cs`, 131 entries) conflicts with every lane that adds Certification files. Recount after each rebase. Regenerate the knowledge graph after `git add`.
- **For 4.5:**
  - `ToolCallingAgent` wraps each tool call as `using var read = EgressSubject.BeginRead(); …; read.Complete();`;
  - a labelled tool calls `read.Report(label)`;
  - RAGTool's "read nothing" is `Report(SecurityLabel.Public)`;
  - the scope is disposed on every path (M15);
  - the floor-pinning convention for production `EgressSubject.Enter` calls (D19) is 4.5's. 4.4 pins only the Detach call site, AgentBus.cs ×1.
- A HighWaterMark observed directly, not through `EgressSubject.Observe`, after its frame is disposed does not reach the enclosing frames. This is documented in the remarks but not tested.
- Not run: the CLI egress twins (`application/src/Ashlar.Tests.CLI`, `EgressCliSiteTwinTests`). They use one frame per case, so nesting does not affect them.

## 4.6: branch `claude/spec-007-pr4-4.6-mode` @ `a501c2b26`

### Lane result

- **Base:** `79e988c` (#713). One squashed commit, `a501c2b` (tree `f596ff9`). The pre-squash WIP head `78f573e`, which was the pushed branch before, has the identical tree. The PR body draft is `pr-4.6-body.md`.
- **What it does:**
  - one mode resolver with a 6×4 table, adding `ModeBasis`, `Refused` and `Ref` to the decision and appending them to event 1;
  - three opt-ins: `ASHLAR_EGRESS_MODE` read once, the raise-only `AshlarHostingOptions.EgressMode`, and the `EgressGuard(profile, mode)` constructor;
  - strictest profile wins (a later `AddAshlar` cannot lower AirGapped);
  - the reset seam `EgressProcessState`, and the process-global convention extended to it;
  - the composed guard replaces only `ProcessDefault`;
  - IVT for AI.Pipeline;
  - a startup line: Information log event 7302, plus one stderr line when the mode is not plain report.

  Every profile still defaults to report, and nothing reads `Mode`, `ModeBasis` or `Refused`, so nothing refuses.
- **Evidence:**
  - Ashlar.Abstractions builds on every TFM with 0 warnings and 0 errors, and Ashlar.Hosting builds with 0 errors.
  - Targeted run over the 17 touched classes: net8.0 604/604 and net10.0 609/609.
  - Full cert-gate (net8.0): 2637/2637, skip guard 0. The arithmetic is 2542 + 63 + 31 + 1.
  - The build-core equivalent (`Ashlar.LocalDevCore.slnf`) succeeded with 0 errors.
  - Repo gates: 25/25, and the regenerated knowledge graph had no diff.
- **Mutations:** 15 of 15 KILLED through `scripts/mutation-check.sh` at `78f573e`, which has the same tree as the head. The m13 first attempt was stopped by the 2-hour background limit and re-run. The red counts:

  | Mutation | Red |
  |---|---|
  | m01 an enforce override reports | 34/94 |
  | m02 a mode fault fails open | 1/31 |
  | m03 the last profile wins | 5/31 |
  | m04 the override is re-read | 4/31 |
  | m05 the composed guard is unbound | 3/33 |
  | m06 the option lowers the mode | 1/31 |
  | m07 the seam skips the latch | 1/31 |
  | m08 Refused ignores the mode | 5/200 |
  | m09 event 1 drops the ref | 2/200 |
  | m10 the explicit guard reads the latch | 1/31 |
  | m11 an AG composer is not serialized | 1/4 |
  | m12 the seam is skipped | 1/4 |
  | m13 the startup line is silent | 2/31 |
  | m14 an unrecognised profile reports | 4/231 |
  | m15 the stderr line goes to stdout | 1/31 |

### Lane deviations

- **The stderr startup line** is written by the first `AddAshlar` (or the first decision of a process-bound guard) whenever the mode is not plain report. It is not limited to host-less CLI verbs, because the library cannot tell whether a host will start.
- **D7 runs in the single resolver**, so it covers explicit-profile guards too. An unrecognised profile gives enforce with basis `profile:unrecognised`. Because of that, `EgressGuardDecisionTests.An_explicit_profile_is_reported_and_does_not_change_the_decision` gained a mode column, and its air-gapped-ish row now expects enforce.
- **A test seam was added to production code:** `EgressEnforcement.ModeResolutionProbe`, an internal static that is null in production. It exists because no input can make the pure resolver throw.
- **The reset seam** is the internal type `EgressProcessState` (Snapshot, Restore, Reset), plus `AshlarDeploymentProfileEnvironment.RestoreResolved`. Tests reach it by reflection through `Helpers/EgressProcessStateScope.cs`.
- **`ProcessGlobalEnvironmentConventionTests`** treats three more things as process-global writes:
  - an `AddAshlar` with AirGapped or SecureWorkstation;
  - an `EgressMode` assignment beside `AddAshlar`;
  - any use of the seam.

  It also gained a fourth fact, `No_file_that_leaves_egress_state_behind_skips_the_reset_seam`.
- **`AshlarHostingOptions.EgressMode` is a `string?`.** "enforce" or any other non-blank value raises the mode; "report" or null change nothing. Ashlar.Hosting has no PublicAPI baseline.
- **The constructor** became `EgressGuard(string? deploymentProfile = null, string? egressMode = null)`, replacing the one-parameter form (it was only in Unshipped), so the change is source-compatible.
- **The opt-in is named, hedged as unsupported, in `docs/EgressInventory.md`** (which §4's records table requires for 4.6) **and in the CHANGELOG.** No user-facing configuration doc mentions it.
- **Records beyond the row:**
  - cert-gate row 56's offender count was corrected from 22 to 20, the real size;
  - four `docs/EgressInventory.md` line citations were re-derived.
- **Defaults left to later PRs:** D6 goes to 4.11; D8's event 2 and D11's logging go to 4.7. The basis constants `break-glass`, `host-opt-out` and `operator-verb` are declared but never produced.
- **The activator** `EgressModeStartupActivator` is internal to Ashlar.Hosting. It logs category `Ashlar.Egress`, event 7302. 7300 is the decision record, and 7301 is reserved for 4.7's EgressRefused.

### Check verdict

PASS with fixes. The row is implemented as designed, and the behaviour that must not change did not change: every profile still reports, and no route reads Mode, ModeBasis or Refused.

The check verified:
- the 15 mutation definitions are real semantic changes;
- the counts recount;
- the knowledge graph rebuilds clean;
- the EgressInventory citations are right;
- the offender count fix from 22 to 20 is correct.

Flake risk from the process-global seams is nil today, because the EnvironmentVariables collection is `DisableParallelization` and xUnit 2.9.3 runs it after the parallel collections.

### Check findings

1. **[medium] The composed guard uses this call's profile, not the strictest profile noted in the process.**
   - **Where:** `src/Ashlar.Hosting/AshlarServiceCollectionExtensions.Egress.cs:37` (`BindComposedEgressGuard`), with `AshlarServiceCollectionExtensions.cs:111-126`; twin `EgressModeProcessBindingTests.A_second_AddAshlar_with_no_profile_does_not_lower_an_AirGapped_process`.
   - **Problem:** after `AddAshlar(AirGapped)`, a later `new ServiceCollection().AddAshlar()` in the same process notes nothing lower, so D5 holds for `ProcessDefault` and the validators. But that container's own `IEgressGuard` is `new EgressGuard("full", …)`, so its factory clients and MEAI targets decide under Full. The twin asserts only the noted profile and `ProcessDefault`. Two related problems:
     - A guard composed earlier keeps the override it captured, so a later `AshlarHostingOptions.EgressMode=enforce` does not reach it, although the option's doc says "for the whole process".
     - A second `AddAshlar` in the same collection replaces `EgressModeStartup` with its own profile, so the startup line can say "profile full" in an AG container.

     It is not exploitable today, because every production `AddAshlar` takes the profile from the same variable. At 4.11, only D6 would backstop it.
   - **Fix:**
     - compose with `AshlarDeploymentProfileEnvironment.ResolvedRaw ?? canonicalProfile` after `NoteResolved`, and build the startup line from the same value;
     - extend the twin to resolve the second container's `IEgressGuard` and assert `Profile == "air-gapped"` and `ModeBasis == "profile:air-gapped"`;
     - mutation-check it by reverting to `canonicalProfile`.

     This matches 4.6's own open issue for SecureWorkstation, and it closes the issue for both profiles.
2. **[low] `ProcessDefault` reads the profile twice per decision.**
   - **Where:** `EgressGuard.cs:171-186` (`ResolveMode`) and `:193-204` (`ResolveProfile`).
   - **Problem:** a concurrent `AddAshlar` between the two reads can give `ModeBasis profile:full` with `Profile air-gapped`.
   - **Fix:** read the effective profile once at the top of `Evaluate` and pass it to both. The explicit guard uses `_deploymentProfile`.
3. **[low] A null-profile guard with a constructor override never sees a raise.**
   - **Where:** `EgressGuard.cs:178` (`_egressMode ?? EgressEnforcement.ProcessOverride()`); the `AshlarHostingOptions.cs:20-29` doc.
   - **Problem:** `new EgressGuard(null, "report")` never sees the host raise or a latched `enforce`. The hosting-option doc claims a lower mode comes only from the environment variable.
   - **Fix:** for a null-profile guard, take the stricter of the constructor override and `ProcessOverride()`. Otherwise correct the doc and add the case to 4.11's D6 twin list.
4. **[low] Some documented behaviour is untested or never seen red.**
   - **Where:** `EgressEnforcement.cs:166-173` (`NoteHostingOption`).
   - **Problem:** nothing covers an unrecognised `AshlarHostingOptions.EgressMode` (for example "junk") raising to enforce. The randomness of `Ref` and `AddAshlar_keeps_a_guard_the_host_registered_before_it` have no mutation.
   - **Fix:** add a "junk" theory row asserting enforce, basis `override`, and a Warning. Mutation-check three more:
     - drop the Unrecognised raise;
     - make `Ref = Sequence.ToString("x16")`;
     - remove the `ReferenceEquals(ProcessDefault)` condition in `BindComposedEgressGuard`.
5. **[low] Event 1's payload grew without a version bump.**
   - **Where:** `EgressEventSource.cs:60`.
   - **Problem:** event 1 gained three fields, but its Version was not bumped. ETW and TraceEvent consumers key manifests on provider, id and version.
   - **Fix:** add `Version = 1`, and note in the remarks that appended fields bump it.
6. **[low; for 4.11] The profile and the mode latch differently.**
   - **Where:** `EgressGuard.cs:177` and `AshlarDeploymentProfileEnvironment.Effective`.
   - **Problem:** where `AddAshlar` never ran, `ProcessDefault` re-reads `ASHLAR_DEPLOYMENT_PROFILE` at every decision, so in-process code can lower AG to Full with `SetEnvironmentVariable`. The design accepted the per-decision read.
   - **Fix:** record it as a 4.11 decision or a known limit. One option: when the variable names AG or SW, `ProcessDefault` notes it through `NoteResolved`.
7. **[low] Record drift.**
   - (a) SPEC-007 "4.6 (this PR)" needs the PR number.
   - (b) Cert-gate row 64 still calls the guard "report-only" with no Mode, ModeBasis, Refused or Ref, and its Tests column omits `EgressModeResolutionTests` and `EgressModeProcessBindingTests`.
   - (c) The row says the opt-in "stays undocumented until 4.11", yet the CHANGELOG, EgressInventory and AddAshlar's XML doc name `ASHLAR_EGRESS_MODE`, hedged.
   - (d) 4.11's flip list names `An_explicit_profile_is_reported_and_does_not_change_the_decision`, which now already has an enforce row.
   - **Fix:**
     - fill the PR number;
     - add one sentence and the two classes to row 64;
     - (c) is the integrator's call: "undocumented until 4.11" is design default D-text, not an owner answer. The recommendation is to keep the EgressInventory section, which §4 requires, and trim the CHANGELOG and XML doc to say a mode field exists without naming the variable;
     - note in the 4.11 plan that 4.6 split that twin.
8. **[low] The fault probe is a process-global static.**
   - **Where:** `EgressEnforcement.cs:95` (`ModeResolutionProbe`).
   - **Problem:** it fires for every guard. It is safe only while it is set from the DisableParallelization collection.
   - **Fix:** make it `AsyncLocal<Action?>`, or document the constraint on `EgressProcessStateScope.SetModeResolutionProbe`.

### Lane open issues

- **Expected merge conflicts** with the sibling lanes:
  - the cert-gate count paragraph (this lane: 125 → 127 `.cs`, 128 → 130 entries);
  - SPEC-007's "4.6 (this PR)" line;
  - CHANGELOG Unreleased ### Changed;
  - `docs/EgressInventory.md`;
  - the knowledge graph;
  - `ProcessGlobalEnvironmentConventionTests`.
- **For 4.7:**
  - the record property is `EgressDecision.Refused`, while the design's helpers are `Refuses`/`ThrowIfRefused`; define `Refuses` in terms of `Refused`;
  - the ILogger sink (event 7300) does not yet carry Mode, ModeBasis or Ref; add them with the refusal sink.
- **For 4.11:** the composed guard carries its own call's profile (closed by check finding 1 if fixed in phase B).
- **Behaviour change** for Grok's drift audit and the CHANGELOG: after `AddAshlar(AirGapped)`, a later `AddAshlar` with another profile no longer lowers `ForbidsRemoteProtocolEgress` or `DisplayName`, and the MCP and A2A validators stay on AirGapped. Out-of-repo hosts that compose profiles twice in one process will see it.
- **One more stdout line:** `AddAshlar` adds one hosted service, which logs one Information line in `Ashlar.Egress` when a host starts. A CLI path that starts a host with console logging to stdout outside `--format-json` prints one more line. No test pins that output.
- **A blind spot in the convention:** `ProcessGlobalEnvironmentConventionTests` cannot see a class that composes AG/SW only through an environment variable read by a helper. This is stated in the class remarks and in row 56.
- **Not run locally:**
  - the whole `Ashlar.Tests.Infrastructure` suite outside the cert-gate filter;
  - `Tests.CLI`, `Tests.AI.Pipeline`, the Mcp and A2A suites;
  - `make kernel-gate` and `make test-prod-style`.
