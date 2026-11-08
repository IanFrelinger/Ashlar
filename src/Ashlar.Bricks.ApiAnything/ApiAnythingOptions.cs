using System.Net;

namespace Ashlar.Bricks.ApiAnything;

/// <summary>
/// Where the local <c>api-anything serve</c> listens and how it is called.
/// </summary>
public sealed class ApiAnythingOptions
{
    /// <summary>The server's base URL. Must be a loopback address: the server holds the operator's sessions and token.</summary>
    public Uri BaseUrl { get; init; } = new("http://127.0.0.1:8787");

    /// <summary>The bearer token <c>api-anything serve</c> wrote to its token file. Never a website credential.</summary>
    public string? Token { get; init; }

    /// <summary>Upper bound for one call, including polling a <c>pending</c> job.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Answer from a result this recent instead of asking the site again (<c>maxAgeMs</c>). Two callers reading the
    /// same public board inside the window share one request to the employer. <see langword="null"/> turns it off.
    /// </summary>
    public TimeSpan? MaxAge { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Highest transport tier: 1 is plain HTTP, 2 and 3 run a browser. Default 1.</summary>
    public int MaxTier { get; init; } = 1;

    /// <summary>
    /// Reads <c>API_ANYTHING_URL</c> and <c>API_ANYTHING_TOKEN</c>; without a token variable, the token file
    /// <c>serve-token</c> under <c>API_ANYTHING_HOME</c> (default <c>~/.api-anything</c>), as the server writes it.
    /// </summary>
    /// <returns>The options.</returns>
    public static ApiAnythingOptions FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("API_ANYTHING_URL");
        var token = Environment.GetEnvironmentVariable("API_ANYTHING_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            var home = Environment.GetEnvironmentVariable("API_ANYTHING_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".api-anything");
            var file = Path.Combine(home, "serve-token");
            if (File.Exists(file))
                token = File.ReadAllText(file).Trim();
        }

        return new ApiAnythingOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(url) ? new Uri("http://127.0.0.1:8787") : new Uri(url),
            Token = token,
        };
    }

    /// <summary>True when <paramref name="uri"/> is http(s) to <c>localhost</c> or a loopback IP.</summary>
    /// <param name="uri">The base URL.</param>
    /// <returns>Whether it stays on this host.</returns>
    public static bool IsLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        var host = uri.IdnHost.TrimEnd('.');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }
}
