using System.Net;
using Microsoft.Extensions.Configuration;

namespace Ashlar.Infrastructure.Deployment;

/// <summary>
/// On AirGapped and SecureWorkstation every Ashlar inbound listener binds loopback, or the host does not boot (SPEC-007
/// PR 4.10, owner decision Q6). Responses on inbound connections are not mediated until PR 5's <c>CanRead</c> at the
/// server seams, so on those profiles nothing off this machine may connect.
/// </summary>
/// <remarks>
/// <para>Ashlar.API makes two checks with it. Before the server starts, <see cref="ConfiguredAddresses"/> reads every
/// address the host's configuration gives Kestrel (<c>Kestrel:Endpoints:*:Url</c>, <c>urls</c> /
/// <c>ASPNETCORE_URLS</c> / <c>--urls</c>, and, when <c>urls</c> is unset, <c>http_ports</c> / <c>https_ports</c>,
/// which bind every interface), and the API refuses to start on a non-loopback one, so nothing is bound. After the
/// server starts, its <c>LoopbackListenerVerifier</c> reads the addresses Kestrel actually bound, which also covers a
/// listener a host adds in code, and fails the start on a non-loopback one. Mesh serve, which listens on every
/// interface, refuses to serve on those profiles with the same message.</para>
/// <para>Loopback means <c>localhost</c>, an IPv4 or IPv6 loopback address, a Unix domain socket or a named pipe. A
/// wildcard (<c>*</c>, <c>+</c>, <c>0.0.0.0</c>, <c>[::]</c>), any other host name or address, and anything that does
/// not parse are not.</para>
/// </remarks>
public static class LoopbackListenerPolicy
{
    /// <summary>The addresses the host's configuration gives Kestrel, plus <paramref name="programmaticUrls"/>.</summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="programmaticUrls">Addresses added in code, such as <c>WebApplication.Urls</c>.</param>
    public static IReadOnlyList<string> ConfiguredAddresses(IConfiguration configuration, IEnumerable<string>? programmaticUrls = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var addresses = new List<string>();
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = endpoint["Url"];
            if (!string.IsNullOrWhiteSpace(url))
                addresses.Add(url.Trim());
        }

        var urls = configuration["urls"];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            addresses.AddRange(Split(urls));
        }
        else
        {
            // Kestrel turns these into http://*:<port> and https://*:<port> when urls is unset.
            addresses.AddRange(Split(configuration["http_ports"]).Select(port => $"http://*:{port}"));
            addresses.AddRange(Split(configuration["https_ports"]).Select(port => $"https://*:{port}"));
        }

        if (programmaticUrls is not null)
            addresses.AddRange(programmaticUrls.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()));

        return addresses;
    }

    /// <summary><see langword="true"/> when <paramref name="address"/> listens on this machine only.</summary>
    /// <param name="address">A Kestrel address, such as <c>http://localhost:5000</c>.</param>
    public static bool IsLoopback(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            return false;

        var rest = address[(schemeEnd + 3)..];
        if (rest.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase))
            return true;

        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var hostAndPort = slash >= 0 ? rest[..slash] : rest;
        string host;
        if (hostAndPort.StartsWith('['))
        {
            var close = hostAndPort.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
                return false;
            host = hostAndPort[1..close];
        }
        else
        {
            var colon = hostAndPort.LastIndexOf(':');
            host = colon >= 0 ? hostAndPort[..colon] : hostAndPort;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// The explained failure when <paramref name="profile"/> requires loopback listeners and an address is not one;
    /// otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="profile">The profile <c>AddAshlar</c> resolved, or <see langword="null"/>.</param>
    /// <param name="addresses">The listener addresses.</param>
    /// <param name="what">The listener, for the message.</param>
    /// <param name="remedy">How to bind loopback, for the message; <see langword="null"/> for Kestrel's
    /// <c>ASPNETCORE_URLS</c>.</param>
    public static string? Violation(
        ResolvedDeploymentProfile? profile,
        IEnumerable<string> addresses,
        string what,
        string? remedy = null)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (profile is not { RequiresLoopbackInbound: true })
            return null;

        var offending = addresses.Where(address => !IsLoopback(address)).ToArray();
        if (offending.Length == 0)
            return null;

        return $"{what} would listen on {string.Join(", ", offending)}, which is not loopback. The {profile.DisplayName} " +
               "deployment profile allows inbound listeners on loopback only (localhost, 127.0.0.1, [::1], a Unix socket " +
               "or a named pipe): responses on inbound connections are not mediated until SPEC-007 PR 5. " +
               (remedy ?? "Bind loopback (for example ASPNETCORE_URLS=http://localhost:5000)") +
               $", or run under a profile other than {profile.DisplayName}.";
    }

    private static IEnumerable<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
