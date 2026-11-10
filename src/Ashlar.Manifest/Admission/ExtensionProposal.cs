namespace Ashlar.Manifest.Admission;

/// <summary>
/// A change the running application wants to make to itself, after the gate has evaluated
/// its courses. The admission layer receives proposals whose courses have already run —
/// evaluation is the gate's job; deciding what the OUTCOME means under the policy's mode is
/// this layer's job.
/// </summary>
public sealed record ExtensionProposal
{
    /// <summary>Stable proposal identifier.</summary>
    public required string Id { get; init; }

    /// <summary>What kind of thing this adds. Checked against <c>selfExtend.mayAdd</c> —
    /// only kinds inside the envelope are admissible at all.</summary>
    public required string Kind { get; init; }

    /// <summary>One-line human summary, e.g. "add brick invoice.classify.v2".</summary>
    public required string Summary { get; init; }

    /// <summary>Which agent proposed it.</summary>
    public required string ProposedBy { get; init; }

    /// <summary>When it was proposed (UTC).</summary>
    public required DateTimeOffset ProposedAt { get; init; }

    /// <summary>The course results the gate produced for this proposal.</summary>
    public required IReadOnlyList<CourseResult> Courses { get; init; }

    /// <summary>Compact diff or change description, for the review surface.</summary>
    public string Diff { get; init; } = string.Empty;

    /// <summary>
    /// Forge proposal ids holding this extension's actual file changes (M1 propose → hold →
    /// apply). Empty when the extension carries no mediated writes. Seating the stone
    /// applies these; refusing rejects them.
    /// </summary>
    public IReadOnlyList<string> ForgeProposalIds { get; init; } = [];

    /// <summary>
    /// Content claims for the mediated writes: one (path, sha256) per forge row, in
    /// <see cref="ForgeProposalIds"/> order, recorded when the proposal is built and thereafter
    /// covered by the record's signature. The forge ids name WHERE packaging re-reads content
    /// from — a mutable, unsigned store — and these claims pin WHAT must be there, so a row
    /// edited after the gate decided fails verification instead of travelling under the
    /// origin's signature.
    ///
    /// <para>Null means "nothing claimed": records signed before claims existed, and proposals
    /// with no mediated writes. Null — never an empty list — is the normative spelling
    /// (SPEC-006 S-5): the canonical signing form omits null, keeping every pre-claims
    /// signature verifying byte-for-byte, while an empty list would enter the signed bytes.
    /// The accessor enforces the rule mechanically, so the empty spelling cannot be
    /// constructed, deserialized, or signed.</para>
    /// </summary>
    public IReadOnlyList<FileClaim>? Files
    {
        get => _files;
        init => _files = value is { Count: 0 } ? null : value;
    }

    private readonly IReadOnlyList<FileClaim>? _files;
}

/// <summary>
/// One signed content claim: the project-relative path of an admitted write and the SHA-256 of
/// the exact content the gate decided over. Rides inside <see cref="ExtensionProposal.Files"/>.
/// </summary>
public sealed record FileClaim
{
    /// <summary>Project-relative target path, exactly as the forge row records it.</summary>
    public required string Path { get; init; }

    /// <summary>Lowercase hex SHA-256 over the UTF-8 bytes of the file's full new content.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Builds the claim for one file's content.</summary>
    public static FileClaim For(string path, string content) => new()
    {
        Path = path,
        Sha256 = HashContent(content),
    };

    /// <summary>The claim hash for <paramref name="content"/>: lowercase hex SHA-256 of its
    /// UTF-8 bytes. One definition, used by claimers and verifiers alike — the two sides must
    /// never disagree on what "the content's hash" means.</summary>
    public static string HashContent(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>True when <paramref name="content"/> hashes to this claim's <see cref="Sha256"/>.
    /// Hex comparison is case-insensitive — spelling is presentation, the digest is the claim.</summary>
    public bool Matches(string content) =>
        string.Equals(Sha256, HashContent(content), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Terminal and intermediate states of a proposal. See SPEC-004: transition
/// authority is the security model.</summary>
public enum ProposalState
{
    /// <summary>All courses passed; awaiting a human verdict (proposing mode, or budget
    /// exhaustion in self-extending mode).</summary>
    Held,

    /// <summary>Admitted — either automatically (self-extending, within budget) or by a
    /// human deciding a held proposal.</summary>
    Admitted,

    /// <summary>Refused by a human, with a recorded reason that feeds back to the proposer.</summary>
    Refused,

    /// <summary>Refused automatically: a course failed, the kind is outside the envelope,
    /// or the mode is sealed. No human involved; the reason names the rule.</summary>
    Rejected,
}

/// <summary>The admission layer's verdict on an evaluated proposal.</summary>
public sealed record AdmissionOutcome
{
    /// <summary>Resulting state.</summary>
    public required ProposalState State { get; init; }

    /// <summary>Why — always populated, because a refusal that does not teach produces the
    /// same proposal again tomorrow.</summary>
    public required string Reason { get; init; }
}
