using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// F9: named controls, run through the same classifier as the tree. Each is a real shape from the inventory
/// (named where it came from), a shape PR 3b will write, or a spelling a careless edit would produce.
/// </summary>
public sealed partial class EgressGuardConventionTests
{
    public static TheoryData<string> ControlNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Controls.Keys.OrderBy(k => k, StringComparer.Ordinal))
            data.Add(name);
        return data;
    }

    /// <summary>
    /// Every control must hold at least one examined token, and its whole classification — every marker's
    /// (total, guarded), every other marker zero — must equal what it declares. A control that must fail a
    /// structural fact also proves that fact's predicate fires on it.
    /// </summary>
    [Theory]
    [MemberData(nameof(ControlNames))]
    public void F9_the_classifier_reads_each_control_as_declared(string name)
    {
        var control = Controls[name];
        var model = new SourceModel("control.cs", control.Source);
        var found = Scanner.Classify(model);

        found.Should().NotBeEmpty("control '{0}' holds no examined token, so it exercises nothing", name);
        Render(found).Should().Be(control.Expected,
            "control '{0}' declares its classification; the scan read it as:\n{1}", name, string.Join("\n", found));

        switch (control.Fact)
        {
            case null:
                break;
            case "F3":
                found.Should().Contain(o => o.Marker == Marker.HttpRegister && !o.Guarded,
                    "control '{0}' is an AddHttpClient whose member never calls AddAshlarEgressGuard(, so the Factory route F3 checks is not met", name);
                break;
            case "F4":
                found.Should().Contain(o => o.Marker == Marker.Banned, "control '{0}' must trip F4's banned count", name);
                break;
            case "F5":
                ChatGovernance(model).Violations.Should().NotBeEmpty("control '{0}' must trip F5's governed-statement check", name);
                break;
            default:
                throw new InvalidOperationException($"control '{name}' names unknown fact '{control.Fact}'");
        }
    }

    /// <summary>"marker total/guarded" per marker, ordinal by marker, joined by "; ".</summary>
    private static string Render(IEnumerable<Occurrence> found) =>
        string.Join("; ", found
            .GroupBy(o => o.Marker)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Count()}/{g.Count(o => o.Guarded)}"));

    private sealed record Control(string Source, string Expected, string? Fact = null);

    private static readonly Dictionary<string, Control> Controls = new(StringComparer.Ordinal)
    {
        // ── must count as unguarded ─────────────────────────────────────────────────────────────────────
        ["unguarded: a static field built target-typed (ProviderFactory.cs:60)"] = new("""
            public static class ProviderFactory
            {
                private static readonly HttpClient Http = new();
            }
            """, "http.new 1/0"),
        ["unguarded: an object initializer on the next line (OllamaHttpChatClient.cs:166)"] = new("""
            private static HttpClient Build(Uri baseUrl)
            {
                return new HttpClient
                {
                    BaseAddress = baseUrl,
                };
            }
            """, "http.new 1/0"),
        ["unguarded: a factory fallback counts exactly once (IdeEndpoints.cs:192)"] = new("""
            var http = services.GetService<IHttpClientFactory>()?.CreateClient() ?? new HttpClient();
            """, "http.new 1/0"),
        ["unguarded: a raw handler under a raw client counts twice (MeshAutoPullService.cs:56-71)"] = new("""
            var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            """, "http.new 2/0"),
        ["unguarded: fully qualified with global::"] = new("""
            var client = new global::System.Net.Http.HttpClient();
            """, "http.new 1/0"),
        ["unguarded: a UDP send with no guard (MeshDiscoveryService.cs:245)"] = new("""
            using var sender = new UdpClient();
            await sender.SendAsync(payload, payload.Length, endpoint);
            """, "socket 1/0"),
        ["unguarded: a gRPC channel over a raw handler in its file (DefaultGrpcChannelFactory.cs:66-74)"] = new("""
            var h = new HttpClientHandler();
            var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = h });
            """, "http.new 1/0; sdk.client 1/0"),
        ["unguarded: a two-argument MCP transport builds its own client (McpClientConnectionManager.cs:325)"] = new("""
            var transport = new HttpClientTransport(transportOptions, _loggerFactory);
            """, "sdk.client 1/0"),
        ["unguarded: an AWS SDK client (SmsIngressDynamoDbServiceCollectionExtensions.cs:12)"] = new("""
            services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
            """, "sdk.client 1/0"),
        ["unguarded: a Process with a ProcessStartInfo initializer counts twice (WorkflowCommand.cs:336)"] = new("""
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("ollama") { ArgumentList = { "pull", model } },
            };
            """, "process 2/0"),
        ["unguarded: Process.Start of a pushing git"] = new("""
            Process.Start("git", "push");
            """, "process 1/0"),
        ["unguarded: a mesh publish with no guard (PkgCommand.cs:384)"] = new("""
            var dest = MeshStore.Publish(ResolveStore(store), json);
            """, "door 1/0"),
        ["unguarded: an OTLP exporter with no guard (Ashlar.API/Program.cs:244)"] = new("""
            builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("Ashlar").AddOtlpExporter());
            """, "telemetry 1/0"),
        ["unguarded: a guard call AFTER the primitive"] = new("""
            public void Run(ProcessStartInfo psi, IEgressGuard guard)
            {
                using var p = Process.Start(psi);
                _ = guard.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-02", "host:dotnet"));
            }
            """, "process 1/0"),
        ["unguarded: a guard call in a sibling member, expression-bodied or block"] = new("""
            public sealed class Runner
            {
                private readonly IEgressGuard _guard = EgressGuard.ProcessDefault;

                public void Check() => _ = _guard.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-01", "nuget-feeds"));

                public void CheckToo()
                {
                    _ = _guard.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-01", "nuget-feeds"));
                }

                public void Run(ProcessStartInfo psi)
                {
                    using var p = Process.Start(psi);
                }
            }
            """, "process 1/0"),
        ["unguarded: a guard call in a field initializer"] = new("""
            public sealed class Publisher
            {
                private static readonly EgressDecision Decided =
                    EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.MeshPublish, "EG-MESH-01", "host:mesh"));

                public string Publish(string dir, string json) => MeshStore.Publish(dir, json);
            }
            """, "door 1/0"),
        ["unguarded: a handler local in a sibling member is not laundered by a later Wrap"] = new("""
            public sealed class Handlers
            {
                public HttpMessageHandler Raw()
                {
                    var h = new HttpClientHandler();
                    return h;
                }

                public HttpMessageHandler Guarded(HttpMessageHandler h) => EgressHttp.Wrap(h, EgressFamilies.Http, "EG-HTTP-05");
            }
            """, "http.new 2/1"),
        ["unguarded: a static handler field is not a local, even when every use wraps it"] = new("""
            public static class Shared
            {
                private static readonly HttpClientHandler Handler = new HttpClientHandler();

                public static HttpClient Client() => EgressHttp.CreateClient(Handler, EgressFamilies.Http, "EG-HTTP-05");
            }
            """, "http.new 2/1"),
        ["unguarded: a field assigned in a constructor and wrapped there is not a local; the raw handler reaches gRPC"] = new("""
            public sealed class Channels
            {
                private readonly SocketsHttpHandler _raw;
                private readonly HttpClient _client;

                public Channels()
                {
                    _raw = new SocketsHttpHandler();
                    _client = EgressHttp.CreateClient(_raw, EgressFamilies.Grpc, "EG-XPT-03");
                }

                public GrpcChannel Open(Uri endpoint) => GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = _raw });
            }
            """, "http.new 2/1; sdk.client 1/0"),
        ["unguarded: a handler local returned raw after a wrap names it (DefaultGrpcChannelFactory.BuildHandler slip)"] = new("""
            private HttpMessageHandler BuildHandler(GrpcTransportOptions options)
            {
                var handler = new HttpClientHandler();
                var guarded = EgressHttp.Wrap(handler, EgressFamilies.Grpc, "EG-XPT-03");
                _logger.LogDebug("built {Handler}", guarded);
                return handler;
            }
            """, "http.new 2/1"),
        ["unguarded: a handler local handed to an SDK beside its wrap"] = new("""
            var handler = new SocketsHttpHandler();
            var client = EgressHttp.CreateClient(handler, EgressFamilies.Grpc, "EG-XPT-03");
            var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = handler });
            """, "http.new 2/1; sdk.client 1/0"),
        ["unguarded: an A2A client while its file still builds a raw HttpClient (A2AAgentTransport.cs:81, :168)"] = new("""
            var client = new A2AClient(baseUrl, GetHttpClient(baseUrl));
            private HttpClient GetHttpClient(Uri baseUrl) => _clients.GetOrAdd(baseUrl.Authority, _ => new HttpClient());
            """, "http.new 1/0; sdk.client 1/0"),
        ["unguarded: a named door member with no guard (SneakernetTransport.ExportAsync)"] = new("""
            public sealed class SneakernetTransport
            {
                public async Task ExportAsync(string path, CancellationToken ct)
                {
                    await File.WriteAllTextAsync(path, "{}", ct);
                }
            }
            """, "door 1/0"),
        ["unguarded: a guard inside a nested if does not cover a door member's body"] = new("""
            public sealed class FileBasedSharedAdaptationStore
            {
                public Task BroadcastAsync(AdaptationRecord record, bool audit)
                {
                    if (audit)
                    {
                        _ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.MeshPublish, "EG-MESH-07", "host:shared"));
                    }

                    return File.WriteAllTextAsync(Path.Combine(Root, record.Id), record.Body);
                }
            }
            """, "door 1/0"),
        ["unguarded: a DNS lookup"] = new("""
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            """, "socket 1/0"),
        ["unguarded: a namespace-qualified ProcessStartInfo and Start (ProviderFactory.cs:704-716)"] = new("""
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            """, "process 2/0"),
        ["unguarded: an optional HttpClient parameter is still a parameter (CloudAvailabilityResolver.cs:29)"] = new("""
            public CloudAvailabilityResolver(IConfiguration configuration, HttpClient? httpClient = null, bool enableNetworkProbe = false)
            {
                _httpClient = httpClient;
            }
            """, "http.param 1/0"),
        ["unguarded: a using statement declares a local, not a parameter; its construction counts"] = new("""
            using (HttpClient c = new HttpClient())
            {
                await c.GetAsync(url);
            }
            """, "http.new 1/0"),

        // ── must fail a structural fact ─────────────────────────────────────────────────────────────────
        ["fails F5: a keyed chat client without UseAshlarGovernance"] = new("""
            services.AddKeyedChatClient("cloud:other", sp => new OllamaHttpChatClient(sp.GetRequiredService<IOptions<MeaiPipelineOptions>>()));
            """, "chat.register 2/0", "F5"),
        ["fails F4: a bare HttpClient singleton"] = new("""
            services.AddSingleton<HttpClient>();
            """, "banned 1/0", "F4"),
        ["fails F4: resolving typeof(HttpClient)"] = new("""
            var client = (HttpClient)sp.GetRequiredService(typeof(HttpClient));
            """, "banned 1/0", "F4"),
        ["fails F3: an AddHttpClient member that never installs the guard"] = new("""
            public static class Registration
            {
                public static IServiceCollection AddThing(this IServiceCollection services)
                {
                    services.AddHttpClient("x");
                    return services;
                }
            }
            """, "http.register 1/0", "F3"),
        ["fails F3: the guard installed in a sibling member does not count"] = new("""
            public static class Registration
            {
                public static void Install(IServiceCollection services) => services.AddAshlarEgressGuard();

                public static void Register(IServiceCollection services)
                {
                    services.AddHttpClient();
                }
            }
            """, "http.register 1/0", "F3"),

        // ── must not count as unguarded ─────────────────────────────────────────────────────────────────
        ["guarded: EgressHttp.CreateClient"] = new("""
            var client = EgressHttp.CreateClient(EgressFamilies.Mcp, "EG-XPT-06");
            """, "http.new 1/1"),
        ["guarded: a handler constructed inside EgressHttp.Wrap's arguments"] = new("""
            return EgressHttp.Wrap(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) }, EgressFamilies.Grpc, "EG-XPT-03");
            """, "http.new 2/2"),
        ["guarded: a handler local that a later Wrap names (DefaultGrpcChannelFactory.BuildHandler, 3b)"] = new("""
            private static HttpMessageHandler BuildHandler(GrpcTransportOptions options)
            {
                var handler = new HttpClientHandler();
                if (options.AllowUntrustedCertificates)
                    handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                return EgressHttp.Wrap(handler, EgressFamilies.Grpc, "EG-XPT-03");
            }
            """, "http.new 2/2"),
        ["guarded: a typed handler local, configured by member assignment, then wrapped (MeshAutoPullService, 3b)"] = new("""
            SocketsHttpHandler handler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
            handler.SslOptions.RemoteCertificateValidationCallback = ValidatePeer;
            return EgressHttp.Wrap(handler, EgressFamilies.MeshPull, "EG-MESH-04");
            """, "http.new 2/2"),
        ["guarded: factory.CreateClient is not a construction"] = new("""
            var named = factory.CreateClient("x");
            var raw = EgressHttp.CreateClient(EgressFamilies.Http, "EG-HTTP-06");
            """, "http.new 1/1"),
        ["store only: a Docker client is never http (DockerCommand.cs:538)"] = new("""
            var docker = new DockerClientConfiguration(uri).CreateClient();
            """, "store 1/0"),
        ["guarded: a constructor in a doc comment does not count"] = new("""
            /// <summary>Never <c>new HttpClient()</c>; see <see cref="EgressHttp"/>.</summary>
            public static HttpClient Make() => EgressHttp.CreateClient(EgressFamilies.Http, "EG-HTTP-05");
            """, "http.new 1/1"),
        ["guarded: a raw-string template is text, not a launch (MockScaffoldingResponder.Templates.cs:734)"] = new(""""
            var template = """
                var psi = new ProcessStartInfo("dotnet", "build");
                using var p = Process.Start(psi);
                """;
            _ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-01", "nuget-feeds"));
            Process.Start(psi);
            """", "process 1/1"),
        ["guarded: new HttpRequestMessage is not a client"] = new("""
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var client = EgressHttp.CreateClient(EgressFamilies.Http, "EG-HTTP-06");
            """, "http.new 1/1"),
        ["guarded: a field is not a parameter; the constructor parameter is"] = new("""
            public sealed class Thing
            {
                private readonly HttpClient _http;

                public Thing(HttpClient http) => _http = http;
            }
            """, "http.param 1/0"),
        ["guarded: a local transport send is not egress (ILocalTransport)"] = new("""
            await _transport.SendAsync(peerId, message, cancellationToken);
            var client = EgressHttp.CreateClient(EgressFamilies.Http, "EG-HTTP-05");
            """, "http.new 1/1"),
        ["guarded: an embedding generator call is not egress"] = new("""
            var vector = await generator.GenerateAsync(["text"], cancellationToken: ct);
            var client = EgressHttp.CreateClient(EgressFamilies.Http, "EG-HTTP-05");
            """, "http.new 1/1"),
        ["guarded: a guard call then the primitive in the same member (DotnetRunner, 3b)"] = new("""
            public static async Task<int> RunAsync(string arguments, IEgressGuard? guard = null)
            {
                _ = (guard ?? EgressGuard.ProcessDefault).Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-01", "nuget-feeds"));
                var psi = new ProcessStartInfo("dotnet", arguments) { RedirectStandardOutput = true };
                using var p = Process.Start(psi)!;
                await p.WaitForExitAsync();
                return p.ExitCode;
            }
            """, "process 2/2"),
        ["guarded: a guard in the enclosing block before a lambda holding the primitive (textual, not temporal)"] = new("""
            public void Map(WebApplication app, IEgressGuard guard, Stream stream)
            {
                _ = guard.Evaluate(new EgressRequest(EgressFamilies.MeshServe, "EG-MESH-03", "tcp://peer"));
                app.MapGet("/pkg", () =>
                {
                    return Results.Stream(stream, "application/json");
                });
            }
            """, "door 1/1"),
        ["guarded: AddHttpClient in a member that installs the guard (RunPod extension, 3b)"] = new("""
            public static IServiceCollection AddRunPod(this IServiceCollection services)
            {
                services.AddAshlarEgressGuard();
                services.AddHttpClient<IRunPodClient, RunPodHttpClient>((sp, client) => client.BaseAddress = new Uri("https://api.runpod.io"));
                return services;
            }
            """, "http.register 1/1"),
        ["guarded: an MCP transport handed an EgressHttp client, and the client created over it (3b)"] = new("""
            var transport = new HttpClientTransport(options, EgressHttp.CreateClient(EgressFamilies.Mcp, "EG-XPT-06"), _loggerFactory, ownsHttpClient: true);
            return await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: _loggerFactory, cancellationToken: ct);
            """, "http.new 1/1; sdk.client 2/2"),
        ["guarded: an A2A client over an EgressHttp client in the same file (3b)"] = new("""
            var client = new A2AClient(baseUrl, GetHttpClient(baseUrl));
            private HttpClient GetHttpClient(Uri baseUrl) => _clients.GetOrAdd(baseUrl.Authority, _ => EgressHttp.CreateClient(EgressFamilies.A2A, "EG-XPT-01"));
            """, "http.new 1/1; sdk.client 1/1"),
        ["guarded: a gRPC channel whose handler is wrapped in place"] = new("""
            var handler = new HttpClientHandler();
            return GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = EgressHttp.Wrap(handler, EgressFamilies.Grpc, "EG-XPT-03") });
            """, "http.new 2/2; sdk.client 1/1"),
        ["guarded: a named door member whose body holds the guard (NativeBundle.StageApp, 3b)"] = new("""
            public static class NativeBundle
            {
                public static void StageApp(string projectDir, string outDir, string site)
                {
                    _ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.FileExport, site, "file:" + outDir));
                    File.Copy(projectDir, outDir);
                }
            }
            """, "door 1/1"),
        ["guarded: a guard at the root of a top-level program precedes the exporter (Ashlar.API/Program.cs, 3b)"] = new("""
            var builder = WebApplication.CreateBuilder(args);
            _ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Telemetry, "EG-TEL-01", new Uri(endpoint)));
            builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddOtlpExporter());
            """, "telemetry 1/1"),

        // ── http.param: a declaration's parameter counts; nothing else that spells "HttpClient name" does ──
        ["not a parameter: an out argument, TryGetValue(k, out HttpClient? c) (A2AAgentTransport's client cache)"] = new("""
            private HttpClient GetHttpClient(Uri baseUrl, HttpClient fallback)
            {
                if (_httpClients.TryGetValue(baseUrl.Authority, out HttpClient? client))
                    return client;
                return fallback;
            }
            """, "http.param 1/0"),
        ["not a parameter: a tuple deconstruction"] = new("""
            public void Use(HttpClient seen)
            {
                (HttpClient client, string name) = Build();
                _ = client.GetAsync(name);
            }
            """, "http.param 1/0"),
        ["not a parameter: a tuple-typed field"] = new("""
            public sealed class Pool(HttpClient seen)
            {
                private readonly List<(HttpClient Client, string Name)> _clients = [(seen, "default")];
            }
            """, "http.param 1/0"),
        ["not a parameter: a typed lambda's parameter list"] = new("""
            public void Use(HttpClient seen)
            {
                Func<HttpClient, Task<HttpResponseMessage>> probe = (HttpClient c) => c.GetAsync("/health");
            }
            """, "http.param 1/0"),
        ["a parameter: an out parameter of a declared method"] = new("""
            public bool TryGetClient(string key, [NotNullWhen(true)] out HttpClient? client)
            {
                client = null;
                return false;
            }
            """, "http.param 1/0"),
        ["a parameter: an explicit interface implementation's (ISnsSignatureVerifier)"] = new("""
            public sealed class Verifier : ISnsSignatureVerifier
            {
                Task<bool> ISnsSignatureVerifier.VerifyAsync(SnsEnvelope envelope, HttpClient httpClient, CancellationToken ct) => Task.FromResult(true);
            }
            """, "http.param 1/0"),
        ["a parameter: a constructor with no modifier"] = new("""
            public sealed class Probe
            {
                private readonly HttpClient _http;

                Probe(HttpClient http) => _http = http;
            }
            """, "http.param 1/0"),
        ["a parameter: a method returning a tuple counts its parameter, not the tuple's element"] = new("""
            private static (HttpClient Client, int Retries) Configure(HttpClient http, int retries) => (http, retries);
            """, "http.param 1/0"),

        // ── known misses: pinned as the scan reads them today (a seen companion token, or the guarded count it
        // ── wrongly grants), so the change that teaches the scan flips them ──
        ["known miss: a guard in an unbraced if body counts though it may not run"] = new("""
            public void Run(ProcessStartInfo psi, IEgressGuard guard, bool audit)
            {
                if (audit) _ = guard.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-02", "host:dotnet"));
                using var p = Process.Start(psi);
            }
            """, "process 1/1"),
        ["known miss: a guard in an unbraced loop body counts though the loop may not run"] = new("""
            public void Run(ProcessStartInfo psi, IEgressGuard guard, string[] feeds)
            {
                foreach (var feed in feeds) _ = guard.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-02", feed));
                using var p = Process.Start(psi);
            }
            """, "process 1/1"),
        ["known miss: a guard in a sibling switch case counts for another case"] = new("""
            public void Run(ProcessStartInfo psi, IEgressGuard guard, int mode)
            {
                switch (mode)
                {
                    case 1:
                        _ = guard.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-02", "host:dotnet"));
                        break;
                    case 2:
                        Process.Start(psi);
                        break;
                }
            }
            """, "process 1/1"),
        ["known miss: a guard in an expression-bodied local function that is never called"] = new("""
            public void Run(ProcessStartInfo psi, IEgressGuard guard)
            {
                void Never() => _ = guard.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-02", "host:dotnet"));
                using var p = Process.Start(psi);
            }
            """, "process 1/1"),
        ["known miss: a guard in an expression-bodied lambda that is never invoked"] = new("""
            public void Run(ProcessStartInfo psi, IEgressGuard guard)
            {
                Func<EgressDecision> later = () => guard.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-02", "host:dotnet"));
                using var p = Process.Start(psi);
            }
            """, "process 1/1"),
        ["known miss: a using alias hides the type"] = new("""
            using H = System.Net.Http.HttpClient;
            var hidden = new H();
            var seen = new HttpClient();
            """, "http.new 1/0"),
        ["known miss: a construction inside an interpolation hole"] = new("""
            var text = $"timeout {new HttpClient().Timeout}";
            var seen = new HttpClient();
            """, "http.new 1/0"),
        ["known miss: an SDK client nobody taught the scan"] = new("""
            var openai = new OpenAIClient(apiKey);
            var channel = GrpcChannel.ForAddress(endpoint);
            """, "sdk.client 1/0"),
        ["known miss: a funnel caller choosing the executable (TimedProcess)"] = new("""
            await TimedProcess.RunAsync("curl", ["-fsSL", url], TimeSpan.FromMinutes(1), ct);
            Process.Start("dotnet", "--info");
            """, "process 1/0"),
        ["known miss: a file copy to removable media without a door"] = new("""
            File.Copy(package, "/media/usb/out.ashpkg", overwrite: true);
            var dest = MeshStore.Publish(store, json);
            """, "door 1/0"),
        ["known miss: a reflection-built client"] = new("""
            var hidden = (HttpClient)Activator.CreateInstance(Type.GetType("System.Net.Http.HttpClient")!)!;
            var seen = new HttpClient();
            """, "http.new 1/0"),
    };
}
