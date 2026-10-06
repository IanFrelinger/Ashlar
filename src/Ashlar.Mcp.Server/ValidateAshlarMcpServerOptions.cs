using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ashlar.Mcp.Server;

/// <summary>
/// Fail-closed startup validation for <see cref="AshlarMcpServerOptions"/> (wired via
/// <c>ValidateOnStart</c> so a misconfigured host refuses to boot instead of serving a
/// half-configured protocol surface).
/// </summary>
/// <remarks>
/// <b>MCP over HTTP fails boot on SecureWorkstation</b> (SPEC-007 PR 4.10, owner decision Q6): inbound connections
/// there are loopback only, and the responses an HTTP client receives are not mediated until PR 5's <c>CanRead</c>
/// at the server seams. The check is the service <c>.WithHttpTransport()</c> registers
/// (<see cref="HttpTransportMarkerTypeName"/>); the stdio transport, which a local IDE spawns, still boots. When this
/// validator cannot tell (no service provider, or the marker type is missing from the MCP SDK), it assumes HTTP, so
/// the twin that boots stdio on SecureWorkstation catches an SDK change before a release does.
/// </remarks>
public sealed class ValidateAshlarMcpServerOptions : IValidateOptions<AshlarMcpServerOptions>
{
    /// <summary>Deployment-profile environment variable honored by the Ashlar kernel.</summary>
    public const string DeploymentProfileVariable = "ASHLAR_DEPLOYMENT_PROFILE";

    /// <summary>
    /// The service <c>.WithHttpTransport()</c> (ModelContextProtocol.AspNetCore) registers, assembly-qualified: its
    /// registration is how this validator knows the server is offered over HTTP.
    /// </summary>
    public const string HttpTransportMarkerTypeName =
        "ModelContextProtocol.AspNetCore.StreamableHttpHandler, ModelContextProtocol.AspNetCore";

    private readonly IServiceProviderIsService? _services;

    /// <summary>A validator with no view of the container: on SecureWorkstation it assumes MCP over HTTP.</summary>
    public ValidateAshlarMcpServerOptions()
    {
    }

    /// <summary>A validator that sees which MCP transport the container registered.</summary>
    /// <param name="services">The container's registration query.</param>
    public ValidateAshlarMcpServerOptions(IServiceProviderIsService services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary><see langword="true"/> unless the container is known not to serve MCP over HTTP.</summary>
    private bool ServesOverHttp()
    {
        var marker = Type.GetType(HttpTransportMarkerTypeName, throwOnError: false);
        return _services is null || marker is null || _services.IsService(marker);
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
        else if (AshlarDeploymentProfileEnvironment.IsSecureWorkstation(profile) && ServesOverHttp())
        {
            failures.Add(
                "MCP over HTTP is not permitted under the SecureWorkstation deployment profile " +
                $"({DeploymentProfileVariable}={profile}): inbound connections are loopback only, and responses on " +
                "inbound connections are not mediated until SPEC-007 PR 5. Serve MCP over stdio instead " +
                "(Ashlar.Mcp.Server.Host), or set Ashlar:Mcp:Server:Enabled=false.");
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
