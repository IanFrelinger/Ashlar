using System.Reflection;
using System.Runtime.CompilerServices;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Agents.TestKit;
using Ashlar.Core.Application.Common.Services;
using Ashlar.Core.Application.Environments;
using Ashlar.Core.Application.Ephemeral.Ports;
using Ashlar.Core.Domain.Agents;
using Ashlar.Core.Domain.Behaviors;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Core.Domain.Execution.Events;
using Ashlar.Infrastructure.Environments;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.LoadPolicy;
using Ashlar.Infrastructure.Execution.Models;
using Ashlar.Infrastructure.Execution.Ollama;
using Ashlar.Orchestration.Agents.Planning;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Orchestration.Coordination.Conflicts;
using Ashlar.Orchestration.Models;
using Ashlar.Orchestration.Negotiation;
using Ashlar.Orchestration.Negotiation.Models;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using IProviderFactory = Ashlar.Infrastructure.Execution.IProviderFactory;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.8b: the masks PR 4.8 left (review 2026-10-11 BL4, BL5 and M2). A refused Ollama availability
/// probe outside <c>AdaptiveProviderFactory</c>, and a refusal reaching the orchestration model wrapper, must reach
/// the caller as the refusal itself: never echo output, never "not available", never an outage whose chain lost it.
/// Every probe here is a real <see cref="ProviderFactory"/> health check over a stub transport that throws the
/// refusal, so no byte leaves the process and every send is counted.
/// </summary>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class EgressRefusalMaskingTests
{
    // mode 0: ashlar.model.prefer=deterministic with a provider directive (echo fallback without ASHLAR_ALLOW_MOCK);
    // mode 1: a provider directive under ASHLAR_ALLOW_MOCK=1 (echo fallback);
    // mode 2: a provider directive without ASHLAR_ALLOW_MOCK (ModelUnavailableException, which lost the refusal).
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, true)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, true)]
    public async Task Refused_Ollama_probe_reaches_the_HotSwappableModel_caller_through_ProviderBackedModel(
        int mode, bool adaptive, bool wrapped)
    {
        var refusal = Refusal();
        using var ollama = await RefusingOllama.CreateAsync(() => wrapped ? new HttpRequestException("private-canary", refusal) : refusal);
        IProviderFactory factory = adaptive
            ? new AdaptiveProviderFactory(ollama.Factory, Mock.Of<ILoadPolicy>())
            : ollama.Factory;
        var model = new HotSwappableModel(
            new ProviderBackedModel(factory, NullLogger<ProviderBackedModel>.Instance),
            NullLogger<HotSwappableModel>.Instance);
        using var providerEnv = EnvironmentVariableScope.Unset("ASHLAR_MODEL_PROVIDER");
        using var mockEnv = new EnvironmentVariableScope("ASHLAR_ALLOW_MOCK", mode == 1 ? "1" : null);
        var directives = "ashlar.model.provider=ollama" + (mode == 0 ? "\nashlar.model.prefer=deterministic" : "");

        ModelOutput? output = null;
        var error = await Record.ExceptionAsync(async () => output = await model.CompleteAsync(
            new ModelInput(new[] { ("system", directives), ("user", "echo-canary") }), CancellationToken.None));

        output.Should().BeNull("a refused availability probe must never become echo output");
        error.Should().BeSameAs(refusal, "the refusal is the final error, not 'not available' or an outage");
        ollama.Sends.Should().Be(1, "one health probe was refused before any byte left; nothing retried it");
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public async Task Orchestration_echo_fallback_never_answers_a_refusal(bool allowMock, bool deterministicWithProvider, bool wrapped)
    {
        var refusal = Refusal();
        var primary = new CountingModel(() => wrapped ? new IOException("private-canary", refusal) : refusal);
        var fallback = new CountingModel(fault: null);
        var model = WithFallback(new OrchestrationHotSwappableModel(primary, NullLogger<OrchestrationHotSwappableModel>.Instance), fallback);
        using var mockEnv = new EnvironmentVariableScope("ASHLAR_ALLOW_MOCK", allowMock ? "1" : null);
        var system = deterministicWithProvider ? "ashlar.model.prefer=deterministic\nashlar.model.provider=ollama" : "agent";

        ModelOutput? output = null;
        var error = await Record.ExceptionAsync(async () => output = await model.CompleteAsync(
            new ModelInput(new[] { ("system", system), ("user", "echo-canary") }), CancellationToken.None));

        output.Should().BeNull("a refused primary must never become echo output");
        error.Should().BeSameAs(refusal);
        primary.Calls.Should().Be(1);
        fallback.Calls.Should().Be(0, "the echo fallback is never consulted for a refusal");
    }

    [Fact]
    public async Task Orchestration_echo_fallback_seam_is_live_for_an_ordinary_failure()
    {
        // Control for the fact above: the replaced fallback IS the one the wrapper calls, so its zero count there is
        // a measurement, not an unwired fake.
        var primary = new CountingModel(() => new IOException("ordinary outage"));
        var fallback = new CountingModel(fault: null);
        var model = WithFallback(new OrchestrationHotSwappableModel(primary, NullLogger<OrchestrationHotSwappableModel>.Instance), fallback);
        using var mockEnv = new EnvironmentVariableScope("ASHLAR_ALLOW_MOCK", "1");

        var output = await model.CompleteAsync(new ModelInput(new[] { ("system", "agent"), ("user", "echo-canary") }), CancellationToken.None);

        output.Text.Should().Be(CountingModel.FallbackText);
        primary.Calls.Should().Be(1);
        fallback.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Refused_Ollama_probes_share_one_warning_window_flushed_on_factory_disposal()
    {
        var logger = new WarningRecorder();
        var refusals = new List<EgressRefusedException>();
        using var ollama = await RefusingOllama.CreateAsync(() =>
        {
            var refusal = Refusal();
            refusals.Add(refusal);
            return refusal;
        }, logger);

        ollama.Factory.IsProviderAvailable("ollama").Should().BeFalse();
        ollama.Factory.IsProviderAvailable("ollama").Should().BeFalse();

        ollama.Sends.Should().Be(2);
        refusals.Should().HaveCount(2);
        logger.Warnings.Should().Equal(new[] { (7307, 0L, false, refusals[0].Ref) },
            "two refusals at one site and reason inside one five-minute window log one Warning (D28)");
        ollama.Factory.Should().BeAssignableTo<IDisposable>("the factory owns its warning window and flushes it");
        (ollama.Factory as IDisposable)?.Dispose();
        logger.Warnings.Should().Equal(new[] { (7307, 0L, false, refusals[0].Ref), (7307, 1L, true, refusals[0].Ref) },
            "disposal flushes the suppressed count as one summary");
    }

    [Fact]
    public async Task Refused_probe_that_leaves_no_implementation_is_the_behavior_step_error()
    {
        var refusal = Refusal();
        using var ollama = await RefusingOllama.CreateAsync(() => refusal);
        var brick = new AgenticOnlyBrick();
        var executor = new BehaviorExecutor(
            new SingleBrickRegistry(brick),
            ollama.Factory,
            new SemanticCache(NullLogger<SemanticCache>.Instance),
            new SequentialLoopKernel(),
            NullLogger<BehaviorExecutor>.Instance);
        var behavior = new Behavior
        {
            Id = "refusal-behavior",
            Name = "refusal-behavior",
            Description = "one agentic step",
            Steps = new[] { new BehaviorStep { Id = "s1", BrickId = brick.Id } },
            OnStepFailure = FailurePolicy.Abort
        };
        var agent = new AgentCard { Id = "agent", Name = "agent", Description = "agent", Behaviors = new[] { behavior.Id } };

        var result = await executor.ExecuteAsync(agent, behavior, new BehaviorInput(),
            new ExecutionOptions { Provider = "ollama", IsAirGapped = false });

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain(refusal.Ref)
            .And.NotContain("No available implementation");
        brick.Calls.Should().Be(0, "no implementation could run");
        ollama.Sends.Should().BeGreaterThan(0, "the agentic availability probe was the refused send");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Planning_agent_never_answers_a_refusal_with_its_canned_recommendation(bool allowMock, bool wrapped)
    {
        // The production composition: AgentFactory.WrapModel puts AgentScopedModel(OrchestrationHotSwappableModel(..))
        // around the DI model of every planning, guide, assistant and wizard agent.
        var refusal = Refusal();
        var primary = new CountingModel(() => wrapped ? new IOException("private-canary", refusal) : refusal);
        var spec = new AgentSpawnSpec { AgentId = "planner", Domain = "planning", Goal = "plan" };
        var model = new AgentScopedModel(
            new OrchestrationHotSwappableModel(primary, NullLogger<OrchestrationHotSwappableModel>.Instance),
            spec.AgentId, spec.Domain, new ModelRuntimeSpec());
        var agent = new RecommendingPlanningAgent(spec, model);
        await agent.InitializeAsync();
        agent.AnswerEveryQuestion();
        using var mockEnv = new EnvironmentVariableScope("ASHLAR_ALLOW_MOCK", allowMock ? "1" : null);

        object? output = null;
        var error = await Record.ExceptionAsync(async () => output = await agent.ExecuteAsync());

        output.Should().BeNull("a refused recommendation must not become the canned one");
        error.Should().BeSameAs(refusal);
        primary.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synthesis_engine_never_answers_a_refusal_with_its_phased_fallback(bool wrapped)
    {
        var refusal = Refusal();
        var model = new CountingModel(() => wrapped ? new IOException("private-canary", refusal) : refusal);
        var engine = new SynthesisEngine(NullLogger<SynthesisEngine>.Instance, model);
        var positions = new[]
        {
            new NegotiationPosition { AgentId = "a1", Domain = "combat", PrimaryGoal = "action" },
            new NegotiationPosition { AgentId = "a2", Domain = "economy", PrimaryGoal = "depth" },
        };
        var conflict = new Conflict { ConflictType = ConflictType.Philosophy, AgentIds = new[] { "a1", "a2" }, Description = "pacing" };

        ProposedResolution? resolution = null;
        var error = await Record.ExceptionAsync(async () => resolution = await engine.SynthesizeAsync(positions, conflict));

        resolution.Should().BeNull("two positions would otherwise get the 'Phased approach' fallback at confidence 0.4");
        error.Should().BeSameAs(refusal);
        model.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Refused_probe_then_failing_fallback_reports_the_refusal_as_the_step_error()
    {
        var refusal = Refusal();
        using var ollama = await RefusingOllama.CreateAsync(() => refusal);
        var brick = new ScriptedBrick(agentic: () => throw new InvalidOperationException("agentic must not run"),
            deterministic: () => throw new IOException("deterministic-broke"));

        var events = await RunStepAsync(ollama.Factory, brick);

        brick.AgenticCalls.Should().Be(0, "the refused probe skipped the agentic implementation");
        brick.DeterministicCalls.Should().Be(1);
        var errors = StepErrors(events);
        errors.Should().NotBeEmpty();
        errors[^1].Should().Contain(refusal.Ref,
            "every alternative failed or was skipped and one was refused, so the refusal is the final error (D23)");
        events.OfType<BehaviorCompletedEvent>().Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task In_chain_refusal_then_failing_fallback_reports_the_refusal_as_the_step_error()
    {
        var refusal = Refusal();
        var brick = new ScriptedBrick(agentic: () => throw new IOException("private-canary", refusal),
            deterministic: () => throw new IOException("deterministic-broke"));

        var events = await RunStepAsync(AvailableProviders(), brick);

        brick.AgenticCalls.Should().Be(1);
        brick.DeterministicCalls.Should().Be(1);
        var errors = StepErrors(events);
        errors.Should().NotBeEmpty();
        errors[^1].Should().Contain(refusal.Ref,
            "the refused attempt, not the fallback's own failure, is the final error (D23)");
        events.OfType<BehaviorCompletedEvent>().Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Unwrapped_refused_attempt_then_failing_fallback_reports_the_refusal_once()
    {
        // The attempt's own error event already carries the refusal's message; the step must not report it twice
        // (IBehaviorExecutor.ExecuteAsync puts every StepErrorEvent into Errors).
        var refusal = Refusal();
        var brick = new ScriptedBrick(agentic: () => throw refusal,
            deterministic: () => throw new IOException("deterministic-broke"));

        var events = await RunStepAsync(AvailableProviders(), brick);

        brick.AgenticCalls.Should().Be(1);
        brick.DeterministicCalls.Should().Be(1);
        var errors = StepErrors(events);
        errors.Count(e => e.Contains(refusal.Ref, StringComparison.Ordinal)).Should().Be(1,
            "the refusal is reported exactly once in the step");
        errors.Should().HaveCount(2, "the refused attempt and the failing fallback, nothing appended");
        events.OfType<BehaviorCompletedEvent>().Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Material_intelligence_never_answers_a_refusal_with_heuristic_materials(bool wrapped)
    {
        var refusal = Refusal();
        var model = new CountingModel(() => wrapped ? new IOException("private-canary", refusal) : refusal);
        var service = new ModelBackedMaterialIntelligenceService(model);

        MaterialIntelligenceResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await service.SuggestMaterialsAsync(
            new MaterialIntelligenceRequest("coastal", new Dictionary<string, string> { ["highway"] = "primary" },
                new MapDataRequestContext("session-1"))));

        result.Should().BeNull("a refused model must not become heuristic materials");
        error.Should().BeSameAs(refusal);
        model.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Vector_map_intelligence_never_answers_a_refusal_with_the_input_passed_through(bool wrapped)
    {
        var refusal = Refusal();
        var model = new CountingModel(() => wrapped ? new IOException("private-canary", refusal) : refusal);
        var service = new ModelBackedVectorMapIntelligenceService(model);
        var raw = System.Text.Encoding.UTF8.GetBytes("""{"type":"FeatureCollection","features":[]}""");

        VectorMapIntelligenceResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await service.RefineAsync(
            new VectorMapIntelligenceRequest(raw, "geojson", new MapDataRequestContext("session-1"))));

        result.Should().BeNull("a refused model must not become the input passed through as a result");
        error.Should().BeSameAs(refusal);
        model.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Refused_attempt_then_succeeding_fallback_completes_the_step()
    {
        // Control: a brick's deterministic implementation is not the echo model, so D23 lets it answer.
        var refusal = Refusal();
        var brick = new ScriptedBrick(agentic: () => throw new IOException("private-canary", refusal),
            deterministic: () => new BrickOutput { Summary = "deterministic-ran" });

        var events = await RunStepAsync(AvailableProviders(), brick);

        brick.AgenticCalls.Should().Be(1);
        brick.DeterministicCalls.Should().Be(1);
        events.OfType<StepCompletedEvent>().Should().ContainSingle().Which.Implementation.Should().Be(ImplementationType.Deterministic);
        StepErrors(events).Should().ContainSingle("only the refused attempt reported an error; the fallback's success stands");
        events.OfType<BehaviorCompletedEvent>().Should().ContainSingle().Which.Success.Should().BeTrue();
    }

    private static IProviderFactory AvailableProviders()
    {
        var providers = new Mock<IProviderFactory>();
        providers.Setup(x => x.IsProviderAvailable(It.IsAny<string>())).Returns(true);
        return providers.Object;
    }

    private static async Task<List<ExecutionEvent>> RunStepAsync(IProviderFactory providers, DomainBrick brick)
    {
        var executor = new BehaviorExecutor(
            new SingleBrickRegistry(brick),
            providers,
            new SemanticCache(NullLogger<SemanticCache>.Instance),
            new SequentialLoopKernel(),
            NullLogger<BehaviorExecutor>.Instance);
        var behavior = new Behavior
        {
            Id = "fallback-behavior",
            Name = "fallback-behavior",
            Description = "one step, agentic then deterministic",
            Steps = new[] { new BehaviorStep { Id = "s1", BrickId = brick.Id } },
            OnStepFailure = FailurePolicy.Abort
        };
        var agent = new AgentCard { Id = "agent", Name = "agent", Description = "agent", Behaviors = new[] { behavior.Id } };
        var events = new List<ExecutionEvent>();
        await foreach (var evt in executor.ExecuteWithEventsAsync(agent, behavior, new BehaviorInput(),
            new ExecutionOptions { Provider = "ollama", IsAirGapped = false, SwapOnFailure = true }))
            events.Add(evt);
        return events;
    }

    private static List<string> StepErrors(IEnumerable<ExecutionEvent> events) =>
        events.OfType<StepErrorEvent>().Where(e => e.StepId == "s1").Select(e => e.Error).ToList();

    private static IModel WithFallback(OrchestrationHotSwappableModel model, IModel fallback)
    {
        var field = typeof(OrchestrationHotSwappableModel).GetField("_fallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(model, fallback);
        field.GetValue(model).Should().BeSameAs(fallback, "arrange: the counting fallback is wired");
        return model;
    }

    private static EgressRefusedException Refusal() => new(new EgressGuard("full", "enforce").Evaluate(
        new EgressRequest(EgressFamilies.Http, "provider-refusal", new Uri("https://remote.example"))));

    /// <summary>A real <see cref="ProviderFactory"/> whose Ollama health probe (EG-MDL-07, GET api/tags) is refused.</summary>
    private sealed class RefusingOllama : IDisposable
    {
        private const string BaseUrl = "https://remote.example";
        private readonly HttpClient _client;
        private readonly StrongBox<int> _sends;

        private RefusingOllama(ProviderFactory factory, HttpClient client, StrongBox<int> sends)
        {
            Factory = factory;
            _client = client;
            _sends = sends;
        }

        internal ProviderFactory Factory { get; }

        internal int Sends => Volatile.Read(ref _sends.Value);

        internal static async Task<RefusingOllama> CreateAsync(Func<Exception> fault, ILogger<ProviderFactory>? logger = null)
        {
            var lifecycle = new Mock<IEphemeralModelLifecycle>();
            // The constructor's background warm-up ends at Debug (cancelled), so every Warning a test sees is a probe's.
            lifecycle.Setup(x => x.GetBaseUrlAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
            var factory = new ProviderFactory(logger ?? NullLogger<ProviderFactory>.Instance, lifecycle.Object);
            await factory.OllamaWarmup;
            lifecycle.Setup(x => x.GetBaseUrlAsync(It.IsAny<CancellationToken>())).ReturnsAsync(BaseUrl);
            var sends = new StrongBox<int>();
            var client = new HttpClient(StubHttpMessageHandler.FromSync((_, _) =>
            {
                Interlocked.Increment(ref sends.Value);
                throw fault();
            }));
            typeof(ProviderFactory).GetField("_ollamaProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(factory, new OllamaProvider(client, BaseUrl));
            typeof(ProviderFactory).GetField("_ollamaProviderBaseUrl", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(factory, BaseUrl);
            return new RefusingOllama(factory, client, sends);
        }

        public void Dispose()
        {
            // Compiles against a factory that is not disposable, so the twins can run red before the fix.
            (Factory as IDisposable)?.Dispose();
            _client.Dispose();
        }
    }

    private sealed class CountingModel(Func<Exception>? fault) : IModel
    {
        internal const string FallbackText = "counting-fallback";
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return fault is null ? Task.FromResult(new ModelOutput(FallbackText)) : Task.FromException<ModelOutput>(fault());
        }
    }

    private sealed class WarningRecorder : ILogger<ProviderFactory>
    {
        private readonly List<(int EventId, long Suppressed, bool Summary, string Ref)> _warnings = new();

        internal IReadOnlyList<(int EventId, long Suppressed, bool Summary, string Ref)> Warnings
        {
            get { lock (_warnings) return _warnings.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            var values = state as IReadOnlyList<KeyValuePair<string, object?>>;
            object? Value(string key) => values?.FirstOrDefault(pair => pair.Key == key).Value;
            var entry = (eventId.Id, Value("Suppressed") as long? ?? -1L, Value("Summary") as bool? ?? false,
                Value("Ref") as string ?? "");
            lock (_warnings) _warnings.Add(entry);
        }
    }

    private sealed class SingleBrickRegistry(DomainBrick brick) : Ashlar.Core.Domain.Execution.IBrickRegistry
    {
        public DomainBrick? GetBrick(string id) => id == brick.Id ? brick : null;

        public IReadOnlyList<DomainBrick> GetAllBricks() => new[] { brick };
    }

    /// <summary>A planning agent whose questions are all answered, so the next execution asks the model.</summary>
    private sealed class RecommendingPlanningAgent(AgentSpawnSpec spec, IModel model)
        : PlanningAgent(spec, NullLogger<Ashlar.Orchestration.Agents.BaseAgent>.Instance, model)
    {
        internal void AnswerEveryQuestion()
        {
            var questions = (System.Collections.ICollection)typeof(PlanningAgent)
                .GetField("_questions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            questions.Count.Should().BeGreaterThan(0, "arrange: the agent has questions to answer");
            typeof(PlanningAgent).GetField("_currentQuestionIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(this, questions.Count);
        }
    }

    /// <summary>A brick with both implementations, each scripted, counting its calls.</summary>
    private sealed class ScriptedBrick : DomainBrick
    {
        private readonly Func<BrickOutput> _agentic;
        private readonly Func<BrickOutput> _deterministic;
        private int _agenticCalls;
        private int _deterministicCalls;

        public ScriptedBrick(Func<BrickOutput> agentic, Func<BrickOutput> deterministic)
        {
            _agentic = agentic;
            _deterministic = deterministic;
            Id = "test.scripted";
            Name = "scripted";
            Description = "Agentic first, deterministic fallback.";
            Category = BrickCategory.Analysis;
            Implementations = new BrickImplementations
            {
                Deterministic = new DeterministicImplementation { Id = "d", Name = "d", Description = "d", Executor = "x" },
                Agentic = new AgenticImplementation { Id = "a", Name = "a", Description = "a" }
            };
            DefaultImplementation = ImplementationType.Agentic;
            FallbackChain = new[] { ImplementationType.Agentic, ImplementationType.Deterministic };
        }

        internal int AgenticCalls => Volatile.Read(ref _agenticCalls);

        internal int DeterministicCalls => Volatile.Read(ref _deterministicCalls);

        public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation,
            IExecutionContext context, CancellationToken cancellationToken = default)
        {
            if (implementation == ImplementationType.Agentic)
            {
                Interlocked.Increment(ref _agenticCalls);
                return Task.FromResult(_agentic());
            }
            Interlocked.Increment(ref _deterministicCalls);
            return Task.FromResult(_deterministic());
        }
    }

    private sealed class AgenticOnlyBrick : DomainBrick
    {
        private int _calls;

        public AgenticOnlyBrick()
        {
            Id = "test.agentic-only";
            Name = "agentic-only";
            Description = "Only an agentic implementation, so a refused probe leaves nothing to run.";
            Category = BrickCategory.Analysis;
            Implementations = new BrickImplementations
            {
                Agentic = new AgenticImplementation { Id = "a", Name = "a", Description = "a" }
            };
            DefaultImplementation = ImplementationType.Agentic;
            FallbackChain = new[] { ImplementationType.Agentic };
        }

        internal int Calls => Volatile.Read(ref _calls);

        public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation,
            IExecutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new BrickOutput { Summary = "ran" });
        }
    }
}
