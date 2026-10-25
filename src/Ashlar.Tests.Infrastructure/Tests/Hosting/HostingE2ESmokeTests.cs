using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Trust;
using Ashlar.Abstractions.Routing;
using Ashlar.Authoring;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Validation.Ports;
using Ashlar.Hosting;
using Ashlar.Runtime.Routing;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Hosting;

/// <summary>
/// E2E smoke tests for the AddAshlar hosting API.
/// Validates that the full kernel (orchestration, adaptation, persistence, validation) can be resolved and used.
/// Two tests set process-wide env vars (ASHLAR_DEPLOYMENT_PROFILE, ASHLAR_USE_MEAI_PIPELINE) that
/// AddAshlar reads, so the class runs in the serialized "EnvironmentVariables" collection.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "ProdStyle")]
[Collection("EnvironmentVariables")]
public sealed class HostingE2ESmokeTests
{
    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AddAshlar_ShouldBuildServiceProvider()
    {
        await Task.CompletedTask;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlar();
        var sp = services.BuildServiceProvider();

        sp.Should().NotBeNull();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AddAshlar_ShouldResolveAndRunValidation()
    {
        var host = Host.CreateDefaultBuilder(Array.Empty<string>())
            .ConfigureServices(services =>
            {
                services.AddAshlar();
            })
            .Build();

        var validationService = host.Services.GetRequiredService<IValidationService>();
        validationService.Should().NotBeNull();

        var result = await validationService.ValidateAsync(filter: null, progress: null, CancellationToken.None);

        result.Should().NotBeNull();
        Assert.True(result.TestsRun >= 0);
        Assert.True(result.TestsPassed >= 0);
        Assert.True(result.TestsFailed >= 0);
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AddAshlar_ShouldResolveAnalysisService()
    {
        await Task.CompletedTask;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlar();
        var sp = services.BuildServiceProvider();

        var analysisService = sp.GetRequiredService<Ashlar.Core.Application.Analysis.Ports.IAnalysisService>();
        analysisService.Should().NotBeNull();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AddAshlar_RegistersObservationPipeline_ByDefault()
    {
        await Task.CompletedTask;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlar();
        var sp = services.BuildServiceProvider();

        var patternStore = sp.GetRequiredService<Ashlar.Core.Application.Observation.Ports.IPatternStore>();
        patternStore.Should().NotBeNull();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AddAshlar_WithDisableObservationPipeline_DoesNotRegister()
    {
        await Task.CompletedTask;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlar(o => o.DisableObservationPipeline = true);
        var sp = services.BuildServiceProvider();

        var patternStore = sp.GetService<Ashlar.Core.Application.Observation.Ports.IPatternStore>();
        patternStore.Should().BeNull("observation pipeline should not register IPatternStore when disabled");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AddAshlar_WithDisableObservationPipeline_StartsBackgroundAgentHost()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddAshlar(o =>
                {
                    o.DisableObservationPipeline = true;
                    o.RegisterBackgroundAgentHostedService = true;
                });
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AddAshlarProfile_Edge_PeelsOffOptionalServices()
    {
        await Task.CompletedTask;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlarProfile(AshlarDeploymentProfile.Edge);
        var sp = services.BuildServiceProvider();

        sp.GetService<IPatternStore>().Should().BeNull();
        sp.GetService<IBackgroundAgentRegistry>().Should().BeNull();
        sp.GetRequiredService<IEndpointRegistry>().Should().NotBeOfType<InMemoryEndpointRegistry>();
        sp.GetService<ICloudSanitizationProxy>().Should().BeNull();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AddAshlar_UsesEnvironmentDeploymentProfile_WhenOptionsDoNotOverride()
    {
        await Task.CompletedTask;
        const string profileKey = "ASHLAR_DEPLOYMENT_PROFILE";
        var previous = Environment.GetEnvironmentVariable(profileKey);
        Environment.SetEnvironmentVariable(profileKey, "edge");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAshlar();
            var sp = services.BuildServiceProvider();

            sp.GetService<IPatternStore>().Should().BeNull();
            sp.GetService<IBackgroundAgentRegistry>().Should().BeNull();
            sp.GetRequiredService<IEndpointRegistry>().Should().NotBeOfType<InMemoryEndpointRegistry>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(profileKey, previous);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AddAshlar_Defaults_To_Meai_Pipeline_And_VectorData_Rag()
    {
        await Task.CompletedTask;
        var previous = Environment.GetEnvironmentVariable("ASHLAR_USE_MEAI_PIPELINE");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_USE_MEAI_PIPELINE", null);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAshlar();
            using var sp = services.BuildServiceProvider();

            sp.GetService<Microsoft.Extensions.AI.IChatClient>().Should().NotBeNull(
                "Phase 6 defaults the MEAI governed chat pipeline on");
            sp.GetRequiredService<Ashlar.BackgroundAgents.RAG.IRAGService>()
                .Should().BeOfType<Ashlar.Hosting.Meai.MeaiVectorDataRagAdapter>();
            sp.GetRequiredService<Ashlar.AI.Pipeline.Rag.VectorDataRagService>().Should().NotBeNull();
            sp.GetRequiredService<Ashlar.Infrastructure.Execution.Models.HotSwappableModel>().Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_USE_MEAI_PIPELINE", previous);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AddAshlar_Meai_OptOut_Skips_ChatClient_But_Keeps_VectorData_Rag()
    {
        await Task.CompletedTask;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlar(o => o.UseMeaiPipeline = false);
        using var sp = services.BuildServiceProvider();

        sp.GetService<Microsoft.Extensions.AI.IChatClient>().Should().BeNull();
        sp.GetRequiredService<Ashlar.BackgroundAgents.RAG.IRAGService>()
            .Should().BeOfType<Ashlar.Hosting.Meai.MeaiVectorDataRagAdapter>();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AddAshlarBrick_then_AddAshlar_puts_the_brick_in_the_registry()
    {
        await Task.CompletedTask;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlarBrick<Adaptation.AdaptationBrickRegistrationTests.RegisteredByTypeBrick>();
        services.AddAshlar();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<Ashlar.Core.Domain.Execution.IBrickRegistry>()
            .GetBrick(Adaptation.AdaptationBrickRegistrationTests.RegisteredByTypeBrick.BrickId)
            .Should().NotBeNull("Ashlar.Authoring registers the brick without referencing the host");
    }
}
