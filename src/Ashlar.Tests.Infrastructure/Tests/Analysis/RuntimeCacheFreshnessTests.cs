using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Application.Analysis.Ports;
using Ashlar.Core.Application.Common.Models;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Validation.Models;
using Ashlar.Core.Application.Validation.Ports;
using Ashlar.Infrastructure.Analysis.Adapters;
using Ashlar.Infrastructure.Caching;
using System.Runtime.InteropServices;
using Ashlar.Infrastructure.Validation.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Analysis;

[Collection("ProcessCwd")]
public sealed class RuntimeCacheFreshnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ashlar-cache-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ICacheStrategy> _cache = new();
    private readonly List<string> _keys = [];

    public RuntimeCacheFreshnessTests()
    {
        Directory.CreateDirectory(_root);
        _cache.Setup(c => c.GetAsync<AnalysisResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((key, _) => _keys.Add(key)).ReturnsAsync((AnalysisResult?)null);
        _cache.Setup(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((key, _) => _keys.Add(key)).ReturnsAsync((ValidationResult?)null);
    }

    [Fact]
    public async Task Analysis_tracks_content_after_the_first_hundred_files_even_if_timestamp_is_preserved()
    {
        for (var i = 0; i < 101; i++) File.WriteAllText(Path.Combine(_root, $"{i:D3}.cs"), "old");
        var adapter = AnalysisAdapter();
        await adapter.AnalyzeAsync(new DirectoryInfo(_root));
        var file = Path.Combine(_root, "100.cs");
        var timestamp = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "new");
        File.SetLastWriteTimeUtc(file, timestamp);
        await adapter.AnalyzeAsync(new DirectoryInfo(_root));
        Assert.NotEqual(_keys[0], _keys[1]);
    }

    [Fact]
    public async Task Analysis_distinguishes_empty_roots_and_stable_input_reuses_identity()
    {
        var other = Directory.CreateDirectory(Path.Combine(_root, "other"));
        var empty = Directory.CreateDirectory(Path.Combine(_root, "empty"));
        var adapter = AnalysisAdapter();
        await adapter.AnalyzeAsync(empty);
        await adapter.AnalyzeAsync(empty);
        await adapter.AnalyzeAsync(other);
        Assert.Equal(_keys[0], _keys[1]);
        Assert.NotEqual(_keys[0], _keys[2]);
    }

    [Fact]
    public async Task Analysis_tracks_added_and_deleted_files()
    {
        var adapter = AnalysisAdapter();
        await adapter.AnalyzeAsync(new DirectoryInfo(_root));
        var file = Path.Combine(_root, "input.txt");
        File.WriteAllText(file, "input");
        await adapter.AnalyzeAsync(new DirectoryInfo(_root));
        File.Delete(file);
        await adapter.AnalyzeAsync(new DirectoryInfo(_root));
        Assert.NotEqual(_keys[0], _keys[1]);
        Assert.Equal(_keys[0], _keys[2]);
    }

    [Fact]
    public async Task Analysis_does_not_cache_a_result_if_inputs_changed_during_execution()
    {
        var inner = new Mock<IAnalysisService>();
        inner.Setup(s => s.AnalyzeAsync(It.IsAny<DirectoryInfo>(), It.IsAny<IProgress<ProgressReport>?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                File.WriteAllText(Path.Combine(_root, "changed.cs"), "changed");
                return Task.FromResult(new AnalysisResult { HasViolations = false, Violations = [], TotalViolations = 0 });
            });
        await new CachedAnalysisServiceAdapter(inner.Object, _cache.Object,
            NullLogger<CachedAnalysisServiceAdapter>.Instance).AnalyzeAsync(new DirectoryInfo(_root));
        _cache.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<AnalysisResult>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Analysis_unreadable_snapshot_falls_through_without_cache_access()
    {
        await AnalysisAdapter().AnalyzeAsync(new DirectoryInfo(Path.Combine(_root, "missing")));
        Assert.Empty(_keys);
    }

    [Fact]
    public async Task Validation_tracks_source_content_and_distinguishes_null_from_literal_none()
    {
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "Tests.csproj"), "<Project />");
            var source = Path.Combine(_root, "Production.cs");
            File.WriteAllText(source, "old");
            var inner = new Mock<IValidationService>();
            inner.Setup(s => s.ValidateAsync(It.IsAny<string?>(), It.IsAny<IProgress<ProgressReport>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ValidationResult { Passed = true, Message = "ok", TestsRun = 0, TestsPassed = 0, TestsFailed = 0 });
            var adapter = new CachedValidationServiceAdapter(inner.Object, _cache.Object,
                NullLogger<CachedValidationServiceAdapter>.Instance);
            await adapter.ValidateAsync(null);
            await adapter.ValidateAsync("none");
            var timestamp = File.GetLastWriteTimeUtc(source);
            File.WriteAllText(source, "new");
            File.SetLastWriteTimeUtc(source, timestamp);
            await adapter.ValidateAsync(null);
            Assert.Equal(3, _keys.Distinct().Count());
        }
        finally { Directory.SetCurrentDirectory(previous); }
    }

    [Fact]
    public async Task Validation_ignores_its_own_generated_outputs_but_invalidates_changed_sources()
    {
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_root);
            var source = Path.Combine(_root, "Tests.cs");
            File.WriteAllText(source, "source");
            var results = new Dictionary<string, ValidationResult>();
            _cache.Setup(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) => results.GetValueOrDefault(key));
            _cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<ValidationResult>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .Callback<string, ValidationResult, TimeSpan?, CancellationToken>((key, value, _, _) => results[key] = value)
                .Returns(Task.CompletedTask);
            var inner = new Mock<IValidationService>();
            inner.Setup(s => s.ValidateAsync(It.IsAny<string?>(), It.IsAny<IProgress<ProgressReport>?>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    foreach (var directory in new[] { "bin", "obj", "TestResults" })
                    {
                        Directory.CreateDirectory(Path.Combine(_root, directory));
                        File.WriteAllText(Path.Combine(_root, directory, "generated.txt"), Guid.NewGuid().ToString());
                    }
                    return Task.FromResult(new ValidationResult { Passed = true, Message = "ok", TestsRun = 1, TestsPassed = 1, TestsFailed = 0 });
                });
            var adapter = new CachedValidationServiceAdapter(inner.Object, _cache.Object,
                NullLogger<CachedValidationServiceAdapter>.Instance);
            await adapter.ValidateAsync(null);
            Assert.Single(results);
            await adapter.ValidateAsync(null);
            inner.Verify(s => s.ValidateAsync(It.IsAny<string?>(), It.IsAny<IProgress<ProgressReport>?>(), It.IsAny<CancellationToken>()), Times.Once);
            File.WriteAllText(source, "changed source");
            await adapter.ValidateAsync(null);
            inner.Verify(s => s.ValidateAsync(It.IsAny<string?>(), It.IsAny<IProgress<ProgressReport>?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally { Directory.SetCurrentDirectory(previous); }
    }

    [Fact]
    public async Task Analysis_still_fingerprints_compiled_outputs_in_bin()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin"));
        var assembly = Path.Combine(bin.FullName, "assembly.dll");
        File.WriteAllText(assembly, "first");
        var adapter = AnalysisAdapter();
        await adapter.AnalyzeAsync(new DirectoryInfo(_root));
        File.WriteAllText(assembly, "other");
        await adapter.AnalyzeAsync(new DirectoryInfo(_root));
        Assert.NotEqual(_keys[0], _keys[1]);
    }

    [Fact]
    public async Task Oversized_trees_bypass_cache_without_reading_the_whole_file()
    {
        var path = Path.Combine(_root, "large-input");
        using (var stream = File.Create(path)) stream.SetLength(DirectoryFingerprint.MaximumBytes + 1);
        Assert.Null(await DirectoryFingerprint.TryComputeAsync(new DirectoryInfo(_root), CancellationToken.None));
    }

    [Fact]
    public async Task Unix_fifo_bypasses_cache_without_waiting_for_a_writer()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_root, "fifo");
        Assert.Equal(0, MakeFifo(path, 384)); // 0600
        var fingerprint = Task.Run(() => DirectoryFingerprint.TryComputeAsync(new DirectoryInfo(_root), CancellationToken.None));
        try
        {
            Assert.Null(await fingerprint.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            if (!fingerprint.IsCompleted)
            {
                // Release an incorrect blocking implementation so the red test does not leave
                // a thread waiting on open(). O_RDWR on a FIFO connects both ends immediately.
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                await fingerprint.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
    }

    [Fact]
    public void Fingerprint_file_handle_allows_concurrent_writes_and_atomic_replacement()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_root, "replaceable.txt");
        File.WriteAllText(path, "before");
        using var reader = CacheFingerprintFile.Open(path);
        File.WriteAllText(path, "edited");
        var replacement = Path.Combine(_root, "replacement.txt");
        File.WriteAllText(replacement, "replaced");
        File.Move(replacement, path, overwrite: true);
        Assert.Equal("replaced", File.ReadAllText(path));
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private CachedAnalysisServiceAdapter AnalysisAdapter()
    {
        var inner = new Mock<IAnalysisService>();
        inner.Setup(s => s.AnalyzeAsync(It.IsAny<DirectoryInfo>(), It.IsAny<IProgress<ProgressReport>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisResult { HasViolations = false, Violations = [], TotalViolations = 0 });
        return new CachedAnalysisServiceAdapter(inner.Object, _cache.Object, NullLogger<CachedAnalysisServiceAdapter>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
