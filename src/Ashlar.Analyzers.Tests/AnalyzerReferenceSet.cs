using Microsoft.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Ashlar.Analyzers.Tests;

/// <summary>
/// The one place in this project that builds a Roslyn <see cref="MetadataReference"/> set.
/// </summary>
/// <remarks>
/// <para>Both analyzer harnesses here used to derive their reference set by enumerating the
/// running load context — the assemblies the test host <em>happened to have loaded</em> by the
/// time the first test in the class ran. (The exact call is spelled out in
/// <c>AnalyzerReferenceSetTests</c>, which forbids it; naming it here would match that scan.)
/// That is ambient state nobody declared, and it decided whether the samples compiled. <c>Microsoft.NET.Test.Sdk</c> 18.9.0
/// stopped flowing <c>Newtonsoft.Json</c> into the test host; with it went the
/// <c>System.ComponentModel</c> facade that carries the type-forward for
/// <see cref="IServiceProvider"/>; and all seven <c>SelfRecursiveRegistrationAnalyzerTests</c>
/// began failing with CS1069 on a sample that had never had a reason to compile. No test source,
/// no analyzer and no product code changed.</para>
///
/// <para>This replaces "what the host loaded" with two sets that are both declared and both on
/// disk before a single test runs:</para>
/// <list type="number">
///   <item>the assemblies the host was <em>launched</em> with, read off the
///   <c>TRUSTED_PLATFORM_ASSEMBLIES</c> list and narrowed to the shared framework directory;</item>
///   <item>this test project's own build output directory — the deploy closure MSBuild
///   computed — plus any explicit anchor types the caller names.</item>
/// </list>
///
/// <para>The old set was a subset of this one: everything the host loaded came from one of those
/// two directories. So the change can only add references, never remove them. What it removes is
/// the dependence on load <em>order</em>.</para>
///
/// <para>The repository's own adversarial corpus already refuses this pattern in a brick
/// (<c>tests/adversarial-corpus/fixtures/b2-appdomain-assemblies</c>, class B, expect refuse,
/// "enumerated the certifier's own loaded assemblies"). It should not have been load-bearing in
/// the harness that tests the analyzers.</para>
/// </remarks>
internal static class AnalyzerReferenceSet
{
    /// <summary>
    /// The full assembly list the host process was launched with. Present on every
    /// framework-dependent .NET host; absent under single-file/AOT publishing, which this project
    /// never uses and which is therefore a hard failure rather than a smaller reference set.
    /// </summary>
    private const string TrustedPlatformAssembliesKey = "TRUSTED_PLATFORM_ASSEMBLIES";

    /// <summary>
    /// Builds the reference set. <paramref name="anchors"/> name types whose assemblies the
    /// samples compile against; they are added unconditionally, so a caller never has to reason
    /// about whether something else pulled them in first.
    /// </summary>
    public static IReadOnlyList<MetadataReference> For(params Type[] anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        return Compose(anchors.Select(anchor => string.IsNullOrEmpty(anchor.Assembly.Location)
                ? throw new InvalidOperationException($"Analyzer reference set refused: anchor type '{anchor}' has no assembly location.")
                : anchor.Assembly.Location),
            OutputDirectoryAssemblies(), SharedFrameworkAssemblies());
    }

    internal static readonly string[] RequiredFrameworkAssemblies =
    [
        "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll",
        "System.Linq.dll", "System.ComponentModel.dll", "System.Runtime.Numerics.dll",
    ];

    internal static IReadOnlyList<MetadataReference> Compose(
        IEnumerable<string> anchors, IEnumerable<string> outputFiles, IEnumerable<string> frameworkFiles)
    {
        // Keyed by file name so an app-local copy and a shared-framework copy of the same
        // assembly cannot both enter the set (Roslyn reports CS1703 for equivalent identities).
        // Validate before reserving a name: an unusable app-local file must not hide a valid
        // shared-framework copy. Explicit anchors are required even when no sample uses them.
        var byFileName = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in anchors)
        {
            var reference = TryCreateReference(path);
            if (reference is null)
                throw new InvalidOperationException(
                    $"Analyzer reference set refused: anchor '{path}' is missing or has no readable managed metadata.");
            byFileName[Path.GetFileName(path)] = reference;
        }

        foreach (var path in outputFiles.Concat(frameworkFiles))
        {
            if (string.IsNullOrWhiteSpace(path) || byFileName.ContainsKey(Path.GetFileName(path)))
                continue;
            var reference = TryCreateReference(path);
            if (reference is not null)
                byFileName.Add(Path.GetFileName(path), reference);
        }

        var missing = RequiredFrameworkAssemblies.Except(byFileName.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException(
                "Analyzer reference set refused: missing framework assemblies " + string.Join(", ", missing)
                + ". Analyzer samples require a framework-dependent host with readable assemblies on disk.");

        return byFileName.Values
            .OrderBy(reference => reference.Display, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>This project's deploy closure, as MSBuild laid it out on disk.</summary>
    private static IEnumerable<string> OutputDirectoryAssemblies()
        => Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.TopDirectoryOnly);

    private static string SharedFrameworkDirectory()
        => Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;

    private static IReadOnlyList<string> SharedFrameworkAssemblies()
    {
        var frameworkDirectory = SharedFrameworkDirectory();
        if (frameworkDirectory.Length == 0)
            return Array.Empty<string>();

        var trusted = AppContext.GetData(TrustedPlatformAssembliesKey) as string ?? string.Empty;

        return trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(path => string.Equals(
                Path.GetDirectoryName(path), frameworkDirectory, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// Skips native libraries that share the managed extension on Windows. A file that is not a
    /// managed assembly has no metadata to reference. Declared anchors are refused by the caller;
    /// unusable discovered files are skipped and the composed framework floor is checked afterward.
    /// </summary>
    private static MetadataReference? TryCreateReference(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            // The Roslyn factory is lazy. HasMetadata proves a directory exists, not that its
            // header is readable; force that read inside this caught and disposed boundary.
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return null;
            _ = pe.GetMetadataReader();
            return MetadataReference.CreateFromFile(path);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Assembly names in the set, for diagnostics and for the tests that pin its shape.</summary>
    public static IReadOnlyList<string> FileNamesFor(params Type[] anchors)
        => For(anchors)
            .OfType<PortableExecutableReference>()
            .Select(reference => Path.GetFileName(reference.FilePath ?? string.Empty))
            .ToArray();
}
