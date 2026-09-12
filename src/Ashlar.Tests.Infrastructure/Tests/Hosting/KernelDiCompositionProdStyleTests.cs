using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Persistence;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Execution;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Hosting;

/// <summary>
/// Pins the DI composition decisions that consolidation item C4 centralised.
///
/// These are not "does it resolve" tests — <see cref="KernelPhaseResolutionTests"/>
/// already covers presence. These pin WHICH implementation wins and, for the
/// provider factory, the ORDER of the decorator chain. Both were previously
/// decided across two assemblies, and both are invisible to a presence check:
/// a chain that lost its sanitizing layer still resolves, still reports
/// AdaptiveProviderFactory, and still passes every existing test — while silently
/// sending unscrubbed prompts past the trust boundary.
/// </summary>
[Trait("Category", "ProdStyle")]
[Trait("Category", "E2E")]
[Collection("EnvironmentVariables")]
public sealed class KernelDiCompositionProdStyleTests : IDisposable
{
    private readonly string? _trust = Environment.GetEnvironmentVariable("ASHLAR_TRUST_ENABLED");
    private readonly string? _loadPref = Environment.GetEnvironmentVariable("ASHLAR_LOAD_PREFERENCE");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASHLAR_TRUST_ENABLED", _trust);
        Environment.SetEnvironmentVariable("ASHLAR_LOAD_PREFERENCE", _loadPref);
    }

    // ─────────────────────────── provider factory chain ───────────────────────

    public static TheoryData<bool, bool, string[]> ProviderChains => new()
    {
        // trust, adaptive, expected chain from outermost inward
        { false, false, new[] { nameof(ProviderFactory) } },
        { true,  false, new[] { "SanitizingProviderFactory", nameof(ProviderFactory) } },
        { false, true,  new[] { nameof(AdaptiveProviderFactory), nameof(ProviderFactory) } },
        { true,  true,  new[] { nameof(AdaptiveProviderFactory), "SanitizingProviderFactory", nameof(ProviderFactory) } },
    };

    [Theory(Timeout = TestTimeouts.E2E)]
    [MemberData(nameof(ProviderChains))]
    public async Task ProviderFactory_ComposesExpectedChain(bool trust, bool adaptive, string[] expected)
    {
        await Task.CompletedTask;
        using var sp = BuildProvider(AshlarDeploymentProfile.Full, trust, adaptive);

        Chain(sp.GetRequiredService<IProviderFactory>()).Should().Equal(
            expected,
            "the provider factory is the gateway every LLM call flows through; a missing "
            + "layer changes what leaves the trust boundary without changing what resolves");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ConcreteProviderFactory_IsRegisteredOnlyWhenSomethingWrapsIt()
    {
        await Task.CompletedTask;
        // A deliberate asymmetry carried over from the pre-consolidation wiring:
        // the plain path registers the interface only. Pinned so that "fixing" it
        // is a decision rather than a side effect.
        using (var plain = BuildProvider(AshlarDeploymentProfile.Full, trust: false, adaptive: false))
        {
            plain.GetService<ProviderFactory>().Should().BeNull();
        }

        using var wrapped = BuildProvider(AshlarDeploymentProfile.Full, trust: true, adaptive: false);
        wrapped.GetService<ProviderFactory>().Should().NotBeNull(
            "the sanitizing wrapper resolves the concrete factory, so it must be registered");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task SanitizingLayer_WrapsTheSameInstanceThatIsRegistered()
    {
        await Task.CompletedTask;
        using var sp = BuildProvider(AshlarDeploymentProfile.Full, trust: true, adaptive: false);

        var wrapper = sp.GetRequiredService<IProviderFactory>();
        var inner = Inner(wrapper);

        inner.Should().BeSameAs(
            sp.GetRequiredService<ProviderFactory>(),
            "wrapper and wrapped must share one instance — two would mean two provider "
            + "caches and two ephemeral-model lifecycles");
    }

    // ───────────────────────────── observation core ───────────────────────────

    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData(AshlarDeploymentProfile.Full, true)]
    [InlineData(AshlarDeploymentProfile.AirGapped, false)]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation, true)]
    public async Task ObservationCore_IsRegisteredExactlyOnce(AshlarDeploymentProfile profile, bool pipelineActive)
    {
        await Task.CompletedTask;
        // Adaptation and the observation pipeline both need these services, and both
        // used to register them. Exactly one may do so: the two disagree about the
        // store path, so a duplicate is not merely redundant — it decides where
        // learned patterns are persisted.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlarProfile(profile, o => o.PatternStorePath = "pinning-patterns.db");

        foreach (Type contract in new[] { typeof(IPatternStore), typeof(IPatternProcessedStore), typeof(IContextAssembler) })
        {
            services.Count(d => d.ServiceType == contract).Should().Be(
                1,
                $"{contract.Name} must have a single owner (pipeline active: {pipelineActive})");
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ObservationPipeline_OwnsTheStorePath_whenActive()
    {
        await Task.CompletedTask;
        // A relative path is the only way to tell the two registrations apart: the
        // pipeline roots it at the repo, adaptation takes it verbatim. With an
        // absolute path both produce the same string and the difference hides.
        using var sp = BuildProvider(AshlarDeploymentProfile.Full, trust: false, adaptive: false,
            o => o.PatternStorePath = "pinning-patterns.db");

        // Composed through the helper rather than spelled out: this test is about WHICH
        // registration owns the path, not about the connection mode, and hard-coding the mode
        // here would make it fail for the wrong reason the next time that changes.
        ConnectionString(sp.GetRequiredService<IPatternStore>()).Should().NotBe(
            LiteDbConnectionString.ForSharedAccess("pinning-patterns.db"),
            "the observation pipeline owns the path when active, and it roots it at the repo");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task Adaptation_OwnsTheStorePath_whenPipelineIsDisabled()
    {
        await Task.CompletedTask;
        // AirGapped ships adaptation ON and the observation pipeline OFF, so
        // adaptation's registration is load-bearing here rather than dead. This is
        // the case that makes the ownership conditional instead of a deletion.
        using var sp = BuildProvider(AshlarDeploymentProfile.AirGapped, trust: false, adaptive: false,
            o => o.PatternStorePath = "pinning-patterns.db");

        ConnectionString(sp.GetRequiredService<IPatternStore>()).Should().Be(
            LiteDbConnectionString.ForSharedAccess("pinning-patterns.db"),
            "with the pipeline off, adaptation registers the store and uses the path verbatim");
    }

    // ───────────────────────────────── helpers ────────────────────────────────

    private static ServiceProvider BuildProvider(
        AshlarDeploymentProfile profile,
        bool trust,
        bool adaptive,
        Action<AshlarHostingOptions>? configure = null)
    {
        Environment.SetEnvironmentVariable("ASHLAR_TRUST_ENABLED", trust ? "1" : null);
        Environment.SetEnvironmentVariable("ASHLAR_LOAD_PREFERENCE", adaptive ? "latency" : null);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlarProfile(profile, o => configure?.Invoke(o));
        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Names of the decorator chain, outermost first.</summary>
    private static List<string> Chain(IProviderFactory factory)
    {
        var names = new List<string>();
        object? current = factory;
        while (current is not null)
        {
            names.Add(current.GetType().Name);
            current = Inner(current);
        }

        return names;
    }

    private static object? Inner(object decorator) =>
        decorator.GetType()
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(f => typeof(IProviderFactory).IsAssignableFrom(f.FieldType))
            .Select(f => f.GetValue(decorator))
            .FirstOrDefault(v => v is not null);

    private static string ConnectionString(IPatternStore store) =>
        (string?)store.GetType()
            .GetField("_connectionString", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(store) ?? "<none>";
}
