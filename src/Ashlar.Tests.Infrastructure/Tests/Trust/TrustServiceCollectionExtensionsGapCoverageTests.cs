using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Core.Application.Trust.Models;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Infrastructure.Trust.Sdk.Extensions;
using Ashlar.Infrastructure.Trust;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Trust;

/// <summary>Tests for trust service collection extensions gap coverage.</summary>
[Collection("EnvironmentVariables")]
public sealed class TrustServiceCollectionExtensionsGapCoverageTests
{
    [Fact]
    public void AddUserKnowledgeLog_uses_in_memory_store_when_path_is_blank()
    {
        var services = new ServiceCollection();
        services.AddUserKnowledgeLog(null);
        services.AddUserKnowledgeLog("   ");

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IUserKnowledgeLogStore>()
            .Should()
            .AllBeOfType<InMemoryUserKnowledgeLogStore>()
            .And.HaveCount(1);
    }

    [Fact]
    public void AddUserKnowledgeLog_uses_lite_db_when_path_is_provided()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-ukl-" + Guid.NewGuid() + ".db");
        var services = new ServiceCollection();
        services.AddUserKnowledgeLog(path);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IUserKnowledgeLogStore>().Should().BeOfType<LiteDbUserKnowledgeLogStore>();

        if (File.Exists(path))
            File.Delete(path);
    }

    [Fact]
    public void AddAccessBoundary_registers_observation_gate_and_applies_active_pack()
    {
        var pack = new TrustPolicyPack(
            "active",
            "1.0.0",
            "Active",
            "desc",
            PauseObservationByDefault: true,
            new Dictionary<string, bool> { ["shell"] = false },
            new Dictionary<string, bool>(),
            Array.Empty<TrustPolicyPackProjectRule>());

        var registry = new Mock<ITrustPolicyPackRegistry>();
        registry.Setup(r => r.GetActivePack()).Returns(new ActiveTrustPolicyPack("active", "1.0.0", DateTimeOffset.UtcNow));
        registry.Setup(r => r.GetById("active")).Returns(pack);

        var services = new ServiceCollection();
        services.AddSingleton(registry.Object);
        services.AddAccessBoundary();

        using var provider = services.BuildServiceProvider();
        var boundary = provider.GetRequiredService<IAccessBoundary>();
        var gate = provider.GetRequiredService<IObservationGate>();

        boundary.IsObservationPaused.Should().BeTrue();
        boundary.IsCategoryAllowed("shell").Should().BeFalse();
        gate.Should().NotBeNull();
    }

    [Fact]
    public void AddAccessBoundary_wires_boundary_changed_to_audit_log_when_registered()
    {
        var audit = new Mock<IDataDecisionAuditLog>();
        var services = new ServiceCollection();
        services.AddSingleton(audit.Object);
        services.AddAccessBoundary();

        using var provider = services.BuildServiceProvider();
        var boundary = provider.GetRequiredService<IAccessBoundary>();
        boundary.SetPause(true);

        audit.Verify(
            l => l.LogBoundaryChange(It.Is<BoundaryChangeEvent>(e => e.ChangeType == "pause")),
            Times.Once);
    }

    [Fact]
    public void AddCloudAvailabilityResolver_registers_resolver_with_logger()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCloudAvailabilityResolver(configPath: "/tmp/ashlar-config.json", enableNetworkProbe: true);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICloudAvailabilityResolver>().Should().BeOfType<CloudAvailabilityResolver>();
    }

    [Fact]
    public void AddCloudAvailabilityResolver_uses_ashlar_config_path_env_when_config_path_null()
    {
        var key = "ASHLAR_CONFIG_PATH";
        var previous = Environment.GetEnvironmentVariable(key);
        var customPath = Path.Combine(Path.GetTempPath(), "ashlar-resolver-config-" + Guid.NewGuid() + ".json");
        Environment.SetEnvironmentVariable(key, customPath);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCloudAvailabilityResolver();

            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<ICloudAvailabilityResolver>().Should().BeOfType<CloudAvailabilityResolver>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }
}
