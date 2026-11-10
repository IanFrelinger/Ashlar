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
