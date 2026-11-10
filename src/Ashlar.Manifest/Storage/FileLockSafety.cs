namespace Ashlar.Manifest.Storage;

/// <summary>
/// Checks the runtime's FileShare.None behavior once per process, before a store can be constructed.
/// This is a runtime sanity check, not certification of every filesystem or protection from an
/// actor who can unlink/replace the store's persistent .lock file. Store directories must be trusted.
/// </summary>
internal static class FileLockSafety
{
    // Lazy caches both success and failure, including when stores initialize concurrently.
    private static readonly Lazy<bool> Verified = new(Verify);

    internal static void EnsureSupported() => _ = Verified.Value;

    private static bool IsSharingViolation(IOException exception)
    {
        // Windows reports HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION/ERROR_LOCK_VIOLATION).
        // Unix FileStream exposes flock's EWOULDBLOCK as raw errno: Linux 11, macOS/BSD 35.
        // Do not mistake a missing probe, descriptor exhaustion, or another I/O error for safety.
        return OperatingSystem.IsWindows()
            ? exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
            : exception.HResult is 11 or 35;
    }

    private static bool Verify()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-lock-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            try
            {
                using var second = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                return true; // The existing exclusive handle must reject a second open.
            }

            throw new InvalidOperationException(
                "FileShare.None exclusivity is unavailable. Refusing gate-store and ledger initialization and writes. " +
                "Unset DOTNET_SYSTEM_IO_DISABLEFILELOCKING (or set it to 0) and restart the process.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Could not verify FileShare.None exclusivity. Refusing gate-store and ledger initialization and writes. " +
                "Check the temporary directory and DOTNET_SYSTEM_IO_DISABLEFILELOCKING, then restart the process.", ex);
        }
        finally
        {
            // This private probe is never used for store coordination. Store .lock files, unlike
            // the probe, must remain on disk after their handles are released.
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
