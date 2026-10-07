using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Commercial.MeshDirector;

/// <summary>
/// Phase 7: HTTP client for the mesh director API from headless CLI hosts (no in-process Ashlar.API).
/// </summary>
public sealed class MeshDirectorCommand : Command
{
    internal const string DirectorBaseUrlEnv = "ASHLAR_MESH_DIRECTOR_BASE_URL";
    internal const string MeshApiKeyEnv = "ASHLAR_MESH_API_KEY";
    internal const string MeshMutatingTokenEnv = "ASHLAR_MESH_MUTATING_TOKEN";
    internal const string MeshPeerRegistrationKeyEnv = "ASHLAR_MESH_PEER_REGISTRATION_KEY";

    private const string DefaultApiKeyHeader = "X-Ashlar-Api-Key";
    private const string DefaultMeshTokenHeader = "X-Ashlar-Mesh-Token";

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public MeshDirectorCommand() : base("director", "Call mesh director HTTP API (Phase 7 — headless / edge workers).")
    {
        var baseUrlOpt = new Option<string?>(
            "--base-url",
            () => null,
            $"Director base URL (e.g. https://hub.example:8080). Defaults to {DirectorBaseUrlEnv}.");

        var apiKeyOpt = new Option<string?>(
            "--api-key",
            () => null,
            $"Optional {DefaultApiKeyHeader} (defaults to {MeshApiKeyEnv}).");

        var meshTokenOpt = new Option<string?>(
            "--mesh-token",
            () => null,
            $"Optional {DefaultMeshTokenHeader} for mutating /api/mesh (defaults to {MeshMutatingTokenEnv}).");

        var timeoutOpt = new Option<int>("--timeout-seconds", () => 120, "HTTP timeout");
        var jsonOpt = new Option<bool>("--json", () => false, "Pretty-print JSON response bodies when possible");

        var pathArg = new Argument<string>("path", "Request path from server root, e.g. /api/mesh/fleet/nodes");

        var getCmd = new Command("get", "GET from director");
        getCmd.Add(baseUrlOpt);
        getCmd.Add(apiKeyOpt);
        getCmd.Add(meshTokenOpt);
        getCmd.Add(timeoutOpt);
        getCmd.Add(jsonOpt);
        getCmd.AddArgument(pathArg);
        getCmd.SetHandler(InvokeGetAsync, baseUrlOpt, apiKeyOpt, meshTokenOpt, timeoutOpt, jsonOpt, pathArg);

        var pathPostArg = new Argument<string>("path", "Request path, e.g. /api/mesh/fleet/nodes");
        var bodyFileOpt = new Option<string?>("--body-file", "UTF-8 JSON body file");
        var bodyOpt = new Option<string?>("--body", "Inline JSON body");

        var postCmd = new Command("post", "POST to director");
        postCmd.Add(baseUrlOpt);
        postCmd.Add(apiKeyOpt);
        postCmd.Add(meshTokenOpt);
        postCmd.Add(timeoutOpt);
        postCmd.Add(jsonOpt);
        postCmd.AddArgument(pathPostArg);
        postCmd.Add(bodyFileOpt);
        postCmd.Add(bodyOpt);
        postCmd.SetHandler(InvokePostAsync, baseUrlOpt, apiKeyOpt, meshTokenOpt, timeoutOpt, jsonOpt, pathPostArg, bodyFileOpt, bodyOpt);

        var pathPatchArg = new Argument<string>("path", "Request path, e.g. /api/mesh/tasks/{id}/status");
        var patchCmd = new Command("patch", "PATCH on director");
        patchCmd.Add(baseUrlOpt);
        patchCmd.Add(apiKeyOpt);
        patchCmd.Add(meshTokenOpt);
        patchCmd.Add(timeoutOpt);
        patchCmd.Add(jsonOpt);
        patchCmd.AddArgument(pathPatchArg);
        patchCmd.Add(bodyFileOpt);
        patchCmd.Add(bodyOpt);
        patchCmd.SetHandler(InvokePatchAsync, baseUrlOpt, apiKeyOpt, meshTokenOpt, timeoutOpt, jsonOpt, pathPatchArg, bodyFileOpt, bodyOpt);

        var peerIdArg = new Argument<string>("peerId", "Fleet peer id");
        var apiBaseOpt = new Option<string>("--api-base-url", "Worker API base URL (http/https)") { IsRequired = true };
        var trustTierOpt = new Option<string>("--trust-tier", () => "Trusted", "Trusted or Untrusted");
        var peerRegKeyOpt = new Option<string?>(
            "--peer-registration-key",
            () => null,
            $"Per-peer registration secret (defaults to {MeshPeerRegistrationKeyEnv}).");

        var registerCmd = new Command("register", "POST /api/mesh/fleet/nodes (fleet register)");
        registerCmd.Add(baseUrlOpt);
        registerCmd.Add(apiKeyOpt);
        registerCmd.Add(meshTokenOpt);
        registerCmd.Add(timeoutOpt);
        registerCmd.Add(jsonOpt);
        registerCmd.Add(peerIdArg);
        registerCmd.Add(apiBaseOpt);
        registerCmd.Add(trustTierOpt);
        registerCmd.Add(peerRegKeyOpt);
        registerCmd.SetHandler(async (InvocationContext ctx) =>
        {
            await InvokeRegisterAsync(
                ctx.ParseResult.GetValueForOption(baseUrlOpt),
                ctx.ParseResult.GetValueForOption(apiKeyOpt),
                ctx.ParseResult.GetValueForOption(meshTokenOpt),
                ctx.ParseResult.GetValueForOption(timeoutOpt),
                ctx.ParseResult.GetValueForOption(jsonOpt),
                ctx.ParseResult.GetValueForArgument(peerIdArg),
                ctx.ParseResult.GetValueForOption(apiBaseOpt)!,
                ctx.ParseResult.GetValueForOption(trustTierOpt)!,
                ctx.ParseResult.GetValueForOption(peerRegKeyOpt)).ConfigureAwait(false);
        });

        var listNodesCmd = new Command("list-nodes", "GET /api/mesh/fleet/nodes (fleet director registry)");
        listNodesCmd.Add(baseUrlOpt);
        listNodesCmd.Add(apiKeyOpt);
        listNodesCmd.Add(meshTokenOpt);
        listNodesCmd.Add(timeoutOpt);
        listNodesCmd.Add(jsonOpt);
        listNodesCmd.SetHandler(
            (string? baseUrl, string? apiKey, string? meshToken, int timeout, bool json) =>
                InvokeGetAsync(baseUrl, apiKey, meshToken, timeout, json, FleetNodesPath),
            baseUrlOpt,
            apiKeyOpt,
            meshTokenOpt,
            timeoutOpt,
            jsonOpt);

        var directorHealthCmd = new Command("health", "GET /health on the fleet director host");
        directorHealthCmd.Add(baseUrlOpt);
        directorHealthCmd.Add(apiKeyOpt);
        directorHealthCmd.Add(timeoutOpt);
        directorHealthCmd.Add(jsonOpt);
        directorHealthCmd.SetHandler(
            (string? baseUrl, string? apiKey, int timeout, bool json) =>
                InvokeGetAsync(baseUrl, apiKey, null, timeout, json, "/health"),
            baseUrlOpt,
            apiKeyOpt,
            timeoutOpt,
            jsonOpt);

        var admitCmd = new Command("admit", "POST /api/mesh/fleet/nodes/{peerId}/admit (fleet director)");
        admitCmd.Add(baseUrlOpt);
        admitCmd.Add(apiKeyOpt);
        admitCmd.Add(meshTokenOpt);
        admitCmd.Add(timeoutOpt);
        admitCmd.Add(jsonOpt);
        admitCmd.AddArgument(peerIdArg);
        admitCmd.SetHandler(InvokeAdmitAsync, baseUrlOpt, apiKeyOpt, meshTokenOpt, timeoutOpt, jsonOpt, peerIdArg);

        var revokeCmd = new Command("revoke", "POST /api/mesh/fleet/nodes/{peerId}/revoke (fleet director)");
        revokeCmd.Add(baseUrlOpt);
        revokeCmd.Add(apiKeyOpt);
        revokeCmd.Add(meshTokenOpt);
        revokeCmd.Add(timeoutOpt);
        revokeCmd.Add(jsonOpt);
        revokeCmd.AddArgument(peerIdArg);
        revokeCmd.SetHandler(InvokeRevokeAsync, baseUrlOpt, apiKeyOpt, meshTokenOpt, timeoutOpt, jsonOpt, peerIdArg);

        AddCommand(getCmd);
        AddCommand(postCmd);
        AddCommand(patchCmd);
        AddCommand(registerCmd);
        AddCommand(listNodesCmd);
        AddCommand(directorHealthCmd);
        AddCommand(admitCmd);
        AddCommand(revokeCmd);
    }

    internal const string FleetNodesPath = "/api/mesh/fleet/nodes";

    internal static string BuildFleetNodePath(string peerId, string action) =>
        $"/api/mesh/fleet/nodes/{Uri.EscapeDataString(peerId)}/{action}";

    private static string? ResolveApiKey(string? fromOption) =>
        string.IsNullOrWhiteSpace(fromOption) ? Environment.GetEnvironmentVariable(MeshApiKeyEnv) : fromOption.Trim();

    private static string? ResolveMeshToken(string? fromOption) =>
        string.IsNullOrWhiteSpace(fromOption) ? Environment.GetEnvironmentVariable(MeshMutatingTokenEnv) : fromOption.Trim();

    private static string? ResolvePeerRegistrationKey(string? fromOption)
    {
        if (!string.IsNullOrWhiteSpace(fromOption))
            return fromOption.Trim();
        var env = Environment.GetEnvironmentVariable(MeshPeerRegistrationKeyEnv);
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    private static string ResolveBaseUrl(string? fromOption)
    {
        var v = string.IsNullOrWhiteSpace(fromOption) ? Environment.GetEnvironmentVariable(DirectorBaseUrlEnv) : fromOption.Trim();
        return string.IsNullOrWhiteSpace(v) ? string.Empty : v.Trim();
    }

    internal static Uri BuildRequestUri(string baseUrl, string path)
    {
        var b = baseUrl.Trim().TrimEnd('/');
        var p = path.Trim();
        if (string.IsNullOrEmpty(p))
            throw new ArgumentException("Path is required.", nameof(path));
        if (!p.StartsWith('/'))
            p = "/" + p;
        return new Uri(b + p, UriKind.Absolute);
    }

    private static void ApplyHeaders(HttpRequestMessage msg, string? apiKey, string? meshToken, HttpMethod method)
    {
        if (!string.IsNullOrEmpty(apiKey))
            msg.Headers.TryAddWithoutValidation(DefaultApiKeyHeader, apiKey);

        if (method != HttpMethod.Get && !string.IsNullOrEmpty(meshToken))
            msg.Headers.TryAddWithoutValidation(DefaultMeshTokenHeader, meshToken);
    }

    private static async Task InvokeGetAsync(
        string? baseUrlOpt,
        string? apiKeyOpt,
        string? meshTokenOpt,
        int timeoutSeconds,
        bool json,
        string path)
    {
        var baseUrl = ResolveBaseUrl(baseUrlOpt);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Console.Error.WriteLine($"Set --base-url or environment variable {DirectorBaseUrlEnv}.");
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            using var client = CreateHttpClient(timeoutSeconds);
            var uri = BuildRequestUri(baseUrl, path);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyHeaders(req, ResolveApiKey(apiKeyOpt), ResolveMeshToken(meshTokenOpt), HttpMethod.Get);
            using var resp = await client.SendAsync(req).ConfigureAwait(false);
            await WriteResponseAsync(resp, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
    }

    private static Task InvokePostAsync(
        string? baseUrlOpt,
        string? apiKeyOpt,
        string? meshTokenOpt,
        int timeoutSeconds,
        bool json,
        string path,
        string? bodyFile,
        string? bodyJson) =>
        SendWithBodyAsync(HttpMethod.Post, baseUrlOpt, apiKeyOpt, meshTokenOpt, timeoutSeconds, json, path, bodyFile, bodyJson);

    private static Task InvokePatchAsync(
        string? baseUrlOpt,
        string? apiKeyOpt,
        string? meshTokenOpt,
        int timeoutSeconds,
        bool json,
        string path,
        string? bodyFile,
        string? bodyJson) =>
        SendWithBodyAsync(HttpMethod.Patch, baseUrlOpt, apiKeyOpt, meshTokenOpt, timeoutSeconds, json, path, bodyFile, bodyJson);

    private static Task InvokeRegisterAsync(
        string? baseUrlOpt,
        string? apiKeyOpt,
        string? meshTokenOpt,
        int timeoutSeconds,
        bool json,
        string peerId,
        string apiBaseUrl,
        string trustTier,
        string? peerRegistrationKeyOpt)
    {
        var body = new Dictionary<string, object?>
        {
            ["peerId"] = peerId.Trim(),
            ["apiBaseUrl"] = apiBaseUrl.Trim(),
            ["trustTier"] = trustTier.Trim(),
        };
        var regKey = ResolvePeerRegistrationKey(peerRegistrationKeyOpt);
        if (!string.IsNullOrEmpty(regKey))
            body["peerRegistrationKey"] = regKey;

        var payload = JsonSerializer.Serialize(body);
        return SendWithBodyAsync(
            HttpMethod.Post,
            baseUrlOpt,
            apiKeyOpt,
            meshTokenOpt,
            timeoutSeconds,
            json,
            "/api/mesh/fleet/nodes",
            bodyFile: null,
            bodyJson: payload);
    }

    private static Task InvokeAdmitAsync(
        string? baseUrlOpt,
        string? apiKeyOpt,
        string? meshTokenOpt,
        int timeoutSeconds,
        bool json,
        string peerId) =>
        SendEmptyPostAsync(baseUrlOpt, apiKeyOpt, meshTokenOpt, timeoutSeconds, json, BuildFleetNodePath(peerId, "admit"));

    private static Task InvokeRevokeAsync(
        string? baseUrlOpt,
        string? apiKeyOpt,
        string? meshTokenOpt,
        int timeoutSeconds,
        bool json,
        string peerId) =>
        SendEmptyPostAsync(baseUrlOpt, apiKeyOpt, meshTokenOpt, timeoutSeconds, json, BuildFleetNodePath(peerId, "revoke"));

    private static async Task SendEmptyPostAsync(
        string? baseUrlOpt,
        string? apiKeyOpt,
        string? meshTokenOpt,
        int timeoutSeconds,
        bool printJson,
        string path)
    {
        var baseUrl = ResolveBaseUrl(baseUrlOpt);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Console.Error.WriteLine($"Set --base-url or environment variable {DirectorBaseUrlEnv}.");
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            using var client = CreateHttpClient(timeoutSeconds);
            var uri = BuildRequestUri(baseUrl, path);
            using var req = new HttpRequestMessage(HttpMethod.Post, uri);
            ApplyHeaders(req, ResolveApiKey(apiKeyOpt), ResolveMeshToken(meshTokenOpt), HttpMethod.Post);
            using var resp = await client.SendAsync(req).ConfigureAwait(false);
            await WriteResponseAsync(resp, printJson).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
    }

    private static async Task SendWithBodyAsync(
        HttpMethod method,
        string? baseUrlOpt,
        string? apiKeyOpt,
        string? meshTokenOpt,
        int timeoutSeconds,
        bool printJson,
        string path,
        string? bodyFile,
        string? bodyJson)
    {
        var baseUrl = ResolveBaseUrl(baseUrlOpt);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Console.Error.WriteLine($"Set --base-url or environment variable {DirectorBaseUrlEnv}.");
            Environment.ExitCode = 1;
            return;
        }

        string payload;
        if (!string.IsNullOrWhiteSpace(bodyFile))
        {
            if (!File.Exists(bodyFile))
            {
                Console.Error.WriteLine($"Body file not found: {bodyFile}");
                Environment.ExitCode = 1;
                return;
            }

            payload = await File.ReadAllTextAsync(bodyFile).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(bodyJson))
            payload = bodyJson;
        else
        {
            Console.Error.WriteLine("Provide --body or --body-file for POST/PATCH.");
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            using var client = CreateHttpClient(timeoutSeconds);
            var uri = BuildRequestUri(baseUrl, path);
            using var req = new HttpRequestMessage(method, uri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            ApplyHeaders(req, ResolveApiKey(apiKeyOpt), ResolveMeshToken(meshTokenOpt), method);
            using var resp = await client.SendAsync(req).ConfigureAwait(false);
            await WriteResponseAsync(resp, printJson).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
    }

    private static HttpClient CreateHttpClient(int timeoutSeconds)
    {
        var s = Math.Clamp(timeoutSeconds, 5, 3600);
        var client = EgressHttp.CreateClient(EgressFamilies.Http, "EG-HTTP-07");
        client.Timeout = TimeSpan.FromSeconds(s);
        return client;
    }

    private static async Task WriteResponseAsync(HttpResponseMessage resp, bool preferFormattedJson)
    {
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        Console.WriteLine($"{(int)resp.StatusCode} {resp.ReasonPhrase}");

        if (string.IsNullOrEmpty(body))
        {
            Environment.ExitCode = resp.IsSuccessStatusCode ? 0 : 1;
            return;
        }

        if (preferFormattedJson &&
            resp.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, PrettyJson));
            }
            catch
            {
                Console.WriteLine(body);
            }
        }
        else
            Console.WriteLine(body);

        Environment.ExitCode = resp.IsSuccessStatusCode ? 0 : 1;
    }
}
