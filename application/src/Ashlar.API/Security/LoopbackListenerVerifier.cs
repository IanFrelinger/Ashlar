using Ashlar.Infrastructure.Deployment;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Ashlar.API.Security;

/// <summary>
/// Fails the API's start when, on AirGapped or SecureWorkstation, Kestrel bound a non-loopback address (SPEC-007 PR
/// 4.10, owner decision Q6). It runs after the server has started, so it sees every listener, including one a host
/// adds in code; the configuration check in <c>Program.cs</c> refuses the configured ones before anything binds.
/// </summary>
internal sealed class LoopbackListenerVerifier : IHostedLifecycleService
{
    private readonly IServer _server;
    private readonly ResolvedDeploymentProfile? _profile;

    /// <summary>Takes the server and the resolved profile.</summary>
    /// <param name="server">The server whose bound addresses are checked.</param>
    /// <param name="profile">The profile <c>AddAshlar</c> resolved, or <see langword="null"/>.</param>
    public LoopbackListenerVerifier(IServer server, ResolvedDeploymentProfile? profile = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _profile = profile;
    }

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? (ICollection<string>)[];
        var violation = LoopbackListenerPolicy.Violation(_profile, addresses, "Ashlar.API");
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
