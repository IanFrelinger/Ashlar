using LiteDB;

namespace Ashlar.Infrastructure.Persistence;

/// <summary>
/// Builds a document type's LiteDB <c>EntityMapper</c> exactly once, single-threaded, before any
/// concurrent caller can reach it.
/// </summary>
/// <remarks>
/// LiteDB 5.0.21's <c>BsonMapper.BuildAddEntityMapper</c> does
/// <c>var mapper = new EntityMapper(type); _entities[type] = mapper;</c> and only THEN populates
/// <c>mapper.Members</c>, and <c>GetEntityMapper</c>'s fast path reads <c>_entities.TryGetValue</c>
/// OUTSIDE <c>lock (_entities)</c> over a plain <c>Dictionary&lt;Type, EntityMapper&gt;</c>. So a
/// thread arriving while a type is cold can be handed a mapper whose member list is still being
/// filled, or can read the dictionary while another build resizes it.
///
/// PR #586 removed the one manifestation a call site could spell its way out of — the
/// <c>NotSupportedException</c> out of <c>LinqExpressionVisitor.ResolveMember</c>, which only
/// reached LiteDB through an expression. The rest are inside LiteDB's own document conversion
/// (<c>BsonMapper.SerializeObject</c> under <c>Insert</c>, <c>BsonMapper.DeserializeObject</c> on
/// the read path) and its collection lookup (<c>EntityMapper.get_Id</c> under
/// <c>GetCollection&lt;T&gt;</c>). Every caller reaches those, so no spelling avoids them and the
/// only fix is to remove the concurrent first touch itself.
///
/// They matter more than their throw counts suggest, because the same gap CORRUPTS far more often
/// than it throws: a document serialized from a half-built mapper is written with fields missing and
/// the insert returns success. Measured on this repo at 60 rounds x 8 threads per document type,
/// against the code this replaced: 451 throws but 6,357 short documents on net8.0, 132 throws but
/// 5,655 on net10.0, worst samples carrying only <c>[_id]</c> where the reference write had 6-12
/// keys. An audit entry or a fleet registration written that way is silently wrong on disk.
///
/// Upstream knows (LiteDB issue #2536, fixed by PR #2538, merged 2024-11-14) but has never shipped
/// it in a stable 5.x: 5.0.21 is the newest stable and everything after it is 6.0.0-prerelease,
/// which changes the engine and the on-disk format. Upgrading would put NU5104 on two packable
/// assemblies for a prerelease dependency — the consequence Directory.Packages.props already records
/// for its one prerelease pin — and would need a file-format migration for twelve existing stores.
/// Revisit when 6.0.0 goes stable, as its own change with that migration. Until then the warm-up is
/// both the available fix and the stronger one, because it removes the concurrent first touch rather
/// than merely locking the cache around it.
/// </remarks>
public static class LiteDbDocumentMapper
{
    /// <summary>The mapper <see cref="Warmed"/> describes. Guarded by that mapper's own monitor.</summary>
    private static BsonMapper? _gatedMapper;

    /// <summary>Types already built against <see cref="_gatedMapper"/>. Guarded by that mapper's own monitor.</summary>
    private static readonly HashSet<Type> Warmed = new();

    /// <summary>
    /// Ensures <typeparamref name="TDoc"/> is fully mapped on the current <see cref="BsonMapper.Global"/>,
    /// building it under a process-wide lock if it is not.
    /// </summary>
    /// <typeparam name="TDoc">A LiteDB document type — including one reached only as a nested element.</typeparam>
    /// <remarks>
    /// Call this at the top of every public method of a LiteDB-backed store, and from its
    /// constructor. The constructor alone is not enough: a caller holding a store across a
    /// <c>BsonMapper.Global</c> swap would first-touch a cold mapper on its next call. After the
    /// first build the call is one uncontended lock and a hash lookup, which is nothing beside the
    /// <c>new LiteDatabase(...)</c> that follows it. Call it BEFORE taking the store's own lock, so
    /// this monitor is never nested inside one.
    ///
    /// <c>ToDocument</c> rather than <c>GetEntityMapper</c>: the latter is nonpublic in 5.0.21, and
    /// the former both calls it and WALKS the member list, so the members are proven enumerable —
    /// not merely allocated — before another thread can see them.
    ///
    /// Warm every type LiteDB will serialize, not every type that names a collection. Serializing an
    /// empty parent does not reach a child element type, so a child needs its own call: warming only
    /// <c>PipelineRunDocument</c> and not <c>PipelineStageRunDocument</c> still wrote 146-160 of
    /// ~480 raced stage sub-documents with fields missing, and on net10.0 without throwing once.
    /// </remarks>
    public static void EnsureMapped<TDoc>()
        where TDoc : new()
    {
        // Read the static exactly once. A second read could see a different instance, and then the
        // mapper we compared against would not be the mapper we built into.
        var mapper = BsonMapper.Global;

        // The gate is the MAPPER INSTANCE, not a private static of this class, and it is one gate for
        // every document type rather than one per type. Two reasons, in that order.
        //
        // One gate for all types, because a per-type lock would still let two DIFFERENT types build
        // concurrently, and that is an unsynchronised TryGetValue against a Dictionary another thread
        // may be resizing — the second hazard above, independent of whether either type is half-built.
        //
        // The mapper instance rather than a private static, because Ashlar.Commercial.Fleet.Infrastructure
        // owns two more document types and deliberately does not reference this assembly (it is a lean
        // fleet-node library; this one carries Roslyn, Docker.DotNet and LlamaSharp). Its copy of this
        // gate locks the same mapper, so the two assemblies serialise against each other without a
        // shared reference. Locking an object this code does not own is normally a mistake; it is safe
        // here because LiteDB 5.0.21 locks only _entities, _cacheName, _cacheCtor, _stream, _header,
        // _free and _transactions — never a BsonMapper instance and never `this` inside BsonMapper — so
        // nothing inside ToDocument can contend for it.
        lock (mapper)
        {
            if (!ReferenceEquals(_gatedMapper, mapper))
            {
                // A different mapper instance has nothing built against it. Production never assigns
                // BsonMapper.Global — every production reference is a read — but the concurrency tests
                // that prove this fix do, and a plain bool or a Lazy<T> would report those cold types
                // as warm and leave them racing while the suite went green.
                Warmed.Clear();
                _gatedMapper = mapper;
            }

            if (!Warmed.Add(typeof(TDoc)))
                return;

            mapper.ToDocument(typeof(TDoc), new TDoc());
        }
    }
}
