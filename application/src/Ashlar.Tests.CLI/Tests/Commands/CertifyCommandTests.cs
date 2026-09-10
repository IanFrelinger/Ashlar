using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using Ashlar.Certification.Contracts;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// <c>ashlar certify brick</c> — the certification gate reached from an installed CLI instead of
/// from a clone of this repository.
///
/// <para>These run through the REAL root command, not the handler, because three of the facts under
/// test only exist there: <c>--format-json</c> is registered as a global option on the root, the
/// verb has to be reachable as <c>certify brick</c>, and the exit code has to survive
/// System.CommandLine's own parse handling. The candidate is the adversarial corpus' honest brick
/// (<c>tests/adversarial-corpus/fixtures/a8-gate-emitted-hash-bind</c>) — the one fixture the corpus
/// already pins as "admit", so an ADMIT that stops being an ADMIT is a real regression rather than a
/// fixture that rotted.</para>
/// </summary>
[Trait("Category", "CLI")]
public sealed class CertifyCommandTests : IDisposable
{
    private readonly string _dir;

    public CertifyCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ashlar-certify-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Runs through the REAL root command. Copied from PolicyCommandTests: the console writers are
    /// restored to <see cref="ConsoleCapture"/>'s, never to the (possibly foreign) inherited ones.
    /// </summary>
    private static async Task<(int rc, string stdout, string stderr)> RunCliAsync(params string[] args)
    {
        var so = new StringWriter();  // not disposed: a disposed writer left on Console poisons later tests
        var se = new StringWriter();
        Console.SetOut(so);
        Console.SetError(se);
        try
        {
            var rc = await Ashlar.CLI.Program.BuildRootCommand().InvokeAsync(args).ConfigureAwait(false);
            return (rc, so.ToString(), se.ToString());
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }
    }

    private string CopyHonestBrick()
    {
        var source = Path.Combine(CorpusRoot(), "fixtures", "a8-gate-emitted-hash-bind", "project");
        var dest = Path.Combine(_dir, "brick");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        return dest;
    }

    private static string CorpusRoot()
    {
        // Anchored on the test assembly, not Environment.CurrentDirectory, which other suites in
        // this assembly move.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "adversarial-corpus");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("tests/adversarial-corpus not found");
    }

    private static JsonElement ReadRecord(string path)
    {
        File.Exists(path).Should().BeTrue($"the run must leave a record at {path}");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    /// <summary>
    /// Sets a minting key for the duration of a test. Admission verifies
    /// <see cref="CertificationVerifyOptions.Strict"/>, which REQUIRES an Ed25519 signature — so
    /// with no operator key configured the gate certifies and the registry then refuses, and the
    /// run is a REJECT. That is the certifier's fail-closed posture, not a test artefact:
    /// <c>tools/Ashlar.CertifyBrick</c> behaves the same way, which is why the gate script is run
    /// with a key in the environment. A raw Ed25519 private key is any 32 bytes.
    /// </summary>
    private static IDisposable OperatorKey() =>
        new EnvironmentVariableScope(
            CertificationRecordEd25519.PrivateKeyEnvVar,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    // ---- the verdicts ---------------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task Certify_admitsTheHonestBrick_andExitsZero()
    {
        using var key = OperatorKey();
        var brickDir = CopyHonestBrick();
        var recordPath = Path.Combine(_dir, "certification-record.json");

        var (rc, stdout, stderr) = await RunCliAsync(
            "certify", "brick", brickDir,
            "--witness", Path.Combine(brickDir, "witness.json"),
            "--record", recordPath);

        rc.Should().Be(0, stdout + Environment.NewLine + stderr);
        stdout.Should().Contain("ADMIT brick=honest").And.Contain($"Record: {recordPath}");

        var record = ReadRecord(recordPath);
        record.GetProperty("admitted").GetBoolean().Should().BeTrue();
        record.GetProperty("signed").GetBoolean().Should().BeTrue();
        record.GetProperty("status").GetString().Should().Be("PASS");

        // The registry's own copy, keyed by brick id — this is what a consumer's runtime looks up,
        // and it is written by the same run rather than by a second command.
        File.Exists(Path.Combine(_dir, "honest.json")).Should().BeTrue();
        // A8: the certifier ships the binary it judged, so the consumer never recompiles author source.
        File.Exists(Path.Combine(_dir, "gate-emitted-brick.dll")).Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task Certify_refusesWhenTheWitnessDisagrees_andStillEmitsTheJudgedBinary()
    {
        var brickDir = CopyHonestBrick();
        // The honest brick adds one. Claim it adds two: a correctness refusal, not a load refusal.
        var witnessPath = Path.Combine(_dir, "wrong.witness.json");
        await File.WriteAllTextAsync(witnessPath, """
            {"brickId":"honest","cases":[{"input":{"n":3},"expectedOutput":{"n":5,"$summary":"ok"}}]}
            """);
        var recordPath = Path.Combine(_dir, "certification-record.json");

        var (rc, stdout, stderr) = await RunCliAsync(
            "certify", "brick", brickDir, "--witness", witnessPath, "--record", recordPath);

        rc.Should().Be(1, stdout + Environment.NewLine + stderr);
        // Naming the stage IS the assertion. This test runs without an operator key, so an honest
        // brick with a CORRECT witness refuses too — same exit code, same FAIL record, same emitted
        // binary, only at admission instead. A bare "REJECT (" matches that just as well, so a gate
        // that had stopped judging the witness at all would leave this test green.
        stderr.Should().Contain("REJECT (correctness)").And.Contain($"Record: {recordPath}");
        stdout.Should().BeEmpty("a refusal is not a result — nothing goes to stdout");

        var record = ReadRecord(recordPath);
        record.GetProperty("admitted").GetBoolean().Should().BeFalse();
        record.GetProperty("status").GetString().Should().Be("FAIL");

        // The emitted assembly is written on a REJECT too: it is the evidence behind the verdict.
        // Moving that write inside an `if (admitted)` reads as a cleanup and is a behaviour change.
        File.Exists(Path.Combine(_dir, "gate-emitted-brick.dll")).Should().BeTrue();
    }

    // ---- usage ----------------------------------------------------------------------------

    [Fact(Timeout = 15000)]
    public async Task Certify_withoutWitness_exitsTwo_andWritesNothing()
    {
        var brickDir = CopyHonestBrick();

        var (rc, _, stderr) = await RunCliAsync("certify", "brick", brickDir);

        // 2 is usage, exactly as in tools/Ashlar.CertifyBrick. It must not collide with 1, which
        // means "this brick was judged and refused".
        rc.Should().Be(2);
        stderr.Should().Contain("--witness");
        File.Exists(Path.Combine(_dir, "certification-record.json"))
            .Should().BeFalse("nothing ran, so there is no verdict to record");
    }

    [Fact(Timeout = 15000)]
    public async Task Certify_withNoArguments_exitsTwo()
    {
        var (rc, _, stderr) = await RunCliAsync("certify", "brick");

        rc.Should().Be(2);
        stderr.Should().Contain("Usage: ashlar certify brick");
    }

    [Fact(Timeout = 15000)]
    public async Task Certify_withNoSubcommand_exitsTwo()
    {
        var (rc, _, stderr) = await RunCliAsync("certify");

        // Left to System.CommandLine a missing subcommand is exit 1 — the REFUSE code, which for
        // this verb promises a signed record that nothing here ever wrote. The bare verb carries its
        // own handler so a half-typed command cannot be read as a verdict on a brick.
        rc.Should().Be(2);
        stderr.Should().Contain("Usage: ashlar certify brick");
    }

    // ---- the refusal is evidence ------------------------------------------------------------

    [Fact(Timeout = 15000)]
    public async Task Certify_whenTheLoaderRefuses_writesASignedFailRecord()
    {
        // No .csproj and no source: the loader throws before the gate ever runs. The old behaviour
        // was to print the exception and leave no file — and "no record" is what Get() returns for a
        // brick that was never certified at all, so the refusal was invisible. It must be evidence.
        var brickDir = Path.Combine(_dir, "brick");
        Directory.CreateDirectory(brickDir);
        var witnessPath = Path.Combine(_dir, "witness.json");
        await File.WriteAllTextAsync(witnessPath, """{"brickId":"honest","cases":[]}""");
        var recordPath = Path.Combine(_dir, "certification-record.json");

        var (rc, _, stderr) = await RunCliAsync(
            "certify", "brick", brickDir, "--witness", witnessPath, "--record", recordPath);

        rc.Should().Be(1);
        stderr.Should().Contain("REJECT (load):");

        var record = ReadRecord(recordPath);
        record.GetProperty("status").GetString().Should().Be("FAIL");
        record.GetProperty("stage").GetString().Should().Be("load");
        record.GetProperty("admitted").GetBoolean().Should().BeFalse();
        record.GetProperty("signed").GetBoolean().Should().BeTrue("an unsigned refusal is not evidence");
        // The brick id comes from the witness, so the refusal lands under the id a later lookup uses.
        record.GetProperty("brickId").GetString().Should().Be("honest");
    }

    [Fact(Timeout = 15000)]
    public async Task Certify_withoutRecordOption_writesBesideTheBrickProject()
    {
        // The default is Path.Combine(brickDir, "..", "certification-record.json") — un-normalized,
        // so the echoed path carries a literal "..". That string is what every existing caller of
        // tools/Ashlar.CertifyBrick already parses; tidying it here would break them silently.
        var brickDir = Path.Combine(_dir, "brick");
        Directory.CreateDirectory(brickDir);
        var witnessPath = Path.Combine(_dir, "witness.json");
        await File.WriteAllTextAsync(witnessPath, """{"brickId":"honest","cases":[]}""");

        var (rc, _, stderr) = await RunCliAsync("certify", "brick", brickDir, "--witness", witnessPath);

        rc.Should().Be(1);
        stderr.Should().Contain(Path.Combine(brickDir, "..", "certification-record.json"));
        File.Exists(Path.Combine(_dir, "certification-record.json")).Should().BeTrue();
    }

    // ---- --format-json ----------------------------------------------------------------------

    [Fact(Timeout = 15000)]
    public async Task Certify_withFormatJson_putsExactlyOneJsonDocumentOnStdout()
    {
        var brickDir = Path.Combine(_dir, "brick");
        Directory.CreateDirectory(brickDir);
        var witnessPath = Path.Combine(_dir, "witness.json");
        await File.WriteAllTextAsync(witnessPath, """{"brickId":"honest","cases":[]}""");
        var recordPath = Path.Combine(_dir, "certification-record.json");

        var (rc, stdout, _) = await RunCliAsync(
            "certify", "brick", brickDir, "--witness", witnessPath, "--record", recordPath, "--format-json");

        rc.Should().Be(1, "a refusal is still a refusal when it is rendered as JSON");
        stdout.Should().NotContain("REJECT", "prose must not reach a caller that asked for JSON");

        // Parsing the WHOLE of stdout is the assertion: a single stray prose line would make this throw.
        var payload = JsonDocument.Parse(stdout).RootElement;
        payload.GetProperty("brickId").GetString().Should().Be("honest");
        payload.GetProperty("admitted").GetBoolean().Should().BeFalse();
        payload.GetProperty("outcome").GetString().Should().Be("load-refused");
        payload.GetProperty("failureCheck").GetString().Should().Be("load");
        payload.GetProperty("recordPath").GetString().Should().Be(recordPath);
        payload.GetProperty("killedMutants").GetInt32().Should().Be(0, "the brief pins a count, not the id list");
    }

    [Fact(Timeout = 60_000)]
    public async Task Certify_withFormatJson_carriesTheMutationNumbersOnAnAdmit()
    {
        // The refusal above pins every mutation field in its EMPTY state — no escape rate, no
        // mutants, no artifact — so a rendering that dropped one of them outright would still
        // satisfy it. Only an admitted brick has numbers to lose.
        using var key = OperatorKey();
        var brickDir = CopyHonestBrick();
        var recordPath = Path.Combine(_dir, "certification-record.json");

        var (rc, stdout, stderr) = await RunCliAsync(
            "certify", "brick", brickDir,
            "--witness", Path.Combine(brickDir, "witness.json"),
            "--record", recordPath, "--format-json");

        rc.Should().Be(0, stdout + Environment.NewLine + stderr);

        var payload = JsonDocument.Parse(stdout).RootElement;
        payload.GetProperty("admitted").GetBoolean().Should().BeTrue();
        payload.GetProperty("outcome").GetString().Should().Be("admitted");
        payload.GetProperty("escapeRate").ValueKind
            .Should().Be(JsonValueKind.Number, "a measured escape rate of zero is not the absence of one");
        payload.GetProperty("escapeRate").GetDouble().Should().Be(0);
        var total = payload.GetProperty("totalMutants").GetInt32();
        total.Should().BeGreaterThan(0, "an admit with nothing mutated would mean the suite never ran");
        payload.GetProperty("killedMutants").GetInt32()
            .Should().Be(total, "zero escapes means every mutant died — and this is the count, not the id list");
        payload.GetProperty("artifactPath").GetString()
            .Should().NotBeNullOrEmpty("the consumer loads the judged binary, so its path is part of the verdict");
    }

    [Fact(Timeout = 15000)]
    public async Task Certify_withoutFormatJson_stillPrintsTheProse()
    {
        // The guard rail against over-refusing: the JSON rendering must not swallow the human one.
        var brickDir = Path.Combine(_dir, "brick");
        Directory.CreateDirectory(brickDir);
        var witnessPath = Path.Combine(_dir, "witness.json");
        await File.WriteAllTextAsync(witnessPath, """{"brickId":"honest","cases":[]}""");

        var (rc, stdout, stderr) = await RunCliAsync(
            "certify", "brick", brickDir, "--witness", witnessPath,
            "--record", Path.Combine(_dir, "certification-record.json"));

        rc.Should().Be(1);
        stderr.Should().Contain("REJECT (load):");
        stdout.Should().BeEmpty();
    }
}
