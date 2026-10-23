namespace Ashlar.Manifest.Storage;

/// <summary>
/// Checks the process/runtime's local FileShare.None capability once. This is not a
/// per-store filesystem test, nor protection against deleting a live lock file.
/// </summary>
internal static class FileLockingCapability
{
    private static readonly Lazy<bool> Supported = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void EnsureSupported() => _ = Supported.Value;

    private static bool Probe()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-lock-probe-" + Guid.NewGuid().ToString("N"));
        var created = false;
        try
        {
            using var first = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            created = true;
            try
            {
                using var second = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return true;
            }

            throw new InvalidOperationException(
                "File locking is unavailable: a second FileShare.None handle was accepted. "
                + "Gate stores and instance ledgers require mutual exclusion. Remove "
                + "DOTNET_SYSTEM_IO_DISABLEFILELOCKING from the process environment and restart.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Could not verify file-locking capability using a temporary file. "
                + "Check temporary-directory access before opening a gate store or instance ledger.", ex);
        }
        finally
        {
            if (created)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
