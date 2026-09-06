using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Configuration.Models;
using Ashlar.Core.Application.Configuration.Ports;
using Ashlar.Core.Domain;
using Ashlar.Core.Domain.Exceptions;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Configuration;
using Ashlar.Infrastructure.Execution;
using Ashlar.Tests.Infrastructure.Helpers;
using System.Text.Json;
using Xunit;

using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Tests.Infrastructure.Tests.Onboarding;

/// <summary>
/// E2E tests for the onboarding and first-run experience.
/// Covers provider detection, config resolution, doctor-style checks,
/// first-run state detection, and the full bootstrap-to-ready path.
/// </summary>
[Trait("Category", "E2E")]
[Collection("EnvironmentVariables")]
public sealed class OnboardingE2ETests : IDisposable
{
    private readonly string _tempDir;

    public OnboardingE2ETests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ashlar-onboarding-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Provider Availability ────────────────────────────────────────

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ProviderDetection_MockDisabledByDefault()
    {
        await Task.CompletedTask;
        var prev = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", null);
            var factory = CreateProviderFactory();

            factory.IsProviderAvailable("mock").Should().BeFalse();
            factory.IsProviderAvailable("offline").Should().BeFalse();
            factory.IsProviderAvailable("echo").Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ProviderDetection_MockEnabledByEnvVar()
    {
        await Task.CompletedTask;
        var prev = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", "1");
            var factory = CreateProviderFactory();

            factory.IsProviderAvailable("mock").Should().BeTrue();
            factory.IsProviderAvailable("offline").Should().BeTrue();
            factory.IsProviderAvailable("echo").Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ProviderDetection_OpenAiRequiresApiKey()
    {
        await Task.CompletedTask;
        var prevKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            var factory = CreateProviderFactory();
            factory.IsProviderAvailable("openai").Should().BeFalse();

            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test-key");
            var factory2 = CreateProviderFactory();
            factory2.IsProviderAvailable("openai").Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", prevKey);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ProviderDetection_AzureRequiresAllThreeVars()
    {
        await Task.CompletedTask;
        var prevE = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var prevK = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var prevD = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://test.openai.azure.com");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "test-key");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);
            var factory = CreateProviderFactory();
            factory.IsProviderAvailable("azure").Should().BeFalse("missing deployment");

            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", "gpt-4");
            var factory2 = CreateProviderFactory();
            factory2.IsProviderAvailable("azure").Should().BeTrue("all three set");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", prevE);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", prevK);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", prevD);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ProviderDetection_LocalRequiresModelPath()
    {
        await Task.CompletedTask;
        var prevPath = Environment.GetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", null);
            var factory = CreateProviderFactory();
            factory.IsProviderAvailable("local").Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOCAL_MODEL_PATH", prevPath);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ProviderDetection_AllProviderNames_DoNotThrow()
    {
        await Task.CompletedTask;
        var factory = CreateProviderFactory();
        var names = new[] { "openai", "azure", "ollama", "local", "video", "mock", "offline", "mock-json", "echo", "", "   ", "nonexistent" };
        foreach (var name in names)
        {
            var act = () => factory.IsProviderAvailable(name);
            act.Should().NotThrow($"checking provider '{name}' should not throw");
        }
    }

    // ── Config File Resolution ───────────────────────────────────────

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ConfigResolution_CustomPath_LoadsCorrectFile()
    {
        var configPath = Path.Combine(_tempDir, "onboarding-config.json");
        var config = new AshlarConfiguration
        {
            Analysis = new AnalysisConfiguration
            {
                MaxComplexityThreshold = 99,
                EnabledRules = new[] { "OnboardingRule" },
                EnableSecurityScan = true,
                EnableCodeQuality = false
            },
            Validation = new ValidationConfiguration
            {
                TimeoutSeconds = 42,
                FailOnNoTests = true,
                TestProjectPatterns = new[] { "*.csproj" }
            }
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));

        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", configPath);
            var logger = new LoggerFactory().CreateLogger<ConfigurationServiceAdapter>();
            var adapter = new ConfigurationServiceAdapter(logger);

            var loaded = await adapter.LoadAsync();
            loaded.Analysis.MaxComplexityThreshold.Should().Be(99);
            loaded.Validation.TimeoutSeconds.Should().Be(42);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ConfigResolution_MissingFile_Permissive_ReturnsDefaults()
    {
        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", Path.Combine(_tempDir, "does-not-exist.json"));
            var logger = new LoggerFactory().CreateLogger<ConfigurationServiceAdapter>();
            var adapter = new ConfigurationServiceAdapter(logger, failOnConfigWarnings: false);

            var config = await adapter.LoadAsync();
            config.Should().NotBeNull();
            config.Analysis.MaxComplexityThreshold.Should().Be(AshlarDefaults.AnalysisMaxComplexityThreshold);
            config.Validation.TimeoutSeconds.Should().Be(AshlarDefaults.ValidationTimeoutSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ConfigResolution_MissingFile_StrictMode_Throws()
    {
        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", Path.Combine(_tempDir, "strict-missing.json"));
            var logger = new LoggerFactory().CreateLogger<ConfigurationServiceAdapter>();
            var adapter = new ConfigurationServiceAdapter(logger, failOnConfigWarnings: true);

            var act = async () => await adapter.LoadAsync();
            (await act.Should().ThrowAsync<ConfigurationException>())
                .Which.ErrorCode.Should().Be(ErrorCodes.ConfigFileNotFound);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ConfigResolution_CorruptJson_AlwaysThrows()
    {
        var configPath = Path.Combine(_tempDir, "corrupt.json");
        File.WriteAllText(configPath, "{{{{not json}}}}");

        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", configPath);
            var logger = new LoggerFactory().CreateLogger<ConfigurationServiceAdapter>();
            var adapter = new ConfigurationServiceAdapter(logger, failOnConfigWarnings: false);

            var act = async () => await adapter.LoadAsync();
            (await act.Should().ThrowAsync<ConfigurationException>())
                .Which.ErrorCode.Should().Be(ErrorCodes.ConfigInvalidFormat);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ConfigResolution_SaveCreatesDirectory()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "deep", "config.json");
        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", nestedPath);
            var logger = new LoggerFactory().CreateLogger<ConfigurationServiceAdapter>();
            var adapter = new ConfigurationServiceAdapter(logger);

            var config = adapter.GetDefault();
            await adapter.SaveAsync(config);

            File.Exists(nestedPath).Should().BeTrue("SaveAsync should create parent directories");
            var loaded = await adapter.LoadAsync();
            loaded.Analysis.MaxComplexityThreshold.Should().Be(AshlarDefaults.AnalysisMaxComplexityThreshold);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ConfigResolution_PartialJson_LoadsWithNulls()
    {
        var configPath = Path.Combine(_tempDir, "partial.json");
        File.WriteAllText(configPath, """{"analysis": {"maxComplexityThreshold": 5}}""");

        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", configPath);
            var logger = new LoggerFactory().CreateLogger<ConfigurationServiceAdapter>();
            var adapter = new ConfigurationServiceAdapter(logger);

            var loaded = await adapter.LoadAsync();
            loaded.Should().NotBeNull();
            loaded.Analysis.MaxComplexityThreshold.Should().Be(5);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    // ── DI Host Onboarding ───────────────────────────────────────────

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task HostBootstrap_FullProfile_ResolvesAllOnboardingServices()
    {
        await Task.CompletedTask;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlar();
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IConfigurationService>().Should().NotBeNull();
        sp.GetRequiredService<Ashlar.Infrastructure.Execution.IProviderFactory>().Should().NotBeNull();
        sp.GetRequiredService<StrictModeOptions>().Should().NotBeNull();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task HostBootstrap_StrictModeEnabled_PropagatesConfigFailure()
    {
        await Task.CompletedTask;
        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", Path.Combine(_tempDir, "host-strict-missing.json"));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAshlar(opts => opts.StrictMode.Enabled = true);
            var sp = services.BuildServiceProvider();

            var configService = sp.GetRequiredService<IConfigurationService>();
            var act = async () => await configService.LoadAsync();
            await act.Should().ThrowAsync<ConfigurationException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task HostBootstrap_MockProvider_CanExecuteFirstTask()
    {
        var prevMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", "1");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAshlar();
            var sp = services.BuildServiceProvider();

            var factory = sp.GetRequiredService<Ashlar.Infrastructure.Execution.IProviderFactory>();
            factory.IsProviderAvailable("mock").Should().BeTrue();

            var result = await factory.ExecuteLLMAsync("mock", "system", "hello", new { }, CancellationToken.None);
            result.Should().NotBeNullOrEmpty("mock provider should return a response for first task");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prevMock);
        }
    }

    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData(AshlarDeploymentProfile.Full)]
    [InlineData(AshlarDeploymentProfile.Server)]
    [InlineData(AshlarDeploymentProfile.Edge)]
    [InlineData(AshlarDeploymentProfile.AirGapped)]
    [InlineData(AshlarDeploymentProfile.System)]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation)]
    public async Task HostBootstrap_AllProfiles_ResolveConfigService(AshlarDeploymentProfile profile)
    {
        await Task.CompletedTask;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlarProfile(profile);
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IConfigurationService>().Should().NotBeNull();
        var defaults = sp.GetRequiredService<IConfigurationService>().GetDefault();
        defaults.Analysis.MaxComplexityThreshold.Should().Be(AshlarDefaults.AnalysisMaxComplexityThreshold);
    }

    // ── Defaults Alignment ───────────────────────────────────────────

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task Defaults_ConfigService_MatchesAshlarDefaults()
    {
        await Task.CompletedTask;
        var logger = new LoggerFactory().CreateLogger<ConfigurationServiceAdapter>();
        var adapter = new ConfigurationServiceAdapter(logger);
        var defaults = adapter.GetDefault();

        defaults.Analysis.MaxComplexityThreshold.Should().Be(AshlarDefaults.AnalysisMaxComplexityThreshold);
        defaults.Validation.TimeoutSeconds.Should().Be(AshlarDefaults.ValidationTimeoutSeconds);
        defaults.Analysis.EnabledRules.Should().Contain("SecurityScan");
        defaults.Analysis.EnabledRules.Should().Contain("CodeQuality");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task Defaults_StrictMode_DisabledByDefault()
    {
        await Task.CompletedTask;
        using var _ = EnvironmentVariableScope.Unset("ASHLAR_STRICT_MODE");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlar();
        var sp = services.BuildServiceProvider();

        var strict = sp.GetRequiredService<StrictModeOptions>();
        strict.Enabled.Should().BeFalse();
        strict.ShouldFailFastOnValidation.Should().BeFalse();
        strict.ShouldFailFastOnProvider.Should().BeFalse();
        strict.ShouldFailOnConfigurationWarnings.Should().BeFalse();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task Defaults_StrictMode_FromEnvVar()
    {
        await Task.CompletedTask;
        var prev = Environment.GetEnvironmentVariable("ASHLAR_STRICT_MODE");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_STRICT_MODE", "1");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAshlar();
            var sp = services.BuildServiceProvider();

            sp.GetRequiredService<StrictModeOptions>().Enabled.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_STRICT_MODE", prev);
        }
    }

    // ── Error Messages ───────────────────────────────────────────────

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ErrorMessages_MissingOpenAiKey_IncludesEnvVarName()
    {
        var prevKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var prevMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", "1");
            var factory = CreateProviderFactory();

            var act = () => factory.ExecuteLLMAsync("openai", "s", "u", new { }, CancellationToken.None);
            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("OPENAI_API_KEY");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prevMock);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ErrorMessages_MockDisabled_IncludesAllowMockHint()
    {
        var prevMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", null);
            var factory = CreateProviderFactory();

            var act = () => factory.ExecuteLLMAsync("mock", "s", "u", new { }, CancellationToken.None);
            (await act.Should().ThrowAsync<ModelUnavailableException>())
                .Which.Message.Should().Contain("ASHLAR_ALLOW_MOCK");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prevMock);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ErrorMessages_StrictConfig_IncludesFilePath()
    {
        var missingPath = Path.Combine(_tempDir, "strict-err-path.json");
        var prev = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", missingPath);
            var logger = new LoggerFactory().CreateLogger<ConfigurationServiceAdapter>();
            var adapter = new ConfigurationServiceAdapter(logger, failOnConfigWarnings: true);

            var act = async () => await adapter.LoadAsync();
            (await act.Should().ThrowAsync<ConfigurationException>())
                .Which.Message.Should().Contain(missingPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", prev);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static ProviderFactory CreateProviderFactory()
    {
        var logger = new LoggerFactory().CreateLogger<ProviderFactory>();
        return new ProviderFactory(logger);
    }
}
