using FluentAssertions;
using Ashlar.Certification.Contracts;
using Ashlar.Certification.State;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Attested state log binding: Tier-1 structural refusal/admission tests.
/// Witness fixtures are hand-authored; the verifier is not used to generate oracles.
/// </summary>
[Trait("Category", "Certification")]
public sealed class AttestedStateLogBindingTests
{
    private const string HmacKey = "attested-state-log-test-hmac";

    // Hand-authored schema canonical form (witness oracle).
    private const string WitnessSchemaCanonical =
        """{"version":1,"stateBinding":{"version":"witness-v1","hashLength":44}}""";

    private const string BehaviorAlphaSource = "certified-behavior-alpha-v1";
    private const string BehaviorBetaSource = "certified-behavior-beta-v1";

    private static readonly WitnessFixture Witness = WitnessFixture.Create();
    private static readonly CertifiedTransitionBuilder TransitionBuilder = new();

    [Fact]
    public void R1_UnresolvedBehaviorCert_Refuses()
    {
        var log = Witness.BuildValidLog();
        var tampered = ReplaceTransition(log, 0, transition => transition with
        {
            BehaviorCertContentHash = Witness.UnknownCertHash
        });
        tampered = RehashEntireLog(tampered);

        var result = VerifyTier1(tampered);

        result.Trusted.Should().BeFalse();
        result.FailureCode.Should().Be("behavior-cert-unresolved");
    }

    [Fact]
    public void R2_UntrustedBehaviorCert_Refuses()
    {
        var log = Witness.BuildValidLog();
        var resolver = Witness.CreateResolver(trustAlpha: false);

        var result = StateLogVerifier.Verify(
            log,
            Witness.CreateSchema(),
            resolver,
            HmacKey);

        result.Trusted.Should().BeFalse();
        result.FailureCode.Should().Be("behavior-cert-untrusted");
    }

    [Fact]
    public void R3_SchemaViolatingResultingStateHash_Refuses()
    {
        var log = Witness.BuildValidLog();
        var tampered = ReplaceTransition(log, 1, transition => transition with
        {
            ResultingStateHash = "NOT-A-VALID-SCHEMA-HASH"
        });
        tampered = RehashEntireLog(tampered);

        var result = VerifyTier1(tampered);

        result.Trusted.Should().BeFalse();
        result.FailureCode.Should().Be("schema-violation");
    }

    [Fact]
    public void R4_TamperedPrevEntryHash_Refuses()
    {
        var log = Witness.BuildValidLog();
        var tampered = ReplaceTransition(log, 1, transition => transition with
        {
            PrevEntryHash = Witness.FakeEntryHash
        });

        var result = VerifyTier1(tampered);

        result.Trusted.Should().BeFalse();
        result.FailureCode.Should().Be("chain-prev-entry-break");
    }

    [Fact]
    public void R5_DroppedTransitionGap_Refuses()
    {
        var full = Witness.BuildValidLog();
        var skipped = ReplaceTransition(full, 2, transition => transition with
        {
            PrevEntryHash = full.Transitions[0].EntryHash
        });
        skipped = RehashEntireLog(skipped);

        var result = VerifyTier1(skipped);

        result.Trusted.Should().BeFalse();
        result.FailureCode.Should().Be("chain-gap");
    }

    [Fact]
    public void R6_ReorderedTransitions_Refuses()
    {
        var full = Witness.BuildValidLog();
        var reordered = new AttestedStateLog(new[]
        {
            full.Transitions[0],
            full.Transitions[2],
            full.Transitions[1]
        });

        var result = VerifyTier1(reordered);

        result.Trusted.Should().BeFalse();
        result.FailureCode.Should().Be("chain-reorder");
    }

    [Fact]
    public void R7_InjectedTransitionUnresolvedCert_Refuses()
    {
        var full = Witness.BuildValidLog();
        var injected = TransitionBuilder.Create(
            priorStateHash: full.Transitions[2].ResultingStateHash,
            action: "inject:rogue",
            behaviorCertContentHash: Witness.UnknownCertHash,
            resultingStateHash: Witness.PhaseReadyHash,
            prevEntryHash: full.Transitions[2].EntryHash);

        var log = new AttestedStateLog(full.Transitions.Concat(new[] { injected }).ToArray());

        var result = VerifyTier1(log);

        result.Trusted.Should().BeFalse();
        result.FailureCode.Should().Be("injected-behavior-cert-unresolved");
    }

    [Fact]
    public void R8_SwappedValidCertReplayMismatch_Refuses()
    {
        var log = Witness.BuildValidLog();
        var swapped = ReplaceTransition(log, 1, transition => transition with
        {
            BehaviorCertContentHash = Witness.BetaCertHash,
            ResultingStateHash = Witness.PhaseArmedHash
        });
        swapped = RehashEntireLog(swapped);

        var result = StateLogVerifier.Verify(
            swapped,
            Witness.CreateSchema(),
            Witness.CreateResolver(),
            HmacKey,
            Witness.CreateReplayer());

        result.Trusted.Should().BeFalse();
        result.FailureCode.Should().Be("replay-mismatch");
    }

    [Fact]
    public void A1_FullyValidLog_IsTrusted()
    {
        var log = Witness.BuildValidLog();
        var result = VerifyTier1(log);

        result.Trusted.Should().BeTrue($"expected TRUSTED, got {result.FailureCode}: {result.Reason}");
        result.VerifiedTransitions.Should().Be(log.Count);
    }

    [Fact]
    public void G1_ZeroMutantGuard_VerifiedTransitionCountMatchesLogLength()
    {
        var log = Witness.BuildValidLog();
        var result = VerifyTier1(log);

        result.VerifiedTransitions.Should().BeGreaterThan(0);
        result.VerifiedTransitions.Should().Be(log.Count);
    }

    // ---------- Options overload ----------

    [Fact]
    public void Verify_WithoutOptions_IsStrict()
    {
        // The existing signature must keep meaning Strict — same verdict, same code, same reason,
        // same count — for a log it trusts and for one it refuses.
        var log = Witness.BuildValidLog();
        foreach (var resolver in new[] { Witness.CreateResolver(), Witness.CreateHmacOnlyResolver() })
        {
            var implicitStrict = StateLogVerifier.Verify(log, Witness.CreateSchema(), resolver, HmacKey);
            var explicitStrict = StateLogVerifier.Verify(log, Witness.CreateSchema(), resolver, HmacKey, null, CertificationVerifyOptions.Strict);

            implicitStrict.Trusted.Should().Be(explicitStrict.Trusted);
            implicitStrict.FailureCode.Should().Be(explicitStrict.FailureCode);
            implicitStrict.Reason.Should().Be(explicitStrict.Reason);
            implicitStrict.VerifiedTransitions.Should().Be(explicitStrict.VerifiedTransitions);
        }
    }

    [Fact]
    public void Verify_WithLegacyOptions_TrustsAnHmacOnlyLog_ThatStrictRefuses()
    {
        // The configuration escape: the same log, the same HMAC-only certificates, refused by the
        // default because Strict requires an Ed25519 signature, and trusted under options that
        // do not. Strict's own verdict is unchanged by the overload existing.
        var log = Witness.BuildValidLog();
        var resolver = Witness.CreateHmacOnlyResolver();

        var strict = StateLogVerifier.Verify(log, Witness.CreateSchema(), resolver, HmacKey);
        var legacy = StateLogVerifier.Verify(log, Witness.CreateSchema(), resolver, HmacKey, null, CertificationVerifyOptions.Legacy);

        strict.Trusted.Should().BeFalse();
        strict.FailureCode.Should().Be("behavior-cert-untrusted");
        strict.Reason.Should().Contain("ed25519-signature-required", "the inner verifier's code surfaces in the reason");
        legacy.Trusted.Should().BeTrue($"{legacy.FailureCode}: {legacy.Reason}");
        legacy.VerifiedTransitions.Should().Be(log.Count);
    }

    [Fact]
    public void Verify_WithNullOptions_IsRefused()
    {
        // Null arguments are refusals in this verifier, not exceptions, and a verifier that was not
        // told how strict to be must not guess a default.
        var result = StateLogVerifier.Verify(Witness.BuildValidLog(), Witness.CreateSchema(), Witness.CreateResolver(), HmacKey, null, null!);

        result.Trusted.Should().BeFalse();
        result.FailureCode.Should().Be("verify-options-missing");
        result.VerifiedTransitions.Should().Be(0);
    }

    private static StateLogTrustResult VerifyTier1(AttestedStateLog log) =>
        StateLogVerifier.Verify(log, Witness.CreateSchema(), Witness.CreateResolver(), HmacKey);

    private static AttestedStateLog ReplaceTransition(
        AttestedStateLog log,
        int index,
        Func<CertifiedTransition, CertifiedTransition> mutate)
    {
        var transitions = log.Transitions.ToArray();
        transitions[index] = mutate(transitions[index]);
        return new AttestedStateLog(transitions);
    }

    private static AttestedStateLog RehashEntireLog(AttestedStateLog log)
    {
        var transitions = log.Transitions.ToArray();
        for (var i = 0; i < transitions.Length; i++)
        {
            var current = transitions[i];
            transitions[i] = current with
            {
                EntryHash = WitnessFixture.ComputeEntryHash(
                    current.PriorStateHash,
                    current.Action,
                    current.BehaviorCertContentHash,
                    current.ResultingStateHash,
                    current.PrevEntryHash)
            };
        }

        return new AttestedStateLog(transitions);
    }

    /// <summary>
    /// Hand-authored witness oracle. Hashing reuses contract primitives only; log assembly is independent of the verifier.
    /// </summary>
    private sealed class WitnessFixture
    {
        public string UnknownCertHash { get; }
        public string FakeEntryHash { get; }
        public string AlphaCertHash { get; }
        public string BetaCertHash { get; }
        public string PhaseArmedHash { get; }

        private readonly StateSchema _schema;
        private readonly CertificationRecordData _alphaRecord;
        private readonly CertificationRecordData _betaRecord;
        private readonly CertificationRecordData _untrustedRecord;
        private readonly string _genesisStateHash;
        private readonly string _phaseIdleHash;
        private readonly string _phaseArmedHash;
        private readonly string _phaseReadyHash;

        private WitnessFixture(
            StateSchema schema,
            CertificationRecordData alphaRecord,
            CertificationRecordData betaRecord,
            CertificationRecordData untrustedRecord,
            string genesisStateHash,
            string phaseIdleHash,
            string phaseArmedHash,
            string phaseReadyHash,
            string unknownCertHash,
            string fakeEntryHash)
        {
            _schema = schema;
            _alphaRecord = alphaRecord;
            _betaRecord = betaRecord;
            _untrustedRecord = untrustedRecord;
            _genesisStateHash = genesisStateHash;
            _phaseIdleHash = phaseIdleHash;
            _phaseArmedHash = phaseArmedHash;
            _phaseReadyHash = phaseReadyHash;
            UnknownCertHash = unknownCertHash;
            FakeEntryHash = fakeEntryHash;
            AlphaCertHash = alphaRecord.ContentHash!;
            BetaCertHash = betaRecord.ContentHash!;
            PhaseArmedHash = phaseArmedHash;
        }

        public string PhaseReadyHash => _phaseReadyHash;

        public static WitnessFixture Create()
        {
            var schema = new StateSchema(WitnessSchemaCanonical);
            var genesis = schema.ComputeBoundStateHash("genesis");
            var phaseIdle = schema.ComputeBoundStateHash("phase:idle");
            var phaseArmed = schema.ComputeBoundStateHash("phase:armed");
            var phaseReady = schema.ComputeBoundStateHash("phase:ready");

            var alphaRecord = CreateTrustedRecord("behavior-alpha", BehaviorAlphaSource, HmacKey);
            var betaRecord = CreateTrustedRecord("behavior-beta", BehaviorBetaSource, HmacKey);
            var untrustedRecord = alphaRecord with { Admitted = false, Status = "FAIL", Signed = false };

            return new WitnessFixture(
                schema,
                alphaRecord,
                betaRecord,
                untrustedRecord,
                genesis,
                phaseIdle,
                phaseArmed,
                phaseReady,
                unknownCertHash: BrickContentHasher.ComputeSha256("missing-cert"),
                fakeEntryHash: BrickContentHasher.ComputeSha256("fake-entry"));
        }

        public StateSchema CreateSchema() => _schema;

        public AttestedStateLog BuildValidLog()
        {
            var t0 = CreateTransition(
                priorStateHash: _genesisStateHash,
                action: "phase:advance",
                behaviorCertContentHash: AlphaCertHash,
                resultingStateHash: _phaseIdleHash,
                prevEntryHash: CertifiedTransition.GenesisPrevEntryHash);

            var t1 = CreateTransition(
                priorStateHash: _phaseIdleHash,
                action: "phase:advance",
                behaviorCertContentHash: AlphaCertHash,
                resultingStateHash: _phaseArmedHash,
                prevEntryHash: t0.EntryHash);

            var t2 = CreateTransition(
                priorStateHash: _phaseArmedHash,
                action: "phase:release",
                behaviorCertContentHash: BetaCertHash,
                resultingStateHash: _phaseReadyHash,
                prevEntryHash: t1.EntryHash);

            return new AttestedStateLog(new[] { t0, t1, t2 });
        }

        public ICertificateResolver CreateResolver(bool trustAlpha = true) =>
            new MapCertificateResolver(new Dictionary<string, (CertificationRecordData, string)>
            {
                [AlphaCertHash] = (trustAlpha ? _alphaRecord : _untrustedRecord, BehaviorAlphaSource),
                [BetaCertHash] = (_betaRecord, BehaviorBetaSource)
            });

        /// <summary>
        /// The same certificates with their Ed25519 fields stripped and the HMAC recomputed: the
        /// only certificate shape every target framework can evaluate completely. The content
        /// hashes are unchanged, so the same log resolves against either resolver.
        /// </summary>
        public ICertificateResolver CreateHmacOnlyResolver() =>
            new MapCertificateResolver(new Dictionary<string, (CertificationRecordData, string)>
            {
                [AlphaCertHash] = (HmacOnly(_alphaRecord), BehaviorAlphaSource),
                [BetaCertHash] = (HmacOnly(_betaRecord), BehaviorBetaSource)
            });

        public ITransitionReplayer CreateReplayer() => new PhaseReplayer(_schema);

        public static string ComputeEntryHash(
            string priorStateHash,
            string action,
            string behaviorCertContentHash,
            string resultingStateHash,
            string prevEntryHash)
        {
            var payload = new
            {
                action,
                behaviorCertContentHash,
                prevEntryHash,
                priorStateHash,
                resultingStateHash
            };

            var canonical = System.Text.Json.JsonSerializer.Serialize(
                payload,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                });

            return BrickContentHasher.ComputeSha256(canonical);
        }

        private static CertifiedTransition CreateTransition(
            string priorStateHash,
            string action,
            string behaviorCertContentHash,
            string resultingStateHash,
            string prevEntryHash)
        {
            return new CertifiedTransition
            {
                PriorStateHash = priorStateHash,
                Action = action,
                BehaviorCertContentHash = behaviorCertContentHash,
                ResultingStateHash = resultingStateHash,
                PrevEntryHash = prevEntryHash,
                EntryHash = ComputeEntryHash(
                    priorStateHash,
                    action,
                    behaviorCertContentHash,
                    resultingStateHash,
                    prevEntryHash)
            };
        }

        private static CertificationRecordData CreateTrustedRecord(
            string brickId,
            string source,
            string hmacKey)
        {
            var (privateKey, publicKey) = CreateEd25519Key();
            
            var record = new CertificationRecordData
            {
                SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
                Status = "PASS",
                Stage = "witness",
                Admitted = true,
                Signed = true,
                Timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                BrickId = brickId,
                ContentHash = BrickContentHasher.ComputeSha256(source),
                EscapeRate = 0,
                TotalMutants = 1,
                SurvivingMutants = 0,
                KilledMutants = new[] { "m1" },
                SurvivingMutantIds = Array.Empty<string>(),
                Gate = "Ashlar.Tests.Infrastructure.Tests.Certification.AttestedStateLogBindingTests",
                Inputs = new[]
                {
                    new CertificationInput { Kind = CertificationInputKinds.GateEmittedArtifact, Id = brickId, Hash = "sha256:artifact" },
                    new CertificationInput { Kind = CertificationInputKinds.CertifierIdentity, Id = "test-certifier", Hash = "sha256:certifier" }
                },
                Ed25519PublicKey = publicKey
            };

            return record with
            {
                Signature = CertificationRecordSigning.Sign(record, hmacKey),
                Ed25519Signature = CertificationRecordEd25519.Sign(record, Convert.FromBase64String(privateKey))
            };
        }

        private static CertificationRecordData HmacOnly(CertificationRecordData record)
        {
            var stripped = record with { Ed25519Signature = null, Ed25519PublicKey = null, Signature = null };
            return stripped with { Signature = CertificationRecordSigning.Sign(stripped, HmacKey) };
        }

        private static (string PrivateKeyBase64, string PublicKeyBase64) CreateEd25519Key()
        {
            using var key = NSec.Cryptography.Key.Create(
                NSec.Cryptography.SignatureAlgorithm.Ed25519,
                new NSec.Cryptography.KeyCreationParameters { ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport });
            return (
                Convert.ToBase64String(key.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey)),
                Convert.ToBase64String(key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)));
        }
    }

    private sealed class MapCertificateResolver : ICertificateResolver
    {
        private readonly IReadOnlyDictionary<string, (CertificationRecordData Record, string Source)> _map;

        public MapCertificateResolver(
            IReadOnlyDictionary<string, (CertificationRecordData Record, string Source)> map)
        {
            _map = map;
        }

        public CertificateResolveResult Resolve(string behaviorCertContentHash)
        {
            if (_map.TryGetValue(behaviorCertContentHash, out var resolved))
                return new CertificateResolveResult(true, resolved.Record, resolved.Source);

            return new CertificateResolveResult(false, null, null);
        }
    }

    /// <summary>
    /// Deterministic Tier-2 fake: alpha advances phases, beta releases armed to ready.
    /// Catches swapped-cert transitions that Tier-1 structural checks cannot see.
    /// </summary>
    private sealed class PhaseReplayer : ITransitionReplayer
    {
        private readonly StateSchema _schema;

        public PhaseReplayer(StateSchema schema) => _schema = schema;

        public TransitionReplayResult Replay(
            string priorStateHash,
            string action,
            CertificationRecordData behaviorCert,
            string brickSource)
        {
            var phase = DecodePhase(priorStateHash);
            if (phase is null)
            {
                return new TransitionReplayResult(
                    false,
                    null,
                    "replay-prior-state-invalid",
                    "Prior state hash is not a known witness phase value.");
            }

            string? nextPhase = (phase, brickSource, action) switch
            {
                ("genesis", BehaviorAlphaSource, "phase:advance") => "phase:idle",
                ("phase:idle", BehaviorAlphaSource, "phase:advance") => "phase:armed",
                ("phase:armed", BehaviorBetaSource, "phase:release") => "phase:ready",
                _ => null
            };

            if (nextPhase is null)
            {
                return new TransitionReplayResult(
                    false,
                    null,
                    "replay-behavior-mismatch",
                    "Certified behavior does not admit the recorded action.");
            }

            var computed = _schema.ComputeBoundStateHash(nextPhase);
            return new TransitionReplayResult(true, computed, null, null);
        }

        private string? DecodePhase(string stateHash)
        {
            foreach (var phase in new[] { "genesis", "phase:idle", "phase:armed", "phase:ready" })
            {
                if (string.Equals(_schema.ComputeBoundStateHash(phase), stateHash, StringComparison.Ordinal))
                    return phase;
            }

            return null;
        }
    }
}
