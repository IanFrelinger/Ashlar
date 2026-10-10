using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using Ashlar.Abstractions.Transport;

namespace Ashlar.Abstractions.Security.Egress;

/// <summary>Internal refusal propagation and trust-boundary formatting shared by Ashlar adapters.</summary>
internal static class EgressRefusal
{
    internal const string Code = "EGRESS_REFUSED";
    internal const string ReferenceKey = "egressRef";

    internal static bool IsCode(string? code) =>
        string.Equals(code, Code, StringComparison.OrdinalIgnoreCase)
        || string.Equals(code, "a2a.egress_refused", StringComparison.OrdinalIgnoreCase);

    internal static bool IsRefused(AgentResult result) => !result.Success &&
        (IsCode(result.ErrorCode) || (result.Metadata is not null
            && result.Metadata.TryGetValue("errorCode", out var code) && IsCode(code)));

    // Reference identity keeps custom Exception.Equals implementations from hiding a branch.
    internal static EgressRefusedException? Find(Exception? error)
    {
        if (error is null) return null;
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ExceptionIdentity.Instance);
        pending.Push(error);
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            if (current is EgressRefusedException refusal) return refusal;
            if (current is AggregateException aggregate)
            {
                for (var i = aggregate.InnerExceptions.Count - 1; i >= 0; i--)
                    pending.Push(aggregate.InnerExceptions[i]);
            }
            // Grpc.Core.Api also exposes Status.DebugException as InnerException; the
            // transport tests pin that SDK contract without a Grpc dependency here.
            else if (current.InnerException is not null)
                pending.Push(current.InnerException);
        }
        return null;
    }

    internal static void ThrowIfPresent(Exception error)
    {
        var refusal = Find(error);
        if (refusal is not null) ExceptionDispatchInfo.Capture(refusal).Throw();
    }

    internal static string RemoteMessage(string? reference) =>
        $"egress refused by policy (ref {SafeReference(reference)})";

    internal static string Reference(AgentResult result) =>
        SafeReference(result.Metadata is not null
            && result.Metadata.TryGetValue(ReferenceKey, out var reference) ? reference : null);

    internal static IReadOnlyDictionary<string, string> Metadata(EgressRefusedException refusal) => Metadata(refusal.Ref);

    internal static IReadOnlyDictionary<string, string> Metadata(string? reference) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["errorCode"] = Code,
            [ReferenceKey] = SafeReference(reference)
        };

    internal static string SafeReference(string? reference)
    {
        if (reference == EgressEnforcement.UnavailableReference) return reference;
        if (reference is not null && reference.Length == 16
            && reference.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return reference;
        // Never parse a free-text message, sequence number or arbitrary metadata as a ref.
        try { return EgressEnforcement.NewReference(); }
        catch (System.Security.Cryptography.CryptographicException) { return EgressEnforcement.UnavailableReference; }
    }

    private sealed class ExceptionIdentity : IEqualityComparer<Exception>
    {
        internal static readonly ExceptionIdentity Instance = new();
        public bool Equals(Exception? x, Exception? y) => ReferenceEquals(x, y);
        public int GetHashCode(Exception obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
