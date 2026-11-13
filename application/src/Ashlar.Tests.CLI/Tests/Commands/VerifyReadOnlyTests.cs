using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ashlar.CLI.Commands;
using Ashlar.Manifest;
using Ashlar.Manifest.Ledger;
using Ashlar.Manifest.Signing;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// Exercises the real verify parser/handler against disposable authority state. No host,
/// listener, permission changes or isolation harness; read-only means no project/key writes.
/// </summary>
[Trait("Category", "CLI")]
[Collection("MeshIntegration")]
public sealed class VerifyReadOnlyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ashlar-verify-read-only-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(root, "project");
    private string KeyDir => Path.Combine(root, "keys");
    private string State => Path.Combine(Project, ".ashlar");
    private string EntryPath => Path.Combine(State, "ledger", "000001.json");

    public VerifyReadOnlyTests()
    {
        Directory.CreateDirectory(Project);
        ProjectScaffold.TryScaffold("triage", out var manifest, out var policy, out var reason)
            .Should().BeTrue(reason);
        File.WriteAllText(Path.Combine(Project, "ashlar.yaml"), manifest);
        File.WriteAllText(Path.Combine(Project, "ashlar.policy.yaml"), policy);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    public static IEnumerable<object[]> PrivateStates() =>
        new[] { "present", "absent", "corrupt", "mismatched" }.Select(state => new object[] { state });

    public static IEnumerable<object[]> ProvenanceFailures()
    {
        foreach (var failure in new[] { "stale", "tampered" })
        foreach (var state in new[] { "present", "absent", "corrupt", "mismatched" })
            yield return [failure, state];
    }

    [Theory]
    [MemberData(nameof(PrivateStates))]
    public async Task Read_only_unsigned_project_never_creates_governance_state_or_reads_private_key(string privateState)
    {
        PreparePrivateState(privateState);
        var before = Snapshot();

        var result = await InvokeAsync();

        result.Code.Should().Be(0);
        AssertReadOnlySuccess(result.Output);
        result.Output.Should().NotContain("provenance");
        Directory.Exists(State).Should().BeFalse("checking an unsigned project must not create a ledger or gate store");
        AssertUnchanged(before);
    }

    [Theory]
    [MemberData(nameof(PrivateStates))]
    public async Task Read_only_current_signed_project_preserves_ledger_and_ignores_private_key(string privateState)
    {
        await CertifyAsync();
        PreparePrivateState(privateState);
        PreserveExtraHistoricalBytes();
        var before = Snapshot();

        var result = await InvokeAsync();

        result.Code.Should().Be(0);
        AssertReadOnlySuccess(result.Output);
        result.Output.Should().Contain("provenance").And.Contain("chain intact").And.Contain("covers these documents");
        AssertUnchanged(before);
    }

    [Theory]
    [MemberData(nameof(ProvenanceFailures))]
    public async Task Read_only_provenance_failure_never_recertifies(string failure, string privateState)
    {
        await CertifyAsync();
        if (failure == "stale")
        {
            File.AppendAllText(Path.Combine(Project, "ashlar.yaml"), "\n# A newer document — Zoë 李\n");
        }
        else
        {
            var entry = JsonNode.Parse(File.ReadAllText(EntryPath))!.AsObject();
            entry["Subject"] = "forged-subject";
            File.WriteAllText(EntryPath, entry.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        PreparePrivateState(privateState);
        PreserveExtraHistoricalBytes();
        var before = Snapshot();

        var result = await InvokeAsync();

        result.Code.Should().Be(65);
        result.Output.Should().Contain("FAILED").And.Contain("provenance");
        result.Output.Should().NotContain("✓ VERIFIED").And.NotContain("CERTIFIED");
        File.Exists(Path.Combine(State, "ledger", "000002.json")).Should().BeFalse();
        AssertUnchanged(before);
    }

    [Fact]
    public async Task Default_verify_appends_fresh_certification_and_preserves_existing_history()
    {
        await CertifyAsync();
        var historicalBytes = Convert.ToBase64String(File.ReadAllBytes(EntryPath));
        var privateBytes = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(KeyDir, "operator.key")));
        File.AppendAllText(Path.Combine(Project, "ashlar.yaml"), "\n# Updated documents need explicit recertification.\n");

        var result = await InvokeAsync(readOnly: false);

        result.Code.Should().Be(0);
        result.Output.Should().Contain("CERTIFIED").And.Contain("ledger #2").And.NotContain("read-only");
        Convert.ToBase64String(File.ReadAllBytes(EntryPath)).Should().Be(historicalBytes);
        Convert.ToBase64String(File.ReadAllBytes(Path.Combine(KeyDir, "operator.key"))).Should().Be(privateBytes);
        var chain = new InstanceLedger(State).VerifyChain();
        chain.Count.Should().Be(2);
        chain.Head!.Subject.Should().Be(InstanceLedger.Subject(
            File.ReadAllText(Path.Combine(Project, "ashlar.yaml")),
            File.ReadAllText(Path.Combine(Project, "ashlar.policy.yaml"))));
        ProjectVerifier.Verify(File.ReadAllText(Path.Combine(Project, "ashlar.yaml")),
            File.ReadAllText(Path.Combine(Project, "ashlar.policy.yaml")), Project).Verified.Should().BeTrue();
    }

    [Theory]
    [InlineData("corrupt", "Corrupt operator key")]
    [InlineData("mismatched", "operator.key and operator.pub disagree")]
    public async Task Default_verify_refuses_invalid_private_material_without_unsigned_fallback(string privateState, string refusal)
    {
        PreparePrivateState(privateState);
        var before = Snapshot();

        var result = await InvokeAsync(readOnly: false);

        result.Code.Should().Be(1);
        result.Output.Should().Contain(refusal).And.NotContain("CERTIFIED").And.NotContain("✓ VERIFIED");
        Directory.Exists(State).Should().BeFalse();
        AssertUnchanged(before);
    }

    [Fact]
    public async Task Default_verify_without_a_key_retains_unsigned_behavior()
    {
        var before = Snapshot();

        var result = await InvokeAsync(readOnly: false);

        result.Code.Should().Be(0);
        result.Output.Should().Contain("VERIFIED").And.Contain("unsigned").And.NotContain("read-only");
        Directory.Exists(State).Should().BeFalse();
        AssertUnchanged(before);
    }

    [Fact]
    public async Task Read_only_missing_project_refuses_without_creating_state()
    {
        PreparePrivateState("present");
        var before = Snapshot();

        var result = await InvokeAsync(directory: Path.Combine(root, "missing-project"));

        result.Code.Should().Be(1);
        result.Output.Should().Contain("not an ashlar project");
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData("ashlar.yaml")]
    [InlineData("ashlar.policy.yaml")]
    public async Task Read_only_missing_document_refuses_without_creating_state(string document)
    {
        PreparePrivateState("present");
        File.Delete(Path.Combine(Project, document));
        var before = Snapshot();

        var result = await InvokeAsync();

        result.Code.Should().Be(1);
        result.Output.Should().Contain("not an ashlar project").And.Contain(document);
        AssertUnchanged(before);
    }

    [Fact]
    public async Task Read_only_json_request_refuses_before_verification()
    {
        PreparePrivateState("corrupt");
        var before = Snapshot();

        var result = await InvokeAsync(extraOptions: ["--format-json"]);

        result.Code.Should().Be(1);
        result.Output.Should().Contain("no JSON rendering").And.Contain("--format-json");
        result.Output.Should().NotContain("Corrupt operator key").And.NotContain("✓ VERIFIED");
        AssertUnchanged(before);
    }

    [Fact]
    public async Task Read_only_unrecognized_option_refuses_without_side_effects()
    {
        PreparePrivateState("present");
        var before = Snapshot();

        var result = await InvokeAsync(extraOptions: ["--allow-local-signing"]);

        result.Code.Should().NotBe(0);
        result.Output.Should().Contain("--allow-local-signing").And.NotContain("CERTIFIED");
        AssertUnchanged(before);
    }

    [Fact]
    public async Task Read_only_invalid_contract_reports_failure_without_loading_corrupt_private_key()
    {
        PreparePrivateState("corrupt");
        File.WriteAllText(Path.Combine(Project, "ashlar.yaml"), "not an application manifest");
        var before = Snapshot();

        var result = await InvokeAsync();

        result.Code.Should().Be(65);
        result.Output.Should().Contain("FAILED").And.Contain("contract").And.NotContain("Corrupt operator key");
        Directory.Exists(State).Should().BeFalse();
        AssertUnchanged(before);
    }

    private async Task CertifyAsync()
    {
        var signer = OperatorKey.Generate(KeyDir);
        await new InstanceLedger(State).AppendVerificationAsync(
            signer,
            InstanceLedger.Subject(File.ReadAllText(Path.Combine(Project, "ashlar.yaml")),
                File.ReadAllText(Path.Combine(Project, "ashlar.policy.yaml"))),
            verified: true,
            [new LedgerCourse { Name = "contract", Passed = true, Detail = "historical — Zoë 李" }],
            new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        // Formatting is outside the canonical signature. Preserve the original raw bytes too.
        File.WriteAllText(EntryPath, " \r\n" + File.ReadAllText(EntryPath) + "\r\n");
    }

    private void PreparePrivateState(string state)
    {
        if (!Directory.Exists(KeyDir) && state != "absent") OperatorKey.Generate(KeyDir);
        var privatePath = Path.Combine(KeyDir, "operator.key");
        switch (state)
        {
            case "absent":
                if (File.Exists(privatePath)) File.Delete(privatePath);
                break;
            case "corrupt": File.WriteAllText(privatePath, "not a private seed"); break;
            case "mismatched":
                var other = Path.Combine(root, "other-keys");
                OperatorKey.Generate(other);
                File.Copy(Path.Combine(other, "operator.key"), privatePath, overwrite: true);
                break;
        }
    }

    private void PreserveExtraHistoricalBytes()
    {
        File.WriteAllText(Path.Combine(State, "ledger", "orphan.json.tmp"), "leave this preexisting temporary file alone");
        File.WriteAllText(Path.Combine(State, "historical-note.txt"), "\uFEFFZoë Jose\u0301 李\r\n");
    }

    private async Task<(int Code, string Output)> InvokeAsync(bool readOnly = true, string? directory = null,
        params string[] extraOptions)
    {
        using var keyScope = new EnvironmentVariableScope("ASHLAR_KEY_DIR", KeyDir);
        using var color = new EnvironmentVariableScope("NO_COLOR", "1");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var command = new RootCommand { TreatUnmatchedTokensAsErrors = true };
            command.AddGlobalOption(new Option<bool>("--format-json"));
            command.AddCommand(new VerifyCommand());
            var args = new List<string> { "verify", "--path", directory ?? Project };
            if (readOnly) args.Add("--read-only");
            args.AddRange(extraOptions);
            var code = await command.InvokeAsync(args.ToArray());
            return (code, Regex.Replace(output.ToString() + error.ToString(), "\u001b\\[[0-9;]+m", string.Empty));
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }
    }

    private static void AssertReadOnlySuccess(string output) =>
        output.Should().Contain("VERIFIED").And.Contain("courses · read-only · no certification written")
            .And.NotContain("CERTIFIED").And.NotContain("unsigned");

    private Dictionary<string, string> Snapshot()
    {
        var entries = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => "dir:" + Path.GetRelativePath(root, path), _ => string.Empty);
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            entries.Add("file:" + Path.GetRelativePath(root, path), Convert.ToBase64String(File.ReadAllBytes(path)));
        return entries;
    }

    private void AssertUnchanged(Dictionary<string, string> before) =>
        Snapshot().Should().BeEquivalentTo(before, "read-only verification must preserve every existing project/key byte and directory");
}
