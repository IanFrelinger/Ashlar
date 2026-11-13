using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Ashlar.Tools.GovernanceBrokerPrototype;

/// <summary>
/// Observational Linux metadata checks for preprovisioned prototype paths.
/// These checks do not provision or prove an isolation boundary. The service and root
/// identities, mount namespace, executable and filesystem must already be trusted.
/// POSIX ACL effective group-class rights are reflected in mode bits; this is not an
/// audit of other ACL models, LSM policy, capabilities or namespace configuration.
/// Trusted identities must not mutate checked paths concurrently with startup/use.
/// </summary>
internal static class LinuxBoundary
{
    private const ushort FileTypeMask = 0xf000;
    private const ushort DirectoryType = 0x4000;
    private const ushort RegularFileType = 0x8000;
    private const ushort GroupOtherWrite = 0x0012; // 0022
    private const ushort GroupOtherAccess = 0x003f; // 0077
    private const uint RequiredStatxFields = 0x000f; // TYPE | MODE | NLINK | UID

    internal readonly record struct Metadata(uint OwnerUid, ushort Mode, uint LinkCount);

    internal static uint EffectiveUid()
    {
        RequireLinux();
        return GetEffectiveUid();
    }

    /// <summary>Authenticates a connected Unix peer's OS uid, never a human decision.</summary>
    internal static uint PeerUid(Socket socket)
    {
        RequireLinux();
        ArgumentNullException.ThrowIfNull(socket);
        if (socket.AddressFamily != AddressFamily.Unix
            || socket.SocketType != SocketType.Stream || !socket.Connected)
        {
            throw new ArgumentException("A connected Unix stream socket is required.", nameof(socket));
        }

        uint length = 12; // Linux struct ucred: pid_t, uid_t, gid_t.
        if (GetSocketOption(socket.SafeHandle, 1, 17, out var peer, ref length) != 0)
        {
            throw NativeFailure("Cannot read the Unix peer credentials.");
        }
        if (length != 12 || peer.Uid == uint.MaxValue)
        {
            throw new IOException("The Unix peer credentials are incomplete.");
        }
        return peer.Uid;
    }

    /// <summary>
    /// Checks the existing normalized absolute path and every ancestor without following
    /// symlinks. A private tree additionally requires every leaf/subtree entry to be
    /// service-owned with no group/other permissions. No metadata is changed.
    /// </summary>
    internal static void ValidatePath(string path, uint serviceUid, bool privateTree = false)
    {
        RequireLinux();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0')
            || !string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal)
            || (path.Length > 1 && path.EndsWith('/')))
        {
            throw new ArgumentException("A normalized absolute path is required.", nameof(path));
        }

        var ancestors = new Stack<string>();
        for (string? ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            ancestors.Push(ancestor);
        }
        Metadata leaf = default;
        while (ancestors.TryPop(out var current))
        {
            var isLeaf = string.Equals(current, path, StringComparison.Ordinal);
            var metadata = ReadMetadata(current);
            ValidateMetadata(metadata, serviceUid, privateTree && isLeaf, requireDirectory: !isLeaf);
            if (isLeaf)
            {
                leaf = metadata;
            }
        }

        if (!privateTree || (leaf.Mode & FileTypeMask) != DirectoryType)
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            // Enumerate one level only: inspect each entry before considering recursion.
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var metadata = ReadMetadata(entry);
                ValidateMetadata(metadata, serviceUid, privateTree: true);
                if ((metadata.Mode & FileTypeMask) == DirectoryType)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    /// <summary>Pure policy evaluation; does not access the OS or alter permissions.</summary>
    internal static void ValidateMetadata(
        Metadata metadata, uint serviceUid, bool privateTree = false, bool requireDirectory = false)
    {
        var type = metadata.Mode & FileTypeMask;
        if (type != DirectoryType && type != RegularFileType)
        {
            throw new IOException("Only regular files and directories are accepted.");
        }
        if (requireDirectory && type != DirectoryType)
        {
            throw new IOException("A path ancestor must be a directory.");
        }
        if (metadata.OwnerUid != serviceUid && (privateTree || metadata.OwnerUid != 0))
        {
            throw new UnauthorizedAccessException("The path has an untrusted owner.");
        }
        var forbidden = privateTree ? GroupOtherAccess : GroupOtherWrite;
        if ((metadata.Mode & forbidden) != 0)
        {
            throw new UnauthorizedAccessException("The path grants disallowed group/other permissions.");
        }
        if (type == RegularFileType && metadata.LinkCount != 1)
        {
            throw new IOException("A regular file must have exactly one hard link.");
        }
    }

    private static Metadata ReadMetadata(string path)
    {
        // AT_FDCWD; AT_SYMLINK_NOFOLLOW | AT_NO_AUTOMOUNT.
        if (Statx(-100, path, 0x900, RequiredStatxFields, out var value) != 0)
        {
            throw NativeFailure("Cannot read required path metadata.");
        }
        if ((value.Mask & RequiredStatxFields) != RequiredStatxFields)
        {
            throw new IOException("The filesystem did not return all required path metadata.");
        }
        return new Metadata(value.Uid, value.Mode, value.LinkCount);
    }

    private static IOException NativeFailure(string message) =>
        new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new PlatformNotSupportedException("This prototype boundary requires Linux x64 or arm64.");
        }
    }

    // The Linux statx UAPI is a fixed 256-byte buffer, unlike architecture-dependent stat.
    // Named offsets come from linux/stat.h; unused/reserved bytes remain part of the buffer.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint LinkCount;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(28)] internal ushort Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials
    {
        internal int Pid;
        internal uint Uid;
        internal uint Gid;
    }

    [DllImport("libc", EntryPoint = "geteuid", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint GetEffectiveUid();

    [DllImport("libc", EntryPoint = "getsockopt", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int GetSocketOption(
        SafeSocketHandle socket, int level, int option, out PeerCredentials value, ref uint length);

    [DllImport("libc", EntryPoint = "statx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(
        int directoryFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out StatxBuffer value);
}
