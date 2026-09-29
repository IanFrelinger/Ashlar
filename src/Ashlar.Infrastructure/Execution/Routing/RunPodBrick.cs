using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Infrastructure.Execution.Routing;

/// <summary>
/// Remote generation brick using full RunPod instance lifecycle.
/// </summary>
public sealed class RunPodBrick : DomainBrick, IBrickExecutor
{
    private readonly IRunPodClient _runPodClient;
    private readonly IOptions<RunPodBrickConfig> _config;
    private readonly ILogger<RunPodBrick> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new run pod brick.</summary>
    /// <param name="timeProvider">
    /// Source of "now" for the job-completion deadline. Optional and defaulting to
    /// <see cref="TimeProvider.System"/>, so production wiring is unchanged.
    /// <para>It exists because the deadline below is measured against real wall clock, which makes
    /// any test of the polling loop a race with the machine it runs on rather than a statement about
    /// this code. <c>CapabilityRoutingBrickTests.HappyPath_RemoteExecution</c> stubs a two-entry
    /// status queue that completes in about 40ms and allows 2s for it; on a contended macOS runner it
    /// consumed the whole 2s and reported a timeout, failing the required readiness gate with
    /// 5698/5699 passing. Injecting the clock makes that test a statement about the loop.</para>
    /// </param>
    public RunPodBrick(
        IRunPodClient runPodClient,
        IOptions<RunPodBrickConfig> config,
        ILogger<RunPodBrick> logger,
        TimeProvider? timeProvider = null)
    {
        _runPodClient = runPodClient ?? throw new ArgumentNullException(nameof(runPodClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        Id = "generation.runpod";
        Name = "RunPod Generation";
        Version = "1.0.0";
        Icon = "☁️";
        Category = BrickCategory.Generation;
        Description = "Remote generation via RunPod with full lifecycle management.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("payload", "RunPodJobPayload", "Typed generation payload", false),
                new BrickInputDefinition("modelId", "string", "Target model id", false),
                new BrickInputDefinition("prompt", "string", "Prompt text", false),
                new BrickInputDefinition("parameters", "object", "Generation parameters", false),
                new BrickInputDefinition("outputFormat", "string", "Requested output format", false, "text/plain"),
                new BrickInputDefinition("priority", "bool", "Priority generation flag", false, false),
                new BrickInputDefinition("requirements", "JobRequirements", "Routing requirements", false),
                new BrickInputDefinition("minimumVramBytes", "long", "Minimum VRAM requirement", false, 0L),
                new BrickInputDefinition("computeClass", "string", "Required compute class", false, GpuComputeClass.Low.ToString()),
                new BrickInputDefinition("estimatedDurationSeconds", "int", "Estimated duration in seconds", false, 60),
                new BrickInputDefinition("overnight", "bool", "Overnight/background execution hint", false, false)
            ],
            Outputs =
            [
                new BrickOutputDefinition("success", "bool", "Whether remote generation succeeded"),
                new BrickOutputDefinition("payload", "byte[]", "Raw generated payload"),
                new BrickOutputDefinition("outputPath", "string", "Staged output path"),
                new BrickOutputDefinition("errorCode", "string", "Error code on failure"),
                new BrickOutputDefinition("errorMessage", "string", "Error message on failure")
            ]
        };
        Implementations = new BrickImplementations
        {
            Deterministic = new DeterministicImplementation
            {
                Id = "runpod-lifecycle",
                Name = "RunPod lifecycle executor",
                Description = "Spin-up, dispatch, poll, pull, and teardown",
                Executor = nameof(RunPodBrick)
            }
        };
    }

    /// <summary>Execute asynchronously.</summary>
    public async Task<Result<GenerationExecutionResult>> ExecuteAsync(
        RunPodJobPayload payload,
        JobRequirements requirements,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var options = _config.Value;
        var pollingInterval = options.PollingInterval > TimeSpan.Zero
            ? options.PollingInterval
            : TimeSpan.FromSeconds(2);
        var timeout = options.Timeout > TimeSpan.Zero
            ? options.Timeout
            : TimeSpan.FromMinutes(10);
        var gpuTier = string.IsNullOrWhiteSpace(options.PreferredGpuTier)
            ? "NVIDIA_A4000"
            : options.PreferredGpuTier;

        RunPodInstance? instance = null;
        try
        {
            _logger.LogInformation(
                "runpod.lifecycle stage=spin_up_start model={ModelId} gpuTier={GpuTier} behavior={BehaviorId}",
                payload.ModelId,
                gpuTier,
                context.BehaviorId);

            var spinUp = await _runPodClient.SpinUpInstance(payload.ModelId, gpuTier, cancellationToken).ConfigureAwait(false);
            if (spinUp.IsFailure || spinUp.Value is null)
            {
                _logger.LogWarning(
                    "runpod.lifecycle stage=spin_up_failed code={Code} message={Message}",
                    spinUp.Error?.Code,
                    spinUp.Error?.Message);
                return Result<GenerationExecutionResult>.Failure(
                    spinUp.Error?.Code ?? "runpod.spinup_failed",
                    spinUp.Error?.Message ?? "Failed to spin up RunPod instance.",
                    spinUp.Error?.Detail);
            }

            instance = spinUp.Value;
            _logger.LogInformation(
                "runpod.lifecycle stage=spin_up_complete instanceId={InstanceId}",
                instance.InstanceId);

            _logger.LogInformation(
                "runpod.lifecycle stage=dispatch_start instanceId={InstanceId}",
                instance.InstanceId);
            var dispatch = await _runPodClient.DispatchJob(instance.InstanceId, payload, cancellationToken).ConfigureAwait(false);
            if (dispatch.IsFailure || dispatch.Value is null)
            {
                _logger.LogWarning(
                    "runpod.lifecycle stage=dispatch_failed code={Code} message={Message}",
                    dispatch.Error?.Code,
                    dispatch.Error?.Message);
                return Result<GenerationExecutionResult>.Failure(
                    dispatch.Error?.Code ?? "runpod.dispatch_failed",
                    dispatch.Error?.Message ?? "Failed to dispatch RunPod job.",
                    dispatch.Error?.Detail);
            }

            var handle = dispatch.Value;
            _logger.LogInformation(
                "runpod.lifecycle stage=dispatch_complete jobId={JobId}",
                handle.JobId);

            var deadline = _timeProvider.GetUtcNow() + timeout;
            JobStatus? finalStatus = null;
            while (_timeProvider.GetUtcNow() < deadline && !cancellationToken.IsCancellationRequested)
            {
                var statusResult = await _runPodClient.PollJobStatus(handle, cancellationToken).ConfigureAwait(false);
                if (statusResult.IsFailure || statusResult.Value is null)
                {
                    _logger.LogWarning(
                        "runpod.lifecycle stage=poll_failed code={Code} message={Message}",
                        statusResult.Error?.Code,
                        statusResult.Error?.Message);
                    return Result<GenerationExecutionResult>.Failure(
                        statusResult.Error?.Code ?? "runpod.poll_failed",
                        statusResult.Error?.Message ?? "Failed polling RunPod job status.",
                        statusResult.Error?.Detail);
                }

                finalStatus = statusResult.Value;
                _logger.LogInformation(
                    "runpod.lifecycle stage=poll_status jobId={JobId} state={State}",
                    handle.JobId,
                    finalStatus.State);

                if (finalStatus.State == RunPodJobState.Completed)
                {
                    break;
                }

                if (finalStatus.IsTerminal)
                {
                    return Result<GenerationExecutionResult>.Failure(
                        "runpod.job_failed",
                        finalStatus.Message ?? "RunPod job failed before completion.");
                }

                await Task.Delay(pollingInterval, cancellationToken).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("runpod.lifecycle stage=cancelled");
                return Result<GenerationExecutionResult>.Failure(
                    "runpod.cancelled",
                    "RunPod execution was cancelled.");
            }

            if (finalStatus?.State != RunPodJobState.Completed)
            {
                _logger.LogWarning(
                    "runpod.lifecycle stage=timeout jobModel={ModelId} timeoutSeconds={TimeoutSeconds}",
                    payload.ModelId,
                    timeout.TotalSeconds);
                return Result<GenerationExecutionResult>.Failure(
                    "runpod.timeout",
                    "RunPod job timed out before completion.");
            }

            _logger.LogInformation(
                "runpod.lifecycle stage=pull_start jobId={JobId}",
                handle.JobId);
            var pull = await _runPodClient.PullResults(handle, cancellationToken).ConfigureAwait(false);
            if (pull.IsFailure || pull.Value is null)
            {
                _logger.LogWarning(
                    "runpod.lifecycle stage=pull_failed code={Code} message={Message}",
                    pull.Error?.Code,
                    pull.Error?.Message);
                return Result<GenerationExecutionResult>.Failure(
                    pull.Error?.Code ?? "runpod.pull_failed",
                    pull.Error?.Message ?? "Failed to pull RunPod results.",
                    pull.Error?.Detail);
            }

            var outputPath = await StageOutputAsync(
                options.OutputStagingPath,
                payload.ModelId,
                pull.Value,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "runpod.lifecycle stage=complete model={ModelId} outputPath={OutputPath} audit={AuditMode}",
                payload.ModelId,
                outputPath,
                context.AuditMode);

            return Result<GenerationExecutionResult>.Success(new GenerationExecutionResult
            {
                Payload = pull.Value,
                OutputPath = outputPath,
                Provider = "runpod",
                ModelId = payload.ModelId,
                IsRemote = true,
                Summary = "Remote RunPod execution completed"
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("runpod.lifecycle stage=cancelled");
            return Result<GenerationExecutionResult>.Failure(
                "runpod.cancelled",
                "RunPod execution was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "runpod.lifecycle stage=unexpected_failure");
            return Result<GenerationExecutionResult>.Failure(
                "runpod.unexpected",
                "Unexpected RunPod lifecycle failure.",
                ex.Message);
        }
        finally
        {
            if (instance is not null)
            {
                var terminate = await _runPodClient.TerminateInstance(instance.InstanceId, cancellationToken).ConfigureAwait(false);
                if (terminate.IsFailure)
                {
                    _logger.LogWarning(
                        "runpod.lifecycle stage=teardown_failed instanceId={InstanceId} code={Code} message={Message}",
                        instance.InstanceId,
                        terminate.Error?.Code,
                        terminate.Error?.Message);
                }
                else
                {
                    _logger.LogInformation(
                        "runpod.lifecycle stage=teardown_complete instanceId={InstanceId}",
                        instance.InstanceId);
                }
            }
        }
    }

    /// <summary>Execute asynchronously.</summary>
    public override async Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var payload = ReadPayload(input);
        var requirements = ReadRequirements(input, payload.ModelId);
        var result = await ExecuteAsync(payload, requirements, context, cancellationToken).ConfigureAwait(false);

        var output = new BrickOutput();
        if (result.IsSuccess && result.Value is not null)
        {
            output.Set("success", true);
            output.Set("payload", result.Value.Payload);
            output.Set("outputPath", result.Value.OutputPath);
            output.Summary = result.Value.Summary;
            return output;
        }

        output.Set("success", false);
        output.Set("errorCode", result.Error?.Code ?? "runpod.unknown");
        output.Set("errorMessage", result.Error?.Message ?? "RunPod execution failed.");
        output.Summary = $"RunPod execution failed: {result.Error?.Code ?? "runpod.unknown"}";
        return output;
    }

    private static RunPodJobPayload ReadPayload(BrickInput input)
    {
        var typed = input.Get<RunPodJobPayload?>("payload", null);
        if (typed is not null)
        {
            return typed;
        }

        var parameters = input.Get<IReadOnlyDictionary<string, object>?>("parameters", null) ??
                         new Dictionary<string, object>();
        return new RunPodJobPayload
        {
            ModelId = input.Get<string>("modelId", string.Empty) ?? string.Empty,
            Prompt = input.Get<string>("prompt", string.Empty) ?? string.Empty,
            GenerationParameters = parameters,
            OutputFormat = input.Get<string>("outputFormat", "text/plain") ?? "text/plain",
            IsPriority = input.Get<bool>("priority", false)
        };
    }

    private static JobRequirements ReadRequirements(BrickInput input, string fallbackModelId)
    {
        var typed = input.Get<JobRequirements?>("requirements", null);
        if (typed is not null)
        {
            return typed;
        }

        var computeClassText = input.Get<string>("computeClass", GpuComputeClass.Low.ToString()) ?? GpuComputeClass.Low.ToString();
        var computeClass = Enum.TryParse<GpuComputeClass>(computeClassText, true, out var parsed)
            ? parsed
            : GpuComputeClass.Low;

        var seconds = input.Get<int>("estimatedDurationSeconds", 60);
        return new JobRequirements
        {
            ModelId = input.Get<string>("modelId", fallbackModelId) ?? fallbackModelId,
            MinimumVramBytes = input.Get<long>("minimumVramBytes", 0L),
            ComputeClass = computeClass,
            EstimatedDuration = TimeSpan.FromSeconds(Math.Max(1, seconds)),
            IsOvernightOrBackground = input.Get<bool>("overnight", false)
        };
    }

    private static async Task<string> StageOutputAsync(
        string outputDirectory,
        string modelId,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var safeDir = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(Path.GetTempPath(), "ashlar-runpod")
            : outputDirectory;
        Directory.CreateDirectory(safeDir);

        var safeModel = string.IsNullOrWhiteSpace(modelId)
            ? "unknown-model"
            : modelId.Replace('/', '_').Replace('\\', '_');
        var fileName = $"{safeModel}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bin";
        var fullPath = Path.Combine(safeDir, fileName);
        await File.WriteAllBytesAsync(fullPath, data, cancellationToken).ConfigureAwait(false);
        return fullPath;
    }
}
