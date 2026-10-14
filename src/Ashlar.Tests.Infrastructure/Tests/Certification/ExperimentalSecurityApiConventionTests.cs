using System.Reflection;
using System.Text.RegularExpressions;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The owner's decision of 2026-10-13 (SPEC-007 decisions log, two rows), held as a merge-blocking fact: the
/// security and deployment-profile surface <c>Ashlar.Abstractions</c> added after <c>v0.2.0</c> ships in
/// <c>0.3.0</c> as <c>[Experimental]</c>, under its own diagnostic id, <c>ASHLAREXP002</c>.
///
/// <para><b>The surface.</b> Every public type in <c>Ashlar.Abstractions.Security</c> and
/// <c>Ashlar.Abstractions.Security.Egress</c> (exactly those two namespaces), and
/// <c>Ashlar.Abstractions.AshlarResolvedDeploymentProfileOptions</c>; a type nested in one of them belongs to it.
/// On 60689412e that is 23 types, behind the 160 non-header lines of
/// <c>src/Ashlar.Abstractions/PublicAPI.Unshipped.txt</c>: 63 in <c>.Security</c>, 87 in <c>.Security.Egress</c>
/// and 10 for the deployment-profile type. The Substrate packages' SPEC-007 members are not in it: they stay
/// Substrate tier (an integrator decision, disclosed in the release notes).</para>
///
/// <para><b>What is asserted.</b> (1) Every surface type carries exactly one <c>ExperimentalAttribute</c>, with
/// the id <c>ASHLAREXP002</c> and the help link of the policy's <c>#ashlarexp002</c> section, and the policy has
/// that section. (2) Every line of either API file that names a symbol under <c>Ashlar.Abstractions.Security.</c>
/// or the deployment-profile type is owned by a surface type that passes (1), so no line of the surface is
/// promised without the attribute; a line no surface type owns fails, including one in a new namespace under
/// <c>.Security</c>, whose tier nobody has decided. (3) No other public type of the assembly carries
/// <c>ASHLAREXP002</c>, on the type or on a member: the id names this surface and nothing else. (4) No public or
/// protected member of a type outside the surface has a surface type in its signature, and no such type derives
/// from or implements one: such a member's line is not experimental, so the release promotion would ship it as
/// stable while the type it hands out may change in any release. (5) The id and the link are defined once, in
/// <c>SecurityExperimental</c>, which is internal and not experimental itself (integrator decision: a public
/// holder would add a stable line to the API file for two constants no consumer uses). (6) None of the surface's
/// lines is in <c>PublicAPI.Shipped.txt</c>: the owner decided on 2026-10-13 that <c>[Experimental]</c> lines stay
/// in <c>PublicAPI.Unshipped.txt</c> across the 0.3.0 tag, so the promotion cannot turn one into a promise.</para>
///
/// <para>Where a type-level attribute is impossible - a surface type exposed by a member of a type that is not
/// experimental - (3) and (4) both fail today rather than accept a member-level one, so the first such member is
/// a reviewed change to this file and to the decisions log, not a silent exception.</para>
///
/// <para><b>What this does NOT prove.</b> It reflects over the asset this test runs on (net8.0 or net10.0). The
/// <c>netstandard2.0</c> asset compiles the same declarations against the polyfilled attribute that
/// <c>Directory.Build.targets</c> links (no <c>#if</c> guards any of them), but nothing here reads that asset.
/// It checks declarations, not the compiler: whether a given use reports <c>ASHLAREXP002</c> is the compiler's
/// rule. Members a type inherits from outside the assembly are not its surface and are not read. How Ashlar's own
/// projects opt in is governed by <c>DiagnosticSuppressionConventionTests</c>, not here.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class ExperimentalSecurityApiConventionTests
{
    private const string DiagnosticId = "ASHLAREXP002";
    private const string PolicyDoc = "docs/SdkCompatibilityPolicy.md";
    private const string HelpUrl = "https://github.com/IanFrelinger/Ashlar/blob/master/" + PolicyDoc + "#ashlarexp002";
    private const string ExperimentalAttributeName = "System.Diagnostics.CodeAnalysis.ExperimentalAttribute";
    private const string HolderName = "Ashlar.Abstractions.Security.SecurityExperimental";
    private const string SecurityNamespace = "Ashlar.Abstractions.Security";
    private const string EgressNamespace = "Ashlar.Abstractions.Security.Egress";
    private const string DeploymentProfileType = "Ashlar.Abstractions.AshlarResolvedDeploymentProfileOptions";
    private const string ApiDirectory = "src/Ashlar.Abstractions";

    // Non-vacuity floors. STATED here and lowered only by a visible diff: a count derived from the tree moves
    // down with the tree and can never fire. Measured on 60689412e: 23 surface types, 160 surface lines (all in
    // PublicAPI.Unshipped.txt), 84 type lines across both API files. An experimental type may be removed in any
    // release, so the floors sit under those numbers, and above what a broken walk or parser yields (zero).
    private const int SurfaceTypesFloor = 20;
    private const int SurfaceLinesFloor = 140;
    private const int ExportedTypesFloor = 70;

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly Abstractions = typeof(ToolResult).Assembly;

    private static readonly Regex LeadingModifiers = new(
        @"^(?:(?:abstract|override|static|const|virtual|sealed|readonly|new|extern)\s+)+", RegexOptions.CultureInvariant);

    private static readonly Regex Heading = new(@"^#{1,6}[ \t]+(?<text>.+?)[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly ITestOutputHelper _output;

    public ExperimentalSecurityApiConventionTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------------------------------------
    // The real assembly
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_public_type_of_the_surface_is_experimental_under_ASHLAREXP002()
    {
        Abstractions.GetName().Name.Should().Be("Ashlar.Abstractions", "the anchor type must load the assembly under test");
        var exported = Abstractions.GetExportedTypes();
        exported.Length.Should().BeGreaterThanOrEqualTo(ExportedTypesFloor,
            "the assembly exports {0} types; below the stated floor of {1} the walk is broken", exported.Length, ExportedTypesFloor);

        var surface = exported.Where(InSurface).OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
        surface.Count.Should().BeGreaterThanOrEqualTo(SurfaceTypesFloor,
            "{0} surface types found; below the stated floor of {1} the surface predicate is broken", surface.Count, SurfaceTypesFloor);
        surface.Should().Contain(t => t.FullName == DeploymentProfileType, "the 2026-10-13 row names this type");
        surface.Should().Contain(t => t.Namespace == SecurityNamespace).And.Contain(t => t.Namespace == EgressNamespace);

        var problems = surface
            .Select(t => (Type: t, Verdict: Verdict(ReadExperimental(t))))
            .Where(x => x.Verdict is not null)
            .Select(x => $"{x.Type.FullName} {x.Verdict}")
            .ToList();

        problems.Should().BeEmpty(
            "the owner decided on 2026-10-13 that this surface ships [Experimental] under {0}, with the help link "
            + "{1} (SPEC-007 decisions log; docs/SdkCompatibilityPolicy.md). Apply "
            + "[Experimental(SecurityExperimental.DiagnosticId, UrlFormat = SecurityExperimental.UrlFormat)]. "
            + "{2} of {3} surface types fail:\n{4}",
            DiagnosticId, HelpUrl, problems.Count, surface.Count, string.Join("\n", problems));

        _output.WriteLine($"exported-types={exported.Length} surface-types={surface.Count}");
        foreach (var t in surface)
            _output.WriteLine($"  {t.FullName}");
    }

    [Fact]
    public void Every_api_line_of_the_surface_belongs_to_an_ASHLAREXP002_type()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var surface = new List<(string File, string Line, string Symbol)>();
        foreach (var file in new[] { "PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt" })
        {
            foreach (var line in File.ReadAllLines(Path.Combine(root, ApiDirectory, file)))
            {
                if (SurfaceSymbol(line) is { } symbol)
                    surface.Add((file, line.Trim(), symbol));
            }
        }

        surface.Count.Should().BeGreaterThanOrEqualTo(SurfaceLinesFloor,
            "{0} API lines name a surface symbol; below the stated floor of {1} the line parser is broken",
            surface.Count, SurfaceLinesFloor);

        var problems = new List<string>();
        var owners = new HashSet<Type>();
        foreach (var (file, line, symbol) in surface)
        {
            var owner = OwnerType(Abstractions, symbol);
            if (owner is null)
            {
                problems.Add($"{file}: '{line}': no type of Ashlar.Abstractions owns {symbol}");
                continue;
            }

            owners.Add(owner);
            if (!InSurface(owner))
            {
                problems.Add($"{file}: '{line}': its owner {owner.FullName} is outside the decided surface; "
                    + "decide its tier in the SPEC-007 decisions log, then extend this test");
                continue;
            }

            if (Verdict(ReadExperimental(owner)) is { } verdict)
                problems.Add($"{file}: '{line}': {owner.FullName} {verdict}");
        }

        problems.Should().BeEmpty(
            "every promised line of the surface must be covered by an [Experimental({0})] type, or the promotion "
            + "at the tag ships it as stable. {1} of {2} lines fail:\n{3}",
            DiagnosticId, problems.Count, surface.Count, string.Join("\n", problems));

        _output.WriteLine($"surface-lines={surface.Count} "
            + $"shipped={surface.Count(s => s.File == "PublicAPI.Shipped.txt")} "
            + $"unshipped={surface.Count(s => s.File == "PublicAPI.Unshipped.txt")} owners={owners.Count}");
    }

    [Fact]
    public void No_line_of_the_surface_is_promoted_to_PublicAPI_Shipped()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var unshipped = File.ReadAllLines(Path.Combine(root, ApiDirectory, "PublicAPI.Unshipped.txt"))
            .Count(l => SurfaceSymbol(l) is not null);
        unshipped.Should().BeGreaterThanOrEqualTo(SurfaceLinesFloor,
            "{0} lines of PublicAPI.Unshipped.txt name a surface symbol; below the stated floor of {1} the read is "
            + "broken, and an empty read would pass the check below", unshipped, SurfaceLinesFloor);

        var shipped = File.ReadAllLines(Path.Combine(root, ApiDirectory, "PublicAPI.Shipped.txt"))
            .Where(l => SurfaceSymbol(l) is not null)
            .Select(l => l.Trim())
            .ToList();

        shipped.Should().BeEmpty(
            "the owner decided on 2026-10-13 that [Experimental] symbols stay in PublicAPI.Unshipped.txt across the "
            + "0.3.0 tag, and that the promotion moves only non-experimental lines, so a Shipped line stays a promise "
            + "(SPEC-007 decisions log, 'Release (public API)'). A symbol promoted to stable loses its attribute "
            + "first, which is a change to this test. Move these back to PublicAPI.Unshipped.txt. {0} found:\n{1}",
            shipped.Count, string.Join("\n", shipped));
    }

    [Fact]
    public void No_public_type_outside_the_surface_carries_ASHLAREXP002()
    {
        var outside = Abstractions.GetExportedTypes().Where(t => !InSurface(t)).ToList();
        outside.Count.Should().BeGreaterThanOrEqualTo(ExportedTypesFloor - SurfaceTypesFloor - 10,
            "{0} exported types outside the surface; a walk that finds almost none proves nothing", outside.Count);

        var problems = new List<string>();
        foreach (var type in outside)
        {
            if (ReadExperimental(type).Any(a => a.Id == DiagnosticId))
                problems.Add($"{type.FullName}: the type carries {DiagnosticId}");
            foreach (var member in type.GetMembers(Declared).Where(IsVisibleOutside))
            {
                if (ReadExperimental(member).Any(a => a.Id == DiagnosticId))
                    problems.Add($"{type.FullName}.{member.Name}: the member carries {DiagnosticId}");
            }
        }

        problems.Should().BeEmpty(
            "{0} names the security and deployment-profile surface and nothing else; a new experimental surface "
            + "gets its own id, constant and policy row (docs/SdkCompatibilityPolicy.md). {1} found:\n{2}",
            DiagnosticId, problems.Count, string.Join("\n", problems));

        _output.WriteLine($"outside-types={outside.Count}");
    }

    [Fact]
    public void No_public_member_outside_the_surface_exposes_a_surface_type()
    {
        var outside = Abstractions.GetExportedTypes().Where(t => !InSurface(t)).ToList();
        outside.Count.Should().BeGreaterThanOrEqualTo(ExportedTypesFloor - SurfaceTypesFloor - 10,
            "{0} exported types outside the surface; a walk that finds almost none proves nothing", outside.Count);

        var problems = outside.SelectMany(Exposures).ToList();

        problems.Should().BeEmpty(
            "a member that is not experimental is promised as stable at the next tag; one that hands out an "
            + "[Experimental({0})] type promises nothing about that type. Keep the surface behind the surface, or "
            + "record the exception in the SPEC-007 decisions log first. {1} found:\n{2}",
            DiagnosticId, problems.Count, string.Join("\n", problems));
    }

    [Fact]
    public void The_id_and_its_help_link_are_defined_once_in_an_internal_holder_that_is_not_experimental()
    {
        var holder = Abstractions.GetType(HolderName, throwOnError: false);
        holder.Should().NotBeNull("{0} defines {1} and its help link, as AutonomyExperimental does for ASHLAREXP001",
            HolderName, DiagnosticId);
        holder!.IsVisible.Should().BeFalse(
            "integrator decision: the holder is internal. A public one would add a line to PublicAPI.Unshipped.txt that "
            + "is not experimental, which the release promotion would ship as a stable promise of two constants");
        ReadExperimental(holder).Should().BeEmpty(
            "an attribute binds its arguments in its declaration's scope, so an experimental holder trips the id it names");
        Constant(holder, "DiagnosticId").Should().Be(DiagnosticId);
        Constant(holder, "UrlFormat").Should().Be(HelpUrl);
    }

    [Fact]
    public void The_help_link_names_a_section_the_policy_has()
    {
        var doc = File.ReadAllText(Path.Combine(RepoPathResolver.FindRepoRoot(), PolicyDoc));
        var anchors = Heading.Matches(doc).Select(m => GitHubAnchor(m.Groups["text"].Value)).ToHashSet(StringComparer.Ordinal);

        anchors.Should().Contain("ashlarexp001",
            "the existing ASHLAREXP001 section must be found; a heading reader that finds nothing proves nothing");
        anchors.Should().Contain(HelpUrl[(HelpUrl.IndexOf('#') + 1)..],
            "every ASHLAREXP002 diagnostic links to {0}; the policy needs that section (what the id covers, why, and "
            + "how to opt in)", HelpUrl);
    }

    // ------------------------------------------------------------------------------------------------
    // Controls: the pure parts, over fixed inputs. Negative controls must be caught; positive ones not.
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ASHLAREXP002", HelpUrl, null)]
    [InlineData("ASHLAREXP001", HelpUrl, "not ASHLAREXP002")]
    [InlineData("ashlarexp002", HelpUrl, "not ASHLAREXP002")]
    [InlineData("ASHLAREXP002", null, "UrlFormat")]
    [InlineData("ASHLAREXP002", "https://github.com/IanFrelinger/Ashlar/blob/master/docs/SdkCompatibilityPolicy.md#ashlarexp001", "UrlFormat")]
    public void The_verdict_accepts_only_the_id_and_the_link(string? id, string? url, string? expected)
    {
        var verdict = Verdict([(id, url)]);
        if (expected is null)
            verdict.Should().BeNull();
        else
            verdict.Should().Contain(expected);
    }

    [Fact]
    public void The_verdict_refuses_no_attribute_and_two()
    {
        Verdict([]).Should().Contain("no [Experimental]");
        Verdict([(DiagnosticId, HelpUrl), (DiagnosticId, HelpUrl)]).Should().Contain("2 [Experimental]");
    }

    [Theory]
    [InlineData("Ashlar.Abstractions.Security.SecurityLabel", "Ashlar.Abstractions.Security.SecurityLabel")]
    [InlineData("static Ashlar.Abstractions.Security.SecurityLabel.operator ==(Ashlar.Abstractions.Security.SecurityLabel? left, Ashlar.Abstractions.Security.SecurityLabel? right) -> bool", "Ashlar.Abstractions.Security.SecurityLabel")]
    [InlineData("const Ashlar.Abstractions.AshlarResolvedDeploymentProfileOptions.Full = \"full\" -> string!", DeploymentProfileType)]
    [InlineData("Ashlar.Abstractions.AshlarResolvedDeploymentProfileOptions.Profile.set -> void", DeploymentProfileType)]
    [InlineData("Ashlar.Abstractions.Security.Egress.EgressDecision.Refuses.get -> bool", "Ashlar.Abstractions.Security.Egress.EgressDecision")]
    [InlineData("override Ashlar.Abstractions.Security.SecurityLabelJsonConverter.Read(ref System.Text.Json.Utf8JsonReader reader, System.Type! typeToConvert, System.Text.Json.JsonSerializerOptions! options) -> Ashlar.Abstractions.Security.SecurityLabel!", "Ashlar.Abstractions.Security.SecurityLabelJsonConverter")]
    [InlineData("Ashlar.Abstractions.Security.AccessDecision.AccessDecision() -> void", "Ashlar.Abstractions.Security.AccessDecision")]
    [InlineData("Ashlar.Abstractions.Security.AccessDenialReason.LevelTooLow = 1 -> Ashlar.Abstractions.Security.AccessDenialReason", "Ashlar.Abstractions.Security.AccessDenialReason")]
    public void A_surface_line_resolves_to_its_owning_type(string line, string owner)
    {
        var symbol = SurfaceSymbol(line);
        symbol.Should().NotBeNull("this line names a surface symbol: {0}", line);
        var resolved = OwnerType(Abstractions, symbol!);
        resolved.Should().NotBeNull("a type of Ashlar.Abstractions owns {0}", symbol);
        resolved!.FullName.Should().Be(owner);
    }

    [Theory]
    [InlineData("#nullable enable")]
    [InlineData("")]
    [InlineData("Ashlar.Abstractions.ToolResult")]
    [InlineData("Ashlar.Abstractions.ITool.Name.get -> string!")]
    [InlineData("*REMOVED*Ashlar.Abstractions.Security.SecurityLabel.Gone() -> void")]
    [InlineData("Ashlar.Abstractions.AshlarResolvedDeploymentProfileOptionsExtra")]
    public void A_line_outside_the_surface_is_not_read_as_one(string line)
    {
        SurfaceSymbol(line).Should().BeNull("this line names no surface symbol: {0}", line);
    }

    [Fact]
    public void A_line_in_an_undecided_namespace_under_security_is_read_and_owned_by_nothing()
    {
        var symbol = SurfaceSymbol("Ashlar.Abstractions.Security.Labels.NewThing.Value.get -> int");
        symbol.Should().Be("Ashlar.Abstractions.Security.Labels.NewThing.Value.get");
        OwnerType(Abstractions, symbol!).Should().BeNull("no such type exists, so the real fact reports the line");
    }

    [Theory]
    [InlineData(typeof(LeaksAProperty), "LeaksAProperty.Label: exposes")]
    [InlineData(typeof(LeaksThroughAGeneric), "LeaksThroughAGeneric.Decisions: exposes")]
    [InlineData(typeof(LeaksThroughARefParameter), "LeaksThroughARefParameter.Take: exposes")]
    [InlineData(typeof(LeaksToSubclasses), "LeaksToSubclasses.Levels: exposes")]
    [InlineData(typeof(LeaksThroughAConstructor), "LeaksThroughAConstructor..ctor: exposes")]
    [InlineData(typeof(ImplementsASurfaceInterface), "ImplementsASurfaceInterface: implements")]
    public void A_member_that_exposes_the_surface_is_caught(Type fixture, string expected)
    {
        Exposures(fixture).Should().Contain(p => p.Contains(expected, StringComparison.Ordinal),
            "{0} hands out a surface type", fixture.Name);
    }

    [Theory]
    [InlineData(typeof(KeepsItInside))]
    [InlineData(typeof(MentionsNothing))]
    public void A_type_that_keeps_the_surface_inside_passes(Type fixture)
    {
        Exposures(fixture).Should().BeEmpty("{0} exposes no surface type to a consumer", fixture.Name);
    }

    [Theory]
    [InlineData("ASHLAREXP002", "ashlarexp002")]
    [InlineData("ASHLAREXP001", "ashlarexp001")]
    [InlineData("Release step: promote Unshipped -> Shipped on tag", "release-step-promote-unshipped---shipped-on-tag")]
    [InlineData("`netstandard2.0` targets", "netstandard20-targets")]
    public void The_anchor_is_the_one_github_generates(string heading, string anchor)
    {
        GitHubAnchor(heading).Should().Be(anchor);
    }

    // ------------------------------------------------------------------------------------------------
    // The rules
    // ------------------------------------------------------------------------------------------------

    private static bool InSurface(Type type)
    {
        var outer = type;
        while (outer.DeclaringType is not null)
            outer = outer.DeclaringType;
        return outer.Assembly == Abstractions
            && (outer.Namespace is SecurityNamespace or EgressNamespace || outer.FullName == DeploymentProfileType);
    }

    private static string? Verdict(IReadOnlyList<(string? Id, string? Url)> attributes)
    {
        if (attributes.Count == 0)
            return "carries no [Experimental]";
        if (attributes.Count > 1)
            return $"carries {attributes.Count} [Experimental] attributes";
        var (id, url) = attributes[0];
        if (!string.Equals(id, DiagnosticId, StringComparison.Ordinal))
            return $"is [Experimental(\"{id}\")], not {DiagnosticId}";
        if (!string.Equals(url, HelpUrl, StringComparison.Ordinal))
            return $"carries UrlFormat '{url ?? "(none)"}', not {HelpUrl}";
        return null;
    }

    /// <summary>Every <c>ExperimentalAttribute</c> on a member, read by name so the BCL type and a polyfill both count.</summary>
    private static List<(string? Id, string? Url)> ReadExperimental(MemberInfo member)
    {
        var found = new List<(string? Id, string? Url)>();
        foreach (var data in member.GetCustomAttributesData())
        {
            if (data.AttributeType.FullName != ExperimentalAttributeName)
                continue;
            var id = data.ConstructorArguments.Count > 0 ? data.ConstructorArguments[0].Value as string : null;
            string? url = null;
            foreach (var named in data.NamedArguments)
            {
                if (named.MemberName == "UrlFormat")
                    url = named.TypedValue.Value as string;
            }

            found.Add((id, url));
        }

        return found;
    }

    /// <summary>
    /// The symbol an API-file line declares, when it is under <c>Ashlar.Abstractions.Security.</c> or is the
    /// deployment-profile type or one of its members; otherwise null. Removed lines are not promised and are skipped.
    /// </summary>
    private static string? SurfaceSymbol(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text[0] == '#' || text.StartsWith("*REMOVED*", StringComparison.Ordinal))
            return null;
        var symbol = LeadingModifiers.Replace(text, "");
        var end = symbol.IndexOfAny([' ', '(']);
        if (end >= 0)
            symbol = symbol[..end];
        var inSurface = symbol.StartsWith(SecurityNamespace + ".", StringComparison.Ordinal)
            || symbol == DeploymentProfileType
            || symbol.StartsWith(DeploymentProfileType + ".", StringComparison.Ordinal);
        return inSurface ? symbol : null;
    }

    /// <summary>
    /// The type that owns an API-file symbol: the symbol itself when it names a type, otherwise the longest dotted
    /// prefix that does. A nested type is spelled with '+' to reflection, so every namespace/nesting split is tried.
    /// </summary>
    private static Type? OwnerType(Assembly assembly, string symbol)
    {
        var segments = symbol.Split('.');
        for (var take = segments.Length; take > 1; take--)
        {
            for (var nsLength = take - 1; nsLength >= 1; nsLength--)
            {
                var name = string.Join('.', segments[..nsLength]) + "." + string.Join('+', segments[nsLength..take]);
                if (assembly.GetType(name, throwOnError: false) is { } type)
                    return type;
            }
        }

        return null;
    }

    /// <summary>Where a type outside the surface lets a consumer reach a surface type through its public shape.</summary>
    private static IEnumerable<string> Exposures(Type type)
    {
        var name = type.Name;
        if (type.BaseType is { } baseType && Mentions(baseType) is { } inBase)
            yield return $"{name}: derives from {inBase.FullName}";
        foreach (var contract in type.GetInterfaces())
        {
            if (Mentions(contract) is { } inContract)
                yield return $"{name}: implements {inContract.FullName}";
        }

        foreach (var member in type.GetMembers(Declared).Where(IsVisibleOutside))
        {
            if (member is MethodInfo { IsSpecialName: true } accessor
                && (accessor.Name.StartsWith("get_", StringComparison.Ordinal) || accessor.Name.StartsWith("set_", StringComparison.Ordinal)
                    || accessor.Name.StartsWith("add_", StringComparison.Ordinal) || accessor.Name.StartsWith("remove_", StringComparison.Ordinal)))
                continue; // reported through the property or event

            foreach (var exposed in SignatureTypes(member).Select(Mentions).OfType<Type>().Distinct())
                yield return $"{name}.{member.Name}: exposes {exposed.FullName}";
        }
    }

    private static IEnumerable<Type> SignatureTypes(MemberInfo member)
    {
        switch (member)
        {
            case FieldInfo f:
                return new[] { f.FieldType };
            case PropertyInfo p:
                return p.GetIndexParameters().Select(i => i.ParameterType).Prepend(p.PropertyType);
            case MethodInfo m:
                return m.GetParameters().Select(i => i.ParameterType).Prepend(m.ReturnType);
            case ConstructorInfo c:
                return c.GetParameters().Select(i => i.ParameterType);
            case EventInfo { EventHandlerType: { } handler }:
                return new[] { handler };
            default:
                return Array.Empty<Type>();
        }
    }

    /// <summary>The first surface type a type is or is built from (element type, generic arguments), or null.</summary>
    private static Type? Mentions(Type type)
    {
        if (type.HasElementType)
            return Mentions(type.GetElementType()!);
        if (type.IsGenericParameter)
            return null;
        if (InSurface(type))
            return type;
        return type.IsGenericType ? type.GetGenericArguments().Select(Mentions).FirstOrDefault(t => t is not null) : null;
    }

    private static bool IsVisibleOutside(MemberInfo member) => member switch
    {
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
        PropertyInfo p => p.GetAccessors(nonPublic: true).Any(a => a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly),
        EventInfo e => e.AddMethod is { } add && (add.IsPublic || add.IsFamily || add.IsFamilyOrAssembly),
        _ => false,
    };

    private static object? Constant(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetRawConstantValue();

    /// <summary>GitHub's heading anchor: lower case, punctuation other than '-' and '_' dropped, spaces to '-'.</summary>
    private static string GitHubAnchor(string heading)
    {
        var kept = heading.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')
            .ToArray();
        return new string(kept).Replace(' ', '-');
    }

    // ------------------------------------------------------------------------------------------------
    // Fixtures for the exposure controls
    // ------------------------------------------------------------------------------------------------

    private sealed class LeaksAProperty
    {
        public SecurityLabel Label { get; } = SecurityLabel.Public;
    }

    private sealed class LeaksThroughAGeneric
    {
        public IReadOnlyList<EgressDecision> Decisions() => [];
    }

    private sealed class LeaksThroughARefParameter
    {
        public static void Take(ref AccessDecision decision) => decision = default;
    }

    private abstract class LeaksToSubclasses
    {
        protected SecurityLevel[] Levels = [];

        public int Count => Levels.Length;
    }

    private sealed class LeaksThroughAConstructor
    {
        public LeaksThroughAConstructor(HighWaterMark mark) => Mark = mark.Current.ToString();

        public string Mark { get; }
    }

    private sealed class ImplementsASurfaceInterface : IEgressDecisionSink
    {
        void IEgressDecisionSink.Record(EgressDecision decision)
        {
        }
    }

    private sealed class KeepsItInside
    {
        private readonly SecurityLabel _label = SecurityLabel.Public;

        internal SecurityLabel Internal => _label;

        public string Text => _label.ToString();
    }

    private sealed class MentionsNothing
    {
        public string Name { get; } = "";

        public int Count(IEnumerable<string> items) => items.Count();
    }
}
