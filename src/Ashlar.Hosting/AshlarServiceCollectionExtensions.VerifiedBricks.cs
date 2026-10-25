using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Ashlar.Hosting;

public static partial class AshlarServiceCollectionExtensions
{
    /// <summary>
    /// Adds Ashlar with a fixed verified-only brick registry. Host startup verifies all deployments;
    /// access before startup or after shutdown fails closed. Apply this after other brick registrations.
    /// The host configuration and dependency-injection container remain trusted operator code.
    /// </summary>
    public static IServiceCollection AddAshlarVerifiedBricks(this IServiceCollection services,
        IEnumerable<VerifiedBrickDeployment> deployments, CertificationTrustPolicy policy,
        string? hmacKey = null, Action<AshlarHostingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Strict.PinningEnabled)
            throw new CertificationTrustConfigurationException("Verified hosting requires pinned Ed25519 keys.");
        ArgumentNullException.ThrowIfNull(deployments);
        var snapshot = deployments.ToArray();
        services.AddAshlar(configure);
        services.RemoveAll<IBrickRegistry>();
        services.RemoveAll<BrickRegistry>();
        services.AddSingleton(sp => new HostedVerifiedBrickRegistry(snapshot, policy, hmacKey, sp));
        services.AddSingleton<IBrickRegistry>(sp => sp.GetRequiredService<HostedVerifiedBrickRegistry>());
        // Insert before background workers and servers so none can use an unverified registry at startup.
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(
            sp => sp.GetRequiredService<HostedVerifiedBrickRegistry>()));
        return services;
    }

    private sealed class HostedVerifiedBrickRegistry(
        VerifiedBrickDeployment[] deployments, CertificationTrustPolicy policy, string? hmacKey, IServiceProvider services)
        : IBrickRegistry, IHostedService, IDisposable
    {
        private VerifiedBrickRegistry? _registry;
        private readonly object _lifecycle = new();
        private bool _disposed;
        private bool _started;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!ReferenceEquals(services.GetRequiredService<IBrickRegistry>(), this))
                throw new InvalidOperationException("A later registration replaced the verified brick registry. Apply verified hosting last.");
            lock (_lifecycle)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_started) throw new InvalidOperationException("Verified registry startup has already been attempted.");
                _started = true;
            }
            var loaded = await VerifiedBrickRegistry.LoadFromFilesAsync(deployments, policy, hmacKey, cancellationToken)
                .ConfigureAwait(false);
            lock (_lifecycle)
            {
                if (_disposed)
                {
                    loaded.Dispose();
                    throw new ObjectDisposedException(nameof(HostedVerifiedBrickRegistry));
                }
                _registry = loaded;
            }
        }

        private VerifiedBrickRegistry Registry
        {
            get
            {
                lock (_lifecycle) return _registry ??
                    throw new InvalidOperationException("Verified registry is unavailable until successful host startup.");
            }
        }
        public global::Ashlar.Core.Domain.Bricks.Brick? GetBrick(string id) => Registry.GetBrick(id);
        public IReadOnlyList<global::Ashlar.Core.Domain.Bricks.Brick> GetAllBricks() => Registry.GetAllBricks();
        public Task StopAsync(CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
        public void Dispose()
        {
            lock (_lifecycle)
            {
                _disposed = true;
                _registry?.Dispose();
                _registry = null;
            }
        }
    }
}
