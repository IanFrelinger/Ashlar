using Microsoft.Extensions.Logging;
using Ashlar.Infrastructure.Maintenance.Ports;

namespace Ashlar.Infrastructure.Maintenance.Adapters;

/// <summary>
/// Stops and starts Docker Desktop so blob files can be safely deleted.
/// Platform-specific: macOS (osascript), Windows (taskkill), Linux (systemctl/pkill).
/// Only registered when ASHLAR_BLOB_LIFECYCLE=docker.
/// </summary>
internal sealed class DockerDesktopLifecycleAdapter : IBlobStorageLifecycle
{
    private readonly ILogger<DockerDesktopLifecycleAdapter> _logger;

    /// <summary>Initializes a new docker desktop lifecycle adapter.</summary>
    public DockerDesktopLifecycleAdapter(ILogger<DockerDesktopLifecycleAdapter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Pause asynchronously.</summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Pausing Docker Desktop for blob cleanup...");
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "osascript",
                    ArgumentList = { "-e", "quit app \"Docker\"" },
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (proc != null)
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "osascript quit Docker failed; trying pkill");
                await PkillAsync("Docker", ct).ConfigureAwait(false);
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            await PkillAsync("Docker Desktop", ct).ConfigureAwait(false);
        }
        else
        {
            await PkillAsync("Docker", ct).ConfigureAwait(false);
        }
        await Task.Delay(3000, ct).ConfigureAwait(false);
    }

    /// <summary>Resume asynchronously.</summary>
    public Task ResumeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Resuming Docker Desktop...");
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { "-a", "Docker" },
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restart Docker Desktop");
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "C:\\Program Files\\Docker\\Docker\\Docker Desktop.exe",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restart Docker Desktop");
            }
        }
        return Task.CompletedTask;
    }

    private static async Task PkillAsync(string pattern, CancellationToken ct)
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pkill",
                ArgumentList = { "-f", pattern },
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }
    }
}
