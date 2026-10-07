using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace Ashlar.Mcp.Server.Tests;

public sealed class AshlarMcpServerServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlarMcpServer(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Registers_bridge_gate_snapshot_factory_and_toolbox_contributor()
    {
        using var provider = Build(Config());

        provider.GetRequiredService<AshlarMcpToolBridge>().Should().NotBeNull();
        provider.GetRequiredService<IMcpInvocationGate>().Should().BeOfType<PolicyMcpInvocationGate>();
        provider.GetRequiredService<IMcpWorldSnapshotFactory>().Should().BeOfType<OptionsMcpWorldSnapshotFactory>();
        provider.GetServices<IMcpToolContributor>().Should().ContainSingle(c => c is ToolboxMcpToolContributor);
    }

    [Fact]
    public void Binds_options_from_the_default_section()
    {
        using var provider = Build(Config(
            ($"{AshlarMcpServerOptions.SectionPath}:Enabled", "true"),
            ($"{AshlarMcpServerOptions.SectionPath}:ExposedToolIds:0", "repo.fs.read"),
            ($"{AshlarMcpServerOptions.SectionPath}:ServerName", "ashlar-test"),
            ($"{AshlarMcpServerOptions.SectionPath}:ArgumentOverrides:repo.fs.read:root", "X:/pinned")));

        var options = provider.GetRequiredService<IOptions<AshlarMcpServerOptions>>().Value;

        options.Enabled.Should().BeTrue();
        options.ExposedToolIds.Should().ContainSingle().Which.Should().Be("repo.fs.read");
        options.ServerName.Should().Be("ashlar-test");
        options.ArgumentOverrides["repo.fs.read"]["root"].Should().Be("X:/pinned");
    }

    [Fact]
    public void Server_info_reflects_configured_name()
    {
        using var provider = Build(Config(
            ($"{AshlarMcpServerOptions.SectionPath}:ServerName", "ashlar-test"),
            ($"{AshlarMcpServerOptions.SectionPath}:ServerVersion", "9.9.9")));

        var mcpOptions = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        mcpOptions.ServerInfo!.Name.Should().Be("ashlar-test");
        mcpOptions.ServerInfo.Version.Should().Be("9.9.9");
    }

    [Fact]
    public void Enabled_under_airgapped_profile_fails_options_validation()
    {
        Environment.SetEnvironmentVariable(ValidateAshlarMcpServerOptions.DeploymentProfileVariable, "airgapped");
        try
        {
            using var provider = Build(Config(($"{AshlarMcpServerOptions.SectionPath}:Enabled", "true")));

            var act = () => provider.GetRequiredService<IOptions<AshlarMcpServerOptions>>().Value;

            act.Should().Throw<OptionsValidationException>()
                .WithMessage("*AirGapped*", "protocol surfaces stay dark on air-gapped deployments");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidateAshlarMcpServerOptions.DeploymentProfileVariable, null);
        }
    }

    [Fact]
    public void Disabled_under_airgapped_profile_is_valid()
    {
        Environment.SetEnvironmentVariable(ValidateAshlarMcpServerOptions.DeploymentProfileVariable, "airgapped");
        try
        {
            using var provider = Build(Config());

            var options = provider.GetRequiredService<IOptions<AshlarMcpServerOptions>>().Value;

            options.Enabled.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidateAshlarMcpServerOptions.DeploymentProfileVariable, null);
        }
    }

    [Fact]
    public void Enabled_under_secure_workstation_profile_is_valid_for_local_ide()
    {
        Environment.SetEnvironmentVariable(ValidateAshlarMcpServerOptions.DeploymentProfileVariable, "secure-workstation");
        try
        {
            using var provider = Build(Config(
                ($"{AshlarMcpServerOptions.SectionPath}:Enabled", "true"),
                ($"{AshlarMcpServerOptions.SectionPath}:ServerName", "ashlar-ide")));

            var options = provider.GetRequiredService<IOptions<AshlarMcpServerOptions>>().Value;
            options.Enabled.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidateAshlarMcpServerOptions.DeploymentProfileVariable, null);
        }
    }

    [Fact]
    public void Invalid_concurrency_fails_validation_when_enabled()
    {
        using var provider = Build(Config(
            ($"{AshlarMcpServerOptions.SectionPath}:Enabled", "true"),
            ($"{AshlarMcpServerOptions.SectionPath}:MaxConcurrentToolCalls", "0")));

        var act = () => provider.GetRequiredService<IOptions<AshlarMcpServerOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*MaxConcurrentToolCalls*");
    }

    [Fact]
    public void Enabled_http_transport_under_secure_workstation_fails_options_validation()
    {
        Environment.SetEnvironmentVariable(ValidateAshlarMcpServerOptions.DeploymentProfileVariable, "secure-workstation");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAshlarMcpServer(Config(
                ($"{AshlarMcpServerOptions.SectionPath}:Enabled", "true"),
                ($"{AshlarMcpServerOptions.SectionPath}:ServerName", "ashlar-http")))
                .WithAshlarHttpTransport();
            using var provider = services.BuildServiceProvider();

            var act = () => provider.GetRequiredService<IOptions<AshlarMcpServerOptions>>().Value;

            act.Should().Throw<OptionsValidationException>()
                .WithMessage("*SecureWorkstation*")
                .WithMessage("*HTTP*");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidateAshlarMcpServerOptions.DeploymentProfileVariable, null);
        }
    }
}
