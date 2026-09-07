using System.CommandLine;
using System.CommandLine.Invocation;
using Ashlar.Manifest;
using Ashlar.Manifest.Ledger;
using Ashlar.Manifest.Signing;

namespace Ashlar.CLI.Commands;

/// <summary>
/// <c>ashlar verify</c> — run the courses against the project in the current directory and
/// render the wall.
///
/// <para>Thin by design: every judgement lives kernel-side in <see cref="ProjectVerifier"/>;
/// this class reads the two documents, renders course results in the fixed vocabulary
/// (SPEC-009: gold marks verification only, green is a per-course pass, red is failure,
/// NO_COLOR honoured), and maps the outcome to exit codes — 0 verified, 65 verification
/// failed, 1 usage/environment.</para>
///
/// <para>The verdict word is VERIFIED, not CERTIFIED: certified means signed, and nothing
/// signs yet. When real keys land, the provenance course and the signature line appear here;
/// until then this command prints <c>unsigned</c> and nothing else.</para>
/// </summary>
public sealed class VerifyCommand : Command
{
    /// <summary>Creates a new VerifyCommand instance.</summary>
    public VerifyCommand() : base("verify", "Run the courses against this project and render the wall.")
    {
        var pathOpt = new Option<DirectoryInfo>(
            name: "--path",
            description: "Project directory containing ashlar.yaml and ashlar.policy.yaml (defaults to current).",
            getDefaultValue: () => new DirectoryInfo(Environment.CurrentDirectory));

        AddOption(pathOpt);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            // The wall is an ANSI rendering, not a document, and `--format-json` is global so it
            // parses here regardless. Refuse it: a caller that asked for JSON must not receive the
            // wall under exit 0 and conclude from the exit code that the project verified.
            if (CommandExecutionSupport.RefuseJsonFormat(ctx.ParseResult, "verify", Console.Error) is { } refused)
            {
                ctx.ExitCode = refused;
                return;
            }

            var path = ctx.ParseResult.GetValueForOption(pathOpt)!;
            ctx.ExitCode = await ExecuteAsync(path);
        });
    }

    private const int ExitVerified = 0;
    private const int ExitVerificationFailed = 65;
    private const int ExitUsage = 1;

    private static async Task<int> ExecuteAsync(DirectoryInfo directory)
    {
        var manifestPath = Path.Combine(directory.FullName, "ashlar.yaml");
        var policyPath = Path.Combine(directory.FullName, "ashlar.policy.yaml");

        var missing = new[] { manifestPath, policyPath }.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine(
                "not an ashlar project: missing "
                + string.Join(", ", missing.Select(Path.GetFileName))
                + $" in {directory.FullName}");
            Console.Error.WriteLine("start one with:  ashlar init <name>");
            return ExitUsage;
        }

        var manifestYaml = File.ReadAllText(manifestPath);
        var policyYaml = File.ReadAllText(policyPath);
        var result = ProjectVerifier.Verify(manifestYaml, policyYaml, directory.FullName);

        // The provenance course fails when the documents no longer match the certification. For
        // an operator holding the key that is not a dead end — it is a re-certification: the base
        // courses (everything but provenance) pass, so signing a fresh entry over the CURRENT
        // documents makes them the certified ones again. Split that out here.
        var baseOk = result.Courses.Where(c => c.Name != "provenance").All(c => c.Passed);

        SigningIdentity? signer;
        try
        {
            signer = OperatorKey.TryLoad();
        }
        catch (InvalidOperationException ex)
        {
            // A corrupt operator key fails closed — never a silent fall-back to unsigned.
            Console.Error.WriteLine(ex.Message);
            return ExitUsage;
        }

        if (signer is not null && baseOk)
        {
            try
            {
                var ledger = new InstanceLedger(Path.Combine(directory.FullName, ".ashlar"));
                var entry = await ledger.AppendVerificationAsync(
                    signer,
                    InstanceLedger.Subject(manifestYaml, policyYaml),
                    verified: true,
                    result.Courses.Where(c => c.Name != "provenance")
                        .Select(c => new LedgerCourse { Name = c.Name, Passed = c.Passed, Detail = c.Detail }).ToList(),
                    DateTimeOffset.UtcNow);

                // Re-verify so the rendered provenance course reflects the entry just written —
                // the head now covers these documents, so it reads as certified, not stale.
                result = ProjectVerifier.Verify(manifestYaml, policyYaml, directory.FullName);
                RenderCourses(result);
                RenderCertified(result, signer.Fingerprint, entry.Seq);
                return ExitVerified;
            }
            catch (InvalidOperationException ex)
            {
                // A corrupt ledger cannot be re-certified over — append verifies the chain first.
                RenderCourses(result);
                Console.WriteLine($"  {Bad("× FAILED")}  {Dim(ex.Message)}");
                Console.WriteLine($"  {Dim("exit 65")}");
                return ExitVerificationFailed;
            }
        }

        // No key to re-certify with, or a base course failed. The result — including a provenance
        // course that fails on a tampered/altered application — stands, fail-closed. This is what
        // refuses a downloaded bundle whose documents were changed after it was signed.
        RenderCourses(result);
        if (!result.Verified)
        {
            RenderFailed(result);
            return ExitVerificationFailed;
        }
        RenderUnsigned(result);
        return ExitVerified;
    }

    // ── rendering ───────────────────────────────────────────────────────────

    private static readonly bool Color =
        Environment.GetEnvironmentVariable("NO_COLOR") is null && !Console.IsOutputRedirected;

    private static string Paint(string ansi, string text) =>
        Color ? $"\x1b[{ansi}m{text}\x1b[0m" : text;

    private static string Ok(string t) => Paint("32", t);      // green — per-course pass
    private static string Bad(string t) => Paint("31", t);     // red — failure
    private static string Gold(string t) => Paint("33", t);    // gold — the verdict, nothing else
    private static string Dim(string t) => Paint("90", t);

    private static void RenderCourses(ProjectVerification result)
    {
        Console.WriteLine();
        var index = 1;
        foreach (var course in result.Courses)
        {
            var glyph = course.Passed ? Ok("✓") : Bad("×");
            var label = $"course {index} · {course.Name}";
            Console.WriteLine($"  {glyph} {label,-26} {Dim(course.Detail)}");
            index++;
        }
        Console.WriteLine();
    }

    // VERIFIED, not CERTIFIED: with no operator key there is no signature to claim. The unsigned
    // note is load-bearing honesty — `ashlar keys init` upgrades this to CERTIFIED.
    private static void RenderUnsigned(ProjectVerification result)
    {
        Console.WriteLine(
            $"  {Gold("✓ VERIFIED")}  {Dim($"{result.Courses.Count} courses · unsigned — run `ashlar keys init` to certify")}");
        RenderScope(result);
    }

    // CERTIFIED means signed: a real Ed25519 signature over this verification is now the head of
    // the project's instance ledger.
    private static void RenderCertified(ProjectVerification result, string fingerprint, int seq)
    {
        Console.WriteLine(
            $"  {Gold("✓ CERTIFIED")}  {Dim($"{result.Courses.Count} courses · signed {fingerprint} · ledger #{seq}")}");
        RenderScope(result);
    }

    /// <summary>
    /// Names what the verdict covers, on every verdict. A verdict on its own invites the reader to
    /// supply their own scope, and the scope they supply is always "my application" — which is how
    /// CERTIFIED came to be printed, and a ledger entry signed, over a project holding no code at
    /// all. When there is nothing to certify, this line says so in the same breath as the verdict.
    /// </summary>
    private static void RenderScope(ProjectVerification result)
    {
        var scope = result.Scope;
        Console.WriteLine($"  {Dim("scope")}      {Dim(scope.Summary)}");
        if (!scope.CoversCode)
        {
            Console.WriteLine($"  {Dim("           add the code this project is meant to run, then verify again to certify it.")}");
        }
    }

    private static void RenderFailed(ProjectVerification result)
    {
        var failed = result.Courses.First(c => !c.Passed);
        Console.WriteLine($"  {Bad("× FAILED")}  {Dim($"course '{failed.Name}': {failed.Detail}")}");
        Console.WriteLine($"  {Dim("exit 65")}");
    }
}
