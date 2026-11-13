using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;

namespace Ashlar.Tools.GovernanceBrokerPrototype;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || args.Length != 1)
        {
            Console.Error.WriteLine("Linux prototype requires one service-owned configuration file.");
            return 2;
        }
        try
        {
            var serviceUid = LinuxBoundary.EffectiveUid();
            if (serviceUid == 0) throw new InvalidOperationException("Service must be unprivileged.");
            LinuxBoundary.ValidatePath(args[0], serviceUid);
            if (new FileInfo(args[0]).Length > 8192) throw new InvalidOperationException("Config too large.");
            var config = BrokerConfiguration.Parse(await File.ReadAllTextAsync(args[0]).ConfigureAwait(false));
            config.ValidateIdentity(serviceUid);
            LinuxBoundary.ValidatePath(config.Root, serviceUid, privateTree: true);
            // Explicit marker prevents accidental enrollment of a production or historical store.
            if (await File.ReadAllTextAsync(Path.Combine(config.Root, ".prototype-only")).ConfigureAwait(false)
                != "ashlar-governance-prototype-v1\n")
                throw new InvalidOperationException("Disposable prototype marker required.");
            var keyDir = Path.Combine(config.Root, "keys");
            var stateDir = Path.Combine(config.Root, "state");
            if (!Directory.Exists(keyDir) || !Directory.Exists(stateDir))
                throw new InvalidOperationException("Preprovisioned state and keys required.");
            // Inspect the process setting; do not change host or process security settings.
            var mask = File.ReadLines("/proc/self/status").Single(l => l.StartsWith("Umask:", StringComparison.Ordinal));
            if (mask.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1] != "0077")
                throw new InvalidOperationException("Private file-creation mask required.");
            // GateStore currently consults this ambient variable. The dedicated process pins
            // it from validated service config, and verifies every configured public key first.
            Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", keyDir);
            var signer = LoadRequiredSigner(keyDir);
            // Socket activation provides one pre-created listening endpoint. No mkdir,
            // chmod, unlink, stale-socket replacement, or privileged installation here.
            if (Environment.GetEnvironmentVariable("LISTEN_FDS") != "1"
                || Environment.GetEnvironmentVariable("LISTEN_PID") != Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidOperationException("One systemd-activated socket required.");
            using var listener = new Socket(new SafeSocketHandle((IntPtr)3, ownsHandle: false));
            if (listener.AddressFamily != AddressFamily.Unix || listener.SocketType != SocketType.Stream
                || listener.LocalEndPoint?.ToString() != config.SocketPath
                || Convert.ToInt32(listener.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.AcceptConnection)) != 1)
                throw new InvalidOperationException("Activated endpoint mismatch.");
            // Endpoint directory is administrator-owned/configured; no writable untrusted ancestor.
            LinuxBoundary.ValidatePath(Path.GetDirectoryName(config.SocketPath)!, serviceUid);
            var broker = new Broker(config.Project, config.WorkerUid, new GateStore(stateDir, signer), TimeProvider.System);
            while (true)
            {
                using var client = await listener.AcceptAsync().ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var peerUid = LinuxBoundary.PeerUid(client);
                    if (peerUid != config.WorkerUid) continue;
                    using var stream = new NetworkStream(client, ownsSocket: false);
                    var request = await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
                    var response = await broker.DispatchAsync(request, peerUid, timeout.Token).ConfigureAwait(false);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                    await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                    await stream.WriteAsync(new byte[] { 10 }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                    or ArgumentException or InvalidOperationException)
                {
                    // Invalid/slow clients are closed. No untrusted content is logged.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or JsonException or SocketException or FormatException
            or System.ComponentModel.Win32Exception or EntryPointNotFoundException or PlatformNotSupportedException
            or DllNotFoundException)
        {
            Console.Error.WriteLine("Prototype startup refused; verify service-owned configuration, trust, identity and socket.");
            return 2;
        }
    }

    internal static SigningIdentity LoadRequiredSigner(string keyDir)
    {
        var signer = OperatorKey.TryLoad(keyDir) ?? throw new InvalidOperationException("Signing key required.");
        var trust = OperatorKey.TrustedPublicKeysBase64(keyDir);
        if (!trust.Contains(signer.PublicKeyBase64, StringComparer.Ordinal)
            || trust.Any(k => Convert.FromBase64String(k).Length != 32))
            throw new InvalidOperationException("Invalid configured trust.");
        return signer;
    }

    internal static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new byte[BrokerRequest.MaxBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length), ct).ConfigureAwait(false);
            if (count == 0) throw new ArgumentException("Unterminated frame.");
            var newline = Array.IndexOf(bytes, (byte)10, length, count);
            length += count;
            if (newline >= 0)
            {
                if (newline != length - 1 || newline == 0) throw new ArgumentException("One frame required.");
                return bytes[..newline];
            }
        }
        throw new ArgumentException("Frame too large.");
    }
}

internal sealed record BrokerConfiguration(string Project, string Root, string SocketPath, uint WorkerUid)
{
    public static BrokerConfiguration Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new() { MaxDepth = 2 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Object required.");
        var fields = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        string[] expected = ["project", "root", "socketPath", "workerUid"];
        if (fields.Length != expected.Length || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length
            || fields.Except(expected, StringComparer.Ordinal).Any())
            throw new ArgumentException("Configuration fields.");
        var r = doc.RootElement;
        return new(r.GetProperty("project").GetString()!, r.GetProperty("root").GetString()!,
            r.GetProperty("socketPath").GetString()!, r.GetProperty("workerUid").GetUInt32());
    }

    public void ValidateIdentity(uint serviceUid)
    {
        if (serviceUid == 0 || WorkerUid == 0 || WorkerUid == serviceUid)
            throw new InvalidOperationException("Distinct non-root identities required.");
        if (string.IsNullOrWhiteSpace(Project) || Project.Length > 64)
            throw new ArgumentException("Project ID.");
        foreach (var path in new[] { Root, SocketPath })
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                || Path.GetFullPath(path) != path || path == "/")
                throw new ArgumentException("Canonical absolute path required.");
        if (Encoding.UTF8.GetByteCount(SocketPath) > 100) throw new ArgumentException("Unix socket path too long.");
    }
}
