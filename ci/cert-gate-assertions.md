# What `cert-gate` carries

**Read this before you switch off a required check.**

`cert-gate` is **one of five required status checks on `master`** (the others are `build-core`, `shell-lint`, `lychee (README + docs)`, and `Readiness summary`; see `CONTRIBUTING.md` and `docs/GitHubBranchProtection.md`). It runs on every pull request with **no path filter**, and it selects tests by substring from `scripts/cert-gate-config.sh:6`:

```
FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Certification
|FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Adaptation.GenerationSafety
|FullyQualifiedName~AstMutationEngineTests
```

Because it is the only thing that blocks a merge, it is where every merge-blocking convention in
this repository has to live. That concentration is deliberate and it is also a risk: **one
branch-protection toggle disables all of it at once**, and this repository's demonstrated response
to a red gate is a mute — eight workflows were deleted or de-triggered after going red
(`docs/CiGateInventory.md`).

This file exists so that a returning owner, facing a red required check with the toggle one click
away, can see what they would be turning off.

## Conventions that block a merge

| Assertion | Lives in | What breaks without it |
|---|---|---|
| **Every test project is registered.** Every csproj containing `Microsoft.NET.Test.Sdk` has exactly one row in `ci/test-ownership.tsv`; every row points at a project that still exists; no `UNOWNED` row is past its expiry. | `TestOwnershipConventionTests` | A test project no gate runs re-enters the repo silently. This is exactly how `Ashlar.Commercial.Tests.Fleet.Host` failed for ten days across twenty consecutive runs with no pull request ever running it. |
| **Composition order cannot decide durability.** `AddCertificationGate` and `AddCertificationInfrastructure` resolve to a durable store in either composition order; a host-supplied signer survives. | `CertificationStoreCompositionTests` | Admissions silently revert to in-memory, and for the CLI — a fresh process per invocation — nothing certified can ever be admitted again. |
| **The schema floor refuses a downgraded record.** A forged record with a rewritten gate name verifies at floor 0 and is refused at floor 2. | `SchemaVersionFloorTests` | A record can claim to have passed gates it never ran: the legacy payload lane leaves `Gate`, `GatesPassed`, `Inputs`, `Proposer`, `Attempts` and `Ed25519PublicKey` outside the signed bytes. |
| **The dev key is loud.** A signer falling back to the committed HMAC constant warns, and never logs the key itself. | `CertificationRecordSignerDevKeyTests` | Production admissions run on a key anyone with the source can forge, with nothing on the record saying so. |
| **v1/v2 record bytes are frozen.** The legacy payload is byte-pinned. | `TrustLoopRecordSchemaTests` | Every signature ever written becomes unverifiable, silently. |
| **No eighth unbounded appender.** Every production `File.AppendAllText` / `AppendAllLines` / `AppendText` sits in a frozen allowlist of the seven that exist; a stale allowlist row fails too, so the inventory can only shrink honestly. | `AppendOnlyWriterConventionTests` | An appender on a path that never rotates grows until the disk does not — weeks later, on an unattended node, long after the change that caused it. `CLOSING-PLAN.md` Phase 5 bounds these at the write path; this stops the count going up in the meantime. |
| **The write floor holds under every spelling.** A mediated write (self-extend or imported package) that resolves onto a governance or build-integrity path — the project contract, the operator policy, anything under `.ashlar/` `.git/` `.github/` `.vscode/` `.devcontainer/` `scripts/`, any `Directory.Build.*` / `Directory.Packages.props` / `nuget.config` / `global.json` / `Makefile` / `*.csproj` / `*.sln` — is refused for the whole batch, whatever the spelling (`./`, `..`, backslash, case). | `ForgeApplierGovernanceTests` | Governance was checked on the raw target against a first-segment denylist, so `./ashlar.policy.yaml` and `a/../.ashlar/x` slipped through onto the operator policy and gate state, and the floor was three entries wide — an admitted `Directory.Build.targets` ran on the receiver's next `dotnet build`, outside the loader, the gate and the registry. |
| **Every mediated writer shares one floor.** Forge apply, package import AND shared-adaptation adopt all route through `MediatedWritePath.Refuse`; a peer's adaptation can no longer escape the repo (`../../x`), write a governance/build path, or run through a symlink, and the whole entry is refused before any file is written. | `MediatedWritePathTests`, `SharedAdaptationGovernanceTests` | `FileBasedSharedAdaptationStore.ValidateAndAdoptAsync` used to `Path.Combine(repoRoot, path)` attacker-controlled files with no containment and no floor, then `dotnet build` them — an RCE the forge floor never covered because it was a second, divergent writer. |
| **An untrusted signer's package is refused before it parks.** A node admits an imported package only if its sealer is trusted — its own operator key (self-trust), the project policy's `selfExtend.trustedSigners`, or the operator's local peers keychain (`keys trust <fp>`); the rotation-retention dir `trusted/` is never an admission input. | `PackageTrustGateTests` (+ `pkg-import-refuses-untrusted-signer` in scripts/e2e-loop.sh) | Without it a self-extending node auto-admits and applies ANY signer's code within budget, and wiring the allowlist onto `trusted/` would let a rotated-away stolen key re-authorize itself. |
| **THE node stays deployable.** `deploy/node.yml` keeps a restart policy, a named state volume, a digest pin, log rotation, a `working_dir` on the volume for the gate store, and every `ASHLAR_*` dir under the state dir — and exactly ONE compose file in the repository may claim the state volume. | `NodeUnitConventionTests` | The node file regresses toward a lab stack: `docker rm` starts erasing identity, packages or the entire trust history again, and nothing notices until the machine you are not standing at comes back different. |

The gate also carries the certification gate's own teeth, the hot-swap host, the adversarial
campaigns, the analyzer fence, sandbox-escape tests and the dogfood suites — 31 files in
`src/Ashlar.Tests.Infrastructure/Tests/Certification/`. The table above is only the
*conventions*: assertions about how the repository is allowed to be shaped, which have no other
home and which nothing else would catch.

## Rules

1. **A convention that must block a merge goes in the
   `Ashlar.Tests.Infrastructure.Tests.Certification` namespace.** Anywhere else and it is
   advisory, whatever its author intended. Moving or renaming that namespace silently disarms
   every row above.
2. **Add a row here in the same pull request that adds the assertion.** A convention nobody can
   find is a convention that gets deleted the first time it is inconvenient.
3. **Keep them hermetic.** These run on every PR. Pure file reads — no build, no network, no SDK,
   no clock dependence beyond a dated allowlist. A convention test that flakes will be muted, and
   it will take the whole required check with it.
4. **Never set an expiry that lands inside a few months.** `NoUnownedRow_IsPastItsExpiry` compares
   against `DateTime.UtcNow` inside the required check, so a passed expiry blocks *every* pull
   request in the repository on a date chosen months earlier. See the header of
   `ci/test-ownership.tsv`; the first version of that file made this mistake with seven rows at
   once and it was defused before it tripped.
5. **If this gate is red, fix it or date it.** Muting it removes every row above simultaneously.
   That is the failure mode this file is written against.
