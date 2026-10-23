using System.Text.Json;
using Ashlar.BackgroundAgents.Forge;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Forge;

public sealed class ChangeProposalDurabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ashlar-proposal-durability-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string PathFor(string status) => Path.Combine(_root, status, "p.json");
    private static ChangeProposal Proposal() => new() { Id = "p", TargetPath = "src/X.cs", NewContent = "new", Summary = "proposal" };

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    public void Live_corrupt_record_fails_loudly_in_find_and_list(string contents)
    {
        var store = new ChangeProposalStore(_root);
        File.WriteAllText(PathFor("proposed"), contents);
        Action find = () => store.Find("p");
        Action list = () => store.List();
        find.Should().Throw<InvalidDataException>().WithMessage("*p.json*");
        list.Should().Throw<InvalidDataException>().WithMessage("*p.json*");
        File.ReadAllText(PathFor("proposed")).Should().Be(contents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Destination_collision_does_not_rewrite_source_or_clobber_destination(bool stale)
    {
        var store = new ChangeProposalStore(_root);
        store.Add(Proposal());
        var before = File.ReadAllText(PathFor("proposed"));
        var destination = PathFor(stale ? "stale" : "approved");
        File.WriteAllText(destination, "owned by another record");
        Action transition = () => { if (stale) store.MarkStale("p"); else store.Approve("p"); };
        transition.Should().Throw<IOException>();
        File.ReadAllText(PathFor("proposed")).Should().Be(before);
        File.ReadAllText(destination).Should().Be("owned by another record");
    }

    [Fact]
    public async Task Concurrent_add_publishes_exactly_one_complete_record()
    {
        var stores = Enumerable.Range(0, 8).Select(_ => new ChangeProposalStore(_root)).ToArray();
        using var start = new ManualResetEventSlim();
        var attempts = stores.Select(store => Task.Run(() =>
        {
            start.Wait();
            try { store.Add(Proposal()); return true; }
            catch (InvalidOperationException) { return false; }
        })).ToArray();
        start.Set();
        var outcomes = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));
        outcomes.Count(x => x).Should().Be(1);
        stores[0].List().Should().ContainSingle().Which.NewContent.Should().Be("new");
    }
}
