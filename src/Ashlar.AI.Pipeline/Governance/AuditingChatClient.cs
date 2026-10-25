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
    private readonly IReadOnlyList<IChatInvocationUsageObserver> _usageObservers;

    /// <summary>Creates an auditing decorator.</summary>
    public AuditingChatClient(
        IChatClient innerClient,
        IChatInvocationAuditor auditor,
        string targetKey,
        Func<string?>? correlationIdAccessor = null)
        : this(innerClient, auditor, targetKey, correlationIdAccessor, null)
    {
    }

    /// <summary>Creates an auditing decorator with optional host usage observers.</summary>
    public AuditingChatClient(
        IChatClient innerClient,
        IChatInvocationAuditor auditor,
        string targetKey,
        Func<string?>? correlationIdAccessor,
        IEnumerable<IChatInvocationUsageObserver>? usageObservers)
        : base(innerClient)
    {
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
        _targetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        _correlationIdAccessor = correlationIdAccessor;
        _usageObservers = usageObservers?.ToArray() ?? Array.Empty<IChatInvocationUsageObserver>();
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var invocation = new Invocation(Guid.NewGuid(), DateTimeOffset.UtcNow);
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            Emit(invocation, "success", response.ModelId ?? options?.ModelId, sw.ElapsedMilliseconds, response.Usage);
            return response;
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            sw.Stop();
            EmitRefusal(invocation, refusal, options?.ModelId, sw.ElapsedMilliseconds);
            EgressRefusal.ThrowIfPresent(ex);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            Emit(invocation, "cancelled", options?.ModelId, sw.ElapsedMilliseconds, reasonCode: "cancelled");
            throw;
        }
        catch (PolicyViolationException ex)
        {
            sw.Stop();
            Emit(invocation, "denied", options?.ModelId ?? ex.ModelId, sw.ElapsedMilliseconds, reasonCode: ex.Code);
            throw;
        }
        catch (Exception)
        {
            sw.Stop();
            Emit(invocation, "fault", options?.ModelId, sw.ElapsedMilliseconds, reasonCode: "fault");
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
        var invocation = new Invocation(Guid.NewGuid(), DateTimeOffset.UtcNow);
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
                EmitRefusal(invocation, refusal, modelId, sw.ElapsedMilliseconds, usage);
                EgressRefusal.ThrowIfPresent(ex);
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                Emit(invocation, "cancelled", modelId, sw.ElapsedMilliseconds, usage, "cancelled");
                throw;
            }
            catch (Exception)
            {
                sw.Stop();
                Emit(invocation, "fault", modelId, sw.ElapsedMilliseconds, usage, "fault");
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
                    EmitRefusal(invocation, refusal, modelId, sw.ElapsedMilliseconds, usage);
                    EgressRefusal.ThrowIfPresent(ex);
                    throw;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    sw.Stop();
                    Emit(invocation, "cancelled", modelId, sw.ElapsedMilliseconds, usage, "cancelled");
                    throw;
                }
                catch (PolicyViolationException ex)
                {
                    sw.Stop();
                    Emit(invocation, "denied", modelId ?? ex.ModelId, sw.ElapsedMilliseconds, usage, ex.Code);
                    throw;
                }
                catch (Exception)
                {
                    sw.Stop();
                    Emit(invocation, "fault", modelId, sw.ElapsedMilliseconds, usage, "fault");
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
                    EmitRefusal(invocation, refusal, modelId, sw.ElapsedMilliseconds, usage);
                    EgressRefusal.ThrowIfPresent(ex);
                    throw;
                }
                catch (Exception)
                {
                    sw.Stop();
                    if (!invocation.Emitted) Emit(invocation, "fault", modelId, sw.ElapsedMilliseconds, usage, "dispose_fault");
                    throw;
                }
            }
            if (!completed && !invocation.Emitted && activeRefusal is null)
            {
                sw.Stop();
                Emit(invocation, "cancelled", modelId, sw.ElapsedMilliseconds, usage, "stream_disposed");
            }
        }
        if (completed) Emit(invocation, "success", modelId, sw.ElapsedMilliseconds, usage);
    }

    private void EmitRefusal(Invocation invocation, EgressRefusedException refusal, string? modelId, long latencyMs, UsageDetails? usage = null)
    {
        try
        {
            // Skip when EgressGuardChatClient (or an inner AuditingChatClient) already recorded this Decision.
            if (!EgressRefusalAuditDeduper.ShouldRecord(refusal.Decision))
                return;

            Emit(invocation, "denied", modelId, latencyMs, usage, "egress_refused", refusal.Decision);
        }
        catch (Exception)
        {
            // The operator's auditor must not replace or suppress a policy refusal.
        }
    }

    private void Emit(
        Invocation invocation,
        string outcome,
        string? modelId,
        long latencyMs,
        UsageDetails? usage = null,
        string? reasonCode = null,
        EgressDecision? egressDecision = null)
    {
        invocation.Emitted = true;
        var sanitize = SanitizationCallContext.Result;
        var decisions = new List<string> { $"target={_targetKey}", $"outcome={outcome}" };
        if (sanitize is not null)
        {
            decisions.Add($"sanitize_redactions={sanitize.RedactionCount}");
        }

        var record = new ChatInvocationAuditRecord
        {
            InvocationId = invocation.Id,
            StartedAt = invocation.StartedAt,
            IsProviderInvocation = !string.Equals(_targetKey, Routing.RoutingChatClient.RouterTargetKey, StringComparison.Ordinal),
            TargetKey = _targetKey,
            ModelId = modelId,
            Outcome = outcome,
            PolicyDecisions = decisions,
            RedactionCount = sanitize?.RedactionCount ?? 0,
            RedactionCategories = sanitize?.Categories ?? Array.Empty<string>(),
            InputTokenCount = usage?.InputTokenCount,
            OutputTokenCount = usage?.OutputTokenCount,
            CachedInputTokenCount = usage?.CachedInputTokenCount,
            ReasoningTokenCount = usage?.ReasoningTokenCount,
            UsageIsProviderReported = usage is ProviderReportedChatUsage,
            UsageHasInvalidCounts = usage is ProviderReportedChatUsage { HasInvalidCounts: true },
            LatencyMs = latencyMs,
            CorrelationId = _correlationIdAccessor?.Invoke(),
            ReasonCode = reasonCode,
            EgressDecision = egressDecision,
        };
        foreach (var observer in _usageObservers)
        {
            try { observer.Observe(record); }
            catch (Exception)
            {
                // Optional accounting cannot turn a successful call into a retry or replace a refusal.
            }
        }
        _auditor.Record(record);
    }

    private sealed record Invocation(Guid Id, DateTimeOffset StartedAt)
    {
        internal bool Emitted { get; set; }
    }
}
