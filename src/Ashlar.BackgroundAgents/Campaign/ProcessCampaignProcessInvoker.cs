using System.Diagnostics;
using System.Text;

namespace Ashlar.BackgroundAgents.Campaign;

/// <summary>Spawns a real child process for campaign lanes that need the host toolchain.</summary>
public sealed class ProcessCampaignProcessInvoker : ICampaignProcessInvoker
{
    /// <inheritdoc />
    public async Task<CampaignProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Process file name is required.", nameof(fileName));

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        // Specialists must not leave MSBuild / VBCSCompiler nodes behind; those
        // keep `dotnet run` alive after the campaign has already printed Pass.
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        psi.Environment["UseSharedCompilation"] = "false";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            return new CampaignProcessResult(1, string.Empty, $"Failed to start '{fileName}'.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort teardown of a cancelled child.
            }

            throw;
        }

        return new CampaignProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
