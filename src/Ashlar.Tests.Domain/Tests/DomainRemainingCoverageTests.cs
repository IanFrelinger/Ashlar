using FluentAssertions;
using Ashlar.Core.Domain.Agents;
using Ashlar.Core.Domain.Behaviors;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Clusters;
using Ashlar.Core.Domain.Execution;
using Ashlar.Core.Domain.Execution.Events;
using Ashlar.Core.Domain.Exceptions;
using Ashlar.Core.Domain.Export;
using Ashlar.Core.Domain.Values;
using Ashlar.Core.Domain.Workflows;
using Xunit;
using ExecutionContext = Ashlar.Infrastructure.Execution.ExecutionContext;

namespace Ashlar.Tests.Domain.Tests;

/// <summary>Tests for domain remaining coverage.</summary>
public sealed class DomainRemainingCoverageTests
{
    [Fact]
    public void BrickInput_set_get_and_dictionary_constructor()
    {
        var fromDict = new BrickInput(new Dictionary<string, object> { ["a"] = 1 });
        fromDict.Get<int>("a").Should().Be(1);

        var input = new BrickInput();
        input.Set("key", "value");
        input.Get<string>("key").Should().Be("value");
        input.Get("missing", "default").Should().Be("default");
        input.ToDictionary().Should().ContainKey("key");

        var actMissing = () => input.Get<int>("missing");
        actMissing.Should().Throw<KeyNotFoundException>();

        input.Set("wrong", "text");
        var actCast = () => input.Get<int>("wrong");
        actCast.Should().Throw<InvalidCastException>();

        input.Set("typed", "seven");
        input.Get("typed", 0).Should().Be(0);

        input.Set("n", 5);
        input.Get("n", 0).Should().Be(5);
    }

    [Fact]
    public void BrickOutput_indexer_set_get_and_summary()
    {
        var output = new BrickOutput { Summary = "done" };
        output.Summary.Should().Be("done");
        output["x"] = 42;
        output["x"].Should().Be(42);
        output.Set("y", "z");
        output.Get<string>("y").Should().Be("z");
        output.ToDictionary().Should().ContainKey("y");

        var actMissing = () => output.Get<int>("nope");
        actMissing.Should().Throw<KeyNotFoundException>();

        output.Set("bad", "text");
        var actCast = () => output.Get<int>("bad");
        actCast.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void BrickMetadata_and_implementations_round_trip()
    {
        var meta = new BrickMetadata
        {
            Author = "team",
            License = "MIT",
            Repository = "https://example.com/repo",
            UsageCount = 3,
            LastUpdated = DateTime.UtcNow,
        };
        meta.Author.Should().Be("team");
        meta.UsageCount.Should().Be(3);

        var implementations = new BrickImplementations
        {
            Deterministic = new DeterministicImplementation
            {
                Id = "det-1",
                Name = "Rules",
                Description = "rule-based",
                Executor = "RuleEngineExecutor",
                Config = new Dictionary<string, object> { ["k"] = 1 },
            },
            Agentic = new AgenticImplementation
            {
                Id = "ag-1",
                Name = "LLM",
                Description = "model",
                ProviderMappings = new Dictionary<string, ProviderConfig>
                {
                    ["openai"] = new ProviderConfig("gpt-4", "https://api.openai.com"),
                },
                LLMConfig = new LLMConfig
                {
                    Model = "gpt-4",
                    SystemPrompt = "sys",
                    Temperature = 0.2,
                    MaxTokens = 100,
                    Tools = new[] { "search" },
                },
            },
        };
        implementations.HasDeterministic.Should().BeTrue();
        implementations.HasAgentic.Should().BeTrue();
    }

    [Fact]
    public void BehaviorInput_constructors_initialize_parameters()
    {
        new BehaviorInput().Parameters.Should().BeEmpty();

        var withParams = new BehaviorInput(new Dictionary<string, object> { ["p"] = 1 });
        withParams.Parameters["p"].Should().Be(1);
    }

    [Fact]
    public void AgenticEscalatedEvent_exposes_escalation_fields()
    {
        var evt = new AgenticEscalatedEvent("step-1", "brick-1", "queue depth", "model-x", "ollama");
        evt.StepId.Should().Be("step-1");
        evt.BrickId.Should().Be("brick-1");
        evt.Reason.Should().Be("queue depth");
        evt.ModelId.Should().Be("model-x");
        evt.Provider.Should().Be("ollama");
        evt.Type.Should().Be("agentic_escalated");
    }

    [Fact]
    public void ImmutableCoreViolationException_supports_all_constructors()
    {
        var simple = new ImmutableCoreViolationException("blocked");
        simple.Message.Should().Be("blocked");
        simple.TargetComponent.Should().BeNull();

        var withTarget = new ImmutableCoreViolationException("blocked", "pipeline");
        withTarget.Message.Should().Be("blocked");
        withTarget.TargetComponent.Should().Be("pipeline");

        var inner = new InvalidOperationException("root");
        var wrapped = new ImmutableCoreViolationException("wrapped", inner);
        wrapped.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void ImplementationSelector_prefers_agentic_when_matching_condition()
    {
        var selector = new ImplementationSelector
        {
            PreferDeterministic = [],
            PreferAgentic = ["context.auditMode"],
            Default = ImplementationType.Deterministic,
        };
        var ctx = new ExecutionContext
        {
            AgentId = "a",
            BehaviorId = "b",
            IsAirGapped = false,
            AuditMode = true,
            Provider = "mock",
            Variables = new Dictionary<string, object>(),
        };
        selector.Select(ctx).Should().Be(ImplementationType.Agentic);
    }

    [Fact]
    public void WorkflowDefinition_output_node_and_metadata_defaults()
    {
        var definition = new WorkflowDefinition
        {
            Description = "visual flow",
            Metadata = new WorkflowMetadata
            {
                CustomData = new Dictionary<string, object> { ["tag"] = "demo" },
            },
        };
        definition.Description.Should().Be("visual flow");
        definition.Metadata.CustomData.Should().ContainKey("tag");

        var output = new OutputNode
        {
            WebhookUrl = "https://hook",
            DatabaseConnectionString = "Host=db",
            TableName = "results",
        };
        output.WebhookUrl.Should().Be("https://hook");
        output.TableName.Should().Be("results");
    }

    [Fact]
    public void AgentCard_platform_configs_and_behavior_step_config()
    {
        var card = new AgentCard
        {
            Id = "agent-1",
            Name = "Planner",
            Domain = "game",
            Description = "plans",
            PlatformConfigs = new Dictionary<Platform, PlatformConfig>
            {
                [Platform.Unity] = new PlatformConfig { EntryPoint = "game.main" },
            },
        };
        card.PlatformConfigs.Should().ContainKey(Platform.Unity);

        var step = new BehaviorStep
        {
            BrickId = "echo",
            Config = new Dictionary<string, object> { ["depth"] = 2 },
        };
        step.Config.Should().ContainKey("depth");
    }

    [Fact]
    public void ScalingConfig_dynamic_and_event_driven_fields()
    {
        var scaling = new ScalingConfig
        {
            Mode = ScalingMode.Dynamic,
            DynamicExpression = "playerCount * 2",
            TriggerEvent = "wave_start",
        };
        scaling.DynamicExpression.Should().Be("playerCount * 2");
        scaling.TriggerEvent.Should().Be("wave_start");
    }

    [Fact]
    public void ExecutionOptions_exposes_override_dictionaries()
    {
        var options = new ExecutionOptions
        {
            BehaviorOverrides = new Dictionary<string, ImplementationMode> { ["b"] = ImplementationMode.Mixed },
            BrickOverrides = new Dictionary<string, ImplementationType> { ["echo"] = ImplementationType.Agentic },
        };
        options.BehaviorOverrides["b"].Should().Be(ImplementationMode.Mixed);
        options.BrickOverrides["echo"].Should().Be(ImplementationType.Agentic);
    }

    [Fact]
    public void TestBrick_default_properties_are_initialized()
    {
        var brick = new TestBrick();
        brick.FallbackChain.Should().HaveCount(2);
        brick.Metadata.Should().NotBeNull();
        brick.Selector.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(AIConfidenceLevel))]
    [InlineData(nameof(AIEngineType))]
    [InlineData(nameof(AIProviderType))]
    [InlineData(nameof(BetaProgramStatus))]
    [InlineData(nameof(HealthStatus))]
    [InlineData(nameof(MethodVisibility))]
    [InlineData(nameof(OnboardingStatus))]
    [InlineData(nameof(ProjectStatus))]
    [InlineData(nameof(SprintStatus))]
    [InlineData(nameof(TaskPriority))]
    [InlineData("DomainTaskStatus")]
    [InlineData(nameof(RiskLevel))]
    public void Value_objects_support_equality_and_factory_methods(string typeName)
    {
        var (fromName, fromValue, sample, other) = CreateValueObjectSamples(typeName);
        fromName.Should().NotBeNull();
        fromValue.Should().NotBeNull();
        sample.Equals(other).Should().BeFalse();
        sample.GetHashCode().Should().Be(sample.GetHashCode());
        sample.Equals(fromName).Should().BeTrue();
        (sample == fromName).Should().BeTrue();
        (sample != other).Should().BeTrue();
        sample.ToString().Should().NotBeNullOrWhiteSpace();
        GetDescription(sample).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TaskPriority_comparison_operators_order_by_value()
    {
        (TaskPriority.High > TaskPriority.Low).Should().BeTrue();
        (TaskPriority.Low < TaskPriority.High).Should().BeTrue();
        (TaskPriority.High >= TaskPriority.Medium).Should().BeTrue();
        (TaskPriority.Low <= TaskPriority.Medium).Should().BeTrue();
    }

    [Fact]
    public void ClusterInput_exposes_port_and_parameter_dictionaries()
    {
        var input = new ClusterInput
        {
            PortValues = new Dictionary<string, object> { ["in"] = 1 },
            Parameters = new Dictionary<string, object> { ["p"] = "v" },
        };
        input.PortValues.Should().ContainKey("in");
        input.Parameters["p"].Should().Be("v");
    }

    [Fact]
    public void ClusterStats_tracks_usage_metrics()
    {
        var stats = new ClusterStats { UsageCount = 9, FavoriteCount = 2, AverageRating = 4.5 };
        stats.UsageCount.Should().Be(9);
        stats.FavoriteCount.Should().Be(2);
        stats.AverageRating.Should().Be(4.5);
    }

    [Fact]
    public void DistributionRule_supports_enum_and_range_fields()
    {
        var rule = new DistributionRule
        {
            Parameter = "count",
            Type = DistributionType.Range,
            Values = new object[] { 1, 2 },
            RangeMin = 0,
            RangeMax = 10,
        };
        rule.Parameter.Should().Be("count");
        rule.Type.Should().Be(DistributionType.Range);
        rule.Values.Should().HaveCount(2);
        rule.RangeMin.Should().Be(0);
        rule.RangeMax.Should().Be(10);
    }

    [Fact]
    public void BehaviorResult_and_ExecutionOptions_round_trip()
    {
        var result = new BehaviorResult
        {
            Success = true,
            Outputs = new Dictionary<string, object> { ["k"] = 1 },
            Errors = new[] { "warn" },
            Duration = TimeSpan.FromSeconds(2),
        };
        result.Success.Should().BeTrue();
        result.Outputs.Should().ContainKey("k");
        result.Errors.Should().ContainSingle();
        result.Duration.Should().Be(TimeSpan.FromSeconds(2));

        var opts = new ExecutionOptions
        {
            IsAirGapped = true,
            AuditMode = true,
            Provider = "mock",
            BrickRuntime = new Dictionary<string, BrickRuntimeSpec>
            {
                ["b"] = BrickRuntimeSpec.AgenticOnly(),
            },
        };
        opts.IsAirGapped.Should().BeTrue();
        opts.BrickRuntime["b"].Prefer.Should().Be("agentic");
    }

    [Fact]
    public void BrickRuntimeSpec_factory_methods_set_preference_and_fallback()
    {
        BrickRuntimeSpec.DeterministicOnly().Prefer.Should().Be("deterministic");
        BrickRuntimeSpec.AgenticWithDeterministicFallback().Fallback.Should().HaveCount(2);
        BrickRuntimeSpec.AgenticOnly().Fallback.Should().ContainSingle().Which.Should().Be(ImplementationType.Agentic);
    }

    [Fact]
    public void ImplementationSelector_evaluates_air_gapped_and_audit_conditions()
    {
        var selector = new ImplementationSelector
        {
            PreferDeterministic = new[] { "environment.airGapped", "context.auditMode" },
            PreferAgentic = new[] { "unknown.condition" },
            Default = ImplementationType.Agentic,
        };

        var airGapped = new ExecutionContext
        {
            AgentId = "a",
            BehaviorId = "b",
            IsAirGapped = true,
            AuditMode = false,
            Provider = "mock",
            Variables = new Dictionary<string, object>(),
        };
        selector.Select(airGapped).Should().Be(ImplementationType.Deterministic);

        var audit = new ExecutionContext
        {
            AgentId = "a",
            BehaviorId = "b",
            IsAirGapped = false,
            AuditMode = true,
            Provider = "mock",
            Variables = new Dictionary<string, object>(),
        };
        selector.Select(audit).Should().Be(ImplementationType.Deterministic);

        var defaultCtx = new ExecutionContext
        {
            AgentId = "a",
            BehaviorId = "b",
            IsAirGapped = false,
            AuditMode = false,
            Provider = "mock",
            Variables = new Dictionary<string, object>(),
        };
        selector.Select(defaultCtx).Should().Be(ImplementationType.Agentic);
    }

    [Fact]
    public void Workflow_and_connection_round_trip()
    {
        var workflow = new Workflow
        {
            Id = "wf-1",
            Name = "Main",
            Description = "desc",
            Instances = new[] { new ClusterInstance { InstanceId = "i1", ClusterId = "c1" } },
            Connections = new[]
            {
                new WorkflowConnection
                {
                    Id = "conn-1",
                    FromInstanceId = "i1",
                    FromOutput = "out",
                    ToInstanceId = "i2",
                    ToInput = "in",
                },
            },
        };
        workflow.Instances.Should().ContainSingle();
        workflow.Connections[0].ToInput.Should().Be("in");
    }

    [Fact]
    public void Export_result_and_generation_summary_round_trip()
    {
        var item = new GeneratedItem
        {
            BrickId = "b1",
            ItemType = "dialogue",
            VariationCount = 3,
            Reviewed = true,
        };
        var summary = new GenerationSummary
        {
            ItemsGenerated = 1,
            VariationsCreated = 3,
            Items = new[] { item },
        };
        var result = new ExportResult
        {
            Success = true,
            Mode = ExportMode.AIGeneratedThenDeterministic,
            Target = ExportTarget.CSharp,
            GenerationSummary = summary,
            RuntimeRequirements = new[] { "Ashlar.Runtime" },
            Messages = new[] { "ok" },
        };
        result.GenerationSummary!.Items[0].BrickId.Should().Be("b1");
        result.Messages.Should().ContainSingle();
    }

    [Fact]
    public void Execution_events_expose_expected_metadata()
    {
        new BehaviorStartedEvent("b1", "Behavior", DateTime.UtcNow).Type.Should().Be("behavior_started");
        new BehaviorCompletedEvent("b1", true, new Dictionary<string, object>()).Success.Should().BeTrue();
        new BehaviorCancelledEvent("b1").BehaviorId.Should().Be("b1");
        new StepStartedEvent("s1", "brick", "DomainBrick", ImplementationType.Deterministic, false, 0, 1)
            .StepIndex.Should().Be(0);
        new StepCompletedEvent("s1", "brick", ImplementationType.Agentic, 12, "done").LatencyMs.Should().Be(12);
        new StepSkippedEvent("s1", "condition false").Reason.Should().Contain("condition");
        new StepErrorEvent("s1", "boom", 5).LatencyMs.Should().Be(5);
        var cause = new InvalidOperationException("refused");
        var refused = new StepErrorEvent(
            "s1",
            "EGRESS_REFUSED",
            latencyMs: 9,
            cause: cause,
            refusalErrorCode: "EGRESS_REFUSED",
            refusalRef: "ref-1");
        refused.Cause.Should().BeSameAs(cause);
        refused.RefusalErrorCode.Should().Be("EGRESS_REFUSED");
        refused.RefusalRef.Should().Be("ref-1");
        refused.WasRefused.Should().BeTrue();
        new StepErrorEvent("s2", "plain").WasRefused.Should().BeFalse();
        new CacheHitEvent("s1", "key").CacheKey.Should().Be("key");
        new ProviderSwitchedEvent("openai", "ollama").ToProvider.Should().Be("ollama");
        new OfflineModeActivatedEvent().Type.Should().Be("offline_mode_activated");
    }

    [Fact]
    public void WorkflowDefinition_nodes_and_connections_round_trip()
    {
        var cluster = new ClusterNode
        {
            Name = "Combat",
            ClusterId = "combat-cluster",
            Mode = ImplementationMode.AgenticPreferred,
            Parameters = new Dictionary<string, object> { ["difficulty"] = "hard" },
        };
        cluster.ClusterId.Should().Be("combat-cluster");

        var input = new InputNode
        {
            Name = "Level Data",
            Type = InputType.Content,
            Content = "{}",
            WebhookUrl = "https://example.com/hook",
            FilePath = "/in/data.json",
            DatabaseConnectionString = "Host=localhost",
            Query = "select 1",
        };
        input.Type.Should().Be(InputType.Content);
        input.FilePath.Should().Be("/in/data.json");

        var output = new OutputNode
        {
            Name = "Results",
            Type = OutputType.File,
            Format = WorkflowOutputFormat.Json,
            FilePath = "/out/result.json",
        };
        output.Format.Should().Be(WorkflowOutputFormat.Json);

        var transform = new TransformNode { Name = "Map", Operation = TransformOperation.Filter, Expression = "x > 0" };
        transform.Operation.Should().Be(TransformOperation.Filter);

        var conditional = new ConditionalNode { Name = "Branch", Condition = "score > 10" };
        conditional.Condition.Should().Be("score > 10");

        var definition = new WorkflowDefinition
        {
            Name = "Visual Flow",
            Nodes = new WorkflowNode[] { cluster, input, output, transform, conditional },
            Connections = new[]
            {
                new VisualWorkflowConnection
                {
                    FromNodeId = input.Id,
                    FromPortId = "out",
                    ToNodeId = cluster.Id,
                    ToPortId = "in",
                    Type = ConnectionType.Data,
                },
            },
            Metadata = new WorkflowMetadata { Zoom = 1.5, ViewportCenter = new Ashlar.Core.Domain.Workflows.Position(10, 20) },
        };
        definition.Nodes.Should().HaveCount(5);
        definition.Connections.Should().ContainSingle();
        definition.Metadata.Zoom.Should().Be(1.5);
    }

    [Fact]
    public void WorkflowDefinition_agent_and_brick_nodes_round_trip()
    {
        var agent = new AgentNode
        {
            Name = "Planner",
            AgentId = "planner-1",
            Mode = ImplementationMode.Mixed,
            BehaviorOverrides = new Dictionary<string, ImplementationMode> { ["analyze"] = ImplementationMode.DeterministicOnly },
            BrickOverrides = new Dictionary<string, ImplementationType> { ["Echo"] = ImplementationType.Agentic },
            Parameters = new Dictionary<string, object> { ["depth"] = 2 },
        };
        agent.AgentId.Should().Be("planner-1");

        var brick = new BrickNode
        {
            Name = "Echo",
            BrickId = "echo",
            Implementation = ImplementationType.Deterministic,
            ProviderOverride = "local",
            Parameters = new Dictionary<string, object> { ["msg"] = "hi" },
        };
        brick.ProviderOverride.Should().Be("local");

        var port = new NodePort
        {
            Name = "In",
            Direction = PortDirection.Input,
            DataType = "string",
            Required = false,
            AllowMultiple = true,
        };
        port.AllowMultiple.Should().BeTrue();
    }

    [Fact]
    public void Cluster_port_and_parameter_optional_fields()
    {
        var port = new ClusterPort
        {
            Name = "in",
            Type = "int",
            Description = "input",
            Default = 1,
            InternalMapping = "brick.in",
        };
        port.Default.Should().Be(1);

        var parameter = new ClusterParameter
        {
            Name = "level",
            Type = "int",
            Validations = new[] { new ParameterValidation("min", "0", "too low") },
        };
        parameter.Validations.Should().ContainSingle();
    }

    /// <summary>Gets description.</summary>
    /// <param name="value">Value.</param>
    private static string GetDescription(object value) =>
        (string)value.GetType().GetProperty("Description")!.GetValue(value)!;

    private static (object FromName, object FromValue, object Sample, object Other) CreateValueObjectSamples(string typeName) =>
        typeName switch
        {
            /// <summary>Nameof.</summary>
            nameof(AIConfidenceLevel) => (
                AIConfidenceLevel.FromName("High"),
                AIConfidenceLevel.FromValue(3),
                AIConfidenceLevel.High,
                AIConfidenceLevel.Low),
            /// <summary>Nameof.</summary>
            nameof(AIEngineType) => (
                AIEngineType.FromName("GPT"),
                AIEngineType.FromValue(1),
                AIEngineType.GPT,
                AIEngineType.Claude),
            /// <summary>Nameof.</summary>
            nameof(AIProviderType) => (
                AIProviderType.FromName("OpenAI"),
                AIProviderType.FromValue(5),
                AIProviderType.OpenAI,
                AIProviderType.Ollama),
            /// <summary>Nameof.</summary>
            nameof(BetaProgramStatus) => (
                BetaProgramStatus.FromName("Active"),
                BetaProgramStatus.FromValue(1),
                BetaProgramStatus.Active,
                BetaProgramStatus.Pending),
            /// <summary>Nameof.</summary>
            nameof(HealthStatus) => (
                HealthStatus.FromName("Good"),
                HealthStatus.FromValue(3),
                HealthStatus.Good,
                HealthStatus.Critical),
            /// <summary>Nameof.</summary>
            nameof(MethodVisibility) => (
                MethodVisibility.FromName("Public"),
                MethodVisibility.FromValue(1),
                MethodVisibility.Public,
                MethodVisibility.Private),
            /// <summary>Nameof.</summary>
            nameof(OnboardingStatus) => (
                OnboardingStatus.FromName("Completed"),
                OnboardingStatus.FromValue(2),
                OnboardingStatus.Completed,
                OnboardingStatus.Pending),
            /// <summary>Nameof.</summary>
            nameof(ProjectStatus) => (
                ProjectStatus.FromName("Active"),
                ProjectStatus.FromValue(3),
                ProjectStatus.Active,
                ProjectStatus.Completed),
            /// <summary>Nameof.</summary>
            nameof(SprintStatus) => (
                SprintStatus.FromName("Active"),
                SprintStatus.FromValue(1),
                SprintStatus.Active,
                SprintStatus.Cancelled),
            /// <summary>Nameof.</summary>
            nameof(TaskPriority) => (
                TaskPriority.FromName("High"),
                TaskPriority.FromValue(2),
                TaskPriority.High,
                TaskPriority.Low),
            "DomainTaskStatus" => (
                Ashlar.Core.Domain.Values.TaskStatus.FromName("Done"),
                Ashlar.Core.Domain.Values.TaskStatus.FromValue(2),
                Ashlar.Core.Domain.Values.TaskStatus.Done,
                Ashlar.Core.Domain.Values.TaskStatus.Todo),
            /// <summary>Nameof.</summary>
            nameof(RiskLevel) => (
                RiskLevel.FromName("High"),
                RiskLevel.FromValue(2),
                RiskLevel.High,
                RiskLevel.Low),
            _ => throw new ArgumentOutOfRangeException(nameof(typeName), typeName, null),
        };

    /// <summary>Tests for test brick.</summary>
    private sealed class TestBrick : DomainBrick
    {
        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input,
            ImplementationType implementation,
            IExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrickOutput());
    }
}
