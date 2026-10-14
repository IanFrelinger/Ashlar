using System.Net;
using System.Net.Sockets;
using Ashlar.Infrastructure;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet.Host;

[CollectionDefinition("InboundProfiles", DisableParallelization = true)]
public sealed class InboundProfilesCollection;

/// <summary>Real shipped entry points and real Kestrel sockets: Q6 applies to both inbound hosts.</summary>
[Collection("InboundProfiles")]
[Trait("Category", "ProdStyle")]
public sealed class InboundHostLoopbackProdStyleTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var host in new[] { "grpc", "fleet" })
        foreach (var profile in new[] { "air-gapped", "airgapped", "secure-workstation" })
        foreach (var binding in new[] { "urls", "endpoint", "code", "loopback" })
            yield return new object[] { host, profile, binding };
        foreach (var host in new[] { "grpc", "fleet" })
            yield return new object[] { host, "full", "code" };
    }

    [Theory(Timeout = 120000)]
    [MemberData(nameof(Cases))]
    public Task Shipped_host_enforces_loopback_before_and_after_bind(string host, string profile, string binding)
        => host == "grpc" ? Check<GrpcHostProgram>(profile, binding) : Check<FleetHostProgram>(profile, binding);

    [Theory(Timeout = 120000)]
    [InlineData("grpc", "endpoint")]
    [InlineData("fleet", "endpoint")]
    [InlineData("grpc", "code")]
    [InlineData("fleet", "code")]
    public Task Startup_refusal_survives_disposal_before_the_deferred_observer_starts(string host, string binding)
        => host == "grpc" ? Check<GrpcHostProgram>("airgapped", binding, forceDisposalRace: true)
            : Check<FleetHostProgram>("airgapped", binding, forceDisposalRace: true);

    private static async Task Check<T>(string profile, string binding, bool forceDisposalRace = false) where T : class
    {
        using var state = new EgressProcessStateScope(reset: true);
        using var env = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", profile);
        using var mock = new EnvironmentVariableScope("ASHLAR_ALLOW_MOCK", "1");
        using var ports = new EnvironmentVariableScope("ASPNETCORE_HTTP_PORTS", null);
        var startup = new StartupFailureRecorder();
        await using var factory = new ObservedFactory<T>(startup, forceDisposalRace).WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("urls", binding == "urls" ? "http://0.0.0.0:0" : "http://127.0.0.1:0");
            builder.UseSetting("Ashlar:Security:ApiKey", "loopback-prodstyle-test-key");
            builder.UseSetting("Ashlar:RegisterBackgroundAgentHostedService", "false");
            builder.UseSetting("Ashlar:DisableObservationPipeline", "true");
            if (binding == "endpoint")
                builder.UseSetting("Kestrel:Endpoints:Lan:Url", "http://0.0.0.0:0");
            if (binding == "code")
                builder.ConfigureKestrel(k => k.ListenAnyIP(0));
        });
        factory.UseKestrel();
        var starts = profile == "full" || binding == "loopback";
        Action start = () => _ = factory.Services;
        if (!starts)
        {
            var observed = start.Should().Throw<Exception>().Which;
            if (forceDisposalRace) observed.Should().BeOfType<ObjectDisposedException>();
            var recorded = startup.Failure.Task.IsCompletedSuccessfully ? startup.Failure.Task.Result : null;
            PolicyFailure(observed, recorded, binding).Should().NotBeNull(
                "startup must fail with the genuine typed loopback refusal; observed: {0}; recorded: {1}", observed, recorded);
            return;
        }
        start.Should().NotThrow();
        var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        addresses.Should().NotBeEmpty();
        if (profile != "full") addresses.Should().OnlyContain(a => AshlarInboundListenerPolicy.IsLoopbackEndpoint(a));
        var port = new Uri(addresses.First()).Port;
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port);
        socket.Connected.Should().BeTrue("Kestrel must really accept connections");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("wrong-options")]
    [InlineData("wrong-message")]
    [InlineData("unrelated-observed")]
    [InlineData("postbind-for-prebind")]
    public void Disposal_without_authentic_policy_evidence_is_not_a_refusal(string invalid)
    {
        const string message = "AirGapped: inbound listeners must bind loopback until PR 5 mediates responses on inbound connections.";
        Exception observed = invalid == "unrelated-observed" ? new IOException("unrelated failure") : new ObjectDisposedException("IServiceProvider");
        Exception? recorded = invalid switch
        {
            "missing" => null,
            "wrong-type" => new IOException(message),
            "wrong-options" => new OptionsValidationException(Options.DefaultName, typeof(string), [message]),
            "wrong-message" => new OptionsValidationException(Options.DefaultName, typeof(AshlarInboundListenerOptions), ["unrelated validation"]),
            "postbind-for-prebind" => new InvalidOperationException(message),
            _ => new OptionsValidationException(Options.DefaultName, typeof(AshlarInboundListenerOptions), [message]),
        };
        PolicyFailure(observed, recorded, "endpoint").Should().BeNull();
    }

    private static Exception? PolicyFailure(Exception observed, Exception? recorded, string binding)
    {
        // RunAsync can dispose the root provider before WAF's deferred observer registers.
        // Recover only that masking exception, only from this host's startup-fault event.
        var candidate = observed is ObjectDisposedException ? recorded : observed;
        var expectedType = binding is "urls" or "endpoint"
            ? candidate is OptionsValidationException validation && validation.OptionsType == typeof(AshlarInboundListenerOptions)
            : candidate?.GetType() == typeof(InvalidOperationException);
        return expectedType && candidate!.Message.Contains(": inbound listeners must bind loopback", StringComparison.Ordinal)
            ? candidate : null;
    }

    private sealed class ObservedFactory<T>(StartupFailureRecorder startup, bool forceDisposalRace)
        : WebApplicationFactory<T> where T : class
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
            => builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(_ => startup));

        protected override IHost CreateHost(IHostBuilder builder)
        {
            if (!forceDisposalRace) return base.CreateHost(builder);
            var host = builder.Build();
            // Force the real RunAsync failure/disposal to win before WAF observes startup.
            // This deterministically reproduces the macOS DeferredHost.StartAsync race.
            startup.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            host.Start();
            return host;
        }
    }

    private sealed class StartupFailureRecorder : ILoggerProvider
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => new RecorderLogger(this, categoryName);
        public void Dispose() => Disposed.TrySetResult();

        private sealed class RecorderLogger(StartupFailureRecorder owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == "Microsoft.Extensions.Hosting.Internal.Host" && logLevel == LogLevel.Error
                    && eventId.Id == 11 && eventId.Name == "HostedServiceStartupFaulted" && exception is not null)
                    owner.Failure.TrySetResult(exception);
            }
        }
    }

}
