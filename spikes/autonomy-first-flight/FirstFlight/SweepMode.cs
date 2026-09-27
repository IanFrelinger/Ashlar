using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.BackgroundAgents.Autonomy;
using Ashlar.BackgroundAgents.Objectives;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Autonomy;
using Ashlar.Infrastructure.Certification.HotSwap;
using Ashlar.Infrastructure.Certification.Sdk.Extensions;

namespace Ashlar.Spikes.FirstFlight;

/// <summary>
/// Drives ONE sweep of the standing loop against the real objective store — the path that
/// had never run end to end: store → sweep → witness load → proposal → certification →
/// outcome. Everything upstream of this was proven by hand-constructed candidates inside
/// a spike; this is the first time an objective FILE drives the loop.
///
/// <para>Runs with hold admission on, so nothing can swap whatever the verdict.</para>
/// </summary>
public static class SweepMode
{
    /// <summary>Runs one sweep and reports. Returns a process exit code.</summary>
    public static async Task<int> RunAsync(string objectivesRoot, string sessionImage)
    {
        Console.WriteLine("== autonomy loop: one sweep of the objective store ==");
        Console.WriteLine($"store : {objectivesRoot}");
        Console.WriteLine($"image : {sessionImage}");

        var store = new ObjectiveStore(objectivesRoot);
        var pending = store.List(ObjectiveStatus.Pending);
        Console.WriteLine($"pending objectives: {pending.Count}");
        foreach (var o in pending)
            Console.WriteLine($"  - {o.Id}  (source={o.Source}, priority={o.Priority})");

        if (pending.Count == 0)
        {
            Console.WriteLine("SWEEP: nothing to do — no pending objectives in the store");
            return 1;
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
        services.AddCertificationGate();
        services.AddAshlarAutonomy(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ashlar:Autonomy:Enabled"] = "true",
                ["Ashlar:Autonomy:UseSandboxSessions"] = "true",
                ["Ashlar:Autonomy:BuildCandidateInSession"] = "true",
                ["Ashlar:Autonomy:ExecuteCandidateInSession"] = "true",
                // The whole point of the first run: certify everything, admit nothing.
                ["Ashlar:Autonomy:HoldAdmission"] = "true",
                ["Ashlar:Autonomy:SessionImage"] = sessionImage,
                ["Ashlar:Autonomy:CadenceFloorSeconds"] = "0",
            })
            .Build());

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<AutonomousIterationHarness>();

        var settings = new AutonomyLoopSettings
        {
            IntervalSeconds = 0, // driven manually here, not on a timer
            SessionImage = sessionImage,
            MaxObjectivesPerSweep = 5,
            CompilationReferences = new[]
            {
                typeof(DomainBrick).Assembly.Location,
                typeof(BrickInput).Assembly.Location,
            },
        };

        // Campaign mode: a LIVE proposer inside the loop, so the loop's own repair channel
        // (policy-projected feedback, bounded attempts) is what runs — not a recorded proposal.
        // Every proposal, and the exact projected feedback the model was handed for a repair,
        // is recorded to the campaign directory: that is the evidence ledger's raw material.
        IProposalSource? proposals = null;
        var proposerKind = Environment.GetEnvironmentVariable("ASHLAR_SWEEP_PROPOSER");
        if (string.Equals(proposerKind, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            var options = new OllamaProposalOptions
            {
                BaseUrl = Environment.GetEnvironmentVariable("ASHLAR_OLLAMA_BASE_URL") ?? "http://host.docker.internal:11434",
                Model = Environment.GetEnvironmentVariable("ASHLAR_OLLAMA_MODEL") ?? "codellama:7b",
            };
            // Per-model dials, all optional: a 27B with CPU offload needs a longer timeout; a
            // thinking model needs think=false (or a much larger token budget) to reach the code.
            if (int.TryParse(Environment.GetEnvironmentVariable("ASHLAR_OLLAMA_MAX_TOKENS"), out var maxTokens) && maxTokens > 0)
                options.MaxTokens = maxTokens;
            if (int.TryParse(Environment.GetEnvironmentVariable("ASHLAR_OLLAMA_TIMEOUT_MINUTES"), out var timeoutMinutes) && timeoutMinutes > 0)
                options.Timeout = TimeSpan.FromMinutes(timeoutMinutes);
            if (double.TryParse(Environment.GetEnvironmentVariable("ASHLAR_OLLAMA_TEMPERATURE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var temperature))
                options.Temperature = temperature;
            if (bool.TryParse(Environment.GetEnvironmentVariable("ASHLAR_OLLAMA_THINK"), out var think))
                options.Think = think;
            Console.WriteLine($"proposer dials: max_tokens={options.MaxTokens} timeout={options.Timeout.TotalMinutes:F0}m temperature={options.Temperature} think={(options.Think is null ? "(model default)" : options.Think.ToString())}");
            // Operator preamble (house rules — here, the brick API a small model does not know).
            // Data the proposer is handed, never a witness; the same knob a deployment would set.
            var preamblePath = Environment.GetEnvironmentVariable("ASHLAR_OLLAMA_SYSTEM_PREAMBLE_FILE");
            if (!string.IsNullOrWhiteSpace(preamblePath) && File.Exists(preamblePath))
            {
                options.SystemPreamble = File.ReadAllText(preamblePath).Trim();
                Console.WriteLine($"preamble: {preamblePath} ({options.SystemPreamble.Length} chars)");
            }
            var campaignDir = Environment.GetEnvironmentVariable("ASHLAR_CAMPAIGN_DIR")
                ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(objectivesRoot))!, "campaign");
            Directory.CreateDirectory(campaignDir);
            var live = new OllamaProposalSource(
                new HttpClient(),
                options,
                provider.GetRequiredService<ILogger<OllamaProposalSource>>());
            proposals = new RecordingProposalSource(live, campaignDir);
            Console.WriteLine($"proposer: LIVE ollama {options.Model} @ {options.BaseUrl}  (recording to {campaignDir})");
        }
        else
        {
            Console.WriteLine("proposer: recorded proposals beside each objective (set ASHLAR_SWEEP_PROPOSER=ollama for live)");
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("ASHLAR_SWEEP_MAX_OBJECTIVES"), out var maxObjectives) && maxObjectives > 0)
            settings.MaxObjectivesPerSweep = maxObjectives;

        var loop = new AutonomyLoopService(
            store,
            harness,
            settings,
            provider.GetRequiredService<ILogger<AutonomyLoopService>>(),
            proposals);

        var started = DateTimeOffset.UtcNow;
        var outcome = await loop.SweepAsync();
        var attempted = outcome.Attempted;
        var elapsed = DateTimeOffset.UtcNow - started;

        Console.WriteLine();
        Console.WriteLine($"SWEEP: attempted {attempted} objective(s) in {elapsed.TotalSeconds:F1}s");

        // Attempting zero is a real answer, not a crash: it means every pending objective
        // was ineligible (no witness, or no proposal beside it).
        if (attempted == 0)
        {
            Console.WriteLine("SWEEP: no objective was eligible — check for a witness and proposal beside each one");
            return 1;
        }

        // An objective that was charged but reached no verdict is an INFRASTRUCTURE FAULT, not a
        // result, and it must not leave this process as success. Exit 2 is the script's
        // "attempted one, no verdict" code; it used to be inferred by grepping the log for a
        // warning template, because the exit code could not say it. Now it can.
        if (outcome.Failed > 0)
        {
            Console.WriteLine(
                $"SWEEP: {outcome.Failed} of {attempted} objective(s) reached no verdict — "
                + "infrastructure fault, not a result; refusing to report this as a pass");
            return 2;
        }

        Console.WriteLine("SWEEP: complete (see the iteration outcome logged above)");
        return 0;
    }

    /// <summary>
    /// Records what a proposer was asked and what it answered, per objective and attempt, as
    /// <c>{campaignDir}/{objectiveId}.attempt{N}.json</c>. The recording is downstream of the
    /// policy projection: the <c>feedback</c> field is exactly what the model saw, so a
    /// reviewer can check that no witness value reached it.
    /// </summary>
    private sealed class RecordingProposalSource : IProposalSource
    {
        private static readonly System.Text.Json.JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };

        private readonly IProposalSource _inner;
        private readonly string _dir;

        public RecordingProposalSource(IProposalSource inner, string dir)
        {
            _inner = inner;
            _dir = dir;
        }

        public async Task<ProposedSource?> ProposeAsync(ProposalRequest request, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ProposedSource? proposed = null;
            string? error = null;
            try
            {
                proposed = await _inner.ProposeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                throw;
            }
            finally
            {
                sw.Stop();
                var attempt = request.Repair?.Attempt ?? 0;
                var record = new
                {
                    objectiveId = request.ObjectiveId,
                    attempt,
                    mode = request.Repair is null ? "initial" : $"repair{attempt}",
                    proposedAt = DateTimeOffset.UtcNow,
                    durationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
                    feedback = request.Repair?.Feedback,
                    signature = proposed?.ProposerSignature,
                    typeName = proposed?.TypeName,
                    sourceCode = proposed?.SourceCode,
                    error,
                };
                var path = Path.Combine(_dir, $"{request.ObjectiveId}.attempt{attempt}.json");
                try
                {
                    File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(record, Json));
                    Console.WriteLine($"  recorded {Path.GetFileName(path)} ({record.durationSeconds}s{(proposed is null ? ", NO PROPOSAL" : string.Empty)})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  (could not record {path}: {ex.Message})");
                }
            }

            return proposed;
        }
    }
}
