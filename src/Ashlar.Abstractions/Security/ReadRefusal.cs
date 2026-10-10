using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Ashlar.Abstractions.Security;

/// <summary>Preserves a read refusal through adapter and aggregate exception wrappers.</summary>
internal static class ReadRefusal
{
    internal static ReadRefusedException? Find(Exception? error)
    {
        if (error is null) return null;
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ExceptionIdentity.Instance);
        pending.Push(error);
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            if (current is ReadRefusedException refusal) return refusal;
            if (current is AggregateException aggregate)
            {
                for (var i = aggregate.InnerExceptions.Count - 1; i >= 0; i--)
                    pending.Push(aggregate.InnerExceptions[i]);
            }
            else if (current.InnerException is not null) pending.Push(current.InnerException);
        }
        return null;
    }

    internal static void ThrowIfPresent(Exception error)
    {
        var refusal = Find(error);
        if (refusal is not null) ExceptionDispatchInfo.Capture(refusal).Throw();
    }

    private sealed class ExceptionIdentity : IEqualityComparer<Exception>
    {
        internal static readonly ExceptionIdentity Instance = new();
        public bool Equals(Exception? x, Exception? y) => ReferenceEquals(x, y);
        public int GetHashCode(Exception obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
