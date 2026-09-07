using System.Text.Json;
using Ashlar.Manifest;
using Ashlar.Manifest.Ledger;
using Ashlar.Manifest.Signing;

namespace Ashlar.CLI.Commands;

/// <summary>What a project is, for the purpose of shipping it: its name, whether it verifies,
/// and — if it was ever certified — the signer and how many signed entries its ledger holds.</summary>
public sealed record BundleInfo(
    string Name,
    string Rid,
    bool Verified,
    bool Certified,
    string? SignerFingerprint,
    int LedgerEntries,
    // What the verdict this bundle carries actually covers. A bundle that ships a CERTIFIED banner
    // over a project with no code in it is the same silent overclaim `ashlar verify` used to make;
    // the scope travels with the bundle so the person who unzips it reads the same honest line.
    CertificationScope? Scope = null,
    // The courses exactly as they ran at the source. A refusal to export has to name what failed —
    // "the project does not verify" with no course beside it sends the operator to a second command
    // to learn something this one already knew.
    IReadOnlyList<CourseResult>? Courses = null);

/// <summary>
/// What a project's <c>sandbox</c> declaration means for a BUNDLE: the root as written, whether it
/// can travel at all, and the directories the export creates inside the staged app.
/// </summary>
/// <param name="Root">The <c>sandbox.root</c> value exactly as the policy writes it.</param>
/// <param name="RootIsAbsolute">The root names a directory on a machine, not one in the bundle.</param>
/// <param name="RootEscapesProject">The root is relative but resolves outside the project directory.</param>
/// <param name="Directories">Project-relative directories to create inside the staged app; empty
/// when the root cannot travel.</param>
public sealed record PolicySandbox(
    string Root,
    bool RootIsAbsolute,
    bool RootEscapesProject,
    IReadOnlyList<string> Directories)
{
    /// <summary>True when the root can be carried in a bundle at all.</summary>
    public bool Travels => !RootIsAbsolute && !RootEscapesProject;
}

/// <summary>
/// Stages a governed Ashlar application into a portable, self-proving bundle: the project (its
/// contract, its operator-owned policy, its signed ledger, its bricks) laid out beside a launcher
/// that <em>verifies before it runs</em>. Drop the self-contained runtime next to it (the command
/// publishes one) and the whole folder is a download-and-run agentic application — it proves its
/// own certification, offline, on first launch.
///
/// <para>The staging is deterministic and does no network or build work, so it is unit-tested on
/// its own; producing the native runtime binary is a separate <c>dotnet publish</c> step.</para>
/// </summary>
public static class NativeBundle
{
    /// <summary>The bundle format tag.</summary>
    public const string Format = "ashlar-native/v1";

    /// <summary>
    /// Reads what the project is without changing it: does it verify, was it certified (an intact
    /// signed ledger), and by whom. A corrupt ledger surfaces as not-certified here and fails loudly
    /// at <c>verify</c> time; this describe step never throws for it.
    /// </summary>
    public static BundleInfo Describe(string projectDir, string rid)
    {
        var manifestYaml = File.ReadAllText(Path.Combine(projectDir, "ashlar.yaml"));
        var policyYaml = File.ReadAllText(Path.Combine(projectDir, "ashlar.policy.yaml"));
        var verification = ProjectVerifier.Verify(manifestYaml, policyYaml, projectDir);
        var name = ManifestLoader.TryLoad(manifestYaml, out var manifest, out _)
            ? manifest!.Metadata.Name
            : "app";

        var certified = false;
        string? fingerprint = null;
        var ledgerEntries = 0;
        try
        {
            var chain = new InstanceLedger(Path.Combine(projectDir, ".ashlar")).VerifyChain();
            ledgerEntries = chain.Count;
            // Certified only when the signed head attests THESE exact documents — an intact chain
            // over a since-edited contract is not a certification of what we are about to ship.
            certified = chain.Count > 0
                && string.Equals(chain.Head?.Subject, InstanceLedger.Subject(manifestYaml, policyYaml), StringComparison.Ordinal);
            if (chain.Head?.Signer is { } signer)
            {
                fingerprint = OperatorKey.Fingerprint(Convert.FromBase64String(signer));
            }
        }
        catch (InvalidOperationException)
        {
            // A corrupt ledger is not certified; verify will refuse it at run time.
        }

        return new BundleInfo(
            name, rid, verification.Verified, certified, fingerprint, ledgerEntries, verification.Scope, verification.Courses);
    }

    /// <summary>
    /// Stages the governed app itself into <paramref name="bundleDir"/>/app — its contract, its
    /// operator-owned policy, its signed ledger (the proof it carries), and its bricks. Copies only
    /// these, never bin/obj/.git or the output itself. Shared by every exporter (native, cloud):
    /// what travels is the same regardless of where it lands.
    ///
    /// <para>What is deliberately EXCLUDED from <c>.ashlar/</c>: <c>keys/</c> (an operator who
    /// pointed <c>ASHLAR_KEY_DIR</c> inside the project must never find their PRIVATE key inside a
    /// shipped bundle — SPEC-006's first rule), <c>forge/</c> (raw held/rejected proposal content
    /// is working state, not something to distribute), and lock/temp files. The gates records and
    /// the signed ledger — the governance history the bundle exists to carry — stay.</para>
    /// </summary>
    public static List<string> StageApp(string projectDir, string bundleDir)
    {
        var appDir = Path.Combine(bundleDir, "app");
        Directory.CreateDirectory(appDir);
        var written = new List<string>();
        CopyFile(Path.Combine(projectDir, "ashlar.yaml"), Path.Combine(appDir, "ashlar.yaml"), written, bundleDir);
        CopyFile(Path.Combine(projectDir, "ashlar.policy.yaml"), Path.Combine(appDir, "ashlar.policy.yaml"), written, bundleDir);
        CopyTreeIfPresent(Path.Combine(projectDir, ".ashlar"), Path.Combine(appDir, ".ashlar"), written, bundleDir,
            exclude: rel =>
            {
                var top = rel.Split('/', '\\')[0];
                if (string.Equals(top, "keys", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(top, "forge", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                var name = Path.GetFileName(rel);
                return name is ".lock" || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase);
            });
        // src/ is inside the project too: the same operator who pointed ASHLAR_KEY_DIR at
        // .ashlar/keys can point it at src/keys, and build output is not cargo. Same rule,
        // same filter shape as the .ashlar tree above.
        CopyTreeIfPresent(Path.Combine(projectDir, "src"), Path.Combine(appDir, "src"), written, bundleDir,
            exclude: rel =>
            {
                var parts = rel.Split('/', '\\');
                if (parts[0] is "bin" or "obj" or ".git")
                {
                    return true;
                }
                if (parts.Any(p => string.Equals(p, "keys", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
                var name = Path.GetFileName(rel);
                return name is ".lock" || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase);
            });
        StageDeclaredBrickSources(projectDir, appDir, written, bundleDir);
        StagePolicyDirectories(projectDir, appDir, written, bundleDir);
        return written;
    }

    /// <summary>
    /// Reads what the project's <c>sandbox</c> declaration means for a bundle: the root as the
    /// policy writes it, whether it can travel at all, and the project-relative directories the
    /// export must create inside the staged app. Returns null when the policy cannot be read or
    /// loaded — an unloadable policy is the verifier's refusal to make, in its own wording.
    /// </summary>
    /// <remarks>
    /// <para>The envelope course requires <c>sandbox.root</c> to EXIST. Three shapes of root, three
    /// different truths for a bundle, and the export has to tell them apart because they need three
    /// different things said to the operator:</para>
    /// <list type="bullet">
    ///   <item><description>relative and inside the project — it travels; the export creates it in
    ///   the staged app (see <see cref="StagePolicyDirectories"/>).</description></item>
    ///   <item><description>relative but resolving OUTSIDE the project (<c>../shared</c>) — nothing
    ///   outside the project can travel, so no amount of creating directories at the source helps;
    ///   the refusal has to say to move it in.</description></item>
    ///   <item><description>absolute (<c>/var/lib/app</c>) — it names a directory on a MACHINE. The
    ///   staged copy verifies here whenever this machine happens to have it, and then exits 65 on
    ///   the machine that does not. That is a portability note, not a refusal.</description></item>
    /// </list>
    /// </remarks>
    public static PolicySandbox? DescribeSandbox(string projectDir)
    {
        string policyYaml;
        try
        {
            policyYaml = File.ReadAllText(Path.Combine(projectDir, "ashlar.policy.yaml"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!PolicyLoader.TryLoad(policyYaml, out var policy, out _))
        {
            return null;
        }

        var root = policy!.Sandbox.Root;
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }
        if (Path.IsPathRooted(root))
        {
            return new PolicySandbox(root, RootIsAbsolute: true, RootEscapesProject: false, Array.Empty<string>());
        }

        var rootRelative = RelativeInsideProject(projectDir, projectDir, root);
        if (rootRelative is null)
        {
            return new PolicySandbox(root, RootIsAbsolute: false, RootEscapesProject: true, Array.Empty<string>());
        }

        // Writable paths resolve BENEATH the root — the same way the envelope course resolves them.
        var directories = new List<string>();
        if (rootRelative.Length > 0)
        {
            directories.Add(rootRelative);
        }
        var rootAtSource = Path.Combine(projectDir, rootRelative);
        foreach (var writable in policy.Sandbox.Writable)
        {
            if (string.IsNullOrWhiteSpace(writable) || Path.IsPathRooted(writable))
            {
                continue;   // An absolute writable is the envelope course's refusal, by name, at the source.
            }
            if (RelativeInsideProject(rootAtSource, rootAtSource, writable) is null)
            {
                continue;   // Escapes the root: the envelope course refuses that at the source, by name.
            }
            var rel = RelativeInsideProject(projectDir, rootAtSource, writable);
            if (rel is { Length: > 0 } && !directories.Contains(rel, StringComparer.Ordinal))
            {
                directories.Add(rel);
            }
        }
        return new PolicySandbox(root, RootIsAbsolute: false, RootEscapesProject: false, directories);
    }

    /// <summary>
    /// Resolves <paramref name="relative"/> against <paramref name="baseDir"/> and returns it as a
    /// path relative to <paramref name="projectDir"/> — empty for the project directory itself, and
    /// null when it lands outside the project (or cannot be resolved at all).
    /// </summary>
    private static string? RelativeInsideProject(string projectDir, string baseDir, string relative)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(baseDir, relative));
            // A trailing separator survives GetRelativePath, so `./out/` and `./out` — the same
            // directory, written twice in one writable: list — came back as two different strings
            // and the export said it had created two directories. Normalise before comparing.
            var rel = Path.GetRelativePath(Path.GetFullPath(projectDir), full)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (rel is "." or "")
            {
                return string.Empty;
            }
            var escapes = Path.IsPathRooted(rel)
                || rel == ".."
                || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel.StartsWith("../", StringComparison.Ordinal);
            return escapes ? null : rel;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;   // A path the export cannot resolve is one the verifier refuses by name.
        }
    }

    /// <summary>
    /// The directories, relative to the staged <c>app/</c>, that this project's policy makes the
    /// export create — empty ones. Public so the export can DISCLOSE them: staging on the
    /// operator's behalf is only defensible if it is said out loud.
    /// </summary>
    public static IReadOnlyList<string> StagedPolicyDirectories(string projectDir)
        => DescribeSandbox(projectDir)?.Directories ?? Array.Empty<string>();

    /// <summary>
    /// Creates, inside the staged app, the directories the POLICY declares: <c>sandbox.root</c> and
    /// every writable path beneath it.
    /// </summary>
    /// <remarks>
    /// <para>Without this, narrowing the sandbox — the ordinary hardening move — made
    /// <c>ashlar export</c> an unfixable refusal. A policy naming <c>sandbox.root: ./workspace</c>
    /// verifies at the source, because the directory is there; the staged copy carries
    /// <c>ashlar.yaml</c>, <c>ashlar.policy.yaml</c>, <c>.ashlar/</c>, <c>src/</c> and declared-brick
    /// carriers, and <c>workspace/</c> is none of those, so the envelope course failed on the copy
    /// with "sandbox.root './workspace' does not exist" and <c>SelfVerificationRefusal</c> refused
    /// the export. Forever: the refusal's own named fix — "create it, commit a .gitkeep, re-export"
    /// — was run verbatim and returned the byte-identical refusal, because the missing directory
    /// was never missing at the source. There was no override flag, and the same project exported
    /// fine before the self-verification check existed.</para>
    ///
    /// <para>Staging it is not the export inventing policy. The policy IS the declaration of where
    /// this application writes; carrying that declaration while dropping the thing it declares is
    /// what made the bundle unable to prove itself. What travels is the directory, EMPTY — its
    /// contents are the app's runtime state, not cargo, and copying them would ship whatever the
    /// last run happened to leave there. Exactly two kinds of directory are created, both named by
    /// <c>sandbox</c> in the policy, and the export prints the list beside the verdict rather than
    /// quietly making more exports succeed.</para>
    ///
    /// <para>A root outside the project is left alone, and deliberately: nothing outside the
    /// project directory can travel in a bundle, so creating it here would fake a guarantee that
    /// only the target machine can meet. The refusal names that case, and only that case, itself.</para>
    /// </remarks>
    private static void StagePolicyDirectories(string projectDir, string appDir, List<string> written, string bundleRoot)
    {
        if (DescribeSandbox(projectDir) is not { } sandbox)
        {
            return;
        }

        foreach (var relative in sandbox.Directories)
        {
            var full = Path.Combine(appDir, relative);
            if (Directory.Exists(full))
            {
                continue;
            }
            try
            {
                Directory.CreateDirectory(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never a stack trace: the export command turns this into the operator's message.
                throw new InvalidOperationException(
                    $"refusing to stage this bundle: could not create '{relative}' inside it ({ex.Message}). "
                    + $"ashlar.policy.yaml names it under sandbox.root '{sandbox.Root}', and the bundle's launcher "
                    + "runs `verify --path app` first, so a bundle without it exits 65 on launch. "
                    + $"clear whatever holds '{Path.Combine(bundleRoot, "app", relative)}' — or export to a different "
                    + "--out directory — and re-export.");
            }
            written.Add(Path.GetRelativePath(bundleRoot, full));
        }
    }

    /// <summary>
    /// Stages the source of every brick the manifest DECLARES, wherever in the project it lives.
    ///
    /// <para>The composition course resolves declared bricks by scanning the whole project tree;
    /// this staging used to copy <c>src/</c> and nothing else. A brick implemented anywhere else —
    /// <c>bricks/</c>, <c>libs/</c>, a file at the project root — therefore certified at the source
    /// and was then DROPPED from the bundle, and the bundle's own launcher, which begins with
    /// <c>verify --path app</c>, failed course 2 and exited 65 on every machine. An export that
    /// reports CERTIFIED over an application that cannot start is worse than one that reports
    /// nothing, so what the courses resolve is what travels.</para>
    ///
    /// <para>The unit staged is the resolved file's TOP-LEVEL directory under the project, not the
    /// file: a brick is a project (a csproj, a README, nested folders), and carrying its .cs files
    /// out of the layout that resolved them would satisfy the course while shipping something no
    /// one can build. A resolved file sitting directly in the project root is carried alone,
    /// because its "directory" is the project.</para>
    /// </summary>
    private static void StageDeclaredBrickSources(string projectDir, string appDir, List<string> written, string bundleRoot)
    {
        string manifestYaml;
        try
        {
            manifestYaml = File.ReadAllText(Path.Combine(projectDir, "ashlar.yaml"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // Describe() already read it; a race here is not this method's to report.
        }

        if (!ManifestLoader.TryLoad(manifestYaml, out var manifest, out _) || manifest!.Bricks.Count == 0)
        {
            return;
        }

        var inventory = BrickSourceResolver.Scan(projectDir);
        // Already staged above, by their own rules. Restaging them would duplicate work and, for
        // .ashlar/, would defeat the keys/forge exclusions that protect the operator's private key.
        var alreadyStaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ashlar", "src" };
        var carrierDirectories = new SortedSet<string>(StringComparer.Ordinal);
        var looseFiles = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var brick in manifest.Bricks)
        {
            foreach (var file in BrickSourceResolver.Resolve(inventory, brick.Id))
            {
                var rel = Path.GetRelativePath(projectDir, file);
                if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                {
                    continue;   // Scan never leaves the project; belt and braces.
                }
                var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Length == 1)
                {
                    looseFiles.Add(rel);
                    continue;
                }
                if (alreadyStaged.Contains(segments[0]))
                {
                    continue;
                }
                carrierDirectories.Add(segments[0]);
            }
        }

        foreach (var carrier in carrierDirectories)
        {
            var source = Path.Combine(projectDir, carrier);
            // An --out directory inside the project would otherwise be copied into itself.
            if (IsSameOrUnder(bundleRoot, source))
            {
                continue;
            }
            CopyTreeIfPresent(source, Path.Combine(appDir, carrier), written, bundleRoot,
                exclude: rel =>
                {
                    var parts = rel.Split('/', '\\');
                    if (parts[0] is "bin" or "obj" or ".git")
                    {
                        return true;
                    }
                    if (parts.Any(p => string.Equals(p, "bin", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(p, "obj", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(p, "keys", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                    var name = Path.GetFileName(rel);
                    return name is ".lock" || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase);
                });
        }

        foreach (var loose in looseFiles)
        {
            var source = Path.Combine(projectDir, loose);
            if (!File.Exists(source))
            {
                continue;
            }
            CopyFile(source, Path.Combine(appDir, loose), written, bundleRoot);
        }
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="ancestor"/> or sits inside it.</summary>
    private static bool IsSameOrUnder(string candidate, string ancestor)
    {
        var a = Path.GetFullPath(ancestor).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return c.Equals(a, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs the project's own courses against the STAGED copy and returns a refusal naming what did
    /// not travel, or null when the bundle proves itself.
    ///
    /// <para>This is the guarantee, not the optimism: the launcher every export writes begins with
    /// <c>verify --path app</c>, so a staged copy that fails a course is an application that exits
    /// 65 on first launch, on every machine, while its bundle.json and README say
    /// <c>certified: true</c>. The source project verifying is not evidence that the COPY does —
    /// they are different directories, and only one of them is what the user receives. So the copy
    /// is verified before the export is allowed to call itself successful.</para>
    /// </summary>
    /// <param name="projectDir">The source project, used to name what is present there and absent here.</param>
    /// <param name="bundleDir">The staged bundle root (the one holding <c>app/</c>).</param>
    public static string? SelfVerificationRefusal(string projectDir, string bundleDir)
    {
        var appDir = Path.Combine(bundleDir, "app");
        var manifestPath = Path.Combine(appDir, "ashlar.yaml");
        var policyPath = Path.Combine(appDir, "ashlar.policy.yaml");
        if (!File.Exists(manifestPath) || !File.Exists(policyPath))
        {
            return "refusing to report this export as successful: the staged bundle has no "
                 + $"ashlar.yaml/ashlar.policy.yaml under {appDir}, so its launcher cannot verify it. "
                 + "This is an export bug, not a project one — re-run the export, and report it if it repeats.";
        }

        var staged = ProjectVerifier.Verify(
            File.ReadAllText(manifestPath), File.ReadAllText(policyPath), appDir);
        if (staged.Verified)
        {
            return null;
        }

        var lines = new List<string>
        {
            "refusing to report this export as successful: THE BUNDLE DOES NOT VERIFY ITSELF.",
            string.Empty,
            "the launcher this export writes starts with `verify --path app`, so a course that fails on the",
            "staged copy is an application that exits 65 on first launch, on every machine, while bundle.json",
            "and README.md say it is certified. the source project verifies; the copy does not, which means",
            "something the courses depend on did not travel:",
            string.Empty,
        };
        foreach (var course in staged.Courses.Where(c => !c.Passed))
        {
            lines.Add($"  course '{course.Name}' failed — {course.Detail}");
        }

        var stranded = StrandedBrickSources(projectDir, appDir);
        if (stranded.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("brick source that is in the project but NOT in the bundle:");
            foreach (var rel in stranded.Take(20))
            {
                lines.Add($"  {rel}");
            }
            if (stranded.Count > 20)
            {
                lines.Add($"  … and {stranded.Count - 20} more");
            }
        }

        lines.Add(string.Empty);
        lines.Add("fix:");
        lines.AddRange(RefusalFixes(projectDir, appDir, staged, stranded.Count > 0));
        lines.Add(string.Empty);
        lines.Add($"see it for yourself:  ashlar verify --path {appDir}");
        lines.Add($"the incomplete bundle was left at {bundleDir} so you can see what did and did not arrive.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The fix lines for THIS refusal — only the ones that apply to what actually failed.
    ///
    /// <para>This is the part that had to change. The list used to be fixed: three bullets printed
    /// for every failure, two of them about bricks. A project whose only problem was
    /// <c>sandbox.root: ../shared</c> was told "make it relative to the project" about a root that
    /// was already relative, and "a RELATIVE directory is created inside the bundle for you" about a
    /// directory the export had just declined to create. Every bullet was false for the failure in
    /// hand, so re-running the export reproduced the refusal byte for byte. A refusal that names a
    /// step which cannot be executed is worse than one that names none — so each bullet below is
    /// emitted only when it is the fix for the failure being reported, and every one of them was run
    /// against the failure it claims to fix.</para>
    /// </summary>
    private static List<string> RefusalFixes(
        string projectDir, string appDir, ProjectVerification staged, bool strandedBricks)
    {
        var fixes = new List<string>();
        var sandbox = DescribeSandbox(projectDir);
        var rootFailed = staged.Courses.Any(c =>
            !c.Passed && c.Name == "envelope" && c.Detail.Contains("sandbox.root", StringComparison.Ordinal));
        var compositionFailed = staged.Courses.Any(c => !c.Passed && c.Name == "composition");
        var example = Path.Combine(projectDir, "workspace");

        if (rootFailed && sandbox is { RootEscapesProject: true })
        {
            fixes.Add($"  - sandbox.root '{sandbox.Root}' resolves OUTSIDE the project directory, and nothing outside the");
            fixes.Add("    project travels in a bundle — so creating it at the source cannot help, however many times.");
            fixes.Add($"    move it in:  mkdir -p \"{example}\"");
            fixes.Add("    then set  `root: ./workspace`  under sandbox: in ashlar.policy.yaml.");
            fixes.Add("    a root inside the project IS created inside the bundle for you, empty, on the next export.");
        }
        else if (rootFailed && sandbox is { RootIsAbsolute: true })
        {
            fixes.Add($"  - sandbox.root '{sandbox.Root}' is an absolute path: it names a directory on a machine, not one");
            fixes.Add("    in the bundle, so a bundle can never carry it.");
            fixes.Add($"    make it travel:  mkdir -p \"{example}\"");
            fixes.Add("    then set  `root: ./workspace`  under sandbox: in ashlar.policy.yaml.");
            fixes.Add("    a root inside the project IS created inside the bundle for you, empty, on the next export.");
        }
        else if (rootFailed && sandbox is { Travels: true })
        {
            // The root can travel and the export creates it, so a failure here is the export's own.
            fixes.Add($"  - sandbox.root '{sandbox.Root}' should have been created inside the bundle by this export and");
            fixes.Add("    was not. that is an export bug, not a project one — re-run the export, and report it with");
            fixes.Add("    this message if it repeats.");
        }

        if (compositionFailed || strandedBricks)
        {
            fixes.Add("  - move the brick source listed above under the project directory: everything under it travels");
            fixes.Add("    except bin/, obj/, .git/, .ashlar/keys/ and .ashlar/forge/. a brick outside the project cannot");
            fixes.Add("    ship, wherever the source project happens to resolve it from.");
            fixes.Add("  - or delete that entry from bricks: in ashlar.yaml, if the project no longer uses it.");
        }

        if (fixes.Count == 0)
        {
            fixes.Add($"  - read the failure against the copy itself:  ashlar verify --path {appDir}");
            fixes.Add("    and compare it with  ashlar verify  at the source. the two directories are the only");
            fixes.Add("    difference; if nothing above explains it, that is an export bug — report it with both.");
            return fixes;
        }

        fixes.Add("  then re-certify the change:  ashlar verify   (at the source) and export again.");
        return fixes;
    }

    /// <summary>
    /// What is TRUE about this bundle that its own self-verification cannot catch — printed beside
    /// a successful export, never as a refusal.
    ///
    /// <para>An absolute <c>sandbox.root</c> is the case that matters. The staged copy verifies here
    /// whenever THIS machine happens to have that directory, so the export exits 0 and bundle.json
    /// says certified — and on the machine the bundle is handed to, the launcher's first line
    /// (<c>verify --path app</c>) fails the envelope course and the application exits 65. That is
    /// precisely the failure the self-verification exists to prevent, and the self-verification
    /// cannot see it, because it runs on the one machine where it does not happen. Refusing would
    /// be wrong — deploying onto a machine you provision is legitimate, and a refusal with no way
    /// past it is the defect this whole pass is about — so the export says it, names both real
    /// fixes, and exits 0.</para>
    /// </summary>
    public static IReadOnlyList<string> PortabilityNotes(string projectDir)
    {
        var notes = new List<string>();
        if (DescribeSandbox(projectDir) is not { RootIsAbsolute: true } sandbox)
        {
            return notes;
        }
        var example = Path.Combine(projectDir, "workspace");
        notes.Add($"NOT PORTABLE: sandbox.root '{sandbox.Root}' is an absolute path — a directory on a machine, not one in the bundle.");
        notes.Add("this export verified because THIS machine has it. the launcher runs `verify --path app` first, so on a");
        notes.Add("machine that does not, the application exits 65 while bundle.json and README.md say it is certified.");
        notes.Add($"make it travel:  mkdir -p \"{example}\"  then set  `root: ./workspace`  under sandbox: in ashlar.policy.yaml,");
        notes.Add("                 re-certify with  ashlar verify , and export again — that root is created inside the bundle for you.");
        notes.Add($"or keep it and provision '{sandbox.Root}' on the machine that runs the bundle, before launching it.");
        return notes;
    }

    /// <summary>
    /// Declared-brick source files present under the project and absent from the staged app —
    /// the concrete list a person needs to see, rather than "something did not travel".
    /// </summary>
    private static List<string> StrandedBrickSources(string projectDir, string appDir)
    {
        var stranded = new List<string>();
        try
        {
            var manifestYaml = File.ReadAllText(Path.Combine(projectDir, "ashlar.yaml"));
            if (!ManifestLoader.TryLoad(manifestYaml, out var manifest, out _))
            {
                return stranded;
            }
            var inventory = BrickSourceResolver.Scan(projectDir);
            foreach (var brick in manifest!.Bricks)
            {
                foreach (var file in BrickSourceResolver.Resolve(inventory, brick.Id))
                {
                    var rel = Path.GetRelativePath(projectDir, file);
                    if (!File.Exists(Path.Combine(appDir, rel)))
                    {
                        stranded.Add(rel);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The refusal still stands on the failed courses above; this list is detail, not proof.
        }
        return stranded.Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Stages the project into <paramref name="bundleDir"/>/app and writes the launchers, the
    /// bundle descriptor, and a README. Returns the relative paths written. The runtime binary is
    /// added separately (see the export command's publish step).
    /// </summary>
    public static IReadOnlyList<string> Stage(string projectDir, string bundleDir, BundleInfo info)
    {
        var written = StageApp(projectDir, bundleDir);

        var exe = "ashlar" + (info.Rid.StartsWith("win", StringComparison.Ordinal) ? ".exe" : string.Empty);
        // The launcher must not overclaim: "certified" is only true when a signed ledger attests
        // these exact documents — bundle.json and the README already say it honestly.
        var readiness = info.Certified ? "certified and ready." : "verified and ready (unsigned).";

        // run.sh — verify (self-prove), then run whatever the user asked for.
        var runSh =
            "#!/usr/bin/env sh\n"
            + "set -e\n"
            + "DIR=\"$(cd \"$(dirname \"$0\")\" && pwd)\"\n"
            + "\"$DIR/" + exe + "\" verify --path \"$DIR/app\"\n"
            + "if [ \"$#\" -gt 0 ]; then\n"
            + "  exec \"$DIR/" + exe + "\" run \"$@\" --path \"$DIR/app\"\n"
            + "else\n"
            + "  echo\n"
            + "  echo \"" + readiness + " run a request with:  ./run.sh \\\"classify the invoices in ./inbox\\\"\"\n"
            + "fi\n";
        WriteText(Path.Combine(bundleDir, "run.sh"), runSh, written, bundleDir);

        // run.cmd — the Windows launcher.
        var runCmd =
            "@echo off\r\n"
            + "setlocal\r\n"
            + "set \"DIR=%~dp0\"\r\n"
            + "\"%DIR%" + exe + "\" verify --path \"%DIR%app\"\r\n"
            + "if errorlevel 1 exit /b %errorlevel%\r\n"
            + "if \"%~1\"==\"\" (\r\n"
            + "  echo.\r\n"
            + "  echo " + readiness + " run a request with:  run.cmd \"classify the invoices in .\\inbox\"\r\n"
            + ") else (\r\n"
            + "  \"%DIR%" + exe + "\" run %* --path \"%DIR%app\"\r\n"
            + ")\r\n";
        WriteText(Path.Combine(bundleDir, "run.cmd"), runCmd, written, bundleDir);

        // bundle.json — what is inside and what certifies it.
        var descriptor = new
        {
            format = Format,
            name = info.Name,
            rid = info.Rid,
            verified = info.Verified,
            certified = info.Certified,
            signer = info.SignerFingerprint,
            ledgerEntries = info.LedgerEntries,
            scope = info.Scope is null ? null : new
            {
                summary = info.Scope.Summary,
                coversCode = info.Scope.CoversCode,
                sourceFiles = info.Scope.SourceFiles,
                declaredBricks = info.Scope.DeclaredBricks,
                resolvedBricks = info.Scope.ResolvedBricks,
            },
            runtime = exe,
            run = info.Rid.StartsWith("win", StringComparison.Ordinal) ? "run.cmd \"<request>\"" : "./run.sh \"<request>\"",
        };
        WriteText(Path.Combine(bundleDir, "bundle.json"),
            JsonSerializer.Serialize(descriptor, new JsonSerializerOptions { WriteIndented = true }), written, bundleDir);

        // README — the human's five-second orientation.
        var certLine = info.Certified
            ? $"It is **certified**: signed {info.SignerFingerprint}, with {info.LedgerEntries} entr{(info.LedgerEntries == 1 ? "y" : "ies")} in its tamper-evident ledger."
            : "It is **unsigned** — it verifies, but was not certified with an operator key. Run `ashlar keys init` at the source and re-export to certify.";
        var readme =
            $"# {info.Name} — a portable Ashlar application\n\n"
            + "This folder is a governed AI application you can run offline, with no install.\n\n"
            + $"{certLine}\n\n"
            + (info.Scope is { } scope
                ? $"Scope of that verdict: {scope.Summary}.\n\n"
                  + (scope.CoversCode
                      ? string.Empty
                      : "**There is no application code in this bundle.** The verdict above covers the two\n"
                        + "documents and nothing else — it does not attest an implementation.\n\n")
                : string.Empty)
            + "## Run it\n\n"
            + (info.Rid.StartsWith("win", StringComparison.Ordinal)
                ? "```\nrun.cmd \"<your request>\"\n```\n\n"
                : "```\n./run.sh \"<your request>\"\n```\n\n")
            + "The launcher **verifies the application against its own contract and ledger before it runs** —\n"
            + "if the app or its history were altered, it refuses. That is the whole point: the download\n"
            + "proves its own certification.\n\n"
            + "- `app/` — the project: its contract, its operator-owned policy, its signed ledger.\n"
            + $"- `{exe}` — the self-contained runtime (no .NET install required).\n"
            + "- `bundle.json` — what is inside and what certifies it.\n"
            // Say what the export created that the project did not hand it. These directories are
            // the only thing in app/ that was not copied from the project, and a bundle that
            // silently manufactures directories is a bundle you cannot audit against its source.
            + PolicyDirectoryNote(projectDir);
        WriteText(Path.Combine(bundleDir, "README.md"), readme, written, bundleDir);

        return written;
    }

    /// <summary>
    /// The README paragraph naming the directories the export created inside <c>app/</c>, and why.
    /// Empty when the policy asked for none. Shared by the native and cloud READMEs.
    /// </summary>
    public static string PolicyDirectoryNote(string projectDir)
    {
        var dirs = StagedPolicyDirectories(projectDir);
        if (dirs.Count == 0)
        {
            return string.Empty;
        }
        var list = string.Join(", ", dirs.Select(d => $"`app/{d.Replace(Path.DirectorySeparatorChar, '/')}/`"));
        return "\nCreated by the export, EMPTY: " + list + ".\n"
             + "`ashlar.policy.yaml` names these under `sandbox` as where this application may write, and the\n"
             + "verification the launcher runs requires the sandbox root to exist. Their contents are runtime\n"
             + "state, not part of the application, so nothing from the source project's copies was carried in.\n";
    }

    private static void CopyFile(string src, string dest, List<string> written, string bundleRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, overwrite: true);
        written.Add(Path.GetRelativePath(bundleRoot, dest));
    }

    private static void WriteText(string dest, string content, List<string> written, string bundleRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, content);
        written.Add(Path.GetRelativePath(bundleRoot, dest));
        if (dest.EndsWith(".sh", StringComparison.Ordinal) && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dest,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void CopyTreeIfPresent(string srcDir, string destDir, List<string> written, string bundleRoot, Func<string, bool>? exclude = null)
    {
        if (!Directory.Exists(srcDir))
        {
            return;
        }
        CopyTreeCore(srcDir, srcDir, destDir, written, bundleRoot, exclude);
    }

    private static void CopyTreeCore(string root, string dir, string destDir, List<string> written, string bundleRoot, Func<string, bool>? exclude)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            var rel = Path.GetRelativePath(root, entry);
            if (exclude?.Invoke(rel) == true)
            {
                continue;
            }
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Same rule ForgeApplier enforces on the write side: lexical containment is not
                // enough when a link is in the way. A link can pull content from outside the
                // project — including a private key — into the bundle, past every name filter.
                //
                // The FULL path, not the path relative to whichever subtree is being copied: the
                // message names a step ("remove it"), and `rm src/Widget/link.cs` typed from a
                // message that said 'Widget/link.cs' is a step that fails.
                throw new InvalidOperationException(
                    $"refusing to stage '{entry}': it is a symlink or junction, and a link can pull content from outside the project into the bundle. Replace it with the real file, or remove it, and re-export.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                CopyTreeCore(root, entry, destDir, written, bundleRoot, exclude);
            }
            else
            {
                CopyFile(entry, Path.Combine(destDir, rel), written, bundleRoot);
            }
        }
    }
}
