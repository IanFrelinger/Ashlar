# The certification gate, from a package

This is the builder-facing page for Ashlar's headline capability: the gate that decides whether a
brick becomes a **certified artifact**. It is written for someone consuming `Ashlar.*` from
nuget.org with no repository checkout. For writing the brick itself, see
[`AuthoringBricks.md`](AuthoringBricks.md); for the invariants and the spec, see
[`trust-loop/ashlar-trust-loop-spec.md`](trust-loop/ashlar-trust-loop-spec.md); for what has
actually been proven and where it still falls short, see
[`certification-evidence.md`](certification-evidence.md).

> **Not the same as `ashlar verify`.** The CLI's `verify` command judges a *project* — its
> `ashlar.yaml` and `ashlar.policy.yaml` — and prints VERIFIED or CERTIFIED
> ([`OperatorLifecycle.md`](OperatorLifecycle.md)). This page is about the *artifact* gate that
> judges a brick's source. Different subject, different record.

> **What a nuget.org `0.1.1` consumer actually gets.** This page describes the gate at the `0.1.2`
> line, and every behaviour that `0.1.1` does not have is marked **since `0.1.2`** where it is
> described. At the time of writing `0.1.2` is not on nuget.org. If you restore `Ashlar.Infrastructure
> 0.1.1` you get the *previous* loader: it globs `*.cs` under the brick directory for the source, reads
> the `.csproj` as XML for the dependency leg, and takes the reference set from `*.dll` in the build
> output — so a stock brick project referencing an Ashlar package fails the analyzer leg with
> `analyzer anchor type ... is not resolvable` unless it sets `CopyLocalLockFileAssemblies=true`.
> `Ashlar.Analyzers 0.1.1` ships only `lib/netstandard2.0/` and no `analyzers/dotnet/cs/`, so
> referencing it runs no rules, and the `0.1.1` CLI's `ashlar new brick` scaffolds the *old* template.
> Concretely: **the template scaffold and `samples/hello-brick/HelloBrick` as tracked at this line both
> REJECT under a `0.1.1` host**, and the `0.1.1` template as scaffolded REJECTS under the `0.1.2` gate.
> To reproduce what this page says, build the gate from a checkout at or after the `0.1.2` line
> (`tools/Ashlar.ExportCertifiedBrick`, `tools/Ashlar.CertifyBrick`, `bash scripts/run-cert-gate.sh`)
> until `0.1.2` is published.

## What a witness is

`README.md` and the trust-loop spec both lean on the word "witness" without defining it. Here it is:

> A **witness** is a list of input → expected-output cases for one brick, authored **before** the
> candidate implementation exists and never shown to whoever (or whatever) writes it. It is not a
> unit test: it is an executable statement of what "correct" means, held by the party doing the
> judging rather than the party doing the proposing.

Two consequences fall out of that separation, and they are the whole point:

- The proposer cannot write to the target. It never sees the cases, so it cannot special-case them.
- The witness itself gets audited. Mutation testing (leg 3) deliberately breaks the candidate and
  requires the witness to notice **every** break. A witness that lets a mutant live is a witness
  without teeth, and the gate rejects the whole submission rather than issuing a certificate the
  witness cannot back up.

In code a witness is `WitnessSpec(string BrickId, IReadOnlyList<WitnessCase> Cases)` with
`WitnessCase(IReadOnlyDictionary<string, object> Input, IReadOnlyDictionary<string, object> ExpectedOutput)`
— both in `Ashlar.Core.Application.Certification.Models`. On disk,
`BrickCertificationProjectLoader` reads this JSON shape:

```json
{
  "brickId": "acme.invoice.late-fee",
  "cases": [
    { "input": { "principalCents": 100000, "daysOverdue": 5, "graceDays": 10, "waived": false },
      "expectedOutput": { "feeCents": 0, "tier": "none" } },
    { "input": { "principalCents": 100000, "daysOverdue": 31, "graceDays": 0, "waived": false },
      "expectedOutput": { "feeCents": 3500, "tier": "delinquent" } }
  ]
}
```

Names in `input` / `expectedOutput` are the brick's declared `BrickInputDefinition` /
`BrickOutputDefinition` names. Numbers arrive as `int`, `long` or `double`; strings and booleans
map straight across (`BrickCertificationProjectLoader.FromJsonElement`).

Write the boundary cases. Every literal and every comparison in the brick becomes a mutant, so a
witness with three happy-path cases will be rejected at the mutation leg, not the correctness leg —
which is the gate telling you the certificate would have been worthless.

## What a certificate means

An artifact carries a certificate **if and only if** it passed every leg below, and the certificate
is signed over the artifact's content hash (SHA-256 of the brick source). Change one character of
the source and the record no longer verifies:
`CertificationTrustVerifier.Verify(record, source)` returns `content-hash-mismatch`.

## The five legs, in order

`CertificationGate.CertifyAsync` runs these in a fixed order and stops at the first failure. The
record's `gatesPassed` array is the ordered prefix that *did* pass, so a rejection tells you exactly
how far the candidate got.

| # | Leg (`failureCheck`) | What it checks | Why it is first/here |
|---|---|---|---|
| 0 | `recursion` | Generation lineage is coherent and under the depth ceiling. Absent lineage = human-authored, depth 0. | Runs before everything: an incoherent depth claim must not even be analyzed. Only relevant to the autonomy loop; hand-authored bricks pass trivially. |
| 1 | `analyzer` | The candidate compiles, and the Ashlar analyzer catalog (plus any constraint-manifest rules) reports nothing at or above the severity floor. | A defect a deterministic analyzer can *name* should never cost a mutation run. |
| 2 | `correctness` | Every witness case's actual output equals its expected output. | Cheap, and mutation testing is meaningless if the unmutated candidate is already wrong. |
| 3 | `mutation` | The engine generates mutants of the source and requires `escape_rate == 0` — every mutant must be dead: killed by some witness case, stopped by the wall clock (`timedOutMutants`), or fatal to the process running it (`crashedMutants`). Zero mutants generated is also a failure. Since `0.1.2` the catalog includes operator-class mutants (`+`↔`-`, `*`↔`/`, `<`↔`<=`, unary sign, `!` removal), every mutable site in every member body is generated with no per-kind cap, and each mutant replays in a bounded child process — a mutant that never terminates is filed under `timedOutMutants` with the bound named, instead of hanging the certifier or being credited to the witness. | This is what gives the certificate teeth: it audits the witness, not the candidate. The record keeps the three kinds of death apart, so a certificate never claims the witness caught what the clock caught. |
| 4 | `determinism` | The same case run twice under `AuditMode` canonicalizes identically. A missing repeat is nondeterminism-by-absence, never vacuous agreement. | A nondeterministic brick cannot be certified against a fixed expected output. |
| 5 | `dependency` | The brick project references no other project and only the two allowed packages (next section), and the source contains none of the forbidden kernel tokens. | Last because it is about *shape*, not behaviour — but it is not optional; see below. |

Only after all five does the gate sign the record and return `Admitted = true`.

**Where the brick actually runs.** Legs 2–4 execute author code — the candidate and every mutant of
it — and none of it runs on the certifier's own threads. Unless the request names an execution
backend (the autonomy loop's attested session), the gate compiles a small replay runner once per
certification and launches it as a child process through the same `dotnet` host that is running the
certifier; one child replays the candidate, another replays every mutant in turn, streaming one
observation per case back over stdout. The certifier enforces the wall clock
(`CandidateExecutionLimits`: 5 s per case, 30 s per unit, 10 min per certification, a 1 GiB heap,
all recorded on the `correctness-witness` / `mutation-gate` passes as `perCaseTimeoutMs=…`). A mutant
that loops forever times out and the child exits to shed the spinning thread; a mutant that
overflows the stack, calls `Environment.Exit` or `FailFast`, throws on a background thread or
allocates without bound kills the child, and the certifier reads the exit code and stderr, records
the crash against that mutant, restarts the runner for the rest, and still returns a verdict. A
CANDIDATE that does any of these is rejected at `correctness` with a `TimedOut` / `Crashed` finding.
Before this, an honest brick with a plain countdown loop hung certification forever (the
`shift-relational-boundary` mutant made `while (n > 0)` into `while (n >= 0)`), and an honest brick
with a recursive helper took the certifier down with an uncatchable stack overflow — exit 134, no
verdict, no message.

**The gate reasons about its own epistemics.** Two of the refusals above exist because silence
would be misread as a pass, not because a problem was found:

- Given source with an undefined symbol, the analyzer leg says
  `candidate does not compile, so analyzer silence would be meaningless: CS0103 ...` — it refuses
  because it cannot know.
- Given a compilation that cannot resolve `Ashlar.Core.Domain.Bricks.Brick`, it says
  `analyzer anchor type '...' is not resolvable from the candidate compilation references; the brick
  rules would silently no-op`. Every brick-scoped rule anchors on that type; without it they would
  all pass vacuously.

Both are refusals about the reference set. In Shape A that set is the loader's job, not yours
(**since `0.1.2`**): `BrickCertificationProjectLoader` reads it out of the compiler's own record of
the build (see "What the brick is compiled against"). Only Shape B, where you build the request by hand, has to supply
`CompilationReferences` itself — see "Things that will bite you".

## The two-package rule (and the one-project rule)

This is a hard design constraint, enforced by
`src/Ashlar.Infrastructure/Certification/BrickDependencyChecker.cs`, and it is the leg most
first-time authors hit:

- **A certifiable brick project may reference exactly two NuGet packages:**
  `Ashlar.Brick.Contracts` and `Ashlar.Authoring`. Any other `PackageReference` — including a
  logging package, a JSON package, or another `Ashlar.*` package — is a violation:

  ```
  PackageReference 'Serilog' is not allowed (only Ashlar.Brick.Contracts + Ashlar.Authoring,
  plus Ashlar.Analyzers referenced build-time-only with ExcludeAssets="runtime;compile")
  ```

- **One exception: `Ashlar.Analyzers`, build-time-only** (**since `0.1.2`**, both the exception in
  the dependency leg and the package layout it depends on). Running the fence locally is worth doing:
  it is the same rule catalogue the gate's analyzer leg attaches to your candidate. It does not
  count against the two-package rule — but only in a reference shape that keeps it out of the
  built brick. This is the shape:

  ```xml
  <PackageReference Include="Ashlar.Analyzers" Version="0.1.2" ExcludeAssets="runtime;compile" />
  ```

  `Ashlar.Analyzers 0.1.1` on nuget.org has **no** `analyzers/dotnet/cs/` leg — it ships
  `lib/netstandard2.0/` only — so referencing it in any shape runs no rules in your build, and the
  `0.1.1` dependency leg counts it as a third package and refuses the project.

  **`PrivateAssets="all"` is not enough, and is refused.** This is the one place the rule is
  counter-intuitive, so it is worth being exact about. `PrivateAssets` controls what flows
  *onward*, to projects that reference **yours**; it does nothing to your own project, which still
  receives the package's compile and runtime assets. `Ashlar.Analyzers` deliberately ships a
  `lib/` leg beside `analyzers/dotnet/cs/` (the Ashlar runtime consumes the same assembly as an
  ordinary library), so under `PrivateAssets="all"` the analyzer DLL is copied into your brick's
  output and your brick can bind to analyzer types and still certify — and then fail with
  `FileNotFoundException` in a consumer's process, because the packed brick declares no such
  dependency. `ExcludeAssets="runtime;compile"` leaves the `analyzers` asset group untouched, so
  the rules still **run** in your build, while the assembly stays out of your output and off your
  compile-time reference set. `ExcludeAssets="all"` is also accepted, but it switches the
  analyzers off, which defeats the point of adding the reference.

  ```
  PackageReference 'Ashlar.Analyzers' is allowed only build-time-only, and this one is not.
  Add ExcludeAssets="runtime;compile" to it: ...
  ```

- **A certifiable brick must live in its own project.** *Any* `ProjectReference` is refused
  outright:

  ```
  ProjectReference forbidden: ../../src/Ashlar.Core.Domain/Ashlar.Core.Domain.csproj
  ```

- **The source may not name the kernel.** The strings `Ashlar.Infrastructure`,
  `Ashlar.Core.Application`, `ProjectReference`, `src/Ashlar` and `/workspace` are refused wherever
  they appear in the candidate source:

  ```
  Source contains forbidden token 'Ashlar.Core.Application'
  ```

- **One brick, one `.cs`, one `.csproj`.** The certificate binds a SHA-256 of a single source
  file, and the analyzer and mutation legs compile that file as one compilation unit — so the brick
  must BE one authored source file. Since `0.1.2`, `BrickCertificationProjectLoader` refuses a
  project whose authored C# spans several files rather than picking one and signing as though it
  were the whole brick (the `0.1.1` loader took the first `*.cs` in the directory and hashed only
  that one). It also takes the first `.csproj` in the directory and the first non-abstract
  `DomainBrick` type in the built assembly, so give each brick its own directory.

The reason is that the certificate binds a content hash of one source file. If the brick could pull
in arbitrary code, the hash would cover a fraction of what actually runs, and "certified" would be a
claim about a file rather than about behaviour.

### What counts as "the brick's source", exactly

**Everything in this section is `0.1.2` behaviour.** The `0.1.1` loader globbed `*.cs` under the
brick directory and read the `.csproj` as XML; none of the refusals below exist in it.

Since `0.1.2` the gate does not glob your directory and it does not read your `.csproj` as XML. It asks MSBuild
what the project compiles — for the whole import chain, so a `Directory.Build.props` or
`Directory.Build.targets` beside the project counts exactly as much as the `.csproj` — and then,
after building, it reads the **compiler's own record** of the compilation (the source-document table
in the PDB) and requires the two to agree file for file and byte for byte.

That has consequences worth knowing before you hit them:

- **An MSBuild target that adds a `Compile` item is refused.** So is one that removes an item after
  the compile, or replaces the compile step. The gate compares against what csc recorded, not
  against `@(Compile)`, so none of these hide anything — they just fail. Put brick code in the
  brick's source file.
- **A target that rewrites the brick source during the build is refused.** The gate hashes the file
  before the build and checks that checksum against the compiler's; a generator that overwrites your
  `.cs` on the way to `CoreCompile` makes the certificate describe a program the assembly does not
  contain.
- **`<DebugType>none</DebugType>` cannot be used.** The gate forces `DebugType=portable`,
  `ChecksumAlgorithm=SHA256`, an empty `PathMap` and `DeterministicSourcePaths=false` as *global*
  MSBuild properties, so your project cannot switch them off; a build that still emits no portable
  PDB is refused, because there is then no record of what was compiled.
- **The file extension is irrelevant.** `<Compile Include="Helper.cstxt" />` is a compiled file and
  is treated as one. So is `Sub/obj/Thing.cs` — the SDK excludes only the project's *own* `bin/` and
  `obj/`, and so does the gate.
- **Multi-targeting is refused.** One content hash cannot speak for a per-framework source set.
  Give the brick a single `<TargetFramework>`.
- **A source generator is refused** (as an `<Analyzer>` item, and by the two-package rule): its
  output is compiled into the brick without ever being a source file the hash can cover.

The one thing tolerated outside the hash is the SDK's own boilerplate under the project's own
intermediate output directory — `AssemblyInfo.cs`, `GlobalUsings.g.cs` and friends. That tolerance
turns on MSBuild reporting that an SDK-shipped `.targets` file declared the item, not on the file's
name or its directory, so dropping your own `Generated.cs` into `obj/` does not inherit it.

### What the brick is compiled *under*

The source text is half of a program; the options it is compiled under are the other half. The
analyzer leg and the mutation leg re-compile the brick inside the certifying process, and they do it
under the **build's own compile options**, read from the compiler's record of the build (the
compilation-options block csc writes into the portable PDB): the preprocessor symbols — including
`NET`, `NET8_0` and `NETCOREAPP`, which the SDK defines for every net8.0 project without a csproj
edit — the language version, `CheckForOverflowUnderflow`, `Nullable` and `AllowUnsafeBlocks`; plus
every `global using` the build compiled from its generated `GlobalUsings.g.cs` (so `<Using
Include=... Alias=... />` items bind names in the fence exactly as they bound them in the assembly).

Until this landed the legs parsed the source with Roslyn's defaults. Two projects with a
byte-identical `Brick.cs` — the same signed `contentHash` — then compiled different programs: a
`#if ASHLAR_EVIL` (or `#if NET8_0`) branch holding a `File.WriteAllText` was code in the build and
disabled text to the fence and the mutation leg, and both projects certified ADMIT. A brick built
with checked arithmetic had its mutants compiled unchecked, so every mutant wrapped where the
shipped brick threw and the escape rate came out 0 for a witness with no teeth.

Consequences:

- **The record says which program was judged.** An admitted or rejected record carries a signed
  `compile-options` input whose `id` is the canonical line
  (`langVersion=12.0;checkOverflow=false;nullable=Enable;unsafe=false;symbols=...;globalUsings=...`).
  It sits beside `contentHash` rather than inside it: the content hash is the identity every
  downstream verifier recomputes from the source text alone, and both are under the signature.
- **Options the gate cannot honour are refused before any leg runs**, by name: a `<LangVersion>`
  newer than the gate's own compiler, a PDB whose options block is missing or rewritten, an SDK
  generated file that has grown a declaration, a project `global using` alias named `DomainBrick`
  for anything other than `Ashlar.Core.Domain.Bricks.Brick` (the harness injects that alias itself).
- **Nothing is hard-coded.** The symbol list is whatever csc was given, so a new SDK that defines
  a new symbol needs no change here.

### What the brick is compiled against

**Everything in this section is `0.1.2` behaviour** unless it says otherwise.

The analyzer leg and the mutation leg re-compile the brick source inside the certifying process, and
they need the assemblies csc used — above all the one defining `Ashlar.Core.Domain.Bricks.Brick`,
which every brick rule anchors on. Since `0.1.2` the loader takes that reference set from the build
the same way it takes the source set. The compiler's portable PDB records every assembly it compiled against (file
name and MVID); MSBuild's `ReferencePathWithRefAssemblies` list, read in the same build invocation,
says where each one lives; and a path is accepted for a recorded reference only when the file there
carries the recorded MVID. Package assemblies are therefore read straight from the NuGet cache.
Nothing has to be copied into the build output, and `CopyLocalLockFileAssemblies` is neither needed
nor consulted.

(In `0.1.1` the loader globbed `*.dll` out of the output directory, which for a stock library
project holds only the brick itself, so every brick that referenced an Ashlar package failed the
analyzer leg with `analyzer anchor type ... is not resolvable` unless its author set that property
— the `0.1.1` template did not. Since `0.1.2` the template certifies exactly as `ashlar new brick`
generates it; `BrickCertificationProjectLoaderReferenceTests.The_brick_template_certifies_exactly_as_scaffolded`
substitutes the tokens, builds it and runs all five legs. The template a `0.1.1` CLI scaffolds is the
old one and REJECTS under the `0.1.2` gate; regenerate it with a `0.1.2` CLI or from a checkout.)

**Compile options travel with the references** (`0.1.2`). The in-process legs compile the brick with
the build's own `DefineConstants`, `LangVersion` and `Nullable` settings, read from the same MSBuild
evaluation as the source set, so an `#if` branch the build saw is the branch the analyzer and the
mutants see. In `0.1.1` the in-process compilation used default options: two byte-identical sources
whose `.csproj` differed only by `DefineConstants` certified identically, as though the conditional
code did not exist.

Two consequences:

- **A target that edits the reference list after the compile is refused, by name.** Remove an item
  from `ReferencePathWithRefAssemblies` in an `AfterTargets="CoreCompile"` target, or replace the
  assembly under a path, and the compiler's record no longer matches. The refusal names the
  assembly and the target shape to remove; a build whose reference list comes back empty is
  refused the same way rather than falling back to the output directory.
- **The target framework's reference assemblies are verified but not handed on.** The certifying
  process compiles against its own runtime's framework; giving Roslyn a second core library breaks
  every predefined type. So a brick is analyzed against the framework of the machine certifying it,
  not against the targeting pack it was built with.

**Both shipped samples satisfy this, and the cert-gate suite pins it.**
`samples/hello-brick/HelloBrick/` and `samples/certified-brick-reuse/Ashlar.Certified.DamageResolver/`
are each one project, one source file, one `PackageReference` to `Ashlar.Brick.Contracts` and a
witness beside the source; `ShippedSampleCertificationTests` drives this loader and gate over both
tracked directories, so a change that stops either certifying fails `bash scripts/run-cert-gate.sh`.
Neither did before `0.1.2`: `samples/Directory.Build.props` injected a
`<Compile Include="../../src/Ashlar.Compat/GlobalUsings.DomainBrick.cs" />` into both — bypass 3's
exact shape, invisible to the `0.1.1` `.csproj`-only read — so the props file is now empty and each
brick names its base type in its own file. The reverse also holds: both samples as tracked at this
line **REJECT under a `0.1.1` host**, whose loader cannot resolve their package references from the
output folder. If your own project sits under a `Directory.Build.props`, that is
the shape to look for first when the gate refuses a compile item from outside the brick directory.

A certifiable brick project, complete:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>true</IsPackable>
    <PackageId>Acme.Bricks.LateFee</PackageId>
    <!-- Required if any ancestor directory has a Directory.Packages.props. -->
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Ashlar.Brick.Contracts" Version="0.1.1" />
  </ItemGroup>
</Project>
```

## Driving the gate as a package-only consumer

Two shapes, both written against published packages only. Neither needs a checkout.

### Shape A — a standalone certifier over a brick directory

Best when the brick is a separate project on disk and you want a signed
`certification-record.json` on the other side. This is the shape a CI job wants.

> **The candidate's code runs in the certifying process before any leg does.** `LoadAsync` runs
> `dotnet build` on the brick project, which executes whatever MSBuild targets the project and any
> `Directory.Build.props` / `Directory.Build.targets` beside it declare — an `<Exec>` task in one of
> them runs with your privileges, before the source-set and reference-set checks have anything to
> read. The loader then `Assembly.LoadFrom`s the built assembly and instantiates the brick type,
> which runs the assembly's module initializers, its static constructors and the brick's own
> constructor. All five legs run after that. Certify only code you would be willing to build and
> load on that machine, or run the certifier in a throwaway container.

Project references (a *host* project, not the brick project — the two-package rule applies to the
brick, not to the tool that certifies it):

```xml
<PackageReference Include="Ashlar.Infrastructure" Version="0.1.2" />
<PackageReference Include="Ashlar.Certification.Contracts" Version="0.1.2" />
```

(`0.1.2` is the first version whose `BrickCertificationProjectLoader` behaves as this page says; with
`0.1.1` the snippet below compiles and runs, but the loader is the `*.cs`-glob / output-folder one
described in the callout at the top. `tools/Ashlar.CertifyBrick` in the repository is this same
snippet, built against the checkout.)

```csharp
using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Infrastructure.Certification;

// args: <brick-project-dir> <witness.json> <output-record.json>
var request = await BrickCertificationProjectLoader.LoadAsync(args[0], args[1]);

var gate = new CertificationGate(new CertificationRecordSigner());
var decision = await gate.CertifyAsync(request);

if (!decision.Admitted)
{
    Console.Error.WriteLine($"REJECTED at {decision.FailureCheck}: {decision.Record.Reason}");
    foreach (var finding in decision.ProbeFindings)
        Console.Error.WriteLine($"  probe: {finding}");
    return 2;
}

var data = CertificationRecordMapper.ToData(decision.Record);
await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(data, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
}));

// The certificate is content-bound: this is the check a downstream consumer repeats.
var verify = CertificationTrustVerifier.Verify(data, request.SourceCode);
Console.WriteLine($"trusted={verify.Trusted} contentHash={data.ContentHash}");
return verify.Trusted ? 0 : 3;
```

`BrickCertificationProjectLoader.LoadAsync` shells out to
`dotnet msbuild <csproj> -restore -t:Build -c Release` on the brick project, so the machine running
this needs a .NET SDK, not just a runtime. (Since `0.1.2`, one invocation, which both builds and
reports what it compiled: a separate verification query runs under different MSBuild properties, and a target
conditioned on one of them can then contribute to the build while staying dormant in the query.)
Point it at a specific NuGet config with the `ASHLAR_CERT_NUGET_CONFIG` environment variable — it is
passed on as `-p:RestoreConfigFile` — if the default source list is not what you want.

### Shape B — the gate from DI, inside your own tests

Best when you want a brick's certifiability asserted by `dotnet test` in the same solution that
builds it. Resolve `ICertificationGate` from a normal Ashlar host
(`AddAshlar()` registers it via `AddCertificationInfrastructure`), and build the
`CertificationRequest` yourself:

```csharp
using Ashlar.Authoring;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Hosting;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddAshlarBrick<LateFeeBrick>();                      // before AddAshlar()
services.AddAshlar(o => o.RegisterBackgroundAgentHostedService = false);
using var provider = services.BuildServiceProvider();

var gate = provider.GetRequiredService<ICertificationGate>();

var decision = await gate.CertifyAsync(new CertificationRequest
{
    Brick = new LateFeeBrick(),
    BrickTypeName = typeof(LateFeeBrick).FullName,
    SourceCode = await File.ReadAllTextAsync(pathToLateFeeBrickCs),
    ProjectPath = pathToLateFeeBrickCsproj,
    CompilationReferences = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
        .Select(a => a.Location)
        .Distinct()
        .ToList(),
    Witness = witness,
});

Assert.True(decision.Admitted, $"REJECTED at {decision.FailureCheck}: {decision.Record.Reason}");
```

The test project itself is not the brick project, so it may reference whatever it likes
(`Ashlar.Hosting.Bundle`, xUnit, and so on). `ProjectPath` must point at the **brick's** `.csproj`,
because that file is what the dependency leg reads.

## What a rejection looks like

`CertificationDecision` carries three things worth reading:

- `FailureCheck` — one word, the leg that stopped it (`recursion`, `analyzer`, `correctness`,
  `mutation`, `determinism`, `dependency`).
- `Record.Reason` — the human-facing evidence. This is where the detail lives.
- `Record.GatesPassed` — the ordered prefix of legs that *did* pass, so you can see how far it got.

Real reason strings, by leg:

```
analyzer      candidate does not compile, so analyzer silence would be meaningless: CS0103 ...
correctness   Correctness check failed: <per-case actual vs expected>
mutation      Mutation escape check failed: escape_rate=0.14, survivors=[negate-condition-51];
              <operator, edit and line for each survivor>
mutation      Mutation escape check failed: no mutants were generated from catalog
determinism   Determinism check failed: outputs differ under AuditMode
dependency    Dependency-cleanliness failed: PackageReference 'Serilog' is not allowed
              (only Ashlar.Brick.Contracts + Ashlar.Authoring)
```

On rejection the gate also runs diagnostic probes matched to the failing leg and attaches
`ProbeFindings`. A probe holds no authority — it cannot change a verdict, and a probe that throws is
logged and skipped — so treat findings as extra evidence, never as the verdict.

**Surviving mutants need a human.** `escape_rate > 0` has two opposite causes: a weak witness (add
cases) or an **equivalent mutant** — a rewrite that cannot change behaviour on any input, which no
case can kill and which does not mean the candidate is wrong. Equivalence is undecidable, so the
gate rejects either way and names the operator, the edit and the line for each survivor so you can
tell them apart at a glance. Ledger row S5 in
[`certification-evidence.md`](certification-evidence.md) is this happening for real.

## The record, and verifying it downstream

An admitted record (`CertificationRecordData`, camelCase JSON) carries `status`, `admitted`,
`signed`, `brickId`, `contentHash`, `escapeRate`, `totalMutants`, `killedMutants`,
`survivingMutantIds`, `timedOutMutants`, `crashedMutants`, `gatesPassed[]` (name, version,
configuration), `inputs[]` (kind, id, hash — the witness, and the `compile-options` the program was
judged under), `signature`, `gate` and `schemaVersion`. `totalMutants` is the sum of the four id
lists; only `killedMutants` says anything about the witness. Records are minted at `schemaVersion`
3 (`CertificationRecordData.CurrentSchemaVersion`), whose signed payload covers the two wall-clock
lists; version-2 records already on disk keep verifying under the version-2 payload, and a record
edited to move a timed-out id into `killedMutants` does not verify. A consumer who wants to *use* a
certified brick without re-certifying it needs only `Ashlar.Certification.Contracts`:

```csharp
var result = CertificationTrustVerifier.Verify(record, brickSource);
// result.Trusted, or result.FailureCode: content-hash-mismatch | signature-invalid | ...
```

`samples/certified-brick-reuse/` is the worked example of that split: Project A certifies, Project B
verifies and runs the brick untouched, referencing no gate and no generator.

> **Read this before trusting a signature.** With no explicit key and no
> `ASHLAR_CERT_DEV_HMAC_KEY`, `CertificationRecordSigner` signs with a **committed development key**
> that anyone with the source can reproduce. The constructor logs a warning in that state and
> exposes it as `UsesDevKey`. A certificate under the dev key proves integrity against accident, not
> against an adversary. The known limits of the signing story — including two ways a forged record
> can be made to verify — are numbered in
> [`certification-evidence.md`](certification-evidence.md) under "Known v0 limitations".

## What the child processes see, and who signed (**since `0.1.2`**)

The gate spawns two kinds of child process on the author's behalf: `dotnet msbuild` over the brick
project (Shape A — and a project's targets run whatever the project says, `<Exec>` included), and the
witness replay runner that executes the candidate and every mutant of it. Neither inherits the
certifier's environment. Each gets an explicit allowlist copied by name from the certifier's own
process (`ChildProcessEnvironment`): `PATH`, `HOME`, the temp and locale variables, the `DOTNET_*`
host-location and first-run variables and the `NUGET_*` cache locations the SDK needs to restore and
build, proxy settings, and on Windows the system paths (`SystemRoot`, `APPDATA`, …). Nothing else
crosses — not `ASHLAR_CERT_*` (refused by prefix even if a future edit lists one), not
`DOTNET_STARTUP_HOOKS`, not `DOTNET_ROLL_FORWARD`, not whatever else the operator's shell holds.
Before this, both children inherited everything but a handful of MSBuild property names, so a target
in the brick's `.csproj` — or the candidate's own `ExecuteAsync` — could read `ASHLAR_CERT_DEV_HMAC_KEY`
and mint a record that verified under the operator's key. `ChildProcessEnvironmentTests` pins all
three views: the environment a build target dumps, the environment the runner's candidate reports,
and an end-to-end candidate that forges a record from inside the runner and gets one the verifier
rejects.

**What this does not close.** On Linux a child running as the same user can still read
`/proc/<certifier pid>/environ` — the certifier's *initial* environment block, which is exactly where
a key exported from the launching shell lives. `ChildProcessEnvironmentTests.KnownGap_…` documents
that it is open (and goes red when a platform closes it). Closing it means not holding the key in the
environment at all (a key file readable only by the certifier's user, or a signer the certifier calls
and never sees the key of), or running the children under a different uid or in their own PID
namespace, or `hidepid=2` on `/proc`. Until then, the allowlist is a defence against the *author's
code*, not against a process that already shares your uid.

**Who signed.** Every execution pass (`correctness-witness`, `mutation-gate`, `determinism`) carries
`certifier=Ashlar.Infrastructure/<informational version>;sourceRevision=<sha or unstamped>;certifierMvid=<module version id>`
in its `configuration` — the module version id identifies the exact gate binary under a
`Deterministic` build even when no version or revision was stamped — and `mutation-gate` also carries
`mutationCatalog=<version>` (`AstMutationCatalog.CatalogVersion`, bumped whenever the kinds or the
scope rules change; `AstMutationCatalogVersionTests` pins it). These strings were already under the
signature, so no schema bump: a record re-labelled to another certifier or catalog version does not
verify. A request with no build behind it (a hot-swap generation, a generated candidate) records the
`compile-options` input as `default;reason=no-build` instead of omitting it, so "compiled under the
defaults" is stated rather than inferred from absence.

## Things that will bite you

- **`CompilationReferences` is required in Shape B, and its absence fails closed.** Supply the
  assemblies the candidate compiles against or the analyzer leg refuses with `analyzer anchor type
  ... is not resolvable`. In a test, `AppDomain.CurrentDomain.GetAssemblies()` filtered to
  non-dynamic assemblies with a `Location` is enough. In Shape A there is nothing to supply since
  `0.1.2`: `BrickCertificationProjectLoader` resolves the set from the compiler's own record of the
  build, and a stock brick project needs no property for it (see "What the brick is compiled
  against"). Under a `0.1.1` host, set `CopyLocalLockFileAssemblies=true` on the brick project or
  the analyzer leg refuses it.
- **`ManagePackageVersionsCentrally=false`** on any project under a directory that has a
  `Directory.Packages.props`, or restore will refuse your inline `Version` attributes.
- **The brick must be deterministic by construction, and the gate checks less of that than the
  word suggests.** Leg 4 runs each witness case twice under `AuditMode` and compares canonicalized
  outputs, so it catches only what changes between two back-to-back runs on one machine. The
  analyzer catalog names three shapes statically: `DateTime.Now` / `DateTimeOffset.Now`
  (`UtcNow` is deliberately allowed), `new Random()` / `Random.Shared`, and mutable static fields
  (`ASHLAR0006`–`0008`) — plus file and network access in the *constructor* only
  (`ASHLAR0003` / `0004`), not in `ExecuteAsync`. So: I/O in `ExecuteAsync` that does not alter
  the outputs is caught by nothing; `DateTime.UtcNow` passes the analyzer and passes leg 4 whenever
  both runs round to the same value; floating-point and culture-dependent formatting differ across
  machines rather than across two runs on one, and pass too. Write the brick as a pure function of
  its declared inputs. The gate will not prove that for you.
- **A gate run is not fast.** It builds the project, compiles the candidate, then compiles a mutant
  per mutable site and replays them all in a child process (one process start per certification
  leg, plus one restart per mutant that hangs or crashes). A brick with twenty-odd mutants is
  seconds, not milliseconds; a brick whose mutants can loop adds the per-case budget for each one.
- **A mutant that times out or crashes is dead, not caught.** It appears under `timedOutMutants` or
  `crashedMutants`, never under `killedMutants`, and the `ADMIT` line prints all three counts
  (`mutants_killed=… killed_by_timeout=… killed_by_crash=…`). `escape_rate` is unaffected — the
  mutant cannot certify — but the witness earned no credit for it; if most of a brick's mutants die
  on the clock, the witness is not being audited and you should write cases that observe the loop.

## Where this is proven

- `src/Ashlar.Tests.Infrastructure/Tests/Certification/CertificationGateTeethTests.cs` — the pair
  that defines the gate: `GoodBrick_StrongWitness_Admits_WithZeroEscapeRate` and
  `WeakWitness_AllowsMutantEscapes_RejectsWithTeeth`.
- `.github/workflows/cert-gate.yml` → `bash scripts/run-cert-gate.sh` — the only required status
  check on `master`. Reproduce it locally from a checkout with that same command.
- [`certification-evidence.md`](certification-evidence.md) — one ledger row per proven ADMIT/REJECT,
  each citing the test or spike and the CI run, with "Known v0 limitations" at the end.

---

*Code references on this page were read from `src/` at the `0.1.2` line (unreleased at the time of
writing; `0.1.1` is the latest on nuget.org). The consumer snippets are transcriptions of shapes that
were driven end to end against published `0.1.1` packages, where the same API surface exists; the
behaviours marked "since `0.1.2`" were executed only from a checkout, by the tests this page names.*
