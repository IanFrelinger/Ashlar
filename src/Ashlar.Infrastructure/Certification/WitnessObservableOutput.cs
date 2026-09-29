namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// The single view of a brick's output that every witness judge compares against.
///
/// <para><c>BrickOutput.ToDictionary()</c> returns only the keyed data; <c>Summary</c> is a
/// separate property. Until now every judge — in-process witness, in-process mutant runner,
/// and the session runner's observation shape — compared against the dictionary alone, so
/// <b>no witness could observe the summary at all</b>. That left a whole class of mutants
/// (a mutated summary string literal) unkillable by any witness expressible in the
/// language: the gate correctly said "your witness cannot distinguish this", but no witness
/// could. The first model-proposed dogfood candidate was blocked at the mutation leg by
/// exactly that survivor.</para>
///
/// <para>The fix is to make the summary witnessable under a reserved key,
/// <see cref="SummaryKey"/>, mirroring <c>BrickOutputSerializer</c>, which already treats
/// the summary as part of the canonical output for determinism. A witness that wants to
/// pin the summary writes <c>"$summary": "..."</c> in its expected output; one that does
/// not simply omits it, exactly as for any other key. The reserved prefix keeps it from
/// colliding with declared brick keys, and it is deliberately NOT counted as an undeclared
/// write by the swap host's contract leg.</para>
/// </summary>
internal static class WitnessObservableOutput
{
    /// <summary>Reserved witness key under which a brick's summary is observable.</summary>
    public const string SummaryKey = "$summary";

    /// <summary>Projects an output into the witness-observable view: keyed data plus the summary.</summary>
    public static IReadOnlyDictionary<string, object> Project(
        IReadOnlyDictionary<string, object> data, string? summary)
    {
        if (summary is null)
            return data;

        var view = new Dictionary<string, object>(data, StringComparer.Ordinal)
        {
            [SummaryKey] = summary,
        };
        return view;
    }
}
