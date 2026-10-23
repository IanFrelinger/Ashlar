using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect;
using Ashlar.Orchestration.Architect.Models;
using Ashlar.Orchestration.Coordination;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.Orchestration;

/// <summary>Ownership and cleanup guarantees for the shared resource budget.</summary>
public sealed class ResourceRunOwnershipTests
{
    [Fact(Timeout = 30000)]
    public async Task Throwing_run_releases_its_reservations_and_restores_root()
    {
        var allocator = Allocator(100);
        allocator.TryAllocate(Container("shared", 10, 9), out var root).Should().BeTrue();

        async Task ThrowFromRun()
        {
            using var scope = allocator.BeginRunScope();
            allocator.TryAllocate(Container("shared", 20, 1), out var scoped).Should().BeTrue();
            allocator.TryAllocate(Container("leftover", 30, 1), out _).Should().BeTrue();
            await Task.Yield();
            allocator.GetAllocation("shared").Should().BeSameAs(scoped);
            allocator.GetUsageReport().TotalAllocatedComputeSeconds.Should().Be(60);
            throw new InvalidOperationException("run interrupted after reserving resources");
        }

        Func<Task> run = () => ThrowFromRun().WaitAsync(TimeSpan.FromSeconds(10));
        await run.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("run interrupted after reserving resources");

        allocator.GetAllocation("shared").Should().BeSameAs(root);
        allocator.GetAllocation("leftover").Should().BeNull();
        allocator.GetAllocations().Should().ContainSingle().Which.Should().BeSameAs(root);
        var usage = allocator.GetUsageReport();
        usage.TotalAllocatedComputeSeconds.Should().Be(10);
        usage.TotalAllocatedContextTokens.Should().Be(20);
        usage.TotalAllocatedMemoryMB.Should().Be(30);
        allocator.ReleaseResources("shared");
        allocator.GetAllocations().Should().BeEmpty();
        allocator.GetUsageReport().TotalAllocatedComputeSeconds.Should().Be(0);
    }

    [Fact(Timeout = 30000)]
    public async Task Scoped_preemption_and_duplicate_requests_preserve_exact_ownership()
    {
        await Task.Yield();
        var allocator = Allocator(70);
        allocator.TryAllocate(Container("shared", 10, 9), out var root).Should().BeTrue();
        var outer = allocator.BeginRunScope();
        try
        {
            allocator.TryAllocate(Container("shared", 40, 1), out var low).Should().BeTrue();
            var inner = allocator.BeginRunScope();
            try
            {
                // Only the lower-priority outer reservation may be preempted; root is protected.
                allocator.TryAllocate(Container("shared", 40, 3), out var high).Should().BeTrue();
                allocator.GetAllocations().Should().HaveCount(2);
                allocator.GetAllocations().Should().NotContain(allocation => ReferenceEquals(allocation, low));
                allocator.GetAllocations().Should().Contain(allocation => ReferenceEquals(allocation, root));
                allocator.GetAllocation("shared").Should().BeSameAs(high);

                // Duplicate requests reuse the original reservation, even if their requirements differ.
                allocator.TryAllocate(Container("shared", 55, 3), out var duplicate).Should().BeTrue();
                duplicate.Should().BeSameAs(high);
                allocator.TryAllocate(Container("shared", null, 3), out var zeroDuplicate).Should().BeTrue();
                zeroDuplicate.Should().BeSameAs(high);
                var usage = allocator.GetUsageReport();
                usage.TotalAllocatedComputeSeconds.Should().Be(50);
                usage.TotalAllocatedContextTokens.Should().Be(100);
                usage.TotalAllocatedMemoryMB.Should().Be(150);
            }
            finally { inner.Dispose(); }

            inner.Dispose(); // Idempotent disposal must not disturb the restored outer context.
            allocator.GetAllocation("shared").Should().BeNull("the outer reservation was preempted");
            allocator.ReleaseResources("shared"); // Must not remove the surviving root reservation.
            allocator.GetAllocations().Should().ContainSingle().Which.Should().BeSameAs(root);
            allocator.GetUsageReport().TotalAllocatedComputeSeconds.Should().Be(10);
        }
        finally { outer.Dispose(); }

        outer.Dispose();
        allocator.GetAllocation("shared").Should().BeSameAs(root);
        allocator.GetUsageReport().TotalAllocatedComputeSeconds.Should().Be(10);
        allocator.ReleaseResources("shared");
        allocator.GetAllocations().Should().BeEmpty();
        var finalUsage = allocator.GetUsageReport();
        finalUsage.TotalAllocatedComputeSeconds.Should().Be(0);
        finalUsage.TotalAllocatedContextTokens.Should().Be(0);
        finalUsage.TotalAllocatedMemoryMB.Should().Be(0);
    }

    private static ResourceAllocator Allocator(int computeBudget) => new(
        NullLogger<ResourceAllocator>.Instance, new ResourceBudget { MaxComputeSeconds = computeBudget });

    private static AgentContainer Container(string id, int? amount, int priority) => new(
        new GenericAgent(new AgentSpawnSpec
        {
            AgentId = id,
            Domain = "General",
            Goal = "resource ownership regression",
            Priority = priority,
            ResourceRequirements = amount is null ? null : new ResourceRequirements
            {
                EstimatedComputeSeconds = amount.Value,
                RequiredContextTokens = amount.Value * 2,
                RequiredMemoryMB = amount.Value * 3
            }
        }, NullLogger<GenericAgent>.Instance), NullLogger<AgentContainer>.Instance);
}
