using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Abstractions.Database;
using Ashlar.Core.Application.Persistence.Ports;
using Ashlar.Infrastructure.Persistence;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

/// <summary>Tests for postgres database provisioner gap coverage.</summary>
public sealed class PostgresDatabaseProvisionerGapCoverageTests
{
    [Fact]
    public async Task CreateAsync_SharedServerNewDatabase_WithoutAdminConnection_Throws()
    {
        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance);

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.SharedServerNewDatabase,
                PostgresReadinessTimeout: TimeSpan.Zero));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*AdminConnectionString*");
    }

    [Fact]
    public async Task CreateAsync_DedicatedContainer_PassesCustomOptionsToLifecycle()
    {
        EphemeralDbOptions? captured = null;
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .Callback<EphemeralDbOptions, CancellationToken>((options, _) => captured = options)
            .ReturnsAsync(new EphemeralDbResult("Host=localhost;Port=1;Database=custom", "cid-opt", 1));
        lifecycle
            .Setup(x => x.StopAsync("cid-opt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance, lifecycle.Object);

        await using var db = await sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                DatabaseName: "custom_db",
                ImageTag: "postgres:16",
                Password: "secret",
                PostgresReadinessTimeout: TimeSpan.Zero));

        captured.Should().NotBeNull();
        captured!.Database.Should().Be("custom_db");
        captured.ImageTag.Should().Be("postgres:16");
        captured.Password.Should().Be("secret");
        db.ConnectionString.Should().Contain("custom");
    }

    [Fact]
    public async Task CreateAsync_DedicatedContainer_DefaultDatabaseName_WhenUnspecified()
    {
        EphemeralDbOptions? captured = null;
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .Callback<EphemeralDbOptions, CancellationToken>((options, _) => captured = options)
            .ReturnsAsync(new EphemeralDbResult("Host=localhost;Port=1;Database=ashlar", "cid-default", 1));
        lifecycle
            .Setup(x => x.StopAsync("cid-default", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance, lifecycle.Object);

        await using var db = await sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.Zero));

        captured!.Database.Should().Be("ashlar");
        db.Isolation.Should().Be(DatabaseIsolationLevel.DedicatedContainer);
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent_for_dedicated_container()
    {
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EphemeralDbResult("Host=localhost;Port=1;Database=ashlar", "cid-idem", 1));
        lifecycle
            .Setup(x => x.StopAsync("cid-idem", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance, lifecycle.Object);
        var db = await sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.Zero));

        await db.DisposeAsync();
        await db.DisposeAsync();

        lifecycle.Verify(x => x.StopAsync("cid-idem", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Constructor_null_logger_throws()
    {
        var act = () => new PostgresDatabaseProvisioner(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public async Task CreateAsync_provisioning_hook_failure_disposes_backend()
    {
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EphemeralDbResult("Host=localhost;Port=1;Database=ashlar", "cid-hook-fail", 1));
        lifecycle
            .Setup(x => x.StopAsync("cid-hook-fail", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hook = new Mock<IDatabaseProvisioningHook>(MockBehavior.Strict);
        hook
            .Setup(x => x.AfterProvisionAsync(It.IsAny<IIsolatedDatabase>(), It.IsAny<DatabaseProvisionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hook failed"));

        var sut = new PostgresDatabaseProvisioner(
            NullLogger<PostgresDatabaseProvisioner>.Instance,
            lifecycle.Object,
            new[] { hook.Object });

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.Zero));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*hook failed*");
        lifecycle.Verify(x => x.StopAsync("cid-hook-fail", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_provisioning_failure_still_attempts_dispose_when_stop_throws()
    {
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EphemeralDbResult("Host=localhost;Port=1;Database=ashlar", "cid-stop-fail", 1));
        lifecycle
            .Setup(x => x.StopAsync("cid-stop-fail", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("stop failed"));

        var hook = new Mock<IDatabaseProvisioningHook>(MockBehavior.Strict);
        hook
            .Setup(x => x.AfterProvisionAsync(It.IsAny<IIsolatedDatabase>(), It.IsAny<DatabaseProvisionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hook failed"));

        var sut = new PostgresDatabaseProvisioner(
            NullLogger<PostgresDatabaseProvisioner>.Instance,
            lifecycle.Object,
            new[] { hook.Object });

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.Zero));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*hook failed*");
        lifecycle.Verify(x => x.StopAsync("cid-stop-fail", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("  !!!  ", true)]
    [InlineData("valid_name", false)]
    [InlineData("", true)]
    public void SanitizeIdentifier_strips_invalid_characters(string input, bool expectGeneratedPrefix)
    {
        var sanitized = InvokeSanitizeIdentifier(input);
        sanitized.Should().MatchRegex("^[a-z0-9_]+$");
        if (expectGeneratedPrefix)
            sanitized.Should().StartWith("ashlar_");
        else
            sanitized.Should().Be("valid_name");
    }

    private static string InvokeSanitizeIdentifier(string value)
    {
        var method = typeof(PostgresDatabaseProvisioner).GetMethod(
            "SanitizeIdentifier",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (string)method!.Invoke(null, new object[] { value })!;
    }

    [Fact]
    public async Task CreateAsync_unsupported_isolation_throws()
    {
        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance);
        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                (DatabaseIsolationLevel)999,
                PostgresReadinessTimeout: TimeSpan.Zero));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CreateAsync_readiness_timeout_disposes_backend()
    {
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EphemeralDbResult(
                "Host=127.0.0.1;Port=1;Database=ashlar;Username=u;Password=p;Timeout=1",
                "cid-ready-timeout",
                1));
        lifecycle
            .Setup(x => x.StopAsync("cid-ready-timeout", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance, lifecycle.Object);

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.FromMilliseconds(200)));

        await act.Should().ThrowAsync<TimeoutException>();
        lifecycle.Verify(x => x.StopAsync("cid-ready-timeout", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_shared_schema_without_admin_connection_throws()
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
    public async Task CreateAsync_dedicated_without_lifecycle_throws()
    {
        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance);

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.Zero));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IEphemeralDatabaseLifecycle*");
    }

    [Fact]
    public async Task CreateAsync_cancelled_during_readiness_disposes_backend()
    {
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EphemeralDbResult(
                "Host=127.0.0.1;Port=1;Database=ashlar;Username=u;Password=p;Timeout=1",
                "cid-cancelled",
                1));
        lifecycle
            .Setup(x => x.StopAsync("cid-cancelled", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance, lifecycle.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.FromSeconds(5)),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        lifecycle.Verify(x => x.StopAsync("cid-cancelled", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_post_provision_sql_failure_disposes_backend()
    {
        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EphemeralDbResult(
                "Host=127.0.0.1;Port=1;Database=ashlar;Username=u;Password=p;Timeout=1",
                "cid-post-sql",
                1));
        lifecycle
            .Setup(x => x.StopAsync("cid-post-sql", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance, lifecycle.Object);

        var act = () => sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.Zero,
                PostProvisionSqlBatches: new[] { "SELECT 1" }));

        await act.Should().ThrowAsync<Exception>();
        lifecycle.Verify(x => x.StopAsync("cid-post-sql", It.IsAny<CancellationToken>()), Times.Once);
    }
}
