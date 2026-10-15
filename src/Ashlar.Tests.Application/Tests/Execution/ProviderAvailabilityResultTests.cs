using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Execution.Ports;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Application.Tests.Execution;

/// <summary>
/// Card #802: <see cref="ProviderAvailabilityResult"/> lives in Core.Application and must be covered
/// by this project's tests (testing-strategy pivot: new Core.Application source).
/// </summary>
public sealed class ProviderAvailabilityResultTests
{
    [Fact]
    public void Available_probe_carries_no_refusal()
    {
        var result = new ProviderAvailabilityResult(available: true);
        result.Available.Should().BeTrue();
        result.WasRefused.Should().BeFalse();
        result.Refusal.Should().BeNull();
    }

    [Fact]
    public void Refused_probe_exposes_typed_refusal_identity()
    {
        var refusal = new EgressRefusedException(new EgressGuard("secure-workstation", "enforce")
            .Evaluate(new EgressRequest(EgressFamilies.ModelLegacy, "EG-MDL-07", "http://127.0.0.1:11434")));
        var result = new ProviderAvailabilityResult(available: false, refusal);

        result.Available.Should().BeFalse();
        result.WasRefused.Should().BeTrue();
        result.Refusal.Should().BeSameAs(refusal);
        result.Refusal!.Ref.Should().Be(refusal.Ref);
        result.Refusal.ErrorCode.Should().Be("EGRESS_REFUSED");
    }

    [Fact]
    public void Stub_factory_probe_matches_IsProviderAvailable_without_refusal()
    {
        IProviderFactory factory = new StubFactory(available: false);
        var probe = factory.ProbeProviderAvailability("ollama");
        probe.Available.Should().BeFalse();
        probe.WasRefused.Should().BeFalse();
        probe.Refusal.Should().BeNull();
    }

    private sealed class StubFactory(bool available) : IProviderFactory
    {
        public bool IsProviderAvailable(string provider) => available;

        public ProviderAvailabilityResult ProbeProviderAvailability(string provider) =>
            new(IsProviderAvailable(provider));

        public Task<string> ExecuteLLMAsync(string provider, string systemPrompt, string userPrompt, object config, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> ExecuteVisionAsync(string provider, string systemPrompt, string userPrompt, byte[] imageBytes, object config, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> ExecuteVisionMultiFrameAsync(string provider, string systemPrompt, string userPrompt, IReadOnlyList<byte[]> frameBytes, object config, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> ExecuteVideoAsync(string systemPrompt, string userPrompt, IReadOnlyList<byte[]> frameBytes, object config, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task EnsureOllamaReachableAsync(bool requireVisionModel, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
