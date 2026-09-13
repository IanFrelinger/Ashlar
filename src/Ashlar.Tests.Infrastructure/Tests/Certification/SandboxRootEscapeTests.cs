using System.Text.Json;
using FluentAssertions;
using Ashlar.Abstractions;
using Ashlar.Tools.Dev;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Regression tests for the sandbox-root escape.
///
/// <para>Every repo tool used to declare a <c>root</c> property in its schema and combine it
/// with the model-supplied <c>path</c>. <c>PathAllowlist</c> inspects only <c>path</c> — it
/// rejects traversal and bounds absolute paths correctly, but it never looked at
/// <c>root</c>. A call with <c>root</c> pointing anywhere on disk and an entirely ordinary
/// relative path beginning <c>src/</c> therefore passed every policy and wrote outside the
/// repository, creating directories on the way.</para>
///
/// <para>These live under Tests/Certification deliberately. The cert gate filter matches
/// <c>FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Certification</c>, so a
/// regression here fails a required status check. The pre-existing
/// AdversarialScopeEscapeTests sit in Tests.Adaptation, which the filter does NOT match
/// (it matches <c>Tests.Adaptation.GenerationSafety</c> specifically), so putting them
/// there would have left them ungated.</para>
///
/// <para>They exercise the TOOLS, not the policy. Tools are reachable from the MCP bridge,
/// the gRPC transport and the CLI, not only through the background-agent policy engine, so
/// containment has to hold where it cannot be configured away.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class SandboxRootEscapeTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _outside;

    public SandboxRootEscapeTests()
    {
        var stem = Path.Combine(Path.GetTempPath(), "ashlar-sandbox-" + Guid.NewGuid().ToString("N"));
        _sandbox = Path.Combine(stem, "repo");
        _outside = Path.Combine(stem, "outside");
        Directory.CreateDirectory(_sandbox);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        var stem = Path.GetDirectoryName(_sandbox);
        if (stem is not null && Directory.Exists(stem))
        {
            try { Directory.Delete(stem, recursive: true); } catch { /* best effort */ }
        }
    }

    private WorldSnapshot Sandboxed() => WorldSnapshot.ForRepo(_sandbox);

    private static WorldSnapshot NoRoot() => new(0, new Dictionary<string, object?>());

    private static ToolCall Call(string id, object args) =>
        new(id, JsonSerializer.SerializeToElement(args));

    [Fact]
    public async Task Write_ignores_a_model_supplied_root()
    {
        // The escape: a root the model chose, plus a path that satisfies every allowlist
        // rule. Before the fix this wrote to <outside>/src/note.txt.
        var result = await new RepoFsWriteTool().InvokeAsync(
            Call("repo.fs.write", new { root = _outside, path = "src/note.txt", content = "x" }),
            Sandboxed(),
            CancellationToken.None);

        result.Should().NotBeNull();
        File.Exists(Path.Combine(_outside, "src", "note.txt")).Should().BeFalse(
            "the root supplied in tool arguments must be ignored entirely");
        File.Exists(Path.Combine(_sandbox, "src", "note.txt")).Should().BeTrue(
            "the write belongs under the sandbox root from the snapshot");
    }

    [Fact]
    public async Task Write_fails_closed_when_the_snapshot_declares_no_root()
    {
        var result = await new RepoFsWriteTool().InvokeAsync(
            Call("repo.fs.write", new { root = _outside, path = "src/note.txt", content = "x" }),
            NoRoot(),
            CancellationToken.None);

        result.Delta.Log.Should().Contain(l => l.Contains("REJECTED"));
        Directory.Exists(Path.Combine(_outside, "src")).Should().BeFalse(
            "a missing sandbox root must reject, never fall back to a guess");
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("src/../../escape.txt")]
    public async Task Write_refuses_traversal_out_of_the_sandbox(string path)
    {
        var result = await new RepoFsWriteTool().InvokeAsync(
            Call("repo.fs.write", new { path, content = "x" }),
            Sandboxed(),
            CancellationToken.None);

        result.Delta.Log.Should().Contain(l => l.Contains("REJECTED"));
    }

    [Fact]
    public async Task Write_refuses_an_absolute_path()
    {
        // Path.Combine(root, "/abs") discards root entirely, so this must be caught before
        // the combine rather than after it.
        var target = Path.Combine(_outside, "abs.txt");

        var result = await new RepoFsWriteTool().InvokeAsync(
            Call("repo.fs.write", new { path = target, content = "x" }),
            Sandboxed(),
            CancellationToken.None);

        result.Delta.Log.Should().Contain(l => l.Contains("REJECTED"));
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task Read_cannot_be_aimed_outside_the_sandbox()
    {
        var secret = Path.Combine(_outside, "secret.txt");
        await File.WriteAllTextAsync(secret, "classified");

        var result = await new RepoFsReadTool().InvokeAsync(
            Call("repo.fs.read", new { root = _outside, path = "secret.txt" }),
            Sandboxed(),
            CancellationToken.None);

        // Resolved under the sandbox, where no such file exists — the model's root is ignored.
        JsonSerializer.Serialize(result.Payload).Should().NotContain("classified");
    }

    [Fact]
    public async Task EnsureFile_ignores_a_model_supplied_root()
    {
        await new RepoFsEnsureFileTool().InvokeAsync(
            Call("repo.fs.ensure_file", new { root = _outside, path = "src/ensured.txt", content = "x" }),
            Sandboxed(),
            CancellationToken.None);

        File.Exists(Path.Combine(_outside, "src", "ensured.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task SearchReplace_ignores_a_model_supplied_root()
    {
        var victimDir = Path.Combine(_outside, "src");
        Directory.CreateDirectory(victimDir);
        var victim = Path.Combine(victimDir, "victim.txt");
        await File.WriteAllTextAsync(victim, "original");

        // The path resolves under the sandbox, where no such file exists, so the tool throws
        // FileNotFoundException — its pre-existing behaviour for a missing file, and proof in
        // itself that the model's root was not used. What matters is the file outside.
        var act = async () => await new RepoFsSearchReplaceTool().InvokeAsync(
            Call("repo.fs.search_replace", new { root = _outside, path = "src/victim.txt", find = "original", replace = "tampered" }),
            Sandboxed(),
            CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();

        (await File.ReadAllTextAsync(victim)).Should().Be("original",
            "a file outside the sandbox must be untouched");
    }

    [Fact]
    public async Task RoslynAnalyze_refuses_absolute_file_paths()
    {
        var outsideFile = Path.Combine(_outside, "Outside.cs");
        await File.WriteAllTextAsync(outsideFile, "namespace X; public sealed class Y { }");

        var result = await new RoslynAnalyzeTool().InvokeAsync(
            Call("roslyn.analyze", new { files = new[] { outsideFile }, rules = new { } }),
            Sandboxed(),
            CancellationToken.None);

        JsonSerializer.Serialize(result.Payload).Should().Contain("Roslyn.PathRejected",
            "absolute entries in the files array used to be read verbatim");
    }
}
