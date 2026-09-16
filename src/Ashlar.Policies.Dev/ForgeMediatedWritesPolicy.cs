using System.Text.Json;
using Ashlar.Abstractions;

namespace Ashlar.Policies.Dev;

/// <summary>
/// Routes source-tree writes through Forge change proposals when the daemon is
/// running in a low-trust aggressiveness mode (Passive, SemiActive). In those
/// modes a direct <c>repo.fs.write</c> / <c>repo.fs.search_replace</c> against
/// <c>src/</c> or <c>tests/</c> is rejected with a structured reason that tells
/// the agent to call <c>forge.propose_change</c> instead. In high-trust modes
/// (Active, Ambient) the policy is a no-op so the existing direct-write path
/// continues to work unchanged.
///
/// <para>Paths outside <c>src/</c> and <c>tests/</c> are not mediated by this policy — they
/// pass through it — which is NOT the same as being safe to write directly. This doc used to
/// say non-source paths bypass the pipeline "because they are human-curated / runtime-only";
/// <c>.ashlar/gates/*.json</c> is the counter-example: it is the admission ledger, neither
/// human-curated nor inert, and a cycle wrote its own record into it. Whether an unmediated
/// path may be written AT ALL is decided elsewhere: WHERE by <see cref="PathAllowlist"/>, and
/// WHAT by the governance floor in <c>ToolSandbox.TryResolveWritePath</c>, which refuses the
/// ledger and every build or tooling file whatever this policy says. <c>docs/</c> and
/// <c>application/</c> remain unmediated direct-write prefixes by design.</para>
///
/// <para>This policy is intentionally orthogonal to <see cref="PathAllowlist"/>:
/// PathAllowlist decides "can the agent write here at all?", this policy decides
/// "must the write be mediated by an operator review?".</para>
/// </summary>
public sealed class ForgeMediatedWritesPolicy : IPolicy
{
    private static readonly string[] MediatedPrefixes =
    {
        "src/",
        "tests/"
    };

    private readonly IAggressivenessModeStore? _modeStore;
    private readonly Func<BackgroundAgentAggressivenessMode>? _modeOverride;

    /// <summary>
    /// Constructs the policy from a runtime mode store. The store is read on
    /// every approval so an operator switching modes mid-flight takes effect
    /// before the next tool call, no daemon restart required.
    /// </summary>
    public ForgeMediatedWritesPolicy(IAggressivenessModeStore modeStore)
    {
        _modeStore = modeStore ?? throw new ArgumentNullException(nameof(modeStore));
    }

    /// <summary>Test/diagnostic constructor — supply a fixed or computed mode.</summary>
    public ForgeMediatedWritesPolicy(Func<BackgroundAgentAggressivenessMode> modeOverride)
    {
        _modeOverride = modeOverride ?? throw new ArgumentNullException(nameof(modeOverride));
    }

    public bool Approve(ToolCall call, WorldSnapshot s, out string reason)
    {
        reason = "OK";
        if (call.Id is not "repo.fs.write" and not "repo.fs.search_replace")
            return true;

        var mode = _modeOverride?.Invoke() ?? _modeStore!.GetMode();
        if (!IsLowTrust(mode)) return true;

        var path = ExtractPath(call);
        if (string.IsNullOrWhiteSpace(path)) return true; // PathAllowlist will reject empty paths.

        var rel = path.Replace('\\', '/').TrimStart('/');
        if (!MediatedPrefixes.Any(p => rel.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return true; // Non-source path — no mediation required.

        reason = $"Mode '{mode}' requires forge-mediated writes for '{rel}'. " +
                 "Call forge.propose_change instead and wait for an operator to approve before applying.";
        return false;
    }

    private static bool IsLowTrust(BackgroundAgentAggressivenessMode mode) =>
        mode is BackgroundAgentAggressivenessMode.Passive
             or BackgroundAgentAggressivenessMode.SemiActive;

    private static string ExtractPath(ToolCall call)
    {
        if (call.Arguments.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!call.Arguments.TryGetProperty("path", out var p)) return string.Empty;
        return p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
    }
}
