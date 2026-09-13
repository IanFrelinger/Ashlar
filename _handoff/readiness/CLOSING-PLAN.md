# Closing plan — from what exists to a self-sustaining federated runtime

*Written 2026-08-28 against `master` @ `c22916b`.*

> **STATUS UPDATE (2026-08-31): the goal below is substantially achieved.** Phase 1 (the deployable
> node), Phase 2 (the write floor, #425–#427), and Phase 3 (the trust-root refusal, #428) all landed;
> **v0.1.0 released** (GHCR `nexo-cli:0.1.0`, `deploy/node.yml` re-pinned, #429). On top of it the
> **A0–A5 autonomy roadmap** (#430–#436) made the node self-extend unattended behind a build course
> and a post-apply canary, the **staged arming verb** shipped (#437), and **F1–F4 federation**
> (#438–#441) delivered exactly the "each instance collaborate and share bricks" goal — hub-less peer
> sharing of signed `.ashpkg` over LAN / tailnet / mTLS. A reproducible release-readiness lab lives in
> `scripts/lab/` (#442). Operator docs: [`docs/RunningASelfExtendingNode.md`](../../docs/RunningASelfExtendingNode.md),
> [`docs/Federation.md`](../../docs/Federation.md). The phase-by-phase text below is preserved as the
> record of how it was planned; read it as history, not as open work.

**The goal, in the owner's words:** "a self sustaining, self extending runtime with multiple
agents embedded in it, that I can host across all my hardware I have lying around my apartment,
and have each instance collaborate and share bricks etc."

**How this was produced.** Six specialist personas — systems architect, SRE, security engineer,
product realist, solo-maintainer pragmatist, distributed-systems realist — each planned this
independently and were told not to hedge. They then cross-examined each other, each filing a
formal dissent line. One integrator reconciled them and had to justify every overruled dissent.
Three red teams then attacked the result on abandonment risk, hidden sequencing dependencies,
and the physical reality of consumer hardware. This document is what survived; the
*What the red team changed* section near the end records what it cost.

It follows the audit in `RUNTIME-VISION-AUDIT` territory — read
`_handoff/readiness/STATE-2026-08-27.md` and `DECISION-identity-split.md` first for context.

> **Trust caveat, stated once and meant.** Only about ten of roughly seventy-five audit gap
> claims were adversarially verified — three confirmed, four materially corrected, and two more
> found wrong afterwards. Phases 0 through 4 rest on facts re-verified by direct code read.
> **Phase 5 onward are hypotheses**, and Phase 5 carries an explicit re-check step for exactly
> that reason. Do not treat the later phases as settled findings.

---

## The plan in a paragraph

Build one artifact whose job is to BE a node — an image that boots on the Pi, keeps its operator key and its packages across `docker rm`, actually runs an agent, and prints who sealed every package it sees. Then close the write path, prove a refusal with three keys on one box, and only then key a second machine. Everything expensive (the registry seam, receiver-side re-certification) waits behind an owner decision made with two of your own machines in front of you. Phase 0 is two days of pure fuse-removal with no compiler needed, and it comes first because a required check in this repo goes off by itself on 2026-10-27.

## Why this shape

Three forces set the order. (1) Abandonment is this repo's demonstrated failure mode — eight workflows muted rather than fixed, 109 commits in two bursts across nine active days — so the first observable on real hardware lands in week one, not week four, and it is bought with a print statement that has no dependencies. (2) Two red-team kill shots are unanswerable-as-written and both live in the earliest phase: the shipped node runs ZERO background agents (verified: `find application/src/Ashlar.CLI -iname 'appsettings*'` returns nothing and BackgroundAgentConfigLoader.LoadAsync binds only `BackgroundAgents:Agents`, so the daemon reaches `Task.Delay(Timeout.Infinite)`), and the node has no Ashlar project so `pkg pull` dies at PkgCommand.cs:402-406 with `not an ashlar project` before any trust logic runs. Both are fixed in Phase 1 or nothing downstream measures anything. (3) The persistence rule the plan invented has a blind spot exactly where the trust state lives: the GATE STORE is `new GateStore(Path.Combine(projectDir, ".ashlar"))` (PackageImport.cs:74, PkgCommand.cs:111, SelfExtendAdmissionBridge.cs:106) with no environment variable anywhere, so an ASHLAR_*-based convention test cannot see it and Phase 1's exit criterion would pass with the hole open. Phase 1 persists it by `working_dir` and the convention test names it explicitly. Beyond that: the security property is proven on ONE box with three key directories before a second machine is keyed, so the metal run only has to prove transport; every new mechanism lands as an assertion in `Ashlar.Tests.Infrastructure.Tests.Certification` or `scripts/e2e-loop.sh` because CERT_GATE_FILTER (scripts/cert-gate-config.sh:6) selects exactly three namespaces and cert-gate is the one required check; and CP7-onward are scheduled as hypotheses with an explicit re-check step, because only ~10 of ~75 audit claims were adversarially verified and two were already found wrong.

## Where to stop

End of Phase 1. With the repairs applied it is no longer 'a node that survives docker rm but does nothing to watch' — it boots on the Pi from one pinned compose file, keeps its operator key, its published packages, its cycle history AND its gate store across `docker rm`, actually runs a background agent (the un-repaired version ran zero, forever), parks with a readable reason instead of crashlooping, reports health from a status document you can read with `docker ps`, has a real `ashlar` command on the host, has an update path, and prints the sealer's fingerprint of every package it looks at. That last item is the whole point of stopping there: it is the first thing on real hardware you could not see before, and it lands in week one instead of week four. If you never do another phase you own a self-hosted, restart-durable, identity-stable node — which is more than the repo has ever had, and it is the thing every later phase is keyed to. Phase 3 is where it becomes satisfying (a box refusing a stranger's brick); Phase 4 is where it first matches the sentence you wrote. Be honest with yourself that those are weeks three and four, not week one.

---

## The fleet, corrected (2026-08-28)

**This plan was written assuming a Raspberry Pi. There is no Pi.** The hardware is a **Windows
box** and an **M-series MacBook** (plus Docker Desktop's WSL2 backend on Windows; there is no
user distro installed unless you run `wsl --install -d Ubuntu`).

Phase 0 has been corrected for this and got *cheaper*: the MacBook is itself an arm64 machine, so
the never-executed arm64 probe runs natively on hardware you already own, with no 32-bit-OS branch
to worry about.

**Phases 1 and 3–8 have not been rewritten, and several of their conclusions do not survive the
change.** They are not find-and-replace errors — they are engineering judgements that were correct
for a 4-core board with an SD card and are not obviously correct for a MacBook. Revisit at least:

| Where | What assumed a Pi |
|---|---|
| Phase 5 soak (step 2) and Phase 6 (step 7) | The soak is specified **on the Pi** because it is "the constraint", and tracks `/sys/block/mmcblk0/stat` sector-writes against SD-card death. Neither the constraint nor the failure mode is the same on a Mac. |
| Phase 7 (step 4) and owner decisions 6 and 7 | "The Pi can hold and refuse; on a 4-core board against an SD card it can never be a re-certifying receiver." An M-series Mac plausibly **can**, which reopens the receiver-side re-certification question the plan defers. |
| Owner decision 3 | Physical recovery assumes a spare imaged SD card and a UART console after swap thrash kills sshd. |
| Owner decision 12 | Per-node inference vs one box serving the LAN was framed around a Pi's RAM headroom. A MacBook's is a different order of magnitude. |
| Phase 4 (step 1) | The "honest always-on pair" was the Pi plus a laptop. With two machines and no always-on box, this needs a real answer — the Mac sleeps without `caffeinate`, the Windows box has no sshd by default. |

Do not treat those as corrected simply because Phase 0 is. `HARDWARE-BRINGUP.md` carries the
measured results from the Windows box and is accurate as of this date.

---

## Phases at a glance

| # | Phase | Wall clock | Safe to stop after? |
|---|---|---|---|
| 0 | Remove the fuses and learn whether the arm64 artifact runs at all | 2 days focused; do it in one sitting | yes |
| 1 | The node: it boots, it keeps its name, it does work, and it tells you who sealed things | 6–8 focused days; 2 weeks calendar at this repo's demonstrated burst rate | yes |
| 2 | The write floor | 3 focused days | yes |
| 3 | Refusal, proven on one box | 5–7 focused days | yes |
| 4 | Two machines, two keys, a refusal on metal | 3–4 focused days | yes |
| 5 | The substrate under it | 2–2.5 weeks, most of it waiting | yes |
| 6 | Make 'extending' true, or knowingly pick the other stack | 3–4 weeks after the owner decision | **NO** |
| 7 | What the receiver checks, and what survives a dead disk | 2–3 weeks | yes |

Legend for steps: **[S/M/L/XL]** size · **[SDK]** needs a .NET SDK (the agent environment has
none, so CI is the first compiler) · **[agent / human / CI]** who performs it.

---

## Phase 0 — Remove the fuses and learn whether the arm64 artifact runs at all

*2 days focused; do it in one sitting · safe to stop after: **yes***

> **STATUS (revised 2026-08-28): this phase is nearly closed, and it got cheaper.**
> The three agent-executable items landed with this document: the seven `UNOWNED` rows are
> re-dated to 2027-03-31 and the brick template staggered to 2027-06-30, so no row expires
> within 180 days; `reusable-container-publish.yml` now builds the immutable `sha-<12>` tag for
> `linux/amd64,linux/arm64` on master as well as on `v*` tags; and `ci/cert-gate-assertions.md`
> exists.
>
> **The fleet is a Windows box and an M-series MacBook. There is no Raspberry Pi.** That is the
> single biggest change to this phase, because the Mac is itself an arm64 machine: Docker Desktop
> on Apple Silicon runs `linux/arm64` natively, with the same `linux-arm64` RID and the same
> native libsodium a Pi would have loaded. The arm64 question is answered on hardware you already
> own, on the same day, with no 32-bit-OS branch to worry about.
>
> **Already measured on the Windows box (2026-08-28):** the amd64 path is green end to end — L0
> and L1 pass, cert-gate 194/194, `e2e-loop` 119/119, `dotnet test Ashlar.sln` ~7,336 tests and
> effectively green. And the **arm64 image was executed for the first time**, under QEMU
> emulation: `--help` exits 0, `/app/runtimes/linux-arm64/native/libsodium.so` is present, and
> `keys init` produced `ed25519:76f8c74a6c63477e`. That is strong evidence against the feared
> native-load failure, but it is emulation, not hardware — see `HARDWARE-BRINGUP.md`.
>
> **What remains is one probe on the Mac, and only you can run it.**

**Goal.** Spend two days on the things that cost nothing, need no compiler, and either defuse a scheduled outage or tell you the whole plan is built on an artifact that does not work. Nothing here changes runtime behaviour; every item strictly reduces risk.

1. **[S]** · *agent* — Re-date the seven UNOWNED rows in ci/test-ownership.tsv from 2026-10-27 to 2027-03-31, in a commit of its own, with the reason written into the note column. VERIFIED: the rows are Commercial.Tests.Fleet.Host, Commercial.Tests.GameDirector, Commercial.Tests.MeshDirector, Ashlar.Analyzers.Tests, Ashlar.Ingress.AwsSns.Tests, Ashlar.Ingress.DynamoDb.Tests, Ashlar.Tests.Contracts. TestOwnershipConventionTests.cs:82 reads `DateTime.UtcNow.Date` and rides cert-gate, one of five required status checks — so on 2026-10-27 every PR in the repo blocks with no code change and no warning. The file's own header rule ('do not extend a date in the change that trips it') is honoured because nothing is tripping yet. Ten minutes today; a dead end in two months.

2. **[S]** · *human* — On the MacBook, in this order. First `echo $DOCKER_DEFAULT_PLATFORM`: the repo's own docs tell Apple Silicon users to set it to `linux/amd64`, and if it is set the Mac silently runs the **amd64** image under emulation — no error, 3–5× slower, NSec in a configuration nobody has tested, and the arm64 question goes unanswered while looking answered. Unset it. Then `uname -m` (expect `arm64`), then `docker run --rm ghcr.io/ianfrelinger/nexo-cli:latest --help` and `... keys init` — with the variable unset, Docker selects `linux/arm64` natively and no `--platform` flag is needed. This is the first execution of the arm64 artifact **on real hardware**, and it proves NSec.Cryptography 25.4.0's per-RID native libsodium loads from a framework-dependent publish on linux-arm64 — the repo documents native-toolchain trouble on this platform (docs/prod-dry-run.md:11, docs/MeshVirtualLab.md:44). Note the container runs as uid 1654 and a fresh volume's root comes back `root:root`, so seed an app-owned subdirectory first; `HARDWARE-BRINGUP.md` §2 has the exact commands.

3. **[S]** · *human* — Record the Mac's answers, and the Windows box's, in deploy/README.md. On Windows, prefer a real WSL distro over Git Bash: Git Bash rewrites POSIX-looking paths in arguments into Windows paths before `docker.exe` sees them, so `-e ASHLAR_KEY_DIR=/data/keys` arrives as a `C:\...` string and the CLI dies on `Access to the path '/app/C:' is denied` — a failure that looks exactly like a product bug and is not one. `export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'` is the workaround if you stay in Git Bash. Docker Desktop's WSL2 backend does **not** give you a usable shell on its own; `wsl --install -d Ubuntu` does.

4. **[S]** · *agent* — Edit .github/workflows/reusable-container-publish.yml so master branch builds set SHA_PLATFORMS="linux/amd64,linux/arm64", not only on refs/tags/v*. VERIFIED at the 'Define image metadata' step: the immutable per-commit `sha-<12>` tag is amd64-ONLY on every master build, and the only multi-arch digest today is `:latest`, which is mutable, republished on every matching master push, and whose prior manifest becomes untagged and GHCR-GC-eligible. Without this edit there is NO digest that is both multi-arch and permanent: pin the sha digest and the arm64 node fails 'no matching manifest for linux/arm64'; pin the :latest index digest and it 404s after a few merges, surfacing at exactly the reboot Phase 1 exists to survive. OBSERVED 2026-08-28: `:latest` moved twice inside a single working session — `sha256:baaa9b5d…` to `sha256:e851f223…` — as #406 and #407 merged. The mutability is not theoretical.

5. **[S]** · *agent* — Write ci/cert-gate-assertions.md: one line per merge-blocking convention test cert-gate carries and why it exists, updated as each lands. When you come back after three weeks to a red required check with a branch-protection toggle one click away, this is the only artifact that tells you what you would be switching off. Given eight already-muted workflows in this repo, it is the highest-leverage paragraph in the plan.

**Exit criterion (an observable event, not a status).** `awk -F'\t' '$2=="UNOWNED"' ci/test-ownership.tsv` shows no row expiring within 180 days; a master build has published a `sha-<12>` tag whose manifest list contains linux/arm64; and on the MacBook, with `DOCKER_DEFAULT_PLATFORM` unset, `docker run --rm ghcr.io/ianfrelinger/nexo-cli@sha256:<that digest> keys init` prints a fingerprint instead of a native-load failure. (Under QEMU on the Windows box this already passes — see the STATUS note above — but the criterion is real arm64 hardware.)

> **Read this before scoring the awk criterion above (added 2026-09-13).** That command now returns a
> single row - the `__BrickName__` scaffolding template, expiring 2027-06-30. **This is not evidence of
> readiness progress.** Rows 47-49 left `UNOWNED` because their notes were found to be false, not
> because coverage was added: native readiness's `validate` sweep had been running all three for weeks
> (runs 34687917934 and 34737292726). No CI behaviour changed. Note also that
> `TestOwnershipConventionTests` never reads the `expires` column of a non-`UNOWNED` row, so flipping a
> row removes its dated pressure entirely and replaces it with nothing machine-enforced. The live debt
> those three rows now carry is routing: none appears in either readiness path list, so a PR touching
> only them still runs none of their tests.

---

## Phase 1 — The node: it boots, it keeps its name, it does work, and it tells you who sealed things

*6–8 focused days; 2 weeks calendar at this repo's demonstrated burst rate · safe to stop after: **yes***

**Goal.** Produce the first artifact in this repo whose job is to BE a node. It must survive recreation with its operator key, its mesh store, its cycle history AND its gate store; it must actually run an agent rather than sleeping forever; it must park rather than crashloop; and on day one it must print something on real hardware that you could not see before.

1. **[S]** · *agent* — Add to the ENV block at .docker/Dockerfile.cli:39-42: ASHLAR_KEY_DIR=/data/state/keys, ASHLAR_MESH_DIR=/data/state/mesh, ASHLAR_CYCLE_EVENTS_PATH=/data/state/runtime-studio/cycles.jsonl. VERIFIED absent — OperatorKey.ResolveKeyDir() falls back to ~/.ashlar/keys and MeshStore.Resolve to ~/.ashlar/mesh/published, both inside the container HOME, and CycleEventStore's parameterless ctor writes under CWD /app/.ashlar. Today `docker rm` destroys the node's identity and every package it ever published. Add a comment stating that MeshStore.Resolve APPENDS '/published', so the directory to hand a sync tool is $ASHLAR_MESH_DIR/published — a one-level-off here produces an empty peer store with no error.

2. **[M]** · *agent* — Ship a default agent set INTO the image. Write .docker/node-agents.json starting from docs/background-agents/examples/minimal-agent.json with one interval-scheduled extender agent, add `COPY .docker/node-agents.json /app/config/agents.json` to Dockerfile.cli (/app/config already exists and is chowned at line 43), and pass `--config /app/config/agents.json`. THIS IS THE HIGHEST-VALUE ITEM IN THE PLAN: without it the node runs zero agents, so the Phase 5 soak charts a flat line by construction and its clock exit criterion has nothing to fire against — GateStore.ProposeAsync (the only budget consumer, line 161) is reached from exactly one non-CLI caller, SelfExtendAdmissionBridge.cs:107, only when an extender agent runs.

3. **[M]** · *agent* — Replace `ENTRYPOINT ["dotnet", "/app/Ashlar.CLI.dll"]` with .docker/node-entrypoint.sh doing three things before exec: (a) a bounded ~120s wait for a plausible clock — `now > IMAGE_BUILD_DATE` stamped as a build ARG — because a Pi 4 has no RTC, fake-hwclock restores the last shutdown time, and dockerd starts before chrony syncs over Wi-Fi; (b) first-run `ashlar init node --path /data/state/project` when ashlar.yaml is absent (InitCommand.cs:49-58 refuses to overwrite, so this is idempotent and safe); (c) `exec dotnet /app/Ashlar.CLI.dll background-agent daemon --config /app/config/agents.json --disable-observation`. The `--disable-observation` flag stays until Phase 5's bounded writer lands — it removes the highest-rate SD-card writer and the inotify load from every unattended node during the longest exposed stretch of this plan.

4. **[S]** · *agent* — Persist the GATE STORE, which no ASHLAR_* variable can reach. Set `working_dir: /data/state/project` in node.yml so `Path.Combine(projectDir, ".ashlar")/gates` lands on the named volume. VERIFIED there is no environment variable for it anywhere: GateStore's ctor takes a stateRoot (src/Ashlar.Manifest/Admission/GateStore.cs:59,65) and all three call sites pass a projectDir. That directory holds every durable trust decision, the HELD queue and the whole admission history — on the shipped node today it lands in the container layer (WORKDIR /app) and dies on `docker rm`, while Phase 1's exit criterion as originally written would have passed anyway because it only checked keys and mesh.

5. **[M]** · **[SDK]** · *agent* — [SDK] Move the park-don't-exit rule FORWARD from Phase 5 to here, because Phase 1 is where its trigger ships. BackgroundAgentDaemonCommand.RunAsync's config-not-found branch and its outer `catch (Exception ex) => WriteError(...)` return non-zero into `Environment.Exit(exitCode)` at Program.BackgroundAgentCommands.cs:717-723; under `restart: unless-stopped` that is an unthrottled crashloop. Instead: write the reason into {state}/heartbeat, sleep on a backoff, re-evaluate, never exit. Add a startup writability probe on ASHLAR_STATE_DIR that parks with the exact `chown` command in the message — the obvious Pi move is a bind mount to an external SSD, which is root-owned and breaks a container running as USER $APP_UID.

6. **[M]** · **[SDK]** · *agent* — [SDK] Heartbeat as a STATUS DOCUMENT on a fixed >=60s timer, rewritten in place (never appended, never per-cycle — ScheduleExecutor.RunContinuousAsync ticks every 1s, so 'each cycle' is ~86,000 small SD writes/day added before anything bounds the seven existing appenders). Fields: {status: running|parked, reason, lastAdmissionAt, cyclesSinceStart, nodeId, keyFingerprint, trustSetDigest, clockSynced}. Add the first HEALTHCHECK to Dockerfile.cli reporting unhealthy when status=parked, when lastAdmissionAt is stale, or when the file's mtime is stale. This single change makes both the crashloop and the silent-park visible from `docker ps` after three weeks away.

7. **[M]** · *agent* — Write deploy/node.yml — at deploy/, NOT deploy/compose/, which already holds sixteen files including the muted mesh-lab ones. Add one line to deploy/compose/README.md and deploy/README.md naming it as THE node. Contents: image pinned by digest (@sha256:) to the Phase-0 multi-arch sha tag, no `build:` key, no repo bind-mount, named volume ashlar-state:/data/state, `working_dir: /data/state/project`, `restart: unless-stopped`, `logging: {driver: json-file, options: {max-size: 10m, max-file: 3}}` (verified zero log-rotation config exists anywhere in deploy/, .docker/ or scripts/install/), `mem_limit` and `cpus` (no compose file sets either today, and on a Pi the alternative to a clean OOM is swap thrash on the SD card that kills sshd before the container and leaves a physical power pull as the only remedy), a commented ready-to-uncomment bind of the host's shared folder onto /data/state/mesh so Phase 4 uncomments a line instead of fighting the convention test, and a comment stating plainly that `restart: unless-stopped` does NOT act on HEALTHCHECK — Docker restarts on exit only.

8. **[M]** · *agent* — Do NOT edit run_optional_daemon_smoke() in scripts/install/container-bootstrap-linux.sh:225. Verified it returns early without DAEMON_DURATION, always passes --duration, and `docker run --rm --restart` is rejected by Docker outright. It is a smoke test wearing an installer's name. Instead, point all four installers' final instruction at deploy/node.yml, and have them install an `ashlar` HOST WRAPPER: `docker exec` into the running node, falling back to `docker run --rm -v ashlar-state:/data/state <image>`. Without it there is no `ashlar` command on any host — the installers today only print `docker run --rm` examples with no state volume — and the owner's natural fallback of a host build resolves OperatorKey.ResolveKeyDir() to ~/.ashlar/keys, giving every box TWO operator identities and making Phase 4 fail in the most confusing way available.

9. **[S]** · *agent* — Write scripts/node-update.sh (resolve the current published digest, rewrite deploy/node.yml, print the diff for review) and scripts/fleet-update.sh (ssh each target from deploy/fleet.env, `docker compose pull && up -d`, print each node's RESOLVED image digest and architecture — which also catches a silently-emulated amd64 node on Apple Silicon). There is no delivery mechanism anywhere in this plan otherwise, and a digest pin without an update path means the Pi runs code that predates every later phase.

10. **[S]** · **[SDK]** · *agent* — [SDK] THE WEEK-ONE OBSERVABLE, moved here from Phase 3: print Fingerprint(pkg.SealSigner) on EVERY branch of PkgCommand's pull loop — ADMITTED, HELD, already-have, REJECTED, REFUSED. It has no dependency on trustedSigners, PolicyLoader or the keys verbs. Today the loop prints only `result.Package.Record.Proposal.Summary`, which is attacker-chosen text, next to a gold checkmark. Correction to the audit: Fingerprint IS already called — PackageImport.cs:99 sets `Reason = $"package sealed by {Fingerprint(pkg.SealSigner)}"` on the parked proposal — the gap is purely that PkgCommand never prints it. Also tighten the enumeration at PkgCommand.cs:413 to skip names beginning '._' or '.' and files whose mtime is within a few seconds, so macOS AppleDouble sidecars and in-flight scp copies do not bury the signal in `× REJECTED` debris.

11. **[S]** · **[SDK]** · *agent* — [SDK] CP2, pulled forward from Phase 2 (minutes of work, and CertificationStoreCompositionTests already exists to catch it): in AdaptationServiceCollectionExtensions.cs replace the `: null` fallback for certificationRecordPath with RepoPathResolver.ResolveStateDirectory(), mirroring the same method's own fallback ~70 lines below where stateBasePath does exactly that. RE-VERIFIED still broken at master post-#400.

12. **[S]** · **[SDK]** · *agent* — [SDK] Also pulled forward from Phase 2, because it protects every convention test the rest of the plan adds: Tests/Certification/CertGateFilterCoverageTests.cs reflecting over the test assembly and failing if any *ConventionTests type sits in a namespace CERT_GATE_FILTER does not select; then move TimeoutConventionTests from ...Tests.Testing into ...Tests.Certification. VERIFIED the filter (scripts/cert-gate-config.sh:6) selects exactly Tests.Certification, Tests.Adaptation.GenerationSafety and AstMutationEngineTests, and nothing in any workflow references Tests.Testing.

13. **[M]** · **[SDK]** · *agent* — [SDK] Tests/Certification/NodeUnitConventionTests.cs asserting against deploy/node.yml: (a) a restart policy present and not `no`; (b) a named volume mounted at exactly the ASHLAR_STATE_DIR value; (c) no `build:` key; (d) log rotation options present; (e) the image reference contains '@sha256:'; (f) every ASHLAR_*_PATH/_ROOT/_DIR the production code reads resolves under the state dir; (g) an EXPLICIT clause naming the gate store — working_dir resolves under the state dir — because clause (f) is structurally blind to it; (h) exactly ONE compose file in the repo declares a named volume at the ASHLAR_STATE_DIR value, so the test cannot silently pass on a missing or renamed file. It must NOT assert which image or which process the node is: that is open owner decision 1, and a required check is the most expensive place to be wrong.

**Exit criterion (an observable event, not a status).** On the Raspberry Pi: `docker compose -f deploy/node.yml up -d`; `ashlar keys show` prints fingerprint F; `ashlar pkg publish` puts a package in the mesh store; `docker inspect --format '{{.State.Health.Status}}'` prints healthy and the heartbeat shows status=running with a non-zero cyclesSinceStart (proving the agent set loaded — today this number cannot be non-zero). Then `docker compose down && docker compose up -d`: SAME F, the package still listed, and a gate record written before the restart is still listable after it. Finally, `ashlar pkg pull --from <folder holding one package sealed elsewhere>` prints a line naming that sealer's fingerprint. Today that sequence returns a different fingerprint, an empty store, no gate record, a zero cycle count, and an attacker-chosen summary string.

---

## Phase 2 — The write floor

*3 focused days · safe to stop after: **yes***

**Goal.** Close the write path that bypasses every control the rest of the plan proposes, before anything widens what a node may admit. This is the one phase with no user-visible observable, so it is deliberately three days, not a week and a half, and its test ships in the same pull request as its fix.

1. **[M]** · **[SDK]** · *agent* — [SDK] ForgeApplier normalization. VERIFIED in src/Ashlar.BackgroundAgents.HostRunners/ForgeApplier.cs: containment is checked on `Path.GetFullPath(Path.Combine(rootFull, target))` but IsGovernancePath is called on the RAW relative string, so './ashlar.policy.yaml' splits to ['.','ashlar.policy.yaml'], length 2, falls through the `segments.Length == 1` branch, returns false — and resolves inside the root. Normalize to a repo-relative form ONCE, check governance on the normalized form, and reject any '..' or '.' segment at proposal time in ForgeProposeChangeTool. ExtensionPackaging.IsSafeRelativePath already does this correctly and is the model to copy.

2. **[M]** · **[SDK]** · *agent* — [SDK] Expand the governance floor in the SAME change: Directory.Build.props/.targets, Directory.Packages.props, *.csproj, *.sln, nuget.config, global.json, .git/, .github/, .vscode/, .devcontainer/, Makefile, scripts/. VERIFIED the current denylist is exactly three things (.ashlar/, ashlar.yaml, ashlar.policy.yaml), so an admitted package writing Directory.Build.targets with an <Exec> runs on the receiver's next `dotnet build` — outside the loader, the gate and the registry entirely.

3. **[M]** · **[SDK]** · *agent* — [SDK] Wire policy.Sandbox.Writable as an ALLOWLIST but behind a policy opt-in whose default is the expanded floor. VERIFIED that property has one reader repo-wide (ProjectVerifier.cs:184) and is never read by the choke point; making it mandatory today would change behaviour for every existing project at once.

4. **[M]** · **[SDK]** · *agent* — [SDK] Table-driven Tests/Certification/ForgeApplierGovernanceTests.cs covering the normalization-equivalent spellings ('./x', 'a/../.ashlar/y', backslashes, case) and each floor entry — SHIPPED IN THE SAME PULL REQUEST as the two steps above, not as a trailing step. VERIFIED there is currently ZERO test coverage on ForgeApplier anywhere in src/Ashlar.Tests.Infrastructure, so a half-landed floor is invisible: it reads as protected and is not, and nothing tells you which half landed.

**Exit criterion (an observable event, not a status).** cert-gate goes green on a PR where a forge proposal targeting './ashlar.policy.yaml' and one targeting 'Directory.Build.targets' are both refused by the new table-driven test, and cert-gate's discovered test count is higher than before Phase 1 (the timeout tests now run inside the filter).

---

## Phase 3 — Refusal, proven on one box

*5–7 focused days · safe to stop after: **yes***

**Goal.** Make a node refuse a package sealed by a key it does not know — proven with three key directories on ONE machine, before any second machine holds a key. This is the security property the fleet demo exists to show; proving it here means the metal run only has to prove transport.

1. **[M]** · **[SDK]** · *agent* — [SDK] Add TrustedSigners to PolicySelfExtend (src/Ashlar.Manifest/AshlarPolicy.cs) and parse it in PolicyLoader. DO NOT add a load-time rejection for empty trustedSigners — dropped on the red team's finding and I agree with it. The real control is the import comparison two steps below, which refuses before anything is parked; the load-time version buys no additional security and breaks ~15 e2e scenarios via set_proposing() plus the inline grep at portability-gate.yml:66-70 on a non-required 3-OS gate that can sit red through an entire absence. It also required `ashlar init` to emit a fingerprint, which it cannot: InitCommand has zero references to OperatorKey, refuses to overwrite an existing project (InitCommand.cs:49-58), and scaffolds `self-extend: sealed` — a mode the rejection would not even apply to (PolicyLoader only fires for proposing/self-extending).

2. **[S]** · **[SDK]** · *agent* — [SDK] Ship `ashlar policy migrate --add-self` instead — a verb that adds trustedSigners to an EXISTING policy — and in the same PR add a trustedSigners clause to scripts/e2e-loop.sh's set_proposing() helper at line 54. That is the actual migration path for the actual population that has already flipped to proposing mode.

3. **[M]** · **[SDK]** · *agent* — [SDK] KeysCommand gains `trust <fingerprint>`, `untrust <fingerprint>` and `peers`, writing to a NEW <ASHLAR_KEY_DIR>/peers/<fingerprint>.pub. untrust ships in the SAME PR as trust — an allowlist you cannot shrink turns a key theft into permanent authorization. `keys trust --from-package <file>` prints the fingerprint in chunked groups and REFUSES, telling you to run `keys trust <fp>` with the value read off the ORIGIN box; sourcing the pin from the artifact being authorized is consent-by-fatigue, not a control. `keys peers` also prints a one-line TRUST-SET DIGEST (sha of the sorted trusted fingerprints) — a box that was off for a week is the one that still trusts a revoked key, and nothing else in this plan makes that divergence visible.

4. **[S]** · **[SDK]** · *agent* — [SDK] One comparison in application/src/Ashlar.CLI/Commands/PackageImport.cs, inserted after the `PolicyLoader.TryLoad` block and BEFORE `var forge = AshlarProjectMediation.ProjectStore(projectDir)` — VERIFIED as the ordering (TryOpen, policy file check, policy load, forge open, gate store, park loop). Placing it there means an untrusted package never parks anything, so a stranger writing into a shared folder cannot fill a Pi's SD card by spamming pulls. State explicitly that it returns PackageAdmission.Refused, which lands in PkgCommand's `default:` branch printing `× REFUSED  {summary} · {message}` — the fingerprint goes in result.Message, not a dash suffix. Two later phases assert on that exact string.

5. **[M]** · **[SDK]** · *agent* — [SDK] Retire the trap: OperatorKey's --rotate path writes the superseded public key to keys/superseded/ instead of keys/trusted/ (verified at OperatorKey.cs:45-51, where rotation copies the PREVIOUS public key into trusted/); update the assertion at scripts/e2e-loop.sh:279-280; add a Tests/Certification test asserting no production path reads keys/trusted/ or keys/superseded/ as an authorization input. Wiring an allowlist onto trusted/ would make rotating after a theft permanently re-authorize the stolen key.

6. **[M]** · *agent* — Split the actor in the harness. VERIFIED scripts/e2e-loop.sh exports ONE ASHLAR_KEY_DIR=$WORK/pkgkeys for the whole pkg block and references it at lines 351, 410, 418, 459 and 465 plus the trusted/ assertion at 279 — five downstream references, not one export — so origin and receiver have always been the same actor and the refusal path has never executed. Replace with keys-a (origin), keys-b (receiver, trusts A), keys-x (stranger). Add scenario `pkg-import-refuses-untrusted-signer`, written RED FIRST. Assert on the printed REFUSED line, its fingerprint, and a durable record in B's gate store — NOT on the exit code: VERIFIED PullAsync returns `admitted + held + skipped > 0 ? 0 : 65`, so a mixed batch exits 0.

7. **[S]** · **[SDK]** · *agent* — [SDK] Mirror the refusal as a deterministic assertion in Tests/Certification so it rides cert-gate, not only shell-lint. Register it in ci/cert-gate-assertions.md from Phase 0.

8. **[M]** · **[SDK]** · *agent* — [SDK] Add a bounded retry-with-jitter around LiteDB open, or set Connection=shared on the daemon's stores. VERIFIED every store builds `Filename={path}` (LiteDbAdaptationLog.cs:21, LiteDbPatternStore.cs:22 and siblings) with no connection sharing and no retry anywhere, so LiteDB 5 opens Direct with an exclusive file lock. `ashlar gates --admit` through the host wrapper is a second process against the same /data/state and will collide intermittently with the daemon — widest on the Pi, where every open/close is slowest, and intermittent failure in front of you during the demo is worse than deterministic failure.

**Exit criterion (an observable event, not a status).** `bash scripts/e2e-loop.sh` on ONE machine prints PASS for pkg-import-refuses-untrusted-signer: B refuses X's package naming X's fingerprint on the `× REFUSED` line, B's forge queue is empty afterwards, B accepts A's, and deleting the one comparison in PackageImport turns that line RED. The one-machine freeze lifts on this event, not on a date.

---

## Phase 4 — Two machines, two keys, a refusal on metal

*3–4 focused days · safe to stop after: **yes***

**Goal.** Put the already-proven property on your own hardware over your own LAN, in a form you can re-run and re-watch — and leave behind the one document that survives both a three-week absence and a dead SD card.

1. **[S]** · *human* — Decide the always-on pair FIRST and write it into deploy/fleet.env with explicit hostnames or IPs, never .local names. The gaming box dual-boots to Windows (no sshd, and no node at all while gaming), the Mac needs Remote Login enabled manually and will sleep mid-run without `caffeinate`, .local resolution is the same mDNS that AP client isolation blocks, and DHCP moves IPs across reboots. The honest pair is the Pi plus the old laptop on AC with lid-close suspend disabled, or the Pi plus the Mac under caffeinate.

2. **[S]** · *human* — Re-pin first: run scripts/node-update.sh and `docker compose -f deploy/node.yml pull && up -d` on both boxes so they are running an image that CONTAINS Phase 3's code. Without this step the Pi runs the Phase-0 digest and the exit criterion below is literally unreachable — the binary predates the code that prints REFUSED, and the symptom reads as 'the trust root does not work'.

3. **[S]** · *human* — `ashlar keys init` on box two through the host wrapper, so ASHLAR_KEY_DIR lands on the Phase-1 volume. Per-box keys, never a copied key: a copied key makes one stolen laptop the whole apartment, makes revocation impossible, and makes the refusal untestable by construction. Then seed trust out-of-band — `ashlar keys show` on box one, read the fingerprint across, `ashlar keys trust <fp>` on box two. The ceremony is the point; it is what the --from-package refusal exists to force.

4. **[S]** · *human* — Write deploy/fleet-roster.md and check it in: one row per box with hostname, node id, key fingerprint, date trusted, and who trusts whom, plus the demo recording's re-cut date in the header. This is the only artifact that survives both an absence and a dead disk — trust otherwise exists only in <ASHLAR_KEY_DIR>/peers/ on the machines it describes, in a directory Phase 7's backup excludes, so 'is the Pi trusted by the Mac, or the other way, or both?' becomes unanswerable after a reimage. Put the re-cut date HERE, not on cert-gate: this plan defuses one wall-clock bomb on the required check in Phase 0 and must not plant another.

5. **[M]** · *agent* — Write scripts/fleet-demo.sh reading deploy/fleet.env: run node.yml on each box; one shared folder as the mesh (Syncthing/SMB/scp — MeshStore is deliberately transport-naive and Publish() is a file write) bind-mounted at /data/state/mesh so the Phase-1 convention rule holds and no env override is needed. BEFORE pulling, ASSERT THE BYTES ARRIVED: compare file count and a checksum of $ASHLAR_MESH_DIR/published on both boxes over ssh, so a stalled sync reports as a transport failure instead of masquerading as `the peer store is empty — nothing to pull`, which PkgCommand.cs:414-418 prints with exit 0 whether the peer is off, unsynced, or genuinely empty. Then: A `pkg share`; B `pkg pull` → HELD → `gates --admit`; stranger key → REFUSED with fingerprint → `keys trust` → HELD. Print each node's health status, heartbeat age and trust-set digest. RESULT lines throughout.

6. **[M]** · **[SDK]** · *agent* — [SDK] `ashlar doctor clock`: read the kernel's adjtimex/ntp_adjtime STA_UNSYNC flag and compare skew against a named peer. DROP the timedatectl / systemsetup branch: the node is a container FROM mcr.microsoft.com/dotnet/aspnet with no systemd, no timedatectl binary and no /run/systemd mount, and `systemsetup` is macOS-host-only — as originally specified the diagnostic would print 'unknown' on every node, on the one axis already known to be dangerous. The clock itself IS shared with the host (no time namespace), so adjtimex works. fleet-demo.sh warns above 60s skew. Add chrony or systemd-timesyncd to the Linux installer dependency list, and `dphys-swapfile swapoff` to the Pi path.

7. **[S]** · *human* — Record the run with asciinema, check it in, link it from README, and add its dated re-cut row to deploy/fleet-roster.md's header so it must be re-cut or deleted rather than silently certifying renamed verbs forever (docs-link-check.yml will happily keep validating the link).

**Exit criterion (an observable event, not a status).** On box two over ssh, `ashlar pkg pull --from /data/state/mesh/published` prints `× REFUSED  <summary> · sealed by ed25519:ab12…` for the stranger's package and `! HELD` for box one's; `ashlar keys trust <fp>` then makes the same stranger package HELD on a re-run; scripts/fleet-demo.sh reproduces the whole sequence in one invocation, having first asserted the shared folder's checksums match on both boxes; deploy/fleet-roster.md matches live `keys show` output on both machines; and node.yml's digest has been re-pinned to an image containing Phase 3's code.

---

## Phase 5 — The substrate under it

*2–2.5 weeks, most of it waiting · safe to stop after: **yes***

**Goal.** Measure the running system at N=2 before trusting anything about it, then bound the three things that will actually kill an unattended Pi: the budget clock, unbounded writers, and fail-closed checks that crashloop. Also the point where CP7-onward stop being audit claims and start being measurements.

1. **[M]** · **[SDK]** · *agent* — [SDK] Land the budget-refill test FIRST as a DETERMINISTIC unit test in Tests/Certification, before touching any container clock. VERIFIED the clock is already an injected parameter: GateStore.ProposeAsync(policy, proposal, DateTimeOffset now, ct) at line 161 and AdmittedInWindowAsync(window, now, ct) at line 304 both take `now`; only the three call sites hardcode DateTimeOffset.UtcNow. Lines 306-308 compute `cutoff = now - window` and count `DecidedAt >= cutoff`, so a FORWARD jump pushes every prior admission out of the window and refills the self-extension budget to full. (A backward jump counts MORE prior admissions and is conservative — the audit's threat model had this inverted and its remedy guarded the harmless direction.) This makes the phase's headline property provable today, independent of both the soak and the libfaketime spike.

2. **[L]** · *human* — Run the baseline soak UNFIXED, first, as an instrument, ON THE PI — naming the machine matters; run it on the gaming box and every number is taken on hardware that is not the constraint. scripts/soak/node-soak.sh: two containers, two ASHLAR_NODE_IDs, two ASHLAR_KEY_DIRs, one shared bind-mounted mesh dir, 8 hours, CSV every 60s. Columns: RSS, CPU, RestartCount, health status, du of the state dir, wc -l of every *.jsonl, cycle count, AND cumulative sectors-written from /sys/block/mmcblk0/stat — `du` measures size at rest, which plateaus by design and is uncorrelated with SD card death; cards die from cumulative writes. Scripted SIGKILL and a host reboot mid-run. WRITE IT AS THE GATE'S SCRIPT from run one so going nightly is a scheduler change. Header caveat: the runtime performs few or no admissions yet, so no CI ceiling may be derived from these numbers. If the Pi cannot host two containers, that IS the finding.

3. **[S]** · *human* — Optional 30-minute spike, no longer load-bearing: check whether LD_PRELOAD libfaketime with per-container FAKETIME intercepts .NET's clock_gettime — .NET resolves it through the vDSO, which LD_PRELOAD does not reliably intercept, so this may simply not work. Do NOT use `date -s` or --cap-add SYS_TIME: the time namespace is not virtualised by default, so those mutate the HOST clock and corrupt the soak they are part of. The deterministic unit test above is the real proof; this is end-to-end confirmation only.

4. **[M]** · **[SDK]** · *agent* — [SDK] The budget clock fix, with a RECOVERY path. Persist {state}/budget-cursor.json holding a monotonically non-decreasing high-water DecidedAt; fail closed to Held when now moves backward past the cursor, and when now jumps forward by more than the window. Critically: recover on 'the clock became synchronized' — adjtimex STA_UNSYNC clearing, or a plausibility floor such as now > the image build date — NOT only on a timer backoff. Without that recovery the guard fires on EVERY Pi boot (no RTC, fake-hwclock restores last shutdown time, dockerd starts before chrony syncs), and a box that has been off for a week sees a week-long forward jump and parks indefinitely. That is the plan's own safety fix firing correctly against hardware whose normal behaviour it failed to model.

5. **[L]** · **[SDK]** · *agent* — [SDK] Bounded writers AT THE WRITE PATH, not a janitor a wedged process no longer runs: one BoundedJsonlWriter with a size cap, N-file roll and a total-bytes budget, routing all seven appenders — CycleEventStore.cs:72, JsonlObservationStore.cs:61, PlannerScratchpad.cs:97, WorkflowLabHistoryStore.cs:89, AdaptiveRuntimeExecutionHistoryStore.cs:58, DocsUpdateTool.cs:36, RepoGitCommitTool.cs:35. Rotation triggers are SIZE-based, never wall-clock age, because a Pi's clock is wrong at exactly the moment rotation first runs. Replace the two bare `catch { }` swallows with a failure counter the heartbeat can read. Only after this lands, remove `--disable-observation` from deploy/node.yml and re-run the soak.

6. **[S]** · **[SDK]** · *agent* — [SDK] Tests/Certification/AppendOnlyWriterConventionTests.cs failing on any File.AppendAllText / AppendAllLines / AppendText outside the rotating-writer allowlist, so an eighth writer cannot land. Register it in ci/cert-gate-assertions.md.

7. **[M]** · **[SDK]** · *agent* — [SDK] Refuse to run the GATE STORE on a recognized network or sync filesystem (statfs type, .stfolder/.dropbox/iCloud markers) — as a PARKED DEGRADED STATE, not an exit. GateStore's FileShare.None lock is a no-op across machines and append-once degrades to last-writer-wins; MeshStore's own docstring invites you to sync directories, so this guard is what stops that pattern being generalised to the one store where it corrupts. Confirm the Phase-1 park rule now covers every fail-closed check in the codebase: a safety precondition failure parks, keeps the process alive with a readable reason on the heartbeat, and re-evaluates on a backoff. It never calls Environment.Exit — under `restart: unless-stopped` a startup refusal is a crashloop that fills the card, and the repo documents that exact outcome against itself at deploy/compose/docker-compose.agent-server.yml:66-68.

8. **[M]** · *human* — Re-run the soak after these land, compare the curves, and RE-CHECK THE CP7+ HYPOTHESES against what the run actually showed. Only ~10 of ~75 audit gap claims were adversarially verified and two were already found wrong. If instances.json clobbering never fires in 8 hours on real hardware, write that down and drop it down the list rather than defending it. Same for wire-version skew and the RAM floor: the soak either produces a number or the claim goes back to being a guess.

**Exit criterion (an observable event, not a status).** The deterministic unit test proves a forward `now` jump no longer refills the budget, and a proposal that should exceed it is HELD with a reason naming the clock. An 8-hour N=2 run ON THE PI, with observation re-enabled, ends with both containers alive, RestartCount ≤ 1, state-dir bytes AND cumulative sectors-written both plateaued rather than climbing, the certification record written before the scripted SIGKILL readable after it, the node id unchanged, and the heartbeat showing status=running throughout. A simulated reboot with a wrong clock parks the node and then UNPARKS it once the clock syncs.

---

## Phase 6 — Make 'extending' true, or knowingly pick the other stack

*3–4 weeks after the owner decision · safe to stop after: **NO — see the goal***

**Goal.** Resolve whether a running node can absorb new behaviour without a rebuild, and if so build the seam once, correctly, with a lease. NOT SAFE TO STOP MID-PHASE and it must still exist: the façade replaces two competing IBrickRegistry bindings, so a half-landed swap leaves the system with an admission target that some resolvers see and others do not — strictly worse than today's honest disconnection. The RED invariant test at step 1 is the safe stopping point; after that, finish or revert.

1. **[M]** · **[SDK]** · *agent* — [SDK] Land the invariant test RED, FIRST, in Tests/Certification/RegistryResolutionInvariantTests.cs (NOT Tests.Execution, which runs on no gate), registered in ci/test-ownership.tsv and ci/cert-gate-assertions.md, marked as a known-red row in a non-blocking tier until the seam lands. Four assertions against a real CLI composition: exactly one IBrickRegistry service descriptor after full composition; the resolved IBrickRegistry is reference-equal to the object ICertifiedBrickAdmission writes into; a brick admitted after provider build is returned by a BehaviorExecutor constructed before the admission; and no remote source may satisfy an id a local or certified source also offers. VERIFIED the divergence: AdaptationServiceCollectionExtensions binds IBrickRegistry→BrickRegistry while CertifiedBrickRegistry is a separate singleton, and AddCertificationGate — the only binding that would join them — has zero production callers. This test alone is worth landing even if the rest never does: it is the cheapest possible written proof of the gap.

2. **[S]** · *human* — STOP and get the admission control channel decision answered. VERIFIED constraint: the daemon is Host.CreateDefaultBuilder with no Kestrel and no port, `ashlar brick admit` is a separate process, and a brick generation lives in a collectible ALC that cannot cross a process boundary. Without a channel the seam can only ever exist inside short-lived CLI verbs, and this phase is not XL — it is not possible as specified. This decision changes the work from XL to M, or to zero.

3. **[L]** · **[SDK]** · *agent* — [SDK] Build it once: a single IBrickRegistry registration backed by a façade holding an ordered, volatile-swapped chain [certified generation] → [bootstrap] → [remote catalogs], with a publish verb on a sibling IBrickCatalogPublisher port so IBrickRegistry's two getters stay source-compatible. AddCertificationGate and AddAdaptationInfrastructure both contribute SOURCES instead of each binding their own registry.

4. **[M]** · **[SDK]** · *agent* — [SDK] Replace CertifiedBrickRegistry's plain Dictionary with an immutable-snapshot Interlocked.Exchange, and expose admission through a narrow PUBLIC publisher port — it is `internal` today, so nothing in application/ can drive an admission at all. Torn Dictionary reads do not throw; they hang.

5. **[L]** · **[SDK]** · *agent* — [SDK] The lease: the façade returns a proxy taking BrickGeneration.TryEnter on resolve and Exit on completion, mirroring the retry loop at CertifiedBrickHotSwapHost.cs:271-278 — with a BOUNDED retry, a timeout, a leaked-lease COUNTER and a live-generation GAUGE published onto the heartbeat. A leaked lease pins a generation forever, throws nothing, and otherwise shows up only as an RSS chart nobody is drawing.

6. **[M]** · **[SDK]** · *agent* — [SDK] Deterministic collision policy INSIDE the façade (not CompositeBrickRegistry, which no shipping host resolves): local always wins and says so; among remotes, highest brick version, ties broken by ordinal comparison of the source node id so every node computes the same winner with no coordination protocol; LogWarning on every shadowed id naming winner, loser and both sources. HotSwapCatalogSource bridges CertifiedBrickHotSwapHost to the façade — the host stays a generation manager and does NOT become an IBrickRegistry.

7. **[M]** · *human* — Re-run the N=2 Pi soak with this workload actually present and re-derive the growth curves. The Phase-5 numbers were taken with the principal workload switched off; Roslyn compilations and collectible ALCs dominate this system's memory profile and the Pi's headroom is the constraint.

**Exit criterion (an observable event, not a status).** Against a daemon that has been up for an hour, an admission driven through the chosen control channel makes a BehaviorExecutor constructed at daemon start execute a brick whose source did not exist when the process started — no rebuild, no restart. `ashlar brick list --certified` shows generation 1 containing it, the RegistryResolutionInvariantTests turn green, and the live-generation gauge returns to baseline after 500 swap/execute interleavings and a 2-hour run.

---

## Phase 7 — What the receiver checks, and what survives a dead disk

*2–3 weeks · safe to stop after: **yes***

**Goal.** Stop the receiver adjudicating the sender's own answer key, and give yourself a recovery story that has actually been executed once rather than documented.

1. **[M]** · **[SDK]** · *agent* — [SDK] Durable, listable held queue FIRST: `ashlar gates --list --held` showing every held candidate with its sealer fingerprint and decision time, surviving a restart (which Phase 1's working_dir change is what makes possible). Everything below is a silent drop without it — and a silent drop is what makes an owner switch the control off.

2. **[M]** · **[SDK]** · *agent* — [SDK] Origin as a RECEIVER-DERIVED fact: set at import into a node-local sidecar ({id}.origin.json alongside the gate record), never as a field on ExtensionProposal. GateRecord's Ed25519 signature covers canonical JSON with the sig fields nulled, so a new field on a type that crosses a machine boundary either breaks verification between two nodes imaged months apart or is silently ignored by the older one — and 'silently ignored' means the ceiling does not apply, with nothing printed anywhere. Given 'assorted hardware in my apartment', two nodes built months apart is the normal case, not the edge.

3. **[M]** · **[SDK]** · *agent* — [SDK] The ceiling for imported-origin proposals is POLICY-CONFIGURED with a fail-closed default of Held, not hard-coded in AdmissionGate.Decide. Empty trustedSigners ⇒ imported caps at Held; a pinned signer plus an explicit policy setting is what unlocks auto-admit. Hard-coding the branch would mean shipping trust and removing the safety property in the same commit.

4. **[L]** · **[SDK]** · *agent* — Cost the third option before spending on either extreme: spike compile-and-test of imported source in a SHORT-LIVED SEPARATE PROCESS with no key access and no state-dir write, loading in-process only if it passed. Compiling and loading attacker source in-process IS execution — module initializers and static constructors run on the load side of the lease boundary — so 'the ALC compile is the receiver's verification' is only true in a sandbox. Two costs must go in front of the decision, not be discovered during it: the runtime stage of Dockerfile.cli is mcr.microsoft.com/dotnet/aspnet, NOT sdk, so an ADMITTED package on the Pi today writes source files into a container that can never compile them; and adding an SDK is roughly an order of magnitude of image size plus a Pi disk budget that appears nowhere in this plan. On a 4-core Pi against an SD card, a Roslyn compile plus dotnet test plus restore is minutes per package, four pinned cores, and thermal throttling in a passive case.

5. **[M]** · **[SDK]** · *agent* — [SDK] `ashlar state backup --out <tarball>` / `restore --from`, EXCLUDING the key directory from the tarball by default. Note the correction: since Phase 1 puts ASHLAR_KEY_DIR inside /data/state, a rule that REFUSES when the key dir resolves inside the backup root would refuse unconditionally, forever — and the only way to reach this phase's exit criterion would be `--include-keys`, the one thing the guard exists to prevent. Exclude by default; require `--include-keys` plus a passphrase to include. Otherwise a nightly job writes copies of every node's private seed and the natural next move is to put them where they survive the box dying — the shared folder.

6. **[M]** · *agent* — Rewrite scripts/dr-gate-tier-c.sh's non-mesh branch to do a real backup and restore of a real LiteDB store instead of `echo ashlar-dr-placeholder > fake.litedb` and printing PASS. A DR gate that is green because `cp` works is why a dashboard stays green while the card fills.

7. **[M]** · *agent* — Write docs/OPERATIONS-node-loss.md: the mesh directory IS the backup (every .ashpkg verifies intrinsically with no local keys and no network), so a dead disk costs that node's local gate history and its operator private key and nothing else. Document the re-trust ritual keyed off deploy/fleet-roster.md — `keys init` on the replacement, then `keys untrust <dead fp>` and `keys trust <new fp>` on each surviving box — and record explicitly that a box which was powered off during the ritual is the one node still admitting the revoked signer, detectable by comparing trust-set digests. Record per-node-class policy: the Pi holds and refuses, it never re-certifies.

8. **[S]** · **[SDK]** · *agent* — [SDK] Key hygiene at LOAD, not only at write: Generate chmods the key directory 0700; TryLoad refuses a group- or world-readable operator.key as loudly as it already refuses a corrupt pair; assert the key dir is not under any mounted workspace.

**Exit criterion (an observable event, not a status).** `docker volume rm ashlar-state` on box two, then restore from tarball: its certification records, gate history and cycle count match what they were before deletion; it comes up with a NEW key fingerprint; box one REFUSES its next package until `keys trust` is run with the new fingerprint; and deploy/fleet-roster.md is updated as part of the drill. Executed once, by hand, and recorded.

---

## Decisions only you can make

Each carries the deadline by which it must be answered. The plan is sequenced so that an
unanswered decision blocks one phase rather than invalidating the whole plan.

1. NODE IDENTITY OF THE PROCESS — CLI daemon or Ashlar.API? ANSWER BEFORE PHASE 1's convention test merges. Evidence favours the CLI daemon: Dockerfile.cli:7 has --platform=$BUILDPLATFORM and the image is multi-arch on GHCR, all four installers pull it, and it is the one process where the executors and the hot-swap host share an address space (a brick generation lives in a collectible ALC and cannot cross a process boundary). Dockerfile.api line 1 is a plain FROM with no BUILDPLATFORM, followed by COPY . . and a full restore+publish, so 'try it on the Pi' means an SDK build from a whole-repo copy on a 4GB ARM board — the decision has to be made on the evidence that exists, not on a number that artifact cannot produce. I have built Phase 1 on the CLI daemon and deliberately kept NodeUnitConventionTests from asserting it. Consequence of getting it wrong: a required check encodes the wrong process boundary, which is the most expensive place in the repo to be wrong.

2. DOCKER AS A HARD FLOOR PER NODE — ANSWER BEFORE PHASE 1. Everything in Phases 1-5 assumes it. If the Pi must run bare-metal under systemd, deploy/node.yml needs a systemd sibling, the log-rotation guarantee moves to journald limits, the HEALTHCHECK becomes a systemd watchdog, and the entrypoint script becomes an ExecStartPre. This is a fork in Phase 1, not a later adjustment.

3. WHICH TWO MACHINES ARE PHASE 4, AND WHAT IS THE PHYSICAL RECOVERY STORY — ANSWER BEFORE PHASE 4, ideally before Phase 1 so you buy hardware early. The gaming box has no sshd and no node while it is gaming; the Mac sleeps; a laptop with a dying battery is a UPS with a bad cell. Also decide now whether a spare imaged SD card and a UART console exist: when swap thrash kills sshd on the Pi, the only remedy is walking over and pulling power — into LiteDB and ext4 on a card that was just thrashed — and no phase in this plan has a next step after that.

4. NODE NAME vs KEY FINGERPRINT AS IDENTITY — ANSWER BEFORE PHASE 3. If the key is the identity, rotating a key renames the node and orphans its whole history. If the name is the identity, a reimaged box silently inherits the trust of the machine it replaced — wrong after exactly the disk failure that motivates backups. Recommendation: two separate values, both printed on the heartbeat and in fleet-roster.md, neither derived from the other. It costs a decision now because the peers/ layout and Phase 7's origin sidecar format both depend on it.

5. THE ADMISSION CONTROL CHANNEL — ANSWER BEFORE PHASE 6, and Phase 6 cannot start without it. `ashlar brick admit` as a separate process cannot publish into the running daemon's registry. Pick: (a) a daemon-side watched admission directory — simplest, no new listener, fits the file-based grain of everything else; (b) a local Unix socket or loopback control endpoint on the daemon — also gives you a real /health, at the cost of a listener on a generic host; (c) accept that certification/hot-swap only ever runs inside short-lived CLI verbs, in which case the registry seam is closed as not-applicable and the policy-gate stack is the only extension path. This moves the work from XL to M, or to zero.

6. WHICH SELF-EXTENSION STACK GETS THE INVESTMENT — DECIDE AFTER PHASE 4, with your own two machines in front of you, and reject the either/or framing. PackageFile.Content and CertifiedBrickLoadRequest.SourceCode are both `string`; these are a producer and a consumer one adapter apart, not two competing products. The real question is whether you fund the adapter (Phase 6 plus the process-isolated compile in Phase 7) or accept that receiving a brick means the receiver rebuilds. Frame it PER NODE CLASS, not fleet-wide: the Pi can hold and refuse; on a 4-core board against an SD card it can never be a re-certifying receiver.

7. THE .NET SDK ON RECEIVING NODES — ANSWER BEFORE PHASE 7 step 4, not during it. The runtime stage of Dockerfile.cli is aspnet, not sdk. An ADMITTED package on the Pi today writes source files into a container that cannot compile them, so 'admitted' means bytes on disk. Phase 7's process-isolated compile silently requires an SDK on every receiving node: roughly an order of magnitude of image size, a Pi disk budget that appears nowhere in this plan, and minutes of four pinned cores per package.

8. AUTONOMY DIAL FOR IMPORTED CODE — ANSWER DURING PHASE 7. May a package from a pinned trusted key auto-admit while you are asleep, or does every cross-machine transfer wait at Held for a keystroke? Phase 7 ships the mechanism with a fail-closed Held default; you set the dial. The honest framing: a signer pin proves 'I recognise this key', never 'I verified this code' — and per-box keys mean the Pi is a distinct principal that can itself be compromised.

9. THE FLEET-WIDE BACKLOG — ANSWER AFTER PHASE 4. Do nodes exchange only finished immutable artifacts and each keep a local queue (sound, no coordination protocol, you queue work per box), or is there one backlog any node can claim from? I cut the cheap version of the latter: pointing every node's ASHLAR_OBJECTIVES_ROOT at a synced folder LOSES objectives silently and BREEDS phantom ones — ObjectiveStore.MoveTo is write-tmp, delete-new, move, delete-old with a window where an objective exists in two status directories or none, and across a sync boundary those replicate independently; and List() takes Path.GetFileNameWithoutExtension as the id with NO validation, so a Syncthing conflict copy is not a corrupt file, it is a new valid claimable objective. If you want a shared backlog it is per-node inbox directories of immutable handoff records with a deterministic tie-break — about one week, not an afternoon.

10. DOES THE SOAK BECOME A REQUIRED CHECK, AND WHAT GETS DELETED TO PAY FOR IT — ANSWER AFTER PHASE 5. A running-system observation catches what no unit test can. But GitHub-hosted runners cap a job at 6 hours, so an 8h nightly must be self-hosted, which turns one of your machines into CI infrastructure. And the repo has 58 workflows, 18 PR-triggered, 1 required, with 10 continue-on-error in uat-gate alone. If the soak becomes a gate, uat-gate's continue-on-error come out or uat-gate goes.

11. HARD REFUSAL OR PARKED DEGRADED STATE when the gate store lands on a synced filesystem — ANSWER DURING PHASE 5. I chose parked-and-loud-and-alive. A hard refusal will occasionally block a legitimate setup at an inconvenient moment; a warning will be ignored. This is a judgement about your tolerance for a runtime that declines to work.

12. PER-NODE LOCAL INFERENCE OR ONE BOX SERVING THE LAN — ANSWER AFTER PHASE 5. OllamaProposalOptions.BaseUrl is configurable, so it is a deployment choice, and the Phase-5 soak on the Pi gives you the real RAM headroom number to decide with.

---

## What an agent can do, and what needs you

The agent environment has NO .NET SDK, so CI is the first compiler and every C# change is written blind and validated by a push. That is workable but it sets the grain of the work. AN AGENT CAN DO UNSUPERVISED: all of Phase 0 except the two hardware probes (TSV re-dating, the reusable-container-publish.yml platform edit, ci/cert-gate-assertions.md); every Dockerfile, compose, shell and markdown artifact in Phase 1 (node.yml, node-entrypoint.sh, node-agents.json, node-update.sh, fleet-update.sh, the installer wrapper); all of Phase 2 and Phase 3's C# and the e2e-loop.sh three-actor restructuring; the Phase-5 deterministic budget unit test, BoundedJsonlWriter and the convention tests; Phase 6's façade and lease; Phase 7's held queue, sidecar, backup verb and dr-gate rewrite. Each of those is written, pushed, and iterated against cert-gate — expect two or three round trips per C# change because there is no local build. WHAT NEEDS YOU, PHYSICALLY: the Phase-0 `uname -m` and the first-ever arm64 execution on the Pi (a plan-shaping answer no agent can produce); the Mac's DOCKER_DEFAULT_PLATFORM check; the Phase-4 key ceremony, which is deliberately manual because reading a fingerprint across from one box to another is the control, not a chore; the Phase-4 recording; the Phase-5 soak, which runs on the Pi over 8 hours with a scripted SIGKILL and a real reboot; the Phase-7 restore drill, which is worthless unless a human has actually executed it once; and every owner decision above. TWO PLACES THE NO-SDK CONSTRAINT ACTUALLY HURTS: (1) the Phase-1 entrypoint script and heartbeat are only testable by building the image, so budget a CI image-build loop and get the entrypoint's clock-wait and first-run-init logic reviewed carefully before the first push; (2) Phase 6's lease and snapshot-swap work is concurrency code, which is the worst possible category to write without a local runner — that is another reason it sits last and behind a decision. Mitigation used throughout: every mechanism lands as an assertion inside the cert-gate namespaces or scripts/e2e-loop.sh, so CI tells you whether the blind write was correct instead of the Pi telling you three weeks later.

---

## What the red team changed

The integrated plan did not survive contact unchanged. These are the corrections, kept because
each names a specific failure the original would have walked into:

- ADDED PHASE 0 and moved the 2026-10-27 fuse into it as day-one work. Seven UNOWNED rows expire on that date, TestOwnershipConventionTests.cs:82 reads DateTime.UtcNow.Date, and it rides cert-gate — the only required check. It was previously scheduled at the END of a 1.5-week Phase 2 behind a 3-4 day Phase 1; one interruption pushed it past the fuse, blocking every PR at exactly the moment of return, with the file's own header reading as 'this cannot be fixed in one PR'. Now re-dated to 2027-03-31 in its own commit before anything else happens.

- PAIRED THE DIGEST PIN WITH BOTH AN IMAGE FIX AND AN UPDATE PATH. Verified there is NO digest today that is both multi-arch and permanent: reusable-container-publish.yml sets SHA_PLATFORMS="linux/amd64" on branch builds and only widens on refs/tags/v*, so the immutable sha tag is amd64-only and the only multi-arch tag is mutable, republished and GC-prunable. Phase 0 now fixes the workflow so master builds publish arm64 on the immutable tag; Phase 1 adds scripts/node-update.sh and scripts/fleet-update.sh; and Phase 4's exit criterion has an explicit re-pin clause. Without this Phase 4's exit criterion was literally unreachable — the Pi would run an image predating the code that prints REFUSED, and the symptom reads as 'the trust root is broken'.

- SHIPPED A DEFAULT AGENT SET INTO THE IMAGE. Verified `find application/src/Ashlar.CLI -iname 'appsettings*'` returns nothing, BackgroundAgentConfigLoader.LoadAsync binds only `BackgroundAgents:Agents`, and Dockerfile.cli copies only /app/publish — so the shipped node loads zero agent configs and reaches Task.Delay(Timeout.Infinite). Phase 1 now COPYs .docker/node-agents.json to /app/config/agents.json (that directory already exists and is chowned at Dockerfile.cli:43) and passes --config. Without it Phase 5's soak charts a flat line by construction and its clock exit criterion has nothing to fire against, because GateStore.ProposeAsync's only non-CLI caller is reached solely when an extender agent runs.

- GAVE THE NODE A PROJECT DIRECTORY. PkgCommand.cs:402-406 hard-requires ashlar.yaml AND ashlar.policy.yaml in --path, and no phase created one on a node — Phase 4's exit criterion died two lines into the verb with `not an ashlar project: /app`. Phase 1's entrypoint now runs a first-run `ashlar init node --path /data/state/project` (idempotent: InitCommand.cs:49-58 refuses to overwrite).

- PERSISTED THE GATE STORE, WHICH THE CONVENTION RULE COULD NOT SEE. Verified all three call sites construct `new GateStore(Path.Combine(projectDir, ".ashlar"))` with no environment variable anywhere, so an ASHLAR_*-based assertion is structurally blind to the directory holding every trust decision, the HELD queue and the whole admission history. Phase 1's node.yml sets working_dir: /data/state/project, NodeUnitConventionTests gains an explicit clause naming it, and Phase 1's exit criterion now checks that a gate record written before a restart is listable after it.

- MOVED THE PARK-DON'T-EXIT RULE FROM PHASE 5 TO PHASE 1, where its trigger ships. `restart: unless-stopped` plus BackgroundAgentDaemonCommand's non-zero returns feeding Environment.Exit at Program.BackgroundAgentCommands.cs:717-723 is an unthrottled crashloop, and Phase 1's own 10m×3 log rotation would erase the first failure. Added a startup writability probe on ASHLAR_STATE_DIR printing the exact chown command, for the root-owned external-SSD bind mount the Pi will hit.

- TURNED THE HEARTBEAT INTO A STATUS DOCUMENT ON A >=60s TIMER. 'Each cycle' against ScheduleExecutor's 1-second tick is ~86,000 SD writes/day, added four to six weeks before anything bounds the seven existing appenders. It is now rewritten in place on a fixed timer and carries {status, reason, lastAdmissionAt, cyclesSinceStart, nodeId, keyFingerprint, trustSetDigest, clockSynced}, with the HEALTHCHECK reporting unhealthy on parked or stale. This is the one change that makes both the crashloop and the silent-park visible from `docker ps` after three weeks away.

- MOVED THE FINGERPRINT PRINT FROM PHASE 3 TO PHASE 1. It is a print statement with no dependency on trustedSigners, PolicyLoader or the keys verbs, and it converts the first observable on real hardware from week four to week one — the cheapest available fix for the abandonment window. Also corrected the plan's own claim: Fingerprint IS already called at PackageImport.cs:99; the gap is purely that PkgCommand never prints it.

- SPLIT PHASE 2 RATHER THAN REORDERING IT. CP2's one-line fix, CertGateFilterCoverageTests and the TimeoutConventionTests namespace move went into Phase 1 (minutes of work each, and CertificationStoreCompositionTests already exists to catch CP2). Phase 2 shrinks from 1-1.5 weeks of invisible debt to three days of the only work that must precede trust — and ForgeApplierGovernanceTests now ships in the SAME pull request as the normalization, so a half-landed governance floor is impossible.

- DROPPED THE LOAD-TIME PolicyLoader REJECTION. The real control is the PackageImport comparison, which refuses before anything is parked. The load-time version bought no additional security and broke ~15 e2e scenarios plus portability-gate.yml:66-70 on a non-required 3-OS gate that can sit red through an entire absence. Its migration story was also inert: InitCommand has zero OperatorKey references, refuses to overwrite existing projects, and scaffolds `sealed` — a mode the rejection does not apply to. Replaced with `ashlar policy migrate --add-self` plus a trustedSigners clause in e2e-loop.sh's set_proposing().

- NAMED THE ENUM AND FIXED THE ASSERTED STRING. The trust check returns PackageAdmission.Refused, landing in PkgCommand's `default:` branch which prints `× REFUSED  {summary} · {message}` — the fingerprint goes in result.Message, not a dash suffix. Two phases assert on that exact format.

- SIZED THE E2E HARNESS CHANGE CORRECTLY. $WORK/pkgkeys is referenced at scripts/e2e-loop.sh lines 351, 410, 418, 459 and 465 plus the trusted/ assertion at 279 — five downstream references, not one export.

- INVERTED THE PHASE-5 CLOCK WORK. The budget-refill test now lands FIRST as a deterministic unit test, because GateStore.ProposeAsync (line 161) and AdmittedInWindowAsync (line 304) both already take `now` as a parameter. libfaketime is demoted to optional end-to-end confirmation with an explicit note that .NET resolves clock_gettime via the vDSO. The phase's headline property no longer depends on a spike that may not work or on a runtime that may not be admitting anything.

- GAVE THE CLOCK GUARD A RECOVERY PATH. A Pi has no RTC, fake-hwclock restores the last shutdown time, and dockerd starts before chrony syncs — so a forward-jump guard fires on EVERY boot, and a box off for a week sees a week-long jump and parks indefinitely. The guard now recovers on 'the clock became synchronized' (adjtimex STA_UNSYNC, or a plausibility floor of now > image build date), not only on a timer backoff.

- REPLACED THE doctor clock IMPLEMENTATION. timedatectl and systemsetup are both unreachable from an aspnet container with no systemd and no /run/systemd mount, so as specified the diagnostic printed 'unknown' on every node. Now reads adjtimex/ntp_adjtime plus peer skew — the container shares the host clock, so this works.

- ADDED SD-CARD WEAR TO THE SOAK AND NAMED THE MACHINE. `du` measures size at rest, which plateaus by design and is uncorrelated with card death; cumulative sectors-written from /sys/block/mmcblk0/stat is added, with a wear budget in the script header. The soak runs ON THE PI — if the Pi cannot host two containers, that is the finding. Also added --disable-observation to node.yml through the Phase-1-to-Phase-5 window and mem_limit/cpus plus `dphys-swapfile swapoff`, because swap thrash on an SD card kills sshd before the container and leaves a physical power pull as the only remedy.

- ADDED deploy/fleet-roster.md AS A CHECKED-IN ARTIFACT and had fleet-demo.sh diff it against live `keys show` output. Trust otherwise exists only in peers/ on the machines it describes, in a directory Phase 7's backup excludes — unanswerable after a reimage. Added a trust-set digest to `keys peers` and to the heartbeat so a box that was off during a revocation shows as divergent. The recording's re-cut date lives in that file's header, NOT on cert-gate: the plan defuses one wall-clock bomb on the required check and must not plant another.

- FIXED THE PHASE-7 BACKUP GUARD, WHICH WOULD HAVE REFUSED UNCONDITIONALLY. Phase 1 puts the key dir inside /data/state, so 'refuse when the key dir resolves inside the backup root' fires on every invocation forever, and the only path to the phase's exit criterion was --include-keys — the one thing the guard exists to prevent. The rule is now 'exclude the key directory from the tarball by default'.

- RECONCILED ASHLAR_MESH_DIR. Phase 1 keeps it under the state dir and ships a commented, ready-to-uncomment bind of the host's shared folder onto /data/state/mesh, so Phase 4 uncomments a line instead of overriding the variable the required gate certifies. Also documented that MeshStore.Resolve APPENDS '/published' — a one-level-off produces an empty peer store with no error.

- MADE THE DEMO DISTINGUISH 'PEER IS OFF' FROM 'PEER HAS NOTHING NEW'. PkgCommand.cs:414-418 prints `the peer store is empty` and returns 0 in both cases, and under AP client isolation a stalled Syncthing produces exactly that. fleet-demo.sh now checksums $ASHLAR_MESH_DIR/published on both boxes over ssh before pulling, so a dead transport reports as a transport failure instead of masquerading as a gate result.

- TIGHTENED THE PACKAGE ENUMERATION. PkgCommand.cs:413 globs *.ashpkg, which matches macOS AppleDouble sidecars (._pkg-*.ashpkg) and in-flight scp copies; both parse-fail into `× REJECTED` lines that bury the one clean REFUSED line the recorded demo exists to show. Now skips names beginning '._' or '.' and files whose mtime is within a few seconds.

- ADDED A LiteDB OPEN RETRY. Every store builds `Filename={path}` with no Connection=shared and no retry anywhere, so LiteDB 5 opens Direct/exclusive — and `ashlar gates --admit` through the host wrapper is a second process against the same state directory. Intermittent failure in front of you during the demo is worse than deterministic failure.

- INSTALLED AN `ashlar` HOST WRAPPER. There is no such command on any host: the installers print `docker run --rm` examples with no state volume, and the natural fallback of a host build resolves OperatorKey.ResolveKeyDir() to ~/.ashlar/keys, giving every box TWO operator identities. Every exit criterion in Phases 1/3/4/7 that reads `ashlar …` now names a real command that touches the node's state.

- MOVED THE NODE FILE TO deploy/node.yml AND HARDENED ITS TEST. deploy/compose/ already holds sixteen files including the muted mesh-lab ones. NodeUnitConventionTests now also asserts that exactly ONE compose file in the repo declares a named volume at the ASHLAR_STATE_DIR value, so it cannot silently pass on a missing or renamed file — the exact pattern that produced the Fleet.Host incident this repo just wrote a registry to prevent.

- ADDED ci/cert-gate-assertions.md IN PHASE 0. The plan concentrates roughly eight merge-blocking assertions on the one required check, whose demonstrated failure response in this repo is a mute — eight times in August alone. This file is the only thing that tells a returning owner what a single branch-protection toggle would delete.

- ADDED FOUR OWNER DECISIONS the plan was missing: which two machines are Phase 4 (and whether a spare SD card and UART console exist); whether a .NET SDK ships on receiving nodes (Phase 7's compile silently requires one, and the runtime stage is aspnet); Docker-as-floor promoted to a Phase-1 blocker rather than a general note; and node-name-versus-fingerprint as identity, which the peers/ layout and the origin sidecar both depend on.

- MARKED PHASE 6 NOT SAFE TO STOP AFTER, explicitly, with the reason: the façade replaces two competing IBrickRegistry bindings, so a half-landed swap leaves an admission target some resolvers see and others do not — strictly worse than today's honest disconnection. Its step 1 (the RED invariant test) is the safe stopping point and is worth landing on its own.

---

## Residual risk — what this plan does not address

- THE ARM64 IMAGE HAS NEVER BEEN EXECUTED BY ANYTHING. The publish workflow's only smoke test is `docker run --rm ${SHA_TAG} --help` on an amd64 runner against an amd64-only tag. NSec.Cryptography 25.4.0's per-RID native libsodium resolution on linux-arm64 from a framework-dependent publish is unvalidated, and the repo already documents native-toolchain trouble on this platform. Phase 0 step 2 is the first execution ever. If it fails, this plan does not have a fallback and Phase 1 changes shape.

- IF THE PI IS 32-BIT (armv7l) THE PUBLISHED IMAGE IS UNUSABLE and there is no armv7 variant. That is a ten-second check scheduled in Phase 0, but nothing in this plan solves it if the answer is bad.

- CI IS THE FIRST COMPILER FOR EVERY C# CHANGE. No .NET SDK exists in the agent environment, so every [SDK] step is written blind and validated by a push. Expect two or three round trips per change. The worst case is Phase 6's lease and snapshot-swap work, which is concurrency code — the category least suited to blind authorship, and another reason it sits last.

- NOTHING HERE ADDRESSES DISCOVERY. mDNS is cut deliberately and the replacement is a hand-maintained deploy/fleet.env plus a shared folder. That is right for 3-6 boxes whose names you know and it survives a machine vanishing for a week. It does not scale, it does not self-heal, and adding a seventh box is manual work on every existing box.

- NOTHING HERE GIVES A PEER ANY LIVENESS SIGNAL. The heartbeat file lives inside its own container. A dead node and a live node with nothing new produce identical output from `pkg pull`. Phase 4's checksum assertion mitigates the transport case only; there is still no way for box A to know box B is alive without ssh.

- MESH STORE GROWTH IS UNBOUNDED AND PULL IS O(n) FOREVER. PullAsync has no cursor and no filename-level skip: it reads and fully Ed25519-verifies EVERY .ashpkg on EVERY invocation before it can determine AlreadyImported. Six months in with a few hundred packages that is minutes of SD reads and pinned CPU per pull on the Pi. No phase bounds the store or adds a seen-set. This is the most likely thing to make a working fleet slowly stop feeling like one.

- WIREFORMATVERSION IS STAMPED AND COMPARED BY NOBODY. Two instances built months apart is the normal case for 'assorted hardware in my apartment'. Phase 7's sidecar rule avoids making it worse; nothing in this plan makes it better. What two skewed nodes actually do to each other remains a known unknown.

- REVOCATION DOES NOT PROPAGATE. `keys untrust` is manual and local, and a box powered off during a revocation is the one node still admitting the compromised signer. The trust-set digest makes the divergence VISIBLE when you look; nothing makes it converge.

- THE PLAN'S ESTIMATES ARE FOCUSED-DAY ESTIMATES. This repo's history is 109 commits across nine active days in two bursts separated by a five-day gap, and eight workflows muted rather than fixed. Sum the focused days and the whole plan is roughly seven to nine weeks of work; at the demonstrated burst rate the metal demo is week three or four and Phase 6 is month three or later. Phases 0-4 are the part I would actually bet on.

- CERT-GATE REMAINS A SINGLE POINT OF FAILURE. Roughly eight merge-blocking assertions now concentrate on the one required check. ci/cert-gate-assertions.md tells you what a mute would delete; it cannot stop you muting it. I removed the wall-clock fuses (the TSV re-dating in Phase 0, the recording's expiry moved to fleet-roster.md) because those were the triggers most likely to fire while you were away, but a renamed path or an eighth appender added by an agent will still turn it red.

- NOTHING HERE MAKES A HELD CANDIDATE REACHABLE UNTIL PHASE 7. Between Phase 3 and Phase 7, 'hold' is a durable gate record you can list only through a CLI verb against the node's project directory — which now works, but there is no queue view, no notification, and no reason a returning owner would think to look.

- THE PROCESS-ISOLATED COMPILE IN PHASE 7 IS A SPIKE, NOT A COMMITMENT. If it prices out badly — and on the Pi it will — the honest outcome is that receiving a brick means the receiver rebuilds, and 'self-extending across the fleet' means 'source propagates, behaviour does not, until a rebuild'. That is a real possible ending for this plan and it should not arrive as a surprise in month four.

- CP7-ONWARD REMAIN HYPOTHESES. Only about ten of roughly seventy-five audit gap claims were adversarially verified, three confirmed and four materially corrected, and two were already found wrong. Phase 5's final step is an explicit re-check against what the soak actually showed. If instances.json clobbering, wire-version skew or the RAM floor turn out not to bite in eight hours on real hardware, write that down and drop them rather than defending the audit.

