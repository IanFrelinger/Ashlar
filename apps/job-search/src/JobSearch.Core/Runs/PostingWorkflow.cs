using Ashlar.WebOperations.Jobs;
using Ashlar.Core.Application.Workflows;
using Ashlar.Core.Domain.Workflows;
using JobSearch.Bricks;
using JobSearch.Materials;
using JobSearch.Screening;
using JobSearch.Studio;

namespace JobSearch.Runs;

/// <summary>What the per-posting workflow produced.</summary>
public sealed record PostingOutcome(
    string Signal, bool ProvesOpen, string Detail, JobPosting? Posting, Assessment? Assessment,
    bool Drafted, Draft? Resume, Draft? CoverLetter, ValidationRecord? ResumeValidation, ValidationRecord? CoverValidation,
    byte[]? ResumePdf, byte[]? CoverPdf, IReadOnlyList<string> NodeLog, string? CoverLetterOrigin = null);

/// <summary>
/// One posting through an Ashlar workflow, executed by Ashlar's <see cref="WorkflowExecutor"/>:
/// <code>
///   [posting ref] ─► jobs.get-posting ─► jobsearch.screen ─► jobsearch.recommendation ─► jobsearch.decide ─► jobsearch.tailor ─► jobsearch.validate-materials
///   [candidate]  ──────────────────────────┴───────────────────────────────────────────────┴─────────────────┴────────────────────┘
/// </code>
/// <c>jobsearch.recommendation</c> is a certified brick: the node runs the gate-emitted assembly its certification record
/// covers, loaded by the host after the record's signature verified (JobSearch.Host.CertifiedBricks). Screening only
/// counts; the certified rule decides. Runtime values enter as InputNode text, because the executor gives brick nodes
/// only what arrives on connected ports (WorkflowInput.Parameters reach cluster nodes only). Drafting is gated by
/// <c>jobsearch.decide</c>'s <c>draft</c> output wired to the tailor brick's <c>proceed</c> input, because a ConditionalNode
/// computes a flag but does not stop downstream nodes. Both are recorded in docs/DOGFOOD-FINDINGS.md.
/// </summary>
public static class PostingWorkflow
{
    private static NodePort In(string name) => new() { Id = $"in:{name}", Name = name, Direction = PortDirection.Input, DataType = "any", Required = false };
    private static NodePort Out(string name) => new() { Id = $"out:{name}", Name = name, Direction = PortDirection.Output, DataType = "any" };

    private static VisualWorkflowConnection Wire(string from, string fromPort, string to, string toPort) =>
        new() { Id = $"{from}.{fromPort}->{to}.{toPort}", FromNodeId = from, FromPortId = $"out:{fromPort}", ToNodeId = to, ToPortId = $"in:{toPort}" };

    /// <summary>The workflow for one posting and one candidate, with the user's own steps (none by default).</summary>
    public static WorkflowDefinition Build(string postingKey, string employer, CandidateContext candidate, IReadOnlyList<CompiledStep>? steps = null)
    {
        steps ??= [];
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
                Inputs = [In("signal"), In("posting"), In("candidate")],
                Outputs = [Out("screened"), Out("assessment"), .. ScreenBrick.DecisionPorts.Select(Out)],
            },
            new BrickNode
            {
                Id = "recommend", Name = "Recommend (certified)", BrickId = Certified.CertifiedIds.Recommendation, Implementation = Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic,
                Inputs = [.. ScreenBrick.DecisionPorts.Select(In)], Outputs = [Out("recommendation"), Out("coveragePercent"), Out("reason")],
            },
            new BrickNode
            {
                Id = "decide", Name = "Apply the recommendation", BrickId = "jobsearch.decide", Implementation = Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic,
                Inputs =
                [
                    In("screened"), In("assessment"), In("recommendation"), In("reason"), In("posting"), In("candidate"), In("steps"),
                    .. steps.SelectMany(st => StepContract.Outputs(st.Kind).Select(o => In($"{o}.{st.Key}"))),
                ],
                Outputs = [Out("recommendation"), Out("assessment"), Out("draft")],
            },
            new BrickNode
            {
                Id = "tailor", Name = "Draft materials", BrickId = "jobsearch.tailor", Implementation = Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic,
                Inputs = [In("proceed"), In("posting"), In("assessment"), In("candidate")], Outputs = [Out("drafted"), Out("resume"), Out("coverLetter"), Out("coverLetterOrigin")],
            },
            new BrickNode
            {
                Id = "validate", Name = "Validate materials", BrickId = "jobsearch.validate-materials", Implementation = Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic,
                Inputs = [In("drafted"), In("resume"), In("coverLetter"), In("posting"), In("assessment"), In("candidate")],
                Outputs = [Out("validated"), Out("passed"), Out("resumeValidation"), Out("coverValidation"), Out("resumePdf"), Out("coverPdf")],
            },
        };
        // The user's steps: each gets the posting's fields and its own settings, and reports to the decide node.
        if (steps.Count > 0)
        {
            nodes.Add(new BrickNode
            {
                Id = "fields", Name = "Posting fields", BrickId = "jobsearch.posting-fields", Implementation = Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic,
                Inputs = [In("posting"), In("assessment")], Outputs = [.. StepContract.PostingInputs.Select(Out)],
            });
        }
        nodes.Add(new InputNode
        {
            Id = "steps-meta", Name = "Your steps", Type = InputType.Content, Outputs = [Out("data")],
            Content = Store.Json.Write(steps.Select(st => new DecideBrick.StepMeta(st.Key, st.Name, st.Kind)).ToList()),
        });
        foreach (var st in steps)
        {
            nodes.Add(new BrickNode
            {
                Id = StepNode(st), Name = st.Name, BrickId = st.BrickId, Implementation = Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic,
                Inputs = [.. StepContract.Inputs.Select(In)], Outputs = [.. StepContract.Outputs(st.Kind).Select(Out)],
            });
            for (var i = 0; i < StepContract.SettingInputs.Length; i++)
            {
                nodes.Add(new InputNode
                {
                    Id = $"{StepNode(st)}-{StepContract.SettingInputs[i]}", Name = $"{st.Name}: {StepContract.SettingInputs[i]}", Type = InputType.Content,
                    Content = i < st.Settings.Count ? st.Settings[i] : "", Outputs = [Out("data")],
                });
            }
        }

        List<VisualWorkflowConnection> wires =
        [
            Wire("posting-ref", "data", "get", "posting"), Wire("employer", "data", "get", "employer"),
            Wire("get", "signal", "screen", "signal"), Wire("get", "posting", "screen", "posting"), Wire("candidate", "data", "screen", "candidate"),
            .. ScreenBrick.DecisionPorts.Select(p => Wire("screen", p, "recommend", p)),
            Wire("screen", "screened", "decide", "screened"), Wire("screen", "assessment", "decide", "assessment"),
            Wire("recommend", "recommendation", "decide", "recommendation"), Wire("recommend", "reason", "decide", "reason"),
            Wire("get", "posting", "decide", "posting"), Wire("candidate", "data", "decide", "candidate"),
            Wire("decide", "draft", "tailor", "proceed"), Wire("get", "posting", "tailor", "posting"), Wire("decide", "assessment", "tailor", "assessment"),
            Wire("candidate", "data", "tailor", "candidate"),
            Wire("tailor", "drafted", "validate", "drafted"), Wire("tailor", "resume", "validate", "resume"), Wire("tailor", "coverLetter", "validate", "coverLetter"),
            Wire("get", "posting", "validate", "posting"), Wire("decide", "assessment", "validate", "assessment"), Wire("candidate", "data", "validate", "candidate"),
            Wire("steps-meta", "data", "decide", "steps"),
        ];
        if (steps.Count > 0)
        {
            wires.Add(Wire("get", "posting", "fields", "posting"));
            wires.Add(Wire("screen", "assessment", "fields", "assessment"));
        }
        foreach (var st in steps)
        {
            var node = StepNode(st);
            wires.AddRange(StepContract.PostingInputs.Select(f => Wire("fields", f, node, f)));
            wires.AddRange(StepContract.SettingInputs.Select(si => Wire($"{node}-{si}", "data", node, si)));
            wires.AddRange(StepContract.Outputs(st.Kind).Select(o => Wire(node, o, "decide", $"{o}.{st.Key}")));
        }
        return new WorkflowDefinition
        {
            Id = $"jobsearch.posting:{postingKey}", Name = "jobsearch: read, screen, decide (certified), draft, validate one posting",
            Description = "Per-posting pipeline of the job-search app (apps/job-search).", Nodes = nodes, Connections = wires,
        };
    }

    /// <summary>The node id of a user step.</summary>
    public static string StepNode(CompiledStep step) => $"user-{step.Key}";

    /// <summary>Executes the workflow and collects its outputs and a log line per node.</summary>
    public static async Task<PostingOutcome> RunAsync(WorkflowExecutor executor, string postingKey, string employer, CandidateContext candidate, CancellationToken ct,
        IReadOnlyList<CompiledStep>? steps = null)
    {
        var log = new List<string>();
        using var _ = executor.Events.Subscribe(new Observer(e =>
        {
            if (e is NodeCompletedEvent { Node: BrickNode n } done)
                lock (log) log.Add($"{n.BrickId}: {Describe(done.Result.Outputs)}");
        }));
        var result = await executor.ExecuteAsync(Build(postingKey, employer, candidate, steps), new WorkflowInput(), ct).ConfigureAwait(false);

        T? Out<T>(string node, string key) =>
            result.NodeResults.TryGetValue(node, out var r) && r.Outputs.TryGetValue(key, out var v) && v is T t ? t : default;

        return new PostingOutcome(
            Out<string>("get", "signal") ?? "error", Out<bool>("get", "provesOpen"), Out<string>("get", "detail") ?? "",
            Out<JobPosting>("get", "posting"), Out<Assessment>("decide", "assessment"),
            Out<bool>("tailor", "drafted"), Out<Draft>("tailor", "resume"), Out<Draft>("tailor", "coverLetter"),
            Out<ValidationRecord>("validate", "resumeValidation"), Out<ValidationRecord>("validate", "coverValidation"),
            Out<byte[]>("validate", "resumePdf"), Out<byte[]>("validate", "coverPdf"), log, Out<string>("tailor", "coverLetterOrigin"));
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
