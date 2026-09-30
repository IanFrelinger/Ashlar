using Ashlar.Abstractions.Paths;
using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Infrastructure.Execution.Scratch;

/// <summary>
/// Snapshot-then-write <see cref="IManagedFileSet"/>: each relative path is
/// staged via <c>.ashlar-tmp</c> then swapped into place. Rollback restores
/// prior content (or deletes newly created files).
/// </summary>
public sealed class SnapshotManagedFileSet : IManagedFileSet
{
    /// <inheritdoc />
    public async Task<ManagedFileApplyResult> ApplyAsync(
        string destRoot,
        IReadOnlyDictionary<string, string> files,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destRoot))
            throw new ArgumentException("destRoot is required.", nameof(destRoot));
        if (files is null)
            throw new ArgumentNullException(nameof(files));

        destRoot = Path.GetFullPath(destRoot);
        Directory.CreateDirectory(destRoot);

        // path → previous content (null when the file did not exist)
        var snapshots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();

        try
        {
            foreach (var (relRaw, content) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rel = NormalizeRel(relRaw);
                var dest = Path.GetFullPath(Path.Combine(destRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                EnsureUnderRoot(destRoot, dest);

                if (!snapshots.ContainsKey(dest))
                {
                    snapshots[dest] = File.Exists(dest)
                        ? await File.ReadAllTextAsync(dest, cancellationToken).ConfigureAwait(false)
                        : null;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var tmp = dest + ".ashlar-tmp";
                await File.WriteAllTextAsync(tmp, content ?? "", cancellationToken).ConfigureAwait(false);
                File.Move(tmp, dest, overwrite: true);
                written.Add(dest);
            }
        }
        catch
        {
            await RestoreAsync(snapshots, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new ManagedFileApplyResult
        {
            Succeeded = true,
            Rollback = ct => RestoreAsync(snapshots, ct)
        };
    }

    private static async Task RestoreAsync(
        IReadOnlyDictionary<string, string?> snapshots,
        CancellationToken cancellationToken)
    {
        foreach (var (path, previous) in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var tmp = path + ".ashlar-tmp";
                if (File.Exists(tmp))
                    File.Delete(tmp);

                if (previous is null)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, previous, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // best-effort rollback
            }
        }
    }

    private static string NormalizeRel(string name)
    {
        var rel = (name ?? "").Replace('\\', '/').Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(rel) || rel.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException($"invalid managed file path: {name}");
        return rel;
    }

    private static void EnsureUnderRoot(string root, string fullPath)
    {
        if (!PathContainment.IsWithin(fullPath, root))
        {
            throw new InvalidOperationException($"managed file escapes destRoot: {fullPath}");
        }
    }
}
