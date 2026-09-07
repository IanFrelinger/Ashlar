using FluentAssertions;
using Ashlar.CLI.Commands;
using Ashlar.Manifest.Ledger;
using Ashlar.Manifest.Signing;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// The kernel's two LOSSY ledger refusals — a chain shorter than its anchor, and a ledger whose
/// entries are gone from under a live anchor — deliberately cannot be cleared by
/// <c>ashlar verify</c>, because clearing them there would let a fresh valid-looking head be written
/// over a history someone deleted. The deliberate acceptance of that loss lives in
/// <c>InstanceLedger.ReanchorAsync</c>, and until <c>ashlar ledger reanchor</c> existed the refusal
/// could only name a C# method. An operator standing at a terminal cannot run a method: a refusal
/// naming an unrunnable fix is the defect this whole pass exists to remove, so these pin that the
/// command the refusal names is a command that runs, and that it does what the message promises.
/// </summary>
public sealed class LedgerReanchorCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _keyDir;
    private readonly string _stateRoot;
    private readonly SigningIdentity _signer;

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public LedgerReanchorCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ashlar-ledgercmd-" + Guid.NewGuid().ToString("N"));
        _keyDir = Path.Combine(_root, "keys");
        _stateRoot = Path.Combine(_root, "project", ".ashlar");
        Directory.CreateDirectory(_stateRoot);
        _signer = OperatorKey.Generate(_keyDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DirectoryInfo Project => new(Path.Combine(_root, "project"));

    private InstanceLedger Ledger() => new(_stateRoot);

    private async Task AppendAsync(int count)
    {
        var ledger = Ledger();
        for (var i = 0; i < count; i++)
        {
            await ledger.AppendVerificationAsync(
                _signer, $"subject-{i}", verified: true, [], Now.AddMinutes(i));
        }
    }

    /// <summary>Deletes the newest entry — what truncation leaves behind, and what the anchor detects.</summary>
    private void TruncateNewestEntry()
    {
        var dir = Path.Combine(_stateRoot, "ledger");
        var newest = Directory.EnumerateFiles(dir, "*.json")
            .Where(f => !f.EndsWith(".json.tmp", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Last();
        File.Delete(newest);
    }

    [Fact]
    public async Task The_refusal_names_a_command_and_that_command_exists()
    {
        await AppendAsync(3);
        TruncateNewestEntry();

        var refusal = Assert.Throws<InvalidOperationException>(() => Ledger().VerifyChain());

        refusal.Message.Should().Contain("ashlar ledger reanchor",
            "a refusal has to name a fix the person reading it can type, not only a kernel identifier");
        refusal.Message.Should().Contain("ReanchorAsync",
            "the API name stays for anyone driving the kernel directly");
        refusal.Message.Should().Contain("restore",
            "restoring is still the repair that KEEPS the history, and is still named first");
    }

    [Fact]
    public async Task Status_prints_the_refusal_verbatim_and_exits_65()
    {
        await AppendAsync(3);
        TruncateNewestEntry();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = LedgerCommand.Status(Project, stdout, stderr);

        exit.Should().Be(65);
        stderr.ToString().Should().Contain("ashlar ledger reanchor");
    }

    [Fact]
    public async Task Status_on_an_intact_ledger_is_clean()
    {
        await AppendAsync(2);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = LedgerCommand.Status(Project, stdout, stderr);

        exit.Should().Be(0, stderr.ToString());
        stdout.ToString().Should().Contain("2 signed entries");
    }

    [Fact]
    public void Status_on_a_project_that_was_never_certified_says_so_rather_than_refusing()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = LedgerCommand.Status(Project, stdout, stderr);

        exit.Should().Be(0, "absence is not corruption");
        stdout.ToString().Should().Contain("never been certified");
    }

    [Fact]
    public async Task Reanchor_without_yes_shows_what_would_be_accepted_and_changes_nothing()
    {
        await AppendAsync(3);
        TruncateNewestEntry();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await LedgerCommand.ReanchorAsync(Project, _keyDir, yes: false, CancellationToken.None, stdout, stderr);

        exit.Should().Be(1);
        var text = stderr.ToString();
        text.Should().Contain("--yes");
        text.Should().Contain("does NOT recover anything",
            "the operator has to understand that this accepts the loss rather than undoing it");
        text.Should().Contain("restore .ashlar/ledger");
        Assert.Throws<InvalidOperationException>(() => Ledger().VerifyChain());
    }

    [Fact]
    public async Task Reanchor_with_yes_accepts_the_shortened_history_and_the_ledger_then_verifies()
    {
        await AppendAsync(3);
        TruncateNewestEntry();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await LedgerCommand.ReanchorAsync(Project, _keyDir, yes: true, CancellationToken.None, stdout, stderr);

        exit.Should().Be(0, stderr.ToString());
        stdout.ToString().Should().Contain("what was accepted",
            "a re-anchor that does not record what it swallowed is the erasure it exists to make deliberate");
        // The named fix must actually fix it — that is the whole point of naming it.
        var chain = Ledger().VerifyChain();
        chain.Count.Should().Be(2);
    }

    /// <summary>Deletes every entry, leaving the anchor alive — "the history was deleted".</summary>
    private void DeleteAllEntries()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_stateRoot, "ledger"), "*.json"))
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task The_entries_gone_refusal_names_a_command_that_can_actually_run()
    {
        // The defect this pass removes, reintroduced one state over. RestoreOnlyFix is attached to
        // TWO states; for this one the named command refused an empty ledger before doing anything,
        // so status refused 65, the fix it named refused 65, verify refused 65, and the only escape
        // was deleting ledger.head.json — the act the refusal exists to detect.
        await AppendAsync(3);
        DeleteAllEntries();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        LedgerCommand.Status(Project, stdout, stderr).Should().Be(65);
        stderr.ToString().Should().Contain("ashlar ledger reanchor");

        var exit = await LedgerCommand.ReanchorAsync(Project, _keyDir, yes: true, CancellationToken.None, stdout, stderr);

        exit.Should().Be(0, "a refusal whose only working fix is the act it warns against is a dead end, not a gate");
        LedgerCommand.Status(Project, new StringWriter(), new StringWriter()).Should().Be(0);
    }

    [Fact]
    public async Task Accepting_a_destroyed_history_records_it_rather_than_erasing_it()
    {
        // The reason deleting ledger.head.json was never an acceptable fix: it leaves a project
        // that reads as one which was simply never certified. Accepting the loss has to leave
        // evidence that there was something to lose.
        await AppendAsync(3);
        var destroyedHead = Ledger().VerifyChain().Head!;
        DeleteAllEntries();

        await LedgerCommand.ReanchorAsync(
            Project, _keyDir, yes: true, CancellationToken.None, new StringWriter(), new StringWriter());

        var chain = Ledger().VerifyChain();
        chain.Count.Should().Be(1);
        chain.Head!.Verified.Should().BeFalse("nothing was verified — a loss was accepted");
        chain.Head.Courses.Should().ContainSingle(c => c.Name == "ledger-anchor" && !c.Passed)
            .Which.Detail.Should().Contain("the whole history had been deleted")
            .And.Contain(destroyedHead.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "the record has to say how much history there was");
        chain.Head.Signer.Should().Be(_signer.PublicKeyBase64, "accepting the loss is a SIGNED act");
    }

    [Fact]
    public async Task An_ordinary_verify_still_cannot_bury_a_destroyed_history()
    {
        // Making the recovery verb work must not make the loss clearable as a side effect of
        // verifying — that is the whole reason it is a separate verb.
        await AppendAsync(3);
        DeleteAllEntries();

        var append = async () => await Ledger().AppendVerificationAsync(
            _signer, "subject-new", verified: true, [], Now.AddHours(1));

        (await append.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Refusing to start a fresh history on top of one that was destroyed*");
    }

    [Fact]
    public async Task Reanchor_on_a_project_with_neither_entries_nor_anchor_is_still_refused()
    {
        // "Never certified" is not a loss to accept, and there is nothing for an anchor to pin.
        await AppendAsync(1);
        DeleteAllEntries();
        File.Delete(Path.Combine(_stateRoot, "ledger.head.json"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await LedgerCommand.ReanchorAsync(Project, _keyDir, yes: true, CancellationToken.None, stdout, stderr);

        exit.Should().Be(1);
        stderr.ToString().Should().Contain("nothing to re-anchor");
    }

    [Fact]
    public async Task Reanchor_refuses_a_forged_entry_even_with_yes()
    {
        await AppendAsync(2);
        var dir = Path.Combine(_stateRoot, "ledger");
        var first = Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).First();
        File.WriteAllText(first, File.ReadAllText(first).Replace("subject-0", "subject-x", StringComparison.Ordinal));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await LedgerCommand.ReanchorAsync(Project, _keyDir, yes: true, CancellationToken.None, stdout, stderr);

        exit.Should().Be(65, "a re-anchor accepts a SHORTER history, never a forged one");
        stderr.ToString().Should().Contain("re-anchor refused");
    }

    [Fact]
    public async Task Reanchor_on_an_intact_ledger_does_nothing_and_says_why()
    {
        await AppendAsync(2);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await LedgerCommand.ReanchorAsync(Project, _keyDir, yes: true, CancellationToken.None, stdout, stderr);

        exit.Should().Be(0);
        stdout.ToString().Should().Contain("nothing to re-anchor");
    }

    [Fact]
    public async Task Reanchor_with_no_operator_key_refuses_and_names_keys_init()
    {
        await AppendAsync(3);
        TruncateNewestEntry();
        var empty = Path.Combine(_root, "no-keys");
        Directory.CreateDirectory(empty);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await LedgerCommand.ReanchorAsync(Project, empty, yes: true, CancellationToken.None, stdout, stderr);

        exit.Should().Be(1);
        stderr.ToString().Should().Contain("ashlar keys init",
            "a signed act with no key is refused, and the refusal names how to get one");
    }

    [Fact]
    public async Task The_no_key_refusal_names_the_directory_it_actually_searched()
    {
        // It named OperatorKey.ResolveKeyDir() — the env/default directory — whatever --key-dir
        // said. So an operator who passed --key-dir was told to look somewhere the command had not
        // looked, which is a refusal describing a search that never happened.
        await AppendAsync(3);
        TruncateNewestEntry();
        var empty = Path.Combine(_root, "no-keys-named");
        Directory.CreateDirectory(empty);
        var stderr = new StringWriter();

        await LedgerCommand.ReanchorAsync(Project, empty, yes: true, CancellationToken.None, new StringWriter(), stderr);

        stderr.ToString().Should().Contain(Path.GetFullPath(empty),
            "the directory in the message must be the directory that was searched");
        stderr.ToString().Should().NotContain(OperatorKey.ResolveKeyDir(),
            "naming the default directory when --key-dir was given sends the operator to the wrong place");
    }

    [Fact]
    public async Task The_no_key_refusal_names_a_keys_init_that_actually_clears_it()
    {
        // The defect this pass exists to remove, in its purest form: the refusal said "run
        // `ashlar keys init`, then run this command again". With --key-dir given, bare
        // `keys init` writes to the DEFAULT directory, which this command does not search — so
        // following the instruction exactly and re-running returned the byte-identical refusal.
        // A fix that loops is not a fix. So the command it names must carry the directory, and
        // running THAT must clear the refusal.
        await AppendAsync(3);
        TruncateNewestEntry();
        var empty = Path.Combine(_root, "no-keys-runnable");
        Directory.CreateDirectory(empty);
        var stderr = new StringWriter();

        var refused = await LedgerCommand.ReanchorAsync(
            Project, empty, yes: true, CancellationToken.None, new StringWriter(), stderr);
        refused.Should().Be(1);

        var named = stderr.ToString();
        named.Should().Contain($"ashlar keys init --key-dir \"{Path.GetFullPath(empty)}\"",
            "the named command has to put a key where this command will look for it");

        // Run exactly what it named, then exactly what it said to do next.
        OperatorKey.Generate(Path.GetFullPath(empty));
        var exit = await LedgerCommand.ReanchorAsync(
            Project, empty, yes: true, CancellationToken.None, new StringWriter(), new StringWriter());

        exit.Should().Be(0, "the fix a refusal names must be one that clears the refusal");
    }

    [Fact]
    public async Task Accepting_a_destroyed_history_never_calls_the_marker_a_survivor()
    {
        // The success line read "{Count} surviving entr(y|ies)" for both outcomes. When every
        // entry was gone the count is 1 — the destruction marker the re-anchor had just written —
        // so `rm -rf .ashlar/ledger` followed by the fix printed "1 surviving entry" to an
        // operator whose history was entirely destroyed. The number was right and the sentence was
        // a lie, which is the worst shape a refusal-adjacent message can take.
        await AppendAsync(3);
        DeleteAllEntries();
        var stdout = new StringWriter();

        var exit = await LedgerCommand.ReanchorAsync(
            Project, _keyDir, yes: true, CancellationToken.None, stdout, new StringWriter());

        exit.Should().Be(0);
        var text = stdout.ToString();
        text.Should().NotContain("surviving entry",
            "nothing survived; the one entry on disk is the record of the loss, not a piece of the history");
        text.Should().Contain("NOTHING survived");
        text.Should().Contain("nothing was recovered");
    }

    [Fact]
    public async Task Accepting_a_truncation_still_reports_the_entries_that_really_did_survive()
    {
        // The control case for the message above: where entries genuinely survive, the count is a
        // count of survivors and must still be reported as one.
        await AppendAsync(3);
        TruncateNewestEntry();
        var stdout = new StringWriter();

        var exit = await LedgerCommand.ReanchorAsync(
            Project, _keyDir, yes: true, CancellationToken.None, stdout, new StringWriter());

        exit.Should().Be(0);
        stdout.ToString().Should().Contain("2 surviving entries");
        stdout.ToString().Should().NotContain("NOTHING survived");
    }
}
