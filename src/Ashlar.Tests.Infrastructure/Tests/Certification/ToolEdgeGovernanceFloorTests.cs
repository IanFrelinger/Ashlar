using System.Text.Json;
using FluentAssertions;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.Tools.Dev;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The governance floor at the AUTHORING write edge: <c>ToolSandbox.TryResolveWritePath</c>,
/// exercised through the real tools rather than the policy engine.
///
/// <para>These exercise the TOOLS, not the policy, for the same reason
/// <see cref="SandboxRootEscapeTests"/> does: the policy chain is composed per host and is
/// reachable from the MCP bridge, the gRPC transport and the CLI as well as the background-agent
/// engine, and the composition that wrote the admission ledger had <c>.ashlar/</c> on its
/// allowlist. A floor that a policy list can widen is not a floor.</para>
///
/// <para>In <c>Tests/Certification</c> so they ride cert-gate. The existing tests that pinned the
/// permissive edge (<c>PathAllowlistTests</c>, <c>PathAllowlistGapCoverageTests</c>,
/// <c>PathAllowlistPropertyTests</c>) all sit in namespaces the cert-gate filter does not match.
/// Hermetic: a temp directory and the tools.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class ToolEdgeGovernanceFloorTests : IDisposable
{
    private readonly string _root;

    public ToolEdgeGovernanceFloorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ashlar-write-floor-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private WorldSnapshot Sandboxed() => WorldSnapshot.ForRepo(_root);

    private static ToolCall Call(string id, object args) =>
        new(id, JsonSerializer.SerializeToElement(args));

    private static JsonElement Payload(ToolResult result) =>
        JsonSerializer.SerializeToElement(result.Payload);

    private Task<ToolResult> WriteAsync(string path, string content = "x") =>
        new RepoFsWriteTool().InvokeAsync(Call("repo.fs.write", new { path, content }), Sandboxed(), CancellationToken.None);

    /// <summary>Three spellings of the same ledger file: canonical, case-flipped, and via a parent hop.</summary>
    public static TheoryData<string> LedgerSpellings() => new()
    {
        ".ashlar/gates/ext-1.json",
        ".Ashlar/gates/g1.json",
        "docs/../.ashlar/gates/g1.json",
    };

    [Theory]
    [MemberData(nameof(LedgerSpellings))]
    public async Task Every_write_tool_refuses_the_governance_ledger(string path)
    {
        var write = await WriteAsync(path, "{}");
        var replace = await new RepoFsSearchReplaceTool().InvokeAsync(
            Call("repo.fs.search_replace", new { path, find = "a", replace = "b" }), Sandboxed(), CancellationToken.None);
        var ensure = await new RepoFsEnsureFileTool().InvokeAsync(
            Call("repo.fs.ensure_file", new { path, content = "{}" }), Sandboxed(), CancellationToken.None);

        Payload(write).GetProperty("written").GetBoolean().Should().BeFalse(path);
        Payload(replace).GetProperty("replaced").GetBoolean().Should().BeFalse(path);
        Payload(ensure).GetProperty("created").GetBoolean().Should().BeFalse(path);
        foreach (var result in new[] { write, replace, ensure })
        {
            result.Delta.Log.Should().ContainSingle().Which.Should().Contain("REJECTED",
                "a governance refusal must be a rejection, not a silent no-op, for '{0}'", path);
        }

        Directory.Exists(Path.Combine(_root, ".ashlar")).Should().BeFalse("no tool may create the governance directory");
        Directory.Exists(Path.Combine(_root, ".Ashlar")).Should().BeFalse("no tool may create the governance directory under another spelling");
    }

    /// <summary>
    /// Build imports and tooling config are governance at ANY depth: a nested
    /// <c>.editorconfig</c> silences analyzers, a <c>dotnet-tools.json</c> restores a tool that
    /// runs on the next build, a <c>.props</c> anywhere above a project changes how it builds.
    /// Being under an allowed prefix does not make them authorable.
    /// </summary>
    [Theory]
    [InlineData("src/.editorconfig")]
    [InlineData("src/.globalconfig")]
    [InlineData("src/.config/dotnet-tools.json")]
    [InlineData("docs/Makefile")]
    [InlineData("application/global.json")]
    [InlineData("src/build/Directory.Build.props")]
    [InlineData("src/build/custom.props")]
    public async Task Write_refuses_build_and_tooling_configuration_under_an_allowed_prefix(string path)
    {
        var result = await WriteAsync(path);

        Payload(result).GetProperty("written").GetBoolean().Should().BeFalse(path);
        result.Delta.Log.Should().ContainSingle().Which.Should().Contain("REJECTED").And.Contain("governance");
        File.Exists(Path.Combine(_root, path)).Should().BeFalse(path);
    }

    /// <summary>
    /// The asymmetry the two floors exist for. A self-extend cycle scaffolds projects — the
    /// offline demo's <c>MockScaffoldingResponder</c> writes four <c>.csproj</c> files under
    /// <c>docs/UiDomainDemoGenerated</c> and <c>SelfExtendWorkflowSpec.UiSmokeProjectPath</c>
    /// names this one — so the authoring edge must NOT reuse <c>IsGovernancePath</c>.
    /// </summary>
    [Fact]
    public async Task Write_still_creates_a_project_file_under_an_allowed_prefix()
    {
        const string path = "docs/UiDomainDemoGenerated/avalonia/Ashlar.Ui.AvaloniaHost/Ashlar.Ui.AvaloniaHost.csproj";

        var result = await WriteAsync(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        result.Delta.Log.Should().ContainSingle().Which.Should().StartWith("write:").And.NotContain("REJECTED");
        File.Exists(Path.Combine(_root, path)).Should().BeTrue("a project file is authorable at the tool edge");
    }

    /// <summary>
    /// The safe-shape leg: an NTFS alternate data stream, a trailing dot (Win32 strips it,
    /// aliasing the file), a reserved device name, and an in-root <c>.</c> segment. Each of these
    /// passes lexical containment, so only the floor refuses them.
    /// </summary>
    [Theory]
    [InlineData("src/x.cs:evil")]
    [InlineData("src/a/b.")]
    [InlineData("src/con.cs")]
    [InlineData("src/./x.cs")]
    public async Task Write_refuses_an_alternate_data_stream_and_an_unsafe_segment(string path)
    {
        var result = await WriteAsync(path);

        Payload(result).GetProperty("written").GetBoolean().Should().BeFalse(path);
        result.Delta.Log.Should().ContainSingle().Which.Should().Contain("REJECTED");
        Directory.Exists(Path.Combine(_root, "src")).Should().BeFalse(
            "the refusal must land before the tool creates the parent directory, for '{0}'", path);
    }

    /// <summary>
    /// The reparse-point leg lives in the floor and the floor lives in the tool, so a leaf link
    /// planted under an allowed prefix cannot carry a write onto the operator policy. Skipped
    /// where the platform refuses to create the link — nothing to assert then.
    /// </summary>
    [Fact]
    public async Task A_leaf_symlink_cannot_carry_a_write_out_of_the_floor()
    {
        var policy = Path.Combine(_root, "ashlar.policy.yaml");
        await File.WriteAllTextAsync(policy, "original");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(_root, "src", "link.cs"), Path.Combine("..", "ashlar.policy.yaml"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // platform refuses unprivileged symlinks
        }

        var result = await WriteAsync("src/link.cs", "tampered");

        Payload(result).GetProperty("written").GetBoolean().Should().BeFalse();
        result.Delta.Log.Should().ContainSingle().Which.Should().Contain("REJECTED").And.Contain("symlink");
        (await File.ReadAllTextAsync(policy)).Should().Be("original", "the link's target must be untouched");
    }

    /// <summary>
    /// A refusal is not a write. <c>SelfExtendRunnerAdapter.ExtractWritePaths</c> harvests the
    /// delta log into the write paths the signed admission record claims as its diff; when the
    /// refusal shared the <c>write:</c> prefix, every refused governance write became a signed
    /// claim that it landed. The successful control keeps this from passing vacuously if the
    /// harvester's prefix ever changes.
    /// </summary>
    [Fact]
    public async Task A_refused_write_is_not_recorded_as_a_write_path()
    {
        var refused = await WriteAsync(".ashlar/gates/x.json", "{}");
        var landed = await WriteAsync("src/ok.cs", "// ok");

        refused.Delta.Log.Should().ContainSingle().Which.Should().StartWith("write-refused:");
        SelfExtendRunnerAdapter.ExtractWritePaths(refused.Delta.Log).Should().BeEmpty(
            "a refused write must never be harvested into the record's diff");
        SelfExtendRunnerAdapter.ExtractWritePaths(landed.Delta.Log).Should().ContainSingle()
            .Which.Should().Be("src/ok.cs", "the control: a write that landed is harvested as before");
    }
}
