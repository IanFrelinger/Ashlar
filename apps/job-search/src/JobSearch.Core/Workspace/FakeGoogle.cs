using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobSearch.Materials;
using JobSearch.Store;

namespace JobSearch.Workspace;

/// <summary>
/// Several Google accounts in one process, for tests and the demo. It enforces the property the design relies on: a
/// client holds one user's token, the token maps to one account, and another account's file id answers NotFound, as
/// Drive does for a file the caller cannot see. Files keep revisions. Failures can be injected per account.
/// Optionally persisted to a JSON file so the CLI demo keeps "Drive" between invocations.
/// </summary>
public sealed class FakeGoogle
{
    private readonly object _lock = new();
    private readonly string? _path;
    private State _state = new();

    /// <summary>Kinds of file.</summary>
    public const string Folder = "folder", Doc = "doc", Sheet = "sheet", Binary = "binary";

    /// <summary>One file. Content: text for docs, JSON rows for sheets, base64 for binary.</summary>
    public sealed class FakeFile
    {
        /// <summary>Id.</summary>
        public string Id { get; set; } = "";
        /// <summary>Name.</summary>
        public string Name { get; set; } = "";
        /// <summary>Parent folder.</summary>
        public string? Parent { get; set; }
        /// <summary>folder | doc | sheet | binary.</summary>
        public string Kind { get; set; } = "";
        /// <summary>MIME type for binary files.</summary>
        public string Mime { get; set; } = "";
        /// <summary>Content.</summary>
        public string Content { get; set; } = "";
        /// <summary>App key.</summary>
        public string? AppKey { get; set; }
        /// <summary>Version, bumped on every change.</summary>
        public long Version { get; set; } = 1;
        /// <summary>Last change.</summary>
        public string Modified { get; set; } = "";
        /// <summary>Revisions: (id, content, keepForever).</summary>
        public List<Revision> Revisions { get; set; } = [];
    }

    /// <summary>A revision.</summary>
    public sealed record Revision(string Id, string Content, bool KeepForever);

    private sealed class State
    {
        public Dictionary<string, Dictionary<string, FakeFile>> Accounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, TokenInfo> Tokens { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class TokenInfo
    {
        public string Account { get; set; } = "";
        public bool Valid { get; set; } = true;
    }

    private readonly List<(string? Account, string? Method, Exception Error)> _failures = [];

    /// <summary>Every call made: (account, method). For tests.</summary>
    public List<(string Account, string Method)> Calls { get; } = [];

    /// <summary>In-memory only.</summary>
    public FakeGoogle() { }

    /// <summary>Persisted to <paramref name="path"/> after every change.</summary>
    public FakeGoogle(string path)
    {
        _path = path;
        if (File.Exists(path))
            _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
    }

    /// <summary>Adds an account and returns a token for it.</summary>
    public string AddAccount(string email)
    {
        lock (_lock)
        {
            _state.Accounts.TryAdd(email, new Dictionary<string, FakeFile>(StringComparer.Ordinal));
            var token = "fake-" + Guid.NewGuid().ToString("N");
            _state.Tokens[token] = new TokenInfo { Account = email };
            Save();
            return token;
        }
    }

    /// <summary>Invalidates a token, as a revoked or expired authorization.</summary>
    public void ExpireToken(string token)
    {
        lock (_lock)
        {
            _state.Tokens[token].Valid = false;
            Save();
        }
    }

    /// <summary>The next call on <paramref name="account"/> (any account when null) of <paramref name="method"/> (any when null) throws <paramref name="error"/>.</summary>
    public void InjectFailure(Exception error, string? account = null, string? method = null)
    {
        lock (_lock) _failures.Add((account, method, error));
    }

    /// <summary>A client holding <paramref name="token"/>.</summary>
    public IGoogleWorkspace Client(string token) => new FakeClient(this, token);

    /// <summary>A copy of an account's files.</summary>
    public IReadOnlyDictionary<string, FakeFile> FilesOf(string account)
    {
        lock (_lock)
            return _state.Accounts[account].ToDictionary(kv => kv.Key, kv => Clone(kv.Value), StringComparer.Ordinal);
    }

    /// <summary>The user typing into a tracker cell in the Sheets UI.</summary>
    public void UserEditCell(string account, string sheetId, int row, string column, string value)
    {
        lock (_lock)
        {
            var f = _state.Accounts[account][sheetId];
            var rows = Rows(f);
            var col = rows[0].IndexOf(column);
            while (rows[row].Count <= col) rows[row].Add("");
            rows[row][col] = value;
            SetRows(f, rows);
            Bump(f);
            Save();
        }
    }

    /// <summary>The user deleting a tracker row in the Sheets UI.</summary>
    public void UserDeleteRow(string account, string sheetId, int row)
    {
        lock (_lock)
        {
            var f = _state.Accounts[account][sheetId];
            var rows = Rows(f);
            rows.RemoveAt(row);
            SetRows(f, rows);
            Bump(f);
            Save();
        }
    }

    /// <summary>The user editing a Google Doc's text in the Docs UI.</summary>
    public void UserEditDoc(string account, string fileId, string text)
    {
        lock (_lock)
        {
            var f = _state.Accounts[account][fileId];
            f.Content = text;
            Bump(f);
            Save();
        }
    }

    /// <summary>A sheet's rows.</summary>
    public List<List<string>> SheetRows(string account, string sheetId)
    {
        lock (_lock) return Rows(_state.Accounts[account][sheetId]);
    }

    private static List<List<string>> Rows(FakeFile f) => JsonSerializer.Deserialize<List<List<string>>>(f.Content) ?? [];

    private static void SetRows(FakeFile f, List<List<string>> rows) => f.Content = JsonSerializer.Serialize(rows);

    private static FakeFile Clone(FakeFile f) => JsonSerializer.Deserialize<FakeFile>(JsonSerializer.Serialize(f))!;

    private static void Bump(FakeFile f)
    {
        f.Version++;
        f.Modified = Clock.Iso();
        f.Revisions.Add(new Revision(f.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), f.Content, false));
    }

    private void Save()
    {
        if (_path is not null)
            File.WriteAllText(_path, JsonSerializer.Serialize(_state));
    }

    private sealed class FakeClient(FakeGoogle g, string token) : IGoogleWorkspace
    {
        private Dictionary<string, FakeFile> Files(string method, out string account)
        {
            lock (g._lock)
            {
                if (!g._state.Tokens.TryGetValue(token, out var info) || !info.Valid)
                    throw new AuthExpiredException("google");
                account = info.Account;
                g.Calls.Add((account, method));
                for (var i = 0; i < g._failures.Count; i++)
                {
                    var (acct, m, error) = g._failures[i];
                    if ((acct is null || acct == account) && (m is null || m == method))
                    {
                        g._failures.RemoveAt(i);
                        throw error;
                    }
                }
                return g._state.Accounts[account];
            }
        }

        private static FakeFile Get(Dictionary<string, FakeFile> files, string id) =>
            files.TryGetValue(id, out var f) ? f : throw new NotFoundException("google", $"file {id}");

        private T Locked<T>(Func<T> work)
        {
            lock (g._lock)
            {
                var r = work();
                g.Save();
                return r;
            }
        }

        private string New(Dictionary<string, FakeFile> files, string name, string? parent, string kind, string mime, string content, string appKey)
        {
            if (parent is not null)
                Get(files, parent);
            var id = "f_" + Guid.NewGuid().ToString("N")[..12];
            files[id] = new FakeFile
            {
                Id = id, Name = name, Parent = parent, Kind = kind, Mime = mime, Content = content, AppKey = appKey, Modified = Clock.Iso(),
                Revisions = [new Revision("1", content, false)],
            };
            return id;
        }

        private string? Find(Dictionary<string, FakeFile> files, string appKey) => files.Values.FirstOrDefault(f => f.AppKey == appKey)?.Id;

        public Task<string> AccountEmailAsync(CancellationToken ct = default) => Task.FromResult(Locked(() => { Files("about", out var a); return a; }));

        public Task<string?> FindByAppKeyAsync(string appKey, CancellationToken ct = default) =>
            Task.FromResult(Locked(() => Find(Files("find", out _), appKey)));

        public Task<string> EnsureFolderAsync(string name, string? parentId, string appKey, CancellationToken ct = default) =>
            Task.FromResult(Locked(() => { var fs = Files("ensureFolder", out _); return Find(fs, appKey) ?? New(fs, name, parentId, Folder, "", "", appKey); }));

        public Task<string> CreateDocAsync(string name, string parentId, string html, string appKey, CancellationToken ct = default) =>
            Task.FromResult(Locked(() => { var fs = Files("createDoc", out _); return Find(fs, appKey) ?? New(fs, name, parentId, Doc, "", HtmlToText(html), appKey); }));

        public Task<string> CreateFileAsync(string name, string parentId, byte[] data, string mime, string appKey, CancellationToken ct = default) =>
            Task.FromResult(Locked(() => { var fs = Files("createFile", out _); return Find(fs, appKey) ?? New(fs, name, parentId, Binary, mime, Convert.ToBase64String(data), appKey); }));

        public Task<string> ReadTextAsync(string fileId, CancellationToken ct = default) =>
            Task.FromResult(Locked(() =>
            {
                var f = Get(Files("readText", out _), fileId);
                return f.Kind switch
                {
                    Doc => f.Content,
                    Binary when f.Mime == "application/pdf" => PdfText(Convert.FromBase64String(f.Content)),
                    Binary => Encoding.UTF8.GetString(Convert.FromBase64String(f.Content)),
                    _ => f.Content,
                };
            }));

        public Task UpdateFileAsync(string fileId, byte[] data, string mime, CancellationToken ct = default) =>
            Task.FromResult(Locked(() =>
            {
                var f = Get(Files("updateFile", out _), fileId);
                f.Content = Convert.ToBase64String(data);
                f.Mime = mime;
                Bump(f);
                return 0;
            }));

        public Task<FileMeta> MetaAsync(string fileId, CancellationToken ct = default) =>
            Task.FromResult(Locked(() =>
            {
                var f = Get(Files("meta", out _), fileId);
                return new FileMeta(f.Id, f.Name, f.Parent is null ? [] : [f.Parent], f.Version, f.Modified, f.Kind == Binary ? f.Mime : f.Kind);
            }));

        public Task MoveAsync(string fileId, string newParentId, CancellationToken ct = default) =>
            Task.FromResult(Locked(() =>
            {
                var fs = Files("move", out _);
                Get(fs, newParentId);
                Get(fs, fileId).Parent = newParentId;
                return 0;
            }));

        public Task<string> CopyAsync(string fileId, string name, string parentId, string appKey, CancellationToken ct = default) =>
            Task.FromResult(Locked(() =>
            {
                var fs = Files("copy", out _);
                var src = Get(fs, fileId);
                return Find(fs, appKey) ?? New(fs, name, parentId, src.Kind, src.Mime, src.Content, appKey);
            }));

        public Task<string?> KeepRevisionForeverAsync(string fileId, CancellationToken ct = default) =>
            Task.FromResult(Locked(() =>
            {
                var f = Get(Files("keepRevision", out _), fileId);
                var head = f.Revisions[^1];
                f.Revisions[^1] = head with { KeepForever = true };
                return (string?)head.Id;
            }));

        public Task<string> CreateSheetAsync(string name, string parentId, IReadOnlyList<string> headers, string appKey, CancellationToken ct = default) =>
            Task.FromResult(Locked(() =>
            {
                var fs = Files("createSheet", out _);
                return Find(fs, appKey) ?? New(fs, name, parentId, Sheet, "", JsonSerializer.Serialize(new List<List<string>> { headers.ToList() }), appKey);
            }));

        public Task<List<List<string>>> ReadRowsAsync(string sheetId, CancellationToken ct = default) =>
            Task.FromResult(Locked(() => Rows(Get(Files("readRows", out _), sheetId))));

        public Task UpdateCellsAsync(string sheetId, IReadOnlyList<(int Row, string Column, string Value)> cells, CancellationToken ct = default) =>
            Task.FromResult(Locked(() =>
            {
                var f = Get(Files("updateCells", out _), sheetId);
                var rows = Rows(f);
                foreach (var (row, column, value) in cells)
                {
                    var col = rows[0].IndexOf(column);
                    if (col < 0) throw new ProviderException("google", $"no column {column}", retryable: false);
                    while (rows[row].Count <= col) rows[row].Add("");
                    rows[row][col] = value;
                }
                SetRows(f, rows);
                Bump(f);
                return 0;
            }));

        public Task AppendRowsAsync(string sheetId, IReadOnlyList<IReadOnlyList<string>> newRows, CancellationToken ct = default) =>
            Task.FromResult(Locked(() =>
            {
                var f = Get(Files("appendRows", out _), sheetId);
                var rows = Rows(f);
                rows.AddRange(newRows.Select(r => r.ToList()));
                SetRows(f, rows);
                Bump(f);
                return 0;
            }));

        public Task SetHeadersAsync(string sheetId, IReadOnlyList<string> headers, CancellationToken ct = default) =>
            Task.FromResult(Locked(() =>
            {
                var f = Get(Files("setHeaders", out _), sheetId);
                var rows = Rows(f);
                rows[0] = headers.ToList();
                SetRows(f, rows);
                Bump(f);
                return 0;
            }));
    }

    private static string HtmlToText(string html)
    {
        var t = Regex.Replace(html, @"<li>", "- ", RegexOptions.None, TimeSpan.FromSeconds(1));
        t = Regex.Replace(t, @"</(p|h\d|li)>", "\n", RegexOptions.None, TimeSpan.FromSeconds(1));
        t = Regex.Replace(t, "<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        return System.Net.WebUtility.HtmlDecode(t).Replace(" ", "", StringComparison.Ordinal).TrimEnd('\n');
    }

    private static string PdfText(byte[] pdf)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => p.Text));
    }

    /// <summary>The plain text Drive would export for a draft (what staleness compares).</summary>
    public static string ExportedText(Draft draft) => HtmlToText(draft.Html());
}
