using FluentAssertions;
using Ashlar.Infrastructure.Execution;
using System.Reflection;
using Xunit;

using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Tests.Infrastructure.Tests.Safety;

[Collection("LocalModelProviderState")]

/// <summary>
/// Safety tests for LocalModelProvider.
/// Ensures explicit failure when model is unavailable — no silent fallback.
///
/// LocalModelProvider_InAirGappedMode_DoesNotAttemptNetworkCall — intentionally not implemented.
/// LlamaSharp performs in-process inference from a GGUF file on disk.
/// It does not use HttpClient or any network stack. Network isolation
/// is enforced at the OS/container level (--network none) via make test-airgapped.
/// Verified by: test-air-gapped-no-network.yml CI workflow.
/// </summary>
[Trait("Category", "Safety")]
[Trait("Category", "Unit")]
public sealed class LocalModelProviderSafetyTests
{
    [Fact]
    public void LocalModelProvider_WhenModelFileNotFound_IsAvailableReturnsFalse()
    {
        var previous = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", "/nonexistent/model.gguf");

            var available = LocalModelProvider.IsAvailable();

            available.Should().BeFalse("nonexistent model path must report unavailable");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", previous);
        }
    }

    [Fact]
    public async Task LocalModelProvider_WhenModelFileNotFound_ExecuteAsyncThrowsExplicitly()
    {
        var previous = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", "/nonexistent/model.gguf");

            var act = () => LocalModelProvider.ExecuteAsync("sys", "user", null, default);

            await act.Should().ThrowAsync<ModelUnavailableException>()
                .WithMessage("*Local model not configured*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", previous);
        }
    }

    [Fact]
    public void LocalModelProvider_WhenRelativePathExists_IsAvailableReturnsTrue()
    {
        var original = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH");
        var tempDir = Path.Combine(Path.GetTempPath(), "ashlar-local-model-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var modelFile = Path.Combine(tempDir, "model.gguf");
        File.WriteAllBytes(modelFile, [0x1]);

        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", modelFile);
            LocalModelProvider.IsAvailable().Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", original);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LocalModelProvider_resolves_relative_model_path_from_current_directory()
    {
        var originalEnv = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH");
        var originalCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), "ashlar-local-model-rel-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        File.WriteAllBytes(Path.Combine(tempDir, "model.gguf"), [0x1]);

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", "model.gguf");
            LocalModelProvider.IsAvailable().Should().BeTrue();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", originalEnv);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LocalModelProvider_WhenPathNotSet_IsAvailableReturnsFalse()
    {
        var previous = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", null);

            var available = LocalModelProvider.IsAvailable();

            available.Should().BeFalse("unset path must report unavailable");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", previous);
        }
    }

    [Fact]
    public async Task LocalModelProvider_WhenInvalidGgufFile_ExecuteAsyncThrowsLoadFailure()
    {
        ResetLoadedModelState();
        var previousPath = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH");
        var previousContext = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_CONTEXT_SIZE");
        var tempDir = Path.Combine(Path.GetTempPath(), "ashlar-invalid-gguf-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var modelFile = Path.Combine(tempDir, "invalid.gguf");
        await File.WriteAllBytesAsync(modelFile, [0x1, 0x2, 0x3, 0x4]);

        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", modelFile);
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_CONTEXT_SIZE", "512");

            var act = () => LocalModelProvider.ExecuteAsync("sys", "user", null, default);

            await act.Should().ThrowAsync<ModelUnavailableException>()
                .WithMessage("*Failed to load local model*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", previousPath);
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_CONTEXT_SIZE", previousContext);
            ResetLoadedModelState();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LocalModelProvider_expands_environment_variables_in_model_path()
    {
        var original = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH");
        var tempDir = Path.Combine(Path.GetTempPath(), "ashlar-local-model-env-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var modelFile = Path.Combine(tempDir, "model.gguf");
        File.WriteAllBytes(modelFile, [0x1]);
        Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_ENV_DIR", tempDir);

        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", "%ASHLAR_LOCAL_MODEL_ENV_DIR%/model.gguf");
            LocalModelProvider.IsAvailable().Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", original);
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_ENV_DIR", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalModelProvider_invalid_context_size_falls_back_to_default_load_failure()
    {
        ResetLoadedModelState();
        var previousPath = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH");
        var previousContext = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_CONTEXT_SIZE");
        var tempDir = Path.Combine(Path.GetTempPath(), "ashlar-local-context-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var modelFile = Path.Combine(tempDir, "invalid.gguf");
        await File.WriteAllBytesAsync(modelFile, [0x1, 0x2, 0x3, 0x4]);

        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", modelFile);
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_CONTEXT_SIZE", "not-a-number");

            var act = () => LocalModelProvider.ExecuteAsync("", "user only", null, default);
            await act.Should().ThrowAsync<ModelUnavailableException>()
                .WithMessage("*Failed to load local model*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", previousPath);
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_CONTEXT_SIZE", previousContext);
            ResetLoadedModelState();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void ResetLoadedModelState()
    {
        var type = typeof(LocalModelProvider);
        type.GetField("_weights", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
        type.GetField("_context", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
    }
}
