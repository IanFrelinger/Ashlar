using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Ashlar.Commercial.MeshDirector;
using Xunit;
using Xunit.Abstractions;

namespace Ashlar.Commercial.Tests.MeshDirector;

/// <summary>The tests that swap <see cref="Console.Out"/> run alone, so no other test's output lands in a capture.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MeshDirectorConsoleCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "MeshDirector console capture";
}

/// <summary>
/// Characterizes, byte for byte, the JSON the director CLI sends and prints: the registration request body
/// (<c>JsonSerializer.Serialize</c> of the body dictionary) and the formatted response
/// (<c>JsonSerializer.Serialize(doc.RootElement, PrettyJson)</c>), plus the status line before it.
/// </summary>
/// <remarks>
/// SPEC-007 PR 3b, owner answer Q-A = A: routing EG-HTTP-07 through <c>EgressHttp</c> gives this net8.0 executable a
/// ProjectReference to Ashlar.Abstractions, which brings an app-local System.Text.Json 10 in place of the shared
/// framework's System.Text.Json 8. These expectations were observed green BEFORE that reference was added, and must
/// stay byte-identical after it: a changed byte here is a change to what the director CLI sends or prints, and is
/// reported, not absorbed by editing the expected text. The inputs cover nested objects and arrays, non-ASCII text, a
/// quote, a backslash, <c>&lt;</c>, <c>&amp;</c>, <c>'</c>, <c>+</c>, U+2028, and the numbers 1.0, 1e3 and -0.
/// </remarks>
[Collection(MeshDirectorConsoleCollection.Name)]
public sealed class MeshDirectorJsonOutputCharacterizationTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private readonly ITestOutputHelper _output;

    /// <summary>Initializes a new instance of the <see cref="MeshDirectorJsonOutputCharacterizationTests"/> class.</summary>
    public MeshDirectorJsonOutputCharacterizationTests(ITestOutputHelper output)
    {
        _output = output;
        var stj = typeof(JsonSerializer).Assembly;
        _output.WriteLine($"System.Text.Json {stj.GetName().Version} from {stj.Location}");
    }

    [Fact]
    public async Task Formatted_json_response_is_printed_byte_identical()
    {
        const string body =
            "{\"name\":\"caf\u00e9 \u65e5\u672c\",\"quote\":\"say \\\"hi\\\"\",\"path\":\"C:\\\\dir\\\\file\","
            + "\"html\":\"<b>&amp;</b>\",\"sep\":\"a\u2028b\",\"escaped\":\"\\u00e9\\u2028\\/\","
            + "\"nested\":{\"list\":[1.0,1e3,-0,{\"deep\":[true,false,null]}],\"empty\":{},\"none\":[]},\"plain\":\"ascii\"}";
        using var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            ReasonPhrase = "OK",
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var (stdout, stderr, exitCode) = await CaptureAsync(() => WriteResponseAsync(resp, preferFormattedJson: true));

        Assert.Equal(
            Lines(
                "200 OK",
                "{",
                "  \"name\": \"caf\\u00E9 \\u65E5\\u672C\",",
                "  \"quote\": \"say \\u0022hi\\u0022\",",
                "  \"path\": \"C:\\\\dir\\\\file\",",
                "  \"html\": \"\\u003Cb\\u003E\\u0026amp;\\u003C/b\\u003E\",",
                "  \"sep\": \"a\\u2028b\",",
                "  \"escaped\": \"\\u00E9\\u2028/\",",
                "  \"nested\": {",
                "    \"list\": [",
                "      1.0,",
                "      1e3,",
                "      -0,",
                "      {",
                "        \"deep\": [",
                "          true,",
                "          false,",
                "          null",
                "        ]",
                "      }",
                "    ],",
                "    \"empty\": {},",
                "    \"none\": []",
                "  },",
                "  \"plain\": \"ascii\"",
                "}"),
            stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Unformatted_and_unparseable_bodies_are_printed_verbatim()
    {
        const string text = "caf\u00e9 <&> \"q\" \\ a\u2028b";
        using var plain = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new StringContent(text, Encoding.UTF8, "text/plain"),
        };
        using var broken = new HttpResponseMessage(HttpStatusCode.OK)
        {
            ReasonPhrase = "OK",
            Content = new StringContent("{\"a\":1.0,", Encoding.UTF8, "application/json"),
        };

        var (plainOut, plainErr, plainExit) = await CaptureAsync(() => WriteResponseAsync(plain, preferFormattedJson: true));
        var (brokenOut, brokenErr, brokenExit) = await CaptureAsync(() => WriteResponseAsync(broken, preferFormattedJson: true));

        Assert.Equal(Lines("400 Bad Request", text), plainOut);
        Assert.Equal(string.Empty, plainErr);
        Assert.Equal(1, plainExit);
        Assert.Equal(Lines("200 OK", "{\"a\":1.0,"), brokenOut);
        Assert.Equal(string.Empty, brokenErr);
        Assert.Equal(0, brokenExit);
    }

    [Fact]
    public async Task Register_request_body_and_formatted_response_are_byte_identical()
    {
        const string responseBody =
            "{\"id\":\"peer-\u00e9\",\"tags\":[\"<a>\",\"b&c\"],\"score\":1.0,\"big\":1e3,\"neg\":-0,"
            + "\"nested\":{\"ok\":true,\"none\":null,\"sep\":\"x\u2028y\"}}";
        using var server = LoopbackServer.Start("201 Created", responseBody);

        var (stdout, stderr, exitCode) = await CaptureAsync(() => InvokeRegisterAsync(
            baseUrl: server.BaseUrl,
            apiKey: "k",
            meshToken: "t",
            timeoutSeconds: 30,
            json: true,
            peerId: "  peer-\u00e9 <&> \"q\" \\ a\u2028b  ",
            apiBaseUrl: " https://hub.example:8443/api?x=1&y=2 ",
            trustTier: "Trusted",
            peerRegistrationKey: "reg+key'"));
        var request = await server.Request;

        const string expectedPayload =
            "{\"peerId\":\"peer-\\u00E9 \\u003C\\u0026\\u003E \\u0022q\\u0022 \\\\ a\\u2028b\","
            + "\"apiBaseUrl\":\"https://hub.example:8443/api?x=1\\u0026y=2\","
            + "\"trustTier\":\"Trusted\","
            + "\"peerRegistrationKey\":\"reg\\u002Bkey\\u0027\"}";
        Assert.Equal(expectedPayload, Encoding.UTF8.GetString(request.Body));
        Assert.Equal(
            "POST /api/mesh/fleet/nodes HTTP/1.1\r\n"
            + $"Host: 127.0.0.1:{server.Port}\r\n"
            + "X-Ashlar-Api-Key: k\r\n"
            + "X-Ashlar-Mesh-Token: t\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + $"Content-Length: {Encoding.UTF8.GetByteCount(expectedPayload)}\r\n"
            + "\r\n",
            request.Head);
        Assert.Equal(
            Lines(
                "201 Created",
                "{",
                "  \"id\": \"peer-\\u00E9\",",
                "  \"tags\": [",
                "    \"\\u003Ca\\u003E\",",
                "    \"b\\u0026c\"",
                "  ],",
                "  \"score\": 1.0,",
                "  \"big\": 1e3,",
                "  \"neg\": -0,",
                "  \"nested\": {",
                "    \"ok\": true,",
                "    \"none\": null,",
                "    \"sep\": \"x\\u2028y\"",
                "  }",
                "}"),
            stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(0, exitCode);
    }

    private static string Lines(params string[] lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.Append(line).Append(Environment.NewLine);
        return sb.ToString();
    }

    private static Task WriteResponseAsync(HttpResponseMessage resp, bool preferFormattedJson) =>
        (Task)typeof(MeshDirectorCommand).GetMethod("WriteResponseAsync", PrivateStatic)!
            .Invoke(null, [resp, preferFormattedJson])!;

    private static Task InvokeRegisterAsync(
        string baseUrl,
        string apiKey,
        string meshToken,
        int timeoutSeconds,
        bool json,
        string peerId,
        string apiBaseUrl,
        string trustTier,
        string peerRegistrationKey) =>
        (Task)typeof(MeshDirectorCommand).GetMethod("InvokeRegisterAsync", PrivateStatic)!
            .Invoke(null, [baseUrl, apiKey, meshToken, timeoutSeconds, json, peerId, apiBaseUrl, trustTier, peerRegistrationKey])!;

    private static async Task<(string Stdout, string Stderr, int ExitCode)> CaptureAsync(Func<Task> act)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalExitCode = Environment.ExitCode;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            Environment.ExitCode = -1;
            await act().ConfigureAwait(false);
            return (stdout.ToString(), stderr.ToString(), Environment.ExitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.ExitCode = originalExitCode;
        }
    }

    /// <summary>A one-shot HTTP/1.1 server on loopback that records the raw request and answers with a fixed body.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

        private LoopbackServer(TcpListener listener, string status, string body)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Request = ServeOnceAsync(status, body);
        }

        public int Port { get; }

        public string BaseUrl => $"http://127.0.0.1:{Port}";

        public Task<(string Head, byte[] Body)> Request { get; }

        public static LoopbackServer Start(string status, string body)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LoopbackServer(listener, status, body);
        }

        public void Dispose()
        {
            _listener.Stop();
            _timeout.Dispose();
        }

        private async Task<(string Head, byte[] Body)> ServeOnceAsync(string status, string body)
        {
            using var client = await _listener.AcceptTcpClientAsync(_timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var received = new List<byte>();
            var buffer = new byte[4096];
            var headEnd = -1;
            while (headEnd < 0)
            {
                var n = await stream.ReadAsync(buffer, _timeout.Token).ConfigureAwait(false);
                if (n == 0)
                    throw new IOException("The client closed the connection before the request head ended.");
                received.AddRange(buffer.AsSpan(0, n).ToArray());
                headEnd = IndexOfHeadEnd(received);
            }

            var head = Encoding.ASCII.GetString(received.GetRange(0, headEnd + 4).ToArray());
            var length = ContentLength(head);
            while (received.Count - (headEnd + 4) < length)
            {
                var n = await stream.ReadAsync(buffer, _timeout.Token).ConfigureAwait(false);
                if (n == 0)
                    throw new IOException("The client closed the connection before the request body ended.");
                received.AddRange(buffer.AsSpan(0, n).ToArray());
            }

            var requestBody = received.GetRange(headEnd + 4, length).ToArray();
            var payload = Encoding.UTF8.GetBytes(body);
            var responseHead = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: application/json; charset=utf-8\r\n"
                + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHead, _timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(payload, _timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(_timeout.Token).ConfigureAwait(false);
            return (head, requestBody);
        }

        private static int IndexOfHeadEnd(List<byte> bytes)
        {
            for (var i = 0; i + 3 < bytes.Count; i++)
            {
                if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                    return i;
            }

            return -1;
        }

        private static int ContentLength(string head)
        {
            foreach (var line in head.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    return int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }

            return 0;
        }
    }
}
