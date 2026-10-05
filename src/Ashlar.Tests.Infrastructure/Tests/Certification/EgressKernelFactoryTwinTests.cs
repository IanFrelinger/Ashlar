using System.Collections.Concurrent;
using System.Net;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Application.ModelArtifacts.Ports;
using Ashlar.Core.Application.NodeCapabilityRuntime.Ports;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Egress;
using Ashlar.Infrastructure.NodeCapabilityRuntime.Backends;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 3b, the behavioural twin of the factory route in the kernel: a provider built by
/// <c>AddAshlar</c> puts the report-only guard handler on its <see cref="IHttpClientFactory"/> clients, once, with
/// no extra call from the host.
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> <c>AddAshlar</c> calls <c>AddAshlarEgressGuard</c> after its own
/// <c>AddHttpClient</c>, so the default client of a bare kernel records one decision per send with site
/// <c>factory:</c>. The kernel members that register a client of their own (RunPod, NCR and the model-artifact
/// catalog on Linux) call it again after their registration; the marker makes those calls add nothing, so a send
/// is still one decision, the collection still holds one activator and one <see cref="IEgressGuard"/>, and a named
/// kernel client's site is <c>factory:</c> plus its name.</para>
/// <para><b>Why the static facts are not enough.</b> The convention test sees that each registration has a call
/// after it in the same block; only a send through a real kernel provider shows that the handler is on the client
/// exactly once.</para>
/// <para><b>Isolation.</b> The decision log is process-wide and other classes make decisions in parallel, so every
/// assertion filters by a destination host unique to the test. Hermetic: a test-only client default puts a stub
/// primary handler (a new one per handler build) under every client, so nothing leaves the process.
/// <see cref="IHostedService"/> is never resolved, so no kernel background service starts. The collection
/// serializes this class with the other tests that compose <c>AddAshlar</c> from environment variables.</para>
/// </remarks>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class EgressKernelFactoryTwinTests
{
    [Fact]
    public async Task AnAddAshlarKernelProvider_DefaultCreateClient_RecordsOneDecision_WithSiteFactoryColon()
    {
        var host = UniqueHost();
        var recorder = new DecisionRecorder(host);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var sends = new SendCounter();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlarProfile(AshlarDeploymentProfile.System);
        UseStubPrimaryHandler(services, sends);

        TheGuardIsRegisteredOnce(services);

        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient();

        using (var response = await client.GetAsync(new Uri($"https://{host}/kernel?q=1")))
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the stub primary handler answers every send");

        sends.Count.Should().Be(1, "the stub saw the one send");
        var decision = recorder.Decisions.Should().ContainSingle(
            "AddAshlar installs the guard handler on its factory clients, once").Which;
        decision.Site.Should().Be("factory:", "the unnamed default client's name is empty");
        decision.Family.Should().Be(EgressFamilies.HttpFactory);
        decision.Destination.Should().Be($"https://{host}", "the record keeps scheme and host, never the path or query");
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        decision.Fault.Should().BeNull();
    }

    [Fact]
    public async Task EveryKernelMemberThatInstallsTheGuard_LeavesOneHandler()
    {
        var host = UniqueHost();
        var recorder = new DecisionRecorder(host);
        using var subscription = EgressDecisionLog.Subscribe(recorder);
        var sends = new SendCounter();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlarProfile(AshlarDeploymentProfile.Full);
        UseStubPrimaryHandler(services, sends);

        // Positive control: the members that call AddAshlarEgressGuard after their own registration ran here, so
        // the marker, not their absence, is what keeps the handler single.
        services.Should().Contain(d => d.ServiceType == typeof(IRunPodClient), "the Full profile adds RunPod routing");
        services.Should().Contain(d => d.ServiceType == typeof(IModelArtifactCatalogService),
            "the Full profile adds the model-artifact catalog");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            services.Should().Contain(d => d.ServiceType == typeof(IModelServingBackend) && d.ImplementationFactory != null,
                "on a desktop platform the NCR member replaces the null backend with the Ollama one");
        }

        TheGuardIsRegisteredOnce(services);

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using (var client = factory.CreateClient())
        using (await client.GetAsync(new Uri($"https://{host}/default")))
        {
        }

        using (var client = factory.CreateClient(nameof(OllamaModelServingBackend)))
        using (await client.GetAsync(new Uri($"https://{host}/ncr")))
        {
        }

        sends.Count.Should().Be(2, "the stub saw both sends");
        recorder.Decisions.Select(d => d.Site).Should().Equal(
            new[] { "factory:", "factory:" + nameof(OllamaModelServingBackend) },
            "each send is exactly one decision however many kernel members called AddAshlarEgressGuard");
        recorder.Decisions.Should().OnlyContain(d => d.Family == EgressFamilies.HttpFactory && d.Fault == null);
    }

    private static void TheGuardIsRegisteredOnce(IServiceCollection services)
    {
        services.Where(d => !d.IsKeyedService
                && d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(EgressDecisionLoggerActivator))
            .Should().ContainSingle("one hosted activator, however many members installed the guard");
        services.Where(d => !d.IsKeyedService && d.ServiceType == typeof(IEgressGuard))
            .Should().ContainSingle("one guard, however many members installed it")
            .Which.ImplementationInstance.Should().BeSameAs(EgressGuard.ProcessDefault);
    }

    // Test-only: registered after AddAshlar, so it is the last default to set the primary handler. A new stub per
    // handler build, so a rotated handler is never a disposed one.
    private static void UseStubPrimaryHandler(IServiceCollection services, SendCounter sends) =>
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(sends)));

    private static string UniqueHost() => $"egress-kernel-{Guid.NewGuid():N}.example";

    /// <summary>Keeps the decisions whose destination names this test's unique host.</summary>
    private sealed class DecisionRecorder : IEgressDecisionSink
    {
        private readonly string _host;
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();

        public DecisionRecorder(string host) => _host = host;

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public void Record(EgressDecision decision)
        {
            if (decision.Destination.Contains(_host, StringComparison.Ordinal))
                _decisions.Enqueue(decision);
        }
    }

    private sealed class SendCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    /// <summary>The primary handler: answers 204 to every send, so nothing leaves the process.</summary>
    private sealed class StubPrimaryHandler : HttpMessageHandler
    {
        private readonly SendCounter _sends;

        public StubPrimaryHandler(SendCounter sends) => _sends = sends;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _sends.Increment();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request });
        }
    }
}
