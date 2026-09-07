using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Ashlar.Core.Application.Resilience.Ports;

namespace Ashlar.Orchestration.Resilience;

/// <summary>
/// Circuit breaker pattern implementation for resilient service calls.
/// 
/// Protects against cascading failures by:
/// - Tracking failure counts per operation
/// - Opening circuit after failure threshold
/// - Providing fallback mechanisms
/// - Auto-recovery after timeout period
/// 
/// States:
/// - Closed: Normal operation, failures are tracked
/// - Open: Circuit is open, requests fail fast with fallback
/// - HalfOpen: Testing recovery, allowing limited requests
/// 
/// Thread-safe implementation using concurrent collections and locks.
/// </summary>
public sealed class CircuitBreaker : ICircuitBreaker
{
    private readonly string _name;
    private readonly int _failureThreshold;
    private readonly TimeSpan _timeout;
    private readonly ILogger<CircuitBreaker>? _logger;
    private readonly ConcurrentDictionary<string, CircuitState> _circuits = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreaker"/> class.
    /// </summary>
    /// <param name="name">The name of the circuit breaker (for logging and identification).</param>
    /// <param name="failureThreshold">Number of consecutive failures before opening the circuit (default: 5).</param>
    /// <param name="timeout">Time to wait before attempting half-open state (default: 1 minute).</param>
    /// <param name="logger">Optional logger instance.</param>
    public CircuitBreaker(
        string name,
        int failureThreshold = 5,
        TimeSpan? timeout = null,
        ILogger<CircuitBreaker>? logger = null)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _failureThreshold = failureThreshold;
        _timeout = timeout ?? TimeSpan.FromMinutes(1);
        _logger = logger;
    }

    /// <summary>
    /// Executes an operation with circuit breaker protection.
    /// 
    /// Behavior:
    /// - Closed state: Executes operation, tracks failures
    /// - Open state: Returns fallback or throws CircuitBreakerOpenException
    /// - HalfOpen state: Allows one test request, transitions based on result
    /// 
    /// Automatically transitions states based on failure thresholds and timeouts.
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="operationKey">Unique key identifying the operation</param>
    /// <param name="operation">Operation to execute</param>
    /// <param name="fallback">Optional fallback function if circuit is open</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Operation result or fallback value</returns>
    /// <exception cref="CircuitBreakerOpenException">Thrown when circuit is open and no fallback provided</exception>
    public async Task<T> ExecuteAsync<T>(
        string operationKey,
        Func<Task<T>> operation,
        Func<Exception, T>? fallback = null,
        CancellationToken cancellationToken = default)
    {
        var state = GetOrCreateState(operationKey);

        // Check circuit state with lock
        lock (state.SyncLock)
        {
            if (state.State == CircuitStateType.Open)
            {
                if (state.LastFailureTime.HasValue && DateTimeOffset.UtcNow - state.LastFailureTime.Value < _timeout)
                {
                    _logger?.LogWarning("Circuit breaker {Name} is OPEN for {Operation}. Using fallback.", 
                        _name, operationKey);
                    
                    if (fallback != null)
                    {
                        try
                        {
                            return fallback(new CircuitBreakerOpenException(
                                $"Circuit breaker {_name} is open for operation {operationKey}"));
                        }
                        catch
                        {
                            throw new CircuitBreakerOpenException(
                                $"Circuit breaker {_name} is open for operation {operationKey}");
                        }
                    }
                    throw new CircuitBreakerOpenException(
                        $"Circuit breaker {_name} is open for operation {operationKey}");
                }
                else
                {
                    // Timeout expired, attempt half-open
                    state.State = CircuitStateType.HalfOpen;
                    state.ConsecutiveSuccesses = 0;
                    _logger?.LogInformation(
                        "Circuit breaker {Name} transitioning to HALF-OPEN for {Operation}", 
                        _name, operationKey);
                }
            }
        }

        try
        {
            var result = await operation();

            // Success - update state with lock
            lock (state.SyncLock)
            {
                state.ConsecutiveFailures = 0;
                state.LastSuccessTime = DateTimeOffset.UtcNow;

                if (state.State == CircuitStateType.HalfOpen)
                {
                    state.ConsecutiveSuccesses++;
                    if (state.ConsecutiveSuccesses >= 2)
                    {
                        state.State = CircuitStateType.Closed;
                        _logger?.LogInformation(
                            "Circuit breaker {Name} transitioning to CLOSED for {Operation}", 
                            _name, operationKey);
                    }
                }
                else if (state.State != CircuitStateType.Closed)
                {
                    state.State = CircuitStateType.Closed;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            // Failure - update state with lock
            lock (state.SyncLock)
            {
                state.ConsecutiveFailures++;
                state.LastFailureTime = DateTimeOffset.UtcNow;
                state.LastError = ex;

                if (state.ConsecutiveFailures >= _failureThreshold)
                {
                    state.State = CircuitStateType.Open;
                    _logger?.LogError(ex, 
                        "Circuit breaker {Name} opened for {Operation} after {Failures} failures",
                        _name, operationKey, state.ConsecutiveFailures);
                }
            }

            // Try fallback if available
            if (fallback != null)
            {
                try
                {
                    return fallback(ex);
                }
                catch
                {
                    // Fallback failed, rethrow original
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Gets the current state of a circuit.
    /// </summary>
    /// <param name="operationKey">The operation key to check.</param>
    /// <returns>The current circuit state (Closed, Open, or HalfOpen).</returns>
    public CircuitStateType GetState(string operationKey)
    {
        return GetOrCreateState(operationKey).State;
    }

    /// <summary>
    /// Manually opens a circuit.
    /// 
    /// Forces the circuit into Open state, causing all requests to fail fast.
    /// Useful for maintenance or when a service is known to be down.
    /// </summary>
    /// <param name="operationKey">The operation key to open the circuit for.</param>
    public void Open(string operationKey)
    {
        var state = GetOrCreateState(operationKey);
        lock (state.SyncLock)
        {
            state.State = CircuitStateType.Open;
            state.LastFailureTime = DateTimeOffset.UtcNow;
        }
        _logger?.LogWarning("Circuit breaker {Name} manually opened for {Operation}", _name, operationKey);
    }

    /// <summary>
    /// Manually closes a circuit.
    /// 
    /// Forces the circuit into Closed state, resetting failure counters.
    /// Useful when a service is known to be recovered.
    /// </summary>
    /// <param name="operationKey">The operation key to close the circuit for.</param>
    public void Close(string operationKey)
    {
        var state = GetOrCreateState(operationKey);
        lock (state.SyncLock)
        {
            state.State = CircuitStateType.Closed;
            state.ConsecutiveFailures = 0;
            state.ConsecutiveSuccesses = 0;
        }
        _logger?.LogInformation("Circuit breaker {Name} manually closed for {Operation}", _name, operationKey);
    }

    /// <summary>
    /// Resets all circuits.
    /// 
    /// Clears all circuit state, effectively resetting all circuits to Closed state.
    /// </summary>
    public void Reset()
    {
        _circuits.Clear();
        _logger?.LogInformation("Circuit breaker {Name} reset", _name);
    }

    private CircuitState GetOrCreateState(string operationKey)
    {
        return _circuits.GetOrAdd(operationKey, _ => new CircuitState
        {
            OperationKey = operationKey,
            State = CircuitStateType.Closed
        });
    }
}
