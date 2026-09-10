namespace Ashlar.Core.Application.Paths;

/// <summary>
/// Shared utilities for resolving repository, observation and runtime-state paths.
/// </summary>
public static class RepoPathResolver
{
    /// <summary>
    /// Environment variable that overrides the runtime-state directory (LiteDB stores, snapshots).
    /// Absolute, or relative to the resolved repo/app root. Compose stacks point it at a named volume.
    /// </summary>
    public const string StateDirectoryEnvironmentVariable = "ASHLAR_STATE_DIR";

    /// <summary>
    /// Default runtime-state directory relative to the repo/app root (<c>.ashlar/</c> is gitignored).
    /// </summary>
    public const string DefaultStateDirectoryRelativePath = ".ashlar/state";

    /// <summary>
    /// Environment variable that overrides where promoted-adaptation documentation
    /// (<c>docs/bricks/</c>) is written. Absolute, or relative to the resolved repo/app root.
    /// Unset means the repo root itself: Ashlar documents itself in its own tree.
    /// </summary>
    public const string DocsRootEnvironmentVariable = "ASHLAR_DOCS_ROOT";

    /// <summary>
    /// Walks up from the given directory until Ashlar.sln is found.
    /// </summary>
    /// <param name="startDir">Starting directory. Defaults to <see cref="Environment.CurrentDirectory"/>.</param>
    /// <returns>Repository root directory path, or current directory if not found.</returns>
    public static string FindRepoRoot(string? startDir = null)
    {
        var dir = new DirectoryInfo(startDir ?? Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ashlar.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Returns the Block 1 Observation path when running in the Ashlar repo.
    /// Prefers src/Ashlar.Infrastructure/Observation, then src/Ashlar.BackgroundAgents/Observation.
    /// </summary>
    /// <param name="repoRoot">Repository root. If null, uses <see cref="FindRepoRoot"/>.</param>
    /// <returns>Path to Observation folder, or current directory if not in Ashlar repo.</returns>
    public static string FindBlock1ObservationPath(string? repoRoot = null)
    {
        var root = repoRoot ?? FindRepoRoot();
        var sln = Path.Combine(root, "Ashlar.sln");
        if (!File.Exists(sln))
            return Directory.GetCurrentDirectory();

        var infraObs = Path.Combine(root, "src", "Ashlar.Infrastructure", "Observation");
        var bgObs = Path.Combine(root, "src", "Ashlar.BackgroundAgents", "Observation");
        if (Directory.Exists(infraObs))
            return infraObs;
        if (Directory.Exists(bgObs))
            return bgObs;
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Resolves the directory that holds Ashlar runtime state (LiteDB stores such as
    /// <c>ashlar-patterns.db</c>, <c>ashlar-adaptation.db</c>, <c>ashlar-copilot-tasks.db</c>, and
    /// <c>ashlar-snapshots/</c>). Reads <see cref="StateDirectoryEnvironmentVariable"/> and defers to
    /// <see cref="ResolveStateDirectory(string, string?)"/>.
    /// </summary>
    /// <param name="repoRoot">Repository / application root. If null, uses <see cref="FindRepoRoot"/>.</param>
    /// <returns>Absolute state directory path. Created when it does not exist yet (best effort).</returns>
    public static string ResolveStateDirectory(string? repoRoot = null)
        => ResolveStateDirectory(
            repoRoot ?? FindRepoRoot(),
            Environment.GetEnvironmentVariable(StateDirectoryEnvironmentVariable));

    /// <summary>
    /// Resolves the runtime-state directory from an explicit root and an optional override.
    /// Precedence: <paramref name="configuredStateDirectory"/> (absolute, or relative to
    /// <paramref name="repoRoot"/>) → legacy layout (state files already sitting at
    /// <paramref name="repoRoot"/> and no <c>.ashlar/state/</c> yet — kept so existing installs keep
    /// reading their data; move the <c>ashlar-*.db</c> files and <c>ashlar-snapshots/</c> into
    /// <c>.ashlar/state/</c> to migrate) → <c>&lt;repoRoot&gt;/.ashlar/state</c>.
    /// </summary>
    /// <param name="repoRoot">Repository / application root the default and relative overrides hang off.</param>
    /// <param name="configuredStateDirectory">Override (normally the <c>ASHLAR_STATE_DIR</c> value); null or blank = none.</param>
    /// <returns>Absolute state directory path. Created when it does not exist yet (best effort).</returns>
    public static string ResolveStateDirectory(string repoRoot, string? configuredStateDirectory)
    {
        var root = string.IsNullOrWhiteSpace(repoRoot) ? Directory.GetCurrentDirectory() : repoRoot;

        string stateDir;
        if (!string.IsNullOrWhiteSpace(configuredStateDirectory))
        {
            // netstandard2.0 has no nullable annotation on IsNullOrWhiteSpace; the '!' is safe here.
            var configured = configuredStateDirectory!.Trim();
            stateDir = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(root, configured));
        }
        else
        {
            stateDir = Path.GetFullPath(Path.Combine(root, DefaultStateDirectoryRelativePath));
            if (!Directory.Exists(stateDir) && HasLegacyStateFiles(root))
                return Path.GetFullPath(root);
        }

        try
        {
            Directory.CreateDirectory(stateDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave it to the store that first opens a file here to surface the real error;
            // resolving a path must not take a host down at DI time.
        }

        return stateDir;
    }

    /// <summary>
    /// Resolves the root that promoted-adaptation documentation (<c>docs/bricks/</c>) is written
    /// beneath. Reads <see cref="DocsRootEnvironmentVariable"/> and defers to
    /// <see cref="ResolveDocsRoot(string, string?)"/>.
    /// </summary>
    /// <param name="repoRoot">Repository / application root. If null, uses <see cref="FindRepoRoot"/>.</param>
    /// <returns>Absolute documentation root. Not created here; the writer creates <c>docs/bricks/</c> when it has something to put there.</returns>
    public static string ResolveDocsRoot(string? repoRoot = null)
        => ResolveDocsRoot(
            repoRoot ?? FindRepoRoot(),
            Environment.GetEnvironmentVariable(DocsRootEnvironmentVariable));

    /// <summary>
    /// Resolves the documentation root from an explicit root and an optional override.
    /// Precedence: <paramref name="configuredDocsRoot"/> (absolute, or relative to
    /// <paramref name="repoRoot"/>) → <paramref name="repoRoot"/>.
    /// </summary>
    /// <remarks>
    /// The override exists for a process that cannot be handed a path in code. The CLI E2E tests
    /// run <c>ashlar improve</c> as a child process that composes its own container, so a
    /// promotion there documented itself wherever that process resolved the root — the
    /// developer's checkout, as <c>docs/bricks/unknown.md</c> (#580). The environment is the one
    /// channel a test host has into that process, and it is the same channel
    /// <see cref="StateDirectoryEnvironmentVariable"/> already uses for the state directory.
    /// </remarks>
    /// <param name="repoRoot">Repository / application root the default and relative overrides hang off.</param>
    /// <param name="configuredDocsRoot">Override (normally the <c>ASHLAR_DOCS_ROOT</c> value); null or blank = none.</param>
    /// <returns>Absolute documentation root.</returns>
    public static string ResolveDocsRoot(string repoRoot, string? configuredDocsRoot)
    {
        var root = string.IsNullOrWhiteSpace(repoRoot) ? Directory.GetCurrentDirectory() : repoRoot;
        if (string.IsNullOrWhiteSpace(configuredDocsRoot))
            return Path.GetFullPath(root);

        // netstandard2.0 has no nullable annotation on IsNullOrWhiteSpace; the '!' is safe here.
        var configured = configuredDocsRoot!.Trim();
        return Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(root, configured));
    }

    /// <summary>
    /// True when pre-<c>.ashlar/state</c> LiteDB state files (<c>ashlar-*.db</c>) live directly at <paramref name="root"/>.
    /// </summary>
    private static bool HasLegacyStateFiles(string root)
    {
        try
        {
            return Directory.Exists(root) && Directory.EnumerateFiles(root, "ashlar-*.db").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
