using System.Text;
using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Infrastructure.Execution.Routing;

/// <summary>
/// Local generation executor backed by the existing provider factory.
/// </summary>
internal sealed class ProviderFactoryLocalExecutor : ILocalExecutor
{
    private readonly IProviderFactory _providerFactory;
    private readonly ILogger<ProviderFactoryLocalExecutor> _logger;

    /// <summary>Initializes a new provider factory local executor.</summary>
    public ProviderFactoryLocalExecutor(
        IProviderFactory providerFactory,
        ILogger<ProviderFactoryLocalExecutor> logger)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Execute asynchronously.</summary>
    public async Task<Result<GenerationExecutionResult>> ExecuteAsync(
        RunPodJobPayload payload,
        JobRequirements requirements,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "capability-routing local executor start: provider={Provider} model={ModelId}",
                context.Provider,
                payload.ModelId);

            var response = await _providerFactory.ExecuteLLMAsync(
                context.Provider,
                string.Empty,
                payload.Prompt,
                payload.GenerationParameters,
                cancellationToken).ConfigureAwait(false);

            var bytes = Encoding.UTF8.GetBytes(response);
            return Result<GenerationExecutionResult>.Success(new GenerationExecutionResult
            {
                Payload = bytes,
                Provider = context.Provider,
                ModelId = payload.ModelId,
                IsRemote = false,
                Summary = "Local execution completed"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local generation execution failed for model={ModelId}", payload.ModelId);
            // Preserve EGRESS_REFUSED and the redacted message/ref even when the refusal is wrapped
            // (card #809 / 4.8b Known limit). Ordinary failures keep local.execution_failed.
            if (EgressRefusal.Find(ex) is { } refusal)
            {
                return Result<GenerationExecutionResult>.Failure(
                    refusal.ErrorCode,
                    "Local execution refused by egress policy.",
                    refusal.Message);
            }

            return Result<GenerationExecutionResult>.Failure(
                "local.execution_failed",
                "Local execution failed.",
                ex.Message);
        }
    }
}
