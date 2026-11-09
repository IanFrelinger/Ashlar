using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Ashlar.Infrastructure.Preflight;

/// <summary>Observation only. A caller decides whether to throw, warn, skip, or select an alternative.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DependencyAvailability>))]
public enum DependencyAvailability
{
    Available,
    Missing,
    Unavailable,
    Unsupported,
    TimedOut,
    NotRegistered,
    NotChecked
}

/// <summary>A dependency selected by the operation being planned, not a global environment checklist.</summary>
public sealed record DependencyRequirement(string Id, bool Required = true);

/// <summary>A bounded, read-only probe's observation. Do not put credentials or raw command output in detail.</summary>
public sealed record DependencyObservation(DependencyAvailability Availability, string Detail);

/// <summary>
/// A trusted probe adapter. Implementations must be read-only, return promptly with an asynchronous task,
/// honor cancellation, and keep mandatory authorization and security checks on their execution path.
/// </summary>
public sealed record DependencyProbe(string Id, Func<CancellationToken, Task<DependencyObservation>> CheckAsync);

/// <summary>The observation and requirement for one selected dependency.</summary>
public sealed record DependencyCheckResult(string Id, bool Required, DependencyAvailability Availability, string Detail)
{
    public bool Available => Availability == DependencyAvailability.Available;
}

/// <summary>A snapshot of readiness, never authorization to execute or proof of subsequent execution.</summary>
public sealed record DependencyPreflightReport(string Operation, bool Ran, IReadOnlyList<DependencyCheckResult> Checks)
{
    public bool Complete => Ran && Checks.All(check => check.Availability != DependencyAvailability.NotChecked);
    public bool RequiredAvailable => Ran && Checks.All(check => !check.Required || check.Available);
}

/// <summary>
/// Runs only the dependencies declared by this operation. No installation, fallback, exception policy,
/// global environment discovery, or cache is built into the runner. Unknown probes fail closed as data.
/// </summary>
public sealed class DependencyPreflight
{
    private readonly IReadOnlyDictionary<string, DependencyProbe> _probes;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _operationTimeout;

    public DependencyPreflight(
        IEnumerable<DependencyProbe> probes,
        TimeSpan? probeTimeout = null,
        TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probeTimeout = ValidateTimeout(probeTimeout ?? TimeSpan.FromSeconds(8), nameof(probeTimeout));
        _operationTimeout = ValidateTimeout(operationTimeout ?? TimeSpan.FromSeconds(30), nameof(operationTimeout));
        var registry = new Dictionary<string, DependencyProbe>(StringComparer.Ordinal);
        foreach (var probe in probes)
        {
            ArgumentNullException.ThrowIfNull(probe);
            ArgumentException.ThrowIfNullOrWhiteSpace(probe.Id);
            ArgumentNullException.ThrowIfNull(probe.CheckAsync);
            if (!registry.TryAdd(probe.Id, probe))
                throw new ArgumentException($"Duplicate dependency probe '{probe.Id}'.", nameof(probes));
        }
        _probes = registry;
    }

    public async Task<DependencyPreflightReport> RunAsync(
        string operation,
        IEnumerable<DependencyRequirement> requirements,
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(requirements);
        cancellationToken.ThrowIfCancellationRequested();
        // Snapshot and validate before invoking anything. A repeated optional declaration must not
        // weaken a required one, and each dependency is probed at most once per operation.
        var selected = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var requirement in requirements)
        {
            ArgumentNullException.ThrowIfNull(requirement);
            ArgumentException.ThrowIfNullOrWhiteSpace(requirement.Id);
            selected[requirement.Id] = requirement.Required || selected.GetValueOrDefault(requirement.Id);
        }

        var checks = new List<DependencyCheckResult>();
        var timer = Stopwatch.StartNew();
        var budgetExhausted = false;
        foreach (var (id, required) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DependencyObservation observation;
            var remaining = _operationTimeout - timer.Elapsed;
            if (!enabled)
                observation = new(DependencyAvailability.NotChecked, "Optional preflight was disabled; no readiness evidence.");
            else if (budgetExhausted || remaining <= TimeSpan.Zero)
                observation = new(DependencyAvailability.NotChecked, "Operation preflight deadline exhausted; dependency was not checked.");
            else if (!_probes.TryGetValue(id, out var probe))
                observation = new(DependencyAvailability.NotRegistered, "No probe registered for this dependency.");
            else
            {
                var limitedByOperation = remaining <= _probeTimeout;
                var observed = await ObserveAsync(probe, limitedByOperation ? remaining : _probeTimeout, cancellationToken).ConfigureAwait(false);
                observation = observed.Observation;
                // Timers may round below the remaining Stopwatch duration. Once the operation's
                // own deadline fires, do not spend that sub-millisecond remainder on another probe.
                budgetExhausted = limitedByOperation && observed.DeadlineExpired;
            }
            checks.Add(new(id, required, observation.Availability, observation.Detail));
        }
        return new(operation, enabled, checks.AsReadOnly());
    }

    private static async Task<(DependencyObservation Observation, bool DeadlineExpired)> ObserveAsync(DependencyProbe probe, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var pending = probe.CheckAsync(deadline.Token);
        ArgumentNullException.ThrowIfNull(pending);
        try
        {
            // Race completion against cancellation explicitly. WaitAsync's TimeoutException would
            // be indistinguishable from an adapter's own TimeoutException, which must propagate.
            var expired = Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
            var completed = await Task.WhenAny(pending, expired).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (completed != pending)
            {
                ObserveLateFault(pending);
                return (new(DependencyAvailability.TimedOut, "Dependency probe exceeded its deadline."), true);
            }
            var observation = await pending.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            deadline.Token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(observation);
            if (!Enum.IsDefined(observation.Availability))
                throw new InvalidOperationException("Dependency probe returned an unknown availability.");
            return (observation, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return (new(DependencyAvailability.TimedOut, "Dependency probe exceeded its deadline."), true);
        }
        catch (OperationCanceledException)
        {
            ObserveLateFault(pending);
            throw;
        }
        finally
        {
            // Also release the timer task after a successful probe. Cancellation reaches any
            // defective adapter still running, without detaching the mandatory security context.
            deadline.Cancel();
        }
        // In particular, policy refusals and programming errors must propagate, not become missing
        // dependencies that a caller might degrade around.
    }

    private static void ObserveLateFault(Task pending) =>
        _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static TimeSpan ValidateTimeout(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(name, "Preflight deadlines must be positive and at most five minutes.");
        return value;
    }
}
