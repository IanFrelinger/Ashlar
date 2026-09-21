namespace Ashlar.Manifest.Admission;

/// <summary>
/// What a call to <see cref="GateStore.ActivateSigningAsync"/> actually did — as opposed to what
/// is now on disk, which is <see cref="Marker"/> alone.
///
/// <para><b>Why this is not just the marker.</b> The marker cannot tell the operator which of
/// three quite different things just happened. A marker this machine vouches for is returned
/// unchanged; a marker under a key nobody here vouches for is REPLACED; and under
/// <c>--repair</c> a vouched-for marker's inventory is re-minted while its instant is kept. All
/// three leave a valid marker signed by this operator, and the second is the one an operator
/// most needs told about: somebody else's declaration about this store was in force, and it no
/// longer is. Reporting that as "already active" is the lie this type exists to stop.</para>
///
/// <para><b>Why the admitted count travels with it.</b> The whole grandfather mechanism rests on
/// an operator reading one printed number and objecting if it is larger than they expect. That
/// number must come from the same scan, under the same lock, that minted the inventory — a count
/// taken by re-reading the store afterwards is a different number about a different instant, and
/// on an authorization surface that difference is the whole game.</para>
/// </summary>
/// <param name="Marker">The activation marker now on disk.</param>
/// <param name="WasAlreadyActive">A marker was already present when this call arrived.</param>
/// <param name="ReplacedUnvouchedMarker">That marker was signed by a key this machine does not
/// vouch for, and has been overwritten by this operator's own.</param>
/// <param name="GrandfatheredAdmitted">How many records in <see cref="Marker"/>'s inventory are
/// in state <see cref="ProposalState.Admitted"/> — the ones that spend self-extension budget.</param>
public readonly record struct GateSigningOutcome(
    GateSigningActivation Marker,
    bool WasAlreadyActive,
    bool ReplacedUnvouchedMarker,
    int GrandfatheredAdmitted);
