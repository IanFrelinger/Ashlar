using Ashlar.Agents.TestKit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Application.NodeCapabilityRuntime.Ports;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.Routing;
using Ashlar.Core.Application.Mesh.Models;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>Tests for capability routing brick.</summary>
public sealed class CapabilityRoutingBrickTests
{
    [Fact]
    public async Task HappyPath_LocalExecution()
    {
        var snapshot = new SnapshotStub
        {
            AvailableVramBytes = 32L * 1024 * 1024 * 1024,
            ComputeClass = GpuComputeClass.High,
            CurrentQueueDepth = 0
        };
        var localExecutor = new StubLocalExecutor(_ => Result<GenerationExecutionResult>.Success(new GenerationExecutionResult
        {
            Payload = [1, 2, 3],
            Provider = "local",
            ModelId = "local-model",
            IsRemote = false,
            Summary = "local ok"
        }));
        var config = Options.Create(new RunPodBrickConfig
        {
            Timeout = TimeSpan.FromSeconds(2),
            PollingInterval = TimeSpan.FromMilliseconds(20),
            QueueDepthThreshold = 4,
            OutputStagingPath = Path.Combine(Path.GetTempPath(), "ashlar-runpod-tests-local")
        });
        var runPodClient = new StubRunPodClient();
        var runPodBrick = new RunPodBrick(runPodClient, config, NullLogger<RunPodBrick>.Instance);
        var peerExecutor = new StubPeerExecutor();
        var peerSnapshot = new StubPeerSnapshot();
        var router = new NcrCapabilityRouter(
            snapshot,
            peerSnapshot,
            peerExecutor,
            localExecutor,
            runPodBrick,
            config,
            NullLogger<NcrCapabilityRouter>.Instance);
        var brick = new CapabilityRoutingBrick(router, NullLogger<CapabilityRoutingBrick>.Instance);

        var result = await brick.ExecuteAsync(
            new RunPodJobPayload
            {
                ModelId = "local-model",
                Prompt = "hello"
            },
            new JobRequirements
            {
                ModelId = "local-model",
                MinimumVramBytes = 2L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.Medium,
                EstimatedDuration = TimeSpan.FromSeconds(10)
            },
            TestExecutionContext());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.IsRemote.Should().BeFalse();
        result.Value.Provider.Should().Be("local");
    }

    [Fact]
    public async Task HappyPath_RemoteExecution()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"ashlar-runpod-tests-remote-{Guid.NewGuid():N}");
        var snapshot = new SnapshotStub
        {
            AvailableVramBytes = 256L * 1024 * 1024,
            ComputeClass = GpuComputeClass.Low,
            CurrentQueueDepth = 0
        };
        var config = Options.Create(new RunPodBrickConfig
        {
            Timeout = TimeSpan.FromSeconds(2),
            PollingInterval = TimeSpan.FromMilliseconds(20),
            QueueDepthThreshold = 4,
            OutputStagingPath = outputDir
        });
        var runPodClient = new StubRunPodClient
        {
            SpinUpResult = Result<RunPodInstance>.Success(new RunPodInstance
            {
                InstanceId = "inst-1",
                ModelId = "remote-model",
                GpuType = "NVIDIA_A4000"
            }),
            DispatchResult = Result<JobHandle>.Success(new JobHandle
            {
                InstanceId = "inst-1",
                JobId = "job-1"
            }),
            StatusSequence = new Queue<JobStatus>(
            [
                new JobStatus { State = RunPodJobState.Running },
                new JobStatus { State = RunPodJobState.Completed }
            ]),
            PullResult = Result<byte[]>.Success([9, 8, 7]),
            TerminateResult = Result<Unit>.Success(Unit.Value)
        };

        var localExecutor = new StubLocalExecutor(_ =>
            Result<GenerationExecutionResult>.Failure("local.should_not_run", "Local executor should not run in this test."));
        // The deadline in RunPodBrick is measured against the clock this provides. With
        // TimeProvider.System it is measured against real wall clock, which made this fact a race
        // with the machine: the stub status queue below completes in about 40ms and the config allows
        // 2s, and on a contended macOS runner it consumed the full 2s and reported runpod.timeout -
        // 5698/5699 passing, and the required readiness gate red. The step clock advances 1ms per
        // reading, so the loop's budget is spent by the loop rather than by the runner's load.
        var pollClock = new SteppingUtcClock(TimeSpan.FromMilliseconds(1));
        var runPodBrick = new RunPodBrick(runPodClient, config, NullLogger<RunPodBrick>.Instance, pollClock);
        var peerExecutor = new StubPeerExecutor();
        var peerSnapshot = new StubPeerSnapshot();
        var router = new NcrCapabilityRouter(
            snapshot,
            peerSnapshot,
            peerExecutor,
            localExecutor,
            runPodBrick,
            config,
            NullLogger<NcrCapabilityRouter>.Instance);
        var brick = new CapabilityRoutingBrick(router, NullLogger<CapabilityRoutingBrick>.Instance);

        var result = await brick.ExecuteAsync(
            new RunPodJobPayload
            {
                ModelId = "remote-model",
                Prompt = "hello remote"
            },
            new JobRequirements
            {
                ModelId = "remote-model",
                MinimumVramBytes = 4L * 1024 * 1024 * 1024,
                ComputeClass = GpuComputeClass.High,
                EstimatedDuration = TimeSpan.FromMinutes(1)
            },
            TestExecutionContext());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.IsRemote.Should().BeTrue();
        result.Value.Provider.Should().Be("runpod");
        File.Exists(result.Value.OutputPath).Should().BeTrue();
        runPodClient.TerminateCalls.Should().Be(1);
    }

    [Fact]
    public void NCR_RoutesToRemote_DueToInsufficientVram()
    {
        var router = BuildRouter(
            availableVram: 128L * 1024 * 1024,
            computeClass: GpuComputeClass.High,
            queueDepth: 0,
            queueThreshold: 4);

        var target = router.ResolveExecutionTarget(new JobRequirements
        {
            ModelId = "m",
            MinimumVramBytes = 2L * 1024 * 1024 * 1024,
            ComputeClass = GpuComputeClass.Low
        });

        target.Should().BeOfType<ExecutionTarget.Remote>();
    }

    [Fact]
    public void NCR_RoutesToRemote_DueToQueueDepth()
    {
        var router = BuildRouter(
            availableVram: 32L * 1024 * 1024 * 1024,
            computeClass: GpuComputeClass.High,
            queueDepth: 12,
            queueThreshold: 4);

        var target = router.ResolveExecutionTarget(new JobRequirements
        {
            ModelId = "m",
            MinimumVramBytes = 256L * 1024 * 1024,
            ComputeClass = GpuComputeClass.Low
        });

        target.Should().BeOfType<ExecutionTarget.Remote>();
    }

    [Fact]
    public void NCR_RoutesToRemote_DueToInsufficientComputeClass()
    {
        var router = BuildRouter(
            availableVram: 32L * 1024 * 1024 * 1024,
            computeClass: GpuComputeClass.Low,
            queueDepth: 0,
            queueThreshold: 4);

        var target = router.ResolveExecutionTarget(new JobRequirements
        {
            ModelId = "m",
            MinimumVramBytes = 512L * 1024 * 1024,
            ComputeClass = GpuComputeClass.High
        });

        target.Should().BeOfType<ExecutionTarget.Remote>();
    }

    [Fact]
    public void OvernightFlag_ForcesRemote()
    {
        var router = BuildRouter(
            availableVram: 32L * 1024 * 1024 * 1024,
            computeClass: GpuComputeClass.Extreme,
            queueDepth: 0,
            queueThreshold: 99);

        var target = router.ResolveExecutionTarget(new JobRequirements
        {
            ModelId = "m",
            MinimumVramBytes = 1,
            ComputeClass = GpuComputeClass.Low,
            IsOvernightOrBackground = true
        });

        target.Should().BeOfType<ExecutionTarget.Remote>();
    }

    [Fact]
    public async Task RunPodTimeout_WithTeardown()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"ashlar-runpod-timeout-{Guid.NewGuid():N}");
        var config = Options.Create(new RunPodBrickConfig
        {
            Timeout = TimeSpan.FromMilliseconds(120),
            PollingInterval = TimeSpan.FromMilliseconds(15),
            OutputStagingPath = outputDir
        });
        var client = new StubRunPodClient
        {
            SpinUpResult = Result<RunPodInstance>.Success(new RunPodInstance
            {
                InstanceId = "inst-timeout",
                ModelId = "m",
                GpuType = "gpu"
            }),
            DispatchResult = Result<JobHandle>.Success(new JobHandle
            {
                InstanceId = "inst-timeout",
                JobId = "job-timeout"
            }),
            StatusSequence = new Queue<JobStatus>(
            [
                new JobStatus { State = RunPodJobState.Running },
                new JobStatus { State = RunPodJobState.Running },
                new JobStatus { State = RunPodJobState.Running },
                new JobStatus { State = RunPodJobState.Running },
                new JobStatus { State = RunPodJobState.Running }
            ]),
            TerminateResult = Result<Unit>.Success(Unit.Value)
        };
        var brick = new RunPodBrick(client, config, NullLogger<RunPodBrick>.Instance);

        var result = await brick.ExecuteAsync(
            new RunPodJobPayload { ModelId = "m", Prompt = "timeout please" },
            new JobRequirements { ModelId = "m" },
            TestExecutionContext());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("runpod.timeout");
        client.TerminateCalls.Should().Be(1);
    }

    [Fact]
    public async Task RunPodFailure_WithTeardown()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"ashlar-runpod-failure-{Guid.NewGuid():N}");
        var config = Options.Create(new RunPodBrickConfig
        {
            Timeout = TimeSpan.FromSeconds(2),
            PollingInterval = TimeSpan.FromMilliseconds(20),
            OutputStagingPath = outputDir
        });
        var client = new StubRunPodClient
        {
            SpinUpResult = Result<RunPodInstance>.Success(new RunPodInstance
            {
                InstanceId = "inst-failure",
                ModelId = "m",
                GpuType = "gpu"
            }),
            DispatchResult = Result<JobHandle>.Failure("dispatch.failed", "Dispatch failed"),
            TerminateResult = Result<Unit>.Success(Unit.Value)
        };
        var brick = new RunPodBrick(client, config, NullLogger<RunPodBrick>.Instance);

        var result = await brick.ExecuteAsync(
            new RunPodJobPayload { ModelId = "m", Prompt = "fail dispatch" },
            new JobRequirements { ModelId = "m" },
            TestExecutionContext());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("dispatch.failed");
        client.TerminateCalls.Should().Be(1);
    }

    [Fact]
    public void Config_IsLoaded_FromExistingOptionsPattern()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ashlar:RunPod:ApiKey"] = "test-key",
                ["Ashlar:RunPod:PreferredGpuTier"] = "NVIDIA_H100",
                ["Ashlar:RunPod:Timeout"] = "00:00:07",
                ["Ashlar:RunPod:PollingInterval"] = "00:00:01",
                ["Ashlar:RunPod:OutputStagingPath"] = "/tmp/ashlar-test-staging",
                ["Ashlar:RunPod:QueueDepthThreshold"] = "9"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProviderFactory>(new FakeProviderFactory("ok") { Available = true, OllamaReachable = true });
        services.AddRunPodCapabilityRouting(configuration);
        using var provider = services.BuildServiceProvider();

        var config = provider.GetRequiredService<IOptions<RunPodBrickConfig>>().Value;
        var adaptation = provider.GetRequiredService<IOptions<AdaptationBrickOptions>>().Value;

        config.ApiKey.Should().Be("test-key");
        config.PreferredGpuTier.Should().Be("NVIDIA_H100");
        config.Timeout.Should().Be(TimeSpan.FromSeconds(7));
        config.PollingInterval.Should().Be(TimeSpan.FromSeconds(1));
        config.OutputStagingPath.Should().Be("/tmp/ashlar-test-staging");
        config.QueueDepthThreshold.Should().Be(9);
        adaptation.AdditionalBrickTypes.Should().Contain(typeof(RunPodBrick));
        adaptation.AdditionalBrickTypes.Should().Contain(typeof(CapabilityRoutingBrick));
    }

    [Fact]
    public void PeerPreferred_RoutesToPeer_WhenEligiblePeerExists()
    {
        var localExecutor = new StubLocalExecutor(_ => Result<GenerationExecutionResult>.Success(new GenerationExecutionResult
        {
            Payload = [1],
            Provider = "local",
            ModelId = "m",
            IsRemote = false
        }));
        var runPodConfig = Options.Create(new RunPodBrickConfig
        {
            Timeout = TimeSpan.FromSeconds(2),
            PollingInterval = TimeSpan.FromMilliseconds(20),
            QueueDepthThreshold = 4,
            EnablePeerNetworkRouting = true,
            PreferPeerNetworkOverCloud = true,
            OutputStagingPath = Path.Combine(Path.GetTempPath(), $"ashlar-peer-pref-{Guid.NewGuid():N}")
        });
        var runPod = new RunPodBrick(new StubRunPodClient(), runPodConfig, NullLogger<RunPodBrick>.Instance);
        var peerExecutor = new StubPeerExecutor();
        var peerSnapshot = new StubPeerSnapshot
        {
            Candidates =
            [
                new PeerExecutionCandidate
                {
                    PeerId = "peer-1",
                    Endpoint = "http://peer-1",
                    AvailableVramBytes = 24L * 1024 * 1024 * 1024,
                    ComputeClass = GpuComputeClass.High,
                    QueueDepth = 0,
                    CapturedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        var router = new NcrCapabilityRouter(
            new SnapshotStub
            {
                AvailableVramBytes = 128L * 1024 * 1024,
                ComputeClass = GpuComputeClass.Low,
                CurrentQueueDepth = 0
            },
            peerSnapshot,
            peerExecutor,
            localExecutor,
            runPod,
            runPodConfig,
            NullLogger<NcrCapabilityRouter>.Instance);

        var target = router.ResolveExecutionTarget(new JobRequirements
        {
            ModelId = "m",
            MinimumVramBytes = 4L * 1024 * 1024 * 1024,
            ComputeClass = GpuComputeClass.Medium,
            RemoteExecutionPreference = RemoteExecutionPreference.PreferPeerNetwork
        });

        target.Should().BeOfType<ExecutionTarget.Remote>();
        var remote = (ExecutionTarget.Remote)target;
        remote.Executor.Should().BeSameAs(peerExecutor);
        remote.Reason.ToLowerInvariant().Should().Contain("peer");
    }

    [Fact]
    public void PeerOnly_RoutesToPeer_WhenEligiblePeerExists()
    {
        var runPodConfig = Options.Create(new RunPodBrickConfig
        {
            Timeout = TimeSpan.FromSeconds(2),
            PollingInterval = TimeSpan.FromMilliseconds(20),
            QueueDepthThreshold = 4,
            EnablePeerNetworkRouting = true,
            OutputStagingPath = Path.Combine(Path.GetTempPath(), $"ashlar-peer-only-{Guid.NewGuid():N}")
        });

        var localExecutor = new StubLocalExecutor(_ => Result<GenerationExecutionResult>.Success(new GenerationExecutionResult
        {
            Payload = [1],
            Provider = "local",
            ModelId = "m",
            IsRemote = false
        }));
        var runPod = new RunPodBrick(new StubRunPodClient(), runPodConfig, NullLogger<RunPodBrick>.Instance);
        var peerExecutor = new StubPeerExecutor();
        var peerSnapshot = new StubPeerSnapshot
        {
            Candidates =
            [
                new PeerExecutionCandidate
                {
                    PeerId = "peer-1",
                    Endpoint = "http://peer-1",
                    AvailableVramBytes = 16L * 1024 * 1024 * 1024,
                    ComputeClass = GpuComputeClass.High,
                    QueueDepth = 0,
                    CapturedAt = DateTimeOffset.UtcNow
                }
            ]
        };

        var router = new NcrCapabilityRouter(
            new SnapshotStub
            {
                AvailableVramBytes = 128L * 1024 * 1024,
                ComputeClass = GpuComputeClass.Low,
                CurrentQueueDepth = 0
            },
            peerSnapshot,
            peerExecutor,
            localExecutor,
            runPod,
            runPodConfig,
            NullLogger<NcrCapabilityRouter>.Instance);

        var target = router.ResolveExecutionTarget(new JobRequirements
        {
            ModelId = "m",
            MinimumVramBytes = 2L * 1024 * 1024 * 1024,
            ComputeClass = GpuComputeClass.Medium,
            RemoteExecutionPreference = RemoteExecutionPreference.PeerNetworkOnly
        });

        target.Should().BeOfType<ExecutionTarget.Remote>();
        var remote = (ExecutionTarget.Remote)target;
        remote.Executor.Should().BeSameAs(peerExecutor);
        remote.Reason.ToLowerInvariant().Should().Contain("peer");
    }

    [Fact]
    public void PeerPreferred_FallsBackToCloud_WhenOnlyUntrustedPeersUnderTrustedOnlyPolicy()
    {
        var runPodConfig = Options.Create(new RunPodBrickConfig
        {
            Timeout = TimeSpan.FromSeconds(2),
            PollingInterval = TimeSpan.FromMilliseconds(20),
            QueueDepthThreshold = 4,
            EnablePeerNetworkRouting = true,
            PreferPeerNetworkOverCloud = true,
            PeerTrustPolicy = "trusted-only",
            OutputStagingPath = Path.Combine(Path.GetTempPath(), $"ashlar-peer-trust-only-{Guid.NewGuid():N}")
        });

        var localExecutor = new StubLocalExecutor(_ => Result<GenerationExecutionResult>.Success(new GenerationExecutionResult
        {
            Payload = [1],
            Provider = "local",
            ModelId = "m",
            IsRemote = false
        }));
        var runPod = new RunPodBrick(new StubRunPodClient(), runPodConfig, NullLogger<RunPodBrick>.Instance);
        var peerExecutor = new StubPeerExecutor();
        var peerSnapshot = new StubPeerSnapshot
        {
            Candidates =
            [
                new PeerExecutionCandidate
                {
                    PeerId = "peer-untrusted",
                    Endpoint = "http://peer-untrusted",
                    AvailableVramBytes = 16L * 1024 * 1024 * 1024,
                    ComputeClass = GpuComputeClass.High,
                    QueueDepth = 0,
                    TrustTier = PeerTrustTier.Untrusted,
                    CapturedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        var router = new NcrCapabilityRouter(
            new SnapshotStub
            {
                AvailableVramBytes = 128L * 1024 * 1024,
                ComputeClass = GpuComputeClass.Low,
                CurrentQueueDepth = 0
            },
            peerSnapshot,
            peerExecutor,
            localExecutor,
            runPod,
            runPodConfig,
            NullLogger<NcrCapabilityRouter>.Instance);

        var target = router.ResolveExecutionTarget(new JobRequirements
        {
            ModelId = "m",
            MinimumVramBytes = 4L * 1024 * 1024 * 1024,
            ComputeClass = GpuComputeClass.Medium,
            RemoteExecutionPreference = RemoteExecutionPreference.PreferPeerNetwork
        });

        target.Should().BeOfType<ExecutionTarget.Remote>();
        var remote = (ExecutionTarget.Remote)target;
        remote.Executor.Should().BeSameAs(runPod);
        remote.Reason.ToLowerInvariant().Should().Contain("insufficient");
    }

[Fact]
    public async Task PeerOnly_ReturnsErrorExecutor_WhenNoEligiblePeers()
    {
        var runPodConfig = Options.Create(new RunPodBrickConfig
        {
            Timeout = TimeSpan.FromSeconds(2),
            PollingInterval = TimeSpan.FromMilliseconds(20),
            QueueDepthThreshold = 4,
            EnablePeerNetworkRouting = true,
            OutputStagingPath = Path.Combine(Path.GetTempPath(), $"ashlar-peer-none-{Guid.NewGuid():N}")
        });

        var localExecutor = new StubLocalExecutor(_ => Result<GenerationExecutionResult>.Success(new GenerationExecutionResult
        {
            Payload = [1],
            Provider = "local",
            ModelId = "m",
            IsRemote = false
        }));
        var runPod = new RunPodBrick(new StubRunPodClient(), runPodConfig, NullLogger<RunPodBrick>.Instance);
        var peerExecutor = new StubPeerExecutor
        {
            NextResult = Result<GenerationExecutionResult>.Failure("peer-routing.no_eligible_peers", "No eligible peer endpoint is currently available.")
        };
        var peerSnapshot = new StubPeerSnapshot
        {
            Candidates =
            [
                new PeerExecutionCandidate
                {
                    PeerId = "peer-low",
                    Endpoint = "http://peer-low",
                    AvailableVramBytes = 128L * 1024 * 1024,
                    ComputeClass = GpuComputeClass.Low,
                    QueueDepth = 0,
                    CapturedAt = DateTimeOffset.UtcNow
                }
            ]
        };

        var router = new NcrCapabilityRouter(
            new SnapshotStub
            {
                AvailableVramBytes = 128L * 1024 * 1024,
                ComputeClass = GpuComputeClass.Low,
                CurrentQueueDepth = 0
            },
            peerSnapshot,
            peerExecutor,
            localExecutor,
            runPod,
            runPodConfig,
            NullLogger<NcrCapabilityRouter>.Instance);

        var target = router.ResolveExecutionTarget(new JobRequirements
        {
            ModelId = "m",
            MinimumVramBytes = 8L * 1024 * 1024 * 1024,
            ComputeClass = GpuComputeClass.High,
            RemoteExecutionPreference = RemoteExecutionPreference.PeerNetworkOnly
        });

        target.Should().BeOfType<ExecutionTarget.Remote>();
        var remote = (ExecutionTarget.Remote)target;
        var result = await remote.Executor.ExecuteAsync(
            new RunPodJobPayload { ModelId = "m", Prompt = "p" },
            new JobRequirements { ModelId = "m" },
            TestExecutionContext());
        result.IsFailure.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("peer-routing.no_eligible_peers");
    }

    private static NcrCapabilityRouter BuildRouter(
        long availableVram,
        GpuComputeClass computeClass,
        int queueDepth,
        int queueThreshold)
    {
        var snapshot = new SnapshotStub
        {
            AvailableVramBytes = availableVram,
            ComputeClass = computeClass,
            CurrentQueueDepth = queueDepth
        };
        var config = Options.Create(new RunPodBrickConfig
        {
            QueueDepthThreshold = queueThreshold,
            Timeout = TimeSpan.FromSeconds(2),
            PollingInterval = TimeSpan.FromMilliseconds(20),
            OutputStagingPath = Path.Combine(Path.GetTempPath(), $"ashlar-runpod-router-{Guid.NewGuid():N}")
        });
        var localExecutor = new StubLocalExecutor(_ => Result<GenerationExecutionResult>.Success(new GenerationExecutionResult
        {
            Payload = [1],
            Provider = "local",
            ModelId = "m",
            IsRemote = false
        }));
        var runPodClient = new StubRunPodClient();
        var runPodBrick = new RunPodBrick(runPodClient, config, NullLogger<RunPodBrick>.Instance);
        var peerExecutor = new StubPeerExecutor();
        var peerSnapshot = new StubPeerSnapshot();
        return new NcrCapabilityRouter(
            snapshot,
            peerSnapshot,
            peerExecutor,
            localExecutor,
            runPodBrick,
            config,
            NullLogger<NcrCapabilityRouter>.Instance);
    }

    /// <summary>Tests for snapshot stub.</summary>
    private sealed class SnapshotStub : INCRCapabilitySnapshot
    {
        /// <summary>Available vram bytes.</summary>
        public long AvailableVramBytes { get; set; }
        /// <summary>Compute class.</summary>
        public GpuComputeClass ComputeClass { get; set; } = GpuComputeClass.None;
        /// <summary>Current queue depth.</summary>
        public int CurrentQueueDepth { get; set; }
        /// <summary>Captured at.</summary>
        public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Tests for stub peer snapshot.</summary>
    private sealed class StubPeerSnapshot : IPeerCapabilitySnapshot
    {
        /// <summary>Candidates.</summary>
        public IReadOnlyList<PeerExecutionCandidate> Candidates { get; init; } = [];
    }

    /// <summary>Tests for stub local executor.</summary>
    private sealed class StubLocalExecutor : ILocalExecutor
    {
        private readonly Func<RunPodJobPayload, Result<GenerationExecutionResult>> _handler;

        public StubLocalExecutor(Func<RunPodJobPayload, Result<GenerationExecutionResult>> handler)
        {
            _handler = handler;
        }

        public Task<Result<GenerationExecutionResult>> ExecuteAsync(
            RunPodJobPayload payload,
            JobRequirements requirements,
            IExecutionContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_handler(payload));
    }

    /// <summary>Tests for stub run pod client.</summary>
    private sealed class StubRunPodClient : IRunPodClient
    {
        /// <summary>Spin up result.</summary>
        public Result<RunPodInstance> SpinUpResult { get; set; } = Result<RunPodInstance>.Failure("stub.spinup.not_configured", "Spin-up not configured.");
        /// <summary>Dispatch result.</summary>
        public Result<JobHandle> DispatchResult { get; set; } = Result<JobHandle>.Failure("stub.dispatch.not_configured", "Dispatch not configured.");
        /// <summary>Status sequence.</summary>
        public Queue<JobStatus> StatusSequence { get; set; } = new();
        /// <summary>Pull result.</summary>
        public Result<byte[]> PullResult { get; set; } = Result<byte[]>.Failure("stub.pull.not_configured", "Pull not configured.");
        /// <summary>Terminate result.</summary>
        public Result<Unit> TerminateResult { get; set; } = Result<Unit>.Success(Unit.Value);
        /// <summary>Terminate calls.</summary>
        public int TerminateCalls { get; private set; }

        public Task<Result<RunPodInstance>> SpinUpInstance(
            string modelId,
            string gpuType,
            CancellationToken cancellationToken = default)
            => Task.FromResult(SpinUpResult);

        public Task<Result<JobHandle>> DispatchJob(
            string instanceId,
            RunPodJobPayload payload,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DispatchResult);

        public Task<Result<JobStatus>> PollJobStatus(
            JobHandle jobHandle,
            CancellationToken cancellationToken = default)
        {
            if (StatusSequence.Count > 0)
            {
                return Task.FromResult(Result<JobStatus>.Success(StatusSequence.Dequeue()));
            }

            return Task.FromResult(Result<JobStatus>.Success(new JobStatus
            {
                State = RunPodJobState.Running
            }));
        }

        public Task<Result<byte[]>> PullResults(
            JobHandle jobHandle,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PullResult);

        public Task<Result<Unit>> TerminateInstance(
            string instanceId,
            CancellationToken cancellationToken = default)
        {
            TerminateCalls++;
            return Task.FromResult(TerminateResult);
        }
    }

    /// <summary>Tests for test execution context.</summary>
    // Execution context for these tests. The IExecutionContext implementation now
    // lives in Ashlar.Agents.TestKit.FakeExecutionContext; only the fixture values
    // that are specific to this suite stay here.
    private static FakeExecutionContext TestExecutionContext() => new()
    {
        AgentId = "test-agent",
        BehaviorId = "test-behavior",
        AuditMode = true,
        Provider = "ollama"
    };


    /// <summary>Tests for stub peer executor.</summary>
    private sealed class StubPeerExecutor : IPeerExecutor
    {
        /// <summary>Next result.</summary>
        public Result<GenerationExecutionResult> NextResult { get; set; } =
            Result<GenerationExecutionResult>.Failure("peer-routing.stub", "Stub peer executor should not be invoked in router-only tests.");

        public Task<Result<GenerationExecutionResult>> ExecuteAsync(
            RunPodJobPayload payload,
            JobRequirements requirements,
            IExecutionContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(NextResult);
    }

    /// <summary>
    /// A UTC clock that advances a fixed step on every reading and never consults the machine.
    /// <para>Deliberately NOT a fake that also fakes delays: the polling interval here is 20ms of
    /// real time, which is cheap and keeps the loop's own asynchrony honest. What must not depend on
    /// the machine is the DEADLINE, and this is the smallest change that achieves that.</para>
    /// </summary>
    private sealed class SteppingUtcClock : TimeProvider
    {
        private readonly TimeSpan _step;
        private long _ticks;

        public SteppingUtcClock(TimeSpan step) => _step = step;

        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(Interlocked.Add(ref _ticks, _step.Ticks), TimeSpan.Zero);
    }
}
