using Ashlar.Abstractions.Security.Egress;
using Ashlar.Agents.TestKit;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Infrastructure.Execution.Routing;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>Tests for run pod http client.</summary>
public sealed class RunPodHttpClientTests
{
    [Fact]
    public async Task Every_RunPod_operation_preserves_refusal_as_a_distinct_failure()
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "runpod-refusal", new Uri("https://remote.example"))));
        var calls = 0;
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("wrapper", refusal));
        })) { BaseAddress = new Uri("https://api.runpod.io/") };
        var client = new RunPodHttpClient(http, Options.Create(new RunPodBrickConfig()), NullLogger<RunPodHttpClient>.Instance);
        var job = new JobHandle { InstanceId = "instance", JobId = "job" };
        var errors = new[]
        {
            (await client.SpinUpInstance("model", "gpu")).Error,
            (await client.DispatchJob("instance", new RunPodJobPayload { ModelId = "model", Prompt = "prompt" })).Error,
            (await client.PollJobStatus(job)).Error,
            (await client.PullResults(job)).Error,
            (await client.TerminateInstance("instance")).Error
        };
        calls.Should().Be(5);
        errors.Should().OnlyContain(e => e != null && e.Code == "runpod.egress_refused" && e.Message == refusal.Message);
    }

    [Fact]
    public async Task PollJobStatus_MapsCancelledState_ToFailed()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Contain("/v2/jobs/job-123");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "status": "cancelled",
                  "message": "job cancelled by provider"
                }
                """, Encoding.UTF8, "application/json")
            });
        }))
        {
            BaseAddress = new Uri("https://api.runpod.io/")
        };

        var sut = new RunPodHttpClient(
            httpClient,
            Options.Create(new RunPodBrickConfig()),
            NullLogger<RunPodHttpClient>.Instance);

        var result = await sut.PollJobStatus(new JobHandle
        {
            InstanceId = "instance-1",
            JobId = "job-123"
        });

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.State.Should().Be(RunPodJobState.Failed);
        result.Value.IsTerminal.Should().BeTrue();
        result.Value.Message.Should().Contain("cancelled");
    }

    /// <summary>Tests for fake http message handler.</summary>
}
