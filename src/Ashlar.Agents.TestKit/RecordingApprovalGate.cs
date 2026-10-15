using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Infrastructure.Execution;

namespace Ashlar.Agents.TestKit;

/// <summary>One recorded approval request.</summary>
/// <param name="Description">Action description the caller supplied.</param>
/// <param name="Timeout">Timeout the caller allowed.</param>
public sealed record RecordedApproval(string Description, TimeSpan Timeout);

/// <summary>
/// Recording <see cref="IApprovalGate"/> with scripted answers. Lets a test assert
/// that human-review routing happened — and, just as importantly, that it did NOT
/// happen for verdicts a human has no business overriding.
/// </summary>
public sealed class RecordingApprovalGate : IApprovalGate
{
    private readonly Scripted<ApprovalResult> _answers;

    /// <summary>Creates a gate from a scripted run of answers.</summary>
    public RecordingApprovalGate(Scripted<ApprovalResult> answers) =>
        _answers = answers ?? throw new ArgumentNullException(nameof(answers));

    /// <summary>Creates a gate that always returns the given answer.</summary>
    public RecordingApprovalGate(ApprovalResult answer = ApprovalResult.Approved)
        : this(Scripted<ApprovalResult>.Always(answer)) { }

    /// <summary>Requests seen, in order.</summary>
    public List<RecordedApproval> Requests { get; } = new();

    /// <summary>True when the gate was never consulted.</summary>
    public bool WasNeverConsulted => Requests.Count == 0;

    /// <inheritdoc />
    public Task<ApprovalResult> RequestApprovalAsync(
        string actionDescription,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(new RecordedApproval(actionDescription, timeout));
        return Task.FromResult(_answers.Next());
    }
}

/// <summary>
/// Scripted <see cref="IProviderFactory"/>. Offline by default: every model call
/// throws unless the test scripts a response, so a profile that silently depends on
/// a live model fails loudly instead of hanging on a socket.
/// </summary>
public sealed class FakeProviderFactory : IProviderFactory
{
    private readonly Scripted<string>? _responses;

    /// <summary>Creates a factory whose model calls all fail (the offline default).</summary>
    public FakeProviderFactory() { }

    /// <summary>Creates a factory with scripted model responses.</summary>
    public FakeProviderFactory(Scripted<string> responses) => _responses = responses;

    /// <summary>Creates a factory from explicit responses.</summary>
    public FakeProviderFactory(params string[] responses)
        : this(new Scripted<string>(responses)) { }

    /// <summary>Prompts the factory was asked to run, in order.</summary>
    public List<string> Prompts { get; } = new();

    /// <summary>True when no model call was attempted.</summary>
    public bool WasNeverCalled => Prompts.Count == 0;

    /// <summary>Whether <see cref="IsProviderAvailable"/> reports availability.</summary>
    public bool Available { get; init; }

    /// <summary>
    /// Whether <see cref="EnsureOllamaReachableAsync"/> succeeds instead of throwing.
    ///
    /// Default false keeps the offline-hostile stance: a component that quietly
    /// depends on a live Ollama fails loudly rather than hanging on a socket. Set
    /// true for tests whose subject legitimately calls the reachability probe and
    /// treats a throw as cause to retry — there, throwing turns a fast assertion
    /// into a hung suite.
    /// </summary>
    public bool OllamaReachable { get; init; }

    /// <inheritdoc />
    public bool IsProviderAvailable(string provider) => Available;

    /// <inheritdoc />
    public Ashlar.Core.Application.Execution.Ports.ProviderAvailabilityResult ProbeProviderAvailability(string provider) =>
        new(IsProviderAvailable(provider));

    /// <inheritdoc />
    public Task<string> ExecuteLLMAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        object config,
        CancellationToken cancellationToken = default)
    {
        Prompts.Add(userPrompt);
        if (_responses is null)
            return Task.FromException<string>(Offline());
        return Task.FromResult(_responses.Next());
    }

    /// <inheritdoc />
    public Task<string> ExecuteVisionAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        byte[] imageBytes,
        object config,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(Offline());

    /// <inheritdoc />
    public Task<string> ExecuteVisionMultiFrameAsync(
        string provider,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]> frameBytes,
        object config,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(Offline());

    /// <inheritdoc />
    public Task<string> ExecuteVideoAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]> frameBytes,
        object config,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(Offline());

    /// <inheritdoc />
    public Task EnsureOllamaReachableAsync(
        bool requireVisionModel,
        CancellationToken cancellationToken = default) =>
        OllamaReachable ? Task.CompletedTask : Task.FromException(Offline());

    private static InvalidOperationException Offline() =>
        new("FakeProviderFactory is offline: no scripted model response was configured.");
}
