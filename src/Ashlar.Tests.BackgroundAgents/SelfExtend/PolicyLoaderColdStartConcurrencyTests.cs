using System.Reflection;
using System.Runtime.Loader;
using Ashlar.Manifest;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.SelfExtend;

/// <summary>
/// Two threads parsing a policy for the first time in a process must both get the policy.
///
/// <para><b>Why this exists.</b> The self-extend gate fails closed on a policy it cannot parse,
/// so a spurious parse failure is a spurious rejection. That is what reddened
/// <see cref="SelfExtendAdmissionBridgeTests.A_share_failure_annotates_the_outcome_but_never_fails_the_cycle"/>
/// on the macOS lane of readiness run 36740078979 with "GATE ERROR: REJECTED: policy could not be
/// parsed: Exception during deserialization", and <c>ManifestContractTests</c> on Linux and
/// Windows before it. <see cref="PolicyLoader"/> held ONE static YamlDotNet deserializer for the
/// whole process. In YamlDotNet 13.7.1 that deserializer's object factory caches each type's
/// <c>[OnDeserializing]</c>/<c>[OnDeserialized]</c> lookup in a plain <c>Dictionary</c> that it
/// writes, unlocked, the first time it meets the type. Two test classes (or two self-extend
/// cycles) doing their first parse at the same instant wrote it together; the corrupted insert
/// threw, and YamlDotNet wrapped that as "Exception during deserialization".</para>
///
/// <para><b>Why a process start has to be rebuilt here.</b> The cache is written only on a miss,
/// so the race exists only while it is cold: the first parses of a process. Re-running any test
/// in a process that has already parsed a policy can never see it, which is why the original
/// failure was ~0.1% per lane. Each trial below therefore loads FRESH copies of
/// <c>Ashlar.Manifest</c> and <c>YamlDotNet</c> into their own collectible
/// <see cref="AssemblyLoadContext"/>, where every static they own starts empty exactly as it does
/// at process start, and releases all the threads into <c>PolicyLoader.TryLoad</c> through one
/// barrier. With a shared deserializer restored it failed nearly every run when measured, pinned
/// to two cores included; with no shared state there is nothing to race on and it cannot fail.</para>
///
/// <para><b>What this cannot see.</b> Only <see cref="PolicyLoader"/>. <c>ManifestLoader</c> keeps
/// two static deserializers of the same kind and is not exercised here.</para>
/// </summary>
public sealed class PolicyLoaderColdStartConcurrencyTests
{
    /// <summary>More threads than any CI runner has cores: the barrier, not the core count, lines them up.</summary>
    private const int Threads = 8;

    /// <summary>
    /// Cold starts per run. Pinned to two cores, 20 trials still missed a restored shared
    /// deserializer in 2 runs of 20, so a small runner gets twice that.
    /// </summary>
    private const int Trials = 40;

    /// <summary>Byte-for-byte what <c>SelfExtendAdmissionBridgeTests.WritePolicy("self-extending")</c> writes.</summary>
    private const string Yaml = """
        apiVersion: ashlar/v1
        kind: Policy
        sandbox:
          root: .
          writable: []
        selfExtend:
          mode: self-extending
          budget:
            extensions: 3
            window: 24h
          mayAdd: [brick]
          gatesRequired: [sandbox]
        never:
          - modify_gate
          - widen_sandbox
          - access_signing_keys
          - truncate_ledger
          - grant_capability
        """;

    [Fact]
    public void Concurrent_first_parses_of_a_process_all_get_the_policy()
    {
        var failures = new List<string>();
        var calls = 0;

        for (var trial = 0; trial < Trials; trial++)
        {
            var context = new ColdStartContext();
            try
            {
                var tryLoad = context.PolicyLoaderTryLoad();
                var results = RaceFirstParses(tryLoad);
                calls += results.Length;
                failures.AddRange(results.Where(r => r is not null).Select(r => $"trial {trial}: {r}"));

                // The trial proves nothing unless YamlDotNet itself was a fresh copy too, not the
                // default context's warm one.
                context.FreshAssemblyNames().Should().Contain(
                    ColdStartContext.Fresh,
                    "every statically held YamlDotNet cache must start cold, as at process start");
            }
            finally
            {
                context.Unload();
            }
        }

        calls.Should().Be(Threads * Trials, "every thread must have reached TryLoad and reported");
        failures.Should().BeEmpty(
            "a valid policy must parse however many threads meet it first, yet {0} of {1} first parses "
            + "were rejected; a shared, unsynchronized deserializer rejects it at random, and the gate "
            + "then fails closed on a policy that is fine",
            failures.Count, calls);
    }

    [Fact]
    public void The_cold_start_context_really_is_a_second_copy_of_the_loader()
    {
        // Non-vacuity for the fact above: if the load context quietly resolved to the default
        // context's assemblies, every trial would share one warm deserializer and pass for the
        // wrong reason.
        var context = new ColdStartContext();
        try
        {
            var tryLoad = context.PolicyLoaderTryLoad();

            var freshLoader = tryLoad.DeclaringType!.Assembly;
            freshLoader.Should().NotBeSameAs(typeof(PolicyLoader).Assembly);
            AssemblyLoadContext.GetLoadContext(freshLoader).Should().BeSameAs(context);

            var args = new object?[] { Yaml, null, null };
            ((bool)tryLoad.Invoke(null, args)!).Should().BeTrue((string?)args[2]);
            args[1].Should().NotBeNull().And.NotBeOfType<AshlarPolicy>(
                "the policy type must come from the fresh copy of Ashlar.Manifest");

            context.FreshAssemblyNames().Should().BeEquivalentTo(ColdStartContext.Fresh);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Releases <see cref="Threads"/> threads into TryLoad at once; null means that call got the policy.</summary>
    private static string?[] RaceFirstParses(MethodInfo tryLoad)
    {
        var results = new string?[Threads];
        var reported = new bool[Threads];
        using var barrier = new Barrier(Threads);
        var workers = new Thread[Threads];
        for (var i = 0; i < Threads; i++)
        {
            var slot = i;
            workers[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    var args = new object?[] { Yaml, null, null };
                    var loaded = (bool)tryLoad.Invoke(null, args)!;
                    results[slot] = loaded && args[1] is not null
                        ? null
                        : (string?)args[2] ?? "TryLoad returned no policy and no reason";
                }
                catch (Exception ex)
                {
                    results[slot] = "TryLoad threw " + (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
                }
                reported[slot] = true;
            })
            { IsBackground = true };
        }

        foreach (var worker in workers)
        {
            worker.Start();
        }
        foreach (var worker in workers)
        {
            worker.Join();
        }

        reported.Should().OnlyContain(r => r, "a thread that never reported proves nothing");
        return results;
    }

    /// <summary>
    /// A collectible load context holding its own copies of the assemblies whose statics make up
    /// "the first parse of a process". Everything else resolves to the default context.
    /// </summary>
    private sealed class ColdStartContext : AssemblyLoadContext
    {
        public static readonly string[] Fresh = ["Ashlar.Manifest", "YamlDotNet"];

        private readonly string _directory;

        public ColdStartContext()
            : base("policy-cold-start", isCollectible: true)
        {
            var location = typeof(PolicyLoader).Assembly.Location;
            location.Should().NotBeNullOrEmpty("the loader must be loadable from a file for a second copy to exist");
            _directory = Path.GetDirectoryName(location)!;
        }

        public MethodInfo PolicyLoaderTryLoad()
        {
            var assembly = LoadFromAssemblyName(typeof(PolicyLoader).Assembly.GetName());
            var loader = assembly.GetType(typeof(PolicyLoader).FullName!, throwOnError: true)!;
            return loader.GetMethod(nameof(PolicyLoader.TryLoad), BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("PolicyLoader.TryLoad not found on the fresh copy");
        }

        public IEnumerable<string> FreshAssemblyNames() =>
            Assemblies.Select(a => a.GetName().Name!).Where(n => Fresh.Contains(n, StringComparer.Ordinal));

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is { } name && Fresh.Contains(name, StringComparer.Ordinal))
            {
                return LoadFromAssemblyPath(Path.Combine(_directory, name + ".dll"));
            }

            return null;
        }
    }
}
