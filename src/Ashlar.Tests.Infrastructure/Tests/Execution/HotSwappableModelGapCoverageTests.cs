using FluentAssertions;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.Models;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>Tests for hot swappable model gap coverage.</summary>
[Collection("EnvironmentVariables")]
public sealed class HotSwappableModelGapCoverageTests
{
    [Fact]
    public async Task CompleteAsync_prefers_deterministic_echo_without_provider()
    {
        var model = CreateModel();
        var input = new ModelInput(new List<(string role, string content)>
        {
            ("system", "ashlar.model.prefer=deterministic"),
            ("user", "ping"),
        });

        var output = await model.CompleteAsync(input, CancellationToken.None);

        output.Text.Should().Be("ping");
    }

    [Fact]
    public async Task CompleteAsync_uses_system_provider_directive()
    {
        /// <summary>With env.</summary>
        /// <param name="(">(.</param>
        await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
        {
            var model = CreateModel();
            var input = new ModelInput(new List<(string role, string content)>
            {
                ("system", "ashlar.model.provider=offline"),
                ("user", "hello"),
            });

            var output = await model.CompleteAsync(input, CancellationToken.None);

            output.Text.Should().NotBeNullOrWhiteSpace();
        });
    }

    [Fact]
    public async Task CompleteAsync_uses_env_provider_when_no_directive()
    {
        /// <summary>With env.</summary>
        /// <param name="(">(.</param>
        await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
        {
            /// <summary>With env.</summary>
            /// <param name="(">(.</param>
            await WithEnv("ASHLAR_MODEL_PROVIDER", "offline", async () =>
            {
                var model = CreateModel();
                var input = new ModelInput(new List<(string role, string content)> { ("user", "hello") });

                var output = await model.CompleteAsync(input, CancellationToken.None);

                output.Text.Should().NotBeNullOrWhiteSpace();
            });
        });
    }

    [Fact]
    public async Task CompleteAsync_deterministic_with_provider_falls_back_to_echo_on_failure()
    {
        var model = CreateModel();
        var input = new ModelInput(new List<(string role, string content)>
        {
            ("system", "ashlar.model.prefer=deterministic\nashlar.model.provider=openai"),
            ("user", "fallback"),
        });

        /// <summary>With env.</summary>
        /// <param name="(">(.</param>
        await WithEnv("OPENAI_API_KEY", null, async () =>
        {
            var output = await model.CompleteAsync(input, CancellationToken.None);
            output.Text.Should().Be("fallback");
        });
    }

    [Fact]
    public async Task CompleteAsync_injects_provider_when_no_system_message_exists()
    {
        /// <summary>With env.</summary>
        /// <param name="(">(.</param>
        await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
        {
            /// <summary>With env.</summary>
            /// <param name="(">(.</param>
            await WithEnv("ASHLAR_MODEL_PROVIDER", "offline", async () =>
            {
                var model = CreateModel();
                var input = new ModelInput(new List<(string role, string content)> { ("user", "route-me") });

                var output = await model.CompleteAsync(input, CancellationToken.None);

                output.Text.Should().NotBeNullOrWhiteSpace();
            });
        });
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
            /// <summary>Action.</summary>
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, prior);
        }
    }
}
