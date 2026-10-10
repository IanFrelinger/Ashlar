using Ashlar.Abstractions.Security;
using Ashlar.Manifest;
using Microsoft.Extensions.DependencyInjection;

namespace Ashlar.Hosting;

public static partial class AshlarServiceCollectionExtensions
{
    private static void RegisterSubjectClearances(IServiceCollection services, AshlarHostingOptions options)
    {
        IReadOnlyDictionary<string, string?> ceilings = new Dictionary<string, string?>();
        if (options.AgentClearancePolicyPath is not null)
        {
            var yaml = File.ReadAllText(options.AgentClearancePolicyPath);
            if (!PolicyLoader.TryLoad(yaml, out var policy, out _))
                throw new InvalidOperationException("Agent clearance policy failed validation.");
            ceilings = policy!.AgentClearances;
        }
        // Snapshot before user code can mutate an options dictionary. This singleton is authority,
        // not a live configuration view, and neither requests nor app manifests participate.
        services.AddSingleton(new SubjectClearanceAuthority(ceilings, options.AgentClearanceLimits));
    }
}
