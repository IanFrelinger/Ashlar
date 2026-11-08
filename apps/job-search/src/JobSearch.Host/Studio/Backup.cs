using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using JobSearch.Store;
using Microsoft.Data.Sqlite;

namespace JobSearch.Host.Studio;

/// <summary>What one backup holds: when it was taken, by which build, how many tombstones, and every file with its hash.</summary>
public sealed record BackupManifest(string CreatedAt, string Build, int Erasures, IReadOnlyList<BackupFile> Files);

/// <summary>A file in a backup, by path relative to the backup root.</summary>
public sealed record BackupFile(string Path, long Bytes, string Sha256);

/// <summary>
/// <c>jobsearch backup --out DIR</c>: a consistent copy of the data directory, taken while the studio runs. The
/// database goes through SQLite's online backup API, so the copy is one transactionally consistent snapshot with the
/// write-ahead log folded in; the encrypted secret store, the step library and the learned site specs are copied as
/// files. <c>keys/</c> never goes in: <c>secrets.json</c> cannot be read without the master key, which lives in the
/// parameter store and not in any backup. <c>erasures.json</c> lists the tombstones (keyed hashes and times, no
/// personal data), so restoring an older backup can re-apply the erasures made since (<see cref="Compliance.Reapply"/>).
/// </summary>
public static class Backup
{
    /// <summary>The database file in the data directory and in a backup.</summary>
    public const string Database = "jobsearch.db";
    /// <summary>The tombstone ledger in a backup.</summary>
    public const string Ledger = "erasures.json";
    /// <summary>The manifest in a backup.</summary>
    public const string Manifest = "manifest.json";

    /// <summary>
    /// Top-level entries a backup leaves out: the keys, scratch space, the host's staging area for backups
    /// (<c>deploy/host/jobsearch-backup</c>), and the live database files, which are copied through the backup API.
    /// </summary>
    public static readonly IReadOnlySet<string> Excluded = new HashSet<string>(StringComparer.Ordinal)
    {
        "keys", "forge", ".backup", Database, Database + "-wal", Database + "-shm", Database + "-journal",
    };

    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Writes a backup of <paramref name="dataDir"/> into the new or empty directory <paramref name="outDir"/>.</summary>
    public static BackupManifest Write(string dataDir, string outDir)
    {
        var source = Path.Combine(dataDir, Database);
        if (!File.Exists(source))
            throw new ArgumentException($"no database at {source}");
        var root = Path.GetFullPath(outDir);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException($"{outDir} is not empty");
        // Owner-only, whatever the caller's umask (docker exec runs with 0000): the copy holds personal data.
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(root);
        else
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var copy = Path.Combine(root, Database);
        using (var from = Open(source, SqliteOpenMode.ReadWrite))
        using (var to = Open(copy, SqliteOpenMode.ReadWriteCreate))
            from.BackupDatabase(to);
        List<Erasure> ledger;
        using (var c = Open(copy, SqliteOpenMode.ReadWrite))
        {
            // One self-contained file: the copy needs no -wal or -shm beside it.
            c.Execute("PRAGMA journal_mode=DELETE;");
            ledger = c.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='deletions'") == 0
                ? []
                : c.Query<Erasure>("SELECT subject_hash AS SubjectHash, deleted_at AS DeletedAt, rows AS Rows FROM deletions ORDER BY deleted_at, subject_hash").AsList();
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(dataDir))
        {
            var name = Path.GetFileName(entry);
            if (Excluded.Contains(name) || Path.GetFullPath(entry) == root)
                continue;
            if (Directory.Exists(entry))
                CopyDirectory(entry, Path.Combine(root, name), root);
            else
                File.Copy(entry, Path.Combine(root, name));
        }
        File.WriteAllText(Path.Combine(root, Ledger), JsonSerializer.Serialize(ledger, Indented));

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => new BackupFile(Path.GetRelativePath(root, f).Replace('\\', '/'), new FileInfo(f).Length, Sha256(f)))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        var manifest = new BackupManifest(Clock.Iso(), Environment.GetEnvironmentVariable("JOBSEARCH_BUILD") ?? "dev", ledger.Count, files);
        File.WriteAllText(Path.Combine(root, Manifest), JsonSerializer.Serialize(manifest, Indented));
        return manifest;
    }

    /// <summary>Reads a tombstone ledger written by <see cref="Write"/>.</summary>
    public static IReadOnlyList<Erasure> ReadLedger(string path) =>
        JsonSerializer.Deserialize<List<Erasure>>(File.ReadAllText(path), Indented) ?? throw new ArgumentException($"{path} is not an erasure ledger");

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 30 }.ToString());
        c.Open();
        c.Execute("PRAGMA busy_timeout=30000;");
        return c;
    }

    private static void CopyDirectory(string from, string to, string skip)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(from))
            if (Path.GetFullPath(dir) != skip)
                CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)), skip);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
