using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;
using Ashlar.Tools.GovernanceBrokerPrototype;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GovernanceBrokerPrototype.Tests;

// Application-contract tests only. No OS identities, ACL changes, transport peers,
// lock replacement, service installation, or claims of deployed isolation.
public sealed class BrokerParserTests
{
    [Theory]
    [InlineData("path")]
    [InlineData("stateRoot")]
    [InlineData("keyDir")]
    [InlineData("policy")]
    [InlineData("now")]
    [InlineData("actor")]
    [InlineData("signer")]
    [InlineData("outcome")]
    [InlineData("courses")]
    [InlineData("peerUid")]
    [InlineData("forgeProposalIds")]
    [InlineData("files")]
    public void Extra_authority_fields_are_rejected(string field)
    {
        var request = Requests.SubmitNode();
        request[field] = "client-controlled";
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(Requests.Bytes(request)));
    }

    [Theory]
    [InlineData("decide")]
    [InlineData("sign")]
    [InlineData("activate")]
    [InlineData("register")]
    [InlineData("append")]
    [InlineData("Submit")]
    public void Unsupported_operations_are_rejected(string operation)
    {
        var request = Requests.SubmitNode();
        request["op"] = operation;
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(Requests.Bytes(request)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"op\":null}")]
    [InlineData("{\"op\":true}")]
    [InlineData("{\"op\":1}")]
    [InlineData("{\"op\":[[[[[\"submit\"]]]]]}")]
    [InlineData("{\"op\":\"submit\",\"op\":\"read\"}")]
    public void Invalid_shapes_are_rejected(string json)
    {
        Assert.NotNull(Xunit.Record.Exception(() => BrokerRequest.Parse(Encoding.UTF8.GetBytes(json))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    [InlineData("11111111222233334444555555555555")]
    [InlineData("../state")]
    public void Request_id_requires_nonempty_D_format_guid(string id)
    {
        var request = Requests.SubmitNode();
        request["requestId"] = id;
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(Requests.Bytes(request)));
    }

    [Theory]
    [InlineData("op")]
    [InlineData("project")]
    [InlineData("requestId")]
    [InlineData("kind")]
    [InlineData("summary")]
    [InlineData("diff")]
    public void Each_submit_field_is_required_with_exact_case(string field)
    {
        var request = Requests.SubmitNode();
        var value = request[field]!.DeepClone();
        request.Remove(field);
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(Requests.Bytes(request)));
        request[field.ToUpperInvariant()] = value;
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(Requests.Bytes(request)));
    }

    [Fact]
    public void Duplicate_field_in_an_otherwise_complete_request_is_rejected()
    {
        var json = Encoding.UTF8.GetString(Requests.Submit());
        var duplicate = json.Insert(1, "\"op\":\"submit\",");
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(Encoding.UTF8.GetBytes(duplicate)));
    }

    [Fact]
    public void Exact_Unicode_text_survives_without_normalization()
    {
        const string text = "e\u0301 / é / 日本語 / 👩🏽‍💻 / مرحبا";
        var request = Requests.SubmitNode(summary: text, diff: text);
        var parsed = BrokerRequest.Parse(Requests.Bytes(request));
        Assert.Equal(text, parsed.Summary);
        Assert.Equal(text, parsed.Diff);
    }

    [Fact]
    public void Read_accepts_only_its_three_fields()
    {
        var parsed = BrokerRequest.Parse(Requests.Read());
        Assert.Equal("read", parsed.Op);
        Assert.Null(parsed.Summary);
        var extra = JsonNode.Parse(Requests.Read())!.AsObject();
        extra["summary"] = "ignored?";
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(Requests.Bytes(extra)));
    }

    [Fact]
    public void Request_and_text_lengths_are_bounded()
    {
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(ReadOnlyMemory<byte>.Empty));
        // Valid JSON at the exact limit proves this is a byte-bound check, not
        // rejection caused only by malformed input or a JSON parser exception.
        var json = Encoding.UTF8.GetString(Requests.Submit());
        var atLimit = Encoding.UTF8.GetBytes(json.PadRight(BrokerRequest.MaxBytes));
        Assert.Equal(BrokerRequest.MaxBytes, atLimit.Length);
        Assert.Equal("submit", BrokerRequest.Parse(atLimit).Op);
        var oversized = Encoding.UTF8.GetBytes(json.PadRight(BrokerRequest.MaxBytes + 1));
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(oversized));
        var request = Requests.SubmitNode(summary: new string('x', 2048));
        Assert.Equal(2048, BrokerRequest.Parse(Requests.Bytes(request)).Summary!.Length);
        request["summary"] = new string('x', 2049);
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(Requests.Bytes(request)));
        request = Requests.SubmitNode(diff: new string('x', 24001));
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(Requests.Bytes(request)));
    }

    [Fact]
    public void Submit_rejects_non_brick_kind()
    {
        var request = Requests.SubmitNode();
        request["kind"] = "tool";
        Assert.Throws<ArgumentException>(() => BrokerRequest.Parse(Requests.Bytes(request)));
    }
}

[CollectionDefinition("GovernanceBrokerEnvironment", DisableParallelization = true)]
public sealed class GovernanceBrokerEnvironmentCollection { }

[Collection("GovernanceBrokerEnvironment")]
public sealed class BrokerPersistenceTests : IDisposable
{
    private const uint Worker = 12001;
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "ashlar-broker-contract-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousKeys;
    private readonly SigningIdentity signer;
    private readonly GateStore store;
    private readonly Broker broker;

    public BrokerPersistenceTests()
    {
        Directory.CreateDirectory(root);
        previousKeys = Environment.GetEnvironmentVariable("ASHLAR_KEY_DIR");
        var keys = Path.Combine(root, "keys");
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", keys);
        signer = OperatorKey.Generate(keys);
        store = new GateStore(Path.Combine(root, "state"), signer);
        broker = NewBroker();
    }

    private Broker NewBroker() => new(Requests.Project, Worker,
        new GateStore(Path.Combine(root, "state"), signer), new FixedClock(Now));

    [Theory]
    [InlineData(0u)]
    [InlineData(12002u)]
    public async Task Unauthorized_peer_is_rejected_before_any_persistence(uint peer)
    {
        var before = Snapshot();
        var response = await broker.DispatchAsync(Requests.Submit(), peer);
        Assert.Equal("unauthorized", response.Error);
        AssertSnapshot(before);
    }

    [Fact]
    public async Task Unknown_project_is_rejected_before_persistence()
    {
        var before = Snapshot();
        var request = Requests.SubmitNode();
        request["project"] = "another-project";
        var response = await broker.DispatchAsync(Requests.Bytes(request), Worker);
        Assert.Equal("unknown-project", response.Error);
        AssertSnapshot(before);
    }

    [Theory]
    [InlineData("decide")]
    [InlineData("sign")]
    [InlineData("activate")]
    public async Task Unsupported_operation_is_rejected_before_persistence(string op)
    {
        var before = Snapshot();
        var request = Requests.SubmitNode();
        request["op"] = op;
        var response = await broker.DispatchAsync(Requests.Bytes(request), Worker);
        Assert.Equal("invalid-request", response.Error);
        AssertSnapshot(before);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("stateRoot")]
    [InlineData("keyDir")]
    [InlineData("policy")]
    [InlineData("now")]
    [InlineData("actor")]
    [InlineData("signer")]
    [InlineData("outcome")]
    [InlineData("courses")]
    [InlineData("peerUid")]
    [InlineData("forgeProposalIds")]
    [InlineData("files")]
    public async Task Extra_authority_field_is_rejected_before_persistence(string field)
    {
        var before = Snapshot();
        var request = Requests.SubmitNode();
        request[field] = "client-controlled";
        var response = await broker.DispatchAsync(Requests.Bytes(request), Worker);
        Assert.Equal("invalid-request", response.Error);
        AssertSnapshot(before);
    }

    [Fact]
    public async Task Client_course_results_cannot_be_signed_as_verified_facts()
    {
        var before = Snapshot();
        var request = Requests.SubmitNode();
        request["courses"] = new JsonArray(new JsonObject { ["name"] = "tests", ["passed"] = true });
        var response = await broker.DispatchAsync(Requests.Bytes(request), Worker);
        Assert.Equal("invalid-request", response.Error);
        AssertSnapshot(before);
    }

    [Theory]
    [InlineData("\\ud800")]
    [InlineData("\\udfff")]
    [InlineData("\\ud800x")]
    public async Task Invalid_surrogates_return_invalid_request_without_persistence(string escaped)
    {
        var before = Snapshot();
        var json = Encoding.UTF8.GetString(Requests.Submit()).Replace("bounded change", escaped, StringComparison.Ordinal);
        var response = await broker.DispatchAsync(Encoding.UTF8.GetBytes(json), Worker);
        Assert.Equal("invalid-request", response.Error);
        AssertSnapshot(before);
    }

    [Fact]
    public async Task Submission_uses_server_time_and_identity_and_can_only_be_signed_rejected()
    {
        const string summary = "e\u0301 / 日本語 / 👩🏽‍💻";
        const string diff = "line one\nline two مرحبا";
        var response = await broker.DispatchAsync(Requests.Submit(summary: summary, diff: diff), Worker);
        Assert.True(response.Ok);
        Assert.Null(response.Error);
        var record = Assert.IsType<GateRecord>(response.Record);
        Assert.Equal(ProposalState.Rejected, record.State);
        Assert.Equal("gate", record.Actor);
        Assert.Equal(Now, record.DecidedAt);
        Assert.Equal(Now, record.Proposal.ProposedAt);
        Assert.Equal("worker-uid:12001", record.Proposal.ProposedBy);
        Assert.Equal(summary, record.Proposal.Summary);
        Assert.Equal(diff, record.Proposal.Diff);
        Assert.Empty(record.Proposal.Courses);
        Assert.Empty(record.Proposal.ForgeProposalIds);
        Assert.Null(record.Proposal.Files);
        Assert.False(string.IsNullOrWhiteSpace(record.Sig));
        Assert.Equal(signer.PublicKeyBase64, record.Signer);
        Assert.True(OperatorKey.Verify(record.Signer!, CanonicalJson.Bytes(record with { Sig = null, Signer = null }), record.Sig!));
        Assert.False(string.IsNullOrWhiteSpace(record.StoreId));
        Assert.Equal(record.Sig, (await store.GetAsync(record.Proposal.Id))!.Sig);
    }

    [Fact]
    public async Task Same_request_after_restart_returns_same_record_without_changing_bytes()
    {
        var original = await broker.DispatchAsync(Requests.Submit(), Worker);
        Assert.True(original.Ok);
        var before = Snapshot();
        var repeat = await NewBroker().DispatchAsync(Requests.Submit(), Worker);
        Assert.True(repeat.Ok);
        Assert.Equal(original.Record!.Sig, repeat.Record!.Sig);
        Assert.Equal(original.Record.DecidedAt, repeat.Record.DecidedAt);
        AssertSnapshot(before);
    }

    [Fact]
    public async Task Reused_id_with_different_content_conflicts_without_rewrite()
    {
        Assert.True((await broker.DispatchAsync(Requests.Submit(), Worker)).Ok);
        var before = Snapshot();
        var response = await broker.DispatchAsync(Requests.Submit(summary: "different request"), Worker);
        Assert.False(response.Ok);
        Assert.Equal("request-conflict", response.Error);
        AssertSnapshot(before);
    }

    [Fact]
    public async Task Read_and_reconstruct_preserve_existing_signed_record_and_marker_bytes()
    {
        var original = await broker.DispatchAsync(Requests.Submit(summary: "historical e\u0301"), Worker);
        Assert.True(original.Ok);
        Assert.True(File.Exists(Path.Combine(root, "state", GateSigningActivation.FileName)));
        var before = Snapshot();
        var reconstructed = NewBroker();
        AssertSnapshot(before);
        var response = await reconstructed.DispatchAsync(Requests.Read(), Worker);
        Assert.True(response.Ok);
        Assert.Equal(original.Record!.Sig, response.Record!.Sig);
        AssertSnapshot(before);
        var denied = await reconstructed.DispatchAsync(Requests.Submit(), Worker + 1);
        Assert.False(denied.Ok);
        AssertSnapshot(before);
    }

    [Fact]
    public async Task Missing_read_does_not_create_a_record()
    {
        var before = Snapshot();
        var response = await broker.DispatchAsync(Requests.Read(), Worker);
        Assert.Equal("not-found", response.Error);
        AssertSnapshot(before);
    }

    [Fact]
    public async Task Concurrent_duplicate_submissions_persist_one_signed_record()
    {
        var requests = Enumerable.Range(0, 12).Select(_ => broker.DispatchAsync(Requests.Submit(), Worker));
        var responses = await Task.WhenAll(requests);
        Assert.All(responses, response => Assert.True(response.Ok));
        Assert.Single(responses.Select(response => response.Record!.Sig).Distinct(StringComparer.Ordinal));
        Assert.Single(await store.ListAsync());
        Assert.All(responses, response => Assert.Equal(ProposalState.Rejected, response.Record!.State));
    }

    [Fact]
    public async Task Unreadable_record_returns_generic_failure_without_a_new_unsigned_record()
    {
        var path = Path.Combine(root, "state", "gates", "broken.json");
        await File.WriteAllTextAsync(path, "not a record");
        var before = Snapshot();
        var response = await broker.DispatchAsync(Requests.Submit(), Worker);
        Assert.False(response.Ok);
        Assert.Equal("store-unavailable", response.Error);
        Assert.Null(response.Record);
        AssertSnapshot(before);
        Assert.False(File.Exists(Path.Combine(root, "state", GateSigningActivation.FileName)));
    }

    [Fact]
    public async Task Responses_contain_no_private_signing_material()
    {
        var response = await broker.DispatchAsync(Requests.Submit(), Worker);
        Assert.True(response.Ok);
        var serialized = JsonSerializer.Serialize(response);
        var privateSeed = (await File.ReadAllTextAsync(Path.Combine(root, "keys", "operator.key"))).Trim();
        Assert.DoesNotContain(privateSeed, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(root, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("operator.key", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_capacity_refuses_new_ID_but_retains_retry_and_read()
    {
        var bounded = new Broker(Requests.Project, Worker, store, new FixedClock(Now), maximumRecords: 1);
        var first = await bounded.DispatchAsync(Requests.Submit(), Worker);
        Assert.True(first.Ok);
        var before = Snapshot();
        var second = Requests.SubmitNode();
        second["requestId"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var full = await bounded.DispatchAsync(Requests.Bytes(second), Worker);
        Assert.False(full.Ok);
        Assert.Equal("prototype-capacity", full.Error);
        AssertSnapshot(before);

        var retry = await bounded.DispatchAsync(Requests.Submit(), Worker);
        var read = await bounded.DispatchAsync(Requests.Read(), Worker);
        Assert.True(retry.Ok);
        Assert.True(read.Ok);
        Assert.Equal(first.Record!.Sig, retry.Record!.Sig);
        Assert.Equal(first.Record.Sig, read.Record!.Sig);
        Assert.Single(await store.ListAsync());
        AssertSnapshot(before);
    }

    [Fact]
    public void Required_signer_refuses_a_missing_key_directory_without_state_writes()
    {
        var before = Snapshot();
        Assert.Throws<InvalidOperationException>(() =>
            Program.LoadRequiredSigner(Path.Combine(root, "missing-keys")));
        AssertSnapshot(before);
        Assert.False(Directory.Exists(Path.Combine(root, "missing-keys")));
    }

    [Fact]
    public void Required_signer_refuses_a_missing_public_key_without_state_writes()
    {
        File.Delete(Path.Combine(root, "keys", "operator.pub"));
        var before = Snapshot();
        Assert.Throws<InvalidOperationException>(() => Program.LoadRequiredSigner(Path.Combine(root, "keys")));
        AssertSnapshot(before);
    }

    [Fact]
    public void Required_signer_refuses_malformed_trusted_key_without_state_writes()
    {
        var trusted = Path.Combine(root, "keys", "trusted");
        Directory.CreateDirectory(trusted);
        File.WriteAllText(Path.Combine(trusted, "fixture.pub"), "not-base64");
        var before = Snapshot();
        Assert.Throws<InvalidOperationException>(() => Program.LoadRequiredSigner(Path.Combine(root, "keys")));
        AssertSnapshot(before);
    }

    [Fact]
    public void Explicit_signer_load_preserves_bytes_and_ignores_unrelated_ambient_keys()
    {
        var unrelatedKeys = Path.Combine(root, "unrelated-keys");
        var unrelated = OperatorKey.Generate(unrelatedKeys);
        Assert.NotEqual(signer.PublicKeyBase64, unrelated.PublicKeyBase64);
        var previous = Environment.GetEnvironmentVariable("ASHLAR_KEY_DIR");
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", unrelatedKeys);
        try
        {
            var before = Snapshot();
            var loaded = Program.LoadRequiredSigner(Path.Combine(root, "keys"));
            Assert.Equal(signer.PublicKeyBase64, loaded.PublicKeyBase64);
            Assert.Equal(unrelatedKeys, Environment.GetEnvironmentVariable("ASHLAR_KEY_DIR"));
            AssertSnapshot(before);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", previous);
        }
    }

    // Hashes keep a failed assertion from dumping test key bytes into a runner log.
    private string[] Snapshot() => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .ToArray();

    private void AssertSnapshot(string[] expected) => Assert.Equal(expected, Snapshot());

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", previousKeys);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FixedClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}

internal static class Requests
{
    internal const string Project = "prototype-project";
    internal const string Id = "11111111-2222-3333-4444-555555555555";

    internal static JsonObject SubmitNode(string summary = "bounded change", string diff = "descriptive text") => new()
    {
        ["op"] = "submit", ["project"] = Project, ["requestId"] = Id,
        ["kind"] = "brick", ["summary"] = summary, ["diff"] = diff,
    };

    internal static byte[] Submit(string summary = "bounded change", string diff = "descriptive text") =>
        Bytes(SubmitNode(summary, diff));

    internal static byte[] Read() => Bytes(new JsonObject
    {
        ["op"] = "read", ["project"] = Project, ["requestId"] = Id,
    });

    internal static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
}
