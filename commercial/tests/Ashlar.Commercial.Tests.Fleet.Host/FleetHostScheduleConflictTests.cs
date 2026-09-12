using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;
using Ashlar.Commercial.Fleet.Infrastructure;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet.Host;

/// <summary>
/// A placement that loses a write race is a CONFLICT, not a bad request.
/// </summary>
/// <remarks>
/// <para><c>MeshTaskPlacementService</c> returns <c>schedule.conflict</c> when a placement's
/// precondition no longer holds by the time the store applies it - two overlapping schedules of one
/// task, or the timer-driven <c>MeshPendingTaskRebalancerBackgroundService</c> crossing an
/// operator's call. The first revision of both schedule endpoints special-cased only
/// <c>schedule.idempotency_conflict</c> for 409 and fell through to <c>BadRequest</c>, so a caller
/// whose request was perfectly well formed was told not to retry. Nothing in the product or the
/// tests asserted any status code for the new error, and the error string itself was new in that
/// commit, so there was nothing to notice it.</para>
///
/// <para><b>Deterministic, not a race.</b> A decorator commits a competing write immediately before
/// the first <c>UpdateAsync</c> the placement makes - which for a Pending task with no deadline is
/// the assign itself - so the four-field precondition fails every time rather than sometimes. The
/// registry is registered in the test's own <c>ConfigureServices</c>, which runs after the host's
/// <c>TryAddSingleton</c> and therefore wins.</para>
///
/// <para>This project is in no solution and no automatically triggered lane, which
/// <c>ci/test-ownership.tsv</c> tracks: read it as a regression record rather than as a guard.</para>
/// </remarks>
public sealed class FleetHostScheduleConflictTests
    : IClassFixture<WebApplicationFactory<FleetHostProgram>>
{
    private const string TestApiKey = "fleet-host-conflict-test-key";
    private const string PeerId = "commercial-fleet-host-conflict-peer";

    private readonly WebApplicationFactory<FleetHostProgram> _factory;
    private readonly LoseTheFirstWriteRace _registry = new(new InMemoryMeshTaskRegistry());

    /// <summary>Fleet host schedule conflict tests.</summary>
    /// <param name="factory">Factory.</param>
    public FleetHostScheduleConflictTests(WebApplicationFactory<FleetHostProgram> factory) =>
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Ashlar:Security:ApiKey", TestApiKey);
            builder.ConfigureServices(services =>
                services.AddSingleton<IMeshTaskRegistry>(_registry));
        });

    /// <summary>
    /// The loser of a schedule write race is told 409, so a client retries instead of treating its
    /// own request as malformed.
    /// </summary>
    [Fact]
    public async Task A_schedule_that_loses_the_write_race_is_a_conflict_not_a_bad_request()
    {
        using var client = _factory.CreateClient();

        await PostAsync(client, "/api/mesh/fleet/nodes", new
        {
            peerId = PeerId,
            apiBaseUrl = "http://127.0.0.1:8080",
            trustTier = "Trusted",
        });

        var created = await PostJsonAsync<MeshTaskDto>(
            client, "/api/mesh/tasks", new { name = "conflict-task", steps = 1 });
        created.Should().NotBeNull();

        _registry.Arm(created!.TaskId);

        using var schedule = new HttpRequestMessage(
            HttpMethod.Post, $"/api/mesh/tasks/{created.TaskId}/schedule")
        {
            Content = JsonContent.Create(new { }),
        };
        schedule.Headers.Add("X-Ashlar-Api-Key", TestApiKey);

        var response = await client.SendAsync(schedule);

        _registry.Fired.Should().BeTrue(
            "the control for the decorator: without the competing write this asserts nothing about "
            + "a lost race");
        response.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "schedule.conflict means the task moved, so the caller should retry. 400 says the "
            + "request was malformed, which by convention is not retried - and the request was "
            + "perfectly well formed.");
    }

    /// <summary>
    /// An uncontended schedule is still 200 - the positive control for the mapping.
    /// </summary>
    /// <remarks>
    /// Mapping every unsuccessful placement to 409 would satisfy the fact above while hiding every
    /// real bad request, so the accepted path is asserted in the same class.
    /// </remarks>
    [Fact]
    public async Task An_uncontended_schedule_is_still_ok()
    {
        using var client = _factory.CreateClient();

        await PostAsync(client, "/api/mesh/fleet/nodes", new
        {
            peerId = PeerId,
            apiBaseUrl = "http://127.0.0.1:8080",
            trustTier = "Trusted",
        });

        var created = await PostJsonAsync<MeshTaskDto>(
            client, "/api/mesh/tasks", new { name = "uncontended-task", steps = 1 });
        created.Should().NotBeNull();

        using var schedule = new HttpRequestMessage(
            HttpMethod.Post, $"/api/mesh/tasks/{created!.TaskId}/schedule")
        {
            Content = JsonContent.Create(new { }),
        };
        schedule.Headers.Add("X-Ashlar-Api-Key", TestApiKey);

        var response = await client.SendAsync(schedule);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the positive control: with nothing racing it a placement succeeds, so the 409 above is "
            + "about the race and not about the endpoint refusing everything");
    }

    private static async Task PostAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Ashlar-Api-Key", TestApiKey);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    private static async Task<T?> PostJsonAsync<T>(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Ashlar-Api-Key", TestApiKey);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<T>();
    }

    /// <summary>Wire DTO for a mesh task.</summary>
    /// <param name="TaskId">Task id.</param>
    /// <param name="Status">Status name.</param>
    private sealed record MeshTaskDto(string TaskId, string Status);

    /// <summary>
    /// Commits a competing write immediately before the first <c>UpdateAsync</c> for one armed task,
    /// so the placement's precondition fails every time rather than sometimes.
    /// </summary>
    private sealed class LoseTheFirstWriteRace : IMeshTaskRegistry
    {
        private readonly IMeshTaskRegistry _inner;
        private string? _armed;

        public LoseTheFirstWriteRace(IMeshTaskRegistry inner) => _inner = inner;

        /// <summary>Whether the competing write actually ran.</summary>
        public bool Fired { get; private set; }

        /// <summary>Arms the decorator for one task id.</summary>
        /// <param name="taskId">The task whose first update loses.</param>
        public void Arm(string taskId) => _armed = taskId;

        public Task<MeshTaskState> CreateAsync(MeshTaskCreateSpec spec, CancellationToken cancellationToken = default)
            => _inner.CreateAsync(spec, cancellationToken);

        public Task<MeshTaskState?> TryGetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default)
            => _inner.TryGetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);

        public Task<MeshTaskState?> GetAsync(string taskId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(taskId, cancellationToken);

        public Task<IReadOnlyList<MeshTaskState>> ListAsync(CancellationToken cancellationToken = default)
            => _inner.ListAsync(cancellationToken);

        public async Task<MeshTaskUpdateResult> UpdateAsync(
            string taskId,
            Func<MeshTaskState, MeshTaskState?> transform,
            CancellationToken cancellationToken = default)
        {
            if (_armed is not null && string.Equals(_armed, taskId, StringComparison.Ordinal))
            {
                _armed = null;

                // Anything the four-field precondition compares will do; the attempt count is the
                // member a competing RETRY would move, and it needs no lease to exist yet.
                await _inner.UpdateAsync(
                    taskId,
                    current => current with { AttemptCount = current.AttemptCount + 1 },
                    cancellationToken).ConfigureAwait(false);
                Fired = true;
            }

            return await _inner.UpdateAsync(taskId, transform, cancellationToken).ConfigureAwait(false);
        }
    }
}
