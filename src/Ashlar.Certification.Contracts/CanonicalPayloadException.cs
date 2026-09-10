namespace Ashlar.Certification.Contracts;

/// <summary>
/// Thrown when the canonical signing payload does not have the exact shape its lane declares,
/// or cannot be written at all.
/// <para>
/// Those bytes are the message every HMAC and Ed25519 certification signature is computed over,
/// so a payload that is not the declared shape does not describe the record it claims to cover.
/// Minting one is refused; the alternative is a certificate whose bytes nobody can vouch for.
/// The payload is written field by field rather than serialized from an object graph, precisely
/// so that its shape is decided here: reflection-based serialization does not survive trimming
/// or ahead-of-time publishing, and under a trimmed publish the payload could serialize to an
/// empty object with no exception and no warning. What remains is a post-condition on the
/// writer, which is what an editing mistake in it would trip.
/// </para>
/// <para>
/// Also thrown when the payload cannot be written as a single well-formed record at all — a
/// proposer supplying the same parameter key twice, whose keys are emitted verbatim and would
/// therefore produce a duplicate property name.
/// </para>
/// <para>
/// Also thrown when the record's schema version selects no lane at all
/// (<c>CertificationRecordSigning.IsKnownSchemaVersion</c>): a version this code has never seen
/// would otherwise be serialized under a shape chosen by guesswork, and an unknown schema
/// version is an error, not a guess. <c>CertificationTrustVerifier</c> reports that case with
/// its own code, <c>schema-version-unknown</c>, so a record fault is not read as the
/// deployment fault above.
/// </para>
/// <para>
/// On the signing side this propagates, because a loud failure at mint time is the correct
/// outcome. On the verification side it is caught and turned into a refusal
/// (<c>CertificationRecordSigning.VerifySignature</c> returns false;
/// <c>CertificationTrustVerifier</c> reports <c>payload-not-canonical</c>), because a verifier
/// must answer the question it was asked rather than throw into its host.
/// </para>
/// </summary>
public sealed class CanonicalPayloadException : InvalidOperationException
{
    /// <summary>Initializes a new instance with a default message.</summary>
    public CanonicalPayloadException()
        : base("The canonical certification payload is not in its declared canonical form.")
    {
    }

    /// <summary>Initializes a new instance with the supplied message.</summary>
    /// <param name="message">Description of how the payload departed from its declared shape.</param>
    public CanonicalPayloadException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the supplied message and inner exception.</summary>
    /// <param name="message">Description of how the payload departed from its declared shape.</param>
    /// <param name="innerException">Underlying failure, typically a serialization or parse error.</param>
    public CanonicalPayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
