namespace Ashlar.Manifest;

/// <summary>
/// The envelope — <c>ashlar.policy.yaml</c>. Describes what the application is PERMITTED TO
/// BECOME.
///
/// <para>The application cannot read, propose, or modify this document; the gate can. That
/// asymmetry is the entire safety model for self-extension. Ship it in the same repository
/// if you like — it is read by a different process and signed by a different key.</para>
/// </summary>
public sealed record AshlarPolicy
{
    /// <summary>Schema version. Only <c>ashlar/v1</c> is accepted.</summary>
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>Document kind. Must be <c>Policy</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Filesystem confinement for every tool the application holds.</summary>
    public PolicySandbox Sandbox { get; init; } = new();

    /// <summary>Runtime self-extension settings.</summary>
    public PolicySelfExtend SelfExtend { get; init; } = new();

    /// <summary>
    /// Prohibitions. Not configuration — see <see cref="PolicyLoader.RequiredNeverEntries"/>.
    /// A policy that omits any required entry fails to load rather than yielding a
    /// permissive gate.
    /// </summary>
    public List<string> Never { get; init; } = [];

    /// <summary>
    /// Clearances for agents this project composes (SPEC-007 PR 5). Omitted means every agent is
    /// <c>Public</c>. The running application cannot edit this file.
    /// </summary>
    public PolicyClearances Clearances { get; init; } = new();
}

/// <summary>
/// Agent clearances. Each value is canonical security-label text (<c>Secret</c>,
/// <c>Secret//C:ALPHA</c>, <c>SystemHigh</c>). Quote a value that contains <c>//</c>, or YAML reads the
/// rest of the line as a comment. An absent default is <c>Public</c> at composition time.
/// A host may only tighten a clearance; it cannot raise one above this ceiling.
/// </summary>
public sealed record PolicyClearances
{
    /// <summary>Clearance for an agent that <see cref="Agents"/> does not name. Empty means unset.</summary>
    public string Default { get; init; } = "";

    /// <summary>Per-agent ceiling, keyed by the agent name compared ordinally.</summary>
    public Dictionary<string, string> Agents { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Filesystem confinement.</summary>
public sealed record PolicySandbox
{
    /// <summary>
    /// The sandbox root, supplied by the host. Nothing inside the confined system may widen
    /// it — that is <c>widen_sandbox</c> on the never-list, and it is the same rule the tool
    /// layer already enforces by taking its root from the world snapshot rather than from
    /// tool arguments.
    /// </summary>
    public string Root { get; init; } = string.Empty;

    /// <summary>Paths beneath <see cref="Root"/> that may be written.</summary>
    public List<string> Writable { get; init; } = [];

    /// <summary>
    /// When true, a mediated apply is additionally confined to <see cref="Writable"/>: a target
    /// outside every writable entry is refused, on top of the always-on governance floor. Default
    /// false, so existing projects keep the floor-only behaviour they were written against;
    /// opting in turns <see cref="Writable"/> from advisory metadata into an enforced allowlist.
    /// </summary>
    public bool EnforceWritableAllowlist { get; init; }
}

/// <summary>Runtime self-extension settings.</summary>
public sealed record PolicySelfExtend
{
    /// <summary>Extension mode. See <see cref="SelfExtendMode"/>.</summary>
    public string Mode { get; init; } = SelfExtendMode.Sealed;

    /// <summary>How much the application may extend itself within a window.</summary>
    public PolicyBudget Budget { get; init; } = new();

    /// <summary>
    /// Kinds the application may add to itself. Only <c>brick</c> is permitted: a brick adds
    /// capability INSIDE the existing envelope, whereas tools and capabilities WIDEN it, so
    /// they are never self-addable.
    /// </summary>
    public List<string> MayAdd { get; init; } = [];

    /// <summary>Gates every proposed extension must clear before it can be admitted.</summary>
    public List<string> GatesRequired { get; init; } = [];

    /// <summary>
    /// Operator fingerprints (<c>ed25519:…</c>) whose sealed packages this project will admit.
    /// Empty means trust nothing imported — an imported package is refused before it parks unless
    /// its sealer is listed here OR in the operator's local peers keychain
    /// (<c>keys trust &lt;fp&gt;</c>). This is the portable, checked-in half of the trust root;
    /// the keychain is the local half. Neither is a load-time obligation — an empty list is a
    /// valid, fail-closed posture, not a rejection.
    /// </summary>
    public List<string> TrustedSigners { get; init; } = [];
}

/// <summary>Extension budget.</summary>
public sealed record PolicyBudget
{
    /// <summary>Maximum extensions admitted per window.</summary>
    public int Extensions { get; init; }

    /// <summary>Window, expressed as a duration string such as <c>24h</c>.</summary>
    public string Window { get; init; } = string.Empty;
}

/// <summary>
/// The three runtime modes. A dial, not a switch — the difference between them is who is
/// permitted to seat the bonding stone on a self-authored change.
/// </summary>
public static class SelfExtendMode
{
    /// <summary>Nothing changes after deploy. The runtime refuses to load anything absent
    /// from the signed bundle. The default for a new project.</summary>
    public const string Sealed = "sealed";

    /// <summary>The application may author extensions and run them through every gate, but
    /// the verdict stops at Held: a person seats the stone.</summary>
    public const string Proposing = "proposing";

    /// <summary>Extensions clearing every gate are admitted automatically and signed into the
    /// ledger. Anything short of certified is refused.</summary>
    public const string SelfExtending = "self-extending";

    /// <summary>Every recognised mode.</summary>
    public static readonly IReadOnlyList<string> All = [Sealed, Proposing, SelfExtending];
}
