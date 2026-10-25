using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Bricks.Owasp.Security;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Execution;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Adaptation;

/// <summary>Tests for adaptation brick registration.</summary>
[Trait("Category", "Adaptation")]
public sealed class AdaptationBrickRegistrationTests : TempDirTestBase
{
    public AdaptationBrickRegistrationTests() : base("ashlar-adapt-reg") { }

    [Fact]
    public void AddAdaptationBricks_RegistersBrickInRegistry()
    {
        var storePath = Path.Combine(TempDir, "adapt.db");
        var services = new ServiceCollection()
                .AddLogging(b => b.AddConsole())
                .AddSingleton<IProviderFactory, ProviderFactory>()
                .AddAdaptationInfrastructure(storePath)
                .AddAdaptationBricks(typeof(OWASPScannerBrick))
                .BuildServiceProvider();

            var registry = services.GetRequiredService<Ashlar.Core.Domain.Execution.IBrickRegistry>();
            var brick = registry.GetBrick("owasp-scanner");

            brick.Should().NotBeNull();
            brick!.Id.Should().Be("owasp-scanner");
    }

    [Fact]
    public void BrickTypeRegistration_reaches_the_registry_after_the_option_types()
    {
        var storePath = Path.Combine(TempDir, "adapt.db");
        var services = new ServiceCollection()
                .AddLogging(b => b.AddConsole())
                .AddSingleton<IProviderFactory, ProviderFactory>()
                .AddAdaptationInfrastructure(storePath)
                .AddSingleton(new BrickTypeRegistration(typeof(RegisteredByTypeBrick)))
                .AddAdaptationBricks(typeof(OWASPScannerBrick))
                .BuildServiceProvider();

        var registry = services.GetRequiredService<IBrickRegistry>();

        registry.GetBrick(RegisteredByTypeBrick.BrickId).Should().NotBeNull(
            "AddAshlarBrick<T>() registers only a BrickTypeRegistration, so the registry must read those");
        registry.GetAllBricks().Select(b => b.Id).Should().ContainInOrder("owasp-scanner", RegisteredByTypeBrick.BrickId);
    }

    public sealed class RegisteredByTypeBrick : Brick
    {
        public const string BrickId = "test.registered-by-type";

        public RegisteredByTypeBrick()
        {
            Id = BrickId;
            Name = "Registered by type";
            Version = "1.0.0";
        }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input, ImplementationType implementation, IExecutionContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new BrickOutput());
    }

    [Fact]
    public void AddAdaptationBricks_EmptyTypes_DoesNotThrow()
    {
        var services = new ServiceCollection()
            .AddAdaptationInfrastructure()
            .AddAdaptationBricks()
            .BuildServiceProvider();

        var registry = services.GetRequiredService<Ashlar.Core.Domain.Execution.IBrickRegistry>();
        registry.GetAllBricks().Should().BeEmpty();
    }
}
