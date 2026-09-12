using System.Reflection;
using System.Reflection.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Infrastructure.Certification;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Pins the property <see cref="CertifierReferenceSet"/> exists for: the references A2
/// (<see cref="RoslynExtensionCompileCheck"/>) and A4 (<see cref="RoslynPostApplyVerification"/>)
/// judge a proposal against are DECLARED, not whatever this process happened to load.
///
/// <para><b>Why the existing suite could not see this.</b> Every source in
/// <c>ExtensionCompileCheckTests</c> and <c>PostApplyVerificationTests</c> uses nothing outside
/// <c>System.Object</c> and <c>System.String</c>, so all four verdicts hold against a reference set
/// of one assembly. They were green whatever the ambient set did, and two byte-identical copies of
/// an ambient reference set sat in the certifier unnoticed. They stay as they are — they pin real
/// behaviour — but they have no discriminating power over this property, so the positive controls
/// below supply it (<c>docs/HowGatesGoQuiet.md</c> section 4).</para>
///
/// <para><b>Where this has been measured, stated per host rather than in one number.</b> Linux in
/// the devtest container on net8.0 and net10.0, and Windows on net10.0. Per
/// <c>docs/HowGatesGoQuiet.md</c> section 8 that leaves macOS NOT measured, and it also means the
/// declared COUNT is a per-host fact (net8.0/Linux and net10.0/Windows do not agree on it), which is
/// why nothing here asserts one. The assertions are relations instead: the set contains every managed
/// assembly the framework directory holds, it is a superset of what this host loaded, it carries every
/// assembly named in <see cref="CertifierReferenceSet.RequiredFrameworkAssemblies"/>, and every
/// refusal branch is driven through <see cref="CertifierReferenceSet.Compose"/> with the inputs that
/// produce it. Those hold on any platform by construction.</para>
///
/// <para>In <c>...Tests.Certification</c> so it rides cert-gate (<c>ci/test-ownership.tsv</c>
/// line 57), and listed in <c>ci/cert-gate-assertions.md</c> per that file's Rule 2. The #605
/// precedent this copies its design from lives in <c>Ashlar.Analyzers.Tests</c>, which
/// <c>ci/test-ownership.tsv</c> line 47 records as UNOWNED — no gate runs it. The design transfers;
/// the placement must not.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class CertifierReferenceSetTests : IDisposable
{
    private static readonly RoslynExtensionCompileCheck Check = new();
    private static readonly RoslynPostApplyVerification Verifier = new();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ashlar-refset-" + Guid.NewGuid().ToString("N")[..12]);

    public CertifierReferenceSetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// THE POSITIVE CONTROL, and the mutation detector. Every type named here lives in the shared
    /// framework and in an assembly a bare test host has no reason to load:
    /// <c>System.Data.DataTable</c> in System.Data.Common, <c>System.Web.HttpUtility</c> in
    /// System.Web.HttpUtility, <see cref="IServiceProvider"/> forwarded to System.ComponentModel
    /// (the exact CS1069 that Test.Sdk 18.9.0 produced in #605), <c>BigInteger</c> in
    /// System.Runtime.Numerics. Against the old ambient set all of them were REFUSED with
    /// "does not exist in the namespace"; against the declared set they compile.
    ///
    /// <para>It is also the floor for the refusal assertions further down: a reference set that
    /// collapsed to nothing would satisfy every refusal in this file and fail here.</para>
    /// </summary>
    [Fact]
    public async Task CompileCheck_resolves_framework_types_this_host_has_no_reason_to_load()
    {
        var files = new[] { new ProposedFileContent("src/Brick.cs", UsesUnloadedFrameworkTypes) };

        var result = await Check.CheckAsync(files);

        result.Passed.Should().BeTrue(
            "the reference set must be the shared framework this host was LAUNCHED with, not the "
            + "subset it happens to have loaded by the time a proposal arrives: " + result.Detail);
    }

    /// <summary>The same fact for A4, whose verdict decides rollback with no policy knob.</summary>
    [Fact]
    public async Task PostApplyVerification_resolves_framework_types_this_host_has_no_reason_to_load()
    {
        var applied = Write("src/Brick.cs", UsesUnloadedFrameworkTypes);

        var result = await Verifier.VerifyAsync(_root, new[] { applied });

        result.Passed.Should().BeTrue(
            "an applied change referencing a framework type the host had not happened to load was "
            + "rolled straight back off an unattended node: " + result.Detail);
    }

    /// <summary>
    /// The #602 freeze, stated behaviourally. A2 and A4 were byte-identical copies of one method;
    /// copies drift. They now share one cached instance, and this fails if they ever stop agreeing
    /// about what a given set of bytes compiles against — in either direction.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_stages_reach_the_same_verdict_on_the_same_bytes(bool expectedToCompile)
    {
        var source = expectedToCompile ? UsesUnloadedFrameworkTypes : UsesCertifierInternals;

        var a2 = await Check.CheckAsync(new[] { new ProposedFileContent("src/Brick.cs", source) });
        var a4 = await Verifier.VerifyAsync(_root, new[] { Write("src/Brick.cs", source) });

        a2.Passed.Should().Be(expectedToCompile, a2.Detail);
        a4.Passed.Should().Be(
            a2.Passed,
            "A2 admits and A4 decides whether the admitted change stays on disk; if they judge "
            + $"against different surfaces a node commits what it should revert. A2: {a2.Detail} / "
            + $"A4: {a4.Detail}");
    }

    /// <summary>
    /// The framework half stated as a SET RELATION against the framework directory, which is the
    /// assertion the first version of this file was missing. It checked four named assemblies and a
    /// count over one hundred, so it passed with the framework four assemblies short — and it WAS four
    /// short, silently, for a reason nothing here could have found by naming more assemblies.
    ///
    /// <para><c>TRUSTED_PLATFORM_ASSEMBLIES</c> is a RESOLVED list: one path wins per assembly
    /// identity, and an app-local copy beats the shared-framework copy. Narrowing that list to the
    /// framework directory therefore DROPS every framework assembly the deployment overrides from
    /// NuGet rather than substituting the framework copy sitting on disk beside it. On net8.0 that was
    /// <c>System.Text.Json</c>, <c>System.Text.Encodings.Web</c>,
    /// <c>System.Diagnostics.DiagnosticSource</c> and <c>System.Threading.Channels</c> —
    /// <c>Directory.Packages.props</c> pins the first ahead of the 8.0 framework and the other three
    /// travel with it. The miss is unbounded in principle, so the assertion is a set difference and
    /// not a longer list of names.</para>
    /// </summary>
    [Fact]
    public void Set_carries_every_managed_assembly_the_framework_directory_holds()
    {
        var frameworkDirectory = CertifierReferenceSet.SharedFrameworkDirectory();
        frameworkDirectory.Should().NotBeEmpty();

        var managedOnDisk = Directory
            .GetFiles(frameworkDirectory, "*.dll")
            .Where(HasManagedMetadata)
            .Select(path => Path.GetFileName(path))
            .ToArray();
        managedOnDisk.Should().HaveCountGreaterThan(
            100,
            "the shared framework is well over a hundred managed assemblies; a listing that collapsed "
            + "would make the difference below vacuous");

        var declared = CertifierReferenceSet
            .FileNames(CertifierReferenceSet.Shared)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var absent = managedOnDisk.Except(declared, StringComparer.OrdinalIgnoreCase).ToArray();
        absent.Should().BeEmpty(
            "the declared framework half is the framework DIRECTORY, not the resolved platform list. "
            + "An assembly the framework ships that is missing from the set is a type a brick project "
            + "compiles against and A2 reports as a compile error in the proposal. On disk but not "
            + "declared: " + string.Join(", ", absent));
    }

    /// <summary>
    /// The same defect stated as a MECHANISM rather than as a host observation, because the host
    /// observation is framework-version dependent: on net10.0 the pinned <c>System.Text.Json</c> is
    /// older than the framework's, so nothing is app-local and the set difference above cannot see it.
    /// Here the app-local override is constructed, so this fails on any platform and any framework if
    /// the framework half ever goes back to narrowing the resolved list.
    /// </summary>
    [Fact]
    public void Framework_half_survives_an_app_local_override_of_a_framework_assembly()
    {
        const string overridden = "System.Text.Json.dll";
        var frameworkDirectory = CertifierReferenceSet.SharedFrameworkDirectory();
        var frameworkCopy = Path.Combine(frameworkDirectory, overridden);
        File.Exists(frameworkCopy).Should().BeTrue(
            $"'{overridden}' ships in Microsoft.NETCore.App on every platform, and the override below "
            + "is only meaningful if the framework really holds a copy to be shadowed");

        // Exactly what the platform list looks like when a deployment carries a newer NuGet copy: the
        // framework path for that one identity is GONE from the list, replaced by an app-local path.
        var appLocalCopy = Path.Combine(_root, overridden);
        File.Copy(frameworkCopy, appLocalCopy);
        var resolvedList = string.Join(
            Path.PathSeparator,
            Directory.GetFiles(frameworkDirectory, "*.dll")
                .Where(path => !string.Equals(
                    Path.GetFileName(path), overridden, StringComparison.OrdinalIgnoreCase))
                .Append(appLocalCopy));

        var composed = CertifierReferenceSet.Compose(
            CertifierReferenceSet.SelfExtendAuthoringAnchors,
            resolvedList,
            frameworkDirectory,
            AppContext.BaseDirectory);

        var entry = composed
            .OfType<PortableExecutableReference>()
            .SingleOrDefault(reference => string.Equals(
                Path.GetFileName(reference.FilePath), overridden, StringComparison.OrdinalIgnoreCase));
        entry.Should().NotBeNull(
            $"'{overridden}' is in the shared framework on disk. Narrowing the RESOLVED platform list "
            + "to the framework directory drops it entirely once a deployment overrides it "
            + "app-locally, so the set silently loses assemblies in proportion to the host's NuGet "
            + "closure and A2 records 'compile error(s)' about the proposal for source a brick project "
            + "builds");
        Path.GetDirectoryName(entry!.FilePath).Should().Be(
            frameworkDirectory,
            "and it must be the FRAMEWORK copy: the app output directory contributes only the anchors "
            + "named in code");
    }

    /// <summary>
    /// Order-independence, stated structurally rather than behaviourally — this is the assertion
    /// that survives xunit's parallelism. A behavioural probe ("compile something using
    /// System.ComponentModel") can go green on broken code because another test in the same
    /// collection loaded the facade first. A superset relation cannot.
    ///
    /// <para>The second half is the floor: it asserts the two sets are demonstrably DIFFERENT on
    /// this host, so the first half is not passing because everything is loaded anyway.</para>
    /// </summary>
    [Fact]
    public void Set_is_a_strict_superset_of_the_shared_framework_this_host_has_loaded()
    {
        var frameworkDirectory = CertifierReferenceSet.SharedFrameworkDirectory();
        frameworkDirectory.Should().NotBeEmpty(
            "a framework-dependent host always knows where its shared framework is; an empty value "
            + "here means the test is running under a published host the certifier refuses anyway");

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .Select(assembly => assembly.Location)
            .Where(location => !string.IsNullOrEmpty(location))
            .Where(location => string.Equals(
                Path.GetDirectoryName(location), frameworkDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var declared = CertifierReferenceSet
            .FileNames(CertifierReferenceSet.Shared)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = loaded.Except(declared, StringComparer.OrdinalIgnoreCase).ToArray();
        missing.Should().BeEmpty(
            "the declared set can only add references relative to the ambient one — everything the "
            + "host loaded from the shared framework was launched with it. What it removes is the "
            + "dependence on load ORDER. Loaded but not declared: " + string.Join(", ", missing));

        declared.Count.Should().BeGreaterThan(
            loaded.Count,
            "this host has NOT loaded the whole shared framework, so the two sets are different and "
            + "the superset above is a real assertion rather than an equality in disguise. "
            + $"Declared {declared.Count}, loaded from the framework directory {loaded.Count}");
    }

    /// <summary>
    /// The other direction of the defect, and a real behaviour change: the ambient set contained the
    /// HOST's own assemblies, so a proposal naming a type inside the certifier compiled clean — an
    /// accurate statement about THIS process and not about the change being judged, because what sits
    /// beside the host differs between the CLI, an API host, a test host and a node. The declared set
    /// is the framework plus named anchors, and <c>Ashlar.Infrastructure</c> is not one of them, so
    /// those types are not in scope on any of them.
    ///
    /// <para>Not asserted here, because it is not true: that a brick project cannot compile such
    /// source. Measured from the built package, <c>Ashlar.Authoring</c> depends on
    /// <c>Ashlar.Hosting</c>, which has a <c>ProjectReference</c> on <c>Ashlar.Infrastructure</c>, so
    /// the certifier's assembly IS in a scaffolded brick's restore closure. That is a packaging fact
    /// worth its own look; it is not what this assertion rests on.</para>
    /// </summary>
    [Fact]
    public async Task Proposal_referencing_the_certifiers_own_internals_is_refused()
    {
        var files = new[] { new ProposedFileContent("src/Brick.cs", UsesCertifierInternals) };

        var result = await Check.CheckAsync(files);

        result.Passed.Should().BeFalse(
            "a proposal may be judged against the shipped authoring surface only. Compiling it "
            + "against whatever is deployed beside the certifier records 'compiles clean' for "
            + "source that a brick project cannot build");
        result.Detail.Should().Contain("compile error");
    }

    /// <summary>
    /// The anchors are the DOCUMENTED authoring surface, and this is the assertion that makes that a
    /// measurement rather than a claim. <c>docs/AuthoringBricks.md</c> says the
    /// <c>Ashlar.Authoring</c> package brings <c>Brick</c>, <c>BrickInput</c>, <c>BrickOutput</c>,
    /// <c>IExecutionContext</c> and <c>IBrickExecutor</c>; its csproj reaches them through five
    /// assemblies, and <c>Microsoft.Extensions.Logging.Abstractions</c> flows to a brick project from
    /// <c>Ashlar.Core.Domain</c>'s own package reference.
    ///
    /// <para>Anchoring only <c>Ashlar.Brick.Contracts</c> — the first version of this change — refused
    /// every source below. That is the failure mode this file's own standard names: "a legitimate
    /// extension that stops compiling is a missing anchor, not an acceptable loss."</para>
    /// </summary>
    [Theory]
    [InlineData("IBrickExecutor, from Ashlar.Core.Application",
        "namespace Demo; public static class R { public static System.Type T() "
        + "=> typeof(Ashlar.Core.Application.Execution.Routing.IBrickExecutor); }")]
    [InlineData("BrickRuntimeSpec, from Ashlar.Core.Domain",
        "namespace Demo; public static class R { public static System.Type T() "
        + "=> typeof(Ashlar.Core.Domain.Bricks.BrickRuntimeSpec); }")]
    [InlineData("IServiceCollection, from Microsoft.Extensions.DependencyInjection.Abstractions",
        "using Microsoft.Extensions.DependencyInjection; namespace Demo; "
        + "public static class R { public static void Go(IServiceCollection s) { } }")]
    [InlineData("ILogger<T> constructor injection, from Microsoft.Extensions.Logging.Abstractions",
        "using Microsoft.Extensions.Logging; namespace Demo; public sealed class B "
        + "{ private readonly ILogger<B> _log; public B(ILogger<B> log) { _log = log; } }")]
    [InlineData("System.Text.Json, which the 8.0 framework ships and this repository overrides",
        "namespace Demo; public static class R { public static string J(object o) "
        + "=> System.Text.Json.JsonSerializer.Serialize(o); }")]
    public async Task The_documented_brick_authoring_surface_compiles(string surface, string source)
    {
        var result = await Check.CheckAsync(new[] { new ProposedFileContent("src/Brick.cs", source) });

        result.Passed.Should().BeTrue(
            $"a brick project referencing Ashlar.Authoring compiles this ({surface}), so A2 refusing "
            + "it writes a FAILED build course onto the signed append-once record for a legitimate "
            + "extension — and A4 would roll the same change back off the node with no policy knob: "
            + result.Detail);
    }

    /// <summary>
    /// The other side of that boundary, pinned so it stays a decision rather than becoming an
    /// accident. <c>AddAshlarBrick&lt;T&gt;()</c> is declared in <c>Ashlar.Authoring</c> itself, and
    /// <c>Ashlar.Authoring</c> → <c>Ashlar.Hosting</c> → <c>Ashlar.Infrastructure</c>
    /// (<c>src/Ashlar.Hosting/Ashlar.Hosting.csproj</c>), so this assembly cannot name a type in
    /// either without an assembly-reference cycle. Resolving them by file name beside the host is the
    /// app-output-directory mechanism <see cref="CertifierReferenceSet"/> refuses; skipping them when
    /// absent is the silent partial set it refuses. So host registration is out of the declared
    /// surface, which is defensible only because the envelope lets a self-extending node add a
    /// <c>brick</c> and nothing else — and host registration is not part of a brick.
    /// </summary>
    [Fact]
    public async Task Host_registration_code_is_outside_the_declared_surface()
    {
        const string registration = """
            using Microsoft.Extensions.DependencyInjection;
            using Ashlar.Authoring;

            namespace Demo;

            public static class Registration
            {
                public static void Go(IServiceCollection services)
                    => services.AddAshlarBrick<Ashlar.Core.Domain.Bricks.Brick>();
            }
            """;

        var result = await Check.CheckAsync(
            new[] { new ProposedFileContent("src/Registration.cs", registration) });

        result.Passed.Should().BeFalse(
            "not because host registration is illegitimate, but because Ashlar.Authoring and "
            + "Ashlar.Hosting cannot be anchored from inside Ashlar.Infrastructure. If that ever "
            + "changes this assertion is the thing to delete: " + result.Detail);
        result.Detail.Should().Contain(
            "Ashlar",
            "and the refusal must name the unresolved authoring assembly, so an operator reading the "
            + "record can tell this boundary from a defect in the proposal: " + result.Detail);
    }

    /// <summary>
    /// The certification chain keeps its own, narrower anchor list, and this says why in a form that
    /// fails if someone converges them for tidiness: <c>GateEmittedArtifactCompiler</c>'s emitted
    /// bytes are hashed into the certificate and pinned by golden corpora, so widening what the chain
    /// compiles against can move certificate bytes.
    /// </summary>
    [Fact]
    public void The_certification_chain_keeps_its_own_narrower_anchor_list()
    {
        CertifierReferenceSet.SelfExtendAuthoringAnchors.Should().Contain(
            CertifierReferenceSet.BrickAuthoringAnchors,
            "the self-extend surface may only ADD to the chain's, or the two stages stop being "
            + "comparable");
        CertifierReferenceSet.SelfExtendAuthoringAnchors.Count.Should().BeGreaterThan(
            CertifierReferenceSet.BrickAuthoringAnchors.Count,
            "and they are deliberately different, so this is not an equality in disguise");

        var chain = (List<string>)typeof(BrickCertificationProjectLoader)
            .GetMethod("DefaultCompilationReferences", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null)!;

        chain.Select(path => Path.GetFileName(path)).Should().BeEquivalentTo(
            CertifierReferenceSet.BrickAuthoringAnchors
                .Select(anchor => Path.GetFileName(anchor.Assembly.Location))
                .Distinct(StringComparer.OrdinalIgnoreCase),
            "the chain's reference set is unchanged by this work, which is what keeps "
            + "GateEmittedArtifact bytes — and therefore every golden corpus — untouched");
    }

    /// <summary>
    /// The strongest single statement that the anchor set is the RIGHT one rather than merely a
    /// declared one: the repository's own brick template — read from <c>samples/</c> at test time so
    /// it cannot drift from the thing the scaffold writes — compiles through the real A2 check.
    ///
    /// <para>Against the ambient set it did not, and the reason mattered: <c>Ashlar.Brick.Contracts</c>
    /// physically holds <c>Ashlar.Core.Domain.Bricks.Brick</c> and a bare host never loads it, so the
    /// canonical shape of the only kind a self-extending node may add ("brick" is the sole entry the
    /// envelope permits) failed its own build course on a cold host.</para>
    ///
    /// <para>The template's project enables <c>ImplicitUsings</c> and A2 compiles standalone files
    /// with no project context, so the usings are supplied here explicitly. The second assertion is
    /// the point of saying so: without them the template still fails, but on missing <c>using</c>
    /// directives rather than on unresolved Ashlar types — a usings question, deliberately left
    /// open, and not the reference question this change is about. Keeping both halves stops the
    /// first from being read as "A2 accepts the template as written".</para>
    ///
    /// <para><b>What this one does NOT detect, measured rather than assumed.</b> Reverting the
    /// helper to the ambient load context leaves it GREEN: this test host has loaded
    /// <c>Ashlar.Brick.Contracts</c> for its own reasons, so the ambient set happens to contain the
    /// anchor here even though a bare host's does not. It is a missing-ANCHOR detector — emptying
    /// <see cref="CertifierReferenceSet.SelfExtendAuthoringAnchors"/> reddens it — and the ambient
    /// regression is caught by the framework-type controls above, which do not depend on what this
    /// host loaded. Stated here so a reader does not take a green template as evidence about load
    /// order (<c>docs/HowGatesGoQuiet.md</c> sections 4 and 12).</para>
    /// </summary>
    [Fact]
    public async Task The_brick_template_compiles_once_its_project_usings_are_supplied()
    {
        var template = BrickTemplateSource();

        var withUsings = await Check.CheckAsync(
            [new ProposedFileContent("src/ProbeBrick.cs", ImplicitUsings + template)]);
        withUsings.Passed.Should().BeTrue(
            "the brick-authoring anchors must resolve the contracts the scaffold's own output "
            + "depends on. A legitimate extension that stops compiling is a missing anchor, not an "
            + "acceptable loss: " + withUsings.Detail);

        var bare = await Check.CheckAsync(
            [new ProposedFileContent("src/ProbeBrick.cs", template)]);
        bare.Passed.Should().BeFalse();
        bare.Detail.Should().Contain(
            "using directive",
            "what is left once the references are declared is the project context A2 does not "
            + "reproduce — implicit usings — and that is a separate decision: " + bare.Detail);
        bare.Detail.Should().NotContain(
            "Ashlar.Core",
            "no Ashlar type may be unresolved; the anchors are what this change fixed: " + bare.Detail);
    }

    /// <summary>
    /// The usings <c>samples/templates/brick/__BrickName__Brick/__BrickName__Brick.csproj</c> gets
    /// from <c>&lt;ImplicitUsings&gt;enable&lt;/ImplicitUsings&gt;</c>, which a standalone compile
    /// does not.
    /// </summary>
    private const string ImplicitUsings = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using System.Net.Http;
        using System.Threading;
        using System.Threading.Tasks;

        """;

    private static string BrickTemplateSource()
    {
        var path = Path.Combine(
            RepoRoot(), "samples", "templates", "brick", "__BrickName__Brick", "__BrickName__Brick.cs");
        File.Exists(path).Should().BeTrue(
            $"the template is read from the tree rather than pasted, so it cannot drift; '{path}' "
            + "is not there, so the assertion cannot run and must not report a clean result");

        return File.ReadAllText(path)
            .Replace("__Namespace__", "Demo.Probe", StringComparison.Ordinal)
            .Replace("__BrickName__", "Probe", StringComparison.Ordinal)
            .Replace("__BrickId__", "probe", StringComparison.Ordinal)
            .Replace("__DisplayName__", "Probe", StringComparison.Ordinal)
            .Replace("__AshlarVersion__", "0.0.0", StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ashlar.sln")))
            directory = directory.Parent;

        directory.Should().NotBeNull(
            "no Ashlar.sln was found walking up from " + AppContext.BaseDirectory);
        return directory!.FullName;
    }

    /// <summary>
    /// Every verdict now carries the identity of the set that produced it, so two nodes that
    /// disagree about one proposal can be told apart from their signed records alone rather than
    /// only by an unexplained rollback.
    ///
    /// <para>It is also the assertion that closes the original defect's sharpest edge, and a review
    /// of this change reported seeing it open: a verdict whose recorded set differs from the set the
    /// process holds, and two proposals in one process disagreeing about identical bytes. That report
    /// could not be reproduced here — four runs on this host printed the same size and digest from a
    /// direct read of <see cref="CertifierReferenceSet.Shared"/> and from the A2 verdict produced
    /// beside it, with one <c>Ashlar.Infrastructure</c> loaded, one MVID and the default load context
    /// — and it could not have been open at the same time as this test was green, which is the
    /// structural half of the answer. So the assertion is widened rather than argued: BOTH stages and
    /// TWO successive proposals must all name the same set, which is exactly the shape the report
    /// described.</para>
    /// </summary>
    [Fact]
    public async Task Verdicts_record_which_reference_set_produced_them()
    {
        var source = "namespace Demo; public sealed class Greeter { public string Hi() => \"hello\"; }";
        var second = "namespace Demo; public sealed class Other { public int N() => 1; }";
        var expected = CertifierReferenceSet.Describe(CertifierReferenceSet.Shared);

        var a2 = await Check.CheckAsync(new[] { new ProposedFileContent("src/Greeter.cs", source) });
        var a4 = await Verifier.VerifyAsync(_root, new[] { Write("src/Greeter.cs", source) });
        var a2Again = await Check.CheckAsync(new[] { new ProposedFileContent("src/Other.cs", second) });

        a2.Detail.Should().Contain(expected);
        a4.Detail.Should().Contain(expected);
        a2Again.Detail.Should().Contain(
            expected,
            "proposal #1 and proposal #2 in one process must be judged against, and must RECORD, the "
            + "same set. The original defect was that the check's own first invocation loads Roslyn, "
            + "so the second proposal saw a larger ambient set than the first: " + a2Again.Detail);
    }

    // ---- the published-host answer -------------------------------------------------------------
    // The inputs behave five different ways once an app is published, so these drive Compose with
    // each shape directly rather than publishing five ways from a unit test. Measured shapes
    // (Linux, SDK 10.0.11): framework-dependent — the platform list names real files in the
    // framework directory; self-contained single-file — an EMPTY STRING; NativeAOT — NULL; and
    // single-file + trimmed — 161 entries of which ZERO exist on disk. That last one is why the
    // guard is on the narrowed, on-disk count: an emptiness-only check passes it and then throws out
    // of Roslyn, once per entry. The fifth is self-contained-not-single-file, where the framework
    // sits IN the app output directory; it is the one shape where every other guard is satisfied and
    // the set is wrong anyway.

    [Fact]
    public void Refuses_when_the_platform_list_is_absent()
    {
        var refusal = Refuse(trustedPlatformAssemblies: null, frameworkDirectory: string.Empty);

        refusal.Message.Should().Contain("returned null");
        refusal.Message.Should().Contain("ahead-of-time");
    }

    [Fact]
    public void Refuses_when_the_platform_list_is_empty()
    {
        var refusal = Refuse(trustedPlatformAssemblies: string.Empty, frameworkDirectory: string.Empty);

        refusal.Message.Should().Contain("listed 0 entries");
        refusal.Message.Should().Contain("single-file");
    }

    /// <summary>
    /// The trap case. The list is long and looks healthy; none of it is on disk. A guard that only
    /// asked "is the list empty" would proceed here.
    /// </summary>
    [Fact]
    public void Refuses_when_the_platform_list_names_files_that_are_not_on_disk()
    {
        var bundleDirectory = Path.Combine(_root, "published");
        var synthesised = string.Join(
            Path.PathSeparator,
            Enumerable.Range(0, 161).Select(i => Path.Combine(bundleDirectory, $"Synthesised{i}.dll")));

        var refusal = Refuse(synthesised, frameworkDirectory: bundleDirectory);

        refusal.Message.Should().Contain(
            "listed 161 entries",
            "the refusal must say what it saw, so a node's log distinguishes 'no list' from 'a list "
            + "of files that are not there'");
        refusal.Message.Should().Contain("trimmed");
        refusal.Message.Should().NotContain(
            "returned null", "this host did have a list; what it did not have was the files");
    }

    /// <summary>
    /// The shape every other guard passes. Under a self-contained publish that is NOT single-file,
    /// <c>typeof(object).Assembly.Location</c> is inside the app output directory, so "the framework
    /// directory" and "beside the host" are one path: the platform list is present, its entries are
    /// real files in that directory, and the framework is all there. Reading it would pull
    /// <c>Ashlar.Infrastructure</c> into the set and make
    /// <see cref="Proposal_referencing_the_certifiers_own_internals_is_refused"/> pass by compiling
    /// the certifier's internals clean — on the deployment shape that exclusion was written for.
    ///
    /// <para>Measured before the guard existed: composing with both directories set to this test
    /// host's own output directory returned a set containing <c>Ashlar.Infrastructure.dll</c>,
    /// <c>Ashlar.Core.Application.dll</c> and <c>Microsoft.CodeAnalysis.dll</c>.</para>
    /// </summary>
    [Fact]
    public void Refuses_when_the_framework_directory_is_the_app_output_directory()
    {
        var appDirectory = AppContext.BaseDirectory;
        var asPublished = Directory.GetFiles(
            appDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "*.dll");

        var refusal = Assert.Throws<InvalidOperationException>(() => CertifierReferenceSet.Compose(
            CertifierReferenceSet.SelfExtendAuthoringAnchors,
            string.Join(Path.PathSeparator, asPublished),
            appDirectory,
            appDirectory,
            asPublished));

        refusal.Message.Should().Contain("self-contained publish");
        refusal.Message.Should().Contain(
            "Ashlar.Infrastructure",
            "the refusal names the assembly whose presence in the set is the actual harm");
    }

    /// <summary>
    /// The floor, and the reason a count is not one. A trimmed framework directory satisfies every
    /// guard above — the platform list is present, its entries are real files in a directory of their
    /// own — and holds only what the trimmer kept. Composing on it returns a NON-EMPTY set that
    /// cannot resolve anything, so an emptiness check proceeds and every non-trivial proposal is then
    /// refused with unresolved-type diagnostics naming the proposal. Measured before the floor
    /// existed: two framework paths composed to three references and proceeded; one composed to two.
    /// </summary>
    [Fact]
    public void Refuses_a_trimmed_framework_directory_that_is_not_empty()
    {
        var frameworkDirectory = CertifierReferenceSet.SharedFrameworkDirectory();
        var whatTheTrimmerKept = new[]
        {
            Path.Combine(frameworkDirectory, "System.Private.CoreLib.dll"),
            Path.Combine(frameworkDirectory, "System.Runtime.dll"),
        };
        whatTheTrimmerKept.Should().OnlyContain(path => File.Exists(path));

        var refusal = Assert.Throws<InvalidOperationException>(() => CertifierReferenceSet.Compose(
            CertifierReferenceSet.SelfExtendAuthoringAnchors,
            string.Join(Path.PathSeparator, whatTheTrimmerKept),
            frameworkDirectory,
            AppContext.BaseDirectory,
            whatTheTrimmerKept));

        refusal.Message.Should().Contain("TRIMMED");
        refusal.Message.Should().Contain(
            "System.Collections.dll",
            "the refusal names which required assemblies are missing, so the log says what the host "
            + "looked like rather than only that it was refused");
        refusal.Message.Should().Contain("A count is not a floor here");
    }

    /// <summary>
    /// A missing anchor is a fault in the verifier, not a failing proposal, so it refuses by name
    /// rather than quietly producing a set that cannot resolve the brick contracts. A dynamic
    /// assembly has no location, which is the same shape as an anchor that did not deploy.
    /// </summary>
    [Fact]
    public void Refuses_when_an_anchor_assembly_has_no_location()
    {
        var dynamicType = AssemblyBuilder
            .DefineDynamicAssembly(new AssemblyName("AshlarAnchorProbe"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("main")
            .DefineType("AnchorWithNoLocation", TypeAttributes.Public)
            .CreateType();

        var refusal = Assert.Throws<InvalidOperationException>(() => CertifierReferenceSet.Compose(
            [dynamicType],
            AppContext.GetData(CertifierReferenceSet.TrustedPlatformAssembliesKey) as string,
            CertifierReferenceSet.SharedFrameworkDirectory(),
            AppContext.BaseDirectory));

        refusal.Message.Should().Contain("AnchorWithNoLocation");
        refusal.Message.Should().Contain("A missing anchor is a fault in this verifier");
    }

    /// <summary>
    /// A file in the framework directory that carries no managed metadata must never reach a
    /// compilation, and the guard that does that has to read the PE headers EAGERLY. Measured on
    /// Windows: <c>MetadataReference.CreateFromFile</c> returns a reference for a native <c>.dll</c>
    /// and for a text file renamed <c>.dll</c> without throwing, and so does
    /// <c>AssemblyMetadata.CreateFromFile</c> — so a <c>catch (BadImageFormatException)</c> around
    /// either never fires. The entry then surfaces as
    /// <c>CS0009: ... PE image doesn't contain managed metadata</c>, once per compilation, on a
    /// verdict about someone's proposal. Thirteen native <c>.dll</c>s sit in the 10.0 framework
    /// directory on Windows, so the only thing that kept this from firing was that the TPA slice
    /// happened to name none of them — a property of the host's platform list that the old code
    /// neither asserted nor documented, and that the framework-directory listing removes.
    ///
    /// <para>The second assertion is the positive control: the set still compiles something.</para>
    /// </summary>
    [Fact]
    public void A_file_that_is_not_a_managed_assembly_never_reaches_a_compilation()
    {
        var frameworkDirectory = CertifierReferenceSet.SharedFrameworkDirectory();
        var notAnAssembly = Path.Combine(_root, "Definitely.Not.An.Assembly.dll");
        File.WriteAllText(notAnAssembly, "this is not a portable executable");

        var composed = CertifierReferenceSet.Compose(
            CertifierReferenceSet.SelfExtendAuthoringAnchors,
            AppContext.GetData(CertifierReferenceSet.TrustedPlatformAssembliesKey) as string,
            frameworkDirectory,
            AppContext.BaseDirectory,
            Directory.GetFiles(frameworkDirectory, "*.dll").Append(notAnAssembly).ToArray());

        CertifierReferenceSet.FileNames(composed).Should().NotContain(
            Path.GetFileName(notAnAssembly),
            "a file with no managed metadata has nothing to reference, and leaving it in the set "
            + "fabricates a diagnostic that names the proposal");

        var emitted = CSharpCompilation.Create(
                "refset-positive-control",
                [CSharpSyntaxTree.ParseText("namespace Demo; internal sealed class Trivial { }")],
                composed,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .Emit(new MemoryStream());

        emitted.Success.Should().BeTrue(
            "and the set it leaves must still compile. This is the assertion the drop has to earn: "
            + "a set that dropped everything would satisfy the first half. Diagnostics: "
            + string.Join(
                "; ",
                emitted.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(3)
                    .Select(d => d.GetMessage())));
    }

    /// <summary>
    /// The positive control for every refusal above: with this host's REAL inputs, Compose proceeds
    /// and returns the framework. Without it, a Compose that refused unconditionally would satisfy
    /// every refusal assertion in this section.
    /// </summary>
    [Fact]
    public void Compose_proceeds_on_a_framework_dependent_host()
    {
        var composed = CertifierReferenceSet.Compose(
            CertifierReferenceSet.SelfExtendAuthoringAnchors,
            AppContext.GetData(CertifierReferenceSet.TrustedPlatformAssembliesKey) as string,
            CertifierReferenceSet.SharedFrameworkDirectory(),
            AppContext.BaseDirectory);

        CertifierReferenceSet.FileNames(composed).Should().Contain(
            CertifierReferenceSet.RequiredFrameworkAssemblies,
            "the floor is asserted here as a fact about a real host, not only as a refusal message");
        composed.Should().HaveCountGreaterThan(100);
    }

    private static bool HasManagedMetadata(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            return pe.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static InvalidOperationException Refuse(
        string? trustedPlatformAssemblies, string frameworkDirectory)
        => Assert.Throws<InvalidOperationException>(() => CertifierReferenceSet.Compose(
            CertifierReferenceSet.SelfExtendAuthoringAnchors,
            trustedPlatformAssemblies,
            frameworkDirectory,
            AppContext.BaseDirectory));

    private AppliedFile Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return new AppliedFile(relative, full);
    }

    private const string UsesUnloadedFrameworkTypes = """
        namespace Demo;

        public static class UsesTypesNothingInThisHostLoads
        {
            public static object Table() => new System.Data.DataTable();
            public static string Encode(string raw) => System.Web.HttpUtility.HtmlEncode(raw);
            public static object Resolve(System.IServiceProvider p, System.Type t) => p.GetService(t);
            public static System.Numerics.BigInteger Big() => System.Numerics.BigInteger.One;
        }
        """;

    private const string UsesCertifierInternals = """
        namespace Demo;

        public static class UsesTheCertifiersOwnAssembly
        {
            public static string GateEmittedAssemblyName()
                => Ashlar.Infrastructure.Certification.GateEmittedArtifactCompiler.AssemblyName;
        }
        """;
}
