using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Paths;
using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Infrastructure.Execution.Scratch;

/// <summary>Temp-directory <see cref="IScratchSpace"/>.</summary>
public sealed class FileScratchSpace : IScratchSpace
{
    /// <inheritdoc />
    public IScratchDir CreateScratchDir(string prefix)
    {
        var name = $"{prefix?.Trim() ?? "ashlar"}-{Guid.NewGuid():N}";
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
        Directory.CreateDirectory(path);
        return new ScratchDir(path);
    }

    private sealed class ScratchDir : IScratchDir
    {
        public ScratchDir(string path) => Path = path;
        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}

/// <summary>
/// WORKSPACE_ROOT-aware path policy. When unset, containment is a logged no-op.
/// </summary>
public sealed class WorkspacePathPolicy : IWorkspacePathPolicy
{
    private readonly string? _root;
    private readonly ILogger<WorkspacePathPolicy>? _logger;

    /// <summary>Creates a policy reading WORKSPACE_ROOT from the environment.</summary>
    public WorkspacePathPolicy(ILogger<WorkspacePathPolicy>? logger = null)
    {
        _logger = logger;
        var root = Environment.GetEnvironmentVariable("WORKSPACE_ROOT");
        _root = string.IsNullOrWhiteSpace(root) ? null : System.IO.Path.GetFullPath(root);
        if (_root is null)
            _logger?.LogInformation("WORKSPACE_ROOT unset — workspace path containment is a no-op");
    }

    /// <inheritdoc />
    public void RequireExistingDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Required directory missing: {path}");
    }

    /// <inheritdoc />
    public string ResolveUnderRoot(string relativeOrAbsolute)
    {
        if (System.IO.Path.IsPathRooted(relativeOrAbsolute))
            return System.IO.Path.GetFullPath(relativeOrAbsolute);
        if (_root is null)
            return System.IO.Path.GetFullPath(relativeOrAbsolute);
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, relativeOrAbsolute));
    }

    /// <inheritdoc />
    public bool IsUnderRoot(string path)
    {
        if (_root is null) return true;
        // GetFullPath first, so an unresolvable path still throws here as it always has.
        var full = System.IO.Path.GetFullPath(path);
        return PathContainment.IsWithin(full, _root);
    }

    /// <inheritdoc />
    public void EnsureContained(string path)
    {
        if (_root is null) return;
        if (!IsUnderRoot(path))
            throw new InvalidOperationException($"Path escapes WORKSPACE_ROOT: {path}");
    }
}

/// <summary>In-memory single-flight guard.</summary>
public sealed class SingleFlightGuard : ISingleFlightGuard
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <inheritdoc />
    public async Task<T> RunExclusiveAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
