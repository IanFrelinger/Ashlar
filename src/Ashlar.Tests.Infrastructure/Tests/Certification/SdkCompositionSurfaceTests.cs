using FluentAssertions;
using Mono.Cecil;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Every DI extension class in a packed assembly stays PUBLIC.
///
/// <para><b>The defect this exists for was introduced by an automated sweep and caught by hand.</b>
/// A sweep that internalised "types no other assembly references" proposed
/// <c>ObservationServiceCollectionExtensions</c> and <c>RollbackServiceCollectionExtensions</c>,
/// because no other assembly IN THIS REPOSITORY calls <c>AddObservationCore</c> or
/// <c>AddRollbackInfrastructure</c> - the only in-repo callers live in Ashlar.Infrastructure itself.
/// The compiler was happy. Every test passed. And
/// <c>docs/architecture/SdkStructure.md</c> names those two methods as SDK composition surface, so
/// the change would have deleted two documented entry points from a package that ships.</para>
///
/// <para><b>Why "unreferenced" cannot decide this.</b> A DI extension method exists to be called by a
/// CONSUMER assembling a host. Its in-repo reference count is therefore expected to be low or zero,
/// and is evidence of nothing. That is the same reason <c>Ashlar.Policies</c> shows all three of its
/// public types unreferenced: they are the product, not detritus.</para>
///
/// <para><b>Why the rule is keyed on the type name and not the folder.</b> SdkStructure.md states
/// every DI extension file sits under <c>Feature/Sdk/Extensions/</c>. Three do not
/// (<c>Autonomy</c>, <c>Environments</c>, <c>MeshLab</c>), so a path-keyed rule would silently skip
/// them. The suffix is what the convention actually guarantees.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class SdkCompositionSurfaceTests
{
    private const string PackedAssembly = "Ashlar.Infrastructure.dll";

    /// <summary>
    /// Fewer than this many means the scan stopped seeing the convention, not that the convention
    /// stopped being violated. Chosen below the count at the time of writing (25) so ordinary
    /// additions and removals do not trip it.
    /// </summary>
    private const int MinimumExtensionClasses = 20;

    [Fact]
    public void EveryServiceCollectionExtensionsClassInAPackedAssemblyIsPublic()
    {
        var path = Path.Combine(AppContext.BaseDirectory, PackedAssembly);
        File.Exists(path).Should().BeTrue(
            "{0} must sit beside the test assembly, or this fact scans nothing and passes vacuously",
            PackedAssembly);

        using var module = ModuleDefinition.ReadModule(path);

        var extensionClasses = module.Types
            .Where(t => t.Name.EndsWith("ServiceCollectionExtensions", StringComparison.Ordinal))
            .ToList();

        // POSITIVE CONTROL: a rename of the convention, or a change in how Cecil enumerates types,
        // would leave this list empty - and an empty list satisfies the assertion below.
        extensionClasses.Should().HaveCountGreaterThanOrEqualTo(
            MinimumExtensionClasses,
            "the scan found {0} *ServiceCollectionExtensions type(s) in {1}. Below {2} the convention "
            + "has moved and this fact is inspecting nothing. Fix the scan; do not lower the bound.",
            extensionClasses.Count, PackedAssembly, MinimumExtensionClasses);

        var hidden = extensionClasses
            .Where(t => !t.IsPublic)
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        hidden.Should().BeEmpty(
            "a *ServiceCollectionExtensions class is how a consumer composes this package into their "
            + "host. Making one internal removes an entry point from a package that ships, and "
            + "nothing else notices: the in-repo callers are all inside this same assembly, so it "
            + "still compiles and every test still passes. Found: {0}",
            string.Join(", ", hidden));
    }
}
