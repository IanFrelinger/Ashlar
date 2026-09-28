using System.Reflection;
using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Security;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.Composition;
using Ashlar.Tests.Infrastructure.Certification.Fixtures;
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
/// <para>Joins the non-parallel <c>EnvironmentVariables</c> collection, for two reasons now.
/// <see cref="The_environment_variables_an_operator_sets_are_the_ones_that_turn_pinning_on"/>
/// WRITES <c>ASHLAR_CERT_TRUSTED_ED25519_KEYS</c> and <c>ASHLAR_CERT_PINNING_REQUIRED</c>, which
/// are process-global; and the self-extend edge verifies with no explicit HMAC key, so it READS
/// <c>ASHLAR_CERT_DEV_HMAC_KEY</c> through <c>CertificationRecordSigning</c>, and a class that
/// rewrote that variable beside it would invalidate signatures minted here between the mint and
/// the verification. The collection serializes the classes in it; what it cannot serialize is a
/// class in another collection resolving <c>CertificationTrustPolicy.Ambient</c> for the first
/// time inside the write window, so that test resolves <c>Ambient</c> itself before writing
/// anything.</para>
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
    /// start without keys.
    /// </summary>
    [Fact]
    public void Requiring_pinning_without_keys_refuses_to_start()
    {
        var missingKeys = () => CertificationTrustPolicy.FromConfiguration(Config(null, "true"));
        missingKeys.Should().Throw<CertificationTrustConfigurationException>()
            .WithMessage($"*{CertificationTrustPolicy.TrustedKeysVariable}*");

        CertificationTrustPolicy
            .FromConfiguration(Config(CreateEd25519Key().PublicKeyBase64, "yes"))
            .PinningConfigured.Should().BeTrue();
        CertificationTrustPolicy
            .FromConfiguration(Config(CreateEd25519Key().PublicKeyBase64, "off"))
            .PinningConfigured.Should().BeTrue("keys configure pinning whether or not it is demanded");
    }

    /// <summary>
    /// Fact (b), the switch itself: a value this code cannot read refuses to start rather than
    /// being taken as "not required".
    ///
    /// <para><b>Blank is the case that matters, and it was a fail-open.</b>
    /// <c>ParsePinningRequired</c> treated a present-but-empty value as falsy, so a deployment
    /// template that rendered <c>ASHLAR_CERT_PINNING_REQUIRED=</c> with nothing substituted into
    /// it — a Kubernetes configMap empty value, <c>export VAR=$UNSET</c>, a YAML empty string, an
    /// <c>IConfiguration</c> key present with no value — started the host UNPINNED while the
    /// operator believed they had demanded pinning, which is the one failure this variable exists
    /// to catch. The sibling keys variable throws for the identical input. Keys are supplied in
    /// every case here, so the only thing that can throw is the switch — and the message
    /// assertion names the switch's own variable, so the keys refusal cannot satisfy it.</para>
    /// </summary>
    [Theory]
    [InlineData("treu")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_pinning_switch_value_that_cannot_be_read_refuses_to_start(string configured)
    {
        var keys = CreateEd25519Key().PublicKeyBase64;

        var resolve = () => CertificationTrustPolicy.FromConfiguration(Config(keys, configured));

        resolve.Should().Throw<CertificationTrustConfigurationException>(
                "a value read as false would silently switch pinning off, and the operator would "
                + "see no error at all")
            .WithMessage($"*{CertificationTrustPolicy.PinningRequiredVariable}*");
    }

    /// <summary>
    /// The path production actually uses, executed rather than described.
    ///
    /// <para>Every wiring site resolves <see cref="CertificationTrustPolicy.Ambient"/>, whose
    /// factory is <see cref="CertificationTrustPolicy.FromEnvironment"/>. Every OTHER fact in this
    /// class reaches <c>FromConfiguration</c> through <see cref="Config"/>, whose keys are
    /// <c>CertificationTrustPolicy.TrustedKeysVariable</c> itself — so arrange and act read the
    /// same symbol, and changing that constant's VALUE, or breaking the
    /// <c>Environment.GetEnvironmentVariable</c> call, leaves them all green while no operator can
    /// turn pinning on. This one sets the REAL variables.</para>
    ///
    /// <para><c>FromEnvironment</c> and not <c>Ambient</c>: <c>Ambient</c> is a
    /// <see cref="Lazy{T}"/> resolved once per process, so it would answer whatever the first
    /// caller anywhere in the run happened to see. For the same reason this test TOUCHES
    /// <c>Ambient</c> before it writes anything — every store, registry and host built anywhere in
    /// this assembly resolves it on construction, and a first resolution landing inside this
    /// test's window would pin the whole process to a key only this test knows.</para>
    /// </summary>
    [Fact]
    public void The_environment_variables_an_operator_sets_are_the_ones_that_turn_pinning_on()
    {
        _ = CertificationTrustPolicy.Ambient;

        var key = CreateEd25519Key().PublicKeyBase64;
        var keysBefore = Environment.GetEnvironmentVariable(CertificationTrustPolicy.TrustedKeysVariable);
        var requiredBefore = Environment.GetEnvironmentVariable(CertificationTrustPolicy.PinningRequiredVariable);
        try
        {
            Environment.SetEnvironmentVariable(CertificationTrustPolicy.TrustedKeysVariable, key);
            Environment.SetEnvironmentVariable(CertificationTrustPolicy.PinningRequiredVariable, "true");

            var configured = CertificationTrustPolicy.FromEnvironment();

            configured.PinningConfigured.Should().BeTrue(
                "this is the only path any production consumer uses: all seven wiring sites fall "
                + "through to Ambient, whose factory is FromEnvironment");
            configured.TrustedEd25519PublicKeys.Should().BeEquivalentTo(new[] { key });
            configured.Strict.PinningEnabled.Should().BeTrue();

            Store(configured).Get(SaveRecordSignedBy(CreateEd25519Key().PrivateKeyBase64))
                .Should().BeNull("a signer absent from the environment's list is not trusted");

            Environment.SetEnvironmentVariable(CertificationTrustPolicy.TrustedKeysVariable, null);
            var demandedWithoutKeys = () => CertificationTrustPolicy.FromEnvironment();
            demandedWithoutKeys.Should().Throw<CertificationTrustConfigurationException>(
                "the operator's assertion is checkable through the environment too, or it is not "
                + "checkable at all");

            Environment.SetEnvironmentVariable(CertificationTrustPolicy.PinningRequiredVariable, null);
            CertificationTrustPolicy.FromEnvironment().Strict
                .Should().BeSameAs(CertificationVerifyOptions.Strict,
                    "with neither variable set, the environment path leaves the preset alone");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CertificationTrustPolicy.TrustedKeysVariable, keysBefore);
            Environment.SetEnvironmentVariable(CertificationTrustPolicy.PinningRequiredVariable, requiredBefore);
        }
    }

    /// <summary>
    /// <see cref="CertificationTrustPolicy.FromTrustedKeys"/> — new public surface on a security
    /// type, documented as the path for "a mounted trust bundle, a parsed manifest", and until now
    /// with no test at all. It pins like a setting does, and it refuses the entries a setting
    /// refuses, including the blank line a bundle read line-by-line will contain.
    /// </summary>
    [Fact]
    public void A_trust_bundle_handed_in_directly_pins_the_way_a_setting_does()
    {
        var first = CreateEd25519Key();
        var second = CreateEd25519Key();
        var stranger = CreateEd25519Key();

        var policy = CertificationTrustPolicy.FromTrustedKeys(
            new[] { first.PublicKeyBase64, second.PublicKeyBase64 });

        policy.PinningConfigured.Should().BeTrue();
        var store = Store(policy);
        store.Get(SaveRecordSignedBy(first.PrivateKeyBase64)).Should().NotBeNull();
        store.Get(SaveRecordSignedBy(second.PrivateKeyBase64)).Should().NotBeNull();
        store.Get(SaveRecordSignedBy(stranger.PrivateKeyBase64)).Should().BeNull(
            "a bundle is a pinning set like any other, or it is decoration");

        var withBlankLine = () => CertificationTrustPolicy.FromTrustedKeys(
            new[] { first.PublicKeyBase64, "   " });
        withBlankLine.Should().Throw<CertificationTrustConfigurationException>(
            "dropping it would return a set narrower than the bundle lists, which refuses honest "
            + "records signed by the key that went missing");

        var notAKey = () => CertificationTrustPolicy.FromTrustedKeys(new[] { "c2hvcnQ=" });
        notAKey.Should().Throw<CertificationTrustConfigurationException>();

        var nothingAtAll = () => CertificationTrustPolicy.FromTrustedKeys(Array.Empty<string>());
        nothingAtAll.Should().Throw<CertificationTrustConfigurationException>(
            "an empty bundle reads as pinning off at every call site downstream");
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
    /// <c>Apply</c> must carry its ARGUMENT through, not a preset it was once handed.
    ///
    /// <para>This is a hole a reviewer proved rather than argued:
    /// <see cref="Pinning_adds_to_strictness_and_relaxes_nothing"/> applies <c>Strict</c> and
    /// compares every value back to <c>Strict</c>, so hardcoding <c>Strict</c>'s three copied
    /// values inside <c>Apply</c> left the whole suite green — while every pinned host that asked
    /// for <c>Default</c> or <c>Legacy</c> would silently have been handed <c>Strict</c>, which is
    /// a strictness change nobody configured. A configured policy's <c>Default</c> is therefore
    /// checked against the <c>Default</c> preset, and <c>Apply(Legacy)</c> against <c>Legacy</c> —
    /// the two bases whose values DIFFER from Strict's.</para>
    /// </summary>
    [Fact]
    public void A_configured_policy_pins_each_basis_without_promoting_it_to_Strict()
    {
        var key = CreateEd25519Key().PublicKeyBase64;
        var policy = Policy(key);

        var pinnedDefault = policy.Default;
        pinnedDefault.PinningEnabled.Should().BeTrue();
        pinnedDefault.MinimumSchemaVersion.Should()
            .Be(CertificationVerifyOptions.Default.MinimumSchemaVersion);
        pinnedDefault.RequireGateEmittedArtifact.Should()
            .Be(CertificationVerifyOptions.Default.RequireGateEmittedArtifact).And.BeFalse(
                "Default demands no gate-emitted artifact and pinning adds none; if this turns "
                + "true, Apply returned Strict's value in place of its basis's");
        pinnedDefault.RequireCertifierIdentity.Should()
            .Be(CertificationVerifyOptions.Default.RequireCertifierIdentity).And.BeFalse(
                "Default names no judge requirement either");

        var pinnedLegacy = policy.Apply(CertificationVerifyOptions.Legacy);
        pinnedLegacy.PinningEnabled.Should().BeTrue(
            "a configured policy pins whatever basis it is given, Legacy included");
        pinnedLegacy.MinimumSchemaVersion.Should()
            .Be(CertificationVerifyOptions.Legacy.MinimumSchemaVersion).And.Be(0,
                "Legacy sets no schema floor, and configuration that says nothing about schema "
                + "must not raise one");
        pinnedLegacy.RequireGateEmittedArtifact.Should().BeFalse();
        pinnedLegacy.RequireCertifierIdentity.Should().BeFalse();
        pinnedLegacy.RequireEd25519Signature.Should().BeTrue(
            "the single value Apply sets on its own authority: an unsigned record cannot be pinned");
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

    /// <summary>
    /// The conflict check has to be SET equality on both sides, and a count plus one-directional
    /// containment is not.
    ///
    /// <para>Basis <c>{A, A}</c> against configuration <c>{A, B}</c>: the counts match and every
    /// basis key is present, so the old check reported "same set", no conflict was raised, and
    /// <c>Apply</c> returned options accepting B — a signer the basis did not accept. That is
    /// precisely the widening <c>Apply</c>'s own summary promises never happens, so the guarantee
    /// was documented and false. The configured side is de-duplicated by <c>Validate</c>; the basis
    /// side comes from a caller-supplied options object and nothing de-duplicates it.</para>
    /// </summary>
    [Fact]
    public void A_basis_that_repeats_one_key_is_not_the_same_set_as_one_that_adds_another()
    {
        var mine = CreateEd25519Key().PublicKeyBase64;
        var yours = CreateEd25519Key().PublicKeyBase64;
        var repeatedInCode = new CertificationVerifyOptions
        {
            TrustedEd25519PublicKeys = new[] { mine, mine },
        };

        var widen = () => Policy($"{mine},{yours}").Apply(repeatedInCode);
        widen.Should().Throw<CertificationTrustConfigurationException>(
            "the basis accepts one signer and the configuration two, so this is the widening the "
            + "conflict check exists to refuse — equal counts are not equal sets");

        Policy($"{mine} {mine}").Apply(repeatedInCode).TrustedEd25519PublicKeys
            .Should().BeEquivalentTo(
                new[] { mine },
                "one key on both sides, however often either side repeats it, is the same set and "
                + "not a conflict — a count comparison would have refused this one instead");
    }

    /// <summary>
    /// Validation decodes; enforcement compares strings. The two have to agree on the encoding, so
    /// the stored set is canonical.
    ///
    /// <para>A 32-byte key has four valid 44-character Base64 encodings, because the 43rd
    /// character carries two bits the decoder discards. Storing the operator's string verbatim
    /// therefore let a value that decodes to exactly the right key match no record at all:
    /// <c>record.Ed25519PublicKey</c> is always <c>Convert.ToBase64String</c> of the raw key and
    /// both verification tiers compare ordinally. Fail-CLOSED, so an outage rather than a hole —
    /// but a host that refuses every honest record while reporting itself correctly pinned is a
    /// nasty thing to debug.</para>
    /// </summary>
    [Fact]
    public void A_key_encoded_differently_but_decoding_the_same_still_matches_its_records()
    {
        var signer = CreateEd25519Key();
        var variant = SecondValidEncodingOf(signer.PublicKeyBase64);

        variant.Should().NotBeNull(
            "this fact's premise is that a second valid encoding of the same 32 bytes exists; "
            + "without one it asserts nothing, so the premise is checked rather than assumed");
        variant.Should().NotBe(signer.PublicKeyBase64, "the two STRINGS must differ");
        Convert.FromBase64String(variant!).Should().Equal(
            Convert.FromBase64String(signer.PublicKeyBase64), "and the BYTES must not");

        var policy = Policy(variant!);

        policy.TrustedEd25519PublicKeys.Should().BeEquivalentTo(
            new[] { signer.PublicKeyBase64 },
            "the operator's encoding is normalized to the one a record carries");
        Store(policy).Get(SaveRecordSignedBy(signer.PrivateKeyBase64)).Should().NotBeNull(
            "this is the key that minted the record; storing the operator's spelling verbatim "
            + "would refuse it");
    }

    /// <summary>
    /// Fact (a) on a third production consumer: <c>CertifiedBrickRegistry</c>'s admission path.
    /// Five of the seven wiring sites were unfalsifiable — with nothing configured
    /// <c>Ambient.Strict</c> IS the <c>Strict</c> preset instance, so reverting a call site to the
    /// bare preset is invisible to every test that does not configure a policy. This one and
    /// <c>PinnedHotSwapVerifyAtLoadTests</c> take two of the five.
    /// </summary>
    [Fact]
    public void A_configured_pinning_set_makes_the_certified_registry_refuse_a_foreign_signer()
    {
        var ours = CreateEd25519Key();
        var theirs = CreateEd25519Key();
        var brick = new MutationProbeBrick();
        var record = new CertificationRecordSigner(
                hmacKey: StoreHmacKey,
                ed25519PrivateKeyBase64: theirs.PrivateKeyBase64)
            .SignRecord(UnsignedRecord(brick.Id));
        var verifier = new CertificationRecordSigner(hmacKey: StoreHmacKey);

        new CertifiedBrickRegistry(
                new InMemoryCertificationRecordStore(),
                verifier,
                trustPolicy: Policy(ours.PublicKeyBase64))
            .TryAdmit(brick, record)
            .Should().BeFalse(
                "the record is signed by a key this operator did not pin, and without a pinning "
                + "set the registry only asks whether the record is self-consistent");

        new CertifiedBrickRegistry(
                new InMemoryCertificationRecordStore(),
                verifier,
                trustPolicy: Policy(theirs.PublicKeyBase64))
            .TryAdmit(brick, record)
            .Should().BeTrue(
                "pinned to the key that minted it, the same record must still be admitted — "
                + "otherwise the refusal above proves nothing about pinning");
    }

    /// <summary>
    /// The operator-facing NAMES, pinned to their literals and to the document that tells an
    /// operator to set them.
    ///
    /// <para><b>Measured, not assumed.</b> Every other fact in this class — the environment fact
    /// above included — writes and reads through
    /// <c>CertificationTrustPolicy.TrustedKeysVariable</c> itself, so arrange and act agree on
    /// whatever that constant happens to say. A reviewer changed its VALUE to
    /// <c>ASHLAR_CERT_TRUSTED_ED25519_KEYS_MUTANT</c> and reddened nothing at all, while every
    /// operator following <c>docs/Configuration.md</c> would have been setting a variable this
    /// process never reads — pinning silently off, with no error anywhere. A variable whose NAME is
    /// pinned by nothing is not an operator-facing variable; it is an internal detail with
    /// documentation attached.</para>
    ///
    /// <para>The documentation half is the other direction of the same fact: <c>Configuration.md</c>
    /// is the only place an operator learns the spelling, and line 444 of it had already drifted
    /// from this code once. Asserting the code's literal alone would let the doc rot instead.</para>
    /// </summary>
    [Fact]
    public void The_variable_names_an_operator_is_told_to_set_are_the_names_this_type_reads()
    {
        CertificationTrustPolicy.TrustedKeysVariable.Should().Be(
            "ASHLAR_CERT_TRUSTED_ED25519_KEYS",
            "this exact string lives in docs/Configuration.md and in deployment templates nothing "
            + "in this repository can grep, so renaming it turns pinning off for every host that "
            + "had configured it — fail-open, and silent");
        CertificationTrustPolicy.PinningRequiredVariable.Should().Be(
            "ASHLAR_CERT_PINNING_REQUIRED",
            "the same, for the switch whose whole purpose is making pinning checkable at startup");

        var configurationDoc = Path.Combine(
            RepoPathResolver.FindRepoRoot(), "docs", "Configuration.md");
        File.Exists(configurationDoc).Should().BeTrue(
            "the documentation half of this fact needs the document to exist, or it asserts nothing");

        var documented = File.ReadAllText(configurationDoc);
        documented.Should().Contain(
            CertificationTrustPolicy.TrustedKeysVariable,
            "the name an operator is told to set and the name this type reads are one fact, or they "
            + "are two facts that drift");
        documented.Should().Contain(
            CertificationTrustPolicy.PinningRequiredVariable,
            "an undocumented switch is a switch nobody sets");
    }

    /// <summary>
    /// A basis that pins a blank or null entry is refused by name, rather than reaching
    /// <c>SameKeys</c>.
    ///
    /// <para>This is the sharp edge the <c>SameKeys</c> rewrite introduced. The loop it replaced
    /// tolerated a null entry; <c>new HashSet&lt;string&gt;(left, StringComparer.Ordinal)</c> does
    /// not necessarily, and a security type should not be resting on which. No production caller
    /// pins in code today, so nothing reached it — which is exactly why it had to be decided
    /// deliberately rather than left to the framework.</para>
    ///
    /// <para>The configured side is <c>{mine}</c> and the basis is <c>{mine}</c> plus a hole, so
    /// the two sets agree on every stated key: without the guard this is either a bare
    /// <c>ArgumentNullException</c> out of a security type or a CONFLICT report about a key the
    /// caller never stated. The message assertion is what separates the two refusals — the
    /// conflict message does not say "blank entry".</para>
    /// </summary>
    [Fact]
    public void A_basis_that_pins_a_blank_entry_is_refused_by_name()
    {
        var mine = CreateEd25519Key().PublicKeyBase64;
        var policy = Policy(mine);

        var nullEntry = () => policy.Apply(new CertificationVerifyOptions
        {
            TrustedEd25519PublicKeys = new[] { mine, null! },
        });
        nullEntry.Should().Throw<CertificationTrustConfigurationException>(
                "a null entry is a key no signature can match, and the answer to it is this type's "
                + "own named refusal rather than whatever StringComparer.Ordinal does with null")
            .WithMessage("*blank entry*");

        var whitespaceEntry = () => policy.Apply(new CertificationVerifyOptions
        {
            TrustedEd25519PublicKeys = new[] { mine, "   " },
        });
        whitespaceEntry.Should().Throw<CertificationTrustConfigurationException>(
                "whitespace is refused for the same reason and with the same message, so the two "
                + "cannot diverge later")
            .WithMessage("*blank entry*");

        policy.Apply(new CertificationVerifyOptions { TrustedEd25519PublicKeys = new[] { mine } })
            .TrustedEd25519PublicKeys.Should().BeEquivalentTo(
                new[] { mine },
                "the same basis without the hole is the same set as the configuration and must "
                + "still pass, or the guard has turned an honest basis into a startup failure");
    }

    /// <summary>
    /// Fact (a) on a fourth production consumer: the composition lane's constituent check.
    ///
    /// <para>Of the seven wiring sites, three were unfalsifiable even after the first round — with
    /// nothing configured <c>Ambient.Strict</c> IS the <c>Strict</c> preset instance, so reverting a
    /// call site to the bare preset is invisible to every test that configures no policy. This one
    /// has the same seam the registry and the hot-swap host have (an optional
    /// <c>CertificationTrustPolicy</c> parameter), so it takes a behavioural fact rather than a
    /// disclosure: a composition whose constituent record was signed by an unpinned key must fail
    /// the check, and the identical composition under a policy pinned to the minting key must
    /// pass.</para>
    ///
    /// <para>The positive control is load-bearing here for the usual reason: <c>Check</c> reports a
    /// violation for a missing record, an unadmitted record and a bad signature alike, so a failure
    /// alone would prove nothing about pinning.</para>
    /// </summary>
    [Fact]
    public void A_configured_pinning_set_makes_the_composition_constituent_check_refuse_a_foreign_signer()
    {
        var ours = CreateEd25519Key();
        var theirs = CreateEd25519Key();
        var brickId = "pinned-constituent-" + Guid.NewGuid().ToString("N")[..8];
        var store = new InMemoryCertificationRecordStore();
        store.Save(new CertificationRecordSigner(
                hmacKey: StoreHmacKey,
                ed25519PrivateKeyBase64: theirs.PrivateKeyBase64)
            .SignRecord(UnsignedRecord(brickId)));
        var verifier = new CertificationRecordSigner(hmacKey: StoreHmacKey);
        var spec = new CompositionSpec(
            "pinned-composition",
            new[] { new CompositionNode("only-node", brickId) },
            Array.Empty<CompositionEdge>(),
            Array.Empty<CompositionPort>(),
            Array.Empty<CompositionPort>());

        var foreign = CompositionConstituentChecker.Check(
            spec, store, verifier, trustPolicy: Policy(ours.PublicKeyBase64));

        foreign.Passed.Should().BeFalse(
            "the constituent's record is signed by a key this operator did not pin, and without a "
            + "pinning set the check only asks whether the record is self-consistent");
        foreign.Violations.Should().ContainSingle()
            .Which.Should().Contain(
                "invalid certification signature",
                "the violation must be the SIGNATURE one — a missing or unadmitted record produces "
                + "its own violation, and either would satisfy a bare Passed==false");

        CompositionConstituentChecker
            .Check(spec, store, verifier, trustPolicy: Policy(theirs.PublicKeyBase64))
            .Passed.Should().BeTrue(
                "pinned to the key that minted it, the identical composition must pass — otherwise "
                + "the refusal above proves nothing about pinning");
    }

    /// <summary>
    /// A second valid 44-character Base64 encoding of the same 32 bytes, or null when this
    /// runtime's decoder rejects the spare bits — in which case the trap this guards cannot arise
    /// and the caller should say so rather than pass quietly. Found by search over the alphabet
    /// rather than by bit arithmetic, so the fact does not rest on my arithmetic.
    /// </summary>
    private static string? SecondValidEncodingOf(string canonical)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        if (canonical.Length != 44)
            return null;

        var bytes = Convert.FromBase64String(canonical);
        foreach (var replacement in Alphabet)
        {
            var candidate = canonical[..42] + replacement + "=";
            if (string.Equals(candidate, canonical, StringComparison.Ordinal))
                continue;

            try
            {
                if (Convert.FromBase64String(candidate).AsSpan().SequenceEqual(bytes))
                    return candidate;
            }
            catch (FormatException)
            {
                // Not a valid encoding at all; keep looking.
            }
        }

        return null;
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
