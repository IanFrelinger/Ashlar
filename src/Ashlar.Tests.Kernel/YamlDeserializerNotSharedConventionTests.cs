using System.Reflection;
using Ashlar.Manifest;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// No static field in <c>Ashlar.Manifest</c> may hold a YamlDotNet type.
///
/// <para>A YamlDotNet 13.7.1 deserializer caches per-type lookups in a plain <c>Dictionary</c> that it
/// writes, unlocked, on the first parse of each type. Shared across threads, two cold first parses
/// corrupt it, and the loader rejects a VALID policy or manifest as "could not be parsed". That is the
/// intermittent red of <c>SelfExtendAdmissionBridgeTests</c>, <c>ManifestContractTests</c> and
/// <c>ProjectVerifierTests</c> in the readiness gate, and a spurious fail-closed rejection in any
/// process that parses two documents at once for the first time. Both loaders now build a new
/// deserializer per parse.</para>
///
/// <para><b>Why a structural rule and not only the race test.</b>
/// <c>PolicyLoaderColdStartConcurrencyTests</c> reproduces the race, and catches an eagerly shared
/// deserializer or a cached builder. It does not catch a lazily assigned one
/// (<c>x ??= new DeserializerBuilder()...Build()</c>): its barrier releases every thread while the field
/// is still null, so each thread builds its own and nothing collides - yet in production the first caller
/// to finish publishes one instance that later cold callers share. Measured: that mutation stayed green 20
/// of 20 runs. Every caching shape needs a field, and every such field is found here, so this rule has no
/// such gap. A per-parse deserializer costs nothing that matters: a document is parsed once per command
/// or self-extend cycle.</para>
/// </summary>
public sealed class YamlDeserializerNotSharedConventionTests
{
    private const string YamlDotNet = "YamlDotNet";

    private static readonly Assembly ManifestAssembly = typeof(PolicyLoader).Assembly;

    /// <summary>41 types were declared in <c>src/Ashlar.Manifest</c> on 2026-09-30. The floor sits
    /// below that so ordinary additions and removals never trip it; it exists to fail a scan of the
    /// wrong or an empty assembly, which would report "no offenders" having looked at nothing.</summary>
    private const int MinimumTypesScanned = 30;

    [Fact]
    public void No_static_field_in_Ashlar_Manifest_holds_a_YamlDotNet_type()
    {
        ManifestAssembly.GetReferencedAssemblies().Select(a => a.Name)
            .Should().Contain(YamlDotNet,
                "the rule is only meaningful while Ashlar.Manifest parses YAML; if it stops, delete this test");

        var types = ManifestAssembly.GetTypes();
        types.Length.Should().BeGreaterThanOrEqualTo(MinimumTypesScanned,
            "a scan that finds no types proves nothing");

        var offenders = types
            .SelectMany(t => t.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => MentionsYamlDotNet(f.FieldType))
            .Select(f => $"{f.DeclaringType!.FullName}.{f.Name} : {f.FieldType}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "a YamlDotNet 13.7.1 deserializer, builder or object factory held in a static field is shared by "
            + "every thread, and two concurrent first parses corrupt its type cache so a VALID document is "
            + "rejected. Build a new one per parse, as PolicyLoader.NewDeserializer and "
            + "ManifestLoader.NewDeserializer do. Upstream made the cache concurrent in YamlDotNet 16.1.0; "
            + "if the pin moves past that, revisit this rule rather than suppressing it.");
    }

    /// <summary>The predicate must recognise every way a deserializer can be cached, or the rule above is
    /// narrower than it reads. Each shape here was, or would be, a real field type.</summary>
    [Fact]
    public void The_predicate_recognises_every_caching_shape_and_nothing_else()
    {
        var yaml = Assembly.Load(new AssemblyName(YamlDotNet));
        var deserializer = yaml.GetType("YamlDotNet.Serialization.IDeserializer", throwOnError: true)!;
        var builder = yaml.GetType("YamlDotNet.Serialization.DeserializerBuilder", throwOnError: true)!;
        var factory = yaml.GetType("YamlDotNet.Serialization.IObjectFactory", throwOnError: true)!;

        MentionsYamlDotNet(deserializer).Should().BeTrue("an eager or ??=-assigned static deserializer");
        MentionsYamlDotNet(builder).Should().BeTrue("a cached builder shares its lazily built object factory");
        MentionsYamlDotNet(factory).Should().BeTrue("the object factory is the unsafe cache itself");
        MentionsYamlDotNet(typeof(Lazy<>).MakeGenericType(deserializer)).Should().BeTrue("Lazy<IDeserializer>");
        MentionsYamlDotNet(typeof(Func<>).MakeGenericType(deserializer)).Should().BeTrue("a cached factory delegate");
        MentionsYamlDotNet(deserializer.MakeArrayType()).Should().BeTrue("a pool");
        MentionsYamlDotNet(typeof(Dictionary<,>).MakeGenericType(typeof(string), deserializer))
            .Should().BeTrue("a keyed cache");

        MentionsYamlDotNet(typeof(string)).Should().BeFalse();
        MentionsYamlDotNet(typeof(IReadOnlyList<string>)).Should().BeFalse();
        MentionsYamlDotNet(typeof(Lazy<Dictionary<string, object>>)).Should().BeFalse();
    }

    private static bool MentionsYamlDotNet(Type type)
    {
        if (type.Assembly.GetName().Name == YamlDotNet)
        {
            return true;
        }

        if (type.HasElementType && MentionsYamlDotNet(type.GetElementType()!))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(MentionsYamlDotNet);
    }
}
