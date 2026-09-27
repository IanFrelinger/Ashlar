using System.Text;
using System.Text.RegularExpressions;
using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Which on-disk anchors a gate store's signature posture is allowed to rest on, and who can write
/// them.
///
/// <para><b>Why this blocks a merge.</b> SPEC-006 S-6 decides whether a missing signature is
/// corruption from anchors that are NOT the record being judged, and every anchor it uses today is
/// a file inside the state root — the directory the attacker is attacking. That is the largest
/// disclosed residual in the rule. The rows in <see cref="Anchors"/> are where somebody had to
/// decide, in writing, whether a new input to the posture is reachable by the actor being defended
/// against. An input added without a row is an input nobody weighed, and
/// <see cref="Only_the_listed_anchors_decide_this_stores_posture"/> exists to make one impossible
/// to add quietly.</para>
///
/// <para><b>Why the scan is a closed world rather than a list of forbidden needles.</b> The first
/// draft of this class forbade four spellings — <c>Environment.GetEnvironmentVariable</c>,
/// <c>File.ReadAllText</c>, <c>File.ReadAllLines</c>, <c>Directory.GetFiles</c> — and three
/// reviewers independently pointed out that a fifth spelling walks past all four:
/// <c>DirectoryInfo.EnumerateFiles</c>, <c>new StreamReader(path).ReadToEnd()</c>,
/// <c>Environment.GetEnvironmentVariables()</c>, <c>File.OpenText</c>. A deny-list of reads is only
/// ever as wide as its author's imagination. So this class inverts it: every member access on a
/// filesystem or environment gateway type in either scanned file, and every construction of a
/// reader or stream, is inventoried in <see cref="Gateways"/>, and the fact asserts SET EQUALITY in
/// both directions. A spelling nobody listed is red whatever it is called; a listed spelling that
/// has vanished is red too, because a row that overstates the surface is how a shrinking inventory
/// stops meaning anything. <see cref="ForeignChannels"/> then closes the two holes the closed world
/// itself has: a fully-qualified <c>System.IO.File.ReadAllText</c> is invisible to a scan anchored
/// on the short type name, and a <c>using static</c> erases the type name altogether.</para>
///
/// <para><b>Needle discipline.</b> Anchor paths are pinned as quoted STRING LITERALS, never as
/// expressions containing parameter names: renaming <c>stateRoot</c> is an ordinary, meaningless
/// edit, and a security gate that reddens on one teaches the next person to edit the gate rather
/// than to ask what changed. The one exception is <see cref="Anchor.RootedBy"/>, which deliberately
/// spells the parameter, because the whole content of the second fact is that the anchor is built
/// out of the caller's state root — see that fact's documentation.</para>
///
/// <para><b>Deliberately NOT an anchor row:</b> <c>OperatorKey.TrustedPublicKeysBase64</c>. That is
/// the signer-PINNING set — key material outside the state root that says which keys this READER
/// vouches for, never that this store is signed. Confusing the two is how a reader concludes that a
/// keyed reader holds a memory of this store, which it does not, and that is the false belief the
/// changelog and the spec both used to carry. It has its own residual sentence in S-6, because the
/// directory it lives in is writable in a configuration this repository documents
/// (<c>NativeBundle.StageApp</c> points <c>ASHLAR_KEY_DIR</c> inside the project). It is pinned
/// here at exactly one resolution so that it cannot quietly BECOME an anchor.</para>
///
/// <para><b>Not the same fact as
/// <see cref="GateRecordReadFunnelConventionTests.One_resolution_point_serves_every_read"/>.</b>
/// That one counts resolution POINTS: one posture per store operation. This one bounds the INPUTS a
/// posture may be resolved from. <c>GateSigningActivation.TryRead( == 1</c> is asserted in both
/// places, for two different reasons, and this is said out loud here rather than left to look like
/// an accident: there it means "not resolved twice", here it means "the marker anchor is consulted
/// once, so there is no second answer for an attacker to race between".</para>
///
/// <para>Hermetic: pure file reads, no build, no SDK, no network, no environment variable. Helpers
/// are duplicated per class, which is the house pattern — every helper on
/// <c>GateRecordReadFunnelConventionTests</c> and <c>AppendOnlyWriterConventionTests</c> is private
/// and not reusable, and this class must not take a dependency on another test class's
/// internals.</para>
///
/// <para><b>These are tripwires, not proofs.</b> A text scan is defeated by an extension method
/// that wraps the read, by reflection, or by a helper in a third file that the store merely calls.
/// Framing a scan as a proof is how a gate goes quiet (<c>docs/HowGatesGoQuiet.md</c>).</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed partial class GateStoreAnchorProvenanceConventionTests
{
    private const string StorePath = "src/Ashlar.Manifest/Admission/GateStore.cs";
    private const string MarkerPath = "src/Ashlar.Manifest/Admission/GateSigningActivation.cs";
    private const string SpecPath = "docs/specs/SPEC-006-keys-and-signing.md";

    /// <summary>
    /// The sentence in SPEC-006 §4 S-6 that discloses what the row set below shows. Spelled
    /// mid-sentence, with no em-dash and no leading capital, so markdown emphasis markers and a
    /// sentence-initial capital cannot break an ordinal match after flattening.
    /// </summary>
    private const string AttackerWritableDisclosure = "anchor this rule rests on is a file in the state root";

    private static readonly string[] Roots = ["src", "application", "commercial"];

    /// <summary>A project is a test project if it pulls in the test SDK. Nothing else is reliable.</summary>
    private const string TestSdkMarker = "Microsoft.NET.Test.Sdk";

    /// <summary>
    /// Channels of outside state that neither scanned file touches at all, and that the closed
    /// gateway world in <see cref="Gateways"/> would not catch on its own.
    ///
    /// <para>The first four are the closed world's own blind spots, and they matter more than the
    /// exotic ones: <c>GatewayMember</c> anchors on a SHORT type name behind a
    /// <c>(?&lt;![\w.])</c> lookbehind, so <c>System.IO.File.ReadAllText(sidecar)</c> and
    /// <c>System.Environment.GetEnvironmentVariable("...")</c> both slide past it, and
    /// <c>using static System.IO.File;</c> or a <c>global::</c> qualification erases the type name
    /// the scan is looking for. <c>GetEnvironment</c> is a bare substring on purpose, so it catches
    /// the singular, the plural and any qualification of either.</para>
    ///
    /// <para><c>/*</c> is a precondition of the scan rather than a security claim:
    /// <see cref="StripCommentLines"/> drops only lines that START with <c>//</c>, so a block
    /// comment would leave prose inside the scanned text and could redden the gateway inventory for
    /// a comment. Whoever unpicked that confusion would loosen the scan to make it stop. Keeping
    /// these two files free of block comments is cheaper than making the stripper smart.</para>
    /// </summary>
    private static readonly string[] ForeignChannels =
    [
        "System.IO.",
        "System.Environment",
        "using static",
        "global::",
        "GetEnvironment",
        "HttpClient",
        "WebClient",
        "Socket",
        "NamedPipe",
        "Process.",
        "Registry",
        "AppContext.",
        "AppDomain.",
        "Assembly.",
        "Console.",
        "Marshal.",
        "DllImport",
        "MemoryMappedFile",
        "ZipFile",
        "FileSystemWatcher",
        "DriveInfo",
        "SpecialFolder",
        "GetTempPath",
        "IConfiguration",
        "RepoPathResolver",
        "/*",
    ];

    /// <summary>One on-disk input the store's signature posture rests on.</summary>
    /// <param name="Name">Short stable name, used in failure messages.</param>
    /// <param name="Location">Where it lives, relative to the state root.</param>
    /// <param name="DeclaredIn">The production file that spells its path literal.</param>
    /// <param name="PathLiterals">
    /// The quoted string literals that spell this anchor's path, each of which must appear exactly
    /// once in <paramref name="DeclaredIn"/> and nowhere in the other scanned file. A path spelled
    /// twice is two paths that can drift.
    /// </param>
    /// <param name="RootedBy">
    /// The production expression that shows this anchor is built by combining the CALLER'S state
    /// root with a constant. This is the one needle in the class that deliberately spells a
    /// parameter name, because it is the whole substance of
    /// <see cref="While_every_anchor_is_attacker_writable_the_spec_must_say_so"/>: the anchor being
    /// attacker-writable is not a bool somebody typed into this table, it is the fact that its path
    /// is <c>stateRoot</c> plus a constant, read out of production.
    /// </param>
    /// <param name="RepoWideConfinedAs">
    /// The unquoted needle whose presence in any OTHER production file is a second reader or writer
    /// of this anchor — or null when the literal is too ordinary for a repo-wide scan to mean
    /// anything, which is said per row rather than assumed.
    /// </param>
    /// <param name="AttackerWritable">
    /// True when the actor S-6 defends against can write it. Every row is true today and SPEC-006
    /// S-6 carries the sentence that says so; the day one is false,
    /// <see cref="While_every_anchor_is_attacker_writable_the_spec_must_say_so"/> forces the spec
    /// edit in the same commit.
    /// </param>
    /// <param name="Why">What this anchor proves and what it costs.</param>
    private sealed record Anchor(
        string Name,
        string Location,
        string DeclaredIn,
        string[] PathLiterals,
        string RootedBy,
        string? RepoWideConfinedAs,
        bool AttackerWritable,
        string Why);

    private static readonly Anchor[] Anchors =
    [
        new("in-store-marker",
            "(state root)/gate-signing.json",
            MarkerPath,
            PathLiterals: ["\"gate-signing.json\""],
            RootedBy: "Path.Combine(stateRoot, FileName)",
            RepoWideConfinedAs: "gate-signing.json",
            AttackerWritable: true,
            "the operator's signed declaration about this store: the activation instant and the "
            + "grandfather inventory, both inside the signed bytes. It is a SIBLING of gates/, under "
            + "the same state root, so whoever can write a record can delete it — and an actor who "
            + "has stripped every signature deletes this too, which is why the first residual in "
            + "S-6 says the store's whole account of itself lives where the attack is. Confined "
            + "repo-wide because the literal is distinctive: a second production file that spells it "
            + "can read or write the marker without going through the type that verifies its "
            + "signature."),

        new("derived-record-anchor",
            "(state root)/gates/*.json",
            StorePath,
            PathLiterals: ["\"gates\"", "\"*.json\""],
            RootedBy: "Path.Combine(stateRoot, \"gates\")",
            RepoWideConfinedAs: null,
            AttackerWritable: true,
            "any record here whose signature verifies AND comes from a key this reader vouches for "
            + "is intrinsic proof the store is signed. Also inside the state root, and the anchor set "
            + "shrinks to nothing when every signature is stripped — the other half of the same "
            + "residual. NOT confined repo-wide: `gates` and `*.json` are ordinary substrings that "
            + "occur in a dozen unrelated stores and CLI verbs, so a repo-wide scan of them would be "
            + "noise a reviewer learns to wave through, which is worse than no scan. The confinement "
            + "that does the work for this row is the enumeration equality in the first fact — every "
            + "directory the store enumerates is rooted at _dir."),
    ];

    /// <summary>
    /// One spelling through which a scanned file reaches the filesystem, the environment or the
    /// clock, with why it is allowed to be there.
    /// </summary>
    /// <param name="Owner">The scanned file this row belongs to.</param>
    /// <param name="Spelling">
    /// The normalized form <see cref="GatewaySpellings"/> produces: <c>Type.Member</c>, or
    /// <c>new Type</c> for a construction.
    /// </param>
    /// <param name="Why">What it is for. A row whose reason is "no idea" is a row to delete.</param>
    private sealed record Gateway(string Owner, string Spelling, string Why);

    /// <summary>
    /// The complete inventory of both files' gateways to outside state, measured on this tree.
    /// <see cref="Only_the_listed_anchors_decide_this_stores_posture"/> asserts equality in both
    /// directions, so this table is the closed world: a read spelled any way at all that is not
    /// here is red, and a row whose spelling has gone is red as a stale row.
    /// </summary>
    private static readonly Gateway[] Gateways =
    [
        // ── the store ──────────────────────────────────────────────────────────────────────────
        new(StorePath, "Directory.CreateDirectory",
            "creates gates/ in the constructor, so the anchor directory exists before anything reads it"),
        new(StorePath, "Directory.EnumerateFiles",
            "the derived-record anchor: the *.json listing in ParseAllAsync and the *.json.tmp sweep, "
            + "both rooted at _dir — which the enumeration equality in the first fact pins"),
        new(StorePath, "Environment.TickCount64",
            "the cross-process lock's 15s deadline. A monotonic tick count, not an input to the "
            + "posture, and the only Environment member in the file — the point of listing it is that "
            + "a SECOND Environment member would be"),
        new(StorePath, "File.Create",
            "the .json.tmp a record is written through before the atomic move"),
        new(StorePath, "File.Delete",
            "the stray .json.tmp sweep, under the lock, where no writer can be mid-move"),
        new(StorePath, "File.Exists",
            "RecordAsync's refusal to overwrite an existing record. A write guard, not a read of the "
            + "posture"),
        new(StorePath, "File.Move",
            "the atomic publish of a written record over its .json.tmp"),
        new(StorePath, "File.OpenRead",
            "the ONE place a record's bytes enter this process, inside ParseAllAsync. Pinned at one "
            + "occurrence below: a second open is a second file whose path no row names"),
        new(StorePath, "Path.Combine",
            "three, and only three: gates/, the .lock inside it, and PathFor's leaf. Pinned at three "
            + "below"),
        new(StorePath, "Path.GetFileName",
            "the leaf a refusal names to the operator, and the canonical name the id-collision "
            + "tie-break compares a planted file against. Never a new path — it only shortens one"),
        new(StorePath, "new FileStream",
            "the FileShare.None lock handle the OS releases when the holder dies"),

        // ── the marker type ────────────────────────────────────────────────────────────────────
        new(MarkerPath, "Directory.CreateDirectory",
            "the state root, before each of the two write paths"),
        new(MarkerPath, "File.Delete",
            "cleans up the temp file when a losing writer's move finds the marker already there"),
        new(MarkerPath, "File.Exists",
            "the \"no marker at all\" answer in ReadVerified, and the lost-race detection in Activate"),
        new(MarkerPath, "File.Move",
            "the atomic publish of the marker: once refusing to overwrite, once permitting it for the "
            + "operator's explicit verb"),
        new(MarkerPath, "File.ReadAllText",
            "the ONE read of the one anchor this type owns. Pinned at one occurrence below, and the "
            + "reason File.ReadAllText cannot be a blanket zero across both files"),
        new(MarkerPath, "File.WriteAllText",
            "the temp file behind each of the two write paths"),
        new(MarkerPath, "Path.Combine",
            "the single PathFor. Pinned at one occurrence below: every path this type touches derives "
            + "from it, so a second Path.Combine is a second file"),
    ];

    /// <summary>
    /// The two production files that resolve a gate store's signature posture read exactly the
    /// anchors <see cref="Anchors"/> lists, through exactly the gateways <see cref="Gateways"/>
    /// lists, and nothing else — no environment variable, no sidecar witness, no second directory
    /// scan, no out-of-band channel. A new anchor cannot appear without a row.
    ///
    /// <para><b>Why a row matters.</b> Every anchor in the row set is attacker-writable, and that is
    /// the largest disclosed residual in SPEC-006 S-6. The row is where somebody had to decide, in
    /// writing, whether a new input to the posture is reachable by the actor being defended against.
    /// An input added without a row is an input nobody weighed.</para>
    ///
    /// <para><b>What actually bites here, limb by limb.</b> The path-literal counts are the weakest
    /// limb and are honest about it: they catch a duplicated path, not a new one. The load is
    /// carried by (a) the gateway set equality, which is red for ANY spelling of ANY read that is
    /// not in the table, including the four this class's first draft would have missed; (b) the
    /// enumeration equality, an equality rather than the magic number 2 so a legitimate third
    /// enumeration UNDER gates/ is allowed and a first enumeration outside it is not; (c) the
    /// per-file read counts, which catch a second use of a gateway that is already listed — the one
    /// evasion set equality alone cannot see; and (d) the repo-wide confinement of the marker's file
    /// name, which catches a second production file reaching the anchor directly.</para>
    ///
    /// <para><b>Mutations.</b> Add any second source of posture to either file and one of those
    /// limbs is red and names the file: <c>Environment.GetEnvironmentVariable("ASHLAR_GATE_SIGNED")</c>
    /// or <c>Directory.GetFiles</c> or <c>new StreamReader</c> or <c>DirectoryInfo.EnumerateFiles</c>
    /// trips the set equality; <c>System.IO.File.ReadAllLines</c> trips
    /// <see cref="ForeignChannels"/>; a second <c>File.OpenRead</c> of a sidecar, or a fourth
    /// <c>Path.Combine</c>, trips a count; a second <c>Directory.EnumerateFiles</c> rooted anywhere
    /// but <c>_dir</c> trips the equality; and a new production file that spells
    /// <c>gate-signing.json</c> trips the confinement.</para>
    /// </summary>
    [Fact]
    public void Only_the_listed_anchors_decide_this_stores_posture()
    {
        var root = RepoPathResolver.FindRepoRoot();
        Directory.Exists(Path.Combine(root, "src", "Ashlar.Manifest")).Should().BeTrue(
            "RepoPathResolver.FindRepoRoot returns the CURRENT directory rather than throwing when "
            + "Ashlar.sln is not found, so an unguarded scan of a misresolved root would pass by "
            + "reading nothing at all");

        Anchors.Should().HaveCount(2,
            "an empty or collapsed row set makes both facts in this class vacuous");
        Anchors.Select(a => a.DeclaredIn).OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            new[] { MarkerPath, StorePath }.OrderBy(p => p, StringComparer.Ordinal),
            "the rows must describe the files this fact actually scans, or they are decoration");

        var scanned = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorePath] = StripCommentLines(File.ReadAllText(Path.Combine(root, StorePath))),
            [MarkerPath] = StripCommentLines(File.ReadAllText(Path.Combine(root, MarkerPath))),
        };

        // ── (a) each row's path literals are spelled once, in that row's own file ──────────────
        foreach (var anchor in Anchors)
        {
            anchor.PathLiterals.Should().NotBeEmpty(
                "an anchor row with no path literal pins nothing about where the anchor lives. Row: {0}",
                anchor.Name);

            foreach (var literal in anchor.PathLiterals)
            {
                Occurrences(scanned[anchor.DeclaredIn], literal).Should().Be(1,
                    "the {0} anchor at {1} is spelled ONCE in {2}, as {3}. Zero means the row now "
                    + "describes an anchor that no longer exists and the rest of this fact is "
                    + "measuring a file it does not understand; two means two paths that can drift "
                    + "apart, and the posture would then depend on which one a given code path built",
                    anchor.Name, anchor.Location, anchor.DeclaredIn, literal);

                foreach (var other in scanned.Keys.Where(k => !string.Equals(k, anchor.DeclaredIn, StringComparison.Ordinal)))
                {
                    Occurrences(scanned[other], literal).Should().Be(0,
                        "{0} spells the {1} anchor's path {2}, which is declared in {3}. Two files "
                        + "that each build the same path is exactly the drift the single spelling "
                        + "above exists to prevent — derive it from the declaring type instead",
                        other, anchor.Name, literal, anchor.DeclaredIn);
                }
            }
        }

        // ── (b) the marker's file name is reachable from ONE production file ───────────────────
        Anchors.Any(a => a.RepoWideConfinedAs is not null).Should().BeTrue(
            "at least one anchor must carry a needle distinctive enough to confine repo-wide, or "
            + "this limb silently stops running while still looking present");

        foreach (var anchor in Anchors.Where(a => a.RepoWideConfinedAs is not null))
        {
            ProductionSources(root)
                .Where(s => s.Code.Contains(anchor.RepoWideConfinedAs!, StringComparison.Ordinal))
                .Select(s => s.Path)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Should().Equal(
                    new[] { anchor.DeclaredIn },
                    "a second production file that spells `{0}` can read or write the {1} anchor "
                    + "without going through {2} — the type that VERIFIES the marker's signature "
                    + "before anyone is allowed to believe it. A reader that skips that step honours "
                    + "an unsigned marker, which lets anyone with filesystem write access brick a "
                    + "keyless store by planting a far-past activation instant",
                    anchor.RepoWideConfinedAs, anchor.Name, anchor.DeclaredIn);
        }

        // ── (c) the closed world: no gateway to outside state that nobody listed ───────────────
        Gateways.Select(g => g.Owner).Distinct(StringComparer.Ordinal)
            .OrderBy(o => o, StringComparer.Ordinal)
            .Should().Equal(
                new[] { MarkerPath, StorePath }.OrderBy(p => p, StringComparer.Ordinal),
                "the gateway table must cover exactly the files this fact scans; a table entry for a "
                + "file nobody reads is decoration, and a scanned file with no entries makes its "
                + "whole inventory vacuous");

        Gateways.Select(g => g.Owner + " " + g.Spelling).Should().OnlyHaveUniqueItems(
            "a duplicated row lets a reviewer delete one copy and believe the surface shrank");

        Gateways.Should().OnlyContain(g => g.Why.Length > 0,
            "a gateway row with no reason is a row nobody weighed, which is the thing this class "
            + "exists to prevent");

        foreach (var (file, code) in scanned)
        {
            var found = GatewaySpellings(code);
            var listed = Gateways
                .Where(g => string.Equals(g.Owner, file, StringComparison.Ordinal))
                .Select(g => g.Spelling)
                .ToList();

            var unlisted = found.Except(listed, StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            var vanished = listed.Except(found, StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();

            unlisted.Should().BeEmpty(
                "{0} now reaches outside the process through a gateway nobody listed: {1}. This is a "
                + "THIRD input to the store's posture — an environment variable, a sidecar witness, a "
                + "second directory — and no row means nobody decided whether the actor being "
                + "defended against can write it. The order of operations is: add the Anchor row, "
                + "decide its AttackerWritable, edit SPEC-006 S-6 if the answer is false, add the "
                + "Gateway row with its reason, and only then add the read",
                file, string.Join(", ", unlisted));

            vanished.Should().BeEmpty(
                "these gateway rows for {0} no longer match anything in the file: {1}. A row that "
                + "overstates the surface reads as accounted-for debt that is in fact gone, which is "
                + "how an inventory stops meaning anything — and it also means the scan is now "
                + "measuring a file whose shape it does not know, so its silence elsewhere is worth "
                + "nothing. Delete the row with the change that removed the read",
                file, string.Join(", ", vanished));

            foreach (var channel in ForeignChannels)
            {
                Occurrences(code, channel).Should().Be(0,
                    "{0} must not contain `{1}`. The gateway inventory above anchors on a SHORT type "
                    + "name behind a lookbehind that rejects a preceding dot, so a fully-qualified or "
                    + "statically-imported read is invisible to it, and a channel that is not a file "
                    + "at all — a socket, the registry, a config object — has no spelling there to "
                    + "match. Each of these is zero in both files today, which is what makes the "
                    + "closed world above trustworthy; the day one is not, the closed world has a "
                    + "hole of unknown size",
                    file, channel);
            }
        }

        var store = scanned[StorePath];
        var marker = scanned[MarkerPath];

        // ── (d) a second use of an ALREADY-LISTED gateway is the one evasion set equality misses ─
        Occurrences(store, "Directory.EnumerateFiles(")
            .Should().Be(Occurrences(store, "Directory.EnumerateFiles(_dir,"),
            "every directory the store enumerates is the records directory named in the "
            + "derived-record-anchor row. An EQUALITY rather than the number 2 on purpose: a "
            + "legitimate third enumeration under gates/ is allowed, and a first enumeration rooted "
            + "anywhere else is a new anchor that the gateway table cannot see, because "
            + "Directory.EnumerateFiles is already listed for this file");

        Occurrences(store, "File.OpenRead(").Should().Be(1,
            "exactly one place opens a record's bytes — ParseAllAsync, behind the single funnel every "
            + "read shares. A second File.OpenRead is a second file entering the posture through a "
            + "gateway this table already permits, so nothing else here would notice it");

        Occurrences(store, "Path.Combine(").Should().Be(3,
            "three paths, each named in the store's gateway rows: gates/, the .lock inside it, and "
            + "PathFor's leaf. A fourth is a fourth path — a sidecar witness, a second anchor "
            + "directory — built through a call this table already permits");

        Occurrences(marker, "File.ReadAllText(").Should().Be(1,
            "one read, in ReadVerified, of the one anchor this type owns. This is why "
            + "File.ReadAllText cannot be a blanket zero across both files, and why it is pinned "
            + "per-file instead: the store has none, and the gateway table for the store says so");

        Occurrences(marker, "Path.Combine(").Should().Be(1,
            "every path this type touches derives from the single PathFor. A second Path.Combine is a "
            + "second file, and the marker type is the one place a reader trusts to have verified a "
            + "signature before believing what it read");

        Occurrences(store, "GateSigningActivation.TryRead(").Should().Be(1,
            "the in-store-marker anchor is consulted once per operation. Asserted in "
            + "GateRecordReadFunnelConventionTests too, for a different reason — there it means the "
            + "posture is not resolved twice; here it means the anchor has one answer, and the marker "
            + "is a file the attacker can delete between two reads of it");

        Occurrences(store, "OperatorKey.TrustedPublicKeysBase64").Should().Be(1,
            "the signer-PINNING set, resolved once in the constructor. It is deliberately NOT an "
            + "Anchor row — see this class's documentation — because it says which keys this READER "
            + "vouches for, never that this store is signed. A second resolution, especially one "
            + "inside a read path, is how it becomes an anchor by accident");
    }

    /// <summary>
    /// Every anchor the store's posture rests on is built out of the caller's own state root — the
    /// directory the attacker is attacking — and for as long as that is true SPEC-006 §4 S-6 must
    /// carry the sentence that discloses it.
    ///
    /// <para><b>Three limbs. One of them cannot fail today, and that is stated rather than
    /// hidden.</b></para>
    ///
    /// <para><i>Limb one is an ADDITION TRIPWIRE, not a fact.</i> "No row is operator-side" reads a
    /// bool out of this class's own hand-written table, so no production change can redden it, and
    /// on this tree both rows are attacker-writable, so it is a tautology. It is kept for one
    /// narrow purpose: the day someone adds an anchor outside the state root, the sentence in S-6
    /// stops being true, and this limb forces the spec edit into the SAME commit rather than a
    /// follow-up that never lands. A reviewer who calls it decoration is right about what it proves;
    /// the answer is that a dead branch documented as waiting for a named future event is honest in
    /// a way a missing branch is not. It is deliberately NOT written as
    /// <c>Anchors.Where(a =&gt; !a.AttackerWritable).Should().BeEmpty()</c> alone, because that
    /// spelling looks like a security check and is not one.</para>
    ///
    /// <para><i>Limb two is live and reads production.</i> This is where the claim "every anchor is
    /// attacker-writable" is actually earned. It is not earned by the bool: it is earned by
    /// <see cref="Anchor.RootedBy"/>, the expression in production that builds each anchor's path
    /// from the <c>stateRoot</c> its CALLER passed in, plus a constant. An anchor built that way is
    /// inside the directory whose records are being forged, full stop — there is no configuration in
    /// which it is not. Move either anchor out of the state root, or reach it through anything but
    /// the caller's own root, and this limb reddens without anybody having to remember to update a
    /// table.</para>
    ///
    /// <para><i>Limb three is live and reads the spec.</i> Deleting or rewording the disclosure
    /// sentence reddens this fact today, which is the mutation that keeps the residual from reading
    /// as covered debt. Turning a disclosed residual into an undisclosed hole is a security change
    /// wearing a copy edit, and this is the limb that refuses it.</para>
    ///
    /// <para><b>Why the phrase is matched through <see cref="Flatten"/> and not with
    /// <c>Contains</c>.</b> Measured, not assumed: <c>docs/specs/SPEC-006-keys-and-signing.md</c> is
    /// CRLF in a Windows working tree and LF on the Linux cert-gate runner
    /// (<c>.gitattributes</c> has <c>* text=auto</c>), and its sentences wrap mid-clause — this one
    /// breaks between "a file in the" and "state root". A raw <c>Contains</c> against a sentence
    /// that IS present therefore finds nothing, and an unflattened doc assertion is a second vacuous
    /// tripwire of exactly the kind this class exists to avoid. The needle is flattened too, so it
    /// may be spelled across several source lines with <c>+</c>.</para>
    ///
    /// <para><b>Why this phrase and not one <c>GateSignatureResidualTests</c> pins.</b> This class
    /// holds a sentence no other fact holds, so it adds disclosure coverage rather than restating a
    /// row of the residual inventory.</para>
    /// </summary>
    [Fact]
    public void While_every_anchor_is_attacker_writable_the_spec_must_say_so()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var specPath = Path.Combine(root, SpecPath);
        File.Exists(specPath).Should().BeTrue(
            "this fact is vacuous if the spec moved; point {0} at the new path", nameof(SpecPath));

        Anchors.Should().NotBeEmpty(
            "an empty row set makes every limb below pass by describing nothing");

        // ── limb one: the addition tripwire, honest about being one ───────────────────────────
        var operatorSide = Anchors
            .Where(a => !a.AttackerWritable)
            .Select(a => a.Name + " at " + a.Location)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        operatorSide.Should().BeEmpty(
            "an anchor outside the state root has appeared: {0}. This limb cannot be reddened by a "
            + "production change — it reads this class's own table — and it exists only for this "
            + "moment: SPEC-006 S-6's residual block states that every anchor this rule rests on is "
            + "a file in the state root, and that is no longer true. Edit the residual in THIS "
            + "commit and relax this limb with it. The mechanism and its disclosure move together, "
            + "or the disclosure is a lie",
            string.Join(", ", operatorSide));

        // ── limb two: the rootedness the bool above only claims, read out of production ────────
        var scanned = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorePath] = StripCommentLines(File.ReadAllText(Path.Combine(root, StorePath))),
            [MarkerPath] = StripCommentLines(File.ReadAllText(Path.Combine(root, MarkerPath))),
        };

        foreach (var anchor in Anchors.Where(a => a.AttackerWritable))
        {
            anchor.RootedBy.Should().NotBeEmpty(
                "an attacker-writable row has to say WHERE production roots the anchor, or the claim "
                + "rests on nothing but the bool. Row: {0}", anchor.Name);

            Occurrences(scanned[anchor.DeclaredIn], anchor.RootedBy).Should().Be(1,
                "the {0} anchor is attacker-writable because {1} builds it as `{2}` — the state root "
                + "its CALLER handed in, plus a constant — which puts it inside the very directory "
                + "whose records are being forged. That is the claim, and this is the only limb of "
                + "this fact that reads production rather than this class's table. Zero occurrences "
                + "means the anchor is now reached some other way and nobody has re-decided whether "
                + "the attacker can still write it; two means two spellings that can disagree about "
                + "where it lives",
                anchor.Name, anchor.DeclaredIn, anchor.RootedBy);
        }

        // ── limb three: the disclosure itself ─────────────────────────────────────────────────
        Flatten(File.ReadAllText(specPath)).Should().Contain(Flatten(AttackerWritableDisclosure),
            "every anchor in this class's row set is a file the actor being defended against can "
            + "write, so the store's whole account of itself lives inside the directory being "
            + "attacked. An actor who strips every signature and deletes the marker leaves no anchor "
            + "at all, the store resolves to never-signed, and it reads clean for every reader — "
            + "keyed or keyless. The only thing standing between that and a reader who assumes "
            + "otherwise is one sentence in {0}. Deleting or rewording it turns a disclosed residual "
            + "into an undisclosed hole, which is a security change wearing a copy edit. Phrase: "
            + "\"{1}\"",
            SpecPath, AttackerWritableDisclosure);
    }

    // ─────────────────────────── the scan ───────────────────────────

    /// <summary>
    /// Every distinct gateway to the filesystem, the environment or the clock in
    /// <paramref name="code"/>, normalized to <c>Type.Member</c> or <c>new Type</c> and sorted
    /// ordinally. A SET, not counts: the counts that matter are asserted by name, and pinning the
    /// rest would redden on a harmless second <c>Path.GetFileName</c>, which teaches the next person
    /// to edit the gate instead of asking what changed.
    /// </summary>
    private static IReadOnlyList<string> GatewaySpellings(string code)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match m in GatewayMember().Matches(code))
            found.Add(m.Groups[1].Value + "." + m.Groups[2].Value);

        foreach (Match m in GatewayConstruction().Matches(code))
            found.Add("new " + m.Groups[1].Value);

        return found.ToList();
    }

    /// <summary>
    /// A member access on a BCL type that reaches outside the process. Longest alternatives first so
    /// <c>DirectoryInfo.EnumerateFiles</c> is reported as itself rather than as nothing; the
    /// lookbehind keeps <c>SomeThing.File</c> and <c>obj.Path</c> out, at the cost of missing a
    /// fully-qualified <c>System.IO.File.…</c> — which is why <see cref="ForeignChannels"/> forbids
    /// that spelling outright.
    /// </summary>
    [GeneratedRegex(@"(?<![\w.])(DirectoryInfo|FileSystemInfo|FileInfo|Directory|File|Path|Environment)\s*\.\s*(\w+)")]
    private static partial Regex GatewayMember();

    /// <summary>A constructed reader or stream: the spelling that reaches a file with no
    /// <c>File.</c> or <c>Directory.</c> anywhere in it.</summary>
    [GeneratedRegex(@"(?<![\w.])new\s+(StreamReader|StreamWriter|FileStream|BinaryReader|BinaryWriter|FileInfo|DirectoryInfo)\s*\(")]
    private static partial Regex GatewayConstruction();

    /// <summary>
    /// Every run of whitespace collapses to one space, so a needle is matched as PROSE rather than
    /// as bytes. Mandatory, and measured: the spec is CRLF in a Windows working tree and LF on the
    /// Linux cert-gate runner (<c>.gitattributes</c> has <c>* text=auto</c>), and markdown wraps the
    /// disclosure sentence mid-clause — a raw <c>Contains</c> over that file finds it NOT AT ALL
    /// while it is plainly present. Applied to the needle as well, so a phrase may be spelled across
    /// several source lines with <c>+</c>.
    /// </summary>
    private static string Flatten(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Non-overlapping occurrences of <paramref name="needle"/>, ordinally.</summary>
    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            count++;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    /// <summary>
    /// Drops lines that START with <c>//</c>, so prose about an anchor neither satisfies nor trips
    /// the scan. Block comments are NOT handled, which is why <see cref="ForeignChannels"/> forbids
    /// <c>/*</c> in these two files outright rather than leaving the hole open.
    /// </summary>
    private static string StripCommentLines(string text)
    {
        var kept = text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
        return string.Join('\n', kept);
    }

    /// <summary>
    /// Every C# source under the scanned roots that is NOT inside a test project, repo-root-relative
    /// and with comment lines removed.
    /// </summary>
    private static IEnumerable<(string Path, string Code)> ProductionSources(string root)
    {
        var testDirs = new List<string>();
        foreach (var scanRoot in Roots)
        {
            var dir = Path.Combine(root, scanRoot);
            if (Directory.Exists(dir))
                CollectTestProjectDirs(dir, testDirs);
        }

        foreach (var scanRoot in Roots)
        {
            var dir = Path.Combine(root, scanRoot);
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Sources(dir))
            {
                if (testDirs.Any(t => file.StartsWith(t + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                    continue;

                yield return (Normalize(Path.GetRelativePath(root, file)), StripCommentLines(File.ReadAllText(file)));
            }
        }
    }

    private static IEnumerable<string> Sources(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            yield return file;

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsPruned(child))
                continue;

            foreach (var file in Sources(child))
                yield return file;
        }
    }

    private static void CollectTestProjectDirs(string directory, List<string> found)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.csproj"))
        {
            if (File.ReadAllText(file).Contains(TestSdkMarker, StringComparison.Ordinal))
                found.Add(directory);
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsPruned(child))
                continue;

            CollectTestProjectDirs(child, found);
        }
    }

    /// <summary>
    /// Build output, agent scratch space, and the root of any nested checkout — the structural rule
    /// the other convention tests use, so a vendored copy this repository never names is caught too.
    /// Only ever called on directories below the repo root.
    /// </summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);

        if (string.Equals(name, "bin", StringComparison.Ordinal)
            || string.Equals(name, "obj", StringComparison.Ordinal)
            || string.Equals(name, ".claude", StringComparison.Ordinal))
        {
            return true;
        }

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
