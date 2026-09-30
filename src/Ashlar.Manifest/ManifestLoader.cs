using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ashlar.Manifest;

/// <summary>
/// Loads <c>ashlar.yaml</c> — the project contract agents may propose changes to.
///
/// <para>The load rule that matters is <see cref="PolicyOwnedKeys"/>: a manifest naming a
/// sandbox root, a self-extension mode, or a never-list is REJECTED rather than having those
/// keys quietly ignored. Silently dropping them would let a proposed manifest edit look like
/// it widened the envelope and appear to succeed; the author — human or agent — must be told
/// plainly that the envelope is not theirs to set.</para>
/// </summary>
public static class ManifestLoader
{
    /// <summary>The only accepted schema version.</summary>
    public const string ExpectedApiVersion = "ashlar/v1";

    /// <summary>The only accepted document kind.</summary>
    public const string ExpectedKind = "Application";

    /// <summary>
    /// Top-level keys that belong to the operator-owned policy and are therefore illegal in a
    /// project manifest. Their presence is an error, not a no-op.
    /// </summary>
    public static readonly IReadOnlyList<string> PolicyOwnedKeys =
    [
        "sandbox",
        "selfExtend",
        "self_extend",   // rejected under either spelling, so a near-miss is not a silent pass
        "never",
        "policy",
    ];

    /// <summary>
    /// NEW deserializers for every parse, for the reason given on
    /// <c>PolicyLoader.NewDeserializer</c>: a YamlDotNet 13.7.1 deserializer is not safe to share
    /// across threads during its first parses, and a shared one made this loader reject valid
    /// manifests as "could not be parsed" (seen in <c>ProjectVerifierTests</c>, run 34062198636).
    /// <c>YamlDeserializerNotSharedConventionTests</c> fails if any static field in this assembly
    /// holds a YamlDotNet type again.
    /// </summary>
    private static IDeserializer NewDeserializer() => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static IDeserializer NewRawDeserializer() => new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses and validates a project manifest.
    /// </summary>
    /// <param name="yaml">Raw YAML.</param>
    /// <param name="manifest">The validated manifest, when this returns true.</param>
    /// <param name="reason">Why the manifest was rejected, when this returns false.</param>
    /// <returns>True only if the document parses and declares nothing policy-owned.</returns>
    public static bool TryLoad(string? yaml, out AshlarManifest? manifest, out string reason)
    {
        manifest = null;

        if (string.IsNullOrWhiteSpace(yaml))
        {
            reason = "REJECTED: manifest document is empty.";
            return false;
        }

        if (!YamlGuard.Check(yaml!, "manifest", out var guardReason))
        {
            reason = guardReason;
            return false;
        }

        // Read the top-level keys first, so a policy-owned key gets a precise explanation
        // rather than a generic schema error.
        Dictionary<string, object>? raw;
        try
        {
            raw = NewRawDeserializer().Deserialize<Dictionary<string, object>>(yaml!);
        }
        catch (YamlException ex)
        {
            reason = $"REJECTED: manifest could not be parsed: {ex.Message}";
            return false;
        }

        if (raw is not null)
        {
            var offending = raw.Keys
                .Where(k => PolicyOwnedKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (offending.Count > 0)
            {
                reason = "REJECTED: manifest declares policy-owned keys: "
                       + string.Join(", ", offending)
                       + ". The envelope lives in ashlar.policy.yaml, which the application cannot set. "
                       + "Move these there.";
                return false;
            }
        }

        AshlarManifest? parsed;
        try
        {
            parsed = NewDeserializer().Deserialize<AshlarManifest>(yaml!);
        }
        catch (YamlException ex)
        {
            reason = $"REJECTED: manifest could not be parsed: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            reason = "REJECTED: manifest document contained no content.";
            return false;
        }

        if (!string.Equals(parsed.ApiVersion, ExpectedApiVersion, StringComparison.Ordinal))
        {
            reason = $"REJECTED: unsupported manifest apiVersion '{parsed.ApiVersion}'; expected '{ExpectedApiVersion}'.";
            return false;
        }

        if (!string.Equals(parsed.Kind, ExpectedKind, StringComparison.Ordinal))
        {
            reason = $"REJECTED: manifest kind must be '{ExpectedKind}', not '{parsed.Kind}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Metadata.Name))
        {
            reason = "REJECTED: manifest must declare metadata.name.";
            return false;
        }

        var duplicateAgents = parsed.Agents
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateAgents.Count > 0)
        {
            reason = "REJECTED: duplicate agent ids: " + string.Join(", ", duplicateAgents) + ".";
            return false;
        }

        manifest = parsed;
        reason = string.Empty;
        return true;
    }
}
