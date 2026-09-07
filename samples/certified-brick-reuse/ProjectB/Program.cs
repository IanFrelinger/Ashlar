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
    private const string Usage =
        "Usage: ProjectB <path-to-DamageResolverBrick.cs> <path-to-certification-record.json>";

    public static async Task<int> Main(string[] args)
    {
        // Argument parsing is explicit and positional. The two files are not interchangeable: the
        // source is hashed, the record is parsed as JSON. Handing them over in the wrong order used to
        // surface as an unhandled JsonException (exit 134) instead of a usage line.
        if (args.Length != 2)
        {
            return UsageError($"expected 2 arguments, got {args.Length}.");
        }

        var brickSourcePath = args[0];
        var recordPath = args[1];

        if (!brickSourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || !recordPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return UsageError(
                $"the first argument must be the brick source (.cs) and the second the certification record (.json); got '{brickSourcePath}' and '{recordPath}'.");
        }

        if (!File.Exists(brickSourcePath))
        {
            return UsageError($"brick source not found: '{brickSourcePath}'.");
        }

        if (!File.Exists(recordPath))
        {
            return UsageError($"certification record not found: '{recordPath}'.");
        }

        var source = await File.ReadAllTextAsync(brickSourcePath).ConfigureAwait(false);
        var recordJson = await File.ReadAllTextAsync(recordPath).ConfigureAwait(false);

        CertificationRecordData? record;
        try
        {
            record = JsonSerializer.Deserialize<CertificationRecordData>(
                recordJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return UsageError($"'{recordPath}' is not a certification record: {ex.Message}");
        }

        if (record is null)
        {
            return UsageError($"'{recordPath}' is not a certification record (JSON null).");
        }

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

    private static int UsageError(string detail)
    {
        Console.Error.WriteLine($"ProjectB: {detail}");
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
