using Ashlar.Abstractions.Security.Egress;
using System.Net.Http;
using System.Net.Sockets;

namespace Ashlar.Core.Application.Resilience.Ports;

/// <summary>
/// Built-in transient-exception classifiers for <see cref="RetryPolicy"/>.
/// Domain-neutral: classifiers use BCL transport types only — never provider-
/// or language-specific exception types. Callers (and concrete infra) may
/// supply a custom <see cref="RetryPolicy.IsTransient"/> for their own faults.
/// </summary>
public static class TransientClassifiers
{
    /// <summary>
    /// Treats common network/IO/timeout failures as transient.
    /// Does not inspect LLM/provider-specific exception types; those belong
    /// in concrete infrastructure classifiers layered on top of this default.
    /// </summary>
    /// <remarks>
    /// Caller cancellation is enforced by <see cref="IResilientExecutor"/> itself
    /// (never retry when the caller's token is cancelled). Remaining
    /// <see cref="OperationCanceledException"/> instances (e.g. transport
    /// timeouts that surface as <see cref="TaskCanceledException"/>) are
    /// treated as transient here.
    /// </remarks>
    public static bool Network(Exception exception) =>
        EgressRefusal.Find(exception) is null && exception switch
        {
            null => false,
            OperationCanceledException => true,
            HttpRequestException => true,
            TimeoutException => true,
            SocketException => true,
            IOException => true,
            _ => false
        };
}
