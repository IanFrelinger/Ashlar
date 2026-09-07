# Handoff — game-layer extraction and the Ashlar rename

**Revision 3. THE RENAME IS APPLIED AND GREEN** — commit `a0609ebe`.

```
verify-rename.sh                 PASS   content, paths, .sln refs, ProjectRefs all clean
dotnet build Ashlar.Kernel.sln   0 Warning(s), 0 Error(s)
scripts/run-cert-gate.sh         169/169 passed
```

4,000 files rewritten by content, 228 files renamed, 99 directories renamed;
25,155 insertions and 25,155 deletions — exactly symmetric, as a pure token
substitution should be.

Revision 1 was written in a cloud session with **no .NET SDK**, so nothing in it
had been compiled. Revision 2 recorded what compiling revealed: three load-bearing
claims were wrong, two of them damaging. Revision 3 records what *actually running
`--apply`* revealed — see §2.5. The short version: a dry run exercises the counting
path, `--apply` exercises the mutation path, and every remaining bug lived there.

---

## 1. Decisions (unchanged)

| Decision | Answer |
|---|---|
| **Rename scope** | **Full rename.** `Nexo` → `Ashlar` across namespaces, assemblies, packages, directories and filenames. |
| **Game code destination** | **Its own repository**, consuming the kernel as a package. |

Reference material from the originating session — both written pre-rename, both
still say "Nexo" throughout:

- **The Nexo Dossier** — source map, usage modes, positioning, and an audited list
  of where documentation and code disagree.
  <https://claude.ai/code/artifact/a1b2328c-05ac-4fad-b7c5-283ee07ebf06>
- **Nexo Cleanup Plan** — seven waves with file-level work items.
  <https://claude.ai/code/artifact/bcfebaf4-6b03-42df-8e8d-0ca2c6424689>

---

## 2. What revision 1 got wrong

### 2.1 "Tier 1 cannot break a kernel build" — false. It breaks it.

Revision 1 classified the Playtest tree and `TileMapRenderTool.cs` as Tier 1:
*self-contained game code with no inbound references from kernel production code.*

Applied in a throwaway worktree and built exactly as revision 1 documented:

```
Build FAILED.  14 Error(s)

AgentFactory.cs(7,33):  CS0234  namespace 'Playtest' does not exist in 'Nexo.Orchestration.Agents'
AgentFactory.cs(12,26): CS0234  namespace 'Playtest' does not exist in 'Nexo.Orchestration'
ToolsDevTests.cs:       CS0246  'TileMapRenderTool' could not be found   (x10)
```

`AgentFactory.CreateAgent` calls `IsPlaytestDomain(spec.Domain)` and then
`CreatePlaytestAgent(spec)`, which switches on `"aiplayer"` / `"playtest"` /
`"balance"` / `"feedback"` and news up `AIPlayerAgent`, `BalanceAnalyzerAgent` and
`FeedbackSynthesizerAgent`, resolving `IGameRunner` and `ITelemetryStore` from
`Nexo.Orchestration.Playtest.Ports`.

That is the **same hardcoded-switch coupling revision 1 itself described for
Tier 3**. Playtest was misfiled. It is Tier 3.

There is also a reference the documented green-check *cannot see*:
`RepoFsToolboxFactory.cs:48` and `:95` do `tools.Register(new TileMapRenderTool())`.
`Nexo.BackgroundAgents.HostRunners` is not a member of `Nexo.Kernel.sln`. Building
that project directly against an applied extraction fails too.

**Tier 1 is empty.** Every candidate has an inbound reference from code that stays.

The extract-game-layer handoff script (removed with the game vertical) ran an inbound-reference check first
and **refuses `--apply`** while any blocker stands. Current output:

```
BLOCKED  Nexo.Orchestration.Agents.Playtest  -> AgentFactory.cs, OrchestrationFactoryAndCommunicationTests.cs
BLOCKED  Nexo.Orchestration.Playtest         -> AgentFactory.cs, OrchestrationFactoryAndCommunicationTests.cs
BLOCKED  TileMapRenderTool                   -> RepoFsToolboxFactory.cs, TileMapRenderToolTests.cs, ToolsDevTests.cs
```

The one Tier 1 claim that survived: **`AddPlaytestServices` has zero callers, and
its body is entirely commented out.** It is an empty no-op returning `services`.
Deleting it is safe.

### 2.2 The rename script would have destroyed a git worktree

`rename-to-ashlar.sh` walked the tree with `find`, pruning only `./.git` and
`./_handoff`. On a working checkout that also swept:

| Swept | Files |
|---|---|
| `.claude/worktrees/<name>/` — a parked **git worktree**, 1.5 GB | 4,429 |
| `bin/` + `obj/` build output | 3,896 |
| The actual source tree | 4,192 |
| **Total** | **11,854** |

The worktree is listed in `.git/info/exclude`, so `git status` never shows it and
**`git checkout .` / `git reset --hard` cannot restore it.** Pass 1 also rewrites
that worktree's `.git` gitfile, which holds the absolute path
`.../Nexo-Framework/.git/worktrees/...` — breaking the link permanently.

Fixed by scoping from **`git ls-files`** instead of `find`. Tracked files are
exactly the set that needs renaming; build output regenerates, and a worktree is a
separate checkout that must be renamed on its own branch if at all. The script now
also refuses `--apply` against a dirty tree.

Verified dry run after the fix — matching revision 1's intended figures exactly,
with zero worktree or build-output hits:

```
files with Nexo/NEXO/nexo in content: 4000     # 4005 on this branch, see below
files renamed: 228
directories renamed: 99
```

The filename and directory counts match revision 1 to the file. The content count
is 4,000 measured on `master` and **4,005 on this branch** — the difference is the
five handoff files themselves, which mention `Nexo` throughout. Not a discrepancy;
just remember the number moves with whatever docs you add.

### 2.3 `verify-rename.sh` would have failed a correct rename

Two bugs, both fixed:

1. It scanned with `grep -r .` and `find`, excluding only `.git` and `_handoff`.
   Since the rename is correctly scoped to tracked files, those scans still see
   `Nexo` in build output and in the parked worktree — reporting **FAIL on a
   rename that was entirely correct.** Now scoped to `git ls-files`, with untracked
   residue reported as INFO rather than failure.
2. Its solution-integrity check resolved project paths from the repo root, but
   paths inside a `.sln` are relative to *that solution's* directory. It reported
   3 phantom `MISSING` entries for the then-named `Nexo.Application.sln` **on a
   completely untouched tree.** Now resolved relative to each solution.

### 2.5 What only `--apply` could reveal

Six more bugs, none of which a dry run could have found. The dry run exercises the
*counting* path; `--apply` exercises the *mutation* path. Recorded because the same
traps apply to any large mechanical refactor, not just this one.

| # | Bug | Consequence |
|---|---|---|
| 1 | Pass 1 rewrote the rename's **own tooling**. `verify-rename.sh` had its search tokens replaced and came out grepping for `Ashlar`. | The verifier reported the entire correctly-renamed tree as residue. Docs became `Nexo -> Ashlar` → `Ashlar -> Ashlar`. |
| 2 | `mv A B` **nests** when `B` exists, rather than renaming. | 88 directories shaped `src/Ashlar.Foo/Nexo.Foo`. Nothing errored; the tree was quietly wrong. |
| 3 | `git clean -fd` does **not** remove gitignored files. | `bin/`/`obj/` kept `Ashlar.*` directory shells alive through a revert, which caused #2. Needs `-x`. |
| 4 | Pass 3 renames directories with plain `mv`, which stages nothing. | `git ls-files` reported 3,571 stale paths, **zero of which existed on disk**, and the verifier read the index rather than the tree. |
| 5 | Pass 1 rewrites `.gitignore`, but pass 3 only renames directories derived from *tracked* paths. `.nexo/` has no tracked files. | The ignore pattern moved to `.ashlar/`; the directory did not. The private agent workspace became un-ignored and `git add -A` staged 213 of its files, whose contents the verifier then printed to stdout. |
| 6 | `git add -A` stages untracked local state. `.claude/settings.local.json` is 15 KB with 58 `nexo` lines, ignored on the host **only** by the user's global excludes, which root in the container lacks. | Untracked, so pass 1 never rewrote it; `-A` staged it; the verifier failed on 58 tokens the rename never touched — aborting the run under `set -e` before the build or gate. |

Fixes: exclude `scripts/handoff/` and `docs/handoff/`; a pre-flight that refuses
`--apply` when any `Ashlar`-named directory already exists, plus per-`mv`
destination guards; move `.nexo/` and `NEXO_AGENT_NOTES.md` on disk to match the
rewritten patterns; and stage with `git add -u` plus an explicit add of only the
directories pass 3 created, never `git add -A`.

Bugs 5 and 6 were found by a 41-agent adversarial audit of the scripts, run after
bug 2 made it clear the tooling needed more than incremental patching. Both were in
code I had written an hour earlier *as a fix* for bug 4.

**One trap that was mine, not the script's:** pass 2 uses `git mv`, which *stages*.
A later `git commit` that looked like a two-file change actually carried 230 files,
because `git commit` commits the whole index regardless of what you just `git add`ed.
Check `git diff --cached --name-only` before committing anything near this script.

### 2.4 The ordering constraint was unnecessary

Revision 1 insisted extraction must precede the rename, because the rename would
invalidate the extraction script's `Nexo.*` paths. It does not: the extraction
script is a **tracked file**, so pass 1 rewrites its internal paths while pass 3
renames the matching directories. They stay consistent.

Given the extraction is now blocked pending a refactor, **do the rename first.**

---

## 3. Prerequisites

- **.NET SDK 10.x** — `global.json` pins the 10.0 band. Confirmed working here at
  `10.0.400`.
- **Git**, and **bash** (Git Bash is fine — the scripts are POSIX-ish bash).
- No provider credentials. The kernel build and cert gate run offline.

### Tests must run in the dev container — the handoff devbox script (removed)

`dotnet build Nexo.Kernel.sln` is **green** on the host — 0 warnings, 0 errors, 80s.
But `bash scripts/run-cert-gate.sh` **cannot run on the host at all**:

```
Catastrophic failure: System.IO.FileLoadException: Could not load file or assembly
'...Nexo.Tests.Infrastructure.dll'. An Application Control policy has blocked this file. (0x800711C7)
```

Zero tests discovered, zero run. This is **Windows Application Control blocking the
freshly-built unsigned test assembly** — a machine policy, not a code defect. It is
not specific to the cert gate: **26 scripts under `scripts/` invoke `dotnet test`**
and every one of them hits it.

The policy does not apply inside a Linux container, so run those through:

```bash
bash scripts/handoff/devbox.sh bash scripts/run-cert-gate.sh
bash scripts/handoff/devbox.sh dotnet build Nexo.Kernel.sln
bash scripts/handoff/devbox.sh                                  # interactive shell
```

**Measured: 169/169 tests pass in the container** — 2.3 min cold, 1.4 min on a warm
NuGet volume. That is the only green cert gate anyone has produced on this machine.

`devbox.sh` uses the same image as `.devcontainer/devcontainer.json`
(`mcr.microsoft.com/devcontainers/dotnet:10.0-noble`) and follows the conventions
already established by `scripts/Verify-DevContainer.ps1`:

- **Runs as root**, not `vscode`. Bind-mounted Windows files arrive with host-side
  ownership; running as root avoids a UID mismatch when writing `bin/` and `obj/`.
- **`DOTNET_ROLL_FORWARD=LatestMajor`** — load-bearing, not decoration. The image
  ships only the 10.0 runtime and the cert gate runs `-f net8.0`. CI installs 8.0.x
  separately; this does the equivalent.
- **Payload is base64'd** into the container so quoting survives Git Bash.
- **NuGet lives in a named volume**, so restores are cached between runs.

Note the payload runs under `set -euo pipefail`: a non-zero exit anywhere aborts
the rest. Correct for gates, surprising for compound commands — guard with
`|| true` when you deliberately expect a failure.

#### Run the file-heavy passes in the container too — they are ~9× faster there

Strictly speaking, Application Control only blocks *loading freshly-built
assemblies*; it does not touch `sed`, `git mv` or `grep`, so `rename-to-ashlar.sh`
and `verify-rename.sh` *can* run on the host. Do not. They are dramatically slower
there.

Same script, same tree, `rename-to-ashlar.sh` dry run, two runs each:

| Environment | Run 1 | Run 2 |
|---|---|---|
| Host (Windows) | 495s | 514s |
| Dev container (incl. Docker start) | **57s** | **57s** |

Roughly **9× faster in the container**, and reproducible. The intuition that a
Windows↔Linux bind mount would make the container slower is wrong here: the
dominant cost is Windows per-file I/O, with real-time AV scanning on every one of
the 4,005 files. Linux reading the same mount pays far less.

Results are **identical in both environments** — 4005 / 228 / 99, and the same 3
extraction blockers — so this is a pure speed win with no behavioural difference.

**Conclusion: run everything through `devbox.sh`.** There is no step in this
handoff that is better off on the host.

### A pre-existing failure, unrelated to this work

`dotnet build Nexo.sln` fails at *restore*, on a clean untouched branch:

```
GameDirector.Host.csproj : error NU1201: Project Nexo.API is not compatible with
net8.0. Project Nexo.API supports: net10.0
```

`GameDirector.Host` targets net8.0 but references net10.0-only `Nexo.API`. This
predates the rename work. It matters here only because it means you cannot use
`Nexo.sln` as the broad green-check — build affected projects directly instead.

---

## 4. The sequence

**Everything runs through `devbox.sh`** — required for anything that executes build
output, and ~9× faster for everything else. See §3.

### ~~Step 0 — baseline~~ DONE

### ~~Step 1 — the rename~~ DONE — commit `a0609ebe`

Kept for reference. To re-verify at any time:

```bash
bash scripts/handoff/devbox.sh '
  bash scripts/handoff/verify-rename.sh          # PASS
  dotnet build Ashlar.Kernel.sln                 # 0 Warning(s), 0 Error(s)
  bash scripts/run-cert-gate.sh                  # 169/169
'
```

The rename is idempotent and the script now refuses to run against a tree that
already has `Ashlar`-named directories, so re-running `--apply` is a no-op that
fails loudly rather than a second rename.

### ~~Step 2 — the provider refactor~~ DONE — `602ba229`, `5fbea7a7`, `8b5a91c5`, `2af96fb8`

`extract-game-layer.sh` reports **0 blockers**. Four seams were introduced:

| Seam | Replaces |
|---|---|
| `IToolSource` / `extraTools` (already existed) | `RepoFsToolboxFactory` hardcoding `TileMapRenderTool` |
| `IDomainAgentProvider` + `IAgentCreationContext` | `AgentFactory`'s hardcoded playtest and game-domain switch arms |
| `IDomainPatternProvider` | `DomainRecognizer`'s hardcoded Combat/Economy/Gameplay regex tables |
| `AddGameDomain()` | — the one call an application installing the game layer makes |

The kernel now knows: assets, planning, infrastructure, security, generic. Everything
else arrives from a package.

**Two things that bit, recorded so they do not bite again:**

1. **`0 blockers` is necessary, not sufficient.** The check greps namespaces and type
   names, so it proves the kernel still *compiles*. It cannot prove it still *behaves*,
   because the coupling being removed is keyed on **domain strings**.
   `OrchestrationRuntimeSpecTests` spawned `Domain = "Combat"` as a vehicle for testing
   runtime-spec directives, named no game type, passed every grep — and failed at run
   time, because "combat" now falls through to `GenericAgent`, which takes no model at
   all. Always follow the check with the test suites, not just a build.
2. **The AI domain is split on purpose.** `DomainRecognizer` keeps the general-purpose AI
   vocabulary (agent, neural, learning, decision) because Ashlar is an agent framework;
   the game half (npc, pathfinding, steering) moves. But `AIAgent` itself moves, because
   its system prompt is *"an expert AI/ML engineer specializing in game AI"*. Recognising
   a domain and having a specialist for it are separate concerns.

**Next up is Step 3**, once the Tier 2 question below is settled.

Run `verify-rename.sh` **before** the build: two of its four checks catch failures
that surface at runtime rather than compile time.

Commit this alone. A 4,000-file diff mixed with anything else is unreviewable.

### Step 2 — the provider refactor (unblocks the extraction)

Introduce the two registries, so `AgentFactory` and `DomainRecognizer` stop
hardcoding game domains:

```csharp
public interface IDomainAgentProvider
{
    bool Handles(string domain);
    BaseAgent Create(AgentSpawnSpec spec);
}

public interface IDomainPatternProvider
{
    IReadOnlyDictionary<string, IReadOnlyList<Regex>> Patterns { get; }
}
```

`AgentFactory` resolves `IEnumerable<IDomainAgentProvider>` and asks each in turn;
`DomainRecognizer` composes its pattern table from the registered providers.
`RepoFsToolboxFactory` needs the same treatment one level down — take its tool set
from a registry instead of newing up concrete tools.

`InfrastructureAgent` and `SecurityAgent` are **not** game-specific. They stay in
the kernel and register as providers like everything else.

Re-run `extract-game-layer.sh` until the inbound-reference check reports `ok` on
all three symbols. That check is the definition of done for this step.

### ~~Step 3 — the extraction~~ DONE — `302a5e02`

(The extraction script and `_handoff/game-layer` itself were removed in the
2026-08-31 native-responsibility slim; both are preserved on the archive branch
`archive/verticals-2026-08-31`.)

Then delete `AddPlaytestServices` (empty no-op, zero callers), drop the moved files
from any `.csproj` that lists them explicitly, and verify:

```bash
bash scripts/handoff/devbox.sh '
  dotnet build Ashlar.Kernel.sln
  dotnet build src/Ashlar.BackgroundAgents.HostRunners/Ashlar.BackgroundAgents.HostRunners.csproj
  dotnet build src/Ashlar.Tests.BackgroundAgents/Ashlar.Tests.BackgroundAgents.csproj
  bash scripts/run-cert-gate.sh
'
```

The middle two are **not** in `Ashlar.Kernel.sln` and are exactly where revision 1's
breakage hid.

### Step 4 — make the game layer a repository

`_handoff/game-layer/README.md` has the steps. `GameLayer` is a placeholder — pick
the real name first.

**Superseded 2026-09-01:** `Ashlar.*` **is** on nuget.org at `0.1.1`, so a sibling
repository can consume it by `PackageReference` with no feed setup — see
`docs/ConsumingFromNuGet.md`. A `ProjectReference` into a sibling checkout, or a local
folder feed, remains an option for an unreleased version. `consumer-template/` carries
the pins.

---

## 5. Tier map (corrected)

| Tier | Contents | Status |
|---|---|---|
| ~~1~~ | — | **empty.** Everything previously here had inbound references. |
| ~~3~~ | Playtest tree, `TileMapRenderTool`, `DomainRecognizer` game patterns, Combat/Economy/Gameplay/AI agents | **DONE** — behind `IDomainAgentProvider` / `IDomainPatternProvider` / `IToolSource`. 0 blockers. |
| ~~2~~ | `Agents/Assets/` — the three concrete agents only | **DONE, split.** Ports (`IImageGenerator`, `IAudioGenerator`, `IModel3DGenerator`, `IAssetStorage`), request/result types, `BaseAssetAgent` and the `Generated*Asset` models **stay in the kernel** — none carry a word of game vocabulary. Only `ImageAssetAgent`, `AudioAssetAgent` and `Model3DAssetAgent` move, because their own prompts ask for "a high-quality game asset" / "high-quality game audio". |

**All tiers are now separated. `extract-game-layer.sh` reports 0 blockers across four
symbols, and the game layer is one `AddGameDomain()` call away from being a package.**

What the kernel kept, and why it is not an oversight: the *capability* to generate assets,
recognise domains, and run agents is general. Only the game-flavoured *framings* of those
capabilities moved. The clearest illustration is AI — `DomainRecognizer` still recognises
"agent", "neural", "decision", but `AIAgent` left, because its prompt reads "specializing
in game AI". Recognising a domain and having a specialist for it are separate concerns.

**Certification fixtures — leave alone.** `DamageResolverSources.cs`,
`HealthApplierSources.cs` and `DamageResolverBrickConstraints.cs` are game-*themed*
but are fixtures for the certification gate, cited by the evidence ledger in
`docs/certification-evidence.md`. Moving them risks the one CI-proven subsystem for
a cosmetic gain. Rename later if the theming bothers you; do not relocate.

---

## 6. Gotchas

### 6.0 External identifiers the rename must NOT touch

A token substitution cannot tell a *name of a thing in the code* from a *name of a
thing that lives outside the repository*. The second kind must survive the rename
unchanged, because renaming a reference does not rename the thing it points at — it
just points at nothing. CI caught four of these **after** the rename was applied and
merged onto the branch; every one had to be reverted by hand:

| Identifier | Points at | What renaming it broke |
|---|---|---|
| `github.com/IanFrelinger/Nexo` | the GitHub repository (still named Nexo) | 61 links across 29 files 404'd, incl. `RepositoryUrl` in 11 `.csproj` — the link checker failed |
| `nexo.provenance.v1` | a key inside a **signed certificate** | the signature covers the key; renaming it forged the cert — `unit-tests` failed |
| `nexo.portal.prefs.v1` | a browser `localStorage` key | would silently discard every user's saved portal preferences |
| `ghcr.io/ianfrelinger/nexo-cli` | a **published container image** on ghcr | `docker run` pulled a non-existent image — `doctor` smoke test warned, `uat` failed |

The rule: **an identifier that addresses a resource outside this git repo keeps its
old name until that resource is deliberately renamed, in a separate coordinated
step.** The repo, the published image, and any signed/persisted wire keys all fall
under this. If you ever run a rename again — including giving the extracted game
layer its own name — grep for these classes first: `github.com/`, `ghcr.io/`,
`localStorage`, any `*.vN` wire key, and anything base64 next to a "signature"
field. `verify-rename.sh` does not catch them; only CI did.

| Gotcha | Detail |
|---|---|
| **Parked git worktree** | `.claude/worktrees/` holds a full 1.5 GB checkout, git-excluded and unrecoverable by `reset --hard`. Both scripts now skip it. If you add tooling that walks the tree, skip it there too. |
| **Two clones** | `Downloads/Nexo` and `Downloads/Nexo-Framework` share one origin. `Nexo` has substantial uncommitted work. Do this in `Nexo-Framework`, or commit that work first. |
| **Env var break** | `NEXO_ALLOW_MOCK`, `NEXO_TRUST_ENABLED`, `NEXO_DEPLOYMENT_PROFILE` → `ASHLAR_*`. Shell profiles, `.env` files and CI secrets are untracked — the script cannot reach them. Consider a temporary compat shim that reads the old name and warns. |
| **Config key break** | `Nexo:Security:ExposureProfile` → `Ashlar:Security:ExposureProfile`, and every other `Nexo:*` key. |
| **State directory** | `.nexo/` → `.ashlar/`, matching the brand materials. Existing local state will not be found afterwards. |
| **Container images** | `ghcr.io/ianfrelinger/nexo-cli` → `ashlar-cli`. Does not exist under the new name until published; the audit could not confirm the old one exists either. |
| **Git remote / repo URLs** | The script rewrote `github.com/IanFrelinger/Nexo` to `.../Ashlar` in 61 places across 29 files, including `RepositoryUrl` in eleven `.csproj`. That URL names the REPOSITORY, which is still Nexo, so every link 404d and CI's link checker failed. Reverted by hand. Rewrite them only when the GitHub repo is actually renamed, in the same commit. `.git/config` is untouched either way. |
| **`_handoff/` is skipped** | Both scripts exclude it, so the extracted game layer keeps its own identity. *(Corrected 2026-08-27: `_handoff/` is **tracked** — `git ls-files _handoff` returns 56 files; the extracted game layer and the readiness handoff set landed on master in #399. Both rename scripts exclude it by explicit path, not because git does. It also sits outside the docs link checker and `docs/DocsIndex.md`.)* |
| **PublicAPI files** | `PublicAPI.Unshipped.txt` holds fully-qualified type names and is rewritten by pass 1. All entries are `Unshipped`, so no baseline breaks. |

---

## 7. Fix these while you are in there

From the audit. Cheap during this work, genuinely worth doing.

1. **The write-allowlist escape.** `PathAllowlist.Approve` inspects only the `path`
   argument; `RepoFsWriteTool` takes a separate model-supplied `root` and does
   `Path.Combine(args.root, args.path)` with no validation. `root="/etc"` plus
   `path="src/x.cs"` passes every policy and writes outside the repo. Fix by
   **removing `root` from all 14 tool schemas in `src/Nexo.Tools.Dev/`** and
   sourcing it from the sandbox declaration. Add the hostile-root test —
   `Tests/Adaptation/AdversarialScopeEscapeTests` covers hostile `path` only.
   Worth doing **before** the rename: small, independently valuable, and its diff
   stays readable in the vocabulary you already know.
2. **Two lying package descriptions.** `src/Nexo.Lite/Nexo.Lite.csproj:14` and
   `src/Nexo.Sdk/Nexo.Sdk.csproj:14` describe software that does not exist. This is
   package metadata and would ship to a feed verbatim.
3. **Six silent no-ops.** `BehaviorExecutor.cs:443` and `:501`,
   `ImplementationSelector.cs:50`, both pipeline stage adapters, the orchestration
   fallback agent, and `EnableParallel` (`LoopOptions.cs:16` /
   `ParallelLoopKernel.cs:34`). All fail by quietly doing nothing. Make them throw.
4. **`cert-gate-config.sh` documents the wrong test count.** Its header carries a
   per-class breakdown under the claim *"must match `--list-tests` output"*. That
   breakdown sums to **99**. The gate actually runs **169**. The zero-test guard is
   fine — it derives the expected count at runtime — but the comment has drifted by
   70 tests and reads as authoritative. Either regenerate it or delete it; a stale
   inventory of the one CI-proven subsystem is worse than none.

---

## 8. Checklist

- [x] Tests can run — via `scripts/handoff/devbox.sh`, **169/169 green in ~2 min**
- [x] Baseline: kernel build green (0/0); cert gate green in container
- [x] Rename applied; `verify-rename.sh` prints **PASS**
- [x] `dotnet build Ashlar.Kernel.sln` green (0/0); cert gate still **169/169**
- [x] Rename committed alone — `a0609ebe`, 4,205 files, no tooling or local state in it
- [x] Local setup swept. On *this* machine most of the warned-about state did not exist:
      no `NEXO_*` env vars (user or machine), no shell profiles referencing it, no
      `.env` files, no `.nexo/` state dir, no dotnet user-secrets. Two things did and
      were fixed: a stale user **PATH** entry pointing at an empty
      `%TEMP%\nexo-dotnet-home\.dotnet\tools`, and 83 dead `Nexo.*` paths in
      `.claude/settings.local.json` (untracked, so the rename could not reach it).
      Still open, deliberately: the git remote (see below) and the repo folder name.
- [ ] GitHub repo renamed, then `git remote set-url origin <new>` — **your call**; the
      local remote is only correct to change after the rename. GitHub redirects, so
      pushing keeps working either way.
- [ ] Repo folder `Nexo-Framework` — left alone on purpose. The parked worktree's
      gitfile hardcodes the absolute path `C:/Users/icfre/Downloads/Nexo-Framework/.git/worktrees/...`,
      so renaming the folder breaks that link. Fix the worktree first if you want it.
- [x] `IDomainAgentProvider` / `IDomainPatternProvider` / tool registry landed
- [x] `extract-game-layer.sh` reports **0 blockers** — all tiers separated
- [x] Extraction applied; `AddPlaytestServices` deleted; kernel **and** HostRunners build
- [x] Extraction committed alone — 48 renames, 0 deletions, 0 content changes
- [ ] `_handoff/game-layer/` given a real name and moved to its own repo — **the only step left**. See its README: it has no .csproj, its namespaces still say `Ashlar.Orchestration.*`, and `GameToolSource.cs` has never been compiled.
