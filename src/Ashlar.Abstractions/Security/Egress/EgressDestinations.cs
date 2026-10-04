using System.Globalization;
using System.Net;
using System.Text;

namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The destination classifier and label table, and the redaction of destination text.
/// </summary>
/// <remarks>
/// <para>The first matching rule wins:</para>
/// <list type="number">
/// <item>URI scheme <c>unix</c> or <c>npipe</c>; host <c>localhost</c>, <c>*.localhost</c> or a loopback IP; or a
/// name starting with <c>host:</c>: <see cref="EgressDestinationClass.Host"/>, SystemHigh.</item>
/// <item>Family <c>model.*</c>: <see cref="EgressDestinationClass.ExternalModel"/>, Internal.</item>
/// <item>Family <c>web-search</c>: <see cref="EgressDestinationClass.WebSearch"/>, Confidential.</item>
/// <item>Families a2a, grpc, mcp, http, http.factory, mesh.*, file.export, process, telemetry:
/// <see cref="EgressDestinationClass.NetworkExport"/>, Internal.</item>
/// <item>Anything else: <see cref="EgressDestinationClass.Unknown"/>, Public.</item>
/// </list>
/// <para>The labels are not configuration. Each is the highest built-in data-sensitivity level whose flag allows
/// that kind of destination (<c>DataSensitivityLevels</c> in Ashlar.BackgroundAgents), and a cert-gate twin test
/// pins that derivation.</para>
/// </remarks>
internal static class EgressDestinations
{
    internal const string HostBasis = "inside the host boundary";
    internal const string ExternalModelBasis = "highest level with AllowsExternalLLM (Public, Internal)";
    internal const string WebSearchBasis = "highest level with AllowsWebSearch (Public to Confidential)";
    internal const string NetworkExportBasis = "highest level with AllowsNetworkExports (Public, Internal)";
    internal const string UnknownBasis = "an unknown destination fails closed to the bottom";

    /// <summary>The destination text when there is none to report (an empty name, or a request without a URI).</summary>
    internal const string UnknownDestination = "unknown";

    /// <summary>The prefix of a destination name that declares a destination inside the host boundary.</summary>
    internal const string HostNamePrefix = "host:";

    /// <summary>The longest caller-supplied text a record keeps; longer text is cut and ends in <c>...</c>.</summary>
    internal const int MaxTextLength = 256;

    private const string SchemeSeparator = "://";
    private const string Truncated = "...";

    private static readonly SecurityLabel InternalLabel = new(SecurityLevel.Internal);
    private static readonly SecurityLabel ConfidentialLabel = new(SecurityLevel.Confidential);

    /// <summary>Classifies a destination and gives its redacted text, class, label and basis.</summary>
    /// <param name="family">The request's family, as the caller passed it (matched ordinally).</param>
    /// <param name="uri">The destination URI, or <see langword="null"/> when <paramref name="name"/> is used.</param>
    /// <param name="name">The destination name, used when <paramref name="uri"/> is <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="uri"/> is relative, so it names no host.</exception>
    internal static (string Text, EgressDestinationClass Class, SecurityLabel Label, string Basis) Classify(
        string? family, Uri? uri, string? name)
    {
        bool insideHost;
        var text = uri is not null ? DescribeUri(uri, out insideHost) : DescribeName(name, out insideHost);

        var destinationClass = insideHost ? EgressDestinationClass.Host : ClassOfFamily(family);
        return (text, destinationClass, LabelOf(destinationClass), BasisOf(destinationClass));
    }

    /// <summary>The class a family gives a destination that is not inside the host boundary.</summary>
    internal static EgressDestinationClass ClassOfFamily(string? family)
    {
        if (family is null)
            return EgressDestinationClass.Unknown;

        if (family.StartsWith("model.", StringComparison.Ordinal))
            return EgressDestinationClass.ExternalModel;

        if (string.Equals(family, EgressFamilies.WebSearch, StringComparison.Ordinal))
            return EgressDestinationClass.WebSearch;

        if (family.StartsWith("mesh.", StringComparison.Ordinal))
            return EgressDestinationClass.NetworkExport;

        switch (family)
        {
            case EgressFamilies.A2A:
            case EgressFamilies.Grpc:
            case EgressFamilies.Mcp:
            case EgressFamilies.Http:
            case EgressFamilies.HttpFactory:
            case EgressFamilies.FileExport:
            case EgressFamilies.Process:
            case EgressFamilies.Telemetry:
                return EgressDestinationClass.NetworkExport;
            default:
                return EgressDestinationClass.Unknown;
        }
    }

    internal static SecurityLabel LabelOf(EgressDestinationClass destinationClass) => destinationClass switch
    {
        EgressDestinationClass.Host => SecurityLabel.SystemHigh,
        EgressDestinationClass.ExternalModel => InternalLabel,
        EgressDestinationClass.WebSearch => ConfidentialLabel,
        EgressDestinationClass.NetworkExport => InternalLabel,
        _ => SecurityLabel.Public,
    };

    internal static string BasisOf(EgressDestinationClass destinationClass) => destinationClass switch
    {
        EgressDestinationClass.Host => HostBasis,
        EgressDestinationClass.ExternalModel => ExternalModelBasis,
        EgressDestinationClass.WebSearch => WebSearchBasis,
        EgressDestinationClass.NetworkExport => NetworkExportBasis,
        _ => UnknownBasis,
    };

    /// <summary>The stable name of a class, as it is written to the event source and logs.</summary>
    internal static string ClassName(EgressDestinationClass destinationClass) => destinationClass switch
    {
        EgressDestinationClass.Host => "Host",
        EgressDestinationClass.ExternalModel => "ExternalModel",
        EgressDestinationClass.WebSearch => "WebSearch",
        EgressDestinationClass.NetworkExport => "NetworkExport",
        EgressDestinationClass.Unknown => "Unknown",
        _ => ((int)destinationClass).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// <see langword="true"/> when <paramref name="host"/> is <c>localhost</c>, ends with <c>.localhost</c>, or is
    /// a loopback IP address (IPv6 in brackets or not, IPv4-mapped IPv6 included). One trailing dot (the
    /// fully-qualified form) is ignored. Nothing is resolved.
    /// </summary>
    internal static bool IsLoopbackHost(string? host)
    {
        var bare = Unbracket(host);
        if (bare.Length > 1 && bare[bare.Length - 1] == '.')
            bare = bare.Substring(0, bare.Length - 1);
        if (bare.Length == 0)
            return false;

        if (string.Equals(bare, "localhost", StringComparison.OrdinalIgnoreCase)
            || bare.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(bare, out var address))
            return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Caller-supplied text made safe for a record: at most <see cref="MaxTextLength"/> characters (then
    /// <c>...</c>), with control, format, line and paragraph separator and surrogate characters replaced by
    /// <c>?</c>, as <see cref="SecurityLabel"/> quotes text for its messages. <see langword="null"/> is empty.
    /// </summary>
    internal static string Bound(string? text)
    {
        if (text is null || text.Length == 0)
            return string.Empty;

        var keep = text.Length > MaxTextLength ? MaxTextLength : text.Length;
        var clean = text.Length == keep;
        for (var i = 0; clean && i < keep; i++)
            clean = !IsUnsafeInRecord(text[i]);
        if (clean)
            return text;

        var builder = new StringBuilder(keep + Truncated.Length);
        for (var i = 0; i < keep; i++)
            builder.Append(IsUnsafeInRecord(text[i]) ? '?' : text[i]);
        if (text.Length > keep)
            builder.Append(Truncated);
        return builder.ToString();
    }

    // scheme://host[:port] only. A relative URI names no host, so it cannot be classified.
    private static string DescribeUri(Uri uri, out bool insideHost)
    {
        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("A relative URI names no destination host.", nameof(uri));

        insideHost = IsHostScheme(uri.Scheme) || IsLoopbackHost(uri.Host);
        return Bound(uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
    }

    private static string DescribeName(string? name, out bool insideHost)
    {
        insideHost = false;
        if (name is null || name.Length == 0)
            return UnknownDestination;

        if (name.StartsWith(HostNamePrefix, StringComparison.Ordinal))
        {
            insideHost = true;
            return Bound(name);
        }

        // A URL passed as a name is still a URL: classify and redact it as one, so a URL-shaped name cannot carry
        // a userinfo, path or query into a record.
        var separator = name.IndexOf(SchemeSeparator, StringComparison.Ordinal);
        if (separator > 0 && Uri.CheckSchemeName(name.Substring(0, separator)))
        {
            return Uri.TryCreate(name, UriKind.Absolute, out var parsed)
                ? DescribeUri(parsed, out insideHost)
                : RedactUnparsedUrl(name, separator);
        }

        return Bound(name);
    }

    // A "scheme://..." name that System.Uri rejects: keep the scheme and the authority after any userinfo, and
    // drop everything from the first '/', '\', '?' or '#' after the authority starts.
    private static string RedactUnparsedUrl(string name, int separator)
    {
        var start = separator + SchemeSeparator.Length;
        var end = start;
        var hostStart = start;
        while (end < name.Length && name[end] is not ('/' or '\\' or '?' or '#'))
        {
            if (name[end] == '@')
                hostStart = end + 1;
            end++;
        }

        var builder = new StringBuilder(separator + SchemeSeparator.Length + (end - hostStart));
        builder.Append(name, 0, separator).Append(SchemeSeparator).Append(name, hostStart, end - hostStart);
        return Bound(builder.ToString());
    }

    private static bool IsHostScheme(string scheme) =>
        string.Equals(scheme, "unix", StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, "npipe", StringComparison.OrdinalIgnoreCase);

    private static string Unbracket(string? host)
    {
        if (host is null)
            return string.Empty;
        if (host.Length >= 2 && host[0] == '[' && host[host.Length - 1] == ']')
            return host.Substring(1, host.Length - 2);
        return host;
    }

    // Control, format (bidi overrides, zero-width) and line/paragraph separators would let caller text reshape a
    // log line; lone surrogates are not text. The same set SecurityLabel replaces in its messages.
    private static bool IsUnsafeInRecord(char c) =>
        char.IsControl(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Surrogate;
}
