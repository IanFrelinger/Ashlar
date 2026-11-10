using System.Text.Json;
using Ashlar.BackgroundAgents.Forge;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Forge;

/// <summary>Real on-disk store and recovery, with injected failures at persistence boundaries.</summary>
[Trait("Category", "ProdStyle")]
public sealed class ChangeProposalDurabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ashlar-proposal-durability-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    private ChangeProposalStore Open(Action<string>? fault = null) => new(_root, fault);
    private string Row(string bucket = "proposed", string id = "p") => Path.Combine(_root, bucket, id + ".json");
    private static ChangeProposal Proposal(string id = "p") => new()
        { Id = id, TargetPath = "src/X.cs", NewContent = "new content", Summary = "change" };

    [Theory]
    [InlineData("{ truncated")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Corruption_is_not_missing_or_an_empty_queue(string corrupt)
    {
        var store = Open();
        store.Add(Proposal());
        File.WriteAllText(Row(), corrupt);
        Action find = () => store.Find("p");
        Action list = () => store.List();
        Action approve = () => store.Approve("p");
        find.Should().Throw<InvalidDataException>();
        list.Should().Throw<InvalidDataException>();
        approve.Should().Throw<InvalidDataException>();
        File.ReadAllText(Row()).Should().Be(corrupt);
    }

    [Fact]
    public void Read_denial_is_surfaced_and_preserves_the_existing_row()
    {
        Open().Add(Proposal());
        var bytes = File.ReadAllBytes(Row());
        var store = Open(stage => { if (stage == "read") throw new UnauthorizedAccessException("injected denied read"); });
        Action find = () => store.Find("p");
        Action list = () => store.List();
        find.Should().Throw<UnauthorizedAccessException>();
        list.Should().Throw<UnauthorizedAccessException>();
        File.ReadAllBytes(Row()).Should().Equal(bytes);
    }

    [Theory]
    [InlineData("add.writing")]
    [InlineData("add.flushed")]
    public void Failed_add_does_not_publish_partial_json_or_misreport_collision(string stageToFail)
    {
        var store = Open(stage => { if (stage == stageToFail) throw new IOException("injected disk full"); });
        Action add = () => store.Add(Proposal());
        add.Should().Throw<IOException>().WithMessage("injected disk full");
        File.Exists(Row()).Should().BeFalse();
        Open().Find("p").Should().BeNull();
        Open().Add(Proposal()).Status.Should().Be(ChangeProposalStatus.Proposed);
    }

    [Theory]
    [InlineData("journal.writing")]
    [InlineData("journal.flushed")]
    public void Failure_before_commit_preserves_the_original_bytes_and_status(string stageToFail)
    {
        Open().Add(Proposal());
        var bytes = File.ReadAllBytes(Row());
        var store = Open(stage => { if (stage == stageToFail) throw new IOException("injected disk full"); });
        Action approve = () => store.Approve("p", "alice", "approved");
        approve.Should().Throw<IOException>();
        File.ReadAllBytes(Row()).Should().Equal(bytes);
        Open().Find("p")!.Status.Should().Be(ChangeProposalStatus.Proposed);
        Directory.EnumerateFiles(Path.Combine(_root, ".transactions"), "*.json").Should().BeEmpty();
    }

    [Theory]
    [InlineData("journal.published")]
    [InlineData("destination.writing")]
    [InlineData("destination.flushed")]
    [InlineData("destination.published")]
    [InlineData("transition.destination-published")]
    [InlineData("transition.source-deleted")]
    [InlineData("transition.journal-deleted")]
    public void Interrupted_committed_transition_recovers_one_complete_decision(string stageToFail)
    {
        var original = Open().Add(Proposal());
        var store = Open(stage => { if (stage == stageToFail) throw new IOException("simulated process interruption"); });
        Action approve = () => store.Approve("p", "alice", "approved");
        approve.Should().Throw<IOException>();

        var reopened = Open();
        var recovered = reopened.Find("p")!;
        recovered.Status.Should().Be(ChangeProposalStatus.Approved);
        recovered.ResolvedBy.Should().Be("alice");
        recovered.ResolutionNote.Should().Be("approved");
        recovered.NewContent.Should().Be(original.NewContent);
        recovered.CreatedAt.Should().Be(original.CreatedAt);
        reopened.List().Should().ContainSingle().Which.Should().Be(recovered);
        File.Exists(Row()).Should().BeFalse();
        File.Exists(Row("approved")).Should().BeTrue();
        Directory.EnumerateFiles(Path.Combine(_root, ".transactions"), "*.json").Should().BeEmpty();
        Open().Find("p").Should().Be(recovered, "recovery must be idempotent");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stale_transition_is_recoverable_from_both_nonterminal_states(bool approved)
    {
        Open().Add(Proposal());
        if (approved) Open().Approve("p", "alice");
        var store = Open(stage => { if (stage == "destination.published") throw new IOException("interruption"); });
        Action stale = () => store.MarkStale("p", "expired");
        stale.Should().Throw<IOException>();
        Open().List().Should().ContainSingle().Which.Status.Should().Be(ChangeProposalStatus.Stale);
        Open().Find("p")!.ResolutionNote.Should().Be("expired");
    }

    [Fact]
    public void Interrupted_add_publishes_a_complete_append_once_row()
    {
        var store = Open(stage => { if (stage == "add.published") throw new IOException("interruption"); });
        Action add = () => store.Add(Proposal());
        add.Should().Throw<IOException>();
        Open().Find("p")!.NewContent.Should().Be("new content");
        Action retry = () => Open().Add(Proposal());
        retry.Should().Throw<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public void Recovery_refuses_conflicting_destination_without_overwriting_anything()
    {
        Open().Add(Proposal());
        var store = Open(stage => { if (stage == "journal.published") throw new IOException("interruption"); });
        Action approve = () => store.Approve("p");
        approve.Should().Throw<IOException>();
        var conflicting = JsonSerializer.Serialize(Proposal() with { Status = ChangeProposalStatus.Approved, NewContent = "different" });
        File.WriteAllText(Row("approved"), conflicting);
        var original = File.ReadAllText(Row());
        Action recover = () => Open().Find("p");
        recover.Should().Throw<InvalidDataException>().WithMessage("*Conflicting destination*");
        File.ReadAllText(Row("approved")).Should().Be(conflicting);
        File.ReadAllText(Row()).Should().Be(original);
        Directory.EnumerateFiles(Path.Combine(_root, ".transactions"), "*.json").Should().ContainSingle();
    }

    [Fact]
    public void Corrupt_journal_blocks_reads_instead_of_hiding_an_uncertain_decision()
    {
        Open().Add(Proposal());
        File.WriteAllText(Path.Combine(_root, ".transactions", "p.json"), "{");
        Action list = () => Open().List();
        list.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Original\":null,\"Updated\":null}")]
    [InlineData("{\"Original\":{}}")]
    public void Malformed_journal_preserves_all_existing_bytes(string malformed)
    {
        Open().Add(Proposal());
        var bytes = File.ReadAllBytes(Row());
        var journal = Path.Combine(_root, ".transactions", "p.json");
        File.WriteAllText(journal, malformed);
        Action read = () => Open().Find("p");
        read.Should().Throw<InvalidDataException>();
        File.ReadAllBytes(Row()).Should().Equal(bytes);
        File.ReadAllText(journal).Should().Be(malformed);
    }

    [Theory]
    [InlineData("invalid-edge")]
    [InlineData("modified-content")]
    [InlineData("conflicting-source")]
    [InlineData("third-bucket")]
    [InlineData("both-missing")]
    public void Invalid_or_conflicting_transition_never_repairs_by_overwriting(string attack)
    {
        var original = Open().Add(Proposal());
        var updated = original with { Status = ChangeProposalStatus.Approved, ResolvedBy = "alice" };
        if (attack == "invalid-edge") updated = updated with { Status = ChangeProposalStatus.Applied };
        if (attack == "modified-content") updated = updated with { NewContent = "tampered" };
        if (attack == "conflicting-source") File.WriteAllText(Row(), JsonSerializer.Serialize(original with { Summary = "different" }));
        if (attack == "third-bucket") File.WriteAllText(Row("rejected"), JsonSerializer.Serialize(original with { Status = ChangeProposalStatus.Rejected }));
        if (attack == "both-missing") File.Delete(Row());
        File.WriteAllText(Path.Combine(_root, ".transactions", "p.json"),
            JsonSerializer.Serialize(new { Original = original, Updated = updated }));
        var before = Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllText);
        Action read = () => Open().List();
        read.Should().Throw<InvalidDataException>();
        var after = Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllText);
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public void Duplicate_or_misfiled_rows_are_corruption()
    {
        var proposal = Open().Add(Proposal());
        File.WriteAllText(Row("approved"), JsonSerializer.Serialize(proposal with { Status = ChangeProposalStatus.Approved }));
        Action find = () => Open().Find("p");
        Action list = () => Open().List(ChangeProposalStatus.Proposed);
        find.Should().Throw<InvalidDataException>().WithMessage("*Duplicate*");
        list.Should().Throw<InvalidDataException>().WithMessage("*Duplicate*");
        File.Delete(Row("approved"));
        File.WriteAllText(Row(), JsonSerializer.Serialize(proposal with { Status = ChangeProposalStatus.Applied }));
        find.Should().Throw<InvalidDataException>().WithMessage("*identity/status*");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/tmp/escape")]
    [InlineData("a\\b")]
    [InlineData("CON")]
    public void Identifier_cannot_escape_the_store_or_name_a_device(string id)
    {
        Action add = () => Open().Add(Proposal(id));
        Action find = () => Open().Find(id);
        add.Should().Throw<ArgumentException>();
        find.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Case_distinct_ids_are_consistent_with_filesystem_name_equivalence()
    {
        var store = Open();
        store.Add(Proposal("lower"));
        // Probe the actual filesystem, not the operating-system name (a mount can differ).
        if (File.Exists(Row(id: "LOWER")))
        {
            Action alias = () => store.Add(Proposal("LOWER"));
            alias.Should().Throw<InvalidOperationException>();
            store.List().Should().ContainSingle();
        }
        else
        {
            store.Add(Proposal("LOWER"));
            store.Approve("lower");
            store.Find("LOWER")!.Status.Should().Be(ChangeProposalStatus.Proposed);
            store.List().Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task Separate_store_instances_serialize_competing_decisions_and_additions()
    {
        Open().Add(Proposal());
        var successes = 0;
        var refusals = 0;
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            try
            {
                if (i % 2 == 0) Open().Approve("p", "alice"); else Open().Reject("p", "bob");
                Interlocked.Increment(ref successes);
            }
            catch (InvalidOperationException) { Interlocked.Increment(ref refusals); }
        })));
        successes.Should().Be(1);
        refusals.Should().Be(11);
        Open().List().Should().ContainSingle();
        successes = 0;
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            try { Open().Add(Proposal("racing")); Interlocked.Increment(ref successes); }
            catch (InvalidOperationException) { }
        })));
        successes.Should().Be(1);
    }
}
