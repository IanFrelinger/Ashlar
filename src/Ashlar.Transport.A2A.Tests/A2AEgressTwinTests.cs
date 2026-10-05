using System.Collections.Concurrent;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ashlar.Transport.A2A.Tests;

/// <summary>SPEC-007 PR 3b: the per-authority A2A client records a decision under EG-XPT-01 when it sends.</summary>
public sealed class A2AEgressTwinTests
{
    private const string Refused = "http://127.0.0.1:1";

    [Fact]
    public async Task Agent_card_fetch_over_the_transport_client_records_EG_XPT_01()
    {
        var sink = new SiteSink("EG-XPT-01");
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var transport = new A2AAgentTransport(Options.Create(new A2ATransportOptions()), NullLogger<A2AAgentTransport>.Instance);

        var health = await transport.CheckEndpointAsync("a2a+" + Refused + "/api/a2a/twin");

        health.IsHealthy.Should().BeFalse();
        sink.Seen.Should().NotBeEmpty().And.OnlyContain(d => d.Family == EgressFamilies.A2A);
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
