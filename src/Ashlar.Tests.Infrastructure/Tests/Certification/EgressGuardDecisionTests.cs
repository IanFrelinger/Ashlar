using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Reflection;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.BackgroundAgents.DataSensitivity;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 3a, behavioural twin of the egress guard's decision core: <see cref="EgressGuard"/>,
/// <see cref="EgressSubject"/>, <see cref="EgressDecisionLog"/> and the <c>Ashlar-Egress</c> event source.
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> The destination table row by row (loopback v4 and v6, <c>localhost</c>,
/// <c>*.localhost</c>, <c>unix:</c>, <c>npipe:</c>, <c>host:</c> names, every family, and Unknown giving Public),
/// and, since PR 4.1, that a <c>file</c> URI or a <c>file:</c> name is never inside the host boundary;
/// that the table's labels are the ones derived from the built-in <see cref="DataSensitivityLevels"/> flags through
/// <see cref="DataSensitivityLabelBridge.ToDataLabel"/>; that with no subject every non-host destination is refused
/// with <see cref="AccessDenialReason.SystemHighData"/> and the host boundary is allowed; that an
/// <see cref="EgressSubject"/> frame lowers the current label, flows across <c>await</c>, and is restored on
/// <c>Dispose</c>, and that a nested frame decides at the join of every frame it was entered inside (SPEC-007 PR 4.4; the rest of
/// frame semantics is in <see cref="EgressSubjectNestingTests"/>); that a record never carries a userinfo, path, query or fragment; that a
/// fault is a record with <see cref="EgressDecision.Fault"/> and <c>default(AccessDecision)</c>, never a throw; that an
/// explicit profile is reported and sets <see cref="EgressDecision.ProfileEnforcesByDefault"/> without changing the
/// decision; and that every record reaches each subscriber and the event source.</para>
/// <para><b>Process-global state.</b> The decision log, the event source and the sequence counter belong to the
/// process, and other classes in this assembly make decisions in parallel. Every assertion on published records
/// therefore filters by a site string unique to the test (<see cref="NewSite"/>), every subscription and listener is
/// disposed, every subject frame is disposed, and no environment variable is read or written: each guard gets its
/// profile through the constructor. The fault counters the core keeps are internal and only ever rise, so they are
/// read by reflection and asserted as "rose by at least one".</para>
/// <para>Hermetic: no network, no files, no environment.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressGuardDecisionTests
{
    private const string NoSubject = "no-subject";
    private const string SubjectPrefix = "subject:";
    private const string SourceName = "Ashlar-Egress";
    private const string Remote = "https://remote.example/v1/chat";

    /// <summary>The design's "Basis recorded" column, verbatim.</summary>
    private const string HostBasis = "inside the host boundary";
    private const string ExternalModelBasis = "highest level with AllowsExternalLLM (Public, Internal)";
    private const string WebSearchBasis = "highest level with AllowsWebSearch (Public to Confidential)";
    private const string NetworkExportBasis = "highest level with AllowsNetworkExports (Public, Internal)";
    private const string UnknownBasis = "an unknown destination fails closed to the bottom";

    /// <summary>Text that must never reach a record when it sits in a userinfo, path, query or fragment.</summary>
    private static readonly string[] SecretMarkers = ["twin-user", "pa55word", "PATHMARK", "QUERYTOKEN", "FRAGMARK"];

    /// <summary>A guard with an explicit profile, so no decision here reads the environment.</summary>
    private static readonly EgressGuard Guard = new("full");

    private static readonly SecurityLabel InternalLabel = new(SecurityLevel.Internal);
    private static readonly SecurityLabel ConfidentialLabel = new(SecurityLevel.Confidential);

    /// <summary>
    /// Every family the design names and the class it gives a destination outside the host boundary. Compared with
    /// <see cref="EgressFamilies"/> by reflection, so a family added there without a row here fails.
    /// </summary>
    private static readonly Dictionary<string, EgressDestinationClass> FamilyTable = new(StringComparer.Ordinal)
    {
        ["model.meai"] = EgressDestinationClass.ExternalModel,
        ["model.legacy"] = EgressDestinationClass.ExternalModel,
        ["web-search"] = EgressDestinationClass.WebSearch,
        ["a2a"] = EgressDestinationClass.NetworkExport,
        ["grpc"] = EgressDestinationClass.NetworkExport,
        ["mcp"] = EgressDestinationClass.NetworkExport,
        ["http"] = EgressDestinationClass.NetworkExport,
        ["http.factory"] = EgressDestinationClass.NetworkExport,
        ["mesh.publish"] = EgressDestinationClass.NetworkExport,
        ["mesh.serve"] = EgressDestinationClass.NetworkExport,
        ["mesh.pull"] = EgressDestinationClass.NetworkExport,
        ["mesh.discovery"] = EgressDestinationClass.NetworkExport,
        ["file.export"] = EgressDestinationClass.NetworkExport,
        ["process"] = EgressDestinationClass.NetworkExport,
        ["telemetry"] = EgressDestinationClass.NetworkExport,
    };

    // ---------------------------------------------------------------------------------------------------------
    // The destination table
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    // Loopback IPv4: the whole 127/8 block, with a port and a path.
    [InlineData("http", "http://127.0.0.1:8080/v1", EgressDestinationClass.Host)]
    [InlineData("model.meai", "http://127.8.9.10/", EgressDestinationClass.Host)]
    // Loopback IPv6, bracketed, and IPv4-mapped IPv6.
    [InlineData("grpc", "http://[::1]:5000/", EgressDestinationClass.Host)]
    [InlineData("grpc", "http://[::ffff:127.0.0.1]/", EgressDestinationClass.Host)]
    // localhost, case-insensitive, and its fully-qualified form.
    [InlineData("model.meai", "http://localhost:11434", EgressDestinationClass.Host)]
    [InlineData("model.legacy", "http://LOCALHOST:11434/api", EgressDestinationClass.Host)]
    [InlineData("http", "http://localhost./", EgressDestinationClass.Host)]
    // *.localhost.
    [InlineData("http", "http://ollama.localhost/", EgressDestinationClass.Host)]
    [InlineData("web-search", "http://a.b.localhost:9200/", EgressDestinationClass.Host)]
    // unix: and npipe: sockets.
    [InlineData("http", "unix:///var/run/docker.sock", EgressDestinationClass.Host)]
    [InlineData("model.legacy", "npipe://./pipe/docker_engine", EgressDestinationClass.Host)]
    // Not the host boundary: private, any-address, non-loopback IPv6, and names that only contain "localhost".
    [InlineData("http", "http://10.0.0.1/", EgressDestinationClass.NetworkExport)]
    [InlineData("http", "http://192.168.1.10:11434/", EgressDestinationClass.NetworkExport)]
    [InlineData("http", "http://0.0.0.0/", EgressDestinationClass.NetworkExport)]
    [InlineData("http", "http://[::2]/", EgressDestinationClass.NetworkExport)]
    [InlineData("http", "http://localhost.evil.example/", EgressDestinationClass.NetworkExport)]
    [InlineData("http", "http://evil-localhost/", EgressDestinationClass.NetworkExport)]
    [InlineData("model.meai", "http://ollama:11434/", EgressDestinationClass.ExternalModel)]
    // Every family, for a destination outside the host boundary.
    [InlineData("model.meai", Remote, EgressDestinationClass.ExternalModel)]
    [InlineData("model.legacy", Remote, EgressDestinationClass.ExternalModel)]
    [InlineData("web-search", Remote, EgressDestinationClass.WebSearch)]
    [InlineData("a2a", Remote, EgressDestinationClass.NetworkExport)]
    [InlineData("grpc", Remote, EgressDestinationClass.NetworkExport)]
    [InlineData("mcp", Remote, EgressDestinationClass.NetworkExport)]
    [InlineData("http", Remote, EgressDestinationClass.NetworkExport)]
    [InlineData("http.factory", Remote, EgressDestinationClass.NetworkExport)]
    [InlineData("mesh.publish", Remote, EgressDestinationClass.NetworkExport)]
    [InlineData("mesh.serve", "tcp://203.0.113.7:7420", EgressDestinationClass.NetworkExport)]
    [InlineData("mesh.pull", Remote, EgressDestinationClass.NetworkExport)]
    [InlineData("mesh.discovery", "udp://239.7.42.1:7421", EgressDestinationClass.NetworkExport)]
    [InlineData("file.export", Remote, EgressDestinationClass.NetworkExport)]
    [InlineData("process", Remote, EgressDestinationClass.NetworkExport)]
    [InlineData("telemetry", Remote, EgressDestinationClass.NetworkExport)]
    // Unknown families: matched ordinally, so near misses are unknown too, and unknown is Public.
    [InlineData("not-a-family", Remote, EgressDestinationClass.Unknown)]
    [InlineData("", Remote, EgressDestinationClass.Unknown)]
    [InlineData("MODEL.meai", Remote, EgressDestinationClass.Unknown)]
    [InlineData("model", Remote, EgressDestinationClass.Unknown)]
    [InlineData("mesh", Remote, EgressDestinationClass.Unknown)]
    [InlineData("Web-Search", Remote, EgressDestinationClass.Unknown)]
    [InlineData("http ", Remote, EgressDestinationClass.Unknown)]
    public void A_uri_destination_is_classified_and_labelled_by_the_table(
        string family, string uri, EgressDestinationClass expected)
    {
        var decision = Guard.Evaluate(new EgressRequest(family, NewSite(), new Uri(uri)));

        AssertRow(decision, expected, $"family '{family}' to {uri}");
    }

    [Theory]
    // A host: name is inside the host boundary whatever the family.
    [InlineData("process", "host:dotnet", EgressDestinationClass.Host)]
    [InlineData("model.meai", "host:ollama", EgressDestinationClass.Host)]
    [InlineData("not-a-family", "host:anything", EgressDestinationClass.Host)]
    // The prefix is exact and case-sensitive.
    [InlineData("process", "HOST:dotnet", EgressDestinationClass.NetworkExport)]
    [InlineData("process", "hostname", EgressDestinationClass.NetworkExport)]
    [InlineData("process", " host:dotnet", EgressDestinationClass.NetworkExport)]
    // Other names fall to the family.
    [InlineData("model.meai", "aws-bedrock", EgressDestinationClass.ExternalModel)]
    [InlineData("web-search", "bing", EgressDestinationClass.WebSearch)]
    [InlineData("mesh.publish", "meshstore", EgressDestinationClass.NetworkExport)]
    [InlineData("not-a-family", "anything", EgressDestinationClass.Unknown)]
    [InlineData("http", "", EgressDestinationClass.NetworkExport)]
    // A URL-shaped name is read as the URL it is.
    [InlineData("process", "tcp://127.0.0.1:7000", EgressDestinationClass.Host)]
    [InlineData("model.meai", "http://localhost:11434/api", EgressDestinationClass.Host)]
    [InlineData("process", "tcp://10.1.2.3:7000", EgressDestinationClass.NetworkExport)]
    public void A_named_destination_is_classified_and_labelled_by_the_table(
        string family, string name, EgressDestinationClass expected)
    {
        var decision = Guard.Evaluate(new EgressRequest(family, NewSite(), name));

        AssertRow(decision, expected, $"family '{family}' to the name '{name}'");
    }

    /// <summary>
    /// SPEC-007 PR 4.1, gap 3: a <c>file</c> URI is never inside the host boundary, whatever its host. A file
    /// written to <c>//localhost/share</c> or <c>//127.0.0.1/E$</c> leaves through a share, not through the host,
    /// so it is classified by its family (a network export for both file families).
    /// </summary>
    [Theory]
    [InlineData("file.export", "file://localhost/share/out.nxpkg")]
    [InlineData("file.export", "file://127.0.0.1/E$/out.nxpkg")]
    [InlineData("mesh.publish", "file://[::1]/share/published")]
    [InlineData("mesh.publish", "FILE://LOCALHOST/share")]
    [InlineData("file.export", "file://ollama.localhost/x")]
    public void A_file_uri_is_never_inside_the_host_boundary(string family, string uri)
    {
        var decision = Guard.Evaluate(new EgressRequest(family, NewSite(), new Uri(uri)));

        AssertRow(decision, EgressDestinationClass.NetworkExport, $"family '{family}' to the file URI {uri}");
    }

    /// <summary>
    /// SPEC-007 PR 4.1, gap 3: a name that starts with <c>file:</c> (in any case) is a path. It is never read as a
    /// URL, so a path spelled <c>//127.0.0.1/…</c> cannot make <c>file:</c> plus that path a URL with a loopback
    /// host; it is recorded as written, like every other file site's path (<c>file:/home/…</c> always was), and
    /// classified by its family. So the URL redaction of <see cref="AssertRow"/> does not apply to it; the bound on
    /// caller text still does.
    /// </summary>
    [Theory]
    [InlineData("file.export", "file://127.0.0.1/E$/out.nxpkg")]
    [InlineData("mesh.publish", "file://localhost/share")]
    [InlineData("file.export", "file://[::1]/x/out.nxpkg")]
    [InlineData("mesh.publish", "FILE://127.0.0.1/share")]
    [InlineData("file.export", "File:////127.0.0.1/share/out.nxpkg")]
    [InlineData("file.export", @"file:\\127.0.0.1\share\out.nxpkg")]
    public void A_file_name_is_a_path_recorded_as_written_and_never_inside_the_host_boundary(string family, string name)
    {
        var decision = Guard.Evaluate(new EgressRequest(family, NewSite(), name));

        var what = $"family '{family}' to the name '{name}'";
        decision.Fault.Should().BeNull(what);
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport, what);
        decision.DestinationLabel.Should().Be(InternalLabel, what);
        decision.DestinationBasis.Should().Be(NetworkExportBasis, what);
        decision.Destination.Should().Be(name, "a file: name is a path, recorded as written like every other file site's");
    }

    [Fact]
    public void Every_declared_family_has_a_row_and_classifies_by_it()
    {
        var declared = typeof(EgressFamilies)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        declared.Should().OnlyHaveUniqueItems();
        declared.Should().BeEquivalentTo(
            FamilyTable.Keys,
            "the design names exactly these families, and a new one needs a class decided on purpose");

        foreach (var (family, expected) in FamilyTable)
        {
            AssertRow(Guard.Evaluate(new EgressRequest(family, NewSite(), new Uri(Remote))), expected, family);
            AssertRow(
                Guard.Evaluate(new EgressRequest(family, NewSite(), new Uri("http://127.0.0.1:9/"))),
                EgressDestinationClass.Host,
                family + " to loopback, where the host rule wins over the family");
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The labels are derived, not configured
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Each label in the table is the data label of the highest built-in level whose flag allows that kind of
    /// destination, mapped through <see cref="DataSensitivityLabelBridge.ToDataLabel"/>.
    /// </summary>
    [Fact]
    public void The_destination_labels_are_the_highest_built_in_level_allowing_that_destination()
    {
        var flags = new (string Flag, Func<IDataSensitivityLevel, bool> Allows, EgressDestinationClass Class)[]
        {
            ("AllowsExternalLLM", level => level.AllowsExternalLLM, EgressDestinationClass.ExternalModel),
            ("AllowsWebSearch", level => level.AllowsWebSearch, EgressDestinationClass.WebSearch),
            ("AllowsNetworkExports", level => level.AllowsNetworkExports, EgressDestinationClass.NetworkExport),
        };

        foreach (var (flag, allows, destinationClass) in flags)
        {
            var allowing = DataSensitivityLevels.All.Where(allows).OrderBy(level => level.SensitivityValue).ToList();
            allowing.Should().NotBeEmpty("some built-in level has {0}", flag);
            var highest = allowing[^1];

            // "Highest level with the flag" is a threshold only if the flag is downward closed.
            DataSensitivityLevels.All
                .Where(level => level.SensitivityValue <= highest.SensitivityValue)
                .Should().OnlyContain(level => allows(level), "{0} holds for every level up to {1}", flag, highest.Value);

            var derived = highest.ToDataLabel();
            var families = FamilyTable.Where(row => row.Value == destinationClass).Select(row => row.Key).ToList();
            families.Should().NotBeEmpty();

            foreach (var family in families)
            {
                var decision = Guard.Evaluate(new EgressRequest(family, NewSite(), new Uri(Remote)));

                decision.DestinationClass.Should().Be(destinationClass, family);
                decision.DestinationLabel.Should().Be(
                    derived,
                    "{0} is {1}, the highest built-in level with {2}", family, highest.Value, flag);
                decision.DestinationBasis.Should().Contain(flag).And.Contain(highest.Value);
            }
        }

        // Unknown is the bottom: the lowest built-in level's label, which is Public.
        var lowest = DataSensitivityLevels.All.OrderBy(level => level.SensitivityValue).First();
        Guard.Evaluate(new EgressRequest("not-a-family", NewSite(), new Uri(Remote)))
            .DestinationLabel.Should().Be(lowest.ToDataLabel()).And.Be(SecurityLabel.Public);

        // The host boundary must receive anything, including unlabelled data (SystemHigh) and RequiresLocalOnly
        // levels, so its label must dominate the bridge's label for no level at all.
        var host = Guard.Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), new Uri("http://127.0.0.1/")));
        host.DestinationLabel.Dominates(DataSensitivityLabelBridge.ToDataLabel(null)).Should().BeTrue();
        host.DestinationLabel.Should().Be(SecurityLabel.SystemHigh);
    }

    /// <summary>
    /// The same derivation, seen from the decision: a subject whose mark is a built-in level's data label is allowed
    /// to a destination exactly when that level's flag allows it.
    /// </summary>
    [Fact]
    public void For_every_built_in_level_the_guard_allows_exactly_what_its_flag_allows()
    {
        foreach (var level in DataSensitivityLevels.All)
        {
            using var scope = EgressSubject.Enter("level-" + level.Value, new HighWaterMark(level.ToDataLabel()));

            foreach (var (family, destinationClass) in FamilyTable)
            {
                var expected = destinationClass switch
                {
                    EgressDestinationClass.ExternalModel => level.AllowsExternalLLM,
                    EgressDestinationClass.WebSearch => level.AllowsWebSearch,
                    _ => level.AllowsNetworkExports,
                };

                var decision = Guard.Evaluate(new EgressRequest(family, NewSite(), new Uri(Remote)));
                decision.Access.Allowed.Should().Be(
                    expected, "{0} data to {1} ({2}): {3}", level.Value, family, destinationClass, decision.Access);
                decision.CurrentBasis.Should().Be(SubjectPrefix + "level-" + level.Value);
            }

            Guard.Evaluate(new EgressRequest(EgressFamilies.Process, NewSite(), "host:dotnet"))
                .Access.Allowed.Should().BeTrue("{0} data may always stay inside the host boundary", level.Value);
            Guard.Evaluate(new EgressRequest("not-a-family", NewSite(), new Uri(Remote)))
                .Access.Allowed.Should().Be(
                    level.SensitivityValue == 0, "only the bottom level may go to an unknown destination ({0})", level.Value);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The current label: no subject, and subject frames
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("model.meai")]
    [InlineData("model.legacy")]
    [InlineData("web-search")]
    [InlineData("a2a")]
    [InlineData("grpc")]
    [InlineData("mcp")]
    [InlineData("http")]
    [InlineData("http.factory")]
    [InlineData("mesh.publish")]
    [InlineData("mesh.serve")]
    [InlineData("mesh.pull")]
    [InlineData("mesh.discovery")]
    [InlineData("file.export")]
    [InlineData("process")]
    [InlineData("telemetry")]
    [InlineData("not-a-family")]
    public void With_no_subject_a_non_host_destination_is_refused_with_SystemHighData(string family)
    {
        var decision = Guard.Evaluate(new EgressRequest(family, NewSite(), new Uri(Remote)));

        decision.Current.Should().Be(SecurityLabel.SystemHigh);
        decision.CurrentBasis.Should().Be(NoSubject);
        decision.Access.Allowed.Should().BeFalse("unlabelled data fails closed upward, to SystemHigh");
        decision.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        decision.Access.Detail.Should().NotBeNullOrWhiteSpace("every refusal is explained");
        decision.Fault.Should().BeNull();
    }

    [Theory]
    [InlineData("model.meai", "http://127.0.0.1:11434/api/chat")]
    [InlineData("grpc", "http://[::1]:5000/")]
    [InlineData("http", "http://localhost:8080/")]
    [InlineData("web-search", "http://search.localhost/")]
    [InlineData("http", "unix:///var/run/docker.sock")]
    [InlineData("http", "npipe://./pipe/docker_engine")]
    public void With_no_subject_the_host_boundary_is_allowed(string family, string uri)
    {
        var decision = Guard.Evaluate(new EgressRequest(family, NewSite(), new Uri(uri)));

        decision.CurrentBasis.Should().Be(NoSubject);
        decision.Access.Allowed.Should().BeTrue("{0} is inside the host boundary: {1}", uri, decision.Access);
        decision.Access.Reason.Should().Be(AccessDenialReason.None);

        var named = Guard.Evaluate(new EgressRequest(EgressFamilies.Process, NewSite(), "host:dotnet"));
        named.Access.Allowed.Should().BeTrue("a host: name is inside the host boundary: {0}", named.Access);
    }

    [Fact]
    public async Task An_Internal_subject_may_reach_an_external_model_across_await_and_Dispose_restores_the_previous_frame()
    {
        Decide(EgressFamilies.ModelMeai, Remote).CurrentBasis.Should().Be(NoSubject);

        using (EgressSubject.Enter("agent-outer", new HighWaterMark(InternalLabel)))
        {
            await Task.Yield();
            await Task.Delay(1);

            var outer = Decide(EgressFamilies.ModelMeai, Remote);
            outer.Current.Should().Be(InternalLabel);
            outer.CurrentBasis.Should().Be(SubjectPrefix + "agent-outer");
            outer.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
            outer.Access.Allowed.Should().BeTrue("Internal data may go to an Internal destination: {0}", outer.Access);
            outer.Access.Reason.Should().Be(AccessDenialReason.None);

            using (EgressSubject.Enter("agent-inner", new HighWaterMark()))
            {
                await Task.Delay(1);

                // SPEC-007 PR 4.4 flipped this block. It pinned "inner Public decides Public", which let a fresh
                // frame declassify what the enclosing subject had read; a nested frame now joins every frame it was entered inside.
                var inner = Decide("not-a-family", Remote);
                inner.CurrentBasis.Should().Be(SubjectPrefix + "agent-inner", "the basis names the innermost subject");
                inner.Current.Should().Be(InternalLabel, "a nested frame never decides below a frame it was entered inside");
                inner.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow, "the enclosing Internal data may not be written down to Public");
            }

            await Task.Yield();

            var restored = Decide("not-a-family", Remote);
            restored.CurrentBasis.Should().Be(SubjectPrefix + "agent-outer", "disposing the inner frame restores the outer one");
            restored.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow, "Internal may not write down to Public");
        }

        await Task.Yield();

        var after = Decide(EgressFamilies.ModelMeai, Remote);
        after.CurrentBasis.Should().Be(NoSubject, "disposing the outer frame restores no-subject");
        after.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
    }

    [Fact]
    public void The_mark_is_read_when_the_decision_is_made()
    {
        var mark = new HighWaterMark(InternalLabel);
        using var scope = EgressSubject.Enter("agent-reads", mark);

        Decide(EgressFamilies.ModelMeai, Remote).Access.Allowed.Should().BeTrue();

        mark.Observe(new SecurityLabel(SecurityLevel.Secret));

        var decision = Decide(EgressFamilies.ModelMeai, Remote);
        decision.Current.Should().Be(new SecurityLabel(SecurityLevel.Secret));
        decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow, "the subject has since read Secret data");
    }

    [Fact]
    public void Frames_disposed_out_of_order_or_twice_never_resurrect()
    {
        var outer = EgressSubject.Enter("outer", new HighWaterMark());
        var inner = EgressSubject.Enter("inner", new HighWaterMark());

        outer.Dispose();
        Decide("not-a-family", Remote).CurrentBasis.Should().Be(SubjectPrefix + "inner", "the inner frame is still live");

        inner.Dispose();
        Decide("not-a-family", Remote).CurrentBasis.Should().Be(NoSubject, "both frames are disposed");

        outer.Dispose();
        inner.Dispose();
        Decide("not-a-family", Remote).CurrentBasis.Should().Be(NoSubject, "a second Dispose does nothing");

        using (EgressSubject.Enter("kept", new HighWaterMark()))
        {
            var stale = EgressSubject.Enter("stale", new HighWaterMark());
            stale.Dispose();
            stale.Dispose();
            Decide("not-a-family", Remote).CurrentBasis.Should().Be(SubjectPrefix + "kept");
        }

        Decide("not-a-family", Remote).CurrentBasis.Should().Be(NoSubject);
    }

    [Fact]
    public async Task Each_async_flow_sees_only_its_own_frame()
    {
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        async Task<EgressDecision> InFlow(string id, SecurityLabel floor)
        {
            using var scope = EgressSubject.Enter(id, new HighWaterMark(floor));
            if (Interlocked.Increment(ref entered) == 2)
                bothEntered.SetResult();
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return Decide(EgressFamilies.ModelMeai, Remote);
        }

        var results = await Task.WhenAll(
            Task.Run(() => InFlow("flow-public", SecurityLabel.Public)),
            Task.Run(() => InFlow("flow-secret", new SecurityLabel(SecurityLevel.Secret))));

        results[0].CurrentBasis.Should().Be(SubjectPrefix + "flow-public");
        results[0].Access.Allowed.Should().BeTrue();
        results[1].CurrentBasis.Should().Be(SubjectPrefix + "flow-secret");
        results[1].Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);

        Decide(EgressFamilies.ModelMeai, Remote).CurrentBasis.Should().Be(NoSubject, "no frame flows back out to the caller");
    }

    [Fact]
    public async Task A_task_that_outlives_its_frame_falls_back_to_no_subject()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EgressDecision> child;

        using (EgressSubject.Enter("parent", new HighWaterMark()))
        {
            child = Task.Run(async () =>
            {
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
                return Decide("not-a-family", Remote);
            });
        }

        release.SetResult();
        var decision = await child.WaitAsync(TimeSpan.FromSeconds(30));

        decision.CurrentBasis.Should().Be(NoSubject, "a disposed frame is never the innermost live one, so a child with no frame of its own fails closed");
        decision.Current.Should().Be(SecurityLabel.SystemHigh);
    }

    [Fact]
    public async Task A_frame_disposed_from_another_flow_no_longer_names_this_flows_decisions()
    {
        var scope = EgressSubject.Enter("shared", new HighWaterMark());
        try
        {
            Decide("not-a-family", Remote).CurrentBasis.Should().Be(SubjectPrefix + "shared");

            await Task.Run(scope.Dispose).WaitAsync(TimeSpan.FromSeconds(30));

            Decide("not-a-family", Remote).CurrentBasis.Should().Be(NoSubject, "the frame is disposed, so it is no longer the innermost live one");
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void A_blank_subject_id_is_refused(string subjectId)
    {
        var act = () => EgressSubject.Enter(subjectId, new HighWaterMark());

        act.Should().Throw<ArgumentException>().WithParameterName("subjectId");
        Decide("not-a-family", Remote).CurrentBasis.Should().Be(NoSubject, "a refused Enter leaves no frame behind");
    }

    [Fact]
    public void A_null_subject_id_or_mark_is_refused()
    {
        ((Action)(() => EgressSubject.Enter(null!, new HighWaterMark())))
            .Should().Throw<ArgumentNullException>().WithParameterName("subjectId");
        ((Action)(() => EgressSubject.Enter("agent", null!)))
            .Should().Throw<ArgumentNullException>().WithParameterName("mark");
    }

    /// <summary>
    /// The most hostile subject the public API can build: an id of control, format, separator and surrogate
    /// characters far past any bound, a mark raised concurrently through compartments and caveats to SystemHigh,
    /// and the frame disposed from another flow while decisions are being made on several threads.
    /// </summary>
    /// <remarks>
    /// <see cref="HighWaterMark"/> is sealed and its <see cref="HighWaterMark.Current"/> cannot throw, so a subject
    /// whose label read throws cannot be constructed through the public API; this is the closest it gets.
    /// </remarks>
    [Fact]
    public async Task Evaluate_never_throws_for_a_hostile_subject()
    {
        var hostileId = "agent\u0000\u0007\r\n\u202E\u200B\u2028\uD800" + new string('x', 10_000);
        var mark = new HighWaterMark();
        var faults = new ConcurrentBag<string>();
        var thrown = new ConcurrentBag<Exception>();

        var scope = EgressSubject.Enter(hostileId, mark);
        try
        {
            var first = Decide(EgressFamilies.Http, Remote);
            first.Fault.Should().BeNull();
            first.CurrentBasis.Should().StartWith(SubjectPrefix + "agent");
            first.CurrentBasis.Length.Should().BeLessThan(hostileId.Length, "the subject id is bounded in a record");
            first.CurrentBasis.Should().EndWith("...");
            UnsafeCharacters(first.CurrentBasis).Should().BeEmpty("caller text cannot reshape a record");

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deciders = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
                for (var i = 0; i < 250; i++)
                {
                    try
                    {
                        var decision = Decide(i % 2 == 0 ? EgressFamilies.ModelMeai : "not-a-family", Remote);
                        if (decision.Fault is not null)
                            faults.Add(decision.Fault);
                    }
                    catch (Exception ex)
                    {
                        thrown.Add(ex);
                    }
                }
            })).ToArray();

            var observer = Task.Run(async () =>
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
                for (var i = 0; i < 200; i++)
                {
                    var level = (SecurityLevel)(i % 5);
                    mark.Observe(new SecurityLabel(level, ["C" + (i % 7).ToString(CultureInfo.InvariantCulture)], ["K" + (i % 3).ToString(CultureInfo.InvariantCulture)]));
                }

                mark.Observe(SecurityLabel.SystemHigh);
            });

            var disposer = Task.Run(async () =>
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await Task.Yield();
                scope.Dispose();
            });

            started.SetResult();
            await Task.WhenAll(deciders.Append(observer).Append(disposer)).WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            scope.Dispose();
        }

        thrown.Should().BeEmpty("Evaluate never throws");
        faults.Should().BeEmpty("nothing a subject can do is a fault");
        Decide(EgressFamilies.Http, Remote).CurrentBasis.Should().Be(NoSubject);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Redaction and bounding
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://twin-user:pa55word@api.example.com:8443/v1/PATHMARK?q=QUERYTOKEN#FRAGMARK", "https://api.example.com:8443")]
    [InlineData("https://api.example.com:443/PATHMARK", "https://api.example.com")]
    [InlineData("http://twin-user@127.0.0.1:11434/api/PATHMARK?token=QUERYTOKEN", "http://127.0.0.1:11434")]
    [InlineData("https://sns.example.com/?Action=Confirm&Token=QUERYTOKEN", "https://sns.example.com")]
    [InlineData("http://[::1]:5000/PATHMARK#FRAGMARK", "http://[::1]:5000")]
    [InlineData("https://twin-user:pa55word@search.example/bing?q=QUERYTOKEN", "https://search.example")]
    public void A_uri_destination_keeps_only_scheme_host_and_port(string uri, string expected)
    {
        var decision = Guard.Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), new Uri(uri)));

        decision.Destination.Should().Be(expected);
        AssertRedacted(decision.Destination);
    }

    /// <summary>
    /// A URL-shaped name gives <c>scheme://host[:port]</c> when its authority can be read without guessing, and
    /// <c>scheme://&lt;unparsed&gt;</c> when it cannot: an <c>@</c> after the authority and before any query or
    /// fragment, a host <see cref="Uri.CheckHostName"/> calls Unknown, or a port that is not all digits. The
    /// <c>bad host</c> row recorded <c>tcp://bad host</c> before R10; a host with a space is not a host, so the
    /// authority is no longer guessed at.
    /// </summary>
    [Theory]
    [InlineData("https://twin-user:pa55word@host.example/PATHMARK?x=QUERYTOKEN#FRAGMARK", "https://host.example")]
    [InlineData("https://u:p@h/p?q", "https://h")]
    [InlineData("tcp://twin-user:pa55word@bad host/PATHMARK?QUERYTOKEN#FRAGMARK", "tcp://<unparsed>")]
    [InlineData("tcp://bad host:7000", "tcp://<unparsed>")]
    [InlineData("tcp://twin-user:pa55word@good.example:99999/PATHMARK", "tcp://good.example:99999")]
    [InlineData("tcp://good.example:7x/PATHMARK", "tcp://<unparsed>")]
    [InlineData("ftp://user:p/ss@host/x", "ftp://<unparsed>")]
    [InlineData("amqp://svc:1234/x@broker", "amqp://<unparsed>")]
    [InlineData("https://search.example/PATHMARK?q=QUERYTOKEN@mail.example", "https://search.example")]
    [InlineData("aws-bedrock", "aws-bedrock")]
    [InlineData("host:dotnet", "host:dotnet")]
    [InlineData("", "unknown")]
    public void A_url_shaped_name_is_redacted_like_a_url(string name, string expected)
    {
        var decision = Guard.Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), name));

        decision.Destination.Should().Be(expected);
        AssertRedacted(decision.Destination);
    }

    /// <summary>
    /// R10. A credential that holds an unencoded <c>/</c>, <c>?</c>, <c>#</c> or <c>\</c> ends the authority early,
    /// so its head reads as <c>host[:port]</c>; before the fix that head reached the record. No piece of the
    /// credential may reach <see cref="EgressDecision.Destination"/> or any field of the event, and the record is
    /// outside the host boundary. <c>sequence</c> and <c>at</c> are a counter and a clock, which carry no caller text
    /// and could hold a digit run by chance, so they are the only fields not searched; the site has no digits.
    /// </summary>
    [Theory]
    [InlineData("ftp://twin-user:pa55/w0rdmark@host.example/x", "ftp://<unparsed>", "twin-user|pa55|w0rdmark")]
    [InlineData("s3://AKIATWINKEYMARK:wJalrTWINMARK/K7MDENG/bPxRfiCYTWINKEY@bucket", "s3://<unparsed>", "AKIATWINKEYMARK|wJalrTWINMARK|K7MDENG|bPxRfiCYTWINKEY")]
    [InlineData("amqp://twin-user:pa55?w0rdmark@broker:5672", "amqp://<unparsed>", "twin-user|pa55|w0rdmark")]
    [InlineData("amqp://twin-user:pa55#w0rdmark@broker:5672", "amqp://<unparsed>", "twin-user|pa55|w0rdmark")]
    [InlineData(@"amqp://twin-user:pa55\w0rdmark@broker:5672", "amqp://<unparsed>", "twin-user|pa55|w0rdmark")]
    [InlineData(@"http://twin-user:4321\w0rdmark@host.example/", "http://<unparsed>", "twin-user|4321|w0rdmark")]
    [InlineData("amqp://twin-user:4321/w0rdmark@broker", "amqp://<unparsed>", "twin-user|4321|w0rdmark")]
    [InlineData("tcp://twin-user:pa55/w0rdmark@127.0.0.1:7000", "tcp://<unparsed>", "twin-user|pa55|w0rdmark")]
    [InlineData("tcp://twin-user:pa55w0rdmark@bad host:7000/x", "tcp://<unparsed>", "twin-user|pa55w0rdmark")]
    [InlineData("https://twin-user:pa55w0rdmark@host.example/PATHMARK?QUERYTOKEN", "https://host.example", "twin-user|pa55w0rdmark|PATHMARK|QUERYTOKEN")]
    public void A_credential_holding_a_delimiter_reaches_no_record_field(string name, string expected, string credential)
    {
        var pieces = credential.Split('|');

        // Make sure the source exists before the listener, so the listener is told about it at construction.
        Guard.Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), new Uri(Remote)));

        var site = NewSiteWithoutDigits();
        using var listener = new EgressListener();
        var decision = Guard.Evaluate(new EgressRequest(EgressFamilies.Http, site, name));

        decision.Fault.Should().BeNull();
        decision.Destination.Should().Be(expected);
        decision.Destination.Should().NotContainAny(pieces);
        AssertRedacted(decision.Destination);
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport, "none of these names is inside the host boundary");

        var written = listener.Events.Where(e => e.EventId == 1 && Field(e, "site") as string == site).ToList();
        written.Should().ContainSingle("each decision is written once");
        var e = written[0];
        Field(e, "destination").Should().Be(expected);
        var names = e.PayloadNames!;
        var payload = e.Payload!;
        names.Should().HaveCount(payload.Count).And.Contain("destination").And.Contain("detail");
        for (var i = 0; i < payload.Count; i++)
        {
            // sequence/at are counters/timestamps. ref is a random 64-bit hex id (see EgressDecision.Ref) —
            // not derived from the destination — so a digit-only credential piece can collide by chance.
            if (names[i] is "sequence" or "at" or "ref")
                continue;
            Convert.ToString(payload[i], CultureInfo.InvariantCulture).Should().NotContainAny(
                pieces, "the {0} field must carry no piece of the credential in {1}", names[i], name);
        }
    }

    [Fact]
    public void Caller_text_is_bounded_and_cannot_reshape_a_record()
    {
        var decision = Guard.Evaluate(new EgressRequest("http\r\n", "twin:\u0007site\u202E\u2028", new Uri(Remote)));

        decision.Family.Should().Be("http??");
        decision.Site.Should().Be("twin:?site??");
        decision.DestinationClass.Should().Be(EgressDestinationClass.Unknown, "the family is matched as given, ordinally");

        var longName = new string('a', 1000);
        var bounded = Guard.Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), longName)).Destination;
        bounded.Length.Should().BeLessThan(longName.Length);
        bounded.Should().EndWith("...");
        longName.Should().StartWith(bounded[..^3]);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Faults
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>A relative URI names no host, so the classifier throws inside <see cref="EgressGuard.Evaluate"/>.</summary>
    [Fact]
    public void A_classifier_that_throws_gives_a_Fault_and_NoDecision_without_throwing()
    {
        var site = NewSite();
        var sink = new SiteSink(site);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var relative = new Uri("/v1/PATHMARK?token=QUERYTOKEN#FRAGMARK", UriKind.Relative);

        EgressDecision? decision = null;
        var act = () => decision = Guard.Evaluate(new EgressRequest(EgressFamilies.Http, site, relative));

        act.Should().NotThrow();
        AssertFaulted(decision!, typeof(ArgumentException));
        decision!.Family.Should().Be(EgressFamilies.Http, "stages that completed before the fault keep their fields");
        decision.Site.Should().Be(site);
        decision.Profile.Should().Be("full");
        decision.Destination.Should().Be("unknown");
        AssertRedacted(decision.Destination);
        sink.Seen.Should().ContainSingle("a faulted record is still published").Which.Should().BeSameAs(decision);
    }

    [Fact]
    public void A_null_request_gives_a_Fault_and_NoDecision_without_throwing()
    {
        var sink = new PredicateSink(d => d.Fault is not null);
        using var subscription = EgressDecisionLog.Subscribe(sink);

        EgressDecision? decision = null;
        var act = () => decision = Guard.Evaluate(null!);

        act.Should().NotThrow();
        AssertFaulted(decision!, typeof(ArgumentNullException));
        sink.Seen.Should().Contain(decision!, "a faulted record is still published");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Profile
    // ---------------------------------------------------------------------------------------------------------

    /// <remarks>SPEC-007 PR 4.6: every one of the six profiles still reports. A profile that is none of them fails
    /// closed to <c>enforce</c> (the mode table is pinned in <see cref="EgressModeResolutionTests"/>); nothing acts on the
    /// mode yet, so the access decision is unchanged either way.</remarks>
    [Theory]
    [InlineData("air-gapped", true, "report")]
    [InlineData("secure-workstation", true, "report")]
    [InlineData("AirGapped", true, "report")]
    [InlineData("SECURE_WORKSTATION", true, "report")]
    [InlineData("workstation", true, "report")]
    [InlineData("full", false, "report")]
    [InlineData("server", false, "report")]
    [InlineData("edge", false, "report")]
    [InlineData("system", false, "report")]
    [InlineData("", false, "report")]
    [InlineData("air-gapped-ish", false, "enforce")]
    public void An_explicit_profile_is_reported_and_does_not_change_the_decision(string profile, bool enforcesByDefault, string mode)
    {
        var request = new EgressRequest(EgressFamilies.ModelMeai, NewSite(), new Uri(Remote));

        var decision = new EgressGuard(profile).Evaluate(request);

        decision.Profile.Should().Be(profile);
        decision.ProfileEnforcesByDefault.Should().Be(enforcesByDefault);
        decision.Mode.Should().Be(mode, "every profile defaults to report until PR 4.11; an unreadable one fails closed");
        decision.Access.Should().Be(Guard.Evaluate(request).Access, "the profile is reported, not enforced");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The record and where it goes
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_record_carries_the_request_and_a_process_wide_sequence()
    {
        var site = NewSite();
        var before = DateTimeOffset.UtcNow;

        var first = Guard.Evaluate(new EgressRequest(EgressFamilies.Mcp, site, new Uri(Remote)));
        var second = new EgressGuard("air-gapped").Evaluate(new EgressRequest(EgressFamilies.Mcp, site, new Uri(Remote)));

        var after = DateTimeOffset.UtcNow;
        first.Mode.Should().Be("report");
        first.Family.Should().Be(EgressFamilies.Mcp);
        first.Site.Should().Be(site);
        first.Fault.Should().BeNull();
        first.At.Offset.Should().Be(TimeSpan.Zero);
        first.At.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        second.Sequence.Should().BeGreaterThan(first.Sequence, "the sequence is shared by every guard in the process");
    }

    [Fact]
    public void Every_decision_reaches_each_subscriber_once_as_the_returned_instance()
    {
        var site = NewSite();
        var sink = new SiteSink(site);
        var subscription = EgressDecisionLog.Subscribe(sink);

        var decision = Guard.Evaluate(new EgressRequest(EgressFamilies.Http, site, new Uri(Remote)));
        sink.Seen.Should().ContainSingle().Which.Should().BeSameAs(decision);

        subscription.Dispose();
        subscription.Dispose();
        Guard.Evaluate(new EgressRequest(EgressFamilies.Http, site, new Uri(Remote)));
        sink.Seen.Should().HaveCount(1, "a disposed subscription receives nothing, and disposing twice is harmless");

        using (EgressDecisionLog.Subscribe(sink))
        using (EgressDecisionLog.Subscribe(sink))
        {
            Guard.Evaluate(new EgressRequest(EgressFamilies.Http, site, new Uri(Remote)));
        }

        sink.Seen.Should().HaveCount(3, "subscribing the same sink twice delivers each record twice");

        ((Action)(() => EgressDecisionLog.Subscribe(null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_throwing_subscriber_reaches_neither_the_caller_nor_the_other_subscribers()
    {
        var site = NewSite();
        var throwing = new ThrowingSink(site);
        var behind = new SiteSink(site);
        using var first = EgressDecisionLog.Subscribe(throwing);
        using var second = EgressDecisionLog.Subscribe(behind);
        var faultsBefore = InternalCounter(typeof(EgressDecisionLog), "SinkFaults");

        EgressDecision? decision = null;
        var act = () => decision = Guard.Evaluate(new EgressRequest(EgressFamilies.Http, site, new Uri(Remote)));

        act.Should().NotThrow();
        throwing.Calls.Should().Be(1);
        behind.Seen.Should().ContainSingle("a later subscriber still receives the record").Which.Should().BeSameAs(decision);
        InternalCounter(typeof(EgressDecisionLog), "SinkFaults").Should().BeGreaterThan(faultsBefore, "a sink fault is counted");
    }

    [Fact]
    public void A_subscriber_that_causes_an_egress_does_not_recurse()
    {
        var site = NewSite();
        var nestedSite = NewSite();
        var sink = new ReentrantSink(site, nestedSite);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var skipsBefore = InternalCounter(typeof(EgressDecisionLog), "ReentrantSkips");

        Guard.Evaluate(new EgressRequest(EgressFamilies.Http, site, new Uri(Remote)));

        sink.Outer.Should().Be(1);
        sink.NestedReturned.Should().NotBeNull("the nested decision is still returned to the sink that asked");
        sink.NestedReturned!.Site.Should().Be(nestedSite);
        sink.NestedReturned.Fault.Should().BeNull();
        sink.Nested.Should().Be(0, "a decision made while publishing on the same thread is not published again");
        InternalCounter(typeof(EgressDecisionLog), "ReentrantSkips").Should().BeGreaterThan(skipsBefore);

        Guard.Evaluate(new EgressRequest(EgressFamilies.Http, nestedSite, new Uri(Remote)));
        sink.Nested.Should().Be(1, "outside a publish the same site is published as usual");
    }

    [Fact]
    public void The_event_source_writes_each_decision_as_event_Decision_from_Ashlar_Egress()
    {
        // Make sure the source exists before the listener, so the listener is told about it at construction.
        Guard.Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), new Uri(Remote)));

        var site = NewSite();
        using var listener = new EgressListener();
        var decision = Guard.Evaluate(new EgressRequest(
            EgressFamilies.ModelMeai,
            site,
            new Uri("https://twin-user:pa55word@es.example:8443/v1/PATHMARK?q=QUERYTOKEN#FRAGMARK")));

        var written = listener.Events.Where(e => e.EventId == 1 && Field(e, "site") as string == site).ToList();
        written.Should().ContainSingle("each decision is written once");
        var e = written[0];

        e.EventSource.Name.Should().Be(SourceName);
        e.EventName.Should().Be("Decision");
        e.Level.Should().Be(EventLevel.Informational);
        e.PayloadNames.Should().Equal(
            "sequence", "at", "mode", "family", "site", "destination", "destinationClass", "destinationLabel",
            "destinationBasis", "current", "currentBasis", "allowed", "reason", "detail", "profile",
            "profileEnforcesByDefault", "fault", "modeBasis", "refused", "ref");
        e.Payload.Should().OnlyContain(value => value is string || value is bool, "every field is a string or a bool");

        Field(e, "sequence").Should().Be(decision.Sequence.ToString(CultureInfo.InvariantCulture));
        Field(e, "mode").Should().Be("report");
        Field(e, "family").Should().Be(EgressFamilies.ModelMeai);
        Field(e, "destination").Should().Be("https://es.example:8443");
        Field(e, "destinationClass").Should().Be("ExternalModel");
        Field(e, "destinationLabel").Should().Be("Internal");
        Field(e, "destinationBasis").Should().Be(ExternalModelBasis);
        Field(e, "current").Should().Be("SystemHigh");
        Field(e, "currentBasis").Should().Be(NoSubject);
        Field(e, "allowed").Should().Be(false);
        Field(e, "reason").Should().Be("SystemHighData");
        Field(e, "detail").Should().Be(decision.Access.Detail);
        Field(e, "profile").Should().Be("full");
        Field(e, "profileEnforcesByDefault").Should().Be(false);
        Field(e, "fault").Should().Be(string.Empty);
        Field(e, "modeBasis").Should().Be("profile:full");
        Field(e, "refused").Should().Be(false, "report mode refuses nothing");
        Field(e, "ref").Should().Be(decision.Ref);

        var everything = string.Join("|", e.Payload!.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));
        everything.Should().NotContainAny(SecretMarkers);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>A site string no other test uses, so records published process-wide can be told apart.</summary>
    private static string NewSite() => "twin:decision:" + Guid.NewGuid().ToString("N");

    /// <summary><see cref="NewSite"/> with each digit mapped to a letter, for a test that searches records for digits.</summary>
    private static string NewSiteWithoutDigits() =>
        "twin:decision:" + new string(Guid.NewGuid().ToString("N").Select(c => c is >= '0' and <= '9' ? (char)('g' + (c - '0')) : c).ToArray());

    private static EgressDecision Decide(string family, string uri) =>
        Guard.Evaluate(new EgressRequest(family, NewSite(), new Uri(uri)));

    private static void AssertRow(EgressDecision decision, EgressDestinationClass expected, string what)
    {
        decision.Fault.Should().BeNull(what);
        decision.DestinationClass.Should().Be(expected, what);
        decision.DestinationLabel.Should().Be(LabelOf(expected), what);
        decision.DestinationBasis.Should().Be(BasisOf(expected), what);
        AssertRedacted(decision.Destination);
    }

    private static SecurityLabel LabelOf(EgressDestinationClass destinationClass) => destinationClass switch
    {
        EgressDestinationClass.Host => SecurityLabel.SystemHigh,
        EgressDestinationClass.ExternalModel => InternalLabel,
        EgressDestinationClass.WebSearch => ConfidentialLabel,
        EgressDestinationClass.NetworkExport => InternalLabel,
        _ => SecurityLabel.Public,
    };

    private static string BasisOf(EgressDestinationClass destinationClass) => destinationClass switch
    {
        EgressDestinationClass.Host => HostBasis,
        EgressDestinationClass.ExternalModel => ExternalModelBasis,
        EgressDestinationClass.WebSearch => WebSearchBasis,
        EgressDestinationClass.NetworkExport => NetworkExportBasis,
        _ => UnknownBasis,
    };

    /// <summary><c>scheme://host[:port]</c> or a name: no userinfo, path, query or fragment.</summary>
    private static void AssertRedacted(string destination)
    {
        destination.Should().NotContainAny(SecretMarkers);
        destination.Should().NotContainAny("?", "#", "@");
        var separator = destination.IndexOf("://", StringComparison.Ordinal);
        if (separator >= 0)
            destination[(separator + 3)..].Should().NotContain("/", "a record keeps no path: {0}", destination);
    }

    private static void AssertFaulted(EgressDecision decision, Type exceptionType)
    {
        decision.Should().NotBeNull();
        decision.Fault.Should().Be(exceptionType.FullName, "only the exception's type name is recorded");
        decision.Access.Should().Be(default(AccessDecision));
        decision.Access.Allowed.Should().BeFalse("NoDecision reads as refused");
        decision.Access.Reason.Should().Be(AccessDenialReason.NoDecision);
        decision.Access.Detail.Should().NotBeNullOrWhiteSpace();
        decision.Mode.Should().Be("report");
        decision.DestinationClass.Should().Be(EgressDestinationClass.Unknown, "an unclassified destination fails closed to the bottom");
        decision.DestinationLabel.Should().Be(SecurityLabel.Public);
        decision.Current.Should().Be(SecurityLabel.SystemHigh, "an unresolved current label fails closed to the top");
        decision.Fault.Should().NotContainAny(SecretMarkers);
    }

    private static IEnumerable<char> UnsafeCharacters(string text) =>
        text.Where(c => char.IsControl(c)
            || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate);

    /// <summary>
    /// An internal counter of the core, by reflection: this assembly is not in Ashlar.Abstractions'
    /// InternalsVisibleTo. Counters only rise, and other classes run in parallel, so callers compare "rose".
    /// </summary>
    private static long InternalCounter(Type type, string name)
    {
        var property = type.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Static);
        property.Should().NotBeNull("{0}.{1} is where the core counts swallowed faults", type.Name, name);
        return Convert.ToInt64(property!.GetValue(null), CultureInfo.InvariantCulture);
    }

    private static object? Field(EventWrittenEventArgs e, string name)
    {
        var index = e.PayloadNames?.IndexOf(name) ?? -1;
        return index < 0 || e.Payload is null || index >= e.Payload.Count ? null : e.Payload[index];
    }

    private sealed class SiteSink(string site) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _seen = new();

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        public void Record(EgressDecision decision)
        {
            if (string.Equals(decision.Site, site, StringComparison.Ordinal))
                _seen.Enqueue(decision);
        }
    }

    private sealed class PredicateSink(Func<EgressDecision, bool> keep) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _seen = new();

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        public void Record(EgressDecision decision)
        {
            if (keep(decision))
                _seen.Enqueue(decision);
        }
    }

    private sealed class ThrowingSink(string site) : IEgressDecisionSink
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Record(EgressDecision decision)
        {
            if (!string.Equals(decision.Site, site, StringComparison.Ordinal))
                return;

            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("a subscriber failed");
        }
    }

    /// <summary>
    /// Makes one nested decision when it records <c>site</c>, and only counts <c>nestedSite</c>, so even without the
    /// re-entrancy guard the recursion is one level deep and a regression fails an assertion instead of the process.
    /// </summary>
    private sealed class ReentrantSink(string site, string nestedSite) : IEgressDecisionSink
    {
        private int _outer;
        private int _nested;

        public int Outer => Volatile.Read(ref _outer);

        public int Nested => Volatile.Read(ref _nested);

        public EgressDecision? NestedReturned { get; private set; }

        public void Record(EgressDecision decision)
        {
            if (string.Equals(decision.Site, nestedSite, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _nested);
                return;
            }

            if (!string.Equals(decision.Site, site, StringComparison.Ordinal))
                return;

            Interlocked.Increment(ref _outer);
            NestedReturned = Guard.Evaluate(new EgressRequest(EgressFamilies.Http, nestedSite, new Uri(Remote)));
        }
    }

    private sealed class EgressListener : EventListener
    {
        // A field initializer runs before the base constructor, which may already call OnEventSourceCreated.
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (string.Equals(eventSource.Name, SourceName, StringComparison.Ordinal))
                EnableEvents(eventSource, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (string.Equals(eventData.EventSource.Name, SourceName, StringComparison.Ordinal))
                _events.Enqueue(eventData);
        }
    }
}
