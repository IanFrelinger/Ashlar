using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.Composition;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// An operator's key reaches the composition lane, and it gets there without any key material
/// crossing a boundary (limitation 9, operative half).
///
/// <para><b>These are KEY-PATH facts, not flag facts.</b> Each expected signature is computed here
/// with <see cref="HMACSHA256"/> over the composition signer's own canonical payload, independently
/// of the class under test, and each has a negative control against the committed constant. A
/// flag-only assertion would not catch a key-path regression: the earlier partial fix kept
/// <c>CertificationForgeAttackTests.CompositionSigner_HonorsExplicitKey</c> green throughout, which
/// is exactly what <c>docs/certification-evidence.md</c> records about it.</para>
///
/// <para><b>The <c>[Collection("EnvironmentVariables")]</c> on this class is LOAD-BEARING — do not
/// "tidy" these facts into a class without it.</b> Their whole claim is "the key came from the brick
/// signer, NOT from the environment", which is only meaningful with <c>ASHLAR_CERT_DEV_HMAC_KEY</c>
/// controlled. Reading or scoping a process-global while a neighbouring class writes it is precisely
/// the flake <c>ProcessGlobalEnvironmentConventionTests</c> exists for, and a flaky cert-gate is this
/// repository's documented route to a mute (<c>docs/HowGatesGoQuiet.md</c>).</para>
///
/// <para>Environment access goes through <see cref="EnvironmentVariableScope"/>, so this file does
/// NOT need a row in that convention's allowlist — and must not be given one, because a stale row
/// fails as loudly as a missing one.</para>
/// </summary>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class CompositionSignerKeyThreadingTests
{
    private const string OperatorKey = "operator-secret-not-committed";

    private static readonly CompositionCertificationRecord Probe = new()
    {
        Status = "PASS",
        Stage = "C0-C3",
        Admitted = true,
        Signed = true,
        Timestamp = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero),
        CompositionId = "composition-key-threading-probe",
        CompositionEscapeRate = 0d,
        TotalStructuralMutants = 2,
        SurvivingStructuralMutants = 0,
        KilledStructuralMutantIds = new[] { "m1", "m2" },
        SurvivingStructuralMutantIds = Array.Empty<string>(),
        Reason = "key-threading probe",
    };

    /// <summary>The signature the record MUST carry under <paramref name="key"/>, derived here.</summary>
    private static string ExpectedHmac(string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(CompositionCertificationRecordSigner.BuildPayload(Probe))));
    }

    [Fact]
    public void BrickSignersKey_SignsTheCompositionRecord_NotTheCommittedConstant()
    {
        using var unset = EnvironmentVariableScope.Unset(CertificationRecordSigning.HmacKeyEnvVar);

        var composition = new CompositionCertificationRecordSigner(
            new CertificationRecordSigner(hmacKey: OperatorKey));

        var signature = composition.Sign(Probe);

        signature.Should().Be(
            ExpectedHmac(OperatorKey),
            "the key an operator supplied to the brick lane is the key composition records must be minted under");
        signature.Should().NotBe(
            ExpectedHmac(CertificationRecordSigning.DefaultDevKey),
            "negative control: a pass under the COMMITTED constant would mean the operator key never arrived");
    }

    [Fact]
    public void BrickSignersKey_ClearsTheDevKeyFlag_AndStaysQuiet()
    {
        using var unset = EnvironmentVariableScope.Unset(CertificationRecordSigning.HmacKeyEnvVar);
        var logger = new ListLogger<CompositionCertificationRecordSigner>();

        var composition = new CompositionCertificationRecordSigner(
            new CertificationRecordSigner(hmacKey: OperatorKey), logger);

        composition.UsesDevKey.Should().BeFalse("the flag a host reads must agree with the bytes it signs");
        logger.Warnings.Should().BeEmpty("a host that configured a key must not be nagged on this lane either");
    }

    [Fact]
    public void AnExplicitKey_OutranksAnInjectedBrickSigner()
    {
        using var unset = EnvironmentVariableScope.Unset(CertificationRecordSigning.HmacKeyEnvVar);

        var composition = new CompositionCertificationRecordSigner(
            brickSigner: new CertificationRecordSigner(hmacKey: "brick-key"),
            hmacKey: "composition-key");

        var signature = composition.Sign(Probe);

        signature.Should().Be(ExpectedHmac("composition-key"), "the most specific statement wins");
        signature.Should().NotBe(ExpectedHmac("brick-key"));
    }

    [Fact]
    public void ADevKeyBrickSigner_StillMakesTheCompositionLaneLoud()
    {
        using var unset = EnvironmentVariableScope.Unset(CertificationRecordSigning.HmacKeyEnvVar);
        var logger = new ListLogger<CompositionCertificationRecordSigner>();

        var composition = new CompositionCertificationRecordSigner(new CertificationRecordSigner(), logger);

        composition.UsesDevKey.Should().BeTrue();
        composition.Sign(Probe).Should().Be(ExpectedHmac(CertificationRecordSigning.DefaultDevKey));
        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain(CertificationRecordSigning.HmacKeyEnvVar);
        logger.Warnings.Single().Should().NotContain(
            CertificationRecordSigning.DefaultDevKey, "the log must not spell the key out, even the dev one");
    }

    [Fact]
    public void WhitespaceExplicitKey_FallsThroughToTheInjectedBrickSigner_NotToTheEnvironment()
    {
        // A decision, not a derivation: a blank key is treated as "nothing said", consistent with
        // IsNullOrWhiteSpace everywhere else on this path. Pinned so the next reader inherits the
        // decision rather than a coin flip - and note that it now falls through to something MORE
        // specific than the environment, not less.
        using var set = new EnvironmentVariableScope(CertificationRecordSigning.HmacKeyEnvVar, "env-key");

        var composition = new CompositionCertificationRecordSigner(
            brickSigner: new CertificationRecordSigner(hmacKey: "brick-key"),
            hmacKey: "   ");

        composition.Sign(Probe).Should().Be(ExpectedHmac("brick-key"));
        composition.Sign(Probe).Should().NotBe(ExpectedHmac("env-key"));
    }

    [Fact]
    public void NoBrickSigner_ResolvesOneString_SoTheFlagAgreesWithTheBytes()
    {
        using var set = new EnvironmentVariableScope(CertificationRecordSigning.HmacKeyEnvVar, "env-key");

        var composition = new CompositionCertificationRecordSigner();

        composition.Sign(Probe).Should().Be(ExpectedHmac("env-key"));
        composition.UsesDevKey.Should().BeFalse(
            "the flag is derived from the same resolved string as the bytes");
    }

    [Fact]
    public void NoBrickSignerAndNoKeyAnywhere_BehavesExactlyAsBefore()
    {
        using var unset = EnvironmentVariableScope.Unset(CertificationRecordSigning.HmacKeyEnvVar);

        var composition = new CompositionCertificationRecordSigner();

        composition.UsesDevKey.Should().BeTrue();
        composition.Sign(Probe).Should().Be(
            ExpectedHmac(CertificationRecordSigning.DefaultDevKey),
            "an unconfigured host must be byte-identical to the previous build");
    }

    /// <summary>
    /// Duplicated rather than shared: the equivalent in <c>CertificationRecordSignerDevKeyTests</c>
    /// is private to its class, and extracting a shared logger helper is a separate change.
    /// </summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
