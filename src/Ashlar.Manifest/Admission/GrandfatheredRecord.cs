namespace Ashlar.Manifest.Admission;

/// <summary>
/// One unsigned gate record the operator authorized at the instant they activated signing: its
/// proposal id and the sha256 of its CANONICAL bytes, lowercase hex.
///
/// <para><b>Why a list and not a date.</b> The grace floor used to be a timestamp compared against
/// <c>GateRecord.DecidedAt</c> — and on an unsigned record <c>DecidedAt</c> is the attacker's own
/// field. An actor who could write <c>gates/</c> did not have to strip anything: they could write a
/// brand-new unsigned <c>Admitted</c> record dated one second below a floor they could read off the
/// store's own records, and it was grandfathered, counted toward the self-extension budget, and
/// shown by <c>ashlar gates</c> as a decision nobody made. Membership in a set the operator's key
/// signed is not a comparison against a field the attacker owns.</para>
///
/// <para><b>Why the canonical hash and not the file's bytes.</b> Hashing the file as it sits on
/// disk makes a trailing newline, a reformat or a <c>text=auto</c> line-ending normalisation refuse
/// the whole store forever, with the operator's only exit being the command that re-mints the set.
/// The canonical form is derived from the deserialized record, so formatting cannot move it — and
/// neither can a future nullable member, because <c>CanonicalJson</c> omits nulls, the same
/// mechanism SPEC-006 S-5 already depends on.</para>
///
/// <para>Top-level rather than nested in <see cref="GateSigningActivation"/> so
/// <see cref="GateSignatureExpectation"/> can name it without naming the marker type, which is
/// what keeps the marker-reader inventory honest.</para>
/// </summary>
/// <param name="Id">The <c>Proposal.Id</c> of the grandfathered record.</param>
/// <param name="Sha256">Lowercase hex sha256 over <c>CanonicalJson.Bytes(record)</c> at activation.</param>
public sealed record GrandfatheredRecord(string Id, string Sha256);
