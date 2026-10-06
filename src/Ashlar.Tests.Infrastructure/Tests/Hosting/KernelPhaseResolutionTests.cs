using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Routing;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Trust;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Common.Services;
using Ashlar.Core.Application.Configuration.Ports;
using Ashlar.Core.Application.Knowledge.Ports;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;
using Ashlar.Core.Application.Validation.Ports;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Execution;
using Ashlar.Runtime.Routing;
using Ashlar.Tests.Infrastructure.Helpers;
using Ashlar.Transport.Grpc;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Hosting;

/// <summary>
/// Maps <see cref="AshlarKernelRegistrar"/> phases to resolvable services per
/// <see cref="AshlarDeploymentProfile"/>. See docs/architecture/KernelPhaseMatrix.md.
/// </summary>
[Trait("Category", "ProdStyle")]
[Trait("Category", "E2E")]
[Collection("EnvironmentVariables")]
public sealed class KernelPhaseResolutionTests : IDisposable
{
    // SPEC-007 PR 4.6: composing AirGapped or SecureWorkstation notes a profile no later AddAshlar lowers, so each
    // test restores the process egress state (the noted profile and the mode latch) through the reset seam.
    private readonly EgressProcessStateScope _egressState = new();

    public void Dispose() => _egressState.Dispose();

    public static TheoryData<AshlarDeploymentProfile, KernelProfileExpectations> ProfileMatrix =>
        new()
        {
            { AshlarDeploymentProfile.Full, KernelProfileExpectations.Full },
            { AshlarDeploymentProfile.Server, KernelProfileExpectations.Full },
            { AshlarDeploymentProfile.Edge, KernelProfileExpectations.Edge },
            { AshlarDeploymentProfile.AirGapped, KernelProfileExpectations.AirGapped },
            { AshlarDeploymentProfile.System, KernelProfileExpectations.System },
            { AshlarDeploymentProfile.SecureWorkstation, KernelProfileExpectations.SecureWorkstation },
        };

    [Theory(Timeout = TestTimeouts.E2E)]
    [MemberData(nameof(ProfileMatrix))]
    public async Task Profile_ResolvesExpectedKernelServices(
        AshlarDeploymentProfile profile,
        KernelProfileExpectations expected)
    {
        await Task.CompletedTask;
        var sp = BuildProvider(profile);

        AssertPresence(sp, expected.LoopKernel, sp => sp.GetService<ILoopKernel>());
        AssertPresence(sp, expected.ConfigurationService, sp => sp.GetService<IConfigurationService>());
        AssertPresence(sp, expected.Model, sp => sp.GetService<IModel>());
        AssertPresence(sp, expected.ProviderFactory, sp => sp.GetService<IProviderFactory>());
        AssertPresence(sp, expected.PipelineValidator, sp => sp.GetService<IPipelineTemplateValidator>());
        AssertPresence(sp, expected.PatternStore, sp => sp.GetService<IPatternStore>());
        AssertPresence(sp, expected.BackgroundAgents, sp => sp.GetService<IBackgroundAgentRegistry>());
        AssertPresence(sp, expected.GrpcChannelFactory, sp => sp.GetService<IGrpcChannelFactory>());
        AssertPresence(sp, expected.CloudSanitization, sp => sp.GetService<ICloudSanitizationProxy>());

        if (expected.EndpointRegistryNotInMemory)
        {
            sp.GetRequiredService<IEndpointRegistry>().Should().NotBeOfType<InMemoryEndpointRegistry>();
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task FullProfile_LoopKernel_IsSequentialByDefault()
    {
        await Task.CompletedTask;
        var sp = BuildProvider(AshlarDeploymentProfile.Full);
        var loop = sp.GetRequiredService<ILoopKernel>();
        loop.Should().BeOfType<SequentialLoopKernel>();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task EdgeProfile_ValidatesMinimalPipelineTemplate()
    {
        await Task.CompletedTask;
        var sp = BuildProvider(AshlarDeploymentProfile.Edge);
        var validator = sp.GetRequiredService<IPipelineTemplateValidator>();
        var result = validator.Validate(new PipelineTemplate
        {
            TemplateId = "kernel-gate-edge",
            Version = "1.0",
            Stages = new[]
            {
                new PipelineStageDefinition { Id = "a", Name = "A", Mode = PipelineExecutionMode.Deterministic },
            },
        });

        result.IsValid.Should().BeTrue(
            result.Errors is { Count: > 0 } errors ? string.Join("; ", errors) : "validation failed");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task FullProfile_ResolvesKnowledgeQuery_whenObservationEnabled()
    {
        await Task.CompletedTask;
        var sp = BuildProvider(AshlarDeploymentProfile.Full);
        sp.GetRequiredService<IKnowledgeQueryService>().Should().NotBeNull();
    }

    private static ServiceProvider BuildProvider(AshlarDeploymentProfile profile)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlarProfile(profile);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static void AssertPresence<T>(ServiceProvider sp, bool expected, Func<ServiceProvider, T?> resolve)
        where T : class
    {
        if (!expected)
        {
            resolve(sp).Should().BeNull(typeof(T).Name);
            return;
        }

        resolve(sp).Should().NotBeNull(typeof(T).Name);
    }

    public sealed record KernelProfileExpectations(
        bool LoopKernel,
        bool ConfigurationService,
        bool Model,
        bool ProviderFactory,
        bool PipelineValidator,
        bool PatternStore,
        bool BackgroundAgents,
        bool GrpcChannelFactory,
        bool CloudSanitization,
        bool EndpointRegistryNotInMemory)
    {
        public static KernelProfileExpectations Full { get; } = new(
            LoopKernel: true,
            ConfigurationService: true,
            Model: true,
            ProviderFactory: true,
            PipelineValidator: true,
            PatternStore: true,
            BackgroundAgents: true,
            GrpcChannelFactory: true,
            CloudSanitization: true,
            EndpointRegistryNotInMemory: false);

        public static KernelProfileExpectations Edge { get; } = new(
            LoopKernel: true,
            ConfigurationService: true,
            Model: true,
            ProviderFactory: true,
            PipelineValidator: true,
            PatternStore: false,
            BackgroundAgents: false,
            GrpcChannelFactory: false,
            CloudSanitization: false,
            EndpointRegistryNotInMemory: true);

        public static KernelProfileExpectations AirGapped { get; } = new(
            LoopKernel: true,
            ConfigurationService: true,
            Model: true,
            ProviderFactory: true,
            PipelineValidator: true,
            PatternStore: false,
            BackgroundAgents: false,
            GrpcChannelFactory: false,
            CloudSanitization: false,
            EndpointRegistryNotInMemory: true);

        public static KernelProfileExpectations System { get; } = new(
            LoopKernel: true,
            ConfigurationService: true,
            Model: true,
            ProviderFactory: true,
            PipelineValidator: false,
            PatternStore: false,
            BackgroundAgents: false,
            GrpcChannelFactory: false,
            CloudSanitization: false,
            EndpointRegistryNotInMemory: true);

        public static KernelProfileExpectations SecureWorkstation { get; } = new(
            LoopKernel: true,
            ConfigurationService: true,
            Model: true,
            ProviderFactory: true,
            PipelineValidator: true,
            PatternStore: true,
            BackgroundAgents: true,
            GrpcChannelFactory: false,
            CloudSanitization: true,
            EndpointRegistryNotInMemory: true);
    }
}
