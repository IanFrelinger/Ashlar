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
/// name starting with <c>host:</c>: <see cref="EgressDestinationClass.Host"/>, SystemHigh. Never a <c>file</c> URI,
/// whatever its host, and never a name starting with <c>file:</c> (in any case): such a name is a path, recorded as
/// written and never read as a URL, so <c>file:</c> plus a path spelled <c>//127.0.0.1/…</c> is not a loopback URL. A
/// file written to a share leaves the host, so it takes its family's class (SPEC-007 PR 4.1).</item>
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

    /// <summary>
    /// The prefix, matched in any case, of a destination name that is a file path. Such a name is never read as a URL
    /// and is never inside the host boundary.
    /// </summary>
    internal const string FileNamePrefix = "file:";

    /// <summary>The longest caller-supplied text a record keeps; longer text is cut and ends in <c>...</c>.</summary>
    internal const int MaxTextLength = 256;

    private const string SchemeSeparator = "://";
    private const string Truncated = "...";

    /// <summary>The authority recorded for a URL-shaped name whose authority cannot be read without guessing.</summary>
    private const string UnparsedAuthority = "<unparsed>";

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

    // scheme://host[:port] only. A relative URI names no host, so it cannot be classified. A file URI is never inside
    // the host boundary: file://localhost/share and file://127.0.0.1/E$ name a share, which leaves the host.
    private static string DescribeUri(Uri uri, out bool insideHost)
    {
        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("A relative URI names no destination host.", nameof(uri));

        insideHost = !IsFileScheme(uri.Scheme) && (IsHostScheme(uri.Scheme) || IsLoopbackHost(uri.Host));
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

        // A file: name is a path, recorded as written like every other file site's, and never read as a URL: "file:" plus
        // a path spelled //127.0.0.1/... would otherwise parse with a loopback host and read as inside the host.
        if (name.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase))
            return Bound(name);

        // A URL passed as a name is still a URL: classify and redact it as one, so a URL-shaped name cannot carry
        // a userinfo, path or query into a record.
        var separator = name.IndexOf(SchemeSeparator, StringComparison.Ordinal);
        if (separator > 0 && Uri.CheckSchemeName(name.Substring(0, separator)))
            return DescribeUrlName(name, separator, out insideHost);

        return Bound(name);
    }

    // A "scheme://..." name. Its authority ends at the first '/', '\', '?' or '#', as System.Uri reads it, and RFC 3986
    // requires those characters percent-encoded in a userinfo. A credential that holds one unencoded therefore splits:
    // its head reads as host[:port] and its tail, '@' included, as path ("s3://KEY:abc/def@bucket" reads as host KEY
    // and port "abc"). So nothing is guessed. An '@' after the authority and before any '?' or '#' records
    // scheme://<unparsed>. Otherwise System.Uri parses the name; a name it rejects keeps host[:port] only when the
    // host passes Uri.CheckHostName and the port is all digits, and records scheme://<unparsed> when not. Only a name
    // System.Uri parses can be inside the host boundary; any other fails closed to its family's class.
    // Not caught: a credential whose text before an unencoded '?' or '#' is a valid host:port ("amqp://svc:1234?x@b").
    // That is a valid URL whose query or fragment holds an '@', and it is read as one.
    private static string DescribeUrlName(string name, int separator, out bool insideHost)
    {
        insideHost = false;
        var start = separator + SchemeSeparator.Length;
        var end = start;
        var hostStart = start;
        while (end < name.Length && name[end] is not ('/' or '\\' or '?' or '#'))
        {
            if (name[end] == '@')
                hostStart = end + 1;
            end++;
        }

        if (HasUserInfoMarkerAfter(name, end))
            return UnparsedUrl(name, separator);

        if (Uri.TryCreate(name, UriKind.Absolute, out var parsed))
            return DescribeUri(parsed, out insideHost);

        if (!IsHostAndPort(name, hostStart, end))
            return UnparsedUrl(name, separator);

        var builder = new StringBuilder(separator + SchemeSeparator.Length + (end - hostStart));
        builder.Append(name, 0, separator).Append(SchemeSeparator).Append(name, hostStart, end - hostStart);
        return Bound(builder.ToString());
    }

    // An '@' between the end of the authority and the first '?' or '#' (the path) means the authority may be the
    // head of a credential.
    private static bool HasUserInfoMarkerAfter(string name, int authorityEnd)
    {
        for (var i = authorityEnd; i < name.Length && name[i] is not ('?' or '#'); i++)
        {
            if (name[i] == '@')
                return true;
        }

        return false;
    }

    // host[:port], where host passes Uri.CheckHostName (an IPv6 literal in brackets) and port is one or more ASCII
    // digits.
    private static bool IsHostAndPort(string name, int start, int end)
    {
        var hostEnd = end;
        if (start < end && name[start] == '[')
        {
            hostEnd = start;
            while (hostEnd < end && name[hostEnd] != ']')
                hostEnd++;
            if (hostEnd == end)
                return false;
            hostEnd++;
        }
        else
        {
            for (var i = start; i < end; i++)
            {
                if (name[i] == ':')
                {
                    hostEnd = i;
                    break;
                }
            }
        }

        if (Uri.CheckHostName(name.Substring(start, hostEnd - start)) == UriHostNameType.Unknown)
            return false;
        if (hostEnd == end)
            return true;
        if (name[hostEnd] != ':' || hostEnd + 1 == end)
            return false;

        for (var i = hostEnd + 1; i < end; i++)
        {
            if (name[i] is < '0' or > '9')
                return false;
        }

        return true;
    }

    private static string UnparsedUrl(string name, int separator)
    {
        var builder = new StringBuilder(separator + SchemeSeparator.Length + UnparsedAuthority.Length);
        builder.Append(name, 0, separator).Append(SchemeSeparator).Append(UnparsedAuthority);
        return Bound(builder.ToString());
    }

    private static bool IsFileScheme(string scheme) =>
        string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase);

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
