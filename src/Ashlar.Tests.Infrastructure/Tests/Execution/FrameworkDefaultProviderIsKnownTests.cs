using FluentAssertions;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.Core.Domain;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.Models;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>
/// The provider allow-list is the right idea — a name that is not a provider is a typo no fallback
/// rescues — but the first version of it left out the framework's OWN default.
///
/// <para><c>BackgroundAgentConfig.ModelProvider</c> defaults to <c>"deterministic"</c>,
/// <c>MeaiBackedModel</c> treats it as a first-class offline route, and
/// <c>BackgroundAgentRegistry</c> reads it as the no-LLM sentinel — and
/// <c>ProviderFactory.KnownProviders</c> did not contain it. A scaffold carrying the default got
/// "CERTIFIED" from <c>ashlar verify</c> and then "refusing to run: ... which this build does not
/// know", exit 65, on the same directory. That is not an operator mistake to refuse; it is the
/// framework disagreeing with itself, and a refusal is the worst possible way to express it.</para>
///
/// <para>So the default and the allow-list are now spelled from one constant, and these facts pin
/// that they stay that way.</para>
/// </summary>
public sealed class FrameworkDefaultProviderIsKnownTests
{
    [Fact]
    public void The_framework_default_provider_is_a_provider_the_build_knows()
    {
        ProviderFactory.IsKnownProvider(AshlarDefaults.DeterministicProviderName).Should().BeTrue(
            "a default the allow-list rejects certifies and then refuses to run on the same directory");
    }

    [Fact]
    public void The_background_agent_default_and_the_allow_list_come_from_the_same_constant()
    {
        new BackgroundAgentConfig().ModelProvider.Should().Be(AshlarDefaults.DeterministicProviderName);
        ProviderFactory.KnownProviders.Should().Contain(AshlarDefaults.DeterministicProviderName);
        ProviderFactory.KnownProviderList().Should().Contain(AshlarDefaults.DeterministicProviderName,
            "the refusal lists what it accepts, so the default has to appear there too");
    }

    [Fact]
    public async Task A_node_configured_with_the_framework_default_runs_instead_of_refusing()
    {
        // The reproduction, at the layer that threw: a daemon-configured extender whose
        // ModelProvider was never touched by anyone.
        await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
        {
            var loggerFactory = LoggerFactory.Create(_ => { });
            var providerFactory = new ProviderFactory(loggerFactory.CreateLogger<ProviderFactory>());
            var providerBacked = new ProviderBackedModel(providerFactory, loggerFactory.CreateLogger<ProviderBackedModel>());
            var model = new HotSwappableModel(providerBacked, loggerFactory.CreateLogger<HotSwappableModel>());

            var input = new ModelInput(new List<(string role, string content)>
            {
                ("system", $"ashlar.model.provider={new BackgroundAgentConfig().ModelProvider}"),
                ("user", "ping"),
            });

            var output = await model.CompleteAsync(input, CancellationToken.None);

            output.Should().NotBeNull();
        });
    }

    [Fact]
    public async Task A_name_that_is_genuinely_not_a_provider_is_still_refused()
    {
        // Widening the list by one must not widen it by anything else: the typo case is the whole
        // reason the allow-list exists.
        await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
        {
            var loggerFactory = LoggerFactory.Create(_ => { });
            var providerFactory = new ProviderFactory(loggerFactory.CreateLogger<ProviderFactory>());
            var providerBacked = new ProviderBackedModel(providerFactory, loggerFactory.CreateLogger<ProviderBackedModel>());
            var model = new HotSwappableModel(providerBacked, loggerFactory.CreateLogger<HotSwappableModel>());

            var input = new ModelInput(new List<(string role, string content)>
            {
                ("system", "ashlar.model.provider=determinstic"),
                ("user", "ping"),
            });

            await model.Invoking(m => m.CompleteAsync(input, CancellationToken.None))
                .Should().ThrowAsync<ModelUnavailableException>();
        });
    }

    private static async Task WithEnv(string key, string? value, Func<Task> action)
    {
        var previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
        try
        {
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }
}
