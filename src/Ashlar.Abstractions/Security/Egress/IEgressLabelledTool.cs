namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// A tool that labels everything its result carries. The agent that holds the read scope accepts
/// <see cref="ReportRead"/> only from a tool that implements this. Any other tool's read stays unreported
/// and is observed as <see cref="SecurityLabel.SystemHigh"/> when the scope ends.
/// </summary>
/// <remarks>
/// The scope is not ambient and is never handed to a tool. The holder accepts reports through a
/// <see cref="ReadReporter"/>, which cannot complete or dispose the scope. In PR 4 the only labelled
/// production tool is <c>RAGTool</c>; host implementations are part of the host's trusted base.
/// </remarks>
public interface IEgressLabelledTool
{
    /// <summary>
    /// Reports the label of everything <paramref name="result"/> carries into <paramref name="read"/>, before
    /// the holder completes the scope. A read that returned nothing reports <see cref="SecurityLabel.Public"/>.
    /// </summary>
    /// <param name="read">The report-only channel for the still-open scope around this tool call.</param>
    /// <param name="result">What the tool returned.</param>
    void ReportRead(ReadReporter read, ToolResult result);
}
