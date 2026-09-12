using System.Reflection;
using Ashlar.Certification.Contracts;

namespace Ashlar.Samples.CertificationTrustConsumer;

/// <summary>
/// Behavioural conformance transcript for the PACKED <c>Ashlar.Certification.Contracts</c>.
///
/// <para>This sample exists because auditing the source is not auditing the product. Every
/// certification test in the repository runs against the source tree; a consumer runs against
/// the package. Those are different artifacts, and a release can make them disagree — a preset
/// that is fail-closed in <c>src/</c> and fail-open in the published <c>.nupkg</c> is invisible
/// to every source-level test in the repository.</para>
///
/// <para>So this project takes a <c>PackageReference</c>, never a <c>ProjectReference</c>, and
/// asserts the behaviour a consumer actually gets. It prints a transcript rather than relying on
/// its exit code: a harness that checks only the exit status is green for a consumer that never
/// ran at all.</para>
/// </summary>
internal static class Program
{
#if NET10_0_OR_GREATER
    private const string ExpectedLibFolder = "net10.0";
#else
    private const string ExpectedLibFolder = "net8.0";
#endif

    private const string BrickSource = "public sealed class TextSlugBrick { public string Run(string s) => s; }";

    private static int _run;
    private static int _failed;

    private static int Main()
    {
        var asm = typeof(CertificationVerifyOptions).Assembly;
        var location = (asm.Location ?? string.Empty).Replace('\\', '/');

        Console.WriteLine("=== Ashlar.Certification.Contracts packed-artifact conformance ===");
        Console.WriteLine($"CONSUMER_TFM={ExpectedLibFolder}");
        Console.WriteLine($"CONTRACTS_ASSEMBLY={location}");
        Console.WriteLine($"CONTRACTS_VERSION={asm.GetName().Version}");
        Console.WriteLine($"CONTRACTS_INFORMATIONAL_VERSION={asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "(none)"}");
        Console.WriteLine();

        // ---- Which asset did the consumer actually bind? -------------------------------------
        // A net6/net7 consumer silently binds the netstandard2.0 asset, where Ed25519 cannot be
        // evaluated at all. "The package is fail-closed" is only ever true of the asset that loaded.
        //
        // Assembly.Location is no good for this: the build copies the chosen asset into the app's
        // output folder, so every asset looks alike by the time the process is running. The deps
        // file records the path NuGet actually selected, which is the question being asked.
        var expectedAsset = "lib/" + ExpectedLibFolder + "/Ashlar.Certification.Contracts.dll";
        var depsPath = Path.Combine(AppContext.BaseDirectory, "CertificationTrustConsumer.deps.json");
        var deps = File.Exists(depsPath) ? File.ReadAllText(depsPath) : string.Empty;
        Console.WriteLine($"SELECTED_ASSET={(deps.Contains(expectedAsset, StringComparison.Ordinal) ? expectedAsset : "(not " + expectedAsset + ")")}");
        Check(
            "bound-asset-matches-consumer-tfm",
            deps.Contains(expectedAsset, StringComparison.Ordinal),
            $"deps.json at '{depsPath}' does not record '{expectedAsset}'; the consumer bound a different asset than its own target framework");

        // ---- Preset surface ------------------------------------------------------------------
        // Cheap, and these are the exact values a release can ship wrong while every source-level
        // test in the repository stays green.
        Check("default-requires-ed25519",
            CertificationVerifyOptions.Default.RequireEd25519Signature,
            "Default.RequireEd25519Signature is false in the packed artifact");

        Check("strict-requires-ed25519",
            CertificationVerifyOptions.Strict.RequireEd25519Signature,
            "Strict.RequireEd25519Signature is false in the packed artifact");

        Check("default-schema-floor-is-trust-loop",
            CertificationVerifyOptions.Default.MinimumSchemaVersion >= CertificationRecordData.TrustLoopSchemaVersion,
            $"Default.MinimumSchemaVersion is {CertificationVerifyOptions.Default.MinimumSchemaVersion}");

        Check("strict-schema-floor-is-trust-loop",
            CertificationVerifyOptions.Strict.MinimumSchemaVersion >= CertificationRecordData.TrustLoopSchemaVersion,
            $"Strict.MinimumSchemaVersion is {CertificationVerifyOptions.Strict.MinimumSchemaVersion}");

        Check("strict-requires-gate-emitted-artifact",
            CertificationVerifyOptions.Strict.RequireGateEmittedArtifact,
            "Strict.RequireGateEmittedArtifact is false");

        Check("strict-requires-certifier-identity",
            CertificationVerifyOptions.Strict.RequireCertifierIdentity,
            "Strict.RequireCertifierIdentity is false");


        Check("default-reports-itself-strict",
            CertificationVerifyOptions.Default.IsStrict,
            "Default.IsStrict is false");

        // Legacy is meant to be open. Asserting that it stays open is not pedantry: if the three
        // presets were ever swapped, every refusal assertion below would still pass while the
        // preset a consumer names in production had quietly become the permissive one.
        Check("legacy-reports-itself-not-strict",
            !CertificationVerifyOptions.Legacy.IsStrict,
            "Legacy.IsStrict is true — the preset identities may have been shuffled");

        // ---- The fallback must be self-reporting ---------------------------------------------
        // With no ASHLAR_CERT_DEV_HMAC_KEY set, the HMAC key resolves to a constant compiled into
        // the package. A consumer can only detect that if the package says so.
        Check("hmac-fallback-reports-dev-key",
            CertificationRecordSigning.UsesDevKey(),
            "UsesDevKey() is false with no ASHLAR_CERT_DEV_HMAC_KEY set — the fallback stopped self-reporting");

        // ---- Positive control ------------------------------------------------------------------
        // Every assertion below this line is a refusal. Without one case that is TRUSTED, a package
        // that refused absolutely everything would pass them all.
        var hmacOnlyV2 = SignWithDevKey(BuildRecord(CertificationRecordData.TrustLoopSchemaVersion));
        var legacyVerdict = CertificationTrustVerifier.Verify(
            hmacOnlyV2, BrickSource, options: CertificationVerifyOptions.Legacy);
        Check("positive-control-legacy-trusts-valid-hmac-record",
            legacyVerdict.Trusted,
            $"Legacy refused a well-formed HMAC record: {legacyVerdict.FailureCode} / {legacyVerdict.Reason}");

        // ---- The behaviour that matters --------------------------------------------------------
        // An HMAC-only record minted with the package's own committed default key. Anyone who can
        // read the published bytes can mint this. Both production presets must refuse it.
        var defaultVerdict = CertificationTrustVerifier.Verify(
            hmacOnlyV2, BrickSource, options: CertificationVerifyOptions.Default);
        Check("default-refuses-hmac-only-record",
            !defaultVerdict.Trusted && defaultVerdict.FailureCode == "ed25519-signature-required",
            $"Default returned trusted={defaultVerdict.Trusted} code={defaultVerdict.FailureCode ?? "(none)"}");

        var strictVerdict = CertificationTrustVerifier.Verify(
            hmacOnlyV2, BrickSource, options: CertificationVerifyOptions.Strict);
        Check("strict-refuses-hmac-only-record",
            !strictVerdict.Trusted && strictVerdict.FailureCode == "ed25519-signature-required",
            $"Strict returned trusted={strictVerdict.Trusted} code={strictVerdict.FailureCode ?? "(none)"}");

        // The same record with no options argument at all. A consumer that never names a preset
        // gets Default; if that overload ever bound Legacy, the two lines above would not catch it.
        var impliedVerdict = CertificationTrustVerifier.Verify(hmacOnlyV2, BrickSource);
        Check("omitted-options-refuses-hmac-only-record",
            !impliedVerdict.Trusted && impliedVerdict.FailureCode == "ed25519-signature-required",
            $"Verify() with no options returned trusted={impliedVerdict.Trusted} code={impliedVerdict.FailureCode ?? "(none)"}");

        // ---- Downgrade ---------------------------------------------------------------------------
        // The legacy payload omits Gate, GatesPassed, Inputs, Proposer, Attempts and the Ed25519
        // public key from the signed bytes, so a downgrade rewrites what the signature covers.
        var legacyRecord = SignWithDevKey(BuildRecord(null));
        var downgradeVerdict = CertificationTrustVerifier.Verify(
            legacyRecord, BrickSource, options: CertificationVerifyOptions.Default);
        Check("default-refuses-schema-downgrade",
            !downgradeVerdict.Trusted && downgradeVerdict.FailureCode == "schema-version-below-floor",
            $"Default returned trusted={downgradeVerdict.Trusted} code={downgradeVerdict.FailureCode ?? "(none)"}");

        // Deliberately NOT signed: an unknown version selects no payload lane, so there are no
        // bytes to sign, and Sign() refuses rather than inventing some. The verifier has to reach
        // that same conclusion from the version alone, before it looks at any signature — which is
        // what this asserts, under the most permissive preset so the floor cannot take the credit.
        var unknownRecord = BuildRecord(4242) with { Signature = "not-a-real-signature" };
        var unknownVerdict = CertificationTrustVerifier.Verify(
            unknownRecord, BrickSource, options: CertificationVerifyOptions.Legacy);
        Check("unknown-schema-version-refused",
            !unknownVerdict.Trusted && unknownVerdict.FailureCode == "schema-version-unknown",
            $"Legacy returned trusted={unknownVerdict.Trusted} code={unknownVerdict.FailureCode ?? "(none)"}");

        // ---- Content binding ----------------------------------------------------------------------
        // Proves the verifier reached the hash comparison rather than short-circuiting somewhere
        // earlier: same record, different source.
        var tamperedVerdict = CertificationTrustVerifier.Verify(
            hmacOnlyV2, BrickSource + " // patched", options: CertificationVerifyOptions.Legacy);
        Check("content-hash-mismatch-refused",
            !tamperedVerdict.Trusted && tamperedVerdict.FailureCode == "content-hash-mismatch",
            $"Legacy returned trusted={tamperedVerdict.Trusted} code={tamperedVerdict.FailureCode ?? "(none)"}");

        // A stripped HMAC signature must not be a pass under the most permissive preset either.
        var unsigned = hmacOnlyV2 with { Signature = null };
        var unsignedVerdict = CertificationTrustVerifier.Verify(
            unsigned, BrickSource, options: CertificationVerifyOptions.Legacy);
        Check("stripped-hmac-signature-refused",
            !unsignedVerdict.Trusted && unsignedVerdict.FailureCode == "signature-invalid",
            $"Legacy returned trusted={unsignedVerdict.Trusted} code={unsignedVerdict.FailureCode ?? "(none)"}");

        Console.WriteLine();
        Console.WriteLine($"ASSERTIONS_RUN={_run}");
        Console.WriteLine($"ASSERTIONS_FAILED={_failed}");
        Console.WriteLine(_failed == 0 ? "RESULT=OK" : "RESULT=FAIL");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail)
    {
        _run++;
        if (ok)
        {
            Console.WriteLine("PASS " + name);
        }
        else
        {
            _failed++;
            Console.WriteLine("FAIL " + name + ": " + detail);
        }
    }

    private static CertificationRecordData BuildRecord(int? schemaVersion) => new()
    {
        Status = "PASS",
        Stage = "certification",
        Admitted = true,
        Signed = true,
        Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        BrickId = "text-slug",
        ContentHash = BrickContentHasher.ComputeSha256(BrickSource),
        EscapeRate = 0,
        TotalMutants = 12,
        SurvivingMutants = 0,
        Gate = schemaVersion is null ? null : "cert-gate",
        SchemaVersion = schemaVersion,
        Inputs = schemaVersion is null
            ? Array.Empty<CertificationInput>()
            : new[]
            {
                new CertificationInput
                {
                    Kind = CertificationInputKinds.CertifierIdentity,
                    Id = "packed-artifact-conformance",
                    Hash = BrickContentHasher.ComputeSha256("certifier"),
                },
                new CertificationInput
                {
                    Kind = CertificationInputKinds.GateEmittedArtifact,
                    Id = "TextSlugBrick.dll",
                    Hash = BrickContentHasher.ComputeSha256("artifact"),
                },
            },
    };

    /// <summary>
    /// Signs with the key a consumer gets when no <c>ASHLAR_CERT_DEV_HMAC_KEY</c> is set — the
    /// constant compiled into the package. That is the point: this is the cheapest forgery
    /// available to anyone who can read the published bytes, and it is exactly what the production
    /// presets have to refuse.
    /// </summary>
    private static CertificationRecordData SignWithDevKey(CertificationRecordData record) =>
        record with { Signature = CertificationRecordSigning.Sign(record) };
}
