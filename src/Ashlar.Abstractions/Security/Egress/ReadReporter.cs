namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The report channel a labelled reader is handed for one read: <see cref="Report"/> and nothing else. It forwards to
/// the caller's <see cref="ReadScope"/>, which alone can complete or end the read.
/// </summary>
/// <remarks>
/// A read counts at what it reported only when it returned and reported (the completion rule), so the code that awaits
/// the read must be the only code that can complete it: a reader that could complete the scope and then throw would
/// have its reports counted, where SPEC-007 requires <see cref="SecurityLabel.SystemHigh"/>. This type has no public
/// constructor and exposes no way to reach the scope; the scope hands one out as <see cref="ReadScope.Reporter"/>.
/// </remarks>
public sealed class ReadReporter
{
    private readonly ReadScope _scope;

    internal ReadReporter(ReadScope scope)
    {
        _scope = scope;
    }

    /// <summary>
    /// Reports that the read returned data labelled <paramref name="label"/>: see <see cref="ReadScope.Report"/>.
    /// </summary>
    /// <param name="label">The label of what the read returned.</param>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> is <see langword="null"/>.</exception>
    public void Report(SecurityLabel label) => _scope.Report(label);
}
