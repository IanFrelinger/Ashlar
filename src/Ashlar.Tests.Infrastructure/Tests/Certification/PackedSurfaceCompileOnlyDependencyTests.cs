using FluentAssertions;
using Mono.Cecil;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// No PUBLIC type in a packed assembly may be implemented against a package the .nupkg does not ship.
///
/// <para><b>The defect this exists for actually shipped.</b> <c>Ashlar.Infrastructure</c> references
/// xunit as <c>PrivateAssets=all; IncludeAssets=compile</c> - it compiles against xunit, the nuspec
/// declares no xunit dependency, and no xunit assembly is packed. That is correct for an
/// implementation detail and fatal for public API. <c>UnitTestFrameworkBridge</c> was a
/// <c>public static class</c> whose execution path called <c>Xunit.Assert.Fail</c>, packed and
/// published in v0.1.0, v0.1.1 and v0.1.2, so any consumer who called it received a
/// <c>FileNotFoundException</c> for xunit.core instead of a result. Nothing caught it: the type
/// compiles, the package restores, every test in this repository passes because THIS assembly has
/// xunit, and a consumer only discovers it at run time.</para>
///
/// <para><b>Why IL and not a source grep.</b> A grep for <c>using Xunit</c> sees a file, not a
/// surface: it cannot tell a public method from a private one, it misses a reference reached through
/// a nested closure or an iterator state machine, and it fires on a comment. This reads the compiled
/// assembly and asks the only question that matters - can a consumer reach it.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class PackedSurfaceCompileOnlyDependencyTests
{
    /// <summary>
    /// Packages referenced with <c>IncludeAssets=compile</c> (and <c>PrivateAssets=all</c>) by an
    /// assembly this repository packs. A type from one of these is present at build time and absent
    /// at a consumer's run time.
    /// </summary>
    private static readonly string[] CompileOnlyAssemblyPrefixes =
    [
        "xunit",
    ];

    private const string PackedAssembly = "Ashlar.Infrastructure.dll";

    [Fact]
    public void NoPublicTypeInAPackedAssemblyTouchesACompileOnlyPackage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, PackedAssembly);
        File.Exists(path).Should().BeTrue(
            "{0} must sit beside the test assembly, or this fact scans nothing and passes vacuously",
            PackedAssembly);

        using var module = ModuleDefinition.ReadModule(path);

        // POSITIVE CONTROL. The compile-only reference must still EXIST in this assembly, otherwise
        // the assertion below is satisfied by a repository where xunit was simply removed - and this
        // fact would then sit green forever while guarding nothing. It is the reference being
        // confined to non-public types that is the property under test, not its absence.
        var referencesCompileOnly = module.AssemblyReferences.Any(IsCompileOnly);
        referencesCompileOnly.Should().BeTrue(
            "POSITIVE CONTROL: {0} is expected to still reference a compile-only package ({1}). If "
            + "that reference is gone this fact no longer proves anything, so remove the fact or "
            + "point it at whatever replaced it - do not leave it passing.",
            PackedAssembly, string.Join(", ", CompileOnlyAssemblyPrefixes));

        var offenders = module.Types
            .SelectMany(Flatten)
            .Where(IsPubliclyReachable)
            .SelectMany(type => type.Methods.Where(m => m.HasBody).Select(m => (type, m)))
            .Where(pair => MethodTouchesCompileOnly(pair.m))
            .Select(pair => $"{pair.type.FullName}::{pair.m.Name}")
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        offenders.Should().BeEmpty(
            "these members are reachable by a consumer of the {0} package and are implemented "
            + "against a package that package does not ship, so calling them throws "
            + "FileNotFoundException at run time rather than doing anything. Make the type or the "
            + "member internal (Ashlar.Infrastructure.csproj already grants InternalsVisibleTo to "
            + "the test assemblies), or move it into a test-only assembly. This is not theoretical: "
            + "UnitTestFrameworkBridge shipped this way in v0.1.0 through v0.1.2.",
            PackedAssembly);
    }

    private static bool IsCompileOnly(AssemblyNameReference reference) =>
        CompileOnlyAssemblyPrefixes.Any(prefix =>
            reference.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsCompileOnly(string? assemblyName) =>
        assemblyName is not null
        && CompileOnlyAssemblyPrefixes.Any(prefix =>
            assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A nested type is reachable only when every type enclosing it is too, so this walks outward
    /// rather than trusting <c>IsPublic</c> on the innermost type alone.
    /// </summary>
    private static bool IsPubliclyReachable(TypeDefinition type)
    {
        var current = type;
        while (current.IsNested)
        {
            if (!current.IsNestedPublic && !current.IsNestedFamily && !current.IsNestedFamilyOrAssembly)
                return false;
            current = current.DeclaringType;
        }

        return current.IsPublic;
    }

    /// <summary>
    /// True when the method's signature or its body mentions a type from a compile-only package.
    /// Body instructions are included because the failure is a run-time load, so a call inside a
    /// method whose signature is clean still throws for a consumer.
    /// </summary>
    private static bool MethodTouchesCompileOnly(MethodDefinition method)
    {
        if (IsCompileOnly(method.ReturnType?.Scope?.Name)
            || method.Parameters.Any(p => IsCompileOnly(p.ParameterType?.Scope?.Name)))
        {
            return true;
        }

        // TypeReference derives from MemberReference in Cecil, so it must be matched FIRST. With the
        // arms the other way round the compiler rejects the second as unreachable - and had it not,
        // a bare `ldtoken SomeXunitType` would have been read through DeclaringType, which is null
        // for a top-level type, and silently missed.
        return method.Body.Instructions.Any(instruction => instruction.Operand switch
        {
            TypeReference typeRef => IsCompileOnly(typeRef.Scope?.Name),
            MemberReference member => IsCompileOnly(member.DeclaringType?.Scope?.Name),
            _ => false,
        });
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) =>
        type.NestedTypes.SelectMany(Flatten).Prepend(type);
}
