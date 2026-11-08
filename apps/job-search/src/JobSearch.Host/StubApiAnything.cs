using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JobSearch.Host;

/// <summary>
/// A stand-in for <c>api-anything serve</c>, for the demo and tests. NOT API Anything. It answers the REST contract of
/// API Anything's docs/SERVE.md (POST /v1/call/{site}/{op} → 200 with ok/class/data; input for a closed posting) from
/// synthetic boards (fixtures/boards.json), as the transport of the real <c>ApiAnythingClient</c>, so the client, the
/// egress check and the bricks all run for real.
/// </summary>
public sealed class StubApiAnything : HttpMessageHandler
{
    private static readonly Dictionary<string, string> IdField = new(StringComparer.Ordinal)
    {
        ["greenhouse"] = "jobId", ["lever"] = "postingId", ["ashby"] = "jobId", ["workable"] = "shortcode", ["workday"] = "job",
    };
    private static readonly Dictionary<string, string[]> ListFields = new(StringComparer.Ordinal)
    {
        ["greenhouse"] = ["jobId", "title", "department", "location", "url", "updatedAt"],
        ["lever"] = ["postingId", "title", "team", "department", "location", "allLocations", "commitment", "workplaceType", "country", "createdAt", "url"],
        ["ashby"] = ["jobId", "title", "teamId", "location", "workplaceType", "employmentType", "secondaryLocations", "compensation"],
    };
    private static readonly Dictionary<string, string[]> DetailFields = new(StringComparer.Ordinal)
    {
        ["greenhouse"] = ["title", "location", "description"],
        ["lever"] = ["postingId", "title", "team", "department", "location", "allLocations", "commitment", "workplaceType", "country", "createdAt", "description", "sections", "additional", "salaryRange", "url"],
        ["ashby"] = ["jobId", "title", "departmentName", "teamNames", "location", "secondaryLocationNames", "employmentType", "workplaceType", "descriptionHtml", "compensation", "compensationTiers"],
    };

    private readonly object _lock = new();
    private readonly JsonObject _boards;
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
    private readonly List<(string Site, string Op, string Json)> _forced = [];

    /// <summary>Every call: (site, op, args).</summary>
    public List<(string Site, string Op, Dictionary<string, string> Args)> Calls { get; } = [];

    /// <summary>Loads boards from a JSON file in the fixtures shape.</summary>
    public StubApiAnything(string boardsJsonPath) => _boards = JsonNode.Parse(File.ReadAllText(boardsJsonPath))!.AsObject();

    /// <summary>The employer closes a posting: it leaves the board and getJob answers input.</summary>
    public void Close(string site, string company, string id)
    {
        lock (_lock) _closed.Add($"{site}:{company}:{id}");
    }

    /// <summary>The next call of site.op answers this result JSON instead.</summary>
    public void Force(string site, string op, string resultJson)
    {
        lock (_lock) _forced.Add((site, op, resultJson));
    }

    /// <summary>Calls of one site/op.</summary>
    public int Count(string site, string op)
    {
        lock (_lock) return Calls.Count(c => c.Site == site && c.Op == op);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var parts = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        string json;
        if (parts is ["v1", "call", var site, var op])
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!;
            var args = body["args"]?.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.Ordinal) ?? [];
            json = Call(site, op, args);
        }
        else
        {
            json = """{"ok":false,"class":"input","reason":"unknown route"}""";
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private string Call(string site, string op, Dictionary<string, string> args)
    {
        lock (_lock)
        {
            Calls.Add((site, op, args));
            var forced = _forced.FindIndex(f => f.Site == site && f.Op == op);
            if (forced >= 0)
            {
                var j = _forced[forced].Json;
                _forced.RemoveAt(forced);
                return j;
            }
            var company = args.GetValueOrDefault("company", "");
            if (_boards[site]?[company] is not JsonArray postings)
                return """{"ok":false,"class":"input","reason":"no such board (404)","retry":"after_fix"}""";
            var open = postings.Select(p => p!.AsObject()).Where(p => !_closed.Contains($"{site}:{company}:{p[IdField[site]]}")).ToList();
            if (op == "listJobs")
            {
                var page = int.Parse(args.GetValueOrDefault("page", "1"), System.Globalization.CultureInfo.InvariantCulture);
                var items = open.Skip((page - 1) * 50).Take(50).Select(p => Pick(p, ListFields[site])).ToList();
                return Ok(new JsonArray(items.ToArray<JsonNode?>()));
            }
            var id = args.GetValueOrDefault(IdField[site], "");
            var hit = open.FirstOrDefault(p => p[IdField[site]]?.ToString() == id);
            if (hit is null)
                return """{"ok":false,"class":"input","reason":"posting not found (404)","retry":"after_fix"}""";
            var detail = Pick(hit, DetailFields[site]);
            return Ok(site == "greenhouse" ? new JsonArray(detail) : detail);
        }
    }

    private static JsonObject Pick(JsonObject p, string[] fields)
    {
        var o = new JsonObject();
        foreach (var f in fields)
        {
            var source = f switch { "location" when p.ContainsKey("location") => "location", _ => f };
            if (p[source] is { } v)
                o[f] = v.DeepClone();
        }
        return o;
    }

    private static string Ok(JsonNode data) =>
        new JsonObject { ["ok"] = true, ["class"] = "ok", ["tier"] = 1, ["data"] = data }.ToJsonString(new JsonSerializerOptions());
}
