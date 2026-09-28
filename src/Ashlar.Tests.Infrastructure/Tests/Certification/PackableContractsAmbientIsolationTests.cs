using System.Linq;
using FluentAssertions;
using Mono.Cecil;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// <c>Ashlar.Certification.Contracts</c> is <c>IsPackable=true</c>: it ships to consumers who build ON
/// Ashlar rather than inside it. <c>CertificationTrustPolicy.Ambient</c> reads process configuration,
/// so if any code path inside that package ever consulted it implicitly, every consumer's verification
/// decisions would start depending on the host's environment without anyone choosing that.
///
/// <para>Today they do not, and that was measured rather than assumed: reaching ambient behaviour
/// requires naming <c>CertificationTrustPolicy.Ambient</c>, and
/// <see cref="Ashlar.Certification.Contracts.CertificationTrustVerifier"/> falls back to
/// <c>CertificationVerifyOptions.Default</c> — the static preset — when no options are supplied. Every
/// ambient read lives in <c>Ashlar.Infrastructure</c>, i.e. in Ashlar's own hosts, which is where an
/// operator's configuration should apply.</para>
///
/// <para>These two facts exist because that property is one a single convenience edit destroys. Writing
/// <c>options ?? CertificationTrustPolicy.Ambient.Strict</c> inside the package would look like a
/// kindness and would silently make every downstream consumer environment-dependent. Nothing else in
/// the suite would notice: with no configuration present <c>Ambient.Strict</c> is
/// <em>reference-identical</em> to the <c>Strict</c> preset, so such a change is invisible to any test
/// that does not set the variables.</para>
/// </summary>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class PackableContractsAmbientIsolationTests
{
    private const string PackableAssembly = "Ashlar.Certification.Contracts.dll";
    private const string ControlAssembly = "Ashlar.Infrastructure.dll";
    private const string TrustPolicyTypeName = "Ashlar.Certification.Contracts.CertificationTrustPolicy";
    private const string AmbientAccessor = "get_Ambient";

    /// <summary>
    /// Structural half: no IL in the packable assembly reads the ambient policy.
    ///
    /// <para>An assertion that a set is EMPTY is the easiest kind to make vacuous — a typo in either
    /// constant, a renamed accessor, or a missing assembly all produce an empty set and a green test.
    /// So the detector is exercised against an assembly where call sites are known to exist BEFORE it
    /// is trusted to report their absence. If the control is ever empty, this fact fails rather than
    /// passing quietly, because at that point it has stopped measuring anything.</para>
    /// </summary>
    [Fact]
    public void No_code_in_the_packable_contracts_package_reads_the_ambient_trust_policy()
    {
        var control = AmbientCallSitesIn(ControlAssembly);
        control.Should().NotBeEmpty(
            "POSITIVE CONTROL: {0} is known to read CertificationTrustPolicy.Ambient at five wiring "
            + "sites. If this detector cannot find them, it cannot be trusted to prove their absence "
            + "in {1} either, and the assertion below would pass vacuously. Check {2} and {3} before "
            + "believing the second assertion.",
            ControlAssembly, PackableAssembly, nameof(TrustPolicyTypeName), nameof(AmbientAccessor));

        var sites = AmbientCallSitesIn(PackableAssembly);

        sites.Should().BeEmpty(
            "{0} is a PACKABLE library. Reading CertificationTrustPolicy.Ambient inside it makes every "
            + "external consumer's verification depend on the host's process configuration, which they "
            + "never opted into — and with nothing configured Ambient.Strict IS the Strict preset, so "
            + "no behavioural test would notice. If a fallback here is genuinely wanted, it is an API "
            + "decision about the published package, not a convenience: take it deliberately and "
            + "rewrite this fact. Offending site(s): {1}",
            PackableAssembly, string.Join(", ", sites));
    }

    /// <summary>
    /// Behavioural half, and the one a consumer would recognise: with trust material present in the
    /// environment, the packable library's own verifier entry point still behaves as the unpinned
    /// preset unless a policy is handed to it. The structural fact above cannot see a future implicit
    /// read that arrives through a path IL analysis is fooled by; this one cannot see a read on a code
    /// path it does not traverse. Together they cover more than either.
    /// </summary>
    [Fact]
    public void Trust_material_in_the_environment_does_not_reach_a_consumer_who_never_asked_for_it()
    {
        var keys = Environment.GetEnvironmentVariable(
            Ashlar.Certification.Contracts.CertificationTrustPolicy.TrustedKeysVariable);
        var required = Environment.GetEnvironmentVariable(
            Ashlar.Certification.Contracts.CertificationTrustPolicy.PinningRequiredVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                Ashlar.Certification.Contracts.CertificationTrustPolicy.TrustedKeysVariable,
                Convert.ToBase64String(new byte[32]));
            Environment.SetEnvironmentVariable(
                Ashlar.Certification.Contracts.CertificationTrustPolicy.PinningRequiredVariable,
                "true");

            // Precondition: the environment really is configured, read through the same accessor a
            // host uses. Without this the assertions below would hold on an unset environment too.
            var configured = Ashlar.Certification.Contracts.CertificationTrustPolicy.FromEnvironment();
            configured.PinningConfigured.Should().BeTrue(
                "the arrange must take effect, or this fact proves nothing about isolation");

            // A consumer who names nothing gets the presets, pinning off.
            Ashlar.Certification.Contracts.CertificationVerifyOptions.Strict.PinningEnabled
                .Should().BeFalse(
                    "the Strict PRESET must stay a constant of the library: an operator's environment "
                    + "must not reach a consumer who never asked for a policy");
            Ashlar.Certification.Contracts.CertificationVerifyOptions.Default.PinningEnabled
                .Should().BeFalse("same, for Default");

            // And opting in is what changes behaviour — otherwise the two assertions above would be
            // satisfied by a build where configuration does nothing at all.
            configured.Strict.PinningEnabled.Should().BeTrue(
                "naming the ambient policy is what applies the operator's keys; if this is false the "
                + "isolation above is not isolation, it is a broken feature");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Ashlar.Certification.Contracts.CertificationTrustPolicy.TrustedKeysVariable, keys);
            Environment.SetEnvironmentVariable(
                Ashlar.Certification.Contracts.CertificationTrustPolicy.PinningRequiredVariable, required);
        }
    }

    private static string[] AmbientCallSitesIn(string assemblyFileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assemblyFileName);
        File.Exists(path).Should().BeTrue(
            "{0} must sit beside the test assembly, or this scan reads nothing and reports an empty "
            + "result that looks like success",
            assemblyFileName);

        using var module = ModuleDefinition.ReadModule(path);
        return module.Types
            .SelectMany(Flatten)
            .SelectMany(t => t.Methods.Where(m => m.HasBody))
            .Where(m => m.Body.Instructions.Any(IsAmbientAccess))
            .Select(m => m.FullName)
            .ToArray();
    }

    private static bool IsAmbientAccess(Mono.Cecil.Cil.Instruction instruction) =>
        instruction.Operand is MethodReference callee
        && string.Equals(callee.Name, AmbientAccessor, StringComparison.Ordinal)
        && string.Equals(callee.DeclaringType.FullName, TrustPolicyTypeName, StringComparison.Ordinal);

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) =>
        type.NestedTypes.SelectMany(Flatten).Prepend(type);
}
