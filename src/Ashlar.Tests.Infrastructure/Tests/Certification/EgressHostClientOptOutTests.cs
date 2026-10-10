using System.Net;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Egress;
using Ashlar.Client;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Infrastructure.Execution.Routing.Sdk.Extensions;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Host exceptions are named, late-configurable, and cannot lower AirGapped or opt out Ashlar clients.</summary>
[Collection("EnvironmentVariables")]
[Trait("Category", "Certification")]
public sealed class EgressHostClientOptOutTests
{
    [Theory]
    [InlineData(AshlarDeploymentProfile.AirGapped, null, true, 0)]
    [InlineData(AshlarDeploymentProfile.AirGapped, "report", true, 0)]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation, null, true, 1)]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation, null, false, 0)]
    [InlineData(AshlarDeploymentProfile.Full, "enforce", true, 1)]
    [InlineData(AshlarDeploymentProfile.Full, "enforce", false, 0)]
    public async Task A_late_named_opt_out_reports_only_its_host_client_except_on_AirGapped(
        AshlarDeploymentProfile profile, string? mode, bool optOut, int sends)
    {
        using var profileVariable = EnvironmentVariableScope.Unset("ASHLAR_DEPLOYMENT_PROFILE");
        using var modeVariable = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", mode);
        using var state = new EgressProcessStateScope(reset: true);
        var services = new ServiceCollection();
        services.AddAshlarProfile(profile);
        if (optOut) services.Configure<EgressGuardOptions>(o => o.ReportOnlyClients.Add("host-test"));
        var transport = new CountingHandler();
        services.AddHttpClient("host-test").ConfigurePrimaryHttpMessageHandler(() => transport);
        var records = new List<EgressDecision>();
        using var subscription = EgressDecisionLog.Subscribe(new Sink(d =>
        {
            if (d.Site == "factory:host-test") records.Add(d);
        }));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("host-test");
        var error = await Record.ExceptionAsync(async () => { using var response = await client.GetAsync("https://remote.example/private"); });
        transport.Sends.Should().Be(sends);
        var decision = records.Should().ContainSingle("owned guards publish the effective policy once").Which;
        if (sends == 1)
        {
            error.Should().BeNull();
            decision.Mode.Should().Be("report");
            decision.ModeBasis.Should().Be("host-opt-out");
            decision.Access.Allowed.Should().BeFalse("the exception changes mode, not the access decision");
        }
        else error.Should().BeOfType<EgressRefusedException>();

        var other = new CountingHandler();
        using var direct = EgressHttp.CreateClient(other, EgressFamilies.Http, "unrelated", provider.GetRequiredService<IEgressGuard>());
        var unrelated = await Record.ExceptionAsync(async () => { using var response = await direct.GetAsync("https://remote.example/other"); });
        unrelated.Should().BeOfType<EgressRefusedException>();
        other.Sends.Should().Be(0, "a named exception never changes the container's general guard");
    }

    [Theory]
    [InlineData("")]
    [InlineData("AshlarExecution")]
    [InlineData("IRunPodClient")]
    [InlineData("IAshlarClient")]
    [InlineData("ashlar-sns-signing")]
    [InlineData("mesh-lab-worker-executor")]
    [InlineData("OllamaModelServingBackend")]
    [InlineData("Ashlar.ModelArtifactCatalog.OllamaTags")]
    [InlineData("Ashlar.ModelArtifactCatalog.DockerOllamaProbe")]
    [InlineData("Ashlar.ModelArtifactCatalog.OllamaRemoteLibrary")]
    public async Task A_protected_client_opt_out_fails_actual_host_start(string name)
    {
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddAshlarEgressGuard();
            services.Configure<EgressGuardOptions>(o => o.ReportOnlyClients.Add(name));
        }).Build();
        Func<Task> start = () => host.StartAsync();
        await start.Should().ThrowAsync<OptionsValidationException>();
    }

    [Theory]
    [InlineData("classification")]
    [InlineData("mode")]
    [InlineData("junk")]
    [InlineData("host-throws")]
    [InlineData("host-null")]
    public async Task A_named_opt_out_never_turns_a_fault_into_a_send(string fault)
    {
        using var profileVariable = EnvironmentVariableScope.Unset("ASHLAR_DEPLOYMENT_PROFILE");
        using var modeVariable = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", fault == "junk" ? "junk" : null);
        using var state = new EgressProcessStateScope(reset: true);
        var services = new ServiceCollection();
        services.AddAshlarProfile(AshlarDeploymentProfile.SecureWorkstation);
        services.Configure<EgressGuardOptions>(o => o.ReportOnlyClients.Add("fault-test"));
        if (fault.StartsWith("host-", StringComparison.Ordinal))
            services.AddSingleton<IEgressGuard>(new FaultyGuard(fault == "host-null"));
        var transport = new CountingHandler();
        services.AddHttpClient("fault-test").ConfigurePrimaryHttpMessageHandler(() => transport);
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("fault-test");
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        if (fault == "mode")
            EgressProcessStateScope.SetModeResolutionProbe(() => throw new InvalidOperationException("mode fault"));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            fault == "classification" ? new Uri("/relative", UriKind.Relative) : new Uri("https://remote.example/"));
        var error = await Record.ExceptionAsync(async () => { using var response = await invoker.SendAsync(request, CancellationToken.None); });
        var decision = error.Should().BeOfType<EgressRefusedException>().Which.Decision;
        decision.Mode.Should().Be("enforce");
        decision.ModeBasis.Should().NotBe("host-opt-out");
        transport.Sends.Should().Be(0);
    }

    private sealed class FaultyGuard(bool returnsNull) : IEgressGuard
    {
        public EgressDecision Evaluate(EgressRequest request) =>
            returnsNull ? null! : throw new InvalidOperationException("host fault");
    }

    [Theory]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation, false, false, 1)]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation, true, false, 2)]
    [InlineData(AshlarDeploymentProfile.AirGapped, true, false, 1)]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation, false, true, 1)]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation, true, true, 2)]
    [InlineData(AshlarDeploymentProfile.AirGapped, true, true, 1)]
    public async Task Redirect_hops_keep_the_named_policy_and_AirGapped_ignores_it(
        AshlarDeploymentProfile profile, bool optOut, bool hostGuard, int sends)
    {
        using var profileVariable = EnvironmentVariableScope.Unset("ASHLAR_DEPLOYMENT_PROFILE");
        using var modeVariable = EnvironmentVariableScope.Unset("ASHLAR_EGRESS_MODE");
        using var state = new EgressProcessStateScope(reset: true);
        var services = new ServiceCollection();
        services.AddAshlarProfile(profile);
        if (hostGuard) services.AddSingleton<IEgressGuard>(new DelegatingGuard(new EgressGuard("full", "enforce")));
        if (optOut) services.Configure<EgressGuardOptions>(o => o.ReportOnlyClients.Add("redirect-opt-out"));
        var transport = new RedirectingHandler();
        services.AddHttpClient("redirect-opt-out").ConfigurePrimaryHttpMessageHandler(() => transport);
        var records = new List<EgressDecision>();
        using var subscription = EgressDecisionLog.Subscribe(new Sink(d =>
        {
            if (d.Site == "factory:redirect-opt-out") records.Add(d);
        }));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("redirect-opt-out");
        transport.AllowAutoRedirect.Should().BeFalse("the production follower takes over a recognized primary's redirects");
        var error = await Record.ExceptionAsync(async () => { using var response = await client.GetAsync("http://127.0.0.1/first"); });
        transport.Requested.Should().HaveCount(sends);
        transport.Requested[0].Host.Should().Be("127.0.0.1");
        var last = records.Last();
        last.Destination.Should().Be("https://redirect-target.example");
        if (sends == 2)
        {
            error.Should().BeNull();
            transport.Requested[1].Host.Should().Be("redirect-target.example");
            last.Mode.Should().Be("report");
            last.ModeBasis.Should().Be("host-opt-out");
            last.Access.Allowed.Should().BeFalse();
        }
        else error.Should().BeOfType<EgressRefusedException>().Which.Decision.Should().BeSameAs(last);
        if (!hostGuard) records.Should().HaveCount(2, "one final decision is published per hop by the owned guard");
    }

    private sealed class DelegatingGuard(IEgressGuard inner) : IEgressGuard
    {
        public EgressDecision Evaluate(EgressRequest request) => inner.Evaluate(request);
    }

    private sealed class RedirectingHandler : HttpClientHandler
    {
        public List<Uri> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            var response = new HttpResponseMessage(Requested.Count == 1 ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.OK)
                { RequestMessage = request };
            if (Requested.Count == 1) response.Headers.Location = new Uri("https://redirect-target.example/last");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void Typed_client_reserved_names_match_the_real_registrations()
    {
        using var profileVariable = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "full");
        using var modeVariable = EnvironmentVariableScope.Unset("ASHLAR_EGRESS_MODE");
        using var state = new EgressProcessStateScope(reset: true);
        var services = new ServiceCollection();
        services.AddRunPodCapabilityRouting(new ConfigurationBuilder().Build());
        services.AddAshlarClient("http://127.0.0.1:12345");
        var names = new HashSet<string>(StringComparer.Ordinal);
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(builder =>
            names.Add(builder.Name ?? string.Empty)));
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IRunPodClient>();
        _ = provider.GetRequiredService<IAshlarClient>();
        names.Should().BeEquivalentTo(new[] { "IRunPodClient", "IAshlarClient" },
            "the protected registry must use actual factory names, not interface full names");
    }

    [Theory]
    [InlineData("full")]
    [InlineData("air-gapped")]
    public async Task Every_configured_opt_out_is_warned_at_host_start_even_when_ignored(string profile)
    {
        using var profileVariable = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", profile);
        using var modeVariable = EnvironmentVariableScope.Unset("ASHLAR_EGRESS_MODE");
        using var state = new EgressProcessStateScope(reset: true);
        var capture = new WarningCapture();
        using var host = new HostBuilder().ConfigureLogging(logging => logging.ClearProviders().AddProvider(capture))
            .ConfigureServices(services =>
            {
                services.AddAshlarEgressGuard();
                services.Configure<EgressGuardOptions>(o =>
                {
                    o.ReportOnlyClients.Add("host-first");
                    o.ReportOnlyClients.Add("host-second");
                });
            }).Build();
        await host.StartAsync();
        await host.StopAsync();
        capture.Messages.Should().HaveCount(2);
        capture.Messages.Should().ContainSingle(m => m.Contains("host-first", StringComparison.Ordinal));
        capture.Messages.Should().ContainSingle(m => m.Contains("host-second", StringComparison.Ordinal));
    }

    private sealed class WarningCapture : ILoggerProvider, ILogger
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 7307)
            {
                logLevel.Should().Be(LogLevel.Warning);
                Messages.Add(formatter(state, exception));
            }
        }
        public void Dispose() { }
    }

    private sealed class Sink(Action<EgressDecision> record) : IEgressDecisionSink
    {
        public void Record(EgressDecision decision) => record(decision);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        }
    }
}
