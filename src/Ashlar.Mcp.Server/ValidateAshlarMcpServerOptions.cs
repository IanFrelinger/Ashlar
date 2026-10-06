using Microsoft.Extensions.Options;

namespace Ashlar.Mcp.Server;

/// <summary>
/// Fail-closed startup validation for <see cref="AshlarMcpServerOptions"/> (wired via
/// <c>ValidateOnStart</c> so a misconfigured host refuses to boot instead of serving a
/// half-configured protocol surface).
/// </summary>
public sealed class ValidateAshlarMcpServerOptions : IValidateOptions<AshlarMcpServerOptions>
{
    /// <summary>Deployment-profile environment variable honored by the Ashlar kernel.</summary>
    public const string DeploymentProfileVariable = "ASHLAR_DEPLOYMENT_PROFILE";

    private readonly bool _httpTransport;

    /// <summary>
    /// Creates the validator. DI supplies every <see cref="AshlarMcpHttpTransportMarker"/>; stdio registers none.
    /// </summary>
    public ValidateAshlarMcpServerOptions(IEnumerable<AshlarMcpHttpTransportMarker>? httpTransports = null)
    {
        _httpTransport = httpTransports is not null && httpTransports.Any();
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AshlarMcpServerOptions options)
    {
        if (!options.Enabled)
        {
            // Disabled is always valid — the surface stays dark regardless of the rest.
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        // The air-gapped profile promises "no protocol ingress/egress". Refusing enablement here
        // (rather than trusting every host to check) keeps that promise even when an operator
        // copies an enabling env block onto the wrong machine.
        var profile = AshlarDeploymentProfileEnvironment.Effective(
            Environment.GetEnvironmentVariable(DeploymentProfileVariable));
        if (AshlarDeploymentProfileEnvironment.IsAirGapped(profile))
        {
            failures.Add(
                $"{nameof(AshlarMcpServerOptions.Enabled)}=true is not permitted under the AirGapped deployment profile " +
                $"({DeploymentProfileVariable}={profile}). The MCP server is a network protocol surface and stays off.");
        }
        else if (_httpTransport && AshlarDeploymentProfileEnvironment.IsSecureWorkstation(profile))
        {
            failures.Add(
                $"{nameof(AshlarMcpServerOptions.Enabled)}=true with HTTP transport is not permitted under the SecureWorkstation " +
                $"deployment profile ({DeploymentProfileVariable}={profile}). MCP over HTTP fails boot; stdio stays.");
        }

        if (string.IsNullOrWhiteSpace(options.ServerName))
        {
            failures.Add($"{nameof(AshlarMcpServerOptions.ServerName)} must be non-empty when the MCP server is enabled.");
        }

        if (options.MaxConcurrentToolCalls < 1)
        {
            failures.Add($"{nameof(AshlarMcpServerOptions.MaxConcurrentToolCalls)} must be >= 1 (got {options.MaxConcurrentToolCalls}).");
        }

        foreach (var toolOverrides in options.ArgumentOverrides)
        {
            if (string.IsNullOrWhiteSpace(toolOverrides.Key))
            {
                failures.Add("ArgumentOverrides contains an empty tool id key.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
