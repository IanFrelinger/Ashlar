using System.Text.Json;
using Ashlar.Abstractions.Security.Egress;
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
    int LedgerEntries);

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

        return new BundleInfo(name, rid, verification.Verified, certified, fingerprint, ledgerEntries);
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
    public static List<string> StageApp(string projectDir, string bundleDir, string site)
    {
        ArgumentNullException.ThrowIfNull(site);
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.FileExport, site, "file:" + bundleDir)).ThrowIfRefused();
        return CopyApp(projectDir, bundleDir);
    }

    internal static List<string> StageAppWithInitiator(string projectDir, string bundleDir, string site, EgressInitiator initiator)
    {
        ArgumentNullException.ThrowIfNull(site);
        // SPEC-007: the project tree leaves through the bundle (EG-FILE-01 native, EG-FILE-02 cloud).
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.FileExport, site, "file:" + bundleDir)
            { Initiator = initiator }).ThrowIfRefused();
        return CopyApp(projectDir, bundleDir);
    }

    private static List<string> CopyApp(string projectDir, string bundleDir)
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
        return written;
    }

    /// <summary>
    /// Stages the project into <paramref name="bundleDir"/>/app and writes the launchers, the
    /// bundle descriptor, and a README. Returns the relative paths written. The runtime binary is
    /// added separately (see the export command's publish step).
    /// </summary>
    public static IReadOnlyList<string> Stage(string projectDir, string bundleDir, BundleInfo info)
        => Stage(projectDir, bundleDir, info, EgressInitiator.Unspecified);

    internal static IReadOnlyList<string> Stage(string projectDir, string bundleDir, BundleInfo info, EgressInitiator initiator)
    {
        var written = StageAppWithInitiator(projectDir, bundleDir, "EG-FILE-01", initiator);

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
            + "## Run it\n\n"
            + (info.Rid.StartsWith("win", StringComparison.Ordinal)
                ? "```\nrun.cmd \"<your request>\"\n```\n\n"
                : "```\n./run.sh \"<your request>\"\n```\n\n")
            + "The launcher **verifies the application against its own contract and ledger before it runs** —\n"
            + "if the app or its history were altered, it refuses. That is the whole point: the download\n"
            + "proves its own certification.\n\n"
            + "- `app/` — the project: its contract, its operator-owned policy, its signed ledger.\n"
            + $"- `{exe}` — the self-contained runtime (no .NET install required).\n"
            + "- `bundle.json` — what is inside and what certifies it.\n";
        WriteText(Path.Combine(bundleDir, "README.md"), readme, written, bundleDir);

        return written;
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
                throw new InvalidOperationException(
                    $"refusing to stage '{rel}': it is a symlink or junction, and a link can pull content from outside the project into the bundle. Replace it with the real file, or remove it, and re-export.");
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
