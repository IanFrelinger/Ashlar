using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// The one place the self-extend certifier builds a Roslyn <see cref="MetadataReference"/> set.
/// </summary>
/// <remarks>
/// <para><b>What this replaces.</b> <see cref="RoslynExtensionCompileCheck"/> (A2) and
/// <see cref="RoslynPostApplyVerification"/> (A4) each carried a byte-identical private copy that
/// enumerated the running load context — the assemblies the host <em>happened to have loaded</em> by
/// the time a proposal arrived. Loading is lazy and order-dependent, so the reference set was a
/// function of the host's history rather than of the code under test, in both directions: a proposal
/// naming a framework type nothing had touched yet was refused, and a proposal naming a type the host
/// had loaded for its own reasons — including types private to the certifier's own assembly —
/// compiled clean. The check's own first invocation loads Roslyn and its dependencies, so proposal #1
/// and proposal #2 in the same process could get different verdicts for identical bytes.</para>
///
/// <para><b>What it is now.</b> Two declared inputs, both fixed before a proposal is seen:</para>
/// <list type="number">
///   <item>the shared framework the host was <em>launched</em> with, read off the
///   <c>TRUSTED_PLATFORM_ASSEMBLIES</c> list, narrowed to the directory that holds
///   <c>System.Private.CoreLib</c>, and narrowed again to entries that exist on disk;</item>
///   <item>explicit anchor types named in code — <see cref="BrickAuthoringAnchors"/> for the
///   certifier's own callers.</item>
/// </list>
///
/// <para><b>Where the #605 precedent (<c>AnalyzerReferenceSet</c>) transfers, and where it does
/// not.</b> Three of its design moves carry over unchanged: compose from inputs that are on disk
/// before anything runs; key the set by FILE NAME so an app-local and a shared-framework copy of one
/// assembly cannot both enter it (Roslyn reports CS1703 for equivalent identities); and throw rather
/// than run on a partial set. Two do not.</para>
/// <list type="bullet">
///   <item><b>The app output directory is deliberately NOT included here.</b> For a test project
///   <c>AppContext.BaseDirectory</c> is the deploy closure MSBuild computed for the samples, which is
///   exactly the surface the samples should compile against. For a deployed node it is whatever
///   happens to sit beside the host — including <c>Ashlar.Infrastructure</c> itself. Including it
///   would keep a proposal referencing the certifier's own internals compiling clean, which is a true
///   statement about this process and a false one about the artefact: a brick is built against the
///   <c>Ashlar.Authoring</c> package, which does not carry them. The anchors below are the shipped
///   authoring surface, named rather than inherited from a directory listing.</item>
///   <item><b>A shipped certifier cannot assume its host.</b> <c>AnalyzerReferenceSet</c> may treat an
///   absent platform list as impossible because its one project never publishes single-file or
///   ahead-of-time. This runs on deployed nodes, so the absent case is a measured runtime branch with
///   a defined answer — see <see cref="Compose"/>.</item>
/// </list>
///
/// <para>The set is built once per process and shared by both stages, so A2 and A4 cannot disagree
/// about what they compiled against; a failed build is re-thrown to every caller rather than cached
/// as an empty set.</para>
///
/// <para>The repository's own adversarial corpus already refuses a brick for enumerating the
/// certifier's loaded assemblies (<c>tests/adversarial-corpus/fixtures/b2-appdomain-assemblies</c>,
/// class B, expect refuse). It should not have been load-bearing in the certifier itself.</para>
/// </remarks>
internal static class CertifierReferenceSet
{
    /// <summary>
    /// The full assembly list the host process was launched with. Present on every
    /// framework-dependent .NET host; absent or fictional under single-file and ahead-of-time
    /// publishing, which <see cref="Compose"/> refuses by name rather than degrading.
    /// </summary>
    internal const string TrustedPlatformAssembliesKey = "TRUSTED_PLATFORM_ASSEMBLIES";

    /// <summary>
    /// The shipped authoring surface a candidate brick compiles against: the same three types
    /// <see cref="BrickCertificationProjectLoader.DefaultCompilationReferences"/> names for the
    /// certification chain, so a self-extend proposal is judged against exactly the surface a
    /// certified brick is judged against. They all resolve to <c>Ashlar.Brick.Contracts</c> today;
    /// naming three types rather than one assembly keeps the intent readable if that ever splits.
    /// </summary>
    internal static readonly IReadOnlyList<Type> BrickAuthoringAnchors =
    [
        typeof(DomainBrick),
        typeof(BrickInput),
        typeof(IExecutionContext),
    ];

    private static readonly Lazy<IReadOnlyList<MetadataReference>> SharedSet = new(
        () => For(BrickAuthoringAnchors),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The process-wide certifier reference set, anchored on <see cref="BrickAuthoringAnchors"/>.
    /// Built once; a build that refused is re-thrown to every later caller.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The host cannot supply a declared reference set — see <see cref="Compose"/>. This is a
    /// verifier fault, never a verdict about a proposal: both call sites let it propagate, and their
    /// callers record it with a distinct wording (<c>compile check errored</c> / <c>canary
    /// errored</c>) so it can never be read back as "the proposal does not compile".
    /// </exception>
    public static IReadOnlyList<MetadataReference> Shared => SharedSet.Value;

    /// <summary>Builds a set anchored on <paramref name="anchors"/>, from this host's real inputs.</summary>
    public static IReadOnlyList<MetadataReference> For(IReadOnlyList<Type> anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);

        return Compose(
            anchors,
            AppContext.GetData(TrustedPlatformAssembliesKey) as string,
            SharedFrameworkDirectory(),
            AppContext.BaseDirectory);
    }

    /// <summary>The directory holding the shared framework this process runs on.</summary>
    /// <remarks>
    /// Empty under single-file and ahead-of-time publishing, where <c>Assembly.Location</c> is empty
    /// for bundled assemblies.
    /// </remarks>
    internal static string SharedFrameworkDirectory()
        => Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;

    /// <summary>
    /// Composes the set from inputs supplied explicitly, so the refusal branches below are testable
    /// without publishing four ways.
    /// </summary>
    /// <remarks>
    /// <para><b>The single-file / ahead-of-time answer, stated rather than inherited.</b> The
    /// platform list behaves three different ways under publishing, so an emptiness check alone is
    /// not enough:</para>
    /// <list type="bullet">
    ///   <item>framework-dependent: the list is present and its entries are real files in the shared
    ///   framework directory — the only shape this certifier can judge against;</item>
    ///   <item>self-contained single-file: <c>AppContext.GetData</c> returns an empty string;</item>
    ///   <item>ahead-of-time: it returns <see langword="null"/>;</item>
    ///   <item>single-file + trimmed: it returns a list of over a hundred entries <em>none of which
    ///   exist on disk</em> — synthesised paths beside the bundle. A check that only asked whether
    ///   the list was empty would sail past this one and then fail inside Roslyn, once per entry.</item>
    /// </list>
    /// <para>So the guard is on the NARROWED, ON-DISK count, and every published shape refuses with a
    /// message naming which condition failed and what the host looked like. It does not fall back to
    /// the loaded-assembly list: a silent ambient fallback is the defect this type exists to remove,
    /// reintroduced where nobody would see it.</para>
    /// </remarks>
    internal static IReadOnlyList<MetadataReference> Compose(
        IReadOnlyList<Type> anchors,
        string? trustedPlatformAssemblies,
        string frameworkDirectory,
        string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(anchors);

        // Keyed by file name so an anchor and a shared-framework copy of the same assembly cannot
        // both enter the set. Anchors are added first and win: they are the declared surface.
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in anchors)
        {
            var location = anchor.Assembly.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                throw new InvalidOperationException(
                    $"Certifier reference set refused: anchor type '{anchor.FullName}' resolves to assembly "
                    + $"'{anchor.Assembly.GetName().Name}', whose location is "
                    + (string.IsNullOrEmpty(location) ? "empty" : $"'{location}', which does not exist")
                    + ". Without it a candidate brick cannot resolve the authoring contracts, and every "
                    + "verdict below would be a statement about the missing anchor rather than about the "
                    + "proposal. A missing anchor is a fault in this verifier, not a failing proposal.");
            }

            byFileName[Path.GetFileName(location)] = location;
        }

        var entries = (trustedPlatformAssemblies ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var framework = entries
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(path => frameworkDirectory.Length > 0 && string.Equals(
                Path.GetDirectoryName(path), frameworkDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .ToArray();

        if (framework.Length == 0)
        {
            throw new InvalidOperationException(
                "Certifier reference set refused: no shared-framework assemblies could be declared. "
                + $"AppContext.GetData(\"{TrustedPlatformAssembliesKey}\") "
                + (trustedPlatformAssemblies is null
                    ? "returned null"
                    : $"listed {entries.Length} entr{(entries.Length == 1 ? "y" : "ies")}")
                + "; the shared framework directory is "
                + (frameworkDirectory.Length == 0
                    ? "unknown (typeof(object).Assembly.Location is empty)"
                    : $"'{frameworkDirectory}'")
                + $"; the application base directory is '{baseDirectory}'. That is the signature of a "
                + "single-file, trimmed, or ahead-of-time published host, where the platform list is "
                + "absent or names files that are not on disk. This certifier compiles proposals in "
                + "process and cannot do so without the framework, so it refuses here rather than "
                + "returning a partial set: on a partial set every non-trivial proposal fails with "
                + "unresolved-type diagnostics that name the proposal, and the fault in the verifier "
                + "is recorded as a fact about the change. Run the certifier from a "
                + "framework-dependent host.");
        }

        foreach (var path in framework)
        {
            var key = Path.GetFileName(path);
            if (!byFileName.ContainsKey(key))
                byFileName[key] = path;
        }

        return byFileName.Values
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(TryCreateReference)
            .Where(reference => reference is not null)
            .Select(reference => reference!)
            .ToArray();
    }

    /// <summary>
    /// Skips native libraries that share the managed extension on Windows. A file that is not a
    /// managed assembly has no metadata to reference; every other failure propagates, because a
    /// reference silently dropped is a reference set nobody declared.
    /// </summary>
    private static MetadataReference? TryCreateReference(string path)
    {
        try
        {
            return MetadataReference.CreateFromFile(path);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Assembly file names in a set, for diagnostics and for the tests that pin its shape.</summary>
    public static IReadOnlyList<string> FileNames(IReadOnlyList<MetadataReference> set)
        => set
            .OfType<PortableExecutableReference>()
            .Select(reference => Path.GetFileName(reference.FilePath ?? string.Empty))
            .ToArray();

    /// <summary>
    /// A short, stable identity for a reference set: its size and a digest over the ordered file
    /// names. Recorded in the <c>build</c> course detail so a signed record says what it compiled
    /// against, and so two nodes that disagree about a proposal can be told apart from the records
    /// alone rather than only by an unexplained rollback (#603's habit, applied to the compile).
    /// </summary>
    public static string Describe(IReadOnlyList<MetadataReference> set)
    {
        var names = FileNames(set).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", names))))[..6].ToLowerInvariant();
        return $"refs: {set.Count}, set {digest}";
    }
}
