using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.Composition;
using Ashlar.Infrastructure.Certification.Sdk.Extensions;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// An operator's key reaches the composition lane, and it gets there without any key material
/// crossing a boundary (limitation 9, operative half).
///
/// <para><b>The seven key-path facts are KEY-PATH facts, not flag facts.</b> Each expected signature
/// is computed here with <see cref="HMACSHA256"/> over the composition signer's own canonical
/// payload, independently of the class under test — six of the seven do; the seventh,
/// <see cref="BrickSignersKey_ClearsTheDevKeyFlag_AndStaysQuiet"/>, asserts the honesty flag and the
/// absence of a warning instead. The seven partition as THREE / THREE / ONE: three compute a
/// signature and carry a negative control (the primary fact against the committed constant, the two
/// precedence facts against the key they must NOT have used); three compute a signature alone; one
/// computes no signature at all. (Three earlier versions of this paragraph got this wrong three
/// different ways, the last by saying "the remaining four assert the signature alone" — which
/// counted the flag fact twice. Count before you quote.) A flag-only assertion would not catch a key-path regression: the earlier
/// partial fix kept <c>CertificationForgeAttackTests.CompositionSigner_HonorsExplicitKey</c> green
/// throughout, which is exactly what <c>docs/certification-evidence.md</c> records about it.</para>
///
/// <para>The FIVE lane-agreement facts at the end of this class are a different kind and are not
/// covered by the paragraph above: they assert on captured warnings rather than on a signature,
/// because what they check is whether the gate NOTICES two independently built signers. (The
/// false-positive fact additionally re-derives the signature, to show the alarm really is false.)
/// Seven key-path facts plus five lane-agreement facts is the twelve this class carries. Do not read
/// the five as key-path evidence.</para>
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

    [Fact]
    public void TheGate_WarnsWhenTheTwoLanesWereBuiltIndependently()
    {
        using var unset = EnvironmentVariableScope.Unset(CertificationRecordSigning.HmacKeyEnvVar);
        var store = new InMemoryCertificationRecordStore();
        var brick = new CertificationRecordSigner(hmacKey: "brick-key");
        var logger = new ListLogger<CompositionCertificationGate>();

        _ = new CompositionCertificationGate(
            store,
            brick,
            new CertifiedBrickRegistry(store, brick),
            new CompositionCertificationRecordSigner(hmacKey: "a-different-key"),
            logger);

        logger.Warnings.Should().ContainSingle(
            "this gate checks constituent signatures with one signer and mints the composition record "
            + "with another; if they are not provably the same key, the certificate may not attest the "
            + "chain it appears to")
            .Which.Should().Contain("same key");
    }

    [Fact]
    public void TheGate_IsSilentWhenTheCompositionSignerDerivesFromTheBrickSigner()
    {
        using var unset = EnvironmentVariableScope.Unset(CertificationRecordSigning.HmacKeyEnvVar);
        var store = new InMemoryCertificationRecordStore();
        var brick = new CertificationRecordSigner(hmacKey: "brick-key");
        var logger = new ListLogger<CompositionCertificationGate>();

        _ = new CompositionCertificationGate(
            store,
            brick,
            new CertifiedBrickRegistry(store, brick),
            new CompositionCertificationRecordSigner(brick),
            logger);

        logger.Warnings.Should().BeEmpty(
            "this is the shape AddCertificationInfrastructure produces — one signer injected into both "
            + "lanes — and the supported wiring must not warn");
    }

    [Fact]
    public void TheGate_IsSilentWhenBothLanesAreOnTheCommittedDevKey()
    {
        using var unset = EnvironmentVariableScope.Unset(CertificationRecordSigning.HmacKeyEnvVar);
        var store = new InMemoryCertificationRecordStore();
        var brick = new CertificationRecordSigner();
        var logger = new ListLogger<CompositionCertificationGate>();

        _ = new CompositionCertificationGate(
            store,
            brick,
            new CertifiedBrickRegistry(store, brick),
            new CompositionCertificationRecordSigner(),
            logger);

        logger.Warnings.Should().BeEmpty(
            "two signers both falling back to the committed constant agree at construction, so warning "
            + "here would be noise on every host that configured nothing — and noise is how a warning "
            + "stops being read. This exemption is not a proof they stay in agreement: the brick lane "
            + "re-resolves the env var per call while a non-delegating composition signer freezes its "
            + "bytes, so setting the var afterwards diverges them silently. See the gate's constructor.");
    }

    /// <summary>
    /// The claim the whole warn-never-refuse design rests on, made EXECUTABLE.
    ///
    /// <para>"The shipped DI path stays silent by construction" was asserted in four places — this
    /// gate's constructor comment, the signer's XML doc, <c>docs/certification-evidence.md</c> and the
    /// commit message — and nothing ran it. Every other fact in this class hands the gate a signer it
    /// built by hand, which proves nothing about what <see cref="CertificationServiceCollectionExtensions
    /// .AddCertificationGate"/> actually composes.</para>
    ///
    /// <para>This resolves the gate out of a real container, with a real operator key, and asserts the
    /// warning does not fire. If a future registration stops threading the brick signer — the
    /// <c>TryAddSingleton</c> factory is the single point where that could regress — this reddens, and
    /// the person who reads it learns that hosts are being warned at for a correct configuration.</para>
    /// </summary>
    [Fact]
    public void TheShippedDiPath_ComposesAGateThatStaysSilent()
    {
        using var unset = EnvironmentVariableScope.Unset(CertificationRecordSigning.HmacKeyEnvVar);
        var warnings = new List<string>();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new CapturingLoggerProvider(warnings)));

        // What SPEC-006 S-4 tells a host to do, and the only way to hold a real key.
        services.AddSingleton(new CertificationRecordSigner(hmacKey: OperatorKey));
        services.AddCertificationGate();

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<ICompositionCertificationGate>();

        warnings.Should().BeEmpty(
            "AddCertificationInfrastructure registers the composition signer with a factory that passes "
            + "the brick signer, so the gate sees one key holder across both lanes and must not warn — "
            + "and a host that followed the spec must not be told its configuration looks wrong");
    }

    /// <summary>
    /// The gate's KNOWN FALSE POSITIVE, pinned so it cannot be "fixed" by accident.
    ///
    /// <para>Two signers built independently under the SAME explicit key are correctly configured —
    /// every record either lane mints or verifies is under one key — and the gate warns anyway,
    /// because it asks reference identity and not "are these the same key". That is the deliberate
    /// trade: answering it exactly would mean comparing key material, which is the one thing this
    /// design exists to avoid.</para>
    ///
    /// <para>This fact asserts the WARNING, not its absence. If someone later teaches the gate to
    /// compare keys, this test goes red, and the person reading it is the person who needs to know
    /// that the fix costs a key comparison. Do not "correct" it to expect silence without changing
    /// the gate and this comment together.</para>
    /// </summary>
    [Fact]
    public void TheGate_WarnsEvenWhenBothLanesHoldTheSameExplicitKey_WhichIsAFalsePositive()
    {
        using var unset = EnvironmentVariableScope.Unset(CertificationRecordSigning.HmacKeyEnvVar);
        var store = new InMemoryCertificationRecordStore();
        var brick = new CertificationRecordSigner(hmacKey: OperatorKey);
        var logger = new ListLogger<CompositionCertificationGate>();

        _ = new CompositionCertificationGate(
            store,
            brick,
            new CertifiedBrickRegistry(store, brick),
            new CompositionCertificationRecordSigner(hmacKey: OperatorKey),
            logger);

        logger.Warnings.Should().ContainSingle(
            "reference identity cannot see that these two signers hold the same key, so a correctly "
            + "configured host is warned — a FALSE POSITIVE, and the accepted cost of never comparing "
            + "key material");

        // The alarm is false, but the lanes really are in agreement. Proven here rather than
        // asserted, so the word "false" in the name is backed by something.
        new CompositionCertificationRecordSigner(hmacKey: OperatorKey).Sign(Probe)
            .Should().Be(ExpectedHmac(OperatorKey));
    }

    /// <summary>
    /// Captures warnings from EVERY category, unlike <see cref="ListLogger{T}"/>, because the DI fact
    /// above must notice a warning from any type the container composes — not only the gate.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _sink;

        public CapturingLoggerProvider(List<string> sink) => _sink = sink;

        public ILogger CreateLogger(string categoryName) => new SinkLogger(_sink);

        public void Dispose() { }

        private sealed class SinkLogger : ILogger
        {
            private readonly List<string> _sink;

            public SinkLogger(List<string> sink) => _sink = sink;

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning)
                    return;

                lock (_sink)
                    _sink.Add(formatter(state, exception));
            }
        }

        private sealed class NoScope : IDisposable
        {
            public static readonly NoScope Instance = new();
            public void Dispose() { }
        }
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
