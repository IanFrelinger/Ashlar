namespace Ashlar.Manifest;

/// <summary>
/// Answers the question <c>ashlar verify</c> never used to ask: <em>is the brick this project
/// declares actually here?</em>
///
/// <para>A <c>bricks:</c> entry in <c>ashlar.yaml</c> is a dependency the application says it is
/// built on. Nothing in the manifest layer used to resolve one, so a declared brick that existed
/// nowhere on disk passed the composition course, and a signed ledger entry then attested a
/// composition that could not run. That is the shape this file exists to close: an enforcement
/// point that was absent, and whose absence read as a pass.</para>
///
/// <para>Resolution is deliberately generous about LAYOUT and strict about EXISTENCE. A brick is
/// resolved when some C# under the project plausibly implements it — a file named after it, or a
/// directory named after it with C# inside. It is refused only when nothing under the project
/// matches at all, which is the only case where the answer is unambiguous.</para>
///
/// <para>"C# under the project" means what the compiler compiles: every <c>.cs</c> outside build
/// output. It deliberately does NOT filter by file NAME — see the note above
/// <c>IgnoredDirectories</c> — because a name like <c>.g.cs</c> says nothing about whether the SDK
/// compiles the file, and it does.</para>
///
/// <para><strong>This is an inventory, not a certified source set, and the two are answered
/// differently on purpose.</strong> The certification gate asks MSBuild what a brick project
/// compiles (<c>BrickCertificationProjectLoader</c> /
/// <c>EvaluatedBrickProject</c>) and refuses anything it cannot resolve, because the answer there
/// is signed. This class cannot do that and must not pretend to: it is handed an APPLICATION
/// directory rather than a project file — zero or many <c>.csproj</c> under it, and a brick may be
/// authored as loose files with none — and <c>ashlar verify</c> runs inside an exported bundle on
/// machines with no .NET SDK, where there is no MSBuild to ask. So it uses the SDK's own DIRECTORY
/// rule, anchored where the SDK anchors it (see <c>BuildOutputDirectories</c>), and nothing
/// finer.</para>
///
/// <para>What that costs is bounded, and it is bounded in the safe direction. A file this walk
/// includes but the compiler ignores makes a declared brick RESOLVE that a stricter answer might
/// refuse, and makes an export carry a directory it need not have carried. A file it MISSED would
/// be the dangerous direction — a brick whose real source is invisible here does not get staged by
/// <c>ashlar export</c> and quietly does not travel — which is exactly what the old any-depth
/// <c>obj</c>/<c>bin</c> rule caused, and why that rule was anchored.</para>
/// </summary>
public static class BrickSourceResolver
{
    /// <summary>
    /// Directories that never hold authored source, wherever they sit. Tooling and VCS state, not
    /// build output — see <see cref="BuildOutputDirectories"/> for the two that need a position,
    /// not just a name.
    /// </summary>
    private static readonly string[] IgnoredDirectories =
    [
        ".git", ".ashlar", ".vs", ".idea", "node_modules", "TestResults", "packages",
    ];

    /// <summary>
    /// The two directory names that mean "build output" — but only where the SDK means them.
    /// </summary>
    /// <remarks>
    /// <para>This list used to sit in <see cref="IgnoredDirectories"/> and be matched at ANY depth.
    /// The SDK does not do that. <c>$(BaseOutputPath)</c> and <c>$(BaseIntermediateOutputPath)</c>
    /// are <c>bin/</c> and <c>obj/</c> DIRECTLY under the project that owns them, and those two
    /// paths are the whole of the default compile glob's exclusion. Everything else it compiles,
    /// <c>Sub/obj/Payload.cs</c> included. Matching the name at any depth is therefore a hole with a
    /// pure-layout trigger: a directory called <c>obj</c> nested anywhere under the project made
    /// real, compiled source invisible to this inventory with no csproj edit to notice, which is
    /// the same bypass the certification loader carried (see
    /// <c>BrickCertificationProjectLoader.FindBrickSourceFiles</c>).</para>
    ///
    /// <para>So the exclusion is anchored: <c>bin</c>/<c>obj</c> is skipped only when its PARENT is
    /// a project root — a directory holding a <c>.csproj</c> — or the scan root itself. Anywhere
    /// else the name means nothing and the files are walked.</para>
    /// </remarks>
    private static readonly string[] BuildOutputDirectories = ["bin", "obj"];

    // There is deliberately NO "generated file name" exclusion list here any more.
    //
    // This file used to skip *.g.cs, *.generated.cs, *.Designer.cs, *.AssemblyInfo.cs and
    // *.AssemblyAttributes.cs on the theory that those names mean "generated, therefore not
    // authored". The compiler disagrees: the SDK's default glob excludes obj/ and bin/ and
    // compiles every one of those names anywhere else in the project tree. So the suffix list did
    // not describe generated code — it described a set of FILE NAMES that made real, compiled
    // source invisible to this inventory, which is the same hole the certification loader carried
    // (see BrickCertificationProjectLoader.FindBrickSourceFiles). Here it meant a brick genuinely
    // implemented in Invoice.generated.cs read as "declared but absent" and was refused, and — the
    // direction that matters more — compiled source under a blessed name was simply not part of
    // the inventory that certification reasons about.
    //
    // Build output is already excluded, by DIRECTORY and in the POSITION the SDK puts it, in
    // BuildOutputDirectories / IsProjectBuildOutput above. That is the rule the compiler itself
    // uses, and it needs no help from a name.

    /// <summary>
    /// Everything authored under a project directory that a certification could be ABOUT: the
    /// C# files, and the directories that hold them.
    /// </summary>
    /// <param name="projectDirectory">The project root (the directory holding ashlar.yaml).</param>
    public static ProjectSourceInventory Scan(string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
        {
            return new ProjectSourceInventory([], []);
        }

        var files = new List<string>();
        var directories = new List<string>();
        Walk(new DirectoryInfo(projectDirectory), files, directories, depth: 0);
        files.Sort(StringComparer.Ordinal);
        directories.Sort(StringComparer.Ordinal);
        return new ProjectSourceInventory(files, directories);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a build-output directory in the position the SDK
    /// puts one: <c>bin</c> or <c>obj</c> sitting directly inside a project root.
    /// </summary>
    private static bool IsProjectBuildOutput(DirectoryInfo parent, DirectoryInfo candidate, int depth)
    {
        if (!BuildOutputDirectories.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // depth 0 is the scan root: an application's own bin/ and obj/ live there whether or not a
        // csproj sits beside them, and they are output either way.
        if (depth == 0)
        {
            return true;
        }

        try
        {
            return parent.GetFiles("*.csproj").Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot tell whether this is a project root. Walk it: an extra file in the inventory
            // is a brick that resolves, while a missed one is a brick whose source silently does
            // not travel in an export.
            return false;
        }
    }

    private static void Walk(DirectoryInfo dir, List<string> files, List<string> directories, int depth)
    {
        // A project tree is not unbounded, but a symlink loop is. Cap the walk rather than hang a
        // verification: a brick nested twelve directories deep is not a layout to accommodate.
        if (depth > 12)
        {
            return;
        }

        FileInfo[] localFiles;
        DirectoryInfo[] subdirectories;
        try
        {
            localFiles = dir.GetFiles("*.cs");
            subdirectories = dir.GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in localFiles)
        {
            files.Add(file.FullName);
        }

        foreach (var sub in subdirectories)
        {
            if (IgnoredDirectories.Contains(sub.Name, StringComparer.OrdinalIgnoreCase)
                || IsProjectBuildOutput(dir, sub, depth))
            {
                continue;
            }
            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            directories.Add(sub.FullName);
            Walk(sub, files, directories, depth + 1);
        }
    }

    /// <summary>
    /// The authored C# that plausibly implements <paramref name="brickId"/>, or an empty list when
    /// nothing under the project does.
    /// </summary>
    public static IReadOnlyList<string> Resolve(ProjectSourceInventory inventory, string? brickId)
    {
        var key = Normalize(brickId);
        if (key.Length == 0)
        {
            return [];
        }

        var matches = new List<string>();
        foreach (var file in inventory.SourceFiles)
        {
            if (Matches(Path.GetFileNameWithoutExtension(file), key))
            {
                matches.Add(file);
            }
        }

        foreach (var directory in inventory.Directories)
        {
            if (!Matches(Path.GetFileName(directory), key))
            {
                continue;
            }
            // A directory named after the brick only counts when there is C# inside it. An empty
            // folder is not an implementation, and treating one as resolution would reopen exactly
            // the hole this class closes.
            var withSeparator = directory.EndsWith(Path.DirectorySeparatorChar)
                ? directory
                : directory + Path.DirectorySeparatorChar;
            matches.AddRange(inventory.SourceFiles.Where(f => f.StartsWith(withSeparator, StringComparison.Ordinal)));
        }

        return matches.Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The exact places a reader should look, spelled out for the refusal message. A refusal that
    /// names the fix has to say what it searched for, or the fix is guesswork.
    /// </summary>
    public static string DescribeSearch(string brickId)
    {
        var pascal = ToPascal(brickId);
        return $"a directory named '{brickId}' (or '{pascal}') with C# in it, "
             + $"or a file named {brickId}.cs / {pascal}.cs / {pascal}Brick.cs, "
             + "anywhere under the project except a project's own bin/ and obj/, and .ashlar/";
    }

    private static bool Matches(string name, string key)
    {
        var candidate = Normalize(name);
        if (candidate.Length == 0)
        {
            return false;
        }
        return candidate == key
            || candidate == key + "brick"
            || candidate + "brick" == key;
    }

    /// <summary>
    /// Case-, separator- and punctuation-insensitive identity. <c>invoice-classifier</c>,
    /// <c>InvoiceClassifier</c> and <c>invoice_classifier</c> name the same brick to a person, so
    /// they must name the same brick here — a refusal that turned on a hyphen would be a bad refusal.
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var buffer = new char[value!.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }
        return new string(buffer, 0, length);
    }

    private static string ToPascal(string? brickId)
    {
        var parts = (brickId ?? string.Empty).Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}

/// <summary>The authored C# under a project, and the directories holding it.</summary>
/// <param name="SourceFiles">Absolute paths of authored <c>.cs</c> files.</param>
/// <param name="Directories">Absolute paths of directories walked (build output excluded).</param>
public sealed record ProjectSourceInventory(
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> Directories);
