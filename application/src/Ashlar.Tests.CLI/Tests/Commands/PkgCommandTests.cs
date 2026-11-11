using System.CommandLine;
using System.Diagnostics;
using FluentAssertions;
using Ashlar.CLI.Commands;
using Ashlar.Tests.CLI.Helpers;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// #458: <c>ashlar pkg pull --from</c> takes a mesh store DIRECTORY. An HTTP URL used to be coerced
/// into a DirectoryInfo, mangling the URL into a nonsense path that then "does not exist"; it must be
/// refused legibly, pointing at the daemon's ASHLAR_MESH_PEERS instead. The ordinary directory path
/// must keep working after the option changed from DirectoryInfo to string.
/// </summary>
public sealed class PkgCommandTests : IDisposable
{
    private readonly string _dir;

    public PkgCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ashlar-pkg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<(int rc, string stdout, string stderr)> RunAsync(params string[] args)
    {
        var so = new StringWriter();  // not disposed: a disposed writer left on Console poisons later tests
        var se = new StringWriter();
        Console.SetOut(so);
        Console.SetError(se);
        try
        {
            var root = new RootCommand();
            root.AddCommand(new PkgCommand());
            var rc = await root.InvokeAsync(args).ConfigureAwait(false);
            return (rc, so.ToString(), se.ToString());
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }
    }

    [Theory]
    [InlineData("http://peer.example:8080/store")]
    [InlineData("https://peer.example/store")]
    public async Task Pull_fromHttpUrl_isRefusedLegibly_notMangledIntoAPath(string url)
    {
        var project = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "ashlar.yaml"), "kind: Application\n");
        await File.WriteAllTextAsync(Path.Combine(project, "ashlar.policy.yaml"), "kind: Policy\n");

        var (rc, _, stderr) = await RunAsync("pkg", "pull", "--from", url, "--path", project);

        rc.Should().NotBe(0);
        stderr.Should().Contain("takes a directory");
        stderr.Should().Contain("ASHLAR_MESH_PEERS", "the refusal must point at the daemon's HTTP-pull path");
        stderr.Should().NotContain("no such peer store", "the URL must not be mangled into a path that 'does not exist'");
    }

    [Fact]
    public async Task Pull_fromAnEmptyDirectoryStore_stillWorksAfterTheStringRefactor()
    {
        // Proves --from as a plain directory string still reaches the normal path: an existing but
        // empty store is "nothing to pull", exit 0 — no URL false-positive, no coercion breakage.
        var project = Path.Combine(_dir, "proj2");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "ashlar.yaml"), "kind: Application\n");
        await File.WriteAllTextAsync(Path.Combine(project, "ashlar.policy.yaml"), "kind: Policy\n");

        var store = Path.Combine(_dir, "empty-store");
        Directory.CreateDirectory(store);

        var (rc, stdout, _) = await RunAsync("pkg", "pull", "--from", store, "--path", project);

        rc.Should().Be(0);
        stdout.Should().Contain("nothing to pull");
    }

    private async Task<string> ProjectAsync(string name)
    {
        var project = Path.Combine(_dir, name);
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "ashlar.yaml"), "kind: Application\n");
        await File.WriteAllTextAsync(Path.Combine(project, "ashlar.policy.yaml"), "kind: Policy\n");
        return project;
    }

    // Sparse. An honest regular file over the ceiling is refused on its DECLARED length before a
    // byte is read, so there is nothing to fill in; 16 MiB + 1 KiB is one kilobyte past the CLI's
    // ceiling, which is also ExtensionPackaging's parse ceiling — the two layers are separated by
    // exactly this margin, which is why the assertions below are on the WORDING and not on the exit
    // code: both layers refuse with 65, and only the wording says whether the file was bounded
    // before it was buffered. The cases the declared length CANNOT catch — a FIFO, a symlink to a
    // device, a symlink to a real oversized file — are pinned in SafePackageReadTests and, at this
    // level, by the two Unix-only facts at the bottom of this class.
    private static void WriteOversizedPackage(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(16L * 1024 * 1024 + 1024);
    }

    [Fact]
    public async Task Publish_ofAnOversizedPackage_isRefusedBeforeTheFileIsRead()
    {
        // A .ashpkg handed to publish routinely arrived off a share, so it is no more trustworthy
        // here than at import. Publish read it unbounded and was rescued only by TryOpen's char cap —
        // a cap that measures a string which exists only once the read has already succeeded, or
        // exhausted memory trying.
        var giant = Path.Combine(_dir, "giant.ashpkg");
        WriteOversizedPackage(giant);
        var store = Path.Combine(_dir, "publish-store");

        var (rc, _, stderr) = await RunAsync("pkg", "publish", giant, "--store", store);

        rc.Should().Be(65, "an oversized package is a verification refusal, exactly as for pkg import and pkg show");
        stderr.Should().Contain("bytes", "the refusal must come from the pre-read byte guard, not the post-read char cap");
        stderr.Should().Contain("refusing before reading it");
        stderr.Should().NotContain("characters", "reaching the char cap means the whole file was buffered first");
        Directory.Exists(store).Should().BeFalse("a refused package never reaches the mesh store");
    }

    [Fact]
    public async Task Pull_ofAnOversizedPackage_refusesTheRowAndKeepsPulling()
    {
        var project = await ProjectAsync("proj-oversized");

        var store = Path.Combine(_dir, "planted-store");
        Directory.CreateDirectory(store);
        // Ordinal order puts the planted giant FIRST, so a pull that ends on it never reaches the
        // second file — which is the denial being pinned. A mesh store is a plain directory, so
        // planting needs no publish and no key.
        WriteOversizedPackage(Path.Combine(store, "aaa-planted.ashpkg"));
        await File.WriteAllTextAsync(Path.Combine(store, "zzz-junk.ashpkg"), "{ not a package");

        var (rc, stdout, _) = await RunAsync("pkg", "pull", "--from", store, "--path", project);

        rc.Should().Be(65, "a refusal is never masked — the pass exits non-zero so a script notices");
        stdout.Should().Contain("bytes", "the row must be refused by the byte guard, before the read");
        stdout.Should().Contain("refusing before reading it");
        stdout.Should().Contain("zzz-junk.ashpkg", "the loop must continue past the planted row");
        stdout.Should().Contain("pulled 2");
        stdout.Should().Contain("refused/rejected 2", "the oversized row is counted as refused, not skipped");
    }

    // file:/// spelled the way each platform spells it, built by hand rather than by asking Uri to
    // parse a bare path — that parse is exactly the host-dependent behaviour the fix refuses to
    // rely on, so a test must not lean on it either.
    private static string FileUrl(string directory)
    {
        var full = Path.GetFullPath(directory).Replace('\\', '/');
        return full.StartsWith('/') ? "file://" + full : "file:///" + full;
    }

    [Fact]
    public async Task Pull_fromFileUrl_reachesTheDirectoryItNames()
    {
        // A mesh store is a folder, and a sync client or a file manager hands out its location as a
        // file:// URL. That URL names the same directory a plain path does, so pull must reach it
        // instead of coercing "file:///…" into a DirectoryInfo and reporting the store as missing.
        var project = await ProjectAsync("proj-file-url");
        var store = Path.Combine(_dir, "file-url-store");
        Directory.CreateDirectory(store);

        var (rc, stdout, stderr) = await RunAsync("pkg", "pull", "--from", FileUrl(store), "--path", project);

        rc.Should().Be(0, "a file: URL names a directory, the one URL shape pull can honour");
        stdout.Should().Contain("nothing to pull");
        stderr.Should().NotContain("no such peer store", "the operator's own store must not be reported missing");
    }

    [Theory]
    [InlineData("ftp://peer.example/store")]
    [InlineData("ssh://peer.example/store")]
    [InlineData("s3://bucket/store")]
    public async Task Pull_fromAnUnsupportedScheme_isRefusedLegibly_notMangledIntoAPath(string url)
    {
        // Nothing stands behind these transports here. Coercing them into a DirectoryInfo produced
        // "no such peer store: <nonsense>", which reads as the store being gone rather than as the
        // scheme being unsupported.
        var project = await ProjectAsync("proj-scheme");

        var (rc, _, stderr) = await RunAsync("pkg", "pull", "--from", url, "--path", project);

        rc.Should().NotBe(0);
        stderr.Should().Contain("takes a directory");
        stderr.Should().NotContain("no such peer store", "the URL must not be mangled into a path that 'does not exist'");
    }

    [Fact]
    public async Task Pull_fromADirectoryWhoseNameLooksLikeUrlSyntax_isPassedThroughUntouched()
    {
        // Guards the tempting WRONG fix for file:// — routing every Uri.IsFile token through
        // Uri.LocalPath. On Unix an ordinary rooted path parses as an absolute file: URI, so that fix
        // would percent-decode "%20" and truncate at "#", then report a live store as missing. This
        // test cannot fail against the pre-fix code; it exists to fail against that one.
        var project = await ProjectAsync("proj-urlish");
        var store = Path.Combine(_dir, "store%20v1#2");
        Directory.CreateDirectory(store);

        var (rc, stdout, _) = await RunAsync("pkg", "pull", "--from", store, "--path", project);

        rc.Should().Be(0, "a directory name is not URL syntax to be decoded");
        stdout.Should().Contain("nothing to pull");
    }

    [Fact]
    public async Task Pull_fromAnEmptyToken_isRefusedLegibly_notAStackTrace()
    {
        // `new DirectoryInfo("")` throws, so the operator got an unhandled exception where a refusal
        // belongs — the same shape as any other unusable --from.
        var project = await ProjectAsync("proj-empty");

        var (rc, _, stderr) = await RunAsync("pkg", "pull", "--from", string.Empty, "--path", project);

        rc.Should().NotBe(0);
        stderr.Should().Contain("takes a directory");
        stderr.Should().NotContain("Unhandled exception");
    }

    private static void Mkfifo(string path)
    {
        using var p = Process.Start(new ProcessStartInfo("mkfifo", path) { UseShellExecute = false })!;
        p.WaitForExit(20_000);
        p.ExitCode.Should().Be(0, "the fixture itself must exist before the claim means anything");
    }

    [UnixOnlyFact("mkfifo and ln -s")]
    public async Task Pull_pastAPlantedFifoAndDeviceLink_refusesEachRowAndStillReachesTheStoreBehindThem()
    {
        // The headline regression. A mesh store is a plain synced directory, so `mkfifo x.ashpkg`
        // and `ln -s /dev/zero x.ashpkg` are one command each for anyone who can write to the
        // share — no oversized file needed. Before the bounded read, the first was a permanent hang
        // that wedged every pull on the fleet and the second was an OutOfMemoryException that ended
        // the pass at the banner: no REFUSED row, no summary, and the legitimate package behind them
        // never processed. Planted first in Ordinal order, so a pass that dies on them proves it.
        var project = await ProjectAsync("proj-nonregular");
        var store = Path.Combine(_dir, "nonregular-store");
        Directory.CreateDirectory(store);
        Mkfifo(Path.Combine(store, "aaa-hang.ashpkg"));
        File.CreateSymbolicLink(Path.Combine(store, "aab-zero.ashpkg"), "/dev/zero");
        await File.WriteAllTextAsync(Path.Combine(store, "zzz-junk.ashpkg"), "{ not a package");

        // Task.Run wraps the CALL, not the task it returns. SafePackageRead's open sits in the
        // synchronous prefix of the pull — nothing between here and it awaits anything that
        // yields — so a regression blocks before any Task exists to time-bound, and a timeout
        // around the returned task could never fire. On its own thread the hang fails this test
        // instead of hanging the run, which is the only way a regression stays readable.
        var run = Task.Run(() => RunAsync("pkg", "pull", "--from", store, "--path", project));
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(run, finished);   // a timeout here IS the bug — the FIFO hang, unfixed
        var (rc, stdout, _) = await run;

        rc.Should().Be(65, "a refusal is never masked — the pass exits non-zero so a script notices");
        stdout.Should().Contain("fifo, socket or pipe");
        stdout.Should().Contain("symbolic link");
        stdout.Should().Contain("zzz-junk.ashpkg", "the pass must reach the package behind the planted rows");
        stdout.Should().Contain("pulled 3");
        stdout.Should().Contain("refused/rejected 3", "each planted row is refused, not skipped");
    }

    // ─────────── the forged ADMITTED row, through the package's own formatVersion ───────────

    // ONE RAW CONTROL BYTE APPEARS NOWHERE IN THIS STRING. It is a JSON string literal, so what is
    // on disk is the ASCII characters backslash-r, backslash-u-0-0-1-b and so on — six printable
    // characters each. That is the whole reason four rounds of review cleared this sink: they tested
    // MALFORMED json, where System.Text.Json hex-escapes a control byte into its own syntax-error
    // message and nothing hostile reaches the terminal. This document PARSES. The escapes are
    // decoded by the deserializer, land in a `required` string the sender chose, and are then quoted
    // into a refusal composed BEFORE the seal and both signatures are looked at.
    //
    // What it does on a terminal, if it is printed raw: the CR returns the cursor to column zero of
    // the row that was just written, so "  × REFUSED  evil.ashpkg  · REFUSED: unsupported package
    // format '" is overwritten by a green counterfeit naming a sealer fingerprint — the one part of
    // a row the code's own documentation says a sender cannot choose. The trailing SGR 8 (conceal)
    // hides the "'; expected 'ashpkg/v1'." tail that would otherwise show through.
    private const string ForgedFormatVersionJson =
        "ashpkg/v1\\r\\u001b[32m  \\u2713 package verifies  verdict signed ed25519:597a8e"
      + " \\u00b7 sealed ed25519:597a8e \\u00b7 1 file(s)\\u009bK\\u001b[K\\u001b[0m\\u001b[8m";

    // `record` and `files` are present (both are `required`, so a missing one is a DESERIALIZER
    // error and never reaches the format check) but null, which is enough: the formatVersion
    // comparison is the first thing ExtensionPackaging.TryOpen does with a document that parsed.
    // PascalCase on purpose: ExtensionPackaging's JsonSerializerOptions set no naming policy and
    // leave PropertyNameCaseInsensitive at its default, so these are the names Pack writes and the
    // only names TryOpen will bind. A camelCase document is a MISSING-required-property error, which
    // is the malformed-JSON path — the very path earlier reviews mistook for this one.
    private static string PackageWithFormatVersion(string formatVersionJsonLiteral) =>
        "{\"FormatVersion\":\"" + formatVersionJsonLiteral + "\",\"Record\":null,\"Files\":null}";

    /// <summary>Console.WriteLine emits Environment.NewLine, which on Windows contains a CR. Only a
    /// LONE CR forges a row, so the line breaks this CLI wrote are normalised away before the
    /// assertion — otherwise the test would fail on Windows for an honest reason.</summary>
    private static string WithoutOwnLineBreaks(string output) => output.Replace("\r\n", "\n");

    private static void AssertNoForgery(string output, string where)
    {
        WithoutOwnLineBreaks(output).Should().NotContain("\r",
            $"{where}: a lone CR returns the cursor to column zero and lets the rest of the "
            + "sender's string overwrite the refusal that names the file");
        output.Should().NotContain("\u001b[32m",
            $"{where}: SGR 32 paints the counterfeit green; this CLI never colours a refusal green");
        output.Should().NotContain("\u001b[8m",
            $"{where}: SGR 8 conceals the tail of the refusal that would give the forgery away");
        output.Should().NotContain("\u009b",
            $"{where}: U+009B is a CSI with no ESC in front of it");
        output.Should().Contain("\ufffd",
            $"{where}: the escapes must be SHOWN defused, not deleted — the operator has to see that "
            + "the file carried them");
        output.Should().Contain("ashpkg/v1",
            $"{where}: the operator still has to be told what the offending value was");
    }

    [Fact]
    public async Task ShowImportPublishAndPull_ofAForgedFormatVersion_cannotRepaintTheRefusalRow()
    {
        // The field is `required` and is compared BEFORE the seal, before the record signature, and
        // before the sealer/admitter binding — so when it reaches the console it has been verified
        // by nothing at all. All four pkg verbs quote it, and `pull`'s `default:` row is the one a
        // previous wave half-fixed: it escaped the row's LEFT half (the filename) and left the RIGHT
        // half (this reason) raw.
        var project = await ProjectAsync("proj-forged-format");
        var forged = Path.Combine(_dir, "evil.ashpkg");
        await File.WriteAllTextAsync(forged, PackageWithFormatVersion(ForgedFormatVersionJson));

        var (showRc, _, showErr) = await RunAsync("pkg", "show", forged);
        showRc.Should().Be(65);
        AssertNoForgery(showErr, "pkg show");

        var (importRc, importOut, importErr) = await RunAsync("pkg", "import", forged, "--path", project);
        importRc.Should().Be(65);
        AssertNoForgery(importOut + importErr, "pkg import");

        var (publishRc, _, publishErr) = await RunAsync(
            "pkg", "publish", forged, "--store", Path.Combine(_dir, "forged-store"));
        publishRc.Should().Be(65);
        AssertNoForgery(publishErr, "pkg publish");

        var store = Path.Combine(_dir, "forged-pull-store");
        Directory.CreateDirectory(store);
        File.Copy(forged, Path.Combine(store, "evil.ashpkg"));

        var (pullRc, pullOut, _) = await RunAsync("pkg", "pull", "--from", store, "--path", project);
        pullRc.Should().Be(65);
        AssertNoForgery(pullOut, "pkg pull");
        pullOut.Should().Contain("refused/rejected 1", "the forged package is refused, not skipped");
    }

    [Fact]
    public async Task AFormatVersionOfUnboundedLength_isTruncatedAtTheConsoleBoundary()
    {
        // formatVersion has no cap of its own — anything under ExtensionPackaging's 16 MiB document
        // ceiling fits — so escaping alone would still let a sender choose how many characters the
        // operator's terminal has to render. Replacement is one-for-one, so 100,000 hostile
        // characters would be 100,000 U+FFFDs: the refusal that named the file, and every row before
        // it, scrolled out of the scrollback.
        var huge = Path.Combine(_dir, "huge-format.ashpkg");
        await File.WriteAllTextAsync(huge, PackageWithFormatVersion(new string('A', 100_000)));

        var (rc, _, stderr) = await RunAsync("pkg", "show", huge);

        rc.Should().Be(65);
        stderr.Should().Contain("unsupported package format",
            "the operator still learns which check refused the file");
        stderr.Should().Contain("truncated", "what was dropped must be named, or the line reads as the sender's actual text");
        // The count is the whole refusal's length, not the field's, because the bound is applied to
        // the composed line at the console boundary — one place, covering every reason
        // ExtensionPackaging can build, not just this one.
        stderr.Should().MatchRegex(@"truncated: 100,0\d\d characters, limit 2,000",
            "the operator is told how much there was and what the ceiling is");
        stderr.Length.Should().BeLessThan(4096,
            "the whole refusal is bounded — a sender does not get to choose the size of the output");
    }

    [UnixOnlyFact("ln -s /dev/zero")]
    public async Task ImportAndPublishAndShow_ofASymlinkToADevice_areRefused_notOutOfMemoryCrashes()
    {
        // publish, import and show take a CALLER-SUPPLIED path, so they are reachable directly
        // rather than only through a store — including import, which the refactor had treated as
        // the already-safe reference and which crashed on this exactly like the other two.
        var project = await ProjectAsync("proj-device-link");
        var link = Path.Combine(_dir, "zero.ashpkg");
        File.CreateSymbolicLink(link, "/dev/zero");
        var store = Path.Combine(_dir, "device-link-store");

        var (importRc, _, importErr) = await RunAsync("pkg", "import", link, "--path", project);
        importRc.Should().Be(65);
        importErr.Should().Contain("not a regular file");

        var (publishRc, _, publishErr) = await RunAsync("pkg", "publish", link, "--store", store);
        publishRc.Should().Be(65);
        publishErr.Should().Contain("not a regular file");
        Directory.Exists(store).Should().BeFalse("a refused package never reaches the mesh store");

        var (showRc, _, showErr) = await RunAsync("pkg", "show", link);
        showRc.Should().Be(65);
        showErr.Should().Contain("not a regular file");
    }

    // ─────────── #492: a long symlink target must not cut the refusal's guidance ───────────

    // The parts of the symlink refusal the operator ACTS on. They follow the quoted target, so they
    // are what a line-level bound cuts first.
    private static readonly string[] SymlinkGuidance =
    [
        "not a regular file",
        "Point at the real file instead",
        "a store DIRECTORY that is itself a symlink still works.",
    ];

    // About 3,000 characters: past UntrustedText.DefaultMaxChars on its own, and under Linux's
    // 4,095-byte symlink target limit. It need not exist; the link is refused without being followed.
    //
    // LINUX ONLY, and not as a convenience. macOS caps a symlink target at 1,023 bytes, so this
    // fixture cannot be created there (PathTooLongException). The cut it guards against needs a
    // target past roughly 1,600 characters, so on macOS the bug cannot happen either.
    private static string LongTarget() =>
        "/" + string.Join("/", Enumerable.Repeat(new string('t', 200), 15)) + "/payload.ashpkg";

    [LinuxOnlyFact("symlink target over 1,023 bytes")]
    public async Task Import_ofASymlinkWithALongTarget_keepsTheExplanationAndRemediation()
    {
        var project = await ProjectAsync("proj-long-link");
        var link = Path.Combine(_dir, "long-link.ashpkg");
        var target = LongTarget();
        target.Length.Should().BeGreaterThan(2000, "the fixture must exceed the line bound or it proves nothing");
        File.CreateSymbolicLink(link, target);

        var (rc, _, stderr) = await RunAsync("pkg", "import", link, "--path", project);

        rc.Should().Be(65);
        stderr.Should().Contain("is a symbolic link (-> /tttt");
        stderr.Should().ContainAll(SymlinkGuidance);
        stderr.Should().Contain("limit 256]", "the target is bounded as a component, and says so");
        stderr.Should().NotContain(target, "the sender does not get to choose how much of the line it fills");
        CountOf(stderr, "[truncated:").Should().Be(1, "only the target is cut; the composed refusal never reaches the line bound");
    }

    [LinuxOnlyFact("symlink target over 1,023 bytes")]
    public async Task Pull_pastASymlinkWithALongTarget_keepsTheGuidanceOnTheRefusedRow()
    {
        // The pull row is a second renderer: "× REFUSED  <name> · <reason>", with the reason bounded
        // on its own. Pinned separately so a fix in one renderer cannot hide a cut in the other.
        var project = await ProjectAsync("proj-long-link-pull");
        var store = Path.Combine(_dir, "long-link-store");
        Directory.CreateDirectory(store);
        File.CreateSymbolicLink(Path.Combine(store, "aaa-long.ashpkg"), LongTarget());

        var (rc, stdout, _) = await RunAsync("pkg", "pull", "--from", store, "--path", project);

        rc.Should().Be(65);
        var row = stdout.Split('\n').Single(line => line.Contains("REFUSED", StringComparison.Ordinal)
                                                     && line.Contains("aaa-long.ashpkg", StringComparison.Ordinal));
        row.Should().ContainAll(SymlinkGuidance);
        CountOf(row, "[truncated:").Should().Be(1, "only the target is cut; the row's reason never reaches the line bound");
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
