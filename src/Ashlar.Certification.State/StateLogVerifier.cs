namespace Ashlar.Certification.State;

/// <summary>
/// Verifies attested state logs: certified behavior provenance, schema validity, and hash-chain integrity.
/// Tier-1 is structural; optional <see cref="ITransitionReplayer"/> enables Tier-2 replay checks.
/// </summary>
public static class StateLogVerifier
{
    private static readonly CertifiedTransitionBuilder TransitionBuilder = new();

    /// <summary>
    /// Verifies <paramref name="log"/> under <see cref="Contracts.CertificationVerifyOptions.Strict"/>:
    /// every behavior certificate must carry a verifying Ed25519 signature, a gate-emitted
    /// artifact and a certifier identity. This is the production default and is unchanged.
    /// </summary>
    /// <remarks>
    /// Strict requires an Ed25519 signature, which the netstandard2.0 asset of
    /// <c>Ashlar.Certification.Contracts</c> cannot evaluate, so on that asset this overload can
    /// never return a trusted verdict: every transition is refused as
    /// <c>behavior-cert-untrusted</c> with <c>ed25519-verification-unavailable</c> inside the
    /// reason, whatever the certificates carry. A netstandard2.0 host that has decided HMAC-only
    /// certificates are acceptable for its state log selects options it can evaluate through the
    /// overload that takes <see cref="Contracts.CertificationVerifyOptions"/>. Doing so does not
    /// widen what that asset can trust — a certificate that carries an Ed25519 signature is
    /// refused there under every options instance — so only HMAC-only certificates ever verify
    /// on it, and the set it trusts stays a subset of what net8.0 trusts.
    /// </remarks>
    public static StateLogTrustResult Verify(
        AttestedStateLog log,
        StateSchema schema,
        ICertificateResolver resolver,
        string? hmacKey = null,
        ITransitionReplayer? replayer = null) =>
        Verify(log, schema, resolver, hmacKey, replayer, Contracts.CertificationVerifyOptions.Strict);

    /// <summary>
    /// Verifies <paramref name="log"/>, applying <paramref name="options"/> to every behavior
    /// certificate the log binds. The structural checks (schema, hash chain, resolution, optional
    /// replay) are the same under every options instance; only the certificate verdict varies.
    /// </summary>
    /// <param name="log">Attested state log to verify.</param>
    /// <param name="schema">Schema every resulting state hash must satisfy.</param>
    /// <param name="resolver">Resolves each transition's behavior certificate and brick source.</param>
    /// <param name="hmacKey">Optional HMAC key override for certificate verification.</param>
    /// <param name="replayer">Optional Tier-2 replayer; null keeps verification structural.</param>
    /// <param name="options">
    /// Strictness applied to each behavior certificate. Required, and placed last on purpose: the
    /// overload above takes an optional <c>string?</c> in the same position a nullable options
    /// parameter would otherwise occupy, and a literal <c>null</c> argument must keep meaning "no
    /// key". A null options instance is refused (<c>verify-options-missing</c>) rather than
    /// defaulted: a verifier that was not told how strict to be must not guess.
    /// </param>
    public static StateLogTrustResult Verify(
        AttestedStateLog log,
        StateSchema schema,
        ICertificateResolver resolver,
        string? hmacKey,
        ITransitionReplayer? replayer,
        Contracts.CertificationVerifyOptions options)
    {
        if (log is null)
            return Refused("log-missing", "Attested state log is required.", 0);

        if (schema is null)
            return Refused("schema-missing", "State schema is required.", 0);

        if (resolver is null)
            return Refused("resolver-missing", "Certificate resolver is required.", 0);

        if (options is null)
            return Refused("verify-options-missing", "Certification verify options are required.", 0);

        var verified = 0;
        string? priorResultingStateHash = null;
        string? priorEntryHash = null;

        for (var index = 0; index < log.Transitions.Count; index++)
        {
            var transition = log.Transitions[index];
            var transitionLabel = $"transition[{index}]";

            if (!schema.IsValidStateHash(transition.ResultingStateHash))
            {
                return Refused(
                    "schema-violation",
                    $"{transitionLabel} has a resulting state hash that violates the schema.",
                    verified);
            }

            if (index == 0)
            {
                if (!string.Equals(transition.PrevEntryHash, CertifiedTransition.GenesisPrevEntryHash, StringComparison.Ordinal))
                {
                    return Refused(
                        "chain-prev-entry-break",
                        $"{transitionLabel} must start the hash chain with an empty prev-entry hash.",
                        verified);
                }
            }
            else
            {
                if (!string.Equals(transition.PriorStateHash, priorResultingStateHash, StringComparison.Ordinal))
                {
                    return Refused(
                        "chain-reorder",
                        $"{transitionLabel} prior-state hash does not match the prior resulting state hash.",
                        verified);
                }

                if (!string.Equals(transition.PrevEntryHash, priorEntryHash, StringComparison.Ordinal))
                {
                    var code = IsDroppedTransitionGap(log, index, transition.PrevEntryHash, priorEntryHash)
                        ? "chain-gap"
                        : "chain-prev-entry-break";

                    return Refused(
                        code,
                        $"{transitionLabel} prev-entry hash does not link to the prior entry.",
                        verified);
                }
            }

            var expectedEntryHash = TransitionBuilder.ComputeEntryHash(
                transition.PriorStateHash,
                transition.Action,
                transition.BehaviorCertContentHash,
                transition.ResultingStateHash,
                transition.PrevEntryHash);

            if (!string.Equals(transition.EntryHash, expectedEntryHash, StringComparison.Ordinal))
            {
                return Refused(
                    "chain-prev-entry-break",
                    $"{transitionLabel} entry hash does not match its fields.",
                    verified);
            }

            var resolve = resolver.Resolve(transition.BehaviorCertContentHash);
            if (!resolve.Found || resolve.Record is null || resolve.BrickSource is null)
            {
                var code = index == log.Transitions.Count - 1 && log.Transitions.Count > 1
                    ? "injected-behavior-cert-unresolved"
                    : "behavior-cert-unresolved";

                return Refused(
                    code,
                    $"{transitionLabel} behavior certification could not be resolved.",
                    verified);
            }

            var trust = Contracts.CertificationTrustVerifier.Verify(
                resolve.Record,
                resolve.BrickSource,
                hmacKey,
                options);

            if (!trust.Trusted)
            {
                return Refused(
                    "behavior-cert-untrusted",
                    $"{transitionLabel} behavior certification is not trusted ({trust.FailureCode}: {trust.Reason}).",
                    verified);
            }

            if (replayer is not null)
            {
                var replay = replayer.Replay(
                    transition.PriorStateHash,
                    transition.Action,
                    resolve.Record,
                    resolve.BrickSource);

                if (!replay.Matches
                    || !string.Equals(replay.ComputedStateHash, transition.ResultingStateHash, StringComparison.Ordinal))
                {
                    return Refused(
                        "replay-mismatch",
                        replay.Reason ?? $"{transitionLabel} replay did not reproduce the resulting state hash.",
                        verified);
                }
            }

            verified++;
            priorResultingStateHash = transition.ResultingStateHash;
            priorEntryHash = transition.EntryHash;
        }

        return new StateLogTrustResult(true, null, null, verified);
    }

    private static bool IsDroppedTransitionGap(
        AttestedStateLog log,
        int index,
        string observedPrevEntryHash,
        string? expectedPrevEntryHash)
    {
        if (string.IsNullOrEmpty(observedPrevEntryHash)
            || string.Equals(observedPrevEntryHash, expectedPrevEntryHash, StringComparison.Ordinal))
        {
            return false;
        }

        for (var priorIndex = 0; priorIndex < index - 1; priorIndex++)
        {
            if (string.Equals(log.Transitions[priorIndex].EntryHash, observedPrevEntryHash, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static StateLogTrustResult Refused(string code, string reason, int verified) =>
        new(false, code, reason, verified);
}
