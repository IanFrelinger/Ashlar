using FluentAssertions;
using Mono.Cecil;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Every production verifier must take its strictness from <c>CertificationTrustPolicy</c>, and this
/// is the guard that notices when one stops.
///
/// <para><b>Why a structural guard and not only behavioural ones.</b> With nothing configured,
/// <c>CertificationTrustPolicy.Ambient.Strict</c> is reference-identical to the
/// <c>CertificationVerifyOptions.Strict</c> preset. So reverting any wiring site from
/// <c>(trustPolicy ?? CertificationTrustPolicy.Ambient).Strict</c> back to the bare preset changes
/// no observable behaviour in a test that configures no policy — and a green gate is therefore not
/// evidence that the wiring is still there. That is not hypothetical: when pinning was first wired
/// in, five of these seven sites could each be reverted with the whole suite green, and nobody would
/// have seen the operator's pinning set stop being applied. A behavioural fact closes a site only
/// where the site takes a policy a test can hand it; two of the seven do not, and this guard is what
/// covers them.</para>
///
/// <para><b>What it asserts.</b> For each site, that the compiled IL of the declaring type — its
/// nested closures and state machines included, because one site lives inside a DI factory
/// lambda — still calls <c>CertificationTrustPolicy.get_Ambient</c>. Reverting the site to the bare
/// preset deletes that call and reddens the row. This is a call-graph assertion on shipped IL, not
/// a source-text grep: a site cannot satisfy it by mentioning the type in a comment or a doc
/// <c>cref</c>.</para>
///
/// <para><b>What it does NOT assert.</b> That the options reaching each verifier are actually
/// enforced — that is what the behavioural facts in
/// <see cref="TrustedKeyPinningConfigurationTests"/> and
/// <see cref="PinnedHotSwapVerifyAtLoadTests"/> are for, and five of the seven rows have one. It
/// also asserts presence, not count: if a type ever grows a second <c>Ambient</c> site, losing the
/// first would no longer redden. Each of the seven types has exactly one today.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class PinnedWiringSiteConventionTests
{
    private const string TrustPolicyTypeName = "Ashlar.Certification.Contracts.CertificationTrustPolicy";

    private const string AmbientAccessor = "get_Ambient";

    /// <summary>
    /// The seven sites, each named by the assembly it ships in and the type that holds it. The
    /// fifth column of the story — whether the site also has a behavioural fact — is in the
    /// <c>behaviourallyCovered</c> argument, so a reader can see at a glance which rows are carried
    /// by this guard alone.
    /// </summary>
    [Theory]
    [InlineData(
        "Ashlar.Infrastructure.dll",
        "Ashlar.Infrastructure.Certification.FileCertificationRecordStore",
        true)]
    [InlineData(
        "Ashlar.Infrastructure.dll",
        "Ashlar.Infrastructure.Certification.CertifiedBrickRegistry",
        true)]
    [InlineData(
        "Ashlar.Infrastructure.dll",
        "Ashlar.Infrastructure.Certification.HotSwap.CertifiedBrickHotSwapHost",
        true)]
    [InlineData(
        "Ashlar.Infrastructure.dll",
        "Ashlar.Infrastructure.Certification.Composition.CompositionConstituentChecker",
        true)]
    [InlineData(
        "Ashlar.BackgroundAgents.dll",
        "Ashlar.BackgroundAgents.Security.SelfProducedBrickCertificationPolicy",
        true)]
    [InlineData(
        "Ashlar.Certification.State.dll",
        "Ashlar.Certification.State.StateLogVerifier",
        false)]
    [InlineData(
        "Ashlar.Infrastructure.dll",
        "Ashlar.Infrastructure.Adaptation.Sdk.Extensions.AdaptationServiceCollectionExtensions",
        false)]
    public void Every_production_verifier_still_takes_its_strictness_from_the_trust_policy(
        string assemblyFileName,
        string typeFullName,
        bool behaviourallyCovered)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assemblyFileName);
        File.Exists(path).Should().BeTrue(
            "{0} must sit beside the test assembly, or this row scans nothing and passes vacuously",
            assemblyFileName);

        using var module = ModuleDefinition.ReadModule(path);
        var scope = module.Types
            .SelectMany(Flatten)
            .Where(t => string.Equals(t.FullName, typeFullName, StringComparison.Ordinal)
                || t.FullName.StartsWith(typeFullName + "/", StringComparison.Ordinal))
            .ToArray();

        scope.Should().NotBeEmpty(
            "{0} is a named pinning wiring site. If it moved or was renamed, move this row with it "
            + "rather than deleting it — deleting the row is how a site goes back to being "
            + "unverified.",
            typeFullName);

        var sites = scope
            .SelectMany(t => t.Methods.Where(m => m.HasBody))
            .Where(m => m.Body.Instructions.Any(IsAmbientAccess))
            .Select(m => m.FullName)
            .ToArray();

        sites.Should().NotBeEmpty(
            "nothing in {0} reads CertificationTrustPolicy.Ambient any more, so an operator's "
            + "pinning set is not applied there. With nothing configured Ambient.Strict IS the "
            + "Strict preset, so this change is invisible to every behavioural test — which is "
            + "precisely why this guard exists.{1}",
            typeFullName,
            behaviourallyCovered
                ? " This site also has a behavioural fact, which should have reddened alongside "
                + "this row; if it did not, check that the fact still hands the site a policy."
                : " This site has NO behavioural fact: it takes no policy a test can supply, so "
                + "this row is the only thing standing between it and silent drift.");
    }

    private static bool IsAmbientAccess(Mono.Cecil.Cil.Instruction instruction) =>
        instruction.Operand is MethodReference callee
        && string.Equals(callee.Name, AmbientAccessor, StringComparison.Ordinal)
        && string.Equals(callee.DeclaringType.FullName, TrustPolicyTypeName, StringComparison.Ordinal);

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) =>
        type.NestedTypes.SelectMany(Flatten).Prepend(type);
}
