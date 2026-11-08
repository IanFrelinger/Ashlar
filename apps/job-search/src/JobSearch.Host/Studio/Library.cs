using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using Dapper;
using JobSearch.Store;
using JobSearch.Studio;

namespace JobSearch.Host.Studio;

/// <summary>
/// The platform's keys: the Ed25519 key that signs every step the studio certifies, the public keys it trusts, and the
/// master key that encrypts secrets at rest. In production the private keys come from the environment
/// (<c>JOBSEARCH_SIGNING_KEY</c>, <c>JOBSEARCH_MASTER_KEY</c>, from AWS SSM); without them, development keys are created
/// under the data directory's <c>keys/</c>, which the backup script leaves out.
/// </summary>
public sealed class StudioKeys
{
    private StudioKeys(byte[] signingKey, byte[] masterKey, IReadOnlyList<string> trusted)
    {
        SigningKey = signingKey;
        MasterKey = masterKey;
        PublicKey = CertificationRecordEd25519.DerivePublicKeyBase64(signingKey);
        TrustedKeys = [.. trusted.Append(PublicKey).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Raw Ed25519 private key that signs certified steps.</summary>
    public byte[] SigningKey { get; }
    /// <summary>Its public key (Base64).</summary>
    public string PublicKey { get; }
    /// <summary>Every public key a step may be signed by: this platform's, rotated-out ones, and the app's shipped key.</summary>
    public IReadOnlyList<string> TrustedKeys { get; }
    /// <summary>AES-256 key for secrets at rest.</summary>
    public byte[] MasterKey { get; }

    /// <summary>The trust policy for library steps.</summary>
    public CertificationTrustPolicy Policy => CertificationTrustPolicy.FromTrustedKeys(TrustedKeys);

    /// <summary>Loads (or, for development, creates) the keys.</summary>
    public static StudioKeys Load(string dataDir, string? signingKeyBase64, string? masterKeyBase64, IEnumerable<string> alsoTrusted)
    {
        var signing = signingKeyBase64 is { Length: > 0 } s ? Decode(s, "JOBSEARCH_SIGNING_KEY") : DevKey(dataDir, "signing.key");
        var master = masterKeyBase64 is { Length: > 0 } m ? Decode(m, "JOBSEARCH_MASTER_KEY") : DevKey(dataDir, "master.key");
        return new StudioKeys(signing, master, [.. alsoTrusted]);
    }

    private static byte[] Decode(string base64, string name)
    {
        var bytes = Convert.FromBase64String(base64.Trim());
        if (bytes.Length != 32)
            throw new InvalidOperationException($"{name} must be 32 random bytes, Base64-encoded");
        return bytes;
    }

    private static byte[] DevKey(string dataDir, string file)
    {
        var dir = Path.Combine(dataDir, "keys");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, file);
        if (File.Exists(path))
            return Decode(File.ReadAllText(path), path);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }
}

/// <summary>A step in the shared library.</summary>
public sealed record LibraryEntry
{
    /// <summary>Brick id, <c>step.&lt;slug&gt;.&lt;hex&gt;</c>.</summary>
    public string Id { get; init; } = "";
    /// <summary>check or column.</summary>
    public string Kind { get; init; } = "";
    /// <summary>Name.</summary>
    public string Name { get; init; } = "";
    /// <summary>Description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Settings.</summary>
    public List<StepSetting> Settings { get; init; } = [];
    /// <summary>The rules in words.</summary>
    public string Behavior { get; init; } = "";
    /// <summary>Content hash of the certified source.</summary>
    public string ContentHash { get; init; } = "";
    /// <summary>shared or private.</summary>
    public string Visibility { get; init; } = "shared";
    /// <summary>The user a private step belongs to; never shown for a shared one (STUDIO.md I7).</summary>
    public string? OwnerUserId { get; init; }
    /// <summary>active or quarantined.</summary>
    public string Status { get; init; } = "active";
    /// <summary>Why it is quarantined.</summary>
    public string? StatusDetail { get; init; }
    /// <summary>How many workflows have added it.</summary>
    public long Uses { get; init; }
    /// <summary>When it was certified.</summary>
    public string CreatedAt { get; init; } = "";
    /// <summary>seed, claude or private-model: who wrote the code.</summary>
    public string Origin { get; init; } = "";
    /// <summary>Mutants the gate generated and killed.</summary>
    public int Mutants { get; init; }
}

/// <summary>
/// The shared library of certified steps: rows in the database, files (record, source, assembly, witness) under
/// <c>&lt;data&gt;/library/&lt;id&gt;</c>. The web server verifies a step's signature and hashes before publishing it,
/// but never loads its code; steps run in the step host (<see cref="StepHostPool"/>).
/// </summary>
public sealed class StepLibrary
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS library_steps (
            id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL, description TEXT NOT NULL, settings_json TEXT NOT NULL,
            behavior TEXT NOT NULL, content_hash TEXT NOT NULL, visibility TEXT NOT NULL, owner_user_id TEXT, status TEXT NOT NULL,
            status_detail TEXT, uses INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, origin TEXT NOT NULL, mutants INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS library_steps_owner ON library_steps(owner_user_id);
        """;

    private readonly Db _db;
    private readonly StudioKeys _keys;
    private readonly StepHostPool _hosts;
    private readonly Dictionary<string, LibraryStepBrick> _bricks = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Opens the library rooted at <paramref name="root"/>.</summary>
    public StepLibrary(Db db, string root, StudioKeys keys, StepHostPool hosts)
    {
        _db = db;
        Root = root;
        _keys = keys;
        _hosts = hosts;
        Directory.CreateDirectory(root);
        db.Read(c => c.Execute(Schema));
    }

    /// <summary>Where step folders live.</summary>
    public string Root { get; }

    /// <summary>A step's folder.</summary>
    public string Folder(string id) => Path.Combine(Root, id);

    private const string Columns = "id, kind, name, description, settings_json AS SettingsJson, behavior, content_hash AS ContentHash, visibility, owner_user_id AS OwnerUserId, status, status_detail AS StatusDetail, uses, created_at AS CreatedAt, origin, mutants";

    private sealed record Row(string Id, string Kind, string Name, string Description, string SettingsJson, string Behavior, string ContentHash,
        string Visibility, string? OwnerUserId, string Status, string? StatusDetail, long Uses, string CreatedAt, string Origin, long Mutants)
    {
        public LibraryEntry ToEntry() => new()
        {
            Id = Id, Kind = Kind, Name = Name, Description = Description, Settings = JobSearch.Store.Json.Read<List<StepSetting>>(SettingsJson) ?? [], Behavior = Behavior,
            ContentHash = ContentHash, Visibility = Visibility, OwnerUserId = OwnerUserId, Status = Status, StatusDetail = StatusDetail, Uses = Uses,
            CreatedAt = CreatedAt, Origin = Origin, Mutants = (int)Mutants,
        };
    }

    /// <summary>A step, or null.</summary>
    public LibraryEntry? Get(string id) =>
        _db.Read(c => c.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM library_steps WHERE id=@id", new { id }))?.ToEntry();

    /// <summary>Whether <paramref name="userId"/> may use the step.</summary>
    public static bool VisibleTo(LibraryEntry e, string userId) => e.Visibility == "shared" || e.OwnerUserId == userId;

    /// <summary>Every step the user may use: shared ones and their own private ones.</summary>
    public IReadOnlyList<LibraryEntry> Visible(string userId) =>
        _db.Read(c => c.Query<Row>($"SELECT {Columns} FROM library_steps WHERE visibility='shared' OR owner_user_id=@userId ORDER BY uses DESC, name", new { userId }))
            .Select(r => r.ToEntry()).ToList();

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "that", "with", "this", "from", "any", "are", "not", "postings", "posting", "jobs", "job", "step", "steps",
        "want", "only", "show", "add", "please", "me", "my", "which", "when", "than", "into", "about", "them", "those", "these",
    };

    /// <summary>The visible steps closest to <paramref name="text"/> by shared words, best first.</summary>
    public IReadOnlyList<LibraryEntry> Search(string text, string userId, int limit)
    {
        var words = Words(text);
        if (words.Count == 0)
            return [.. Visible(userId).Take(limit)];
        return Visible(userId)
            .Select(e => (e, score: 3 * Overlap(words, Words(e.Name)) + Overlap(words, Words(e.Description)) + Overlap(words, Words(e.Behavior))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenByDescending(x => x.e.Uses)
            .Take(limit).Select(x => x.e).ToList();
    }

    private static HashSet<string> Words(string text) =>
        [.. text.ToLowerInvariant().Split((char[])[' ', ',', '.', ';', ':', '-', '(', ')', '"', '\'', '/', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))];

    private static int Overlap(HashSet<string> a, HashSet<string> b) => a.Count(b.Contains);

    /// <summary>
    /// Publishes a certified step from <paramref name="staged"/> (its signed record, source, assembly and witness):
    /// verifies it against the pinned keys without loading it, moves it into the library and records it.
    /// </summary>
    public LibraryEntry Publish(LibraryEntry entry, string staged)
    {
        var problem = CertifiedBricks.VerifyOnly(entry.Id, staged, _keys.Policy);
        if (problem is not null)
            throw new InvalidOperationException($"step {entry.Id} does not verify: {problem}");
        var target = Folder(entry.Id);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(staged))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        var stored = entry with { Status = "active", CreatedAt = Clock.Iso() };
        _db.Write((c, tx) => c.Execute("""
            INSERT INTO library_steps (id, kind, name, description, settings_json, behavior, content_hash, visibility, owner_user_id, status, status_detail, uses, created_at, origin, mutants)
            VALUES (@Id, @Kind, @Name, @Description, @SettingsJson, @Behavior, @ContentHash, @Visibility, @OwnerUserId, @Status, NULL, 0, @CreatedAt, @Origin, @Mutants)
            ON CONFLICT(id) DO UPDATE SET content_hash=excluded.content_hash, status='active', status_detail=NULL
            """, new
        {
            stored.Id, stored.Kind, stored.Name, stored.Description, SettingsJson = JobSearch.Store.Json.Write(stored.Settings), stored.Behavior, stored.ContentHash,
            stored.Visibility, stored.OwnerUserId, stored.Status, stored.CreatedAt, stored.Origin, stored.Mutants,
        }, tx));
        lock (_lock) _bricks.Remove(entry.Id);
        return Get(entry.Id)!;
    }

    /// <summary>Switches a step off for everyone, with the reason (a timeout or a crash in the step host).</summary>
    public void Quarantine(string id, string reason) =>
        _db.Write((c, tx) => c.Execute("UPDATE library_steps SET status='quarantined', status_detail=@reason WHERE id=@id", new { id, reason }, tx));

    /// <summary>An operator switches a quarantined step back on.</summary>
    public void Reinstate(string id) =>
        _db.Write((c, tx) => c.Execute("UPDATE library_steps SET status='active', status_detail=NULL WHERE id=@id", new { id }, tx));

    /// <summary>Counts a workflow adding the step.</summary>
    public void CountUse(string id) =>
        _db.Write((c, tx) => c.Execute("UPDATE library_steps SET uses=uses+1 WHERE id=@id", new { id }, tx));

    /// <summary>
    /// Account deletion: removes the user's private steps and their files, and detaches the user from shared steps
    /// (shared steps are generic code with no personal data; others may be using them).
    /// </summary>
    public int ForgetUser(string userId)
    {
        var mine = _db.Read(c => c.Query<string>("SELECT id FROM library_steps WHERE owner_user_id=@userId AND visibility='private'", new { userId }).AsList());
        _db.Write((c, tx) =>
        {
            c.Execute("DELETE FROM library_steps WHERE owner_user_id=@userId AND visibility='private'", new { userId }, tx);
            c.Execute("UPDATE library_steps SET owner_user_id=NULL WHERE owner_user_id=@userId", new { userId }, tx);
        });
        foreach (var id in mine)
        {
            if (Directory.Exists(Folder(id)))
                Directory.Delete(Folder(id), recursive: true);
            lock (_lock) _bricks.Remove(id);
        }
        return mine.Count;
    }

    /// <summary>The brick the workflow executor runs for a step: a proxy into the step host.</summary>
    public Brick? Brick(string id)
    {
        lock (_lock)
        {
            if (_bricks.TryGetValue(id, out var b))
                return b;
        }
        var entry = Get(id);
        if (entry is null)
            return null;
        var brick = new LibraryStepBrick(entry, this, _hosts);
        lock (_lock) _bricks[id] = brick;
        return brick;
    }

    /// <summary>Certifies and publishes every seed that is not in the library yet. Seeds are shared, written by the studio.</summary>
    public async Task<int> EnsureSeedsAsync(StepCertifier certifier, string seedsDir, string workDir, CancellationToken ct)
    {
        if (!Directory.Exists(seedsDir))
            return 0;
        var added = 0;
        foreach (var file in Directory.GetFiles(seedsDir, "*.json").Order(StringComparer.Ordinal))
        {
            var seed = JsonSerializer.Deserialize<StepSeed>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), JobSearch.Store.Json.Options)
                ?? throw new InvalidOperationException($"{file} is empty");
            var id = SeedId(seed);
            if (Get(id) is not null)
                continue;
            var cases = StepSource.FromFlat(seed.Design.Kind, seed.Cases);
            var outcome = await certifier.CertifyAsync(id, seed.Design, new StepCode { Code = seed.Code, Cases = cases }, Path.Combine(workDir, "seed-" + seed.Slug), ct).ConfigureAwait(false);
            if (!outcome.Admitted)
                throw new InvalidOperationException($"seed {seed.Slug} was not admitted: {outcome.Check}: {outcome.Reason}");
            Publish(new LibraryEntry
            {
                Id = id, Kind = seed.Design.Kind, Name = seed.Design.Name, Description = seed.Design.Description, Settings = seed.Design.Settings,
                Behavior = seed.Design.Behavior, ContentHash = outcome.ContentHash, Visibility = "shared", Origin = "seed", Mutants = outcome.Mutants,
            }, outcome.Folder);
            added++;
        }
        return added;
    }

    /// <summary>A seed's stable id.</summary>
    public static string SeedId(StepSeed seed) =>
        StepSource.NewId(seed.Design.Name, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("seed:" + seed.Slug)))[..8]);
}

/// <summary>
/// The workflow executor's view of a library step. It runs the step in the step host. A step that cannot run flags the
/// posting (never passes it silently); a step that overruns or crashes the host is quarantined for everyone.
/// </summary>
public sealed class LibraryStepBrick : Brick
{
    private readonly StepLibrary _library;
    private readonly StepHostPool _hosts;
    private readonly string _kind;

    /// <summary>Creates the proxy.</summary>
    public LibraryStepBrick(LibraryEntry entry, StepLibrary library, StepHostPool hosts)
    {
        _library = library;
        _hosts = hosts;
        _kind = entry.Kind;
        Id = entry.Id;
        Name = entry.Name;
        Version = "1.0.0";
        Category = entry.Kind == StepContract.Check ? BrickCategory.Validation : BrickCategory.Analysis;
        Description = entry.Description;
        Implementations = new BrickImplementations
        {
            Deterministic = new DeterministicImplementation { Id = "step-host", Name = "Step host", Executor = "code", Description = "Certified studio step, run in the isolated step host." },
        };
        Interface = new BrickInterface
        {
            Inputs = [.. StepContract.Inputs.Select(i => new BrickInputDefinition(i, "string", StepContract.InputHelp[i], required: false, defaultValue: ""))],
            Outputs = [.. StepContract.Outputs(entry.Kind).Select(o => new BrickOutputDefinition(o, "string", o))],
        };
    }

    /// <inheritdoc />
    public override async Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var entry = _library.Get(Id);
        if (entry is null)
            return Failed("this step is no longer in the library");
        if (entry.Status == "quarantined")
            return Failed($"this step is switched off for everyone ({entry.StatusDetail})");
        var inputs = StepContract.Inputs.ToDictionary(i => i, i => input.TryGet<string>(i, out var v) ? v ?? "" : "", StringComparer.Ordinal);
        try
        {
            var outputs = await _hosts.RunAsync(Id, _library.Folder(Id), inputs, cancellationToken).ConfigureAwait(false);
            var output = new BrickOutput();
            foreach (var o in StepContract.Outputs(_kind))
                output.Set(o, outputs.GetValueOrDefault(o) ?? "");
            output.Summary = string.Join(" ", StepContract.Outputs(_kind).Select(o => $"{o}={outputs.GetValueOrDefault(o)}"));
            return output;
        }
        catch (StepRunException ex) when (ex.Kind is "timeout" or "crashed")
        {
            _library.Quarantine(Id, $"{ex.Kind} at {Clock.Iso()}: {ex.Message}");
            return Failed($"this step could not run ({ex.Message}) and has been switched off until an operator reviews it");
        }
        catch (StepRunException ex)
        {
            return Failed($"this step failed: {ex.Message}");
        }
    }

    private BrickOutput Failed(string why)
    {
        var output = new BrickOutput { Summary = why };
        if (_kind == StepContract.Check)
        {
            output.Set("verdict", "flag");
            output.Set("note", why);
        }
        else
        {
            output.Set("value", "(step unavailable)");
        }
        return output;
    }
}

/// <summary>What certifying one step produced.</summary>
public sealed record CertifyOutcome(bool Admitted, string Check, string Reason, string Folder, string ContentHash, int Mutants);

/// <summary>
/// Certifies a step in a child process (<see cref="CertifyStepCommand"/>) and signs the admitted record with the
/// platform key here, in the parent, so the process that runs model-written code never holds a private key.
/// </summary>
public sealed class StepCertifier(StudioKeys keys, TimeSpan? timeout = null, string? cacheDir = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(4);

    /// <summary>Writes the step into <paramref name="workDir"/>, certifies it, and signs it when admitted.</summary>
    public async Task<CertifyOutcome> CertifyAsync(string stepId, StepDesign design, StepCode code, string workDir, CancellationToken ct)
    {
        if (Directory.Exists(workDir))
            Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);
        var source = StepSource.Render(stepId, design, code.Code);
        var witness = StepSource.Witness(stepId, code.Cases);
        await File.WriteAllTextAsync(Path.Combine(workDir, "GeneratedStep.cs"), source, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(workDir, "Step.csproj"), StepSource.ProjectFile, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(workDir, CertifyStepCommand.WitnessFile), witness, ct).ConfigureAwait(false);

        var cacheKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\u0000" + witness)));
        var cached = cacheDir is null ? null : Path.Combine(cacheDir, cacheKey);
        string verdictJson;
        if (cached is not null && File.Exists(Path.Combine(cached, "verdict.json")))
        {
            verdictJson = await File.ReadAllTextAsync(Path.Combine(cached, "verdict.json"), ct).ConfigureAwait(false);
            foreach (var f in new[] { CertifiedBricks.RecordFile, CertifiedArtifactExporter.ArtifactFileName })
                if (File.Exists(Path.Combine(cached, f)))
                    File.Copy(Path.Combine(cached, f), Path.Combine(workDir, f), overwrite: true);
        }
        else
        {
            var psi = ChildCommand.StartInfo(1024L * 1024 * 1024, "certify-step", workDir);
            var (exit, stdout, stderr, timedOut) = await ChildCommand.RunAsync(psi, null, _timeout, ct).ConfigureAwait(false);
            if (timedOut)
                return new CertifyOutcome(false, "timeout", $"certification did not finish within {_timeout.TotalMinutes:0} minutes (a loop that never ends?)", workDir, "", 0);
            verdictJson = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.TrimStart().StartsWith('{')) ?? "";
            if (exit != 0 || verdictJson.Length == 0)
                return new CertifyOutcome(false, "crashed", $"the certifier exited with {exit}: {Tail(stderr)}", workDir, "", 0);
            if (cached is not null)
            {
                Directory.CreateDirectory(cached);
                await File.WriteAllTextAsync(Path.Combine(cached, "verdict.json"), verdictJson, ct).ConfigureAwait(false);
                foreach (var f in new[] { CertifiedBricks.RecordFile, CertifiedArtifactExporter.ArtifactFileName })
                    if (File.Exists(Path.Combine(workDir, f)))
                        File.Copy(Path.Combine(workDir, f), Path.Combine(cached, f), overwrite: true);
            }
        }

        using var verdict = JsonDocument.Parse(verdictJson);
        var root = verdict.RootElement;
        if (!root.GetProperty("admitted").GetBoolean())
            return new CertifyOutcome(false, root.GetProperty("check").GetString() ?? "", root.GetProperty("reason").GetString() ?? "", workDir, "", 0);

        var recordPath = Path.Combine(workDir, CertifiedBricks.RecordFile);
        var record = JsonSerializer.Deserialize<CertificationRecordData>(await File.ReadAllTextAsync(recordPath, ct).ConfigureAwait(false),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("the certifier wrote an empty record");
        var signed = Sign(record, keys.SigningKey, keys.PublicKey);
        await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(signed, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), ct).ConfigureAwait(false);
        var problem = CertifiedBricks.VerifyOnly(stepId, workDir, keys.Policy);
        if (problem is not null)
            return new CertifyOutcome(false, "signature", $"the signed record does not verify: {problem}", workDir, "", 0);
        return new CertifyOutcome(true, "", "", workDir, signed.ContentHash ?? "", signed.TotalMutants ?? 0);
    }

    /// <summary>
    /// Adds the platform's Ed25519 signature. The HMAC payload covers the Ed25519 public key, so the HMAC is recomputed
    /// over the same canonical payload too (both signatures exclude each other structurally).
    /// </summary>
    public static CertificationRecordData Sign(CertificationRecordData record, byte[] privateKey, string publicKey)
    {
        var withKey = record with { Ed25519PublicKey = publicKey, Ed25519Signature = null, Signature = null };
        return withKey with
        {
            Signature = CertificationRecordSigning.Sign(withKey),
            Ed25519Signature = CertificationRecordEd25519.Sign(withKey, privateKey),
        };
    }

    private static string Tail(string text) => text.Length <= 600 ? text : text[^600..];
}
