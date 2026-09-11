using LiteDB;

namespace Ashlar.Commercial.Fleet.Infrastructure;

/// <summary>
/// The fleet assembly's half of the LiteDB cold-mapper gate. Same mechanism, same monitor, and the
/// same reasons as <c>Ashlar.Infrastructure.Persistence.LiteDbDocumentMapper</c> — read that one for
/// what the race is and why the warm-up closes it.
/// </summary>
/// <remarks>
/// Why a second copy rather than a reference: this assembly is a lean fleet-node library whose only
/// package dependencies are LiteDB and the Microsoft.Extensions abstractions, while Ashlar.Infrastructure
/// carries Roslyn, Docker.DotNet, LlamaSharp, QuestPDF, Npgsql and SQLite. Taking that reference to
/// reach ten lines of gate would be a worse trade than restating them.
///
/// The copies are not independent, which is the part that matters. Both lock the <c>BsonMapper</c>
/// INSTANCE, so a build here and a build there are serialised against each other even though neither
/// assembly can see the other's statics — and they must be, because LiteDB's mapper cache is one
/// unsynchronised <c>Dictionary</c> shared by every document type in the process, so two different
/// types building at once is a hazard on its own. Changing either copy's lock target to a private
/// static would silently reopen that.
/// </remarks>
internal static class LiteDbDocumentMapper
{
    /// <summary>The mapper <see cref="Warmed"/> describes. Guarded by that mapper's own monitor.</summary>
    private static BsonMapper? _gatedMapper;

    /// <summary>Types already built against <see cref="_gatedMapper"/>. Guarded by that mapper's own monitor.</summary>
    private static readonly HashSet<Type> Warmed = new();

    /// <summary>
    /// Ensures <typeparamref name="TDoc"/> is fully mapped on the current <see cref="BsonMapper.Global"/>,
    /// building it under a process-wide lock if it is not.
    /// </summary>
    /// <typeparam name="TDoc">A LiteDB document type.</typeparam>
    internal static void EnsureMapped<TDoc>()
        where TDoc : new()
    {
        // Read the static exactly once; see the sibling in Ashlar.Infrastructure.
        var mapper = BsonMapper.Global;

        lock (mapper)
        {
            if (!ReferenceEquals(_gatedMapper, mapper))
            {
                Warmed.Clear();
                _gatedMapper = mapper;
            }

            if (!Warmed.Add(typeof(TDoc)))
                return;

            mapper.ToDocument(typeof(TDoc), new TDoc());
        }
    }
}
