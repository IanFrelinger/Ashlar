# Changelog

All notable changes to Ashlar are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Commit messages follow Conventional Commits by convention (no commitlint hook or workflow enforces it); `scripts/changelog-snippet-for-release.sh` drafts release notes from the commits since the last `v*` tag.

At release time, move the `[Unreleased]` notes under a new `[X.Y.Z] - YYYY-MM-DD` heading, bump `VERSION`, tag `vX.Y.Z`, and publish a GitHub Release.

## [Unreleased]

### Added

- **Automated dogfood campaign.** `make dogfood-campaign` (and every other `make dogfood-*` target) runs inside the repo's Dev Container via `scripts/run-in-devcontainer.sh` so the .NET SDK is not a host install. A release-manager coordinator dispatches specialist sub-agents (docs-drift, regression, developer-tool); silence is fail-closed. Agent set: `docs/background-agents/examples/dogfood-campaign.json`. Operator page: `docs/DogfoodCampaign.md`.
- **Product split scaffolds** under `products/` (workstation, cluster, cloud, native) and framework distributed contracts (`ExecutionEnvelope`, `ResultEvidence`, `ITaskScheduler`, `INativeExecutionHost`).
- **`AshlarDeploymentProfile.SecureWorkstation`** — local trust, agents, RAG, and observation without runtime transport. `AirGapped` remains the slim offline profile. Under AirGapped, MCP client, A2A, and MCP server are all refused. Under SecureWorkstation, MCP client and A2A refuse enablement; local MCP server remains allowed.
- **`products-gate`** runs extractable product scaffolds plus `DistributedContractTests`.

### Changed

- **Docs** — product-split, operator env-var tables, protocol, licensing, and CI inventory distinguish `AirGapped` from `SecureWorkstation`, drop the `Ashlar.Client` product-consumption overclaim, and document `products-gate` / `DistributedContractTests` ownership.
- **Published pin is 0.1.2.** `ci/published-version` matches nuget.org and GHCR `nexo-cli:0.1.2`. Consumer-template pins follow the pin. The v0.1.2 `release.yml` pack-and-publish job timed out on nuget.org index lag after a successful push; visibility/registration polls now default to 40×15s (and raise a shorter repo-var budget) so the next tag does not fail the same way. nuget.org restore samples pin `System.Text.Encodings.Web` **10.0.11** so they do not NU1605 against the 0.1.2 graph.

### Fixed

- **Test ownership** registers `products/tests/Ashlar.Tests.Products` (cert-gate convention test) and records `products-gate` as the runner for the `DistributedContractTests` subset of `Ashlar.Tests.Contracts`. Product tests themselves execute in **`products-gate`**, not cert-gate.
- **UAT tier 9** experimental-not-promised no longer treats English phrases such as "the class name" as a type called `name`.
- **Distributed contracts** factories reject undefined enums, malformed digests, non-positive duration, and succeeded evidence without a hash.
- **SecureWorkstation composition** cannot be weakened by a later `configure` callback (profile and `TrustEnabled` are re-asserted). MCP client, A2A, and MCP server validators honor the profile `AddAshlar` recorded (`NoteResolved`), not only `ASHLAR_DEPLOYMENT_PROFILE`.
- **Cluster scheduler** is idempotent on the same envelope id + hash and refuses a conflicting hash (`TryAdd`).
- **ashlar-cloud** records reject blank ids / non-positive quotas, and the dependency-boundary gate forbids cloud → `src/` or `commercial/` references.

### Security

- **A package could forge its own `× REFUSED` row into a green `✓ ADMITTED` one through a field nobody had treated as sender-chosen: its `formatVersion`.** That field is a `required` JSON string whose value is whatever the file says, it has no length cap, and `ExtensionPackaging.TryOpen` compares it **first** — before the seal, before the record signature, before the sealer/admitter binding — then quotes it into the refusal. So the bytes reaching the terminal had been verified by nothing at all. A `formatVersion` of `ashpkg/v1` + CR + an SGR-green counterfeit returns the cursor to column zero and repaints the refusal as an admission **carrying a sealer fingerprint**, which is precisely the one part of a row the sender is not supposed to be able to choose; a trailing SGR 8 conceals the `'; expected 'ashpkg/v1'.` tail that would give it away. Every `pkg` verb was affected — `show`, `import` and `publish` on stderr, and `pull`'s `× REFUSED` row, whose **left** half (the filename) an earlier change escaped while leaving the **right** half (this reason) raw. All six sinks now go through the shared escaper, as does `pull`'s `× REJECTED` row and `import`'s `× REFUSED — …`. Earlier reviews cleared this sink three times because they tested **malformed** JSON, where `System.Text.Json` hex-escapes control bytes into its own syntax-error text; a document that *parses* and carries the escapes inside a string bypasses that entirely. The same escaping now also covers the other sender-chosen text those reasons quote — a package's declared file paths and the signed-claim mismatch messages.
- **Sender-chosen text on a refusal line is now length-bounded as well as escaped** (2,000 characters, then an explicit `… [truncated: N characters, limit 2,000]`). Escaping is one-for-one, so it makes a hostile string safe without making it shorter: with `formatVersion` uncapped under the 16 MiB document ceiling, a sender could still choose how many characters the operator's terminal had to render and scroll the row naming the refused file — and every row before it — out of the scrollback.
- **The mesh serve daemon was a sixth `.ashpkg` read path that never went through the bounded read, and on it the gap was an arbitrary file read.** `GET /mesh/v1/pkg/{file}` and the `GET /mesh/v1/index` listing both gated on `FileInfo.Length` and then handed the path to `Results.File`. A file name is validated against a traversal-free pattern and the resolved path is checked for containment — but `Path.GetFullPath` does not resolve symlinks, so a link planted in the published directory satisfies both while pointing anywhere on the host. Measured against the real serve path: `ln -s /etc/passwd d-secret.ashpkg` returned `/etc/passwd`; `ln -s <a 40 MiB file> linked-big.ashpkg` was advertised at **23 bytes** (the length of its target's *path string*) and served at 41,943,040 — ten times the documented 4 MiB ceiling; and one `GET` on a planted FIFO blocked inside `open(2)` in Kestrel's `SendFileAsync`, which a client disconnect cannot unblock, so 105 concurrent requests (the connection limit is 100) wedged the node until `hello` and `index` stopped answering. Both sites now open through the same primitive the CLI verbs use: it refuses a `LinkTarget` without following it, refuses anything not seekable / not `FILE_TYPE_DISK`, opens without ever blocking, and takes the length **off the opened handle** — so the bytes advertised, the bytes bounded, and the bytes served are the same bytes. A refusal is a 404 and never its reason, since this is the one caller whose output goes to a stranger. Serving is still opt-in (`ASHLAR_MESH_SERVE_PORT`), but the published directory is not a private staging area: `ASHLAR_MESH_AUTOSHARE` writes admitted packages into it unattended and it is often a synced folder, so "everything offered is already sealed" described what the node *put* there, not what is there. Issue #488. **The primitive moved** from a private type inside `PkgCommand` to `Ashlar.CLI/Packaging/SafePackageRead.cs` — same behaviour, same refusal wording, now findable by the next path that needs it. With this, the read ceiling claim below covers all six paths: `import`, `show`, `publish`, `pull`, the auto-pull daemon's folder pass, and mesh serve.

- **The `.ashpkg` read ceiling is enforced on every path that reads one — all six of them, mesh serve included (see above; that path was missed until #488), and it is a bounded READ rather than a size check.** Before this, only `pkg import` and `pkg show` consulted a ceiling at all — `pkg publish` and `pkg pull` read the file with `File.ReadAllText` and no bound — and the ceiling they did consult was `FileInfo.Length`, which is not a ceiling: it reports **0** for a FIFO, a Unix socket, and a character device such as `/dev/zero`, and for a **symlink** it reports the length of the target's *path string*, so `ln -s <a 400 MiB file> x.ashpkg` measured about twenty bytes and walked straight past the limit. All four verbs (`import`, `show`, `publish`, `pull`), the mesh auto-pull daemon, and the mesh SERVE endpoints now go through one primitive: a non-blocking open (so a planted FIFO cannot wedge a pull), a refusal for anything that is not a regular file, and a cap enforced on the bytes as they arrive rather than inferred from metadata. **The two bounds are different numbers, and the primitive takes whichever its caller passes.** The four `pkg` verbs pass **16 MiB**, which is `ExtensionPackaging`'s own parse ceiling; the daemon's local folder pass passes **`MeshWire.MaxPackageBytes`, 4 MiB** — the same bound it already applied to a package arriving over HTTP, so a peer cannot make a node buffer more locally than it may over the wire. Either way an oversized or endless file is a refused row in milliseconds instead of an `OutOfMemoryException` or a permanent hang. **Breaking:** a `.ashpkg` larger than 16 MiB that `publish` or `pull` previously accepted is now refused, exit 65. The daemon's 4 MiB is unchanged in value; what changed is that it is now enforced during the read and inside the per-file guard, where the `FileInfo.Length` check it replaces sat outside and took the whole unattended pass down with it.
- **A symlinked `.ashpkg` is refused outright** — fail-closed, and **breaking** for anyone who links packages into a mesh store. The link is not followed, because following it *is* the bypass. The refusal names the way out: point the path argument or `--from` at the directory the package really lives in, or copy the file into the store rather than linking it. This applies to the `.ashpkg` **file** only — a store **directory** that is itself a symlink still works.
- **One planted file can no longer deny every package behind it in a `pkg pull`.** A mesh store is a plain synced directory, so anyone who can write to the share can drop a file into it. Every per-row failure — the read, the gate submission (which sat outside the guard and still aborted the whole pass, e.g. when the receiving project's `.ashlar` cannot be created), and the row rendering — is now a `REFUSED` row that increments the counter and lets the pass continue. A refusal never becomes a silent skip: the pass still exits 65 and the summary names the count. A cancelled pass still cancels.
- **Sender-chosen text in every `.ashpkg` read refusal is escaped before it reaches the terminal.** Two parts of those lines are chosen by whoever wrote to the share and not by the operator: the **filename** — which is also what a `× REFUSED` row falls back to when the package did not parse and there is no summary to print — and the **symlink target** quoted inside the refusal, which reaches the operator even when every character they typed was honest (`pkg import store/x.ashpkg`, on a link someone planted). A bare CR, LF, or ANSI/OSC escape in either could repaint the line into a counterfeit `✓ ADMITTED … sealed by <a fingerprint the operator trusts>`, defeating the sealer fingerprint — the one part of a row a sender cannot choose. C0 and C1 controls (including U+009B, a CSI with no ESC in front of it), DEL, and bidi overrides are now replaced with U+FFFD, on every `pkg pull` refusal row and on the `import` / `show` / `publish` refusals written to stderr; a legitimate non-ASCII package name passes through byte-identical. The pre-existing sites that print a **verified** package's summary, `proposedBy`, or a course detail on the success paths are unchanged and tracked in #483; the escaping helper is shared so that issue can reuse it.

### Fixed

- **Two CI lanes that ran nothing now run on every pull request.** A previous change repaired two test filters that matched zero tests — but the *steps* invoking them were `if: github.event_name == 'workflow_dispatch' && …`, and `pull_request` is the only automatic trigger either workflow has, so the vacuous pass simply moved from the filter to the step condition. Application Tier B (the CLI suite, 207 tests, ~20s) and Security Tier C (the package-admission and untrusted-output suites, 61 tests, ~4s) now use the `github.event_name != 'workflow_dispatch' || …` form their sibling tiers already used; every step's condition was evaluated against a `pull_request` context to confirm the selection, and the dispatch tier choices still resolve as before. Security Gate's `paths:` were also widened to the CLI directories Tier C actually asserts on — a correct step condition never fires if no path change starts the workflow. **`TrustCommandTests` now has an automatic runner** as well: it is a `UnitTestBase` suite reachable only through the `UnitTestBridgeTests` theory, one row of which hangs, so a comment declared it un-runnable — but a single row can be selected by narrowing on `DisplayName`, exactly as the composition/mesh gate already does, and it terminates in 453 ms.
- **`ashlar pkg pull --from` no longer mangles a URL into a path and then reports the operator's own store as missing.** `--from` was a `DirectoryInfo`, so System.CommandLine coerced whatever was typed — `file:///srv/mesh`, `http://peer:8080/store`, `ftp://…` — into a directory name that then "does not exist", which reads as the store having vanished rather than as the wrong kind of argument. It is a string now, resolved before use: a **`file://` URL is accepted** and resolves to the directory it names (the shape a sync client or a file manager's "copy location" hands an operator); `http`/`https` is refused with a pointer to `ASHLAR_MESH_PEERS`, which is where HTTP pull actually lives; any other scheme is refused with the scheme named back; and an empty `--from` is a refusal instead of the unhandled exception `new DirectoryInfo("")` threw. A directory whose *name* merely looks like URL syntax (`store%20v1#2`) is still passed through untouched — the scheme test reads the raw token, because on Unix `Uri` parses an ordinary rooted path as an absolute `file:` URI and routing those through `LocalPath` would percent-decode and truncate real store names, losing a live store to fix a cosmetic one.
- **`ashlar doctor --format-json` now emits the JSON report** instead of parsing the global flag and dropping it. `doctor` spells its own switch `--json`; `--format-json` is the CLI-wide flag and is global, so it parsed here and was silently ignored, handing a caller who was piping stdout into a parser eight lines of prose. Both spellings now reach the report that already existed.

### Changed

- **Progress and diagnostic markers moved from stdout to stderr** (`[progress]`, `[complete]`, `[NN%]`). **Breaking for anything that scraped stdout for them.** They were written to the same stream as the `--format-json` document and bracketed it, so `--format-json --verbose` produced output no JSON reader could parse — under exit 0, a green light over a broken pipeline. stdout is now the document; stderr carries the narration, which is what `--format-json`'s own help text already promised.
- **`--format-json` is now refused, loudly, by commands that have no JSON rendering** — `verify`, `policy show`, `policy set` — instead of being accepted and silently ignored. **Breaking:** those invocations now exit 1 with an explanation on stderr where they previously exited 0 and printed prose. The shared helper locates the option by **alias**, never by name: System.CommandLine strips the leading `--` from `Option.Name`, so the `o.Name == "--format-json"` test spelled elsewhere in the tree matches nothing and reports "no JSON asked for" on every invocation. `docker`, `test portable` and `test multi-env` still carry that test and still ignore the flag — deliberately untouched here; the helper exists so they can be moved onto it.

## [0.1.2] - 2026-09-04

**Ashlar v0.1.2 — compile-authority.** A certificate names the bytes the certifier compiled and fenced. Disk certify and generate→certify mint `gate-emitted-artifact`. Production hot-swap and self-extend admission use `CertificationVerifyOptions.Strict`; hot-swap binds judged PE when the supplied image matches that input.

The 2-arg `Verify(record, source)` helper remains HMAC-era for callers that opt into it. Strict without artifact bytes still checks input kinds, not fence/identity hashes.

### Added

- **Compile-authority fences (certificate means the bytes).** The certifier no longer `dotnet build`s author projects. It compiles the candidate itself (`GateEmittedArtifactCompiler`) under closed-world `BrickCompileOptions` (C# 12, no unsafe, Release), discovers types from PE metadata, allowlist-fences the IL (`allowlist-v3`: signatures, `ldtoken`, P/Invoke, `calli`, module initializers), activates those bytes, and binds `gate-emitted-artifact` / `compile-options` / `il-import-fence` / `certifier-identity` into the signed record. Exporters write `gate-emitted-brick.dll`. `CertificationVerifyOptions.Strict` requires the artifact.
- **Adversarial corpus** at `tests/adversarial-corpus/` replayed by `AdversarialCorpusTests` (cert-gate). Living-oracle: an intentional verdict change updates `expect.json` in the same commit.
- **Certifier-boundary inventory** (`ci/certifier-boundary-inventory.tsv`) — shrink-only freeze of Load/CreateInstance sites in `Ashlar.Infrastructure.Certification*`.
- **C6 published-version lint** — docs that name a nuget.org pin key off `ci/published-version`, never `VERSION`.

### Fixed

- **Windows certification-record replace.** Concurrent `FileCertificationRecordStore.Save` calls for the same brick no longer throw `UnauthorizedAccessException` when Windows refuses `MoveFileEx` replace-existing; the store retries the staged move so a save in flight cannot fail another. The previous verdict stays on disk until a retry lands.
- **Hot-swap fallback test** sets `ASHLAR_ALLOW_MOCK=1` so kernel-coverage exercises the documented echo fallback instead of the fail-closed `ModelUnavailableException`.
- **`Ashlar.CertifyBrick` load/fence refusals** write a signed FAIL record (`LoadRefusalRecord`) instead of exiting with no file. A missing record reads as uncertified; a refuse must be evidence.
- **`CleanArtifactsTool`** no longer null-derefs when the cleanup service returns nothing (Windows readiness used a Unix snapshot path that missed the mock).
- **Mesh TLS tests** export the RSA key PEM at creation time so macOS Security.framework is not asked to re-export a PFX-loaded key. Production `LoadCertWithKey` stays on the persistent PKCS#12 path SChannel accepts (no `EphemeralKeySet`).
- **Autonomy loop start test** waits for the start log instead of a 50ms delay, so a loaded macOS runner cannot `StopAsync` before `ExecuteAsync` logs the enforced hold.
- **Background agent service tests** stop through `StopAsync` after observing `StartAllAsync`, instead of canceling the token passed to `StartAsync` (that token is linked into `ExecuteAsync` and raced a short delay on Windows).
- **Production Readiness Gate v1** CLI checks expect the unconfigured default pipeline adapter to fail closed. They no longer require fabricated `pipeline run` success.
- **Docs link check** retries once when `lychee-action` fails to download its binary (GitHub Releases SSL connect error 35), so an install flake is not reported as a broken doc link.
- **macOS NCR routing test** waits for the capability poller's VRAM snapshot instead of a 300ms delay, so a loaded runner cannot route to RunPod before local capacity is published.
- **Compose Ubuntu test image** retries `apt-get update` on Hash Sum mismatch so a mirror flake is not reported as a product failure.
- **Certification gate binds judged PE.** When `EmittedArtifact` is set, the gate inspects and activates those bytes and witnesses the resulting instance. `CertifiedBrickActivator` inspects before `Assembly.Load`.
- **`ci verify` / `release preflight` / `test-multi-env` / `runtime execute` children** run under `TimedProcess.OperatorCommandTimeout` (2h) with process-tree kill. Docker API `WaitContainer` is capped at 30 minutes.
- **Consumer verify binds judged bytes.** Hot-swap and self-extend admission use `CertificationVerifyOptions.Strict`. When hot-swap is given a PE whose hash matches `gate-emitted-artifact`, it uses the artifact-bytes `Verify` overload. HMAC-era records without that input are refused at those hosts.

### Changed

- **IL import fence is an allowlist**, not a `System.*` denylist. Round-10 attacks (reflective `Environment.Exit`, reading `ASHLAR_CERT_DEV_HMAC_KEY`, `AppDomain`/`AssemblyLoadContext`, filesystem writes) and round-11 attacks (P/Invoke with no IL body, `[ModuleInitializer]`, `typeof(System.IO.File)` / `ldtoken`, extra author `.cs` files) are corpus fixtures. Round-12 adds `Thread`/`ThreadPool` and `localloc` (stackalloc). Round-13 closes remaining fire-and-forget: `Timer`/`PeriodicTimer`, `Task.Run`/`Task.Start`/`TaskFactory.StartNew`, `async void`, and `CancellationTokenSource.CancelAfter`.
- **Hot-swap loads a supplied PE only when its SHA-256 matches `gate-emitted-artifact`.** A mismatched or HMAC-era unbound image is rematerialized from wrapped source, never loaded.
- **Generate→certify** compiles with `GateEmittedArtifactCompiler`, activates through `CertifiedBrickActivator`, and mints `gate-emitted-artifact` / `execution-mode=gate-emitted` the same way the disk loader does.
- **Stable public API promoted.** Unshipped symbols in the stable-tier `PublicAPI.*.txt` files moved to `Shipped.txt` for the v0.1.2 promise.
- **Compile-options parity.** Mutants (`RoslynCodeAnalysisService`), the analyzer fence, hot-swap rematerialize, and the in-session MSBuild project all use `BrickCompileOptions`. The autonomy harness activates the gate-emitted artifact and passes those bytes into the first swap.
- **Release Manager extracted** to [github.com/IanFrelinger/ashlar-release-manager](https://github.com/IanFrelinger/ashlar-release-manager) — the first out-of-tree consumer of the published packages (CI restores from nuget.org only; smoke-verified: all four deterministic agents register, run, and drain). Completes the graduation→extraction path LICENSING.md promised. `consumer-template/` refreshed to `0.1.1` and no longer claims the packages are unpublished.

## [0.1.1] - 2026-09-01

**Ashlar v0.1.1 — "the slimmed keep."** The first release published to nuget.org (Trusted Publishing/OIDC, with SPDX SBOMs). Between 0.1.0 and this tag the repository was cut down to Ashlar's native responsibilities, the open-core boundary gained enforceable covenants and in-force commercial terms, and the documentation was audited against source end to end (PRs #445–#448).

### Removed

- **Native-responsibility slim: the monorepo now carries the runtime, not the verticals.** A six-area audit classified every piece of the tree against Ashlar's native responsibilities (kernel + trust surfaces, runtime/SDK/hosting, interface surfaces, federation + the commercial fleet-governance tier, and distribution for the above). Removed as verticals riding along (~670 files, all preserved intact on branch `archive/verticals-2026-08-31` for extraction to their own repos): the entire `applications/` product layer (physical-atom certification, provenance graph, six spatial/AR projects), the game vertical (nine `commercial/` GameDirector/GameDomain projects, `apps/game-director`, `apps/ashlar-forge`, game data, compose files, engine-bridge and forge/portal/aesthetic docs), the Unity dev vertical inside the CLI (`ashlar unity-dev`, its nine handlers, twelve constraint records, and ~1,300 lines of tests), `_handoff/game-layer`, vertical demo tools and samples, the orphaned `ValidationUtilities` and `src/Ashlar.API` stub, and stale working docs (`docs/planning`, `docs/cleanup`, promotion residue). Coupled edits: solutions, `ci/test-ownership.tsv`, path-scoped workflows and gates, `LICENSING.md` (Tier 3 is now empty in-repo; the evaluation grant scope shrank to `commercial/`; no open code moved behind a paywall — covenant 2 untouched), `ProjectTiers.md`, `OpenCoreBoundary.md` (which also gains a tracked follow-up for the game/spatial vocabulary still compiled into core: `Core.Application/Environments/**`, `ModelBackedMaterialIntelligenceService`, `QuestPdfWorkflowExporter`, the `/api/director` naming). `apps/release-manager` and `apps/runtime-studio` stay until their planned extractions land. A stale `GameDirector.Mcp` reference in `Fleet.Host` was severed in passing.

### Changed

- **Licensing: the open-core boundary now has teeth.** `LICENSING.md` gains seven published **covenants** (verify-free-forever, one-way ratchet, architectural walls, buyer-based placement, no commercial trust exceptions, no telemetry in open packages, data freedom). The evaluation grant for commercial sources is **in force** (it was a draft marked "needs owner sign-off"), all 19 remaining `COMMERCIAL-LICENSE.md` files replace "Commercial terms TBD" with real terms (evaluation grant, commercial contact, lapsed-license degrades-to-read-only commitment), and `apps/release-manager` + `apps/runtime-studio` **graduate commercial → open** — the ratchet exercised in its trust-building direction, resolving the Tier 3 listing that contradicted the headline "single-node + inspectable = open" rule.

## [0.1.0] - 2026-08-30

**Ashlar v0.1.0 — "the refusing node."** The first tagged release: a governed, self-extending, single-node runtime, published as `ghcr.io/ianfrelinger/nexo-cli:0.1.0` (multi-arch; `deploy/node.yml` pins its digest). The baseline below was backfilled on 2026-08-16 from `git log e6682152..master` (this file's first commit, 2026-08-13, through PR #337); the release-cycle work — Phases 2–3, packaging, autonomy A0–A5, arming, and federation F1–F4 — is listed first under **### Added**. PR numbers are given where a change landed as one PR.

### Added

**The refusing node (Phases 2–3): write floor + trust root**

- **Phase 2 — the write floor.** `ForgeApplier` / `MediatedWritePath` refuse any mediated write to the project contract, the operator policy, anything under `.ashlar/`, or a build-executed file — normalized so no spelling (`./ashlar.policy.yaml`, `a/../.ashlar/x`, leaf symlinks) slips the denylist; an opt-in writable allowlist; 60-case `ForgeApplierGovernanceTests` on cert-gate (#425). One shared write floor across local self-extend, package import, and mesh adopt, closing a mesh-adopt RCE (#426, #427).
- **Phase 3 — the refusal.** A node refuses a `.ashpkg` sealed by a signer it does not trust, before anything is parked. The trust root is `selfExtend.trustedSigners` (portable, in policy) ∪ the operator's local peers keychain (`ashlar keys trust` / `untrust` / `peers`) ∪ self-trust; an empty trust set refuses everything, fail-closed (#428).

**Certified extension packages**

- `.ashpkg` sealed extension packages (Ed25519 seal + signed gate record); `ashlar pkg export` / `import` / `publish` / `pull` / `share`; `MeshStore` publish/resolve; the sealer fingerprint is printed on the pull path; opt-in auto-share of an admitted extension to a mesh folder (`ASHLAR_MESH_AUTOSHARE`) (#392, #393).

**Autonomy (A0–A5): the node extends itself, unattended**

- **A0 — honest model failure.** A missing or failing model backend now exits non-zero rather than silently echoing a canned answer and reporting success (`--allow-mock false`) (#430).
- **A1 — a reachable real model.** One env var (`ASHLAR_OLLAMA_BASE_URL`) reaches an Ollama model on both model paths; verified in containers (#431).
- **A2 — real executed-evidence courses.** An in-process Roslyn **build course** (no .NET SDK on the node) — a proposal that does not compile earns a failed course and is never admissible (#432).
- **A3 — unattended self-extension.** The daemon extender proposes on its own timer, armed and durable across a redeploy (`.docker/node-agents.json`, mode path on the state volume) (#433).
- **A4 — the safety envelope.** A transactional apply with a **post-apply canary + auto-rollback** (`RoslynPostApplyVerification`); the overnight audit `ashlar background-agent report`; and the emergency stop `ashlar background-agent disarm` (#434, #435).
- **A5 — cross-machine sharing.** A node auto-pulls trusted signed `.ashpkg` from a folder or peer and re-gates it through its own trust root (#436).

**Staged arming**

- `ashlar policy set self_extend <sealed|proposing|self-extending>` and `ashlar policy show` — the only supported post-`init` policy edit; it changes only the mode, preserves the rest, validates the result (won't arm without `gatesRequired`), and fails closed on a duplicate `mode` key or an unsupported key (#437).

**Federation (F1–F4): a hub-less peer mesh**

- **F1 + F2.** Nodes serve their signed packages over a Kestrel `/mesh/v1/hello|index|pkg` endpoint (`ASHLAR_MESH_SERVE_PORT`) and pull from peers (`ASHLAR_MESH_PEERS`) — every package re-gated through the receiver's own trust root and policy (#438).
- **F3.** Zero-config LAN discovery over multicast (`ASHLAR_MESH_DISCOVERY`, surfaced by `ashlar mesh lan`), and the `IPeerSource` strategy seam that lets discovery mechanisms swap without touching the pull or the gate (#439).
- **F4.** A Tailscale tailnet peer source for internet-wide P2P without a LAN (`ASHLAR_MESH_TAILNET`) (#440); TLS/mTLS serving for a private fleet, validated against the fleet CA and **fail-closed** on a half-specified cert config (#441).

**Node deployment, release, and lab**

- The deployable node: `deploy/node.yml` (digest-pinned, restart-durable, gate store on a durable volume), `.docker/node-entrypoint.sh` (boot-vs-CLI dispatch, first-run scaffold, clock floor), a `HEALTHCHECK` on the heartbeat, the `ashlar` host wrapper, and `scripts/node-update.sh` / `fleet-update.sh` (Phase 1, #419–#424).
- **v0.1.0 released** — `release.yml` cut the tag and published GHCR `nexo-cli:0.1.0` (multi-arch, smoke-tested) plus a draft GitHub Release with the `Ashlar.*` NuGet packages; `deploy/node.yml` re-pinned to the 0.1.0 digest (#428, #429).
- A reproducible on-machine release-readiness lab under `scripts/lab/` (#442).

<!-- Baseline (backfill through PR #337) -->


**Baseline platform (before 2026-08-13)**

- Kernel spine: `Core.Domain`/`Abstractions` contracts, `Core.Application` use cases and ports, orchestration (architect, agents, coordination), background agents (scheduler, RAG, observe loop), and infrastructure (provider factory, persistence, adaptation, execution routing).
- Trust path on the execution route: sanitization with PII/secret filters, policy gates, audit trails, and barrier identity.
- Execution targets: local-first (Ollama / ONNX / offline), opt-in cloud providers, and peer/mesh execution including RunPod.
- Entry surfaces: `Ashlar.CLI` (`ashlar`), `Ashlar.API` (HTTP + portal), and embedded hosting via `AddAshlar()` for NuGet consumers.
- Mesh/federation with gRPC transport and AWS ingress; four `apps/` host configurations.
- Distribution paths: NuGet packages, GHCR container images, and Docker Compose deployments.
- CI gate suite covering kernel build/test/coverage, compose, container images, dependency and layer boundaries, cross-platform tests, docs link checking, and release readiness.

**Contracts**

- Public API contract: `Microsoft.CodeAnalysis.PublicApiAnalyzers` on every stable-tier package (`Ashlar.Sdk`, `Ashlar.Client`, `Ashlar.Brick.Contracts`, `Ashlar.Authoring`, `Ashlar.Hosting.Bundle`) plus `Ashlar.Abstractions`; the autonomy (self-extension) surface is `[Experimental("ASHLAREXP001")]`; HTTP API versioning policy in `docs/api/versioning.md`; the v0.1.0 stability promise in `docs/SdkCompatibilityPolicy.md`.

**Trust loop / certification gate**

- Analyzer fence gate runs first in the brick gate chain; manifest-derived analyzer rules are enforced semantically at the gate; trust-loop analyzer catalog `ASHLAR0003`–`ASHLAR0009`; touch-set enforcement (`ASHLAR0013`/`ASHLAR0014`); generation-side analyzer fence validator (V3); diagnostic probes on gate failure (landed through #269).
- Certification records bind the artifact content hash into the signed payload; the summary is witnessable under a reserved key; analyzer-dead mutants count as kills; new coalesce-degrade mutation operator; mutation survivors carry the edit that produced them (#326).
- Adversarial validation campaigns for the analyzer half and the session half of the trust spec (#277, #278) and for unattended Tier-0 autonomy (#285).
- Ledger corrections: the S5 `semver-parse` survivor is an equivalent mutant — a gate soundness hole, not a weak witness (#325).

**Attested execution sessions**

- Sandboxed session runner with deadline-label reaper and TestKit fake; proposer confinement (one declaration derives tool allowlist and mounts); session attestation, certificate environment inputs, and provenance events (landed through #269).
- In-session candidate build (P3) and in-session witness/determinism/mutation execution (P5) — untrusted candidate code never runs in the harness process (#306).

**Autonomy loop (experimental, hold mode by default)**

- Objective intake (source trust, touch-sets, tier classifier), generation depth and recursion ceilings, judge growth and capability narrowing, swap-host tier gate, generation retention, no-build rollback, revocation chains, watch window with auto-rollback and quarantine, global pause / cadence floor / in-flight blocking / digest, ledger scan feeding the digest, host composition for the loop (landed through #269).
- Proposal-iteration harness (#290); LIVE model proposals at flight time with witness-blind prompts and committed recordings (#307).
- Standing loop over the objective store, hold-by-default (#312); tracked worked example under `samples/autonomy-objectives/` (#314).
- Repair channel: policy-projected, bounded repair feedback measured through the shipped path (#316); compile failures enter the repair channel; build repairs carry the whole objective; operator preamble with the brick API (#323).
- Dogfood campaigns 1 and 2 — five human-authored objectives, live in-loop proposer, then the same objectives across three proposer models (`-Models`, per-model dials) (#323, #324).
- Self-extend invariant D — cross-cycle extension ceilings; the last self-extend audit GAP closes (#321).
- `spikes/autonomy-first-flight/` — the flight runner (`-SessionBuild`, `-SessionExecute`, `-Proposed`, `-Live`, `-Sweep`, `-Models`) and its recordings; `spikes/README.md` documents every spike and which ledger rows cite it.

**Protocols**

- MCP server bridge over `ITool` with allowlists, argument overrides, policy gate, concurrency ceiling, audit, and a stdio host (#268); MCP client tool adapters over external MCP servers; A2A server core + client transport with scheme-dispatched remotes (#266); `Ashlar.API` wiring of `/api/mcp` and `/api/a2a/{agentId}` behind auth and rate limits (#269) with ProdStyle coverage and a CI gate (#270); compose stacks set both explicitly off (#289).

**Repository shape, docs, and brand**

- `applications/` layer for open products built on the core (physical-atom certification, provenance graph, spatial); `Ashlar.Kernel.sln` lists the spine only; the dependency-boundary gate forbids core → `applications/` references (#310).
- Compose stacks live under `deploy/compose/` (#262).
- Brand kit under `assets/brand/` with terminal style guide and console reference (#308); NuGet icon and README hero wired (#309).
- Community health files, `CHANGELOG.md`, README badges, and the branching policy in `CONTRIBUTING.md` (#261).
- Onboarding docs guard: backtick-quoted repo paths in README/docs/scripts/Makefile must exist; the meta-gate and hub deleted in `0bcc2718` restored and every dead reference fixed (#332).
- Authoring docs say what is not yet published (nothing on nuget.org), make `samples/hello-brick` the primary path, document the local feed, and pack the CLI on release (#333).
- `docs/TesterQuickstart.md` — one lane from clone to an audited job to the certification gate; README front door rewritten around auditable workflows / certified artifacts / your infrastructure, with a trust-loop section; `docs/DocsIndex.md` restructured; stale status docs marked historical.

### Changed

- net8.0 executables and test hosts roll forward onto the 9.x runtime (`RollForward=Major` in `Directory.Build.targets`), so an SDK-9-only machine runs the CLI/API without a separate .NET 8 runtime (#327).
- One Ollama resolution order across `ASHLAR_OLLAMA_*`, `Ashlar:Meai:*` / `Ashlar:NodeCapabilityRuntime:Ollama:*`, and legacy `OLLAMA_*` (#330).
- Compose defaults: repo mounts default to `../..` (the repo root) so `deploy/compose/` stacks see the repository; quickstart/Ollama/Neo4j publish on loopback; Neo4j password comes from the environment, not the file (#331).
- Docker images select the target framework with `-f`; `TARGETFRAMEWORK` no longer leaks into the MSBuild environment.
- Dependency bumps: `Microsoft.Extensions.*` 10.0.11 and MEAI 10.9.0 (#263), AWS Bedrock SDKs and CsCheck (#322), AWSSDK.DynamoDBv2 4.0.103.1 (#299), coverlet.collector 10.0.1 (#301), Avalonia (#295), `actions/checkout` v7 (#240), `actions/setup-dotnet` v6 (#242), `lycheeverse/lychee-action` 2.9.0 (#241).

### Fixed

- `ashlar trust` is registered on the root command again (dropped in #162) (#328).
- `Ashlar.API` defaults `RequireExplicitBarrier=false` and surfaces `BARRIER_CONTEXT_MISSING` instead of a silent `0 agent(s) executed`; exception text stays redacted outside Development in the orchestration summary (#329).
- Full Platform Readiness Gate green on `master`: skipped is not failed in `ashlar validate`, per-project target framework, templates and hidden trees skipped, cwd sink flake (#317); prebuilt CLI in the E2E smoke and the extender armed in the daemon-claim smoke (#318); NCR routing flip waited on instead of a fixed delay (#319); API image smoke probes `/health` instead of hanging 90 minutes (#320); the four remaining identifiable flakes fixed at the root, air-gapped refusal kept in the MCP/A2A gate filter (#335).
- `onboarding-quickstart-gate` YAML indentation (red since July) (#311); CLI built before Ops Tier E, mesh-lab typo, egress out of the coverage badge (#334).
- Pack graph: `Certification.Physical` dropped from the hosting pack graph and the `Hosting.Bundle` metapackage after the application split (#313); `Ashlar.Analyzers` packed as a runtime dependency of Infrastructure.
- Kernel-coverage-gate timeout hang nets sized so the gate is deterministic (#260).
- Flight runner: ASCII-only strings for PowerShell 5.1, comma-joined `-Models` token split, `\uXXXX` unescaped when extracting model proposals (#323, #324).
- Certification composition: `AddCertificationGate` takes and forwards `recordStorePath`, so the gate can be composed with a durable record store; the in-memory store and `CertificationRecordSigner` are registered with `TryAdd` so a default cannot displace an explicit choice, and an explicit path removes a default already registered. `AddCertificationInfrastructure(recordStorePath: path)` followed by `AddCertificationGate()` silently reverted the store to in-memory — fatal for the CLI, a fresh process per invocation, where nothing certified could then be admitted. Composition records remain in-memory only.

### Security

- Certification verification gains opt-in strictness (`CertificationVerifyOptions`), applied by both `CertificationTrustVerifier.Verify` and `CertificationRecordSigner.Verify`: a **minimum accepted schema version** (SPEC-006 S-5), a require-Ed25519 mode, and trusted-key pinning. Without the floor, the canonical payload lane is selected by the record's own `SchemaVersion` and the legacy lane leaves `Gate`, `GatesPassed`, `Inputs`, `Proposer`, `Attempts` and `Ed25519PublicKey` outside the signed bytes, so a stripped-and-downgraded record can claim to have passed gates it never ran under a recomputed HMAC. Every default reproduces prior behaviour, so this is a control an operator must switch on; see limitations 7 and 8 in `docs/certification-evidence.md`.

- Remote container-execution routes (`/api/execution/*`) are unmapped unless `Ashlar__Execution__ServeRemoteExecution=true` and are behind auth; the API refuses to start on `Lan`/`Tailnet`/`Public` exposure without built-in auth (escape hatch `Ashlar__Security__AllowUnauthenticatedNetworkExposure=true`); no shipped dev keys (#336).
- Session containers run with `--cap-drop ALL`, `--security-opt no-new-privileges`, `--pull never`, an image digest pin, and a read-only rootfs with declared scratch paths; applied containment is attested on the certificate (#337).
- MCP/A2A surfaces: `Enabled=false` defaults, empty allowlists, `ValidateOnStart`, hard refusal under `ASHLAR_DEPLOYMENT_PROFILE=airgapped`, all-verbs auth and per-IP rate limits on `/api/mcp` and `/api/a2a/*` (#268, #266, #269).
