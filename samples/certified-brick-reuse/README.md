# Certified brick reuse (Project A certifies, Project B trusts)

Reference for **Phase 2: cross-project reuse** in [`docs/certification-evidence.md`](../../docs/certification-evidence.md). It shows the smallest honest shape of "ship a certified brick to another project": the consumer verifies a **content-bound, signed certification record** and runs the brick untouched — it never re-certifies, and it references **no generator and no gate**.

## What is here

| Path | Role |
|------|------|
| `Ashlar.Certified.DamageResolver/DamageResolverBrick.cs` | The certified brick: `finalDamage = max(0, (isCrit ? baseDamage * critMultiplierPercent / 100 : baseDamage) - armor)`. The whole brick is this one file — it names its base type `Ashlar.Core.Domain.Bricks.Brick` in full, because a certificate binds one content hash over one text and the gate refuses any compile item outside the brick directory. |
| `Ashlar.Certified.DamageResolver/Ashlar.Certified.DamageResolver.csproj` | Packable artifact (`PackageId` `Ashlar.Certified.DamageResolver`, `0.1.0`); packs `certification-record.json` under `content/certified-brick/`. References only `Ashlar.Brick.Contracts`, at nuget.org's `0.1.1`, so it certifies from a plain checkout with no local feed. |
| `Ashlar.Certified.DamageResolver/damage-resolver.witness.json` | The six witness cases the certification gate replays (e.g. `50 / 100% / armor 10 / no crit -> 40`). Not packed. |
| `Ashlar.Certified.DamageResolver/certification-record.json` | The signed record Project A produced: `contentHash` = SHA-256 of the brick source, the mutation summary (`totalMutants`, `killedMutants`, `survivingMutantIds` — read the current numbers from the file; they move whenever the mutation catalog gains an operator, and `escapeRate` must be `0`), HMAC `signature`. This is the sidecar Project B verifies. |
| `ProjectB/ProjectB.csproj` | The consumer. `PackageReference`s only `Ashlar.Brick.Contracts` (`0.1.1`, matching the brick), `Ashlar.Certification.Contracts` and the packed `Ashlar.Certified.DamageResolver` (both `0.1.0`, from the local feed, so the verifier is the same code as the signer that produced the record). No `Ashlar.Authoring`: the program never used it, and its dependency graph reaches nuget.org's `0.1.1` hosting packages, which fails the restore with a package downgrade (`NU1605`). |
| `ProjectB/Program.cs` | `CertificationTrustVerifier.Verify(record, source)`; on TRUSTED runs the brick with `baseDamage 50, crit 100%, armor 10, no crit` and prints `TRUSTED finalDamage=40`. Exit `2` (`UNTRUSTED: <code>`) on signature or content-hash failure; exit `2` with a usage line when the arguments are missing, unreadable, or swapped (`<path-to-DamageResolverBrick.cs>` must come first and end in `.cs`, the record second and end in `.json`). |

Trust model (v0): same-owner reuse via a shared dev HMAC key (`ASHLAR_CERT_DEV_HMAC_KEY`, default dev key when unset). Cross-organization trust would need PKI and is out of scope.

## How it is exercised (CI)

The **cert-gate** workflow (`.github/workflows/cert-gate.yml` -> `scripts/run-cert-gate.sh`) runs two things here. `src/Ashlar.Tests.Infrastructure/Tests/Certification/ShippedSampleCertificationTests.cs` drives the real loader and gate against the checked-in `Ashlar.Certified.DamageResolver/` directory and asserts it ADMITS — so the tracked sample cannot silently stop certifying. `CrossProjectReuseTests.cs` certifies the damage resolver in-process, then plays Project B against the result:

| Test | Proves |
|------|--------|
| `HonestCertifiedBrick_ProjectB_TrustsAndRunsUntouched` | `ProjectB.csproj` has no gate/generator references; verifier says TRUSTED; brick returns `finalDamage == 40`. |
| `TamperedBrick_ProjectB_RejectsContentHashMismatch` | Changing `Math.Max(0, raw - armor)` -> `content-hash-mismatch`. |
| `ForgedSignature_ProjectB_Rejects` | Altered signature -> `signature-invalid`. |

Reproduce locally from the repository root (Docker not required):

```bash
bash scripts/run-cert-gate.sh
```

## Running Project B by hand (local feed)

Prerequisites: .NET SDK per `global.json`. Project B pins `Ashlar.Certification.Contracts` and the certified artifact at `0.1.0`, which predates the first nuget.org release (`0.1.1`), so those come from a **local folder feed** that `scripts/pack-certified-brick-reuse.sh` builds at `artifacts/certified-brick-feed` (override with `ASHLAR_CERTIFIED_REUSE_FEED`; version with `ASHLAR_CERTIFIED_REUSE_VERSION`); `Ashlar.Brick.Contracts` comes from nuget.org at `0.1.1`. The script packs `Ashlar.Brick.Contracts` and `Ashlar.Certification.Contracts`, writes a `NuGet.Config` for the feed + nuget.org, re-runs certification through `tools/Ashlar.ExportCertifiedBrick` (rewriting `certification-record.json`; the tool honours the `ASHLAR_CERT_NUGET_CONFIG` the script exports, and stops the script with the gate's refusal message — exit `4` — rather than a stack trace if the brick cannot be loaded), and finally packs the certified artifact:

```bash
bash scripts/pack-certified-brick-reuse.sh
```

Then restore and run Project B against that feed, passing the brick source and the record it must verify:

```bash
dotnet restore samples/certified-brick-reuse/ProjectB/ProjectB.csproj \
  --configfile artifacts/certified-brick-feed/NuGet.Config
dotnet run --no-restore --project samples/certified-brick-reuse/ProjectB/ProjectB.csproj -- \
  samples/certified-brick-reuse/Ashlar.Certified.DamageResolver/DamageResolverBrick.cs \
  samples/certified-brick-reuse/Ashlar.Certified.DamageResolver/certification-record.json
```

Expected output: `TRUSTED finalDamage=40` (exit code `0`). Edit one character of `DamageResolverBrick.cs` and rerun to see `UNTRUSTED: content-hash-mismatch` (exit code `2`).

Note: the tracked `certification-record.json` is the last export the maintainers committed; re-running the pack script re-signs it with your local key. The content hash is over the exact bytes of `DamageResolverBrick.cs`, which `.gitattributes` pins to LF on every checkout (`eol=lf`) so that `core.autocrlf=true` on Windows leaves it alone; if a tool rewrites the file to CRLF anyway, Project B reports `UNTRUSTED: content-hash-mismatch` until the file is LF again.
