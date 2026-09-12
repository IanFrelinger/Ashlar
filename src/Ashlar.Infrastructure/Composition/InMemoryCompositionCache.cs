using System.Collections.Concurrent;
using Ashlar.Core.Application.Composition.Models;
using Ashlar.Core.Application.Composition.Ports;

namespace Ashlar.Infrastructure.Composition;

/// <summary>
/// In-memory composition cache.
/// </summary>
public sealed class InMemoryCompositionCache : ICompositionCache
{
    private readonly ConcurrentDictionary<string, (ComposedAgent Agent, bool Validated)> _cache = new();

    /// <inheritdoc />
    public Task<bool> TryGetAsync(string problemKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_cache.ContainsKey(problemKey));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para><b>The per-process hash below is left in place, and it is the smaller of this
    /// class's two problems.</b></para>
    ///
    /// <para><c>string.GetHashCode()</c> is randomized per process — measured over five launches
    /// of the same binary on the same problem description: 2098580929, 1593607832, 1338829204,
    /// 1143726908, 1543229914. It does not cross a boundary: the key goes into the
    /// <c>ConcurrentDictionary</c> above, which is never serialized, never written to disk, and
    /// discarded at shutdown. The singleton registration means it lives as long as the process
    /// and no longer.</para>
    ///
    /// <para><b>What would make it a defect.</b> A persistent or distributed
    /// <c>ICompositionCache</c>, at which point stored entries would never be found again after a
    /// restart — silently, because a miss just recomposes.</para>
    ///
    /// <para><b>The larger problem, which a stable hash would not fix.</b>
    /// <c>TryGetAsync</c> takes a caller-supplied <c>problemKey</c> while <c>StoreAsync</c>
    /// derives the key internally and never exposes the derivation. No caller outside this file
    /// can construct a key that will hit, so the cache is unhittable by construction — which is
    /// consistent with there being no production caller of <c>TryGetAsync</c> anywhere in the
    /// repository. The fix is to put the derivation on the port (<c>ICompositionCache</c>), or to
    /// have <c>TryGetAsync</c> take the problem description and derive the key the same way, so
    /// that store and lookup provably agree. Making the hash stable first would only make an
    /// unreachable cache miss more reproducibly.</para>
    ///
    /// <para><b>A warning for whoever takes this up.</b> The one test,
    /// <c>InMemoryCompositionCache_stores_and_retrieves</c>, re-derives the key by copying this
    /// same expression into the test body, in the same process. It therefore passes with the
    /// randomized hash and would pass unchanged with a stable one. It cannot observe this class
    /// of defect and must not be counted as coverage for a fix here; a real test pins a golden
    /// key constant produced by a DIFFERENT process and committed to source, the way
    /// <c>TokenEmbeddingGeneratorTests</c> pins <c>StableTokenHash("rag")</c>.</para>
    /// </remarks>
    public Task StoreAsync(ComposedAgent agent, bool validated, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = agent.ProblemDescription.Trim().ToLowerInvariant().GetHashCode().ToString();
        _cache[key] = (agent, validated);
        return Task.CompletedTask;
    }
}
