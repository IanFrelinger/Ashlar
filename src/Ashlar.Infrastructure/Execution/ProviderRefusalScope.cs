using System.Runtime.ExceptionServices;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Infrastructure.Execution;

/// <summary>Retains the first refused probe/attempt for one adaptive invocation, never across calls.</summary>
internal sealed class ProviderRefusalScope : IDisposable
{
    private static readonly AsyncLocal<ProviderRefusalScope?> Current = new();
    private readonly ProviderRefusalScope? _parent = Current.Value;
    private EgressRefusedException? _first;

    internal ProviderRefusalScope() => Current.Value = this;

    internal static void Record(EgressRefusedException refusal)
    {
        var scope = Current.Value;
        if (scope is not null) Interlocked.CompareExchange(ref scope._first, refusal, null);
    }

    internal void ThrowIfRefused()
    {
        var refusal = Volatile.Read(ref _first);
        if (refusal is not null) ExceptionDispatchInfo.Capture(refusal).Throw();
    }

    public void Dispose() => Current.Value = _parent;
}
