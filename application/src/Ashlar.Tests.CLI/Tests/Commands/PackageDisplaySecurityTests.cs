using System.CommandLine;
using System.Text.RegularExpressions;
using Ashlar.CLI.Commands;
using Ashlar.CLI.Output;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Packaging;
using Ashlar.Manifest.Signing;
using Ashlar.Tests.CLI.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>Valid signatures never authorize terminal control bytes, even before signer trust.</summary>
public sealed class PackageDisplaySecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ashlar-display-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousKeys = Environment.GetEnvironmentVariable("ASHLAR_KEY_DIR");
    private const string Controls = "\u001b]0;forged\u0007\r\n\u009b\u061c\u202e\u2066\u2028\u2029";

    public PackageDisplaySecurityTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", Path.Combine(_dir, "empty-keys"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", _previousKeys);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Package()
    {
        var signer = OperatorKey.Generate(Path.Combine(_dir, "origin-keys"));
        var record = new GateRecord
        {
            Proposal = new ExtensionProposal
            {
                Id = "id" + Controls,
                Kind = "kind" + Controls,
                Summary = "summary" + Controls + new string('x', 4000),
                ProposedBy = "proposer" + Controls,
                ProposedAt = DateTimeOffset.UtcNow,
                Courses = [new CourseResult { Name = "course" + Controls, Passed = true, Detail = "detail" + Controls + new string('y', 4000) }],
            },
            State = ProposalState.Admitted,
            Reason = "signed evidence",
            Actor = "actor" + Controls,
            DecidedAt = DateTimeOffset.UtcNow,
        };
        record = record with { Sig = signer.Sign(CanonicalJson.Bytes(record)), Signer = signer.PublicKeyBase64 };
        var json = ExtensionPackaging.Pack(record, [new PackageFile { Path = "src/file" + Controls + ".cs", Content = "// inert" }], signer);
        ExtensionPackaging.TryOpen(json, out _, out var reason).Should().BeTrue(reason);
        var path = Path.Combine(_dir, "signed.ashpkg");
        File.WriteAllText(path, json);
        return path;
    }

    private static async Task<(int Code, string Output)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var root = new RootCommand { new PkgCommand() };
            var code = await root.InvokeAsync(args);
            // Strip only our fixed SGR colour sequences, never attacker OSC/CR/bidi sequences.
            return (code, Regex.Replace(output.ToString() + error.ToString(), "\u001b\\[[0-9;]+m", string.Empty));
        }
        finally { Console.SetOut(ConsoleCapture.Out); Console.SetError(ConsoleCapture.Error); }
    }

    private static void AssertSafe(string output)
    {
        foreach (var control in Controls.Where(c => char.IsControl(c) || c is '\u061c' or '\u202e' or '\u2066' or '\u2028' or '\u2029').Distinct())
        {
            if (control is '\r' or '\n') continue; // Renderer-owned newlines checked by row count below.
            output.Should().NotContain(control.ToString());
        }
        output.Should().NotContain(Controls);
        output.Should().Contain("summary�]0;forged���");
        output.Should().Contain("truncated:");
        output.Length.Should().BeLessThan(7000);
        output.Split('\n').Length.Should().BeLessThan(20, "sender newlines must not manufacture rows");
    }

    [Fact]
    public async Task Show_validSelfSignedPackage_boundsAndEscapesEveryDisplayComponent()
    {
        var (code, output) = await Run("pkg", "show", Package());
        code.Should().Be(0);
        AssertSafe(output);
        foreach (var prefix in new[] { "id", "kind", "proposer", "course", "detail", "actor", "src/file" })
            output.Should().Contain(prefix + "�]0;forged���");
        output.Should().Contain("package verifies").And.Contain("sealed ed25519:");
    }

    [Fact]
    public async Task Import_untrustedSignerPreview_isSafeBeforeTheTrustRefusal()
    {
        var project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(project);
        ProjectScaffold.TryScaffold("display-test", out var manifest, out var policy, out var reason)
            .Should().BeTrue(reason);
        await File.WriteAllTextAsync(Path.Combine(project, "ashlar.yaml"), manifest);
        await File.WriteAllTextAsync(Path.Combine(project, "ashlar.policy.yaml"), policy);
        var (code, output) = await Run("pkg", "import", Package(), "--path", project);
        code.Should().Be(65);
        AssertSafe(output);
        output.Should().Contain("course�]0;forged���").And.Contain("detail�]0;forged���");
        output.Should().Contain("not a trusted signer").And.Contain("disk untouched");
        Directory.Exists(Path.Combine(project, ".ashlar")).Should().BeFalse();
    }
}
