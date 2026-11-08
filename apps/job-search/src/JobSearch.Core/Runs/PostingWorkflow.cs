using Ashlar.WebOperations.Jobs;
using Ashlar.Core.Application.Workflows;
using Ashlar.Core.Domain.Workflows;
using JobSearch.Bricks;
using JobSearch.Materials;
using JobSearch.Screening;

namespace JobSearch.Runs;

/// <summary>What the per-posting workflow produced.</summary>
public sealed record PostingOutcome(
    string Signal, bool ProvesOpen, string Detail, JobPosting? Posting, Assessment? Assessment,
    bool Drafted, Draft? Resume, Draft? CoverLetter, ValidationRecord? ResumeValidation, ValidationRecord? CoverValidation,
    byte[]? ResumePdf, byte[]? CoverPdf, IReadOnlyList<string> NodeLog);

/// <summary>
/// One posting through an Ashlar workflow, executed by Ashlar's <see cref="WorkflowExecutor"/>:
/// <code>
///   [posting ref] ──► jobs.get-posting ──► jobsearch.screen ──► jobsearch.tailor ──► jobsearch.validate-materials
///   [candidate]  ───────────────────────────┴───────────────────────┴──────────────────────┘
/// </code>
/// Runtime values enter as InputNode text, because the executor gives brick nodes only what arrives on connected
/// ports (WorkflowInput.Parameters reach cluster nodes only). Drafting is gated by the screen brick's <c>draft</c>
/// output wired to the tailor brick's <c>proceed</c> input, because a ConditionalNode computes a flag but does not stop
/// downstream nodes. Both are recorded in docs/DOGFOOD-FINDINGS.md.
/// </summary>
public static class PostingWorkflow
{
    private static NodePort In(string name) => new() { Id = $"in:{name}", Name = name, Direction = PortDirection.Input, DataType = "any", Required = false };
    private static NodePort Out(string name) => new() { Id = $"out:{name}", Name = name, Direction = PortDirection.Output, DataType = "any" };

    private static VisualWorkflowConnection Wire(string from, string fromPort, string to, string toPort) =>
        new() { Id = $"{from}.{fromPort}->{to}.{toPort}", FromNodeId = from, FromPortId = $"out:{fromPort}", ToNodeId = to, ToPortId = $"in:{toPort}" };

    /// <summary>The workflow for one posting and one candidate.</summary>
    public static WorkflowDefinition Build(string postingKey, string employer, CandidateContext candidate)
    {
        var nodes = new List<WorkflowNode>
        {
            new InputNode { Id = "posting-ref", Name = "Posting ref", Type = InputType.Content, Content = postingKey, Outputs = [Out("data")] },
            new InputNode { Id = "employer", Name = "Employer", Type = InputType.Content, Content = employer, Outputs = [Out("data")] },
            new InputNode { Id = "candidate", Name = "Candidate", Type = InputType.Content, Content = Store.Json.Write(candidate), Outputs = [Out("data")] },
            new BrickNode
            {
                Id = "get", Name = "Read posting at the employer", BrickId = "jobs.get-posting", Implementation = Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic,
                Inputs = [In("posting"), In("employer")], Outputs = [Out("signal"), Out("provesOpen"), Out("posting"), Out("detail")],
            },
            new BrickNode
            {
                Id = "screen", Name = "Screen", BrickId = "jobsearch.screen", Implementation = Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic,
                Inputs = [In("signal"), In("posting"), In("candidate")], Outputs = [Out("screened"), Out("recommendation"), Out("assessment"), Out("draft")],
            },
            new BrickNode
            {
                Id = "tailor", Name = "Draft materials", BrickId = "jobsearch.tailor", Implementation = Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic,
                Inputs = [In("proceed"), In("posting"), In("assessment"), In("candidate")], Outputs = [Out("drafted"), Out("resume"), Out("coverLetter")],
            },
            new BrickNode
            {
                Id = "validate", Name = "Validate materials", BrickId = "jobsearch.validate-materials", Implementation = Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic,
                Inputs = [In("drafted"), In("resume"), In("coverLetter"), In("posting"), In("assessment"), In("candidate")],
                Outputs = [Out("validated"), Out("passed"), Out("resumeValidation"), Out("coverValidation"), Out("resumePdf"), Out("coverPdf")],
            },
        };
        var wires = new List<VisualWorkflowConnection>
        {
            Wire("posting-ref", "data", "get", "posting"), Wire("employer", "data", "get", "employer"),
            Wire("get", "signal", "screen", "signal"), Wire("get", "posting", "screen", "posting"), Wire("candidate", "data", "screen", "candidate"),
            Wire("screen", "draft", "tailor", "proceed"), Wire("get", "posting", "tailor", "posting"), Wire("screen", "assessment", "tailor", "assessment"),
            Wire("candidate", "data", "tailor", "candidate"),
            Wire("tailor", "drafted", "validate", "drafted"), Wire("tailor", "resume", "validate", "resume"), Wire("tailor", "coverLetter", "validate", "coverLetter"),
            Wire("get", "posting", "validate", "posting"), Wire("screen", "assessment", "validate", "assessment"), Wire("candidate", "data", "validate", "candidate"),
        };
        return new WorkflowDefinition
        {
            Id = $"jobsearch.posting:{postingKey}", Name = "jobsearch: read, screen, draft, validate one posting",
            Description = "Per-posting pipeline of the job-search app (apps/job-search).", Nodes = nodes, Connections = wires,
        };
    }

    /// <summary>Executes the workflow and collects its outputs and a log line per node.</summary>
    public static async Task<PostingOutcome> RunAsync(WorkflowExecutor executor, string postingKey, string employer, CandidateContext candidate, CancellationToken ct)
    {
        var log = new List<string>();
        using var _ = executor.Events.Subscribe(new Observer(e =>
        {
            if (e is NodeCompletedEvent { Node: BrickNode n } done)
                lock (log) log.Add($"{n.BrickId}: {Describe(done.Result.Outputs)}");
        }));
        var result = await executor.ExecuteAsync(Build(postingKey, employer, candidate), new WorkflowInput(), ct).ConfigureAwait(false);

        T? Out<T>(string node, string key) =>
            result.NodeResults.TryGetValue(node, out var r) && r.Outputs.TryGetValue(key, out var v) && v is T t ? t : default;

        return new PostingOutcome(
            Out<string>("get", "signal") ?? "error", Out<bool>("get", "provesOpen"), Out<string>("get", "detail") ?? "",
            Out<JobPosting>("get", "posting"), Out<Assessment>("screen", "assessment"),
            Out<bool>("tailor", "drafted"), Out<Draft>("tailor", "resume"), Out<Draft>("tailor", "coverLetter"),
            Out<ValidationRecord>("validate", "resumeValidation"), Out<ValidationRecord>("validate", "coverValidation"),
            Out<byte[]>("validate", "resumePdf"), Out<byte[]>("validate", "coverPdf"), log);
    }

    private static string Describe(Dictionary<string, object> outputs) =>
        string.Join(", ", outputs.Where(kv => kv.Value is string or bool or int or double).Select(kv => $"{kv.Key}={kv.Value}"));

    private sealed class Observer(Action<WorkflowExecutionEvent> onNext) : IObserver<WorkflowExecutionEvent>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(WorkflowExecutionEvent value) => onNext(value);
    }
}
