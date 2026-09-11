namespace Ashlar.Core.Application.Persistence;

/// <summary>
/// Composes the connection string every LiteDB-backed store opens with.
/// </summary>
/// <remarks>
/// <para>The one place <c>Connection=Shared</c> is spelled. LiteDB's default is
/// <c>Connection=Direct</c>, which takes an exclusive file lock for the LIFETIME of a
/// <c>LiteDatabase</c> instance — and every store method in this repository opens one per call
/// (<c>using var db = new LiteDatabase</c>, once per method). Two calls that overlap therefore
/// race for the same file. On Windows the loser throws
/// <c>IOException("...because it is being used by another process")</c>, which is how UAT tier 10
/// found it: a copilot request that had already RUN its task lost its record. On Linux the second
/// open is not refused at all — the writers interleave and corrupt each other's pages, so the same
/// race is silent, which is why CI stayed green while a developer's box went red. Measured on the
/// real stores at 20 trials x 8 threads x 250 inserts, Direct lost 92-99.5% of the writes with no
/// exception on most of them; Shared lost none.</para>
///
/// <para>Shared mode serialises the engine through a named mutex instead, so concurrent opens
/// queue rather than collide, and the protection extends to a SECOND PROCESS on the same machine
/// (the CLI and the API both resolve their stores under <c>ASHLAR_STATE_DIR</c>). The mutex is held
/// per OPERATION, not for the lifetime of the <c>LiteDatabase</c>: it makes each call durable, it
/// does NOT make a read-modify-write atomic, and it does not reach across containers, pods or a
/// network mount.</para>
///
/// <para>This lives in Core.Application, not in either infrastructure assembly, because all three
/// LiteDB-bearing projects reference it and none of them reference each other — the alternative was
/// a hand-copied helper in commercial, which is exactly the shape that let the previous two rounds
/// of this bug fix one store and leave the rest. It is pure string manipulation and takes no
/// dependency on LiteDB, so the netstandard2.0 leg of this assembly still compiles; that leg is also
/// why the <c>Connection=</c> probe below is <c>IndexOf</c> rather than
/// <c>Contains(string, StringComparison)</c>, which netstandard2.0 does not have.</para>
/// </remarks>
public static class LiteDbConnectionString
{
    private const string FilenamePrefix = "Filename=";
    private const string ConnectionKey = "Connection=";

    /// <summary>
    /// Normalizes a store's configured path (or connection string) into one that opens in Shared mode.
    /// </summary>
    /// <param name="pathOrConnectionString">
    /// A bare file path (<c>/var/state/patterns.db</c>) or an already-formed LiteDB connection string
    /// (<c>Filename=patterns.db;Connection=Direct</c>).
    /// </param>
    /// <param name="paramName">
    /// Parameter name to report on a rejected argument, so the store's own constructor parameter is
    /// named rather than this method's.
    /// </param>
    /// <returns>A connection string carrying an explicit <c>Connection=</c> mode.</returns>
    /// <exception cref="ArgumentException">
    /// The value is blank, or is a bare path containing <c>;</c> — the separator this method is about
    /// to compose with, which would otherwise silently truncate the filename into a partial path and
    /// a garbage option.
    /// </exception>
    /// <remarks>
    /// An explicit <c>Connection=</c> already supplied by the caller is left exactly as it is. That is
    /// deliberate and it is also this helper's blind spot: a deployment that binds
    /// <c>...;Connection=Direct</c> from configuration gets Direct, and no convention test can see it.
    /// </remarks>
    public static string ForSharedAccess(string pathOrConnectionString, string paramName = "pathOrConnectionString")
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
            throw new ArgumentException("A LiteDB file path or connection string is required.", paramName);

        // netstandard2.0 has no nullable annotation on IsNullOrWhiteSpace; the '!' is safe here.
        var trimmed = pathOrConnectionString!.Trim();

        string withFilename;
        if (trimmed.StartsWith(FilenamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            withFilename = trimmed;
        }
        else
        {
            if (trimmed.IndexOf(';') >= 0)
            {
                throw new ArgumentException(
                    "A LiteDB file path may not contain ';' — it separates connection-string options, "
                    + "so the path would be silently cut short. Pass a full connection string "
                    + "(Filename=...) if that is what was meant.",
                    paramName);
            }

            withFilename = FilenamePrefix + trimmed;
        }

        return withFilename.IndexOf(ConnectionKey, StringComparison.OrdinalIgnoreCase) >= 0
            ? withFilename
            : withFilename + ";Connection=Shared";
    }
}
