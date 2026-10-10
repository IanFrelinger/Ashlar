using Ashlar.Abstractions.Security;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Resilience.Ports;

namespace Ashlar.Infrastructure.Resilience;

/// <summary>
/// Default <see cref="IResilientExecutor"/>: delays come from
/// <see cref="RetryPolicy.DelayForAttempt"/>, so the policy's chosen backoff
/// shape applies. Never retries when the caller's token is cancelled.
/// </summary>
public sealed class ResilientExecutor : IResilientExecutor
{
    private readonly ILogger<ResilientExecutor>? _logger;

    /// <summary>Creates an executor with optional logging.</summary>
    public ResilientExecutor(ILogger<ResilientExecutor>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(policy), policy.MaxAttempts, "MaxAttempts must be ≥ 1.");

        var isTransient = policy.ResolveIsTransient();
        Exception? last = null;

        for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReadRefusal.ThrowIfPresent(ex);
                last = ex;

                // Non-negotiable: never retry when the caller's own token cancelled.
                if (cancellationToken.IsCancellationRequested)
                    throw;

                var canRetry = attempt < policy.MaxAttempts && isTransient(ex);
                if (!canRetry)
                    throw;

                // Backoff lives on the policy, not here: Fixed/Linear/Exponential/
                // Jittered all resolve through one implementation, and the call
                // sites that cannot be an ExecuteAsync (result-flag retries, delay
                // advisors) share it too.
                var delay = policy.DelayForAttempt(attempt);

                _logger?.LogWarning(
                    ex,
                    "Transient failure on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs}ms",
                    attempt,
                    policy.MaxAttempts,
                    delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("Operation failed with no exception captured.");
    }
}
