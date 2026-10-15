using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Testing.Abstractions;
using Ashlar.Core.Application.Testing.Models;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.Models;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>Tests for provider backed model directive.</summary>
public sealed class ProviderBackedModelDirectiveTests : UnitTestBase
{
    public override async Task<TestResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await TestProviderAndModelDirectivesAreForwardedAsync().ConfigureAwait(false);
            return new TestResult
            {
                Name = nameof(ProviderBackedModelDirectiveTests),
                Category = "Infrastructure",
                Passed = true,
                Message = "ProviderBackedModel directive tests passed"
            };
        }
        catch (AssertionException ex)
        {
            return new TestResult
            {
                Name = nameof(ProviderBackedModelDirectiveTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace
            };
        }
        catch (Exception ex)
        {
            return new TestResult
            {
                Name = nameof(ProviderBackedModelDirectiveTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace
            };
        }
    }

    private async Task TestProviderAndModelDirectivesAreForwardedAsync()
    {
        var providerFactory = new CapturingProviderFactory();
        var model = new ProviderBackedModel(providerFactory, NullLogger<ProviderBackedModel>.Instance);
        var input = new ModelInput(new List<(string role, string content)>
        {
            ("system", "ashlar.model.provider=ollama\nashlar.model.name=qwen2.5:7b\nSystem behavior"),
            ("user", "Run task")
        });

        var output = await model.CompleteAsync(input, CancellationToken.None).ConfigureAwait(false);

        /// <summary>Assert equal.</summary>
        AssertEqual("ok", output.Text);
        /// <summary>Assert equal.</summary>
        AssertEqual("ollama", providerFactory.LastProvider);
        /// <summary>Assert equal.</summary>
        AssertEqual("qwen2.5:7b", providerFactory.LastModel);
        AssertTrue((providerFactory.LastSystemPrompt ?? string.Empty).Contains("System behavior", StringComparison.Ordinal));
    }

    /// <summary>Tests for capturing provider factory.</summary>
    private sealed class CapturingProviderFactory : IProviderFactory
    {
        /// <summary>Last provider.</summary>
        public string? LastProvider { get; private set; }
        /// <summary>Last system prompt.</summary>
        public string? LastSystemPrompt { get; private set; }
        /// <summary>Last user prompt.</summary>
        public string? LastUserPrompt { get; private set; }
        /// <summary>Last model.</summary>
        public string? LastModel { get; private set; }

        /// <summary>Returns whether  provider available.</summary>
        /// <param name="provider">Provider.</param>
        public bool IsProviderAvailable(string provider) => true;

        public Ashlar.Core.Application.Execution.Ports.ProviderAvailabilityResult ProbeProviderAvailability(string provider) =>
            new(IsProviderAvailable(provider));

        public Task<string> ExecuteLLMAsync(
            string provider,
            string systemPrompt,
            string userPrompt,
            object config,
            CancellationToken cancellationToken = default)
        {
            LastProvider = provider;
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            LastModel = ReadModel(config);
            return Task.FromResult("ok");
        }

        public Task<string> ExecuteVisionAsync(
            string provider,
            string systemPrompt,
            string userPrompt,
            byte[] imageBytes,
            object config,
            CancellationToken cancellationToken = default)
            => Task.FromResult("ok");

        public Task<string> ExecuteVisionMultiFrameAsync(
            string provider,
            string systemPrompt,
            string userPrompt,
            IReadOnlyList<byte[]> frameBytes,
            object config,
            CancellationToken cancellationToken = default)
            => Task.FromResult("ok");

        public Task<string> ExecuteVideoAsync(
            string systemPrompt,
            string userPrompt,
            IReadOnlyList<byte[]> frameBytes,
            object config,
            CancellationToken cancellationToken = default)
            => Task.FromResult("ok");

        public Task EnsureOllamaReachableAsync(bool requireVisionModel, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static string? ReadModel(object config)
        {
            var prop = config.GetType().GetProperty("model");
            return prop?.GetValue(config) as string;
        }
    }
}
