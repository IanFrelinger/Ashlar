using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.Orchestration.Agents;
using Xunit;
using Ashlar.Tests.BackgroundAgents.Registry;
using Ashlar.Tests.BackgroundAgents.TestHelpers;

namespace Ashlar.Tests.BackgroundAgents.Registry;

/// <summary>
/// Verifies that the registry surfaces a warning when an agent has LLM routing
/// configured but its role doesn't actually invoke an LLM. This catches dead-config
/// bugs early instead of letting operators believe the optimizer/tester is "AI-driven"
/// when it's actually running the deterministic test/analysis runner.
/// </summary>
public sealed class IgnoredLlmConfigWarningTests
{
    [Theory]
    [InlineData("optimizer", "ollama", "llama3.1:latest", true)]
    [InlineData("tester", "ollama", null, true)]
    [InlineData("self-improver", "openai", "gpt-4", true)]
    [InlineData("optimizer", "deterministic", null, false)]
    [InlineData("optimizer", null, null, false)]
    [InlineData("extender", "ollama", "llama3.1:latest", false)]
    public async Task Warning_emitted_only_when_non_extender_has_real_model_routing(
        string role, string? provider, string? name, bool expectWarning)
    {
        var captured = new List<string>();
        var logger = new ListLogger<BackgroundAgentRegistry>(captured);
        var registry = new BackgroundAgentRegistry(
            new AgentScheduler(new ScheduleExecutor(), null),
            logger);

        var config = new BackgroundAgentConfig
        {
            Id = "agent-x",
            Role = role,
            Enabled = true,
            ModelProvider = provider!,
            ModelName = name
        };

        var spec = new BackgroundAgentSpecBuilder(new DataSensitivityRegistry(), null).BuildSpec(config);
        await registry.RegisterAuthoredAsync(new GenericAgent(spec.ToOrchestrationSpec(), NullLogger<GenericAgent>.Instance), config);

        if (expectWarning)
            captured.Should().Contain(m => m.Contains("IGNORED", StringComparison.Ordinal) && m.Contains("agent-x"));
        else
            captured.Should().NotContain(m => m.Contains("IGNORED", StringComparison.Ordinal));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _sink;
        public ListLogger(List<string> sink) { _sink = sink; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                _sink.Add(formatter(state, exception));
        }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
