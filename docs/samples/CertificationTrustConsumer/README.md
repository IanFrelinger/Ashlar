# Packed-artifact certification conformance

Two small consumers that answer a question the test suite cannot: **what does an outside consumer
get from the package?**

Every certification test in this repository compiles `src/`. A consumer compiles a `.nupkg`. Those
are different artifacts, and nothing in CI compared them — so "`Default` and `Strict` are
fail-closed" was a claim about the source tree, asserted nowhere about the thing people install.

A packaged library can diverge from its sources in ways no source-level test can see: a release cut
from a different commit, a pack script that selects different assets, a target framework whose
asset a consumer silently binds instead of the one the tests exercise. Any of those moves the
answer without turning a single test red.

## The two programs

| | `CertificationPresetProbe` | `CertificationTrustConsumer` |
|---|---|---|
| Binds the package by | reflection | the typed API |
| Runs against | **any** version, including surfaces that predate today's | the version built from this commit |
| Target frameworks | `net8.0` | `net8.0` and `net10.0` |
| Verdict | reports, never fails | **fails the build** |
| Question | "what does version X actually carry?" | "is the artifact we are about to publish fail-closed?" |

The probe exists because the gate cannot answer the first question. A typed consumer will not
compile at all against a version whose API surface differs from today's, and a compile error is a
refusal but not a reading. Reflection turns a missing member into a line of output.

## Running them

```bash
# The gate, against a package freshly packed from this working tree
bash scripts/verify-packed-certification-trust-behavior.sh

# The gate, against what nuget.org actually serves
ASHLAR_CONFORMANCE_SOURCE=nuget.org ASHLAR_CONFORMANCE_VERSION=1.2.3 \
  bash scripts/verify-packed-certification-trust-behavior.sh

# The gate, against a pre-packed CI artifact
ASHLAR_CONFORMANCE_FEED=/path/to/nupkgs ASHLAR_CONFORMANCE_VERSION=1.2.3 \
  bash scripts/verify-packed-certification-trust-behavior.sh

# The probe
bash scripts/probe-published-certification-presets.sh 1.2.3
```

## What the gate asserts

Eighteen assertions per target framework, in three groups.

- **Preset surface** — `Default` and `Strict` each require an Ed25519 signature and a trust-loop
  schema floor; `Strict` additionally requires the gate-emitted artifact and the certifier identity;
  `Legacy` reports itself open.
- **Refusals a consumer depends on** — an HMAC-only record minted with the package's own committed
  development key is refused by `Default`, by `Strict`, and by the overload that names no preset at
  all; a schema downgrade is refused; an unknown schema version is refused; a content-hash mismatch
  is refused; a stripped HMAC signature is refused.
- **Controls** — the asset the consumer actually bound matches its own target framework, the HMAC
  fallback still reports itself through `UsesDevKey()`, and one record is **trusted** under
  `Legacy`.

## Where the gate runs

- **`security-gate.yml` / `packed-certification-trust`** — every pull request that touches the
  library, the samples, the pack scripts, the root build props, `VERSION`, or the release workflow.
  A packaging change alone can move the answer without touching `src/`, which is why the trigger is
  wider than the library.
- **`reusable-release-nuget.yml`** — twice. Once against the packed feed **before any push**, so a
  fail-open package fails the release rather than reaching nuget.org; once against nuget.org
  afterwards, because "it was good when it left" and "that is what arrived" are different claims.
- **`nuget-consumer-verify.yml`** — on demand, for a published version.

## Design notes, in case this is ever rewritten

**`PackageReference`, never `ProjectReference`.** A `ProjectReference` would re-answer the question
the repository already answers, against the artifact that was never in doubt.

**The harness asserts on the transcript, not the exit code.** A script that checks only the exit
status reports success for a consumer that never ran — this repository has shipped that mistake
before, in the first version of the trim/AOT lane. `scripts/verify-packed-certification-trust-behavior.sh`
therefore requires `RESULT=OK`, an exact `ASSERTIONS_RUN` count, and every assertion name
individually. The name list in the script is deliberate duplication: it is what makes a *deleted*
assertion a failure rather than a shorter green run.

**There is a positive control.** Every interesting assertion is a refusal, so
`positive-control-legacy-trusts-valid-hmac-record` exists to catch a package that refuses
everything — which would otherwise pass the whole suite.

**`Legacy` is asserted to stay open.** If the three presets were ever swapped, every refusal
assertion would still pass while the preset named in production had become the permissive one.

**The bound asset is read from `deps.json`, not `Assembly.Location`.** The build copies the selected
asset into the app's output folder, so by the time the process runs, every asset looks alike.
`deps.json` records the `lib/<tfm>/` path NuGet actually chose — which matters because a `net6.0` or
`net7.0` consumer silently binds the `netstandard2.0` asset, where Ed25519 cannot be evaluated at
all.

**`netstandard2.0` is not covered here.** That leg has no runtime of its own to run on; it is
measured by `scripts/ns20-canonical-bytes-probe.sh` under Mono.
