# Hardware bring-up — getting Ashlar running on your own machines

*Written 2026-08-28 against `master` @ `0983d74`. **Revised the same day after actually running it**,
on a Windows 11 / Docker Desktop box, against `master` @ `e254bc79`.*

**Read this before the closing plan.** `CLOSING-PLAN.md` says what to *build*. This says how to
get what exists onto your hardware today, and how to *verify* each layer so you know which
failures are expected and which are news.

> **What is measured and what is not.** The first draft of this document was written from reading
> the code, and said so. It has since been executed on one machine — an x86_64 Windows host running
> Docker Desktop in Linux-container mode — and **four of its claims turned out to be wrong**. Those
> are corrected below and marked *measured*.
>
> **The arm64 artifact has now been executed once, under QEMU emulation on that same Windows box:**
> `--help` exits 0, `/app/runtimes/linux-arm64/native/libsodium.so` is present in the image, and
> `keys init` produced `ed25519:76f8c74a6c63477e`. That is real evidence against the feared
> native-load failure — but emulation is not hardware, and **no arm64 machine has run any of
> this.** Running it on the MacBook is what closes the question. Where this document and your
> machine disagree, your machine is right.

---

## 0. The fleet, and one probe that matters

**The fleet is a Windows box and an M-series MacBook.** Earlier drafts of this document and of
`CLOSING-PLAN.md` assumed a Raspberry Pi; there isn't one. That makes the plan's oldest unknown
*cheaper*, not harder — an M-series Mac is an arm64 machine, so it exercises the same
`linux-arm64` RID and the same native libsodium a Pi would have. There is no 32-bit-OS branch to
worry about and no `armv7l` cliff.

One probe still matters, on the Mac, and it must come first:

```bash
echo "${DOCKER_DEFAULT_PLATFORM:-unset}"
```

| Output | Meaning |
|---|---|
| `unset` | Good — Docker selects `linux/arm64` natively. No `--platform` flag needed anywhere below. |
| `linux/amd64` | **Unset it before you test anything.** The repo's own docs tell Apple Silicon users to set this. Left set, the Mac silently runs the **amd64** image under emulation: no error, several times slower, NSec in a configuration nobody has tested — and the arm64 question goes unanswered while appearing answered. |

Then confirm the machine is what you think it is, and that the image really is multi-arch:

```bash
uname -m && docker buildx imagetools inspect ghcr.io/ianfrelinger/nexo-cli:latest
```

Expect `arm64`, and an OCI index listing both `linux/amd64` and `linux/arm64` — *measured*.

### On Windows: use WSL if you can

Docker Desktop's WSL2 backend does **not** give you a usable Linux shell on its own — the only
distro it installs is `docker-desktop`, its own utility VM. `wsl --install -d Ubuntu` gives you a
real one, and from there every path in this document is a native Linux path and the Git Bash trap
below does not exist. If you stay in Git Bash, read the next section first.

### On Windows, one more line before anything

*Measured, and it will waste an hour if you skip it.* Git Bash rewrites POSIX-looking paths in
command arguments into Windows paths before `docker.exe` sees them, so
`-e ASHLAR_KEY_DIR=/data/state/keys` arrives as a `C:\...` string and the CLI dies on
`Access to the path '/app/C:' is denied`. It looks exactly like a product bug and is not one.

```bash
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'
```

With conversion off, `docker build`/`compose -f` then need **Windows-style** paths
(`C:/Users/...`), because nothing is translating for you any more. Both directions bite.

---

## 1. Ground truth before you start

Five things that will otherwise waste an evening. Each says whether it was **read** from the code
or **measured** by running it.

| What you might expect | What is actually true |
|---|---|
| The daemon runs agents | **It runs none — until you give it a config.** *Measured.* No `appsettings*` ships in the CLI image, and `BackgroundAgentConfigLoader` binds only `BackgroundAgents:Agents`, so out of the box it creates zero agents. Pass `--config` and it registers, schedules and executes them. See §2b. |
| The daemon otherwise idles | **It does not idle.** *Measured, and the first draft got this wrong.* It runs an observation pipeline and a filesystem event source, and probes a model backend at `127.0.0.1:11434` — which inside a container is *the container itself*, so it can never reach a model server on your host or LAN. Set `ASHLAR_OLLAMA_BASE_URL`. |
| A container keeps its identity | **It does not — and it fails quietly.** *Measured.* `.docker/Dockerfile.cli` sets `ASHLAR_STATE_DIR` and three siblings but **not** `ASHLAR_KEY_DIR` or `ASHLAR_MESH_DIR`, so `keys init` reports `stored in /home/app/.ashlar/keys`. The next container does not report a *different* key — it reports **no key**, and says `gate decisions are recorded unsigned`. A containerised node silently stops signing. |
| The installer sets you up as a node | It runs `docker run --rm … background-agent daemon` (`scripts/install/container-bootstrap-linux.sh:248`) — **`--rm`, no state volume**. It is a smoke test, not a deployment. |
| `ashlar keys trust` exists | **It does not**, and a node still cannot tell one signer from another. But it is not defenceless, and the first draft overstated this — see below. |

### What "no trust root" actually means

*Measured.* `KeysCommand` has `init` and `show` only. A `trusted/` directory **does** exist
(`OperatorKey.cs:45`), but it holds **your own** superseded public keys after `keys init --rotate`
so previously-signed records still verify. It is not a peer trust store.

The accurate statement is narrower than "a node cannot refuse a package from an unknown signer":

- A **sealed** node refuses everything outright — `× REJECTED … mode is sealed`, nothing written.
- A **proposing** node **holds** everything at its own gate before anything touches disk, remote
  code included.
- A **tampered** package is refused before any gate is consulted.

What is missing is *discrimination*: a node cannot auto-admit Alice while refusing Bob, and
**the operator is not shown which key signed a held package** — the prompt says
`! HELD  add brick rogue.exfil · review with 'ashlar gates'` and no fingerprint. You are asked for
a trust decision with the identity withheld. That is `CLOSING-PLAN.md` Phase 1 (print the signer)
and Phase 3 (a trust root), and it is the real gap.

---

## 2. Track A — one machine, from the published image

Fastest path to something real. No source build, no SDK.

```bash
docker pull ghcr.io/ianfrelinger/nexo-cli:latest
docker run --rm ghcr.io/ianfrelinger/nexo-cli:latest --help
```

**Pin the digest.** `:latest` is republished on every master push and its old manifest becomes
garbage-collectable, so a node pinned to `:latest` silently changes under you and a node pinned to
an old `:latest` digest eventually 404s. Take the immutable tag instead:

```bash
docker buildx imagetools inspect ghcr.io/ianfrelinger/nexo-cli:latest
```

> The `sha-<12>` tags only became multi-arch on master builds as of `0983d74`. **Any `sha-` tag
> published before that is amd64-only** and will fail on the Mac with `no matching manifest for
> linux/arm64`. Use a `sha-` tag from a build after that commit, or the current `:latest` digest.

**Give it a state volume, and name the key and mesh directories.** *Measured*: with these two
variables set, one fingerprint survived three separate containers and an explicit `docker rm`.

```bash
docker volume create ashlar-state
docker run --rm -v ashlar-state:/data/state \
  -e ASHLAR_KEY_DIR=/data/state/keys \
  -e ASHLAR_MESH_DIR=/data/state/mesh \
  ghcr.io/ianfrelinger/nexo-cli:latest keys init
```

Run `keys show` twice in different containers. Same fingerprint means Phase 1's theory holds
before anyone edits a Dockerfile. **No key at all** means you left the variables out.

### The volume-ownership trap

*Measured, and it is not obvious.* The image runs as `app` (uid 1654). Docker Desktop resets a
named volume's **root** ownership to `root:root` on every mount, so `chown` on the mountpoint
looks like it worked and has reverted by the next container — but ownership of **subdirectories
inside** the volume does persist. So every path you hand the app must be a seeded subdirectory:

```bash
docker run --rm -u 0:0 -v ashlar-work:/vol --entrypoint sh IMAGE \
  -c 'mkdir -p /vol/proj && chown 1654:1654 /vol/proj'
```

Without this, `ashlar init` fails with `Access to the path '/work/ashlar.yaml' is denied` and it
looks like a permissions bug in the product.

---

## 2b. A node that actually runs by itself

*Measured end to end.* The daemon runs agents when it is given them. `ModelProvider` defaults to
`deterministic`, so **a node is autonomous with no model server at all**.

`agents.json`:

```json
{
  "BackgroundAgents": {
    "Agents": [
      {
        "Id": "gate-watch",
        "Name": "Gate Watcher",
        "Role": "observer",
        "ModelProvider": "deterministic",
        "Commands": ["observe"],
        "Enabled": true,
        "MaxDataSensitivity": "Public",
        "Schedule": { "Type": "Interval", "Interval": "00:05:00", "InitialDelay": "00:00:10" }
      }
    ]
  }
}
```

```bash
docker run --rm -v ashlar-state:/data \
  -e ASHLAR_STATE_DIR=/data/state -e ASHLAR_KEY_DIR=/data/keys -e ASHLAR_MESH_DIR=/data/mesh \
  -v ./agents.json:/etc/ashlar/agents.json:ro \
  IMAGE background-agent daemon --config /etc/ashlar/agents.json
```

Expect, in the log:

```
Loaded 1 background agent configurations
Creating 1 enabled background agents
Started schedule for agent: gate-watch
Executing background agent: gate-watch (role=observer, cycle #1)
```

A three-service Compose stack that seeds directory ownership, commissions the identity
idempotently, and restarts the daemon unless stopped is the deployable form of this. Its
commissioning step must be idempotent — `keys init` and `ashlar init` both correctly refuse to
overwrite — and its identity must survive `docker compose down`. *Measured*: it does.

**Required, or the node lies to you:** `ASHLAR_KEY_DIR`, `ASHLAR_MESH_DIR`, `ASHLAR_STATE_DIR`,
and `ASHLAR_OLLAMA_BASE_URL` if you want the model probe to reach anything. On a plain Linux host
add `--add-host host.docker.internal:host-gateway`; Docker Desktop resolves it natively.

---

## 3. Track B — from source, if you want to develop

Needs the **.NET 10 SDK** (`global.json` pins `10.0.100`, `rollForward: latestFeature`).
SDK 10.0.400 satisfies it.

```bash
git clone https://github.com/IanFrelinger/Ashlar.git && cd Ashlar
dotnet build Ashlar.sln
```

*Measured*: it builds. The first draft flagged this as possibly-broken because no automatic
workflow runs it; that was caution, not a defect.

### The dev container could not run this repo's net8.0 targets

*Measured.* `mcr.microsoft.com/devcontainers/dotnet:10.0-noble` ships **only** .NET 10 runtimes.
`devcontainer.json` set `DOTNET_ROLL_FORWARD=LatestMajor` to compensate — but `LatestMajor` rolls
`net8.0` onto ASP.NET Core 10 **even when the 8.0 runtime is installed**, and ASP.NET Core 8's
`ResponseBodyPipeWriter` predates `PipeWriter.UnflushedBytes`, which System.Text.Json 10 requires.
Every HTTP-hosting test then fails with an exception that reads exactly like a product bug:

| GameDirector net8.0 suite, 8.0.30 present | result |
|---|---|
| `DOTNET_ROLL_FORWARD=LatestMajor` | 10 failed |
| unset | **167 passed** |

Fixed in **#407**: `post-create.sh` installs the real ASP.NET Core 8 runtime and the variable is
gone. If you build your own test image, install the 8.0 runtime and do **not** set roll-forward.

> **No longer outstanding.** This note used to say `scripts/test-in-container.ps1`,
> `scripts/handoff/devbox.sh`, `scripts/Verify-DevContainer.ps1` and
> `spikes/autonomy-first-flight/run-first-flight.ps1` still set `LatestMajor` themselves. None of
> them does: the three that mention it now carry a comment explaining why they install the real 8.0
> runtime instead, and `Verify-DevContainer.ps1` never mentioned it at all. Verified with
> `grep -rn LatestMajor` across the four. Left here rather than deleted because the trap is real and
> worth recognising — `LatestMajor` is harmless for cert-gate and wrong for anything hosting HTTP,
> measured on this repo at 10 failed vs 167 passed.

### Running a suite in the container

Two entry points, same image and the same read-only mount:

```bash
# bash / Git Bash / Linux / macOS — also runs an arbitrary command, not only `dotnet test`
scripts/test-in-container.sh --filter 'FullyQualifiedName~LiteDb' --framework net8.0
scripts/test-in-container.sh --dirty -- 'dotnet build Ashlar.sln -c Debug --nologo'
```

```powershell
pwsh scripts/test-in-container.ps1 -Filter "FullyQualifiedName~CertifiedBrickHotSwapHostTests"
```

The repository is mounted read-only and re-cloned inside, so no Linux `bin/obj` reaches the host
tree and only committed state is tested — `--dirty` carries uncommitted work in as a patch plus a
tarball of untracked files. **Every build in this harness prints two SourceLink warnings** about the
inner clone's `origin` being `/src-mirror`; they are absent in CI, so grep the warning text and
never compare a warning *count* from here against one from CI.

---

## 4. The testing plan

Five layers, each answering one question. **Run them in order** — a failure at L1 makes L3 results
meaningless. The right-hand column is what one x86_64 Windows box actually produced.

### L0 — Does the artifact run at all?

| # | Command | Expected | Measured on `pc` |
|---|---|---|---|
| 0.1 | `uname -m` | `x86_64` or `aarch64` | **PASS** `x86_64`, Linux-container mode |
| 0.2 | `docker run --rm <image> --help` | verbs incl. `keys`, `pkg`, `verify`, `gates`, `doctor` | **PASS**, exit 0 on amd64, and exit 0 on arm64 under QEMU. On the Mac a native-load error would mean NSec's arm64 libsodium does not load on real hardware — capture full stderr if it fails |
| 0.3 | `doctor --json` | JSON | **exit 1, and that is correct.** Run inside the shipped image, `doctor` audits a *developer workstation*: it reports `hostOs: Ubuntu 24.04.4` (the container), fails `cliSmoke` with "No .NET SDKs were found" and `containerSmoke` with "docker: command not found". Do not treat its exit code as node health |

### L1 — Does state survive?

| # | Check | Expected | Measured on `pc` |
|---|---|---|---|
| 1.1 | `keys init` then `keys show`, fresh volume | a fingerprint | **PASS** |
| 1.2 | `keys show` in a **new container** | the same fingerprint | **PASS with `ASHLAR_KEY_DIR`**; without it, **no key at all** and gate decisions go unsigned |
| 1.3 | repeat after `docker rm` | same fingerprint | **PASS** |
| 1.4 | `init`, `verify`, re-run in a new container | the ledger is still there | **PASS** — ledger `000001` → `000002`, "2 signed entries · chain intact" |

**Do not proceed to L3 until 1.2 passes.** A node that forgets its identity makes every downstream
result meaningless.

### L2 — Does the repo's own suite pass on your machine?

| # | Command | Measured on `pc` |
|---|---|---|
| 2.1 | `bash scripts/run-cert-gate.sh` | **PASS 194/194** — but see the worktree note below |
| 2.2 | `make test-prime-time` | not run locally; green in CI |
| 2.3 | `make kernel-coverage-gate` | not run locally; green in CI |
| 2.4 | `dotnet test Ashlar.sln` | **ANSWERED — it builds and is effectively green.** ~7,336 tests across 27 assemblies, ~25 min. Two reds, both explained: 10 GameDirector failures were the roll-forward artifact above, and one `NullModelServingBackend_supports_full_lifecycle` failure passed 3/3 in isolation — a load-sensitive flake |

> **cert-gate and nested worktrees.** Before #406, `TestOwnershipConventionTests` walked every
> `*.csproj` under the repo and skipped only `bin/` and `obj/`, so any `git worktree` inside the
> tree — including the ones this repo's own tooling creates under `.claude/worktrees/` — turned
> the only required check on master red locally while CI stayed green. Fixed in **#406**.

### L3 — Does the product loop work end to end?

```bash
bash scripts/e2e-loop.sh
```

*Measured*: **119/119, exit 0.** Scenarios 94–104 are a full two-node co-production round trip, so
L4's *logic* is already proven; what is untested is two physical machines.

### L4 — Two machines

Everything below was **measured with two containers holding separate key volumes and a shared
mesh volume** — a faithful model of two machines, short of being two machines.

| # | Check | Result |
|---|---|---|
| 4.1 | B imports A's package | **works** |
| 4.2 | B is shown A's fingerprint | **no** — Phase 1 |
| 4.3 | B refuses a package from an unknown third key | **no discrimination**, but it is *held*, not applied; sealed refuses outright |
| 4.4 | both boxes have distinct identities | **works** with per-node `ASHLAR_KEY_DIR`. Note every "two-node" scenario in `scripts/e2e-loop.sh` shares **one** key dir and runs both nodes as the same actor |

Also measured: a node survived container destruction between receiving a package and deciding on
it, and still admitted it afterwards with content intact.

---

## 5. Failures that are expected — do not debug these

| Symptom | Why | Fix lives in |
|---|---|---|
| Daemon creates no agents | No agent config ships in the image | give it `--config` (§2b) |
| `Connection refused (127.0.0.1:11434)` in the daemon | The model probe defaults to the container's own loopback | set `ASHLAR_OLLAMA_BASE_URL` |
| Key fingerprint gone after `docker rm` | `ASHLAR_KEY_DIR` unset in the image | Phase 1 (workaround in §2) |
| `Access to the path '.../ashlar.yaml' is denied` | Volume root is `root:root`; app is uid 1654 | seed an app-owned subdirectory (§2) |
| `Access to the path '/app/C:' is denied` | Git Bash rewrote a container path | `export MSYS_NO_PATHCONV=1` |
| `pkg pull` says `not an ashlar project` | The verb needs `ashlar.yaml` + `ashlar.policy.yaml` in `--path` | `ashlar init` the node's dir |
| `pkg pull` says `the peer store is empty` | Also what it says when the peer is **off** — indistinguishable | Phase 4 |
| No `ashlar` command on the host | There is no host wrapper; the installers print `docker run` examples | Phase 1 |
| `/health` returns OK on a broken node | Hardcoded literal, by design, today | Phase 1 |

**Fixed since the first draft — if you still see these, that is news:** cert-gate red because of a
nested worktree (#406); `pkg pull` exiting 0 while refusing a forged package (#407); the daemon
discarding barrier audit events and ignoring `Ashlar:Audit:Sinks` entirely (#407); the daemon
burying its own output in repeated HTTP connect stack traces (#407).

---

## 6. What to record

Keep a file per machine. It is the input to `CLOSING-PLAN.md` Phase 1 and to decisions 1, 2 and 5.

```
box:            pc
arch:           x86_64            (MINGW64_NT-10.0-26200, Docker Desktop, linux containers)
docker:         29.7.2
image digest:   sha256:baaa9b5d85cbaad2850c70326ab8762e2fe2c5eee713708cd52d3cf30a0c99f7
L0 result:      pass  (0.3 exits 1 by design inside the image)
L1.2 result:    yes, with ASHLAR_KEY_DIR — no key at all without it
L2.1 cert-gate: pass 194/194
L2.4 sln tests: ~7,336 tests, effectively green
L3 e2e-loop:    119 pass / 0 fail
autonomy:       daemon executes agents on interval with --config; identity survives compose down
```

The most valuable outstanding data point is **L0.2 on the MacBook** — the arm64 artifact on real
hardware rather than under emulation. It is one command and it closes `CLOSING-PLAN.md` Phase 0.

The second is the Mac's **RAM headroom under the daemon**, which feeds owner decision 12
(per-node inference vs one box serving the LAN). Note that decision was framed around a
Raspberry Pi's headroom; an M-series Mac's is a different order of magnitude, so the answer may
not be the one the plan anticipates.

---

## 7. Where to go next

- `CLOSING-PLAN.md` — the eight phases. Read **Where to stop** first.
- `STATE-2026-08-27.md` — the project-wide audit this all came from.
- `DECISION-identity-split.md` — why there is one operator identity and what it costs.
- `ci/cert-gate-assertions.md` — what the one required check enforces, before you consider muting it.

If L0.2 fails on the MacBook, say so before doing anything else in the plan. Phases 1 through 5
assume that image runs, and no arm64 *machine* has ever run it — only QEMU has.

`CLOSING-PLAN.md` also carries Pi-specific reasoning in Phases 4–8 and in owner decisions 3, 6, 7
and 12 that has **not** been revised; its "The fleet, corrected" section lists exactly which.
