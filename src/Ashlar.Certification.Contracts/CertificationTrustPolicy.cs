namespace Ashlar.Certification.Contracts;

/// <summary>
/// Raised when trust configuration is present but cannot be turned into a usable pinning set.
/// </summary>
/// <remarks>
/// This exception exists so that malformed configuration cannot be mistaken for absent
/// configuration. Absent configuration means "this operator has not pinned anything" and leaves
/// verification exactly where it was; malformed configuration means "this operator tried to pin
/// something and we do not know what", and the only safe answer to that is to stop. Degrading it
/// to an empty pinning set would turn pinning OFF — the direction that makes a record signed with
/// an attacker's own keypair verify — so nothing in this file catches it on the operator's behalf.
/// </remarks>
public sealed class CertificationTrustConfigurationException : Exception
{
    /// <summary>Initializes the exception with a message.</summary>
    /// <param name="message">What was wrong with the configuration.</param>
    public CertificationTrustConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and the parse failure beneath it.</summary>
    /// <param name="message">What was wrong with the configuration.</param>
    /// <param name="innerException">The underlying parse failure.</param>
    public CertificationTrustConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The operator-facing way to turn pinning on: reads a set of trusted Ed25519 PUBLIC keys from
/// configuration and hands verifiers the <see cref="CertificationVerifyOptions"/> that carry it.
/// </summary>
/// <remarks>
/// <para><b>Why this type exists.</b> <see cref="CertificationVerifyOptions.TrustedEd25519PublicKeys"/>
/// has always been settable, and <see cref="CertificationVerifyOptions.PinningEnabled"/> has always
/// read it — but nothing outside tests ever assigned it, so every production verifier ran with
/// pinning off and checked each record's signature against the public key the RECORD carries. A
/// record signed with an attacker's own keypair is self-consistent and passes that check. This type
/// is the missing assignment: a host resolves one policy and every verifier it composes applies the
/// same pinning set.</para>
///
/// <para><b>Public keys only.</b> <see cref="TrustedKeysVariable"/> holds Base64 raw 32-byte
/// Ed25519 PUBLIC keys. There is deliberately no configuration path here for a private key — that
/// is the minting capability, it belongs to the certifier, and SPEC-006 keeps it out of the repo
/// and out of CI secrets. Publishing the public half is what pinning is for.</para>
///
/// <para><b>The failure directions are not symmetric.</b> Configuration that is absent leaves the
/// basis preset untouched: the host runs as it did before pinning was configurable, which is a
/// weaker posture the operator already had. Configuration that is PRESENT but unusable — no keys
/// in it, a blank entry, a value that is not Base64, a key that is not 32 bytes, a pinning switch
/// this code cannot read (blank included) — throws
/// <see cref="CertificationTrustConfigurationException"/> at resolution, before any record is
/// verified. An operator who wants "pinning must be in effect" to be checkable sets
/// <see cref="PinningRequiredVariable"/>, and then an absent or empty key set is a startup failure
/// too.</para>
/// </remarks>
public sealed class CertificationTrustPolicy
{
    /// <summary>
    /// Environment variable holding the trusted signer set: Base64 raw 32-byte Ed25519 PUBLIC
    /// keys, separated by commas, semicolons or whitespace (newlines included, so a deployment can
    /// mount the list as a multi-line secret).
    /// </summary>
    public const string TrustedKeysVariable = "ASHLAR_CERT_TRUSTED_ED25519_KEYS";

    /// <summary>
    /// Environment variable that asserts pinning must be in effect. Accepts <c>1/true/yes/on</c>
    /// and <c>0/false/no/off</c>, case-insensitively. When it asserts pinning and
    /// <see cref="TrustedKeysVariable"/> yields no keys, resolution throws rather than running
    /// unpinned. A value that is neither also throws: reading an unrecognized value as "not
    /// required" would let a typo silently switch pinning off. <b>Present but blank throws too</b>
    /// — a deployment template that rendered this variable without substituting a value has said
    /// nothing, and must not be what decides pinning is optional. Only an ABSENT variable means
    /// "no assertion made".
    /// </summary>
    public const string PinningRequiredVariable = "ASHLAR_CERT_PINNING_REQUIRED";

    private static readonly char[] KeySeparators = { ',', ';', ' ', '\t', '\r', '\n' };

    private static readonly string[] TruthyValues = { "1", "true", "yes", "on" };

    private static readonly string[] FalsyValues = { "0", "false", "no", "off" };

    private static readonly Lazy<CertificationTrustPolicy> AmbientPolicy =
        new Lazy<CertificationTrustPolicy>(FromEnvironment, LazyThreadSafetyMode.ExecutionAndPublication);

    private CertificationTrustPolicy(string[]? trustedKeys)
    {
        TrustedEd25519PublicKeys = trustedKeys;
        Strict = Apply(CertificationVerifyOptions.Strict);
        Default = Apply(CertificationVerifyOptions.Default);
    }

    /// <summary>
    /// The policy this process runs under, resolved once from the environment on first use.
    /// </summary>
    /// <remarks>
    /// Cached because a verifier must not change its mind about which signers it accepts halfway
    /// through a process, and because the alternative — re-reading the environment per
    /// verification — makes the trusted set writable by anything that can set a variable in this
    /// process. If resolution throws, every access rethrows the same failure: a host whose trust
    /// configuration is unusable does not get to run verifications with pinning off.
    /// </remarks>
    public static CertificationTrustPolicy Ambient => AmbientPolicy.Value;

    /// <summary>
    /// A policy that pins nothing, for hosts and tests that want the presets verbatim. Every
    /// <c>Apply</c> on it returns its argument.
    /// </summary>
    public static CertificationTrustPolicy Unpinned { get; } = new CertificationTrustPolicy(null);

    /// <summary>
    /// The trusted signer set, or null when the operator configured none. Never empty: an empty
    /// set would read as "pinning off" at every call site that tests
    /// <see cref="CertificationVerifyOptions.PinningEnabled"/>, so it is refused at parse time.
    /// Each key is the CANONICAL <c>Convert.ToBase64String</c> encoding of the 32 raw bytes, not
    /// necessarily the string the operator wrote, because that is the encoding a record carries
    /// and enforcement is an ordinal string comparison.
    /// </summary>
    public IReadOnlyCollection<string>? TrustedEd25519PublicKeys { get; }

    /// <summary>True when this policy carries a signer set, i.e. when pinning is on.</summary>
    public bool PinningConfigured => TrustedEd25519PublicKeys is not null;

    /// <summary>
    /// <see cref="CertificationVerifyOptions.Strict"/> with this policy's pinning set applied.
    /// With nothing configured this is the <c>Strict</c> preset itself, unchanged.
    /// </summary>
    public CertificationVerifyOptions Strict { get; }

    /// <summary>
    /// <see cref="CertificationVerifyOptions.Default"/> with this policy's pinning set applied.
    /// With nothing configured this is the <c>Default</c> preset itself, unchanged.
    /// </summary>
    public CertificationVerifyOptions Default { get; }

    /// <summary>
    /// Resolves the policy from process environment variables. Prefer <see cref="Ambient"/> in
    /// hosts; this overload exists for a host that wants to resolve (and fail) at a point it
    /// chooses.
    /// </summary>
    public static CertificationTrustPolicy FromEnvironment() =>
        FromConfiguration(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Resolves the policy from an arbitrary configuration reader, so a host can source the same
    /// settings from <c>IConfiguration</c>, a secrets mount or a command line rather than the
    /// environment.
    /// </summary>
    /// <param name="read">
    /// Reads a configuration key, returning null when it is not set. Called with
    /// <see cref="TrustedKeysVariable"/> and <see cref="PinningRequiredVariable"/>.
    /// </param>
    /// <exception cref="CertificationTrustConfigurationException">
    /// The configuration is present but unusable, or asserts pinning it does not supply keys for.
    /// </exception>
    public static CertificationTrustPolicy FromConfiguration(Func<string, string?> read)
    {
        if (read is null)
            throw new ArgumentNullException(nameof(read));

        var pinningRequired = ParsePinningRequired(read(PinningRequiredVariable));
        var keys = ParseTrustedKeys(read(TrustedKeysVariable));

        if (pinningRequired && keys is null)
        {
            throw new CertificationTrustConfigurationException(
                $"{PinningRequiredVariable} asserts that certification pinning is in effect, but "
                + $"{TrustedKeysVariable} supplies no trusted Ed25519 public key. Refusing to start "
                + "unpinned: without a pinning set, verification accepts the key each record carries, "
                + "so a record signed with any keypair verifies.");
        }

        return keys is null && !pinningRequired
            ? Unpinned
            : new CertificationTrustPolicy(keys);
    }

    /// <summary>
    /// Builds a policy from an explicit set of Base64 raw Ed25519 public keys, for a host that
    /// already holds them (a mounted trust bundle, a parsed manifest) rather than a raw setting.
    /// The same validation applies: an empty or malformed set throws, and so does a blank entry —
    /// a bundle read line-by-line must have its blank lines removed by the caller rather than
    /// silently yielding a narrower pinning set than the bundle lists.
    /// </summary>
    /// <param name="trustedEd25519PublicKeys">Base64 raw 32-byte Ed25519 public keys.</param>
    public static CertificationTrustPolicy FromTrustedKeys(IEnumerable<string> trustedEd25519PublicKeys)
    {
        if (trustedEd25519PublicKeys is null)
            throw new ArgumentNullException(nameof(trustedEd25519PublicKeys));

        return new CertificationTrustPolicy(Validate(trustedEd25519PublicKeys, TrustedKeysVariable));
    }

    /// <summary>
    /// Returns <paramref name="basis"/> carrying this policy's pinning set. With nothing
    /// configured, returns <paramref name="basis"/> itself — this method never removes a pinning
    /// set, because config that says nothing must not undo a decision the code made.
    /// </summary>
    /// <param name="basis">The strictness preset to pin, e.g. <see cref="CertificationVerifyOptions.Strict"/>.</param>
    /// <exception cref="CertificationTrustConfigurationException">
    /// <paramref name="basis"/> already pins a different set of keys. Silently replacing it would
    /// mean configuration could redirect trust that was chosen in code, and merging the two would
    /// widen the accepted signer set beyond either — so a conflict is reported, not resolved.
    /// </exception>
    public CertificationVerifyOptions Apply(CertificationVerifyOptions basis)
    {
        if (basis is null)
            throw new ArgumentNullException(nameof(basis));

        var keys = TrustedEd25519PublicKeys;
        if (keys is null)
            return basis;

        if (basis.PinningEnabled && !SameKeys(basis.TrustedEd25519PublicKeys!, keys))
        {
            throw new CertificationTrustConfigurationException(
                "The supplied verification options already pin a different set of Ed25519 signers. "
                + $"Configured pinning ({TrustedKeysVariable}) will not override a pinning set chosen "
                + "in code, and will not widen it by merging: resolve the two deliberately.");
        }

        return new CertificationVerifyOptions
        {
            MinimumSchemaVersion = basis.MinimumSchemaVersion,
            // Pinning implies the signature it pins: an unsigned record cannot be pinned, and the
            // verifier already treats PinningEnabled as requiring a signature. Making it explicit
            // here keeps the options object self-describing rather than relying on that coupling.
            RequireEd25519Signature = true,
            TrustedEd25519PublicKeys = keys,
            RequireGateEmittedArtifact = basis.RequireGateEmittedArtifact,
            RequireCertifierIdentity = basis.RequireCertifierIdentity,
        };
    }

    /// <summary>
    /// Parses a configured signer set: null when the setting is absent, otherwise a non-empty,
    /// validated set. Present-but-unusable throws.
    /// </summary>
    /// <param name="configured">The raw setting value, or null when it is not set.</param>
    internal static string[]? ParseTrustedKeys(string? configured)
    {
        if (configured is null)
            return null;

        var entries = configured.Split(KeySeparators, StringSplitOptions.RemoveEmptyEntries);
        return Validate(entries, TrustedKeysVariable);
    }

    private static string[] Validate(IEnumerable<string> entries, string settingName)
    {
        var keys = new List<string>();
        foreach (var entry in entries)
        {
            var candidate = entry?.Trim();
            if (string.IsNullOrEmpty(candidate))
            {
                // Skipping it would hand back a set narrower than the one the caller listed,
                // which is the failure the Base64 message below refuses by name. FromTrustedKeys
                // passes a caller's bundle straight in, and a bundle with a blank line is the
                // ordinary case. It is reachable from ParseTrustedKeys too, though rarer:
                // RemoveEmptyEntries drops the separators in KeySeparators, but whitespace
                // outside that set — a vertical tab, a form feed, a non-breaking space — arrives
                // as an entry that Trim() empties.
                throw new CertificationTrustConfigurationException(
                    $"{settingName} contains a blank entry. Refusing rather than skipping it: a "
                    + "pinning set that silently loses an entry is not the set that was "
                    + "configured, and it refuses honest records signed by the key that went "
                    + "missing. Remove the blank entry, or unset the whole setting to run "
                    + "unpinned deliberately.");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(candidate!);
            }
            catch (FormatException ex)
            {
                throw new CertificationTrustConfigurationException(
                    $"{settingName} contains an entry that is not valid Base64, so it cannot be an "
                    + "Ed25519 public key. Refusing to start with a pinning set that dropped an entry: "
                    + "a set that loses keys silently refuses honest records, and a set that ends up "
                    + "empty stops pinning altogether.",
                    ex);
            }

            if (bytes.Length != 32)
            {
                throw new CertificationTrustConfigurationException(
                    $"{settingName} contains an entry that decodes to {bytes.Length} bytes; an Ed25519 "
                    + "public key is 32 raw bytes. Refusing rather than pinning a value no signature "
                    + "can ever match.");
            }

            // Store the CANONICAL encoding, not the operator's. Validation decodes; enforcement
            // compares strings ordinally against `record.Ed25519PublicKey`, which is always
            // `Convert.ToBase64String` of the raw key. A 32-byte key has four valid 44-character
            // encodings, because the 43rd character carries two bits the decoder discards — so an
            // operator can configure a value that decodes to exactly the right key and then match
            // no record at all, with the host reporting itself correctly pinned. Normalizing here
            // is also what makes the de-duplication below real: two encodings of one key would
            // otherwise count as two keys.
            var canonical = Convert.ToBase64String(bytes);
            if (!keys.Contains(canonical, StringComparer.Ordinal))
                keys.Add(canonical);
        }

        if (keys.Count == 0)
        {
            throw new CertificationTrustConfigurationException(
                $"{settingName} is set but names no Ed25519 public key. An empty pinning set turns "
                + "pinning OFF, which makes verification accept whatever key a record carries — the "
                + "opposite of what setting this asks for. Unset it to run unpinned deliberately.");
        }

        return keys.ToArray();
    }

    private static bool ParsePinningRequired(string? configured)
    {
        if (configured is null)
            return false;

        // Blank is NOT falsy. It is the commonest configuration accident there is — a deployment
        // template rendering `ASHLAR_CERT_PINNING_REQUIRED=` with nothing substituted into it, an
        // `export VAR=$UNSET`, a YAML empty string, an IConfiguration key present with no value —
        // and reading it as "pinning not required" would start the host unpinned while the
        // operator believed they had demanded pinning. That is the one failure this switch exists
        // to catch, so it falls through to the throw below. Absent is handled above, by the null
        // check: absent means "no assertion made", blank means "an assertion that arrived empty".
        // The sibling keys variable already refuses the identical input.
        var value = configured.Trim();
        if (FalsyValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            return false;

        if (TruthyValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            return true;

        throw new CertificationTrustConfigurationException(
            $"{PinningRequiredVariable} is set to a value that is neither true nor false"
            + (value.Length == 0 ? " (it is present but blank)" : $" ('{value}')")
            + ". It is refused rather than read as false, because a typo — or a deployment "
            + "template that rendered the variable without substituting a value — in a switch "
            + "that demands pinning must not be the thing that silently disables it. Unset "
            + $"{PinningRequiredVariable} to run unpinned deliberately.");
    }

    /// <summary>
    /// Set equality, de-duplicated on BOTH sides. The de-duplication is the whole point: counting
    /// and then checking one direction reports "same set" for a basis of <c>{A, A}</c> against a
    /// configuration of <c>{A, B}</c> — matching counts, every left key present — and
    /// <see cref="Apply"/> would return options accepting B, which is exactly the widening its
    /// summary promises never happens. <see cref="Validate"/> de-duplicates the configured side,
    /// but <paramref name="left"/> comes from a caller-supplied options object and nothing
    /// de-duplicates that.
    /// </summary>
    private static bool SameKeys(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right) =>
        new HashSet<string>(left, StringComparer.Ordinal).SetEquals(right);
}
