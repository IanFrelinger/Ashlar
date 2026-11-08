using System.Security.Cryptography;
using Dapper;
using FluentAssertions;
using JobSearch.Host;
using JobSearch.Host.Studio;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JobSearch.Tests;

/// <summary>Backups the host script ships to S3, and the restore step that re-applies erasures (deploy/README.md).</summary>
public sealed class BackupTests
{
    [Fact]
    public void A_backup_is_a_consistent_copy_of_the_data_with_the_secrets_still_encrypted_and_no_keys()
    {
        using var h = new Harness();
        var (jordan, token) = h.Jordan();
        Directory.CreateDirectory(Path.Combine(h.Dir, "forge", "job1"));
        File.WriteAllText(Path.Combine(h.Dir, "forge", "job1", "request.txt"), "scratch space");
        Directory.Exists(Path.Combine(h.Dir, "keys")).Should().BeTrue("the harness runs on development keys under keys/");

        // The host script stages backups inside the data directory, under .backup/.
        var outDir = Path.Combine(h.Dir, ".backup", "b1");
        var manifest = Backup.Write(h.Dir, outDir);

        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(outDir).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, "the copy holds personal data");
        var paths = manifest.Files.Select(f => f.Path).ToList();
        paths.Should().Contain(["jobsearch.db", "secrets.json", Backup.Ledger]);
        paths.Should().NotContain(p => p.StartsWith("keys/", StringComparison.Ordinal), "the master key never travels with the data it protects");
        paths.Should().NotContain(p => p.StartsWith("forge/", StringComparison.Ordinal) || p.StartsWith(".backup/", StringComparison.Ordinal));
        paths.Should().NotContain(p => p.EndsWith("-wal", StringComparison.Ordinal) || p.EndsWith("-shm", StringComparison.Ordinal), "the copy is one self-contained file");
        foreach (var f in manifest.Files)
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(outDir, f.Path)))).Should().Be(f.Sha256, f.Path);

        using (var c = new SqliteConnection($"Data Source={Path.Combine(outDir, Backup.Database)};Mode=ReadOnly;Pooling=False"))
        {
            c.Open();
            c.ExecuteScalar<string>("SELECT email FROM users WHERE id=@jordan", new { jordan }).Should().Be(Harness.JordanEmail);
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM resume_versions WHERE user_id=@jordan", new { jordan }).Should().Be(1);
        }
        File.ReadAllText(Path.Combine(outDir, "secrets.json")).Should().NotContain(token, "secrets are stored encrypted and copied as they are");

        FluentActions.Invoking(() => Backup.Write(h.Dir, outDir)).Should().Throw<ArgumentException>("a backup never writes over another");
    }

    [Fact]
    public void Restoring_an_older_backup_and_applying_the_newest_ledger_erases_again_whoever_was_erased_since()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        var (sam, _) = h.Sam();
        var older = Path.Combine(h.Dir, ".backup", "older");
        Backup.Write(h.Dir, older).Erasures.Should().Be(0);
        h.App.Compliance.Erase(jordan);
        var newest = Path.Combine(h.Dir, ".backup", "newest");
        Backup.Write(h.Dir, newest).Erasures.Should().Be(1);
        File.ReadAllText(Path.Combine(newest, Backup.Ledger)).Should().NotContain(jordan, "the ledger holds keyed hashes, not ids")
            .And.NotContain(Harness.JordanEmail);

        // Restore the older backup into a fresh data directory. The master key comes from the parameter store in
        // production; here, from the harness's development key.
        var restored = Path.Combine(Path.GetTempPath(), "jobsearch-restore-" + Guid.NewGuid().ToString("N"));
        CopyAll(older, restored);
        try
        {
            using var app = new App(new AppOptions
            {
                DataDir = restored, GoogleMode = "fake", SigningKey = Harness.TestSigningKey,
                MasterKey = File.ReadAllText(Path.Combine(h.Dir, "keys", "master.key")),
            });
            app.Accounts.FindUser(Harness.JordanEmail).Should().NotBeNull("the older backup predates the erasure");

            app.Compliance.Reapply(Backup.ReadLedger(Path.Combine(newest, Backup.Ledger))).Should().Be(1);

            app.Accounts.FindUser(Harness.JordanEmail).Should().BeNull();
            app.Db.Read(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM resume_versions WHERE user_id=@jordan", new { jordan })).Should().Be(0);
            File.ReadAllText(Path.Combine(restored, "secrets.json")).Should().NotContain($"google:{jordan}");
            app.Accounts.FindUser(Harness.SamEmail).Should().NotBeNull("re-applying one erasure touches no one else");
            app.Compliance.WasErased(jordan).Should().BeTrue();
            app.Compliance.WasErased(sam).Should().BeFalse();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(restored, recursive: true); } catch (IOException) { }
        }
    }

    private static void CopyAll(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
