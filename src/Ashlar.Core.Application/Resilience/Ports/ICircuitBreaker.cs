namespace Ashlar.Core.Application.Resilience.Ports;

/// <summary>
/// Circuit breaker port for resilient service calls.
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
/// </summary>
public interface ICircuitBreaker
{
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
    Task<T> ExecuteAsync<T>(
        string operationKey,
        Func<Task<T>> operation,
        Func<Exception, T>? fallback = null,
        CancellationToken cancellationToken = default);
}
