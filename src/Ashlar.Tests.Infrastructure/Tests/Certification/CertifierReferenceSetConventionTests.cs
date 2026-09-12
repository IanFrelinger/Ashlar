using Microsoft.CodeAnalysis;
using Mono.Cecil;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Freezes WHERE a Roslyn reference set may be built inside <c>Ashlar.Infrastructure</c>, and what
/// it may be built from. Two byte-identical private copies of an ambient reference set existed in
/// the certifier for as long as the certifier did; a convention that forbids one spelling in the two
/// files that were just repaired would only ever confirm the repair
/// (<c>docs/HowGatesGoQuiet.md</c> section 12).
///
/// <para><b>The population is derived, not listed.</b> Every type in the built
/// <c>Ashlar.Infrastructure.dll</c> is walked, and the match is on IL call targets rather than on
/// source text. #605's own scan was a text search over one project and was evaded three ways: a copy
/// one folder down, a different factory spelling, and <c>AssemblyMetadata</c> in place of
/// <c>MetadataReference</c>. An IL scan is immune to all three, and to a using-alias or a
/// fully-qualified call as well.</para>
///
/// <para><b>What this lens does NOT cover, stated here rather than implied by the name.</b>
/// (1) One assembly — <c>Ashlar.Infrastructure.dll</c>, which is where the certifier lives. A fourth
/// copy of the ambient set exists in <c>src/Ashlar.Tests.Application/Tests/Autonomy/</c>
/// <c>RecursionDisciplineTests.cs</c>; it is test code in another project owned by another gate, and
/// converging it needs its own anchor decision, so it is named here and deliberately left alone.
/// (2) An IL scan sees compiler-generated methods, so sites are de-mangled back to the source
/// method that wrote them — async and iterator state machines, lambdas, and local functions. The
/// last two were added because this scan found two real sites hiding under generated names on its
/// first run; the fact below drives that de-mangling rather than assuming it.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class CertifierReferenceSetConventionTests
{
    /// <summary>
    /// The ambient call the two certifier copies used, plus every way Roslyn hands back a
    /// reference — not only the spelling those copies happened to use.
    /// </summary>
    private static readonly HashSet<string> ReferenceSetApis = new(StringComparer.Ordinal)
    {
        "System.AppDomain::GetAssemblies",
        "Microsoft.CodeAnalysis.MetadataReference::CreateFromFile",
        "Microsoft.CodeAnalysis.MetadataReference::CreateFromStream",
        "Microsoft.CodeAnalysis.MetadataReference::CreateFromImage",
        "Microsoft.CodeAnalysis.AssemblyMetadata::CreateFromFile",
        "Microsoft.CodeAnalysis.AssemblyMetadata::CreateFromStream",
        "Microsoft.CodeAnalysis.AssemblyMetadata::CreateFromImage",
    };

    /// <summary>
    /// The freeze: every site in <c>Ashlar.Infrastructure</c> that touches one of the APIs above,
    /// with the reason it is allowed to. A site absent from this table is a regression; a row that
    /// no longer matches anything is a ghost. Both are failures — an inventory with only the first
    /// fact rots into a list of things that used to be true (section 7).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Inventory =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Ashlar.Infrastructure.Certification.CertifierReferenceSet::TryCreateManagedReference"] =
                "the one declared reference set the self-extend certifier compiles against",
            ["Ashlar.Infrastructure.Certification.GateEmittedArtifactCompiler::Compile"] =
                "the certification chain's own compile; its emitted bytes are hashed into the "
                + "record and pinned by golden corpora, so widening its reference set moves "
                + "certificate bytes. Declared (an explicit DomainBrick anchor), not ambient, and "
                + "converged separately or not at all.",
            ["Ashlar.Infrastructure.Testing.CodeAnalysis.RoslynCodeAnalysisService::BuildReferenceSet"] =
                "caller-supplied reference paths for the analyzer fence gate; deterministic, a "
                + "function of what the caller passed rather than of load order",
            ["Ashlar.Infrastructure.Testing.CodeAnalysis.RoslynCodeAnalysisService::GetDefaultReferences"] =
                "the fence gate's default set; derived from the runtime directory and the platform "
                + "list, never from loaded assemblies. AnalyzerFenceGate hard-fails when the Brick "
                + "anchor is not resolvable from it.",
            ["Ashlar.Infrastructure.Agent.Adapters.AgentExecutorAdapter::FindAgent"] =
                "agent DISCOVERY, not a verdict: the container is asked first and a miss degrades "
                + "to a logged not-found. Nothing is admitted, recorded or written on it.",
            ["Ashlar.Infrastructure.Testing.TestRunnerAdapter::DiscoverTests"] =
                "test DISCOVERY, not a verdict; supplemented by explicit Assembly.Load of named "
                + "assemblies",
        };

    /// <summary>
    /// Fact one — a NEW reference-set builder anywhere in the certifier's assembly fails — and
    /// fact two — a row that has stopped matching fails.
    /// </summary>
    [Fact]
    public void Reference_set_builders_in_the_certifier_stay_inside_the_frozen_inventory()
    {
        var (sites, typesWalked) = ScanForReferenceSetBuilders(CertifierAssemblyPath());

        typesWalked.Should().BeGreaterThan(
            500,
            "the scan must be reading the whole assembly. A population that collapses to nothing "
            + "passes a freeze while measuring nothing");

        var unexpected = sites.Keys.Where(site => !Inventory.ContainsKey(site)).ToArray();
        var stale = Inventory.Keys.Where(site => !sites.ContainsKey(site)).ToArray();

        unexpected.Should().BeEmpty(
            "a reference set built here decides whether a proposed change compiles. A new one must "
            + "be added to the inventory in this file with a reason, or — better — call "
            + "CertifierReferenceSet. Unexpected:\n" + Describe(sites, unexpected));
        stale.Should().BeEmpty(
            "the inventory lists sites that no longer build a reference set; shrink it rather than "
            + "keeping ghosts:\n" + string.Join("\n", stale));
    }

    /// <summary>
    /// Fact three, which drives the classifier the two facts above rest on. Without it the scan can
    /// stop recognising what it forbids and still report clean.
    ///
    /// <para>Two halves. The first is a live sacrificial site in THIS assembly — a method that
    /// really does build a reference set the forbidden way — which the same classifier must find.
    /// If a needle stops matching (an API renamed, a call inlined into a shape Cecil reports
    /// differently) this reddens here rather than leaving the freeze quietly green. The second
    /// asserts the lens actually contains the two types this whole change is about, so "no
    /// offenders" is a statement about them and not about an assembly that does not hold them.</para>
    /// </summary>
    [Fact]
    public void The_scan_still_recognises_a_reference_set_built_the_forbidden_way()
    {
        AmbientReferenceSetTheForbiddenWay().Should().NotBeEmpty(
            "the sacrificial site must really run, so the IL below is real code rather than a "
            + "comment the compiler was free to drop");
        AmbientReferenceSetInsideAClosure().Should().NotBeEmpty();

        var (ownSites, _) = ScanForReferenceSetBuilders(
            Path.Combine(AppContext.BaseDirectory, "Ashlar.Tests.Infrastructure.dll"));

        var here = typeof(CertifierReferenceSetConventionTests).FullName;

        var sacrificial = here + "::" + nameof(AmbientReferenceSetTheForbiddenWay);
        ownSites.Should().ContainKey(
            sacrificial,
            "the classifier must still match an ambient reference set when it sees one");
        ownSites[sacrificial].Should().Contain("System.AppDomain::GetAssemblies");
        ownSites[sacrificial].Should().Contain(
            "Microsoft.CodeAnalysis.MetadataReference::CreateFromFile");

        // The de-mangling half: the same construction written inside a lambda and a local function
        // must be reported under the SOURCE method, not under <>c::<M>b__N_M. Without this the
        // freeze silently stops being human-editable and every closure becomes a new "offender".
        var closured = here + "::" + nameof(AmbientReferenceSetInsideAClosure);
        ownSites.Should().ContainKey(
            closured,
            "a reference set built inside a closure or a local function must normalise back to the "
            + "method that wrote it. Sites seen in this assembly: "
            + string.Join(" | ", ownSites.Keys));

        var (_, typesWalked) = ScanForReferenceSetBuilders(CertifierAssemblyPath());
        typesWalked.Should().BeGreaterThan(500);

        var certifierTypes = AllTypeNames(CertifierAssemblyPath());
        certifierTypes.Should().Contain(
            "Ashlar.Infrastructure.Certification.RoslynExtensionCompileCheck",
            "the two types this convention was written for must be inside the scanned population, "
            + "or a clean result says nothing about them");
        certifierTypes.Should().Contain(
            "Ashlar.Infrastructure.Certification.RoslynPostApplyVerification");
    }

    /// <summary>
    /// The sacrificial site: exactly the construction both certifier copies used, kept alive here
    /// as the classifier's positive control and nowhere else. Not a reference set anything is judged
    /// against — it is thrown away by the caller above.
    /// </summary>
    /// <summary>
    /// The same construction hidden in a lambda and a local function — the two shapes that walked
    /// past this scan's first draft. Kept alive as the de-mangler's positive control.
    /// </summary>
    private static IReadOnlyList<MetadataReference> AmbientReferenceSetInsideAClosure()
    {
        MetadataReference Reference(string path) => MetadataReference.CreateFromFile(path);

        var locations = new Func<IEnumerable<string>>(
            () => AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => a.Location)
                .Take(1))();

        return locations.Select(Reference).ToArray();
    }

    private static IReadOnlyList<MetadataReference> AmbientReferenceSetTheForbiddenWay()
    {
        var refs = new List<MetadataReference>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                continue;
            refs.Add(MetadataReference.CreateFromFile(assembly.Location));
            if (refs.Count == 1)
                break;
        }

        return refs;
    }

    private static (IReadOnlyDictionary<string, IReadOnlyList<string>> Sites, int TypesWalked)
        ScanForReferenceSetBuilders(string assemblyPath)
    {
        File.Exists(assemblyPath).Should().BeTrue(
            $"the scan reads a built assembly; '{assemblyPath}' is not there, so it cannot run and "
            + "must not report a clean result");

        var sites = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var typesWalked = 0;

        using var module = ModuleDefinition.ReadModule(assemblyPath);
        foreach (var type in module.Types.SelectMany(Flatten))
        {
            typesWalked++;
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference callee)
                        continue;

                    var api = callee.DeclaringType.FullName + "::" + callee.Name;
                    if (!ReferenceSetApis.Contains(api))
                        continue;

                    var site = CertifierBoundaryScanTests.NormalizeSite(type.FullName, method.Name);
                    if (!sites.TryGetValue(site, out var apis))
                        sites[site] = apis = [];
                    if (!apis.Contains(api))
                        apis.Add(api);
                }
            }
        }

        return (
            sites.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value, StringComparer.Ordinal),
            typesWalked);
    }

    private static IReadOnlyList<string> AllTypeNames(string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        return module.Types.SelectMany(Flatten).Select(t => t.FullName).ToArray();
    }

    private static string Describe(
        IReadOnlyDictionary<string, IReadOnlyList<string>> sites, IEnumerable<string> keys)
        => string.Join("\n", keys.Select(key => $"  {key}\t{string.Join(", ", sites[key])}"));

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) =>
        type.NestedTypes.SelectMany(Flatten).Prepend(type);

    private static string CertifierAssemblyPath()
        => Path.Combine(AppContext.BaseDirectory, "Ashlar.Infrastructure.dll");
}
