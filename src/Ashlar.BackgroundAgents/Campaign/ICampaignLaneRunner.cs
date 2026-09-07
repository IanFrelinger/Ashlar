namespace Ashlar.BackgroundAgents.Campaign;

/// <summary>Runs one specialist lane and returns a report for the release manager.</summary>
public interface ICampaignLaneRunner
{
    /// <summary>Lane this runner is responsible for.</summary>
    CampaignLane Lane { get; }

    /// <summary>Execute the lane against <paramref name="context"/>.</summary>
    Task<CampaignAgentReport> RunAsync(CampaignRunContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Release-manager coordinator: dispatch specialists, require a report from each,
/// fail closed on silence or failure, publish observations, write the campaign report.
/// </summary>
public interface IReleaseManagerCoordinator
{
    /// <summary>Run the campaign described by <paramref name="agentSet"/>.</summary>
    Task<CampaignReport> RunAsync(
        CampaignAgentSet agentSet,
        CampaignRunContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Host process invoker so lane runners can be tested without spawning real children.</summary>
public interface ICampaignProcessInvoker
{
    /// <summary>Run <paramref name="fileName"/> with <paramref name="arguments"/> in <paramref name="workingDirectory"/>.</summary>
    Task<CampaignProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of one process invocation.</summary>
public sealed record CampaignProcessResult(int ExitCode, string StdOut, string StdErr);
