using System.Text.Json;
using Ashlar.Core.Application.Execution.Ports;
using Xunit;

namespace Ashlar.Tests.Application.Tests.Execution;

/// <summary>
/// The task boundary carries plain data: an image, literal argv, resource budgets and
/// result evidence. Reconstructing that data must not require a live CLR execution context.
/// These are serialization checks, not a promise of a versioned remote wire protocol.
/// </summary>
public sealed class IsolatedTaskContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Request_round_trip_preserves_literal_argv_and_custom_resource_budgets()
    {
        var request = new IsolatedTaskRequest("parser@sha256:abc", new[]
        {
            "/app/parser", "", "a document; $(literal)", "quoted \"value\"\nsecond line", "日本語",
        })
        {
            MemoryBytes = 128L * 1024 * 1024,
            Cpus = 0.25m,
            Pids = 32,
            Timeout = TimeSpan.FromMilliseconds(12_345),
            MaxOutputBytes = 8192,
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var restored = Assert.IsType<IsolatedTaskRequest>(
            JsonSerializer.Deserialize<IsolatedTaskRequest>(json, JsonOptions));

        Assert.Equal(request.Image, restored.Image);
        Assert.Equal(request.Command, restored.Command);
        Assert.NotSame(request.Command, restored.Command);
        Assert.Equal(request.MemoryBytes, restored.MemoryBytes);
        Assert.Equal(request.Cpus, restored.Cpus);
        Assert.Equal(request.Pids, restored.Pids);
        Assert.Equal(request.Timeout, restored.Timeout);
        Assert.Equal(request.MaxOutputBytes, restored.MaxOutputBytes);
    }

    [Fact]
    public void Minimal_request_data_keeps_finite_default_budgets()
    {
        const string json = """
            {"image":"parser:local","command":["parser","document"]}
            """;

        var request = Assert.IsType<IsolatedTaskRequest>(
            JsonSerializer.Deserialize<IsolatedTaskRequest>(json, JsonOptions));

        Assert.Equal("parser:local", request.Image);
        Assert.Equal(new[] { "parser", "document" }, request.Command);
        Assert.Equal(256L * 1024 * 1024, request.MemoryBytes);
        Assert.Equal(1m, request.Cpus);
        Assert.Equal(64, request.Pids);
        Assert.Equal(TimeSpan.FromMinutes(1), request.Timeout);
        Assert.Equal(1024 * 1024, request.MaxOutputBytes);
    }

    [Fact]
    public void Output_limit_failure_exposes_the_budget_for_structured_error_reporting()
    {
        var failure = new ProcessOutputLimitExceededException(4096);

        // Hosts can report a typed failure and its budget without retaining command output
        // or parsing human-readable exception text.
        var json = JsonSerializer.Serialize(new { error = failure.GetType().Name, failure.MaxOutputBytes }, JsonOptions);
        using var report = JsonDocument.Parse(json);

        Assert.Equal("ProcessOutputLimitExceededException", report.RootElement.GetProperty("error").GetString());
        Assert.Equal(4096, report.RootElement.GetProperty("maxOutputBytes").GetInt32());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(17, false)]
    public void Result_round_trip_keeps_program_outcome_and_observed_environment(int exitCode, bool succeeded)
    {
        var evidence = new SessionAttestation
        {
            SessionId = "temporary-session",
            Image = "parser:local",
            ImageDigest = "sha256:observed-image",
            EngineVersion = "29.0.0",
            Requested = new ResourceLimits("134217728", 32, "0.25", TimeSpan.FromSeconds(12)),
            EffectiveMemoryBytes = 128L * 1024 * 1024,
            EffectivePidsLimit = 32,
            EffectiveNanoCpus = 250_000_000,
            EffectiveNetworkMode = "none",
            EffectiveReadOnlyRootFilesystem = true,
            EffectiveDroppedCapabilities = new[] { "ALL" },
            EffectiveSecurityOptions = new[] { "no-new-privileges:true" },
            AttestedAt = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero),
        };
        var result = new IsolatedTaskResult(
            new ProcessCommandResult(exitCode, "{\"rows\":2}\n", "diagnostic\n"), evidence);

        var json = JsonSerializer.Serialize(result, JsonOptions);
        var restored = Assert.IsType<IsolatedTaskResult>(
            JsonSerializer.Deserialize<IsolatedTaskResult>(json, JsonOptions));

        Assert.Equal(result.Command, restored.Command);
        Assert.Equal(succeeded, restored.Command.Succeeded);
        Assert.Equal(evidence.SessionId, restored.Environment.SessionId);
        Assert.Equal(evidence.ImageDigest, restored.Environment.ImageDigest);
        Assert.Equal(evidence.Requested, restored.Environment.Requested);
        Assert.Equal(evidence.AttestedAt, restored.Environment.AttestedAt);
        Assert.Empty(restored.Environment.FindShortfalls());
        // Covers every observed containment field as well as the requested limits, without
        // relying on reference equality for the attestation's deserialized lists.
        Assert.Equal(json, JsonSerializer.Serialize(restored, JsonOptions));
    }
}
