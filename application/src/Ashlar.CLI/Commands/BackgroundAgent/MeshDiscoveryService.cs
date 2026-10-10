using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ashlar.Manifest.Signing;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.CLI.Commands.BackgroundAgent;

/// <summary>A peer heard on the local network: what it announced, where it announced from, and when.</summary>
public sealed record DiscoveredPeer(string Name, string Fingerprint, string Address, int Port, DateTimeOffset LastSeenUtc)
{
    /// <summary>The peer's mesh-serve base URL — the address the datagram actually CAME from (never
    /// self-reported), plus the port it announced.</summary>
    public string BaseUrl => $"http://{Address}:{Port}";
}

/// <summary>
/// The F3 beacon: the tiny datagram a node multicasts to say "I'm at the party" — protocol version,
/// name, key fingerprint, and the port it serves packages on. Everything received is UNTRUSTED
/// network input: parsing is bounded and every field validated, and nothing a beacon says grants any
/// trust — it only nominates an address to PULL from, and pulling is already fail-closed end to end.
/// </summary>
public static class MeshBeacon
{
    /// <summary>Administratively-scoped multicast group + port for mesh discovery.</summary>
    public static readonly IPAddress Group = IPAddress.Parse("239.7.42.1");

    /// <summary>Default discovery port (settings can override, e.g. for test isolation).</summary>
    public const int DefaultPort = 7421;

    /// <summary>Announce cadence.</summary>
    public static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(15);

    /// <summary>A peer silent for this long has left the party.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);

    /// <summary>Largest datagram considered; anything bigger is ignored unparsed.</summary>
    public const int MaxDatagramBytes = 512;

    private const int MaxNameLength = 64;

    /// <summary>Wire shape (short keys — this rides UDP).</summary>
    public sealed record Payload(
        [property: JsonPropertyName("v")] string V,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("fp")] string Fp,
        [property: JsonPropertyName("port")] int Port);

    /// <summary>Encodes an announcement.</summary>
    public static byte[] Encode(string name, string fingerprint, int servePort) =>
        JsonSerializer.SerializeToUtf8Bytes(new Payload(MeshWire.Version, name, fingerprint, servePort));

    /// <summary>
    /// Parses an untrusted datagram. False for anything oversized, malformed, mis-versioned, or with
    /// an invalid name/fingerprint/port — a hostile beacon is simply not heard.
    /// </summary>
    public static bool TryParse(byte[] datagram, out Payload payload)
    {
        payload = null!;
        if (datagram is null || datagram.Length is 0 or > MaxDatagramBytes)
        {
            return false;
        }
        Payload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Payload>(datagram);
        }
        catch (JsonException)
        {
            return false;
        }
        if (parsed is null
            || !string.Equals(parsed.V, MeshWire.Version, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parsed.Name) || parsed.Name.Length > MaxNameLength
            || parsed.Name.Any(char.IsControl)   // no ESC/newline/control: a name is printed by `mesh lan`
            || !OperatorKey.IsValidFingerprint(parsed.Fp)
            || parsed.Port is < 1 or > 65535)
        {
            return false;
        }
        payload = parsed;
        return true;
    }
}

/// <summary>
/// Thread-safe table of peers heard on the network. Bounded (a beacon-spammer cannot grow it past
/// <see cref="MaxPeers"/> — the stalest entry is evicted first) and self-expiring: a snapshot only
/// ever returns peers heard within the TTL.
/// </summary>
public sealed class MeshDiscoveryRegistry
{
    /// <summary>Upper bound on remembered peers; beyond it the stalest is evicted.</summary>
    public const int MaxPeers = 64;

    /// <summary>Per-source-IP cap. A node serves on ONE port, so many ports from one address is a
    /// spoofing attempt; capping per address means a single hostile host can only ever churn its OWN
    /// few slots — it can never evict honest peers at other addresses out of the table.</summary>
    public const int MaxPerAddress = 4;

    private readonly object _lock = new();
    private readonly Dictionary<string, DiscoveredPeer> _peers = new(StringComparer.Ordinal);

    /// <summary>Records (or refreshes) a peer, keyed by where it announced from.</summary>
    public void Report(DiscoveredPeer peer)
    {
        var key = $"{peer.Address}:{peer.Port}";
        lock (_lock)
        {
            if (!_peers.ContainsKey(key))
            {
                // Contain a single hostile source first: if this address already holds its quota,
                // evict the stalest of ITS OWN entries — never an honest peer at another address.
                var sameAddress = _peers.Where(kv => string.Equals(kv.Value.Address, peer.Address, StringComparison.Ordinal)).ToList();
                if (sameAddress.Count >= MaxPerAddress)
                {
                    _peers.Remove(sameAddress.OrderBy(kv => kv.Value.LastSeenUtc).First().Key);
                }
                else if (_peers.Count >= MaxPeers)
                {
                    _peers.Remove(_peers.OrderBy(kv => kv.Value.LastSeenUtc).First().Key);
                }
            }
            _peers[key] = peer;
        }
    }

    /// <summary>Live peers (heard within the TTL), stalest pruned, ordered by name.</summary>
    public IReadOnlyList<DiscoveredPeer> Snapshot(DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            foreach (var key in _peers.Where(kv => kv.Value.LastSeenUtc + MeshBeacon.Ttl < nowUtc).Select(kv => kv.Key).ToList())
            {
                _peers.Remove(key);
            }
            return _peers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}

/// <summary>How discovery runs: the name and fingerprint to announce, the serve port being offered
/// (null = listen-only), where to persist the peer table, and the discovery port (overridable for
/// test isolation).</summary>
public sealed record MeshDiscoverySettings(
    string NodeName, string? Fingerprint, int? ServePort, string StateDir, int DiscoveryPort = MeshBeacon.DefaultPort);

/// <summary>
/// F3 of the LAN party: zero-config presence. A node ANNOUNCES itself on a multicast group (name,
/// fingerprint, serve port) and LISTENS for others, so instances on the same network find each other
/// with no addresses configured. Discovered peers feed the same auto-pull that configured peers do —
/// and nothing more: <b>discovery is presence, not trust</b>. A stranger's beacon gets it pulled FROM
/// (bounded download, then the trust root refuses its unsigned/untrusted packages before anything
/// parks); it is never trusted, never executed, never admitted by virtue of being on the network.
///
/// <para>Announcing requires a signing identity and a serve port (an unsigned node's packages would
/// be refused everywhere anyway); listening works regardless. The peer table is persisted to
/// <c>mesh-peers.json</c> under the state dir so <c>ashlar mesh lan</c> can show who's at the party.</para>
///
/// <para>Environmental honesty: multicast reaches the physical LAN from native/host-network nodes;
/// Docker Desktop's bridge does NOT forward it to the LAN (containers on one bridge still hear each
/// other). Configured peers (<c>ASHLAR_MESH_PEERS</c>) remain the works-everywhere baseline; this is
/// the zero-config convenience on top. Daemon-safe: a socket that cannot be set up is logged and
/// discovery is skipped — never a crash, never a park.</para>
/// </summary>
public sealed class MeshDiscoveryService : BackgroundService
{
    private readonly MeshDiscoverySettings _settings;
    private readonly MeshDiscoveryRegistry _registry;
    private readonly ILogger<MeshDiscoveryService> _logger;
    private readonly Func<byte[], IPEndPoint, CancellationToken, ValueTask<int>>? _sendBeacon;

    /// <summary>Creates the discovery service.</summary>
    public MeshDiscoveryService(
        MeshDiscoverySettings settings, MeshDiscoveryRegistry registry, ILogger<MeshDiscoveryService> logger)
        : this(settings, registry, logger, sendBeacon: null)
    {
    }

    // Instance-scoped transport seam: tests can exercise the real loop without requiring a multicast route.
    // The decision still names the production multicast destination, before this send boundary is reached.
    internal MeshDiscoveryService(
        MeshDiscoverySettings settings, MeshDiscoveryRegistry registry, ILogger<MeshDiscoveryService> logger,
        Func<byte[], IPEndPoint, CancellationToken, ValueTask<int>>? sendBeacon)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sendBeacon = sendBeacon;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UdpClient listener;
        try
        {
            listener = new UdpClient();
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, _settings.DiscoveryPort));
            listener.JoinMulticastGroup(MeshBeacon.Group);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex,
                "Mesh discovery could not join {Group}:{Port} — discovery disabled for this run (configured peers still work).",
                MeshBeacon.Group, _settings.DiscoveryPort);
            return;
        }

        var announcing = _settings.ServePort is not null && _settings.Fingerprint is not null;
        _logger.LogInformation(
            "Mesh discovery armed on {Group}:{Port} — {Mode}. Discovery is presence, not trust.",
            MeshBeacon.Group, _settings.DiscoveryPort,
            announcing ? $"announcing '{_settings.NodeName}' ({_settings.Fingerprint}, serving :{_settings.ServePort})" : "listen-only");

        using var _ = listener;
        var listenTask = ListenAsync(listener, stoppingToken);
        var announceTask = AnnounceLoopAsync(announcing, stoppingToken);
        await Task.WhenAll(listenTask, announceTask).ConfigureAwait(false);
    }

    private async Task ListenAsync(UdpClient listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await listener.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;   // one bad receive never stops the party
            }
            if (!MeshBeacon.TryParse(datagram.Buffer, out var beacon)
                || string.Equals(beacon.Fp, _settings.Fingerprint, StringComparison.Ordinal))
            {
                continue;   // malformed, or our own echo
            }
            // The address is where the packet actually CAME from — never self-reported.
            _registry.Report(new DiscoveredPeer(
                beacon.Name.Trim(), beacon.Fp, datagram.RemoteEndPoint.Address.ToString(), beacon.Port, DateTimeOffset.UtcNow));
        }
    }

    private async Task AnnounceLoopAsync(bool announcing, CancellationToken ct)
    {
        // SPEC-007 EG-MESH-05: decided once per loop, before the sender exists; the group, the port and the
        // beacon are fixed for the loop's life, so one decision covers every send. Unconditional on purpose: G3 cannot
        // see a guard that may not run, so a listen-only node records host:listen-only (it never sends a beacon).
        var decision = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(
            EgressFamilies.MeshDiscovery,
            "EG-MESH-05",
            announcing ? $"udp://{MeshBeacon.Group}:{_settings.DiscoveryPort}" : "host:listen-only"));
        if (decision.Refuses)
        {
            _logger.LogWarning(new EventId(7307, "EgressRefused"),
                "Mesh discovery egress refused: {Refusal}; continuing listen-only",
                new EgressRefusedException(decision).Message);
            announcing = false;
        }
        using var sender = new UdpClient();
        try { sender.MulticastLoopback = true; } catch { /* platform quirk; loopback is best-effort */ }
        var endpoint = new IPEndPoint(MeshBeacon.Group, _settings.DiscoveryPort);
        var beacon = announcing
            ? MeshBeacon.Encode(_settings.NodeName, _settings.Fingerprint!, _settings.ServePort!.Value)
            : null;

        using var timer = new PeriodicTimer(MeshBeacon.AnnounceEvery);
        try
        {
            do
            {
                if (beacon is not null)
                {
                    try
                    {
                        await (_sendBeacon is null
                            ? sender.SendAsync(beacon, endpoint, ct)
                            : _sendBeacon(beacon, endpoint, ct)).ConfigureAwait(false);
                    }
                    catch (SocketException ex) { _logger.LogDebug(ex, "Mesh discovery announce failed"); }
                }
                PersistSnapshot();
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private string? _lastMembership;

    private void PersistSnapshot()
    {
        try
        {
            var snapshot = _registry.Snapshot(DateTimeOffset.UtcNow);
            // Only rewrite when MEMBERSHIP changes — not on every 15s tick and not on every refreshed
            // timestamp — so a stable network (and, with the per-address cap, a beacon-spammer) does
            // not amplify writes on the flash media the node runs on.
            var membership = string.Join("|", snapshot.Select(p => $"{p.Address}:{p.Port}:{p.Fingerprint}:{p.Name}"));
            if (membership == _lastMembership)
            {
                return;
            }
            var path = Path.Combine(_settings.StateDir, "mesh-peers.json");
            var tmp = path + ".tmp";
            Directory.CreateDirectory(_settings.StateDir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
            _lastMembership = membership;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mesh discovery could not persist mesh-peers.json");
        }
    }
}
