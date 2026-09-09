# Project Continuity — Ashlar

**Written 2026-08-24, immediately before a full machine wipe (all apps + Claude data removed).**
This file is the complete resume point. A fresh Claude Code session pointed at this repo should
read this first; everything below is self-contained. The GitHub remote is the source of truth —
nothing of value lived only on the wiped machine except the operator signing key (see §6).

---

## 1. What this product is

**Ashlar** is a development
tool + runtime for building and deploying **governed, cryptographically certified AI/agentic
applications** — CLI and GUI, targeting AWS, Azure, and native platforms, with an optional
self-extending runtime.

The stress-tested one-liner (see `docs/CompetitivePositioning.md` for the full adversarial
analysis of what's unique vs. commodity):

> Ashlar is the first system where an AI application's permission to run, to change itself, and
> to be shared is one mechanism: a fail-closed admission gate whose signed verdicts are the
> tamper-evident history. Everyone else documents governance; Ashlar enforces it — and the
> enforcement can prove itself.

Product vocabulary (used consistently in code, output, and docs): verification checks are
**courses**; the passing set is **the wall**; admitting a change is **seating the stone**; the
admission point is **the gate**; the operator-owned envelope is `ashlar.policy.yaml` (the one
file the running app can never change).

## 2. What is DONE and merged to master

The product loop, end to end, all CI-green (each PR adversarially reviewed; the reviews found
and fixed real bugs at every step):

| Area | PRs | What works |
|---|---|---|
| Product loop | #362–#382 | `ashlar init → verify → gates → run` (mock provider = zero-setup offline) |
| M1 enforcement | #384, #385 | propose → **HOLD** → apply; `gates --admit` applies parked forge writes, refuse rejects them |
| Signing arc | #383, #386–#390 | Ed25519 operator key (`ashlar keys init/show`), signed gate verdicts (S-1 fail-closed, S-2 presence-activated), signed hash-chained **instance ledger**, `verify` prints **CERTIFIED · signed ed25519:… · ledger #N** |
| Doctor | #391 | `ashlar doctor` project-readiness ladder (NOT CERTIFIED → READY TO CERTIFY → CERTIFIED / BLOCKED) |
| Mesh slice 1 | #392 | **Certified packages** (`.ashpkg`): `pkg export/import/show` — admitted extension + course evidence + signed verdict travel as one sealed file; receiver verifies intrinsically (no keys, no network) and admits through **its own gate** |
| Mesh slice 2 | #393 | **Mesh share**: `pkg publish/pull` over a filesystem store; idempotent re-pulls; shared `PackageImport` service |
| Mesh slice 3 | #394 | **Agentic exe**: `ashlar export native` — portable self-proving bundle (app + verify-then-run launcher); provenance course now **binds the ledger head's Subject to the current documents**, so a tampered app fails verify (exit 65) even for a keyless downloader |

Key security invariants now enforced (each pinned by tests):
- A package seal must be signed by the **same key** that signed the admission (`SealSigner == Record.Signer`) — blocks genuine-record + swapped-files forgery.
- `ForgeApplier` refuses **governance paths** (`ashlar.yaml`, `ashlar.policy.yaml`, anything under `.ashlar/`) and reparse-point traversal for the whole batch — an admitted brick can never rewrite the envelope that governs it.
- `GateStore` write/decide returns exactly what was persisted (signature included).
- Bundles never ship `.ashlar/keys`, `.ashlar/forge`, or lock/tmp files (SPEC-006: the private key never travels).

Verification state at wipe: kernel suite ~394 tests, BackgroundAgents 519, CLI 18+, e2e-loop
**106/106** behavioral scenarios (`scripts/e2e-loop.sh`, real binaries, 3-OS in CI).

## 3. WIP: branch `claude/export-cloud` (mesh slice 4 — pushed, NOT merged)

`ashlar export aws|azure` — one-command cloud deploy bundles: `app/` + Dockerfile layered on
`ghcr.io/ianfrelinger/nexo-cli:latest` + verify-then-run `entrypoint.sh` + per-target deploy
script (AWS: ECR push + ECS Fargate one-shot task; Azure: `az acr build` + ACI one-shot).
Unit 14/14, e2e 106/106 green at commit `ac83f891`.

**Before PR'ing it, fix the two likely-real findings** (an adversarial review of the generated
deploy scripts was in flight when the session died; these were its obvious targets):
1. `deploy-azure.sh` passes the ACR admin password on the `az container create` command line
   (process-listing/shell-history exposure). Prefer managed identity, `--registry-password`
   via env/stdin, or token-based auth.
2. `deploy-aws.sh` interpolates the request string into the task-definition JSON
   (`CMD="[\"$REQUEST\"]"`) — quotes/backslashes in a request break or inject JSON. Escape it
   properly (e.g. via `python3 -c 'import json,sys;…'` or jq) or refuse suspicious characters.
Also worth a pass: unquoted expansions, `sleep 10` IAM-propagation hack, idempotency of the
create steps, and whether ECS/ACI surface a verify-failure (exit 65) visibly to the operator.

## 4. NOT started: mesh slice 5 (co-production) — design settled

Goal: multiple coding agents across nodes co-produce verified extensions.
1. Kernel `MeshStore` in `Ashlar.Manifest.Packaging`: `Resolve(explicitDir?)` (env
   `ASHLAR_MESH_DIR`/published, else `~/.ashlar/mesh/published`) + `Publish(storeDir, json)`
   (verify via `ExtensionPackaging.TryOpen`, content-hash dedupe naming). Refactor
   `PkgCommand.PublishAsync` to use it.
2. `ashlar pkg share --id <id> [--store]` = export (seal) + publish in one verb.
3. `SelfExtendAdmissionBridge`: after `ApplyAll` in the Admitted branch, **auto-share** the
   admitted extension when opted in. Use explicit optional params (autoShare flag + meshDir)
   defaulting from env `ASHLAR_MESH_AUTOSHARE=1` — tests inject params, never mutate env
   (xunit parallelism). Best-effort: a share failure logs + annotates the outcome string,
   never fails the cycle.
4. Co-production e2e: node A admits + shares v1 of a file → node B pulls (held→admit), agent
   proposes v2 building on v1, admits, shares back → node A pulls v2, admits → A's file holds
   v2. Both gates exercised in both directions.

## 5. Roadmap beyond slice 5 (from the accepted vision)

- **Decouple the product CLI from `Ashlar.Tests.*` project references** (task was chipped):
  they block self-contained single-file publish (`NETSDK1191`), which the true one-file
  agentic exe needs. Until then `export native` stages the bundle + `RUNTIME.md`.
- Setup/deploy **doctor --fix** (one command from zero to CERTIFIED), local hostable model for
  setup/deploy debugging; **Studio v0** (GUI half); fine-tuned models + cloud premium upsell.
- v2 trust: org trust roots / identity binding (self-carried keys are TOFU — the known
  weakness), revocation, anti-rollback ledger anchor (tail truncation is v1's documented gap).
- Stakeholder demo artifact (interactive, "governance as masonry" design) is published on
  claude.ai (account-tied, survives the wipe):
  `https://claude.ai/code/artifact/050c6ffc-135b-4c96-a717-5a338d69e1f4`

## 6. Machine re-setup after the wipe

1. Install: git, Docker Desktop, Claude Code, `gh` (auth to github.com/IanFrelinger).
2. `git clone https://github.com/IanFrelinger/Ashlar.git` (any path; sessions used
   `C:\Users\icfre\Downloads\Nexo-Framework`).
3. **Everything builds/tests inside the dev container** — host runs may be blocked by Windows
   Application Control (that is WHY the container lane exists):
   `bash scripts/handoff/devbox.sh "dotnet test src/Ashlar.Tests.Kernel/Ashlar.Tests.Kernel.csproj"`
   and `bash scripts/handoff/devbox.sh "bash scripts/e2e-loop.sh"`.
4. **Operator key is gone** (it lived in `~/.ashlar/keys`, wiped, by design never in the repo).
   Run `ashlar keys init` to mint a new identity. Old certified artifacts/ledgers still verify
   intrinsically (records carry their public keys). Re-certifying a project with the new key is
   just `ashlar verify`; rotation semantics keep old pubs under `trusted/`.

## 7. Working conventions (hard-won; keep them)

- **PR flow to master:** any PR touching `application/` needs `[coordinated-integration]` +
  rationale in the PR BODY (layer-boundary gate; docs: `docs/contributing/Branch-layer-rules.md`).
  Five checks are *required*: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, and `Readiness summary`. The `uat (tiers 0-2, 4-10)` job flakes on
  `doc-command-verbatim` — rerun failed jobs once (`gh run rerun <id> --failed`) before
  suspecting the change.
- **Merge discipline:** arm a background watcher (`gh pr checks N --watch --fail-fast` then
  squash-merge on green); background watchers only merge — never let them touch the working
  tree. Foreground syncs only.
- **Review discipline:** every substantive slice gets an adversarial review (multi-lens
  find → independently refute each finding). These found real criticals in *every* slice —
  do not skip them.
- **Style:** fail-closed everywhere; refusals teach (say why + what to do); "unsigned" is said
  honestly rather than papered over; tests use `[Fact(Timeout=…)]` only for ProdStyle/E2E
  classes in the Infrastructure assembly; normalize CRLF in test fixtures
  (`.ReplaceLineEndings("\n")`); e2e claims grep real CLI output with `NO_COLOR=1`.
- Known-benign: Stryker mutation testing gives false survivors here (don't gate on it);
  `docs/bricks/unknown.md` sometimes appears as test drift — `git checkout --` it.

## 8. Open threads / maintainer decisions parked

- Dependabot PRs #359–#361 (Avalonia, xunit bumps) — unreviewed.
- External renames still pending a human call: GitHub repo name (`Nexo` → `Ashlar`), GHCR image
  names, `<Authors>` string, license split (`docs/OpenCoreBoundary.md`).
- SPEC-006 v1 accepted (`docs/specs/SPEC-006-keys-and-signing.md`); S-4 (retire the dev-HMAC
  certification signer) not yet executed.
