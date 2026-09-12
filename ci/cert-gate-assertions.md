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
| **Every strictness field of every preset is pinned by name.** `Default` and `Strict` each require an Ed25519 signature and the trust-loop schema floor; `Strict` also requires the gate-emitted artifact and the certifier identity; `Legacy` is open on all four. | `SchemaVersionFloorTests.Presets_PinEveryStrictnessField` | `IsStrict` is an OR over five independent flags, so a preset can keep reporting `IsStrict == true` while the signature requirement is gone and verification falls back to HMAC under the committed key. An aggregate is not a pin. This covers the SOURCE tree only — what a consumer restores from a package is a different artifact, gated by `scripts/verify-packed-certification-trust-behavior.sh` in `security-gate` and in the release workflow. |
| **The dev key is loud.** A signer falling back to the committed HMAC constant warns, and never logs the key itself. | `CertificationRecordSignerDevKeyTests` | Production admissions run on a key anyone with the source can forge, with nothing on the record saying so. |
| **v1/v2 record bytes are frozen.** Both lanes are byte-pinned against a checked-in golden corpus (`canonical-payloads.golden.json`), which `scripts/ns20-canonical-bytes-probe.sh` (the netstandard2.0 asset under Mono) and `scripts/portability/net9-probe.sh` (the net8.0 asset on the 9.0 runtime) also check against, and a payload that does not carry its lane’s exact property-name sequence is refused rather than signed. | `TrustLoopRecordSchemaTests`, `CanonicalPayloadGoldenTests`, `CanonicalPayloadGuardTests` | Every signature ever written becomes unverifiable, silently. |
| **The other two canonical-bytes producers are frozen too.** The composition admission payload and the certified transition entry hash are byte-pinned against `composition-payloads.golden.json` and `transition-entry-hashes.golden.json`, and written field by field rather than serialized, so a trimmed or ahead-of-time publish emits the same bytes — measured by `scripts/portability/trim-aot-canonical-bytes-probe.sh`, which publishes and runs four such configurations and requires each published binary to still refuse a tampered signature, a broken content binding, an unknown schema version, a repeated proposer parameter key and a non-finite double. That probe now runs in CI as the `trim-aot-canonical-bytes` matrix of the advisory `Runtime Portability Gate` — advisory, so it is not one of the merge-blocking assertions in this table. | `CompositionCanonicalPayloadGoldenTests`, `CertifiedTransitionEntryHashGoldenTests`, `CanonicalPayloadEmitterTests` | Every composition certificate and every attested state log stops verifying, silently, on a host that publishes differently from CI. |
| **Every target reaches the same verdict for the same record.** A record carrying an Ed25519 signature that does not verify is refused under every options instance (Legacy included), and the netstandard2.0 asset — which cannot evaluate the signature — refuses the same corpus records (`ed25519-signature-unverifiable`) instead of trusting them; HMAC-only records stay trusted on both. | `VerifierParityTests` (net8.0 side; `scripts/ns20-canonical-bytes-probe.sh` measures the netstandard2.0 side under Mono against the same corpus) | A downlevel consumer trusts a record every other target refuses, and a certificate's verdict depends on which framework happened to load the verifier. |
| **An unknown schema version is an error, not a guess.** Only null (v1) and 2 (v2) select a canonical payload lane; any other version is refused at mint time and on every verification path (`schema-version-unknown` from the trust verifier), and the null/2 bytes are unchanged against the golden corpus. | `UnknownSchemaVersionTests`, `CanonicalPayloadGoldenTests` | A record stamped with a version no code has ever minted is serialized in a shape chosen by guesswork and clears every schema floor at or below its number. |
| **No eighth unbounded appender.** Every production `File.AppendAllText` / `AppendAllLines` / `AppendText` sits in a frozen allowlist of the seven that exist; a stale allowlist row fails too, so the inventory can only shrink honestly. | `AppendOnlyWriterConventionTests` | An appender on a path that never rotates grows until the disk does not — weeks later, on an unattended node, long after the change that caused it. `CLOSING-PLAN.md` Phase 5 bounds these at the write path; this stops the count going up in the meantime. |
| **Every store opens LiteDB in Shared mode.** Every production file constructing a `LiteDatabase` (or `LiteRepository` / `LiteEngine` / `SharedEngine`) has a row in a frozen twelve-file inventory, every row still constructs one, and each of them composes its connection string through `LiteDbConnectionString.ForSharedAccess` instead of a hand-rolled `Filename=` string. | `LiteDbSharedModeConventionTests` | LiteDB's default is `Connection=Direct`, an exclusive file lock held for the LIFETIME of an instance that every store opens once per call — so two overlapping calls race. On Windows the loser throws `IOException`; on Linux the second open is not refused at all and the writers interleave, so the loss is SILENT and CI never sees it. Measured at 20 trials x 8 threads x 250 inserts, Direct persisted 322-4,042 of 40,000 writes per store and Shared persisted 40,000. The two previous rounds of this bug (#586, #591) each landed with one store converted and the rest left behind; without this, the next store to arrive opens Direct and nothing says so. |
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
