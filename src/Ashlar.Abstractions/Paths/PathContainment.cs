using System.Runtime.InteropServices;
using System.Security;

namespace Ashlar.Abstractions.Paths;

/// <summary>
/// The one lexical answer to "is this path the root, or inside it?". Every containment decision in
/// shipped code calls this instead of re-deriving it: ten hand-written copies had drifted into five
/// different semantics, and one of them (<c>OutputPathSandboxed</c>) compared without a directory
/// separator, so <c>/srv/app/out-evil</c> passed as inside <c>/srv/app/out</c>.
/// <c>PathContainmentConventionTests</c> fails cert-gate on a new hand-written copy it can recognise
/// (its remarks list the shapes it cannot), and <c>PathContainmentTests</c> runs adversarial tables
/// against the call sites themselves.
///
/// <para><b>What it does, in order.</b></para>
/// <list type="number">
///   <item>Refuses (returns <see langword="false"/>) a null, empty or whitespace root or candidate,
///   and one <see cref="Path.GetFullPath(string)"/> cannot normalise (invalid characters, an
///   unsupported format, too long). A path that cannot be resolved is never inside anything.</item>
///   <item>Normalises both with <see cref="Path.GetFullPath(string)"/>: <c>.</c> and <c>..</c>
///   segments collapse, repeated separators collapse, and on Windows <c>/</c> becomes <c>\</c> and a
///   trailing dot or space on a segment is dropped, exactly as the OS will resolve it. A RELATIVE
///   input resolves against the process working directory, not against the other argument, so pass
///   absolute paths (combine a relative candidate with the root first).</item>
///   <item>Refuses a normalised path that still has a <c>.</c> or <c>..</c> segment. Only a Windows
///   <c>\\?\</c> or <c>\\.\</c> path survives normalisation with one, and a lexical prefix test on
///   such a path would read <c>\\?\C:\root\..\evil</c> as inside <c>\\?\C:\root</c>.</item>
///   <item>Drops trailing separators (never below the filesystem root, so <c>/</c> and <c>C:\</c>
///   stay themselves). <c>/srv/app/</c> and <c>/srv/app</c> are the same directory.</item>
///   <item>Compares separator-aware: the candidate is inside when it equals the root, or starts
///   with the root followed by a directory separator. <c>/srv/app/out-evil</c> is NOT inside
///   <c>/srv/app/out</c>. <see cref="IsWithin(string, string)"/> counts the root itself as inside;
///   <see cref="IsStrictlyWithin(string, string)"/> does not.</item>
/// </list>
///
/// <para><b>Case.</b> The two-argument overloads use <see cref="PlatformComparison"/>: ordinal and
/// case-INSENSITIVE on Windows and macOS, whose default file systems fold case, and ordinal
/// case-SENSITIVE everywhere else. On Linux <c>/srv/App/x</c> is a different directory from
/// <c>/srv/app/x</c>, and treating it as inside is an escape. A caller with a stricter contract
/// passes <see cref="StringComparison.Ordinal"/> explicitly. Only the two ordinal comparisons are
/// accepted: a culture-aware comparison can equate strings that name different files.</para>
///
/// <para><b>What it deliberately does NOT do.</b></para>
/// <list type="bullet">
///   <item>It never touches the filesystem, so it does NOT follow symbolic links, junctions or
///   other reparse points: a link inside the root that points outside it is reported as inside.
///   A caller that will WRITE through the answer must probe for links itself —
///   <c>MediatedWritePath</c> does, after its containment leg.</item>
///   <item>It does not model a case-sensitive volume on Windows or macOS (a case-sensitive APFS
///   volume, an NTFS directory with per-directory case sensitivity on). There, a sibling that
///   differs from the root only by case is reported as inside under
///   <see cref="PlatformComparison"/>; pass <see cref="StringComparison.Ordinal"/> where that
///   matters.</item>
///   <item>It does not unify different spellings of one location: 8.3 short names
///   (<c>PROGRA~1</c>), a <c>\\?\</c> prefix on one argument but not the other, a mapped drive
///   against its UNC path, or a bind mount. Each such pair compares as unrelated, which refuses
///   rather than admits.</item>
/// </list>
///
/// <para>Lives in <c>Ashlar.Abstractions</c> because it is the lowest project every copy could
/// reference without a new project reference, and is written to the <c>netstandard2.0</c> API
/// surface: no <c>Path.GetRelativePath</c>, no <c>OperatingSystem</c>, no <c>EndsWith(char)</c>.</para>
/// </summary>
public static class PathContainment
{
    private static readonly StringComparison Platform =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> on Windows and macOS,
    /// <see cref="StringComparison.Ordinal"/> on every other platform: the platform's DEFAULT file
    /// system case rule, which the two-argument overloads use.
    /// </summary>
    public static StringComparison PlatformComparison => Platform;

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> itself or lies inside it,
    /// compared with <see cref="PlatformComparison"/>. See the type remarks for the exact rules.
    /// </summary>
    /// <param name="candidate">The path being admitted. Absolute, or relative to the process working directory.</param>
    /// <param name="root">The directory it must stay inside. Absolute, or relative to the process working directory.</param>
    /// <returns><see langword="false"/> for anything that cannot be normalised.</returns>
    public static bool IsWithin(string? candidate, string? root)
        => Contains(candidate, root, Platform, includeRoot: true);

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> itself or lies inside it,
    /// compared with <paramref name="comparison"/>.
    /// </summary>
    /// <param name="candidate">The path being admitted.</param>
    /// <param name="root">The directory it must stay inside.</param>
    /// <param name="comparison"><see cref="StringComparison.Ordinal"/> or <see cref="StringComparison.OrdinalIgnoreCase"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="comparison"/> is not one of the two ordinal comparisons.</exception>
    public static bool IsWithin(string? candidate, string? root, StringComparison comparison)
        => Contains(candidate, root, RequireOrdinal(comparison), includeRoot: true);

    /// <summary>
    /// True when <paramref name="candidate"/> lies inside <paramref name="root"/> and is not the root
    /// itself, compared with <see cref="PlatformComparison"/>.
    /// </summary>
    /// <param name="candidate">The path being admitted.</param>
    /// <param name="root">The directory it must stay inside.</param>
    public static bool IsStrictlyWithin(string? candidate, string? root)
        => Contains(candidate, root, Platform, includeRoot: false);

    /// <summary>
    /// True when <paramref name="candidate"/> lies inside <paramref name="root"/> and is not the root
    /// itself, compared with <paramref name="comparison"/>.
    /// </summary>
    /// <param name="candidate">The path being admitted.</param>
    /// <param name="root">The directory it must stay inside.</param>
    /// <param name="comparison"><see cref="StringComparison.Ordinal"/> or <see cref="StringComparison.OrdinalIgnoreCase"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="comparison"/> is not one of the two ordinal comparisons.</exception>
    public static bool IsStrictlyWithin(string? candidate, string? root, StringComparison comparison)
        => Contains(candidate, root, RequireOrdinal(comparison), includeRoot: false);

    private static bool Contains(string? candidate, string? root, StringComparison comparison, bool includeRoot)
    {
        if (!TryNormalize(root, out var normalizedRoot) || !TryNormalize(candidate, out var normalizedCandidate))
        {
            return false;
        }

        if (string.Equals(normalizedCandidate, normalizedRoot, comparison))
        {
            return includeRoot;
        }

        // The separator is the whole fix: without it the root's last segment is a prefix of every
        // sibling that extends its name. A filesystem root ("/", "C:\") already ends with one.
        var rootWithSeparator = IsSeparator(normalizedRoot[normalizedRoot.Length - 1])
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(rootWithSeparator, comparison);
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path!);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }

        if (full.Length == 0 || HasDotSegment(full))
        {
            return false;
        }

        normalized = TrimTrailingSeparators(full);
        return true;
    }

    private static bool HasDotSegment(string full)
    {
        foreach (var segment in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment == "." || segment == "..")
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimTrailingSeparators(string full)
    {
        var keep = Path.GetPathRoot(full)?.Length ?? 0;
        var end = full.Length;
        while (end > keep && IsSeparator(full[end - 1]))
        {
            end--;
        }

        return end == full.Length ? full : full.Substring(0, end);
    }

    private static bool IsSeparator(char c)
        => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    private static StringComparison RequireOrdinal(StringComparison comparison)
    {
        if (comparison != StringComparison.Ordinal && comparison != StringComparison.OrdinalIgnoreCase)
        {
            throw new ArgumentException(
                "Path containment compares ordinally; a culture-aware comparison can equate strings that name different files.",
                nameof(comparison));
        }

        return comparison;
    }
}
