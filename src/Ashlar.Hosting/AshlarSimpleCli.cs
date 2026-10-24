using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Hosting;

/// <summary>
/// Simplified hosting helpers for CLI applications that execute bricks.
/// Provides a streamlined API for common CLI scenarios without requiring deep
/// knowledge of Ashlar's hosting infrastructure.
/// </summary>
public static class AshlarSimpleCli
{
    /// <summary>
    /// Creates a simple host builder for CLI applications.
    /// Uses the System deployment profile for minimal overhead.
    /// </summary>
    /// <param name="configureServices">Optional additional service configuration.</param>
    /// <param name="configureLogging">Optional logging configuration.</param>
    /// <returns>A configured host builder.</returns>
    public static IHostBuilder CreateSimpleHostBuilder(
        Action<IServiceCollection>? configureServices = null,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(builder =>
            {
                builder.AddConsole();
                configureLogging?.Invoke(builder);
            })
            .ConfigureServices((context, services) =>
            {
                // Add Ashlar with minimal (System) profile
                services.AddAshlarProfile(AshlarDeploymentProfile.System);
                
                // Allow additional configuration
                configureServices?.Invoke(services);
            });
    }

    /// <summary>
    /// Creates and builds a simple host for CLI applications.
    /// </summary>
    /// <param name="configureServices">Optional additional service configuration.</param>
    /// <param name="configureLogging">Optional logging configuration.</param>
    /// <returns>A built and ready-to-use host.</returns>
    public static IHost CreateSimpleHost(
        Action<IServiceCollection>? configureServices = null,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        return CreateSimpleHostBuilder(configureServices, configureLogging).Build();
    }

    /// <summary>
    /// Executes a brick with fluent input configuration.
    /// Creates a temporary host, executes the brick, and disposes resources.
    /// </summary>
    /// <param name="brick">The brick to execute.</param>
    /// <param name="configureInputs">Action to configure brick inputs.</param>
    /// <param name="implementation">Implementation type (default: Deterministic).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The brick output.</returns>
    public static async Task<BrickOutput> ExecuteAsync(
        this Ashlar.Core.Domain.Bricks.Brick brick,
        Action<BrickInput> configureInputs,
        ImplementationType implementation = ImplementationType.Deterministic,
        CancellationToken cancellationToken = default)
    {
        using var host = CreateSimpleHost();
        var context = host.Services.GetRequiredService<IExecutionContext>();

        var input = new BrickInput();
        configureInputs(input);

        return await brick.ExecuteAsync(input, implementation, context, cancellationToken);
    }

    /// <summary>
    /// Executes a brick with a pre-built input.
    /// Creates a temporary host, executes the brick, and disposes resources.
    /// </summary>
    /// <param name="brick">The brick to execute.</param>
    /// <param name="input">The brick input.</param>
    /// <param name="implementation">Implementation type (default: Deterministic).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The brick output.</returns>
    public static async Task<BrickOutput> ExecuteAsync(
        this Ashlar.Core.Domain.Bricks.Brick brick,
        BrickInput input,
        ImplementationType implementation = ImplementationType.Deterministic,
        CancellationToken cancellationToken = default)
    {
        using var host = CreateSimpleHost();
        var context = host.Services.GetRequiredService<IExecutionContext>();

        return await brick.ExecuteAsync(input, implementation, context, cancellationToken);
    }
}

/// <summary>
/// Extension methods for working with brick outputs in CLI scenarios.
/// </summary>
public static class BrickOutputCliExtensions
{
    /// <summary>
    /// Gets a value from brick output with a default if the key doesn't exist or type doesn't match.
    /// Convenience method for CLI scenarios.
    /// </summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="output">The brick output.</param>
    /// <param name="key">The output key.</param>
    /// <param name="defaultValue">Default value if key doesn't exist or type doesn't match.</param>
    /// <returns>The value or default.</returns>
    public static T GetOrDefault<T>(this BrickOutput output, string key, T defaultValue = default!)
    {
        if (output.TryGet<T>(key, out var value))
        {
            return value!;
        }
        return defaultValue;
    }

    /// <summary>
    /// Prints brick output summary to console.
    /// </summary>
    /// <param name="output">The brick output.</param>
    /// <param name="includeAllOutputs">Whether to print all outputs (default: false, only summary).</param>
    public static void PrintToConsole(this BrickOutput output, bool includeAllOutputs = false)
    {
        if (!string.IsNullOrEmpty(output.Summary))
        {
            Console.WriteLine($"Summary: {output.Summary}");
        }

        if (includeAllOutputs)
        {
            Console.WriteLine("\nOutputs:");
            // Note: BrickOutput doesn't expose all keys directly in current API
            // This is a simplified version - real implementation would need
            // BrickOutput to expose its keys or use reflection
            Console.WriteLine("  (Use TryGet<T> or Get<T> to access specific outputs)");
        }
    }
}

