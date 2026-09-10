using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;

namespace Ashlar.Infrastructure.Certification.Composition;

/// <summary>
/// HMAC signer for composition certification records. Resolves its key from an explicit
/// parameter, then <c>ASHLAR_CERT_DEV_HMAC_KEY</c>, then the committed dev key. Warns when
/// the dev key is in effect. This fixes limitation 9 from certification-evidence.md by
/// honoring explicit keys.
/// </summary>
public sealed class CompositionCertificationRecordSigner
{
    private readonly byte[] _keyBytes;

    /// <summary>Initializes a new composition certification record signer.</summary>
    /// <param name="brickSigner">Unused; kept for API compatibility.</param>
    /// <param name="logger">Optional logger; receives the dev-key warning when the committed key is in effect.</param>
    /// <param name="hmacKey">
    /// Optional explicit HMAC key. When provided, composition records are signed with this key
    /// instead of reading from the environment. This allows hosts to pass a real key and have
    /// it honored (limitation 9 fix).
    /// </param>
    public CompositionCertificationRecordSigner(
        CertificationRecordSigner? brickSigner = null,
        ILogger<CompositionCertificationRecordSigner>? logger = null,
        string? hmacKey = null)
    {
        _ = brickSigner; // Kept for API compatibility but not used for key resolution
        
        var key = string.IsNullOrWhiteSpace(hmacKey)
            ? Environment.GetEnvironmentVariable(CertificationRecordSigning.HmacKeyEnvVar)
              ?? CertificationRecordSigner.DefaultDevKey
            : hmacKey;
        
        _keyBytes = Encoding.UTF8.GetBytes(key);
        UsesDevKey = CertificationRecordSigning.UsesDevKey(hmacKey);
        if (UsesDevKey)
            CertificationRecordSigner.WarnDevKey(logger, nameof(CompositionCertificationRecordSigner));
    }

    /// <summary>
    /// True when composition records are signed with the committed development key
    /// (no explicit key, <c>ASHLAR_CERT_DEV_HMAC_KEY</c> unset): every signature this instance
    /// mints or accepts is forgeable by anyone with the source.
    /// </summary>
    public bool UsesDevKey { get; }

    /// <summary>Sign.</summary>
    public string Sign(CompositionCertificationRecord record)
    {
        var payload = BuildPayload(record);
        using var hmac = new HMACSHA256(_keyBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    /// <summary>Verify.</summary>
    public bool Verify(CompositionCertificationRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Signature))
            return false;

        try
        {
            var expected = Sign(record with { Signature = null });
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(record.Signature),
                Convert.FromBase64String(expected));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Internal rather than private so CompositionCanonicalPayloadGoldenTests can pin these
    // bytes directly. They are the message every composition admission signature is computed
    // over, and a pin that could only observe them through a signature would report "the
    // signature moved" rather than which byte did.
    internal static string BuildPayload(CompositionCertificationRecord record)
    {
        var clone = new
        {
            record.Status,
            record.Stage,
            record.Admitted,
            record.Signed,
            Timestamp = record.Timestamp.UtcDateTime.ToString("O"),
            record.CompositionId,
            record.CompositionEscapeRate,
            record.TotalStructuralMutants,
            record.SurvivingStructuralMutants,
            KilledStructuralMutantIds = record.KilledStructuralMutantIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            SurvivingStructuralMutantIds = record.SurvivingStructuralMutantIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            record.Reason
        };
        return JsonSerializer.Serialize(clone, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}
