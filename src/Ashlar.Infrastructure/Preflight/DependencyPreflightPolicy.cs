namespace Ashlar.Infrastructure.Preflight;

/// <summary>One optional caller policy. The preflight runner never invokes it.</summary>
public static class DependencyPreflightPolicy
{
    /// <summary>Raise for missing or unverified required dependencies, including a skipped preflight.</summary>
    public static void RequireAvailable(DependencyPreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.RequiredAvailable)
            throw new DependencyPreflightException(report);
    }
}

/// <summary>Retains operation and dependency-specific outcomes for the caller's error handling.</summary>
public sealed class DependencyPreflightException : InvalidOperationException
{
    public DependencyPreflightReport Report { get; }

    public DependencyPreflightException(DependencyPreflightReport report)
        : base(BuildMessage(report)) => Report = report;

    private static string BuildMessage(DependencyPreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var unavailable = report.Checks.Where(check => check.Required && !check.Available)
            .Select(check => $"{check.Id}: {check.Availability}");
        return $"Dependencies not verified for '{report.Operation}': {string.Join(", ", unavailable)} (preflight ran: {report.Ran}).";
    }
}
