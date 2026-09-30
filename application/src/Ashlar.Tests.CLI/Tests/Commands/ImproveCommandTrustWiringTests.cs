using Ashlar.BackgroundAgents.Trust;
using Ashlar.CLI.Commands;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// With trust enabled, <c>ashlar improve</c> used to register CloudSanitizationProxy by type alone.
/// Every constructor dependency is optional, so DI built it with no content filter, and the proxy
/// passed every prompt through: "trust enabled" wrapped the provider in a sanitizer that sanitized
/// nothing. These resolve the composition the command actually builds.
/// </summary>
public sealed class ImproveCommandTrustWiringTests
{
    [Fact]
    public void With_trust_enabled_the_proxy_has_a_content_filter()
    {
        using var provider = Build(trustEnabled: true);
        var proxy = provider.GetRequiredService<ICloudSanitizationProxy>();

        // A clean prompt passes. With no filter the proxy now blocks EVERYTHING, so this is the
        // assertion that tells "a filter is wired" apart from "the proxy blocks by default".
        proxy.SanitizeForCloud(new OutgoingContext { SystemPrompt = "Analyze", UserPrompt = "What is the weather?" })
            .Allowed.Should().BeTrue("a proxy WITH a content filter lets a clean prompt through");

        // And a prompt with PII is blocked FOR the PII -- by the filter, not for want of one.
        var blocked = proxy.SanitizeForCloud(new OutgoingContext { SystemPrompt = "Analyze", UserPrompt = "mail someone@example.com" });
        blocked.Allowed.Should().BeFalse();
        blocked.BlockReason.Should().Contain("PII");
    }

    [Fact]
    public void With_trust_enabled_both_provider_ports_are_the_one_sanitizing_factory()
    {
        using var provider = Build(trustEnabled: true);

        var infra = provider.GetRequiredService<Ashlar.Infrastructure.Execution.IProviderFactory>();
        infra.Should().BeOfType<SanitizingProviderFactory>();
        provider.GetRequiredService<Ashlar.Core.Application.Execution.Ports.IProviderFactory>().Should().BeSameAs(infra);
    }

    // POSITIVE CONTROL for the wiring: with trust disabled nothing sanitizing is registered, so the
    // two tests above are about the trust branch and not about something every build registers.
    [Fact]
    public void With_trust_disabled_no_sanitizer_is_registered()
    {
        using var provider = Build(trustEnabled: false);

        provider.GetService<ICloudSanitizationProxy>().Should().BeNull();
        provider.GetRequiredService<Ashlar.Infrastructure.Execution.IProviderFactory>()
            .Should().BeOfType<Ashlar.Infrastructure.Execution.ProviderFactory>();
    }

    private static ServiceProvider Build(bool trustEnabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        ImproveCommand.RegisterProviderFactories(services, trustEnabled);
        return services.BuildServiceProvider();
    }
}
