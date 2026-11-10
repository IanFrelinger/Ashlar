using System.Diagnostics;
using System.Text;
using Ashlar.CLI.Packaging;
using Ashlar.Tests.CLI.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// The bypasses a metadata ceiling could never see. Every read of a <c>.ashpkg</c> — import, show,
/// publish, pull, and the daemon's folder pass — goes through
/// <see cref="SafePackageRead.TryReadTextAsync"/>, so these are the claims the whole guard rests on.
///
/// <para>The defect this pins: <c>FileInfo.Length</c> reports <b>0</b> for a FIFO and for a symlink
/// to a character device, and for a symlink to a real file it reports the length of the target's
/// PATH STRING — nine bytes for <c>-&gt; /dev/zero</c>, about twenty for a link to a 400&#160;MB
/// file. A guard written as <c>if (file.Length &gt; max) refuse;</c> therefore PASSES for all three
/// and the unbounded read behind it runs anyway: an OutOfMemoryException, a permanent hang, or a
/// size limit silently defeated. The primitive must refuse each one without blocking, without
/// throwing, and without buffering past the cap.</para>
/// </summary>
public sealed class SafePackageReadTests : IDisposable
{
    private readonly string _dir;

    /// <summary>A small ceiling, so the boundary cases cost bytes rather than megabytes.</summary>
    private const long Max = 4096;

    public SafePackageReadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ashlar-safe-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string At(string name) => Path.Combine(_dir, name);

    private string WriteBytes(string name, int count, byte fill = (byte)'x')
    {
        var path = At(name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, count).ToArray());
        return path;
    }

    private static void Mkfifo(string path)
    {
        using var p = Process.Start(new ProcessStartInfo("mkfifo", path) { UseShellExecute = false })!;
        p.WaitForExit(20_000);
        p.ExitCode.Should().Be(0, "the fixture itself must exist before the claim means anything");
    }

    // ───────────────────────── the ordinary, portable cases ─────────────────────────

    [Fact]
    public async Task ARegularFileUnderTheCeiling_isReadWhole()
    {
        var path = At("good.ashpkg");
        await File.WriteAllTextAsync(path, "{\"ok\":true}");

        var result = await SafePackageRead.TryReadTextAsync(path, Max);

        result.Ok.Should().BeTrue();
        result.Text.Should().Be("{\"ok\":true}");
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task AFileExactlyAtTheCeiling_isAccepted()
    {
        // The cap is "no MORE than max", so max itself must still be a package. An off-by-one here
        // would refuse legitimate packages at the boundary and look like a mesh outage.
        var path = WriteBytes("at-limit.ashpkg", (int)Max);

        var result = await SafePackageRead.TryReadTextAsync(path, Max);

        result.Ok.Should().BeTrue();
        result.Text!.Length.Should().Be((int)Max);
    }

    [Fact]
    public async Task AFileOneByteOverTheCeiling_isRefusedInTheWordingTheHarnessAssertsOn()
    {
        var path = WriteBytes("over-limit.ashpkg", (int)Max + 1);

        var result = await SafePackageRead.TryReadTextAsync(path, Max);

        result.Ok.Should().BeFalse();
        result.Text.Should().BeNull();
        // scripts/e2e-loop.sh and PkgCommandTests grep for this sentence. Changing it is a
        // cross-repo contract change, not a wording tweak.
        result.Reason.Should().Contain("bytes");
        result.Reason.Should().Contain("refusing before reading it");
    }

    [Fact]
    public async Task AnEmptyRegularFile_isReadAsEmpty_notRefused()
    {
        var path = WriteBytes("empty.ashpkg", 0);

        var result = await SafePackageRead.TryReadTextAsync(path, Max);

        result.Ok.Should().BeTrue("an empty file is a parse refusal one layer down, not a read refusal");
        result.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task AUtf8ByteOrderMark_isStripped_asFileReadAllTextDid()
    {
        // Every one of these call sites used File.ReadAllText before, which strips a BOM. A BOM left
        // at the head of the string is a JSON parse failure an operator would read as a corrupt
        // package — a silent regression the byte-level rewrite could easily have introduced.
        var path = At("bom.ashpkg");
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        bytes.AddRange(Encoding.UTF8.GetBytes("{\"ok\":true}"));
        await File.WriteAllBytesAsync(path, bytes.ToArray());

        var result = await SafePackageRead.TryReadTextAsync(path, Max);

        result.Ok.Should().BeTrue();
        result.Text.Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task ADirectory_isRefused_notThrown()
    {
        var path = At("a-directory.ashpkg");
        Directory.CreateDirectory(path);

        var result = await SafePackageRead.TryReadTextAsync(path, Max);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("directory");
    }

    [Fact]
    public async Task AMissingFile_isRefused_notThrown()
    {
        // A sync client removing a file between the directory scan and the read is ordinary, and it
        // must be a REFUSED row rather than an exception that ends the pass.
        var result = await SafePackageRead.TryReadTextAsync(At("never-existed.ashpkg"), Max);

        result.Ok.Should().BeFalse();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    // ───────────────────────── the Unix-only bypasses ─────────────────────────

    [UnixOnlyFact("ln -s")]
    public async Task ASymlinkToAnOversizedFile_isRefused_theBypassAMetadataCeilingCouldNotSee()
    {
        var target = WriteBytes("real-oversize.bin", (int)Max * 4);
        var link = At("linked-giant.ashpkg");
        File.CreateSymbolicLink(link, target);

        // The bypass itself, pinned: the OLD guard read this number and let the file through. No
        // device node and no OutOfMemoryException needed — just a link, and the limit is gone.
        new FileInfo(link).Length.Should().BeLessThan(Max,
            "FileInfo.Length on a symlink is the length of the TARGET PATH STRING, not the target");

        var result = await SafePackageRead.TryReadTextAsync(link, Max);

        result.Ok.Should().BeFalse("a symlink is refused without being followed");
        result.Reason.Should().Contain("not a regular file");
        result.Reason.Should().Contain("symbolic link");
    }

    [UnixOnlyFact("ln -s /dev/zero")]
    public async Task ASymlinkToACharacterDevice_isRefused_notAnOutOfMemoryException()
    {
        var link = At("zero.ashpkg");
        File.CreateSymbolicLink(link, "/dev/zero");

        var result = await SafePackageRead.TryReadTextAsync(link, Max);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("not a regular file");
    }

    [UnixOnlyFact("mkfifo")]
    public async Task AFifo_isRefusedPromptly_andNeverBlocks()
    {
        // The whole reason for the O_NONBLOCK open. Every managed open — FileStream, File.OpenRead,
        // File.OpenHandle, even FileOptions.Asynchronous — blocks here forever with no exception and
        // no timeout, and one mkfifo in a synced store then wedges every pull on the fleet.
        var fifo = At("hang.ashpkg");
        Mkfifo(fifo);

        // Task.Run wraps the CALL, not the task it returns, and that is the whole guard. G0-G3 are
        // synchronous — the first await inside TryReadTextAsync is the capped read, which a
        // blocking open never reaches — so a regression blocks in the method's synchronous prefix
        // and no Task ever comes back to time-bound. Timing the returned task would leave the
        // timeout unreachable and hang the run instead of failing it.
        var read = Task.Run(() => SafePackageRead.TryReadTextAsync(fifo, Max));
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(read, finished);   // a timeout here IS the bug, so it must not be a hung test run

        var result = await read;
        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("fifo, socket or pipe");
    }

    [UnixOnlyFact("ln -s")]
    public async Task LeafSwappedAfterMetadata_isRefusedWithoutReadingTheTarget()
    {
        var candidate = WriteBytes("swap.ashpkg", 10);
        var outside = At("private-owned-marker.txt");
        await File.WriteAllTextAsync(outside, "NOT_A_PACKAGE_PRIVATE_MARKER");
        var swapped = false;
        SafePackageRead.BeforeOpenForTests = path =>
        {
            if (path != candidate) return;
            File.Delete(candidate);
            File.CreateSymbolicLink(candidate, outside);
            swapped = true;
        };
        try
        {
            var result = await SafePackageRead.TryReadTextAsync(candidate, Max);
            swapped.Should().BeTrue("the swap must happen after the production metadata check");
            result.Ok.Should().BeFalse();
            result.Text.Should().BeNull("no bytes from the outside target may be returned");
        }
        finally { SafePackageRead.BeforeOpenForTests = null; }
    }

    [UnixOnlyFact("directory symlink")]
    public async Task OperatorConfiguredSymlinkedStoreDirectory_stillWorks()
    {
        var real = Path.Combine(_dir, "real-store");
        Directory.CreateDirectory(real);
        await File.WriteAllTextAsync(Path.Combine(real, "good.ashpkg"), "package");
        var configured = Path.Combine(_dir, "configured-store");
        Directory.CreateSymbolicLink(configured, real);
        var result = await SafePackageRead.TryReadTextAsync(Path.Combine(configured, "good.ashpkg"), Max);
        result.Ok.Should().BeTrue();
        result.Text.Should().Be("package");
    }

    [UnixOnlyFact("/dev/null")]
    public async Task ASeekableCharacterDevice_isRefusedBeforeTheRead()
    {
        // /dev/null is seekable and immediately returns EOF. A byte limit alone accepts it.
        var result = await SafePackageRead.TryReadTextAsync("/dev/null", Max);
        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("not a regular file");
    }

    [UnixOnlyFact("/dev/zero")]
    public async Task ACharacterDevice_isRefusedBeforeTheRead()
    {
        var clock = Stopwatch.StartNew();
        var result = await SafePackageRead.TryReadTextAsync("/dev/zero", Max);
        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("not a regular file");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }
}
