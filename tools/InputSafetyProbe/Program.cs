using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Ledger;
using Ashlar.Manifest.Signing;

namespace Ashlar.InputSafetyProbe;

/// <summary>Fresh-process regression probe for runtime input-safety behavior.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "yaml") return YamlProbe.Run(args[1..]);
        if (args.Length == 2 && args[0] == "raw-lock") return RawLock(args[1]);
        if (args.Length == 2 && args[0] == "decision-normalization") return await DecisionNormalizationAsync(args[1]);
        if (args.Length != 3) return 2;
        var (store, root, mode) = (args[0], args[1], args[2]);
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", Path.Combine(root, "keys"));
        using var cancellation = new CancellationTokenSource();
        if (mode == "blocked") cancellation.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            if (mode == "concurrent-construct") ConstructConcurrently(root);
            else if (mode == "construct") Construct(store, root);
            else await WriteAsync(store, root, cancellation.Token);
            Console.WriteLine(mode is "construct" or "concurrent-construct" ? "constructed" : "written");
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", StringComparison.Ordinal))
        {
            // A second store operation must see the cached failure, even when the environment
            // changes after the process has started. Exercise the opposite store as well.
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", "0");
            try
            {
                var otherStore = store == "gate" ? "ledger" : "gate";
                if (mode is "construct" or "concurrent-construct") Construct(otherStore, root);
                else await WriteAsync(otherStore, root, CancellationToken.None);
                Console.Error.WriteLine("A second store bypassed the failed probe.");
                return 3;
            }
            catch (InvalidOperationException repeated) when (ReferenceEquals(ex, repeated))
            {
                Console.Error.WriteLine(ex.Message);
                Console.WriteLine("cached failure");
                return 23;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine("contended");
            return 24;
        }
    }

    private static async Task<int> DecisionNormalizationAsync(string root)
    {
        // The runtime globalization switch is read at startup, before this probe can execute.
        // Exercise the public store APIs; no production switch or bypass is introduced.
        Directory.CreateDirectory(root);
        var keyDir = Path.Combine(root, "keys");
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", keyDir);
        var signer = OperatorKey.Generate(keyDir);
        var store = new GateStore(root, signer);
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var actors = new[]
        {
            (Id: "human-ascii", Actor: "operator"),
            (Id: "human-unicode", Actor: "Zoë Zoe\u0308 李 مریم\u200Cرضا 👩\u200D💻"),
            (Id: "reserved-width", Actor: "ｇａｔｅ"),
        };
        var automaticActors = new List<string>();
        GateRecord? first = null;
        foreach (var item in actors)
        {
            var proposal = new ExtensionProposal
            {
                Id = item.Id, Kind = "brick", Summary = "normalization probe", ProposedBy = "probe",
                ProposedAt = now, Courses = [],
            };
            var recorded = await store.RecordAsync(proposal,
                new AdmissionOutcome { State = ProposalState.Held, Reason = "awaiting human" }, now);
            automaticActors.Add(recorded.Actor);
            first ??= recorded;
        }

        // Historical records bypass new-write validation and retain their existing signed bytes.
        const string historicalActor = "historical\noperator";
        var historical = first! with
        {
            Proposal = first!.Proposal with { Id = "historical-record" },
            Actor = historicalActor, State = ProposalState.Admitted, Sig = null, Signer = null,
        };
        historical = historical with
        {
            Sig = signer.Sign(CanonicalJson.Bytes(historical)), Signer = signer.PublicKeyBase64,
        };
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var historicalPath = Path.Combine(root, "gates", "historical-record.json");
        await File.WriteAllTextAsync(historicalPath, JsonSerializer.Serialize(historical, json));
        var historicalBytes = await File.ReadAllBytesAsync(historicalPath);
        var historicalBefore = await store.GetAsync("historical-record");
        var attempts = new List<object>();
        foreach (var item in actors)
        {
            // A lock acquisition sweeps this file. Rejection must precede that side effect too.
            await File.WriteAllTextAsync(Path.Combine(root, "gates", "orphan.json.tmp"), "uncommitted bytes");
            var before = Snapshot(root);
            Exception? failure = null;
            try { await store.DecideAsync(item.Id, true, item.Actor, "reviewed", now.AddMinutes(1)); }
            catch (Exception ex) { failure = ex; }
            var after = Snapshot(root);
            var persisted = await store.GetAsync(item.Id);
            attempts.Add(new
            {
                id = item.Id,
                outcome = failure is null ? "written" : "rejected",
                exceptionType = failure?.GetType().Name,
                parameter = (failure as ArgumentException)?.ParamName,
                message = failure?.Message,
                beforeSha256 = SnapshotDigest(before), afterSha256 = SnapshotDigest(after),
                filesBefore = before.Keys, filesAfter = after.Keys,
                persistedActor = persisted!.Actor, persistedState = persisted.State.ToString(),
                signatureVerified = OperatorKey.Verify(persisted.Signer!,
                    CanonicalJson.Bytes(persisted with { Sig = null, Signer = null }), persisted.Sig!),
            });
        }
        var historicalAfter = await store.GetAsync("historical-record");
        var all = await store.ListAsync();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = 1,
            invariantSetting = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"),
            normalizationAvailable = "ｇａｔｅ".Normalize(NormalizationForm.FormKC) == "gate",
            automaticActors,
            historicalActorPreserved = historicalBefore!.Actor == historicalActor && historicalAfter!.Actor == historicalActor,
            historicalBytesUnchanged = historicalBytes.SequenceEqual(await File.ReadAllBytesAsync(historicalPath)),
            historicalSignatureVerified = OperatorKey.Verify(historicalAfter!.Signer!,
                CanonicalJson.Bytes(historicalAfter with { Sig = null, Signer = null }), historicalAfter.Sig!),
            historicalListed = all.Count == 4 && all.Any(record => record.Proposal.Id == "historical-record" && record.Actor == historicalActor),
            attempts,
        }));
        return 0;
    }

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')] =
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return files;
    }

    private static string SnapshotDigest(SortedDictionary<string, string> files) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(files))));

    private static void ConstructConcurrently(string root)
    {
        // Dedicated threads, released together, exercise first initialization from both stores
        // without depending on thread-pool injection or the runner's available CPU count.
        using var start = new ManualResetEventSlim();
        var errors = new Exception?[16];
        var threads = Enumerable.Range(0, errors.Length).Select(index => new Thread(() =>
        {
            start.Wait();
            try { Construct(index % 2 == 0 ? "gate" : "ledger", root); }
            catch (Exception ex) { errors[index] = ex; }
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        start.Set();
        foreach (var thread in threads) thread.Join();
        var first = errors.FirstOrDefault(error => error is not null);
        if (first is not null)
        {
            if (errors.Any(error => !ReferenceEquals(error, first)))
                throw new InvalidOperationException("Concurrent constructors did not share one cached failure.");
            Console.WriteLine("16 concurrent constructors shared one cached failure");
            throw first;
        }
        Console.WriteLine("16 concurrent constructors succeeded");
    }

    private static void Construct(string store, string root)
    {
        _ = store switch
        {
            "gate" => (object)new GateStore(root),
            "ledger" => new InstanceLedger(root),
            _ => throw new ArgumentException("Unknown store.", nameof(store)),
        };
    }

    private static int RawLock(string path)
    {
        using var first = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using var second = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Console.WriteLine("second open succeeded: exclusivity disabled");
            return 23;
        }
        catch (IOException)
        {
            Console.WriteLine("second open rejected: exclusivity enforced");
            return 0;
        }
    }

    private static async Task WriteAsync(string store, string root, CancellationToken ct)
    {
        if (store == "gate")
        {
            var proposal = new ExtensionProposal
            {
                Id = "lock-probe", Kind = "brick", Summary = "locking test", ProposedBy = "test",
                ProposedAt = DateTimeOffset.UtcNow, Courses = [],
            };
            await new GateStore(root).RecordAsync(proposal,
                new AdmissionOutcome { State = ProposalState.Held, Reason = "locking test" },
                DateTimeOffset.UtcNow, ct);
        }
        else if (store == "ledger")
        {
            var keyDir = Path.Combine(root, "keys");
            var signer = OperatorKey.TryLoad(keyDir) ?? OperatorKey.Generate(keyDir);
            await new InstanceLedger(root).AppendVerificationAsync(
                signer, "lock-probe", true, [], DateTimeOffset.UtcNow, ct);
        }
        else throw new ArgumentException("Unknown store.", nameof(store));
    }
}
