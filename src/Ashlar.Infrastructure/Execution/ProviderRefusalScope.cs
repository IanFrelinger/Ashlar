using System.Runtime.ExceptionServices;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// Retains the first refused probe/attempt for one invocation, never across calls. Whoever turns an unavailable
/// provider into an error opens one around its probe and surfaces what it holds (SPEC-007 D23, row 24):
/// <see cref="AdaptiveProviderFactory"/>, <see cref="Models.ProviderBackedModel"/> and <see cref="BehaviorExecutor"/>.
/// </summary>
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

    internal EgressRefusedException? Refusal => Volatile.Read(ref _first);

    internal void ThrowIfRefused()
    {
        var refusal = Volatile.Read(ref _first);
        if (refusal is not null) ExceptionDispatchInfo.Capture(refusal).Throw();
    }

    public void Dispose() => Current.Value = _parent;
}
