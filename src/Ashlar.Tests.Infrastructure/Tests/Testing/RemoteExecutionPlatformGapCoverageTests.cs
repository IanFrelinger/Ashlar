using Ashlar.Agents.TestKit;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using FluentAssertions;
using Ashlar.Infrastructure.Testing;
using Ashlar.Infrastructure.Testing.Docker;
using Ashlar.Infrastructure.Testing.ExecutionPlatform;
using Xunit;
using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;
using Moq;

namespace Ashlar.Tests.Infrastructure.Tests.Testing;

/// <summary>Tests for remote execution platform gap coverage.</summary>
public sealed class RemoteExecutionPlatformGapCoverageTests
{
    [Fact]
    public async Task Refused_operations_return_redacted_failures_and_one_windowed_warning()
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "remote.execution", new Uri("https://remote.example"))));
        var calls = 0;
        using var client = new HttpClient(StubHttpMessageHandler.FromSync((_, _) =>
        {
            calls++;
            throw new IOException("private-canary", refusal);
        })) { BaseAddress = new Uri("https://remote.example/") };
        var logger = new Mock<ILogger<RemoteExecutionPlatform>>();
        using var platform = new RemoteExecutionPlatform(client, logger.Object);
        (await platform.IsAvailableAsync()).Should().BeFalse();
        var build = await platform.BuildImageAsync("Dockerfile", "tag", ".");
        build.Success.Should().BeFalse();
        build.ErrorMessage.Should().Be(refusal.Message);
        var run = await platform.RunContainerAsync("tag", ["test"]);
        run.Success.Should().BeFalse();
        run.StandardError.Should().Be(refusal.Message);
        run.StandardOutput.Should().BeEmpty();
        calls.Should().Be(3);
        logger.Verify(x => x.Log(LogLevel.Warning, It.Is<EventId>(e => e.Id == 7307),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        logger.Verify(x => x.Log(It.IsAny<LogLevel>(), It.Is<EventId>(e => e.Id != 7307),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    [Fact]
    public void Constructor_throws_for_null_http_client()
    {
        var act = () => new RemoteExecutionPlatform(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("httpClient");
    }

    [Fact]
    public async Task BuildImageAsync_returns_failure_details_from_remote_response()
    {
        var handler = StubHttpMessageHandler.FromSync((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("build", StringComparison.Ordinal))
            {
                /// <summary>Json.</summary>
                /// <param name="dockerfile"">Dockerfile".</param>
                return Json(HttpStatusCode.OK, """{"success":false,"errorMessage":"bad dockerfile","durationMs":25}""");
            }

            /// <summary>Json.</summary>
            return Json(HttpStatusCode.NotFound, "{}");
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://remote.example/") };
        var platform = new RemoteExecutionPlatform(client);

        var result = await platform.BuildImageAsync("Dockerfile", "tag:latest", ".");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("bad dockerfile");
    }

    /// <summary>Json.</summary>
    /// <param name="status">Status.</param>
    /// <param name="json">Json.</param>
    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

}
