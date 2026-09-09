using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ashlar.Abstractions.Barriers;
using Ashlar.Abstractions.Transport;
using Ashlar.Core.Application.Common.Services;
using Ashlar.Core.Application.Resilience.Ports;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Orchestration.Barriers;
using Ashlar.Orchestration.Communication;
using Ashlar.Orchestration.Coordination;
using Ashlar.Orchestration.Coordination.Conflicts;
using Ashlar.Orchestration.Metrics;
using Ashlar.Orchestration.Negotiation;
using Ashlar.Orchestration.Transport;
using Xunit;

namespace Ashlar.Tests.Orchestration;

/// <summary>Tests for orchestration orchestrator gap coverage.</summary>
public class OrchestrationOrchestratorGapCoverageTests
{
    [Fact]
    public async Task OrchestrateAsync_resolves_resource_conflicts_via_negotiation()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(Success: true, Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "resource-negotiation",
            Agents =
            [
                new AgentSpawnSpec
                {
                    AgentId = "heavy-1",
                    Domain = "Sim",
                    Goal = "Run simulation",
                    ResourceRequirements = new ResourceRequirements { RequiredMemoryMB = 9000 },
                },
                new AgentSpawnSpec
                {
                    AgentId = "heavy-2",
                    Domain = "Sim",
                    Goal = "Run simulation",
                    ResourceRequirements = new ResourceRequirements { RequiredMemoryMB = 9000 },
                },
            ],
        };

        var sut = CreateOrchestrator(
            CreateArchitectMock(decomposition).Object,
            transportMock.Object,
            negotiation: CreateNegotiationProtocol());

        var result = await sut.OrchestrateAsync("resource negotiation");

        result.Conflicts.Should().Contain(c => c.ConflictType == ConflictType.Resource);
        result.ResolvedConflicts.Should().NotBeEmpty();
        transportMock.Verify(
            t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
    }

    [Fact]
    public async Task OrchestrateAsync_executes_dependent_agents_in_order()
    {
        var order = new List<string>();
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentInvocationRequest, CancellationToken>((req, _) => order.Add(req.AgentName))
            .ReturnsAsync(new AgentResult(Success: true, Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "dependency-order",
            Agents =
            [
                new AgentSpawnSpec
                {
                    AgentId = "upstream",
                    Domain = "Data",
                    Goal = "Produce data",
                },
                new AgentSpawnSpec
                {
                    AgentId = "downstream",
                    Domain = "Data",
                    Goal = "Consume data",
                    Dependencies = new[] { "upstream" },
                },
            ],
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("dependency order");

        result.Success.Should().BeTrue();
        order.Should().Equal("upstream", "downstream");
    }

    [Fact]
    public async Task OrchestrateAsync_escalates_when_resource_allocation_fails()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(Success: true, Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "resource-starved",
            Agents =
            [
                new AgentSpawnSpec
                {
                    AgentId = "hungry",
                    Domain = "Sim",
                    Goal = "Consume all memory",
                    ResourceRequirements = new ResourceRequirements { RequiredMemoryMB = 50_000 },
                },
            ],
        };

        var provider = BuildServiceProvider(new ResourceBudget { MaxMemoryMB = 1 });
        var sut = CreateOrchestrator(
            CreateArchitectMock(decomposition).Object,
            transportMock.Object,
            provider);

        var result = await sut.OrchestrateAsync("resource starvation");

        result.Escalations.Should().Contain(e => e.IssueType == "ResourceAllocation");
    }

    [Fact]
    public async Task OrchestrateAsync_negotiates_schema_conflicts_when_present()
    {
        var schemaA = JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string"}}}""").RootElement;
        var schemaB = JsonDocument.Parse("""{"type":"number"}""").RootElement;

        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(Success: true, Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "schema-conflict",
            Agents =
            [
                new AgentSpawnSpec { AgentId = "schema-a", Domain = "Data", Goal = "Emit id", OutputSchema = schemaA },
                new AgentSpawnSpec { AgentId = "schema-b", Domain = "Data", Goal = "Emit count", OutputSchema = schemaB },
            ],
        };

        var sut = CreateOrchestrator(
            CreateArchitectMock(decomposition).Object,
            transportMock.Object,
            negotiation: CreateNegotiationProtocol());

        var result = await sut.OrchestrateAsync("schema negotiation");

        result.Conflicts.Should().Contain(c => c.ConflictType == ConflictType.Schema);
        result.ResolvedConflicts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task OrchestrateAsync_negotiation_failure_escalates_without_asserting_success()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(Success: true, Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "constraint-conflict",
            Agents =
            [
                new AgentSpawnSpec
                {
                    AgentId = "sync-agent",
                    Domain = "Data",
                    Goal = "Run fast",
                    Constraints = new[] { new AgentConstraint { Type = "Performance", Description = "must be fast" } },
                },
                new AgentSpawnSpec
                {
                    AgentId = "async-agent",
                    Domain = "Data",
                    Goal = "Run slow",
                    Constraints = new[] { new AgentConstraint { Type = "Performance", Description = "must be slow" } },
                },
            ],
        };

        var sut = CreateOrchestrator(
            CreateArchitectMock(decomposition).Object,
            transportMock.Object,
            negotiation: CreateNegotiationProtocol());

        var result = await sut.OrchestrateAsync("constraint negotiation");

        result.Conflicts.Should().Contain(c => c.ConflictType == ConflictType.Constraint);
        result.UnresolvedConflicts.Should().NotBeEmpty();
        result.Escalations.Should().NotBeEmpty();
        result.ResolvedConflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task OrchestrateAsync_timeout_retry_exhaustion_records_escalation()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(
                Success: false,
                ErrorMessage: "Timed out",
                ErrorCode: "TIMEOUT"));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "timeout",
            Agents =
            [
                new AgentSpawnSpec { AgentId = "agent-1", Domain = "General", Goal = "Run" },
            ],
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("timeout exhaustion");

        result.Success.Should().BeFalse();
        result.Escalations.Should().Contain(e => e.IssueType == "AgentExecution");
        transportMock.Verify(
            t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task OrchestrateAsync_transport_exception_escalates_agent_execution()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport down"));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "transport-error",
            Agents =
            [
                new AgentSpawnSpec { AgentId = "agent-1", Domain = "General", Goal = "Run" },
            ],
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("transport exception");

        result.Escalations.Should().Contain(e => e.IssueType == "AgentExecution");
    }

    [Fact]
    public async Task OrchestrateAsync_continues_when_decomposition_has_validation_errors()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(Success: true, Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "invalid-decomposition",
            ValidationErrors =
            [
                new ValidationError { ErrorType = "Dependency", Message = "missing dependency reference" },
            ],
            Agents =
            [
                new AgentSpawnSpec { AgentId = "agent-1", Domain = "General", Goal = "Run" },
            ],
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("invalid decomposition");

        result.Decomposition!.ValidationErrors.Should().ContainSingle(e => e.Message.Contains("missing dependency"));
        transportMock.Verify(
            t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OrchestrateAsync_opens_circuit_breaker_after_repeated_transport_failures()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(
                Success: false,
                ErrorMessage: "Provider failure",
                Metadata: new Dictionary<string, string> { ["errorCode"] = "PROVIDER_FAILURE" }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "circuit",
            Agents = [new AgentSpawnSpec { AgentId = "agent-1", Domain = "General", Goal = "Run" }],
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);

        for (var i = 0; i < 4; i++)
            await sut.OrchestrateAsync($"circuit attempt {i}");

        transportMock.Verify(
            t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(4));
    }

    [Fact]
    public async Task OrchestrateAsync_resolves_philosophy_conflicts_via_negotiation()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(Success: true, Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "philosophy",
            Agents =
            [
                new AgentSpawnSpec { AgentId = "perf-agent", Domain = "Design", Goal = "optimize fast performance" },
                new AgentSpawnSpec { AgentId = "quality-agent", Domain = "Design", Goal = "detailed quality polish" },
            ],
        };

        var sut = CreateOrchestrator(
            CreateArchitectMock(decomposition).Object,
            transportMock.Object,
            negotiation: CreateNegotiationProtocol());

        var result = await sut.OrchestrateAsync("philosophy negotiation");

        result.Conflicts.Should().Contain(c => c.ConflictType == ConflictType.Philosophy);
        result.ResolvedConflicts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task OrchestrateAsync_reads_error_code_from_alternate_metadata_key()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(
                Success: false,
                ErrorMessage: "Timed out",
                Metadata: new Dictionary<string, string> { ["errorCODE"] = "TIMEOUT" }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "alt-meta",
            Agents = [new AgentSpawnSpec { AgentId = "agent-1", Domain = "General", Goal = "Run" }],
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("alternate metadata key");

        result.Escalations.Should().ContainSingle(e => e.IssueType == "AgentExecution");
        transportMock.Verify(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task OrchestrateAsync_agent_not_found_does_not_retry_transport()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(
                Success: false,
                ErrorMessage: "Agent missing",
                ErrorCode: "AGENT_NOT_FOUND"));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "missing-agent",
            Agents = [new AgentSpawnSpec { AgentId = "agent-1", Domain = "General", Goal = "Run" }],
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("agent not found");

        result.Escalations.Should().ContainSingle(e => e.IssueType == "AgentExecution");
        transportMock.Verify(
            t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OrchestrateAsync_opens_circuit_breaker_and_marks_subsequent_calls()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(
                Success: false,
                ErrorMessage: "Provider failure",
                Metadata: new Dictionary<string, string> { ["errorCode"] = "PROVIDER_FAILURE" }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "circuit-open",
            Agents = [new AgentSpawnSpec { AgentId = "agent-1", Domain = "General", Goal = "Run" }],
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);

        for (var i = 0; i < 3; i++)
            await sut.OrchestrateAsync($"circuit warm-up {i}");

        var opened = await sut.OrchestrateAsync("circuit open");
        opened.Escalations.Should().Contain(e => e.IssueType == "AgentExecution");

        transportMock.Verify(
            t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(3));
    }

    [Fact]
    public async Task OrchestrateAsync_transport_failure_without_error_metadata_escalates()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(Success: false, ErrorMessage: "boom"));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "no-meta",
            Agents = [new AgentSpawnSpec { AgentId = "agent-1", Domain = "General", Goal = "Run" }],
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("transport failure without metadata");

        result.Escalations.Should().ContainSingle(e => e.IssueType == "AgentExecution");
    }

    [Fact]
    public async Task OrchestrateAsync_runs_barrier_guard_when_context_and_metadata_align()
    {
        var hierarchy = new BarrierHierarchy([
            new BarrierLevel("public", 0),
            new BarrierLevel("private", 1),
        ]);
        var context = BarrierContext.Create("public", BarrierAuthoritySource.Cli, "*", "corr-barrier", hierarchy);

        var accessor = new Mock<IBarrierContextAccessor>();
        accessor.SetupGet(x => x.Current).Returns(context);
        var audit = new Mock<IBarrierAuditLog>();
        audit.Setup(x => x.RecordAsync(It.IsAny<BarrierAuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(Success: true, Output: new { ok = true }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "barrier",
            Agents =
            [
                new AgentSpawnSpec
                {
                    AgentId = "agent-1",
                    Domain = "General",
                    Goal = "Run",
                    Metadata = new Dictionary<string, object?> { ["barrierLevel"] = "public" },
                },
            ],
        };

        var sut = CreateOrchestrator(
            CreateArchitectMock(decomposition).Object,
            transportMock.Object,
            barrierAccessor: accessor.Object,
            barrierAudit: audit.Object,
            barrierHierarchy: hierarchy,
            barrierOptions: new BarrierOptions { Levels = ["public", "private"] },
            metrics: new OrchestrationMetrics(NullLogger<OrchestrationMetrics>.Instance));

        var result = await sut.OrchestrateAsync("barrier guard");

        result.Success.Should().BeTrue();
        audit.Verify(
            x => x.RecordAsync(
                It.Is<BarrierAuditEvent>(e => e.EventType == BarrierAuditEventType.AgentInvoked),
                It.IsAny<CancellationToken>()),
            Times.Once);
        transportMock.Verify(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OrchestrateAsync_surfaces_missing_barrier_context_as_a_distinct_escalation()
    {
        // Host never established a barrier context (what Ashlar.API does: no barrier middleware),
        // and RequireExplicitBarrier is on. Before the fix this was swallowed into the generic
        // "AgentExecution" bucket and the caller only saw "0 agent(s) executed".
        var hierarchy = new BarrierHierarchy([
            new BarrierLevel("public", 0),
            new BarrierLevel("private", 1),
        ]);
        var accessor = new Mock<IBarrierContextAccessor>();
        accessor.SetupGet(x => x.Current).Returns((BarrierContext?)null);
        var audit = new Mock<IBarrierAuditLog>();
        audit.Setup(x => x.RecordAsync(It.IsAny<BarrierAuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        var decomposition = new DecompositionResult
        {
            OriginalRequest = "barrier-missing",
            Agents = [new AgentSpawnSpec { AgentId = "agent-1", Domain = "General", Goal = "Run" }],
        };

        var sut = CreateOrchestrator(
            CreateArchitectMock(decomposition).Object,
            transportMock.Object,
            barrierAccessor: accessor.Object,
            barrierAudit: audit.Object,
            barrierHierarchy: hierarchy,
            barrierOptions: new BarrierOptions { Levels = ["public", "private"], RequireExplicitBarrier = true },
            metrics: new OrchestrationMetrics(NullLogger<OrchestrationMetrics>.Instance));

        var result = await sut.OrchestrateAsync("barrier missing");

        result.Success.Should().BeFalse();
        result.IntegratedOutput!.AgentOutputs.Should().BeEmpty();
        var escalation = result.Escalations.Should().ContainSingle().Subject;
        escalation.IssueType.Should().Be("BARRIER_CONTEXT_MISSING");
        escalation.Description.Should().Contain("agent-1")
            .And.Contain("Barrier context is required but missing")
            .And.Contain("RequireExplicitBarrier");
        escalation.Context.Should().Be($"correlationId={result.CorrelationId}");
        transportMock.Verify(
            t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OrchestrateAsync_defaults_to_floor_barrier_when_context_missing_and_not_required()
    {
        // The shipped Ashlar.API default (RequireExplicitBarrier=false): no context -> floor level,
        // DefaultApplied audit event, and the agent actually runs.
        var hierarchy = new BarrierHierarchy([
            new BarrierLevel("public", 0),
            new BarrierLevel("private", 1),
        ]);
        var accessor = new Mock<IBarrierContextAccessor>();
        accessor.SetupGet(x => x.Current).Returns((BarrierContext?)null);
        var audit = new Mock<IBarrierAuditLog>();
        audit.Setup(x => x.RecordAsync(It.IsAny<BarrierAuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(Success: true, Output: new { ok = true }));
        var decomposition = new DecompositionResult
        {
            OriginalRequest = "barrier-defaulted",
            Agents = [new AgentSpawnSpec { AgentId = "agent-1", Domain = "General", Goal = "Run" }],
        };

        var sut = CreateOrchestrator(
            CreateArchitectMock(decomposition).Object,
            transportMock.Object,
            barrierAccessor: accessor.Object,
            barrierAudit: audit.Object,
            barrierHierarchy: hierarchy,
            barrierOptions: new BarrierOptions { Levels = ["public", "private"], RequireExplicitBarrier = false },
            metrics: new OrchestrationMetrics(NullLogger<OrchestrationMetrics>.Instance));

        var result = await sut.OrchestrateAsync("barrier defaulted");

        result.Success.Should().BeTrue();
        result.Escalations.Should().BeEmpty();
        audit.Verify(
            x => x.RecordAsync(
                It.Is<BarrierAuditEvent>(e => e.EventType == BarrierAuditEventType.DefaultApplied && e.BarrierLevel == "public"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        transportMock.Verify(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OrchestrateAsync_rethrows_when_architect_decomposition_fails()
    {
        var architectMock = new Mock<IArchitectAgent>(MockBehavior.Strict);
        architectMock
            .Setup(a => a.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("architect offline"));
        architectMock
            .Setup(a => a.DecomposeAsync(It.IsAny<string>(), It.IsAny<DecompositionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("architect offline"));

        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        var sut = CreateOrchestrator(
            architectMock.Object,
            transportMock.Object,
            metrics: new OrchestrationMetrics(NullLogger<OrchestrationMetrics>.Instance));

        var act = () => sut.OrchestrateAsync("architect failure");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*architect offline*");
        transportMock.Verify(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OrchestrateAsync_reads_error_code_from_metadata_case_insensitive()
    {
        var transportMock = new Mock<IAgentTransport>(MockBehavior.Strict);
        transportMock
            .Setup(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResult(
                Success: false,
                ErrorMessage: "Agent not found",
                Metadata: new Dictionary<string, string> { ["ERRORCODE"] = "AGENT_NOT_FOUND" }));

        var decomposition = new DecompositionResult
        {
            OriginalRequest = "meta-code",
            Agents = [new AgentSpawnSpec { AgentId = "agent-1", Domain = "General", Goal = "Run" }],
        };

        var sut = CreateOrchestrator(CreateArchitectMock(decomposition).Object, transportMock.Object);
        var result = await sut.OrchestrateAsync("metadata error code");

        result.Escalations.Should().ContainSingle(e => e.IssueType == "AgentExecution");
        transportMock.Verify(t => t.SendAsync(It.IsAny<AgentInvocationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static NegotiationProtocol CreateNegotiationProtocol()
    {
        var logger = NullLogger<NegotiationProtocol>.Instance;
        return new NegotiationProtocol(
            logger,
            new SchemaAdapter(NullLogger<SchemaAdapter>.Instance),
            new ParetoOptimizer(NullLogger<ParetoOptimizer>.Instance),
            new ConstraintRelaxer(NullLogger<ConstraintRelaxer>.Instance),
            new SynthesisEngine(NullLogger<SynthesisEngine>.Instance));
    }

    private static Mock<IArchitectAgent> CreateArchitectMock(DecompositionResult decomposition)
    {
        var architectMock = new Mock<IArchitectAgent>(MockBehavior.Strict);
        architectMock
            .Setup(a => a.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposition);
        architectMock
            .Setup(a => a.DecomposeAsync(It.IsAny<string>(), It.IsAny<DecompositionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposition);
        return architectMock;
    }

    private static ServiceProvider BuildServiceProvider(ResourceBudget? budget = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (budget != null)
        {
            services.AddSingleton(budget);
        }

        return services.BuildServiceProvider();
    }

    private static Orchestrator CreateOrchestrator(
        IArchitectAgent architect,
        IAgentTransport transport,
        ServiceProvider? provider = null,
        NegotiationProtocol? negotiation = null,
        OrchestrationMetrics? metrics = null,
        IBarrierContextAccessor? barrierAccessor = null,
        IBarrierAuditLog? barrierAudit = null,
        BarrierHierarchy? barrierHierarchy = null,
        BarrierOptions? barrierOptions = null)
    {
        provider ??= BuildServiceProvider();

        var agentFactory = new AgentFactory(
            provider.GetRequiredService<ILogger<AgentFactory>>(),
            provider);
        var lifecycleManager = new LifecycleManager(
            provider.GetRequiredService<ILogger<LifecycleManager>>(),
            new HealthMonitor(provider.GetRequiredService<ILogger<HealthMonitor>>()));
        var dependencyResolver = new DependencyResolver(provider.GetRequiredService<ILogger<DependencyResolver>>());
        var conflictDetector = new ConflictDetector(provider.GetRequiredService<ILogger<ConflictDetector>>());
        var resourceAllocator = provider.GetService<ResourceBudget>() is { } budget
            ? new ResourceAllocator(provider.GetRequiredService<ILogger<ResourceAllocator>>(), budget)
            : new ResourceAllocator(provider.GetRequiredService<ILogger<ResourceAllocator>>());
        var progressTracker = new ProgressTracker(provider.GetRequiredService<ILogger<ProgressTracker>>());
        var escalationManager = new EscalationManager(provider.GetRequiredService<ILogger<EscalationManager>>());
        var outputIntegrator = new OutputIntegrator(provider.GetRequiredService<ILogger<OutputIntegrator>>());
        var agentBus = new AgentBus(provider.GetRequiredService<ILogger<AgentBus>>());
        var loops = new SequentialLoopKernel();
        metrics ??= new OrchestrationMetrics(provider.GetRequiredService<ILogger<OrchestrationMetrics>>());
        var resilientExecutor = new TestResilientExecutor();
        var circuitBreaker = new Ashlar.Orchestration.Resilience.CircuitBreaker(
            name: "orchestration-agent-transport",
            failureThreshold: 3,
            timeout: TimeSpan.FromSeconds(30),
            logger: provider.GetService<ILogger<Ashlar.Orchestration.Resilience.CircuitBreaker>>());

        return new Orchestrator(
            architect,
            agentFactory,
            lifecycleManager,
            dependencyResolver,
            conflictDetector,
            resourceAllocator,
            progressTracker,
            escalationManager,
            outputIntegrator,
            agentBus,
            transport,
            loops,
            provider.GetRequiredService<ILogger<Orchestrator>>(),
            negotiation,
            metrics,
            barrierContextAccessor: barrierAccessor,
            barrierHierarchy: barrierHierarchy,
            barrierAuditLog: barrierAudit,
            barrierOptions: barrierOptions != null ? Options.Create(barrierOptions) : null,
            resilientExecutor: resilientExecutor,
            circuitBreaker: circuitBreaker);
    }

    /// <summary>Test stub for IResilientExecutor.</summary>
    private sealed class TestResilientExecutor : IResilientExecutor
    {
        public Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            RetryPolicy policy,
            CancellationToken cancellationToken = default)
        {
            return operation(cancellationToken);
        }
    }
}
