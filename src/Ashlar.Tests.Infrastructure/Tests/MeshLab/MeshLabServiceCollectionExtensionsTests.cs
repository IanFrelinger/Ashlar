using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Infrastructure.MeshLab;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.MeshLab;

/// <summary>Tests for mesh lab service collection extensions.</summary>
public sealed class MeshLabServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAshlarMeshLabWorkerExecutor_skips_registration_when_disabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ashlar:MeshLab:WorkerExecutor:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddAshlarMeshLabWorkerExecutor(configuration);

        services.Should().NotContain(d => d.ServiceType == typeof(MeshLabWorkerExecutorClient));
        services.Should().NotContain(d => d.ServiceType == typeof(MeshLabWorkerExecutorBackgroundService));
        // SPEC-007: the egress guard is installed after the early return, so the disabled path registers none of it.
        services.Should().NotContain(d => d.ServiceType == typeof(IEgressGuard));
    }

    [Fact]
    public void AddAshlarMeshLabWorkerExecutor_registers_executor_when_enabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ashlar:MeshLab:WorkerExecutor:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlarMeshLabWorkerExecutor(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(MeshLabWorkerExecutorClient));
        services.Should().Contain(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(MeshLabWorkerExecutorBackgroundService));
    }

    [Fact]
    public void MeshLabWorkerExecutorOptions_exposes_section_path_and_defaults()
    {
        var options = new MeshLabWorkerExecutorOptions
        {
            Enabled = true,
            DirectorBaseUrl = "http://director:8080",
            ApiKey = "key",
            TaskNamePrefix = "lab",
            PollIntervalMs = 250,
            ExecuteBrickOnAssignedPeer = false,
            ResultSummary = "done",
        };

        MeshLabWorkerExecutorOptions.SectionPath.Should().Be("Ashlar:MeshLab:WorkerExecutor");
        options.DirectorBaseUrl.Should().Be("http://director:8080");
        options.TaskNamePrefix.Should().Be("lab");
        options.PollIntervalMs.Should().Be(250);
        options.ExecuteBrickOnAssignedPeer.Should().BeFalse();
        options.ResultSummary.Should().Be("done");
    }
}
