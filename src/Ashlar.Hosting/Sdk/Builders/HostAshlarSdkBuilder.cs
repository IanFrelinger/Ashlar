using Ashlar.Abstractions;
using Ashlar.Core.Domain.Agents;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Infrastructure.Sdk.Ports;

namespace Ashlar.Hosting.Sdk.Builders;

/// <summary>
/// Default implementation of <see cref="IAshlarSdkBuilder"/>. Configures <see cref="AshlarSdkOptions"/> for kernel registration.
/// </summary>
public class HostAshlarSdkBuilder : IAshlarSdkBuilder
{
    private readonly AshlarSdkOptions _options;

    /// <summary>
    /// Creates a new SDK builder with the given options.
    /// </summary>
    /// <param name="options">Options to populate.</param>
    public HostAshlarSdkBuilder(AshlarSdkOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public IAshlarSdkBuilder RegisterBrick<T>() where T : DomainBrick
    {
        _options.BrickTypes.Add(typeof(T));
        return this;
    }

    /// <inheritdoc />
    public IAshlarSdkBuilder RegisterAgent<T>() where T : class
    {
        if (!typeof(IAgent).IsAssignableFrom(typeof(T)))
        {
            throw new ArgumentException($"Type {typeof(T).Name} must implement {nameof(IAgent)}", nameof(T));
        }

        _options.AgentTypes.Add(typeof(T));
        return this;
    }

    /// <inheritdoc />
    public IAshlarSdkBuilder RegisterAgentCard(AgentCard card)
    {
        if (card == null)
        {
            throw new ArgumentNullException(nameof(card));
        }

        _options.AgentCards.Add(card);
        return this;
    }
}
