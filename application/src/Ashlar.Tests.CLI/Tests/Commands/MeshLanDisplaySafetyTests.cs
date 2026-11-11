using System.Text.Json;
using Ashlar.CLI.Commands;
using Ashlar.CLI.Commands.BackgroundAgent;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// #492 residue 1: <c>ashlar mesh lan</c> prints names an unauthenticated LAN beacon chose. It used a
/// hand-rolled <c>char.IsControl</c> filter, which is Unicode category Cc only, so the bidi overrides
/// and isolates (U+202A–U+202E, U+2066–U+2069, U+200E/U+200F, U+061C) reached the operator's terminal
/// intact. These drive the command's handler body against a <c>mesh-peers.json</c> written
/// the way the daemon writes it, and pin that every terminal-actionable character arrives as U+FFFD
/// while ordinary Unicode, including RTL scripts and astral emoji, is printed exactly as announced.
/// </summary>
[Collection("MeshIntegration")]
public sealed class MeshLanDisplaySafetyTests : IDisposable
{
    private const string GoodFingerprint = "ed25519:0123456789abcdef";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ashlar-mesh-lan-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _scopes = [];

    public MeshLanDisplaySafetyTests()
    {
        Directory.CreateDirectory(_directory);
        _scopes.Add(new EgressProcessStateScope(reset: true));
        _scopes.Add(new EnvironmentVariableScope("ASHLAR_STATE_DIR", _directory));
    }

    public void Dispose()
    {
        foreach (var scope in _scopes.AsEnumerable().Reverse()) scope.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<string> BidiControls() => new()
    {
        "\u061C", "\u200E", "\u200F", "\u2028", "\u2029",
        "\u202A", "\u202B", "\u202C", "\u202D", "\u202E",
        "\u2066", "\u2067", "\u2068", "\u2069",
    };

    [Theory]
    [MemberData(nameof(BidiControls))]
    public async Task Lan_escapesEveryBidiControlInABeaconName(string control)
    {
        // The issue's reproduction: `e2 80 ae` (U+202E) was emitted once. Each of these REORDERS a
        // row rather than moving the cursor, so a filter that only knows Cc lets every one through.
        WritePeers(Peer($"evil{control}fdp.exe"));

        var (exitCode, output) = await LanAsync();

        exitCode.Should().Be(0, output);
        output.Should().NotContain(control, "a bidi control from the network must never reach the terminal");
        output.Should().Contain("evil\uFFFDfdp.exe", "escaped, not stripped: the operator sees that the name carried one");
    }

    [Fact]
    public async Task Lan_escapesC0AndC1Controls_andKeepsOnePeerOnOneRow()
    {
        // ESC starts a sequence, CR overwrites the row, LF forges a second one, U+009B is CSI with no
        // ESC at all — the C1 case a Cc filter did catch, kept so a future swap cannot lose it.
        WritePeers(Peer("a\u001B[2Jb\rc\nd\u009B31me"));

        var (exitCode, output) = await LanAsync();

        exitCode.Should().Be(0, output);
        output.Should().NotContainAny("\u001B", "\r", "\u009B");
        output.Should().Contain("a\uFFFD[2Jb\uFFFDc\uFFFDd\uFFFD31me");
        RowsOf(output).Should().ContainSingle("one peer is one row; an LF in a name must not forge a second");
    }

    [Fact]
    public async Task Lan_printsOrdinaryUnicodeNamesUnchanged()
    {
        // Not every non-ASCII name is hostile. Hebrew and Arabic need no bidi CONTROL to render RTL,
        // and an emoji is a surrogate pair the escaper must not split or replace.
        const string name = "caf\u00E9-\u6771\u4EAC-\u05E9\u05DC\u05D5\u05DD-\u0645\u0631\u062D\u0628\u0627-\uD83C\uDF89";
        WritePeers(Peer(name));

        var (exitCode, output) = await LanAsync();

        exitCode.Should().Be(0, output);
        output.Should().Contain(name);
        output.Should().NotContain("\uFFFD", "nothing in an ordinary name is terminal-actionable");
    }

    [Fact]
    public async Task Lan_escapesTheFingerprintToo_inTheRowAndInTheTrustHint()
    {
        // mesh-peers.json is a state file, so the fingerprint on this row has not necessarily been
        // through MeshBeacon.TryParse. It is printed twice — once as a column, once inside the
        // `ashlar keys trust` hint — and both copies must be the escaped one.
        WritePeers(Peer("node", fingerprint: "ed25519:\u001B]0;owned\u0007\u202Eabc"));

        var (exitCode, output) = await LanAsync();

        exitCode.Should().Be(0, output);
        output.Should().NotContainAny("\u001B", "\u0007", "\u202E");
        output.Should().Contain("`ashlar keys trust ed25519:\uFFFD]0;owned\uFFFD\uFFFDabc`");
    }

    [Fact]
    public async Task Lan_boundsANameToTheBeaconsOwnCap()
    {
        // A beacon is refused past 64 characters; a state file is not a beacon, so the display bounds
        // it again rather than trusting that the file came from TryParse.
        WritePeers(Peer(new string('n', 5000)));

        var (exitCode, output) = await LanAsync();

        exitCode.Should().Be(0, output);
        output.Should().NotContain(new string('n', 65));
        output.Should().Contain("[truncated:");
    }

    [Fact]
    public async Task Lan_ordinaryRow_keepsItsExistingShape()
    {
        // The output contract, so the escaper swap is provably a no-op for an honest peer.
        WritePeers(Peer("alpha-node"));

        var (exitCode, output) = await LanAsync();

        exitCode.Should().Be(0, output);
        output.Should().Contain("Peers on the local network (1):");
        RowsOf(output).Should().ContainSingle()
            .Which.Should().MatchRegex(@"^  alpha-node {11}" + GoodFingerprint.Replace(".", "\\.") + @" +10\.0\.0\.7:7400 +seen \d+s ago  \[");
    }

    private static DiscoveredPeer Peer(string name, string fingerprint = GoodFingerprint) =>
        new(name, fingerprint, "10.0.0.7", 7400, DateTimeOffset.UtcNow);

    private void WritePeers(params DiscoveredPeer[] peers) =>
        // Serialized the way the daemon persists the table, so control characters are on disk as
        // JSON escapes and arrive as real characters only after the command deserializes them.
        File.WriteAllText(Path.Combine(_directory, "mesh-peers.json"), JsonSerializer.Serialize(peers));

    private static IEnumerable<string> RowsOf(string output) =>
        output.Split('\n').Where(line => line.StartsWith("  ", StringComparison.Ordinal) && line.Contains("seen ", StringComparison.Ordinal));

    // The `mesh lan` handler is `EgressCliInvocation.Exit(ExecuteLan(Console.Out))`, and that Exit is
    // Environment.Exit — invoking it through the parser ends the test host. So these drive
    // ExecuteLan, the whole of the handler's body, with the writer the handler passes it.
    private static Task<(int ExitCode, string Output)> LanAsync()
    {
        var stdout = new StringWriter();
        var exitCode = MeshCommand.ExecuteLan(stdout);
        return Task.FromResult((exitCode, stdout.ToString()));
    }
}
