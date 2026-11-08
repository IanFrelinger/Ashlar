using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ashlar.Abstractions.Security.Egress;
using JobSearch.Store;

#pragma warning disable ASHLAREXP002 // IEgressGuard, EgressHttp and EgressFamilies are the experimental egress surface; every Google call goes through the guard by design.

namespace JobSearch.Workspace;

/// <summary>
/// Secrets (OAuth tokens), kept out of the database: one JSON file readable only by the service account
/// (mode 0600 on Unix), keyed by token reference. Never logged, exported or snapshotted.
/// </summary>
public sealed class SecretStore(string path)
{
    private readonly object _lock = new();

    /// <summary>The secret, or null.</summary>
    public Dictionary<string, string>? Get(string reference)
    {
        lock (_lock) return Load().GetValueOrDefault(reference);
    }

    /// <summary>Stores a secret.</summary>
    public void Put(string reference, Dictionary<string, string> secret)
    {
        lock (_lock)
        {
            var all = Load();
            all[reference] = secret;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, path, overwrite: true);
        }
    }

    private Dictionary<string, Dictionary<string, string>> Load() =>
        File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path)) ?? [] : [];
}

/// <summary>
/// Google Drive v3 + Sheets v4 over HTTPS with one user's OAuth token (scope <c>drive.file</c>: the app sees only files
/// it created or the user opened with it). Every request goes through Ashlar's egress guard (<c>EgressHttp</c>), so a
/// deployment profile that refuses external egress refuses these calls too. Not yet run against Google from this
/// repository: see the app README, "What is verified".
/// </summary>
public sealed class GoogleRestWorkspace : IGoogleWorkspace, IDisposable
{
    /// <summary>The egress site id this client reports to the guard.</summary>
    public const string EgressSite = "jobsearch.google";
    private const string Drive = "https://www.googleapis.com/drive/v3";
    private const string Upload = "https://www.googleapis.com/upload/drive/v3";
    private const string Sheets = "https://sheets.googleapis.com/v4/spreadsheets";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string DocMime = "application/vnd.google-apps.document";
    private const string SheetMime = "application/vnd.google-apps.spreadsheet";

    private readonly HttpClient _http;
    private readonly SecretStore _secrets;
    private readonly string _tokenRef;
    private readonly string _clientId;
    private readonly string _clientSecret;

    /// <summary>Creates the client for one user's token.</summary>
    public GoogleRestWorkspace(SecretStore secrets, string tokenRef, string clientId, string clientSecret, IEgressGuard? guard = null)
    {
        _secrets = secrets;
        _tokenRef = tokenRef;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _http = EgressHttp.CreateClient(EgressFamilies.Http, EgressSite, guard);
    }

    private string AccessToken() =>
        _secrets.Get(_tokenRef)?.GetValueOrDefault("access_token") ?? throw new AuthExpiredException("google", "no stored credential; reconnect Google Drive");

    private async Task RefreshAsync(CancellationToken ct)
    {
        var secret = _secrets.Get(_tokenRef) ?? throw new AuthExpiredException("google");
        if (!secret.TryGetValue("refresh_token", out var refresh))
            throw new AuthExpiredException("google", "no refresh token; reconnect Google Drive");
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId, ["client_secret"] = _clientSecret, ["refresh_token"] = refresh, ["grant_type"] = "refresh_token",
        });
        using var resp = await _http.PostAsync(new Uri(TokenUrl), body, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            throw new AuthExpiredException("google", "refresh token rejected (invalid_grant); reconnect Google Drive");
        if (!resp.IsSuccessStatusCode)
            throw new ProviderException("google", $"token refresh HTTP {(int)resp.StatusCode}");
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))!;
        secret["access_token"] = json["access_token"]!.GetValue<string>();
        _secrets.Put(_tokenRef, secret);
    }

    private async Task<byte[]> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct, bool retried = false)
    {
        using var req = new HttpRequestMessage(method, new Uri(url)) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken());
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
            return bytes;
        var detail = Encoding.UTF8.GetString(bytes);
        if (resp.StatusCode == HttpStatusCode.Unauthorized && !retried && content is null or StringContent)
        {
            await RefreshAsync(ct).ConfigureAwait(false);
            var again = content is StringContent sc ? new StringContent(await sc.ReadAsStringAsync(ct).ConfigureAwait(false), Encoding.UTF8, sc.Headers.ContentType?.MediaType ?? "application/json") : null;
            return await SendAsync(method, url, again, ct, retried: true).ConfigureAwait(false);
        }
        throw resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new AuthExpiredException("google"),
            HttpStatusCode.TooManyRequests => new RateLimitedException("google", resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5)),
            HttpStatusCode.Forbidden when detail.Contains("ateLimitExceeded", StringComparison.Ordinal) => new RateLimitedException("google", TimeSpan.FromSeconds(5)),
            HttpStatusCode.NotFound => new NotFoundException("google", url),
            _ => new ProviderException("google", $"HTTP {(int)resp.StatusCode}: {(detail.Length > 300 ? detail[..300] : detail)}", (int)resp.StatusCode >= 500),
        };
    }

    private async Task<JsonNode> JsonAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var content = body is null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var bytes = await SendAsync(method, url, content, ct).ConfigureAwait(false);
        return bytes.Length == 0 ? new JsonObject() : JsonNode.Parse(bytes)!;
    }

    private async Task<string> UploadAsync(object metadata, byte[] data, string mime, CancellationToken ct)
    {
        var boundary = "js" + Guid.NewGuid().ToString("N");
        var head = Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{JsonSerializer.Serialize(metadata)}\r\n--{boundary}\r\nContent-Type: {mime}\r\n\r\n");
        var tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--");
        var content = new ByteArrayContent([.. head, .. data, .. tail]);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/related; boundary={boundary}");
        var bytes = await SendAsync(HttpMethod.Post, $"{Upload}/files?uploadType=multipart&fields=id", content, ct).ConfigureAwait(false);
        return JsonNode.Parse(bytes)!["id"]!.GetValue<string>();
    }

    /// <inheritdoc />
    public async Task<string> AccountEmailAsync(CancellationToken ct = default) =>
        (await JsonAsync(HttpMethod.Get, $"{Drive}/about?fields=user(emailAddress)", null, ct).ConfigureAwait(false))["user"]!["emailAddress"]!.GetValue<string>();

    /// <inheritdoc />
    public async Task<string?> FindByAppKeyAsync(string appKey, CancellationToken ct = default)
    {
        var q = Uri.EscapeDataString($"appProperties has {{ key='jt_key' and value='{appKey.Replace("'", "\\'", StringComparison.Ordinal)}' }} and trashed=false");
        var res = await JsonAsync(HttpMethod.Get, $"{Drive}/files?q={q}&fields=files(id)&pageSize=2", null, ct).ConfigureAwait(false);
        return res["files"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
    }

    /// <inheritdoc />
    public async Task<string> EnsureFolderAsync(string name, string? parentId, string appKey, CancellationToken ct = default) =>
        await FindByAppKeyAsync(appKey, ct).ConfigureAwait(false)
        ?? (await JsonAsync(HttpMethod.Post, $"{Drive}/files?fields=id", new Dictionary<string, object?>
        {
            ["name"] = name, ["mimeType"] = FolderMime, ["appProperties"] = new { jt_key = appKey },
            ["parents"] = parentId is null ? null : new[] { parentId },
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value), ct).ConfigureAwait(false))["id"]!.GetValue<string>();

    /// <inheritdoc />
    public async Task<string> CreateDocAsync(string name, string parentId, string html, string appKey, CancellationToken ct = default) =>
        await FindByAppKeyAsync(appKey, ct).ConfigureAwait(false)
        ?? await UploadAsync(new { name, parents = new[] { parentId }, mimeType = DocMime, appProperties = new { jt_key = appKey } },
            Encoding.UTF8.GetBytes(html), "text/html", ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<string> CreateFileAsync(string name, string parentId, byte[] data, string mime, string appKey, CancellationToken ct = default) =>
        await FindByAppKeyAsync(appKey, ct).ConfigureAwait(false)
        ?? await UploadAsync(new { name, parents = new[] { parentId }, appProperties = new { jt_key = appKey } }, data, mime, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<string> ReadTextAsync(string fileId, CancellationToken ct = default)
    {
        var meta = await MetaAsync(fileId, ct).ConfigureAwait(false);
        if (meta.Mime == DocMime)
            return Encoding.UTF8.GetString(await SendAsync(HttpMethod.Get, $"{Drive}/files/{fileId}/export?mimeType=text/plain", null, ct).ConfigureAwait(false));
        var data = await SendAsync(HttpMethod.Get, $"{Drive}/files/{fileId}?alt=media", null, ct).ConfigureAwait(false);
        if (meta.Mime == "application/pdf")
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(data);
            return string.Join("\n", doc.GetPages().Select(p => p.Text));
        }
        return Encoding.UTF8.GetString(data);
    }

    /// <inheritdoc />
    public async Task UpdateFileAsync(string fileId, byte[] data, string mime, CancellationToken ct = default)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        await SendAsync(HttpMethod.Patch, $"{Upload}/files/{fileId}?uploadType=media&fields=id", content, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FileMeta> MetaAsync(string fileId, CancellationToken ct = default)
    {
        var r = await JsonAsync(HttpMethod.Get, $"{Drive}/files/{fileId}?fields=id,name,parents,version,modifiedTime,mimeType", null, ct).ConfigureAwait(false);
        return new FileMeta(r["id"]!.GetValue<string>(), r["name"]!.GetValue<string>(),
            r["parents"]?.AsArray().Select(p => p!.GetValue<string>()).ToList() ?? [],
            long.Parse(r["version"]?.GetValue<string>() ?? "0", System.Globalization.CultureInfo.InvariantCulture),
            r["modifiedTime"]?.GetValue<string>(), r["mimeType"]!.GetValue<string>());
    }

    /// <inheritdoc />
    public async Task MoveAsync(string fileId, string newParentId, CancellationToken ct = default)
    {
        var old = string.Join(",", (await MetaAsync(fileId, ct).ConfigureAwait(false)).Parents);
        await JsonAsync(HttpMethod.Patch, $"{Drive}/files/{fileId}?addParents={newParentId}&removeParents={old}&fields=id", new { }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> CopyAsync(string fileId, string name, string parentId, string appKey, CancellationToken ct = default) =>
        await FindByAppKeyAsync(appKey, ct).ConfigureAwait(false)
        ?? (await JsonAsync(HttpMethod.Post, $"{Drive}/files/{fileId}/copy?fields=id",
            new { name, parents = new[] { parentId }, appProperties = new { jt_key = appKey } }, ct).ConfigureAwait(false))["id"]!.GetValue<string>();

    /// <inheritdoc />
    public async Task<string?> KeepRevisionForeverAsync(string fileId, CancellationToken ct = default)
    {
        var revs = (await JsonAsync(HttpMethod.Get, $"{Drive}/files/{fileId}/revisions?fields=revisions(id)", null, ct).ConfigureAwait(false))["revisions"]?.AsArray();
        if (revs is null || revs.Count == 0)
            return null; // Google-native files do not support keepForever; the frozen copy is the record
        var id = revs[^1]!["id"]!.GetValue<string>();
        await JsonAsync(HttpMethod.Patch, $"{Drive}/files/{fileId}/revisions/{id}", new { keepForever = true }, ct).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc />
    public async Task<string> CreateSheetAsync(string name, string parentId, IReadOnlyList<string> headers, string appKey, CancellationToken ct = default)
    {
        if (await FindByAppKeyAsync(appKey, ct).ConfigureAwait(false) is { } found)
            return found;
        var id = (await JsonAsync(HttpMethod.Post, $"{Drive}/files?fields=id",
            new { name, mimeType = SheetMime, parents = new[] { parentId }, appProperties = new { jt_key = appKey } }, ct).ConfigureAwait(false))["id"]!.GetValue<string>();
        await SetHeadersAsync(id, headers, ct).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc />
    public async Task<List<List<string>>> ReadRowsAsync(string sheetId, CancellationToken ct = default)
    {
        var r = await JsonAsync(HttpMethod.Get, $"{Sheets}/{sheetId}/values/A1:ZZ", null, ct).ConfigureAwait(false);
        return r["values"]?.AsArray().Select(row => row!.AsArray().Select(c => c?.ToString() ?? "").ToList()).ToList() ?? [];
    }

    /// <inheritdoc />
    public async Task UpdateCellsAsync(string sheetId, IReadOnlyList<(int Row, string Column, string Value)> cells, CancellationToken ct = default)
    {
        if (cells.Count == 0) return;
        var headers = (await ReadRowsAsync(sheetId, ct).ConfigureAwait(false))[0];
        var data = cells.Select(c => new { range = $"{Col(headers.IndexOf(c.Column))}{c.Row + 1}", values = new[] { new[] { c.Value } } }).ToList();
        await JsonAsync(HttpMethod.Post, $"{Sheets}/{sheetId}/values:batchUpdate", new { valueInputOption = "RAW", data }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task AppendRowsAsync(string sheetId, IReadOnlyList<IReadOnlyList<string>> rows, CancellationToken ct = default) =>
        JsonAsync(HttpMethod.Post, $"{Sheets}/{sheetId}/values/A1:append?valueInputOption=RAW&insertDataOption=INSERT_ROWS", new { values = rows }, ct);

    /// <inheritdoc />
    public Task SetHeadersAsync(string sheetId, IReadOnlyList<string> headers, CancellationToken ct = default) =>
        JsonAsync(HttpMethod.Put, $"{Sheets}/{sheetId}/values/A1?valueInputOption=RAW", new { values = new[] { headers } }, ct);

    private static string Col(int index)
    {
        var s = "";
        for (index++; index > 0; index = (index - 1) / 26)
            s = (char)('A' + (index - 1) % 26) + s;
        return s;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}

/// <summary>Resolves each user's client from that user's own connection: fake accounts or Google.</summary>
public sealed class WorkspaceFactory(Accounts.AccountService accounts, Func<Accounts.Connection, IGoogleWorkspace> build) : IWorkspaceFactory
{
    /// <inheritdoc />
    public IGoogleWorkspace ForUser(string userId)
    {
        var conn = accounts.GetConnection(userId, "google") ?? throw new AuthExpiredException("google", "Google Drive is not connected for this user");
        if (conn.Status != "active")
            throw new AuthExpiredException("google", $"Google Drive connection is {conn.Status}: {conn.StatusDetail}; reconnect");
        return build(conn);
    }
}
