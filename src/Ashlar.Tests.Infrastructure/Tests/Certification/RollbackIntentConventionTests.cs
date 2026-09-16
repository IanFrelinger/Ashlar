using System.Reflection;
using FluentAssertions;
using Ashlar.Infrastructure.Certification.HotSwap;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The rollback exemption is a property of the CALL PATH, never of caller-supplied data.
///
/// <para><b>Why this blocks a merge.</b> <see cref="CertifiedBrickHotSwapHost"/> exempts a
/// containment rollback from the pacing and authority gates (pause, cadence floor, in-flight
/// watch window, lineage demotion, recursion ceiling). The only thing that makes that
/// exemption safe is WHO can ask for it: an <c>internal</c> <see cref="SwapIntent"/> accepted
/// by a <c>private</c> overload that <c>RollbackToAsync</c> alone reaches with
/// <c>Rollback</c>, replaying content the host itself retained and looked up by id. Put the
/// intent on <see cref="CertifiedBrickLoadRequest"/> or <see cref="AutonomousAdmission"/>, or
/// on any public signature, and it becomes an unsigned, uncertified lane selector exempting
/// five gates at once — the shape SPEC-006 already carries as a limitation. Every functional
/// fact in <c>RollbackGateExemptionTests</c> stays green through that change; this is the
/// only fact that catches it.</para>
///
/// <para><b>Rule 3, stated plainly.</b> Pure reflection over already-loaded types: no build,
/// no SDK, no network, no clock, and no environment variable.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class RollbackIntentConventionTests
{
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private const BindingFlags PublicDeclared =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void The_public_swap_api_cannot_request_the_rollback_exemption()
    {
        // InternalsVisibleTo lets this assembly name the type. A rename fails to compile here,
        // which is the loud "re-point this test" signal rather than a silent pass.
        var intent = typeof(SwapIntent);

        intent.IsEnum.Should().BeTrue();
        intent.IsVisible.Should().BeFalse(
            "SwapIntent is internal by design: an intent a caller can name is an intent a caller "
            + "can select, and selecting Rollback exempts five gates at once");

        foreach (var carrier in new[] { typeof(CertifiedBrickLoadRequest), typeof(AutonomousAdmission) })
        {
            MembersMentioning(carrier, intent, AllDeclared).Should().BeEmpty(
                $"{carrier.Name} is caller-supplied data; intent on it at ANY accessibility would be "
                + "a lane selector with no certificate behind it");
        }

        var leaks = LoadableTypes(typeof(CertifiedBrickHotSwapHost).Assembly)
            .Where(t => t.IsVisible)
            .SelectMany(t => MembersMentioning(t, intent, PublicDeclared))
            .ToList();
        leaks.Should().BeEmpty(
            "no public signature in the host assembly may carry SwapIntent — a public method, "
            + "constructor, property or field typed on it is the exemption for hire");

        var publicSwaps = typeof(CertifiedBrickHotSwapHost)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(CertifiedBrickHotSwapHost.SwapAsync))
            .ToList();
        publicSwaps.Should().ContainSingle("the public surface is exactly one SwapAsync, always an absorption")
            .Which.GetParameters().Select(p => p.ParameterType)
            .Should().Equal(
                new[] { typeof(IReadOnlyList<CertifiedBrickLoadRequest>), typeof(CancellationToken) },
                "the public overload's shape is frozen: no intent, no lane, no restored-from");

        var intentOverloads = typeof(CertifiedBrickHotSwapHost)
            .GetMethods(AllDeclared)
            .Where(m => m.Name == nameof(CertifiedBrickHotSwapHost.SwapAsync)
                && m.GetParameters().Any(p => p.ParameterType == intent))
            .ToList();
        intentOverloads.Should().ContainSingle(
                "exactly one overload takes the intent; if it was renamed, re-point this test rather than deleting it")
            .Which.IsPrivate.Should().BeTrue(
                "the overload that accepts an intent must be private — internal would let any "
                + "InternalsVisibleTo assembly, this one included, request the exemption");
    }

    private static IEnumerable<string> MembersMentioning(Type owner, Type intent, BindingFlags flags)
    {
        foreach (var method in owner.GetMethods(flags))
        {
            if (Mentions(method.ReturnType, intent) || method.GetParameters().Any(p => Mentions(p.ParameterType, intent)))
                yield return $"{owner.FullName}::{method.Name}";
        }

        foreach (var constructor in owner.GetConstructors(flags))
        {
            if (constructor.GetParameters().Any(p => Mentions(p.ParameterType, intent)))
                yield return $"{owner.FullName}::.ctor";
        }

        foreach (var property in owner.GetProperties(flags))
        {
            if (Mentions(property.PropertyType, intent))
                yield return $"{owner.FullName}::{property.Name}";
        }

        foreach (var field in owner.GetFields(flags))
        {
            if (Mentions(field.FieldType, intent))
                yield return $"{owner.FullName}::{field.Name}";
        }
    }

    /// <summary>
    /// The type itself or wrapped in anything: <c>SwapIntent?</c>, <c>SwapIntent[]</c>,
    /// <c>IReadOnlyList&lt;SwapIntent&gt;</c>, <c>Task&lt;SwapIntent&gt;</c>, <c>ref SwapIntent</c>.
    /// </summary>
    private static bool Mentions(Type type, Type intent)
    {
        if (type == intent)
            return true;
        if (type.HasElementType && type.GetElementType() is { } element)
            return Mentions(element, intent);
        return type.IsGenericType && type.GetGenericArguments().Any(argument => Mentions(argument, intent));
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Select(t => t!);
        }
    }
}
