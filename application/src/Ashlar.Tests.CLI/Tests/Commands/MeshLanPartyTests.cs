using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.CLI.Commands.BackgroundAgent;
using Ashlar.CLI.Packaging;
using Ashlar.Tests.CLI.Helpers;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// F1+F2, the LAN party: a node SERVES its published signed packages read-only, and a peer PULLS them
/// directly through the same trust-gated import as any other source. These exercise the real
/// HttpListener server and the real HTTP client path on loopback — the wire rules (safe names, size
/// bounds, containment) are pinned on BOTH sides, because a puller must never rely on a server having
/// been polite.
/// </summary>
[Xunit.Collection("MeshIntegration")]
public sealed class MeshLanPartyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _published;
    private readonly string _project;

    public MeshLanPartyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ashlar-lanparty-" + Guid.NewGuid().ToString("N"));
        _published = Path.Combine(_dir, "published");
        _project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(_published);
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task<(MeshServeService Service, int Port, HttpClient Client)> StartServeAsync()
    {
        var port = FreePort();
        var service = new MeshServeService(
            new MeshServeSettings(port, _published, "test-node"),
            NullLogger<MeshServeService>.Instance);
        await service.StartAsync(CancellationToken.None);

        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // Wait for the listener to come up.
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var r = await client.GetAsync($"http://127.0.0.1:{port}/mesh/v1/hello");
                if (r.IsSuccessStatusCode) { return (service, port, client); }
            }
            catch (HttpRequestException) { /* not up yet */ }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("mesh serve did not come up");
    }

    [Fact]
    public async Task Hello_announcesNameVersionAndFingerprintField()
    {
        var (service, port, client) = await StartServeAsync();
        try
        {
            var body = await client.GetStringAsync($"http://127.0.0.1:{port}/mesh/v1/hello");
            body.Should().Contain("test-node").And.Contain(MeshWire.Version).And.Contain("fingerprint");
        }
        finally { await service.StopAsync(CancellationToken.None); client.Dispose(); }
    }

    [Fact]
    public async Task Index_listsPackages_andPkgServesContent_andTraversalIs404()
    {
        await File.WriteAllTextAsync(Path.Combine(_published, "demo-abc123.ashpkg"), "{ \"fake\": true }");
        await File.WriteAllTextAsync(Path.Combine(_published, "._sidecar.ashpkg"), "junk");
        var (service, port, client) = await StartServeAsync();
        try
        {
            var index = await client.GetStringAsync($"http://127.0.0.1:{port}/mesh/v1/index");
            index.Should().Contain("demo-abc123.ashpkg");
            index.Should().NotContain("._sidecar", "unsafe names are not offered");

            var pkg = await client.GetStringAsync($"http://127.0.0.1:{port}/mesh/v1/pkg/demo-abc123.ashpkg");
            pkg.Should().Contain("fake");

            (await client.GetAsync($"http://127.0.0.1:{port}/mesh/v1/pkg/missing.ashpkg")).StatusCode
                .Should().Be(HttpStatusCode.NotFound);
            // A traversal-shaped name must never resolve — the server rejects it (404 by the name/path
            // checks, or 400 where Kestrel refuses the encoded slash outright). Never 200.
            (await client.GetAsync($"http://127.0.0.1:{port}/mesh/v1/pkg/..%2fsecret.ashpkg")).StatusCode
                .Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
            (await client.GetAsync($"http://127.0.0.1:{port}/somewhere/else")).StatusCode
                .Should().Be(HttpStatusCode.NotFound);
        }
        finally { await service.StopAsync(CancellationToken.None); client.Dispose(); }
    }

    [Fact]
    public async Task Index_excludesOversizedPackages()
    {
        var big = new string('x', (int)MeshWire.MaxPackageBytes + 1024);
        await File.WriteAllTextAsync(Path.Combine(_published, "huge-000000.ashpkg"), big);
        var (service, port, client) = await StartServeAsync();
        try
        {
            var index = await client.GetStringAsync($"http://127.0.0.1:{port}/mesh/v1/index");
            index.Should().NotContain("huge-000000", "an oversized package is never offered to the LAN");
        }
        finally { await service.StopAsync(CancellationToken.None); client.Dispose(); }
    }

    // ─────────── #488: the serve path is the sixth reader of a .ashpkg ───────────

    [UnixOnlyFact("ln -s")]
    public async Task Pkg_leafSwappedAfterMetadata_returns404WithoutOutsideBytes()
    {
        var candidate = Path.Combine(_published, "swap-000001.ashpkg");
        var outside = Path.Combine(_dir, "owned-private-marker.txt");
        await File.WriteAllTextAsync(outside, "OUTSIDE_PRIVATE_MARKER");
        // Start empty: hello/index must not consume the controlled race before the GET.
        var (service, port, client) = await StartServeAsync();
        await File.WriteAllTextAsync(candidate, "original-package");
        var swapped = 0;
        SafePackageRead.BeforeOpenForTests = path =>
        {
            if (path != candidate || Interlocked.Exchange(ref swapped, 1) != 0) return;
            File.Delete(candidate);
            File.CreateSymbolicLink(candidate, outside);
        };
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/mesh/v1/pkg/swap-000001.ashpkg");
            swapped.Should().Be(1, "the real endpoint must reach the controlled metadata/open boundary");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("OUTSIDE_PRIVATE_MARKER");
            (await client.GetStringAsync($"http://127.0.0.1:{port}/mesh/v1/index")).Should().NotContain("swap-000001");
        }
        finally
        {
            SafePackageRead.BeforeOpenForTests = null;
            await service.StopAsync(CancellationToken.None);
            client.Dispose();
        }
    }

    [UnixOnlyFact("ln -s")]
    public async Task Pkg_neverFollowsASymlink_soAPlantedLinkIsNeitherServedNorAdvertised()
    {
        // THE ARBITRARY FILE READ. IsSafePackageName constrains the NAME and the containment check
        // constrains the RESOLVED PATH — and Path.GetFullPath does not resolve symlinks, so a link
        // planted inside the published directory satisfies both while pointing anywhere on the host.
        // The old size gate could not see it either: FileInfo.Length on a symlink is the length of
        // the TARGET'S PATH STRING, so a link to a 40 MB file measured about twenty bytes, was
        // advertised in the index at that size, and was then served in full — ten times the
        // documented ceiling. Both endpoints now open through SafePackageRead, which refuses a
        // LinkTarget without following it.
        //
        // Planting is not exotic: the published dir is where MeshStore.Publish writes and where
        // ASHLAR_MESH_AUTOSHARE drops admitted packages unattended, and on a real host it is often a
        // synced folder.
        var secret = Path.Combine(_dir, "secret.txt");
        await File.WriteAllTextAsync(secret, "SHOULD-NEVER-CROSS-THE-WIRE");
        var oversized = Path.Combine(_dir, "oversized-target.bin");
        await using (var fs = new FileStream(oversized, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(MeshWire.MaxPackageBytes * 2);
        }

        await File.WriteAllTextAsync(Path.Combine(_published, "a-real-000001.ashpkg"), "{ \"fake\": true }");
        File.CreateSymbolicLink(Path.Combine(_published, "c-linked-big.ashpkg"), oversized);
        File.CreateSymbolicLink(Path.Combine(_published, "d-secret.ashpkg"), secret);

        var (service, port, client) = await StartServeAsync();
        try
        {
            var index = await client.GetStringAsync($"http://127.0.0.1:{port}/mesh/v1/index");
            index.Should().Contain("a-real-000001.ashpkg", "an honest package is still offered");
            index.Should().NotContain("d-secret", "a symlink is not a package and is never advertised");
            index.Should().NotContain("c-linked-big",
                "advertising a link at the length of its target's path string is how 40 MB was offered as 23 bytes");

            var secretResponse = await client.GetAsync($"http://127.0.0.1:{port}/mesh/v1/pkg/d-secret.ashpkg");
            secretResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "a link out of the published directory is an arbitrary file read, not a package");
            (await secretResponse.Content.ReadAsStringAsync()).Should().NotContain("SHOULD-NEVER-CROSS-THE-WIRE",
                "the target's CONTENT must not reach the peer under any status code");

            var bigResponse = await client.GetAsync($"http://127.0.0.1:{port}/mesh/v1/pkg/c-linked-big.ashpkg");
            bigResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "the ceiling must bound the bytes SERVED, not a number read out of a directory entry");
            (bigResponse.Content.Headers.ContentLength ?? 0).Should().BeLessThan(MeshWire.MaxPackageBytes);

            // The node is still a node: refusing the planted rows must not cost the honest one.
            (await client.GetStringAsync($"http://127.0.0.1:{port}/mesh/v1/pkg/a-real-000001.ashpkg"))
                .Should().Contain("fake");
        }
        finally { await service.StopAsync(CancellationToken.None); client.Dispose(); }
    }

    private static void Mkfifo(string path)
    {
        using var p = Process.Start(new ProcessStartInfo("mkfifo", path) { UseShellExecute = false })!;
        p.WaitForExit(20_000);
        p.ExitCode.Should().Be(0, "the fixture itself must exist before the claim means anything");
    }

    [UnixOnlyFact("mkfifo")]
    public async Task APlantedFifo_isNeitherAdvertisedNorAbleToWedgeTheNode()
    {
        // A FIFO in the published directory used to be a permanent denial of service, not a slow
        // one: Kestrel's SendFileAsync blocks inside open(2) waiting for a writer, and a client
        // disconnect cannot unblock a thread parked in a syscall — so enough concurrent GETs
        // (MaxConcurrentConnections is 100) stop the node answering hello and index too. Its
        // FileInfo.Length is 0, so it also sailed under the size bound and was advertised.
        //
        // SafePackageRead opens with O_NONBLOCK and refuses anything it cannot seek, which is why
        // this test can assert a 404 with a five-second client timeout at all.
        Mkfifo(Path.Combine(_published, "b-hang.ashpkg"));
        await File.WriteAllTextAsync(Path.Combine(_published, "a-real-000002.ashpkg"), "{ \"fake\": true }");

        var (service, port, client) = await StartServeAsync();
        try
        {
            var index = await client.GetStringAsync($"http://127.0.0.1:{port}/mesh/v1/index");
            index.Should().NotContain("b-hang", "a fifo is not a package and is never advertised");
            index.Should().Contain("a-real-000002.ashpkg");

            (await client.GetAsync($"http://127.0.0.1:{port}/mesh/v1/pkg/b-hang.ashpkg")).StatusCode
                .Should().Be(HttpStatusCode.NotFound);

            // The node still answers after the planted GET — the point of the whole exercise.
            (await client.GetStringAsync($"http://127.0.0.1:{port}/mesh/v1/hello")).Should().Contain("test-node");
        }
        finally { await service.StopAsync(CancellationToken.None); client.Dispose(); }
    }

    [Fact]
    public async Task PeerPull_refusesAGarbagePackage_throughTheRealWire()
    {
        await File.WriteAllTextAsync(Path.Combine(_published, "junk-111111.ashpkg"), "{ not a package");
        var (service, port, client) = await StartServeAsync();
        try
        {
            var s = await MeshAutoPullService.PullPeerOnceAsync(client, $"http://127.0.0.1:{port}", _project);

            s.Scanned.Should().Be(1);
            s.Refused.Should().Be(1, "an unopenable package is refused (fail-closed), never applied");
            s.Errors.Should().Be(0);
        }
        finally { await service.StopAsync(CancellationToken.None); client.Dispose(); }
    }

    [Fact]
    public async Task PeerPull_capsPackagesPerPeer()
    {
        for (var i = 0; i < MeshAutoPullService.MaxPackagesPerPeer + 5; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_published, $"p{i:D6}-abcdef.ashpkg"), "{ bad");
        }
        var (service, port, client) = await StartServeAsync();
        try
        {
            var s = await MeshAutoPullService.PullPeerOnceAsync(client, $"http://127.0.0.1:{port}", _project);
            s.Scanned.Should().Be(MeshAutoPullService.MaxPackagesPerPeer, "a peer cannot make one tick dial out unbounded");
        }
        finally { await service.StopAsync(CancellationToken.None); client.Dispose(); }
    }

    [Fact]
    public async Task PeerPull_doesNotFollowRedirects_noSsrf()
    {
        // A hostile peer 302s the index to another path that WOULD yield a package. If the client
        // followed the redirect it would fetch it (Refused=1); not following means Errors=1, Scanned=0.
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync().WaitAsync(cts.Token); } catch { break; }
                var p = c.Request.Url!.AbsolutePath;
                if (p == "/mesh/v1/index")
                {
                    c.Response.StatusCode = 302;
                    c.Response.RedirectLocation = $"http://127.0.0.1:{port}/real-index";
                }
                else if (p == "/real-index")
                {
                    var body = Encoding.UTF8.GetBytes("[{\"File\":\"x-abc123.ashpkg\",\"Size\":5}]");
                    await c.Response.OutputStream.WriteAsync(body);
                }
                else { c.Response.StatusCode = 404; }
                c.Response.Close();
            }
        });

        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var s = await MeshAutoPullService.PullPeerOnceAsync(client, $"http://127.0.0.1:{port}", _project);

        cts.Cancel();
        listener.Stop();
        s.Scanned.Should().Be(0, "a redirected index is a failed fetch, never followed to an internal address");
        s.Errors.Should().Be(1);
    }

    [Fact]
    public async Task PeerPull_offlinePeer_isAnErrorCount_neverAThrow()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var s = await MeshAutoPullService.PullPeerOnceAsync(client, $"http://127.0.0.1:{FreePort()}", _project);
        s.Errors.Should().Be(1, "a guest who left the party is not an incident");
        s.Scanned.Should().Be(0);
    }

    [Fact]
    public async Task PeerPull_rejectsNonHttpUrls()
    {
        using var client = new HttpClient();
        (await MeshAutoPullService.PullPeerOnceAsync(client, "file:///etc/passwd", _project)).Errors.Should().Be(1);
        (await MeshAutoPullService.PullPeerOnceAsync(client, "not a url", _project)).Errors.Should().Be(1);
    }

    // ---- wire rules, pinned directly -----------------------------------------------------------

    [Theory]
    [InlineData("demo-abc123.ashpkg", true)]
    [InlineData("a.ashpkg", true)]
    [InlineData("../evil.ashpkg", false)]
    [InlineData("..\\evil.ashpkg", false)]
    [InlineData(".hidden.ashpkg", false)]
    [InlineData("._sidecar.ashpkg", false)]
    [InlineData("dir/name.ashpkg", false)]
    [InlineData("name.txt", false)]
    [InlineData("", false)]
    public void SafePackageNames(string name, bool ok) =>
        MeshWire.IsSafePackageName(name).Should().Be(ok);

    [Fact]
    public async Task BoundedRead_returnsNullOverTheBound_andTextUnderIt()
    {
        using var small = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        (await MeshWire.ReadBoundedTextAsync(small, 100)).Should().Be("hello");

        using var big = new MemoryStream(new byte[300]);
        (await MeshWire.ReadBoundedTextAsync(big, 200)).Should().BeNull("over the bound is refusal, not truncation");
    }
}
