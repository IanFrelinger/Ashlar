using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ashlar.Bricks.GitHub;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;

namespace Ashlar.ReleaseEvidenceEvaluation;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private const string PlaceholderCredential = "evaluation-placeholder-not-a-real-token";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--transport-probe" }))
                return await TransportProbeAsync();
            if (args.SequenceEqual(new[] { "--self-test" }))
                return await TransportSelfTestAsync();
            var options = Options.Parse(args);
            if (Directory.Exists(options.Output))
                throw new InvalidOperationException("Output directory must not already exist; previous results are immutable.");
            var assemblyVersion = typeof(GitHubBlockersBrick).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            if (!assemblyVersion.Contains(options.CandidateCommit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The loaded brick assembly provenance does not contain the requested full candidate commit.");
            var frozenManifestBytes = await File.ReadAllBytesAsync(options.FixtureManifest!);
            var frozenTruthBytes = await File.ReadAllBytesAsync(options.GroundTruth!);
            using var frozenManifest = JsonDocument.Parse(frozenManifestBytes);
            var baseline = ManifestCandidate(frozenManifest.RootElement);
            if (!string.Equals(baseline, options.CandidateCommit, StringComparison.OrdinalIgnoreCase) && !options.AllowCandidateRerun)
                throw new InvalidOperationException("Candidate differs from frozen baseline; an explicit --allow-candidate-rerun is required.");
            Directory.CreateDirectory(options.Output);
            var started = DateTimeOffset.UtcNow;
            // Frozen input copies are retained as bytes; their labels never enter the collector.
            await File.WriteAllBytesAsync(Path.Combine(options.Output, "frozen-fixture-manifest.json"), frozenManifestBytes);
            await File.WriteAllBytesAsync(Path.Combine(options.Output, "frozen-ground-truth.json"), frozenTruthBytes);
            var results = new List<CaseResult>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in options.Cases)
            {
                var bytes = await File.ReadAllBytesAsync(path);
                if (bytes.Length > 32 * 1024 * 1024)
                    throw new InvalidOperationException("Case exceeds the 32 MiB input bound.");
                using var doc = JsonDocument.Parse(bytes);
                var data = doc.RootElement;
                var id = data.GetProperty("id").GetString() ?? throw new InvalidOperationException("Case ID required.");
                if (!Regex.IsMatch(id, "^[A-Za-z0-9_.-]+$") || id is "." or ".." || !ids.Add(id))
                    throw new InvalidOperationException("Case IDs must be distinct safe directory names.");
                var directory = Path.Combine(options.Output, id);
                Directory.CreateDirectory(directory);
                var handler = new ReplayHandler(data.GetProperty("routes"));
                var clock = Stopwatch.StartNew();
                var supplied = data.TryGetProperty("credentialSupplied", out var credential) && credential.ValueKind == JsonValueKind.True;
                await File.WriteAllBytesAsync(Path.Combine(directory, "input.json"), bytes);
                var result = new CaseResult(id, id + "/input.json", Hash(bytes));
                try
                {
                    var client = CreateProductionClient(handler, supplied);
                    var ctor = typeof(GitHubBlockersBrick).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                        .Single(c => c.GetParameters().Length == 4);
                    var brick = (GitHubBlockersBrick)ctor.Invoke(new object?[]
                    {
                        NullLogger<GitHubBlockersBrick>.Instance,
                        new Func<string?, IGitHubClient>(_ => client),
                        new Func<TimeSpan, Task>(_ => Task.CompletedTask),
                        null,
                    });
                    var input = new BrickInput();
                    input.Set("url", data.GetProperty("target").GetString() ?? throw new InvalidOperationException("Target URL required."));
                    if (supplied)
                        input.Set("githubToken", PlaceholderCredential);
                    if (data.TryGetProperty("collectionCheckName", out var checkName) && checkName.ValueKind == JsonValueKind.String)
                        input.Set("collectionCheckName", checkName.GetString()!);
                    var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, new EvaluationContext());
                    if (handler.Errors.Count != 0)
                        throw new ReplayFailure(string.Join("; ", handler.Errors));
                    var payload = new { brickId = brick.Id, brickVersion = brick.Version, summary = output.Summary, outputs = output.ToDictionary() };
                    await WriteJsonAsync(directory, "payload.json", payload);
                    result.PayloadPath = id + "/payload.json";
                    var evidence = output.Get<JsonElement>("evidencePack");
                    if (evidence.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException("The PR collector did not return an object evidence pack.");
                    await WriteJsonAsync(directory, "release-evidence.json", evidence);
                    await File.WriteAllTextAsync(Path.Combine(directory, "release-evidence.md"), output.Get<string>("evidenceMarkdown"));
                    result.EvidencePath = id + "/release-evidence.json";
                    result.MarkdownPath = id + "/release-evidence.md";
                    result.Status = "answered";
                    result.ExportStatus = "success";
                }
                catch (Exception error)
                {
                    var actual = Unwrap(error);
                    result.Status = handler.Errors.Count > 0 || actual is ReplayFailure ? "harness-error" : "collector-error";
                    result.ExportStatus = result.PayloadPath is null ? "not-attempted" : "error";
                    result.ErrorType = actual.GetType().FullName;
                    result.ErrorMessage = actual.Message.Replace(PlaceholderCredential, "[redacted]", StringComparison.Ordinal);
                    await WriteJsonAsync(directory, "error.json", new { type = result.ErrorType, message = result.ErrorMessage });
                    result.ErrorPath = id + "/error.json";
                }
                clock.Stop();
                result.ReplayElapsedMilliseconds = clock.Elapsed.TotalMilliseconds;
                result.RequestCount = handler.Transcript.Count;
                result.UnusedRoutes = handler.UnusedRoutes;
                result.RouteCoverage = handler.RouteCoverage;
                await WriteJsonAsync(directory, "transcript.json", handler.Transcript);
                result.TranscriptPath = id + "/transcript.json";
                foreach (var relative in new[] { result.EvidencePath, result.MarkdownPath, result.PayloadPath, result.TranscriptPath, result.ErrorPath }.OfType<string>())
                    result.ArtifactSha256[relative] = Hash(await File.ReadAllBytesAsync(Path.Combine(options.Output, relative)));
                result.EvidenceSha256 = ArtifactHash(result.EvidencePath);
                result.MarkdownSha256 = ArtifactHash(result.MarkdownPath);
                result.PayloadSha256 = ArtifactHash(result.PayloadPath);
                result.TranscriptSha256 = ArtifactHash(result.TranscriptPath);
                result.ErrorSha256 = ArtifactHash(result.ErrorPath);
                string? ArtifactHash(string? relative) => relative is null ? null : result.ArtifactSha256[relative];
                results.Add(result);
                Console.WriteLine($"{id}: {result.Status}, requests={result.RequestCount}");
            }
            await WriteJsonAsync(options.Output, "run-manifest.json", new
            {
                schemaVersion = "ashlar.release-evidence-evaluation-run/1",
                candidateCommit = options.CandidateCommit,
                baselineCandidateCommit = baseline,
                candidateMode = string.Equals(baseline, options.CandidateCommit, StringComparison.OrdinalIgnoreCase) ? "baseline" : "repaired-rerun",
                brickAssemblyVersion = assemblyVersion,
                mode = "offline-http-replay",
                startedAt = started,
                completedAt = DateTimeOffset.UtcNow,
                mergeabilityDelay = "elided; request sequence preserved; replay timing is not API latency",
                fixtureManifestSha256 = Hash(frozenManifestBytes),
                fixtureManifestPath = "frozen-fixture-manifest.json",
                groundTruthSha256 = Hash(frozenTruthBytes),
                groundTruthPath = "frozen-ground-truth.json",
                cases = results,
            });
            return results.Any(r => r.Status != "answered") ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Evaluation setup refused: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static IGitHubClient CreateProductionClient(ReplayHandler handler, bool supplied)
    {
        // Reflect only the existing internal transport-injection seam. Production code is unchanged;
        // the real factory (including its egress wrapper), Octokit deserializer and brick execute.
        var factory = typeof(GitHubBlockersBrick).Assembly.GetType("Ashlar.Bricks.GitHub.GitHubClientFactory", throwOnError: true)!;
        var create = factory.GetMethod("Create", BindingFlags.Static | BindingFlags.Public)!;
        return (IGitHubClient)create.Invoke(null, new object?[]
        {
            supplied ? PlaceholderCredential : null,
            new Func<HttpMessageHandler>(() => handler),
            null,
        })!;
    }

    private static async Task<int> TransportProbeAsync()
    {
        using var json = JsonDocument.Parse("""
        [{"method":"GET","path":"/repos/probe/repo/pulls/1/reviews","responses":[{"status":200,"body":[]}]},
         {"method":"GET","path":"/repos/probe/repo/commits/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/check-runs","responses":[{"status":200,"body":{"total_count":0,"check_runs":[]}}]},
         {"method":"GET","path":"/repos/probe/repo/commits/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/status","responses":[{"status":200,"body":{"state":"success","total_count":0,"statuses":[]}}]}]
        """);
        var handler = new ReplayHandler(json.RootElement);
        var client = CreateProductionClient(handler, false);
        await client.PullRequest.Review.GetAll("probe", "repo", 1);
        await client.Check.Run.GetAllForReference("probe", "repo", new string('a', 40));
        await client.Repository.Status.GetCombined("probe", "repo", new string('a', 40));
        foreach (var item in handler.Transcript)
            Console.WriteLine(item.Method + " " + item.Path);
        return 0;
    }

    private static async Task<int> TransportSelfTestAsync()
    {
        using var routes = JsonDocument.Parse("""
        [{"method":"GET","path":"/sequence","responses":[{"status":200,"body":{"value":1}},{"status":200,"body":{"value":2}}]},
         {"method":"GET","path":"/pages?page=2","responses":[{"status":200,"body":{"page":2}}]},
         {"method":"GET","path":"/pages","responses":[{"status":200,"body":{"page":1}}]},
         {"method":"GET","path":"/timeout","responses":[{"status":0,"fault":"timeout"}]},
         {"method":"GET","path":"/not-requested","responses":[{"status":200,"body":{}}]}]
        """);
        using var handler = new ReplayHandler(routes.RootElement);
        var assertions = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Transport self-test failed: " + message);
            assertions++;
        }
        async Task<JsonElement> Get(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com" + path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "must-not-be-retained");
            using var response = await handler.DispatchRecordedAsync(request, CancellationToken.None);
            using var parsed = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return parsed.RootElement.Clone();
        }
        Check((await Get("/sequence")).GetProperty("value").GetInt32() == 1, "first response");
        Check((await Get("/sequence")).GetProperty("value").GetInt32() == 2, "second response");
        Check((await Get("/sequence")).GetProperty("value").GetInt32() == 2, "last response repeats");
        Check((await Get("/pages?page=2")).GetProperty("page").GetInt32() == 2, "exact query route wins");
        Check((await Get("/pages?per_page=100")).GetProperty("page").GetInt32() == 1, "documented bare-path fallback");
        var timedOut = false;
        try { await Get("/timeout"); } catch (TaskCanceledException) { timedOut = true; }
        Check(timedOut, "recorded timeout is a timeout, not HTTP success");
        var unmatched = false;
        try { await Get("/missing"); } catch (ReplayFailure) { unmatched = true; }
        Check(unmatched && handler.Errors.Count == 1, "unmatched endpoint fails loudly");
        var foreign = false;
        try { await handler.DispatchRecordedAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/"), CancellationToken.None); }
        catch (ReplayFailure) { foreign = true; }
        Check(foreign && handler.Errors.Count == 2, "foreign host cannot fall through to network");
        Check(handler.UnusedRoutes.Contains("GET /not-requested"), "unused sources remain visible");
        Check(!JsonSerializer.Serialize(handler.Transcript).Contains("must-not-be-retained", StringComparison.Ordinal), "authorization value omitted");
        Console.WriteLine($"Replay transport self-test: {assertions} passed, 0 failed; no candidate decisions executed.");
        return 0;
    }

    private static Exception Unwrap(Exception error) => error is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : error;
    private static async Task WriteJsonAsync(string directory, string name, object value) =>
        await File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, JsonOptions) + "\n");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string ManifestCandidate(JsonElement manifest)
    {
        var names = new[] { "candidate_commit", "candidateCommit", "baselineCommit" };
        var values = names.Where(name => manifest.TryGetProperty(name, out _))
            .Select(name => manifest.GetProperty(name).GetString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (values.Count != 1 || values[0] is not { } value || !Regex.IsMatch(value, "^[a-fA-F0-9]{40}$"))
            throw new InvalidOperationException("Frozen manifest must identify exactly one full baseline candidate commit.");
        return value;
    }

    private sealed class EvaluationContext : IExecutionContext
    {
        public string AgentId => "evaluation-replay-unauthenticated";
        public string BehaviorId => "github.blockers";
        public bool IsAirGapped => true;
        public bool AuditMode => true;
        public string Provider => "none";
        public IReadOnlyDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
    }

    private sealed record Options(string Output, string CandidateCommit, IReadOnlyList<string> Cases, string? FixtureManifest, string? GroundTruth, bool AllowCandidateRerun)
    {
        public static Options Parse(string[] args)
        {
            string? output = null, candidate = null, fixture = null, truth = null;
            var allowRerun = false;
            var cases = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--allow-candidate-rerun")
                {
                    allowRerun = true;
                    continue;
                }
                if (i + 1 >= args.Length)
                    throw new ArgumentException("All options require a value.");
                var value = args[++i];
                switch (args[i - 1])
                {
                    case "--output": output = Path.GetFullPath(value); break;
                    case "--candidate-commit": candidate = value; break;
                    case "--case": cases.Add(Path.GetFullPath(value)); break;
                    case "--fixture-manifest": fixture = Path.GetFullPath(value); break;
                    case "--ground-truth": truth = Path.GetFullPath(value); break;
                    default: throw new ArgumentException("Unknown option: " + args[i - 1]);
                }
            }
            if (output is null || candidate is null || !Regex.IsMatch(candidate, "^[a-fA-F0-9]{40}$") || cases.Count == 0 || fixture is null || truth is null)
                throw new ArgumentException("Require --output, full --candidate-commit, --fixture-manifest, --ground-truth and at least one --case.");
            return new Options(output, candidate, cases, fixture, truth, allowRerun);
        }
    }

    private sealed class CaseResult(string caseId, string datasetPath, string datasetSha256)
    {
        public string CaseId { get; } = caseId;
        public string DatasetPath { get; } = datasetPath;
        public string DatasetSha256 { get; } = datasetSha256;
        public string Status { get; set; } = "harness-error";
        public string ExportStatus { get; set; } = "not-attempted";
        public string? EvidencePath { get; set; }
        public string? MarkdownPath { get; set; }
        public string? PayloadPath { get; set; }
        public string? TranscriptPath { get; set; }
        public string? ErrorPath { get; set; }
        public string? EvidenceSha256 { get; set; }
        public string? MarkdownSha256 { get; set; }
        public string? PayloadSha256 { get; set; }
        public string? TranscriptSha256 { get; set; }
        public string? ErrorSha256 { get; set; }
        public string? ErrorType { get; set; }
        public string? ErrorMessage { get; set; }
        public double ReplayElapsedMilliseconds { get; set; }
        public int RequestCount { get; set; }
        public IReadOnlyList<string> UnusedRoutes { get; set; } = [];
        public object? RouteCoverage { get; set; }
        public Dictionary<string, string> ArtifactSha256 { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ReplayFailure(string message) : InvalidOperationException(message);

    private sealed class ReplayHandler : HttpMessageHandler
    {
        private readonly List<Route> _routes;
        public List<TranscriptItem> Transcript { get; } = [];
        public List<string> Errors { get; } = [];
        public IReadOnlyList<string> UnusedRoutes => _routes.Where(r => r.Count == 0).Select(r => r.Method + " " + r.Path).ToList();
        public object RouteCoverage => _routes.Select(r => new
        {
            method = r.Method, path = r.Path, requests = r.Count,
            recordedResponses = r.Responses.Length,
            unconsumedResponseIndices = Enumerable.Range(Math.Min(r.Count, r.Responses.Length), Math.Max(0, r.Responses.Length - r.Count)).ToArray(),
            lastResponseReused = Math.Max(0, r.Count - r.Responses.Length),
        }).ToList();

        public ReplayHandler(JsonElement routes)
        {
            _routes = routes.EnumerateArray().Select(r => new Route(
                r.GetProperty("method").GetString()!.ToUpperInvariant(),
                r.GetProperty("path").GetString()!,
                r.GetProperty("responses").EnumerateArray().Select(v => v.Clone()).ToArray())).ToList();
            if (_routes.Any(r => r.Responses.Length == 0) || _routes.GroupBy(r => r.Method + " " + r.Path).Any(g => g.Count() > 1))
                throw new ArgumentException("Each distinct route needs at least one response.");
        }

        // The self-test calls the same in-memory dispatcher directly; it needs no HTTP client
        // primitive. Production replay still enters through the original guarded client factory.
        public Task<HttpResponseMessage> DispatchRecordedAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new ReplayFailure("Request had no URI.");
            var method = request.Method.Method;
            var path = uri.PathAndQuery;
            if (uri.Scheme != "https" || uri.Host != "api.github.com" || Transcript.Count >= 256)
                return Refuse("Request outside fixed api.github.com replay or request bound exceeded.");
            var route = _routes.FirstOrDefault(r => r.Method == method && r.Path == path)
                ?? _routes.FirstOrDefault(r => r.Method == method && !r.Path.Contains('?') && r.Path == uri.AbsolutePath);
            if (route is null)
                return Refuse("No recorded route for " + method + " " + path);
            var index = Math.Min(route.Count++, route.Responses.Length - 1);
            var response = route.Responses[index];
            var requestBody = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var body = response.TryGetProperty("rawBody", out var raw) ? raw.GetString() ?? ""
                : response.TryGetProperty("body", out var value) ? value.GetRawText() : "";
            var fault = response.TryGetProperty("fault", out var f) ? f.GetString() : null;
            var status = response.TryGetProperty("status", out var s) ? s.GetInt32() : 200;
            Transcript.Add(new TranscriptItem(Transcript.Count + 1, method, path, route.Path, index,
                request.Headers.Authorization is not null, requestBody is null ? null : Hash(requestBody), status, fault,
                Hash(Encoding.UTF8.GetBytes(body))));
            if (fault == "timeout")
                throw new TaskCanceledException("Recorded timeout in offline evaluation fixture.");
            if (fault is not null)
                return Refuse("Unsupported recorded transport fault: " + fault);
            var message = new HttpResponseMessage((HttpStatusCode)status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (response.TryGetProperty("headers", out var headers))
            {
                foreach (var header in headers.EnumerateObject())
                {
                    var text = header.Value.ValueKind == JsonValueKind.String ? header.Value.GetString()! : header.Value.GetRawText();
                    if (!message.Headers.TryAddWithoutValidation(header.Name, text))
                        message.Content.Headers.TryAddWithoutValidation(header.Name, text);
                }
            }
            return message;
        }

        private HttpResponseMessage Refuse(string message)
        {
            Errors.Add(message);
            throw new ReplayFailure(message);
        }

        private sealed class Route(string method, string path, JsonElement[] responses)
        {
            public string Method { get; } = method;
            public string Path { get; } = path;
            public JsonElement[] Responses { get; } = responses;
            public int Count { get; set; }
        }
    }

    private sealed record TranscriptItem(int Sequence, string Method, string Path, string MatchedRoute, int ResponseIndex,
        bool RequestAuthorizationPresent, string? RequestBodySha256, int Status, string? Fault, string ResponseBodySha256);
}
