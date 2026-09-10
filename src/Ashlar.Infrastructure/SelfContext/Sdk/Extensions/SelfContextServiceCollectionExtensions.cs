using Microsoft.Extensions.DependencyInjection;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Knowledge.Ports;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Application.SelfContext.Ports;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Infrastructure.Knowledge;
using Ashlar.Infrastructure.Observation;
using Ashlar.Infrastructure.Observation.Sdk.Extensions;
using Ashlar.Infrastructure.SelfContext;

namespace Ashlar.Infrastructure.SelfContext.Sdk.Extensions;
/// <summary>
/// DI extensions for Block 6 self-context.
/// </summary>
public static class SelfContextServiceCollectionExtensions
{
    /// <summary>
    /// Adds execution tracer and self-context assembler.
    /// When <paramref name="patternStorePath"/> is provided, adds observation core so IPatternStore is available.
    /// Requires IAdaptationLog to be registered (from AddAdaptationInfrastructure).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="patternStorePath">Pattern store path; the tracer and test-failure stores are co-located with it. Null uses the state directory.</param>
    /// <param name="docsRoot">
    /// Root the documentation updater writes <c>docs/bricks/</c> beneath. Null keeps the
    /// production default (<c>ASHLAR_DOCS_ROOT</c>, else the repository root). A test that builds
    /// this container passes its temporary directory, for the same reason it passes a temporary
    /// <paramref name="patternStorePath"/>: nothing a test does may land in the checkout.
    /// </param>
    public static IServiceCollection AddSelfContextInfrastructure(this IServiceCollection services, string? patternStorePath = null, string? docsRoot = null)
    {
        if (!string.IsNullOrEmpty(patternStorePath))
            services.AddObservationCore(patternStorePath);
        else
            services.AddSingleton<IPatternStore, EmptyPatternStore>();

        // Co-located with the pattern store; otherwise the state directory (ASHLAR_STATE_DIR, else
        // <repo root>/.ashlar/state) so LiteDB files never land in the CWD / repo root.
        var basePath = !string.IsNullOrEmpty(patternStorePath)
            ? Path.GetDirectoryName(patternStorePath) ?? "."
            : RepoPathResolver.ResolveStateDirectory();
        var tracerDbPath = Path.Combine(basePath, "ashlar-execution.db");
        var testFailuresDbPath = Path.Combine(basePath, "ashlar-test-failures.db");
        services.AddSingleton<IExecutionTracer>(sp => new LiteDbExecutionTracer(tracerDbPath));
        services.AddSingleton<ITestFailureStore>(sp => new LiteDbTestFailureStore(testFailuresDbPath));
        services.AddSingleton<ISelfContextAssembler, SelfContextAssembler>();
        services.AddSingleton<IKnowledgeQueryService>(sp =>
        {
            var adaptationLog = sp.GetRequiredService<IAdaptationLog>();
            var patternStore = sp.GetRequiredService<IPatternStore>();
            var userKnowledgeStore = sp.GetService<IUserKnowledgeLogStore>()
                ?? new Ashlar.Infrastructure.Trust.InMemoryUserKnowledgeLogStore();
            return new KnowledgeQueryService(adaptationLog, patternStore, userKnowledgeStore);
        });
        services.AddChangelogGenerator();
        services.AddDocumentationUpdater(docsRoot);
        return services;
    }

    /// <summary>
    /// Adds IChangelogGenerator. Requires IAdaptationLog (from AddAdaptationInfrastructure). Phase F.
    /// </summary>
    public static IServiceCollection AddChangelogGenerator(this IServiceCollection services)
    {
        services.AddSingleton<IChangelogGenerator, ChangelogGenerator>();
        return services;
    }

    /// <summary>
    /// Adds IDocumentationUpdater. Requires IAdaptationLog and IChangelogGenerator. Phase F.
    /// </summary>
    /// <remarks>
    /// Registered through a factory so <paramref name="docsRoot"/> reaches the constructor. The
    /// previous <c>AddSingleton&lt;IDocumentationUpdater, DocumentationUpdater&gt;()</c> could only
    /// ever select the two-argument constructor — a string is not a resolvable service — which
    /// pinned every container, test containers included, to the repository root (#580).
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="docsRoot">Root for <c>docs/bricks/</c>; null keeps the production default.</param>
    public static IServiceCollection AddDocumentationUpdater(this IServiceCollection services, string? docsRoot = null)
    {
        services.AddSingleton<IDocumentationUpdater>(sp => new DocumentationUpdater(
            sp.GetRequiredService<IAdaptationLog>(),
            sp.GetRequiredService<IChangelogGenerator>(),
            docsRoot));
        return services;
    }
}
