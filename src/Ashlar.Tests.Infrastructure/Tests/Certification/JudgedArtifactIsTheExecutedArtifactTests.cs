using FluentAssertions;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Adaptation.Generation;
using Ashlar.Infrastructure.Certification;
using Ashlar.Tests.Infrastructure.Certification.Dogfood;
using Ashlar.Tests.Infrastructure.Certification.Reuse;
using Mono.Cecil;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Verifier/activator composition over an in-process certifier's artifact: verify the source and
/// artifact bytes, then execute those same bytes. This is not a packaged consumer or host test.
///
/// <para><b>Why this file exists.</b> Inside the certifier, judged-equals-executed is already
/// enforced and already tested: the gate activates and witnesses the emitted PE rather than a
/// caller-supplied brick instance, which is what
/// <c>GateEmittedArtifactTests.Gate_WitnessesActivatedPe_NotCallerBrickInstance</c> pins. The gap
/// was on the CONSUMER side of the record. There, every test exercised the artifact-hash comparison
/// as a pure function: <c>GateEmittedArtifactTests</c> and <c>CrossProjectReuseTests</c> hash bytes,
/// assert <c>artifact-hash-mismatch</c>, and execute nothing.
/// <c>CrossProjectReuseTests.HonestCertifiedBrick_ProjectB_TrustsAndRunsUntouched</c> goes furthest
/// and still stops short: it verifies <c>artifact.AssemblyBytes</c> and then reaches execution
/// through <c>ProjectBTrustConsumer.ExecuteDamageResolverSmokeAsync</c>, which <i>recompiles</i> the
/// source. So the property a consumer actually depends on -- <b>the assembly the certificate covers
/// is the assembly I ran</b> -- had no test that would go red if it stopped being true. That is
/// <c>docs/HowGatesGoQuiet.md</c> section 13: the guard fires correctly on its own input, and its
/// input is not on the path that matters.</para>
///
/// <para><b>What binds what.</b> <c>CertificationTrustVerifier.Verify(record, source, artifactBytes)</c>
/// proves a <i>file</i> is the judged file. It cannot make that file the running program. Only
/// handing the verified in-memory bytes to <c>CertifiedBrickActivator.Activate</c> does that, and the
/// assertion for it is the MVID: it is baked into the PE bytes, so the executing type's module can be
/// compared against the bytes the verifier just hashed.</para>
///
/// <para>These tests exercise the primitives discussed in <c>consumer-template/CONSUMING.md</c>.
/// They receive artifact metadata directly from the certifier; reconstruction from deployed files,
/// package availability and host registration need separate consumer integration coverage.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class JudgedArtifactIsTheExecutedArtifactTests
{
    private static readonly IntentSpec DamageResolverIntent = new(
        CursorGeneratorModel.DamageResolverIntentId,
        "Given baseDamage, critMultiplierPercent, armor, and isCrit, compute final damage.",
        BrickId: CursorGeneratorModel.DamageResolverIntentId,
        Name: "Damage Resolver");

    /// <summary>
    /// The primitive composition: Strict source+artifact verify, then activate the SAME
    /// bytes, then execute and get the certified answer out of them.
    /// </summary>
    [Fact]
    public async Task VerifiedBytes_AreTheBytesThatExecute()
    {
        var certified = await CertifyDamageResolverAsync();
        var record = ProjectBTrustConsumer.FromInternalRecord(certified.Record);

        // Leg 1 -- the certificate side. Three-argument overload: source binding AND artifact-hash
        // binding. The two-argument overload would leave the bytes unbound even under Strict, where
        // RequireGateEmittedArtifact is only a presence check on the record's input list.
        var trust = CertificationTrustVerifier.Verify(
            record,
            certified.SourceCode,
            certified.Artifact.AssemblyBytes,
            options: CertificationVerifyOptions.Strict);
        trust.Trusted.Should().BeTrue($"expected TRUSTED, got {trust.FailureCode}: {trust.Reason}");

        // Leg 2 -- the load side, on the bytes leg 1 just hashed. Not a re-read of a file: that would
        // leave a time-of-check/time-of-use window between the hash and the load.
        var executed = CertifiedBrickActivator.Activate(certified.Artifact);

        // Leg 3 -- the binding itself. The executing type must come from the judged bytes.
        var judgedMvid = ReadMvid(certified.Artifact.AssemblyBytes);
        var executingModule = executed.GetType().Assembly.ManifestModule;
        executingModule.ModuleVersionId.Should().Be(
            judgedMvid,
            "the module executing must be the one whose bytes the verifier hashed");
        executed.GetType().Assembly.GetName().Name.Should().Be(
            GateEmittedArtifactCompiler.AssemblyName,
            "the executing assembly must be the certifier's compile, not a host's or this test's");
        executed.GetType().Assembly.Should().NotBeSameAs(
            typeof(JudgedArtifactIsTheExecutedArtifactTests).Assembly,
            "a compile-time reference to the brick type is the defect this test exists to catch");
        executed.GetType().Assembly.Location.Should().BeEmpty(
            "a byte-loaded assembly has no file location; a non-empty one means this came off disk");

        // Leg 4 -- and it really is the certified behaviour that runs.
        var output = await executed.ExecuteAsync(
            new BrickInput(new Dictionary<string, object>
            {
                ["baseDamage"] = 50,
                ["critMultiplierPercent"] = 100,
                ["armor"] = 10,
                ["isCrit"] = false
            }),
            ImplementationType.Deterministic,
            new ProjectBAuditExecutionContext());
        output.Get<int>("finalDamage").Should().Be(40);
    }

    /// <summary>
    /// Why a host that compiles the brick itself cannot be bound by the record, measured rather than
    /// argued: recompiling the very source the verifier accepted produces a different assembly than
    /// the one the record names.
    ///
    /// <para>This is also the ceiling on what any boot guard can promise. The certifier's emit is not
    /// byte-reproducible -- <c>BrickCompileOptions.CompilationOptions</c> passes no
    /// <c>deterministic:</c> argument, so Roslyn's default of <c>false</c> mints a fresh MVID every
    /// emit, which <c>GateEmittedArtifactCompiler</c> admits in its own remark about the assembly name
    /// being stable for hashing of IL shape and not of MVID. So a third party re-derives the
    /// <i>verdict</i> from the source and never the <c>gate-emitted-artifact</c> hash: the exported
    /// DLL is trusted <b>as shipped</b>, not independently re-derivable.</para>
    /// </summary>
    [Fact]
    public async Task RecompilingTheVerifiedSource_DoesNotReproduceTheJudgedAssembly()
    {
        var certified = await CertifyDamageResolverAsync();
        var record = ProjectBTrustConsumer.FromInternalRecord(certified.Record);

        var trust = CertificationTrustVerifier.Verify(
            record,
            certified.SourceCode,
            certified.Artifact.AssemblyBytes,
            options: CertificationVerifyOptions.Strict);
        trust.Trusted.Should().BeTrue($"expected TRUSTED, got {trust.FailureCode}: {trust.Reason}");

        // Same source, same closed-world options, same reference set -- a second honest compile.
        var recompiled = GateEmittedArtifactCompiler.Compile(
            certified.SourceCode,
            BrickCertificationProjectLoader.DefaultCompilationReferences());

        recompiled.BrickTypeName.Should().Be(
            certified.Artifact.BrickTypeName,
            "the recompile is honest: same type, same source, same options");
        recompiled.AssemblySha256.Should().NotBe(
            certified.Artifact.AssemblySha256,
            "a recompile of verified source is NOT the judged artifact; this is why a host that "
            + "compiles the brick itself cannot be bound by the record's gate-emitted-artifact hash");
        ReadMvid(recompiled.AssemblyBytes).Should().NotBe(ReadMvid(certified.Artifact.AssemblyBytes));

        // And the record refuses those bytes, by hash, which is the point.
        var reverify = CertificationTrustVerifier.Verify(
            record,
            certified.SourceCode,
            recompiled.AssemblyBytes,
            options: CertificationVerifyOptions.Strict);
        reverify.Trusted.Should().BeFalse();
        reverify.FailureCode.Should().Be("artifact-hash-mismatch");
    }

    /// <summary>
    /// The tamper leg, asserted at BOTH doors: the verifier refuses the bytes, and the activator
    /// refuses to load them even if a caller skipped the verifier.
    /// </summary>
    [Fact]
    public async Task TamperedArtifactBytes_AreRefusedAtVerifyAndAtActivate()
    {
        var certified = await CertifyDamageResolverAsync();
        var record = ProjectBTrustConsumer.FromInternalRecord(certified.Record);

        var mutated = certified.Artifact.AssemblyBytes.ToArray();
        mutated[Math.Min(0x80, mutated.Length - 1)] ^= 0xFF;

        var trust = CertificationTrustVerifier.Verify(
            record,
            certified.SourceCode,
            mutated,
            options: CertificationVerifyOptions.Strict);
        trust.Trusted.Should().BeFalse();
        trust.FailureCode.Should().Be("artifact-hash-mismatch");

        var activate = () => CertifiedBrickActivator.Activate(
            certified.Artifact with { AssemblyBytes = mutated });
        activate.Should().Throw<InvalidOperationException>()
            .WithMessage("*hash does not match*");
    }

    private static Guid ReadMvid(byte[] assemblyBytes)
    {
        using var stream = new MemoryStream(assemblyBytes, writable: false);
        using var definition = AssemblyDefinition.ReadAssembly(stream);
        return definition.MainModule.Mvid;
    }

    private static async Task<CertifiedDamageResolver> CertifyDamageResolverAsync()
    {
        // Deliberately NO process-global environment write here, though every sibling certification
        // helper this was modelled on opens by clearing ASHLAR_CERT_NUGET_CONFIG. Such a write is
        // process-global, so restoring it afterwards does nothing about the class running beside
        // this one -- ProcessGlobalEnvironmentConventionTests says exactly that, and it caught this
        // file on its first cert-gate run. It is also unnecessary: nothing under src/ reads that
        // variable (only scripts that export it for a CHILD dotnet process do), run-cert-gate.sh
        // already unsets it before the lane runs, and the in-process certifier path used below
        // takes its reference set directly, as an argument.
        //
        // Note for whoever edits this comment: that convention test scans file TEXT and does not
        // exclude comments, which is why it spells the call as a split literal in its own source
        // and why this comment describes the call instead of naming it.
        var store = new InMemoryCertificationRecordStore();
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var admission = new CertifiedBrickAdmission(
            new CertificationGate(signer),
            new CertifiedBrickRegistry(store, signer));
        var generator = new NewBrickGenerator(new CursorGeneratorModel { Variant = "honest" });

        var witness = DamageResolverDogfoodWitness.Spec;
        var signature = Ashlar.Core.Application.Adaptation.WitnessSignatureBuilder.FromWitness(witness);
        var manifest = await generator.GenerateFromIntentAsync(DamageResolverIntent, signature).ConfigureAwait(false);
        var built = await GeneratedBrickBuilder.BuildAsync(manifest).ConfigureAwait(false);

        var decision = await admission.CertifyAndAdmitAsync(new CertificationRequest
        {
            Brick = built.Brick,
            Witness = witness,
            SourceCode = built.SourceCode,
            ProjectPath = built.ProjectPath,
            CompilationReferences = built.CompilationReferences,
            BrickTypeName = built.BrickTypeName,
            EmittedArtifact = built.EmittedArtifact
        }).ConfigureAwait(false);

        decision.Admitted.Should().BeTrue("the honest damage-resolver must certify");
        return new CertifiedDamageResolver(built.SourceCode, decision.Record, built.EmittedArtifact);
    }

    private sealed record CertifiedDamageResolver(
        string SourceCode,
        CertificationRecord Record,
        GateEmittedArtifact Artifact);

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
