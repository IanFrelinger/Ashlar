using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions;

namespace Ashlar.Infrastructure;

/// <summary>Configured endpoints an Ashlar host may bind. Actual bound addresses also require verification.</summary>
public sealed class AshlarInboundListenerOptions
{
    /// <summary>URLs and bind addresses the host will listen on.</summary>
    public IReadOnlyList<string> Endpoints { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Loopback rule for AirGapped and SecureWorkstation inbound listeners (SPEC-007 PR 4.10, owner Q6).
/// Responses on those connections are not mediated until PR 5.
/// </summary>
public static class AshlarInboundListenerPolicy
{
    /// <summary>
    /// True for <c>localhost</c>, <c>127.0.0.1</c> and <c>[::1]</c> (and other <see cref="IPAddress.IsLoopback"/> addresses).
    /// Bare <c>::1</c> is not accepted (URI parsing requires brackets). <c>0.0.0.0</c>, <c>+</c>, <c>*</c> and any other host are not loopback. A value with no scheme is read as HTTP.
    /// </summary>
    public static bool IsLoopbackEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var text = endpoint.Trim();
        if (text is "+" or "*" or "0.0.0.0" or "::" or "[::]")
            return false;

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (IPAddress.TryParse(text, out var literal))
                return IsLoopbackAddress(literal);
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;

        var host = uri.IdnHost;
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            host = host[1..^1];

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var address) && IsLoopbackAddress(address);
    }

    private static bool IsLoopbackAddress(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>
    /// An explained refusal when <paramref name="profile"/> requires loopback and any endpoint is not,
    /// otherwise null. An empty configured list needs a post-bind check; it does not establish loopback.
    /// </summary>
    public static string? Refusal(AshlarResolvedDeploymentProfileOptions? profile, IEnumerable<string>? endpoints)
    {
        if (profile is not { RequiresLoopback: true })
            return null;

        var bad = (endpoints ?? Array.Empty<string>())
            .Where(static endpoint => !string.IsNullOrWhiteSpace(endpoint) && !IsLoopbackEndpoint(endpoint))
            .ToArray();
        if (bad.Length == 0)
            return null;

        var name = profile.IsAirGapped ? "AirGapped" : "SecureWorkstation";
        return name + ": inbound listeners must bind loopback until PR 5 mediates responses on inbound connections. " +
            "Non-loopback endpoints: " + string.Join(", ", bad) + ". 127.0.0.1 and localhost succeed.";
    }

    /// <summary>
    /// Server urls (<c>ASPNETCORE_URLS</c> / the host <c>urls</c> setting, semicolon-separated) plus
    /// <c>Kestrel:Endpoints:*:Url</c>. With no server urls, <c>http_ports</c>/<c>https_ports</c> expand to wildcard
    /// addresses, matching ASP.NET Core's port-only binding defaults.
    /// </summary>
    public static IReadOnlyList<string> CollectEndpoints(IConfiguration configuration, string? serverUrls)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(serverUrls))
        {
            foreach (var part in serverUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                list.Add(part);
        }

        else
        {
            AddPorts("http", configuration["http_ports"]);
            AddPorts("https", configuration["https_ports"]);
        }

        foreach (var child in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = child["Url"];
            if (!string.IsNullOrWhiteSpace(url))
                list.Add(url.Trim());
        }

        return list;

        void AddPorts(string scheme, string? ports)
        {
            foreach (var port in (ports ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                list.Add($"{scheme}://*:{port}");
        }
    }
}

/// <summary>Fails boot when an AirGapped or SecureWorkstation host binds a non-loopback inbound listener.</summary>
public sealed class ValidateAshlarInboundListeners : IValidateOptions<AshlarInboundListenerOptions>
{
    private readonly IOptions<AshlarResolvedDeploymentProfileOptions> _profile;

    /// <summary>Creates the validator.</summary>
    public ValidateAshlarInboundListeners(IOptions<AshlarResolvedDeploymentProfileOptions> profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AshlarInboundListenerOptions options)
    {
        var message = AshlarInboundListenerPolicy.Refusal(_profile.Value, options.Endpoints);
        return message is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(message);
    }
}

/// <summary>Registers the inbound-listener boot check.</summary>
public static class AshlarInboundListenerServiceCollectionExtensions
{
    /// <summary>
    /// Records <paramref name="endpoints"/> and refuses boot, via <c>ValidateOnStart</c>, when the resolved
    /// profile is AirGapped or SecureWorkstation and any endpoint is not loopback.
    /// </summary>
    public static IServiceCollection AddAshlarInboundListenerValidation(
        this IServiceCollection services,
        IEnumerable<string> endpoints)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(endpoints);
        var captured = endpoints.ToArray();
        services.AddOptions<AshlarInboundListenerOptions>()
            .Configure(options => options.Endpoints = captured)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AshlarInboundListenerOptions>, ValidateAshlarInboundListeners>());
        return services;
    }
}
