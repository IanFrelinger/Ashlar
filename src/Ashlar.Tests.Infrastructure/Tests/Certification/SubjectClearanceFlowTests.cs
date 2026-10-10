using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
public sealed class SubjectClearanceFlowTests
{
    private static SubjectClearanceAuthority Authority() => new(new Dictionary<string, string?>
        { ["agent:reader"] = "Secret", ["agent:operator"] = "SystemHigh" });

    [Fact]
    public void A_high_water_mark_is_not_a_clearance_grant()
    {
        var authority = Authority();
        authority.ResolveCurrent().Should().Be(SecurityLabel.Public);
        using var unknown = EgressSubject.Enter("unknown", new HighWaterMark(SecurityLabel.SystemHigh));
        authority.ResolveCurrent().Should().Be(SecurityLabel.Public);
    }

    [Fact]
    public void A_nested_subject_cannot_widen_an_enclosing_grant()
    {
        var authority = Authority();
        using var parent = EgressSubject.Enter("agent:reader", new HighWaterMark());
        authority.ResolveCurrent().ToString().Should().Be("Secret");
        using (EgressSubject.Enter("agent:operator", new HighWaterMark()))
            authority.ResolveCurrent().ToString().Should().Be("Secret");
        using (EgressSubject.Enter("unknown", new HighWaterMark()))
            authority.ResolveCurrent().Should().Be(SecurityLabel.Public);
        authority.ResolveCurrent().ToString().Should().Be("Secret");
    }

    [Fact]
    public void Read_scopes_and_observations_do_not_change_the_subject_grant()
    {
        var authority = Authority();
        using var subject = EgressSubject.Enter("agent:reader", new HighWaterMark());
        using (EgressSubject.BeginRead())
        {
            EgressSubject.Observe(SecurityLabel.SystemHigh);
            authority.ResolveCurrent().ToString().Should().Be("Secret");
        }
        authority.ResolveCurrent().ToString().Should().Be("Secret");
    }

    [Fact]
    public void Exact_identity_is_not_the_truncated_diagnostic_spelling()
    {
        var prefix = new string('x', 2048);
        var authority = new SubjectClearanceAuthority(new Dictionary<string, string?>
            { [prefix + "reader"] = "Secret", [prefix + "operator"] = "SystemHigh" });
        using (EgressSubject.Enter(prefix + "reader", new HighWaterMark()))
            authority.ResolveCurrent().ToString().Should().Be("Secret");
        using (EgressSubject.Enter(prefix + "operator", new HighWaterMark()))
            authority.ResolveCurrent().Should().Be(SecurityLabel.SystemHigh);
        using (EgressSubject.Enter(prefix + "unknown", new HighWaterMark()))
            authority.ResolveCurrent().Should().Be(SecurityLabel.Public);
    }

    [Fact]
    public async Task Work_that_outlives_a_subject_loses_its_read_grant()
    {
        var authority = Authority();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subject = EgressSubject.Enter("agent:operator", new HighWaterMark());
        var work = Task.Run(async () =>
        {
            authority.ResolveCurrent().Should().Be(SecurityLabel.SystemHigh);
            started.SetResult(true);
            await release.Task;
            return authority.ResolveCurrent();
        });
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { subject.Dispose(); release.TrySetResult(true); }
        (await work.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(SecurityLabel.Public);
        authority.ResolveCurrent().Should().Be(SecurityLabel.Public);
    }

    [Fact]
    public void Disposing_an_enclosing_subject_cannot_leave_its_grant_alive_in_a_child()
    {
        var authority = Authority();
        using var parent = EgressSubject.Enter("agent:operator", new HighWaterMark());
        using var child = EgressSubject.Enter("agent:reader", new HighWaterMark());
        authority.ResolveCurrent().ToString().Should().Be("Secret");
        parent.Dispose();
        authority.ResolveCurrent().Should().Be(SecurityLabel.Public);
    }
}
