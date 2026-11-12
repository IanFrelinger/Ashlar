using System.CommandLine;
using System.Text.RegularExpressions;
using Ashlar.CLI.Commands;
using Ashlar.Tests.CLI.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>Gate-store failures must remain legible when a historical filename contains terminal controls.</summary>
[Collection("MeshIntegration")]
public sealed class KeysGateDisplaySecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ashlar-keys-display-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousKeys = Environment.GetEnvironmentVariable("ASHLAR_KEY_DIR");

    public KeysGateDisplaySecurityTests()
    {
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", Path.Combine(_dir, "keys"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", _previousKeys);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Init_defuses_historical_gate_error_and_keeps_activation_remediation()
    {
        const string fileName = "historical-\u202e-forged.json";
        var project = Path.Combine(_dir, "project");
        var state = Path.Combine(project, ".ashlar");
        var gates = Path.Combine(state, "gates");
        Directory.CreateDirectory(gates);
        var path = Path.Combine(gates, fileName);
        await File.WriteAllTextAsync(path, "null");
        var before = await File.ReadAllBytesAsync(path);
        var output = new StringWriter();
        var error = new StringWriter();
        int code;
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var command = new RootCommand { new KeysCommand() };
            code = await command.InvokeAsync(["keys", "init", "--key-dir", Path.Combine(_dir, "keys"), "--path", project]);
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }

        var rendered = Regex.Replace(output.ToString() + error.ToString(), "\u001b\\[[0-9;]+m", string.Empty);
        code.Should().Be(1);
        rendered.Should().Contain("signing could not be activated").And.Contain("Corrupt gate record");
        rendered.Should().Contain("historical-�-forged.json").And.NotContain("\u202e");
        rendered.Should().Contain("activate it later with:  ashlar gates sign-activate");
        File.Exists(Path.Combine(state, "gate-signing.json")).Should().BeFalse();
        (await File.ReadAllBytesAsync(path)).Should().Equal(before);
    }
}
