using Ashlar.Core.Application.Paths;
using Ashlar.Tests.Infrastructure;

namespace Ashlar.Tests.Infrastructure.Helpers;

/// <summary>
/// Base class for CLI E2E tests. Provides repo root, temp dir, and process timeout for CliRunner.
/// </summary>
public abstract class E2ETestBase : TempDirTestBase
{
    /// <summary>
    /// Repository root (contains Ashlar.sln).
    /// </summary>
    protected string RepoRoot { get; }

    /// <summary>
    /// Default timeout for CLI invocations (55s, under blame-hang 60s).
    /// </summary>
    protected static TimeSpan DefaultCliTimeout => TimeSpan.FromSeconds(55);

    /// <summary>
    /// Creates an E2E test with temp dir and repo root.
    /// </summary>
    /// <param name="prefix">Prefix for the temp dir (e.g. "ashlar-phases59-e2e").</param>
    protected E2ETestBase(string prefix = "ashlar-e2e") : base(prefix)
    {
        RepoRoot = TestPaths.FindRepoRoot();
    }

    /// <summary>
    /// Runs the CLI with timeout to avoid orphaned processes and blame-hang.
    /// </summary>
    /// <remarks>
    /// Every invocation gets <c>ASHLAR_DOCS_ROOT</c> pointed at <see cref="TempDirTestBase.TempDir"/>
    /// unless <paramref name="envOverrides"/> sets it. The CLI composes its own container
    /// in-process, so a promotion in <c>ashlar improve</c> documents itself wherever that process
    /// resolves the docs root — which, for a child of a test host whose working directory is the
    /// repo, was the developer's checkout: <c>docs/bricks/unknown.md</c> (#580). Set here rather
    /// than per call so the dry-run invocations are covered too; they do not promote today, and
    /// the day one does it must not leak the same way.
    /// </remarks>
    /// <param name="cliBuildConfiguration">Pass <c>Release</c> to exercise production-shaped CLI binaries.</param>
    protected Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string args,
        IReadOnlyDictionary<string, string?>? envOverrides = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        string cliBuildConfiguration = "Debug")
    {
        var env = new Dictionary<string, string?>
        {
            [RepoPathResolver.DocsRootEnvironmentVariable] = TempDir,
        };
        if (envOverrides != null)
        {
            foreach (var (k, v) in envOverrides)
                env[k] = v;
        }

        return CliRunner.RunAsync(RepoRoot, args, env, timeout ?? DefaultCliTimeout, ct, cliBuildConfiguration);
    }

    /// <summary>
    /// Last-write stamp of <c>docs/bricks/&lt;brickId&gt;.md</c> in the repository checkout, for a
    /// before/after comparison around a CLI call that promotes. A stamp rather than "does not
    /// exist": residue an older run left in a developer's tree must not fail the test, but this
    /// run must neither create nor rewrite the file. A missing file stamps as a constant.
    /// </summary>
    protected DateTime RepoBrickDocStamp(string brickId = "unknown")
        => File.GetLastWriteTimeUtc(Path.Combine(RepoRoot, "docs", "bricks", $"{brickId}.md"));
}
