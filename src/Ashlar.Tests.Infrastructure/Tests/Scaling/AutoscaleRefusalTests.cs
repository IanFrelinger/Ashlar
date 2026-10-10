using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Scaling.Models;
using Ashlar.Core.Application.Scaling.Ports;
using Ashlar.Infrastructure.Scaling;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Scaling;

public sealed class AutoscaleRefusalTests
{
    [Fact]
    public async Task Refused_ticks_are_windowed_and_do_not_stop_later_ticks()
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Process, "autoscale", new Uri("https://remote.example"))));
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var scaler = new Mock<IWorkloadScaler>();
        scaler.Setup(x => x.ListWorkloadsAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (Interlocked.Increment(ref calls) <= 2) throw new IOException("wrapper", refusal);
            continued.TrySetResult();
            return Task.FromResult<IReadOnlyList<WorkloadDescriptor>>(Array.Empty<WorkloadDescriptor>());
        });
        var logger = new RefusalLogger();
        using var service = new ElasticWorkloadAutoscaleService(scaler.Object, Mock.Of<IWorkloadDemandSignal>(),
            Mock.Of<IWorkloadScalePolicy>(), Options.Create(new WorkloadScalerOptions
            { Autoscale = new WorkloadAutoscaleOptions { IntervalSeconds = 5 } }), logger);
        await service.StartAsync(CancellationToken.None);
        try { await continued.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { await service.StopAsync(CancellationToken.None); }
        calls.Should().BeGreaterThanOrEqualTo(3);
        logger.Warnings.Should().Equal(7307);
        service.Dispose();
        logger.Warnings.Should().Equal(7307, 7307);
    }

    private sealed class RefusalLogger : ILogger<ElasticWorkloadAutoscaleService>
    {
        internal List<int> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (level == LogLevel.Warning) Warnings.Add(id.Id);
        }
    }
}
