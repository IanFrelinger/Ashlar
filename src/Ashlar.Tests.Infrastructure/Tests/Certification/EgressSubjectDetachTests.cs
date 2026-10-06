using System.Reflection;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Paths;
using Ashlar.Orchestration.Communication;
using Ashlar.Orchestration.Communication.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.4: work handed to another component is not decided at the caller's mark. The internal, raise-only
/// <c>EgressSubject.Detach</c> leaves every frame, and <see cref="AgentBus"/> dispatches each subscriber under it.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> <see cref="AgentBus.PublishAsync"/> runs every subscriber's handler through <c>Task.Run</c>, which
/// captures the publisher's execution context, so before 4.4 a subscriber's egress was decided at the publisher's
/// mark: another component's sends attributed to, and allowed or refused by, what the publisher had read.</para>
/// <para><b>What is pinned.</b> A subscriber decides with no subject (<see cref="SecurityLabel.SystemHigh"/>) while
/// the publisher is inside a frame, and the publisher's own frame is restored when <c>PublishAsync</c> returns.
/// Under <c>Detach</c> a decision has no subject; a frame entered under it starts a chain of its own whose reads never
/// reach the caller's frames; disposing it restores the caller's frame on that flow only; and a task started under it
/// keeps no subject after the caller is restored. The convention fact pins every call site of <c>Detach</c> in the
/// repository's C#, and that the bus starts each subscriber inside the detached block.</para>
/// <para><b>Internal surface.</b> This assembly is not in <c>Ashlar.Abstractions</c>' InternalsVisibleTo, so
/// <c>Detach</c> is reached by reflection, as <see cref="EgressGuardDecisionTests"/> reads the core's counters.</para>
/// <para><b>Process-global state.</b> None: frames live on each test's own flow, the guard has an explicit profile,
/// and each bus is a fresh instance with a message type unique to the test. The convention fact is a pure file read.
/// It is a tripwire, not a proof: a call spelled through an alias, a delegate or reflection is not seen.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressSubjectDetachTests
{
    private const string NoSubject = "no-subject";
    private const string SubjectPrefix = "subject:";
    private const string Remote = "https://remote.example/v1/chat";
    private const string UnknownFamily = "not-a-family";

    /// <summary>The call as written, split so that this file is not one of the sites it counts.</summary>
    private const string DetachCall = "EgressSubject" + ".Detach(";

    private const string AgentBusPath = "src/Ashlar.Orchestration/Communication/AgentBus.cs";

    /// <summary>Every production dispatch point that must not inherit its caller's subject (design §2.2, D18).</summary>
    private static readonly string[] ExpectedSites = [AgentBusPath + " x1"];

    /// <summary>The trees the call-site scan reads, where they exist.</summary>
    private static readonly string[] ScanRoots =
        ["src", "application", "applications", "apps", "commercial", "products", "tools", "consumer-template", "extensions"];

    /// <summary>Below this many scanned files the scan is taken to have lost its reach, not to have found nothing.</summary>
    private const int ScannedFileFloor = 1000;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>A guard with an explicit profile, so no decision here reads the environment.</summary>
    private static readonly EgressGuard Guard = new("full");

    private static readonly SecurityLabel Internal = new(SecurityLevel.Internal);
    private static readonly SecurityLabel Secret = new(SecurityLevel.Secret);

    [Fact]
    public async Task An_AgentBus_subscriber_runs_with_no_subject_whatever_the_publisher_has_read()
    {
        var bus = new AgentBus(NullLogger<AgentBus>.Instance);
        var messageType = "egress-detach-" + Guid.NewGuid().ToString("N");
        var decided = new TaskCompletionSource<EgressDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = await bus.SubscribeAsync(messageType, (_, _) =>
        {
            decided.TrySetResult(Decide(EgressFamilies.ModelMeai));
            return Task.CompletedTask;
        });

        using (EgressSubject.Enter("bus-publisher", new HighWaterMark(Internal)))
        {
            await bus.PublishAsync(new OutputEmitted
            {
                MessageId = Guid.NewGuid().ToString("N"),
                FromAgentId = "bus-publisher",
                MessageType = messageType,
                Output = "a result the publisher computed",
            });

            var publisher = Decide(EgressFamilies.ModelMeai);
            publisher.CurrentBasis.Should().Be(SubjectPrefix + "bus-publisher", "publishing restores the publisher's own frame");
            publisher.Current.Should().Be(Internal);

            // Waited for while the publisher's frame is still live, so a subscriber that inherited it would show it.
            var subscriber = await decided.Task.WaitAsync(Patience);
            subscriber.CurrentBasis.Should().Be(NoSubject, "a subscriber is another component, never decided at the publisher's mark");
            subscriber.Current.Should().Be(SecurityLabel.SystemHigh);
            subscriber.Access.Reason.Should().Be(AccessDenialReason.SystemHighData, "no subject fails closed upward");
        }
    }

    [Fact]
    public async Task A_publish_from_a_task_inside_an_ended_parent_frame_keeps_the_parent_for_a_frame_entered_after_it()
    {
        var bus = new AgentBus(NullLogger<AgentBus>.Instance);
        var parentDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EgressDecision> child;

        using (EgressSubject.Enter("bus-ended-parent", new HighWaterMark(Secret)))
        {
            child = Task.Run(async () =>
            {
                await parentDisposed.Task.WaitAsync(Patience);

                // The bus detaches this flow while it dispatches, then restores the frame the flow was in.
                await bus.PublishAsync(new OutputEmitted
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    FromAgentId = "bus-ended-parent",
                    MessageType = "egress-detach-" + Guid.NewGuid().ToString("N"),
                    Output = "a result the task computed inside the parent",
                });

                using (EgressSubject.Enter("bus-ended-late", new HighWaterMark()))
                    return Decide(EgressFamilies.ModelMeai);
            });
        }

        parentDisposed.SetResult();
        var late = await child.WaitAsync(Patience);

        late.CurrentBasis.Should().Be(SubjectPrefix + "bus-ended-late");
        late.Current.Should().Be(
            Secret, "the detachment restores exactly the frame the task was in, the ended parent, so a later frame still counts it");
        late.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public void Detach_leaves_every_frame_until_disposed_and_then_restores_the_callers()
    {
        var callerMark = new HighWaterMark(Internal);

        using (EgressSubject.Enter("detach-caller", callerMark))
        {
            using (Detach())
            {
                var detached = Decide(EgressFamilies.ModelMeai);
                detached.CurrentBasis.Should().Be(NoSubject);
                detached.Current.Should().Be(SecurityLabel.SystemHigh, "it only raises: no subject is the top");
                detached.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);

                var innerMark = new HighWaterMark();
                using (EgressSubject.Enter("detach-inner", innerMark))
                {
                    var inner = Decide(UnknownFamily);
                    inner.CurrentBasis.Should().Be(SubjectPrefix + "detach-inner");
                    inner.Current.Should().Be(SecurityLabel.Public, "a frame entered under a detachment starts a chain of its own");

                    innerMark.Observe(Secret);
                }

                Decide(UnknownFamily).CurrentBasis.Should().Be(NoSubject, "disposing the inner frame returns to the detachment");
            }

            callerMark.Current.Should().Be(Internal, "what was read under the detachment never reaches the caller's frames");

            var restored = Decide(EgressFamilies.ModelMeai);
            restored.CurrentBasis.Should().Be(SubjectPrefix + "detach-caller", "disposing the detachment restores the caller's frame");
            restored.Current.Should().Be(Internal);
            restored.Access.Allowed.Should().BeTrue("Internal data may go to an Internal model: {0}", restored.Access);
        }
    }

    [Fact]
    public async Task A_task_started_under_Detach_keeps_no_subject_after_the_caller_is_restored()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EgressDecision> child;

        using (EgressSubject.Enter("detach-spawner", new HighWaterMark(Internal)))
        {
            using (Detach())
            {
                child = Task.Run(async () =>
                {
                    await release.Task.WaitAsync(Patience);
                    return Decide(EgressFamilies.ModelMeai);
                });
            }

            Decide(EgressFamilies.ModelMeai).CurrentBasis.Should().Be(SubjectPrefix + "detach-spawner");

            release.SetResult();
            var decision = await child.WaitAsync(Patience);

            decision.CurrentBasis.Should().Be(NoSubject, "the task never falls back to the caller's frame, which is still live");
            decision.Current.Should().Be(SecurityLabel.SystemHigh);
        }
    }

    [Fact]
    public async Task A_detachment_disposed_from_another_flow_restores_nothing_there()
    {
        var scope = EgressSubject.Enter("detach-elsewhere", new HighWaterMark(Internal));
        try
        {
            var detachment = Detach();
            await Task.Run(detachment.Dispose).WaitAsync(Patience);

            Decide(UnknownFamily).CurrentBasis.Should().Be(NoSubject, "this flow stays detached: a detachment only raises");

            detachment.Dispose();
            Decide(UnknownFamily).CurrentBasis.Should().Be(NoSubject, "a second Dispose does nothing");
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public void Detach_is_called_only_at_the_listed_dispatch_points()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var (sites, scanned) = Sites(root);

        scanned.Should().BeGreaterThanOrEqualTo(
            ScannedFileFloor, "the scan must reach the repository's C#; an emptied scan proves nothing");
        sites.Should().Equal(
            ExpectedSites,
            "Detach leaves the caller's subject. It belongs only where work is handed to another component (AgentBus "
            + "subscriber dispatch). A new site needs a reason recorded in the design and a row here; a missing one "
            + "means a dispatch point decides at its caller's mark again");
    }

    [Fact]
    public void The_AgentBus_dispatch_starts_every_subscriber_inside_the_detachment()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, AgentBusPath));

        var call = text.IndexOf("using (" + DetachCall + "))", StringComparison.Ordinal);
        call.Should().BeGreaterThanOrEqualTo(0, "PublishAsync dispatches its subscribers inside a using block around the detachment");

        var open = text.IndexOf('{', call);
        open.Should().BeGreaterThan(call);
        var close = MatchingBrace(text, open);
        close.Should().BeGreaterThan(open, "the detached block must close");

        var inside = text.Substring(open, close - open + 1);
        var outside = text.Remove(open, close - open + 1);

        inside.Should().Contain("Task.Run(", "each subscriber's task starts inside the detached block");
        inside.Should().Contain("subscription.Handler(", "and runs the subscriber's handler");
        outside.Should().NotContain("Task.Run(", "no dispatch task starts outside it");
        outside.Should().NotContain(".Handler(", "no handler runs outside it");
    }

    private static IDisposable Detach()
    {
        var method = typeof(EgressSubject).GetMethod("Detach", BindingFlags.NonPublic | BindingFlags.Static, binder: null, Type.EmptyTypes, modifiers: null);
        method.Should().NotBeNull("EgressSubject.Detach is the internal, raise-only way to leave every frame (SPEC-007 PR 4.4)");
        return (IDisposable)method!.Invoke(null, null)!;
    }

    private static EgressDecision Decide(string family) =>
        Guard.Evaluate(new EgressRequest(family, "twin:detach:" + Guid.NewGuid().ToString("N"), new Uri(Remote)));

    private static (List<string> Sites, int Scanned) Sites(string root)
    {
        var found = new List<string>();
        var scanned = 0;
        foreach (var name in ScanRoots)
        {
            var start = Path.Combine(root, name);
            if (Directory.Exists(start))
                scanned += Collect(root, start, found);
        }

        found.Sort(StringComparer.Ordinal);
        return (found, scanned);
    }

    private static int Collect(string root, string directory, List<string> found)
    {
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            scanned++;
            var text = File.ReadAllText(file);
            var count = 0;
            for (var i = text.IndexOf(DetachCall, StringComparison.Ordinal); i >= 0; i = text.IndexOf(DetachCall, i + DetachCall.Length, StringComparison.Ordinal))
                count++;

            if (count > 0)
                found.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')} x{count}");
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (!IsPruned(child))
                scanned += Collect(root, child, found);
        }

        return scanned;
    }

    /// <summary>Build output, tool state and the root of any nested checkout, as the other convention tests prune.</summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);
        if (name is "bin" or "obj" || name.StartsWith('.'))
            return true;

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return i;
        }

        return -1;
    }
}
