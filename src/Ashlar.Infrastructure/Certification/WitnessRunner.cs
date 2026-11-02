using System.Text.Json;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Infrastructure.Certification;

/// <summary>Executes witness specifications against certified brick implementations.</summary>
internal static class WitnessRunner
{
    /// <summary>
    /// Judges one unit's raw backend observations with EXACTLY the semantics and failure
    /// strings of <see cref="RunAsync"/> — the gate decides, the backend only ran. Only
    /// repeat 0 is judged (later repeats exist for determinism comparison).
    /// </summary>
    public static WitnessRunResult JudgeObservations(
        WitnessSpec witness,
        IReadOnlyList<CandidateCaseObservation> observations)
    {
        var failures = new List<string>();
        var findings = new List<WitnessFinding>();

        foreach (var (caseIndex, witnessCase) in witness.Cases.Select((c, i) => (i, c)))
        {
            var observation = observations.FirstOrDefault(o => o.CaseIndex == caseIndex && o.Repeat == 0);
            if (observation is null)
            {
                failures.Add($"case {caseIndex}: the execution backend returned no observation");
                findings.Add(new WitnessFinding(caseIndex, WitnessFindingKind.NoObservation));
                continue;
            }

            if (observation.Threw)
            {
                failures.Add($"case {caseIndex}: execution threw {observation.Error}");
                findings.Add(new WitnessFinding(caseIndex, WitnessFindingKind.Threw, Detail: observation.Error));
                continue;
            }

            var outputs = ProjectObservation(observation);
            foreach (var (key, expected) in witnessCase.ExpectedOutput)
            {
                if (!outputs.TryGetValue(key, out var actual) || actual is null)
                {
                    failures.Add($"case {caseIndex}: missing output key '{key}'");
                    findings.Add(new WitnessFinding(caseIndex, WitnessFindingKind.MissingKey, key, FormatValue(expected)));
                    continue;
                }

                if (!ValuesEqual(expected, actual))
                {
                    failures.Add(
                        $"case {caseIndex}: output['{key}'] expected {FormatValue(expected)} got {FormatValue(actual)}");
                    findings.Add(new WitnessFinding(
                        caseIndex, WitnessFindingKind.Mismatch, key, FormatValue(expected), FormatValue(actual)));
                }
            }
        }

        return new WitnessRunResult(failures.Count == 0, failures, findings);
    }

    /// <summary>
    /// The witness-observable view of a session observation: keyed outputs plus the summary
    /// under <see cref="WitnessObservableOutput.SummaryKey"/> — the same projection the
    /// in-process judges apply, so a witness sees one shape regardless of where the
    /// candidate executed.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ProjectObservation(CandidateCaseObservation observation)
    {
        var view = new Dictionary<string, object?>(
            observation.Outputs ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
        if (observation.Summary is not null)
            view[WitnessObservableOutput.SummaryKey] = observation.Summary;
        return view;
    }

    /// <summary>
    /// Judges a MUTANT unit's observations with exactly the in-process mutant semantics
    /// (<c>MutantWitnessExecutor</c>): any throw, missing key, or mismatch kills. Actual
    /// values are unwrapped from their JSON transport shape by the shared comparer so a
    /// transport artifact can never masquerade as a kill — a vacuous kill is the exact
    /// failure mode the mutation gate exists to prevent.
    /// </summary>
    public static bool JudgeMutantObservations(
        WitnessSpec witness,
        IReadOnlyList<CandidateCaseObservation> observations)
    {
        foreach (var (caseIndex, witnessCase) in witness.Cases.Select((c, i) => (i, c)))
        {
            var observation = observations.FirstOrDefault(o => o.CaseIndex == caseIndex && o.Repeat == 0);
            if (observation is null || observation.Threw || observation.Outputs is null)
                return false;

            foreach (var (key, expected) in witnessCase.ExpectedOutput)
            {
                if (!ProjectObservation(observation).TryGetValue(key, out var actual) || actual is null)
                    return false;

                if (!WitnessValueComparer.AreEqual(expected, actual))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Canonicalizes one observation for determinism comparison: summary plus sorted
    /// outputs. Self-consistent by construction — both sides of the comparison come from
    /// the same backend and pass through here.
    /// </summary>
    public static string CanonicalizeObservation(CandidateCaseObservation observation)
    {
        // Positional envelope, not a merged dictionary: an output key literally named
        // "summary" must not be able to shadow the summary itself.
        var outputs = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in observation.Outputs ?? new Dictionary<string, object?>())
        {
            outputs[key] = value is JsonElement el
                ? el.GetRawText()
                : JsonSerializer.Serialize(value);
        }

        return JsonSerializer.Serialize(new object?[] { observation.Summary, observation.Threw, outputs });
    }

    /// <summary>Run asynchronously.</summary>
    public static async Task<WitnessRunResult> RunAsync(
        DomainBrick brick,
        WitnessSpec witness,
        IExecutionContext context,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var findings = new List<WitnessFinding>();

        foreach (var (caseIndex, witnessCase) in witness.Cases.Select((c, i) => (i, c)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = new BrickInput(witnessCase.Input);
            BrickOutput output;
            try
            {
                output = await brick.ExecuteAsync(
                    input,
                    ImplementationType.Deterministic,
                    context,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add($"case {caseIndex}: execution threw {ex.Message}");
                findings.Add(new WitnessFinding(caseIndex, WitnessFindingKind.Threw, Detail: ex.Message));
                continue;
            }

            var observable = WitnessObservableOutput.Project(output.ToDictionary(), output.Summary);
            foreach (var (key, expected) in witnessCase.ExpectedOutput)
            {
                if (!observable.TryGetValue(key, out var actual))
                {
                    failures.Add($"case {caseIndex}: missing output key '{key}'");
                    findings.Add(new WitnessFinding(caseIndex, WitnessFindingKind.MissingKey, key, FormatValue(expected)));
                    continue;
                }

                if (!ValuesEqual(expected, actual))
                {
                    failures.Add(
                        $"case {caseIndex}: output['{key}'] expected {FormatValue(expected)} got {FormatValue(actual)}");
                    findings.Add(new WitnessFinding(
                        caseIndex, WitnessFindingKind.Mismatch, key, FormatValue(expected), FormatValue(actual)));
                }
            }
        }

        return new WitnessRunResult(failures.Count == 0, failures, findings);
    }

    /// <summary>Runs the witness twice and compares serialized outputs for determinism.</summary>
    public static async Task<(bool Identical, string? First, string? Second)> CheckDeterminismAsync(
        DomainBrick brick,
        WitnessSpec witness,
        CancellationToken cancellationToken)
    {
        if (witness.Cases.Count == 0)
            return (true, null, null);

        var auditContext = new AuditExecutionContext();
        var probe = witness.Cases[0];
        var input = new BrickInput(probe.Input);

        var first = await brick.ExecuteAsync(
            input,
            ImplementationType.Deterministic,
            auditContext,
            cancellationToken).ConfigureAwait(false);
        var second = await brick.ExecuteAsync(
            input,
            ImplementationType.Deterministic,
            auditContext,
            cancellationToken).ConfigureAwait(false);

        var firstJson = BrickOutputSerializer.ToCanonicalJson(first);
        var secondJson = BrickOutputSerializer.ToCanonicalJson(second);
        return (firstJson == secondJson, firstJson, secondJson);
    }

    private static bool ValuesEqual(object expected, object actual) =>
        WitnessValueComparer.AreEqual(expected, actual);

    /// <summary>
    /// Renders a witness value for a failure message. Distinguishing null from the empty
    /// string matters more than it looks: this text is the repair feedback a proposer sees,
    /// and "expected  got " (which is what Convert.ToString produces for both, since it
    /// returns "" for null and JsonElement renders JsonValueKind.Null as empty) tells a
    /// proposer nothing it can act on. Strings are quoted so an empty one is visible.
    /// </summary>
    private static string FormatValue(object? value)
    {
        if (value is null)
            return "<null>";

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => "<null>",
                JsonValueKind.String => Quote(element.GetString()),
                _ => element.GetRawText(),
            };
        }

        return value is string text
            ? Quote(text)
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";
    }

    private static string Quote(string? value) => value is null ? "<null>" : $"\"{value}\"";
}
