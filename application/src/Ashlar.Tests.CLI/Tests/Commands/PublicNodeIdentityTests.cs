using System.Reflection;
using System.Text.Json;
using Ashlar.CLI.Commands.BackgroundAgent;
using Ashlar.Manifest.Signing;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// Public identity consumers are metadata only. Exercise discovery registration and the
/// actual hello handler in memory: no host start, listener, multicast or OS isolation probe.
/// </summary>
[Collection("MeshIntegration")]
public sealed class PublicNodeIdentityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ashlar-public-node-" + Guid.NewGuid().ToString("N"));
    private string Keys => Path.Combine(root, "keys");

    public PublicNodeIdentityTests()
    {
        Directory.CreateDirectory(Keys);
        // Discovery registration already resolves/creates its state directory. Provision it
        // before the byte/directory snapshot so this test isolates public-identity reads.
        Directory.CreateDirectory(Path.Combine(root, "state"));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    public static IEnumerable<object[]> PrivateStates()
    {
        foreach (var consumer in new[] { "heartbeat", "discovery", "hello" })
        foreach (var state in new[] { "absent", "corrupt", "mismatched", "directory" })
            yield return [consumer, state];
    }

    public static IEnumerable<object[]> PublicStates()
    {
        foreach (var consumer in new[] { "heartbeat", "discovery", "hello" })
        foreach (var state in new[] { "absent", "corrupt", "wrong-length", "directory" })
            yield return [consumer, state];
    }

    [Theory]
    [MemberData(nameof(PrivateStates))]
    public async Task Consumer_reads_public_identity_without_private_material(string consumer, string privateState)
    {
        var identity = OperatorKey.Generate(Path.Combine(root, "source"));
        File.WriteAllText(Path.Combine(Keys, "operator.pub"), identity.PublicKeyBase64);
        var privatePath = Path.Combine(Keys, "operator.key");
        switch (privateState)
        {
            case "corrupt": File.WriteAllText(privatePath, "not a private seed"); break;
            case "mismatched":
                var other = Path.Combine(root, "other");
                OperatorKey.Generate(other);
                File.Copy(Path.Combine(other, "operator.key"), privatePath);
                break;
            case "directory": Directory.CreateDirectory(privatePath); break;
        }

        var before = Snapshot();
        (await ReadConsumerAsync(consumer)).Should().Be(identity.Fingerprint);
        AssertUnchanged(before);
    }

    [Theory]
    [MemberData(nameof(PublicStates))]
    public async Task Consumer_does_not_advertise_absent_or_invalid_public_identity(string consumer, string publicState)
    {
        OperatorKey.Generate(Keys);
        var publicPath = Path.Combine(Keys, "operator.pub");
        switch (publicState)
        {
            case "absent": File.Delete(publicPath); break;
            case "corrupt": File.WriteAllText(publicPath, "not a public key"); break;
            case "wrong-length": File.WriteAllText(publicPath, Convert.ToBase64String(new byte[7])); break;
            case "directory": File.Delete(publicPath); Directory.CreateDirectory(publicPath); break;
        }

        // Neither a usable private seed nor retained/peer files are a metadata fallback.
        Directory.CreateDirectory(Path.Combine(Keys, "trusted"));
        File.Copy(Path.Combine(Keys, "operator.key"), Path.Combine(Keys, "trusted", "not-a-public-key.pub"));
        var before = Snapshot();
        (await ReadConsumerAsync(consumer)).Should().Be(consumer == "hello" ? "(unknown)" : null);
        AssertUnchanged(before);
    }

    private async Task<string?> ReadConsumerAsync(string consumer)
    {
        using var keys = new EnvironmentVariableScope("ASHLAR_KEY_DIR", Keys);
        if (consumer == "heartbeat") return NodeHeartbeat.TryFingerprint();
        if (consumer == "discovery")
        {
            using var enabled = new EnvironmentVariableScope("ASHLAR_MESH_DISCOVERY", "1");
            using var port = new EnvironmentVariableScope("ASHLAR_MESH_SERVE_PORT", "7420");
            using var state = new EnvironmentVariableScope("ASHLAR_STATE_DIR", Path.Combine(root, "state"));
            var services = new ServiceCollection();
            var configure = typeof(BackgroundAgentDaemonCommand).GetMethod("TryAddMeshDiscovery",
                BindingFlags.Static | BindingFlags.NonPublic);
            configure.Should().NotBeNull();
            configure!.Invoke(null, [services]);
            var settings = services.Single(d => d.ServiceType == typeof(MeshDiscoverySettings))
                .ImplementationInstance.Should().BeOfType<MeshDiscoverySettings>().Subject;
            return settings.Fingerprint;
        }

        using var service = new MeshServeService(new MeshServeSettings(7420, root, "public-metadata"),
            NullLogger<MeshServeService>.Instance);
        var build = typeof(MeshServeService).GetMethod("BuildApp", BindingFlags.Instance | BindingFlags.NonPublic);
        build.Should().NotBeNull();
        await using var app = (WebApplication)build!.Invoke(service, null)!;
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == "/mesh/v1/hello");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Request.Path = "/mesh/v1/hello";
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(body.ToArray());
        return json.RootElement.GetProperty("fingerprint").GetString();
    }

    private Dictionary<string, string> Snapshot()
    {
        var entries = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => "dir:" + Path.GetRelativePath(root, p), _ => string.Empty);
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            entries.Add("file:" + Path.GetRelativePath(root, path), Convert.ToBase64String(File.ReadAllBytes(path)));
        return entries;
    }

    private void AssertUnchanged(Dictionary<string, string> before) =>
        Snapshot().Should().BeEquivalentTo(before, "public metadata must not create, rewrite or remove key/state files");
}
