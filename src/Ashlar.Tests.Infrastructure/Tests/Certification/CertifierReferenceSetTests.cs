using System.Reflection;
using System.Reflection.Emit;
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
/// <para><b>Measured on Linux, in the devtest container, on net8.0 and net10.0.</b> Per
/// <c>docs/HowGatesGoQuiet.md</c> section 8 that means Windows and macOS are NOT yet measured. The
/// assertions are written against invariants rather than counts for that reason: a superset
/// relation, a floor, and named assemblies that ship in <c>Microsoft.NETCore.App</c> on every
/// platform — never "the set has exactly N entries", which would be a Linux-only fact.</para>
///
/// <para>In <c>...Tests.Certification</c> so it rides cert-gate (<c>ci/test-ownership.tsv</c>
/// line 57). The #605 precedent this copies its design from lives in <c>Ashlar.Analyzers.Tests</c>,
/// which <c>ci/test-ownership.tsv</c> line 47 records as UNOWNED — no gate runs it. The design
/// transfers; the placement must not.</para>
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
    /// The same fact stated on the set itself, so a failure names the assembly that went missing
    /// rather than only reporting that a proposal stopped compiling.
    /// </summary>
    [Fact]
    public void Set_carries_the_whole_shared_framework_and_the_brick_anchors()
    {
        var fileNames = CertifierReferenceSet.FileNames(CertifierReferenceSet.Shared);

        fileNames.Should().Contain("System.Private.CoreLib.dll");
        fileNames.Should().Contain(
            "System.ComponentModel.dll",
            "it carries the type-forward for System.IServiceProvider");
        fileNames.Should().Contain("System.Data.Common.dll");
        fileNames.Should().Contain("System.Web.HttpUtility.dll");
        fileNames.Should().Contain(
            Path.GetFileName(typeof(Ashlar.Core.Domain.Bricks.Brick).Assembly.Location),
            "the brick-authoring anchors are the shipped surface a candidate compiles against, and "
            + "are added by name rather than inherited from a directory listing");

        fileNames.Should().HaveCountGreaterThan(
            100,
            "the shared framework alone is well over a hundred assemblies. A set the size of a "
            + "loaded-assembly list means the framework half was silently dropped and every verdict "
            + "below is a statement about this host's history");
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
    /// accurate statement about this process and a false one about the artefact, since a brick is
    /// built against the <c>Ashlar.Authoring</c> package, which does not carry it. The declared set
    /// is the framework plus named anchors, so those types are simply not in scope.
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
    /// <see cref="CertifierReferenceSet.BrickAuthoringAnchors"/> reddens it — and the ambient
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
    /// </summary>
    [Fact]
    public async Task Verdicts_record_which_reference_set_produced_them()
    {
        var source = "namespace Demo; public sealed class Greeter { public string Hi() => \"hello\"; }";
        var expected = CertifierReferenceSet.Describe(CertifierReferenceSet.Shared);

        var a2 = await Check.CheckAsync(new[] { new ProposedFileContent("src/Greeter.cs", source) });
        var a4 = await Verifier.VerifyAsync(_root, new[] { Write("src/Greeter.cs", source) });

        a2.Detail.Should().Contain(expected);
        a4.Detail.Should().Contain(expected);
    }

    // ---- the published-host answer -------------------------------------------------------------
    // TRUSTED_PLATFORM_ASSEMBLIES behaves three different ways once an app is published, so these
    // drive Compose with each shape directly rather than publishing four ways from a unit test.
    // Measured shapes (Linux, SDK 10.0.11): framework-dependent 174 entries, 173 in the framework
    // directory, all on disk; self-contained single-file an EMPTY STRING; NativeAOT NULL; and
    // single-file + trimmed 161 entries of which ZERO exist on disk. That last one is why the guard
    // is on the narrowed, on-disk count: an emptiness-only check passes it and then throws out of
    // Roslyn, once per entry.

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
    /// The positive control for the four refusals above: with this host's REAL inputs, Compose
    /// proceeds and returns the framework. Without it, a Compose that refused unconditionally would
    /// satisfy every refusal assertion in this section.
    /// </summary>
    [Fact]
    public void Compose_proceeds_on_a_framework_dependent_host()
    {
        var composed = CertifierReferenceSet.Compose(
            CertifierReferenceSet.BrickAuthoringAnchors,
            AppContext.GetData(CertifierReferenceSet.TrustedPlatformAssembliesKey) as string,
            CertifierReferenceSet.SharedFrameworkDirectory(),
            AppContext.BaseDirectory);

        composed.Should().HaveCountGreaterThan(100);
    }

    private static InvalidOperationException Refuse(
        string? trustedPlatformAssemblies, string frameworkDirectory)
        => Assert.Throws<InvalidOperationException>(() => CertifierReferenceSet.Compose(
            CertifierReferenceSet.BrickAuthoringAnchors,
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
