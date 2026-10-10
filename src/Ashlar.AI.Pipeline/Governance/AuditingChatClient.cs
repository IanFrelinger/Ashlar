using Ashlar.Abstractions.Security.Egress;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ashlar.AI.Pipeline.Governance;

/// <summary>
/// Emits an audit record for every invocation. Streaming aggregates into one record at completion
/// (or a distinct record on cancellation/fault).
/// </summary>
public sealed class AuditingChatClient : DelegatingChatClient
{
    private readonly IChatInvocationAuditor _auditor;
    private readonly string _targetKey;
    private readonly Func<string?>? _correlationIdAccessor;

    /// <summary>Creates an auditing decorator.</summary>
    public AuditingChatClient(
        IChatClient innerClient,
        IChatInvocationAuditor auditor,
        string targetKey,
        Func<string?>? correlationIdAccessor = null)
        : base(innerClient)
    {
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
        _targetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        _correlationIdAccessor = correlationIdAccessor;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            Emit("success", response.ModelId ?? options?.ModelId, sw.ElapsedMilliseconds, response.Usage);
            return response;
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            sw.Stop();
            EmitRefusal(refusal, options?.ModelId, sw.ElapsedMilliseconds);
            EgressRefusal.ThrowIfPresent(ex);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            Emit("cancelled", options?.ModelId, sw.ElapsedMilliseconds, reasonCode: "cancelled");
            throw;
        }
        catch (PolicyViolationException ex)
        {
            sw.Stop();
            Emit("denied", options?.ModelId ?? ex.ModelId, sw.ElapsedMilliseconds, reasonCode: ex.Code);
            throw;
        }
        catch (Exception)
        {
            sw.Stop();
            Emit("fault", options?.ModelId, sw.ElapsedMilliseconds, reasonCode: "fault");
            throw;
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        string? modelId = options?.ModelId;
        UsageDetails? usage = null;
        var completed = false;
        EgressRefusedException? activeRefusal = null;

        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        try
        {
            try
            {
                enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
            {
                activeRefusal = refusal;
                sw.Stop();
                EmitRefusal(refusal, modelId, sw.ElapsedMilliseconds, usage);
                EgressRefusal.ThrowIfPresent(ex);
                throw;
            }

            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
                {
                    activeRefusal = refusal;
                    sw.Stop();
                    EmitRefusal(refusal, modelId, sw.ElapsedMilliseconds, usage);
                    EgressRefusal.ThrowIfPresent(ex);
                    throw;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    sw.Stop();
                    Emit("cancelled", modelId, sw.ElapsedMilliseconds, usage, "cancelled");
                    throw;
                }
                catch (PolicyViolationException ex)
                {
                    sw.Stop();
                    Emit("denied", modelId ?? ex.ModelId, sw.ElapsedMilliseconds, usage, ex.Code);
                    throw;
                }
                catch (Exception)
                {
                    sw.Stop();
                    Emit("fault", modelId, sw.ElapsedMilliseconds, usage, "fault");
                    throw;
                }

                if (!moved)
                {
                    break;
                }

                var update = enumerator.Current;
                if (!string.IsNullOrWhiteSpace(update.ModelId))
                {
                    modelId = update.ModelId;
                }

                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usageContent)
                    {
                        usage = usageContent.Details;
                    }
                }

                yield return update;
            }

            completed = true;
            sw.Stop();
        }
        finally
        {
            if (enumerator is not null)
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception) when (activeRefusal is not null)
                {
                    // Disposal cannot replace a refusal already being propagated.
                }
                catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
                {
                    sw.Stop();
                    EmitRefusal(refusal, modelId, sw.ElapsedMilliseconds, usage);
                    EgressRefusal.ThrowIfPresent(ex);
                    throw;
                }
            }

        }
        if (completed) Emit("success", modelId, sw.ElapsedMilliseconds, usage);
    }

    private void EmitRefusal(EgressRefusedException refusal, string? modelId, long latencyMs, UsageDetails? usage = null)
    {
        try
        {
            Emit("denied", modelId, latencyMs, usage, "egress_refused", refusal.Decision);
        }
        catch (Exception)
        {
            // The operator's auditor must not replace or suppress a policy refusal.
        }
    }

    private void Emit(
        string outcome,
        string? modelId,
        long latencyMs,
        UsageDetails? usage = null,
        string? reasonCode = null,
        EgressDecision? egressDecision = null)
    {
        var sanitize = SanitizationCallContext.Result;
        var decisions = new List<string> { $"target={_targetKey}", $"outcome={outcome}" };
        if (sanitize is not null)
        {
            decisions.Add($"sanitize_redactions={sanitize.RedactionCount}");
        }

        _auditor.Record(new ChatInvocationAuditRecord
        {
            TargetKey = _targetKey,
            ModelId = modelId,
            Outcome = outcome,
            PolicyDecisions = decisions,
            RedactionCount = sanitize?.RedactionCount ?? 0,
            RedactionCategories = sanitize?.Categories ?? Array.Empty<string>(),
            InputTokenCount = usage?.InputTokenCount,
            OutputTokenCount = usage?.OutputTokenCount,
            LatencyMs = latencyMs,
            CorrelationId = _correlationIdAccessor?.Invoke(),
            ReasonCode = reasonCode,
            EgressDecision = egressDecision,
        });
    }
}
