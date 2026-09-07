using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Ashlar.Manifest;

namespace Ashlar.CLI.Commands;

/// <summary>
/// <c>ashlar export</c> — turn a certified project into something you can hand someone.
///
/// <para><c>export native</c> produces a portable, self-proving application bundle: the project
/// staged beside a self-contained runtime and a launcher that verifies before it runs. Download,
/// unzip, run — offline, no install, and it proves its own certification on launch. Cloud targets
/// (<c>aws</c>, <c>azure</c>) are the next slice.</para>
/// </summary>
public sealed class ExportCommand : Command
{
    /// <summary>Creates a new ExportCommand instance.</summary>
    public ExportCommand() : base("export", "Export a certified project as a portable application bundle.")
    {
        AddCommand(BuildNative());
        AddCommand(BuildCloud(CloudTarget.Aws));
        AddCommand(BuildCloud(CloudTarget.Azure));
    }

    private static Command BuildCloud(CloudTarget target)
    {
        var (name, desc) = target == CloudTarget.Aws
            ? ("aws", "Build a one-command AWS deploy bundle (ECS Fargate one-shot task via ECR).")
            : ("azure", "Build a one-command Azure deploy bundle (Container Instances via ACR).");
        var pathOpt = new Option<DirectoryInfo>(
            name: "--path",
            description: "Project directory to export (defaults to current).",
            getDefaultValue: () => new DirectoryInfo(Environment.CurrentDirectory));
        var outOpt = new Option<DirectoryInfo>("--out", $"Output directory; the bundle lands in <out>/<name>-{name}.") { IsRequired = true };
        var imageOpt = new Option<string?>(
            "--runtime-image",
            $"Runtime image the bundle layers the app onto (default {CloudBundle.RuntimeImage}). Pin a version or digest so the verifier that runs the app is the one you tested.");

        var cmd = new Command(name, desc) { pathOpt, outOpt, imageOpt };
        cmd.SetHandler((InvocationContext ctx) =>
        {
            ctx.ExitCode = ExecuteCloud(
                ctx.ParseResult.GetValueForOption(pathOpt)!,
                ctx.ParseResult.GetValueForOption(outOpt)!,
                target,
                ctx.ParseResult.GetValueForOption(imageOpt));
        });
        return cmd;
    }

    private static int ExecuteCloud(DirectoryInfo directory, DirectoryInfo outDir, CloudTarget target, string? runtimeImage)
    {
        if (NotAProject(directory) is { } notAProject)
        {
            Console.Error.WriteLine(notAProject);
            return 1;
        }

        var targetName = target == CloudTarget.Aws ? "aws" : "azure";
        var info = NativeBundle.Describe(directory.FullName, targetName);
        if (!info.Verified)
        {
            Console.Error.WriteLine(DoesNotVerify(info));
            return 65;
        }

        var bundleDir = Path.Combine(outDir.FullName, $"{Safe(info.Name)}-{targetName}");
        if (Directory.Exists(bundleDir))
        {
            Directory.Delete(bundleDir, recursive: true);
        }
        try
        {
            CloudBundle.Stage(directory.FullName, bundleDir, info, target, runtimeImage);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 65;
        }

        // The container this bundle deploys runs `verify` before it runs the app, exactly as the
        // native launcher does. A cloud bundle that cannot verify itself is a deployment that
        // refuses to start in ECS/ACI, where the exit code is much harder to read than it is here.
        if (NativeBundle.SelfVerificationRefusal(directory.FullName, bundleDir) is { } refusal)
        {
            Console.Error.WriteLine(refusal);
            return 65;
        }

        var effectiveImage = string.IsNullOrWhiteSpace(runtimeImage) ? CloudBundle.RuntimeImage : runtimeImage;
        Console.WriteLine();
        var verdict = info.Certified ? Gold("✓ CERTIFIED cloud bundle") : Gold("✓ VERIFIED cloud bundle");
        Console.WriteLine($"  {verdict}  {info.Name} · {targetName}");
        Console.WriteLine($"  {Dim(info.Certified ? $"signed {info.SignerFingerprint} · {info.LedgerEntries} ledger entr{(info.LedgerEntries == 1 ? "y" : "ies")}" : "unsigned — run `ashlar keys init` and re-export to certify")}");
        if (effectiveImage.EndsWith(":latest", StringComparison.Ordinal))
        {
            // Honesty over polish: the verifier inside the container is whatever :latest resolves
            // to when the operator builds. Say so, and say how to pin it.
            Console.WriteLine($"  {Dim($"runtime image: {effectiveImage} (mutable tag — pass --runtime-image with a version or digest to pin the verifier)")}");
        }
        RenderScope(info);
        RenderStagingNotes(directory.FullName);
        Console.WriteLine($"  {Dim($"→ {bundleDir}")}");
        Console.WriteLine($"  {Dim($"deploy + run it:  ./deploy-{targetName}.sh \"<request>\"  (the container verifies before it runs)")}");
        return 0;
    }

    /// <summary>
    /// The refusal for a directory that is not an Ashlar project, or null when it is one. Names the
    /// documents that are missing and the command that creates them — the same wording
    /// <c>ashlar verify</c> uses, because "not an ashlar project: /some/path" with no next step is
    /// a dead end for someone who mistyped <c>--path</c>.
    /// </summary>
    private static string? NotAProject(DirectoryInfo directory)
    {
        var missing = new[] { "ashlar.yaml", "ashlar.policy.yaml" }
            .Where(f => !File.Exists(Path.Combine(directory.FullName, f))).ToList();
        if (missing.Count == 0)
        {
            return null;
        }
        return $"not an ashlar project: missing {string.Join(" and ", missing)} in {directory.FullName}"
             + Environment.NewLine + "start one with:  ashlar init <name>"
             + Environment.NewLine + "or point at the project you meant:  --path <directory>";
    }

    /// <summary>
    /// The refusal for a project that does not verify — with the courses that failed, named here.
    ///
    /// <para>It used to say only "the project does not verify. fix it, then: ashlar verify". That
    /// fix runs, which is why it survived; but it withheld what this command had already computed
    /// and made the operator run a second command to be told it. The courses are printed, then the
    /// re-run that re-certifies.</para>
    /// </summary>
    private static string DoesNotVerify(BundleInfo info)
    {
        var lines = new List<string>
        {
            "refusing to export: the project does not verify, and a bundle is only worth making from one that does.",
        };
        foreach (var course in (info.Courses ?? Array.Empty<CourseResult>()).Where(c => !c.Passed))
        {
            lines.Add($"  course '{course.Name}' failed — {course.Detail}");
        }
        lines.Add("fix what those name, then:  ashlar verify   (it re-runs the courses and re-certifies), and export again.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Prints what the export did on the operator's behalf and what the bundle still cannot promise:
    /// the directories the policy made it create inside <c>app/</c>, and — the one failure a bundle's
    /// own self-verification structurally cannot catch — an absolute <c>sandbox.root</c>.
    /// </summary>
    private static void RenderStagingNotes(string projectDir)
    {
        var dirs = NativeBundle.StagedPolicyDirectories(projectDir);
        if (dirs.Count > 0)
        {
            Console.WriteLine($"  {Dim($"created empty in the bundle, because sandbox names {(dirs.Count == 1 ? "it" : "them")}: {string.Join(", ", dirs.Select(d => "app/" + d.Replace(Path.DirectorySeparatorChar, '/')))}")}");
        }
        foreach (var note in NativeBundle.PortabilityNotes(projectDir))
        {
            Console.WriteLine($"  {Dim(note)}");
        }
    }

    /// <summary>
    /// Prints what the exported verdict COVERS, beside the verdict. `export` inherits the whole of
    /// `verify`'s honesty problem: it printed a gold CERTIFIED banner over a project holding no code
    /// at all, and shipped a bundle whose README repeated the claim. The scope line travels with the
    /// bundle (bundle.json, README.md) and is printed here so the person exporting sees it first.
    /// </summary>
    private static void RenderScope(BundleInfo info)
    {
        if (info.Scope is not { } scope)
        {
            return;
        }
        Console.WriteLine($"  {Dim(scope.Summary)}");
        if (!scope.CoversCode)
        {
            Console.WriteLine($"  {Dim("this bundle carries no application code — add the code this project is meant to run, then re-export.")}");
        }
    }

    private static Command BuildNative()
    {
        var pathOpt = new Option<DirectoryInfo>(
            name: "--path",
            description: "Project directory to export (defaults to current).",
            getDefaultValue: () => new DirectoryInfo(Environment.CurrentDirectory));
        var outOpt = new Option<DirectoryInfo>("--out", "Output directory; the bundle lands in <out>/<name>-<rid>.") { IsRequired = true };
        var ridOpt = new Option<string>(
            name: "--rid",
            description: "Target runtime identifier, e.g. linux-x64, win-x64, osx-arm64 (defaults to this machine's).",
            getDefaultValue: () => RuntimeInformation.RuntimeIdentifier);
        var cliProjectOpt = new Option<FileInfo?>("--cli-project", "Path to Ashlar.CLI.csproj, used to publish the self-contained runtime. Auto-detected from the repo when omitted.");
        var noRuntimeOpt = new Option<bool>("--no-runtime", () => false, "Stage the bundle but skip building the runtime binary (writes RUNTIME.md with the publish command).");
        var zipOpt = new Option<bool>("--zip", () => false, "Also produce a single .zip of the bundle.");

        var cmd = new Command("native", "Build a portable, self-proving single-file application bundle.")
        {
            pathOpt, outOpt, ridOpt, cliProjectOpt, noRuntimeOpt, zipOpt,
        };
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            ctx.ExitCode = await ExecuteAsync(
                ctx.ParseResult.GetValueForOption(pathOpt)!,
                ctx.ParseResult.GetValueForOption(outOpt)!,
                ctx.ParseResult.GetValueForOption(ridOpt)!,
                ctx.ParseResult.GetValueForOption(cliProjectOpt),
                ctx.ParseResult.GetValueForOption(noRuntimeOpt),
                ctx.ParseResult.GetValueForOption(zipOpt),
                ctx.GetCancellationToken());
        });
        return cmd;
    }

    private static async Task<int> ExecuteAsync(
        DirectoryInfo directory, DirectoryInfo outDir, string rid, FileInfo? cliProject, bool noRuntime, bool zip, CancellationToken ct)
    {
        if (NotAProject(directory) is { } notAProject)
        {
            Console.Error.WriteLine(notAProject);
            return 1;
        }

        var info = NativeBundle.Describe(directory.FullName, rid);
        if (!info.Verified)
        {
            // You ship governed apps that pass — not broken ones.
            Console.Error.WriteLine(DoesNotVerify(info));
            return 65;
        }

        var bundleDir = Path.Combine(outDir.FullName, $"{Safe(info.Name)}-{rid}");
        if (Directory.Exists(bundleDir))
        {
            Directory.Delete(bundleDir, recursive: true);
        }
        Directory.CreateDirectory(bundleDir);
        try
        {
            NativeBundle.Stage(directory.FullName, bundleDir, info);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 65;
        }

        // Checked BEFORE the runtime is published and before the zip is cut: publishing takes a
        // minute, and there is nothing to spend it on if the thing being wrapped cannot start. A
        // bundle whose own run.sh exits 65 must never be reported as a successful export.
        if (NativeBundle.SelfVerificationRefusal(directory.FullName, bundleDir) is { } refusal)
        {
            Console.Error.WriteLine(refusal);
            return 65;
        }

        var exeName = rid.StartsWith("win", StringComparison.Ordinal) ? "ashlar.exe" : "ashlar";
        var runtimeBuilt = false;
        if (!noRuntime)
        {
            var proj = ResolveCliProject(cliProject);
            if (proj is null)
            {
                WriteRuntimeGuide(bundleDir, rid, exeName);
                Console.WriteLine($"  {Dim("could not locate Ashlar.CLI.csproj to build the runtime — wrote RUNTIME.md with the command.")}");
                Console.WriteLine($"  {Dim("pass --cli-project <path> to build it, or --no-runtime to silence this.")}");
            }
            else
            {
                Console.WriteLine($"  {Dim($"publishing self-contained runtime for {rid} — this can take a minute…")}");
                var ok = await PublishRuntimeAsync(proj, rid, bundleDir, exeName, ct);
                if (ok)
                {
                    runtimeBuilt = true;
                }
                else
                {
                    // The staged bundle is the valuable, deterministic deliverable — do not fail the
                    // whole export because the runtime binary could not be produced here. Leave the
                    // publish command in RUNTIME.md so a release step (or a publish-clean checkout)
                    // can drop the runtime in.
                    WriteRuntimeGuide(bundleDir, rid, exeName);
                    Console.WriteLine($"  {Dim("runtime publish did not complete — the staged bundle is intact; RUNTIME.md has the command.")}");
                }
            }
        }
        else
        {
            WriteRuntimeGuide(bundleDir, rid, exeName);
        }

        string? zipPath = null;
        if (zip)
        {
            zipPath = bundleDir + ".zip";
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
            ZipFile.CreateFromDirectory(bundleDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        }

        Console.WriteLine();
        var verdict = info.Certified ? Gold("✓ CERTIFIED bundle") : Gold("✓ VERIFIED bundle");
        Console.WriteLine($"  {verdict}  {info.Name} · {rid}");
        Console.WriteLine($"  {Dim(info.Certified ? $"signed {info.SignerFingerprint} · {info.LedgerEntries} ledger entr{(info.LedgerEntries == 1 ? "y" : "ies")}" : "unsigned — run `ashlar keys init` and re-export to certify")}");
        RenderScope(info);
        RenderStagingNotes(directory.FullName);
        Console.WriteLine($"  {Dim(runtimeBuilt ? $"runtime: {exeName} (self-contained, single file)" : "runtime: not built (see RUNTIME.md)")}");
        Console.WriteLine($"  {Dim($"→ {(zipPath ?? bundleDir)}")}");
        Console.WriteLine($"  {Dim(runtimeBuilt ? $"run it:  {(rid.StartsWith("win", StringComparison.Ordinal) ? "run.cmd" : "./run.sh")} \"<request>\"" : "add the runtime, then run the launcher")}");
        return 0;
    }

    private static FileInfo? ResolveCliProject(FileInfo? explicitProject)
    {
        if (explicitProject is not null)
        {
            return explicitProject.Exists ? explicitProject : null;
        }
        // Walk up from the running assembly looking for the CLI project in the repo layout.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "application", "src", "Ashlar.CLI", "Ashlar.CLI.csproj");
            if (File.Exists(candidate))
            {
                return new FileInfo(candidate);
            }
        }
        return null;
    }

    private static async Task<bool> PublishRuntimeAsync(FileInfo cliProject, string rid, string bundleDir, string exeName, CancellationToken ct)
    {
        // Publish to a temp dir, then lift out just the single-file exe — the bundle stays clean.
        var tmp = Path.Combine(bundleDir, ".publish-tmp");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
        };
        foreach (var a in new[]
        {
            "publish", cliProject.FullName, "-c", "Release", "-r", rid,
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            // Self-extract ALL content (managed assemblies included), so Assembly.Location resolves
            // to a real path at runtime — the runtime's Roslyn analysis and hot-swap paths depend
            // on it. Disable the static single-file analyzer: it flags those Location uses at build
            // time not knowing they will be extracted, and TreatWarningsAsErrors promotes them.
            "-p:IncludeAllContentForSelfExtract=true",
            "-p:EnableSingleFileAnalyzer=false",
            "-p:PublishTrimmed=false",
            "-o", tmp,
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return false;
        }
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return false;
        }

        var built = Path.Combine(tmp, exeName);
        if (!File.Exists(built))
        {
            Console.Error.WriteLine($"publish succeeded but {exeName} was not found in the output.");
            return false;
        }
        var dest = Path.Combine(bundleDir, exeName);
        File.Copy(built, dest, overwrite: true);
        if (!OperatingSystem.IsWindows() && !rid.StartsWith("win", StringComparison.Ordinal))
        {
            File.SetUnixFileMode(dest,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        try
        {
            Directory.Delete(tmp, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp is cosmetic; the bundle has what it needs.
        }
        return true;
    }

    private static void WriteRuntimeGuide(string bundleDir, string rid, string exeName)
    {
        var guide =
            $"# Adding the runtime for {rid}\n\n"
            + $"This bundle is staged but does not yet include the `{exeName}` runtime. Produce a\n"
            + "self-contained, single-file runtime and drop it in this folder:\n\n"
            + "```\n"
            + $"dotnet publish application/src/Ashlar.CLI -c Release -r {rid} \\\n"
            + "  --self-contained true -p:PublishSingleFile=true \\\n"
            + "  -p:IncludeAllContentForSelfExtract=true -p:EnableSingleFileAnalyzer=false -o out\n"
            + $"cp out/{exeName} .\n"
            + "```\n\n"
            + "Then the launcher (`run.sh` / `run.cmd`) verifies and runs the app offline.\n";
        File.WriteAllText(Path.Combine(bundleDir, "RUNTIME.md"), guide);
    }

    private static string Safe(string name)
    {
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length > 0 ? new string(chars) : "app";
    }

    private static readonly bool Color =
        Environment.GetEnvironmentVariable("NO_COLOR") is null && !Console.IsOutputRedirected;
    private static string Paint(string ansi, string t) => Color ? $"\x1b[{ansi}m{t}\x1b[0m" : t;
    private static string Gold(string t) => Paint("33", t);
    private static string Dim(string t) => Paint("90", t);
}
