namespace Ashlar.Core.Application.Paths;

/// <summary>
/// The single authority for whether a MEDIATED write — a forge-applied proposal, an imported
/// package file, or an adopted shared adaptation — may land at a target under a project/repo root.
/// Every writer of externally-influenced content routes through <see cref="Refuse"/>, so the
/// governance floor is defined once rather than re-derived per writer. That divergence is exactly
/// what let the mesh-adopt path (<c>FileBasedSharedAdaptationStore</c>) write with no floor at all
/// while the forge path had one.
///
/// <para>The floor, in order:</para>
/// <list type="number">
///   <item>the raw target is a SAFE relative path — no <c>.</c>, <c>..</c>, empty, rooted,
///   drive-letter, reserved-device, or trailing dot/space segment;</item>
///   <item>it stays inside the root once resolved (no escape);</item>
///   <item>its normalized form is not a GOVERNANCE or build-executed path;</item>
///   <item>when an allowlist is supplied, it lands under one of its entries;</item>
///   <item>no symlink/junction on the path — or at the leaf — could carry the write elsewhere.</item>
/// </list>
///
/// <para>Lives in <c>Ashlar.Core.Application</c>, which multi-targets <c>netstandard2.0</c>, so this
/// is written to the ns2.0 API surface deliberately: no <c>Path.GetRelativePath</c>, no
/// <c>string.Contains(char, …)</c>/<c>EndsWith(char)</c>, no <c>System.Index</c> (<c>[^1]</c>).</para>
/// </summary>
public static class MediatedWritePath
{
    /// <summary>
    /// Returns <see langword="null"/> when a mediated write to <paramref name="target"/> under
    /// <paramref name="repoRoot"/> is allowed; otherwise a human-readable refusal reason. Pure
    /// apart from reparse-point probes of already-existing paths (a filesystem read, never a write).
    /// </summary>
    public static string? Refuse(string repoRoot, string target, IReadOnlyList<string>? writableAllowlist = null)
        => RefuseCore(repoRoot, target, writableAllowlist, authoringEdge: false);

    /// <summary>
    /// The same floor for an AUTHORING write — one a self-extend cycle makes directly through
    /// <c>repo.fs.write</c> and its siblings — judged against <see cref="IsAuthoringGovernancePath"/>
    /// instead of <see cref="IsGovernancePath"/>, so a project or solution file passes. Every other
    /// leg (safe shape, containment, the reparse-point probes) is the mediated floor's, byte for
    /// byte: one private core, so a fourth copy of the containment and symlink legs cannot drift.
    ///
    /// <para>No allowlist parameter. WHERE an authoring write may land is the policy engine's
    /// decision (<c>PathAllowlist</c>) and is configurable; this is the floor beneath it that is
    /// not.</para>
    /// </summary>
    public static string? RefuseAuthoringWrite(string repoRoot, string target)
        => RefuseCore(repoRoot, target, writableAllowlist: null, authoringEdge: true);

    private static string? RefuseCore(string repoRoot, string target, IReadOnlyList<string>? writableAllowlist, bool authoringEdge)
    {
        // Order matters: each spelling should be refused for the truest reason. Escapes are named
        // as escapes and governance targets as governance, before the catch-all safe-path check —
        // the established error vocabulary two admission phases assert on.
        if (string.IsNullOrWhiteSpace(target))
        {
            return "an empty target is not a safe repo-relative path.";
        }
        if (target.IndexOf(':') >= 0)
        {
            return $"'{target}' is not a safe repo-relative path: a ':' is a drive letter or NTFS alternate data stream.";
        }
        if (target[0] == '/' || target[0] == '\\')
        {
            return $"'{target}' is rooted and escapes the project root.";
        }

        var rootFull = Path.GetFullPath(repoRoot);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(rootFull, target));

        if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            return $"'{target}' escapes the project root.";
        }

        // Governance is judged on the NORMALIZED, root-relative form. Path.GetFullPath has already
        // collapsed '.', '..' and mixed separators, so every spelling ('./x', 'a/../.ashlar/y')
        // reduces to the path the write will actually hit. fullPath is known to be under
        // rootWithSep, so the substring is the clean relative form (ns2.0 has no GetRelativePath).
        var normalizedRel = fullPath.Substring(rootWithSep.Length).Replace('\\', '/');
        if (authoringEdge ? IsAuthoringGovernancePath(normalizedRel) : IsGovernancePath(normalizedRel))
        {
            return authoringEdge
                ? $"'{target}' (resolves to '{normalizedRel}') is a governance path — the project "
                    + "contract, the operator policy, .ashlar/ state, or a build or tooling file the next "
                    + "build would honour. A cycle cannot author it, directly or through "
                    + "forge.propose_change; a change here is an operator's to make."
                : $"'{target}' (resolves to '{normalizedRel}') is a governance path — the project "
                    + "contract, the operator policy, .ashlar/ state, or a build file the receiver's next "
                    + "build would execute.";
        }

        // Remaining unsafe spellings that neither escaped nor hit governance: an in-root '.'/'..',
        // a reserved device name, a trailing dot/space. Reuses the standalone predicate.
        if (!IsSafeRelativePath(target))
        {
            return $"'{target}' is not a safe repo-relative path: no '.', '..', empty, reserved-device "
                + "or trailing dot/space segments.";
        }

        if (writableAllowlist != null && writableAllowlist.Count > 0 && !IsUnderAllowlist(normalizedRel, writableAllowlist))
        {
            return $"'{target}' (resolves to '{normalizedRel}') is outside the policy's writable allowlist.";
        }

        if (TraversesReparsePoint(fullPath, rootFull) || PathIsReparsePoint(fullPath))
        {
            return $"'{target}' runs through — or ends at — a symlink or junction that could leave "
                + "the project root; lexical containment is not enough when a link is in the way.";
        }

        return null;
    }

    /// <summary>
    /// The governance floor for a MEDIATED write — a forge-applied proposal, an imported package
    /// file, an adopted shared adaptation. Strictly wider than
    /// <see cref="IsAuthoringGovernancePath"/>: it is that set PLUS project and solution files,
    /// which a mediated write has no business creating.
    ///
    /// <para>Defined as a superset in code rather than as a second list, so the relationship cannot
    /// drift. Weakening the shared half now visibly weakens BOTH edges, which is the point: an
    /// earlier shape of this fix put a narrowed copy beside the original, and the copy is the one
    /// that would have rotted.</para>
    /// </summary>
    public static bool IsGovernancePath(string relativePath)
    {
        if (IsAuthoringGovernancePath(relativePath))
        {
            return true;
        }

        var projectSegments = relativePath.Split('/', '\\');
        if (projectSegments.Length == 0)
        {
            return false;
        }
        var projectFileName = projectSegments[projectSegments.Length - 1];
        foreach (var suffix in GovernanceProjectFileSuffixes)
        {
            if (projectFileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The governance floor for an AUTHORING write — one a self-extend cycle makes directly through
    /// <c>repo.fs.write</c> and its siblings. Everything <see cref="IsGovernancePath"/> refuses
    /// EXCEPT project and solution files, which the authoring edge legitimately scaffolds.
    ///
    /// <para>There is no carve-out under <c>.ashlar/</c>, and there must not be one.
    /// <c>.ashlar/gates/</c> is the admission ledger that bounds how often a cycle may extend the
    /// system, so a cycle that can write it has no budget. An earlier draft of this fix carved out
    /// <c>.ashlar/tools/</c> and <c>.ashlar/host_apps/</c> to keep an agent sandbox working; that
    /// hands a cycle the NuGet package cache and the host-app project root, which means an analyzer
    /// assembly Roslyn loads on the next build — build-time code execution, which no name-based
    /// check can see. The sandbox belongs outside <c>.ashlar/</c> instead.</para>
    ///
    /// <para>Case-insensitive, because the filesystems this runs on are and a refusal must not be
    /// escapable by spelling.</para>
    /// </summary>
    public static bool IsAuthoringGovernancePath(string relativePath)
    {
        var segments = relativePath.Split('/', '\\');
        if (segments.Length == 0)
        {
            return false;
        }

        // Contract/policy: governance ONLY at the repo root. A nested file of the same name is
        // inert to the loader, so a mediated write may legitimately create it; denying it anywhere
        // would over-block.
        if (segments.Length == 1
            && (string.Equals(segments[0], "ashlar.yaml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segments[0], "ashlar.policy.yaml", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Directory prefixes: everything beneath these is governance state, version control, CI,
        // editor/devcontainer config, or the project's own scripts. Keyed on the FIRST segment.
        foreach (var dir in GovernanceDirPrefixes)
        {
            if (string.Equals(segments[0], dir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Files dangerous at ANY depth: MSBuild, NuGet, the .NET SDK, make, pre-commit and the
        // analyzer config are all discovered by walking the tree (or sit at a well-known name).
        var fileName = segments[segments.Length - 1];
        foreach (var name in GovernanceFileNames)
        {
            if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Build imports, any depth: Directory.Build.*, Directory.Packages.props,
        // Directory.Solution.*, before./after.<sln>.sln.targets and any custom-<Import>ed file all
        // end .props/.targets. Project and solution files are NOT here — they are the half
        // IsGovernancePath adds for the mediated edge.
        foreach (var suffix in GovernanceBuildImportSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A safe relative path: no drive letter/ADS (<c>:</c>), not rooted, no empty/<c>.</c>/<c>..</c>
    /// segment, no trailing dot or space (Win32 strips them, aliasing files), and no Win32 reserved
    /// device-name stem — denied on every OS so a path legal at the origin cannot become an
    /// unwritable or aliased path the moment it is applied on Windows.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        if (path!.IndexOf(':') >= 0)
        {
            return false;
        }
        if (path[0] == '/' || path[0] == '\\')
        {
            return false;
        }
        var segments = path.Split('/', '\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
            var last = segment[segment.Length - 1];
            if (last == '.' || last == ' ')
            {
                return false;
            }
            var stem = segment.Split('.')[0];
            if (Win32Reserved.Contains(stem))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsUnderAllowlist(string normalizedRel, IReadOnlyList<string> allowlist)
    {
        foreach (var entry in allowlist)
        {
            var e = entry.Replace('\\', '/').Trim('/');
            if (e.Length == 0)
            {
                continue;
            }
            if (string.Equals(normalizedRel, e, StringComparison.OrdinalIgnoreCase)
                || normalizedRel.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when any existing directory from just below the root down to the target's parent is a
    /// reparse point. <see cref="Path.GetFullPath(string)"/> normalizes <c>..</c> lexically but does
    /// NOT follow links, so a junction ancestor could carry a lexically-in-root write to a real
    /// location outside the root.
    /// </summary>
    private static bool TraversesReparsePoint(string targetFullPath, string rootFull)
    {
        var dir = Path.GetDirectoryName(targetFullPath);
        while (dir != null && dir.Length > rootFull.Length && dir.StartsWith(rootFull, StringComparison.Ordinal))
        {
            if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    /// <summary>
    /// True when the target itself already exists and is a symlink/junction. The ancestor walk in
    /// <see cref="TraversesReparsePoint"/> stops at the parent, so without this a pre-planted leaf
    /// link (e.g. docs/site.yaml -&gt; ../ashlar.policy.yaml) would be followed by the write and
    /// truncate the link's target.
    /// </summary>
    private static bool PathIsReparsePoint(string fullPath)
    {
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return false;
        }
        return (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0;
    }

    private static readonly string[] GovernanceDirPrefixes =
    {
        ".ashlar", ".git", ".github", ".vscode", ".devcontainer", "scripts",
    };

    private static readonly string[] GovernanceFileNames =
    {
        // Contract/policy are handled root-only above; these are governance/build-executed files at
        // ANY depth (MSBuild/NuGet/SDK/make/pre-commit/analyzer discovery all walk the tree).
        "nuget.config", "global.json",
        "Makefile", "GNUmakefile",           // GNU make reads GNUmakefile before Makefile; OIC covers makefile/MAKEFILE
        ".editorconfig",                      // a nested one can set analyzer severity=none, silencing the repo's gates
        ".globalconfig",                      // the auto-included analyzer config; silences gates exactly like .editorconfig. NOTE: closes only the literal name — a global config carried under an arbitrary name via <GlobalAnalyzerConfigFiles> is content-flagged (is_global=true), not name-flagged, so leaf-matching cannot cover it; that residual (overwriting an already-referenced named config) is tracked separately.
        "dotnet-tools.json",                  // the .NET local-tool manifest, discovered by ancestor-walk at any depth (canonically .config/dotnet-tools.json); a restored tool runs on the next `dotnet tool restore`/build
        ".pre-commit-config.yaml",            // a `repo: local` hook runs on the next git commit
    };

    // Split deliberately, because the two write edges need different answers here and nowhere else.
    //
    // A build IMPORT is discovered by ancestor-walk: dropping a Directory.Build.props anywhere above a
    // project changes how that project builds, and an <Import>ed .targets can add an <Exec> or silence an
    // analyzer. Nothing legitimately authors one of these from inside a cycle, on either edge.
    private static readonly string[] GovernanceBuildImportSuffixes =
    {
        ".props", ".targets",                                   // every MSBuild import, incl. Directory.Solution.*
    };

    // A PROJECT file is the repository's own authoring surface. It is still governance for a mediated
    // write — an imported package or an adopted adaptation has no business creating one — but the
    // authoring edge must be able to scaffold a project, so IsAuthoringGovernancePath omits this set.
    // See IsGovernancePath for how the two relate, and the subset property test that freezes it.
    private static readonly string[] GovernanceProjectFileSuffixes =
    {
        ".csproj", ".fsproj", ".vbproj", ".proj", ".sln", ".slnx",
    };

    private static readonly HashSet<string> Win32Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
}
