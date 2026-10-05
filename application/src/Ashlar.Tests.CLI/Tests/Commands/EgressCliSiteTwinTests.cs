using System.Collections.Concurrent;
using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.BackgroundAgents.Forge;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.CLI.Commands;
using Ashlar.CLI.Commands.BackgroundAgent;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Packaging;
using Ashlar.Manifest.Signing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// SPEC-007 PR 3b, behavioural twin of the CLI's explicit guard calls: the bundle doors (EG-FILE-01 native,
/// EG-FILE-02 cloud), the pkg verbs (EG-MESH-02 export, EG-MESH-01 publish and share), the mesh discovery beacon
/// (EG-MESH-05) and the mesh serve endpoint (EG-MESH-03).
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> <c>NativeBundle.Stage</c> and <c>CloudBundle.Stage</c> each record one
/// <c>file.export</c> decision naming the bundle directory, under their own site, before the app is staged into it;
/// <c>StageApp</c> refuses a null site as its own argument contract, before it decides or writes anything. Each pkg
/// verb, run through the real command line, records one decision for where the package goes (the <c>--out</c> file,
/// or the <c>--store</c> directory), before the package is written there. The discovery loop decides once,
/// before its sender exists: <c>udp://239.7.42.1:&lt;port&gt;</c> (NetworkExport) when it announces, and
/// <c>host:listen-only</c> (Host) when it does not, so a node that never sends records nothing outside the host.
/// <c>MeshServeService.PeerDestination</c> writes the peer as a URL host the guard can read (IPv4-mapped peers as
/// IPv4, IPv6 in brackets), and a package served to a loopback peer records one Host decision, while a 404 records
/// none.</para>
/// <para><b>Isolation.</b> Each case enters its own <see cref="EgressSubject"/> frame and keeps only decisions
/// whose basis is that frame, except the live serve case: the decision is made on Kestrel's request thread, where no
/// frame flows, so it keeps decisions by site. The frame is an <c>AsyncLocal</c>, so it flows across <c>await</c>s
/// into the command handlers. Sinks are called synchronously inside <c>Evaluate</c>, so a sink can look at the file
/// system at the moment of the decision; that is how the bundle and pkg cases show the guard comes first. This class
/// is in the serial <c>MeshIntegration</c> collection, which holds every test that starts a mesh server, so no other
/// EG-MESH-03 decision can be made while it runs. The discovery loop is driven directly, with a token that is already
/// cancelled, so the guard runs, the loop builds its sender, and it stops before any datagram is sent or the timer
/// waits; nothing joins a multicast group.</para>
/// <para><b>The pkg fixture.</b> Each pkg case builds a project holding one admitted, signed and applied extension,
/// with a fresh operator key in a temporary directory. <c>pkg publish</c> takes the package sealed with that key and
/// needs no key of its own. <c>pkg export</c> and <c>pkg share</c> load the operator key from <c>ASHLAR_KEY_DIR</c>,
/// with no seam, so those two cases set it and restore it; the serial collection is what makes that safe. Every store
/// is passed with <c>--store</c>, never resolved from the operator's environment.</para>
/// </remarks>
[Collection("MeshIntegration")]
public sealed class EgressCliSiteTwinTests : IDisposable
{
    private readonly string _dir;

    public EgressCliSiteTwinTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "egress-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------------------------------------------------
    // EG-MESH-03: the peer a served package goes to
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "tcp://unknown")]
    [InlineData("::ffff:10.0.0.5", "tcp://10.0.0.5")]
    [InlineData("::1", "tcp://[::1]")]
    [InlineData("192.168.1.5", "tcp://192.168.1.5")]
    [InlineData("127.0.0.1", "tcp://127.0.0.1")]
    [InlineData("2001:db8::5", "tcp://[2001:db8::5]")]
    public void PeerDestination_writes_the_peer_as_a_URL_host(string? address, string expected)
    {
        var ip = address is null ? null : IPAddress.Parse(address);

        MeshServeService.PeerDestination(ip).Should().Be(expected);
    }

    [Fact]
    public async Task A_package_served_to_a_loopback_peer_records_one_Host_decision_and_a_404_records_none()
    {
        var published = Directory.CreateDirectory(Path.Combine(_dir, "published")).FullName;
        await File.WriteAllTextAsync(Path.Combine(published, "twin-abc123.ashpkg"), "{ \"twin\": true }");
        var sink = new SiteSink("EG-MESH-03");
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var port = FreePort();
        var service = new MeshServeService(new MeshServeSettings(port, published, "twin-node"), NullLogger<MeshServeService>.Instance);
        await service.StartAsync(CancellationToken.None);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            await WaitForHelloAsync(client, port);

            (await client.GetAsync($"http://127.0.0.1:{port}/mesh/v1/pkg/missing.ashpkg")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            sink.Seen.Should().BeEmpty("a refused or missing package is not served, so nothing is decided");

            var body = await client.GetStringAsync($"http://127.0.0.1:{port}/mesh/v1/pkg/twin-abc123.ashpkg");

            body.Should().Contain("twin", "the guard records; it does not change what is served");
            var decision = sink.Seen.Should().ContainSingle().Which;
            decision.Family.Should().Be(EgressFamilies.MeshServe);
            decision.Destination.Should().Be("tcp://127.0.0.1");
            decision.DestinationClass.Should().Be(EgressDestinationClass.Host);
            decision.Fault.Should().BeNull();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // EG-FILE-01 and EG-FILE-02: the bundle doors
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void NativeBundle_Stage_records_EG_FILE_01_for_the_bundle_directory()
    {
        var project = Scaffold();
        var bundle = Path.Combine(_dir, "out-native");
        using var observed = Observe(writtenYet: () => Directory.Exists(Path.Combine(bundle, "app")));

        NativeBundle.Stage(project, bundle, NativeBundle.Describe(project, "linux-x64"));

        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-FILE-01");
        decision.Family.Should().Be(EgressFamilies.FileExport);
        decision.Destination.Should().Be("file:" + bundle);
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        observed.Sink.WrittenAtDecision.Should().Equal(new[] { false }, "the guard decides before the app is staged");
        Directory.Exists(Path.Combine(bundle, "app")).Should().BeTrue();
    }

    [Fact]
    public void CloudBundle_Stage_records_EG_FILE_02_for_the_bundle_directory()
    {
        var project = Scaffold();
        var bundle = Path.Combine(_dir, "out-cloud");
        using var observed = Observe(writtenYet: () => Directory.Exists(Path.Combine(bundle, "app")));

        CloudBundle.Stage(project, bundle, NativeBundle.Describe(project, "aws"), CloudTarget.Aws);

        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-FILE-02");
        decision.Family.Should().Be(EgressFamilies.FileExport);
        decision.Destination.Should().Be("file:" + bundle);
        observed.Sink.WrittenAtDecision.Should().Equal(new[] { false }, "the guard decides before the app is staged");
        Directory.Exists(Path.Combine(bundle, "app")).Should().BeTrue();
    }

    [Fact]
    public void StageApp_refuses_a_null_site_before_it_decides_or_writes_anything()
    {
        var project = Scaffold();
        var bundle = Path.Combine(_dir, "out-null-site");
        using var observed = Observe();

        var act = () => NativeBundle.StageApp(project, bundle, null!);

        act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("site");
        observed.Sink.Seen.Should().BeEmpty("the argument contract is checked before the guard");
        Directory.Exists(bundle).Should().BeFalse("nothing is staged for a call that breaks the contract");
    }

    // ---------------------------------------------------------------------------------------------------------
    // EG-MESH-02 and EG-MESH-01: the pkg verbs
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pkg_export_records_EG_MESH_02_for_the_out_file_before_it_is_written()
    {
        var admitted = await AdmittedProjectAsync();
        var outFile = Path.GetFullPath(Path.Combine(_dir, "exported.ashpkg"));
        using var key = new KeyDirScope(admitted.KeyDir);
        using var observed = Observe(writtenYet: () => File.Exists(outFile));

        var (code, stderr) = await PkgAsync("pkg", "export", "--id", admitted.Id, "--out", outFile, "--path", admitted.Project);

        code.Should().Be(0, stderr);
        File.Exists(outFile).Should().BeTrue();
        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MESH-02");
        decision.Family.Should().Be(EgressFamilies.FileExport);
        decision.Destination.Should().Be("file:" + outFile);
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        decision.Fault.Should().BeNull();
        observed.Sink.WrittenAtDecision.Should().Equal(new[] { false }, "the guard decides before the package is written");
    }

    [Fact]
    public async Task Pkg_publish_records_EG_MESH_01_for_the_store_before_the_package_lands_there()
    {
        var admitted = await AdmittedProjectAsync();
        var package = Path.Combine(_dir, "sealed.ashpkg");
        await File.WriteAllTextAsync(package, ExtensionPackaging.Pack(admitted.Record, [admitted.Packaged], admitted.Signer));
        var store = Path.GetFullPath(Path.Combine(_dir, "store-publish"));
        using var observed = Observe(writtenYet: () => HasPackage(store));

        var (code, stderr) = await PkgAsync("pkg", "publish", package, "--store", store);

        code.Should().Be(0, stderr);
        Directory.EnumerateFiles(store, "*.ashpkg").Should().ContainSingle();
        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MESH-01");
        decision.Family.Should().Be(EgressFamilies.MeshPublish);
        decision.Destination.Should().Be("file:" + store, "the decision names the store, not the package handed in");
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        decision.Fault.Should().BeNull();
        observed.Sink.WrittenAtDecision.Should().Equal(new[] { false }, "the guard decides before the package is placed");
    }

    [Fact]
    public async Task Pkg_share_records_EG_MESH_01_for_the_store_before_the_package_lands_there()
    {
        var admitted = await AdmittedProjectAsync();
        var store = Path.GetFullPath(Path.Combine(_dir, "store-share"));
        using var key = new KeyDirScope(admitted.KeyDir);
        using var observed = Observe(writtenYet: () => HasPackage(store));

        var (code, stderr) = await PkgAsync("pkg", "share", "--id", admitted.Id, "--store", store, "--path", admitted.Project);

        code.Should().Be(0, stderr);
        Directory.EnumerateFiles(store, "*.ashpkg").Should().ContainSingle();
        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MESH-01");
        decision.Family.Should().Be(EgressFamilies.MeshPublish);
        decision.Destination.Should().Be("file:" + store);
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        decision.Fault.Should().BeNull();
        observed.Sink.WrittenAtDecision.Should().Equal(new[] { false }, "the guard decides before the package is placed");
    }

    // ---------------------------------------------------------------------------------------------------------
    // EG-MESH-05: the discovery beacon
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_announcing_discovery_loop_records_one_NetworkExport_decision_for_its_group_and_port()
    {
        var port = Random.Shared.Next(21000, 39000);
        var service = Discovery(port, fingerprint: "ed25519:aaaa1111bbbb2222", servePort: 17001);
        using var observed = Observe();

        await RunAnnounceLoopOnceAsync(service, announcing: true);

        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MESH-05");
        decision.Family.Should().Be(EgressFamilies.MeshDiscovery);
        decision.Destination.Should().Be($"udp://239.7.42.1:{port}");
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
    }

    [Fact]
    public async Task A_listen_only_discovery_loop_records_one_Host_decision_and_never_names_the_group()
    {
        var port = Random.Shared.Next(21000, 39000);
        var service = Discovery(port, fingerprint: null, servePort: null);
        using var observed = Observe();

        await RunAnnounceLoopOnceAsync(service, announcing: false);

        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MESH-05");
        decision.Family.Should().Be(EgressFamilies.MeshDiscovery);
        decision.Destination.Should().Be("host:listen-only");
        decision.DestinationClass.Should().Be(EgressDestinationClass.Host);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private MeshDiscoveryService Discovery(int port, string? fingerprint, int? servePort) => new(
        new MeshDiscoverySettings("twin-node", fingerprint, servePort, Path.Combine(_dir, "state-" + port), port),
        new MeshDiscoveryRegistry(),
        NullLogger<MeshDiscoveryService>.Instance);

    /// <summary>
    /// Runs the private announce loop with a token that is already cancelled: the guard (its first statement) runs,
    /// the sender is built, and the loop ends at its first send or timer wait.
    /// </summary>
    private static async Task RunAnnounceLoopOnceAsync(MeshDiscoveryService service, bool announcing)
    {
        var loop = typeof(MeshDiscoveryService).GetMethod("AnnounceLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        loop.Should().NotBeNull("the discovery service's announce loop is where EG-MESH-05 is decided");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var task = (Task)loop!.Invoke(service, [announcing, cancelled.Token])!;
        await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private string Scaffold()
    {
        var project = Directory.CreateDirectory(Path.Combine(_dir, "project-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        ProjectScaffold.TryScaffold("triage", out var manifest, out var policy, out var reason).Should().BeTrue(reason);
        File.WriteAllText(Path.Combine(project, "ashlar.yaml"), manifest);
        File.WriteAllText(Path.Combine(project, "ashlar.policy.yaml"), policy);
        return project;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task WaitForHelloAsync(HttpClient client, int port)
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var hello = await client.GetAsync($"http://127.0.0.1:{port}/mesh/v1/hello");
                if (hello.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // not up yet
            }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("mesh serve did not come up");
    }

    /// <summary>
    /// A project holding one admitted, signed and applied extension (one parked forge write), the key directory
    /// whose operator key signed it, and the package file that write becomes.
    /// </summary>
    private async Task<AdmittedProject> AdmittedProjectAsync()
    {
        var project = Scaffold();
        var keyDir = Path.Combine(_dir, "keys-" + Guid.NewGuid().ToString("N")[..8]);
        var signer = OperatorKey.Generate(keyDir);
        var forge = AshlarProjectMediation.ProjectStore(project);
        var write = forge.Add(new ChangeProposal
        {
            Id = "forge-" + Guid.NewGuid().ToString("N")[..8],
            TargetPath = "src/Twin.cs",
            NewContent = "// twin",
            Summary = "parked by the cycle",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var proposal = SelfExtendAdmissionBridge.BuildProposal("night-agent", "co-produce", [], 1, 0, [write.Id], forge);
        var record = await new GateStore(Path.Combine(project, ".ashlar"), signer).RecordAsync(
            proposal, new AdmissionOutcome { State = ProposalState.Admitted, Reason = "within budget" }, DateTimeOffset.UtcNow);
        ForgeApplier.ApplyAll(forge, [write.Id], project, "gate");
        return new AdmittedProject(
            project, keyDir, record, new PackageFile { Path = write.TargetPath, Content = write.NewContent }, signer);
    }

    private sealed record AdmittedProject(string Project, string KeyDir, GateRecord Record, PackageFile Packaged, SigningIdentity Signer)
    {
        public string Id => Record.Proposal.Id;
    }

    private static bool HasPackage(string store) =>
        Directory.Exists(store) && Directory.EnumerateFiles(store, "*.ashpkg").Any();

    /// <summary>Runs the real <c>pkg</c> command line, as <c>PkgCommandTests</c> does; returns the exit code and stderr.</summary>
    private static async Task<(int Code, string Stderr)> PkgAsync(params string[] args)
    {
        var stdout = new StringWriter(); // not disposed: a disposed writer left on Console poisons later tests
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var root = new RootCommand();
            root.AddCommand(new PkgCommand());
            var code = await root.InvokeAsync(args);
            return (code, stderr.ToString());
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }
    }

    /// <summary>
    /// Points <c>ASHLAR_KEY_DIR</c> at a temporary key directory and restores the previous value. <c>pkg export</c>
    /// and <c>pkg share</c> call <c>OperatorKey.TryLoad()</c> with no directory, so this is their only seam.
    /// </summary>
    private sealed class KeyDirScope : IDisposable
    {
        private const string Name = "ASHLAR_KEY_DIR";
        private readonly string? _previous = Environment.GetEnvironmentVariable(Name);

        public KeyDirScope(string keyDir) => Environment.SetEnvironmentVariable(Name, keyDir);

        public void Dispose() => Environment.SetEnvironmentVariable(Name, _previous);
    }

    /// <summary>
    /// Subscribes a sink and enters a fresh subject frame; disposing leaves the frame and unsubscribes. When
    /// <paramref name="writtenYet"/> is given, the sink calls it at each decision, inside <c>Evaluate</c>.
    /// </summary>
    private static Observation Observe(Func<bool>? writtenYet = null)
    {
        var id = "egress-twin-" + Guid.NewGuid().ToString("N");
        var sink = new BasisSink("subject:" + id, writtenYet);
        var subscription = EgressDecisionLog.Subscribe(sink);
        var frame = EgressSubject.Enter(id, new HighWaterMark());
        return new Observation(sink, frame, subscription);
    }

    private sealed class Observation(BasisSink sink, IDisposable frame, IDisposable subscription) : IDisposable
    {
        public BasisSink Sink { get; } = sink;

        public void Dispose()
        {
            frame.Dispose();
            subscription.Dispose();
        }
    }

    private sealed class BasisSink(string basis, Func<bool>? writtenYet) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _seen = new();
        private readonly ConcurrentQueue<bool> _writtenAtDecision = new();

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        /// <summary>What the probe saw at each kept decision, in order; empty when there is no probe.</summary>
        public IReadOnlyList<bool> WrittenAtDecision => _writtenAtDecision.ToArray();

        public void Record(EgressDecision decision)
        {
            if (!string.Equals(decision.CurrentBasis, basis, StringComparison.Ordinal))
                return;
            if (writtenYet is not null)
                _writtenAtDecision.Enqueue(writtenYet());
            _seen.Enqueue(decision);
        }
    }

    private sealed class SiteSink(string site) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _seen = new();

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        public void Record(EgressDecision decision)
        {
            if (string.Equals(decision.Site, site, StringComparison.Ordinal))
                _seen.Enqueue(decision);
        }
    }
}
