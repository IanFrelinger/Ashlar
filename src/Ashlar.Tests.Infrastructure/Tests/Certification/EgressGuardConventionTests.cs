using System.Text.RegularExpressions;
using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;
using Xunit.Abstractions;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Every outbound path from production code is listed in <c>ci/egress-inventory.tsv</c> and
/// <c>docs/EgressInventory.md</c>, pinned per file and marker, and a new one cannot appear unlisted. SPEC-007
/// PR 3a ships the egress guard report-only and routes nothing yet, so most rows read <c>Unrouted</c>; PR 3b
/// routes them through <c>EgressHttp</c>, the factory defaults, governance or an explicit guard call, and
/// converts every <c>Unrouted</c> row in the same diff.
///
/// <para><b>A tripwire, not a proof.</b> The scan is textual (no Roslyn in the required check). It sees the
/// tokens listed below in production source with comments and literal contents blanked, and nothing else.
/// Green means "no new token of a taught shape appeared unlisted", never "nothing leaves the process". The
/// behavioural twins (<c>EgressGuardDecisionTests</c>, <c>EgressHttpHandlerTwinTests</c>,
/// <c>EgressFactoryDefaultsTwinTests</c>) are what prove the guard decides and records.</para>
///
/// <para><b>Population.</b> Every <c>.cs</c> under src, application, applications, commercial, products,
/// tools and consumer-template, minus bin, obj, .claude and every dot-directory; minus any directory holding a
/// <c>.git</c> file or directory (a worktree or nested clone); minus TEST PROJECTS, identified by their csproj
/// (<c>Microsoft.NET.Test.Sdk</c> or <c>&lt;IsTestProject&gt;true&lt;</c>), never by name — a name rule once hid
/// 59 production files, and a substring filter in this test's design pass dropped
/// <c>RemoteExecutionPlatform.cs</c>. <c>spikes/</c> and <c>samples/</c> are excluded by name: not shipped,
/// outside Ashlar.sln. Text is cleaned by <c>PathContainmentConventionTests.Scanner.Clean</c>, copied
/// verbatim, so a comment or a code template (<c>MockScaffoldingResponder.Templates.cs</c>) never counts.</para>
///
/// <para><b>Markers</b> (the TSV's <c>marker</c> column; one occurrence per token):</para>
/// <list type="bullet">
///   <item><c>http.new</c>: <c>new [global::][System.Net.Http.]HttpClient</c>, <c>HttpClientHandler</c>,
///   <c>SocketsHttpHandler</c>, <c>WinHttpHandler</c> then '(' or '{' (multi-line); <c>new HttpMessageInvoker(</c>;
///   target-typed <c>HttpClient[?] id = new(</c> (and the handler types). Guarded forms, counted too:
///   <c>EgressHttp.CreateClient(</c> and <c>EgressHttp.Wrap(</c> are each one Wrapped occurrence; a raw
///   construction inside their argument region, or stored in a local that a LATER one names (G2), is Wrapped.</item>
///   <item><c>http.param</c>: <c>HttpClient[?] id</c> as a constructor, method, local-function or lambda
///   parameter. A field is not a parameter. No guarded form.</item>
///   <item><c>http.register</c>: <c>.AddHttpClient(</c>/<c>.AddHttpClient&lt;</c>; Factory when the same member
///   (header included; a top-level program is one member) calls <c>AddAshlarEgressGuard(</c>.</item>
///   <item><c>sdk.client</c>: <c>GrpcChannel.ForAddress(</c>, <c>new HttpClientTransport(</c>,
///   <c>new SseClientTransport(</c>, <c>McpClient.CreateAsync(</c>, <c>new A2AClient(</c>,
///   <c>new A2ACardResolver(</c>, <c>new Amazon…Client(</c>, <c>.AsIChatClient(</c>. G2-file: Wrapped when the
///   argument region passes a client or handler (an <c>EgressHttp.</c> call, an <c>HttpHandler =</c> or
///   <c>HttpClient =</c> property, or at least 2 depth-0 arguments for A2A and 3 for <c>HttpClientTransport</c>)
///   AND the file has no unguarded http.new. <c>McpClient.CreateAsync(</c> takes a transport, not a client, so it
///   passes when every MCP transport constructed in its file passes.</item>
///   <item><c>socket</c>: <c>new UdpClient(</c>, <c>TcpClient(</c>, <c>Socket(</c>, <c>ClientWebSocket(</c>,
///   <c>Dns.GetHostAddresses[Async](</c>, <c>Dns.GetHostEntry[Async](</c>. G3.</item>
///   <item><c>process</c>: <c>new [System.Diagnostics.]ProcessStartInfo</c> then '(' or '{';
///   <c>Process.Start(</c>; <c>new Process</c> then '(' or '{'. G3.</item>
///   <item><c>door</c>: <c>MeshStore.Publish(</c>, <c>ExtensionPackaging.Pack(</c>, <c>Results.Stream(</c>,
///   <c>Results.File(</c>, G3; plus the declarations of <c>NativeBundle.StageApp</c>,
///   <c>SneakernetTransport.ExportAsync</c> and <c>FileBasedSharedAdaptationStore.BroadcastAsync</c>, whose
///   body block must itself hold a guard call.</item>
///   <item><c>telemetry</c>: <c>.AddOtlpExporter(</c>. G3.</item>
///   <item><c>store</c>: <c>new NpgsqlConnection(</c>, <c>new DockerClientConfiguration(</c>. Exempt rows only.</item>
///   <item><c>chat.register</c>: <c>.AddKeyedChatClient(</c>, <c>.AddChatClient(</c>,
///   <c>.AddEmbeddingGenerator(</c>, <c>.AddKeyedEmbeddingGenerator(</c>, <c>new OllamaHttpChatClient(</c>,
///   <c>new LlamaSharpChatClient(</c>. Held structurally by F5.</item>
///   <item><c>banned</c>: <c>[Try]Add…&lt;HttpClient&gt;</c> and <c>typeof(HttpClient)</c>. Never pinned; F4 holds it at zero.</item>
/// </list>
///
/// <para><b>G3</b>, the explicit route: a guard call <c>….Evaluate(new EgressRequest(</c> counts for a
/// primitive when it sits at a LOWER offset, its innermost enclosing block is the primitive's block or an
/// ancestor of it, and that block is not a namespace or type body (the file root counts only in a top-level
/// program). A guard in a sibling member, in a field initializer, inside a nested <c>if</c>, or after the
/// primitive never counts: the laundering lesson of <c>UnstableHashKeyConventionTests</c>, where one override
/// exempted an unrelated call below it.</para>
///
/// <para><b>Pins.</b> One TSV row per observed (path, marker): <c>path marker total guarded guarded_by
/// unguarded_reason ids note</c>, header lines starting with '#', as <c>ci/certifier-boundary-inventory.tsv</c>.
/// Guarded forms are pinned too, so converting a site never deletes its row and the inventory stays complete.
/// <c>guarded_by</c> is Wrapped, Precedes, Factory or <c>-</c>. In 3a <c>unguarded_reason</c> is
/// <c>Unrouted</c>, one of the closed <c>Exempt:</c> set (LocalOnly, Operator, Inbound, DataStore, LocalDaemon,
/// ConsumerSdk, TestDouble, TestSeam), <c>Exempt:GuardImpl</c> under <c>src/Ashlar.Abstractions/Security/Egress/</c>
/// only, or <c>-</c> when nothing is unguarded. Governance, Factory and <c>Upstream:&lt;path&gt;</c> are 3b
/// reasons. A row mixing a routed-later site with an exempt one (the mesh beacon's sender and listener) is
/// <c>Unrouted</c> until 3b routes the sender. <c>ids</c> are <c>EG-…</c> rows of <c>docs/EgressInventory.md</c>.
/// F1, F2 and F3 print the full observed inventory in TSV form when they fail, keeping each pinned row's
/// reason, ids and note: paste it over the data rows and review each <c>?</c>.</para>
///
/// <para><b>The guard's own constructions: option (b).</b> <c>EgressHttp.cs</c> has to build a raw client and
/// handler to hand anyone a guarded one. Those are pinned as <c>Exempt:GuardImpl</c>, a reason F3 accepts only
/// for files under <c>src/Ashlar.Abstractions/Security/Egress/</c>. Option (a), widening G2 to "inside
/// <c>new EgressGuardHandler(</c>", was not taken: it depends on how the guard core spells its composition
/// (<c>new HttpClient(new EgressGuardHandler(…))</c> puts the client OUTSIDE that region), and an exemption that
/// is a fixed folder cannot drift with the implementation.</para>
///
/// <para><b>What it cannot see</b>, stated so nobody mistakes green for proof: HTTP clients built inside
/// third-party SDKs (the AWS SDK, the MCP SDK's own default client); inbound server responses (not scanned:
/// <c>Unscanned:Inbound</c>); file egress through no door method (<c>File.Copy</c> to removable media);
/// aliases (<c>using H = System.Net.Http.HttpClient</c>), constructions inside interpolation holes (Clean
/// blanks them), and reflection; an executable chosen by a caller of a process funnel
/// (<c>TimedProcess.RunAsync("curl", …)</c>); and order: G3 is TEXTUAL order, not temporal, so a lambda can
/// defer the send past the guard (OTLP's decision at registration is intentional). Package-level
/// classification is deferred: a new network SDK is caught only once its constructor is taught here. Each
/// known miss is a pinned control in F9, so the change that teaches the scan must flip it.</para>
///
/// <para><b>Not in 3a.</b> F4's clause "<c>AddAshlar</c> calls <c>AddAshlarEgressGuard</c>"; F5's
/// <c>EgressGuardChatClient</c> ordering assertion; the 3b reasons and their F3 checks (Factory: the receiving
/// type is registered by <c>AddHttpClient&lt;…, T&gt;</c> in a member that installs the guard; Upstream: the
/// named file exists, its http.new is all guarded and it alone constructs the receiving type; Governance only
/// for the leaves in <c>OllamaHttpChatClient.cs</c> and <c>AwsBedrockChatClientFactory.cs</c>).</para>
///
/// <para>Hermetic: pure file reads. Each fact names the file:line of every offending occurrence.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed partial class EgressGuardConventionTests
{
    /// <summary>
    /// The F4 binding call: the ONE place every <c>IHttpClientFactory</c> client gets the guard handler. A
    /// token ending in '(' is checked through its argument region; any other token (an
    /// <c>IHttpMessageHandlerBuilderFilter</c> registration, say) through its whole file.
    /// </summary>
    private const string HttpDefaultsBindingToken = "ConfigureHttpClientDefaults(";

    private const string HttpDefaultsBindingFile = "src/Ashlar.Infrastructure/Egress/EgressServiceCollectionExtensions.cs";

    private const string HttpDefaultsHandlerToken = "EgressHttp.CreateDelegatingHandler(";

    /// <summary>
    /// Non-vacuity floors, examined occurrences counting guarded and unguarded together so converting sites
    /// never trips them. Measured in the devtest container at master 40852ac with this test added, before the
    /// guard files land: 2,111 production .cs files scanned and 143 occurrences examined (http.new 13,
    /// http.param 22, http.register 12, sdk.client 9, socket 2, process 51, door 11, telemetry 2, store 14,
    /// chat.register 7, banned 0). The guard core adds 3 (<c>EgressHttp.cs</c>). Set far enough below to
    /// survive ordinary deletions; a scan that stops reading the tree falls through them. Re-measure and
    /// restate at the PR commit.
    /// </summary>
    private const int ScannedFilesFloor = 1000;

    private const int ExaminedOccurrencesFloor = 60;

    private const string MeaiRegistrationFile = "src/Ashlar.AI.Pipeline/MeaiPipelineServiceCollectionExtensions.cs";

    private const string GovernanceFile = "src/Ashlar.AI.Pipeline/Governance/AshlarGovernanceChatClientBuilderExtensions.cs";

    private const string BedrockFactoryFile = "src/Ashlar.AI.Pipeline/Clients/AwsBedrockChatClientFactory.cs";

    /// <summary>The governed keyed chat registrations today: :105 ollama, :110 onnx, :172 AddAshlarGovernedChatClient, :244 each Bedrock tier.</summary>
    private const int KeyedChatClientStatements = 4;

    /// <summary>The files the guard is made of. Each must be in the scanned population, or pruning broke.</summary>
    private static readonly string[] GuardFiles =
    [
        "src/Ashlar.Abstractions/Security/Egress/EgressHttp.cs",
        "src/Ashlar.Abstractions/Security/Egress/EgressGuard.cs",
        HttpDefaultsBindingFile,
        GovernanceFile,
    ];

    /// <summary>Each must be scanned and contribute at least one examined occurrence of its marker.</summary>
    private static readonly (string Marker, string Path)[] Anchors =
    [
        (Marker.HttpNew, "src/Ashlar.Infrastructure/Execution/ProviderFactory.cs"),
        (Marker.HttpParam, "src/Ashlar.Infrastructure/Execution/Routing/RunPodHttpClient.cs"),
        (Marker.HttpRegister, "src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs"),
        (Marker.SdkClient, "src/Ashlar.Transport.Grpc/DefaultGrpcChannelFactory.cs"),
        (Marker.SdkClient, "src/Ashlar.Mcp.Client/McpClientConnectionManager.cs"),
        (Marker.SdkClient, "src/Ashlar.Transport.A2A/A2AAgentTransport.cs"),
        (Marker.SdkClient, BedrockFactoryFile),
        (Marker.Socket, "application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshDiscoveryService.cs"),
        (Marker.Process, "src/Ashlar.Tools.Dev/DotnetRunner.cs"),
        (Marker.Process, "src/Ashlar.Infrastructure/HostProcess/TimedProcess.cs"),
        (Marker.Door, "application/src/Ashlar.CLI/Commands/PkgCommand.cs"),
        (Marker.Door, "src/Ashlar.BackgroundAgents.HostRunners/SelfExtendAdmissionBridge.cs"),
        (Marker.Door, "application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshServeService.cs"),
        (Marker.Telemetry, "application/src/Ashlar.API/Program.cs"),
        (Marker.Store, "src/Ashlar.Infrastructure/Persistence/PostgresDatabaseProvisioner.cs"),
        (Marker.ChatRegister, MeaiRegistrationFile),
    ];

    private static readonly Lazy<TreeScan> Tree = new(() => TreeScan.Load(RepoPathResolver.FindRepoRoot()));

    private static readonly Lazy<PinFile> Pins = new(() => ReadPins(RepoPathResolver.FindRepoRoot()));

    private readonly ITestOutputHelper _output;

    public EgressGuardConventionTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// F1, over: the "a new outbound path bypasses the guard" assertion. For every observed (path, marker),
    /// neither the total nor the unguarded count may exceed its pin; an unlisted pair is pinned at zero.
    /// </summary>
    [Fact]
    public void F1_no_file_has_more_outbound_paths_than_its_pin()
    {
        var scan = Tree.Value;
        var pins = Pins.Value;
        var byKey = pins.Rows.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var observed = Observed(scan);

        var over = new List<string>();
        foreach (var row in observed)
        {
            byKey.TryGetValue(row.Key, out var pin);
            var pinnedTotal = pin?.Total ?? 0;
            var pinnedUnguarded = pin?.Unguarded ?? 0;
            if (row.Total > pinnedTotal || row.Unguarded > pinnedUnguarded)
            {
                over.Add($"{row.Path} [{row.Marker}]: {row.Total} found ({row.Unguarded} unguarded), pinned at {pinnedTotal} "
                    + $"({pinnedUnguarded} unguarded){(pin is null ? ", NOT LISTED" : string.Empty)}\n"
                    + string.Join("\n", row.Occurrences.Select(o => "    " + o)));
            }
        }

        over.Should().BeEmpty(
            "a new outbound path must go through the egress guard and be written down. Route it: build the client "
            + "with EgressHttp.CreateClient/Wrap, take it from IHttpClientFactory in a member that calls "
            + "AddAshlarEgressGuard, or call guard.Evaluate(new EgressRequest(family, \"EG-…\", destination)) "
            + "before the primitive in the same member. Then add its row to docs/EgressInventory.md and pin it in "
            + "{0}. Over the pin:\n{1}\n{2}",
            InventoryRelativePath, string.Join("\n", over), RenderObserved(observed, pins.Rows));
    }

    /// <summary>
    /// F2, stale: every row equals the observed (total, guarded) exactly, so the pins move with the code in
    /// both directions. A row for a pair with no occurrence is a ghost and fails; so does a duplicate row.
    /// </summary>
    [Fact]
    public void F2_every_pin_equals_what_the_scan_sees()
    {
        var scan = Tree.Value;
        var pins = Pins.Value;
        var observed = Observed(scan);
        var byKey = observed.ToDictionary(r => r.Key, StringComparer.Ordinal);

        var stale = new List<string>();
        if (pins.Rows.Count == 0)
            stale.Add($"{InventoryRelativePath} pins nothing ({string.Join("; ", pins.Errors)})");

        foreach (var duplicate in pins.Rows.GroupBy(p => p.Key).Where(g => g.Count() > 1))
            stale.Add($"{duplicate.Key.Replace('\t', ' ')} is pinned on lines {string.Join(", ", duplicate.Select(p => p.Line))}");

        foreach (var pin in pins.Rows)
        {
            var where = $"{InventoryRelativePath}:{pin.Line} {pin.Path} [{pin.Marker}]";
            if (!byKey.TryGetValue(pin.Key, out var row))
            {
                stale.Add($"{where}: pinned at total {pin.Total}, guarded {pin.Guarded}, but the scan sees no occurrence; delete the row");
                continue;
            }

            if (row.Total != pin.Total || row.Guarded != pin.Guarded)
            {
                stale.Add($"{where}: pinned at total {pin.Total}, guarded {pin.Guarded}; observed total {row.Total}, guarded {row.Guarded}\n"
                    + string.Join("\n", row.Occurrences.Select(o => "    " + o)));
            }
        }

        stale.Should().BeEmpty(
            "a pin that no longer matches its file overstates or understates what is listed, and leaves room for a "
            + "new path to arrive unnoticed. Update the row with the code, in the same diff. Stale:\n{0}\n{1}",
            string.Join("\n", stale), RenderObserved(observed, pins.Rows));
    }

    /// <summary>
    /// F3, routes hold: every row is well formed, its <c>guarded_by</c> is the kind the classifier proved (G2
    /// for Wrapped, G3 for Precedes, the member rule for Factory), and its reason is in the 3a closed set.
    /// </summary>
    [Fact]
    public void F3_every_row_is_well_formed_and_its_route_is_one_the_scan_can_hold()
    {
        var scan = Tree.Value;
        var pins = Pins.Value;
        var observed = Observed(scan).ToDictionary(r => r.Key, StringComparer.Ordinal);

        var problems = new List<string>(pins.Errors);
        foreach (var pin in pins.Rows)
        {
            var where = $"{InventoryRelativePath}:{pin.Line} {pin.Path} [{pin.Marker}]";
            if (!Marker.Pinnable.Contains(pin.Marker, StringComparer.Ordinal))
            {
                problems.Add($"{where}: marker '{pin.Marker}' is not one of {string.Join(", ", Marker.Pinnable)}"
                    + (pin.Marker == Marker.Banned ? " (banned is never pinned; F4 holds it at zero)" : string.Empty));
                continue;
            }

            if (pin.Total < 1 || pin.Guarded < 0 || pin.Guarded > pin.Total)
                problems.Add($"{where}: total {pin.Total} and guarded {pin.Guarded} must satisfy 0 <= guarded <= total, total >= 1");

            var expected = ExpectedGuardKind(pin.Marker);
            if (pin.Guarded == 0 && pin.GuardedBy != "-")
                problems.Add($"{where}: guarded_by '{pin.GuardedBy}' with nothing guarded; write '-'");
            if (pin.Guarded > 0 && (expected == GuardKind.None || pin.GuardedBy != expected.ToString()))
            {
                problems.Add($"{where}: guarded_by '{pin.GuardedBy}', but a guarded {pin.Marker} occurrence can only be "
                    + (expected == GuardKind.None ? "absent: this marker has no guarded form" : expected.ToString()));
            }

            if (pin.Guarded > 0 && observed.TryGetValue(pin.Key, out var row) && row.Guarded > 0 && row.GuardedBy != pin.GuardedBy)
                problems.Add($"{where}: guarded_by '{pin.GuardedBy}', but the scan proved '{row.GuardedBy}'");

            if (pin.Unguarded == 0 && pin.Reason != "-")
                problems.Add($"{where}: unguarded_reason '{pin.Reason}' with nothing unguarded; write '-'");
            if (pin.Unguarded > 0 && !IsAllowedReason(pin.Path, pin.Reason))
            {
                problems.Add($"{where}: unguarded_reason '{pin.Reason}' is not {Unrouted}, "
                    + string.Join(", ", ExemptReasons.Select(r => "Exempt:" + r))
                    + $", or {GuardImplReason} (only under {GuardImplFolder}). Governance, Factory and Upstream: are 3b reasons");
            }

            if (pin.IdsText != "-" && pin.Ids.Any(id => !EgId.IsMatch(id)))
                problems.Add($"{where}: ids '{pin.IdsText}' must be comma-separated EG-… ids, or '-'");
            if (string.IsNullOrWhiteSpace(pin.Note) || pin.Note.StartsWith('?'))
                problems.Add($"{where}: the note is empty or still '?'; say what the row is");
        }

        problems.Should().BeEmpty(
            "every row is a reviewed decision: a guarded count the classifier proves, and a reason from the closed "
            + "set for what is left. Problems:\n{0}\n{1}",
            string.Join("\n", problems), RenderObserved(observed.Values.OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Marker, StringComparer.Ordinal), pins.Rows));
    }

    private static bool IsAllowedReason(string path, string reason) =>
        reason == Unrouted
        || (reason.StartsWith("Exempt:", StringComparison.Ordinal) && ExemptReasons.Contains(reason["Exempt:".Length..], StringComparer.Ordinal))
        || (reason == GuardImplReason && path.StartsWith(GuardImplFolder, StringComparison.Ordinal));

    /// <summary>
    /// F4, HTTP defaults are bound: <see cref="HttpDefaultsBindingToken"/> occurs exactly once in production, in
    /// <see cref="HttpDefaultsBindingFile"/>, and builds the guard handler there; and nothing registers a bare
    /// <c>HttpClient</c> (<c>AddSingleton&lt;HttpClient&gt;</c>, <c>typeof(HttpClient)</c>), which would hand out a
    /// client no factory default ever touches.
    /// </summary>
    [Fact]
    public void F4_http_client_defaults_are_bound_once_and_no_bare_HttpClient_is_registered()
    {
        var scan = Tree.Value;
        var problems = new List<string>();

        var bindings = scan.HttpDefaultsBindings;
        if (bindings.Count != 1)
        {
            problems.Add($"'{HttpDefaultsBindingToken}' occurs {bindings.Count} times in production, expected exactly 1 in "
                + $"{HttpDefaultsBindingFile}: {(bindings.Count == 0 ? "none" : string.Join(", ", bindings.Select(b => b.Path + ":" + b.Line)))}");
        }

        foreach (var (path, line, bindsHandler) in bindings)
        {
            if (path != HttpDefaultsBindingFile)
                problems.Add($"{path}:{line}: '{HttpDefaultsBindingToken}' outside {HttpDefaultsBindingFile}");
            if (!bindsHandler)
                problems.Add($"{path}:{line}: '{HttpDefaultsBindingToken}' does not build '{HttpDefaultsHandlerToken}'");
        }

        problems.AddRange(scan.Occurrences.Where(o => o.Marker == Marker.Banned)
            .Select(o => $"{o.Where}: '{o.Token}' registers or resolves a bare HttpClient; take one from IHttpClientFactory"));

        problems.Should().BeEmpty(
            "every IHttpClientFactory client gets the guard handler through one ConfigureHttpClientDefaults call, "
            + "and that only holds if no bare HttpClient is registered beside the factory. Problems:\n{0}",
            string.Join("\n", problems));
    }

    /// <summary>
    /// F5, chat is governed (3a): the keyed and default chat and embedding registrations live only in
    /// <see cref="MeaiRegistrationFile"/>; each of its <see cref="KeyedChatClientStatements"/>
    /// <c>AddKeyedChatClient</c> statements carries <c>.UseAshlarGovernance(</c> before its depth-0 ';'; the
    /// two leaf chat clients are built only for those statements; the Bedrock SDK client and
    /// <c>.AsIChatClient(</c> live only in <see cref="BedrockFactoryFile"/>; and governance composes
    /// PolicyGate, then Sanitizing, then Auditing.
    /// </summary>
    [Fact]
    public void F5_every_chat_registration_is_governed()
    {
        var scan = Tree.Value;
        var problems = new List<string>();

        foreach (var o in scan.Occurrences.Where(o => o.Marker == Marker.ChatRegister && o.Path != MeaiRegistrationFile))
            problems.Add($"{o.Where}: '{o.Token}' outside {MeaiRegistrationFile}; register chat targets through AddAshlarMeaiPipeline or AddAshlarGovernedChatClient");

        foreach (var o in scan.Occurrences.Where(o => o.Marker == Marker.SdkClient && o.Path != BedrockFactoryFile
                     && (o.Token.Contains("AmazonBedrockRuntimeClient", StringComparison.Ordinal) || o.Token.Contains("AsIChatClient", StringComparison.Ordinal))))
        {
            problems.Add($"{o.Where}: '{o.Token}' outside {BedrockFactoryFile}");
        }

        var root = scan.Root;
        var meai = Load(root, MeaiRegistrationFile);
        if (meai is null)
        {
            problems.Add($"{MeaiRegistrationFile} does not exist");
        }
        else
        {
            var (statements, violations) = ChatGovernance(meai);
            problems.AddRange(violations);
            if (statements != KeyedChatClientStatements)
            {
                problems.Add($"{MeaiRegistrationFile}: {statements} AddKeyedChatClient statements, expected {KeyedChatClientStatements}; "
                    + "a new keyed target is a new egress path: give it .UseAshlarGovernance(, then update the constant and the TSV");
            }
        }

        var governance = Load(root, GovernanceFile);
        if (governance is null)
            problems.Add($"{GovernanceFile} does not exist");
        else
            problems.AddRange(GovernanceOrder(governance));

        problems.Should().BeEmpty(
            "a chat target reaches a model only through UseAshlarGovernance, and the guard's chat adapter (3b) "
            + "rides on exactly that composition. Problems:\n{0}",
            string.Join("\n", problems));
    }

    /// <summary>F6, floors and anchors: the scan reads the tree, and every named anchor contributes its marker.</summary>
    [Fact]
    public void F6_the_scan_reads_the_tree_and_every_anchor_contributes_its_marker()
    {
        var scan = Tree.Value;
        _output.WriteLine($"ScannedFiles={scan.Files.Count} ExaminedOccurrences={scan.ExaminedOccurrences}");
        foreach (var marker in Marker.Pinnable.Append(Marker.Banned))
        {
            var of = scan.Occurrences.Where(o => o.Marker == marker).ToList();
            _output.WriteLine($"  {marker}: total {of.Count}, guarded {of.Count(o => o.Guarded)}, files {of.Select(o => o.Path).Distinct().Count()}");
        }

        var problems = new List<string>();
        if (scan.Files.Count < ScannedFilesFloor)
            problems.Add($"{scan.Files.Count} files scanned, floor {ScannedFilesFloor}: a root or a pruning rule broke");
        if (scan.ExaminedOccurrences < ExaminedOccurrencesFloor)
            problems.Add($"{scan.ExaminedOccurrences} occurrences examined, floor {ExaminedOccurrencesFloor}: a marker stopped matching");

        var files = scan.Files.ToHashSet(StringComparer.Ordinal);
        foreach (var (marker, path) in Anchors)
        {
            if (!files.Contains(path))
                problems.Add($"{path}: anchor for {marker} is not scanned");
            else if (!scan.Occurrences.Any(o => o.Path == path && o.Marker == marker))
                problems.Add($"{path}: anchor for {marker} contributes no {marker} occurrence; the marker stopped matching a real site");
        }

        problems.Should().BeEmpty(
            "every other fact is vacuous on a scan that reads nothing; measured {0} files and {1} occurrences. Problems:\n{2}",
            scan.Files.Count, scan.ExaminedOccurrences, string.Join("\n", problems));
    }

    /// <summary>
    /// F6, the guard files are seen: pruning that drops the guard's own source would also drop the place an
    /// exemption is allowed to live, and nothing else would notice.
    /// </summary>
    [Fact]
    public void F6_the_guard_files_are_in_the_scanned_population()
    {
        var files = Tree.Value.Files.ToHashSet(StringComparer.Ordinal);
        var unseen = GuardFiles.Where(f => !files.Contains(f)).ToList();

        unseen.Should().BeEmpty(
            "the guard core, the factory-defaults binding and the governance composition must be scanned. Not seen: {0}",
            string.Join(", ", unseen));
    }

    /// <summary>
    /// F7, reach: shipped code in directories whose names read like tests is scanned; real test projects are
    /// not. Both halves are load-bearing: "stop pruning" passes the first and fails the second.
    /// </summary>
    [Fact]
    public void F7_the_walk_reaches_shipped_code_and_skips_test_projects()
    {
        var scan = Tree.Value;
        bool Scanned(string prefix) => scan.Files.Any(f => f.StartsWith(prefix, StringComparison.Ordinal));
        var problems = new List<string>();

        foreach (var shipped in new[]
                 {
                     "src/Ashlar.Infrastructure/Testing/ExecutionPlatform/RemoteExecutionPlatform.cs",
                     "src/Ashlar.Agents.TestKit/", "application/src/Ashlar.CLI/", "commercial/src/",
                 })
        {
            if (!Scanned(shipped))
                problems.Add($"{shipped} is shipped and must be scanned");
        }

        foreach (var test in new[]
                 {
                     "src/Ashlar.Tests.Infrastructure/", "application/src/Ashlar.Tests.CLI/",
                     "src/Ashlar.Mcp.Client.Tests/", "src/Ashlar.Transport.A2A.Tests/",
                 }.Concat(UnshippedRoots.Select(r => r + "/")))
        {
            if (Scanned(test))
                problems.Add($"{test} is a test project or unshipped and must not be scanned");
        }

        var root = scan.Root;
        if (!IsTestProjectRoot(Path.Combine(root, "src", "Ashlar.Tests.Infrastructure")))
            problems.Add("src/Ashlar.Tests.Infrastructure declares <IsTestProject>true</IsTestProject> and must classify as a test project");
        if (IsTestProjectRoot(Path.Combine(root, "src", "Ashlar.Agents.TestKit")))
            problems.Add("src/Ashlar.Agents.TestKit is a shipped library of fakes and must not classify as a test project");
        if (IsTestProjectRoot(Path.Combine(root, "src", "Ashlar.Infrastructure")))
            problems.Add("src/Ashlar.Infrastructure must not classify as a test project");

        problems.Should().BeEmpty(
            "a scan that never looks at a file satisfies every other fact forever. Problems:\n{0}",
            string.Join("\n", problems));
    }

    /// <summary>
    /// F8, the inventory is written down: every TSV id and every <c>"EG-…"</c> literal in production code is a
    /// row of <c>docs/EgressInventory.md</c>, and every docs row whose route is not <c>Unscanned:*</c> is named
    /// by at least one TSV row.
    /// </summary>
    [Fact]
    public void F8_every_id_is_a_written_row_and_every_scanned_row_is_pinned()
    {
        var scan = Tree.Value;
        var pins = Pins.Value;
        var (docs, problems) = ReadDocs(scan.Root);

        if (docs.Count > 0)
        {
            var docIds = docs.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var pin in pins.Rows)
            {
                foreach (var id in pin.Ids.Where(id => !docIds.Contains(id)))
                    problems.Add($"{InventoryRelativePath}:{pin.Line}: {id} is not a row of {DocsRelativePath}");
            }

            foreach (var (path, line, id) in scan.SiteIdLiterals.Where(s => !docIds.Contains(s.Id)))
                problems.Add($"{path}:{line}: the site id \"{id}\" is not a row of {DocsRelativePath}");

            var named = pins.Rows.SelectMany(p => p.Ids).ToHashSet(StringComparer.Ordinal);
            foreach (var row in docs.Where(d => d.Route is not null && !IsUnscanned(d.Route) && !named.Contains(d.Id)))
                problems.Add($"{DocsRelativePath}:{row.Line}: {row.Id} (route '{row.Route}') is named by no row of {InventoryRelativePath}");
        }

        problems.Should().BeEmpty(
            "the TSV pins counts and the document says what each path is; an id in one and not the other is a "
            + "path nobody wrote down, or a written path nobody pins. Problems:\n{0}",
            string.Join("\n", problems));
    }

    // ── F5 helpers ───────────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex KeyedChatClientCall = new(@"\.\s*AddKeyedChatClient\s*[(<]", RegexOptions.CultureInvariant);

    private static readonly Regex UseAshlarGovernanceCall = new(@"\.\s*UseAshlarGovernance\s*\(", RegexOptions.CultureInvariant);

    private static readonly Regex LeafChatConstruction = new(
        @"\bnew\s+(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*(?:OllamaHttpChatClient|LlamaSharpChatClient)\s*\(", RegexOptions.CultureInvariant);

    private static readonly Regex DeclaredName = new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*=(?![=>])", RegexOptions.CultureInvariant);

    private static readonly (string Name, Regex Construction)[] GovernanceLayers =
    [
        ("PolicyGate", new Regex(@"\bnew\s+PolicyGateChatClient\s*\(", RegexOptions.CultureInvariant)),
        ("Sanitizing", new Regex(@"\bnew\s+SanitizingChatClient\s*\(", RegexOptions.CultureInvariant)),
        ("Auditing", new Regex(@"\bnew\s+AuditingChatClient\s*\(", RegexOptions.CultureInvariant)),
    ];

    private static SourceModel? Load(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? new SourceModel(relative, File.ReadAllText(path)) : null;
    }

    /// <summary>
    /// Each <c>AddKeyedChatClient</c> statement up to its depth-0 ';' must carry <c>.UseAshlarGovernance(</c>.
    /// A leaf chat client must be built inside such a statement, or as the value of a local whose every other
    /// mention is inside a governed one (the <c>defaultOllama</c>/<c>defaultOnnx</c> delegates at :100-103).
    /// </summary>
    private static (int Statements, List<string> Violations) ChatGovernance(SourceModel m)
    {
        var code = m.Code;
        var violations = new List<string>();
        var statements = new List<(int Start, int End, bool Governed)>();
        foreach (Match k in KeyedChatClientCall.Matches(code))
        {
            var end = Scanner.StatementEnd(code, k.Index);
            var governed = UseAshlarGovernanceCall.IsMatch(code[k.Index..end]);
            statements.Add((Scanner.StatementStart(code, k.Index), end, governed));
            if (!governed)
                violations.Add($"{m.Path}:{m.LineOf(k.Index)}: AddKeyedChatClient statement has no .UseAshlarGovernance( before its ';'");
        }

        bool InGoverned(int at) => statements.Any(s => s.Governed && s.Start <= at && at < s.End);

        foreach (Match leaf in LeafChatConstruction.Matches(code))
        {
            if (InGoverned(leaf.Index))
                continue;

            var start = Scanner.StatementStart(code, leaf.Index);
            var end = Scanner.StatementEnd(code, leaf.Index);
            var declared = DeclaredName.Match(code[start..leaf.Index]);
            var uses = declared.Success
                ? Regex.Matches(code, @"(?<![A-Za-z0-9_.])" + Regex.Escape(declared.Groups[1].Value) + @"(?![A-Za-z0-9_])")
                    .Where(u => u.Index < start || u.Index >= end)
                    .ToList()
                : new List<Match>();
            if (uses.Count == 0 || !uses.All(u => InGoverned(u.Index)))
            {
                violations.Add($"{m.Path}:{m.LineOf(leaf.Index)}: '{leaf.Value.Trim()}' is not built for a governed AddKeyedChatClient statement");
            }
        }

        return (statements.Count, violations);
    }

    private static IEnumerable<string> GovernanceOrder(SourceModel m)
    {
        var at = new List<int>();
        foreach (var (name, construction) in GovernanceLayers)
        {
            var found = construction.Matches(m.Code);
            if (found.Count != 1)
                yield return $"{m.Path}: 'new {name}ChatClient(' occurs {found.Count} times, expected exactly 1";
            at.Add(found.Count > 0 ? found[0].Index : -1);
        }

        for (var i = 1; i < at.Count; i++)
        {
            if (at[i - 1] >= 0 && at[i] >= 0 && at[i - 1] >= at[i])
            {
                yield return $"{m.Path}:{m.LineOf(at[i])}: {GovernanceLayers[i].Name} is composed before {GovernanceLayers[i - 1].Name}; "
                    + "the order is PolicyGate, then Sanitizing, then Auditing (the first Use() is outermost)";
            }
        }
    }
}
