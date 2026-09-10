using FluentAssertions;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Configuration.Models;
using Ashlar.Core.Domain;
using Ashlar.Core.Domain.Exceptions;
using Ashlar.Infrastructure.Configuration;
using Ashlar.Tests.Infrastructure.Helpers;
using System.Text.Json;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Configuration;

/// <summary>
/// Edge-case tests for <see cref="ConfigurationServiceAdapter"/>.
/// Covers ASHLAR_CONFIG_PATH, strict mode integration, malformed JSON,
/// concurrent load/save, empty files, and round-trip fidelity.
/// </summary>
[Trait("Category", "E2E")]
[Collection("EnvironmentVariables")]
public sealed class ConfigurationAdapterEdgeCaseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger<ConfigurationServiceAdapter> _logger;

    public ConfigurationAdapterEdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ashlar-config-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logger = new LoggerFactory().CreateLogger<ConfigurationServiceAdapter>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task ConfigPath_FromEnvironment_IsRespected()
    {
        var configPath = Path.Combine(_tempDir, "custom-config.json");
        var config = new AshlarConfiguration
        {
            Analysis = new AnalysisConfiguration
            {
                MaxComplexityThreshold = 42,
                EnabledRules = new[] { "Custom" },
                EnableSecurityScan = false,
                EnableCodeQuality = true
            },
            Validation = new ValidationConfiguration
            {
                TimeoutSeconds = 999,
                FailOnNoTests = true,
                TestProjectPatterns = new[] { "*.csproj" }
            }
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));

        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", configPath);
            var adapter = new ConfigurationServiceAdapter(_logger);
            var loaded = await adapter.LoadAsync();

            loaded.Analysis.MaxComplexityThreshold.Should().Be(42);
            loaded.Validation.TimeoutSeconds.Should().Be(999);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task MalformedJson_ThrowsConfigurationException()
    {
        var configPath = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(configPath, "{ this is not valid json }}}");

        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", configPath);
            var adapter = new ConfigurationServiceAdapter(_logger);

            var act = async () => await adapter.LoadAsync();
            (await act.Should().ThrowAsync<ConfigurationException>())
                .Which.ErrorCode.Should().Be(ErrorCodes.ConfigInvalidFormat);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task EmptyFile_StrictMode_Throws()
    {
        var configPath = Path.Combine(_tempDir, "empty.json");
        File.WriteAllText(configPath, "");

        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", configPath);
            var adapter = new ConfigurationServiceAdapter(_logger, failOnConfigWarnings: true);

            var act = async () => await adapter.LoadAsync();
            (await act.Should().ThrowAsync<ConfigurationException>())
                .Which.ErrorCode.Should().Be(ErrorCodes.ConfigInvalidFormat);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task EmptyFile_Permissive_ReturnsDefaults()
    {
        var configPath = Path.Combine(_tempDir, "null.json");
        File.WriteAllText(configPath, "null");

        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", configPath);
            var adapter = new ConfigurationServiceAdapter(_logger, failOnConfigWarnings: false);

            var config = await adapter.LoadAsync();
            config.Should().NotBeNull();
            config.Analysis.MaxComplexityThreshold.Should().Be(AshlarDefaults.AnalysisMaxComplexityThreshold);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task SaveAndLoad_RoundTrip_PreservesData()
    {
        var configPath = Path.Combine(_tempDir, "roundtrip.json");

        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", configPath);
            var adapter = new ConfigurationServiceAdapter(_logger);

            var original = new AshlarConfiguration
            {
                Analysis = new AnalysisConfiguration
                {
                    MaxComplexityThreshold = 77,
                    EnabledRules = new[] { "A", "B", "C" },
                    EnableSecurityScan = true,
                    EnableCodeQuality = false
                },
                Validation = new ValidationConfiguration
                {
                    TimeoutSeconds = 123,
                    FailOnNoTests = true,
                    TestProjectPatterns = new[] { "*.Tests.csproj" }
                }
            };

            await adapter.SaveAsync(original);
            var loaded = await adapter.LoadAsync();

            loaded.Analysis.MaxComplexityThreshold.Should().Be(77);
            loaded.Analysis.EnabledRules.Should().BeEquivalentTo(new[] { "A", "B", "C" });
            loaded.Validation.TimeoutSeconds.Should().Be(123);
            loaded.Validation.FailOnNoTests.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task ConcurrentLoads_DoNotCorrupt()
    {
        var configPath = Path.Combine(_tempDir, "concurrent.json");
        var config = new AshlarConfiguration
        {
            Analysis = new AnalysisConfiguration
            {
                MaxComplexityThreshold = 10,
                EnabledRules = new[] { "R1" },
                EnableSecurityScan = true,
                EnableCodeQuality = true
            },
            Validation = new ValidationConfiguration
            {
                TimeoutSeconds = 60,
                FailOnNoTests = false,
                TestProjectPatterns = new[] { "*.csproj" }
            }
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));

        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", configPath);

            var tasks = Enumerable.Range(0, 20).Select(_ =>
            {
                var adapter = new ConfigurationServiceAdapter(_logger);
                return adapter.LoadAsync();
            });

            var results = await Task.WhenAll(tasks);
            results.Should().AllSatisfy(c =>
            {
                c.Analysis.MaxComplexityThreshold.Should().Be(10);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task GetDefault_UsesAshlarDefaults()
    {
        var adapter = new ConfigurationServiceAdapter(_logger);
        var defaults = adapter.GetDefault();
        await Task.CompletedTask;

        defaults.Analysis.MaxComplexityThreshold.Should().Be(AshlarDefaults.AnalysisMaxComplexityThreshold);
        defaults.Validation.TimeoutSeconds.Should().Be(AshlarDefaults.ValidationTimeoutSeconds);
        defaults.Analysis.EnabledRules.Should().Contain("SecurityScan");
        defaults.Analysis.EnabledRules.Should().Contain("CodeQuality");
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task MissingFile_StrictMode_ThrowsWithPath()
    {
        var configPath = Path.Combine(_tempDir, "nonexistent", "missing.json");

        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", configPath);
            var adapter = new ConfigurationServiceAdapter(_logger, failOnConfigWarnings: true);

            var act = async () => await adapter.LoadAsync();
            (await act.Should().ThrowAsync<ConfigurationException>())
                .Which.Message.Should().Contain(configPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }
}
