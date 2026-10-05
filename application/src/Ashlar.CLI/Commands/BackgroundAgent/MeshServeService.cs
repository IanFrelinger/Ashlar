using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ashlar.CLI.Packaging;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Manifest.Signing;

namespace Ashlar.CLI.Commands.BackgroundAgent;

/// <summary>How a node serves its published packages: the port, the read-only published dir, the name
/// it announces, and optional TLS/mTLS for a private fleet (a PEM server cert, and — for mutual TLS —
/// a requirement that clients present a cert the fleet CA signed).</summary>
public sealed record MeshServeSettings(
    int Port, string PublishedDir, string NodeName,
    string? TlsCertPath = null, string? TlsKeyPath = null, bool RequireClientCert = false, string? CaPath = null)
{
    /// <summary>True when a server certificate is configured (endpoint is HTTPS).</summary>
    public bool Tls => !string.IsNullOrWhiteSpace(TlsCertPath) && !string.IsNullOrWhiteSpace(TlsKeyPath);
}

/// <summary>
/// Shared wire rules for the federated mesh (F1): what a package file may be called, how large it may
/// be, and the shapes the peer endpoints exchange. Both the server (<see cref="MeshServeService"/>)
/// and the peer-pull client enforce these INDEPENDENTLY — a hostile or buggy peer on the network must
/// be contained by the client's own checks, never trusted to have applied the server's.
/// </summary>
public static class MeshWire
{
    /// <summary>Protocol tag, so a future incompatible change can be detected instead of guessed.</summary>
    public const string Version = "mesh/v1";

    /// <summary>
    /// Upper bound for one <c>.ashpkg</c> on the wire and on disk. Packages carry source text of a
    /// gated extension — legitimately tens of KB — so 4&#160;MB is generous headroom while still
    /// bounding what a peer can make this node download or buffer.
    ///
    /// <para>ON DISK it is a bound on the BYTES SERVED, not on a number read out of a directory
    /// entry. That sentence was false until the serve path went through
    /// <see cref="Ashlar.CLI.Packaging.SafePackageRead"/>: a symlink's <see cref="FileInfo.Length"/>
    /// is the length of the target's path string, so a link to a 40&#160;MB file measured 23 bytes,
    /// passed this bound, and was then served at 40&#160;MB — ten times the documented ceiling.</para>
    /// </summary>
    public const long MaxPackageBytes = 4 * 1024 * 1024;

    private static readonly Regex SafeName =
        new(@"^[A-Za-z0-9][A-Za-z0-9._-]*\.ashpkg$", RegexOptions.Compiled);

    /// <summary>A bare, traversal-free package file name (what MeshStore.Publish produces).</summary>
    public static bool IsSafePackageName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Contains("..", StringComparison.Ordinal)
        && SafeName.IsMatch(name);

    /// <summary>One row of a peer's package index.</summary>
    public sealed record IndexEntry(string File, long Size);

    /// <summary>Shared JSON options for the index round-trip. Case-insensitive so the client is robust
    /// to either casing on the wire — Kestrel's <c>Results.Json</c> emits camelCase by default.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> from <paramref name="stream"/> and returns the text,
    /// or null when the stream exceeds the bound — the caller treats that as a hostile/oversized
    /// response, never a partial success.
    /// </summary>
    public static async Task<string?> ReadBoundedTextAsync(Stream stream, long maxBytes, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (ms.Length + read > maxBytes)
            {
                return null;
            }
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>
/// F1 of the LAN party: a node SERVES its own published, signed <c>.ashpkg</c> to the network —
/// read-only, over three tiny endpoints — so peers can pull directly from it with no hub, no
/// director, and no shared folder:
/// <list type="bullet">
///   <item><c>GET /mesh/v1/hello</c> — who this node is: name, key fingerprint, package count.</item>
///   <item><c>GET /mesh/v1/index</c> — the published package list (file + size).</item>
///   <item><c>GET /mesh/v1/pkg/{file}</c> — one package's content.</item>
/// </list>
///
/// <para>Built on Kestrel, so the same endpoint serves plaintext HTTP on the LAN or TLS/mTLS for a
/// private fleet (a PEM server cert; optionally requiring a client cert the fleet CA signed) — and
/// Kestrel's connection limits, header timeout, and minimum-response-data-rate give the DoS
/// protections a raw socket loop had to hand-roll: a slow or non-reading client is dropped, not able
/// to wedge serving.</para>
///
/// <para>Safe by construction: the surface is read-only GET, file names are validated against a
/// traversal-free pattern AND resolved-path containment, every file offered or served is opened
/// through <see cref="Ashlar.CLI.Packaging.SafePackageRead"/> — so a symlink, a FIFO, a device or an
/// oversized file is a 404 and never a served byte — and a failed bind is logged and never takes the
/// daemon down. Trust still lives entirely on the RECEIVING side — a peer that pulls from here runs
/// every package through its own trust root and gate; TLS is access control and confidentiality on
/// top, not a substitute for the seal.</para>
///
/// <para>WHAT THIS DIRECTORY IS. The published dir is not a private staging area: MeshStore.Publish
/// writes to it, ASHLAR_MESH_AUTOSHARE writes admitted packages into it unattended
/// (docs/Federation.md), and on a real host it is often a synced folder. "Everything offered is
/// already sealed" describes what this node PUT there, not what is there — so the endpoints check
/// what they are about to serve rather than trusting how it arrived. Issue #488.</para>
///
/// <para>Opt-in (registered only when <c>ASHLAR_MESH_SERVE_PORT</c> is set).</para>
/// </summary>
public sealed class MeshServeService : BackgroundService
{
    private readonly MeshServeSettings _settings;
    private readonly ILogger<MeshServeService> _logger;

    /// <summary>Creates the mesh serve service.</summary>
    public MeshServeService(MeshServeSettings settings, ILogger<MeshServeService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_settings.Port is < 1 or > 65535)
        {
            _logger.LogInformation("Mesh serve disabled (no valid port).");
            return;
        }

        // Fail CLOSED on a half-configured private-fleet TLS setup. A node asked to require client
        // certs — or given only one of cert/key — must NEVER silently fall back to plaintext, which
        // would expose the package index/files to any client with no cert. Refuse to serve instead.
        var configError = ConfigError(_settings);
        if (configError is not null)
        {
            _logger.LogError(
                "Mesh serve refusing to start on :{Port} — {Error} Not serving (fail-closed; no plaintext fallback).",
                _settings.Port, configError);
            return;
        }

        WebApplication app;
        try
        {
            app = BuildApp();
            await app.StartAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A node that cannot serve is still a node — log loud, keep everything else running. A
            // mis-configured cert (unreadable PEM, wrong key) surfaces HERE, not as silent plaintext.
            _logger.LogError(ex, "Mesh serve could not start on :{Port} — serving disabled for this run.", _settings.Port);
            return;
        }

        _logger.LogInformation(
            "Mesh serve armed on :{Port} ({Scheme}) — offering {Dir} to the network (read-only, signed packages; trust is enforced by the puller).",
            _settings.Port, _settings.Tls ? (_settings.RequireClientCert ? "mTLS" : "TLS") : "http", _settings.PublishedDir);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            try { await app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { /* best effort */ }
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns a message when the TLS/mTLS settings are half-specified in a way that must NOT degrade
    /// to plaintext, or null when the config is coherent (including the intended no-TLS LAN default:
    /// cert, key, require, and CA all unset). Pure, so the fail-closed rules are directly testable.
    /// </summary>
    public static string? ConfigError(MeshServeSettings s)
    {
        var certSet = !string.IsNullOrWhiteSpace(s.TlsCertPath);
        var keySet = !string.IsNullOrWhiteSpace(s.TlsKeyPath);
        if (certSet != keySet)
        {
            return "TLS needs BOTH a cert and a key (ASHLAR_MESH_SERVE_TLS_CERT + _TLS_KEY).";
        }
        if (s.RequireClientCert && !s.Tls)
        {
            return "client-cert (mTLS) was required (ASHLAR_MESH_SERVE_REQUIRE_CLIENT_CERT=1) but no server cert/key is set.";
        }
        if (s.RequireClientCert && string.IsNullOrWhiteSpace(s.CaPath))
        {
            return "client-cert (mTLS) was required but no CA (ASHLAR_MESH_SERVE_CA) is set to validate client certs against.";
        }
        return null;
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();   // the daemon owns logging; don't double up
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxConcurrentConnections = 100;
            k.Limits.MaxRequestBodySize = 0;                          // GET only
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            // Kestrel aborts a connection that reads slower than its default MinResponseDataRate —
            // this is the built-in defence against the slow/non-reading client that a raw HttpListener
            // loop had to guard by hand.
            k.ListenAnyIP(_settings.Port, listen =>
            {
                if (_settings.Tls)
                {
                    var serverCert = MeshTls.LoadCertWithKey(_settings.TlsCertPath!, _settings.TlsKeyPath!);
                    listen.UseHttps(https =>
                    {
                        https.ServerCertificate = serverCert;
                        if (_settings.RequireClientCert)
                        {
                            if (string.IsNullOrWhiteSpace(_settings.CaPath))
                            {
                                throw new InvalidOperationException(
                                    "mTLS requires a CA bundle (ASHLAR_MESH_SERVE_CA) to validate client certs against.");
                            }
                            var ca = MeshTls.LoadCaBundle(_settings.CaPath);
                            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                            https.ClientCertificateValidation = (cert, _, _) => MeshTls.ChainsToCa(cert, ca);
                        }
                    });
                }
            });
        });

        var app = builder.Build();

        app.MapGet("/mesh/v1/hello", () =>
        {
            string fingerprint;
            try { fingerprint = OperatorKey.TryLoad()?.Fingerprint ?? "(unsigned)"; }
            catch { fingerprint = "(unsigned)"; }
            return Results.Json(new
            {
                version = MeshWire.Version,
                name = _settings.NodeName,
                fingerprint,
                packages = ListPackages().Count,
            });
        });

        app.MapGet("/mesh/v1/index", () => Results.Json(ListPackages()));

        // ONE DOOR, and this endpoint is the sixth thing behind it. The name check and the
        // containment check below both constrain a PATH; neither says anything about what the path
        // points AT, and Path.GetFullPath does not resolve symlinks. This used to end in
        // `new FileInfo(full).Length > MaxPackageBytes` and `Results.File(full, …)`, which meant a
        // link planted in the published directory was measured at the length of its target's path
        // string and then served in full:
        //
        //   ln -s <a 40 MB file> linked-big.ashpkg   -> advertised at 23 bytes, served at 41,943,040
        //   ln -s /etc/passwd    d-secret.ashpkg     -> ARBITRARY FILE READ over the network
        //   mkfifo               hang.ashpkg         -> one GET blocks in open(2) inside Kestrel's
        //                                               SendFileAsync; a client disconnect cannot
        //                                               unblock it, and 105 of them (the
        //                                               MaxConcurrentConnections limit is 100) wedge
        //                                               the node so hello and index stop answering.
        //
        // SafePackageRead refuses a LinkTarget, refuses anything not seekable / not FILE_TYPE_DISK,
        // opens without ever blocking, and reports the length off the OPENED HANDLE — so the bytes
        // served and the bytes bounded are the same bytes. ASHLAR_MESH_AUTOSHARE writes admitted
        // packages into this very directory, so "nobody would plant a file here" was never the
        // property this rested on. Issue #488.
        app.MapGet("/mesh/v1/pkg/{file}", (string file, HttpContext http) =>
        {
            if (!MeshWire.IsSafePackageName(file))
            {
                return Results.NotFound();
            }
            var full = Path.GetFullPath(Path.Combine(_settings.PublishedDir, file));
            // Containment on the RESOLVED path, independent of the name check.
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(_settings.PublishedDir), StringComparison.Ordinal))
            {
                return Results.NotFound();
            }
            // A refusal is a 404 and never its reason: this is the one caller of the primitive whose
            // output goes to a STRANGER, and the refusals are written for the operator at the
            // console — they name the path, the symlink target, and which gate fired. Telling a peer
            // apart "no such package" from "that one is a symlink to something 40 MB" hands them a
            // probe into the node's filesystem.
            if (!SafePackageRead.TryOpenBounded(full, MeshWire.MaxPackageBytes, out var stream, out _, out _))
            {
                return Results.NotFound();
            }
            // SPEC-007 EG-MESH-03, report-only: the package bytes go to whoever reached the port.
            _ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.MeshServe, "EG-MESH-03", PeerDestination(http.Connection.RemoteIpAddress)));
            // Results.Stream disposes the stream once the response is written; Kestrel streams it
            // under its own connection and data-rate limits.
            return Results.Stream(stream, "application/json");
        });

        return app;
    }

    /// <summary>
    /// The peer a served package goes to, for the egress decision record: <c>tcp://&lt;remote ip&gt;</c>, an
    /// IPv4-mapped address written as IPv4 and an IPv6 address in brackets, so the guard reads it as a URL host
    /// and a loopback peer as inside the host. Never throws; a connection with no remote address is
    /// <c>tcp://unknown</c>.
    /// </summary>
    internal static string PeerDestination(System.Net.IPAddress? ip)
    {
        if (ip is null)
        {
            return "tcp://unknown";
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"tcp://[{ip}]" : $"tcp://{ip}";
    }

    /// <summary>
    /// The packages this node will actually serve, with the size it will actually serve them at.
    ///
    /// <para>Every candidate is OPENED (and closed again — not a byte is read) rather than measured
    /// through <see cref="FileInfo"/>, because a FileInfo length is a claim about a path: it reports
    /// the target's path-string length for a symlink and 0 for a FIFO, so both cleared the 4 MiB
    /// bound and were advertised to the whole LAN. An index row that a peer can rely on has to be
    /// measured the same way the file will later be served.</para>
    /// </summary>
    private List<MeshWire.IndexEntry> ListPackages()
    {
        if (!Directory.Exists(_settings.PublishedDir))
        {
            return [];
        }
        var entries = new List<MeshWire.IndexEntry>();
        foreach (var path in Directory.EnumerateFiles(_settings.PublishedDir, "*.ashpkg"))
        {
            var name = Path.GetFileName(path);
            if (MeshWire.IsSafePackageName(name)
                && SafePackageRead.TryMeasure(path, MeshWire.MaxPackageBytes, out var length))
            {
                entries.Add(new MeshWire.IndexEntry(name, length));
            }
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
        return entries;
    }
}
