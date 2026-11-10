using System.Buffers;
using Ashlar.CLI.Output;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ashlar.CLI.Packaging;

/// <summary>
/// The outcome of one bounded package read: the text, or the reason it was refused, already
/// composed for an operator. A RESULT and never an exception — the caller's <c>catch</c> must not
/// be the only thing standing between one planted file and a whole pull pass.
/// </summary>
/// <param name="Ok">Whether <paramref name="Text"/> holds the file's content.</param>
/// <param name="Text">The decoded package text when <paramref name="Ok"/>; null otherwise.</param>
/// <param name="Reason">The operator-facing refusal; null when <paramref name="Ok"/>.</param>
internal readonly record struct PackageReadResult(bool Ok, string? Text, string? Reason);

/// <summary>
/// One bounded, non-blocking open of a <c>.ashpkg</c> — the single door every package read and every
/// package SERVE goes through: <c>import</c>, <c>show</c>, <c>publish</c>, <c>pull</c>, the daemon's
/// folder pass, and the mesh serve endpoints (<c>GET /mesh/v1/index</c> and
/// <c>GET /mesh/v1/pkg/{file}</c>).
///
/// <para>IT LIVES HERE, not inside PkgCommand, for one reason: a primitive that is hard to find is a
/// primitive the next path will not use. The mesh serve daemon was a sixth reader of <c>.ashpkg</c>
/// files that never went through this — it gated on <see cref="FileInfo.Length"/> — while the
/// CHANGELOG said the ceiling was enforced "on every path that reads one". A shared file is what
/// makes that sentence checkable.</para>
///
/// <para>WHY THIS IS NOT A SIZE CHECK. A ceiling read off metadata is not a ceiling.
/// <see cref="FileInfo.Length"/> reports <b>0</b> for a FIFO, for a Unix socket, and for a character
/// device such as <c>/dev/zero</c> — byte-for-byte indistinguishable from an empty regular file —
/// and for a SYMLINK it reports the length of the target's PATH STRING rather than the target: a
/// link to a 400&#160;MB file measures about twenty bytes. So <c>if (file.Length &gt; max) refuse;</c>
/// PASSES for every one of them and the unbounded read behind it runs anyway.
/// <c>ln -s /dev/zero x.ashpkg</c> is an <see cref="OutOfMemoryException"/> that kills the pass,
/// <c>mkfifo x.ashpkg</c> is a permanent hang that wedges every pull on the fleet, and
/// <c>ln -s big-file x.ashpkg</c> simply ignores the limit. A mesh store is a plain synced
/// directory, so each of those is a single command for anyone who can write to the share — cheaper
/// than the oversized file the ceiling was built for.</para>
///
/// <para>ON A SERVER the same metadata gap is worse than a local crash, because the link is followed
/// on someone else's behalf. <c>ln -s /etc/passwd d-secret.ashpkg</c> inside the published directory
/// is an ARBITRARY FILE READ over the network: the name passes the traversal-free pattern, and
/// <see cref="Path.GetFullPath"/> does not resolve symlinks, so a containment check on the resolved
/// path passes too. An atomic no-follow open is what closes it, including a leaf swapped after metadata — one gate,
/// the same gate, for the reader and for the server.</para>
///
/// <para>The fix is structural rather than a list of bad cases: this never issues an open that can
/// block, and never allocates more than <c>maxBytes + 1</c>. The hang and the
/// <see cref="OutOfMemoryException"/> stop being exceptions to catch and become states that cannot
/// be reached.</para>
///
/// <para>Five gates, in order:
/// <list type="number">
///   <item>METADATA (<c>stat</c>/<c>lstat</c> answer for every file type, so nothing blocks here):
///     refuse a directory; refuse anything with a <see cref="FileSystemInfo.LinkTarget"/>, which
///     kills the link-to-device, link-to-huge-file and link-to-anywhere bypasses together.
///     <see cref="FileInfo.Length"/> is never read.</item>
///   <item>AN OPEN THAT CANNOT BLOCK: on Unix <c>open(O_RDONLY|O_NONBLOCK|O_CLOEXEC|O_NOFOLLOW)</c> returns a
///     usable descriptor on a FIFO immediately, where every managed open blocks forever with no
///     exception and no timeout. On Windows the path is opened through the <c>\\?\</c> prefix, which
///     takes it out of DOS-device parsing entirely, and a path that normalizes into the <c>\\.\</c>
///     device namespace — which a BARE reserved name such as <c>CON</c> does, though a name with an
///     extension does not — is refused before any handle is opened.</item>
///   <item>HANDLE TYPE: Unix fstat must identify a regular file; Windows requires a disk handle
///     with neither directory attributes nor a name-surrogate reparse tag.</item>
///   <item>DECLARED LENGTH, taken from the OPENED HANDLE and not from a <see cref="FileInfo"/>: a
///     fast refusal, and the only length any caller may publish.</item>
///   <item>THE CAPPED READ — the gate that actually holds, for callers that read. A regular file's
///     declared size can lie (<c>/proc/self/maps</c> is a regular file reporting zero bytes that
///     yields tens of KB), so the byte cap is enforced DURING the read and never inferred from
///     metadata.</item>
/// </list></para>
/// </summary>
// Parent directories are an operator-controlled trust boundary. A configured store directory may
// itself be a symlink; only the package leaf is untrusted. Writers allowed to replace the store or
// its ancestors can redirect that configured root and must not be granted that authority.
internal static class SafePackageRead
{
    // A deterministic scheduling seam for race regressions. It cannot replace or skip any gate.
    internal static Action<string>? BeforeOpenForTests { get; set; }

    /// <summary>
    /// Reads <paramref name="path"/> as text when it is a regular file of at most
    /// <paramref name="maxBytes"/>; otherwise returns the refusal. Never blocks on a FIFO, never
    /// follows a symlink, never buffers past the cap, and never throws for a hostile file.
    /// </summary>
    /// <param name="path">The candidate <c>.ashpkg</c>.</param>
    /// <param name="maxBytes">The byte ceiling; a package over it is not a certified extension.</param>
    /// <param name="ct">Cancels the read (the daemon's pass tick).</param>
    /// <returns>The package text, or the composed refusal.</returns>
    public static async Task<PackageReadResult> TryReadTextAsync(
        string path, long maxBytes, CancellationToken ct = default)
    {
        var name = DisplayName(path);
        if (!TryOpenBounded(path, maxBytes, out var fs, out var declared, out var reason))
        {
            return Refuse(reason);
        }

        try
        {
            // ───── G4: the gate that holds. Bounded by maxBytes + 1 whatever the metadata claimed. ─────
            return await ReadCappedAsync(fs, name, maxBytes, declared, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        {
            return Refuse($"REFUSED: {name} could not be read: {ex.Message}");
        }
        finally
        {
            fs.Dispose();
        }
    }

    /// <summary>
    /// Gates G0–G3 and hands back the OPEN stream, so a caller that streams bytes somewhere other
    /// than into a string (the mesh serve endpoint) gets exactly the same refusals as a caller that
    /// reads. The caller owns <paramref name="stream"/> and must dispose it.
    ///
    /// <para><paramref name="length"/> comes off the opened handle. That distinction is the whole
    /// point for a server: a <see cref="FileInfo"/> length is a claim about a PATH, and for a
    /// symlink it is the length of the target's path string — which is how a 40&#160;MB file got
    /// advertised as 23 bytes and then served in full. A length nobody can serve a different number
    /// of bytes behind is the only length worth publishing.</para>
    /// </summary>
    /// <param name="path">The candidate <c>.ashpkg</c>.</param>
    /// <param name="maxBytes">The byte ceiling.</param>
    /// <param name="stream">The open, seekable, regular-file stream on success.</param>
    /// <param name="length">The length reported by the opened handle, on success.</param>
    /// <param name="reason">The operator-facing refusal on failure.</param>
    /// <returns>True when the file passed every gate and <paramref name="stream"/> is open.</returns>
    public static bool TryOpenBounded(
        string path, long maxBytes, out FileStream stream, out long length, out string reason)
    {
        stream = null!;
        length = 0;
        var name = DisplayName(path);

        // ───── G0: metadata only. stat/lstat answer for every file type, so nothing here blocks. ─────
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            {
                reason = $"REFUSED: {name} is a directory, not a package.";
                return false;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            reason = $"REFUSED: {name} is not there — it went away between the scan and the read.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        {
            reason = $"REFUSED: {name} could not be examined: {ex.Message}";
            return false;
        }

        // A symlink is refused WITHOUT being followed, because following it IS the bypass: a link's
        // own FileInfo.Length is the length of the target path string, so `ln -s <400MB file> x`
        // measures about twenty bytes and sails past any ceiling, and `ln -s /dev/zero x` measures
        // nine. On the serve path the same link is an arbitrary file read — `ln -s /etc/passwd
        // d-secret.ashpkg` in the published directory has a legal name and a contained resolved
        // path, and only this test stops it. LinkTarget rather than the FileAttributes.ReparsePoint
        // flag on purpose: OneDrive and Dropbox files-on-demand placeholders are reparse points
        // whose LinkTarget is null, and a mesh store living in a synced folder is an ordinary
        // deployment — testing the attribute would refuse every package in one, turning a security
        // fix into an outage.
        if (info.LinkTarget is not null)
        {
            // The refusal names the way OUT as well as the reason. Fail-closed is only defensible if
            // the operator whose honest workflow it just broke can see what to do instead — otherwise
            // the answer they find on their own is to turn the guard off.
            reason =
                $"REFUSED: {name} is a symbolic link (-> {UntrustedText.ForConsole(info.LinkTarget)}), not a regular file. "
              + "A package must be the bytes themselves; a link is an instruction to go read something else. "
              + "Point at the real file instead — give --from or the path argument the directory the "
              + "package actually lives in, or copy it into the store rather than linking it. "
              + "This is about the .ashpkg only: a store DIRECTORY that is itself a symlink still works.";
            return false;
        }

        // The metadata above is diagnostic only. The leaf can change at this exact point; the
        // no-follow open and descriptor type check below are the security boundary.
        BeforeOpenForTests?.Invoke(path);

        // ───── G1: an open that cannot block or follow a swapped leaf. ─────
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            if (!TryOpenWindows(path, name, out handle, out var windowsRefusal))
            {
                reason = windowsRefusal.Reason!;
                return false;
            }
        }
        else if (!NativeOpen.TryOpen(path, out handle, out var openFailure))
        {
            reason = $"REFUSED: {name} could not be opened ({openFailure}) — it is not a readable regular file.";
            return false;
        }

        FileStream fs;
        try
        {
            fs = new FileStream(handle, FileAccess.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        {
            // The FileStream constructor takes ownership only if it succeeds; if it throws, nobody
            // else would ever close the descriptor.
            handle.Dispose();
            reason = $"REFUSED: {name} could not be read: {ex.Message}";
            return false;
        }

        try
        {
            // ───── G2: fifo, socket, pipe — anything the OS will not let us seek. ─────
            if (!fs.CanSeek)
            {
                fs.Dispose();
                reason =
                    $"REFUSED: {name} is a fifo, socket or pipe, not a regular file. "
                  + "Reading one waits for a writer that may never come, and one of these in a synced "
                  + "store would wedge every pull on the fleet.";
                return false;
            }

            // ───── G3: the declared length, off the OPENED HANDLE. Advisory for a reader, and the
            // only length a server may publish. ─────
            long declared;
            try
            {
                declared = fs.Length;
            }
            catch (NotSupportedException)
            {
                fs.Dispose();
                reason = $"REFUSED: {name} has no length — it is not a regular file.";
                return false;
            }
            if (declared > maxBytes)
            {
                fs.Dispose();
                // THIS WORDING IS LOAD-BEARING: scripts/e2e-loop.sh and PkgCommandTests assert on it.
                reason =
                    $"REFUSED: {name} is {declared:N0} bytes; the limit is {maxBytes:N0}. "
                  + "A package this large is not a certified extension — refusing before reading it.";
                return false;
            }

            stream = fs;
            length = declared;
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        {
            fs.Dispose();
            reason = $"REFUSED: {name} could not be read: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// The length of <paramref name="path"/> as measured through every gate, or false when it is not
    /// a servable regular file within <paramref name="maxBytes"/>. Opens and closes; it does not
    /// read a byte.
    ///
    /// <para>For an index that ADVERTISES sizes. Filtering with <see cref="FileInfo.Length"/> put a
    /// symlink to a 40&#160;MB file in the index at 23 bytes and a FIFO at 0 — both under any bound,
    /// because neither number described the thing that would actually be served.</para>
    /// </summary>
    /// <param name="path">The candidate <c>.ashpkg</c>.</param>
    /// <param name="maxBytes">The byte ceiling.</param>
    /// <param name="length">The length reported by the opened handle, on success.</param>
    /// <returns>True when the file passed every gate.</returns>
    public static bool TryMeasure(string path, long maxBytes, out long length)
    {
        if (!TryOpenBounded(path, maxBytes, out var fs, out length, out _))
        {
            return false;
        }
        fs.Dispose();
        return true;
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/>&#160;+&#160;1 bytes and refuses the moment the total
    /// passes the cap. Peak allocation is bounded by construction, which is what makes an
    /// <see cref="OutOfMemoryException"/> unreachable rather than merely caught.
    /// </summary>
    private static async Task<PackageReadResult> ReadCappedAsync(
        FileStream fs, string name, long maxBytes, long declared, CancellationToken ct)
    {
        // Pre-size from the declared length (already known to be <= maxBytes) purely as an
        // allocation hint. A lying length costs a resize, never a byte past the cap.
        var capacity = (int)Math.Clamp(declared, 4096L, Math.Min(maxBytes, 1L << 20));
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var sink = new MemoryStream(capacity);
        try
        {
            long total = 0;
            while (true)
            {
                var want = (int)Math.Min(buffer.Length, maxBytes + 1 - total);
                if (want <= 0)
                {
                    return TooBig(name, maxBytes);
                }
                var read = await fs.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                total += read;
                if (total > maxBytes)
                {
                    return TooBig(name, maxBytes);
                }
                sink.Write(buffer, 0, read);
            }

            // BOM detection, because every one of these call sites used File.ReadAllText before and
            // that strips one. A UTF-8 BOM left at the head of the string is a JSON parse failure
            // the operator would read as a corrupt package.
            sink.Position = 0;
            using var reader = new StreamReader(sink, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new PackageReadResult(true, await reader.ReadToEndAsync(ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // /proc/self/mem is a regular file that opens cleanly and throws EIO on read. That is a
            // refusal row, not a crash.
            return Refuse($"REFUSED: {name} could not be read: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            sink.Dispose();
        }
    }

    private static PackageReadResult TooBig(string name, long maxBytes) => Refuse(
        $"REFUSED: {name} kept producing bytes past the limit of {maxBytes:N0}. "
      + "A package this large is not a certified extension — refusing to buffer it. "
      + "Its reported length was under the limit, which is exactly why the cap is enforced on the read.");

    private static PackageReadResult Refuse(string reason) => new(false, null, reason);

    private static string DisplayName(string path)
    {
        var name = Path.GetFileName(path);
        return UntrustedText.ForConsole(string.IsNullOrEmpty(name) ? path : name);
    }

    private const int FileTypeDisk = 0x0001;

    /// <summary>
    /// Windows has no FIFO reachable by an ordinary filename, so there is no unbounded-block
    /// analogue of the Unix case. A native no-follow open also rejects swapped reparse leaves. Two Windows-specific hazards
    /// remain and are closed here: the reserved DOS device names (NUL, CON, AUX, PRN, COM1-9,
    /// LPT1-9) and named pipes.
    ///
    /// <para>MEASURED, on Windows 11 build 26200, against <c>GetFullPathNameW</c> — the API
    /// <see cref="Path.GetFullPath"/> wraps — and <c>CreateFileW</c>. An earlier version of this
    /// comment claimed a reserved name is resolved to a device EVEN WITH AN EXTENSION, so that
    /// <c>pkg import CON.ashpkg</c> would open the console. It is not, and it does not:
    /// <c>CON.ashpkg</c>, <c>NUL.ashpkg</c>, <c>COM1.ashpkg</c> and <c>aux.ashpkg</c> all normalize
    /// to ordinary paths under the current directory, and opening one returns ERROR_FILE_NOT_FOUND.
    /// The <c>\\.\</c> branch below therefore never fires for a name that carries an extension.</para>
    ///
    /// <para>A BARE reserved name is a different matter and is the case that branch exists for:
    /// <c>CON</c> normalizes to <c>\\.\CON</c> and opens a FILE_TYPE_CHAR handle to the console. So
    /// <c>pkg import CON</c> is real, the refusal is live rather than dead code, and it is also the
    /// right answer for any path an operator spells with an explicit device prefix. What actually
    /// protects the extension case is the <c>\\?\</c> prefix, which takes the path out of DOS-device
    /// parsing altogether — with the FILE_TYPE_DISK check as the backstop that does not depend on
    /// path parsing at all.</para>
    /// </summary>
    private static bool TryOpenWindows(string path, string name, out SafeFileHandle handle, out PackageReadResult refusal)
    {
        handle = null!;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                     or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            refusal = Refuse($"REFUSED: {name} is not a usable path: {ex.Message}");
            return false;
        }

        // \\.\ is the Win32 DEVICE namespace, which is where GetFullPathName resolves a reserved
        // name to. Refuse it outright rather than opening a console or a COM port.
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            refusal = Refuse($"REFUSED: {name} names a DOS device ({full}), not a file.");
            return false;
        }

        // \\?\ hands the path to the object manager unparsed, so no reserved name is resolved and no
        // relative segment is reinterpreted. That is why it is applied to an already fully-qualified
        // path, and why a UNC path must take the \\?\UNC\ form instead of being prefixed twice.
        var win = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
                : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..]
                : @"\\?\" + full;

        try
        {
            handle = CreateFile(win, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                refusal = Refuse($"REFUSED: {name} could not be opened (Win32 {error}).");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        {
            // Windows enforces FileShare where Unix does not, so a package a sync agent is still
            // writing arrives here as a sharing violation. Saying "could not be opened" with the OS
            // message keeps "locked by another process" from reading as "someone planted a device".
            refusal = Refuse($"REFUSED: {name} could not be opened: {ex.Message}");
            return false;
        }

        // The Windows counterpart of the Unix regular-file test. FILE_TYPE_CHAR is CON/NUL/a COM
        // port; FILE_TYPE_PIPE is a named pipe or redirected stdio.
        if (GetFileType(handle) != FileTypeDisk)
        {
            handle.Dispose();
            refusal = Refuse($"REFUSED: {name} is a device or a pipe, not a regular file.");
            return false;
        }

        // OPEN_REPARSE_POINT opens the leaf itself, never its target. Query THIS handle rather
        // than the pathname (which can already name a different file). Cloud placeholders are
        // allowed, but every name-surrogate tag (including symlinks/junctions) is refused.
        if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>())
            || (info.Attributes & 0x10) != 0 || (info.ReparseTag & 0x20000000) != 0)
        {
            handle.Dispose();
            refusal = Refuse($"REFUSED: {name} is not a regular file, or its handle could not be verified.");
            return false;
        }

        refusal = default;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInfo information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetFileType(SafeFileHandle handle);

    /// <summary>
    /// The one thing the managed surface cannot do: open a path with no possibility of blocking.
    /// On a FIFO, <c>new FileStream(path, ...)</c>, <c>File.OpenRead</c>, <c>File.OpenHandle</c> and
    /// even <c>FileOptions.Asynchronous</c> all block forever — no exception, no timeout, nothing to
    /// catch. Only O_NONBLOCK returns a usable descriptor immediately, which is what turns "one
    /// mkfifo in a synced store wedges every pull on the fleet, permanently" into one REFUSED row —
    /// and, on the serve path, what keeps one planted FIFO from wedging every Kestrel connection
    /// until the node stops answering at all.
    /// </summary>
    private static class NativeOpen
    {
        // Explicit supported ABIs. Unknown systems fail closed instead of guessing flags or
        // degrading to a managed open, which can follow symlinks and block forever on FIFOs.
        private const int LinuxNonBlock = 0x800, LinuxCloExec = 0x80000, LinuxNoFollow = 0x20000;
        private const int MacNonBlock = 0x0004, MacCloExec = 0x1000000, MacNoFollow = 0x0100;
        private const int Eintr = 4;

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open(byte[] path, int flags, int mode);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int Fstat(SafeFileHandle handle, [Out] byte[] stat);

        [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
        private static extern int MacFstat(SafeFileHandle handle, [Out] byte[] stat);

        /// <summary>Opens a leaf without following links; only a verified regular file survives.</summary>
        public static bool TryOpen(string path, out SafeFileHandle handle, out string failure)
        {
            handle = null!;
            failure = string.Empty;
            var mac = OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst();
            var architecture = RuntimeInformation.ProcessArchitecture;
            if ((!OperatingSystem.IsLinux() && !mac)
                || (architecture != Architecture.X64 && architecture != Architecture.Arm64))
            {
                failure = "safe native package reads are unavailable on this platform";
                return false;
            }

            var flags = mac ? MacNonBlock | MacCloExec | MacNoFollow
                            : LinuxNonBlock | LinuxCloExec | LinuxNoFollow;
            var utf8 = new byte[Encoding.UTF8.GetByteCount(path) + 1];
            Encoding.UTF8.GetBytes(path, utf8);
            try
            {
                int fd;
                do { fd = Open(utf8, flags, 0); }
                while (fd < 0 && Marshal.GetLastPInvokeError() == Eintr);
                if (fd < 0)
                {
                    failure = $"errno {Marshal.GetLastPInvokeError()}";
                    return false;
                }
                handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);

                // stat is ABI-specific: Linux x86_64 st_mode is uint32 at 24, Linux aarch64
                // uint32 at 16; Darwin stat64 st_mode is uint16 at 4 on both supported CPUs.
                // Reserve more than every supported struct size (Linux 144/128; Darwin 144).
                // No pathname metadata is consulted after open, so swapping the name cannot turn
                // a checked file into a FIFO, device or directory at the subsequent read/serve.
                var stat = new byte[512];
                int result;
                do { result = mac && architecture == Architecture.X64 ? MacFstat(handle, stat) : Fstat(handle, stat); }
                while (result < 0 && Marshal.GetLastPInvokeError() == Eintr);
                var mode = mac ? BitConverter.ToUInt16(stat, 4)
                    : BitConverter.ToUInt32(stat, architecture == Architecture.X64 ? 24 : 16);
                if (result != 0 || (mode & 0xF000) != 0x8000)
                {
                    handle.Dispose();
                    failure = "not a regular file (fifo, socket or pipe, device, directory, or unverifiable handle)";
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                handle?.Dispose();
                failure = "safe native package reads are unavailable on this platform";
                return false;
            }
        }
    }
}
