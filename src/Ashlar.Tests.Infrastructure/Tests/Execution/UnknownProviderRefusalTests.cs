using FluentAssertions;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.Models;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>
/// The defect: <c>ashlar self-extend run --provider does-not-exist</c> reported "ok / passed QA
/// gates". A provider name nothing can route to survived to request time, the routed call threw,
/// and the catch treated that as "provider unreachable" — so under ASHLAR_ALLOW_MOCK=1 the echo
/// model answered and the cycle reported success over a model that was never called.
///
/// <para>The distinction these pin: an UNREACHABLE provider may degrade to the echo model, because
/// that is a condition to fall back from. A name that is not a provider may not, because no
/// fallback rescues a typo — it only hides it.</para>
/// </summary>
public sealed class UnknownProviderRefusalTests
{
    [Fact]
    public async Task An_unknown_provider_name_is_refused_even_with_mock_allowed()
    {
        await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
        {
            var model = CreateModel();
            var input = new ModelInput(new List<(string role, string content)>
            {
                ("system", "ashlar.model.provider=does-not-exist"),
                ("user", "ping"),
            });

            var act = async () => await model.CompleteAsync(input, CancellationToken.None);

            var ex = await act.Should().ThrowAsync<ModelUnavailableException>();
            ex.Which.Message.Should().Contain("is not a model provider this build knows");
            ex.Which.Message.Should().Contain("mock", "the refusal names the offline provider to use instead");
        });
    }

    [Fact]
    public async Task The_deterministic_preference_does_not_launder_an_unknown_name_either()
    {
        // The deterministic branch also routes to the provider, so the check has to sit ahead of
        // both branches — otherwise one word in a system directive reopens the hole.
        await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
        {
            var model = CreateModel();
            var input = new ModelInput(new List<(string role, string content)>
            {
                ("system", "ashlar.model.prefer=deterministic\nashlar.model.provider=does-not-exist"),
                ("user", "ping"),
            });

            await model.Invoking(m => m.CompleteAsync(input, CancellationToken.None))
                .Should().ThrowAsync<ModelUnavailableException>();
        });
    }

    [Fact]
    public async Task A_known_offline_provider_still_works()
    {
        // The refusal must not become a wall in front of the zero-setup path.
        await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
        {
            var model = CreateModel();
            var input = new ModelInput(new List<(string role, string content)>
            {
                ("system", "ashlar.model.provider=offline"),
                ("user", "ping"),
            });

            var output = await model.CompleteAsync(input, CancellationToken.None);

            output.Should().NotBeNull();
        });
    }

    [Fact]
    public void The_catalogue_answers_known_separately_from_available()
    {
        ProviderFactory.IsKnownProvider("ollama").Should().BeTrue();
        ProviderFactory.IsKnownProvider("OLLAMA").Should().BeTrue("provider names are not case-sensitive");
        ProviderFactory.IsKnownProvider(" mock ").Should().BeTrue();
        ProviderFactory.IsKnownProvider("does-not-exist").Should().BeFalse();
        ProviderFactory.IsKnownProvider(null).Should().BeFalse();
        ProviderFactory.KnownProviderList().Should().Contain("ollama").And.Contain("mock");
    }

    private static HotSwappableModel CreateModel()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var providerFactory = new ProviderFactory(loggerFactory.CreateLogger<ProviderFactory>());
        var providerBacked = new ProviderBackedModel(providerFactory, loggerFactory.CreateLogger<ProviderBackedModel>());
        return new HotSwappableModel(providerBacked, loggerFactory.CreateLogger<HotSwappableModel>());
    }

    private static async Task WithEnv(string key, string? value, Func<Task> action)
    {
        var prior = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
        try
        {
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, prior);
        }
    }
}
