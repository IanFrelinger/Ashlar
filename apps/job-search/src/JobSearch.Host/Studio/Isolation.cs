using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Infrastructure.Certification;
using JobSearch.Certified;

namespace JobSearch.Host.Studio;

/// <summary>
/// Starts this executable as a child process for work that runs model-written code: certifying a step, and running
/// certified steps. The child gets a clean environment (no tokens, no keys, no connection strings), a temporary working
/// directory, a managed-heap cap, and is killed when it overruns its time (STUDIO.md I8).
/// </summary>
public static class ChildCommand
{
    private static readonly string[] KeptVariables =
        ["PATH", "HOME", "DOTNET_ROOT", "DOTNET_CLI_HOME", "TMPDIR", "LANG", "LC_ALL", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"];

    /// <summary>A start description for <c>jobsearch &lt;args&gt;</c>.</summary>
    public static ProcessStartInfo StartInfo(long heapLimitBytes, params string[] args)
    {
        var dll = typeof(Program).Assembly.Location;
        var dir = Path.GetDirectoryName(dll)!;
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.FileName = DotnetHost();
        var ownConfig = Path.Combine(dir, "jobsearch.runtimeconfig.json");
        if (File.Exists(ownConfig))
        {
            psi.ArgumentList.Add(dll);
        }
        else
        {
            // Referenced as a library (the tests): run under the referencing application's runtime config and deps.
            var config = Directory.GetFiles(dir, "*.runtimeconfig.json")
                .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"no runtime config next to {dll}");
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add("--runtimeconfig");
            psi.ArgumentList.Add(config);
            var deps = config.Replace(".runtimeconfig.json", ".deps.json", StringComparison.Ordinal);
            if (File.Exists(deps))
            {
                psi.ArgumentList.Add("--depsfile");
                psi.ArgumentList.Add(deps);
            }
            psi.ArgumentList.Add(dll);
        }
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var kept = KeptVariables.Select(k => (k, v: Environment.GetEnvironmentVariable(k))).Where(p => p.v is not null).ToList();
        psi.Environment.Clear();
        foreach (var (k, v) in kept)
            psi.Environment[k] = v;
        psi.Environment["DOTNET_GCHeapHardLimit"] = "0x" + heapLimitBytes.ToString("X", CultureInfo.InvariantCulture);
        psi.Environment["DOTNET_gcServer"] = "0";
        return psi;
    }

    /// <summary>The <c>dotnet</c> host this process runs under.</summary>
    public static string DotnetHost()
    {
        var own = Environment.ProcessPath;
        if (own is not null && Path.GetFileNameWithoutExtension(own).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return own;
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(candidate) ? candidate : "dotnet";
    }

    /// <summary>Runs a child to completion with a deadline; the process tree is killed when it overruns.</summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> RunAsync(ProcessStartInfo psi, string? stdin, TimeSpan timeout, CancellationToken ct)
    {
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start the child process");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        if (stdin is not null)
            await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        p.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            return (-1, "", "", true);
        }
        return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), false);
    }
}

/// <summary>
/// <c>jobsearch certify-step &lt;dir&gt;</c>: the child side of certification. Runs Ashlar's certification gate on the
/// one source file in the directory against its witness, writes the record and the gate-emitted assembly when admitted,
/// and prints one JSON line. It holds no signing key: the parent adds the platform's signature afterwards.
/// </summary>
public static class CertifyStepCommand
{
    /// <summary>The witness file name in a step directory.</summary>
    public const string WitnessFile = "witness.json";

    /// <summary>Child entry point.</summary>
    public static async Task<int> RunAsync(string dir)
    {
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CERT_NUGET_CONFIG", null);
            var request = await BrickCertificationProjectLoader.LoadAsync(dir, Path.Combine(dir, WitnessFile)).ConfigureAwait(false);
            var gate = new CertificationGate(new CertificationRecordSigner());
            var decision = await gate.CertifyAsync(request).ConfigureAwait(false);
            if (!decision.Admitted || request.EmittedArtifact is null)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { admitted = false, check = decision.FailureCheck ?? "unknown", reason = decision.Record.Reason ?? "not admitted" }));
                return 0;
            }
            var data = CertificationRecordMapper.ToData(decision.Record);
            await CertifiedArtifactExporter.WriteAsync(Path.Combine(dir, CertifiedBricks.RecordFile), data, request.EmittedArtifact).ConfigureAwait(false);
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                admitted = true, check = "", reason = "",
                contentHash = data.ContentHash, mutants = data.TotalMutants, survivors = data.SurvivingMutants,
            }));
            return 0;
        }
#pragma warning disable CA1031 // The child reports every failure as data; the parent decides.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Load-time refusals (single-source fence, IL import fence, compile errors) arrive as exceptions.
            Console.Out.WriteLine(JsonSerializer.Serialize(new { admitted = false, check = "load", reason = ex.Message }));
            return 0;
        }
    }
}

/// <summary>
/// <c>jobsearch step-host --trusted K1,K2</c>: the child that runs certified library steps. Each request names a step
/// folder; the step is verified against the pinned keys and loaded once, then run. One JSON object per line in, one out.
/// Model-written code therefore never runs in the web server's process.
/// </summary>
public static class StepHostServer
{
    /// <summary>Child entry point.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> trustedKeys)
    {
        var policy = CertificationTrustPolicy.FromTrustedKeys(trustedKeys);
        var loaded = new Dictionary<string, Brick>(StringComparer.Ordinal);
        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
                continue;
            long id = 0;
            JsonObject reply;
            try
            {
                var request = JsonNode.Parse(line)!.AsObject();
                id = request["id"]!.GetValue<long>();
                var stepId = request["stepId"]!.GetValue<string>();
                var folder = request["folder"]!.GetValue<string>();
                var key = stepId + "|" + folder;
                if (!loaded.TryGetValue(key, out var brick))
                {
                    brick = CertifiedBricks.LoadVerified(stepId, folder, policy).Brick;
                    loaded[key] = brick;
                }
                var inputs = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var (k, v) in request["inputs"]!.AsObject())
                    inputs[k] = v?.GetValue<string>() ?? "";
                var output = await brick.ExecuteAsync(new Ashlar.Core.Domain.Execution.BrickInput(inputs), ImplementationType.Deterministic, AppExecutionContext.Instance).ConfigureAwait(false);
                var outputs = new JsonObject();
                foreach (var (k, v) in output.ToDictionary())
                    outputs[k] = v?.ToString() ?? "";
                reply = new JsonObject { ["id"] = id, ["ok"] = true, ["outputs"] = outputs };
            }
#pragma warning disable CA1031 // A step's failure is reported to the parent, which flags the posting; the host keeps serving.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                reply = new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = $"{ex.GetType().Name}: {ex.Message}" };
            }
            await Console.Out.WriteLineAsync(reply.ToJsonString()).ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
        }
        return 0;
    }
}

/// <summary>Why a step could not run.</summary>
public sealed class StepRunException(string kind, string message) : Exception(message)
{
    /// <summary><c>timeout</c>, <c>error</c> or <c>crashed</c>.</summary>
    public string Kind { get; } = kind;
}

/// <summary>
/// The parent side: a small pool of step-host processes. A call that overruns its time kills that host (and the code
/// stuck in it), and the caller quarantines the step; the next call starts a fresh host.
/// </summary>
public sealed class StepHostPool : IDisposable
{
    private readonly IReadOnlyList<string> _trustedKeys;
    private readonly TimeSpan _callTimeout;
    private readonly Host[] _hosts;
    private int _next;
    private bool _disposed;

    /// <summary>A pool of <paramref name="size"/> hosts trusting <paramref name="trustedKeys"/>.</summary>
    public StepHostPool(IReadOnlyList<string> trustedKeys, int size = 2, TimeSpan? callTimeout = null)
    {
        _trustedKeys = trustedKeys;
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(3);
        _hosts = Enumerable.Range(0, Math.Max(1, size)).Select(_ => new Host()).ToArray();
    }

    /// <summary>How many host processes have been started (tests use it to see a kill and a restart).</summary>
    public int Starts => _hosts.Sum(h => h.Starts);

    /// <summary>Runs a step; throws <see cref="StepRunException"/> when it cannot.</summary>
    public async Task<Dictionary<string, string>> RunAsync(string stepId, string folder, IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var host = _hosts[(int)((uint)Interlocked.Increment(ref _next) % (uint)_hosts.Length)];
        await host.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var first = !host.Loaded.Contains(stepId + "|" + folder);
            // Loading verifies the record and the assembly, which takes longer than running a loaded step.
            var timeout = first ? _callTimeout + TimeSpan.FromSeconds(10) : _callTimeout;
            var process = host.Ensure(_trustedKeys);
            var id = Interlocked.Increment(ref host.Sequence);
            var request = new JsonObject
            {
                ["id"] = id, ["stepId"] = stepId, ["folder"] = folder,
                ["inputs"] = new JsonObject(inputs.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            };
            await process.StandardInput.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            string? line;
            try
            {
                line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                host.Kill();
                throw new StepRunException("timeout", $"the step did not finish within {timeout.TotalSeconds:0.#} s");
            }
            if (line is null)
            {
                host.Kill();
                throw new StepRunException("crashed", "the step host exited while running the step");
            }
            var reply = JsonNode.Parse(line)!.AsObject();
            if (reply["id"]?.GetValue<long>() != id)
            {
                host.Kill();
                throw new StepRunException("crashed", "the step host answered out of order");
            }
            host.Loaded.Add(stepId + "|" + folder);
            if (reply["ok"]?.GetValue<bool>() != true)
                throw new StepRunException("error", reply["error"]?.GetValue<string>() ?? "the step failed");
            return reply["outputs"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "", StringComparer.Ordinal);
        }
        finally
        {
            host.Gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        foreach (var h in _hosts)
            h.Kill();
    }

    private sealed class Host
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly HashSet<string> Loaded = new(StringComparer.Ordinal);
        public long Sequence;
        public int Starts;
        private Process? _process;

        public Process Ensure(IReadOnlyList<string> trustedKeys)
        {
            if (_process is { HasExited: false })
                return _process;
            Kill();
            var psi = ChildCommand.StartInfo(256L * 1024 * 1024, "step-host", "--trusted", string.Join(",", trustedKeys));
            _process = Process.Start(psi) ?? throw new StepRunException("crashed", "could not start the step host");
            // Drain stderr so a chatty host never blocks on a full pipe.
            _ = Task.Run(async () => { try { await _process.StandardError.ReadToEndAsync().ConfigureAwait(false); } catch (InvalidOperationException) { } });
            Starts++;
            Loaded.Clear();
            return _process;
        }

        public void Kill()
        {
            var p = Interlocked.Exchange(ref _process, null);
            if (p is null)
                return;
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            p.Dispose();
            Loaded.Clear();
        }
    }
}
