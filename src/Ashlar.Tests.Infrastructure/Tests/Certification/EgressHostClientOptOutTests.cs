using System.Net;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Egress;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
