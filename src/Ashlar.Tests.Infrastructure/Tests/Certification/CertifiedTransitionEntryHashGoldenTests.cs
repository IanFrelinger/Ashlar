using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ashlar.Certification.State;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Byte-level pin on the canonical entry-hash payload for certified state transitions.
/// <para>
/// <c>StateLogVerifier</c> recomputes the entry hash of every transition in an attested state
/// log and refuses the log when it does not match the stored one, so these bytes decide whether
/// a log written yesterday still verifies today — the same role the canonical signing payload
/// plays for a certificate. Until this suite existed they were pinned by nothing: the only
/// other in-repo computation of them was a re-typed copy inside a test fixture, which would
/// have drifted with the production code rather than caught it drifting.
/// </para>
/// <para>
/// The corpus in <c>transition-entry-hashes.golden.json</c> pins the payload string, its UTF-8
/// byte length, its SHA-256 and the resulting <c>entryHash</c>. The last of those is the one a
/// caller can observe through the public API, which is what
/// <c>scripts/ns20-canonical-bytes-probe.sh</c> and <c>scripts/portability/net9-probe.sh</c>
/// check on targets where the payload itself is not reachable.
/// </para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class CertifiedTransitionEntryHashGoldenTests
{
    private const string GoldenFileName = "transition-entry-hashes.golden.json";

    private static readonly IReadOnlyDictionary<string, GoldenCase> Corpus = LoadCorpus();

    public static TheoryData<string> GoldenCaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Corpus.Keys.OrderBy(n => n, StringComparer.Ordinal))
                data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GoldenCaseNames))]
    public void EntryHashPayload_IsByteIdenticalToGolden(string caseName)
    {
        var golden = Corpus[caseName];

        var payload = CertifiedTransitionBuilder.BuildCanonicalPayload(
            golden.PriorStateHash,
            golden.Action,
            golden.BehaviorCertContentHash,
            golden.ResultingStateHash,
            golden.PrevEntryHash);

        // The string comparison comes first on purpose: it is the assertion that prints a
        // readable diff. The length and digest below turn "readable" into "byte-exact".
        payload.Should().Be(
            golden.Payload,
            "the entry-hash payload for '{0}' is what every attested state log carrying that "
            + "transition was written against",
            caseName);

        var bytes = Encoding.UTF8.GetBytes(payload);
        bytes.Length.Should().Be(golden.PayloadByteLength, "the hashed message is bytes, not characters");
        Sha256Hex(bytes).Should().Be(
            golden.PayloadSha256,
            "SHA-256 of the UTF-8 entry-hash payload for '{0}'. Actual payload was: {1}",
            caseName,
            payload);
    }

    [Theory]
    [MemberData(nameof(GoldenCaseNames))]
    public void ComputeEntryHash_IsIdenticalToGolden(string caseName)
    {
        // The public half of the same pin, and the only half a netstandard2.0 or .NET 9
        // consumer can observe. StateLogVerifier compares exactly this value, so a change here
        // is a change to whether existing logs verify.
        var golden = Corpus[caseName];

        new CertifiedTransitionBuilder().ComputeEntryHash(
                golden.PriorStateHash,
                golden.Action,
                golden.BehaviorCertContentHash,
                golden.ResultingStateHash,
                golden.PrevEntryHash)
            .Should().Be(golden.EntryHash);
    }

    [Fact]
    public void Create_StampsTheEntryHashItComputed()
    {
        // Pins the binding between the payload and the transition the builder hands back,
        // without introducing a second constant.
        var golden = Corpus["transition-chained"];

        var transition = new CertifiedTransitionBuilder().Create(
            golden.PriorStateHash,
            golden.Action,
            golden.BehaviorCertContentHash,
            golden.ResultingStateHash,
            golden.PrevEntryHash);

        transition.EntryHash.Should().Be(golden.EntryHash);
    }

    [Fact]
    public void GoldenCorpus_CoversTheGenesisAndTheChainedShape()
    {
        // A theory over an empty or half-empty corpus passes without asserting anything, which
        // is the same failure mode as having no golden test at all. Assert the corpus itself.
        Corpus.Should().NotBeEmpty();
        Corpus.Values.Should().Contain(
            c => c.PrevEntryHash == CertifiedTransition.GenesisPrevEntryHash,
            "the first entry of every log carries the genesis prev-entry hash, and an empty string must be written rather than omitted");
        Corpus.Values.Should().Contain(
            c => c.PrevEntryHash.Length > 0,
            "a linked entry is the shape every entry after the first has");
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    private static IReadOnlyDictionary<string, GoldenCase> LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tests", "Certification", GoldenFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Golden transition entry-hash corpus not found at '{path}'.", path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var corpus = new Dictionary<string, GoldenCase>(StringComparer.Ordinal);
        foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString()!;
            var record = element.GetProperty("record");
            corpus[name] = new GoldenCase(
                record.GetProperty("priorStateHash").GetString()!,
                record.GetProperty("action").GetString()!,
                record.GetProperty("behaviorCertContentHash").GetString()!,
                record.GetProperty("resultingStateHash").GetString()!,
                record.GetProperty("prevEntryHash").GetString()!,
                element.GetProperty("payload").GetString()!,
                element.GetProperty("payloadSha256").GetString()!,
                element.GetProperty("payloadByteLength").GetInt32(),
                element.GetProperty("entryHash").GetString()!);
        }

        return corpus;
    }

    private sealed record GoldenCase(
        string PriorStateHash,
        string Action,
        string BehaviorCertContentHash,
        string ResultingStateHash,
        string PrevEntryHash,
        string Payload,
        string PayloadSha256,
        int PayloadByteLength,
        string EntryHash);
}
