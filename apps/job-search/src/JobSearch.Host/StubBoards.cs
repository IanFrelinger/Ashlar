using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace JobSearch.Host;

/// <summary>
/// Synthetic employer job boards for the demo and tests, answering the public endpoints the bundled web-operation specs
/// call (Greenhouse Job Board API, Lever Postings API, Ashby job posting API) from fixtures/boards.json. It plugs in as
/// the transport of the real <c>WebOperationClient</c>, so the specs, the egress check and the bricks run for real.
/// </summary>
public sealed class StubBoards : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly JsonObject _boards;
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
    private readonly List<(Func<HttpRequestMessage, bool> Match, HttpStatusCode Status, string Body, string Type)> _forced = [];

    /// <summary>Every request received.</summary>
    public List<Uri> Requests { get; } = [];

    /// <summary>Loads the boards.</summary>
    public StubBoards(string boardsJsonPath) => _boards = JsonNode.Parse(File.ReadAllText(boardsJsonPath))!.AsObject();

    /// <summary>The employer closes a posting: it leaves the board and its own endpoint answers 404.</summary>
    public void Close(string site, string company, string id)
    {
        lock (_lock) _closed.Add($"{site}:{company}:{id}");
    }

    /// <summary>The next request matching <paramref name="match"/> gets this answer instead.</summary>
    public void Force(Func<HttpRequestMessage, bool> match, HttpStatusCode status, string body, string type = "application/json")
    {
        lock (_lock) _forced.Add((match, status, body, type));
    }

    /// <summary>Requests whose URL contains <paramref name="fragment"/>.</summary>
    public int Count(string fragment)
    {
        lock (_lock) return Requests.Count(u => u.AbsoluteUri.Contains(fragment, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (status, body, type) = Answer(request);
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, type) });
    }

    private (HttpStatusCode, string, string) Answer(HttpRequestMessage request)
    {
        lock (_lock)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var forced = _forced.FindIndex(f => f.Match(request));
            if (forced >= 0)
            {
                var f = _forced[forced];
                _forced.RemoveAt(forced);
                return (f.Status, f.Body, f.Type);
            }
            var seg = uri.AbsolutePath.Trim('/').Split('/');
            JsonArray Open(string site, string company, string idKey) =>
                new((_boards[site]?[company]?.AsArray() ?? []).Where(p => !_closed.Contains($"{site}:{company}:{p![idKey]}")).Select(p => p!.DeepClone()).ToArray());
            bool Has(string site, string company) => _boards[site]?[company] is JsonArray;
            const string NotFound = """{"status":404,"error":"Not Found"}""";

            switch (uri.Host)
            {
                case "boards-api.greenhouse.io" when seg is ["v1", "boards", var c, "jobs"] && Has("greenhouse", c):
                    return (HttpStatusCode.OK, new JsonObject { ["jobs"] = Open("greenhouse", c, "id") }.ToJsonString(), "application/json");
                case "boards-api.greenhouse.io" when seg is ["v1", "boards", var c, "jobs", var id] && Has("greenhouse", c):
                    return Open("greenhouse", c, "id").FirstOrDefault(p => p!["id"]!.ToString() == id) is { } job
                        ? (HttpStatusCode.OK, job.ToJsonString(), "application/json") : (HttpStatusCode.NotFound, NotFound, "application/json");
                case "api.lever.co" when seg is ["v0", "postings", var c] && Has("lever", c):
                    return (HttpStatusCode.OK, Open("lever", c, "id").ToJsonString(), "application/json");
                case "api.lever.co" when seg is ["v0", "postings", var c, var id] && Has("lever", c):
                    return Open("lever", c, "id").FirstOrDefault(p => p!["id"]!.ToString() == id) is { } posting
                        ? (HttpStatusCode.OK, posting.ToJsonString(), "application/json") : (HttpStatusCode.NotFound, NotFound, "application/json");
                case "api.ashbyhq.com" when seg is ["posting-api", "job-board", var c] && Has("ashby", c):
                    return (HttpStatusCode.OK, new JsonObject { ["apiVersion"] = "1", ["jobs"] = Open("ashby", c, "id") }.ToJsonString(), "application/json");
                default:
                    return (HttpStatusCode.NotFound, NotFound, "application/json");
            }
        }
    }
}
