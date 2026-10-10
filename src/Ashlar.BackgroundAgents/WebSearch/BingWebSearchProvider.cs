using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.BackgroundAgents.WebSearch;

/// <summary>
/// Bing Web Search API v7 provider. Requires an Azure Cognitive Services or Bing Search API subscription key.
/// </summary>
public sealed class BingWebSearchProvider : IWebSearchProvider
{
    private const string DefaultBaseUrl = "https://api.bing.microsoft.com/v7.0/search";
    private readonly HttpClient _httpClient;
    private readonly string _subscriptionKey;
    private readonly string _baseUrl;
    private readonly ILogger<BingWebSearchProvider>? _logger;

    /// <inheritdoc />
    public string ProviderId => "bing";

    /// <summary>
    /// Initializes a new instance of the <see cref="BingWebSearchProvider"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client (not disposed by this class).</param>
    /// <param name="subscriptionKey">Bing Search API subscription key (Ocp-Apim-Subscription-Key).</param>
    /// <param name="baseUrl">Optional base URL (default: Bing v7 endpoint).</param>
    /// <param name="logger">Optional logger.</param>
    public BingWebSearchProvider(
        HttpClient httpClient,
        string subscriptionKey,
        string? baseUrl = null,
        ILogger<BingWebSearchProvider>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _subscriptionKey = subscriptionKey ?? throw new ArgumentNullException(nameof(subscriptionKey));
        _baseUrl = baseUrl ?? DefaultBaseUrl;
        _logger = logger;

        _httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _subscriptionKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<WebSearchResult>();

        var count = Math.Clamp(maxResults, 1, 50);
        var uri = new Uri($"{_baseUrl.TrimEnd('/')}?q={Uri.EscapeDataString(query)}&count={count}");
        // SPEC-007 EG-WEB-01: the query leaves in the URL; the record keeps only scheme, host and port.
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.WebSearch, "EG-WEB-01", uri)).ThrowIfRefused();
        try
        {
            var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseBingResponse(json, count);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Bing search request failed for query: {Query}", query);
            return Array.Empty<WebSearchResult>();
        }
        catch (TaskCanceledException)
        {
            return Array.Empty<WebSearchResult>();
        }
    }

    private static IReadOnlyList<WebSearchResult> ParseBingResponse(string json, int maxResults)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("webPages", out var webPages) ||
                !webPages.TryGetProperty("value", out var value))
                return Array.Empty<WebSearchResult>();

            var list = new List<WebSearchResult>();
            foreach (var item in value.EnumerateArray())
            {
                if (list.Count >= maxResults)
                    break;
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";
                list.Add(new WebSearchResult(name, url, snippet));
            }
            return list;
        }
        catch
        {
            return Array.Empty<WebSearchResult>();
        }
    }
}
