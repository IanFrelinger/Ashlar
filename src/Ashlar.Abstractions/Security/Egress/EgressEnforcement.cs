using System.Globalization;
using System.Security.Cryptography;

namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The egress enforcement mode (SPEC-007 PR 4.6): the one resolver every <see cref="EgressGuard"/> uses, the
/// process-wide latch for the <c>ASHLAR_EGRESS_MODE</c> override, and the decision reference.
/// </summary>
/// <remarks>
/// <para><b>The resolver.</b> <see cref="ResolveMode"/> is a pure function of a profile and an override. Until the
/// switch (PR 4.11) every profile defaults to <c>report</c>, and the override is honoured on every profile:
/// <c>enforce</c> is an opt-in, and <c>report</c> keeps report. An override that is neither, and a profile that is
/// not one of the six, fail closed to <c>enforce</c>.</para>
/// <para><b>The latch.</b> The variable is read once per process: by <c>AddAshlar</c>, which notes it together
/// with <c>AshlarHostingOptions.EgressMode</c>, or else at the first decision a process-bound guard makes. A later
/// change to the variable changes nothing. The hosting option can only raise the mode to <c>enforce</c>.</para>
/// <para><b>Route enforcement.</b> HTTP and governed model routes honor the mode in PR 4.7. Previously an
/// <c>enforce</c> record says what would have been refused, not that a send stopped. The netstandard2.0 asset's
/// refusal of a synchronous <c>Send</c> (SPEC-007 PR 4.2) is the runtime's, not the guard's, and holds in every
/// mode.</para>
/// </remarks>
internal static class EgressEnforcement
{
    /// <summary>The mode in which a decision is recorded and the guard refuses nothing.</summary>
    internal const string ReportMode = "report";

    /// <summary>The mode in which a route must refuse what the decision does not allow.</summary>
    internal const string EnforceMode = "enforce";

    /// <summary>The process-wide override, read once.</summary>
    internal const string ModeVariable = "ASHLAR_EGRESS_MODE";

    /// <summary>The profile's own default decided the mode; the canonical profile name follows.</summary>
    internal const string ProfileBasisPrefix = "profile:";

    /// <summary>The profile is not one of the six, so the mode fails closed to <c>enforce</c>.</summary>
    internal const string UnrecognisedProfileBasis = "profile:unrecognised";

    /// <summary>An override (the variable, the hosting option or the guard's constructor) decided the mode.</summary>
    internal const string OverrideBasis = "override";

    /// <summary>The SecureWorkstation break-glass (PR 4.11). Not produced yet.</summary>
    internal const string BreakGlassBasis = "break-glass";

    /// <summary>An override that asked for a lower mode was ignored.</summary>
    internal const string OverrideIgnoredBasis = "override-ignored";

    /// <summary>A host listed the client as report-only (PR 4.11). Not produced yet.</summary>
    internal const string HostOptOutBasis = "host-opt-out";

    /// <summary>An operator verb ran in report mode (PR 4.9). Not produced yet.</summary>
    internal const string OperatorVerbBasis = "operator-verb";

    /// <summary>Resolving the mode faulted, so it fails closed to <c>enforce</c>.</summary>
    internal const string FaultBasis = "fault";

    /// <summary>The profile an unset profile means, as <c>AddAshlar</c> defaults it.</summary>
    internal const string DefaultProfile = "full";

    /// <summary>The reference recorded when a random one could not be drawn.</summary>
    internal const string UnavailableReference = "unavailable";

    private const string HexDigits = "0123456789abcdef";

    private static readonly object Gate = new();

#if NETSTANDARD2_0
    private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();
#endif

    private static readonly AsyncLocal<Action?> Probe = new();

    private static bool _environmentRead;
    private static string? _environmentOverride;
    private static bool _hostRaised;
    private static bool _announced;

    /// <summary>What an override asks for.</summary>
    internal enum OverrideKind
    {
        /// <summary>No override: the profile decides.</summary>
        None,

        /// <summary><c>report</c>.</summary>
        Report,

        /// <summary><c>enforce</c>.</summary>
        Enforce,

        /// <summary>Anything else. It fails closed to <c>enforce</c>.</summary>
        Unrecognised,
    }

    /// <summary>
    /// Test seam, <see langword="null"/> in production: when set, it runs at the start of every mode resolution, after
    /// the decision has read its profile, so a test can make the resolution fault. It is an
    /// <see cref="AsyncLocal{T}"/>: it runs only for decisions made on the flow that set it (and the tasks that flow
    /// starts), so a test that sets it cannot fault a decision another test makes beside it.
    /// <see cref="EgressProcessState"/> snapshots and restores it on the calling flow.
    /// </summary>
    internal static Action? ModeResolutionProbe
    {
        get => Probe.Value;
        set => Probe.Value = value;
    }

    /// <summary>
    /// Resolves the mode for a profile and an override. Pure: it reads nothing but its arguments.
    /// </summary>
    /// <param name="profile">The deployment profile, in any spelling <c>AddAshlar</c> accepts. Blank means the
    /// default profile, <c>full</c>.</param>
    /// <param name="modeOverride"><c>report</c>, <c>enforce</c> (trimmed, any case), anything else, or blank for
    /// none.</param>
    /// <returns>The mode, <c>report</c> or <c>enforce</c>, and its basis.</returns>
    internal static (string Mode, string ModeBasis) ResolveMode(string? profile, string? modeOverride)
    {
        var requested = ParseOverride(modeOverride);

        string canonical;
        if (string.IsNullOrWhiteSpace(profile))
        {
            canonical = DefaultProfile;
        }
        else if (!AshlarDeploymentProfileEnvironment.TryParseKnown(profile, out canonical))
        {
            // A profile nothing can read fails closed, and an override cannot lower it.
            return requested switch
            {
                OverrideKind.None => (EnforceMode, UnrecognisedProfileBasis),
                OverrideKind.Report => (EnforceMode, OverrideIgnoredBasis),
                _ => (EnforceMode, OverrideBasis),
            };
        }

        // Until the switch (PR 4.11) every profile defaults to report, and an override is honoured on every profile.
        return requested switch
        {
            OverrideKind.None => (ReportMode, ProfileBasisPrefix + canonical),
            OverrideKind.Report => (ReportMode, OverrideBasis),
            _ => (EnforceMode, OverrideBasis),
        };
    }

    /// <summary>Reads an override: blank is none; <c>report</c> and <c>enforce</c> are trimmed and any case.</summary>
    internal static OverrideKind ParseOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return OverrideKind.None;

        var text = value!.Trim();
        if (string.Equals(text, ReportMode, StringComparison.OrdinalIgnoreCase))
            return OverrideKind.Report;

        return string.Equals(text, EnforceMode, StringComparison.OrdinalIgnoreCase)
            ? OverrideKind.Enforce
            : OverrideKind.Unrecognised;
    }

    /// <summary>
    /// The override the process binding uses: <c>enforce</c> once <c>AddAshlar</c> raised it, otherwise
    /// <c>ASHLAR_EGRESS_MODE</c> as it was read the first time anything asked.
    /// </summary>
    internal static string? ProcessOverride()
    {
        EnsureEnvironmentRead();
        return Volatile.Read(ref _hostRaised) ? EnforceMode : Volatile.Read(ref _environmentOverride);
    }

    /// <summary>
    /// Notes the hosting option and returns the process override, reading the variable now if nothing has read it.
    /// The option can only raise the mode: <c>enforce</c>, or a value that is neither mode (which fails closed),
    /// raises it for the rest of the process; <c>report</c> and blank change nothing.
    /// </summary>
    /// <param name="hostingOption"><c>AshlarHostingOptions.EgressMode</c>.</param>
    /// <returns>The override every later decision of a process-bound guard uses.</returns>
    internal static string? NoteHostingOption(string? hostingOption)
    {
        EnsureEnvironmentRead();
        if (ParseOverride(hostingOption) is OverrideKind.Enforce or OverrideKind.Unrecognised)
            Volatile.Write(ref _hostRaised, true);

        return ProcessOverride();
    }

    /// <summary>
    /// <see langword="true"/> when the variable, as read once, is neither <c>report</c> nor <c>enforce</c>.
    /// </summary>
    internal static bool EnvironmentOverrideUnrecognised()
    {
        EnsureEnvironmentRead();
        return ParseOverride(Volatile.Read(ref _environmentOverride)) == OverrideKind.Unrecognised;
    }

    /// <summary>A plain report mode: the profile's own default decided it, and it is <c>report</c>.</summary>
    internal static bool IsPlainReport(string mode, string modeBasis) =>
        string.Equals(mode, ReportMode, StringComparison.Ordinal)
        && modeBasis.StartsWith(ProfileBasisPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The one startup line: the mode, its basis, the profile, and whether the profile was defaulted because nothing
    /// set it. The hosted activator logs it, and a process that resolves a mode other than plain report writes it to
    /// standard error once.
    /// </summary>
    internal static string StartupLine(
        string mode,
        string modeBasis,
        string profile,
        bool profileDefaulted,
        bool overrideUnrecognised)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "Ashlar egress mode: {0} (basis {1}; profile {2}{3}). HTTP and governed model routes honor enforcement; explicit sites are not yet enforced (SPEC-007 PR 4.7).",
            mode,
            modeBasis,
            string.IsNullOrEmpty(profile) ? DefaultProfile : profile,
            profileDefaulted ? ", defaulted because nothing set it" : string.Empty);

        return overrideUnrecognised
            ? line + " " + ModeVariable + " or AshlarHostingOptions.EgressMode is neither 'report' nor 'enforce', so the mode fails closed to enforce."
            : line;
    }

    /// <summary>
    /// Writes <see cref="StartupLine"/> to standard error, once per process, the first time it is called for a mode
    /// that is not plain report. Never throws.
    /// </summary>
    internal static void AnnounceOnce(
        string mode,
        string modeBasis,
        string profile,
        bool profileDefaulted,
        bool overrideUnrecognised)
    {
        if (IsPlainReport(mode, modeBasis) || Volatile.Read(ref _announced))
            return;

        lock (Gate)
        {
            if (_announced)
                return;

            _announced = true;
        }

        try
        {
            Console.Error.WriteLine(StartupLine(mode, modeBasis, profile, profileDefaulted, overrideUnrecognised));
        }
#pragma warning disable CA1031 // A closed or failing standard error must not reach a decision's caller.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// A random 64-bit reference for one decision, as 16 lowercase hex digits. Remote parties may see it; unlike the
    /// sequence number it says nothing about how many other decisions the process made.
    /// </summary>
    internal static string NewReference()
    {
        var bytes = new byte[8];
#if NETSTANDARD2_0
        lock (Random)
        {
            Random.GetBytes(bytes);
        }
#else
        RandomNumberGenerator.Fill(bytes);
#endif
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[2 * i] = HexDigits[bytes[i] >> 4];
            chars[(2 * i) + 1] = HexDigits[bytes[i] & 0xF];
        }

        return new string(chars);
    }

    /// <summary>The latch's state, for <see cref="EgressProcessState"/>.</summary>
    internal static object CaptureLatch()
    {
        lock (Gate)
        {
            return new Latch(_environmentRead, _environmentOverride, _hostRaised, _announced, ModeResolutionProbe);
        }
    }

    /// <summary>Puts back a state <see cref="CaptureLatch"/> returned, for <see cref="EgressProcessState"/>.</summary>
    internal static void RestoreLatch(object state)
    {
        var latch = (Latch)state;
        lock (Gate)
        {
            _environmentOverride = latch.EnvironmentOverride;
            _hostRaised = latch.HostRaised;
            _announced = latch.Announced;
            ModeResolutionProbe = latch.Probe;
            Volatile.Write(ref _environmentRead, latch.EnvironmentRead);
        }
    }

    /// <summary>Clears the latch, so the next resolution reads the variable again, for <see cref="EgressProcessState"/>.</summary>
    internal static void ResetLatch() => RestoreLatch(new Latch(false, null, false, false, null));

    private static void EnsureEnvironmentRead()
    {
        if (Volatile.Read(ref _environmentRead))
            return;

        lock (Gate)
        {
            if (_environmentRead)
                return;

            _environmentOverride = Environment.GetEnvironmentVariable(ModeVariable);
            Volatile.Write(ref _environmentRead, true);
        }
    }

    private sealed class Latch
    {
        internal Latch(bool environmentRead, string? environmentOverride, bool hostRaised, bool announced, Action? probe)
        {
            EnvironmentRead = environmentRead;
            EnvironmentOverride = environmentOverride;
            HostRaised = hostRaised;
            Announced = announced;
            Probe = probe;
        }

        internal bool EnvironmentRead { get; }

        internal string? EnvironmentOverride { get; }

        internal bool HostRaised { get; }

        internal bool Announced { get; }

        internal Action? Probe { get; }
    }
}
