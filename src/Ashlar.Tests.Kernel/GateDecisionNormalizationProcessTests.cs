using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>Runtime globalization policy is proved in fresh processes, not changed after startup.</summary>
public sealed class GateDecisionNormalizationProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ashlar-decision-normalization-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Available_normalization_preserves_human_unicode_and_reserves_automatic_identity()
    {
        using var result = await RunProbe(invariant: false);
        var root = result.RootElement;
        AssertCommon(root, invariant: false);
        var attempts = root.GetProperty("attempts").EnumerateArray().ToArray();
        foreach (var item in attempts.Take(2))
        {
            item.GetProperty("outcome").GetString().Should().Be("written");
            item.GetProperty("persistedState").GetString().Should().Be("Admitted");
        }
        attempts[0].GetProperty("persistedActor").GetString().Should().Be("operator");
        attempts[1].GetProperty("persistedActor").GetString().Should().Be("Zoë Zoe\u0308 李 مریم\u200Cرضا 👩\u200D💻");
        var reserved = attempts[2];
        reserved.GetProperty("exceptionType").GetString().Should().Be(nameof(ArgumentException));
        reserved.GetProperty("parameter").GetString().Should().Be("actor");
        AssertRejectedWithoutPersistence(reserved);
    }

    [Fact]
    public async Task Invariant_globalization_refuses_all_human_decisions_before_persistence()
    {
        using var result = await RunProbe(invariant: true);
        var root = result.RootElement;
        AssertCommon(root, invariant: true);
        foreach (var item in root.GetProperty("attempts").EnumerateArray())
        {
            item.GetProperty("exceptionType").GetString().Should().Be(nameof(InvalidOperationException),
                "even an ASCII human actor cannot write when the reserved-label normalization policy is unavailable");
            item.GetProperty("message").GetString()!.ToLowerInvariant().Should().Contain("normalization");
            AssertRejectedWithoutPersistence(item);
        }
    }

    private static void AssertCommon(JsonElement root, bool invariant)
    {
        root.GetProperty("schema").GetInt32().Should().Be(1);
        root.GetProperty("invariantSetting").GetString().Should().Be(invariant ? "1" : "0");
        root.GetProperty("normalizationAvailable").GetBoolean().Should().Be(!invariant,
            "the child must actually exercise the requested runtime mode");
        root.GetProperty("automaticActors").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("gate", "gate", "gate");
        root.GetProperty("historicalActorPreserved").GetBoolean().Should().BeTrue();
        root.GetProperty("historicalBytesUnchanged").GetBoolean().Should().BeTrue();
        root.GetProperty("historicalSignatureVerified").GetBoolean().Should().BeTrue();
        root.GetProperty("historicalListed").GetBoolean().Should().BeTrue();
        var attempts = root.GetProperty("attempts").EnumerateArray().ToArray();
        attempts.Select(item => item.GetProperty("id").GetString())
            .Should().Equal("human-ascii", "human-unicode", "reserved-width");
        foreach (var item in attempts)
            item.GetProperty("signatureVerified").GetBoolean().Should().BeTrue();
    }

    private static void AssertRejectedWithoutPersistence(JsonElement item)
    {
        item.GetProperty("outcome").GetString().Should().Be("rejected");
        item.GetProperty("persistedState").GetString().Should().Be("Held");
        item.GetProperty("persistedActor").GetString().Should().Be("gate");
        item.GetProperty("afterSha256").GetString().Should().Be(item.GetProperty("beforeSha256").GetString(),
            "records, marker, keys and orphan temporary bytes must all remain unchanged");
        var before = item.GetProperty("filesBefore").EnumerateArray().Select(value => value.GetString()).ToArray();
        var after = item.GetProperty("filesAfter").EnumerateArray().Select(value => value.GetString()).ToArray();
        before.Should().Contain("gate-signing.json").And.Contain("gates/orphan.json.tmp");
        after.Should().Equal(before);
    }

    private async Task<JsonDocument> RunProbe(bool invariant)
    {
        Directory.CreateDirectory(_root);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        var probe = Path.Combine(AppContext.BaseDirectory, "InputSafetyProbe", "InputSafetyProbe.dll");
        File.Exists(probe).Should().BeTrue("the test build must include the child-process probe");
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add("decision-normalization");
        start.ArgumentList.Add(_root);
        start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = invariant ? "1" : "0";
        start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "0";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start decision normalization probe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Decision normalization probe did not exit within 30 seconds.");
        }
        var output = await stdout;
        var error = await stderr;
        process.ExitCode.Should().Be(0, "probe stdout: {0}; stderr: {1}", output, error);
        error.Should().BeEmpty();
        return JsonDocument.Parse(output);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
