using System.Text.RegularExpressions;
using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;
using Xunit.Abstractions;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Every outbound path from production code is listed in <c>ci/egress-inventory.tsv</c> and
/// <c>docs/EgressInventory.md</c>, pinned per file and marker, and a new one cannot appear unlisted. Every listed
/// site is routed through the egress guard — <c>EgressHttp</c>, the factory defaults, governance or an explicit
/// guard call — or carries a final <c>Exempt:</c> reason (SPEC-007 PR 3b; the guard is report-only and refuses
/// nothing until PR 4).
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
///   construction inside their argument region is Wrapped, and so is one that initializes a DECLARED local
///   (<c>var h = new …</c>, <c>SocketsHttpHandler h = new() …</c>) whose every later mention in its block is inside
///   such a region or configures it (<c>h.Prop = …</c>), at least one being inside (G2). A field, a property,
///   <c>this.x</c> or an initializer member is never laundered, and returning the local or handing it anywhere
///   else (an SDK's <c>HttpHandler =</c> included) leaves it unguarded.</item>
///   <item><c>http.param</c>: <c>HttpClient[?] id</c> as a constructor, method, local-function, delegate or
///   primary-constructor parameter. Not a parameter: a field, an out argument of a call
///   (<c>TryGetValue(k, out HttpClient? c)</c>), a deconstruction or tuple element, a lambda's parameter, a
///   <c>using (HttpClient c = …)</c> local. Guarded (Precedes) by the stored-client rule: in a type that is not
///   partial, and never in a record's primary constructor (the compiler copies the parameter into a public
///   property), the parameter is only named, null-checked, configured, stored into a private field or property, or
///   sent after a G3 guard; every other mention of that holder is a configuration or G3-preceded; and at least one
///   send is G3-preceded (Bing, the experimental Ollama proposer).</item>
///   <item><c>http.register</c>: <c>.AddHttpClient(</c>/<c>.AddHttpClient&lt;</c>; Factory under D1: a non-declaration
///   <c>AddAshlarEgressGuard(</c> AFTER it, as a statement of the registration's own innermost code block (the file
///   root of a top-level program counts). Not before it, not in an enclosing or sibling block.</item>
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
/// program). A guard in a sibling member, in a field initializer, inside a braced nested block (an if, loop,
/// lambda or local function with its own braces), or after the primitive never counts: the laundering lesson of
/// <c>UnstableHashKeyConventionTests</c>, where one override exempted an unrelated call below it. Blocks are
/// brace pairs only, so a guard with no braces of its own counts for its enclosing block (see below).</para>
///
/// <para><b>Pins.</b> One TSV row per observed (path, marker): <c>path marker total guarded guarded_by
/// unguarded_reason ids note</c>, header lines starting with '#', as <c>ci/certifier-boundary-inventory.tsv</c>.
/// Guarded forms are pinned too, so converting a site never deletes its row and the inventory stays complete.
/// <c>guarded_by</c> is Wrapped, Precedes, Factory or <c>-</c>. <c>unguarded_reason</c> is <c>-</c> when nothing
/// is unguarded, and otherwise one the row's marker allows (F3, <c>ReasonProblems</c>):</para>
/// <list type="bullet">
///   <item>http.new: an <c>Exempt:</c> reason; <c>Exempt:GuardImpl</c> only under
///   <c>src/Ashlar.Abstractions/Security/Egress/</c>; Governance for a governed pair.</item>
///   <item>http.param: Factory, <c>Upstream:&lt;path&gt;</c>, Governance for a governed pair, or an <c>Exempt:</c> reason.</item>
///   <item>http.register: <c>Upstream:&lt;path&gt;</c>, or <c>Exempt:ConsumerSdk</c>.</item>
///   <item>sdk.client: Governance for a governed pair, or an <c>Exempt:</c> reason. chat.register: Governance.</item>
///   <item>socket, process, door, telemetry, store: an <c>Exempt:</c> reason.</item>
/// </list>
/// <para>The <c>Exempt:</c> set is closed and final (LocalOnly, Operator, Inbound, DataStore, LocalDaemon,
/// ConsumerSdk, TestDouble, TestSeam); ConsumerSdk only under <c>src/Ashlar.Client/</c>. <b>Factory</b>: an
/// http.param fed only from <c>IHttpClientFactory</c> — every supply site passes, in the parameter's position, a
/// client created by an <c>IHttpClientFactory</c>-typed receiver declared in that file (directly, through a
/// never-reassigned local, through a same-file private factory method, or through the caller's own parameter,
/// never reassigned in the caller and proven the same way), or the type is a typed client of a Factory-classified
/// <c>AddHttpClient&lt;…, T&gt;</c>.
/// <b>Upstream:&lt;path&gt;</b>: an http.param supplied only from the named file, which builds no unguarded client,
/// exposes no client field, property, indexer or explicit interface property, is not partial, and is itself
/// grounded (a Wrapped http.new, or another routed http.param row); or an http.register in a project that cannot
/// reach Ashlar.Infrastructure, composed by the named executable program, which calls <c>AddAshlarEgressGuard</c>,
/// as every executable composing that project must.
/// <b>Governance</b>: one of the four MEAI pairs (<see cref="GovernedPairs"/>), only while F5 is green. <c>ids</c>
/// are <c>EG-…</c> rows of <c>docs/EgressInventory.md</c>. F1, F2 and F3 print the full observed inventory in TSV
/// form when they fail, keeping each pinned row's reason, ids and note: paste it over the data rows and review each
/// <c>?</c>.</para>
///
/// <para><b>The guard's own constructions: option (b).</b> <c>EgressHttp.cs</c> has to build a raw client and
/// handler to hand anyone a guarded one. Those are pinned as <c>Exempt:GuardImpl</c>, a reason F3 accepts only
/// for files under <c>src/Ashlar.Abstractions/Security/Egress/</c>. Option (a), widening G2 to "inside
/// <c>new EgressGuardHandler(</c>", was not taken: it depends on how the guard core spells its composition
/// (<c>new HttpClient(new EgressGuardHandler(…))</c> puts the client OUTSIDE that region), and an exemption that
/// is a fixed folder cannot drift with the implementation.</para>
///
/// <para><b>What it cannot see</b>, stated so nobody mistakes green for proof. Two lists: the misses a control
/// pins, and the misses that are only written down here.</para>
/// <para><b>Pinned.</b> Each is a <c>known miss:</c> control in F9 or in the route controls (for textual order,
/// the <c>guarded:</c> control that puts the primitive in a lambda), which asserts the miss is NOT caught, so the
/// change that teaches the scan must flip it. An alias (<c>using H = System.Net.Http.HttpClient</c>); a construction inside an
/// interpolation hole (Clean blanks it); a reflection-built client; a network SDK whose constructor nobody taught
/// the scan (package-level classification is deferred); an executable chosen by a caller of a process funnel
/// (<c>TimedProcess.RunAsync("curl", …)</c>); file egress through no door method (<c>File.Copy</c> to removable
/// media). G3 is TEXTUAL order, not temporal, so a lambda can defer the send past the guard (OTLP's decision at
/// registration is intentional). G3 cannot see whether a guard RUNS: a guard with no braces of its own counts for
/// its enclosing block, pinned for the body of an unbraced <c>if</c> and of an unbraced loop, a sibling
/// <c>switch</c> case, a conditional expression, and an expression-bodied lambda or local function that is never
/// invoked. A <c>Keep(h)</c> helper inside a Wrap region keeps the raw handler. The stored-client rule counts every
/// G3-preceded mention of a holder, so a mention that copies the stored client into another member
/// (<c>_other = _http;</c> after a guard) passes while <c>_other</c> is sent unguarded elsewhere. An early return
/// between a registration and its guard. An Upstream supplier passing a static client defined in another file; an
/// Upstream file that hands its client out through a non-private method, or through a <c>Func&lt;…&gt;</c>,
/// <c>Lazy&lt;…&gt;</c>, array or tuple member (the Fields clause reads <c>HttpClient</c> fields, properties,
/// indexers and explicit interface properties, not methods or wrapper types); and an <c>IHttpClientFactory</c>-typed
/// receiver backed by a custom implementation, which only F4 (D) catches.</para>
/// <para><b>Stated only</b>, with no control. HTTP clients built inside third-party SDKs (the AWS SDK, the MCP
/// SDK's own default client); inbound server responses (not scanned: <c>Unscanned:Inbound</c>); a guard in an
/// unbraced <c>else</c> body (the rule the pinned <c>if</c> shows); the stored-client copy made from the parameter
/// itself rather than a holder (<c>_shared = x;</c> into a non-private member, or <c>Use(x)</c>, after a guard),
/// which is the rule the pinned holder copy shows. For the routes: a guard installed on a
/// different <c>IServiceCollection</c> from the one the registration fills; an install call with no braces of its
/// own (a brace-less lambda, an unbraced <c>if</c>), which D1 counts for the block around it as G3 does; a host
/// outside the repository that composes a factory consumer with no Ashlar member that installs the guard
/// (<c>AddAshlarFederatedBrickMesh</c> alone); a reflection or target-typed <c>new(…)</c> supplier outside a
/// declaration (an argument, a <c>return</c>, an expression-bodied <c>T P =&gt; new(…)</c>) and a bare call through
/// <c>using static</c> from another file, which the Factory route does not list as supply sites (it does report a
/// method group, <c>CreateInstance&lt;T&gt;</c>, <c>typeof(T)</c> and an empty-argument generic registration, each
/// with a failing route control); an explicit interface method that returns the client from an Upstream file (the
/// method shape the pinned non-private method shows); an <c>IHttpClientFactory</c> receiver resolved by name over
/// the whole file, so an untyped lambda parameter or a deconstructed local that reuses the name of a typed one
/// elsewhere in the file passes (<c>RouteContext.ReceiverIsHttpClientFactory</c>). For F4 (D) and F5: a registration
/// of <c>IHttpClientFactory</c> or of a raw <c>IChatClient</c> whose service type the text does not name
/// (<c>AddSingleton(sp =&gt; (IChatClient)x)</c>, a descriptor built from a <c>Type</c> variable). And a limit of the
/// records, not of the scan: every factory client records the family <c>http.factory</c> (NetworkExport), the model
/// calls EG-MDL-09/10/13/14 included.</para>
///
/// <para><b>Routes.</b> Factory and Upstream reason about a CLOSED world: every supply site of a receiving member
/// is a production file in this scan, found by name (<c>new T(</c>, <c>T x = new(</c>, an auto-property initializer
/// <c>T P { … } = new(</c>, <c>: this(</c> in T's body, a primary constructor's type included, <c>.N(</c>, a bare
/// <c>N(</c> in the declaring file, or in any file for a non-private method of a type that is neither sealed nor
/// static), and every spelling the scan cannot follow (<c>CreateInstance&lt;T&gt;</c>, <c>typeof(T)</c>, an
/// empty-argument generic registration of T, a method group of N) fails the route instead of being assumed away.
/// That premise holds only for non-partial receiving types, and for a constructor only in a sealed one (a derived
/// type's <c>base(…)</c> is a supply site the scan does not list), which the routes require. A positional record's
/// primary constructor fails both routes: its parameter is also a public property, which <c>x with { P = … }</c>
/// or an object initializer replaces where no supply site shows it. "Never reassigned" (a local under F-call (ii), a
/// forwarded parameter under (iv)) also rules out a deconstruction into it (<c>(v, _) = …</c>, a tuple swap). A row's
/// route and a member's proof are memoised, and a cycle fails.</para>
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
    /// never trips them. Measured in the devtest container on the SPEC-007 PR 3a commit (edf585a): 2,128
    /// production .cs files scanned and 146 occurrences examined. PR 3b adds the two MEAI guard files (2,130
    /// files) and two http.new occurrences, the MCP transport's EgressHttp client and a second construction in
    /// the gRPC channel handler, so 148 occurrences (http.new 18, of which 3 are the guard's own in
    /// <c>EgressHttp.cs</c>; http.param 22, http.register 12, sdk.client 9, socket 2, process 51, door 11,
    /// telemetry 2, store 14, chat.register 7, banned 0). PR 4.1 adds one http.new, the redirect-off
    /// <c>HttpClientHandler</c> in <c>OllamaHttpChatClient.cs</c> (:170), and no production file, so 149 occurrences
    /// (http.new 19, the other markers unchanged). SPEC-007 PR 4.6 adds three files with no outbound path (the mode
    /// resolver, the reset seam and <c>AddAshlar</c>'s egress partial): 2,133 files, 149 occurrences. SPEC-007 PR 4.2
    /// adds the netstandard2.0 synchronous-send hop and its <c>HttpMessageInvoker</c>: 2,134 files, 150 occurrences
    /// (http.new 20, of which 4 are the guard's own, pinned <c>Exempt:GuardImpl</c>). SPEC-007 PR 4.4 adds one file
    /// with no outbound path, <c>ReadScope.cs</c>: 2,135 files, 150 occurrences, as measured in the devtest container
    /// with all four merged. SPEC-007 PR 4.5 adds one file with no outbound path, <c>ILabelledTool.cs</c>: 2,136 files,
    /// 150 occurrences, as measured in the devtest container. Set far enough below to survive ordinary deletions; a
    /// scan that stops reading the tree falls through them. Re-measure and restate when a PR moves them.
    /// </summary>
    private const int ScannedFilesFloor = 1000;

    private const int ExaminedOccurrencesFloor = 60;

    /// <summary>
    /// F8's non-vacuity floor: the <c>| EG-… |</c> rows <c>ReadDocs</c> parses from <c>docs/EgressInventory.md</c>.
    /// Measured at 67 on the SPEC-007 PR 3a commit: 59 are named by TSV rows, and the other 8 have an
    /// <c>Unscanned:</c> route, which no TSV row has to name. Set at about 45% of that, the margin of the two
    /// floors above. A docs edit that breaks every table (backticked or linked id cells, a <c>| :-- |</c>
    /// separator, no leading pipes, an emptied file) parses zero rows, and without this floor F8 would then check
    /// every TSV id and site literal against nothing.
    /// </summary>
    private const int DocsRowsFloor = 30;

    private const string MeaiRegistrationFile = "src/Ashlar.AI.Pipeline/MeaiPipelineServiceCollectionExtensions.cs";

    private const string GovernanceFile = "src/Ashlar.AI.Pipeline/Governance/AshlarGovernanceChatClientBuilderExtensions.cs";

    private const string BedrockFactoryFile = "src/Ashlar.AI.Pipeline/Clients/AwsBedrockChatClientFactory.cs";

    private const string OllamaChatClientFile = "src/Ashlar.AI.Pipeline/Clients/OllamaHttpChatClient.cs";

    private const string LlamaSharpChatClientFile = "src/Ashlar.AI.Pipeline/Clients/LlamaSharpChatClient.cs";

    /// <summary>F4 (A): AddAshlar's own registration file; every http.register in it must be Factory.</summary>
    private const string HostingFile = "src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs";

    /// <summary>An http.register whose project reaches this one can call AddAshlarEgressGuard itself, so it may not be Upstream.</summary>
    private const string InfrastructureProject = "src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj";

    /// <summary>The governed keyed chat registrations today: :105 ollama, :110 onnx, :172 AddAshlarGovernedChatClient, :244 each Bedrock tier.</summary>
    private const int KeyedChatClientStatements = 4;

    /// <summary>
    /// The only (path, marker) pairs that may carry <c>Governance</c>: the MEAI leaves and registrations F5 holds
    /// under <c>UseAshlarGovernance</c>, whose outermost layer is EgressGuardChatClient.
    /// </summary>
    private static readonly (string Path, string Marker)[] GovernedPairs =
    [
        (OllamaChatClientFile, Marker.HttpNew),
        (OllamaChatClientFile, Marker.HttpParam),
        (BedrockFactoryFile, Marker.SdkClient),
        (MeaiRegistrationFile, Marker.ChatRegister),
    ];

    /// <summary>
    /// F5 confinement: these types occur only in their own file and in <see cref="MeaiRegistrationFile"/>, where each
    /// mention is an empty-argument TryAddSingleton registration, inside a governed AddKeyedChatClient statement, or
    /// a leaf construction ChatGovernance accepts.
    /// </summary>
    private static readonly (string Type, string File)[] ConfinedChatTypes =
    [
        ("OllamaHttpChatClient", OllamaChatClientFile),
        ("LlamaSharpChatClient", LlamaSharpChatClientFile),
        ("AwsBedrockChatClientFactory", BedrockFactoryFile),
        ("IBedrockChatClientFactory", BedrockFactoryFile),
    ];

    /// <summary>The files the guard is made of. Each must be in the scanned population, or pruning broke.</summary>
    private static readonly string[] GuardFiles =
    [
        "src/Ashlar.Abstractions/Security/Egress/EgressHttp.cs",
        "src/Ashlar.Abstractions/Security/Egress/EgressGuard.cs",
        HttpDefaultsBindingFile,
        GovernanceFile,
        "src/Ashlar.AI.Pipeline/Governance/EgressGuardChatClient.cs",
        "src/Ashlar.AI.Pipeline/Governance/MeaiEgressDestination.cs",
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
            + "with EgressHttp.CreateClient/Wrap, take it from IHttpClientFactory and call AddAshlarEgressGuard after "
            + "the registration in the same block, or call guard.Evaluate(new EgressRequest(family, \"EG-…\", "
            + "destination)) before the primitive in the same block or an enclosing one. Then add its row to "
            + "docs/EgressInventory.md and pin it in {0}. Over the pin:\n{1}\n{2}",
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
    /// for Wrapped, G3 or the stored-client rule for Precedes, D1 for Factory), and every row with something
    /// unguarded carries a reason its marker allows and whose route holds (<see cref="ReasonProblems"/>).
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
            if (pin.Unguarded > 0)
                problems.AddRange(ReasonProblems(pin, scan, pins.Rows, observed));

            if (pin.IdsText != "-" && pin.Ids.Any(id => !EgId.IsMatch(id)))
                problems.Add($"{where}: ids '{pin.IdsText}' must be comma-separated EG-… ids, or '-'");
            if (string.IsNullOrWhiteSpace(pin.Note) || pin.Note.StartsWith('?'))
                problems.Add($"{where}: the note is empty or still '?'; say what the row is");
        }

        problems.Should().BeEmpty(
            "every row is a reviewed decision: a guarded count the classifier proves, and for what is left a reason "
            + "its marker allows and a route the scan can hold. Problems:\n{0}\n{1}",
            string.Join("\n", problems), RenderObserved(observed.Values.OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Marker, StringComparer.Ordinal), pins.Rows));
    }

    /// <summary>
    /// F4, HTTP defaults are bound: <see cref="HttpDefaultsBindingToken"/> occurs exactly once in production, in
    /// <see cref="HttpDefaultsBindingFile"/>, and builds the guard handler there; nothing registers or resolves a bare
    /// <c>HttpClient</c> (<c>AddSingleton&lt;HttpClient&gt;</c>, <c>typeof(HttpClient)</c>,
    /// <c>GetRequiredService&lt;HttpClient&gt;</c>), which would hand out a client no factory default ever touches;
    /// and (A) every http.register in <see cref="HostingFile"/> (AddAshlar's own) is Factory; (B)
    /// <c>AddAshlarEgressGuard(</c> is declared exactly once in production, in <see cref="HttpDefaultsBindingFile"/>;
    /// (D) no production type implements <c>IHttpClientFactory</c> and nothing registers it directly; (E) every call
    /// of <c>AddAshlarEgressGuard(</c> covers a registration under D1 (the rail: a call that covers nothing is a call
    /// in the wrong place).
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
        problems.AddRange(HostingFactoryProblems(scan.Occurrences, HostingFile));
        problems.AddRange(GuardDeclarationProblems(scan.GuardDeclarations));
        problems.AddRange(scan.FactoryImplementations.Select(f => "(D) " + f));
        problems.AddRange(scan.RailProblems.Select(r => "(E) " + r));

        problems.Should().BeEmpty(
            "every IHttpClientFactory client gets the guard handler through one ConfigureHttpClientDefaults call, "
            + "installed by AddAshlar and after every other registration, and that only holds if no bare HttpClient and "
            + "no other IHttpClientFactory is registered beside the factory. Problems:\n{0}",
            string.Join("\n", problems));
    }

    /// <summary>F4 (A): the http.register occurrences of <paramref name="hostingFile"/> number at least one, and all are Factory.</summary>
    private static IEnumerable<string> HostingFactoryProblems(IEnumerable<Occurrence> occurrences, string hostingFile)
    {
        var registrations = occurrences.Where(o => o.Path == hostingFile && o.Marker == Marker.HttpRegister).ToList();
        if (registrations.Count == 0)
            yield return $"(A) {hostingFile} has no AddHttpClient; AddAshlar's registration moved, so update {nameof(HostingFile)}";
        foreach (var r in registrations.Where(r => r.Guard != GuardKind.Factory))
        {
            yield return $"(A) {r.Where}: AddAshlar's '{r.Token}' is not Factory; call services.AddAshlarEgressGuard() after it, "
                + "as a statement of the same block";
        }
    }

    /// <summary>F4 (B): <c>AddAshlarEgressGuard(</c> is declared exactly once in production, in <see cref="HttpDefaultsBindingFile"/>.</summary>
    private static IEnumerable<string> GuardDeclarationProblems(IReadOnlyCollection<(string Path, int Line)> declarations)
    {
        if (declarations.Count != 1)
        {
            yield return $"(B) AddAshlarEgressGuard( is declared {declarations.Count} times in production, expected once in "
                + $"{HttpDefaultsBindingFile}: {string.Join(", ", declarations.Select(d => d.Path + ":" + d.Line))}";
        }

        foreach (var (path, line) in declarations.Where(d => d.Path != HttpDefaultsBindingFile))
            yield return $"(B) {path}:{line}: AddAshlarEgressGuard( declared outside {HttpDefaultsBindingFile}";
    }

    /// <summary>
    /// F5, chat is governed: the keyed and default chat and embedding registrations live only in
    /// <see cref="MeaiRegistrationFile"/>; each of its <see cref="KeyedChatClientStatements"/>
    /// <c>AddKeyedChatClient</c> statements carries <c>.UseAshlarGovernance(</c> before its depth-0 ';', with the
    /// same key (read from the raw text); the two leaf chat clients are built only for those statements; the
    /// Bedrock SDK client and <c>.AsIChatClient(</c> live only in <see cref="BedrockFactoryFile"/>; the four leaf
    /// types are confined (<see cref="ConfinedChatTypes"/>); no raw <c>IChatClient</c> is registered; and
    /// governance composes EgressGuard, then PolicyGate, then Sanitizing, then Auditing, with
    /// <c>new EgressGuardChatClient(</c> nowhere else.
    /// </summary>
    [Fact]
    public void F5_every_chat_registration_is_governed()
    {
        var problems = Tree.Value.Governance;

        problems.Should().BeEmpty(
            "a chat target reaches a model only through UseAshlarGovernance, and EgressGuardChatClient rides on "
            + "exactly that composition, outermost. Problems:\n{0}",
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
    /// by at least one TSV row. At least <see cref="DocsRowsFloor"/> rows must parse, or both directions would
    /// pass against an empty document.
    /// </summary>
    [Fact]
    public void F8_every_id_is_a_written_row_and_every_scanned_row_is_pinned()
    {
        var scan = Tree.Value;
        var pins = Pins.Value;
        var (docs, problems) = ReadDocs(scan.Root);
        _output.WriteLine($"DocsRows={docs.Count} (floor {DocsRowsFloor})");

        if (docs.Count < DocsRowsFloor)
        {
            problems.Add($"{DocsRelativePath}: {docs.Count} '| EG-… |' table rows parsed, floor {DocsRowsFloor}. Check the id "
                + "cells (a bare EG-… id, no backticks or link), the separator row ('| --- |', three dashes) and the leading "
                + "pipes; with no rows every check below is vacuous");
        }

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

    private static readonly Regex EgressGuardChatClientConstruction = new(
        @"\bnew\s+(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*EgressGuardChatClient\s*\(", RegexOptions.CultureInvariant);

    private static readonly Regex EmptyArgumentTryAddSingleton = new(
        @"\bTryAddSingleton\s*<[^<>;(){}]*>\s*\(\s*\)", RegexOptions.CultureInvariant);

    /// <summary>
    /// A raw IChatClient registration: <c>[Try]Add[Keyed]{Singleton|Scoped|Transient}&lt;IChatClient…</c> or
    /// <c>(typeof(IChatClient)…</c>; <c>ServiceDescriptor.[Keyed]{Singleton|Scoped|Transient|Describe}</c> with
    /// IChatClient before its ';'; or <c>new ServiceDescriptor(typeof(IChatClient), …)</c> (as in
    /// <c>services.Add(…)</c>). A registration whose service type the text does not name
    /// (<c>AddSingleton(sp =&gt; (IChatClient)x)</c>, a descriptor built from a <c>Type</c> variable) is a stated blind spot.
    /// </summary>
    private static readonly Regex RawChatClientRegistration = new(
        @"\b(?:Try)?Add(?:Keyed)?(?:Singleton|Scoped|Transient)\s*(?:<\s*(?:global::)?(?:Microsoft\.Extensions\.AI\.)?IChatClient\b|\(\s*typeof\s*\(\s*(?:global::)?(?:Microsoft\.Extensions\.AI\.)?IChatClient\s*\))"
        + @"|ServiceDescriptor\.(?:Keyed)?(?:Singleton|Scoped|Transient|Describe)\b[^;]*IChatClient"
        + @"|\bnew\s+(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*ServiceDescriptor\s*\(\s*typeof\s*\(\s*(?:global::)?(?:Microsoft\.Extensions\.AI\.)?IChatClient\s*\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    private static readonly (string Name, Regex Construction)[] GovernanceLayers =
    [
        ("EgressGuard", new Regex(@"\bnew\s+EgressGuardChatClient\s*\(", RegexOptions.CultureInvariant)),
        ("PolicyGate", new Regex(@"\bnew\s+PolicyGateChatClient\s*\(", RegexOptions.CultureInvariant)),
        ("Sanitizing", new Regex(@"\bnew\s+SanitizingChatClient\s*\(", RegexOptions.CultureInvariant)),
        ("Auditing", new Regex(@"\bnew\s+AuditingChatClient\s*\(", RegexOptions.CultureInvariant)),
    ];

    /// <summary>
    /// The full F5 list, read from the scan (never from disk), so a route control's fixture is checked exactly as
    /// the tree is. Governance (F3) is allowed only while this is empty.
    /// </summary>
    private static List<string> GovernanceProblems(TreeScan scan)
    {
        var problems = new List<string>();

        foreach (var o in scan.Occurrences.Where(o => o.Marker == Marker.ChatRegister && o.Path != MeaiRegistrationFile))
            problems.Add($"{o.Where}: '{o.Token}' outside {MeaiRegistrationFile}; register chat targets through AddAshlarMeaiPipeline or AddAshlarGovernedChatClient");

        foreach (var o in scan.Occurrences.Where(o => o.Marker == Marker.SdkClient && o.Path != BedrockFactoryFile
                     && (o.Token.Contains("AmazonBedrockRuntimeClient", StringComparison.Ordinal) || o.Token.Contains("AsIChatClient", StringComparison.Ordinal))))
        {
            problems.Add($"{o.Where}: '{o.Token}' outside {BedrockFactoryFile}");
        }

        var meai = scan.Model(MeaiRegistrationFile);
        ChatStatements? governed = null;
        if (meai is null)
        {
            problems.Add($"{MeaiRegistrationFile} is not in the scanned population");
        }
        else
        {
            governed = ChatGovernance(meai);
            problems.AddRange(governed.Violations);
            if (governed.Statements != KeyedChatClientStatements)
            {
                problems.Add($"{MeaiRegistrationFile}: {governed.Statements} AddKeyedChatClient statements, expected {KeyedChatClientStatements}; "
                    + "a new keyed target is a new egress path: give it .UseAshlarGovernance(, then update the constant and the TSV");
            }
        }

        var governance = scan.Model(GovernanceFile);
        if (governance is null)
            problems.Add($"{GovernanceFile} is not in the scanned population");
        else
            problems.AddRange(GovernanceOrder(governance));

        foreach (var path in scan.Files.Where(f => f != GovernanceFile && scan.Code(f)!.Contains("EgressGuardChatClient", StringComparison.Ordinal)))
        {
            foreach (Match c in EgressGuardChatClientConstruction.Matches(scan.Code(path)!))
                problems.Add($"{path}:{scan.Model(path)!.LineOf(c.Index)}: 'new EgressGuardChatClient(' outside {GovernanceFile}; the egress layer is composed only by UseAshlarGovernance");
        }

        problems.AddRange(ConfinementProblems(scan, meai, governed));

        foreach (var path in scan.Files.Where(f => scan.Code(f)!.Contains("IChatClient", StringComparison.Ordinal)))
        {
            foreach (Match r in RawChatClientRegistration.Matches(scan.Code(path)!))
            {
                problems.Add($"{path}:{scan.Model(path)!.LineOf(r.Index)}: '{Whitespace.Replace(r.Value, " ")}' registers a raw IChatClient; "
                    + "register chat targets through AddAshlarMeaiPipeline or AddAshlarGovernedChatClient");
            }
        }

        return problems;
    }

    /// <summary>
    /// Confinement, on cleaned code with <c>nameof(</c> excluded: each of <see cref="ConfinedChatTypes"/> occurs only
    /// in its own file and in <see cref="MeaiRegistrationFile"/>; there, each mention is inside an empty-argument
    /// <c>TryAddSingleton&lt;…&gt;()</c>, inside a governed AddKeyedChatClient statement, or inside a leaf
    /// construction ChatGovernance accepts.
    /// </summary>
    private static IEnumerable<string> ConfinementProblems(TreeScan scan, SourceModel? meai, ChatStatements? governed)
    {
        var accepted = new List<(int Start, int End)>();
        if (meai is not null && governed is not null)
        {
            accepted.AddRange(EmptyArgumentTryAddSingleton.Matches(meai.Code).Select(r => (r.Index, r.Index + r.Length)));
            accepted.AddRange(governed.Governed);
            accepted.AddRange(governed.AcceptedLeaves);
        }

        foreach (var (type, own) in ConfinedChatTypes)
        {
            foreach (var (path, at) in scan.Mentions(type))
            {
                if (path == own || Scanner.InNameof(scan.Code(path)!, at))
                    continue;
                if (path == MeaiRegistrationFile && accepted.Any(a => a.Start <= at && at < a.End))
                    continue;
                yield return $"{path}:{scan.Model(path)!.LineOf(at)}: '{type}' outside {own} and the governed registrations in "
                    + $"{MeaiRegistrationFile}; a leaf chat client reached any other way skips UseAshlarGovernance";
            }
        }
    }

    /// <summary>What ChatGovernance read: the AddKeyedChatClient statements, the violations, the governed spans and the accepted leaf constructions.</summary>
    private sealed record ChatStatements(int Statements, List<string> Violations, List<(int Start, int End)> Governed, List<(int Start, int End)> AcceptedLeaves);

    /// <summary>
    /// Each <c>AddKeyedChatClient</c> statement up to its depth-0 ';' must carry <c>.UseAshlarGovernance(</c>, and
    /// its first argument must equal the governance argument: compared on the RAW text at the same offsets,
    /// whitespace-normalised, because Clean blanks literal contents (<c>"cloud:x"</c> and <c>"local:onnx"</c> both
    /// clean to quotes and spaces). A leaf chat client must be built inside such a statement, or as the value of a
    /// local whose every other mention is inside a governed one (the <c>defaultOllama</c>/<c>defaultOnnx</c>
    /// delegates at :100-103).
    /// </summary>
    private static ChatStatements ChatGovernance(SourceModel m)
    {
        var code = m.Code;
        var violations = new List<string>();
        var statements = new List<(int Start, int End, bool Governed)>();
        foreach (Match k in KeyedChatClientCall.Matches(code))
        {
            var end = Scanner.StatementEnd(code, k.Index);
            var governance = UseAshlarGovernanceCall.Match(code, k.Index);
            var isGoverned = governance.Success && governance.Index + governance.Length <= end;
            statements.Add((Scanner.StatementStart(code, k.Index), end, isGoverned));
            if (!isGoverned)
            {
                violations.Add($"{m.Path}:{m.LineOf(k.Index)}: AddKeyedChatClient statement has no .UseAshlarGovernance( before its ';'");
                continue;
            }

            var open = code.IndexOf('(', k.Index + k.Length - 1);
            var keyArgument = open < 0 ? new List<(int Start, int End)>() : Scanner.SplitArguments(code, open);
            var governanceOpen = governance.Index + governance.Length - 1;
            var key = keyArgument.Count == 0 ? string.Empty : RawText(m, keyArgument[0].Start, keyArgument[0].End);
            var governedKey = RawText(m, governanceOpen + 1, Scanner.ClosingParen(code, governanceOpen));
            if (key.Length == 0 || key != governedKey)
            {
                violations.Add($"{m.Path}:{m.LineOf(k.Index)}: AddKeyedChatClient key '{key}' is governed as '{governedKey}'; "
                    + "the policy, sanitizer, audit and egress record would all name another target");
            }
        }

        bool InGoverned(int at) => statements.Any(s => s.Governed && s.Start <= at && at < s.End);

        var acceptedLeaves = new List<(int Start, int End)>();
        foreach (Match leaf in LeafChatConstruction.Matches(code))
        {
            if (InGoverned(leaf.Index))
            {
                acceptedLeaves.Add((leaf.Index, leaf.Index + leaf.Length));
                continue;
            }

            var start = Scanner.StatementStart(code, leaf.Index);
            var end = Scanner.StatementEnd(code, leaf.Index);
            var declared = DeclaredName.Match(code[start..leaf.Index]);
            var uses = declared.Success
                ? Regex.Matches(code, @"(?<![A-Za-z0-9_.])" + Regex.Escape(declared.Groups[1].Value) + @"(?![A-Za-z0-9_])")
                    .Where(u => u.Index < start || u.Index >= end)
                    .ToList()
                : new List<Match>();
            if (uses.Count == 0 || !uses.All(u => InGoverned(u.Index)))
                violations.Add($"{m.Path}:{m.LineOf(leaf.Index)}: '{leaf.Value.Trim()}' is not built for a governed AddKeyedChatClient statement");
            else
                acceptedLeaves.Add((leaf.Index, leaf.Index + leaf.Length));
        }

        return new ChatStatements(
            statements.Count,
            violations,
            statements.Where(s => s.Governed).Select(s => (s.Start, s.End)).ToList(),
            acceptedLeaves);
    }

    /// <summary>The raw text between two offsets of the cleaned code, whitespace-normalised.</summary>
    private static string RawText(SourceModel m, int start, int end) =>
        Whitespace.Replace(m.Raw[start..Math.Min(end, m.Raw.Length)], " ").Trim();

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
                    + "the order is EgressGuard, then PolicyGate, then Sanitizing, then Auditing (the first Use() is outermost)";
            }
        }
    }
}
