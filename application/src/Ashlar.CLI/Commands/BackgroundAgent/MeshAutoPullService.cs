using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.CLI.Packaging;

namespace Ashlar.CLI.Commands.BackgroundAgent;

/// <summary>How a mesh auto-pull node is configured: an optional shared folder to pull trusted
/// <c>.ashpkg</c> from, optional PEER base URLs to pull from directly (F2, the LAN party), the
/// project whose gate/policy decides them, and the poll interval.</summary>
public sealed record MeshAutoPullSettings(
    string PullDir, string ProjectDir, int IntervalSeconds, IReadOnlyList<string>? Peers = null);

/// <summary>The outcome of one pull pass, aggregated across every package scanned.</summary>
public sealed record MeshPullSummary(
    int Scanned, int Admitted, int Held, int Rejected, int Refused, int AlreadyImported, int Errors)
{
    /// <summary>Nothing to pull — the dir was absent or empty.</summary>
    public static MeshPullSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>Component-wise sum, so a tick can aggregate the folder pass and every peer pass.</summary>
    public static MeshPullSummary operator +(MeshPullSummary a, MeshPullSummary b) => new(
        a.Scanned + b.Scanned, a.Admitted + b.Admitted, a.Held + b.Held, a.Rejected + b.Rejected,
        a.Refused + b.Refused, a.AlreadyImported + b.AlreadyImported, a.Errors + b.Errors);
}

/// <summary>
/// A5 cross-machine sharing (consumer side): a hosted service that, on a timer, pulls TRUSTED signed
/// <c>.ashpkg</c> packages a peer published into a shared folder and submits each through the SAME
/// receiver-sovereign import path a manual <c>ashlar pkg import</c> uses — so how a package arrived
/// never changes how it is admitted. The Phase-3 trust root (a package sealed by an untrusted key is
/// refused before anything parks), the local policy's admission decision (a <c>proposing</c> consumer
/// HOLDS imported code for review — the safe cross-machine default), and the append-once dedupe all
/// come for free from <see cref="PackageImport.SubmitAsync"/>; this only supplies the timer + directory
/// scan the roadmap called the one missing piece.
///
/// <para>Opt-in and fail-closed: it is registered only when a pull dir is configured
/// (<c>ASHLAR_MESH_PULL_DIR</c>), an absent dir is a no-op (not an error), and a per-tick failure is
/// logged and retried next interval — it never crashes the daemon. It builds on signed <c>.ashpkg</c>
/// ONLY; the unsigned <c>.nxpkg</c> sneakernet path is deliberately untouched.</para>
/// </summary>
public sealed class MeshAutoPullService : BackgroundService
{
    private readonly MeshAutoPullSettings _settings;
    private readonly ILogger<MeshAutoPullService> _logger;

    /// <summary>One shared client for peer pulls. A slow peer times out instead of stalling the tick,
    /// and auto-redirect is OFF: an untrusted peer must not be able to bounce this node's request to an
    /// internal/link-local address (a 3xx is treated as a failed fetch, not followed). For a private
    /// mTLS fleet, ASHLAR_MESH_CLIENT_CERT/_KEY present this node's cert and ASHLAR_MESH_CA pins the
    /// fleet CA for validating https peers — applied only to TLS connections, http peers are unchanged.</summary>
    private static readonly HttpClient Http = BuildHttp();

    private static HttpClient BuildHttp()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        var clientCert = Environment.GetEnvironmentVariable("ASHLAR_MESH_CLIENT_CERT");
        var clientKey = Environment.GetEnvironmentVariable("ASHLAR_MESH_CLIENT_KEY");
        var ca = Environment.GetEnvironmentVariable("ASHLAR_MESH_CA");
        if (!string.IsNullOrWhiteSpace(clientCert) && !string.IsNullOrWhiteSpace(clientKey))
        {
            handler.SslOptions.ClientCertificates =
                new System.Security.Cryptography.X509Certificates.X509CertificateCollection { MeshTls.LoadCertWithKey(clientCert, clientKey) };
        }
        if (!string.IsNullOrWhiteSpace(ca))
        {
            var caBundle = MeshTls.LoadCaBundle(ca);
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, cert, _, _) => MeshTls.ChainsToCa(MeshTls.AsCert2(cert), caBundle);
        }
        var http = EgressHttp.CreateClient(handler, EgressFamilies.MeshPull, "EG-MESH-04");
        http.Timeout = TimeSpan.FromSeconds(15);
        return http;
    }

    /// <summary>Cap on packages fetched from one peer per tick, so a peer serving a huge index cannot
    /// turn one tick into an unbounded sequential dial-out that starves honest peers.</summary>
    public const int MaxPackagesPerPeer = 64;

    /// <summary>Creates the mesh auto-pull service. <paramref name="peerSources"/> is the strategy
    /// seam: every registered source (configured, multicast, a future tailnet or rendezvous source)
    /// contributes peer addresses each tick, and all of them feed the same trust-gated pull.</summary>
    public MeshAutoPullService(
        MeshAutoPullSettings settings, ILogger<MeshAutoPullService> logger,
        IEnumerable<IPeerSource>? peerSources = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _peerSources = (peerSources ?? []).ToList();
    }

    private readonly IReadOnlyList<IPeerSource> _peerSources;

    /// <summary>Peers pulled per tick are capped so a beacon-spammer cannot turn one tick into an
    /// unbounded dial-out; the trust gate already makes each individual pull safe.</summary>
    public const int MaxPeersPerTick = 16;

    /// <summary>
    /// The tick's peer list: configured settings peers ∪ every source's current peers, trimmed,
    /// de-duplicated (case-insensitive), capped at <see cref="MaxPeersPerTick"/>. Static and pure for
    /// direct testing.
    /// </summary>
    public static IReadOnlyList<string> MergePeerUrls(
        IReadOnlyList<string>? configured, IEnumerable<IPeerSource>? sources)
    {
        var urls = new List<string>(configured ?? []);
        foreach (var source in sources ?? [])
        {
            try { urls.AddRange(source.CurrentPeerBaseUrls()); }
            catch { /* one broken source never blocks the others */ }
        }
        return urls
            .Select(u => u?.Trim() ?? string.Empty)
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPeersPerTick)
            .ToList();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hasDir = !string.IsNullOrWhiteSpace(_settings.PullDir);
        var peers = _settings.Peers ?? Array.Empty<string>();
        if (_settings.IntervalSeconds <= 0 || (!hasDir && peers.Count == 0 && _peerSources.Count == 0))
        {
            _logger.LogInformation("Mesh auto-pull disabled (no pull dir, no peers, no peer sources, or non-positive interval).");
            return;
        }

        _logger.LogInformation(
            "Mesh auto-pull armed: {Dir} + {PeerCount} configured peer(s) + sources [{Sources}] every {Interval}s → project {Project}. Only signers this node trusts are admitted.",
            hasDir ? _settings.PullDir : "(no folder)", peers.Count,
            string.Join(", ", _peerSources.Select(s => s.Describe())), _settings.IntervalSeconds, _settings.ProjectDir);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.IntervalSeconds));
        try
        {
            // Pull once at startup, then on each tick — a node that just came up should not wait a
            // whole interval to pick up what a peer already published.
            do
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            var s = MeshPullSummary.Empty;
            if (!string.IsNullOrWhiteSpace(_settings.PullDir))
            {
                s += await PullOnceAsync(_settings.PullDir, _settings.ProjectDir, ct).ConfigureAwait(false);
            }
            foreach (var peer in MergePeerUrls(_settings.Peers, _peerSources))
            {
                ct.ThrowIfCancellationRequested();
                s += await PullPeerOnceAsync(Http, peer, _settings.ProjectDir, ct, _logger).ConfigureAwait(false);
            }
            if (s.Scanned > 0 || s.Errors > 0)
            {
                _logger.LogInformation(
                    "Mesh auto-pull: scanned {Scanned} — {Admitted} admitted, {Held} held (awaiting review), "
                    + "{Rejected} rejected, {Refused} refused (trust or egress policy), {Already} already decided, {Errors} error(s).",
                    s.Scanned, s.Admitted, s.Held, s.Rejected, s.Refused, s.AlreadyImported, s.Errors);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A pull pass must never take the daemon down — say so and retry next interval.
            _logger.LogWarning(ex, "Mesh auto-pull tick failed — retrying next interval.");
        }
    }

    /// <summary>
    /// One pull pass: enumerate <c>*.ashpkg</c> in <paramref name="pullDir"/> (skipping dotfiles and
    /// macOS AppleDouble sidecars) and submit each through <see cref="PackageImport.SubmitAsync"/>,
    /// aggregating the outcomes. A per-file failure counts as an error and does not stop the pass.
    /// Static and side-effect-scoped so it is directly testable without the timer.
    /// </summary>
    public static async Task<MeshPullSummary> PullOnceAsync(string pullDir, string projectDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pullDir) || !Directory.Exists(pullDir))
        {
            return MeshPullSummary.Empty;
        }

        var files = Directory.EnumerateFiles(pullDir, "*.ashpkg")
            .Where(f => !Path.GetFileName(f).StartsWith('.'))   // dotfiles + AppleDouble (._x.ashpkg)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        int admitted = 0, held = 0, rejected = 0, refused = 0, already = 0, errors = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // INSIDE the try, unlike the FileInfo.Length guard it replaces: that one sat outside
                // and so a single unreadable file aborted the daemon's WHOLE pass, the same
                // whole-pass abort the CLI's pull loop just had fixed. And FileInfo.Length was never
                // a bound to begin with — it reports 0 for a FIFO or a symlink to /dev/zero, and the
                // length of the target PATH for a symlink to a real file, so the unattended daemon
                // shared the CLI's hole exactly. One door for both: SafePackageRead never blocks and
                // never buffers past the bound, local or remote.
                var read = await SafePackageRead.TryReadTextAsync(file, MeshWire.MaxPackageBytes, ct)
                    .ConfigureAwait(false);
                if (!read.Ok)
                {
                    errors++;   // refused, never buffered — local or remote, same bound
                    continue;
                }
                var result = await PackageImport.SubmitAsync(projectDir, read.Text!).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case PackageAdmission.Admitted: admitted++; break;
                    case PackageAdmission.Held: held++; break;
                    case PackageAdmission.Rejected: rejected++; break;
                    case PackageAdmission.Refused: refused++; break;
                    case PackageAdmission.AlreadyImported: already++; break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                errors++;
            }
        }

        return new MeshPullSummary(files.Count, admitted, held, rejected, refused, already, errors);
    }

    /// <summary>
    /// One pull pass against a PEER (F2, the LAN party): fetch its <c>/mesh/v1/index</c>, download each
    /// package (bounded — a hostile or buggy peer cannot make this node buffer more than
    /// <see cref="MeshWire.MaxPackageBytes"/>), and submit every one through the SAME trust-gated
    /// import as a folder pull. The network is transport, the seal is the trust: an untrusted signer
    /// is refused before anything parks, exactly as if the file had arrived on a USB stick.
    /// A peer that is offline, slow, or malformed yields an error count — never an exception; a
    /// LAN-party guest that left the room is not an incident.
    /// </summary>
    public static async Task<MeshPullSummary> PullPeerOnceAsync(
        HttpClient http, string peerBaseUrl, string projectDir, CancellationToken ct = default)
        => await PullPeerOnceAsync(http, peerBaseUrl, projectDir, ct, null).ConfigureAwait(false);

    internal static async Task<MeshPullSummary> PullPeerOnceAsync(
        HttpClient http, string peerBaseUrl, string projectDir, CancellationToken ct, ILogger? logger)
    {
        if (!Uri.TryCreate(peerBaseUrl?.Trim(), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return MeshPullSummary.Empty with { Errors = 1 };
        }

        List<MeshWire.IndexEntry> entries;
        try
        {
            using var idxResp = await http.GetAsync(new Uri(baseUri, "/mesh/v1/index"),
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!idxResp.IsSuccessStatusCode)
            {
                return MeshPullSummary.Empty with { Errors = 1 };
            }
            var idxJson = await MeshWire.ReadBoundedTextAsync(
                await idxResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                MeshWire.MaxPackageBytes, ct).ConfigureAwait(false);
            entries = idxJson is null
                ? null!
                : JsonSerializer.Deserialize<List<MeshWire.IndexEntry>>(idxJson, MeshWire.JsonOptions) ?? [];
            if (entries is null)
            {
                return MeshPullSummary.Empty with { Errors = 1 };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is not null)
        {
            LogRefusal(logger, EgressRefusal.Find(ex)!);
            return MeshPullSummary.Empty with { Refused = 1 };
        }
        catch (Exception)
        {
            return MeshPullSummary.Empty with { Errors = 1 };   // peer offline / timeout / bad index
        }

        // Bound the per-peer work: a hostile peer's 4MB index could name ~100k packages; take only
        // the first MaxPackagesPerPeer so one peer cannot monopolise the tick.
        var capped = entries.Count > MaxPackagesPerPeer;
        int admitted = 0, held = 0, rejected = 0, refused = 0, already = 0, errors = capped ? 1 : 0;
        foreach (var entry in entries.Take(MaxPackagesPerPeer))
        {
            ct.ThrowIfCancellationRequested();
            // The client re-checks the wire rules independently — a peer's index is DATA, not a promise.
            if (entry is null || !MeshWire.IsSafePackageName(entry.File) || entry.Size > MeshWire.MaxPackageBytes)
            {
                errors++;
                continue;
            }
            try
            {
                using var pkgResp = await http.GetAsync(
                    new Uri(baseUri, "/mesh/v1/pkg/" + Uri.EscapeDataString(entry.File)),
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!pkgResp.IsSuccessStatusCode)
                {
                    errors++;
                    continue;
                }
                var json = await MeshWire.ReadBoundedTextAsync(
                    await pkgResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                    MeshWire.MaxPackageBytes, ct).ConfigureAwait(false);
                if (json is null)
                {
                    errors++;   // response exceeded the bound — hostile or corrupt, never buffered
                    continue;
                }
                var result = await PackageImport.SubmitAsync(projectDir, json).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case PackageAdmission.Admitted: admitted++; break;
                    case PackageAdmission.Held: held++; break;
                    case PackageAdmission.Rejected: rejected++; break;
                    case PackageAdmission.Refused: refused++; break;
                    case PackageAdmission.AlreadyImported: already++; break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (EgressRefusal.Find(ex) is not null)
            {
                LogRefusal(logger, EgressRefusal.Find(ex)!);
                refused++;
            }
            catch (Exception)
            {
                errors++;
            }
        }

        return new MeshPullSummary(Math.Min(entries.Count, MaxPackagesPerPeer), admitted, held, rejected, refused, already, errors);
    }

    private static void LogRefusal(ILogger? logger, EgressRefusedException refusal)
    {
        if (logger is not null)
            logger.LogWarning(new EventId(7307, "EgressRefused"), "Mesh pull egress refused: {Refusal}", refusal.Message);
        else
            Console.Error.WriteLine($"Mesh pull egress refused: {refusal.Message}");
    }
}
