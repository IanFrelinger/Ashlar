using Ashlar.Abstractions.Security.Egress;
using Ashlar.Brick.Contracts;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

public sealed class CompositeCatalogRefusalTests
{
    [Fact]
    public void Refused_catalog_does_not_hide_an_available_catalog_and_warnings_are_windowed()
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "composite.catalog", new Uri("https://remote.example"))));
        var denied = new Mock<IRemoteBrickCatalog>();
        denied.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("wrapper", refusal));
        denied.Setup(x => x.GetByIdAsync("brick", It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("wrapper", refusal));
        var entry = new BrickCatalogEntryDto { Id = "brick", Name = "available", Description = "test", Category = "Control" };
        var allowed = new Mock<IRemoteBrickCatalog>();
        allowed.SetupGet(x => x.BaseUrl).Returns("https://allowed.example");
        allowed.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { entry });
        allowed.Setup(x => x.GetByIdAsync("brick", It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        allowed.Setup(x => x.GetCapabilitiesWithStalenessAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CapabilitiesFetchResult());
        var local = new Mock<IBrickRegistry>();
        local.Setup(x => x.GetAllBricks()).Returns(Array.Empty<DomainBrick>());
        var logger = new Mock<ILogger<CompositeBrickRegistry>>();
        using var client = new HttpClient();
        using var registry = new CompositeBrickRegistry(local.Object, [denied.Object, allowed.Object], client, logger.Object);
        registry.GetBrick("brick")!.Id.Should().Be("brick");
        registry.GetAllBricks().Should().ContainSingle().Which.Id.Should().Be("brick");
        logger.Verify(x => x.Log(LogLevel.Warning, It.Is<EventId>(e => e.Id == 7307),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        logger.Verify(x => x.Log(LogLevel.Warning, It.Is<EventId>(e => e.Id != 7307),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }
}
