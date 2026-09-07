using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Abstractions.Database;
using Ashlar.Core.Application.Persistence.Ports;
using Ashlar.Infrastructure.Persistence;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

/// <summary>Tests for postgres database provisioner integration.</summary>
[Collection("PostgresDocker")]
[Trait("Category", "DockerOptional")]
public sealed class PostgresDatabaseProvisionerIntegrationTests
{
    private readonly PostgresDockerFixture _postgres;
    private readonly ITestOutputHelper _output;

    public PostgresDatabaseProvisionerIntegrationTests(PostgresDockerFixture postgres, ITestOutputHelper output)
    {
        _postgres = postgres;
        _output = output;
    }

    [Fact]
    public async Task SharedServerNewDatabase_provisions_runs_post_sql_and_disposes()
    {
        if (!EnsureReady())
            return;

        var dbName = "ashlar_cov_" + Guid.NewGuid().ToString("N")[..10];
        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance);

        await using (var db = await sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.SharedServerNewDatabase,
                DatabaseName: dbName,
                AdminConnectionString: _postgres.AdminConnectionString,
                PostgresReadinessTimeout: TimeSpan.FromSeconds(30),
                PostProvisionSqlBatches: new[]
                {
                    "   ",
                    "CREATE TABLE IF NOT EXISTS cov_marker (id integer PRIMARY KEY);",
                    "INSERT INTO cov_marker (id) VALUES (1) ON CONFLICT DO NOTHING;",
                })))
        {
            db.Isolation.Should().Be(DatabaseIsolationLevel.SharedServerNewDatabase);
            db.ConnectionString.Should().Contain(dbName);

            await using var conn = new NpgsqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM cov_marker", conn);
            var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            count.Should().BeGreaterThan(0);
        }

        await using (var admin = new NpgsqlConnection(_postgres.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var exists = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @name",
                admin);
            exists.Parameters.AddWithValue("name", dbName);
            var stillThere = await exists.ExecuteScalarAsync();
            stillThere.Should().BeNull();
        }
    }

    [Fact]
    public async Task SharedSchemaNamespaced_provisions_and_disposes_schema()
    {
        if (!EnsureReady())
            return;

        var schema = "ashlar_schema_" + Guid.NewGuid().ToString("N")[..10];
        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance);

        await using (var db = await sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.SharedSchemaNamespaced,
                DatabaseName: schema,
                AdminConnectionString: _postgres.AdminConnectionString,
                PostgresReadinessTimeout: TimeSpan.FromSeconds(30),
                PostProvisionSqlBatches: new[] { "CREATE TABLE IF NOT EXISTS schema_marker (id integer PRIMARY KEY);" })))
        {
            db.Isolation.Should().Be(DatabaseIsolationLevel.SharedSchemaNamespaced);
            db.ConnectionString.Should().Contain(schema);

            await using var conn = new NpgsqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT to_regclass('schema_marker')::text", conn);
            var reg = (string?)await cmd.ExecuteScalarAsync();
            reg.Should().Be("schema_marker");
        }

        await using (var admin = new NpgsqlConnection(_postgres.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var exists = new NpgsqlCommand(
                "SELECT 1 FROM information_schema.schemata WHERE schema_name = @name",
                admin);
            exists.Parameters.AddWithValue("name", schema);
            var stillThere = await exists.ExecuteScalarAsync();
            stillThere.Should().BeNull();
        }
    }

    [Fact]
    public async Task DedicatedContainer_readiness_and_hooks_succeed_against_live_postgres()
    {
        if (!EnsureReady())
            return;

        var lifecycle = new Mock<IEphemeralDatabaseLifecycle>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.StartAsync(It.IsAny<EphemeralDbOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EphemeralDbResult(_postgres.AdminConnectionString, "cid-live-ready", 5432));
        lifecycle
            .Setup(x => x.StopAsync("cid-live-ready", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hookCalled = false;
        var hook = new Mock<IDatabaseProvisioningHook>(MockBehavior.Strict);
        hook
            .Setup(x => x.AfterProvisionAsync(It.IsAny<IIsolatedDatabase>(), It.IsAny<DatabaseProvisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => hookCalled = true)
            .Returns(Task.CompletedTask);

        var sut = new PostgresDatabaseProvisioner(
            NullLogger<PostgresDatabaseProvisioner>.Instance,
            lifecycle.Object,
            new[] { hook.Object });

        await using var db = await sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.DedicatedContainer,
                PostgresReadinessTimeout: TimeSpan.FromSeconds(30),
                PostProvisionSqlBatches: new[] { "SELECT 1" }));

        hookCalled.Should().BeTrue();
        db.ConnectionString.Should().Be(_postgres.AdminConnectionString);
        lifecycle.Verify(x => x.StopAsync("cid-live-ready", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SharedServerNewDatabase_dispose_is_idempotent()
    {
        if (!EnsureReady())
            return;

        var sut = new PostgresDatabaseProvisioner(NullLogger<PostgresDatabaseProvisioner>.Instance);
        var db = await sut.CreateAsync(
            new DatabaseProvisionRequest(
                DatabaseIsolationLevel.SharedServerNewDatabase,
                DatabaseName: "ashlar_idem_" + Guid.NewGuid().ToString("N")[..8],
                AdminConnectionString: _postgres.AdminConnectionString,
                PostgresReadinessTimeout: TimeSpan.FromSeconds(30)));

        await db.DisposeAsync();
        await db.DisposeAsync();
    }

    private bool EnsureReady()
    {
        if (_postgres.IsReady)
            return true;

        _output.WriteLine(_postgres.SkipReason ?? "Postgres fixture not ready.");
        return false;
    }
}
