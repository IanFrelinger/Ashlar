namespace Ashlar.Mcp.Server;

/// <summary>
/// Options for exposing Ashlar tools over the Model Context Protocol.
///
/// Fail-closed by design: <see cref="Enabled"/> defaults to <see langword="false"/> and the
/// exposure lists default to empty, so a host that merely registers the services exposes
/// nothing until an operator explicitly enables the surface AND allowlists tools.
/// </summary>
public sealed class AshlarMcpServerOptions
{
    /// <summary>Default configuration section (<c>Ashlar:Mcp:Server</c>).</summary>
    public const string SectionPath = "Ashlar:Mcp:Server";

    /// <summary>
    /// Master switch for the MCP server surface. Defaults to <see langword="false"/>;
    /// when disabled the HTTP endpoint is not mapped and the startup catalog validation is skipped.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Explicit allowlist of Ashlar tool ids (e.g. <c>repo.fs.read</c>) to expose over MCP.
    /// Empty means "expose zero tools" — there is deliberately no expose-everything switch.
    /// </summary>
    public IList<string> ExposedToolIds { get; } = new List<string>();

    /// <summary>Server name advertised in the MCP <c>initialize</c> handshake.</summary>
    public string ServerName { get; set; } = "ashlar";

    /// <summary>
    /// Server version advertised in the MCP handshake. Defaults to the assembly's
    /// informational version when unset.
    /// </summary>
    public string? ServerVersion { get; set; }

    /// <summary>
    /// Repository root seeded into the <see cref="Ashlar.Abstractions.WorldSnapshot"/> handed to
    /// tools for MCP-originated calls. Defaults to the current working directory. This is the
    /// host's choice — never derived from anything a remote caller sends.
    /// </summary>
    public string? RepoRoot { get; set; }

    /// <summary>Optional output root for the world snapshot (defaults to <c>{RepoRoot}/out</c>).</summary>
    public string? OutputRoot { get; set; }

    /// <summary>
    /// Upper bound on concurrently executing MCP-originated tool calls. Existing Ashlar tools were
    /// written for a single agent loop and their thread-safety is unproven, so the bridge
    /// serializes aggressively by default rather than assuming reentrancy.
    /// </summary>
    public int MaxConcurrentToolCalls { get; set; } = 8;

    /// <summary>
    /// Per-tool argument overrides force-applied by the bridge before invocation, keyed by Ashlar
    /// tool id then argument name. Lets operators pin caller-influenced arguments — e.g. the
    /// <c>root</c> argument of the <c>repo.fs.*</c> tools — to a fixed value so a remote MCP
    /// client cannot point a tool at an arbitrary directory. String values only; they replace
    /// whatever the caller supplied (or are added when absent).
    /// </summary>
    public IDictionary<string, IDictionary<string, string>> ArgumentOverrides { get; }
        = new Dictionary<string, IDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Operator classification floors for complete tool responses (text, structured content and errors), keyed by
    /// canonical Ashlar tool id. Values are canonical security labels, for example <c>Public</c> or
    /// <c>Secret//C:PROJECT_A//K:NOWEB</c>. Missing classifications are SystemHigh, so exposure alone never authorizes
    /// disclosure. Only classify a tool Public when all data it can access is public or its reads report stricter labels.
    /// </summary>
    public IDictionary<string, string> ResponseLabels { get; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Trusted issuer for authenticated caller NameIdentifier claims. Set by the host operator to the issuer used
    /// by its authentication handler. Missing issuer, anonymous requests and ambiguous identities receive Public
    /// clearance. Tool arguments and caller-provided clearance claims are never used.
    /// </summary>
    public string? CallerIdentityIssuer { get; set; }

    /// <summary>
    /// Operator-owned clearance map keyed by authenticated NameIdentifier subject, matched ordinally under
    /// <see cref="CallerIdentityIssuer"/>. Missing subjects have Public clearance. Unauthenticated stdio calls also
    /// remain Public; a transport must supply a trusted authenticated principal to grant higher clearance.
    /// </summary>
    public IDictionary<string, string> CallerClearances { get; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}
