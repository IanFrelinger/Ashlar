using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.Validation.Adapters;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The readiness trigger must cover everything the suites it runs are built from.
///
/// <para><b>What was wrong.</b> The native readiness lanes run <c>ashlar ci verify</c>, which runs
/// <c>ashlar validate</c>, which builds and runs every test project its recursive sweep discovers
/// (<c>ValidationServiceAdapter.IsDiscoverableTestProject</c>). Those lanes are diff-conditional: they
/// run only when a PR touches a glob in <c>READINESS_PATHS</c>. On 876cc527 the ProjectReference
/// closure of the discovered suites held 70 projects and 37 of their directories matched no glob, so
/// editing <c>src/Ashlar.Mcp.Server</c> (built into six suites) skipped every lane that runs them
/// while editing their tests re-ran them. #684 fixed that for the kernel suite alone.</para>
///
/// <para><b>What this proves.</b> Over the checked-out tree: (1) every project directory in the
/// transitive ProjectReference closure of every discovered suite is covered by a glob in BOTH
/// readiness lists - where a closure project's references are read from the project file AND from
/// every shared MSBuild file it is evaluated from (the implicit <c>Directory.Build.props</c>,
/// <c>Directory.Build.targets</c> and <c>Directory.Packages.props</c> above it, and any file reached
/// by an explicit <c>&lt;Import&gt;</c>), because a reference declared there is a real edge of every
/// project that reads it; (2) every implicit per-directory build input MSBuild or the compiler reads
/// for a closure project (<c>Directory.Build.props</c>/<c>.targets</c>/<c>.rsp</c>,
/// <c>Directory.Packages.props</c>, <c>.editorconfig</c>, <c>.globalconfig</c>, <c>nuget.config</c>,
/// <c>global.json</c>, in the project's directory or any ancestor) is covered; (3) every path
/// OUTSIDE the project's own directory that a closure project or one of those shared MSBuild files
/// names is covered, where a path is named by the Include of an item of ANY type except the identity
/// types (a package, framework, assembly or namespace name and the like) and a None item that is not
/// copied, by any item's <c>HintPath</c>, or by an <c>&lt;Import&gt;</c>. A failure names the
/// uncovered path, what names it, the list, and a suite that needs it.</para>
///
/// <para><b>Decisions, stated so they are not rediscovered.</b> Discovery calls the product
/// predicate itself rather than a copy, so the two cannot drift; it is handed repo-relative paths
/// under a virtual root because the predicate also reads the ABSOLUTE directory for the substring
/// "test", and CI's workspace (<c>/home/runner/work/Ashlar/Ashlar</c> and its macOS and Windows
/// equivalents) contains none - a checkout under a directory named <c>*test*</c> would otherwise
/// discover every project. References are read with an XML parser, so attribute order is irrelevant
/// (a regex on <c>&lt;ProjectReference\s+Include=</c> misses
/// <c>&lt;ProjectReference OutputItemType="Analyzer" Include=...&gt;</c>). <b>Analyzer-only
/// references</b> (<c>OutputItemType="Analyzer"</c>, <c>ReferenceOutputAssembly="false"</c>) are
/// followed like any other: the analyzer runs inside the referencing project's compile, so changing
/// it changes whether the suite builds - the reason #684 routed <c>Ashlar.Analyzers</c>.
/// <b>Conditional references</b> are followed too: this lens does not evaluate MSBuild conditions,
/// and over-approximating costs one glob, where under-approximating costs a skipped lane. For the
/// same reason a <b>reference in a shared MSBuild file</b> becomes an edge of every closure project
/// below it, whatever <c>$(MSBuildProjectName)</c> condition guards it, and every ancestor
/// <c>Directory.Build.*</c> counts as read even where MSBuild would stop at the nearest one; a
/// shared file's reference to one of its own importers is dropped, since MSBuild refuses that cycle
/// and only a condition can make such a file build. <b>Item types</b> are a denylist of identity
/// types, not an allowlist of path types: a path-bearing type nobody listed (<c>Protobuf</c>,
/// <c>EditorConfigFiles</c>, a code-generation input) is scanned, and a computed path on one fails.
/// A bare file name in a shared file names a file in each importer's own directory, which that
/// project's glob covers; a path under <c>$(NuGetPackageRoot)</c> is in the package cache, outside
/// the repository.</para>
///
/// <para><b>What this does NOT prove.</b> That the lanes pass, or that a suite selects any test -
/// readiness counts a skipped lane as a pass and <c>validate</c> passes a project that selects zero
/// tests. Runtime file reads (a test opening <c>ci/*.json</c> through <c>FindRepoRoot</c>) are not
/// build inputs and are not traced, and neither is a project built by an <c>&lt;MSBuild&gt;</c>
/// task inside a target. Files read through an MSBuild property function are not traced either:
/// <c>$([System.IO.File]::ReadAllText(...))</c> is how the root <c>Directory.Build.props</c> and
/// <c>Directory.Build.targets</c> read <c>VERSION</c>, which is in both lists by hand, not because
/// this proves it. Nor is a path in item metadata other than <c>HintPath</c> (a Protobuf
/// <c>ProtoRoot</c> or <c>AdditionalImportDirs</c>), or a file a compiled input reaches on its own (a
/// <c>.proto</c> import). Anything <c>ci verify</c> builds OTHER than the discovered suites is out of
/// scope. Unsupported shapes fail rather than being approximated: a <c>$(Property)</c> path, a
/// wildcard ProjectReference or Import, and a ProjectReference or item path in a shared MSBuild
/// file that is not anchored by <c>$(MSBuildThisFileDirectory)</c> (MSBuild resolves those against
/// each importing project, so they name no single file).</para>
/// </summary>
public sealed class ReadinessClosureConventionTests
{
    private const string Readiness = ".github/workflows/full-platform-readiness-gate.yml";
    private const string KernelSuite = "src/Ashlar.Tests.Kernel/Ashlar.Tests.Kernel.csproj";
    private const string KernelDependency = "src/Ashlar.Manifest/Ashlar.Manifest.csproj";

    // The two kinds of shared MSBuild file the reference walk must read: the implicit one every src/
    // project in the closure is evaluated from, and the tree's one explicit <Import> (from the
    // Tests.Infrastructure project, a discovered suite).
    private const string ImplicitSharedFile = "src/Directory.Build.props";
    private const string ExplicitSharedFile = "src/Ashlar.Tests.Infrastructure/CopyAssemblies.targets";

    // Non-vacuity floors. STATED here and lowered only by a visible diff: a count regenerated from the
    // tree would move down with the tree and could never fire. Measured on 876cc527 (2026-09-30):
    // validate discovers 22 suites whose closure is 70 projects, with 10 implicit inputs and 6
    // cross-directory inputs. The floors sit below those numbers so routine reference pruning does not
    // trip a required check, and above what a broken walk yields: a walk that saw only src/ finds 16
    // suites and 50 projects, and a non-recursive or mis-patterned enumeration finds none.
    private const int DiscoveredSuitesFloor = 20;
    private const int ClosureProjectsFloor = 60;
    private const int ImplicitInputsFloor = 6;
    private const int CrossDirectoryInputsFloor = 3;

    private static readonly string[] ImplicitInputNames =
    [
        "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp", "Directory.Packages.props",
        ".editorconfig", ".globalconfig", "nuget.config", "global.json",
    ];

    // The subset of the implicit inputs that is MSBuild XML and can itself pull files in.
    private static readonly string[] ImplicitMsBuildNames =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    // Item types whose Include is an identity and never a file: a project (walked by the closure check
    // instead), a package, framework, assembly or namespace name, an assembly attribute, a capability or
    // platform, or an analyzer-config name. EVERY other item type is read as naming files. This is a
    // denylist on purpose: an allowlist of path types missed a closure project's own Protobuf item, so
    // a path-bearing type nobody listed (Protobuf, EditorConfigFiles, a code-generation input) is
    // scanned anyway, and a computed path on one fails rather than being skipped. A simple or strong
    // assembly name in a Reference holds no directory separator and is skipped as a bare name.
    private static readonly HashSet<string> IdentityItemTypes = new(StringComparer.Ordinal)
    {
        "ProjectReference", "PackageReference", "PackageVersion", "GlobalPackageReference", "PackageDownload",
        "FrameworkReference", "InternalsVisibleTo", "AssemblyAttribute", "AssemblyMetadata", "Using",
        "ProjectCapability", "SupportedPlatform", "CompilerVisibleProperty", "CompilerVisibleItemMetadata",
    };

    // A path under this property names a file in the NuGet package cache, outside the repository, where
    // no glob can reach; the file that names the package and its version is itself covered. The tree's
    // one use is the AssembliesToCopy list in src/Ashlar.Tests.Infrastructure/CopyAssemblies.targets.
    private const string PackageCacheAnchor = "$(NuGetPackageRoot)";

    private static readonly Regex ChainImport = new(
        @"^\$\(\[MSBuild\]::GetPathOfFileAbove\('(Directory\.Build\.props|Directory\.Build\.targets|Directory\.Packages\.props)'",
        RegexOptions.CultureInvariant);

    private readonly ITestOutputHelper _output;

    public ReadinessClosureConventionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Every_project_in_a_discovered_suites_closure_is_a_readiness_path()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var graph = Walk(root);

        graph.Suites.Count.Should().BeGreaterThanOrEqualTo(DiscoveredSuitesFloor,
            "validate's sweep discovered {0} test projects; below the stated floor of {1} the discovery "
            + "walk is broken and every coverage assertion below would pass on an empty closure",
            graph.Suites.Count, DiscoveredSuitesFloor);
        graph.ReachedBy.Count.Should().BeGreaterThanOrEqualTo(ClosureProjectsFloor,
            "the closure of the discovered suites holds {0} projects; below the stated floor of {1} the "
            + "reference walk has stopped following edges", graph.ReachedBy.Count, ClosureProjectsFloor);
        graph.Suites.Should().Contain(KernelSuite, "the kernel suite is discovered by validate, not named by any workflow");
        graph.ReachedBy.ContainsKey(KernelDependency).Should().BeTrue(
            "the kernel suite tests the SPEC-006 admission code in Ashlar.Manifest (the edge #684 routed)");
        graph.ReachedBy[KernelDependency].Should().Contain(KernelSuite);
        graph.AnalyzerOnlyEdges.Should().BeGreaterThan(0,
            "Ashlar.Core.Application references Ashlar.Analyzers as an analyzer only; a walk that sees no "
            + "such edge is not reading the attribute-order-independent XML");
        graph.SharedFiles.Keys.Should().Contain(new[] { ImplicitSharedFile, ExplicitSharedFile },
            "a ProjectReference declared in a shared MSBuild file is an edge of every project evaluated from it, so the "
            + "walk must read the implicit Directory.Build.* files and explicit imports, not only the project files");

        // Both lists are checked before asserting, so one run names every gap in either list.
        var problems = ReadLists(root).SelectMany(x => UncoveredProjects(graph, x.Globs, x.List)).ToList();
        problems.Should().BeEmpty(
            "every directory a discovered suite is built from must re-run the lanes that run that suite; "
            + "{0} uncovered:\n{1}", problems.Count, string.Join("\n", problems));

        _output.WriteLine($"suites={graph.Suites.Count} closure={graph.ReachedBy.Count} analyzer-only-edges={graph.AnalyzerOnlyEdges} "
            + $"shared-msbuild-files={graph.SharedFiles.Count}");
        foreach (var (project, suites) in graph.ReachedBy)
            _output.WriteLine($"  {project}  <- {suites.Count} suite(s)");
        foreach (var (file, readers) in graph.SharedFiles)
            _output.WriteLine($"  shared {file}  <- {readers.Count} closure project(s)");
    }

    [Fact]
    public void Every_implicit_and_cross_directory_build_input_of_that_closure_is_a_readiness_path()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var graph = Walk(root);
        graph.ReachedBy.Count.Should().BeGreaterThanOrEqualTo(ClosureProjectsFloor,
            "the inputs below are read from the closure's {0} projects; below the stated floor of {1} the walk "
            + "is broken and an empty input set would pass", graph.ReachedBy.Count, ClosureProjectsFloor);
        var inputs = BuildInputs(root, graph);

        inputs.Implicit.Count.Should().BeGreaterThanOrEqualTo(ImplicitInputsFloor,
            "found {0} implicit build inputs; below the stated floor of {1} the ancestor scan is broken",
            inputs.Implicit.Count, ImplicitInputsFloor);
        inputs.CrossDirectory.Count.Should().BeGreaterThanOrEqualTo(CrossDirectoryInputsFloor,
            "found {0} cross-directory build inputs; below the stated floor of {1} the item scan is broken",
            inputs.CrossDirectory.Count, CrossDirectoryInputsFloor);
        inputs.Implicit.ContainsKey("src/Directory.Build.props").Should().BeTrue(
            "every src/ project in the closure imports src/Directory.Build.props");
        inputs.CrossDirectory.Keys.Should().Contain(key => key.Path.StartsWith("src/Ashlar.Compat/", StringComparison.Ordinal),
            "src/Directory.Build.props compiles Ashlar.Compat into most of the closure");

        var problems = ReadLists(root).SelectMany(x => UncoveredInputs(graph, inputs, x.Globs, x.List)).ToList();
        problems.Should().BeEmpty(
            "a file the suites in the closure are built from must re-run the lanes that run them; "
            + "{0} uncovered:\n{1}", problems.Count, string.Join("\n", problems));

        _output.WriteLine($"implicit={inputs.Implicit.Count} cross-directory={inputs.CrossDirectory.Count}");
        foreach (var file in inputs.Implicit.Keys)
            _output.WriteLine($"  implicit {file}");
        foreach (var (key, declared) in inputs.CrossDirectory)
            _output.WriteLine($"  cross {key.Path}{(key.IsDirectory ? "/**" : "")}  <- {string.Join(", ", declared.Kinds)}");
    }

    [Fact]
    public void The_walk_follows_transitive_analyzer_only_and_conditional_references_in_any_attribute_order()
    {
        using var fixture = new TreeFixture();
        fixture.Write("tests/Suite.Tests/Suite.Tests.csproj", Project("<ProjectReference Include=\"../../lib/A/A.csproj\" />"));
        // Include LAST: the shape a '<ProjectReference\s+Include=' regex cannot see.
        fixture.Write("lib/A/A.csproj", Project(
            "<ProjectReference OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" Include=\"../B/B.csproj\" />"));
        fixture.Write("lib/B/B.csproj",
            "<Project><ItemGroup Condition=\"'$(Flavor)' == 'x'\"><ProjectReference Include=\"..\\C\\C.csproj\" /></ItemGroup></Project>");
        fixture.Write("lib/C/C.csproj", "<Project />");
        fixture.Write("lib/Unreached/Unreached.csproj", "<Project />");

        var graph = Walk(fixture.Root);

        graph.Suites.Should().Equal("tests/Suite.Tests/Suite.Tests.csproj");
        graph.ReachedBy.Keys.Should().BeEquivalentTo(
            new[] { "tests/Suite.Tests/Suite.Tests.csproj", "lib/A/A.csproj", "lib/B/B.csproj", "lib/C/C.csproj" });
        graph.AnalyzerOnlyEdges.Should().Be(1);
    }

    [Fact]
    public void A_reference_declared_in_a_shared_msbuild_file_is_an_edge_of_every_closure_project_that_reads_it()
    {
        using var fixture = new TreeFixture();
        // Implicit: every project under lib/ is evaluated from lib/Directory.Build.props. The condition
        // names one project; the lens does not evaluate it and gives the edge to every reader.
        fixture.Write("lib/Directory.Build.props",
            "<Project><ItemGroup Condition=\"'$(MSBuildProjectName)' == 'A'\">"
            + "<ProjectReference Include=\"$(MSBuildThisFileDirectory)Shared/Shared.csproj\" /></ItemGroup></Project>");
        // Explicit: the suite imports a targets file that adds an analyzer-only reference, Include last.
        fixture.Write("tests/Suite.Tests/Suite.Tests.csproj",
            "<Project><Import Project=\"Build/Refs.targets\" />"
            + "<ItemGroup><ProjectReference Include=\"../../lib/A/A.csproj\" /></ItemGroup></Project>");
        fixture.Write("tests/Suite.Tests/Build/Refs.targets",
            "<Project><ItemGroup><ProjectReference OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" "
            + "Include=\"$(MSBuildThisFileDirectory)../../../tools/Gen/Gen.csproj\" /></ItemGroup></Project>");
        fixture.Write("lib/A/A.csproj", "<Project />");
        fixture.Write("lib/Shared/Shared.csproj", "<Project />");
        fixture.Write("tools/Gen/Gen.csproj", "<Project />");

        var graph = Walk(fixture.Root);

        graph.Suites.Should().Equal("tests/Suite.Tests/Suite.Tests.csproj");
        graph.ReachedBy.Keys.Should().BeEquivalentTo(new[]
        {
            "tests/Suite.Tests/Suite.Tests.csproj", "lib/A/A.csproj", "lib/Shared/Shared.csproj", "tools/Gen/Gen.csproj",
        }, "neither lib/Shared nor tools/Gen is named by any project file; only the shared files reach them");
        graph.ReachedBy["lib/Shared/Shared.csproj"].Should().Equal("tests/Suite.Tests/Suite.Tests.csproj");
        graph.AnalyzerOnlyEdges.Should().Be(1);
        graph.SharedFiles.Keys.Should().BeEquivalentTo(new[] { "lib/Directory.Build.props", "tests/Suite.Tests/Build/Refs.targets" });
        graph.SharedFiles["lib/Directory.Build.props"].Should().BeEquivalentTo(
            new[] { "lib/A/A.csproj", "lib/Shared/Shared.csproj" },
            "lib/Shared reads the file too; its reference to itself is dropped, not followed or refused");

        var problems = UncoveredProjects(graph, ["tests/Suite.Tests/**", "lib/A/**", "tools/Gen/**"], "fixture");
        problems.Should().ContainSingle().Which.Should().Contain("'lib/Shared/'").And.Contain("\"lib/Shared/**\"");
    }

    [Theory]
    [InlineData("Directory.Build.props", // unanchored: resolves against each importing project, which names no one file
        "<Project><ItemGroup><ProjectReference Include=\"lib/Shared/Shared.csproj\" /></ItemGroup></Project>")]
    [InlineData("Directory.Build.targets",
        "<Project><ItemGroup><ProjectReference Include=\"$(SharedRoot)/Shared.csproj\" /></ItemGroup></Project>")]
    [InlineData("Directory.Build.props",
        "<Project><ItemGroup><ProjectReference Include=\"$(MSBuildThisFileDirectory)lib/*/Shared.csproj\" /></ItemGroup></Project>")]
    [InlineData("tests/Suite.Tests/Refs.targets", // unanchored in an explicit import
        "<Project><ItemGroup><ProjectReference Include=\"../../lib/Shared/Shared.csproj\" /></ItemGroup></Project>")]
    [InlineData("Directory.Build.props", // a wildcard import could pull in a file declaring a reference
        "<Project><Import Project=\"$(MSBuildThisFileDirectory)build/*.targets\" /></Project>")]
    public void A_shared_msbuild_reference_or_import_that_names_no_single_file_is_an_error(string file, string content)
    {
        using var fixture = new TreeFixture();
        fixture.Write("tests/Suite.Tests/Suite.Tests.csproj", "<Project><Import Project=\"Refs.targets\" /></Project>");
        fixture.Write("lib/Shared/Shared.csproj", "<Project />");
        fixture.Write(file, content);
        Assert.Throws<InvalidDataException>(() => Walk(fixture.Root));
    }

    [Fact]
    public void An_uncovered_closure_directory_is_named_with_the_suite_that_needs_it()
    {
        using var fixture = new TreeFixture();
        fixture.Write("tests/Suite.Tests/Suite.Tests.csproj", Project("<ProjectReference Include=\"../../lib/A/A.csproj\" />"));
        fixture.Write("lib/A/A.csproj", Project("<ProjectReference Include=\"../B/B.csproj\" />"));
        fixture.Write("lib/B/B.csproj", "<Project />");
        var graph = Walk(fixture.Root);

        UncoveredProjects(graph, ["tests/Suite.Tests/**", "lib/A/**", "lib/B/**"], "fixture").Should().BeEmpty();
        var problems = UncoveredProjects(graph, ["tests/Suite.Tests/**", "lib/A/**", "lib/B/*"], "fixture");
        problems.Should().ContainSingle("a single * covers only the top level, not a project directory");
        problems[0].Should().Contain("'lib/B/'").And.Contain("tests/Suite.Tests/Suite.Tests.csproj").And.Contain("\"lib/B/**\"");
    }

    [Fact]
    public void Implicit_imports_and_cross_directory_items_are_found_and_packaging_only_items_are_not()
    {
        using var fixture = new TreeFixture();
        fixture.Write("Directory.Build.props",
            "<Project><ItemGroup><None Include=\"$(MSBuildThisFileDirectory)README.md\" Pack=\"true\" /></ItemGroup></Project>");
        fixture.Write("lib/Directory.Build.props",
            "<Project><Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" />"
            + "<ItemGroup><Compile Include=\"$(MSBuildThisFileDirectory)../shared/Common.cs\" /></ItemGroup></Project>");
        fixture.Write("tests/Suite.Tests/Suite.Tests.csproj", Project(
            "<ProjectReference Include=\"../../lib/A/A.csproj\" />"
            + "<Content Include=\"../../data/**/*\" CopyToOutputDirectory=\"PreserveNewest\" />"
            + "<None Include=\"../../notes/Readme.txt\"><CopyToOutputDirectory>Always</CopyToOutputDirectory></None>"
            + "<Compile Include=\"Local/**/*.cs\" />"));
        fixture.Write("lib/A/A.csproj", "<Project />");

        var inputs = BuildInputs(fixture.Root, Walk(fixture.Root));

        inputs.Implicit.Keys.Should().BeEquivalentTo(new[] { "Directory.Build.props", "lib/Directory.Build.props" });
        inputs.CrossDirectory.Keys.Should().BeEquivalentTo(
            new[] { new InputKey("shared/Common.cs", false), new InputKey("data", true), new InputKey("notes/Readme.txt", false) },
            "a pack-only None item is not a build input and an item inside the project directory is already covered");
    }

    [Fact]
    public void A_cross_directory_path_counts_whatever_its_item_type_and_so_does_a_HintPath()
    {
        using var fixture = new TreeFixture();
        // A shared file: an anchored path on a type nothing lists counts; a bare name (a file in each
        // importer's own directory) and an identity (not a file) neither count nor throw.
        fixture.Write("Directory.Build.props", "<Project><ItemGroup>"
            + "<Protobuf Include=\"$(MSBuildThisFileDirectory)contracts/shared.proto\" />"
            + "<Content Include=\"appsettings.json\" CopyToOutputDirectory=\"Always\" />"
            + "<PackageReference Include=\"Grpc.Tools\" /></ItemGroup></Project>");
        fixture.Write("tests/Suite.Tests/Suite.Tests.csproj", Project(
            "<Protobuf Include=\"..\\..\\protos\\agent.proto\" GrpcServices=\"Both\" />"
            + "<EditorConfigFiles Include=\"../../cfg/rules.editorconfig\" />"
            + "<GlobalAnalyzerConfigFiles Include=\"../../cfg/rules.globalconfig\" />"
            + "<SchemaInput Include=\"../../schemas/**/*.json\" />"
            + "<Reference Include=\"Probe\"><HintPath>..\\..\\lib\\Probe.dll</HintPath></Reference>"
            + "<Reference Include=\"../../lib/Direct.dll\" />"
            // Identities, not files - a computed one included - and a file in the package cache, outside the repository.
            + "<Reference Include=\"System.Web, Version=4.0.0.0, Culture=neutral\" />"
            + "<InternalsVisibleTo Include=\"$(AssemblyName).Tests\" />"
            + "<Using Include=\"Xunit\" />"
            + "<AssembliesToCopy Include=\"$(NuGetPackageRoot)/castle.core/5.2.1/lib/net6.0/Castle.Core.dll\" />"
            + "<Protobuf Include=\"Protos\\local.proto\" />"));

        var graph = Walk(fixture.Root);
        var inputs = BuildInputs(fixture.Root, graph);

        inputs.CrossDirectory.Keys.Should().BeEquivalentTo(new[]
        {
            new InputKey("contracts/shared.proto", false), new InputKey("protos/agent.proto", false),
            new InputKey("cfg/rules.editorconfig", false), new InputKey("cfg/rules.globalconfig", false),
            new InputKey("schemas", true), new InputKey("lib/Probe.dll", false), new InputKey("lib/Direct.dll", false),
        }, "a path on an item type nothing lists, or in a HintPath, is a build input exactly as a Compile path is");
        inputs.CrossDirectory[new InputKey("lib/Probe.dll", false)].Kinds.Should().Equal("Reference HintPath");
        var problems = UncoveredInputs(graph, inputs,
            ["Directory.Build.props", "tests/Suite.Tests/**", "contracts/**", "cfg/**", "schemas/**", "lib/**"], "fixture");
        problems.Should().ContainSingle().Which.Should().Contain("'protos/agent.proto'").And.Contain("named by Protobuf in")
            .And.Contain("tests/Suite.Tests/Suite.Tests.csproj");
    }

    [Theory]
    [InlineData("<ProjectReference Include=\"$(Dependency)/D.csproj\" />")]
    [InlineData("<ProjectReference Include=\"../../lib/*/D.csproj\" />")]
    [InlineData("<ProjectReference Include=\"../../lib/Missing/Missing.csproj\" />")]
    [InlineData("<ProjectReference Include=\"/abs/D.csproj\" />")]
    [InlineData("<ProjectReference Include=\"../../../outside/D.csproj\" />")]
    [InlineData("<Content Include=\"$(SomeRoot)/data/**\" CopyToOutputDirectory=\"Always\" />")]
    [InlineData("<Protobuf Include=\"$(ProtoRoot)/agent.proto\" />")] // a type nothing lists is still read as a path
    [InlineData("<Reference Include=\"Probe\"><HintPath>$(LibRoot)/Probe.dll</HintPath></Reference>")]
    public void Unmeasurable_project_shapes_are_errors(string item)
    {
        using var fixture = new TreeFixture();
        fixture.Write("tests/Suite.Tests/Suite.Tests.csproj", Project(item));
        Assert.Throws<InvalidDataException>(() => BuildInputs(fixture.Root, Walk(fixture.Root)));
    }

    [Fact]
    public void An_unanchored_item_in_a_shared_import_is_an_error()
    {
        using var fixture = new TreeFixture();
        fixture.Write("Directory.Build.props", "<Project><ItemGroup><Compile Include=\"shared/Common.cs\" /></ItemGroup></Project>");
        fixture.Write("tests/Suite.Tests/Suite.Tests.csproj", "<Project />");
        Assert.Throws<InvalidDataException>(() => BuildInputs(fixture.Root, Walk(fixture.Root)));
    }

    [Theory]
    [InlineData("src/AI/**", "src/AI/Nested/Deeper/File.cs", true)]
    [InlineData("src/AI/*", "src/AI/Nested/File.cs", false)]
    [InlineData("src/AI/**", "src/AIOther/File.cs", false)]
    [InlineData("src/Ashlar.Policies/**", "src/Ashlar.Policies.Dev/File.cs", false)]
    [InlineData(".editorconfig", ".editorconfig", true)]
    public void The_glob_matcher_keeps_GitHub_semantics(string glob, string path, bool expected)
        => Matches(glob, path).Should().Be(expected);

    // ---- the walk ---------------------------------------------------------------------------------

    /// <param name="Suites">The discovered test projects.</param>
    /// <param name="ReachedBy">Each closure project, with the suites whose closure holds it.</param>
    /// <param name="AnalyzerOnlyEdges">Distinct analyzer-only (project, target) edges walked.</param>
    /// <param name="SharedFiles">Each shared MSBuild file read for references, with the closure projects evaluated from it.</param>
    private sealed record Graph(
        IReadOnlyList<string> Suites,
        SortedDictionary<string, SortedSet<string>> ReachedBy,
        int AnalyzerOnlyEdges,
        SortedDictionary<string, SortedSet<string>> SharedFiles);

    private readonly record struct InputKey(string Path, bool IsDirectory);

    /// <param name="Files">The closure projects and shared MSBuild files that name the path.</param>
    /// <param name="Kinds">What names it: an item type, an item type's HintPath, or Import.</param>
    private sealed record Declared(SortedSet<string> Files, SortedSet<string> Kinds);

    private sealed record Inputs(
        SortedDictionary<string, SortedSet<string>> Implicit,
        SortedDictionary<InputKey, Declared> CrossDirectory);

    private static Graph Walk(string root)
    {
        var projects = EnumerateProjects(root);
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
            canonical.TryAdd(project, project);

        // The predicate reads the absolute directory; hand it the repo-relative path under a root whose
        // own name contains no "test", which is what CI's workspace path is (see the class summary).
        var virtualRoot = new DirectoryInfo(Path.Combine(Path.GetPathRoot(Path.GetFullPath(root))!, "readiness-closure-root"));
        var suites = projects.Where(project => ValidationServiceAdapter.IsDiscoverableTestProject(
            new FileInfo(Path.Combine(virtualRoot.FullName, project)), virtualRoot)).ToArray();

        var edges = new Dictionary<string, IReadOnlyList<(string Target, bool AnalyzerOnly)>>(StringComparer.Ordinal);
        var analyzerOnly = new HashSet<(string, string)>();
        var reachedBy = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var sharedFiles = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var suite in suites)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([suite]);
            while (pending.TryPop(out var project))
            {
                if (!seen.Add(project))
                    continue;
                if (!reachedBy.TryGetValue(project, out var by))
                    reachedBy[project] = by = new SortedSet<string>(StringComparer.Ordinal);
                by.Add(suite);
                if (!edges.TryGetValue(project, out var next))
                {
                    var shared = SharedFilesOf(root, project);
                    foreach (var file in shared)
                    {
                        if (!sharedFiles.TryGetValue(file, out var readers))
                            sharedFiles[file] = readers = new SortedSet<string>(StringComparer.Ordinal);
                        readers.Add(project);
                    }
                    edges[project] = next = References(root, project, shared, canonical);
                }
                foreach (var (target, isAnalyzerOnly) in next)
                {
                    if (isAnalyzerOnly)
                        analyzerOnly.Add((project, target));
                    pending.Push(target);
                }
            }
        }
        return new Graph(suites, reachedBy, analyzerOnly.Count, sharedFiles);
    }

    /// <summary>
    /// Every <c>*.csproj</c> below the root, as the sweep's recursive <c>GetFiles</c> would find it.
    /// Directories named <c>bin</c>/<c>obj</c> or starting with '.' are pruned: the predicate rejects
    /// any project below such a segment, so pruning cannot change the discovered set, and it keeps
    /// <c>.git</c>, agent worktrees and build output out of the walk.
    /// </summary>
    private static List<string> EnumerateProjects(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var directory))
        {
            found.AddRange(Directory.EnumerateFiles(directory, "*.csproj")
                .Where(file => file.EndsWith(".csproj", StringComparison.Ordinal))
                .Select(file => Relative(root, file)));
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                pending.Push(child);
            }
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// The references a project is built with: those in its own file and those in every shared MSBuild
    /// file it is evaluated from. A shared file's path must be anchored by
    /// <c>$(MSBuildThisFileDirectory)</c>; unanchored, MSBuild resolves it against each importing
    /// project, so it names a different project per importer and the lens refuses it.
    /// </summary>
    private static IReadOnlyList<(string Target, bool AnalyzerOnly)> References(
        string root, string project, IReadOnlyList<string> sharedFiles, IReadOnlyDictionary<string, string> canonical)
    {
        var result = new List<(string, bool)>();
        foreach (var file in sharedFiles.Prepend(project))
        {
            var isProject = file == project;
            foreach (var reference in Load(root, file).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                // Update/Remove forms modify an existing item; only Include adds an edge.
                var include = (string?)reference.Attribute("Include");
                if (include is null)
                    continue;
                var analyzerOnly = string.Equals(Metadata(reference, "OutputItemType"), "Analyzer", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Metadata(reference, "ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase);
                foreach (var part in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var path = StripAnchor(part, isProject, out var anchored);
                    if (!isProject && !anchored)
                        throw new InvalidDataException($"{file}: ProjectReference '{part}' in a shared MSBuild file resolves against "
                            + "each importing project; anchor it with $(MSBuildThisFileDirectory) so it names one project");
                    if (path.IndexOfAny(['$', '@', '%', '*', '?']) >= 0)
                        throw new InvalidDataException($"{file}: ProjectReference '{part}' is not a literal path; this lens does not evaluate MSBuild");
                    var target = Resolve(DirectoryOf(file), path, file);
                    if (canonical.TryGetValue(target, out var known))
                        target = known;
                    else if (!File.Exists(Path.Combine(root, target)))
                        throw new InvalidDataException($"{file}: ProjectReference '{part}' names a project that does not exist ({target})");
                    if (target == project)
                        continue; // a shared file naming one of its own importers: MSBuild refuses the cycle, so a condition excludes it
                    result.Add((target, analyzerOnly));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The shared MSBuild files a project is evaluated from besides itself: every
    /// <c>Directory.Build.props</c>, <c>Directory.Build.targets</c> and <c>Directory.Packages.props</c>
    /// in its directory or an ancestor (an over-approximation - MSBuild imports the nearest of each and
    /// reaches farther ones only through a chain import), and every repo file those or the project
    /// reach by an explicit <c>&lt;Import&gt;</c>, transitively.
    /// </summary>
    private static List<string> SharedFilesOf(string root, string project)
    {
        var shared = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { project };
        var pending = new Stack<string>(ImplicitFiles(root, project, ImplicitMsBuildNames).Concat(Imports(root, project, isProject: true)));
        while (pending.TryPop(out var file))
        {
            if (!seen.Add(file))
                continue;
            shared.Add(file);
            foreach (var import in Imports(root, file, isProject: false))
                pending.Push(import);
        }
        shared.Sort(StringComparer.Ordinal);
        return shared;
    }

    /// <summary>
    /// The repo files an MSBuild file imports explicitly. An Import path resolves against the
    /// declaring file. The ancestor chain import and SDK imports are skipped (the first is counted as an
    /// implicit file, the second is not in the repo), as is a literal path to a file that does not
    /// exist (it contributes nothing to the build); a computed or wildcard path is refused.
    /// </summary>
    private static IEnumerable<string> Imports(string root, string file, bool isProject)
    {
        foreach (var element in Load(root, file).Descendants().Where(e => e.Name.LocalName == "Import"))
        {
            var value = (string?)element.Attribute("Project");
            if (value is null || element.Attribute("Sdk") is not null)
                continue;
            foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ChainImport.IsMatch(part))
                    continue;
                var path = StripAnchor(part, isProject, out _);
                if (path.IndexOfAny(['$', '@', '%', '*', '?']) >= 0)
                    throw new InvalidDataException($"{file}: Import '{part}' is not a literal path; this lens does not evaluate "
                        + "MSBuild or expand wildcards, and an imported file can declare references");
                var target = Resolve(DirectoryOf(file), path, file);
                if (File.Exists(Path.Combine(root, target)))
                    yield return target;
            }
        }
    }

    /// <summary>Files with one of <paramref name="names"/> in the project's directory or any ancestor up to the root.</summary>
    private static IEnumerable<string> ImplicitFiles(string root, string project, string[] names)
    {
        var directory = DirectoryOf(project);
        while (true)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, directory)).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    yield return Join(directory, name);
            }
            if (directory.Length == 0)
                yield break;
            directory = DirectoryOf(directory);
        }
    }

    /// <summary>
    /// Strips a leading <c>$(MSBuildThisFileDirectory)</c> (the declaring file's directory) or, in a
    /// project file, <c>$(MSBuildProjectDirectory)</c>; <paramref name="anchored"/> says whether one was.
    /// </summary>
    private static string StripAnchor(string part, bool isProject, out bool anchored)
    {
        const string ThisFile = "$(MSBuildThisFileDirectory)";
        const string ProjectDirectory = "$(MSBuildProjectDirectory)";
        anchored = true;
        if (part.StartsWith(ThisFile, StringComparison.Ordinal))
            return part[ThisFile.Length..].TrimStart('/', '\\');
        if (isProject && part.StartsWith(ProjectDirectory, StringComparison.Ordinal))
            return part[ProjectDirectory.Length..].TrimStart('/', '\\');
        anchored = false;
        return part;
    }

    // ---- implicit and cross-directory inputs ------------------------------------------------------

    private static Inputs BuildInputs(string root, Graph graph)
    {
        var implicitInputs = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var project in graph.ReachedBy.Keys)
            foreach (var relative in ImplicitFiles(root, project, ImplicitInputNames))
            {
                if (!implicitInputs.TryGetValue(relative, out var importers))
                    implicitInputs[relative] = importers = new SortedSet<string>(StringComparer.Ordinal);
                importers.Add(project);
            }

        var cross = new SortedDictionary<InputKey, Declared>(Comparer<InputKey>.Create((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.IsDirectory.CompareTo(b.IsDirectory);
        }));
        void Add(InputKey key, string declaredBy, string kind)
        {
            if (!cross.TryGetValue(key, out var by))
                cross[key] = by = new Declared(new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
            by.Files.Add(declaredBy);
            by.Kinds.Add(kind);
        }
        foreach (var project in graph.ReachedBy.Keys)
            foreach (var (key, kind) in ItemInputs(root, project, isProject: true))
                Add(key, project, kind);
        // The implicit MSBuild files above closure projects and every file they or a project import.
        foreach (var file in graph.SharedFiles.Keys)
            foreach (var (key, kind) in ItemInputs(root, file, isProject: false))
                Add(key, file, kind);
        return new Inputs(implicitInputs, cross);
    }

    /// <summary>
    /// The paths outside the declaring project that an MSBuild file names: the Include of every item
    /// whose type names files (<see cref="NamesFiles"/>), the HintPath of any item, and every Import.
    /// MSBuild resolves an ITEM path against the project being built and an Import against the
    /// declaring file; for a project file both are its own directory, and in a shared import only a
    /// path anchored by <c>$(MSBuildThisFileDirectory)</c> names one file for every importer - except a
    /// bare file name, which names a file in each importer's own directory.
    /// </summary>
    private static IEnumerable<(InputKey Key, string Kind)> ItemInputs(string root, string file, bool isProject)
    {
        var fileDirectory = DirectoryOf(file);
        foreach (var element in Load(root, file).Descendants())
        {
            var kind = element.Name.LocalName;
            var values = new List<(string Label, string Value)>();
            if (kind == "Import")
            {
                if (element.Attribute("Sdk") is null && (string?)element.Attribute("Project") is { } project)
                    values.Add((kind, project));
            }
            else if (element.Parent?.Name.LocalName == "ItemGroup")
            {
                if ((string?)element.Attribute("Include") is { } include && NamesFiles(element))
                    values.Add((kind, include));
                if (Metadata(element, "HintPath") is { } hintPath)
                    values.Add((kind + " HintPath", hintPath));
            }
            foreach (var (label, value) in values)
                foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (kind == "Import" && ChainImport.IsMatch(part))
                        continue; // the ancestor chain, already counted as an implicit input
                    if (part.StartsWith(PackageCacheAnchor, StringComparison.Ordinal))
                        continue; // the NuGet package cache, outside the repository
                    if (kind != "Import" && part.IndexOfAny(['/', '\\', '$', '@', '%']) < 0 && part is not ("." or ".."))
                        continue; // a bare name: a file in the project's own directory, or in each importer's, which its glob covers
                    var path = StripAnchor(part, isProject, out var anchored);
                    if (path.Contains("$(", StringComparison.Ordinal) || path.Contains("@(", StringComparison.Ordinal)
                        || path.Contains("%(", StringComparison.Ordinal))
                        throw new InvalidDataException($"{file}: {label} '{part}' is not a literal path; this lens does not "
                            + "evaluate MSBuild (an item type whose Include names no file belongs in IdentityItemTypes)");
                    if (!anchored && kind != "Import" && !isProject)
                        throw new InvalidDataException($"{file}: {label} '{part}' resolves against each importing project; "
                            + "anchor it with $(MSBuildThisFileDirectory) so it names one file");
                    var segments = path.Replace('\\', '/').Split('/');
                    var wildcard = Array.FindIndex(segments, segment => segment.IndexOfAny(['*', '?']) >= 0);
                    var literal = string.Join('/', wildcard < 0 ? segments : segments.Take(wildcard));
                    var target = Resolve(fileDirectory, literal, file);
                    if (isProject && IsWithin(target, fileDirectory))
                        continue; // covered by the project's own directory glob
                    yield return (new InputKey(target, wildcard >= 0), label);
                }
        }
    }

    /// <summary>
    /// Whether an item's Include names files: every type but an identity type, and a None item only
    /// when it is copied to the output (a pack-only README is a packaging input, not a build or test input).
    /// </summary>
    private static bool NamesFiles(XElement item)
    {
        var kind = item.Name.LocalName;
        return !IdentityItemTypes.Contains(kind) && (kind != "None" || CopiesToOutput(item));
    }

    private static bool CopiesToOutput(XElement item)
    {
        var copy = Metadata(item, "CopyToOutputDirectory");
        return copy is not null && !copy.Trim().Equals("Never", StringComparison.OrdinalIgnoreCase);
    }

    // ---- coverage ---------------------------------------------------------------------------------

    private static List<string> UncoveredProjects(Graph graph, IReadOnlyList<string> globs, string list)
    {
        var problems = new List<string>();
        foreach (var (project, suites) in graph.ReachedBy)
        {
            var directory = DirectoryOf(project);
            if (!CoversDirectory(globs, directory, Path.GetFileName(project)))
                problems.Add($"{list}: project directory '{directory}/' ({Path.GetFileName(project)}) is built into "
                    + $"{suites.Count} discovered suite(s), e.g. {suites.Min}, but no glob covers it; add \"{directory}/**\" "
                    + "to on.push.paths and READINESS_PATHS at the same position");
        }
        return problems;
    }

    private static List<string> UncoveredInputs(Graph graph, Inputs inputs, IReadOnlyList<string> globs, string list)
    {
        // A closure project's first suite, or, for a shared MSBuild file, the first suite of its first reader.
        string SuiteOf(string declaredBy) => graph.ReachedBy.TryGetValue(declaredBy, out var suites)
            ? suites.Min! : SuiteOf(graph.SharedFiles[declaredBy].Min!);
        var problems = new List<string>();
        foreach (var (file, importers) in inputs.Implicit)
            if (!globs.Any(glob => Matches(glob, file)))
                problems.Add($"{list}: '{file}' is an implicit build input of {importers.Min} (in the closure of "
                    + $"{SuiteOf(importers.Min!)}) but no glob covers it; add \"{file}\" to both lists");
        foreach (var (key, declared) in inputs.CrossDirectory)
        {
            var covered = key.IsDirectory
                ? CoversDirectory(globs, key.Path, "Probe.cs")
                : globs.Any(glob => Matches(glob, key.Path));
            if (!covered)
                problems.Add($"{list}: '{key.Path}{(key.IsDirectory ? "/**" : "")}' is named by {string.Join(", ", declared.Kinds)} in "
                    + $"{declared.Files.Min} (in the closure of {SuiteOf(declared.Files.Min!)}) but no glob covers it");
        }
        return problems;
    }

    /// <summary>A directory is covered when one glob matches both a top-level and a deeply nested file in it.</summary>
    private static bool CoversDirectory(IReadOnlyList<string> globs, string directory, string leaf)
        => globs.Any(glob => Matches(glob, Join(directory, leaf)) && Matches(glob, Join(directory, "Nested/Deeper/Probe.cs")));

    /// <summary>
    /// Mirror of <c>AiPipelineCiRoutingConventionTests.Matches</c>: GitHub glob semantics for the
    /// shapes the readiness lists use (literals, basename <c>*</c>, a single trailing <c>/**</c>).
    /// GitHub's single <c>*</c> does not cross '/', unlike Bash's in the in-job matcher.
    /// </summary>
    private static bool Matches(string glob, string path)
    {
        if (!Regex.IsMatch(glob, @"^[A-Za-z0-9_./*\-]+$") || glob.Contains("***", StringComparison.Ordinal)
            || (glob.Contains("**", StringComparison.Ordinal) && glob != "**"
                && (!glob.EndsWith("/**", StringComparison.Ordinal)
                    || glob.IndexOf("**", StringComparison.Ordinal) != glob.Length - 2)))
            throw new InvalidDataException($"Unsupported path glob: {glob}");
        var pattern = Regex.Escape(glob).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*");
        return Regex.IsMatch(path, "\\A" + pattern + "\\z", RegexOptions.CultureInvariant);
    }

    // ---- the two readiness lists ------------------------------------------------------------------

    private static IEnumerable<(string List, IReadOnlyList<string> Globs)> ReadLists(string root)
    {
        var text = File.ReadAllText(Path.Combine(root, Readiness)).Replace("\r\n", "\n");
        YamlMappingNode workflow;
        try
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            workflow = (YamlMappingNode)yaml.Documents.Single().RootNode;
        }
        catch (YamlException ex) { throw new InvalidDataException("Unreadable readiness workflow YAML", ex); }

        var push = ((YamlSequenceNode)At(workflow, "on", "push", "paths")).Children
            .Select(node => ((YamlScalarNode)node).Value!).ToArray();
        var filter = ((YamlSequenceNode)At(workflow, "jobs", "changes", "steps")).Children.Cast<YamlMappingNode>()
            .Single(step => step.Children.TryGetValue(new YamlScalarNode("id"), out var id) && ((YamlScalarNode)id).Value == "filter");
        var internalPaths = ReadShellArray(((YamlScalarNode)At(filter, "run")).Value!);

        push.Should().NotBeEmpty();
        internalPaths.Should().NotBeEmpty();
        yield return ("on.push.paths", push);
        yield return ("READINESS_PATHS", internalPaths);
    }

    private static YamlNode At(YamlNode node, params string[] keys)
    {
        foreach (var key in keys)
            node = ((YamlMappingNode)node).Children.TryGetValue(new YamlScalarNode(key), out var next)
                ? next : throw new InvalidDataException($"Missing YAML key: {key}");
        return node;
    }

    /// <summary>
    /// The literal array's quoted entries. <c>AiPipelineCiRoutingConventionTests</c> owns the strict
    /// shape checks (one literal declaration, no other writes); this reader refuses what it cannot read.
    /// </summary>
    private static string[] ReadShellArray(string script)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim() == "READINESS_PATHS=(");
        if (start < 0)
            throw new InvalidDataException("No literal READINESS_PATHS array");
        var result = new List<string>();
        foreach (var raw in lines.Skip(start + 1))
        {
            var line = raw.Trim();
            if (line == ")")
                return result.ToArray();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var match = Regex.Match(line, "^\"([^\"]+)\"$");
            if (!match.Success)
                throw new InvalidDataException($"Unsupported READINESS_PATHS entry: {line}");
            result.Add(match.Groups[1].Value);
        }
        throw new InvalidDataException("Unterminated READINESS_PATHS");
    }

    // ---- paths ------------------------------------------------------------------------------------

    private static XDocument Load(string root, string relative)
    {
        try { return XDocument.Load(Path.Combine(root, relative)); }
        catch (System.Xml.XmlException ex) { throw new InvalidDataException($"{relative}: unreadable MSBuild XML", ex); }
    }

    private static string? Metadata(XElement item, string name)
        => (string?)item.Attribute(name) ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    /// <summary>Resolves a relative path against a repo-relative directory, refusing absolute or escaping paths.</summary>
    private static string Resolve(string baseDirectory, string relative, string declaredIn)
    {
        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith('/') || (normalized.Length > 1 && normalized[1] == ':'))
            throw new InvalidDataException($"{declaredIn}: '{relative}' is an absolute path");
        var segments = baseDirectory.Length == 0 ? new List<string>() : baseDirectory.Split('/').ToList();
        foreach (var segment in normalized.Split('/'))
        {
            if (segment is "" or ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    throw new InvalidDataException($"{declaredIn}: '{relative}' leaves the repository");
                segments.RemoveAt(segments.Count - 1);
            }
            else
                segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    private static bool IsWithin(string path, string directory)
        => directory.Length == 0 || path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal);

    private static string DirectoryOf(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash < 0 ? "" : relative[..slash];
    }

    private static string Join(string directory, string leaf) => directory.Length == 0 ? leaf : directory + "/" + leaf;

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Project(string items) => $"<Project><ItemGroup>{items}</ItemGroup></Project>";

    private sealed class TreeFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "readiness-closure-" + Guid.NewGuid().ToString("N"));

        public void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
