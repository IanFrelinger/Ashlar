namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// A tool that labels everything its result carries. The agent that holds the read scope accepts
/// <see cref="ReportRead"/> only from a tool that implements this. Any other tool's read stays unreported
/// and is observed as <see cref="SecurityLabel.SystemHigh"/> when the scope ends.
/// </summary>
/// <remarks>
/// The scope is not ambient. A tool cannot reach it except through this method, and the holder decides
/// which tools may call it. In PR 4 the only labelled tool is <c>RAGTool</c>.
/// </remarks>
public interface IEgressLabelledTool
{
    /// <summary>
    /// Reports the label of everything <paramref name="result"/> carries into <paramref name="read"/>, before
    /// the holder completes the scope. A read that returned nothing reports <see cref="SecurityLabel.Public"/>.
    /// </summary>
    /// <param name="read">The scope the holder began around this tool call. Still open.</param>
    /// <param name="result">What the tool returned.</param>
    void ReportRead(ReadScope read, ToolResult result);
}
