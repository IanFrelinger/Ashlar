using System.IO;
using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Paths;

namespace Ashlar.Policies.Dev;

/// <summary>
/// Development policy that bounds WHERE a cycle may write: a relative-prefix allowlist over
/// <c>repo.fs.write</c> and <c>repo.fs.search_replace</c>.
///
/// <para>Defaults: <c>src/</c>, <c>tests/</c>, <c>docs/</c>, <c>application/</c>. Widened by the
/// constructor's extra prefixes and by <c>ASHLAR_PATH_ALLOWLIST_EXTRA</c> — but never onto a
/// governance prefix. After merging, any prefix for which
/// <see cref="MediatedWritePath.IsAuthoringGovernancePath"/> is true is dropped and reported on
/// <see cref="RejectedExtras"/>, so the documented hardening variable is structurally unable to reach
/// <c>.ashlar/</c>, <c>scripts/</c> or a build import. <c>.ashlar/</c> was on the defaults until a
/// self-extend cycle wrote its own admission record through it; removing it alone closed nothing,
/// because the sandbox guide prescribed putting <c>.ashlar/</c> sub-prefixes back through the
/// variable.</para>
///
/// <para>This is the CONFIGURABLE half. The non-configurable half — WHAT may be written — is the
/// floor in <c>ToolSandbox.TryResolveWritePath</c> (<c>MediatedWritePath.RefuseAuthoringWrite</c>),
/// which every write tool applies whatever policy list a host composes, and
/// <c>GovernanceFloorPolicy</c>, which turns that refusal into a counted denial. Deliberately not
/// folded in here: this policy is sampled at hundreds of arbitrary suffixes by the property tests and
/// hammered fifty-wide by the concurrency tests, and the floor's reparse-point probes do
/// filesystem I/O.</para>
///
/// <para>Absolute paths are admitted only inside <c>SandboxRoot</c> (snapshot) or
/// <c>ASHLAR_SANDBOX_ROOT</c>. Implements IPolicy for use with PolicyEngine.</para>
/// </summary>
public sealed class PathAllowlist : IPolicy
{
    private static readonly string[] DefaultAllowed =
    {
        "src/",
        "tests/",
        "docs/",
        "application/",
    };

    private readonly string[] _allowedPrefixes;

    public PathAllowlist(IEnumerable<string>? extraAllowedPrefixes = null)
    {
        var merged = new List<string>(DefaultAllowed);

        if (extraAllowedPrefixes is not null)
            merged.AddRange(extraAllowedPrefixes);

        var fromEnv = Environment.GetEnvironmentVariable("ASHLAR_PATH_ALLOWLIST_EXTRA");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            foreach (var entry in fromEnv.Split(',', ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                merged.Add(entry);
        }

        var (allowed, rejected) = PartitionGovernance(Normalize(merged));
        _allowedPrefixes = allowed;
        RejectedExtras = rejected;
    }

    private PathAllowlist(string[] normalizedExactPrefixes)
    {
        _allowedPrefixes = normalizedExactPrefixes;
        RejectedExtras = Array.Empty<string>();
    }

    /// <summary>
    /// The prefixes handed to the constructor or to <c>ASHLAR_PATH_ALLOWLIST_EXTRA</c> that were
    /// dropped because they are authoring-governance paths, in normalized form. Exposed so a host
    /// can log what its configuration asked for and did not get; a silently narrowed allowlist
    /// would look like a misconfigured one.
    /// </summary>
    public IReadOnlyList<string> RejectedExtras { get; }

    /// <summary>
    /// Splits normalized prefixes into the allowed set and the governance set. A prefix is judged
    /// as the directory it names (<c>.ashlar/</c> → <c>.ashlar</c>), which is what the floor sees
    /// as the first segment of any write beneath it.
    /// </summary>
    private static (string[] Allowed, string[] Rejected) PartitionGovernance(string[] normalized)
    {
        var allowed = new List<string>(normalized.Length);
        var rejected = new List<string>();
        foreach (var prefix in normalized)
        {
            if (MediatedWritePath.IsAuthoringGovernancePath(prefix.TrimEnd('/')))
                rejected.Add(prefix);
            else
                allowed.Add(prefix);
        }
        return (allowed.ToArray(), rejected.ToArray());
    }

    /// <summary>
    /// An allowlist of EXACTLY the given prefixes — no built-in defaults and no
    /// <c>ASHLAR_PATH_ALLOWLIST_EXTRA</c> widening. This is the confinement-declaration mode
    /// (extension spec Part B): when a <c>ProposerConfinement</c> derives the write policy,
    /// that declaration is the single source, and an environment variable silently widening
    /// it would reopen exactly the drift the declaration exists to close.
    /// </summary>
    public static PathAllowlist FromExactPrefixes(IEnumerable<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        var normalized = Normalize(prefixes.ToList());
        if (normalized.Length == 0)
            throw new ArgumentException("An exact allowlist with zero valid prefixes would deny every write.", nameof(prefixes));
        return new PathAllowlist(normalized);
    }

    private static string[] Normalize(List<string> prefixes) => prefixes
        .Select(NormalizePrefix)
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray()!;

    public bool Approve(ToolCall call, WorldSnapshot s, out string reason)
    {
        reason = "OK";
        if (call.Id is "repo.fs.write" or "repo.fs.search_replace")
        {
            if (call.Arguments.ValueKind == JsonValueKind.Object &&
                call.Arguments.TryGetProperty("path", out var p))
            {
                var raw = (p.ValueKind switch
                {
                    JsonValueKind.Null => "",
                    JsonValueKind.String => p.GetString() ?? "",
                    _ => ""
                }).Replace('\\', '/');
                var rel = raw.TrimStart('/');
                if (string.IsNullOrEmpty(rel))
                {
                    reason = "Path not allowed: empty or null path";
                    return false;
                }
                if (Path.IsPathRooted(raw) || raw.StartsWith("/", StringComparison.Ordinal))
                {
                    var sandboxRoot = ResolveSandboxRoot(s);
                    if (string.IsNullOrWhiteSpace(sandboxRoot))
                    {
                        reason = $"Path not allowed: absolute path not permitted: {raw}";
                        return false;
                    }

                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(raw);
                    }
                    catch
                    {
                        reason = $"Path not allowed: invalid absolute path: {raw}";
                        return false;
                    }

                    if (!IsPathWithinRoot(fullPath, sandboxRoot))
                    {
                        reason = $"Path not allowed: outside SandboxRoot: {fullPath}";
                        return false;
                    }

                    return true;
                }
                if (rel.Contains("..", StringComparison.Ordinal))
                {
                    reason = $"Path not allowed: path traversal not permitted: {rel}";
                    return false;
                }
                if (!_allowedPrefixes.Any(a => rel.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
                {
                    reason = $"Path not allowed: {rel}";
                    return false;
                }
            }
        }
        return true;
    }

    private static string? NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return null;

        var normalized = prefix.Replace('\\', '/').Trim();
        normalized = normalized.TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (normalized.Contains("..", StringComparison.Ordinal))
            return null;

        if (!normalized.EndsWith("/", StringComparison.Ordinal))
            normalized += "/";

        return normalized;
    }

    private static string? ResolveSandboxRoot(WorldSnapshot snapshot)
    {
        string? root = null;
        if (snapshot.Data.TryGetValue("SandboxRoot", out var rootObj) && rootObj is string fromSnapshot)
            root = fromSnapshot;

        if (string.IsNullOrWhiteSpace(root))
            root = Environment.GetEnvironmentVariable("ASHLAR_SANDBOX_ROOT");

        if (string.IsNullOrWhiteSpace(root))
            return null;

        try
        {
            return Path.GetFullPath(root);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPathWithinRoot(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = candidate;

        if (normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
