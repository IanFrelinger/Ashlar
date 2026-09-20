using Ashlar.Abstractions;
using Ashlar.Core.Application.Paths;

namespace Ashlar.Tools.Dev;

/// <summary>
/// Resolves the filesystem root a repo tool is allowed to operate under.
///
/// <para>THE ROOT COMES FROM THE SANDBOX, NEVER FROM THE MODEL. Every repo tool used to
/// declare a <c>root</c> property in its schema and combine it with the model-supplied
/// <c>path</c>:</para>
/// <code>
/// var full = Path.Combine(args.root, args.path);   // args.root came from the LLM
/// </code>
/// <para><c>PathAllowlist.Approve</c> inspects only <c>path</c>. It correctly rejects
/// traversal and absolute paths, and correctly bounds absolute paths to the sandbox — but it
/// never looks at <c>root</c>, because nothing told it to. So a call with
/// <c>root</c> pointing anywhere on disk and a perfectly ordinary relative <c>path</c>
/// beginning <c>src/</c> passed every policy and wrote outside the repository. The tool also
/// called <c>Directory.CreateDirectory</c> on the combined path, so it created the target
/// directory too.</para>
///
/// <para>The fix is structural rather than another validation rule: the model is no longer
/// asked for a root, and the tools no longer accept one. The authoritative root is
/// <c>RepoRoot</c> in the <see cref="WorldSnapshot"/>, which is set by whoever built the
/// sandbox — the self-extend runner, the MCP snapshot factory, the CLI — and is not
/// reachable from tool arguments.</para>
///
/// <para>Resolution FAILS CLOSED. A snapshot without a RepoRoot yields a rejection, not a
/// guess and not the process working directory. Guessing is how this class of defect
/// reappears.</para>
/// </summary>
public static class ToolSandbox
{
    /// <summary>The snapshot key holding the authoritative repository root.</summary>
    public const string RepoRootKey = "RepoRoot";

    /// <summary>
    /// Resolves the sandbox root from the snapshot.
    /// </summary>
    /// <param name="snapshot">The world snapshot supplied by the host, not by the model.</param>
    /// <param name="root">The resolved absolute root, when this returns true.</param>
    /// <param name="reason">A rejection reason, when this returns false.</param>
    /// <returns>True when a usable root was found.</returns>
    public static bool TryResolveRoot(WorldSnapshot snapshot, out string root, out string reason)
    {
        root = string.Empty;
        reason = string.Empty;

        if (snapshot?.Data is null ||
            !snapshot.Data.TryGetValue(RepoRootKey, out var value) ||
            value is not string declared ||
            string.IsNullOrWhiteSpace(declared))
        {
            reason = $"REJECTED: no {RepoRootKey} in the world snapshot. The sandbox root is " +
                     "supplied by the host, never by tool arguments, and is not guessed.";
            return false;
        }

        try
        {
            root = Path.GetFullPath(declared);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = $"REJECTED: {RepoRootKey} is not a usable path: {declared}";
            return false;
        }
    }

    /// <summary>
    /// The READ-intent resolver: resolves a model-supplied relative path against the sandbox
    /// root, refusing anything that lands outside it. Containment only — a read of
    /// <c>.ashlar/</c>, a build import or the operator policy is legitimate, so nothing here
    /// judges what the path IS. A tool that will WRITE the resolved path must call
    /// <see cref="TryResolveWritePath"/> instead; <c>WriteToolResolverConventionTests</c> fails
    /// the build of a mutating tool that calls this one.
    ///
    /// <para>This is belt-and-braces with <c>PathAllowlist</c>, deliberately. The allowlist
    /// is a policy and can be swapped or omitted — tools are reachable from the MCP bridge,
    /// the gRPC transport and the CLI, not only through the background-agent policy engine —
    /// so containment is enforced here too, where it cannot be configured away.</para>
    /// </summary>
    /// <param name="snapshot">The world snapshot.</param>
    /// <param name="relativePath">The model-supplied path.</param>
    /// <param name="full">The resolved absolute path, when this returns true.</param>
    /// <param name="reason">A rejection reason, when this returns false.</param>
    /// <returns>True when the path resolves inside the sandbox.</returns>
    public static bool TryResolvePath(
        WorldSnapshot snapshot,
        string? relativePath,
        out string full,
        out string reason)
    {
        full = string.Empty;

        if (!TryResolveRoot(snapshot, out var root, out reason))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            // Wording matters: existing tests assert on the keywords "required" here and
            // "traversal" below. Those are the established vocabulary for these rejections,
            // and other consumers may match on them too.
            reason = "REJECTED: path is required";
            return false;
        }

        // An absolute path would make Path.Combine discard the root entirely.
        if (Path.IsPathRooted(relativePath))
        {
            reason = $"REJECTED: absolute path not permitted: {relativePath}";
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = $"REJECTED: invalid path: {relativePath}";
            return false;
        }

        // GetFullPath has already collapsed any "..", so this catches traversal without
        // needing to pattern-match on the raw string.
        if (!IsWithin(candidate, root))
        {
            reason = $"REJECTED: path traversal outside the sandbox root not permitted: {relativePath}";
            return false;
        }

        full = candidate;
        return true;
    }

    /// <summary>
    /// The WRITE-intent resolver: everything <see cref="TryResolvePath"/> does, then the
    /// authoring governance floor — <see cref="MediatedWritePath.RefuseAuthoringWrite"/> — on
    /// the contained path. The admission ledger under <c>.ashlar/</c>, a build import, a
    /// tooling config and the operator policy are refused under every spelling, a symlink or
    /// junction anywhere on the path is refused, and a project or solution file still lands.
    ///
    /// <para>Here and not only in the policy engine, for the same reason containment is here:
    /// the policy chain is composed per host and reachable from four edges, and the one that
    /// wrote the ledger was composed with <c>.ashlar/</c> on its allowlist. A sibling of the
    /// read resolver rather than a change to it, because <c>repo.fs.read</c> and
    /// <c>repo.fs.list</c> must keep reading <c>.ashlar/</c>.</para>
    ///
    /// <para>The floor runs ONCE per real write and never throws out of here. It probes the
    /// filesystem for reparse points, and a probe on an ancestor the process cannot stat
    /// throws; a tool exception escapes into <c>ToolCallingAgent.RunCycleAsync</c>'s outer
    /// handler and ends the whole cycle, so any throw is converted to a rejection.</para>
    /// </summary>
    /// <param name="snapshot">The world snapshot.</param>
    /// <param name="relativePath">The model-supplied path the tool intends to write.</param>
    /// <param name="full">The resolved absolute path, when this returns true.</param>
    /// <param name="reason">A rejection reason, when this returns false.</param>
    /// <returns>True when the path resolves inside the sandbox AND is beneath the governance floor.</returns>
    public static bool TryResolveWritePath(
        WorldSnapshot snapshot,
        string? relativePath,
        out string full,
        out string reason)
    {
        // The established vocabulary and ordering first: no RepoRoot, "required", absolute,
        // "traversal". Tests and other consumers match on those words.
        if (!TryResolvePath(snapshot, relativePath, out full, out reason))
        {
            return false;
        }

        // TryResolvePath succeeded, so the root resolves; re-derive it rather than widen the
        // read resolver's signature.
        TryResolveRoot(snapshot, out var root, out _);

        string? refusal;
        try
        {
            refusal = MediatedWritePath.RefuseAuthoringWrite(root, relativePath!);
        }
        catch (Exception ex)
        {
            full = string.Empty;
            reason = $"REJECTED: the governance floor could not evaluate '{relativePath}' ({ex.GetType().Name})";
            return false;
        }

        if (refusal is not null)
        {
            full = string.Empty;
            reason = "REJECTED: " + refusal;
            return false;
        }

        return true;
    }

    private static bool IsWithin(string candidate, string root)
    {
        var normalisedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(candidate, normalisedRoot, PathComparison))
        {
            return true;
        }

        return candidate.StartsWith(normalisedRoot + Path.DirectorySeparatorChar, PathComparison)
            || candidate.StartsWith(normalisedRoot + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
