using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Ashlar.Abstractions.Security.Egress;
using JobSearch.Privacy;

#pragma warning disable ASHLAREXP002 // IEgressGuard and EgressHttp are the experimental egress surface; every model call goes through the guard by design.

namespace JobSearch.Host.Studio;

/// <summary>
/// The server-hosted model (Ollama's chat API) for anything that may hold personal data. It must be on this host: the
/// constructor refuses a base URL that is not loopback, and the egress guard, which counts only loopback as inside the
/// host, refuses any other destination from a frame that has read PII.
/// </summary>
public sealed class OllamaPrivateModel : IPrivateModel, IDisposable
{
    /// <summary>Egress family; any <c>model.*</c> family off the host is an external model to the guard.</summary>
    public const string Family = "model.private";
    /// <summary>Egress site id.</summary>
    public const string Site = "jobsearch.private-model";

    private readonly HttpClient _http;
    private readonly string _model;

    /// <summary>A client for <paramref name="model"/> at <paramref name="baseUrl"/>.</summary>
    public OllamaPrivateModel(Uri baseUrl, string model, IEgressGuard guard, HttpMessageHandler? inner = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!baseUrl.IsAbsoluteUri || !baseUrl.IsLoopback)
            throw new InvalidOperationException(
                $"the private model must run on this server: JOBSEARCH_PRIVATE_MODEL_URL must be a loopback address (127.0.0.1, ::1 or localhost), not {baseUrl.Host}");
        _http = inner is null ? EgressHttp.CreateClient(Family, Site, guard) : EgressHttp.CreateClient(inner, Family, Site, guard);
        _http.BaseAddress = baseUrl;
        _http.Timeout = timeout ?? TimeSpan.FromMinutes(5); // CPU inference of an 8B model (owner decision 2026-11-08)
        _model = model;
    }

    /// <inheritdoc />
    public string Describe() => $"{_model} on this server ({_http.BaseAddress})";

    /// <inheritdoc />
    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        using var frame = Pii.EnterPiiFreeFrame("private-model.health");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _http.GetAsync(new Uri("api/tags", UriKind.Relative), deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;
            var models = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false))?["models"]?.AsArray() ?? [];
            // Ollama names a model without a tag as name:latest.
            var wanted = _model.Contains(':', StringComparison.Ordinal) ? _model : _model + ":latest";
            return models.Any(m => m?["name"]?.GetValue<string>() == wanted);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<ModelReply> CompleteJsonAsync(string system, string user, string jsonSchema, CancellationToken ct) =>
        ChatAsync(system, user, JsonNode.Parse(jsonSchema), ct);

    /// <inheritdoc />
    public Task<ModelReply> CompleteTextAsync(string system, string user, CancellationToken ct) => ChatAsync(system, user, null, ct);

    private async Task<ModelReply> ChatAsync(string system, string user, JsonNode? format, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["options"] = new JsonObject { ["temperature"] = 0 },
        };
        if (format is not null)
            body["format"] = format;
        using var response = await _http.PostAsync(new Uri("api/chat", UriKind.Relative),
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"the private model answered {(int)response.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");
        var reply = JsonNode.Parse(text)!;
        return new ModelReply(reply["message"]?["content"]?.GetValue<string>() ?? "", _model,
            reply["prompt_eval_count"]?.GetValue<long>() ?? 0, reply["eval_count"]?.GetValue<long>() ?? 0);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}

/// <summary>Stands in when no private model is configured: every call fails, so nothing falls back to an external model.</summary>
public sealed class UnavailablePrivateModel : IPrivateModel
{
    /// <inheritdoc />
    public string Describe() => "none configured";

    /// <inheritdoc />
    public Task<bool> ReadyAsync(CancellationToken ct) => Task.FromResult(false);

    /// <inheritdoc />
    public Task<ModelReply> CompleteJsonAsync(string system, string user, string jsonSchema, CancellationToken ct) =>
        throw new InvalidOperationException("no private model is configured (JOBSEARCH_PRIVATE_MODEL_URL); personal data is never sent to an external model instead");

    /// <inheritdoc />
    public Task<ModelReply> CompleteTextAsync(string system, string user, CancellationToken ct) => CompleteJsonAsync(system, user, "{}", ct);
}

/// <summary>
/// Claude, through the official Anthropic C# SDK, for writing step code from a generic design. Its HTTP client is an
/// Ashlar egress client with an enforcing guard, so a call from a frame that has read PII is refused before it is sent.
/// </summary>
public sealed class ClaudeStepWriter : IStepWriter
{
    /// <summary>Egress family.</summary>
    public const string Family = "model.anthropic";
    /// <summary>Egress site id.</summary>
    public const string Site = "jobsearch.step-writer";

    private readonly AnthropicClient _client;
    private readonly string _model;

    /// <summary>A writer using <paramref name="model"/> (default <c>claude-opus-5-5</c>).</summary>
    public ClaudeStepWriter(string apiKey, string? model, IEgressGuard guard, HttpMessageHandler? inner = null)
    {
        var http = inner is null ? EgressHttp.CreateClient(Family, Site, guard) : EgressHttp.CreateClient(inner, Family, Site, guard);
        http.Timeout = TimeSpan.FromMinutes(10);
        _client = new AnthropicClient { ApiKey = apiKey, HttpClient = http, MaxRetries = 2 };
        _model = string.IsNullOrWhiteSpace(model) ? "claude-opus-5-5" : model;
    }

    /// <inheritdoc />
    public string Describe() => $"{_model} (Anthropic API)";

    /// <inheritdoc />
    public async Task<ModelReply> WriteJsonAsync(string system, string user, string jsonSchema, CancellationToken ct)
    {
        using var schema = JsonDocument.Parse(jsonSchema);
        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 16000,
            // A declined request is re-served by the recommended fallback model instead of failing (STUDIO.md I2).
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.High,
                Format = new BetaJsonOutputFormat { Schema = schema.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) },
            },
            System = system,
            Messages = [new BetaUserMessageParam { Content = user }],
        }, ct).ConfigureAwait(false);
        if (response.StopReason == "refusal")
            throw new InvalidOperationException("the step writer declined this design");
        var text = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out var t))
                text.Append(t.Text);
        return new ModelReply(text.ToString(), _model, response.Usage.InputTokens, response.Usage.OutputTokens);
    }
}

/// <summary>Stands in when no Anthropic key is configured: new steps can be designed and reused, not written.</summary>
public sealed class UnavailableStepWriter : IStepWriter
{
    /// <inheritdoc />
    public string Describe() => "none configured";

    /// <inheritdoc />
    public Task<ModelReply> WriteJsonAsync(string system, string user, string jsonSchema, CancellationToken ct) =>
        throw new InvalidOperationException("no step writer is configured (ANTHROPIC_API_KEY)");
}

/// <summary>
/// Synthetic mode's private model (QA and demos, STUDIO.md I15): deterministic answers, no Ollama. It reuses a library
/// step when the request names one of its words, and otherwise designs one fixed step: "Flag postings whose title
/// contains a word".
/// </summary>
public sealed class SyntheticPrivateModel : IPrivateModel
{
    /// <summary>The design it proposes for anything it cannot match.</summary>
    public static readonly JsonObject FallbackDesign = new()
    {
        ["kind"] = "check",
        ["name"] = "Flag postings whose title contains a word",
        ["description"] = "Flags a posting whose title contains the word you give.",
        ["settings"] = new JsonArray(new JsonObject { ["label"] = "Word", ["help"] = "One word or phrase; case is ignored.", ["example"] = "trainee" }),
        ["behavior"] = "If setting1, trimmed and lower-cased, is not empty and the lower-cased title contains it, answer flag with the note: title contains \"<word>\". Otherwise pass.",
    };

    /// <inheritdoc />
    public string Describe() => "synthetic (QA)";

    /// <inheritdoc />
    public Task<ModelReply> CompleteJsonAsync(string system, string user, string jsonSchema, CancellationToken ct)
    {
        if (user.StartsWith("Rewrite this step design", StringComparison.Ordinal))
            return Reply(FallbackDesign.DeepClone().AsObject());
        if (user.StartsWith("DESIGN:", StringComparison.Ordinal))
            return new SyntheticStepWriter().WriteJsonAsync(system, user, jsonSchema, ct);
        var request = Between(user, "REQUEST:\n", "\n\nLIBRARY:").ToLowerInvariant();
        var library = Between(user, "LIBRARY:\n", "\n\nEND").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in library)
        {
            var parts = line.Split(" | ");
            if (parts.Length >= 2 && parts[1].ToLowerInvariant().Split(' ').Where(w => w.Length >= 5).Any(request.Contains))
                return Reply(new JsonObject { ["decision"] = "reuse", ["reuseStepId"] = parts[0].Trim(), ["settings"] = Settings(request), ["question"] = "", ["design"] = FallbackDesign.DeepClone() });
        }
        return Reply(new JsonObject { ["decision"] = "new", ["reuseStepId"] = "", ["settings"] = Settings(request), ["question"] = "", ["design"] = FallbackDesign.DeepClone() });
    }

    /// <inheritdoc />
    public Task<ModelReply> CompleteTextAsync(string system, string user, CancellationToken ct) =>
        Task.FromResult(new ModelReply("I would like to bring my pipeline work to your team.", "synthetic", 0, 0));

    private static JsonArray Settings(string request)
    {
        var quoted = request.Split('"');
        return new JsonArray(quoted.Length >= 3 ? quoted[1] : "", "", "");
    }

    private static Task<ModelReply> Reply(JsonObject o) => Task.FromResult(new ModelReply(o.ToJsonString(), "synthetic", 0, 0));

    private static string Between(string text, string start, string end)
    {
        var a = text.IndexOf(start, StringComparison.Ordinal);
        if (a < 0) return "";
        a += start.Length;
        var b = text.IndexOf(end, a, StringComparison.Ordinal);
        return b < 0 ? text[a..] : text[a..b];
    }
}

/// <summary>Synthetic mode's step writer: the code for <see cref="SyntheticPrivateModel.FallbackDesign"/>, written by hand.</summary>
public sealed class SyntheticStepWriter : IStepWriter
{
    /// <summary>The code it returns.</summary>
    public const string Code = """
        private string Evaluate(string title, string employer, string location, string arrangement, string description, string payMin, string payMax, string skillsRequired, string skillsMissing, string setting1, string setting2, string setting3, out string note)
        {
            note = "";
            var word = setting1.Trim().ToLowerInvariant();
            if (word.Length > 0 && title.ToLowerInvariant().Contains(word, StringComparison.Ordinal))
            {
                note = "title contains \"" + word + "\"";
                return "flag";
            }
            return "pass";
        }
        """;

    /// <summary>The cases it returns.</summary>
    public static readonly (string Title, string Setting, string Verdict, string Note)[] Cases =
    [
        ("Data Engineering Intern", "intern", "flag", "title contains \"intern\""),
        ("Senior Data Engineer", "intern", "pass", ""),
        ("Data Engineer", "", "pass", ""),
        ("INTERNAL Tools Engineer", "intern", "flag", "title contains \"intern\""),
        ("Data Engineer", "  Data  ", "flag", "title contains \"data\""),
        ("Platform Lead", "go", "pass", ""),
        ("Go Developer", "go", "flag", "title contains \"go\""),
    ];

    /// <inheritdoc />
    public string Describe() => "synthetic (QA)";

    /// <inheritdoc />
    public Task<ModelReply> WriteJsonAsync(string system, string user, string jsonSchema, CancellationToken ct)
    {
        var cases = new JsonArray();
        foreach (var (title, setting, verdict, note) in Cases)
        {
            var c = new JsonObject();
            foreach (var i in JobSearch.Studio.StepContract.Inputs)
                c[i] = "";
            c["title"] = title;
            c["employer"] = "Example Co";
            c["setting1"] = setting;
            c["verdict"] = verdict;
            c["note"] = note;
            cases.Add(c);
        }
        return Task.FromResult(new ModelReply(new JsonObject { ["code"] = Code, ["cases"] = cases, ["notes"] = "" }.ToJsonString(), "synthetic", 0, 0));
    }
}
