# Certification investigations: issues #493 and #462

Inspected baseline: `5e0beeae3b037508f23b475d98d13cc7e64c25de` (`master`, checked
2026-11-01). Both [#493](https://github.com/IanFrelinger/Ashlar/issues/493) and
[#462](https://github.com/IanFrelinger/Ashlar/issues/462) remained open when read.
An open report is not evidence that its original exploit still works.

## #493: certified source versus compiled program

The current loader does **not** evaluate or build author MSBuild. It reads one root
C# source, compiles that source with the gate's closed reference/options set, inspects
its IL, and activates those emitted bytes. The required consumer binding remains
verification of the source **and artifact bytes**, followed by activation of those
same verified bytes. A host compiling or loading its own artifact is outside that
binding; checking a source-only certificate cannot establish which program it ran.

`CompiledSourceSetRegressionTests` constructs disposable author projects for these
report classes:

| Input | Regression assertion on the current loader |
|---|---|
| `Compile Include="Payload.cstxt"` | Payload type absent from emitted PE; primary source is the judged source. |
| `Sub/obj/Payload.cs` and `Sub/bin/Payload.cs` | Nested payload absent from emitted PE. |
| `Directory.Build.props` contributing source and package inputs | Refused by the build-surface fence before reading the missing witness file. |
| `Compile Remove="Brick.cs"` plus non-C# replacement | Gate emits the primary judged source rather than the author's replacement. |

The excluded payload contains a harmless throwing module initializer. Each successful
loader result is inspected with Cecil and its MVID compared with the activated
brick's MVID. These tests measure the loader/artifact boundary; they do not assert an
end-to-end ADMIT, receiver certification, safe arbitrary MSBuild evaluation, or
isolation of the default in-process execution path. Ancestor sidecars cannot affect
this compiler because it never evaluates the project; arbitrary external builds are
not covered. Existing `GateEmittedArtifactTests` and
`JudgedArtifactIsTheExecutedArtifactTests` exercise the gate and verifier/activator
composition separately.

## #462: witness kinds and transport

The baseline source already rejects the report's original CLR integer-versus-double,
string and boolean examples, including `2` versus `2.4`/`1.5`, `1` versus `"1"`/`true`,
and `0` versus `false`. Residual gaps remain in that source:

- Its final string conversion equates a floating output `3.14` and the string
  `"3.14"`.
- Two `JsonElement` operands bypass the integer/boolean checks, and the correctness
  and mutant judges unwrap different sides. Wrong JSON kinds can match, while an
  honest integer/boolean observation can be treated as a killed mutant.
- Session determinism stringifies CLR output values, conflating a number in one
  repeat and its string representation in another.
- The project witness loader converts aggregate JSON to text, letting an object or
  array witness match a brick returning that JSON as a string.

The change makes both judges call one comparer. It unwraps primitive JSON values on
both sides; integral widths retain the existing equality contract; other CLR values
must have the same type and equal value. Session determinism retains JSON value
encoding rather than converting CLR scalars to display strings. At this shared comparer
boundary, JSON aggregates retain their kind and use conservative raw-text equality.
There is no floating tolerance, arbitrary-precision number guarantee, transport-level
CLR numeric-width preservation, or structural coercion between JSON aggregates and
CLR objects.
This is an integrator choice consistent with the existing exact-witness contract,
not a new owner decision. Existing witness files and certificate formats are unchanged;
a candidate previously relying on the covered cross-kind coercions now fails its witness.
Earlier signed records are not revoked, and signature verification does not rerun
witnesses. A consumer needing these stronger comparison semantics must recertify
with the updated certifier; updating only its verifier cannot strengthen an old
record's underlying assertions.

The project loader also preserves aggregate JSON as a cloned `JsonElement` instead of
its raw string. `AggregateWitnessLoaderRegressionTests` runs a loaded witness against
an emitted brick that returns the object's/array's JSON as a string; correctness must
reject it. This closes the same kind-loss at the authoring boundary. Raw-text equality
for aggregates is conservative: property-order/whitespace differences can cause a
rejection. This is not a complete audit of every witness value shape.

## Reproduction and run record

Never execute adversarial author code on a host carrying production credentials.
The repository helper [`scripts/certification/reproduce-493-462.sh`](../../scripts/certification/reproduce-493-462.sh)
requires a disposable devtest clone and disconnected container networking. It saves
the current comparer, runner and loader sources. It temporarily reads the two
comparer/runner baseline versions with `git show`, runs the focused scalar regressions,
restores those files and separately reproduces the aggregate-loader failure with
only the original loader. It restores all current sources even on failure, runs the
combined focused tests again, then runs the configured full certification filter on
`net10.0`. The actual `scripts/run-cert-gate.sh` uses `net8.0` and its own zero/skip
guards; that CI reproduction is a separate validation. A build failure or absence of
failing tests is refused as a baseline reproduction. Unique TRXs and zero-test guards
prevent an empty filter from being counted as a reproduction; logs retain real test
counts and failure names.

With dependencies restored in the disposable `/repo` clone, no credentials passed
into it, `/src-mirror` mounted read-only and a writable evidence directory:

```bash
# From the host: disconnect this specific disposable container before the probe.
docker network disconnect bridge ashlar-validation
# Then run in that container, never against /src-mirror:
docker exec -e ASHLAR_CERT_DISPOSABLE=1 ashlar-validation \
  bash /repo/scripts/certification/reproduce-493-462.sh /evidence/certification
```

The helper's focused filter is:

```text
FullyQualifiedName~WitnessKindRegressionTests|FullyQualifiedName~CompiledSourceSetRegressionTests|FullyQualifiedName~WitnessValueComparerTests|FullyQualifiedName~AggregateWitnessLoaderRegressionTests
```

Environment preparation used Linux containers with SDK 10.0.403 and the genuine
ASP.NET Core/.NET 8 and 10 runtimes. After VFS disk exhaustion, validation used a
smaller SDK-plus-runtime image and one persistent disposable container rather than
concurrent copies of the larger devcontainer image. Restore used a read-only public combined CA
mount and TLS verification. No production or operator credentials were configured.
The initial concurrent cold restores collided in the shared NuGet cache; a serial
`dotnet restore src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -p:NuGetAudit=false --nologo -v minimal`
completed successfully. Initial test containers then failed to start because VFS
image copies exhausted the environment disk. Neither setup failure is a test pass.

The isolated runs executed on 2026-11-02 in the network-disconnected disposable
clone, at the inspected baseline plus the working implementation and new tests:

| Run | Actual result | Scope |
|---|---|---|
| Scalar/source-set tests with baseline comparer/runner | 7 failed, 22 passed, 29 total; no skips | Confirmed residual #462 scalar/transport and determinism failures. All five #493 source-set cases and existing 11 CLR comparer cases passed. |
| Same tests with updated comparer/runner | 29 passed, zero failures/skips | Covered scalar changes and #493 loader/emitted-artifact regressions. |
| Configured full certification filter, `net10.0` | 3,752 passed, zero failures/skips; 10m7s | Before the later aggregate-loader update. This is supplemental coverage, not the exact `net8.0` CI job. |
| Aggregate loader tests with original loader and fixed comparer | 2 failed, zero passed/skipped | Both object and array witnesses wrongly accepted a brick returning their JSON as a string. |
| Same aggregate loader tests with `JsonElement.Clone()` preservation | 2 passed, zero failures/skips | Real loader → emitted brick → correctness-witness path rejects those wrong kinds. |
| Exact `scripts/run-cert-gate.sh`, `net8.0` | Pending | Must cover the final aggregate update and run its own zero/skip guards before being claimed as passed. |

The scalar failures were JSON number/string and boolean/string matches, honest JSON
integer and boolean mutant comparisons, floating/string comparison, and number/string
session determinism. The original report's CLR integer coercions did not reproduce.
For #493, the tested loader/emitted-artifact boundary rejects or excludes the reported
source-set tricks; this does not establish a complete fix for arbitrary external
consumers.

Logs and unique TRXs are retained in `/evidence/certification` and
`/evidence/certification-aggregate` in the validation container, mounted at
`/workspace/scratch/live-evidence/` on the host. These local records are not an
independently retained audit anchor. To repeat only the aggregate red/green leg after
a scalar reproduction:

```bash
docker exec -e ASHLAR_CERT_DISPOSABLE=1 ashlar-validation \
  bash /repo/scripts/certification/reproduce-493-462.sh --aggregate-only \
  /evidence/certification-aggregate
```

That mode swaps only the loader's baseline source, restores it and exits after its
focused fixed run. Neither issue has been closed, and these results do not revise
autonomy or marketing holds. The exact `net8.0` gate remains pending at this record
update; post-commit mutation evidence follows.


## Post-commit mutation checks

On 2026-11-02, a fresh disposable clone of committed
`2e0d237068af156e94612a8f2e89c56aea788196` ran both mutations in one slim devtest
container, using `scripts/test-in-container.sh --ref 2e0d23706` without `--dirty`.
The container had networking disabled, a 6 GiB memory limit, no production or
operator credentials, read-only source/CA mounts, and a mounted output directory.
Default container capabilities were retained so root could read the restrictive
source-directory permissions. A warm-cache offline restore completed first.

| Applied mutation, exactly once | Real failing assertions | Restored run |
|---|---|---|
| Comparer same-type/value check → invariant string conversion | 3 failed, 12 passed, 15 total | 15 passed, zero failures/skips |
| Loader `element.Clone()` → `element.GetRawText()` | 2 failed, zero passed/skipped | 2 passed, zero failures/skips |

Each applied diff was retained before its red run. After each mutation, `git checkout`
restored the source from the committed ref and `git status --porcelain` was asserted
empty before the green run. Both empty porcelain files, four TRXs, their nonzero-test
guard output and logs are retained in `/workspace/scratch/cert-postcommit/`. The driver
exited zero only after both red assertion failures, clean restorations and green runs.
These are measured mutation kills, not build errors or empty test selections.

The exact session invocation was:

```bash
ASHLAR_DEVTEST_IMAGE=ashlar-devtest:slim \
PATH=/workspace/scratch/cert-mutation-docker:$PATH \
  bash scripts/test-in-container.sh --ref 2e0d23706 -- 'bash /evidence/run.sh'
```

The retained `run.sh`, Docker wrapper and applied diffs record the substitutions and
commands. These local artifacts cover the two mutations above; the earlier baseline
red/green additionally covers the session determinism change. They do not substitute
for the separately recorded complete certification gate or independent security review.
