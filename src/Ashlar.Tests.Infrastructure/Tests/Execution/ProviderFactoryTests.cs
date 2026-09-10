using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Core.Application.Testing.Abstractions;
using Ashlar.Core.Application.Testing.Models;
using Ashlar.Infrastructure.Execution;
using System.Text.Json;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>
/// Tests for ProviderFactory.
/// </summary>
[Collection("EnvironmentVariables")]
public class ProviderFactoryTests : UnitTestBase
{
    [Fact]
    public async Task ProviderFactory_AllTests_Pass()
    {
        var result = await ExecuteAsync(CancellationToken.None);
        Assert.True(result.Passed, result.ErrorMessage ?? result.Message);
    }

    public override async Task<TestResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await TestIsProviderAvailable();
            await TestExecuteLLMAsync();
            await TestExecuteLLMAsync_SelfExtendUnityBootstrap();
            await TestExecuteLLMAsync_SelfExtendUnityBootstrapNuanced();
            await TestExecuteLLMAsync_SelfExtendUnityBootstrap_GeneratesComposableCommandScaffolds();
            await TestExecuteLLMAsync_SelfExtendPersonalApp_GeneratesComposableCommandScaffolds();
            await TestExecuteLLMAsync_SelfExtendUiDemo_GeneratesDomainAndUiScaffolds();
            await TestExecuteLLMAsync_SelfExtendUiFeatureHotload_GeneratesFeatureModule();
            await TestExecuteLLMAsync_SelfExtendAvaloniaHotload_GeneratesFeatureDescriptor();
            await TestExecuteLLMAsync_SelfExtendAvaloniaTransform_GeneratesReplacementDescriptor();
            await TestExecuteVisionAsync();
            
            return new TestResult
            {
                Name = nameof(ProviderFactoryTests),
                Category = "Infrastructure",
                Passed = true,
                Message = "All ProviderFactory tests passed"
            };
        }
        catch (AssertionException ex)
        {
            return new TestResult
            {
                Name = nameof(ProviderFactoryTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = $"Assertion failed: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
        catch (Exception ex)
        {
            return new TestResult
            {
                Name = nameof(ProviderFactoryTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = $"Unexpected exception: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
    }
    
    private async Task TestIsProviderAvailable()
    {
        var mockLogger = new Mock<ILogger<ProviderFactory>>();
        var factory = new ProviderFactory(mockLogger.Object);

        // Mock providers require ASHLAR_ALLOW_MOCK=1 (disabled by default for production)
        WithEnv("ASHLAR_ALLOW_MOCK", "1", () =>
        {
            AssertTrue(factory.IsProviderAvailable("mock"));
            AssertTrue(factory.IsProviderAvailable("offline"));
            AssertTrue(factory.IsProviderAvailable("mock-json"));
            AssertTrue(factory.IsProviderAvailable("echo"));
        });
        WithEnv("ASHLAR_ALLOW_MOCK", null, () =>
        {
            AssertFalse(factory.IsProviderAvailable("mock"));
        });

        _ = factory.IsProviderAvailable("ollama"); // Depends on /api/tags reachability in test environment.
        AssertFalse(factory.IsProviderAvailable("unknown"));

        // Real providers depend on environment configuration; validate behavior is env-sensitive.
        WithEnv("OPENAI_API_KEY", null, () =>
        {
            AssertFalse(factory.IsProviderAvailable("openai"));
        });

        WithEnv("OPENAI_COMPAT_API_KEY", null, () =>
        {
            WithEnv("OPENAI_COMPAT_BASE_URL", null, () =>
            {
                AssertFalse(factory.IsProviderAvailable("openai_compat"));
            });
        });

        WithEnv("OPENAI_COMPAT_API_KEY", "k", () =>
        {
            WithEnv("OPENAI_COMPAT_BASE_URL", null, () =>
            {
                AssertFalse(factory.IsProviderAvailable("openai_compat"));
            });
        });

        WithEnv("OPENAI_COMPAT_API_KEY", "k", () =>
        {
            WithEnv("OPENAI_COMPAT_BASE_URL", "http://localhost:8000", () =>
            {
                AssertTrue(factory.IsProviderAvailable("openai_compat"));
            });
        });

        WithEnv("AZURE_OPENAI_ENDPOINT", null, () =>
        {
            WithEnv("AZURE_OPENAI_API_KEY", null, () =>
            {
                WithEnv("AZURE_OPENAI_DEPLOYMENT", null, () =>
                {
                    AssertFalse(factory.IsProviderAvailable("azure"));
                });
            });
        });
        
        await Task.CompletedTask;
    }
    
    private async Task TestExecuteLLMAsync()
    {
        var mockLogger = new Mock<ILogger<ProviderFactory>>();
        var factory = new ProviderFactory(mockLogger.Object);

        // Mock provider requires ASHLAR_ALLOW_MOCK=1
        await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
        {
            var result = await factory.ExecuteLLMAsync(
                "mock",
                "You are a test",
                "Test prompt",
                new { },
                CancellationToken.None);

            AssertNotNull(result);
            AssertTrue(IsJsonObject(result), "mock provider should return JSON");
        });
        
        // openai when not configured should throw (fail fast, no mock fallback).
        WithEnv("OPENAI_API_KEY", null, async () =>
        {
            await AssertThrowsAsync<InvalidOperationException>(async () =>
                await factory.ExecuteLLMAsync("openai", "Test", "Test", new { }, CancellationToken.None));
        }).GetAwaiter().GetResult();

        // unknown provider should throw.
        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await factory.ExecuteLLMAsync("unknown_provider", "Test", "Test", new { }, CancellationToken.None));

        await Task.CompletedTask;
    }

    private async Task TestExecuteVisionAsync()
    {
        var mockLogger = new Mock<ILogger<ProviderFactory>>();
        var factory = new ProviderFactory(mockLogger.Object);

        // Unknown vision provider should throw (fail fast).
        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await factory.ExecuteVisionAsync(
                "unknown_vision",
                "System",
                "Describe the image",
                new byte[] { 0x89, 0x50, 0x4E },
                new { },
                CancellationToken.None));

        // openai vision: when OPENAI_API_KEY not set, fail fast with InvalidOperationException (same as LLM).
        WithEnv("OPENAI_API_KEY", null, async () =>
        {
            await AssertThrowsAsync<InvalidOperationException>(async () =>
                await factory.ExecuteVisionAsync(
                    "openai",
                    "System",
                    "Describe",
                    Array.Empty<byte>(),
                    new { },
                    CancellationToken.None));
        }).GetAwaiter().GetResult();
    }

    private async Task TestExecuteLLMAsync_SelfExtendUnityBootstrap()
    {
        var mockLogger = new Mock<ILogger<ProviderFactory>>();
        var factory = new ProviderFactory(mockLogger.Object);

        const string systemPrompt = """
You are a self-extending code agent. You may call tools to read/write files in the repository.
Current world state (JSON): {"RepoRoot":"/workspace","OutputRoot":"/workspace/out"}
Available tools:
- repo.fs.write: Write a file under the repo root
""";

        const string userPrompt = """
Objective:
Generate Unity bootstrap files (IGeneratedGameplaySystem, SystemContext, DashAbilitySystem).
""";

        var result = await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
            await factory.ExecuteLLMAsync("mock-json", systemPrompt, userPrompt, new { }, CancellationToken.None));

        using var doc = JsonDocument.Parse(result);
        AssertTrue(doc.RootElement.TryGetProperty("tool_calls", out var calls), "tool_calls should exist");
        AssertEqual(JsonValueKind.Array, calls.ValueKind);
        AssertTrue(calls.GetArrayLength() >= 3, "Expected at least 3 repo.fs.write calls");

        var first = calls[0];
        AssertEqual("repo.fs.write", first.GetProperty("id").GetString());
        var firstArgs = first.GetProperty("arguments");
        AssertEqual("/workspace", firstArgs.GetProperty("root").GetString());
        AssertEqual("Assets/Scripts/Generated/IGeneratedGameplaySystem.cs", firstArgs.GetProperty("path").GetString());
    }

    private async Task TestExecuteLLMAsync_SelfExtendUnityBootstrapNuanced()
    {
        var mockLogger = new Mock<ILogger<ProviderFactory>>();
        var factory = new ProviderFactory(mockLogger.Object);

        const string systemPrompt = """
You are a self-extending code agent. You may call tools to read/write files in the repository.
Current world state (JSON): {"RepoRoot":"/workspace","OutputRoot":"/workspace/out"}
Available tools:
- repo.fs.write: Write a file under the repo root
""";

        const string userPrompt = """
Objective:
Generate a richer Unity gameplay extension layer for Mono compatibility. Required files: IGeneratedGameplaySystem, SystemContext, DashAbilitySystem with cooldown fields, GeneratedSystemErrorState for in-game compile errors, and GeneratedSystemInspectorSnapshot for raw code inspection.
""";

        var result = await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
            await factory.ExecuteLLMAsync("mock-json", systemPrompt, userPrompt, new { }, CancellationToken.None));

        using var doc = JsonDocument.Parse(result);
        AssertTrue(doc.RootElement.TryGetProperty("tool_calls", out var calls), "tool_calls should exist");
        AssertEqual(JsonValueKind.Array, calls.ValueKind);
        AssertTrue(calls.GetArrayLength() >= 5, "Expected at least 5 repo.fs.write calls for nuanced objective");

        var sawErrorState = false;
        var sawInspectorSnapshot = false;
        var sawCooldownInDash = false;

        foreach (var call in calls.EnumerateArray())
        {
            var args = call.GetProperty("arguments");
            var path = args.GetProperty("path").GetString() ?? string.Empty;
            if (path == "Assets/Scripts/Generated/GeneratedSystemErrorState.cs")
                sawErrorState = true;
            if (path == "Assets/Scripts/Generated/GeneratedSystemInspectorSnapshot.cs")
                sawInspectorSnapshot = true;
            if (path == "Assets/Scripts/Generated/DashAbilitySystem.cs")
            {
                var content = args.GetProperty("content").GetString() ?? string.Empty;
                sawCooldownInDash = content.Contains("DashCooldownSeconds", StringComparison.Ordinal);
            }
        }

        AssertTrue(sawErrorState, "Expected GeneratedSystemErrorState.cs tool call");
        AssertTrue(sawInspectorSnapshot, "Expected GeneratedSystemInspectorSnapshot.cs tool call");
        AssertTrue(sawCooldownInDash, "Expected cooldown field usage in DashAbilitySystem.cs content");
    }

    private async Task TestExecuteLLMAsync_SelfExtendUnityBootstrap_GeneratesComposableCommandScaffolds()
    {
        var mockLogger = new Mock<ILogger<ProviderFactory>>();
        var factory = new ProviderFactory(mockLogger.Object);

        const string systemPrompt = """
You are a self-extending code agent. You may call tools to read/write files in the repository.
Current world state (JSON): {"RepoRoot":"/workspace","OutputRoot":"/workspace/out"}
Available tools:
- repo.fs.write: Write a file under the repo root
""";

        const string userPrompt = """
Objective:
Generate a broad Unity adaptation package for a movement-combat vertical slice. Required: IGeneratedGameplaySystem, SystemContext, DashAbilitySystem, JumpAbilitySystem, SprintAbilitySystem, AbilityRegistry, GeneratedSystemErrorState, GeneratedSystemInspectorSnapshot, and a short adaptation README. Include cooldowns, compile-failure fallback metadata, and inspector raw-code visibility. Keep code in Assets/Scripts/Generated.
""";

        var result = await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
            await factory.ExecuteLLMAsync("mock-json", systemPrompt, userPrompt, new { }, CancellationToken.None));

        using var doc = JsonDocument.Parse(result);
        var calls = doc.RootElement.GetProperty("tool_calls");
        var paths = new List<string>();
        foreach (var call in calls.EnumerateArray())
        {
            paths.Add(call.GetProperty("arguments").GetProperty("path").GetString() ?? string.Empty);
        }

        AssertTrue(paths.Contains("application/src/Ashlar.CLI/Commands/SelfExtendGenerated/IComposableExtensionCommand.cs"),
            "Expected composable command contract scaffold.");
        AssertTrue(paths.Contains("application/src/Ashlar.CLI/Commands/SelfExtendGenerated/SelfExtendBundleCommand.cs"),
            "Expected composed bundle command scaffold.");
        AssertTrue(paths.Contains("application/src/Ashlar.Tests.CLI/Tests/Commands/SelfExtendGenerated/DashExtensionCommandStructureTests.cs"),
            "Expected generated test scaffold for extension command structure.");
    }

    private async Task TestExecuteLLMAsync_SelfExtendPersonalApp_GeneratesComposableCommandScaffolds()
    {
        var mockLogger = new Mock<ILogger<ProviderFactory>>();
        var factory = new ProviderFactory(mockLogger.Object);

        const string systemPrompt = """
You are a self-extending code agent. You may call tools to read/write files in the repository.
Current world state (JSON): {"RepoRoot":"/workspace","OutputRoot":"/workspace/out"}
Available tools:
- repo.fs.write: Write a file under the repo root
""";

        const string userPrompt = """
Objective:
Scaffold a personalized productivity application for an individual user with profile, preferences, tasks, reminders, and progress dashboard. Write artifacts under docs/PersonalAppGenerated and scaffold composable extension commands plus tests so the app can compose with existing backend infrastructure.
""";

        var result = await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
            await factory.ExecuteLLMAsync("mock-json", systemPrompt, userPrompt, new { }, CancellationToken.None));

        using var doc = JsonDocument.Parse(result);
        var calls = doc.RootElement.GetProperty("tool_calls");
        var paths = new List<string>();
        foreach (var call in calls.EnumerateArray())
        {
            paths.Add(call.GetProperty("arguments").GetProperty("path").GetString() ?? string.Empty);
        }

        AssertTrue(paths.Contains("docs/PersonalAppGenerated/UserProfile.cs"),
            "Expected personal app user profile scaffold.");
        AssertTrue(paths.Contains("docs/PersonalAppGenerated/ProgressDashboard.cs"),
            "Expected personal app dashboard scaffold.");
        AssertTrue(paths.Contains("application/src/Ashlar.CLI/Commands/SelfExtendGenerated/ProfileExtensionCommand.cs"),
            "Expected generated profile extension command scaffold.");
        AssertTrue(paths.Contains("application/src/Ashlar.CLI/Commands/SelfExtendGenerated/SelfExtendPersonalBundleCommand.cs"),
            "Expected composed personal bundle command scaffold.");
        AssertTrue(paths.Contains("application/src/Ashlar.Tests.CLI/Tests/Commands/SelfExtendGenerated/ProfileExtensionCommandStructureTests.cs"),
            "Expected generated test scaffold for profile extension command structure.");
        AssertTrue(paths.Contains("application/src/Ashlar.Tests.CLI/Tests/Commands/SelfExtendGenerated/SelfExtendPersonalBundleCommandStructureTests.cs"),
            "Expected generated test scaffold for personal bundle composition.");
    }

    private async Task TestExecuteLLMAsync_SelfExtendUiDemo_GeneratesDomainAndUiScaffolds()
    {
        var mockLogger = new Mock<ILogger<ProviderFactory>>();
        var factory = new ProviderFactory(mockLogger.Object);

        const string systemPrompt = """
You are a self-extending code agent. You may call tools to read/write files in the repository.
Current world state (JSON): {"RepoRoot":"/workspace","OutputRoot":"/workspace/out"}
Available tools:
- repo.fs.write: Write a file under the repo root
""";

        const string userPrompt = """
Objective:
Create an interactive demo app with a chatbot interface that explains Ashlar, retains domain knowledge in a dedicated layer, and can scaffold + dynamically load new features with UI updates.
""";

        var result = await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
            await factory.ExecuteLLMAsync("mock-json", systemPrompt, userPrompt, new { }, CancellationToken.None));

        using var doc = JsonDocument.Parse(result);
        var calls = doc.RootElement.GetProperty("tool_calls");
        var paths = new List<string>();
        var appJsContent = string.Empty;
        var htmlContent = string.Empty;
        var avaloniaContractsContent = string.Empty;
        var avaloniaHostProgramContent = string.Empty;
        foreach (var call in calls.EnumerateArray())
        {
            var args = call.GetProperty("arguments");
            var path = args.GetProperty("path").GetString() ?? string.Empty;
            paths.Add(path);
            if (path == "docs/UiDomainDemoGenerated/app/app.js")
                appJsContent = args.GetProperty("content").GetString() ?? string.Empty;
            if (path == "docs/UiDomainDemoGenerated/app/index.html")
                htmlContent = args.GetProperty("content").GetString() ?? string.Empty;
            if (path == "docs/UiDomainDemoGenerated/avalonia/Ashlar.Ui.Abstractions/UiContracts.cs")
                avaloniaContractsContent = args.GetProperty("content").GetString() ?? string.Empty;
            if (path == "docs/UiDomainDemoGenerated/avalonia/Ashlar.Ui.AvaloniaHost/Program.cs")
                avaloniaHostProgramContent = args.GetProperty("content").GetString() ?? string.Empty;
        }

        AssertTrue(paths.Contains("docs/UiDomainDemoGenerated/app/index.html"),
            "Expected generated UI index scaffold.");
        AssertTrue(paths.Contains("docs/UiDomainDemoGenerated/app/domain-knowledge.json"),
            "Expected generated retained domain knowledge catalog.");
        AssertTrue(paths.Contains("docs/UiDomainDemoGenerated/host/UiDemoHost.csproj"),
            "Expected generated .NET host project scaffold.");
        AssertTrue(paths.Contains("docs/UiDomainDemoGenerated/host/Program.cs"),
            "Expected generated .NET host program scaffold.");
        AssertTrue(paths.Contains("docs/UiDomainDemoGenerated/host/UiDemoSmoke.csproj"),
            "Expected generated .NET smoke project scaffold.");
        AssertTrue(paths.Contains("docs/UiDomainDemoGenerated/host/SmokeProgram.cs"),
            "Expected generated .NET smoke program scaffold.");
        AssertTrue(paths.Contains("docs/UiDomainDemoGenerated/avalonia/Ashlar.Ui.Abstractions/UiContracts.cs"),
            "Expected generated Avalonia UI abstraction contracts.");
        AssertTrue(paths.Contains("docs/UiDomainDemoGenerated/avalonia/Ashlar.Ui.AvaloniaHost/Program.cs"),
            "Expected generated Avalonia host program scaffold.");
        AssertTrue(paths.Contains("application/src/Ashlar.CLI/Commands/SelfExtendGenerated/SelfExtendUiDemoBundleCommand.cs"),
            "Expected generated UI demo bundle command.");
        AssertTrue(paths.Contains("application/src/Ashlar.Tests.CLI/Tests/Commands/SelfExtendGenerated/UiDomainKnowledgeRetentionTests.cs"),
            "Expected generated test for UI/domain knowledge retention.");
        AssertTrue(htmlContent.Contains("Ashlar Chatbot", StringComparison.Ordinal),
            "Expected chatbot interface in generated UI HTML.");
        AssertTrue(appJsContent.Contains("/api/scaffold-feature", StringComparison.Ordinal),
            "Expected real scaffold API call in generated UI JS.");
        AssertTrue(appJsContent.Contains("import(moduleUrl)", StringComparison.Ordinal),
            "Expected dynamic module import in generated UI JS.");
        AssertTrue(appJsContent.Contains("explainAshlar", StringComparison.Ordinal),
            "Expected Ashlar explainer behavior in generated UI JS.");
        AssertTrue(avaloniaContractsContent.Contains("ExperienceMode", StringComparison.Ordinal),
            "Expected Avalonia contracts to include descriptor experience mode.");
        AssertTrue(avaloniaHostProgramContent.Contains("AVALONIA_APP_TRANSFORM", StringComparison.Ordinal),
            "Expected Avalonia host scaffold to support full app transformation objective.");
        AssertTrue(avaloniaHostProgramContent.Contains("Galactic Operations Console", StringComparison.Ordinal),
            "Expected Avalonia host scaffold to include a distinct transformed application shell.");
    }

    private async Task TestExecuteLLMAsync_SelfExtendUiFeatureHotload_GeneratesFeatureModule()
    {
        var mockLogger = new Mock<ILogger<ProviderFactory>>();
        var factory = new ProviderFactory(mockLogger.Object);

        const string systemPrompt = """
You are a self-extending code agent. You may call tools to read/write files in the repository.
Current world state (JSON): {"RepoRoot":"/workspace","OutputRoot":"/workspace/out"}
Available tools:
- repo.fs.write: Write a file under the repo root
""";

        const string userPrompt = """
Objective:
Scaffold a hot-loadable UI feature module for the Ashlar interactive demo.
UI_FEATURE_HOTLOAD
Feature request: Add inventory notification panel
Write output module under docs/UiDomainDemoGenerated/app/generated.
""";

        var result = await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
            await factory.ExecuteLLMAsync("mock-json", systemPrompt, userPrompt, new { }, CancellationToken.None));

        using var doc = JsonDocument.Parse(result);
        var calls = doc.RootElement.GetProperty("tool_calls");
        AssertEqual(1, calls.GetArrayLength(), "Expected one module write call for hotload objective.");
        var path = calls[0].GetProperty("arguments").GetProperty("path").GetString() ?? string.Empty;
        var content = calls[0].GetProperty("arguments").GetProperty("content").GetString() ?? string.Empty;
        AssertTrue(path.StartsWith("docs/UiDomainDemoGenerated/app/generated/", StringComparison.Ordinal),
            "Expected generated feature module path under app/generated.");
        AssertTrue(content.Contains("export function mountFeature", StringComparison.Ordinal),
            "Expected generated feature module to export mountFeature.");
    }

    private async Task TestExecuteLLMAsync_SelfExtendAvaloniaHotload_GeneratesFeatureDescriptor()
    {
        var mockLogger = new Mock<ILogger<ProviderFactory>>();
        var factory = new ProviderFactory(mockLogger.Object);

        const string systemPrompt = """
You are a self-extending code agent. You may call tools to read/write files in the repository.
Current world state (JSON): {"RepoRoot":"/workspace","OutputRoot":"/workspace/out"}
Available tools:
- repo.fs.write: Write a file under the repo root
""";

        const string userPrompt = """
Objective:
AVALONIA_FEATURE_HOTLOAD Feature request: Add slick onboarding rail. Write output descriptor under docs/UiDomainDemoGenerated/avalonia/Ashlar.Ui.AvaloniaHost/GeneratedExtensions.
""";

        var result = await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
            await factory.ExecuteLLMAsync("mock-json", systemPrompt, userPrompt, new { }, CancellationToken.None));

        using var doc = JsonDocument.Parse(result);
        var calls = doc.RootElement.GetProperty("tool_calls");
        AssertEqual(1, calls.GetArrayLength(), "Expected one descriptor write call for Avalonia hotload objective.");
        var path = calls[0].GetProperty("arguments").GetProperty("path").GetString() ?? string.Empty;
        var content = calls[0].GetProperty("arguments").GetProperty("content").GetString() ?? string.Empty;
        AssertTrue(path.StartsWith("docs/UiDomainDemoGenerated/avalonia/Ashlar.Ui.AvaloniaHost/GeneratedExtensions/", StringComparison.Ordinal),
            "Expected generated Avalonia descriptor path under GeneratedExtensions.");
        AssertTrue(content.Contains("\"Root\"", StringComparison.Ordinal),
            "Expected generated Avalonia descriptor to include root node.");
        AssertTrue(content.Contains("\"Kind\": \"panel\"", StringComparison.Ordinal),
            "Expected descriptor root node kind panel.");
        AssertTrue(content.Contains("\"ExperienceMode\": \"augment\"", StringComparison.Ordinal),
            "Expected hotload descriptor to default to augment mode.");
    }

    private async Task TestExecuteLLMAsync_SelfExtendAvaloniaTransform_GeneratesReplacementDescriptor()
    {
        var mockLogger = new Mock<ILogger<ProviderFactory>>();
        var factory = new ProviderFactory(mockLogger.Object);

        const string systemPrompt = """
You are a self-extending code agent. You may call tools to read/write files in the repository.
Current world state (JSON): {"RepoRoot":"/workspace","OutputRoot":"/workspace/out"}
Available tools:
- repo.fs.write: Write a file under the repo root
""";

        const string userPrompt = """
Objective:
AVALONIA_APP_TRANSFORM Feature request: Completely transform into a mission operations application. Write output descriptor under docs/UiDomainDemoGenerated/avalonia/Ashlar.Ui.AvaloniaHost/GeneratedExtensions.
""";

        var result = await WithEnv("ASHLAR_ALLOW_MOCK", "1", async () =>
            await factory.ExecuteLLMAsync("mock-json", systemPrompt, userPrompt, new { }, CancellationToken.None));

        using var doc = JsonDocument.Parse(result);
        var calls = doc.RootElement.GetProperty("tool_calls");
        AssertEqual(1, calls.GetArrayLength(), "Expected one descriptor write call for Avalonia app transform objective.");
        var path = calls[0].GetProperty("arguments").GetProperty("path").GetString() ?? string.Empty;
        var content = calls[0].GetProperty("arguments").GetProperty("content").GetString() ?? string.Empty;
        AssertTrue(path.StartsWith("docs/UiDomainDemoGenerated/avalonia/Ashlar.Ui.AvaloniaHost/GeneratedExtensions/", StringComparison.Ordinal),
            "Expected generated Avalonia transform descriptor path under GeneratedExtensions.");
        AssertTrue(content.Contains("\"ExperienceMode\": \"transform\"", StringComparison.Ordinal),
            "Expected descriptor to request full shell transformation.");
        AssertTrue(content.Contains("\"Command\": \"shell:restore\"", StringComparison.Ordinal),
            "Expected transformed descriptor to include explicit restore command.");
        AssertTrue(content.Contains("\"Layout\": \"horizontal\"", StringComparison.Ordinal),
            "Expected transformed descriptor to include adaptive horizontal layout containers.");
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new AssertionException($"Expected {typeof(T).Name} to be thrown");
    }

    private static bool IsJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return false;
        try
        {
            using var _ = JsonDocument.Parse(trimmed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WithEnv(string key, string? value, Action action)
    {
        var old = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, old);
        }
    }

    private static async Task WithEnv(string key, string? value, Func<Task> action)
    {
        var old = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, value);
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, old);
        }
    }

    private static async Task<T> WithEnv<T>(string key, string? value, Func<Task<T>> action)
    {
        var old = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, value);
            return await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, old);
        }
    }
}

