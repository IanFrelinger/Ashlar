using System.Reflection;
using System.Runtime.CompilerServices;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Agents.TestKit;
using Ashlar.Core.Application.Ephemeral.Ports;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.Ollama;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using IProviderFactory = Ashlar.Infrastructure.Execution.IProviderFactory;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>
/// Card #809: the <c>improve</c> verb's provider-registration shape disposes <see cref="ProviderFactory"/>
/// so repeated refused probes yield the suppressed-count summary when the verb finishes, without changing
/// the first-warning behaviour.
/// </summary>
public sealed class AdaptImproveProviderDisposalTwinTests
{
    [Fact]
    public async Task Improve_registration_disposes_ProviderFactory_and_flushes_suppressed_probe_summary()
    {
        var logger = new WarningRecorder();
        var refusals = new List<EgressRefusedException>();
        using var host = await RefusingFactoryHost.CreateAsync(() =>
        {
            var refusal = Refusal();
            refusals.Add(refusal);
            return refusal;
        }, logger);

        var services = new ServiceCollection();
        services.AddLogging();
        // Same dual-port registration shape ImproveCommand uses (trust off): concrete factory + ports.
        services.AddSingleton(host.Factory);
        services.AddSingleton<IProviderFactory>(sp => sp.GetRequiredService<ProviderFactory>());
        services.AddSingleton<Ashlar.Core.Application.Execution.Ports.IProviderFactory>(sp =>
            new Ashlar.Infrastructure.Adapters.ProviderFactoryAdapter(sp.GetRequiredService<IProviderFactory>()));

        using (var provider = services.BuildServiceProvider())
        {
            var factory = provider.GetRequiredService<IProviderFactory>();
            factory.IsProviderAvailable("ollama").Should().BeFalse();
            factory.IsProviderAvailable("ollama").Should().BeFalse();
            host.Sends.Should().Be(2);
            logger.Warnings.Should().Equal(new[] { (7307, 0L, false, refusals[0].Ref) },
                "first refused probe logs one Warning; the second is suppressed inside the window");
        }

        logger.Warnings.Should().Equal(
            new[] { (7307, 0L, false, refusals[0].Ref), (7307, 1L, true, refusals[0].Ref) },
            "disposing the verb's ServiceProvider flushes the suppressed-count summary");
    }

    private static EgressRefusedException Refusal() => new(new EgressGuard("full", "enforce").Evaluate(
        new EgressRequest(EgressFamilies.Http, "provider-refusal", new Uri("https://remote.example"))));

    private sealed class RefusingFactoryHost : IDisposable
    {
        private const string BaseUrl = "https://remote.example";
        private readonly HttpClient _client;
        private readonly StrongBox<int> _sends;

        private RefusingFactoryHost(ProviderFactory factory, HttpClient client, StrongBox<int> sends)
        {
            Factory = factory;
            _client = client;
            _sends = sends;
        }

        internal ProviderFactory Factory { get; }
        internal int Sends => Volatile.Read(ref _sends.Value);

        internal static async Task<RefusingFactoryHost> CreateAsync(
            Func<Exception> fault,
            ILogger<ProviderFactory> logger)
        {
            var lifecycle = new Mock<IEphemeralModelLifecycle>();
            lifecycle.Setup(x => x.GetBaseUrlAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());
            var factory = new ProviderFactory(logger, lifecycle.Object);
            await factory.OllamaWarmup.ConfigureAwait(false);
            lifecycle.Setup(x => x.GetBaseUrlAsync(It.IsAny<CancellationToken>())).ReturnsAsync(BaseUrl);
            var sends = new StrongBox<int>();
            var client = new HttpClient(StubHttpMessageHandler.FromSync((_, _) =>
            {
                Interlocked.Increment(ref sends.Value);
                throw fault();
            }));
            typeof(ProviderFactory).GetField("_ollamaProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(factory, new OllamaProvider(client, BaseUrl));
            typeof(ProviderFactory).GetField("_ollamaProviderBaseUrl", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(factory, BaseUrl);
            return new RefusingFactoryHost(factory, client, sends);
        }

        public void Dispose() => _client.Dispose();
    }

    private sealed class WarningRecorder : ILogger<ProviderFactory>
    {
        private readonly List<(int EventId, long Suppressed, bool Summary, string Ref)> _warnings = new();

        internal IReadOnlyList<(int EventId, long Suppressed, bool Summary, string Ref)> Warnings
        {
            get { lock (_warnings) return _warnings.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            var values = state as IReadOnlyList<KeyValuePair<string, object?>>;
            object? Value(string key) => values?.FirstOrDefault(pair => pair.Key == key).Value;
            var entry = (eventId.Id, Value("Suppressed") as long? ?? -1L, Value("Summary") as bool? ?? false,
                Value("Ref") as string ?? "");
            lock (_warnings) _warnings.Add(entry);
        }
    }
}
