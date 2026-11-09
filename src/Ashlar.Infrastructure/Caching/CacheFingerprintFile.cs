using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ashlar.Infrastructure.Caching;

/// <summary>Opens only classified regular inputs; never waits for a Unix FIFO writer.</summary>
internal static class CacheFingerprintFile
{
    public static FileStream Open(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        // The managed file attributes do not distinguish FIFOs/devices from regular Unix files.
        // Unsupported Unix ABIs disable caching rather than risking a blocking open or infinite read.
        if (!OperatingSystem.IsLinux())
            throw new IOException("Regular-file classification is unavailable on this platform.");
        const int nonBlockingNoFollowCloseOnExec = 0x800 | 0x20000 | 0x80000;
        var descriptor = OpenLinux(path, nonBlockingNoFollowCloseOnExec);
        if (descriptor < 0) throw new IOException("Cannot open fingerprint input.");
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            // statx's ABI is fixed across Linux architectures. AT_EMPTY_PATH classifies the opened
            // descriptor, not a path that could be swapped between checking and opening it.
            if (Statx(descriptor, "", 0x1000, 1, out var info) != 0
                || (info.Mask & 1) == 0 || (info.Mode & 0xF000) != 0x8000)
                throw new IOException("Fingerprint input is not a regular file.");
            return new FileStream(handle, FileAccess.Read, 81920, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxInfo
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out StatxInfo info);
}
