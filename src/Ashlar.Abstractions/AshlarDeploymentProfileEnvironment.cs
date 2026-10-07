/// <summary>
/// Shared parser for <c>ASHLAR_DEPLOYMENT_PROFILE</c> and the profile
/// hosting actually resolved. Lives in Abstractions so hosting and protocol
/// assemblies share one process-wide resolved value.
/// </summary>
internal static class AshlarDeploymentProfileEnvironment
{
    private static readonly object Gate = new();

    internal static string? ResolvedRaw { get; private set; }

    /// <summary>
    /// Notes the profile <c>AddAshlar</c> resolved. The strictest profile noted in the process wins (SPEC-007 PR 4,
    /// default D5): once AirGapped has been noted nothing replaces it, and once SecureWorkstation has been noted only
    /// AirGapped does. Among the other profiles the last one noted wins, as before.
    /// </summary>
    internal static void NoteResolved(string canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical))
        {
            throw new ArgumentException("Resolved profile must be non-blank.", nameof(canonical));
        }

        lock (Gate)
        {
            if (Strictness(canonical) >= Strictness(ResolvedRaw))
            {
                ResolvedRaw = canonical;
            }
        }
    }

    internal static void ClearResolved() => RestoreResolved(null);

    /// <summary>Sets the noted profile as given, bypassing the strictest-wins rule. For the reset seam only.</summary>
    internal static void RestoreResolved(string? raw)
    {
        lock (Gate)
        {
            ResolvedRaw = raw;
        }
    }

    private static int Strictness(string? raw) =>
        IsAirGapped(raw) ? 2 : IsSecureWorkstation(raw) ? 1 : 0;

    internal static string? Effective(string? raw) =>
        string.IsNullOrEmpty(ResolvedRaw) ? raw : ResolvedRaw;

    internal static bool TryNormalize(string? raw, out string folded)
    {
        folded = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw!.Trim();
#if NETSTANDARD2_0
        folded = text.ToUpperInvariant().Replace("-", string.Empty).Replace("_", string.Empty);
#else
        folded = text.ToUpperInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
#endif
        return true;
    }

    internal static bool IsAirGapped(string? raw) =>
        TryNormalize(raw, out var folded) && folded is "AIRGAPPED";

    internal static bool IsSecureWorkstation(string? raw) =>
        TryNormalize(raw, out var folded) && folded is "SECUREWORKSTATION" or "WORKSTATION";

    /// <summary>
    /// Profiles that must not dial remote MCP/A2A or expose an A2A server.
    /// Local MCP server (IDE stdio) stays allowed on SecureWorkstation.
    /// Prefers the profile <see cref="NoteResolved"/> recorded when
    /// <c>AddAshlar</c> ran in this process.
    /// </summary>
    internal static bool ForbidsRemoteProtocolEgress(string? raw)
    {
        var effective = Effective(raw);
        return IsAirGapped(effective) || IsSecureWorkstation(effective);
    }

    internal static bool TryParseKnown(string? raw, out string canonical)
    {
        canonical = "full";
        if (!TryNormalize(raw, out var folded))
        {
            return false;
        }

        canonical = folded switch
        {
            "FULL" => "full",
            "SERVER" => "server",
            "EDGE" => "edge",
            "AIRGAPPED" => "air-gapped",
            "SYSTEM" or "CORE" => "system",
            "SECUREWORKSTATION" or "WORKSTATION" => "secure-workstation",
            _ => folded
        };

        return folded is "FULL" or "SERVER" or "EDGE" or "AIRGAPPED"
            or "SYSTEM" or "CORE"
            or "SECUREWORKSTATION" or "WORKSTATION";
    }

    internal static string DisplayName(string? raw)
    {
        var effective = Effective(raw);
        if (IsSecureWorkstation(effective))
        {
            return "SecureWorkstation";
        }

        if (IsAirGapped(effective))
        {
            return "AirGapped";
        }

        return effective ?? string.Empty;
    }
}
