using Ashlar.Infrastructure;
using Ashlar.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Ashlar.API.Security;

/// <summary>
/// Fails the API's start when, on AirGapped or SecureWorkstation, Kestrel bound a non-loopback address (SPEC-007 PR
/// 4.10, owner decision Q6). It checks the server's published addresses after start, including Kestrel listeners
/// added in code, and fails closed if a restricted host publishes no addresses. The configuration check in
/// <c>Program.cs</c> refuses configured non-loopback listeners before anything binds.
/// </summary>
internal sealed class LoopbackListenerVerifier : IHostedLifecycleService
{
    private readonly IServer _server;
    private readonly IOptions<AshlarResolvedDeploymentProfileOptions>? _profile;

    /// <summary>Takes the server and the resolved profile.</summary>
    /// <param name="server">The server whose bound addresses are checked.</param>
    /// <param name="profile">The profile <c>AddAshlar</c> resolved, or <see langword="null"/>.</param>
    public LoopbackListenerVerifier(IServer server, IOptions<AshlarResolvedDeploymentProfileOptions>? profile = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _profile = profile;
    }

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (_profile?.Value.RequiresLoopback == true && (addresses is null || addresses.Count == 0))
            throw new InvalidOperationException("Cannot verify loopback listeners: the server published no bound addresses.");
        var violation = AshlarInboundListenerPolicy.Refusal(_profile?.Value, addresses);
        return violation is null ? Task.CompletedTask : throw new InvalidOperationException(violation);
    }

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
