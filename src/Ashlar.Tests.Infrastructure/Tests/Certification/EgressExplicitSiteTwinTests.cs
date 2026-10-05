using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.BackgroundAgents.WebSearch;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Application.Analysis.Ports;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Scaling;
using Ashlar.Infrastructure.Validation.Adapters;
using Ashlar.Tools.Dev;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 3b, behavioural twin of the explicit guard calls: the sites that call
/// <c>EgressGuard.ProcessDefault.Evaluate(new EgressRequest(…))</c> themselves, because no HTTP handler or factory
/// sits on their path. Each case drives the real production member and reads the decision it publishes.
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> <c>ProcessCommandRunner</c> (EG-PROC-03) names its destination from the executable
/// and, for docker and docker-compose, from <c>DOCKER_HOST</c>, then a non-default <c>DOCKER_CONTEXT</c>, then the
/// local daemon; a live run records exactly one decision. <c>SneakernetTransport.ExportAsync</c> (EG-MESH-08)
/// records the file it actually writes, after the <c>.nxpkg</c> rewrite. <c>FileBasedSharedAdaptationStore.BroadcastAsync</c>
/// (EG-MESH-07) records the shared directory. <c>ValidationServiceAdapter</c> (EG-PROC-02) reads its destination off
/// the argv the real start-info builders produce: <c>dotnet build</c> restores (<c>nuget-feeds</c>) and
/// <c>dotnet test --no-build</c> does not (<c>host:dotnet</c>). The Tools.Dev runner (EG-PROC-01), reached through
/// <c>DotnetBuildTool</c> and <c>DotnetTestTool</c>, records the same split. <c>BingWebSearchProvider</c> (EG-WEB-01)
/// records scheme, host and port only, as <c>WebSearch</c>, while the query still reaches the wire. The last theory
/// classifies every destination shape this lane passes, so a change in the classifier shows up here first.</para>
/// <para><b>Process-global state.</b> The decision log is process-wide and other classes decide in parallel, so each
/// case enters its own <see cref="EgressSubject"/> frame (<c>egress-twin-&lt;guid&gt;</c>) and keeps only decisions
/// whose basis is that frame. Every guard here runs before the member's first <c>await</c>, so the frame reaches it.
/// No environment variable is written: <c>ProcessCommandRunner.Destination</c> takes the two docker variables as
/// arguments. Three cases start a real <c>dotnet</c>: <c>dotnet --version</c>, and the build and test tools in an
/// empty temporary directory, where they fail fast with no project to build and restore nothing. No case reaches the
/// network: Bing's client sends to a stub handler.</para>
/// <para>EG-TEL-01 is not here: it is decided in the API host's <c>Program.cs</c>, and its twin lives in
/// <c>Tests/VirtualProduction</c> on net10.0.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressExplicitSiteTwinTests : IDisposable
{
    private readonly string _dir;

    public EgressExplicitSiteTwinTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "egress-explicit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------------------------------------------------
    // EG-PROC-03: ProcessCommandRunner
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    // docker × each environment combination: DOCKER_HOST unset/set × DOCKER_CONTEXT unset/default/named.
    [InlineData("docker", null, null, "host:docker")]
    [InlineData("docker", null, "default", "host:docker")]
    [InlineData("docker", null, "remote-builder", "docker-context:remote-builder")]
    [InlineData("docker", "unix:///var/run/docker.sock", null, "unix:///var/run/docker.sock")]
    [InlineData("docker", "tcp://10.0.0.5:2376", "default", "tcp://10.0.0.5:2376")]
    [InlineData("docker", "ssh://admin@builder.example.com", "remote-builder", "ssh://admin@builder.example.com")]
    // Blank values are unset; a path and an extension still name docker.
    [InlineData("docker", "  ", " ", "host:docker")]
    [InlineData("/usr/local/bin/docker", null, " ci ", "docker-context:ci")]
    [InlineData("docker.exe", null, "ci", "docker-context:ci")]
    [InlineData("docker-compose", null, null, "host:docker")]
    [InlineData("docker-compose", "tcp://10.0.0.5:2376", null, "tcp://10.0.0.5:2376")]
    [InlineData("docker-compose", null, "remote-builder", "docker-context:remote-builder")]
    // Any other executable is named, whatever the docker variables say.
    [InlineData("/usr/bin/kubectl", "tcp://10.0.0.5:2376", "remote-builder", "process:kubectl")]
    [InlineData("helm", null, null, "process:helm")]
    [InlineData(null, null, null, "process:unknown")]
    [InlineData("", "tcp://10.0.0.5:2376", null, "process:unknown")]
    public void ProcessCommandRunner_Destination_reads_the_executable_then_DOCKER_HOST_then_DOCKER_CONTEXT(
        string? fileName, string? dockerHost, string? dockerContext, string expected)
    {
        ProcessCommandRunner.Destination(fileName, dockerHost, dockerContext).Should().Be(expected);
    }

    [Fact]
    public async Task ProcessCommandRunner_RunAsync_records_exactly_one_process_decision_before_the_process_starts()
    {
        using var observed = Observe();

        var result = await new ProcessCommandRunner().RunAsync("dotnet", ["--version"]);

        result.ExitCode.Should().Be(0, "dotnet --version is the cheapest real process the container always has");
        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-PROC-03");
        decision.Family.Should().Be(EgressFamilies.Process);
        decision.Destination.Should().Be("process:dotnet");
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        decision.Fault.Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------------------------
    // EG-MESH-08 and EG-MESH-07: the adaptation file doors
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SneakernetTransport_ExportAsync_records_the_file_it_writes_after_the_nxpkg_rewrite()
    {
        var target = Path.Combine(_dir, "mesh-export");
        var expected = Path.ChangeExtension(target, ".nxpkg");
        var transport = new SneakernetTransport(new EmptySync());
        using var observed = Observe();

        await transport.ExportAsync(target);

        File.Exists(expected).Should().BeTrue("the decision names the file that was written");
        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MESH-08");
        decision.Family.Should().Be(EgressFamilies.FileExport);
        decision.Destination.Should().Be("file:" + expected);
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
    }

    [Fact]
    public async Task FileBasedSharedAdaptationStore_BroadcastAsync_records_the_shared_directory()
    {
        var shared = Path.Combine(_dir, "shared");
        var store = new FileBasedSharedAdaptationStore(
            shared, new NoOpAdaptationLog(), new PassingRegressionRunner(), new PermissiveImmutableCore());
        using var observed = Observe();

        await store.BroadcastAsync(Entry());

        Directory.EnumerateFiles(shared, "meta.json", SearchOption.AllDirectories).Should().ContainSingle();
        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MESH-07");
        decision.Family.Should().Be(EgressFamilies.MeshPublish);
        decision.Destination.Should().Be("file:" + shared);
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
    }

    [Fact]
    public async Task FileBasedSharedAdaptationStore_records_the_attempt_even_when_the_broadcast_is_refused()
    {
        // R12, attempt semantics: the decision is made before the IsSafeRelativePath refusal fires.
        var shared = Path.Combine(_dir, "shared-refused");
        var store = new FileBasedSharedAdaptationStore(
            shared, new NoOpAdaptationLog(), new PassingRegressionRunner(), new PermissiveImmutableCore());
        using var observed = Observe();

        var act = () => store.BroadcastAsync(Entry("../escape.cs"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a safe relative path*");
        observed.Sink.Seen.Should().ContainSingle().Which.Site.Should().Be("EG-MESH-07");
    }

    // ---------------------------------------------------------------------------------------------------------
    // EG-PROC-02 (ValidationServiceAdapter) and EG-PROC-01 (Tools.Dev DotnetRunner)
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ValidationServiceAdapter_RestoreDestination_over_the_real_start_info_builders()
    {
        var csproj = Path.Combine(_dir, "Some.Tests.csproj");

        ValidationServiceAdapter.RestoreDestination(ValidationServiceAdapter.CreateDotnetBuildStartInfo(csproj))
            .Should().Be("nuget-feeds", "dotnet build restores implicitly");
        ValidationServiceAdapter.RestoreDestination(ValidationServiceAdapter.CreateDotnetTestStartInfo(csproj, null, null, false))
            .Should().Be("host:dotnet", "validate's dotnet test always passes --no-build, so nothing is restored");
        ValidationServiceAdapter.RestoreDestination(ValidationServiceAdapter.CreateDotnetTestStartInfo(csproj, "net8.0", "Category=Unit", true))
            .Should().Be("host:dotnet");
    }

    [Fact]
    public async Task DotnetBuildTool_records_nuget_feeds_and_DotnetTestTool_no_build_records_the_host()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_dir, "empty")).FullName;
        using var observed = Observe();

        _ = await DotnetBuildTool.RunReleaseBuildAsync(empty);
        _ = await DotnetTestTool.RunTrxTestsNoBuildAsync(empty);

        observed.Sink.Seen.Select(d => (d.Site, d.Family, d.Destination, d.DestinationClass)).Should().Equal(
            ("EG-PROC-01", EgressFamilies.Process, "nuget-feeds", EgressDestinationClass.NetworkExport),
            ("EG-PROC-01", EgressFamilies.Process, "host:dotnet", EgressDestinationClass.Host));
    }

    // ---------------------------------------------------------------------------------------------------------
    // EG-WEB-01: BingWebSearchProvider
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BingWebSearchProvider_records_scheme_host_and_port_as_WebSearch_while_the_query_reaches_the_wire()
    {
        var stub = new RecordingHandler();
        using var client = new HttpClient(stub);
        var provider = new BingWebSearchProvider(client, "twin-key");
        using var observed = Observe();

        _ = await provider.SearchAsync("secret twin query", 5);

        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-WEB-01");
        decision.Family.Should().Be(EgressFamilies.WebSearch);
        decision.Destination.Should().Be("https://api.bing.microsoft.com");
        decision.DestinationClass.Should().Be(EgressDestinationClass.WebSearch);
        decision.Destination.Should().NotContain("secret").And.NotContain("v7.0");
        stub.Requests.Should().ContainSingle().Which.Should().Be(
            "https://api.bing.microsoft.com/v7.0/search?q=secret%20twin%20query&count=5",
            "the guard records; it does not change what is sent");
    }

    [Fact]
    public async Task BingWebSearchProvider_records_nothing_for_an_empty_query_that_sends_nothing()
    {
        var stub = new RecordingHandler();
        using var client = new HttpClient(stub);
        using var observed = Observe();

        _ = await new BingWebSearchProvider(client, "twin-key").SearchAsync("  ", 5);

        observed.Sink.Seen.Should().BeEmpty();
        stub.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------------
    // How the guard classifies every destination shape this lane passes
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(EgressFamilies.FileExport, "file:/home/u/out/pkg.ashpkg", "file:/home/u/out/pkg.ashpkg", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.MeshPublish, "file:/home/u/.ashlar/mesh/published", "file:/home/u/.ashlar/mesh/published", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.MeshPublish, @"file:\\server\share\published", @"file:\\server\share\published", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.MeshServe, "tcp://127.0.0.1", "tcp://127.0.0.1", EgressDestinationClass.Host)]
    [InlineData(EgressFamilies.MeshServe, "tcp://[::1]", "tcp://[::1]", EgressDestinationClass.Host)]
    [InlineData(EgressFamilies.MeshServe, "tcp://192.168.1.5", "tcp://192.168.1.5", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.MeshServe, "tcp://10.0.0.5", "tcp://10.0.0.5", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.MeshServe, "tcp://unknown", "tcp://unknown", EgressDestinationClass.NetworkExport)]
    // Why MeshServeService.PeerDestination maps an IPv4-mapped peer to IPv4: unbracketed, it does not parse.
    [InlineData(EgressFamilies.MeshServe, "tcp://::ffff:10.0.0.5", "tcp://<unparsed>", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.MeshDiscovery, "udp://239.7.42.1:7421", "udp://239.7.42.1:7421", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.MeshDiscovery, "host:listen-only", "host:listen-only", EgressDestinationClass.Host)]
    [InlineData(EgressFamilies.Process, "nuget-feeds", "nuget-feeds", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.Process, "host:dotnet", "host:dotnet", EgressDestinationClass.Host)]
    [InlineData(EgressFamilies.Process, "host:docker", "host:docker", EgressDestinationClass.Host)]
    [InlineData(EgressFamilies.Process, "unix:///var/run/docker.sock", "unix://", EgressDestinationClass.Host)]
    [InlineData(EgressFamilies.Process, "npipe:////./pipe/docker_engine", "npipe://", EgressDestinationClass.Host)]
    [InlineData(EgressFamilies.Process, "tcp://10.0.0.5:2376", "tcp://10.0.0.5:2376", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.Process, "ssh://admin@builder.example.com", "ssh://builder.example.com", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.Process, "docker-context:remote-builder", "docker-context:remote-builder", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.Process, "process:kubectl", "process:kubectl", EgressDestinationClass.NetworkExport)]
    [InlineData(EgressFamilies.ModelLegacy, "http://localhost:11434/api/generate", "http://localhost:11434", EgressDestinationClass.Host)]
    [InlineData(EgressFamilies.ModelLegacy, "http://gpu-box:11434/api/generate", "http://gpu-box:11434", EgressDestinationClass.ExternalModel)]
    public void Every_destination_shape_the_explicit_sites_pass_is_classified_as_expected(
        string family, string destination, string recorded, EgressDestinationClass expectedClass)
    {
        var decision = new EgressGuard("full").Evaluate(new EgressRequest(family, "EG-TWIN-1", destination));

        decision.Fault.Should().BeNull();
        decision.Destination.Should().Be(recorded);
        decision.DestinationClass.Should().Be(expectedClass);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Subscribes a sink and enters a fresh subject frame; disposing leaves the frame and unsubscribes.</summary>
    private static Observation Observe()
    {
        var id = "egress-twin-" + Guid.NewGuid().ToString("N");
        var sink = new SubjectSink("subject:" + id);
        var subscription = EgressDecisionLog.Subscribe(sink);
        var frame = EgressSubject.Enter(id, new HighWaterMark());
        return new Observation(sink, frame, subscription);
    }

    private sealed class Observation(SubjectSink sink, IDisposable frame, IDisposable subscription) : IDisposable
    {
        public SubjectSink Sink { get; } = sink;

        public void Dispose()
        {
            frame.Dispose();
            subscription.Dispose();
        }
    }

    private sealed class SubjectSink(string basis) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _seen = new();

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        public void Record(EgressDecision decision)
        {
            if (string.Equals(decision.CurrentBasis, basis, StringComparison.Ordinal))
                _seen.Enqueue(decision);
        }
    }

    private static SharedAdaptationEntry Entry(string path = "src/Fix.cs") => new()
    {
        Id = "adapt-" + Guid.NewGuid().ToString("N")[..8],
        Record = new AdaptationRecord
        {
            Id = "rec",
            Timestamp = DateTimeOffset.UtcNow,
            FailureType = "EmptyCatch",
            FixApplied = AdaptationFixType.Source,
        },
        Files = new Dictionary<string, byte[]> { [path] = Encoding.UTF8.GetBytes("// fixed") },
        BroadcastAt = DateTimeOffset.UtcNow,
    };

    private sealed class EmptySync : ISharedAdaptationSync
    {
        public Task<IReadOnlyList<SharedAdaptationEntry>> PullAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SharedAdaptationEntry>>(Array.Empty<SharedAdaptationEntry>());

        public Task<bool> ValidateAndAdoptAsync(SharedAdaptationEntry entry, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class NoOpAdaptationLog : IAdaptationLog
    {
        public Task LogAsync(AdaptationRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AdaptationRecord>> QueryAsync(
            DateTimeOffset? since = null, DateTimeOffset? until = null, string? brickId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AdaptationRecord>>(Array.Empty<AdaptationRecord>());
    }

    private sealed class PassingRegressionRunner : IRegressionTestRunner
    {
        public Task<RegressionTestResult> RunAsync(string projectOrSolutionPath, string? filter = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new RegressionTestResult { AllPassed = true, PassedCount = 1, FailedCount = 0, Summary = "stub" });
    }

    private sealed class PermissiveImmutableCore : IImmutableCoreRegistry
    {
        public IReadOnlyList<string> CoreComponentIds => Array.Empty<string>();

        public bool IsInImmutableCore(string pathOrComponentId) => false;

        public bool IsCoreNamespace(string namespaceOrPath) => false;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();

        public IReadOnlyList<string> Requests => _requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
