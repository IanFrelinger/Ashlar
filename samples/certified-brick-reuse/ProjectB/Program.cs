using System.Reflection;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Certified.DamageResolver;

namespace CertifiedBrickReuse.ProjectB;

/// <summary>Program.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var brickSourcePath = args.ElementAtOrDefault(0)
            /// <summary>Invalid operation exception.</summary>
            /// <param name="<path-to-certification-record.json>""><path-to-certification-record.json>".</param>
            ?? throw new InvalidOperationException("Usage: ProjectB <path-to-DamageResolverBrick.cs> <path-to-certification-record.json>");
        var recordPath = args.ElementAtOrDefault(1)
            /// <summary>Invalid operation exception.</summary>
            /// <param name="<path-to-certification-record.json>""><path-to-certification-record.json>".</param>
            ?? throw new InvalidOperationException("Usage: ProjectB <path-to-DamageResolverBrick.cs> <path-to-certification-record.json>");

        var source = await File.ReadAllTextAsync(brickSourcePath).ConfigureAwait(false);
        var recordJson = await File.ReadAllTextAsync(recordPath).ConfigureAwait(false);
        var record = JsonSerializer.Deserialize<CertificationRecordData>(
            recordJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            /// <summary>Invalid operation exception.</summary>
            /// <param name="JSON."">Json.".</param>
            ?? throw new InvalidOperationException("Invalid certification record JSON.");

        var artifactPath = args.ElementAtOrDefault(2);
        CertificationTrustResult trust;
        if (!string.IsNullOrWhiteSpace(artifactPath))
        {
            var artifactBytes = await File.ReadAllBytesAsync(artifactPath).ConfigureAwait(false);
            trust = CertificationTrustVerifier.Verify(
                record,
                source,
                artifactBytes,
                options: CertificationVerifyOptions.Strict);
        }
        else
        {
            trust = CertificationTrustVerifier.Verify(
                record,
                source,
                options: CertificationVerifyOptions.Strict);
        }
        if (!trust.Trusted)
        {
            Console.Error.WriteLine($"UNTRUSTED: {trust.FailureCode} — {trust.Reason}");
            return 2;
        }

        // SCOPE OF THIS SAMPLE: it demonstrates RECORD VERIFICATION, not judged-equals-executed.
        //
        // The type below is bound at compile time from the Ashlar.Certified.DamageResolver
        // PackageReference, so it is somebody else's compile of the brick -- NOT the artifact bytes
        // verified above, which are hashed and then dropped. Two consequences worth being explicit
        // about, since this file has been read as a pattern to copy:
        //
        //  * When `artifactPath` is omitted, the source-only overload runs and no artifact is bound
        //    at all; `RequireGateEmittedArtifact` under Strict is then only a presence check on the
        //    record's input list, not a hash comparison.
        //  * Even when it is supplied, verifying the bytes does not make them the running program.
        //
        // To bind the certificate to what executes, a consumer loads the verified in-memory bytes
        // (AssemblyLoadContext.Default.LoadFromStream, or CertifiedBrickActivator.Activate) and
        // resolves the type the record NAMES, and carries no compile-time reference to the brick.
        // That composition is measured in
        // src/Ashlar.Tests.Infrastructure/Tests/Certification/JudgedArtifactIsTheExecutedArtifactTests.cs
        // and written up in consumer-template/CONSUMING.md; see also docs/HowGatesGoQuiet.md
        // section 16.
        var brick = new DamageResolverBrick();
        var output = await brick.ExecuteAsync(
            new BrickInput(new Dictionary<string, object>
            {
                ["baseDamage"] = 50,
                ["critMultiplierPercent"] = 100,
                ["armor"] = 10,
                ["isCrit"] = false
            }),
            ImplementationType.Deterministic,
            new ProjectBExecutionContext()).ConfigureAwait(false);

        Console.WriteLine($"TRUSTED finalDamage={output.Get<int>("finalDamage")}");
        return 0;
    }
}
