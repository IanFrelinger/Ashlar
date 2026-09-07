using System.ComponentModel;
using System.Diagnostics;
using Npgsql;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Persistence;

/// <summary>
/// Optional Postgres server for provisioner integration tests.
/// Uses <c>ASHLAR_TEST_POSTGRES_ADMIN</c> when set; otherwise starts a disposable Docker container.
/// </summary>
public sealed class PostgresDockerFixture : IAsyncLifetime
{
    private string? _containerName;
    private bool _ownsContainer;

    public bool IsReady { get; private set; }

    public string? SkipReason { get; private set; }

    public string AdminConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ASHLAR_TEST_POSTGRES_ADMIN");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            AdminConnectionString = fromEnv.Trim();
            if (await CanConnectAsync(AdminConnectionString))
            {
                IsReady = true;
                return;
            }

            SkipReason = "ASHLAR_TEST_POSTGRES_ADMIN is set but not reachable.";
            return;
        }

        if (!await IsDockerAvailableAsync())
        {
            SkipReason = "Docker is not available.";
            return;
        }

        _containerName = "ashlar-pg-cov-" + Guid.NewGuid().ToString("N")[..10];
        var start = await RunProcessAsync(
            "docker",
            $"run -d --rm --name {_containerName} -e POSTGRES_PASSWORD=postgres -p 127.0.0.1::5432 postgres:16-alpine",
            TimeSpan.FromSeconds(60));
        if (start.ExitCode != 0)
        {
            SkipReason = $"Failed to start Postgres container: {start.StdErr}";
            return;
        }

        _ownsContainer = true;

        var portResult = await RunProcessAsync("docker", $"port {_containerName} 5432/tcp", TimeSpan.FromSeconds(8));
        if (portResult.ExitCode != 0)
        {
            SkipReason = $"Failed to resolve Postgres port: {portResult.StdErr}";
            await StopContainerAsync();
            return;
        }

        var portLine = portResult.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var port = portLine[(portLine.LastIndexOf(':') + 1)..];
        AdminConnectionString =
            $"Host=127.0.0.1;Port={port};Username=postgres;Password=postgres;Database=postgres;Timeout=5";

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await CanConnectAsync(AdminConnectionString))
            {
                IsReady = true;
                return;
            }

            await Task.Delay(500);
        }

        SkipReason = "Postgres container did not become ready in time.";
        await StopContainerAsync();
    }

    public async Task DisposeAsync()
    {
        if (_ownsContainer)
            await StopContainerAsync();
    }

    private async Task StopContainerAsync()
    {
        if (string.IsNullOrWhiteSpace(_containerName))
            return;

        await RunProcessAsync("docker", $"stop {_containerName}", TimeSpan.FromSeconds(15));
        _containerName = null;
        _ownsContainer = false;
    }

    private static async Task<bool> IsDockerAvailableAsync()
    {
        var result = await RunProcessAsync("docker", "info", TimeSpan.FromSeconds(8));
        return result.ExitCode == 0;
    }

    private static async Task<bool> CanConnectAsync(string connectionString)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string file,
        string args,
        TimeSpan timeout)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
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
                    // already gone
                }

                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                }

                return (124, string.Empty, $"docker '{args}' did not finish within {timeout.TotalSeconds:0}s.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
