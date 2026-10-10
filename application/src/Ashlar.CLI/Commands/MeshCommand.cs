using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Mesh;
using Ashlar.Core.Application.Mesh.Models;
using Ashlar.Core.Application.Mesh.Ports;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Analysis;
using Ashlar.Infrastructure.Mesh;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Block 9: Instance discovery and capability mesh.
/// P2.3: ashlar mesh sync pulls and validates shared adaptations before adoption.
/// </summary>
public sealed class MeshCommand : Command
{
    /// <summary>Creates a new MeshCommand instance.</summary>
    public MeshCommand() : base("mesh",
        "Peer discovery and the local instance mesh: `mesh lan` lists LAN-discovered peers (F3); "
        + "sneakernet export/import; peer health, admit, and revoke.")
    {
        var discoverOpt = new Option<bool>("--discover", () => false, "Discover peer instances");
        var advertiseOpt = new Option<bool>("--advertise", () => false, "Advertise this instance's capabilities");
        var capabilityOpt = new Option<string?>("--capability", "Find peers with capability");
        var setTrustTierOpt = new Option<string?>("--set-trust-tier", "Update peer trust tier using <peerId>:<trusted|untrusted|unknown> in instances.json");

        var syncCmd = new Command("sync", "Pull shared adaptations from trusted peers and adopt after validation (P2.3)");
        syncCmd.SetHandler(async (InvocationContext ctx) => await ExecuteSyncAsync());

        var capabilitiesCmd = new Command("capabilities", "Show local instance capabilities for mesh negotiation");
        capabilitiesCmd.SetHandler(ExecuteCapabilities);

        var exportComponentArg = new Argument<string?>("componentId", () => null, "Component/adaptation ID to export; omit to export all");
        var exportToOpt = new Option<string>("--to", "Output path for .nxpkg export file") { IsRequired = true };
        var exportCmd = new Command("export", "Export shared adaptations to .nxpkg file for sneakernet transfer (P3.3)");
        exportCmd.AddArgument(exportComponentArg);
        exportCmd.AddOption(exportToOpt);
        exportCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var componentId = ctx.ParseResult.GetValueForArgument(exportComponentArg);
            var to = ctx.ParseResult.GetValueForOption(exportToOpt)!;
            ctx.ExitCode = await ExecuteExportAsync(to, componentId);
        });

        var importPathArg = new Argument<string>("path", "Path to .nxpkg import file");
        var importCmd = new Command("import", "Import shared adaptations from .nxpkg sneakernet file (P3.3)");
        importCmd.AddArgument(importPathArg);
        importCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var path = ctx.ParseResult.GetValueForArgument(importPathArg)!;
            await ExecuteImportAsync(path);
        });

        var peersCmd = new Command("peers", "List peers from local instances.json (open mesh primitive; not fleet director nodes)");
        peersCmd.SetHandler(ExecutePeers);

        var healthUrlOpt = new Option<string?>("--url", () => null, "Remote host base URL, e.g. https://ashlar.example:8080") { IsRequired = true };
        var healthTimeoutOpt = new Option<int>("--timeout-seconds", () => 30, "HTTP timeout");
        var healthCmd = new Command("health", "GET /health on a remote Ashlar host (generic probe)");
        healthCmd.Add(healthUrlOpt);
        healthCmd.Add(healthTimeoutOpt);
        // InvocationContext binding (as admitCmd/revokeCmd below): setting ctx.ExitCode is the only
        // exit code System.CommandLine honours. Environment.ExitCode is overwritten back to 0 after
        // the handler returns, so a refused connection or a missing --url would otherwise exit 0.
        healthCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var url = ctx.ParseResult.GetValueForOption(healthUrlOpt);
            var timeout = ctx.ParseResult.GetValueForOption(healthTimeoutOpt);
            ctx.ExitCode = await ExecuteHealthAsync(url, timeout).ConfigureAwait(false);
        });

        var admitPeerArg = new Argument<string>("peerId", "Peer id to admit in local instances.json");
        var admitCmd = new Command("admit", "Set admitted=true in local instances.json (not fleet director; use commercial mesh director admit for /api/mesh/fleet)");
        admitCmd.AddArgument(admitPeerArg);
        admitCmd.SetHandler((InvocationContext ctx) =>
        {
            var peerId = ctx.ParseResult.GetValueForArgument(admitPeerArg)!;
            var ok = UpdatePeerAdmitted(peerId, admitted: true);
            if (!ok)
            {
                Console.Error.WriteLine($"Peer '{peerId}' not found in instances.json.");
                ctx.ExitCode = 1;
                return;
            }

            Console.WriteLine($"Admitted peer '{peerId}' (admitted=true).");
            ctx.ExitCode = 0;
        });

        var revokePeerArg = new Argument<string>("peerId", "Peer id to revoke in local instances.json");
        var revokeCmd = new Command("revoke", "Set admitted=false in local instances.json (not fleet director; use commercial mesh director revoke for /api/mesh/fleet)");
        revokeCmd.AddArgument(revokePeerArg);
        revokeCmd.SetHandler((InvocationContext ctx) =>
        {
            var peerId = ctx.ParseResult.GetValueForArgument(revokePeerArg)!;
            var ok = UpdatePeerAdmitted(peerId, admitted: false);
            if (!ok)
            {
                Console.Error.WriteLine($"Peer '{peerId}' not found in instances.json.");
                ctx.ExitCode = 1;
                return;
            }

            Console.WriteLine($"Revoked peer '{peerId}' (admitted=false).");
            ctx.ExitCode = 0;
        });

        AddOption(discoverOpt);
        AddOption(advertiseOpt);
        AddOption(capabilityOpt);
        AddOption(setTrustTierOpt);
        AddCommand(syncCmd);
        AddCommand(capabilitiesCmd);
        AddCommand(exportCmd);
        AddCommand(importCmd);
        AddCommand(peersCmd);
        AddCommand(healthCmd);
        AddCommand(admitCmd);
        AddCommand(revokeCmd);

        // ashlar mesh lan — who's at the party. Reads the peer table the daemon's F3 multicast
        // discovery persists (mesh-peers.json under the state dir); read-only, safe beside a live node.
        var lanCmd = new Command("lan", "List peers discovered on the local network (F3 multicast beacon)");
        lanCmd.SetHandler(() => Environment.Exit(ExecuteLan(Console.Out)));
        AddCommand(lanCmd);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            var discover = ctx.ParseResult.GetValueForOption(discoverOpt);
            var advertise = ctx.ParseResult.GetValueForOption(advertiseOpt);
            var capability = ctx.ParseResult.GetValueForOption(capabilityOpt);
            var setTrustTier = ctx.ParseResult.GetValueForOption(setTrustTierOpt);
            await ExecuteAsync(discover, advertise, capability, setTrustTier);
        });
    }

    private static void ExecuteCapabilities()
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddMeshInfrastructure()
            .BuildServiceProvider();

        var provider = services.GetRequiredService<IInstanceCapabilitiesProvider>();
        var caps = provider.GetCapabilities();

        Console.WriteLine("Local instance capabilities:");
        Console.WriteLine($"  Supported formats: {string.Join(", ", caps.SupportedFormats)}");
        Console.WriteLine($"  Preferred format: {caps.PreferredFormat}");
        Console.WriteLine($"  CanCompile: {caps.CanCompile}");
        Console.WriteLine($"  HasDockerRuntime: {caps.HasDockerRuntime}");
        Console.WriteLine($"  HasWasmRuntime: {caps.HasWasmRuntime}");
        Console.WriteLine($"  IsAirGapped: {caps.IsAirGapped}");
        Console.WriteLine($"  Available components: {string.Join(", ", caps.AvailableComponents)}");
        Environment.ExitCode = 0;
    }

    private static async Task ExecuteSyncAsync()
    {
        var sharedPath = Environment.GetEnvironmentVariable("ASHLAR_SHARED_ADAPTATIONS_PATH");
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddCodeAnalyzers()
            .AddAdaptationInfrastructure() // Adaptation db lives in the state dir (ASHLAR_STATE_DIR, else <repo root>/.ashlar/state)
            .AddSharedAdaptationCache(sharedPath)
            .BuildServiceProvider();

        var sync = services.GetRequiredService<ISharedAdaptationSync>();
        var entries = await sync.PullAsync().ConfigureAwait(false);
        Console.WriteLine($"Pulled {entries.Count} shared adaptation(s).");

        var adopted = 0;
        foreach (var entry in entries)
        {
            var ok = await sync.ValidateAndAdoptAsync(entry).ConfigureAwait(false);
            if (ok) adopted++;
        }
        Console.WriteLine($"Adopted {adopted}/{entries.Count}.");
        Environment.ExitCode = 0;
    }

    private static async Task<int> ExecuteExportAsync(string outputPath, string? componentId = null)
    {
        using var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddCodeAnalyzers()
            .AddAdaptationInfrastructure()
            .AddSharedAdaptationCache()
            .BuildServiceProvider();

        var transport = services.GetRequiredService<ISneakernetTransport>();
        return await ExecuteExportAsync(transport, outputPath, componentId).ConfigureAwait(false);
    }

    internal static async Task<int> ExecuteExportAsync(ISneakernetTransport transport, string outputPath, string? componentId = null)
    {
        try
        {
            if (transport is ISneakernetExportInitiator operatorTransport)
                await operatorTransport.ExportAsync(outputPath, componentId, EgressInitiator.OperatorFileExport, CancellationToken.None).ConfigureAwait(false);
            else
                await transport.ExportAsync(outputPath, componentId).ConfigureAwait(false);
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is not null)
        {
            Console.Error.WriteLine(EgressRefusal.Find(ex)!.Message);
            return 77;
        }
        Console.WriteLine($"Exported to {outputPath}");
        return 0;
    }

    private static async Task ExecuteImportAsync(string inputPath)
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddCodeAnalyzers()
            .AddAdaptationInfrastructure()
            .AddSharedAdaptationCache()
            .BuildServiceProvider();

        var transport = services.GetRequiredService<ISneakernetTransport>();
        var adopted = await transport.ImportAsync(inputPath).ConfigureAwait(false);
        Console.WriteLine($"Imported {adopted} adaptation(s) from {inputPath}");
        Environment.ExitCode = 0;
    }

    private static async Task ExecuteAsync(bool discover, bool advertise, string? capability, string? setTrustTier)
    {
        if (!string.IsNullOrWhiteSpace(setTrustTier))
        {
            if (!TryParseTrustTierChange(setTrustTier, out var peerId, out var trustTier))
            {
                Console.Error.WriteLine("Invalid --set-trust-tier value. Use <peerId>:<trusted|untrusted|unknown>.");
                Environment.ExitCode = 1;
                return;
            }

            var updated = UpdatePeerTrustTier(peerId!, trustTier);
            if (!updated)
            {
                Console.Error.WriteLine($"Peer '{peerId}' not found in instances.json.");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine($"Updated trust tier for peer '{peerId}' => {trustTier}.");
        }

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddMeshInfrastructure()
            .BuildServiceProvider();

        if (discover)
        {
            var discovery = services.GetRequiredService<IInstanceDiscovery>();
            var peers = await discovery.DiscoverAsync().ConfigureAwait(false);
            var trustPolicy = MeshTrustPolicyConfiguration.ResolveDiscoveryPolicy();
            Console.WriteLine($"Active mesh trust policy: {trustPolicy} (ASHLAR_MESH_TRUST_POLICY or ASHLAR_PEER_TRUST_POLICY; default any)");
            Console.WriteLine($"Discovered {peers.Count} peer(s):");
            foreach (var p in peers)
                Console.WriteLine($"  - {p.PeerId} @ {p.Endpoint} tier={p.TrustTier} [{string.Join(", ", p.Capabilities)}]");
        }

        if (advertise)
        {
            var adv = services.GetRequiredService<ICapabilityAdvertisement>();
            await adv.AdvertiseAsync(new[] { new Ashlar.Core.Application.Mesh.Models.CapabilityDescriptor { Id = "ashlar-cli", Name = "Ashlar CLI" } }).ConfigureAwait(false);
            Console.WriteLine("Advertised capabilities.");
        }

        if (!string.IsNullOrEmpty(capability))
        {
            var adv = services.GetRequiredService<ICapabilityAdvertisement>();
            var peers = await adv.FindPeersWithCapabilityAsync(capability).ConfigureAwait(false);
            Console.WriteLine($"Peers with '{capability}': {peers.Count}");
            foreach (var p in peers)
                Console.WriteLine($"  - {p.PeerId} @ {p.Endpoint}");
        }

        if (!discover && !advertise && string.IsNullOrEmpty(capability) && string.IsNullOrWhiteSpace(setTrustTier))
            Console.WriteLine(
                "Use --discover, --advertise, --capability <name>, --set-trust-tier <peerId>:<tier>, " +
                "mesh peers, mesh health, mesh admit, or mesh revoke (local instances.json). " +
                "Fleet director HTTP (register, admit, list-nodes) lives in commercial/src/Ashlar.Commercial.MeshDirector.");
        Environment.ExitCode = 0;
    }

    private static void ExecutePeers()
    {
        var instancesPath = Environment.GetEnvironmentVariable("ASHLAR_MESH_INSTANCES_PATH");
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddMeshInfrastructure(instancesPath)
            .BuildServiceProvider();

        var discovery = services.GetRequiredService<IInstanceDiscovery>();
        var peers = discovery.DiscoverAsync().GetAwaiter().GetResult();
        Console.WriteLine($"Local peers from instances.json ({peers.Count}):");
        foreach (var p in peers)
        {
            Console.WriteLine(
                $"  {p.PeerId,-24} {p.Endpoint,-40} tier={p.TrustTier,-10} admitted={p.Admitted}");
        }

        Environment.ExitCode = 0;
    }

    /// <summary>
    /// Prints the LAN-party peer table the daemon's F3 discovery persists. A fingerprint marked
    /// trusted is one in this operator's keychain (or their own key) — presence on the list grants
    /// nothing; it only says who is announcing. Testable via the injected writer.
    /// </summary>
    internal static int ExecuteLan(TextWriter stdout)
    {
        var path = Path.Combine(
            Ashlar.Core.Application.Paths.RepoPathResolver.ResolveStateDirectory(), "mesh-peers.json");
        if (!File.Exists(path))
        {
            stdout.WriteLine("No one at the party yet — no mesh-peers.json. Is the daemon running with ASHLAR_MESH_DISCOVERY=1?");
            return 0;
        }

        List<Ashlar.CLI.Commands.BackgroundAgent.DiscoveredPeer>? peers;
        try
        {
            peers = System.Text.Json.JsonSerializer.Deserialize<List<Ashlar.CLI.Commands.BackgroundAgent.DiscoveredPeer>>(
                File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            stdout.WriteLine($"mesh-peers.json could not be read ({ex.Message}).");
            return 1;
        }

        peers ??= [];
        stdout.WriteLine($"Peers on the local network ({peers.Count}):");
        var now = DateTimeOffset.UtcNow;
        foreach (var p in peers)
        {
            bool trusted;
            try { trusted = Ashlar.Manifest.Signing.OperatorKey.IsSignerTrusted(p.Fingerprint, Array.Empty<string>()); }
            catch { trusted = false; }
            var age = now - p.LastSeenUtc;
            // Defense-in-depth: strip control characters before printing an untrusted, network-sourced
            // name to the terminal — a beacon field must never inject escape sequences or fake rows.
            var safeName = new string((p.Name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray());
            stdout.WriteLine(
                $"  {safeName,-20} {p.Fingerprint,-26} {p.Address}:{p.Port,-6} seen {Math.Max(0, (int)age.TotalSeconds)}s ago"
                + (trusted ? "  [keychain-trusted]" : "  [not trusted — `ashlar keys trust " + p.Fingerprint + "`]"));
        }
        if (peers.Count == 0)
        {
            stdout.WriteLine("  (empty — beacons expire after 90s of silence)");
        }
        return 0;
    }

    private static async Task<int> ExecuteHealthAsync(string? baseUrl, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Console.Error.WriteLine("--url is required.");
            return 1;
        }

        var root = baseUrl.Trim().TrimEnd('/');
        // Guard the parse INSIDE the handler: a malformed --url used to reach `new Uri(..., Absolute)`
        // unguarded and throw an UriFormatException with a stack trace. Reject it legibly instead, and
        // require an http(s) scheme so a token like `example:8080` (a valid absolute URI, but not a
        // reachable host) is refused here rather than blowing up inside HttpClient.
        if (!Uri.TryCreate(root + "/health", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Console.Error.WriteLine($"--url is not a valid URL: {baseUrl}");
            return 1;
        }

        var client = EgressHttp.CreateClient(EgressFamilies.Http, "EG-HTTP-06");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120));
        try
        {
            using var resp = await client.GetAsync(uri).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.WriteLine($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
            if (!string.IsNullOrWhiteSpace(body))
                Console.WriteLine(body);
            return resp.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool TryParseTrustTierChange(string value, out string? peerId, out PeerTrustTier tier)
    {
        peerId = null;
        tier = PeerTrustTier.Unknown;
        var split = value.Split(':', 2, StringSplitOptions.TrimEntries);
        if (split.Length != 2 || string.IsNullOrWhiteSpace(split[0]))
            return false;
        if (!Enum.TryParse<PeerTrustTier>(split[1], true, out tier))
            return false;
        peerId = split[0];
        return true;
    }

    private static bool UpdatePeerTrustTier(string peerId, PeerTrustTier tier)
    {
        return UpdatePeerJsonField(peerId, obj => obj["trustTier"] = tier.ToString());
    }

    private static bool UpdatePeerAdmitted(string peerId, bool admitted)
    {
        return UpdatePeerJsonField(peerId, obj => obj["admitted"] = admitted);
    }

    private static bool UpdatePeerJsonField(string peerId, Action<JsonObject> mutator)
    {
        var instancesPath = ResolveInstancesPath();
        if (!File.Exists(instancesPath))
            return false;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(instancesPath)) as JsonArray;
            if (root == null)
                return false;

            var entry = root
                .OfType<JsonObject>()
                .FirstOrDefault(obj => string.Equals(obj["peerId"]?.GetValue<string>(), peerId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return false;

            mutator(entry);
            File.WriteAllText(instancesPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveInstancesPath()
    {
        var configured = Environment.GetEnvironmentVariable("ASHLAR_MESH_INSTANCES_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ashlar", "instances.json");
    }

}
