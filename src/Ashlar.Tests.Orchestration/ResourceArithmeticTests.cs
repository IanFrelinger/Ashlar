using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Orchestration.Coordination;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.Orchestration;

/// <summary>
/// Public allocator arithmetic regressions. Rollback, invalid-budget rejection, and zero-utilization
/// expectations are explicit integrator policies, not previously documented guarantees.
/// </summary>
public sealed class ResourceArithmeticTests
{
    [Theory(Timeout = 10000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Feasible_preemption_accounts_for_capacity_already_available(int dimension)
    {
        await Task.Yield();
        var allocator = Allocator(Budget(dimension, 100));
        allocator.TryAllocate(Container("low", Requirements(dimension, 60), 1), out _).Should().BeTrue();
        allocator.TryAllocate(Container("incoming", Requirements(dimension, 70), 2), out var incoming).Should().BeTrue();
        allocator.GetAllocations().Should().ContainSingle().Which.Should().BeSameAs(incoming);
        Total(allocator, dimension).Should().Be(70);
    }

    [Theory(Timeout = 10000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Rejected_request_preserves_existing_reservations(int dimension)
    {
        await Task.Yield();
        var allocator = Allocator(Budget(dimension, 100));
        allocator.TryAllocate(Container("low", Requirements(dimension, 40), 1), out var low).Should().BeTrue();
        allocator.TryAllocate(Container("high", Requirements(dimension, 50), 3), out var high).Should().BeTrue();
        allocator.TryAllocate(Container("medium", Requirements(dimension, 60), 2), out var rejected).Should().BeFalse();
        rejected.Should().BeNull();
        allocator.GetAllocations().Should().BeEquivalentTo(new[] { low, high });
        Total(allocator, dimension).Should().Be(90);
    }

    [Fact(Timeout = 10000)]
    public async Task Preemption_that_cannot_satisfy_all_dimensions_preserves_every_reservation()
    {
        await Task.Yield();
        var allocator = Allocator(new ResourceBudget { MaxComputeSeconds = 100, MaxContextTokens = 100, MaxMemoryMB = 100 });
        allocator.TryAllocate(Container("low", new ResourceRequirements { EstimatedComputeSeconds = 60, RequiredContextTokens = 10 }, 1), out var low).Should().BeTrue();
        allocator.TryAllocate(Container("high", new ResourceRequirements { RequiredContextTokens = 80 }, 3), out var high).Should().BeTrue();
        // Low can free sufficient compute but cannot free enough context; high is not preemptible.
        allocator.TryAllocate(Container("medium", new ResourceRequirements { EstimatedComputeSeconds = 70, RequiredContextTokens = 30 }, 2), out var rejected).Should().BeFalse();
        rejected.Should().BeNull();
        allocator.GetAllocations().Should().BeEquivalentTo(new[] { low, high });
        var usage = allocator.GetUsageReport();
        usage.TotalAllocatedComputeSeconds.Should().Be(60);
        usage.TotalAllocatedContextTokens.Should().Be(90);
        usage.TotalAllocatedMemoryMB.Should().Be(0);
    }

    [Theory(Timeout = 10000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Negative_request_is_rejected_without_fabricating_capacity(int dimension)
    {
        await Task.Yield();
        var allocator = Allocator(Budget(dimension, 100));
        bool? accepted = null;
        var error = Record.Exception(() => accepted = allocator.TryAllocate(Container("negative", Requirements(dimension, -10), 1), out _));
        (error is ArgumentException || (error is null && accepted == false)).Should().BeTrue("invalid requirements must reject without ledger mutation");
        allocator.GetAllocations().Should().BeEmpty();
        Total(allocator, dimension).Should().Be(0);
        allocator.TryAllocate(Container("oversized", Requirements(dimension, 110), 1), out _).Should().BeFalse();
        allocator.GetAllocations().Should().BeEmpty();
        Total(allocator, dimension).Should().Be(0);
    }

    [Theory(Timeout = 10000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Negative_budget_is_rejected_at_construction(int dimension)
    {
        await Task.Yield();
        var error = Record.Exception(() => Allocator(Budget(dimension, -1)));
        error.Should().BeAssignableTo<ArgumentException>();
    }

    [Fact(Timeout = 10000)]
    public async Task Null_maxima_preserve_uncapped_allocations_and_finite_reporting()
    {
        await Task.Yield();
        var allocator = Allocator(new ResourceBudget());
        allocator.TryAllocate(Container("large", new ResourceRequirements
        {
            EstimatedComputeSeconds = 1000000, RequiredContextTokens = 1000000, RequiredMemoryMB = 1000000
        }, 1), out _).Should().BeTrue();
        var usage = allocator.GetUsageReport();
        usage.MaxComputeSeconds.Should().BeNull();
        usage.MaxContextTokens.Should().BeNull();
        usage.MaxMemoryMB.Should().BeNull();
        usage.TotalAllocatedComputeSeconds.Should().Be(1000000);
        usage.TotalAllocatedContextTokens.Should().Be(1000000);
        usage.TotalAllocatedMemoryMB.Should().Be(1000000);
        new[] { usage.UtilizationCompute, usage.UtilizationContext, usage.UtilizationMemory }
            .Should().OnlyContain(value => double.IsFinite(value) && value == 0);
        allocator.ReleaseResources("large");
        allocator.GetAllocations().Should().BeEmpty();
    }

    [Fact(Timeout = 10000)]
    public async Task Zero_maxima_accept_zero_reject_positive_and_report_finite_zero()
    {
        await Task.Yield();
        var allocator = Allocator(new ResourceBudget { MaxComputeSeconds = 0, MaxContextTokens = 0, MaxMemoryMB = 0 });
        allocator.TryAllocate(Container("zero", new ResourceRequirements(), 1), out var zero).Should().BeTrue();
        for (var dimension = 0; dimension < 3; dimension++)
            allocator.TryAllocate(Container($"positive-{dimension}", Requirements(dimension, 1), 1), out _).Should().BeFalse();
        allocator.GetAllocations().Should().ContainSingle().Which.Should().BeSameAs(zero);
        var usage = allocator.GetUsageReport();
        new[] { usage.TotalAllocatedComputeSeconds, usage.TotalAllocatedContextTokens, usage.TotalAllocatedMemoryMB }
            .Should().OnlyContain(value => value == 0);
        new[] { usage.UtilizationCompute, usage.UtilizationContext, usage.UtilizationMemory }
            .Should().OnlyContain(value => double.IsFinite(value) && value == 0);
    }

    private static ResourceAllocator Allocator(ResourceBudget budget) => new(NullLogger<ResourceAllocator>.Instance, budget);
    private static ResourceBudget Budget(int dimension, int? value) => new()
    {
        MaxComputeSeconds = dimension == 0 ? value : null,
        MaxContextTokens = dimension == 1 ? value : null,
        MaxMemoryMB = dimension == 2 ? value : null
    };
    private static ResourceRequirements Requirements(int dimension, int value) => new()
    {
        EstimatedComputeSeconds = dimension == 0 ? value : null,
        RequiredContextTokens = dimension == 1 ? value : null,
        RequiredMemoryMB = dimension == 2 ? value : null
    };
    private static int Total(ResourceAllocator allocator, int dimension)
    {
        var report = allocator.GetUsageReport();
        return dimension switch { 0 => report.TotalAllocatedComputeSeconds, 1 => report.TotalAllocatedContextTokens, _ => report.TotalAllocatedMemoryMB };
    }
    private static AgentContainer Container(string id, ResourceRequirements requirements, int priority) => new(
        new GenericAgent(new AgentSpawnSpec
        {
            AgentId = id, Domain = "General", Goal = "resource arithmetic regression", Priority = priority,
            ResourceRequirements = requirements
        }, NullLogger<GenericAgent>.Instance), NullLogger<AgentContainer>.Instance);
}
