using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Core.Application.NodeCapabilityRuntime.Models;
using Ashlar.Core.Application.Testing.Abstractions;
using Ashlar.Core.Application.Testing.Models;
using Ashlar.Core.Domain.Agents;
using Ashlar.Core.Domain.Behaviors;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Core.Domain.Execution.Events;
using Ashlar.Core.Domain.Workflows;
using Ashlar.Core.Application.Common.Services;
using Ashlar.Infrastructure.Execution;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>Tests for behavior executor ncr escalation.</summary>
public sealed class BehaviorExecutorNcrEscalationTests : UnitTestBase
{
    public override async Task<TestResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            /// <summary>Test escalation emits typed event and fallback executes async.</summary>
            await TestEscalationEmitsTypedEventAndFallbackExecutesAsync();

            return new TestResult
            {
                Name = nameof(BehaviorExecutorNcrEscalationTests),
                Category = "Infrastructure",
                Passed = true,
                Message = "BehaviorExecutor NCR escalation tests passed"
            };
        }
        catch (AssertionException ex)
        {
            return new TestResult
            {
                Name = nameof(BehaviorExecutorNcrEscalationTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = $"Assertion failed: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
        catch (Exception ex)
        {
            return new TestResult
            {
                Name = nameof(BehaviorExecutorNcrEscalationTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = $"Unexpected exception: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
    }

    private async Task TestEscalationEmitsTypedEventAndFallbackExecutesAsync()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var metrics = new InMemoryMetricsCollector();
        var brick = new AgenticThenDeterministicBrick();
        var registry = new SingleBrickRegistry(brick);
        var providerFactory = new ProviderAvailableFactory();
        var cache = new SemanticCache(loggerFactory.CreateLogger<SemanticCache>());
        var agenticEngine = new EscalatingAgenticEngine();

        var executor = new BehaviorExecutor(
            registry,
            providerFactory,
            cache,
            new SequentialLoopKernel(),
            loggerFactory.CreateLogger<BehaviorExecutor>(),
            agenticEngine,
            null,
            metrics);

        var behavior = new Behavior
        {
            Id = "b-escalate",
            Name = "escalate",
            Description = "escalate",
            Steps =
            [
                new BehaviorStep
                {
                    Id = "s-escalate",
                    BrickId = brick.Id,
                    Implementation = ImplementationType.Auto,
                    InputMapping = new Dictionary<string, string>(),
                    OutputMapping = new Dictionary<string, string> { ["ok"] = "ok" }
                }
            ],
            OnStepFailure = FailurePolicy.Abort
        };

        var agent = new AgentCard { Id = "a1", Name = "a", Description = "a", Behaviors = [behavior.Id] };
        var options = new ExecutionOptions
        {
            Provider = "ollama",
            SwapOnFailure = true,
            ImplementationMode = ImplementationMode.AgenticPreferred
        };

        var sawEscalated = false;
        var sawCompleted = false;
        var implementations = new List<ImplementationType>();
        await foreach (var evt in executor.ExecuteWithEventsAsync(agent, behavior, new BehaviorInput(), options, CancellationToken.None))
        {
            if (evt is StepStartedEvent started)
            {
                implementations.Add(started.Implementation);
            }
            else if (evt is AgenticEscalatedEvent escalated)
            {
                sawEscalated = true;
                /// <summary>Assert equal.</summary>
                AssertEqual("s-escalate", escalated.StepId);
                /// <summary>Assert equal.</summary>
                AssertEqual(brick.Id, escalated.BrickId);
                AssertEqual(ResolutionReason.EscalatedPolicyBlocked.ToString(), escalated.Reason);
            }
            else if (evt is StepCompletedEvent)
            {
                sawCompleted = true;
            }
        }

        /// <summary>Assert true.</summary>
        /// <param name="AgenticEscalatedEvent"">Agentic escalated event".</param>
        AssertTrue(sawEscalated, "Escalation should emit AgenticEscalatedEvent");
        /// <summary>Assert true.</summary>
        /// <param name="complete"">Complete".</param>
        AssertTrue(sawCompleted, "Deterministic fallback should complete");
        /// <summary>Assert true.</summary>
        /// <param name="2">2.</param>
        /// <param name="deterministic"">Deterministic".</param>
        AssertTrue(implementations.Count >= 2, "Should attempt agentic then deterministic");
        /// <summary>Assert equal.</summary>
        AssertEqual(ImplementationType.Agentic, implementations[0]);
        AssertTrue(metrics.Counters.TryGetValue("ncr.execution.escalated.EscalatedPolicyBlocked", out var escalations) && escalations == 1);
    }

    /// <summary>Tests for agentic then deterministic brick.</summary>
    private sealed class AgenticThenDeterministicBrick : DomainBrick
    {
        public AgenticThenDeterministicBrick()
        {
            Id = "test.escalating";
            Name = "Escalating";
            Description = "Escalates when agentic";
            Category = BrickCategory.Analysis;
            Implementations = new BrickImplementations
            {
                Deterministic = new DeterministicImplementation { Id = "d", Name = "d", Description = "d", Executor = "x" },
                Agentic = new AgenticImplementation { Id = "a", Name = "a", Description = "a" }
            };
            DefaultImplementation = ImplementationType.Agentic;
            FallbackChain = [ImplementationType.Agentic, ImplementationType.Deterministic];
        }

        public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
        {
            if (implementation == ImplementationType.Agentic)
            {
                /// <summary>Invalid operation exception.</summary>
                /// <param name="escalation"">Escalation".</param>
                throw new InvalidOperationException("agentic path should not execute after escalation");
            }

            return Task.FromResult(new BrickOutput
            {
                ["ok"] = true,
                Summary = "ok"
            });
        }
    }

    /// <summary>Tests for escalating agentic engine.</summary>
    private sealed class EscalatingAgenticEngine : IAgenticBrickEngine
    {
        public Task<ModelResolution> ResolveModelForBrickAsync(DomainBrick brick, IExecutionContext context, CancellationToken ct = default)
        {
            return Task.FromResult(new ModelResolution
            {
                Model = new ModelDescriptor
                {
                    Id = "phi3-mini",
                    Provider = "ollama",
                    ProviderModelId = "phi3-mini"
                },
                Target = InferenceTarget.Escalate,
                Reason = ResolutionReason.EscalatedPolicyBlocked
            });
        }

        public Task RecordExecutionOutcomeAsync(ModelResolution resolution, BrickExecutionOutcome outcome, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>Tests for single brick registry.</summary>
    private sealed class SingleBrickRegistry : Ashlar.Core.Domain.Execution.IBrickRegistry
    {
        private readonly DomainBrick _brick;

        /// <summary>Single brick registry.</summary>
        /// <param name="brick">Brick.</param>
        public SingleBrickRegistry(DomainBrick brick) => _brick = brick;

        /// <summary>Gets brick.</summary>
        /// <param name="id">Id.</param>
        public DomainBrick? GetBrick(string id) => id == _brick.Id ? _brick : null;

        /// <summary>Gets all bricks.</summary>
        public IReadOnlyList<DomainBrick> GetAllBricks() => [_brick];
    }

    /// <summary>Tests for provider available factory.</summary>
    private sealed class ProviderAvailableFactory : Ashlar.Infrastructure.Execution.IProviderFactory
    {
        /// <summary>Returns whether  provider available.</summary>
        /// <param name="provider">Provider.</param>
        public bool IsProviderAvailable(string provider) => true;
        public Task<string> ExecuteLLMAsync(string provider, string systemPrompt, string userPrompt, object config, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");
        public Task<string> ExecuteVisionAsync(string provider, string systemPrompt, string userPrompt, byte[] imageBytes, object config, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");
        public Task<string> ExecuteVisionMultiFrameAsync(string provider, string systemPrompt, string userPrompt, IReadOnlyList<byte[]> frameBytes, object config, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");
        public Task<string> ExecuteVideoAsync(string systemPrompt, string userPrompt, IReadOnlyList<byte[]> frameBytes, object config, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");
        public Task EnsureOllamaReachableAsync(bool requireVisionModel, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>Tests for in memory metrics collector.</summary>
    private sealed class InMemoryMetricsCollector : IMetricsCollector
    {
        /// <summary>Execution times.</summary>
        public Dictionary<string, TimeSpan> ExecutionTimes { get; } = new(StringComparer.Ordinal);
        /// <summary>Counters.</summary>
        public Dictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);

        public void RecordExecutionTime(string operationName, TimeSpan duration)
        {
            ExecutionTimes.TryGetValue(operationName, out var existing);
            ExecutionTimes[operationName] = existing + duration;
        }

        public void IncrementCounter(string counterName, int value = 1)
        {
            Counters.TryGetValue(counterName, out var existing);
            Counters[counterName] = existing + value;
        }

        public Task<MetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new MetricsSnapshot
            {
                ExecutionTimes = new Dictionary<string, TimeSpan>(ExecutionTimes),
                Counters = new Dictionary<string, long>(Counters)
            });
        }
    }
}
