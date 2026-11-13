using System.Text.Json;
using System.Text.Json.Serialization;
using Ashlar.Core.Application.Paths;
using Ashlar.Manifest.Signing;

namespace Ashlar.CLI.Commands.BackgroundAgent;

/// <summary>
/// The node's status document: one small JSON file, rewritten in place, that answers "what is this
/// node doing?" without attaching to it.
///
/// <para><b>Why a document and not a log.</b> Appending a line per cycle would add roughly 86,000
/// small writes a day at ScheduleExecutor's one-second tick — on an SD card, weeks before anything
/// bounds the seven appenders that already exist. This is rewritten on a fixed timer instead, so
/// its cost is independent of how fast the node works.</para>
///
/// <para><b>Why it matters more than a log line.</b> <c>restart: unless-stopped</c> does not act on
/// HEALTHCHECK — Docker restarts on EXIT only. A node that parks, or wedges, stays up forever and
/// looks exactly like a healthy one from the outside. This file plus the image's HEALTHCHECK is
/// what makes the difference visible from <c>docker ps</c> after three weeks away.</para>
/// </summary>
public sealed record NodeHeartbeat
{
    /// <summary>File name under the state directory.</summary>
    public const string FileName = "heartbeat.json";

    /// <summary><c>running</c> or <c>parked</c>. Anything else is a bug in the writer.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Why the node is parked, in words an operator can act on. Null while running.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>When this document was last rewritten. Staleness is itself a symptom.</summary>
    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Node identity. Today this is the operator key fingerprint; there is no separate node id yet.</summary>
    [JsonPropertyName("nodeId")]
    public string? NodeId { get; init; }

    /// <summary>Configured public identity, or null when unavailable. This does not prove private-key possession, signing readiness or authenticated peer identity.</summary>
    [JsonPropertyName("keyFingerprint")]
    public string? KeyFingerprint { get; init; }

    /// <summary>
    /// Total agent executions since this process started. Zero after the start-up grace period
    /// means the agent set did not load — the condition that made every earlier soak chart a flat
    /// line by construction.
    /// </summary>
    [JsonPropertyName("cyclesSinceStart")]
    public int CyclesSinceStart { get; init; }

    /// <summary>Most recent agent completion, or null if none has completed yet.</summary>
    [JsonPropertyName("lastAdmissionAt")]
    public DateTimeOffset? LastAdmissionAt { get; init; }

    /// <summary>
    /// Digest of the set of keys this node trusts. NULL, deliberately: there is no trust root yet
    /// (`ashlar keys trust` is Phase 3). The field exists so the schema does not change when the
    /// trust root lands, and null is honest — a fabricated digest would be acted on by anything
    /// comparing nodes.
    /// </summary>
    [JsonPropertyName("trustSetDigest")]
    public string? TrustSetDigest { get; init; }

    /// <summary>
    /// Whether the clock was verified plausible at start-up. NULL until Phase 1 step 3's clock
    /// wait lands. Same reasoning as <see cref="TrustSetDigest"/>: absent, not invented.
    /// </summary>
    [JsonPropertyName("clockSynced")]
    public bool? ClockSynced { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Resolves the heartbeat path under the state directory (ASHLAR_STATE_DIR when set).</summary>
    public static string ResolvePath() => Path.Combine(RepoPathResolver.ResolveStateDirectory(), FileName);

    /// <summary>
    /// Rewrites the document in place. Best-effort by design: a node must never fail because it
    /// could not describe itself, and the HEALTHCHECK already treats a stale or missing file as
    /// unhealthy — so a write that silently fails still surfaces, one interval later.
    /// </summary>
    public void Write()
    {
        try
        {
            var path = ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write-then-move so a reader (the HEALTHCHECK, every interval) never sees a half file.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately swallowed. If the state directory is unwritable the daemon has already
            // parked on that exact condition with a louder message than this could produce.
        }
    }

    /// <summary>Reads public identity metadata only; unavailable or malformed public material yields null.</summary>
    public static string? TryFingerprint()
    {
        try
        {
            return OperatorKey.TryReadPublicIdentity()?.Fingerprint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            // Metadata is unknown, never a substitute signing identity or trust decision.
            // Private-key health remains the responsibility of explicit signing/diagnostic paths.
            return null;
        }
    }
}
