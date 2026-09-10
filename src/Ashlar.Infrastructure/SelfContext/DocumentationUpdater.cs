using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Application.SelfContext.Ports;

namespace Ashlar.Infrastructure.SelfContext;

/// <summary>
/// Updates docs/bricks/ when adaptations are promoted.
/// </summary>
public sealed class DocumentationUpdater : IDocumentationUpdater
{
    private readonly IAdaptationLog _adaptationLog;
    private readonly IChangelogGenerator _changelogGenerator;
    private readonly string? _docsRoot;

    /// <summary>
    /// Initializes a new documentation updater with no explicit root; the location is resolved at
    /// write time as described on the three-argument overload. Kept for hand-constructed callers.
    /// The container registration goes through a factory and always passes a root, because
    /// <c>docsRoot</c> is not a registered service and constructor selection could only ever
    /// land here — which is how every test container ended up writing into the checkout (#580).
    /// </summary>
    public DocumentationUpdater(IAdaptationLog adaptationLog, IChangelogGenerator changelogGenerator)
        : this(adaptationLog, changelogGenerator, null)
    {
    }

    /// <summary>
    /// Initializes a new documentation updater writing under an explicit root.
    /// </summary>
    /// <param name="adaptationLog">Adaptation log to read promoted records from.</param>
    /// <param name="changelogGenerator">Changelog generator for the document body.</param>
    /// <param name="docsRoot">
    /// Directory to write <c>docs/bricks/</c> beneath. Null means
    /// <see cref="RepoPathResolver.ResolveDocsRoot(string?)"/>: <c>ASHLAR_DOCS_ROOT</c> when set,
    /// else the repository root, which is the dogfooding behaviour: Ashlar documents itself in
    /// its own tree.
    /// <para>Tests MUST pass a temporary directory — through <c>docsRoot</c> on
    /// <c>AddSelfContextInfrastructure</c> when composing a container, and through
    /// <c>ASHLAR_DOCS_ROOT</c> when driving the CLI as a child process, which composes its own.
    /// Writing under the repo root means a test mutates tracked files in the developer's working
    /// tree, and cleanup in a <c>finally</c> is not enough — a run killed partway through (a
    /// cancelled CI job, Ctrl+C, a stopped container) leaves the residue behind, which is how
    /// docs/bricks/unknown.md turned up modified with no obvious author.</para>
    /// </param>
    public DocumentationUpdater(
        IAdaptationLog adaptationLog,
        IChangelogGenerator changelogGenerator,
        string? docsRoot)
    {
        _adaptationLog = adaptationLog ?? throw new ArgumentNullException(nameof(adaptationLog));
        _changelogGenerator = changelogGenerator ?? throw new ArgumentNullException(nameof(changelogGenerator));
        _docsRoot = docsRoot;
    }

    /// <summary>Resolved lazily so the repo-root walk happens at write time, not construction.</summary>
    private string BricksDirectory =>
        Path.Combine(_docsRoot ?? RepoPathResolver.ResolveDocsRoot(), "docs", "bricks");

    /// <summary>Update for adaptation asynchronously.</summary>
    public async Task UpdateForAdaptationAsync(string adaptationId, CancellationToken ct = default)
    {
        var records = await _adaptationLog.QueryAsync(DateTimeOffset.UtcNow.AddDays(-1), null, null, ct).ConfigureAwait(false);
        var record = records.FirstOrDefault(r => string.Equals(r.Id, adaptationId, StringComparison.OrdinalIgnoreCase) && r.Promoted);
        if (record == null) return;

        var brickId = record.BrickId ?? "unknown";
        var docPath = Path.Combine(BricksDirectory, $"{brickId}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);

        var changelog = await _changelogGenerator.GenerateAsync(DateTimeOffset.UtcNow.AddDays(-1), null, ct).ConfigureAwait(false);
        var content = $@"# {brickId}

## Behavior

Adapted from promotion {adaptationId}.

**Last updated:** {DateTimeOffset.UtcNow:yyyy-MM-dd}

## Changelog

```markdown
{changelog}
```
";
        await File.WriteAllTextAsync(docPath, content, ct).ConfigureAwait(false);
    }

    /// <summary>Generate stub asynchronously.</summary>
    public Task GenerateStubAsync(string componentId, CancellationToken ct = default)
    {
        var docPath = Path.Combine(BricksDirectory, $"{componentId}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);

        if (File.Exists(docPath))
            return Task.CompletedTask;

        var content = $@"# {componentId}

## Behavior

(Stub - no documentation yet)

**Generated:** {DateTimeOffset.UtcNow:yyyy-MM-dd}
";
        return File.WriteAllTextAsync(docPath, content, ct);
    }
}
