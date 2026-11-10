using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using Ashlar.BackgroundAgents.Forge;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.CLI.Commands;
using Ashlar.CLI.Commands.BackgroundAgent;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Packaging;
using Ashlar.Manifest.Signing;
using Ashlar.Tests.CLI.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

[Collection("MeshIntegration")]
public sealed class PackageImportIntegrityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ashlar-import-integrity-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousKeys = Environment.GetEnvironmentVariable("ASHLAR_KEY_DIR");
    private readonly SigningIdentity _origin;
    private readonly SigningIdentity _other;
    private readonly string _project;
    private readonly string _keys;

    public PackageImportIntegrityTests()
    {
        Directory.CreateDirectory(_dir);
        _keys = Path.Combine(_dir, "receiver-keys");
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", _keys);
        _origin = OperatorKey.Generate(Path.Combine(_dir, "origin-keys"));
        _other = OperatorKey.Generate(Path.Combine(_dir, "other-keys"));
        OperatorKey.Trust(_origin.Fingerprint, _keys);
        OperatorKey.Trust(_other.Fingerprint, _keys);
        _project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(_project);
        SetMode("proposing");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", _previousKeys);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void SetMode(string mode)
    {
        ProjectScaffold.TryScaffold("import-test", out var manifest, out var policy, out var reason).Should().BeTrue(reason);
        policy = policy!.Replace("mode: sealed", "mode: " + mode, StringComparison.Ordinal)
            .Replace("extensions: 0", "extensions: 20", StringComparison.Ordinal)
            .Replace("mayAdd: []", "mayAdd: [brick]", StringComparison.Ordinal)
            .Replace("gatesRequired: []", "gatesRequired: [tests]", StringComparison.Ordinal);
        PolicyLoader.TryLoad(policy, out _, out reason).Should().BeTrue(reason);
        File.WriteAllText(Path.Combine(_project, "ashlar.yaml"), manifest);
        File.WriteAllText(Path.Combine(_project, "ashlar.policy.yaml"), policy);
    }

    private static ExtensionProposal Proposal(string id) => new()
    {
        Id = id, Kind = "brick", Summary = id, ProposedBy = "origin",
        ProposedAt = DateTimeOffset.Parse("2026-11-10T00:00:00Z"),
        Courses = [new CourseResult { Name = "tests", Passed = true, Detail = "passed" }],
    };

    private string Package(string id = "ext-shared", string content = "// original", SigningIdentity? signer = null,
        ExtensionProposal? proposal = null)
    {
        signer ??= _origin;
        var record = new GateRecord
        {
            Proposal = proposal ?? Proposal(id), State = ProposalState.Admitted,
            Reason = "reviewed", Actor = "origin", DecidedAt = DateTimeOffset.Parse("2026-11-10T00:01:00Z"),
        };
        record = record with { Sig = signer.Sign(CanonicalJson.Bytes(record)), Signer = signer.PublicKeyBase64 };
        var json = ExtensionPackaging.Pack(record,
            [new PackageFile { Path = "src/One.cs", Content = content }, new PackageFile { Path = "src/Two.cs", Content = "// second" }], signer);
        ExtensionPackaging.TryOpen(json, out _, out var reason).Should().BeTrue(reason);
        return json;
    }

    private ChangeProposalStore Forge => AshlarProjectMediation.ProjectStore(_project);
    private GateStore Gate => new(Path.Combine(_project, ".ashlar"), OperatorKey.TryLoad());

    private void BreakGateWrite(string id) => Directory.CreateDirectory(Path.Combine(_project, ".ashlar", "gates", id + ".json"));

    [Fact]
    public async Task Forge_id_collision_preserves_the_unowned_row_and_cleans_up_only_this_attempt()
    {
        Forge.Add(new ChangeProposal
        {
            Id = "pkg-collision", TargetPath = "src/Unrelated.cs", NewContent = "// unrelated", Summary = "unrelated",
        });
        var original = Forge.Find("pkg-collision");
        var ids = new Queue<string>(["pkg-owned", "pkg-collision"]);
        var result = await PackageImport.SubmitAsync(_project, Package(), () => ids.Dequeue());
        result.Outcome.Should().Be(PackageAdmission.Failed);
        Forge.Find("pkg-collision").Should().BeEquivalentTo(original);
        Forge.Find("pkg-owned")!.Status.Should().Be(ChangeProposalStatus.Rejected);
        (await Gate.GetAsync("ext-shared")).Should().BeNull();
    }

    [Theory]
    [InlineData("proposing", PackageAdmission.Held)]
    [InlineData("self-extending", PackageAdmission.Admitted)]
    [InlineData("sealed", PackageAdmission.Rejected)]
    public async Task Exact_package_reimport_preserves_record_and_queue(string mode, PackageAdmission expected)
    {
        SetMode(mode);
        var json = Package();
        (await PackageImport.SubmitAsync(_project, json)).Outcome.Should().Be(expected);
        var before = await File.ReadAllBytesAsync(Path.Combine(_project, ".ashlar", "gates", "ext-shared.json"));
        var ids = Forge.List().Select(p => p.Id).ToArray();
        // Whitespace is transport spelling, not identity.
        json = JsonNode.Parse(json)!.ToJsonString();
        var result = await PackageImport.SubmitAsync(_project, json);
        result.Outcome.Should().Be(PackageAdmission.AlreadyImported);
        (await File.ReadAllBytesAsync(Path.Combine(_project, ".ashlar", "gates", "ext-shared.json"))).Should().Equal(before);
        Forge.List().Select(p => p.Id).Should().BeEquivalentTo(ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Different_package_with_colliding_id_is_refused_before_parking(bool differentOrigin)
    {
        var first = Package();
        (await PackageImport.SubmitAsync(_project, first)).Outcome.Should().Be(PackageAdmission.Held);
        var before = await File.ReadAllBytesAsync(Path.Combine(_project, ".ashlar", "gates", "ext-shared.json"));
        var incoming = differentOrigin ? _other : _origin;
        var result = await PackageImport.SubmitAsync(_project, Package(content: "// different", signer: incoming));
        result.Outcome.Should().Be(PackageAdmission.Refused);
        result.Message.Should().Contain("collides").And.Contain(_origin.Fingerprint).And.Contain(incoming.Fingerprint);
        Forge.List().Should().HaveCount(2);
        (await File.ReadAllBytesAsync(Path.Combine(_project, ".ashlar", "gates", "ext-shared.json"))).Should().Equal(before);
        File.Exists(Path.Combine(_project, "src", "One.cs")).Should().BeFalse();
    }

    [Fact]
    public async Task Legacy_record_without_package_receipt_is_not_called_a_duplicate()
    {
        await Gate.RecordAsync(Proposal("ext-shared"), new AdmissionOutcome { State = ProposalState.Held, Reason = "legacy" }, DateTimeOffset.UtcNow);
        var result = await PackageImport.SubmitAsync(_project, Package());
        result.Outcome.Should().Be(PackageAdmission.Refused);
        result.Message.Should().Contain("unknown (local or legacy record)").And.Contain(_origin.Fingerprint);
        Forge.List().Should().BeEmpty();
    }

    [Fact]
    public async Task Imported_receipt_is_derived_locally_and_stays_outside_signed_wire_bytes()
    {
        var json = Package();
        (await PackageImport.SubmitAsync(_project, json)).Outcome.Should().Be(PackageAdmission.Held);
        ExtensionPackaging.TryOpen(json, out var package, out _).Should().BeTrue();
        var recorded = (await Gate.GetAsync("ext-shared"))!.Proposal;
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(ReceiptPath))!;
        receipt["Seal"]!.GetValue<string>().Should().Be(package!.Seal);
        receipt["Signer"]!.GetValue<string>().Should().Be(_origin.PublicKeyBase64);
        receipt["ProposalSha256"]!.GetValue<string>().Should().Be(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(CanonicalJson.Bytes(recorded))));
        Encoding.UTF8.GetString(CanonicalJson.Bytes(recorded)).Should().NotContain(package.Seal!);
        Encoding.UTF8.GetString(CanonicalJson.Bytes(recorded)).Should().NotContain(_origin.PublicKeyBase64);
        (await PackageImport.SubmitAsync(_project, json)).Outcome.Should().Be(PackageAdmission.AlreadyImported);
    }

    private string ReceiptPath => Path.Combine(_project, ".ashlar", "import-receipts", "ext-shared.json");

    [Fact]
    public async Task Receipt_identity_survives_a_held_to_admitted_decision()
    {
        var json = Package();
        (await PackageImport.SubmitAsync(_project, json)).Outcome.Should().Be(PackageAdmission.Held);
        await Gate.DecideAsync("ext-shared", true, "operator", "reviewed", DateTimeOffset.UtcNow);
        (await PackageImport.SubmitAsync(_project, json)).Outcome.Should().Be(PackageAdmission.AlreadyImported);
        Forge.List().Should().HaveCount(2);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("mismatched")]
    [InlineData("bad-signer")]
    public async Task Unusable_receipt_is_an_unknown_collision_never_a_duplicate(string damage)
    {
        var json = Package();
        (await PackageImport.SubmitAsync(_project, json)).Outcome.Should().Be(PackageAdmission.Held);
        if (damage == "missing") File.Delete(ReceiptPath);
        else if (damage == "malformed") await File.WriteAllTextAsync(ReceiptPath, "invalid json");
        else
        {
            var receipt = JsonNode.Parse(await File.ReadAllTextAsync(ReceiptPath))!;
            receipt[damage == "mismatched" ? "ProposalSha256" : "Signer"] = "invalid";
            await File.WriteAllTextAsync(ReceiptPath, receipt.ToJsonString());
        }
        var result = await PackageImport.SubmitAsync(_project, json);
        result.Outcome.Should().Be(PackageAdmission.Refused);
        result.Message.Should().Contain("collides").And.Contain("unknown");
        Forge.List().Should().HaveCount(2);
    }

    [Theory]
    [InlineData("proposing", ProposalState.Held)]
    [InlineData("self-extending", ProposalState.Admitted)]
    public async Task Receipt_write_failure_preserves_rows_owned_by_a_durable_decision(string mode, ProposalState expected)
    {
        SetMode(mode);
        Directory.CreateDirectory(Path.Combine(_project, ".ashlar"));
        await File.WriteAllTextAsync(Path.Combine(_project, ".ashlar", "import-receipts"), "not a directory");
        var result = await PackageImport.SubmitAsync(_project, Package());
        result.Outcome.Should().Be(PackageAdmission.Failed);
        result.Message.Should().Contain("was recorded").And.Contain("inspect its apply state");
        (await Gate.GetAsync("ext-shared"))!.State.Should().Be(expected);
        Forge.List(ChangeProposalStatus.Proposed).Should().HaveCount(2);
        Forge.List(ChangeProposalStatus.Rejected).Should().BeEmpty();
        File.Exists(Path.Combine(_project, "src", "One.cs")).Should().BeFalse();
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("bad/id")]
    [InlineData("ext.traversal")]
    public async Task Illegal_package_id_is_refused_before_creating_node_state(string id)
    {
        var result = await PackageImport.SubmitAsync(_project, Package(id));
        result.Outcome.Should().Be(PackageAdmission.Refused);
        result.Message.Should().Contain("Illegal proposal id");
        Directory.Exists(Path.Combine(_project, ".ashlar")).Should().BeFalse();
    }

    [Fact]
    public void Null_receipt_fields_preserve_legacy_canonical_bytes()
    {
        var proposal = Proposal("legacy");
        var bytes = CanonicalJson.Bytes(proposal);
        var node = JsonNode.Parse(bytes)!;
        node.AsObject().ContainsKey("ImportedPackageSeal").Should().BeFalse();
        node.AsObject().ContainsKey("ImportedPackageSigner").Should().BeFalse();
        Encoding.UTF8.GetString(bytes).Should().NotContain("ImportedPackage");
    }

    [Fact]
    public async Task Gate_io_failure_after_parking_rejects_every_attempt_and_reports_node_error()
    {
        BreakGateWrite("ext-shared");
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var result = await PackageImport.SubmitAsync(_project, Package());
            result.Outcome.Should().Be(PackageAdmission.Failed);
            result.Message.Should().Contain("node failed while importing");
            Forge.List(ChangeProposalStatus.Proposed).Should().BeEmpty();
            Forge.List(ChangeProposalStatus.Rejected).Should().HaveCount(2 * attempt);
            File.Exists(Path.Combine(_project, "src", "One.cs")).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Corrupt_local_gate_is_a_node_error_not_a_package_refusal()
    {
        var gateDir = Path.Combine(_project, ".ashlar", "gates");
        Directory.CreateDirectory(gateDir);
        await File.WriteAllTextAsync(Path.Combine(gateDir, "ext-shared.json"), "not json");
        var result = await PackageImport.SubmitAsync(_project, Package());
        result.Outcome.Should().Be(PackageAdmission.Failed);
        result.Message.Should().Contain("node failed while importing");
        Forge.List().Should().BeEmpty();
    }

    private static async Task<(int Code, string Output)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Console.SetOut(output); Console.SetError(error);
        try
        {
            var code = await new RootCommand { new PkgCommand() }.InvokeAsync(args);
            return (code, output.ToString() + error.ToString());
        }
        finally { Console.SetOut(ConsoleCapture.Out); Console.SetError(ConsoleCapture.Error); }
    }

    [Fact]
    public async Task Pull_reports_node_errors_separately_and_continues_to_good_and_refused_rows()
    {
        var share = Path.Combine(_dir, "share");
        Directory.CreateDirectory(share);
        BreakGateWrite("ext-broken");
        await File.WriteAllTextAsync(Path.Combine(share, "a.ashpkg"), Package("ext-broken"));
        await File.WriteAllTextAsync(Path.Combine(share, "b.ashpkg"), Package("ext-good"));
        await File.WriteAllTextAsync(Path.Combine(share, "c.ashpkg"), "invalid package");
        var (code, output) = await Run("pkg", "pull", "--from", share, "--path", _project);
        code.Should().Be(1);
        output.Should().Contain("× ERROR").And.Contain("! HELD").And.Contain("× REFUSED");
        output.Should().Contain("held 1").And.Contain("refused/rejected 1").And.Contain("errors 1");
        Forge.List(ChangeProposalStatus.Proposed).Should().HaveCount(2, "only the good held package stays parked");
        Forge.List(ChangeProposalStatus.Rejected).Should().HaveCount(2);
    }

    [Fact]
    public async Task Import_reports_node_error_without_claiming_disk_untouched()
    {
        BreakGateWrite("ext-shared");
        var path = Path.Combine(_dir, "broken.ashpkg");
        await File.WriteAllTextAsync(path, Package());
        var (code, output) = await Run("pkg", "import", path, "--path", _project);
        code.Should().Be(1);
        output.Should().Contain("× ERROR").And.NotContain("× REFUSED").And.NotContain("disk untouched");
        Forge.List(ChangeProposalStatus.Proposed).Should().BeEmpty();
    }

    [Fact]
    public async Task Daemon_counts_node_faults_as_errors_and_keeps_processing()
    {
        var share = Path.Combine(_dir, "share");
        Directory.CreateDirectory(share);
        BreakGateWrite("ext-broken");
        await File.WriteAllTextAsync(Path.Combine(share, "a.ashpkg"), Package("ext-broken"));
        await File.WriteAllTextAsync(Path.Combine(share, "b.ashpkg"), Package("ext-good"));
        var result = await MeshAutoPullService.PullOnceAsync(share, _project);
        result.Errors.Should().Be(1);
        result.Refused.Should().Be(0);
        result.Held.Should().Be(1);
        Forge.List(ChangeProposalStatus.Proposed).Should().HaveCount(2);
        Forge.List(ChangeProposalStatus.Rejected).Should().HaveCount(2);
    }

    [Fact]
    public async Task Peer_daemon_counts_node_faults_as_errors_and_keeps_processing()
    {
        BreakGateWrite("ext-broken");
        using var http = new HttpClient(new PackagePeerHandler(Package("ext-broken"), Package("ext-good")));
        var result = await MeshAutoPullService.PullPeerOnceAsync(http, "https://peer.invalid", _project);
        result.Scanned.Should().Be(2);
        result.Errors.Should().Be(1);
        result.Refused.Should().Be(0);
        result.Held.Should().Be(1);
        Forge.List(ChangeProposalStatus.Proposed).Should().HaveCount(2);
        Forge.List(ChangeProposalStatus.Rejected).Should().HaveCount(2);
    }

    private sealed class PackagePeerHandler(string broken, string good) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.RequestUri!.AbsolutePath switch
            {
                "/mesh/v1/index" => """[{"file":"a.ashpkg","size":1},{"file":"b.ashpkg","size":1}]""",
                "/mesh/v1/pkg/a.ashpkg" => broken,
                "/mesh/v1/pkg/b.ashpkg" => good,
                _ => throw new InvalidOperationException("Unexpected fixture URL"),
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(content, Encoding.UTF8, "application/json") });
        }
    }

}
