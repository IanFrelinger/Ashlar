using Ashlar.Abstractions.Paths;
using Ashlar.Tests.Application.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Helpers;

/// <summary>
/// Base class for tests that create temporary files.
/// Auto-creates a temp dir and disposes it, enforcing resource cleanup.
/// </summary>
public abstract class TempDirTestBase : IDisposable
{
    private readonly string _tempDir;
    private readonly IDisposable _tempDirCleanup;
    private bool _disposed;

    /// <summary>
    /// Path to the temp directory for this test. All temp files should go under this path.
    /// </summary>
    protected string TempDir => _tempDir;

    /// <summary>
    /// Creates a temp directory with the given prefix.
    /// </summary>
    /// <param name="prefix">Prefix for the temp dir name (e.g. "ashlar-dogfood-block1").</param>
    protected TempDirTestBase(string prefix = "ashlar-test")
    {
        (_tempDir, _tempDirCleanup) = TestHelpers.CreateTempDirectoryWithCleanup(prefix);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        if (Environment.GetEnvironmentVariable("ASHLAR_TEST_MEMORY_DIAG") == "1")
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            _ = GC.GetTotalMemory(false);
        }
        _tempDirCleanup.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asserts that the temp dir is under the system temp path (guard against accidental repo pollution).
    /// </summary>
    protected void AssertTempDirIsUnderSystemTemp()
    {
        var tempRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullTempDir = Path.GetFullPath(_tempDir);
        Assert.True(PathContainment.IsWithin(fullTempDir, tempRoot),
            $"Temp dir should be under {tempRoot}, got {fullTempDir}");
    }
}
