using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Forge;
using Ashlar.BackgroundAgents.Observations;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Security;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Policies.Dev;
using Ashlar.Runtime;
using Ashlar.Tools.Dev;

namespace Ashlar.BackgroundAgents.HostRunners;

/// <summary>
/// Shared factory for the repo development toolbox. The "minimal" variant gives the
/// planner the four file-system primitives (read, list, write, search_replace);
/// the "withBuildTest" variant additionally exposes <c>dotnet.build</c> and
/// <c>dotnet.test</c> behind a <see cref="BuildTestBudget"/> rate-limiter so the
/// planner can probe the codebase's health between writes without burning the
/// per-cycle deadline on a build/test loop.
///
/// <para>The factory is the single place that decides which tools the planner
/// sees, so any new capability can be added here without touching agent or
/// runner code. When an <see cref="IObservationStore"/> is supplied, build/test
/// invocations are decorated to publish their outcomes into the observations
/// log — closing the planner ↔ tester feedback loop for tools the planner
/// triggered itself.</para>
/// </summary>
internal static class RepoFsToolboxFactory
{
    /// <summary>
    /// Creates the original minimal toolbox (read/list/write/search_replace).
    /// Preserved for callers that don't want build/test capability surface.
    /// </summary>
    /// <param name="extraTools">Additional tools folded in after the built-ins (e.g. proxies from
    /// <see cref="IToolSource"/> providers such as remote MCP servers). Registered last, so an
    /// extra tool with a clashing id wins per <see cref="CapabilityRegistry.Register"/> semantics —
    /// providers are expected to namespace their ids (e.g. <c>mcp:server:tool</c>).</param>
    public static (CapabilityRegistry tools, PolicyEngine policies) CreateMinimal(
        IEnumerable<ITool>? extraTools = null)
    {
        var tools = new CapabilityRegistry();
        tools.Register(new RepoFsListTool());
        tools.Register(new RepoFsReadTool());
        tools.Register(new RepoFsWriteTool());
        tools.Register(new RepoFsSearchReplaceTool());
        RegisterExtraTools(tools, extraTools);

        var policies = new PolicyEngine(new IPolicy[]
        {
            new PathAllowlist(),
            new MaxWriteSize(),
            // LAST, here and in CreateWithBuildTest: PolicyEngine returns on the first refusal,
            // so order is observable in denial reasons and tests assert on which policy wins.
            new GovernanceFloorPolicy()
        });

        return (tools, policies);
    }

    /// <summary>
    /// Creates a toolbox with the minimal repo-fs tools plus <c>dotnet.build</c>
    /// and <c>dotnet.test</c>, gated by a <see cref="BuildTestBudget"/>. When
    /// <paramref name="observations"/> is supplied, build/test invocations also
    /// publish a <see cref="RuntimeObservation"/> per call so the next planner
    /// cycle sees the outcome of the previous probe.
    /// </summary>
    /// <param name="observations">Optional sink for build/test observations.</param>
    /// <param name="source">Logical source label for emitted observations (typically the agent id).</param>
    /// <returns>The toolbox, the policy engine, and the budget instance — exposed so
    /// callers (typically the agent runner) can call <see cref="BuildTestBudget.Reset"/>
    /// at cycle boundaries.</returns>
    /// <param name="confinement">
    /// Optional single-source confinement declaration (extension spec Part B). When present,
    /// the write allowlist is derived from EXACTLY its writable prefixes — no built-in
    /// defaults, no environment widening — the same declaration that derives the session's
    /// bind mounts. Null preserves the historical default allowlist.
    /// </param>
    public static (CapabilityRegistry tools, PolicyEngine policies, BuildTestBudget budget) CreateWithBuildTest(
        IObservationStore? observations = null,
        string source = "self-extend",
        string? objectiveId = null,
        IChangeProposalStore? proposals = null,
        IAggressivenessModeStore? modeStore = null,
        ICertificationRecordStore? certificationStore = null,
        IBackgroundAgentRegistry? agentRegistry = null,
        IDataSensitivityRegistry? sensitivityRegistry = null,
        IEnumerable<ITool>? extraTools = null,
        Ashlar.Core.Application.Execution.Ports.ProposerConfinement? confinement = null)
    {
        var tools = new CapabilityRegistry();
        tools.Register(new RepoFsListTool());
        tools.Register(new RepoFsReadTool());
        tools.Register(new RepoFsWriteTool());
        tools.Register(new RepoFsSearchReplaceTool());
        RegisterExtraTools(tools, extraTools);

        ITool buildTool = new DotnetBuildTool();
        ITool testTool = new DotnetTestTool();
        if (observations is not null)
        {
            buildTool = new ObservingTool(buildTool, observations,
                ObservingTool.DotnetProjector(source, ObservationKind.Build, objectiveId));
            testTool = new ObservingTool(testTool, observations,
                ObservingTool.DotnetProjector(source, ObservationKind.Test, objectiveId));
        }
        tools.Register(buildTool);
        tools.Register(testTool);

        // Forge: when a proposal store is supplied, register the propose/check
        // tools so the LLM has a non-write path to suggest changes. The policy
        // is registered only when a mode store is also supplied so the *enforcement*
        // can be toggled by operators without code changes.
        if (proposals is not null)
        {
            tools.Register(new ForgeProposeChangeTool(
                proposals,
                agentIdProvider: () => source,
                objectiveIdProvider: () => objectiveId));
            tools.Register(new ForgeCheckPrTool(proposals));
            ITool forgeBuild = new ForgeBuildTool();
            if (observations is not null)
            {
                forgeBuild = new ObservingTool(forgeBuild, observations,
                    ObservingTool.DotnetProjector(source, ObservationKind.Build, objectiveId));
            }
            tools.Register(forgeBuild);

            ITool forgeTest = new ForgeTestTool();
            if (observations is not null)
            {
                forgeTest = new ObservingTool(forgeTest, observations,
                    ObservingTool.DotnetProjector(source, ObservationKind.Test, objectiveId));
            }
            tools.Register(forgeTest);
        }

        var budget = new BuildTestBudget();
        var policyList = new List<IPolicy>
        {
            confinement is null
                ? new PathAllowlist()
                : PathAllowlist.FromExactPrefixes(confinement.ToPathAllowlistPrefixes()),
            new MaxWriteSize(),
            budget
        };
        if (modeStore is not null)
        {
            // ForgeMediatedWritesPolicy is mode-aware and lives next to the other
            // dev policies. It only kicks in for low-trust modes (Passive,
            // SemiActive); at Active/Ambient it's a no-op so existing flows
            // continue to write directly.
            policyList.Add(new ForgeMediatedWritesPolicy(modeStore));
        }

        policyList.Add(new SelfProducedBrickCertificationPolicy(
            certificationStore ?? FailClosedCertificationRecordStore.Instance));

        if (agentRegistry is not null && sensitivityRegistry is not null)
        {
            policyList.Add(new DataExfiltrationPolicy(agentRegistry, sensitivityRegistry));
        }

        // LAST. PolicyEngine returns on the first refusal, so a governance path that another
        // policy also refuses keeps that policy's reason (SelfExtendInvariantACertGateTests asserts
        // the certification policy's), and nothing earlier in the chain admits one, so last still
        // closes it. The tool edge is the floor; this turns its refusal into a counted denial.
        policyList.Add(new GovernanceFloorPolicy());

        var policies = new PolicyEngine(policyList.ToArray());

        return (tools, policies, budget);
    }

    private static void RegisterExtraTools(CapabilityRegistry tools, IEnumerable<ITool>? extraTools)
    {
        if (extraTools is null)
        {
            return;
        }

        foreach (var tool in extraTools)
        {
            tools.Register(tool);
        }
    }
}
