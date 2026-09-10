using Ashlar.Agents.TestKit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Infrastructure.Maintenance.Sdk.Extensions;
using Ashlar.Infrastructure.Testing.Agents;
using Ashlar.Infrastructure.Testing.CodeAnalysis;
using Ashlar.Infrastructure.Testing.Docker;
using Ashlar.Infrastructure.Testing.ExecutionPlatform;
using System.Net;
using System.Text;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests;

/// <summary>Tests for infrastructure testing gap coverage.</summary>
[Collection("CompatibilityTestHooks")]
public class InfrastructureTestingGapCoverageTests
{
    [Fact]
    public void AgentPlatformCompatibilityChecker_returns_platform_result()
    {
        var result = AgentPlatformCompatibilityChecker.CheckCompatibility();
        result.Platform.Should().NotBeNullOrWhiteSpace();
        result.Issues.Should().NotBeNull();
    }

    [Fact]
    public void CodeAnalysisPlatformCompatibilityChecker_returns_platform_result()
    {
        var result = PlatformCompatibilityChecker.CheckCompatibility();
        result.Platform.Should().NotBeNullOrWhiteSpace();
        result.IsCompatible.Should().BeTrue();
    }

    [Fact]
    public void Docker_result_records_round_trip()
    {
        new DockerBuildResult(true, null, TimeSpan.FromSeconds(1)).Success.Should().BeTrue();

        var failed = new DockerRunResult(false, 1, "out", "err", "cid", TimeSpan.FromMilliseconds(500));
        failed.Success.Should().BeFalse();
        failed.ExitCode.Should().Be(1);
        failed.StandardOutput.Should().Be("out");
        failed.StandardError.Should().Be("err");
        failed.ContainerId.Should().Be("cid");
        failed.Duration.Should().Be(TimeSpan.FromMilliseconds(500));

        var succeeded = new DockerRunResult(true, 0, "stdout", "stderr", null, TimeSpan.FromSeconds(2));
        succeeded.Success.Should().BeTrue();
        succeeded.ExitCode.Should().Be(0);
        succeeded.StandardOutput.Should().Be("stdout");
        succeeded.StandardError.Should().Be("stderr");
        succeeded.ContainerId.Should().BeNull();
        succeeded.Duration.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RemoteExecutionPlatform_builds_runs_and_handles_failures()
    {
        var handler = StubHttpMessageHandler.FromSync((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("status"))
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            if (req.RequestUri.AbsolutePath.Contains("build"))
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, """{"success":true,"durationMs":100}""");
            if (req.RequestUri.AbsolutePath.Contains("run"))
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, """{"success":true,"exitCode":0,"standardOutput":"ok","standardError":"","containerId":"c1","durationMs":50}""");
            /// <summary>Json.</summary>
            return Json(HttpStatusCode.NotFound, "{}");
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
        var platform = new RemoteExecutionPlatform(client, NullLogger<RemoteExecutionPlatform>.Instance);
        platform.PlatformName.Should().Be("Remote");

        (await platform.IsAvailableAsync()).Should().BeTrue();
        (await platform.BuildImageAsync("Dockerfile", "tag:latest", ".", null)).Success.Should().BeTrue();
        (await platform.RunContainerAsync("tag:latest", ["echo", "hi"])).StandardOutput.Should().Be("ok");
        await platform.RemoveContainerAsync("c1");
        await platform.RemoveImageAsync("tag:latest");
    }

    [Fact]
    public async Task RemoteExecutionPlatform_returns_false_when_unreachable()
    {
        using var client = new HttpClient(StubHttpMessageHandler.FromSync((_, _) => throw new HttpRequestException("down")))
        {
            BaseAddress = new Uri("https://remote.example/"),
        };
        var platform = new RemoteExecutionPlatform(client);
        (await platform.IsAvailableAsync()).Should().BeFalse();
        (await platform.BuildImageAsync("Dockerfile", "tag", ".")).Success.Should().BeFalse();
    }

    [Fact]
    public void AddArtifactCleanup_registers_cleanup_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArtifactCleanup();

        services.BuildServiceProvider().GetService<Ashlar.Core.Application.Maintenance.Ports.IArtifactCleanupService>()
            .Should().NotBeNull();
    }

    /// <summary>Json.</summary>
    /// <param name="status">Status.</param>
    /// <param name="json">Json.</param>
    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Tests for fake testing handler.</summary>
}
