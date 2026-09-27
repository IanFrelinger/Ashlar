using System.Reflection;
using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Security;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Infrastructure.Certification;
using FluentAssertions;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Pinning as an operator can actually turn it on. <see cref="CertificationVerifyOptions.TrustedEd25519PublicKeys"/>
/// was always settable and <see cref="CertificationVerifyOptions.PinningEnabled"/> always read it,
/// but nothing outside tests ever assigned it: every production verifier therefore ran with pinning
/// off and checked each signature against the public key the RECORD carries, which a record signed
/// with an attacker's own keypair satisfies. <see cref="CertificationTrustPolicy"/> is the assignment.
/// These tests drive it THROUGH the production consumers — the file-backed record store and the
/// self-extend admission edge — rather than handing a verifier options built in the test, because
/// options built in a test prove only that the verifier's pinning branch works, which was never the
/// part that was missing.
///
/// <para>The second direction matters as much as the first: configuration that is present but
/// unusable must not resolve to an EMPTY pinning set. An empty set reads as pinning off at every
/// call site that tests <c>PinningEnabled</c>, so silently degrading to it would turn an operator's
/// attempt to pin into the weakest posture available, exactly when they believed they had the
/// strongest.</para>
///
/// <para>Joins the non-parallel <c>EnvironmentVariables</c> collection. This class writes no
/// environment variable, but the self-extend edge verifies with no explicit HMAC key, so it READS
/// <c>ASHLAR_CERT_DEV_HMAC_KEY</c> through <c>CertificationRecordSigning</c>; a class that rewrote
/// that variable beside it would invalidate signatures minted here between the mint and the
/// verification.</para>
/// </summary>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class TrustedKeyPinningConfigurationTests : IDisposable
{
    private const string BrickRelativePath = "src/Ashlar.Bricks.Pinning/PinnedProbeBrick.cs";
    private const string BrickSource = "namespace Ashlar.Bricks.Pinning; public sealed class PinnedProbeBrick { }";
    private const string StoreHmacKey = "pinning-tests-explicit-hmac-key";

    private readonly string _directory;

    /// <summary>Creates the record directory this class's file store reads.</summary>
    public TrustedKeyPinningConfigurationTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "ashlar-pinning-config-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Removes the record directory.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Fact (a) on the load path: a configured pinning set makes the store that re-verifies records
    /// on load refuse one signed by a key the operator did not list — and the same store, pinned to
    /// the signer that actually minted it, still returns it. The positive control is what makes this
    /// load-bearing: <c>Get</c> reports an unverifiable record as ABSENT, so a refusal alone could
    /// have come from anything.
    /// </summary>
    [Fact]
    public void A_configured_pinning_set_makes_the_record_store_refuse_a_foreign_signer()
    {
        var ours = CreateEd25519Key();
        var theirs = CreateEd25519Key();
        var brickId = SaveRecordSignedBy(theirs.PrivateKeyBase64);

        Store(Policy(ours.PublicKeyBase64)).Get(brickId).Should().BeNull(
            "the record is signed by a key this operator did not pin, and verification checks the "
            + "signature against the key the record itself carries, so pinning is the only thing "
            + "that can tell those two keys apart");

        Store(Policy(theirs.PublicKeyBase64)).Get(brickId).Should().NotBeNull(
            "pinned to the key that actually minted the record, the same store must still load it — "
            + "otherwise the refusal above proves nothing about pinning");

        Store(CertificationTrustPolicy.Unpinned).Get(brickId).Should().NotBeNull(
            "this is the defect being closed: with no pinning set configured, a record signed by "
            + "any keypair at all is self-consistent and loads");
    }

    /// <summary>
    /// Fact (a) on the admission edge: the same configuration refuses a self-produced brick whose
    /// certificate was signed by a key the operator did not list, with the verifier's own failure
    /// code in the denial reason.
    /// </summary>
    [Fact]
    public void A_configured_pinning_set_makes_the_self_extend_edge_refuse_a_foreign_signer()
    {
        var ours = CreateEd25519Key();
        var theirs = CreateEd25519Key();
        var store = new InMemoryCertificationRecordStore();
        var brickId = BrickAdmissionPathHelper.InferBrickId(BrickRelativePath, BrickSource)!;
        store.Save(new CertificationRecordSigner(ed25519PrivateKeyBase64: theirs.PrivateKeyBase64)
            .SignRecord(UnsignedRecord(brickId)));

        var call = new ToolCall(
            "repo.fs.write",
            JsonSerializer.SerializeToElement(new { path = BrickRelativePath, content = BrickSource }));
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?>
        {
            ["RepoRoot"] = "/workspace",
            ["selfExtendAdmission"] = true,
        });

        new SelfProducedBrickCertificationPolicy(store, Policy(ours.PublicKeyBase64))
            .Approve(call, snapshot, out var refused)
            .Should().BeFalse("the certificate is signed by a key this operator did not pin");
        refused.Should().Contain("ed25519-key-not-trusted");

        new SelfProducedBrickCertificationPolicy(store, Policy(theirs.PublicKeyBase64))
            .Approve(call, snapshot, out var admitted)
            .Should().BeTrue("pinned to the minting key, the same write must still be admitted");
        admitted.Should().Be("OK");
    }

    /// <summary>
    /// Fact (a), multi-signer: an operator listing several keys — commas, semicolons, newlines,
    /// duplicates — gets a set that accepts each of them and nothing else.
    /// </summary>
    [Fact]
    public void A_configured_pinning_set_accepts_every_signer_it_lists_and_no_other()
    {
        var first = CreateEd25519Key();
        var second = CreateEd25519Key();
        var stranger = CreateEd25519Key();
        var policy = CertificationTrustPolicy.FromConfiguration(
            Config($" {first.PublicKeyBase64};{second.PublicKeyBase64}\n\t{first.PublicKeyBase64} "));

        policy.TrustedEd25519PublicKeys.Should().BeEquivalentTo(
            new[] { first.PublicKeyBase64, second.PublicKeyBase64 },
            "separators are commas, semicolons and whitespace, and a repeated key is one key");

        var store = Store(policy);
        store.Get(SaveRecordSignedBy(first.PrivateKeyBase64)).Should().NotBeNull();
        store.Get(SaveRecordSignedBy(second.PrivateKeyBase64)).Should().NotBeNull();
        store.Get(SaveRecordSignedBy(stranger.PrivateKeyBase64)).Should().BeNull(
            "a key absent from the list stays absent from the trusted set");
    }

    /// <summary>
    /// Fact (b): configuration that is present but names no key is refused at resolution. Returning
    /// a policy whose set is empty would read as "pinning off" everywhere downstream, so there is
    /// no such policy to inspect — the refusal is the whole assertion.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    [InlineData(",")]
    [InlineData(" ; , ")]
    public void Configuration_that_names_no_key_is_refused_rather_than_disabling_pinning(string configured)
    {
        var resolve = () => CertificationTrustPolicy.FromConfiguration(Config(configured));

        resolve.Should().Throw<CertificationTrustConfigurationException>(
                "an empty pinning set turns pinning OFF, which is the dangerous direction: "
                + "verification would accept whatever key the record carries")
            .WithMessage($"*{CertificationTrustPolicy.TrustedKeysVariable}*");
    }

    /// <summary>
    /// Fact (b), malformed rather than empty: an entry that cannot be an Ed25519 public key is
    /// refused, including when it sits beside a good one. Dropping the bad entry would leave a set
    /// that is quietly narrower than the operator wrote, and dropping the only entry would leave no
    /// set at all.
    /// </summary>
    [Theory]
    [InlineData("this-is-not-base64!!")]
    [InlineData("c2hvcnQ=")]
    // 44 unpadded Base64 characters decode to 33 bytes: one byte too long for Ed25519, and the
    // kind of near-miss a truncated or concatenated secret produces. 43 characters plus '=' would
    // decode to a structurally valid 32-byte key, which is why the length is spelled out here.
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Configuration_that_cannot_be_a_key_is_refused(string configured)
    {
        var alone = () => CertificationTrustPolicy.FromConfiguration(Config(configured));
        alone.Should().Throw<CertificationTrustConfigurationException>();

        var good = CreateEd25519Key().PublicKeyBase64;
        var beside = () => CertificationTrustPolicy.FromConfiguration(Config($"{good},{configured}"));
        beside.Should().Throw<CertificationTrustConfigurationException>(
            "a set that silently loses an entry is not the set the operator configured");
    }

    /// <summary>
    /// Fact (b), the operator's own assertion: a host told that pinning must be in effect refuses to
    /// start without keys, and a switch value it cannot read is refused rather than taken as "not
    /// required" — a typo must not be the thing that disables pinning.
    /// </summary>
    [Fact]
    public void Requiring_pinning_without_keys_refuses_to_start()
    {
        var missingKeys = () => CertificationTrustPolicy.FromConfiguration(Config(null, "true"));
        missingKeys.Should().Throw<CertificationTrustConfigurationException>()
            .WithMessage($"*{CertificationTrustPolicy.TrustedKeysVariable}*");

        var typo = () => CertificationTrustPolicy.FromConfiguration(
            Config(CreateEd25519Key().PublicKeyBase64, "treu"));
        typo.Should().Throw<CertificationTrustConfigurationException>(
            "an unrecognized value read as false would silently switch pinning off");

        CertificationTrustPolicy
            .FromConfiguration(Config(CreateEd25519Key().PublicKeyBase64, "yes"))
            .PinningConfigured.Should().BeTrue();
        CertificationTrustPolicy
            .FromConfiguration(Config(CreateEd25519Key().PublicKeyBase64, "off"))
            .PinningConfigured.Should().BeTrue("keys configure pinning whether or not it is demanded");
    }

    /// <summary>
    /// Absent configuration is not malformed configuration: it leaves every preset byte-for-byte
    /// where it was, so wiring this policy into the production verifiers changed nothing for a host
    /// that configures nothing.
    /// </summary>
    [Fact]
    public void Absent_configuration_leaves_the_presets_exactly_as_they_were()
    {
        var policy = CertificationTrustPolicy.FromConfiguration(_ => null);

        policy.PinningConfigured.Should().BeFalse();
        policy.TrustedEd25519PublicKeys.Should().BeNull("empty and absent must not be the same value");
        policy.Strict.Should().BeSameAs(CertificationVerifyOptions.Strict);
        policy.Default.Should().BeSameAs(CertificationVerifyOptions.Default);
        policy.Apply(CertificationVerifyOptions.Legacy).Should().BeSameAs(CertificationVerifyOptions.Legacy);
    }

    /// <summary>
    /// Pinning is additive: it must not relax any other strictness the basis carried. The reflection
    /// check is the guard against the copy in <c>Apply</c> rotting — a sixth strictness option added
    /// to <see cref="CertificationVerifyOptions"/> would be silently dropped on every pinned host,
    /// which is a relaxation nobody would see.
    /// </summary>
    [Fact]
    public void Pinning_adds_to_strictness_and_relaxes_nothing()
    {
        var key = CreateEd25519Key().PublicKeyBase64;
        var pinned = Policy(key).Strict;

        pinned.PinningEnabled.Should().BeTrue();
        pinned.TrustedEd25519PublicKeys.Should().BeEquivalentTo(new[] { key });
        pinned.RequireEd25519Signature.Should().BeTrue("an unsigned record cannot be pinned");
        pinned.MinimumSchemaVersion.Should().Be(CertificationVerifyOptions.Strict.MinimumSchemaVersion);
        pinned.RequireGateEmittedArtifact.Should().Be(CertificationVerifyOptions.Strict.RequireGateEmittedArtifact);
        pinned.RequireCertifierIdentity.Should().Be(CertificationVerifyOptions.Strict.RequireCertifierIdentity);

        typeof(CertificationVerifyOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(
                new[]
                {
                    nameof(CertificationVerifyOptions.MinimumSchemaVersion),
                    nameof(CertificationVerifyOptions.RequireEd25519Signature),
                    nameof(CertificationVerifyOptions.TrustedEd25519PublicKeys),
                    nameof(CertificationVerifyOptions.RequireGateEmittedArtifact),
                    nameof(CertificationVerifyOptions.RequireCertifierIdentity),
                },
                "CertificationTrustPolicy.Apply copies these five by hand; a new settable option "
                + "must be added there too, or every pinned host silently loses it");
    }

    /// <summary>
    /// Configuration may turn pinning on; it may not redirect a pinning set that code already chose,
    /// and merging the two would widen the accepted signer set beyond either. Applying the same set
    /// twice is not a conflict.
    /// </summary>
    [Fact]
    public void Applying_configuration_over_a_pinning_set_chosen_in_code_is_refused()
    {
        var mine = CreateEd25519Key().PublicKeyBase64;
        var yours = CreateEd25519Key().PublicKeyBase64;
        var inCode = new CertificationVerifyOptions { TrustedEd25519PublicKeys = new[] { mine } };

        var redirect = () => Policy(yours).Apply(inCode);
        redirect.Should().Throw<CertificationTrustConfigurationException>();

        Policy(mine).Apply(inCode).TrustedEd25519PublicKeys.Should().BeEquivalentTo(new[] { mine });
    }

    private static Func<string, string?> Config(string? trustedKeys, string? pinningRequired = null) =>
        name => name switch
        {
            CertificationTrustPolicy.TrustedKeysVariable => trustedKeys,
            CertificationTrustPolicy.PinningRequiredVariable => pinningRequired,
            _ => null,
        };

    private static CertificationTrustPolicy Policy(string publicKeyBase64) =>
        CertificationTrustPolicy.FromConfiguration(Config(publicKeyBase64));

    private FileCertificationRecordStore Store(CertificationTrustPolicy policy) =>
        new(_directory, new CertificationRecordSigner(hmacKey: StoreHmacKey), policy);

    /// <summary>
    /// Writes a record for a fresh brick id, signed by <paramref name="privateKeyBase64"/>, and
    /// returns that id. The store's own <c>Save</c> does the writing, so the bytes under test are
    /// the bytes production writes.
    /// </summary>
    private string SaveRecordSignedBy(string privateKeyBase64)
    {
        var brickId = "pinned-probe-" + Guid.NewGuid().ToString("N")[..8];
        var signed = new CertificationRecordSigner(
                hmacKey: StoreHmacKey,
                ed25519PrivateKeyBase64: privateKeyBase64)
            .SignRecord(UnsignedRecord(brickId));
        Store(CertificationTrustPolicy.Unpinned).Save(signed);
        return brickId;
    }

    /// <summary>
    /// A record that satisfies every <c>Strict</c> requirement except the pinning set: trust-loop
    /// schema, admitted PASS, content bound to <see cref="BrickSource"/>, and both compile-authority
    /// inputs. Pinning is then the only thing that can decide it.
    /// </summary>
    private static CertificationRecord UnsignedRecord(string brickId)
    {
        var hash = BrickContentHasher.ComputeSha256(BrickSource);
        return new CertificationRecord
        {
            Status = "PASS",
            Stage = "admit",
            Admitted = true,
            Signed = true,
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = brickId,
            ContentHash = hash,
            SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
            Inputs = new[]
            {
                new CertificationInput
                {
                    Kind = CertificationInputKinds.GateEmittedArtifact,
                    Id = brickId,
                    Hash = hash,
                },
                CertifierIdentity.ToInput(),
            },
        };
    }

    private static (string PrivateKeyBase64, string PublicKeyBase64) CreateEd25519Key()
    {
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (
            Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)),
            Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }
}
