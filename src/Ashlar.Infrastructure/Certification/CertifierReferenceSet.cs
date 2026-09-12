using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Execution.Routing;
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
///   <item>the shared framework this host runs on, read as the DIRECTORY LISTING of the directory
///   holding <c>System.Private.CoreLib</c>, narrowed to files that carry managed metadata;</item>
///   <item>explicit anchor types named in code — <see cref="SelfExtendAuthoringAnchors"/> for the
///   self-extend certifier, <see cref="BrickAuthoringAnchors"/> for the certification chain.</item>
/// </list>
///
/// <para><b>Why the framework half is the directory and not the platform list.</b> The first version
/// of this type narrowed <c>TRUSTED_PLATFORM_ASSEMBLIES</c> to entries whose directory was the
/// framework directory. That list is RESOLVED: for each assembly identity exactly one path wins, and
/// an app-local copy beats the shared-framework copy. So every framework assembly a deployment
/// overrides from NuGet was dropped from the set ENTIRELY rather than substituted with the framework
/// copy sitting on disk one directory away, and the miss was silent and unbounded. Measured on
/// net8.0: <c>Directory.Packages.props</c> pins <c>System.Text.Json</c> ahead of the 8.0 framework's
/// copy and three assemblies travel with it (<c>System.Text.Encodings.Web</c>,
/// <c>System.Diagnostics.DiagnosticSource</c>, <c>System.Threading.Channels</c>), so all four sat
/// app-local, none of them entered the set, and a proposal writing
/// <c>System.Text.Json.JsonSerializer.Serialize</c> — which a brick project referencing
/// <c>Ashlar.Authoring</c> builds without complaint — was recorded as a compile error against the
/// proposal. The platform list is still read, but only as EVIDENCE that this is a framework-dependent
/// host; what the set contains is the framework.</para>
///
/// <para><b>Where the #605 precedent (<c>AnalyzerReferenceSet</c>) transfers, and where it does
/// not.</b> Three of its design moves carry over unchanged: compose from inputs that are on disk
/// before anything runs; key the set by FILE NAME so an app-local and a shared-framework copy of one
/// assembly cannot both enter it (Roslyn reports CS1703 for equivalent identities); and throw rather
/// than run on a partial set. Two do not.</para>
/// <list type="bullet">
///   <item><b>The app output directory is not part of the set, and that is enforced rather than
///   assumed.</b> For a test project <c>AppContext.BaseDirectory</c> is the deploy closure MSBuild
///   computed for the samples, which is exactly the surface the samples should compile against. For a
///   deployed node it is whatever happens to sit beside the host — including
///   <c>Ashlar.Infrastructure</c> itself, which is what let a proposal reference the certifier's own
///   internals and compile clean. That surface is not a property of the change being judged: it
///   differs between the CLI, an API host, a test host and a node, so the same bytes get different
///   verdicts on machines running the same build. Only assemblies NAMED in code get in from there,
///   via the anchors. (What it is NOT is a claim that a brick project cannot see those types:
///   measured from the built package, <c>Ashlar.Authoring</c> depends on <c>Ashlar.Hosting</c>, which
///   has a <c>ProjectReference</c> on <c>Ashlar.Infrastructure</c>. The property here is
///   declaredness, not reachability.)
///   Under a self-contained publish the framework and the app share one directory, which would admit
///   the whole app output directory through the framework half; <see cref="Compose"/> refuses that
///   shape by name rather than letting the documented property quietly invert.</item>
///   <item><b>A shipped certifier cannot assume its host.</b> <c>AnalyzerReferenceSet</c> may treat an
///   absent platform list as impossible because its one project never publishes single-file or
///   ahead-of-time. This runs on deployed nodes, so every published shape is a measured runtime
///   branch with a defined answer — see <see cref="Compose"/>.</item>
/// </list>
///
/// <para><b>What "the shared framework" means here, and what it leaves out.</b> One framework: the
/// <c>Microsoft.NETCore.App</c> directory, located as the one holding <c>System.Private.CoreLib</c>.
/// A host that also runs on <c>Microsoft.AspNetCore.App</c> has a second shared framework in a
/// different directory, and its assemblies are NOT in the set — unchanged from the ambient scheme,
/// which only ever contained what had been loaded. A brick is not an ASP.NET Core component
/// (<c>docs/AuthoringBricks.md</c>), so this is a limit rather than a gap; it is written down because
/// the alternative is a reader inferring "every framework the host was launched with".</para>
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
    /// publishing. Read as EVIDENCE of the publish shape only — the framework half of the set comes
    /// from the framework directory, for the reason stated on the type.
    /// </summary>
    internal const string TrustedPlatformAssembliesKey = "TRUSTED_PLATFORM_ASSEMBLIES";

    /// <summary>
    /// The anchor types the CERTIFICATION CHAIN compiles a candidate brick against, consumed by
    /// <see cref="BrickCertificationProjectLoader.DefaultCompilationReferences"/>. They all resolve to
    /// <c>Ashlar.Brick.Contracts</c> today; naming three types rather than one assembly keeps the
    /// intent readable if that ever splits.
    ///
    /// <para>Deliberately NOT widened to <see cref="SelfExtendAuthoringAnchors"/>: the chain's
    /// emitted artefact is hashed into the certificate and pinned by golden corpora, so widening what
    /// it may compile against can move certificate bytes and is its own change. The two lists are
    /// separate for that reason, and the difference is asserted rather than implied.</para>
    /// </summary>
    internal static readonly IReadOnlyList<Type> BrickAuthoringAnchors =
    [
        typeof(DomainBrick),
        typeof(BrickInput),
        typeof(IExecutionContext),
    ];

    /// <summary>
    /// The authoring surface a SELF-EXTEND proposal is judged against: the assemblies a brick project
    /// referencing the <c>Ashlar.Authoring</c> package compiles against, named type by type.
    ///
    /// <para><c>docs/AuthoringBricks.md</c> describes that package as bringing "<c>Brick</c>,
    /// <c>BrickInput</c>, <c>BrickOutput</c>, <c>IExecutionContext</c>, <c>IBrickExecutor</c>" plus
    /// host registration, and <c>src/Ashlar.Authoring/Ashlar.Authoring.csproj</c> reaches those
    /// through <c>Ashlar.Brick.Contracts</c>, <c>Ashlar.Core.Domain</c>, <c>Ashlar.Core.Application</c>
    /// and <c>Microsoft.Extensions.DependencyInjection.Abstractions</c>, with
    /// <c>Microsoft.Extensions.Logging.Abstractions</c> flowing from <c>Ashlar.Core.Domain</c>. Each of
    /// the five is anchored below by a type that lives in it. Four sources a brick project builds were
    /// refused by the first version of this type, which anchored only the first assembly:
    /// <c>IBrickExecutor</c>, <c>IServiceCollection</c>, <c>ILogger&lt;T&gt;</c> and
    /// <c>BrickRuntimeSpec</c>.</para>
    ///
    /// <para><b>Two assemblies of that package are deliberately absent, with the reason.</b>
    /// <c>Ashlar.Authoring</c> itself (which declares <c>AddAshlarBrick&lt;T&gt;()</c> in
    /// <c>AshlarAuthoringServiceCollectionExtensions.cs</c>) and <c>Ashlar.Hosting</c> cannot be
    /// anchored from here at all: <c>src/Ashlar.Hosting/Ashlar.Hosting.csproj</c> has a
    /// <c>ProjectReference</c> on <c>Ashlar.Infrastructure</c>, so naming a type in either would be an
    /// assembly-reference cycle. Resolving them by file name beside the host is the
    /// app-output-directory mechanism this type refuses, and skipping them when absent is the silent
    /// partial set it refuses. So a proposal containing HOST REGISTRATION code is refused by A2, and
    /// that is a narrowed claim rather than an accident: the envelope lets a self-extending node add a
    /// <c>brick</c> and nothing else (<c>docs/RunningASelfExtendingNode.md</c>), and host registration
    /// is not part of a brick. <c>CertifierReferenceSetTests</c> pins both halves so the boundary stays
    /// visible.</para>
    /// </summary>
    internal static readonly IReadOnlyList<Type> SelfExtendAuthoringAnchors =
    [
        typeof(DomainBrick),                // Ashlar.Brick.Contracts
        typeof(BrickInput),                 // Ashlar.Brick.Contracts
        typeof(IExecutionContext),          // Ashlar.Brick.Contracts
        typeof(BrickRuntimeSpec),           // Ashlar.Core.Domain
        typeof(IBrickExecutor),             // Ashlar.Core.Application
        typeof(IServiceCollection),         // Microsoft.Extensions.DependencyInjection.Abstractions
        typeof(ILogger),                    // Microsoft.Extensions.Logging.Abstractions
    ];

    /// <summary>
    /// The floor, stated as NAMES rather than as a count. Every one of these has shipped in
    /// <c>Microsoft.NETCore.App</c> on every platform since .NET Core 3.0, so the assertion is
    /// OS-independent and survives a framework version bump — unlike "at least N assemblies", which
    /// is a fact about one host.
    ///
    /// <para>This exists because an emptiness check is not a floor. A trimmed self-contained layout
    /// leaves a SUBSET of the framework on disk, and a guard that only asks whether the framework half
    /// came back empty proceeds on whatever the trimmer left, then refuses every non-trivial proposal
    /// with unresolved-type diagnostics that name the proposal. The list is checked against the
    /// COMPOSED SET rather than against the directory listing, so an assembly that is present but
    /// carries no readable metadata — dropped by <see cref="TryCreateManagedReference"/> — fails here
    /// too.</para>
    /// </summary>
    internal static readonly IReadOnlyList<string> RequiredFrameworkAssemblies =
    [
        "System.Private.CoreLib.dll",
        "System.Runtime.dll",
        "netstandard.dll",
        "mscorlib.dll",
        "System.Collections.dll",
        "System.Collections.Immutable.dll",
        "System.Console.dll",
        "System.Linq.dll",
        "System.Linq.Expressions.dll",
        "System.ObjectModel.dll",
        "System.Threading.dll",
        "System.Runtime.InteropServices.dll",
        "System.Runtime.Numerics.dll",
        "System.Text.RegularExpressions.dll",
        "System.Text.Json.dll",
        "System.Net.Http.dll",
        "System.ComponentModel.dll",
        "System.Data.Common.dll",
        "System.Web.HttpUtility.dll",
    ];

    private static readonly Lazy<IReadOnlyList<MetadataReference>> SharedSet = new(
        () => For(SelfExtendAuthoringAnchors),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The process-wide certifier reference set, anchored on <see cref="SelfExtendAuthoringAnchors"/>.
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
    /// for bundled assemblies. Equal to the app output directory under a self-contained publish, which
    /// <see cref="Compose"/> refuses.
    /// </remarks>
    internal static string SharedFrameworkDirectory()
        => Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;

    /// <summary>
    /// Composes the set from inputs supplied explicitly, so every refusal branch below is testable
    /// without publishing five ways.
    /// </summary>
    /// <param name="anchors">Anchor types; their assemblies are added by name and win a file-name tie.</param>
    /// <param name="trustedPlatformAssemblies">
    /// The raw <c>TRUSTED_PLATFORM_ASSEMBLIES</c> value, read as evidence of the publish shape.
    /// </param>
    /// <param name="frameworkDirectory">The directory holding <c>System.Private.CoreLib</c>.</param>
    /// <param name="baseDirectory">The application base directory, which may not contribute.</param>
    /// <param name="frameworkFiles">
    /// The framework directory's own listing. <see langword="null"/> means "read it from
    /// <paramref name="frameworkDirectory"/>"; a test supplies it to drive a layout it cannot publish.
    /// </param>
    /// <remarks>
    /// <para><b>The published-host answer, stated rather than inherited.</b> The inputs behave five
    /// different ways once an app is published, and each has its own refusal naming the
    /// condition:</para>
    /// <list type="bullet">
    ///   <item>framework-dependent: the platform list is present, its entries are real files, and the
    ///   framework directory is a directory of its own — the only shape this certifier judges
    ///   against;</item>
    ///   <item>self-contained single-file: <c>AppContext.GetData</c> returns an empty string;</item>
    ///   <item>ahead-of-time: it returns <see langword="null"/>;</item>
    ///   <item>single-file + trimmed: it returns a list of over a hundred entries <em>none of which
    ///   exist on disk</em> — synthesised paths beside the bundle. A check that only asked whether
    ///   the list was empty would sail past this one and then fail inside Roslyn, once per entry;</item>
    ///   <item>self-contained, not single-file: the framework sits IN the app output directory, so the
    ///   framework half would carry every app assembly including <c>Ashlar.Infrastructure</c>. Refused
    ///   by the directory comparison, because the alternative is the certifier's own internals
    ///   compiling clean on exactly the deployment shape the exclusion was written for.</item>
    /// </list>
    /// <para>And a TRIMMED framework directory passes every one of those and still cannot judge
    /// anything, so the last guard is <see cref="RequiredFrameworkAssemblies"/> by name, applied to
    /// the composed set. Nothing falls back to the loaded-assembly list: a silent ambient fallback is
    /// the defect this type exists to remove, reintroduced where nobody would see it.</para>
    /// </remarks>
    internal static IReadOnlyList<MetadataReference> Compose(
        IReadOnlyList<Type> anchors,
        string? trustedPlatformAssemblies,
        string frameworkDirectory,
        string baseDirectory,
        IReadOnlyList<string>? frameworkFiles = null)
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

        if (SamePath(frameworkDirectory, baseDirectory))
        {
            throw new InvalidOperationException(
                "Certifier reference set refused: the shared framework directory and the application "
                + $"base directory are the same path ('{frameworkDirectory}'). That is the signature of "
                + "a self-contained publish, where the framework is deployed INTO the app output "
                + "directory. This certifier declares the framework by reading that directory, so on "
                + "that layout the set would also carry every assembly shipped beside the host — "
                + "Ashlar.Infrastructure included — so a verdict would depend on this host's deploy "
                + "closure rather than on declared inputs, and would differ between the CLI, an API "
                + "host and a node running the same proposal. The app output directory contributes only "
                + "the anchor assemblies named in code, so it refuses here rather than letting that "
                + "exclusion invert silently. Run the certifier from a framework-dependent host.");
        }

        var entries = (trustedPlatformAssemblies ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var platformEvidence = entries
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(path => frameworkDirectory.Length > 0 && string.Equals(
                Path.GetDirectoryName(path), frameworkDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .ToArray();

        if (platformEvidence.Length == 0)
        {
            throw new InvalidOperationException(
                "Certifier reference set refused: this host cannot be shown to be running on a shared "
                + "framework. "
                + $"AppContext.GetData(\"{TrustedPlatformAssembliesKey}\") "
                + (trustedPlatformAssemblies is null
                    ? "returned null"
                    : $"listed {entries.Length} entr{(entries.Length == 1 ? "y" : "ies")}")
                + ", none of which is a file on disk in the shared framework directory; that directory "
                + "is "
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

        // The framework half is the DIRECTORY, not the resolved platform list: an app-local override
        // wins in that list, which silently removed the framework copy rather than substituting it.
        foreach (var path in frameworkFiles ?? ListFrameworkDirectory(frameworkDirectory))
        {
            var key = Path.GetFileName(path);
            if (!byFileName.ContainsKey(key))
                byFileName[key] = path;
        }

        var composed = byFileName.Values
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(TryCreateManagedReference)
            .Where(reference => reference is not null)
            .Select(reference => reference!)
            .ToArray();

        var present = FileNames(composed).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RequiredFrameworkAssemblies
            .Where(name => !present.Contains(name))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Certifier reference set refused: the shared framework directory does not carry the "
                + $"assemblies a proposal has to be judged against. Missing {missing.Length} of "
                + $"{RequiredFrameworkAssemblies.Count} required: {string.Join(", ", missing)}. The set "
                + $"composed to {composed.Length} reference(s) from "
                + (frameworkDirectory.Length == 0
                    ? "an unknown framework directory"
                    : $"'{frameworkDirectory}'")
                + ". That is the signature of a TRIMMED deployment, where the platform list and the "
                + "framework directory can both look healthy and hold only what the trimmer kept. A "
                + "count is not a floor here — these are named, because a SUBSET of the framework "
                + "judges every non-trivial proposal to be a compile error and attributes a fault in "
                + "this verifier to the change. Run the certifier from a framework-dependent, "
                + "untrimmed host.");
        }

        return composed;
    }

    /// <summary>
    /// The framework directory's managed-assembly candidates. An unreadable directory contributes
    /// nothing rather than throwing; the guards in <see cref="Compose"/> then refuse by name.
    /// </summary>
    private static IReadOnlyList<string> ListFrameworkDirectory(string frameworkDirectory)
    {
        if (frameworkDirectory.Length == 0 || !Directory.Exists(frameworkDirectory))
            return [];

        try
        {
            return Directory.GetFiles(frameworkDirectory, "*.dll");
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Creates a reference, skipping files that carry no managed metadata — the native libraries that
    /// share the managed extension on Windows, of which a 10.0 framework directory holds thirteen.
    ///
    /// <para>This reads the PE headers EAGERLY, which is the whole point.
    /// <c>MetadataReference.CreateFromFile</c> is lazy: measured on Windows it returns a reference for
    /// a native <c>.dll</c> and for a text file renamed <c>.dll</c> without throwing anything, and
    /// <c>AssemblyMetadata.CreateFromFile</c> does the same. A <c>catch (BadImageFormatException)</c>
    /// around either therefore never fires, and the bad entry surfaces later as
    /// <c>CS0009: ... PE image doesn't contain managed metadata</c> — a fabricated diagnostic, once
    /// per compilation, on a verdict about someone's proposal. <see cref="PEReader"/> throws on a
    /// non-PE file and reports <see cref="PEReader.HasMetadata"/> for a native one, at header-read
    /// cost and without loading anything.</para>
    ///
    /// <para>Every other failure propagates: a reference silently dropped is a reference set nobody
    /// declared. What this does drop still has to clear
    /// <see cref="RequiredFrameworkAssemblies"/>.</para>
    /// </summary>
    private static MetadataReference? TryCreateManagedReference(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }

        return MetadataReference.CreateFromFile(path);
    }

    /// <summary>
    /// Directory equality for the self-contained guard. Compared case-insensitively on every platform:
    /// a false match refuses and is recoverable, a false miss admits the app output directory.
    /// </summary>
    private static bool SamePath(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
            return false;

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
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
