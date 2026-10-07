using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ashlar.Manifest;

/// <summary>
/// Loads <c>ashlar.policy.yaml</c> — the operator-owned envelope.
///
/// <para>Every rule here FAILS CLOSED. A policy that cannot be fully understood is a
/// rejection, never a permissive default: the whole point of this document is to be the
/// thing that constrains a system which may otherwise rewrite itself, so "I could not parse
/// the constraints" must never resolve to "then there are none".</para>
/// </summary>
public static class PolicyLoader
{
    /// <summary>The only accepted schema version.</summary>
    public const string ExpectedApiVersion = "ashlar/v1";

    /// <summary>The only accepted document kind.</summary>
    public const string ExpectedKind = "Policy";

    /// <summary>
    /// Prohibitions that are COMPILED IN rather than configured. A policy file must declare
    /// all of them; one that omits an entry fails to load. They are listed in the file even
    /// though they are mandatory so that anyone reading a policy sees the whole envelope in
    /// one place, without having to know what the loader adds behind their back.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredNeverEntries =
    [
        "modify_gate",          // a system that can rewrite its own admission controller has none
        "widen_sandbox",        // the root comes from the host, never from the confined thing
        "access_signing_keys",  // a certification it can forge certifies nothing
        "truncate_ledger",      // the audit trail is not a capability the application holds
        "grant_capability",     // adding a brick is in scope; granting itself the network is not
    ];

    /// <summary>
    /// Kinds an application may ever add to itself. A brick adds capability inside the
    /// existing envelope; a tool or capability widens it.
    /// </summary>
    public static readonly IReadOnlyList<string> AddableKinds = ["brick"];

    /// <summary>
    /// A NEW deserializer for every parse. Never make this a shared static again.
    ///
    /// <para>YamlDotNet 13.7.1 (Directory.Packages.props) is not safe to share across threads.
    /// Each deserializer owns one <c>DefaultObjectFactory</c>, which caches every type's
    /// <c>[OnDeserializing]</c>/<c>[OnDeserialized]</c> lookup in a plain <c>Dictionary</c> and
    /// writes it, unlocked, the first time it meets that type. When this was one static, two
    /// threads doing their first parse of a process at the same moment wrote it together; the
    /// corrupted insert threw, YamlDotNet wrapped that as "Exception during deserialization", and
    /// this loader rejected a VALID policy as one that "could not be parsed". The self-extend gate
    /// then failed closed on it. That was the intermittent red of
    /// <c>SelfExtendAdmissionBridgeTests</c> and <c>ManifestContractTests</c> in the readiness
    /// gate, and any process parsing two policies at once for the first time could hit it.</para>
    ///
    /// <para>Caching the <see cref="DeserializerBuilder"/> instead would NOT help: a builder hands
    /// the same lazily created object factory to every <c>Build()</c>. Upstream made the cache
    /// concurrent in YamlDotNet 16.1.0. A policy is parsed once per command or self-extend cycle, so a
    /// fresh deserializer costs nothing that matters, and with nothing shared there is nothing to
    /// race on. <c>YamlDeserializerNotSharedConventionTests</c> fails if any static field in this
    /// assembly holds a YamlDotNet type, which covers every caching shape. The race itself is
    /// reproduced by <c>PolicyLoaderColdStartConcurrencyTests</c>, which catches an eagerly shared
    /// deserializer or builder but not a lazily assigned one.</para>
    /// </summary>
    private static IDeserializer NewDeserializer() => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Parses and validates a policy document.
    /// </summary>
    /// <param name="yaml">Raw YAML.</param>
    /// <param name="policy">The validated policy, when this returns true.</param>
    /// <param name="reason">Why the policy was rejected, when this returns false.</param>
    /// <returns>True only if the document is complete and every rule holds.</returns>
    public static bool TryLoad(string? yaml, out AshlarPolicy? policy, out string reason)
    {
        policy = null;

        if (string.IsNullOrWhiteSpace(yaml))
        {
            reason = "REJECTED: policy document is empty. An absent envelope is not an open one.";
            return false;
        }

        if (!YamlGuard.Check(yaml!, "policy", out var guardReason))
        {
            reason = guardReason;
            return false;
        }

        AshlarPolicy? parsed;
        try
        {
            parsed = NewDeserializer().Deserialize<AshlarPolicy>(yaml!);
        }
        catch (YamlException ex)
        {
            reason = $"REJECTED: policy could not be parsed: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            reason = "REJECTED: policy document contained no content.";
            return false;
        }

        if (!string.Equals(parsed.ApiVersion, ExpectedApiVersion, StringComparison.Ordinal))
        {
            reason = $"REJECTED: unsupported policy apiVersion '{parsed.ApiVersion}'; expected '{ExpectedApiVersion}'.";
            return false;
        }

        if (!string.Equals(parsed.Kind, ExpectedKind, StringComparison.Ordinal))
        {
            reason = $"REJECTED: policy kind must be '{ExpectedKind}', not '{parsed.Kind}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Sandbox.Root))
        {
            reason = "REJECTED: policy must declare sandbox.root. A sandbox without a root is not a sandbox.";
            return false;
        }

        // The never-list is mandatory and complete, or the gate does not come up.
        var declared = new HashSet<string>(parsed.Never, StringComparer.Ordinal);
        var missing = RequiredNeverEntries.Where(e => !declared.Contains(e)).ToList();
        if (missing.Count > 0)
        {
            reason = "REJECTED: policy omits mandatory never-list entries: "
                   + string.Join(", ", missing)
                   + ". These are not configurable; a policy that does not declare them fails to load "
                   + "rather than producing a permissive gate.";
            return false;
        }

        if (!SelfExtendMode.All.Contains(parsed.SelfExtend.Mode, StringComparer.Ordinal))
        {
            reason = $"REJECTED: unknown selfExtend.mode '{parsed.SelfExtend.Mode}'; expected one of "
                   + string.Join(", ", SelfExtendMode.All) + ".";
            return false;
        }

        // Only bricks are self-addable. Anything else widens the envelope.
        var illegal = parsed.SelfExtend.MayAdd
            .Where(k => !AddableKinds.Contains(k, StringComparer.Ordinal))
            .ToList();
        if (illegal.Count > 0)
        {
            reason = "REJECTED: selfExtend.mayAdd contains kinds that would widen the envelope: "
                   + string.Join(", ", illegal)
                   + ". Only " + string.Join(", ", AddableKinds) + " may be added by the application itself.";
            return false;
        }

        // A mode that can admit anything must say what it admits it against.
        var admits = parsed.SelfExtend.Mode is SelfExtendMode.Proposing or SelfExtendMode.SelfExtending;
        if (admits && parsed.SelfExtend.GatesRequired.Count == 0)
        {
            reason = $"REJECTED: selfExtend.mode '{parsed.SelfExtend.Mode}' requires at least one entry in "
                   + "gatesRequired. An extension path with no gates is not a gate.";
            return false;
        }

        policy = parsed;
        reason = string.Empty;
        return true;
    }
}
