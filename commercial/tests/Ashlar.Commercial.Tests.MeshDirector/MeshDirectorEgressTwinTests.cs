using System.Collections.Concurrent;
using System.Reflection;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Commercial.MeshDirector;
using Xunit;

namespace Ashlar.Commercial.Tests.MeshDirector;

/// <summary>SPEC-007 PR 3b: the director client records a decision under EG-HTTP-07 when it sends.</summary>
public sealed class MeshDirectorEgressTwinTests
{
    private const string Refused = "http://127.0.0.1:1";

    [Fact]
    public async Task Director_client_records_EG_HTTP_07_and_keeps_its_timeout()
    {
        var sink = new SiteSink("EG-HTTP-07");
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var create = typeof(MeshDirectorCommand).GetMethod("CreateHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var client = (HttpClient)create.Invoke(null, [30])!;

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Refused + "/api/mesh/fleet/nodes"));

        Assert.NotEmpty(sink.Seen);
        Assert.All(sink.Seen, d => Assert.Equal(EgressFamilies.Http, d.Family));
    }

    private sealed class SiteSink(string site) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _seen = new();

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        public void Record(EgressDecision decision)
        {
            if (decision.Site == site && decision.Destination == Refused)
                _seen.Enqueue(decision);
        }
    }
}
