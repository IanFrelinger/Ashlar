using System.Text.Json;
using System.Text.Json.Serialization;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>Future human writes are constrained; historical signed bytes remain readable unchanged.</summary>
[Collection("EnvironmentSensitive")]
public sealed class GateDecisionIdentityTests : IDisposable
{
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "decision-identity-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousKeyDir = Environment.GetEnvironmentVariable(KeyDirVariable);
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public GateDecisionIdentityTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(KeyDirVariable,
            Directory.CreateDirectory(Path.Combine(_root, "no-ambient-keys")).FullName);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KeyDirVariable, _previousKeyDir);
        Directory.Delete(_root, recursive: true);
    }

    public static IEnumerable<object[]> InvalidActors()
    {
        (string Name, string? Actor)[] cases =
        [
            ("null", null), ("empty", ""), ("blank", " \t"),
            ("automatic", "gate"), ("automatic-case", "GaTe"),
            ("automatic-width", "ｇａｔｅ"), ("automatic-joiner", "g\u200Date"),
            ("automatic-nonjoiner", "g\u200Cate"), ("automatic-zero-width", "g\u200Bate"),
            ("automatic-bom", "\uFEFFgate"), ("automatic-variation", "gate\uFE0F"),
            ("leading-space", " human"), ("trailing-space", "human\u3000"),
            ("line-forgery", "human\r\nADMITTED by gate"), ("escape", "human\u001b[2J"),
            ("nul", "human\0"), ("tab", "human\tname"), ("delete", "human\u007f"),
            ("c1", "human\u009b2J"), ("arabic-mark", "human\u061c"),
            ("ltr-mark", "human\u200e"), ("rtl-mark", "human\u200f"),
            ("line-separator", "human\u2028name"), ("paragraph-separator", "human\u2029name"),
            ("bidi-embedding", "human\u202a"), ("bidi-override", "human\u202e"),
            ("bidi-pop", "human\u202c"), ("bidi-isolate", "human\u2066"),
            ("bidi-isolate-pop", "human\u2069"),
            ("unpaired-high", "human\ud800"), ("unpaired-low", "human\udfff"),
            ("too-long", new string('a', 257)),
        ];
        foreach (var item in cases)
        foreach (var signed in new[] { false, true })
            yield return [item.Name, item.Actor!, signed];
    }

    [Theory]
    [MemberData(nameof(InvalidActors))]
    public async Task Invalid_human_actor_is_rejected_before_lock_cleanup_or_persistence(string name, string actor, bool signed)
    {
        _ = name; // Names keep otherwise invisible attack vectors identifiable in TRX evidence.
        var signer = signed ? OperatorKey.Generate(Path.Combine(_root, "signing-key")) : null;
        var store = new GateStore(_root, signer);
        await store.RecordAsync(Proposal(), new AdmissionOutcome { State = ProposalState.Held, Reason = "review" }, Now);
        // Acquiring the lock sweeps this file. Validation must precede even that side effect.
        await File.WriteAllTextAsync(Path.Combine(_root, "gates", "orphan.json.tmp"), "uncommitted bytes");
        var before = Snapshot();

        Func<Task> decide = () => store.DecideAsync("ext-identity", admit: true, actor, "reviewed", Now.AddMinutes(1));

        var thrown = await decide.Should().ThrowAsync<ArgumentException>();
        thrown.Which.ParamName.Should().Be("actor");
        Snapshot().Should().BeEquivalentTo(before, "invalid identity cannot sweep, sign or persist anything");
    }

    [Fact]
    public async Task Invalid_actor_is_rejected_even_when_the_store_lock_is_held()
    {
        var store = new GateStore(_root);
        using var held = new FileStream(Path.Combine(_root, "gates", ".lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Func<Task> decide = () => store.DecideAsync("missing", true, "gate", "reviewed", Now, timeout.Token);
        (await decide.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("actor");
    }

    [Fact]
    public async Task Refusal_cannot_use_the_automatic_actor_either()
    {
        var store = new GateStore(_root);
        await store.RecordAsync(Proposal(), new AdmissionOutcome { State = ProposalState.Held, Reason = "review" }, Now);
        var before = Snapshot();
        Func<Task> refuse = () => store.DecideAsync("ext-identity", false, "gate", "refused with explanation", Now);
        (await refuse.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("actor");
        Snapshot().Should().BeEquivalentTo(before);
    }

    public static IEnumerable<object[]> LegitimateActors()
    {
        yield return ["Ian Frelinger"];
        yield return ["Zoë"];
        yield return ["Zoe\u0308"];
        yield return ["李明 / مریم\u200Cرضا"];
        yield return ["operator 👩\u200D💻"];
        yield return [new string('a', 256)];
        yield return [new string('a', 254) + "😀"];
    }

    [Theory]
    [MemberData(nameof(LegitimateActors))]
    public async Task Legitimate_unicode_and_exact_length_boundary_round_trip_without_normalization(string actor)
    {
        var signer = OperatorKey.Generate(Path.Combine(_root, "signing-key"));
        var store = new GateStore(_root, signer);
        var automatic = await store.RecordAsync(Proposal(), new AdmissionOutcome { State = ProposalState.Held, Reason = "review" }, Now);
        automatic.Actor.Should().Be("gate");

        var decided = await store.DecideAsync("ext-identity", true, actor, "reviewed", Now.AddMinutes(1));
        var before = Snapshot();
        var read = await store.GetAsync("ext-identity");
        var listed = await store.ListAsync();

        decided.Actor.Should().Be(actor);
        read!.Actor.Should().Be(actor);
        listed.Should().ContainSingle().Which.Actor.Should().Be(actor);
        OperatorKey.Verify(read.Signer!, CanonicalJson.Bytes(read with { Sig = null, Signer = null }), read.Sig!).Should().BeTrue();
        CanonicalJson.Bytes(read with { Sig = null, Signer = null })
            .Should().Equal(CanonicalJson.Bytes(decided with { Sig = null, Signer = null }));
        Snapshot().Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData(false, "gate")]
    [InlineData(true, "gate")]
    [InlineData(false, "old\nADMITTED\u001b[2J\u202eactor")]
    [InlineData(true, "old\nADMITTED\u001b[2J\u202eactor")]
    [InlineData(false, "oversized")]
    [InlineData(true, "oversized")]
    public async Task Historical_records_bypass_new_write_validation_without_changing_stored_or_signed_bytes(bool signed, string actor)
    {
        if (actor == "oversized") actor = new string('x', 4096);
        // Deliberately load a historical schema directly, not through DecideAsync.
        const string historical = """
            {"Proposal":{"Id":"ext-identity","Kind":"brick","Summary":"historical","ProposedBy":"author","ProposedAt":"2026-11-12T12:00:00+00:00","Courses":[]},"State":"Admitted","Reason":"reviewed","Actor":"old","DecidedAt":"2026-11-12T12:00:00+00:00"}
            """;
        var record = JsonSerializer.Deserialize<GateRecord>(historical, Json)! with { Actor = actor };
        var canonicalBefore = CanonicalJson.Bytes(record);
        if (signed)
        {
            var signer = OperatorKey.Generate(Path.Combine(_root, "signing-key"));
            record = record with { Sig = signer.Sign(canonicalBefore), Signer = signer.PublicKeyBase64 };
        }
        var store = new GateStore(_root);
        var file = Path.Combine(_root, "gates", "ext-identity.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(record, Json));
        var before = Snapshot();

        var read = await store.GetAsync("ext-identity");
        var listed = await store.ListAsync();

        read!.Actor.Should().Be(actor);
        listed.Should().ContainSingle().Which.Actor.Should().Be(actor);
        read.Sig.Should().Be(record.Sig);
        read.StoreId.Should().BeNull("the old schema predates store identity");
        if (signed) OperatorKey.Verify(read.Signer!, CanonicalJson.Bytes(read with { Sig = null, Signer = null }), read.Sig!).Should().BeTrue();
        CanonicalJson.Bytes(read with { Sig = null, Signer = null }).Should().Equal(canonicalBefore);
        Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public void Legacy_canonical_schema_bytes_remain_exact()
    {
        // Freeze the pre-hardening serialized schema independently of GateRecord's properties.
        // CanonicalJson's own tests pin sorting/encoding; this projection pins the exact old payload.
        var legacy = new
        {
            Actor = "gate", DecidedAt = Now, Reason = "reviewed", State = "Admitted",
            Proposal = new
            {
                Courses = Array.Empty<CourseResult>(), Diff = "", ForgeProposalIds = Array.Empty<string>(),
                Id = "ext-identity", Kind = "brick", ProposedAt = Now, ProposedBy = "author", Summary = "historical",
            },
        };
        var record = new GateRecord
        {
            Actor = "gate", DecidedAt = Now, Reason = "reviewed", State = ProposalState.Admitted,
            Proposal = Proposal() with { Summary = "historical", Courses = [] },
        };
        CanonicalJson.Bytes(record).Should().Equal(CanonicalJson.Bytes(legacy));
    }

    private Dictionary<string, string> Snapshot() => Directory.EnumerateFiles(Path.Combine(_root, "gates"))
        .ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToBase64String(File.ReadAllBytes(path)));

    private static ExtensionProposal Proposal() => new()
    {
        Id = "ext-identity", Kind = "brick", Summary = "identity test", ProposedBy = "author",
        ProposedAt = Now, Courses = [new CourseResult { Name = "tests", Passed = true, Detail = "ok" }],
    };
}
