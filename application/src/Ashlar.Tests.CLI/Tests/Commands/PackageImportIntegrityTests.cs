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
            .Replace("mayAdd: []", "mayAdd: [brick]", StringComparison.Ordinal);
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
    public async Task Imported_receipt_is_derived_from_verified_package_not_inherited_from_sender()
    {
        var json = Package(proposal: Proposal("ext-shared") with { ImportedPackageSeal = "invented", ImportedPackageSigner = _other.PublicKeyBase64 });
        (await PackageImport.SubmitAsync(_project, json)).Outcome.Should().Be(PackageAdmission.Held);
        ExtensionPackaging.TryOpen(json, out var package, out _).Should().BeTrue();
        var recorded = (await Gate.GetAsync("ext-shared"))!.Proposal;
        recorded.ImportedPackageSeal.Should().Be(package!.Seal);
        recorded.ImportedPackageSigner.Should().Be(_origin.PublicKeyBase64);
        (await PackageImport.SubmitAsync(_project, json)).Outcome.Should().Be(PackageAdmission.AlreadyImported);
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
}
