using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification;
using Ashlar.Core.Application.Certification.Ports;

namespace Ashlar.BackgroundAgents.Security;

/// <summary>
/// Fail-closed certification gate for brick writes on the self-extend admission edge.
/// Applies only when <c>WorldSnapshot.Data["selfExtendAdmission"]</c> is true.
/// </summary>
public sealed class SelfProducedBrickCertificationPolicy : IPolicy
{
    private readonly ICertificationRecordStore _certificationStore;

    public SelfProducedBrickCertificationPolicy(ICertificationRecordStore certificationStore)
    {
        _certificationStore = certificationStore ?? throw new ArgumentNullException(nameof(certificationStore));
    }

    public bool Approve(ToolCall call, WorldSnapshot s, out string reason)
    {
        reason = "OK";
        if (!IsSelfExtendAdmission(s))
            return true;

        if (call.Id is not ("repo.fs.write" or "repo.fs.search_replace"))
            return true;

        if (!TryGetPathAndContent(call, out var relativePath, out var content))
            return true;

        if (!BrickAdmissionPathHelper.IsRunnableBrickPath(relativePath))
            return true;

        var brickId = BrickAdmissionPathHelper.InferBrickId(relativePath, content);
        if (string.IsNullOrWhiteSpace(brickId))
        {
            reason = "Self-produced brick admission refused: unable to infer brick id";
            return false;
        }

        var record = _certificationStore.Get(brickId);
        if (record is null)
        {
            reason = $"Self-produced brick admission refused: no certification record for '{brickId}'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            reason = $"Self-produced brick admission refused: missing source content for '{brickId}'";
            return false;
        }

        var trust = CertificationTrustVerifier.Verify(
            CertificationRecordMapper.ToData(record),
            content,
            options: CertificationVerifyOptions.Strict);
        if (!trust.Trusted)
        {
            reason = $"Self-produced brick admission refused for '{brickId}': {trust.FailureCode} — {trust.Reason}";
            return false;
        }

        return true;
    }

    private static bool IsSelfExtendAdmission(WorldSnapshot snapshot) =>
        snapshot.Data.TryGetValue("selfExtendAdmission", out var flag)
        && flag is true;

    private static bool TryGetPathAndContent(ToolCall call, out string relativePath, out string? content)
    {
        relativePath = string.Empty;
        content = null;
        if (call.Arguments.ValueKind != JsonValueKind.Object)
            return false;

        if (!call.Arguments.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
            return false;

        relativePath = pathEl.GetString() ?? string.Empty;
        if (call.Arguments.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            content = contentEl.GetString();
        else if (call.Arguments.TryGetProperty("newContent", out var newContentEl) && newContentEl.ValueKind == JsonValueKind.String)
            content = newContentEl.GetString();

        return !string.IsNullOrWhiteSpace(relativePath);
    }
}
