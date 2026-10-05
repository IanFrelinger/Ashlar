using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.4, monotone nesting of <see cref="EgressSubject"/> frames: a nested frame never decides below a
/// frame it was entered inside.
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> A decision's current label is the join of every live frame's mark on the chain, and
/// its basis names the innermost subject. Disposing a frame observes its mark into every enclosing live frame,
/// also when the frame was entered in a child task whose frames never flow back out. <see cref="EgressSubject.Observe"/>
/// joins into every live frame at once, so an enclosing flow that decides while a nested frame is still running in
/// another task already counts what it read, and it only raises. Before 4.4 the guard read only
/// the innermost live frame, so entering a fresh Public frame inside a Secret one declassified everything the
/// enclosing subject had read, and a nested subject's reads were lost when it was disposed.</para>
/// <para><b>Process-global state.</b> None. Frames live on each test's own async flow, the guard has an explicit
/// profile and reads no environment variable, and each decision is read from the value <c>Evaluate</c> returns.</para>
/// <para>Hermetic: no network, no files, no environment.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressSubjectNestingTests
{
    private const string NoSubject = "no-subject";
    private const string SubjectPrefix = "subject:";
    private const string Remote = "https://remote.example/v1/chat";
    private const string UnknownFamily = "not-a-family";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>A guard with an explicit profile, so no decision here reads the environment.</summary>
    private static readonly EgressGuard Guard = new("full");

    private static readonly SecurityLabel Internal = new(SecurityLevel.Internal);
    private static readonly SecurityLabel Confidential = new(SecurityLevel.Confidential);
    private static readonly SecurityLabel Secret = new(SecurityLevel.Secret);

    [Fact]
    public void A_Public_frame_inside_a_Secret_frame_decides_Secret()
    {
        using (EgressSubject.Enter("nest-outer", new HighWaterMark(Secret)))
        using (EgressSubject.Enter("nest-inner", new HighWaterMark()))
        {
            var decision = Decide(EgressFamilies.ModelMeai);

            decision.CurrentBasis.Should().Be(SubjectPrefix + "nest-inner", "the basis names the innermost subject");
            decision.Current.Should().Be(Secret, "a nested frame never decides below a frame it was entered inside");
            decision.Access.Allowed.Should().BeFalse("Secret data may not go to an Internal model: {0}", decision.Access);
            decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }

        Decide(EgressFamilies.ModelMeai).CurrentBasis.Should().Be(NoSubject);
    }

    [Fact]
    public async Task Every_live_frame_on_the_chain_is_joined_across_await()
    {
        var outer = new SecurityLabel(SecurityLevel.Internal, ["NEST"]);

        using (EgressSubject.Enter("chain-outer", new HighWaterMark(outer)))
        {
            await Task.Yield();
            using (EgressSubject.Enter("chain-middle", new HighWaterMark()))
            {
                await Task.Delay(1);
                using (EgressSubject.Enter("chain-inner", new HighWaterMark(Confidential)))
                {
                    await Task.Yield();

                    var decision = Decide(UnknownFamily);
                    decision.CurrentBasis.Should().Be(SubjectPrefix + "chain-inner");
                    decision.Current.Should().Be(
                        new SecurityLabel(SecurityLevel.Confidential, ["NEST"]),
                        "the current label is the join of Internal//C:NEST, Public and Confidential");
                }
            }
        }
    }

    [Fact]
    public void A_frame_disposed_out_of_order_leaves_the_chain()
    {
        var outer = EgressSubject.Enter("order-outer", new HighWaterMark(Secret));
        var inner = EgressSubject.Enter("order-inner", new HighWaterMark(Internal));
        try
        {
            outer.Dispose();

            var decision = Decide(EgressFamilies.ModelMeai);
            decision.CurrentBasis.Should().Be(SubjectPrefix + "order-inner");
            decision.Current.Should().Be(Internal, "a disposed frame counts nowhere, so only the inner mark is left");
            decision.Access.Allowed.Should().BeTrue("Internal data may go to an Internal model: {0}", decision.Access);
        }
        finally
        {
            inner.Dispose();
            outer.Dispose();
        }

        Decide(EgressFamilies.ModelMeai).CurrentBasis.Should().Be(NoSubject);
    }

    [Fact]
    public void Disposing_a_frame_observes_its_mark_into_every_enclosing_live_frame()
    {
        var outerMark = new HighWaterMark();
        var middleMark = new HighWaterMark(Internal);
        var innerMark = new HighWaterMark();

        using (EgressSubject.Enter("dispose-outer", outerMark))
        {
            using (EgressSubject.Enter("dispose-middle", middleMark))
            {
                using (EgressSubject.Enter("dispose-inner", innerMark))
                {
                    // Straight into the inner mark, as a producer holding it would: only disposal carries it outward.
                    innerMark.Observe(Secret);
                    outerMark.Current.Should().Be(SecurityLabel.Public, "nothing has propagated before the inner frame ends");
                }

                middleMark.Current.Should().Be(Secret, "what the inner subject read leaves with its output");
                outerMark.Current.Should().Be(Secret, "every enclosing live frame is raised, not only the nearest");

                var middle = Decide(EgressFamilies.ModelMeai);
                middle.CurrentBasis.Should().Be(SubjectPrefix + "dispose-middle");
                middle.Current.Should().Be(Secret);
                middle.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow, "Secret may not go to an Internal model");
            }

            var outer = Decide(EgressFamilies.ModelMeai);
            outer.CurrentBasis.Should().Be(SubjectPrefix + "dispose-outer");
            outer.Current.Should().Be(Secret);
            outer.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
    }

    [Fact]
    public async Task A_frame_entered_in_a_child_task_reaches_the_parent_frame_when_it_is_disposed()
    {
        using (EgressSubject.Enter("child-parent", new HighWaterMark()))
        {
            await Task.Run(() =>
            {
                var innerMark = new HighWaterMark();
                using (EgressSubject.Enter("child-inner", innerMark))
                    innerMark.Observe(Secret);
            }).WaitAsync(Patience);

            var decision = Decide(EgressFamilies.ModelMeai);
            decision.CurrentBasis.Should().Be(SubjectPrefix + "child-parent", "the child's frame never flows back out");
            decision.Current.Should().Be(Secret, "but what the child's subject read does, when its frame is disposed");
            decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }
    }

    [Fact]
    public void Observe_joins_into_every_live_frame_on_the_chain_at_once()
    {
        var outerMark = new HighWaterMark();
        var innerMark = new HighWaterMark();

        using (EgressSubject.Enter("observe-outer", outerMark))
        using (EgressSubject.Enter("observe-inner", innerMark))
        {
            EgressSubject.Observe(Confidential);

            innerMark.Current.Should().Be(Confidential);
            outerMark.Current.Should().Be(Confidential, "every live frame is raised at once, before the inner frame ends");
        }
    }

    [Fact]
    public async Task Observe_reaches_an_enclosing_flow_while_the_nested_frame_is_still_running()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using (EgressSubject.Enter("live-outer", new HighWaterMark()))
        {
            var child = Task.Run(async () =>
            {
                using (EgressSubject.Enter("live-inner", new HighWaterMark()))
                {
                    EgressSubject.Observe(Secret);
                    observed.SetResult();
                    await release.Task.WaitAsync(Patience);
                    return Decide(EgressFamilies.ModelMeai);
                }
            });

            await observed.Task.WaitAsync(Patience);

            // The child's frame is still live: only the live join can have raised this flow's frame.
            var parent = Decide(EgressFamilies.ModelMeai);
            release.SetResult();
            var inner = await child.WaitAsync(Patience);

            parent.CurrentBasis.Should().Be(SubjectPrefix + "live-outer");
            parent.Current.Should().Be(Secret, "an enclosing flow that egresses while the nested one runs already counts its reads");
            parent.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
            inner.CurrentBasis.Should().Be(SubjectPrefix + "live-inner");
            inner.Current.Should().Be(Secret);
        }
    }

    [Fact]
    public void Observe_only_raises_and_with_no_frame_does_nothing()
    {
        EgressSubject.Observe(SecurityLabel.Public);
        var none = Decide(EgressFamilies.ModelMeai);
        none.CurrentBasis.Should().Be(NoSubject, "with no frame there is nothing to raise: no subject is already the top");
        none.Current.Should().Be(SecurityLabel.SystemHigh);

        var mark = new HighWaterMark(Secret);
        using (EgressSubject.Enter("observe-raises", mark))
        {
            EgressSubject.Observe(SecurityLabel.Public);
            EgressSubject.Observe(Internal);

            mark.Current.Should().Be(Secret, "a lower label never lowers a mark");
            Decide(EgressFamilies.ModelMeai).Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        }

        ((Action)(() => EgressSubject.Observe(null!))).Should().Throw<ArgumentNullException>().WithParameterName("label");
    }

    private static EgressDecision Decide(string family) =>
        Guard.Evaluate(new EgressRequest(family, "twin:nesting:" + Guid.NewGuid().ToString("N"), new Uri(Remote)));
}
