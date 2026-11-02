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

The change makes both judges call one comparer. It unwraps primitive JSON values on
both sides; integral widths retain the existing equality contract; other CLR values
must have the same type and equal value. Session determinism retains JSON value
encoding rather than converting CLR scalars to display strings. At this shared comparer
boundary, JSON aggregates retain their kind and use conservative raw-text equality. There is no floating tolerance, arbitrary-precision
number guarantee, transport-level CLR numeric-width preservation, or structural coercion between JSON aggregates and CLR objects.
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
guards; that CI reproduction is a separate validation. A build failure or absence of failing tests is refused as a
baseline reproduction. Unique TRXs and zero-test guards prevent an empty filter from being counted as a
reproduction; logs retain real test counts and failure names.

With dependencies restored in the disposable `/repo` clone, no credentials passed
into it, `/src-mirror` mounted read-only and a writable evidence directory:

```bash
# From the host: disconnect this specific disposable container before the probe.
docker network disconnect bridge ashlar-devtest-session
# Then run in that container, never against /src-mirror:
docker exec -e ASHLAR_CERT_DISPOSABLE=1 ashlar-devtest-session \
  bash /repo/scripts/certification/reproduce-493-462.sh /evidence/certification
```

The helper's focused filter is:

```text
FullyQualifiedName~WitnessKindRegressionTests|FullyQualifiedName~CompiledSourceSetRegressionTests|FullyQualifiedName~WitnessValueComparerTests|FullyQualifiedName~AggregateWitnessLoaderRegressionTests
```

Environment preparation used the Linux devtest image, SDK 10.0.403 and the genuine
ASP.NET Core/.NET 8 and 10 runtimes. Restore used a read-only public combined CA
mount and TLS verification. No production or operator credentials were configured.
The initial concurrent cold restores collided in the shared NuGet cache; a serial
`dotnet restore src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -p:NuGetAudit=false --nologo -v minimal`
completed successfully. Initial test containers then failed to start because VFS
image copies exhausted the environment disk. Neither setup failure is a test pass.

The first isolated scalar/source-set baseline run executed on 2026-11-02 in the
network-disconnected disposable clone: **7 failed, 22 passed, 29 total**. All five
#493 source-set cases passed, as did the existing 11 CLR comparer tests. The failures
confirmed the residual scalar JSON/string and determinism behavior described above;
the report's original CLR integer coercions did not reproduce. This establishes the
covered loader/emitted-artifact boundary, not a complete fix of arbitrary consumers.

The matching fixed run passed **29/29**, with no skips, on `net10.0`. Its baseline
failures were JSON number/string and boolean/string matches, honest JSON integer and
boolean mutant comparisons, floating/string comparison, and number/string session
determinism. Logs and unique TRXs were written under `/evidence/certification` in the
disposable validation container (host path `/workspace/scratch/live-evidence/certification`).
The separate aggregate loader reproduction and complete filter runs are pending at
this record update. To run only the aggregate red/green leg after a completed
scalar reproduction, pass `--aggregate-only` before the evidence directory. It swaps
only the loader's baseline source and exits after its focused fixed run; the exact
`net8.0` CI gate can then be run separately. Neither issue has been closed, and this
investigation does not revise autonomy or marketing holds.
