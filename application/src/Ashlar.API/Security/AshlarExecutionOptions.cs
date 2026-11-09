namespace Ashlar.API.Security;

/// <summary>
/// Opt-in for the remote container-execution surface (<c>POST /api/execution/build</c> and
/// <c>POST /api/execution/run</c>). Those routes hand caller-chosen images and commands to the
/// local Docker daemon, so they are dark by default and only meant for
/// <c>RemoteExecutionPlatform</c> callers (<c>ASHLAR_EXECUTION_REMOTE_URL</c>).
/// Binds from configuration section <see cref="SectionPath"/>.
/// </summary>
public sealed class AshlarExecutionOptions
{
    /// <summary>Configuration section path (<c>Ashlar:Execution</c>).</summary>
    public const string SectionPath = "Ashlar:Execution";

    /// <summary>
    /// When true, the <c>/api/execution/*</c> routes are mapped. Requests are still refused (403) unless a
    /// built-in <see cref="AshlarSecurityOptions.AuthorizationMode"/> other than <c>None</c> is active;
    /// the surface never runs unauthenticated.
    /// </summary>
    public bool ServeRemoteExecution { get; set; }

    /// <summary>
    /// Legacy setting retained for configuration compatibility only. It does not enable host bind
    /// mounts: every nonempty <c>VolumeMounts</c> on <c>/api/execution/run</c> is rejected (400).
    /// A pathname allowlist cannot confine the Docker daemon's later resolution of writable paths.
    /// </summary>
    public string? AllowedVolumeMountRoot { get; set; }
}
