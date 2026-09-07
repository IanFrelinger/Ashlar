using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Abstractions.Database;
using Ashlar.Core.Application.Persistence.Ports;
using Ashlar.Infrastructure.Persistence;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

/// <summary>Tests for postgres database provisioner.</summary>
public sealed class PostgresDatabaseProvisionerTests
{
    [Fact]
    public async Task CreateAsync_DedicatedContainer_UsesEphemeralLifecycle_AndDisposesStopsContainer()
    {
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EphemeralDbResult("Host=localhost;Port=1;Database=ashlar", "cid-42", 1));

        lifecycle
            .Setup(x => x.StopAsync("cid-42", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance, lifecycle.Object);

        await using var db = await sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                DatabaseName: "ashlar_test",
                PostgresReadinessTimeout: TimeSpan.Zero));

        db.Isolation.Should().Be(DatabaseIsolationLevel.DedicatedContainer);
        db.BackendResourceId.Should().Be("cid-42");
        db.ConnectionString.Should().Contain("Host=localhost");

        await db.DisposeAsync();

        lifecycle.Verify(x => x.StopAsync("cid-42", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_DedicatedContainer_WithoutLifecycle_Throws()
    {
        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance, ephemeralLifecycle: null);

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.Zero));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IEphemeralDatabaseLifecycle*");
    }

    [Fact]
    public async Task CreateAsync_RunsProvisioningHooks_AfterProvision()
    {
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EphemeralDbResult("Host=localhost;Port=1;Database=ashlar", "cid-99", 1));
        lifecycle
            .Setup(x => x.StopAsync("cid-99", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var order = new List<int>();
        var hook1 = new Mock<IDatabaseProvisioningHook>(MockBehavior.Strict);
        hook1
            .Setup(x => x.AfterProvisionAsync(It.IsAny<IIsolatedDatabase>(), It.IsAny<DatabaseProvisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(1))
            .Returns(Task.CompletedTask);
        var hook2 = new Mock<IDatabaseProvisioningHook>(MockBehavior.Strict);
        hook2
            .Setup(x => x.AfterProvisionAsync(It.IsAny<IIsolatedDatabase>(), It.IsAny<DatabaseProvisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(2))
            .Returns(Task.CompletedTask);

        var sut = new PostgresDatabaseProvisioner(
            NullLogger<PostgresDatabaseProvisioner>.Instance,
            lifecycle.Object,
            new[] { hook1.Object, hook2.Object });

        await using var db = await sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.Zero));

        order.Should().Equal(1, 2);
        hook1.Verify(
            x => x.AfterProvisionAsync(db, It.IsAny<DatabaseProvisionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        hook2.Verify(
            x => x.AfterProvisionAsync(db, It.IsAny<DatabaseProvisionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateAsync_ReadinessTimeout_DisposesBackendAndThrows()
    {
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EphemeralDbResult(
                "Host=127.0.0.1;Port=65431;Database=ashlar;Username=u;Password=p",
                "cid-timeout",
                65431));
        lifecycle
            .Setup(x => x.StopAsync("cid-timeout", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance, lifecycle.Object);

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.FromMilliseconds(400)));

        await act.Should().ThrowAsync<TimeoutException>();

        lifecycle.Verify(x => x.StopAsync("cid-timeout", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_UnsupportedIsolation_Throws()
    {
        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance);

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                (DatabaseIsolationLevel)999,
                PostgresReadinessTimeout: TimeSpan.Zero));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CreateAsync_SharedSchemaWithoutAdminConnection_Throws()
    {
        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance);

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.SharedSchemaNamespaced,
                PostgresReadinessTimeout: TimeSpan.Zero));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*AdminConnectionString*");
    }

    [Fact]
    public async Task CreateAsync_PostProvisionSqlFailure_disposes_ephemeral_backend()
    {
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EphemeralDbResult(
                "Host=127.0.0.1;Port=65432;Database=ashlar;Username=u;Password=p",
                "cid-sql-fail",
                65432));
        lifecycle
            .Setup(x => x.StopAsync("cid-sql-fail", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance, lifecycle.Object);

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.Zero,
                PostProvisionSqlBatches: new[] { "SELECT 1" }));

        await act.Should().ThrowAsync<Exception>();

        lifecycle.Verify(x => x.StopAsync("cid-sql-fail", It.IsAny<CancellationToken>()), Times.Once);
    }
}
