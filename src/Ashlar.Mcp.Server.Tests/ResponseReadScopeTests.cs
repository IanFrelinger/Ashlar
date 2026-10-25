using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Xunit;

#pragma warning disable ASHLAREXP002 // ResponseReadScope and EgressSubject verify fail-closed response lifecycle and isolation.

namespace Ashlar.Mcp.Server.Tests;

[Trait("Category", "Security")]
public sealed class ResponseReadScopeTests
{
    [Fact]
    public void Pending_reads_are_denied_even_when_they_report_public_before_completion()
    {
        using var response = new ResponseReadScope("response", SecurityLabel.Public, SecurityLabel.Public);
        using (var read = EgressSubject.BeginRead())
        {
            read.Report(SecurityLabel.Public);
            read.Complete();
            response.CanRead().Allowed.Should().BeFalse();
        }

        response.CanRead().Allowed.Should().BeTrue();
    }

    [Fact]
    public void An_unreported_completed_read_permanently_raises_the_response_to_SystemHigh()
    {
        using var response = new ResponseReadScope("response", SecurityLabel.Public, SecurityLabel.Public);
        using (var read = EgressSubject.BeginRead())
            read.Complete();

        response.CanRead().Allowed.Should().BeFalse();
    }

    [Fact]
    public void Disposal_propagates_observations_to_the_enclosing_subject()
    {
        var outer = new HighWaterMark(SecurityLabel.Public);
        using var subject = EgressSubject.Enter("outer", outer);
        using (var response = new ResponseReadScope("response", SecurityLabel.SystemHigh, SecurityLabel.Public))
        {
            EgressSubject.Observe(SecurityLabel.ParseOrSystemHigh("Secret"));
            response.CanRead().Allowed.Should().BeTrue();
        }

        outer.Current.Should().Be(SecurityLabel.ParseOrSystemHigh("Secret"));
    }

    [Fact(Timeout = 10000)]
    public async Task Concurrent_response_scopes_do_not_share_clearance_or_observed_labels()
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicReadFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var privateResponse = Task.Run(async () =>
        {
            using var response = new ResponseReadScope("private-response", SecurityLabel.Public, SecurityLabel.Public);
            EgressSubject.Observe(SecurityLabel.ParseOrSystemHigh("Secret"));
            observed.SetResult(true);
            await publicReadFinished.Task;
            return response.CanRead().Allowed;
        });
        var publicResponse = Task.Run(async () =>
        {
            await observed.Task;
            using var response = new ResponseReadScope("public-response", SecurityLabel.Public, SecurityLabel.Public);
            var allowed = response.CanRead().Allowed;
            publicReadFinished.SetResult(true);
            return allowed;
        });

        (await privateResponse).Should().BeFalse();
        (await publicResponse).Should().BeTrue();
    }
}
