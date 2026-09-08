using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.BackgroundAgents.Trust;
using Ashlar.BackgroundAgents.WebSearch;
using Ashlar.Infrastructure.Execution;
using Ashlar.Core.Application.Execution.Ports;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Trust;

/// <summary>Tests for sanitizing provider factory.</summary>
public sealed class SanitizingProviderFactoryTests
{
    [Fact]
    public async Task ExecuteLLMAsync_WhenProxyAllows_DelegatesToInner()
    {
        var infraMock = new Mock<Ashlar.Infrastructure.Execution.IProviderFactory>();
        infraMock.Setup(x => x.ExecuteLLMAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("response");

        var proxy = new CloudSanitizationProxy(contentFilter: null);
        var factory = new SanitizingProviderFactory(
            infraMock.Object,
            proxy,
            NullLogger<SanitizingProviderFactory>.Instance);

        var result = await factory.ExecuteLLMAsync("mock", "sys", "user", new { }, default);

        result.Should().Be("response");
        infraMock.Verify(x => x.ExecuteLLMAsync("mock", "sys", "user", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteLLMAsync_WhenProxyBlocks_Throws()
    {
        var infraMock = new Mock<Ashlar.Infrastructure.Execution.IProviderFactory>();
        var filter = new SensitiveContentFilter();
        var proxy = new CloudSanitizationProxy(filter);
        var factory = new SanitizingProviderFactory(
            infraMock.Object,
            proxy,
            NullLogger<SanitizingProviderFactory>.Instance);

        var act = () => factory.ExecuteLLMAsync("mock", "sys", "user@secret.com", new { }, default);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*blocked*");
        infraMock.Verify(x => x.ExecuteLLMAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteLLMAsync_BlocksWhenSanitizerDenies()
    {
        var infraMock = new Mock<Ashlar.Infrastructure.Execution.IProviderFactory>();
        var proxy = new Mock<ICloudSanitizationProxy>();
        proxy.Setup(p => p.SanitizeForCloud(It.IsAny<OutgoingContext>(), It.IsAny<CancellationToken>()))
            .Returns(SanitizationResult.Blocked("blocked"));

        var factory = new SanitizingProviderFactory(infraMock.Object, proxy.Object, NullLogger<SanitizingProviderFactory>.Instance);

        var act = () => factory.ExecuteLLMAsync("openai", "sys", "user", new { }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*blocked*");
        infraMock.Verify(i => i.ExecuteLLMAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteLLMAsync_DelegatesSanitizedPrompts()
    {
        var infraMock = new Mock<Ashlar.Infrastructure.Execution.IProviderFactory>();
        infraMock.Setup(i => i.ExecuteLLMAsync("openai", "clean-sys", "clean-user", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ok");

        var proxy = new Mock<ICloudSanitizationProxy>();
        proxy.Setup(p => p.SanitizeForCloud(It.IsAny<OutgoingContext>(), It.IsAny<CancellationToken>()))
            .Returns(SanitizationResult.AllowedWith(new OutgoingContext
            {
                SystemPrompt = "clean-sys",
                UserPrompt = "clean-user",
                Provider = "openai",
            }));

        var factory = new SanitizingProviderFactory(infraMock.Object, proxy.Object, NullLogger<SanitizingProviderFactory>.Instance);
        var result = await factory.ExecuteLLMAsync("openai", "dirty-sys", "dirty-user", new { }, CancellationToken.None);

        result.Should().Be("ok");
    }

    [Fact]
    public async Task ExecuteVisionAsync_BlocksWhenSanitizerDenies()
    {
        var infraMock = new Mock<Ashlar.Infrastructure.Execution.IProviderFactory>();
        var proxy = new Mock<ICloudSanitizationProxy>();
        proxy.Setup(p => p.SanitizeForCloud(It.IsAny<OutgoingContext>(), It.IsAny<CancellationToken>()))
            .Returns(SanitizationResult.Blocked("vision blocked"));

        var factory = new SanitizingProviderFactory(infraMock.Object, proxy.Object, NullLogger<SanitizingProviderFactory>.Instance);
        var act = () => factory.ExecuteVisionAsync("openai", "sys", "user", new byte[] { 1 }, new { }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*vision blocked*");
    }

    [Fact]
    public async Task ExecuteVisionAsync_DelegatesWhenAllowed()
    {
        var infraMock = new Mock<Ashlar.Infrastructure.Execution.IProviderFactory>();
        infraMock.Setup(i => i.ExecuteVisionAsync("openai", "sys", "user", It.IsAny<byte[]>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("vision-ok");

        var proxy = new Mock<ICloudSanitizationProxy>();
        proxy.Setup(p => p.SanitizeForCloud(It.IsAny<OutgoingContext>(), It.IsAny<CancellationToken>()))
            .Returns(SanitizationResult.AllowedWith(new OutgoingContext
            {
                SystemPrompt = "sys",
                UserPrompt = "user",
                Provider = "openai",
            }));

        var factory = new SanitizingProviderFactory(infraMock.Object, proxy.Object, NullLogger<SanitizingProviderFactory>.Instance);
        var result = await factory.ExecuteVisionAsync("openai", "sys", "user", new byte[] { 1 }, new { }, CancellationToken.None);

        result.Should().Be("vision-ok");
    }

    [Fact]
    public async Task ExecuteVisionMultiFrameAsync_DelegatesWhenAllowed()
    {
        var infraMock = new Mock<Ashlar.Infrastructure.Execution.IProviderFactory>();
        infraMock.Setup(i => i.ExecuteVisionMultiFrameAsync(
                "openai",
                "sys",
                "user",
                It.IsAny<IReadOnlyList<byte[]>>(),
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("frames");

        var proxy = new Mock<ICloudSanitizationProxy>();
        proxy.Setup(p => p.SanitizeForCloud(It.IsAny<OutgoingContext>(), It.IsAny<CancellationToken>()))
            .Returns(SanitizationResult.AllowedWith(new OutgoingContext
            {
                SystemPrompt = "sys",
                UserPrompt = "user",
                Provider = "openai",
            }));

        var factory = new SanitizingProviderFactory(infraMock.Object, proxy.Object, NullLogger<SanitizingProviderFactory>.Instance);
        var result = await factory.ExecuteVisionMultiFrameAsync(
            "openai",
            "sys",
            "user",
            new[] { new byte[] { 1 }, new byte[] { 2 } },
            new { },
            CancellationToken.None);

        result.Should().Be("frames");
    }

    [Fact]
    public async Task ExecuteVideoAsync_DelegatesWhenAllowed()
    {
        var infraMock = new Mock<Ashlar.Infrastructure.Execution.IProviderFactory>();
        infraMock.Setup(i => i.ExecuteVideoAsync("clean-sys", "clean-user", It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("video-ok");

        var proxy = new Mock<ICloudSanitizationProxy>();
        proxy.Setup(p => p.SanitizeForCloud(It.IsAny<OutgoingContext>(), It.IsAny<CancellationToken>()))
            .Returns(SanitizationResult.AllowedWith(new OutgoingContext
            {
                SystemPrompt = "clean-sys",
                UserPrompt = "clean-user",
                Provider = "video",
            }));

        var factory = new SanitizingProviderFactory(infraMock.Object, proxy.Object, NullLogger<SanitizingProviderFactory>.Instance);
        var result = await factory.ExecuteVideoAsync("dirty-sys", "dirty-user", new[] { new byte[] { 1 } }, new { }, CancellationToken.None);

        result.Should().Be("video-ok");
    }

    [Fact]
    public async Task ExecuteVideoAsync_BlocksWhenSanitizerDenies()
    {
        var infraMock = new Mock<Ashlar.Infrastructure.Execution.IProviderFactory>();
        var proxy = new Mock<ICloudSanitizationProxy>();
        proxy.Setup(p => p.SanitizeForCloud(It.IsAny<OutgoingContext>(), It.IsAny<CancellationToken>()))
            .Returns(SanitizationResult.Blocked("video blocked"));

        var factory = new SanitizingProviderFactory(infraMock.Object, proxy.Object, NullLogger<SanitizingProviderFactory>.Instance);
        var act = () => factory.ExecuteVideoAsync("sys", "user", new[] { new byte[] { 1 } }, new { }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*video blocked*");
    }

    [Fact]
    public void IsProviderAvailable_DelegatesToInner()
    {
        var infraMock = new Mock<Ashlar.Infrastructure.Execution.IProviderFactory>();
        infraMock.Setup(x => x.IsProviderAvailable("ollama")).Returns(true);

        var proxy = new CloudSanitizationProxy(contentFilter: null);
        var factory = new SanitizingProviderFactory(
            infraMock.Object,
            proxy,
            NullLogger<SanitizingProviderFactory>.Instance);

        factory.IsProviderAvailable("ollama").Should().BeTrue();
    }
}
