using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ashlar.Infrastructure.Caching;

/// <summary>
/// Fingerprints every file in a local input tree, including its root identity and relative names.
/// Git administration is excluded. Unreadable files and links disable caching rather than using
/// an incomplete key. This is not a fingerprint of external services or out-of-tree dependencies.
/// </summary>
internal static class DirectoryFingerprint
{
    // A large input tree should run uncached, not spend unbounded I/O computing a cache key.
    internal const long MaximumBytes = 64 * 1024 * 1024;
    private sealed class Budget
    {
        public long Bytes;
        public int Entries;
        public readonly byte[] Buffer = new byte[81920];
    }

    public static async Task<string?> TryComputeAsync(DirectoryInfo root, CancellationToken cancellationToken, bool excludeBuildOutputs = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, root.FullName);
            await VisitAsync(root, root.FullName, hash, excludeBuildOutputs, new Budget(), 0, cancellationToken).ConfigureAwait(false);
            return Convert.ToBase64String(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static async Task VisitAsync(DirectoryInfo directory, string root, IncrementalHash hash, bool excludeBuildOutputs, Budget budget, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 128) throw new IOException("Fingerprint tree exceeds the depth budget.");
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Cannot fingerprint linked directories safely.");

        foreach (var entry in directory.EnumerateFileSystemInfos().Take(100_001 - budget.Entries).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (++budget.Entries > 100_000) throw new IOException("Fingerprint tree exceeds the entry budget.");
            if (entry.Name == ".git")
                continue;
            // Validation rebuilds these generated artifacts itself. Including its TRX/build
            // outputs makes every successful run invalidate its own cache entry. Analysis,
            // which reads compiled assemblies, deliberately includes them.
            if (excludeBuildOutputs && entry is DirectoryInfo
                && (entry.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Equals("TestResults", StringComparison.OrdinalIgnoreCase)))
                continue;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cannot fingerprint linked files safely.");
            Append(hash, entry is DirectoryInfo ? "directory" : "file");
            Append(hash, Path.GetRelativePath(root, entry.FullName));
            if (entry is DirectoryInfo child)
            {
                await VisitAsync(child, root, hash, excludeBuildOutputs, budget, depth + 1, ct).ConfigureAwait(false);
            }
            else
            {
                var file = (FileInfo)entry;
                var initialLength = file.Length;
                var initialWriteTime = file.LastWriteTimeUtc;
                if (initialLength > MaximumBytes - budget.Bytes)
                    throw new IOException("Fingerprint tree exceeds the byte budget.");
                await using var stream = CacheFingerprintFile.Open(entry.FullName);
                using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = budget.Buffer;
                long bytesRead = 0;
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
                {
                    bytesRead += count;
                    budget.Bytes += count;
                    if (budget.Bytes > MaximumBytes)
                        throw new IOException("Fingerprint tree exceeds the byte budget.");
                    content.AppendData(buffer, 0, count);
                }
                file.Refresh();
                if (bytesRead != initialLength || !file.Exists || file.Length != initialLength
                    || file.LastWriteTimeUtc != initialWriteTime || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Fingerprint input changed during hashing.");
                hash.AppendData(content.GetHashAndReset());
            }
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
