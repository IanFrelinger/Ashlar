using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Core.Application.ModelArtifacts;
using Ashlar.Core.Application.ModelArtifacts.Ports;
using Ashlar.Infrastructure.ModelArtifacts;
using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Tests.Infrastructure.Tests.ModelArtifacts;

/// <summary>Tests for model artifact catalog service.</summary>
public sealed class ModelArtifactCatalogServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refused_source_preserves_other_results_and_emits_windowed_warnings(bool duringAvailability)
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "catalog", new Uri("https://remote.example"))));
        var source = new Mock<IModelArtifactCatalogSource>();
        source.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        if (duringAvailability)
            source.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("wrapped", refusal));
        else
            source.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("wrapped", refusal));
        var logger = new Mock<ILogger<ModelArtifactCatalogService>>();
        using var sut = new ModelArtifactCatalogService(new[] { source.Object,
            new StubSource("local", true, [new ModelArtifactRecord("m1", "local", ModelArtifactKind.OllamaModel, 1)]) }, logger.Object);
        for (var i = 0; i < 3; i++)
            (await sut.ListAllAsync()).Select(x => x.Id).Should().Equal("m1");
        logger.Verify(x => x.Log(LogLevel.Warning, It.Is<EventId>(e => e.Id == 7307),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        sut.Dispose();
        logger.Verify(x => x.Log(LogLevel.Warning, It.Is<EventId>(e => e.Id == 7307),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Exactly(2));
        logger.Verify(x => x.Log(LogLevel.Warning, It.Is<EventId>(e => e.Id != 7307),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    [Fact]
    public async Task ListAllAsync_ExcludesRemoteInstallableSources()
    {
        var local = new StubSource("local", true, [new ModelArtifactRecord("m1", "local", ModelArtifactKind.OllamaModel, 1)]);
        var remote = new StubRemoteSource(
            "remote",
            true,
            [new ModelArtifactRecord("big", "remote", ModelArtifactKind.OllamaRemoteLibraryModel, 999)]);

        var sut = new ModelArtifactCatalogService(
            new IModelArtifactCatalogSource[] { remote, local },
            NullLogger<ModelArtifactCatalogService>.Instance);

        var all = await sut.ListAllAsync();
        all.Should().ContainSingle();
        all[0].Id.Should().Be("m1");

        var installable = await sut.ListInstallableAsync();
        installable.Should().ContainSingle();
        installable[0].Id.Should().Be("big");
    }

    [Fact]
    public async Task ListAllAsync_MergesAllAvailableSources()
    {
        var a = new StubSource("a", true, [new ModelArtifactRecord("m1", "a", ModelArtifactKind.OllamaModel, 1)]);
        var b = new StubSource("b", false, [new ModelArtifactRecord("m2", "b", ModelArtifactKind.OllamaModel, 2)]);
        var c = new StubSource("c", true, [new ModelArtifactRecord("m3", "c", ModelArtifactKind.OllamaModel, 3)]);

        var sut = new ModelArtifactCatalogService(
            new IModelArtifactCatalogSource[] { a, b, c },
            NullLogger<ModelArtifactCatalogService>.Instance);

        var all = await sut.ListAllAsync();

        all.Should().HaveCount(2);
        all.Select(x => x.Id).Should().BeEquivalentTo("m1", "m3");
    }

    /// <summary>Tests for stub remote source.</summary>
    private sealed class StubRemoteSource : StubSource, IRemoteInstallableModelArtifactSource
    {
        public StubRemoteSource(string sourceId, bool available, IReadOnlyList<ModelArtifactRecord> items)
            : base(sourceId, available, items)
        {
        }
    }

    /// <summary>Tests for stub source.</summary>
    private class StubSource : IModelArtifactCatalogSource
    {
        private readonly bool _available;
        private readonly IReadOnlyList<ModelArtifactRecord> _items;

        public StubSource(string sourceId, bool available, IReadOnlyList<ModelArtifactRecord> items)
        {
            SourceId = sourceId;
            _available = available;
            _items = items;
        }

        /// <summary>Source id.</summary>
        public string SourceId { get; }

        /// <summary>Returns whether  available async.</summary>
        /// <param name="default">Default.</param>
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_available);

        /// <summary>List async.</summary>
        /// <param name="default">Default.</param>
        public Task<IReadOnlyList<ModelArtifactRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_items);
    }
}
