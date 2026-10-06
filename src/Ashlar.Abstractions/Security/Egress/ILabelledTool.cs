namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// A tool that declares itself labelled: invoked through <see cref="InvokeLabelledAsync"/>, it reports through the
/// caller's <see cref="ReadReporter"/> a label for everything its result carries. Implementing this interface is the
/// declaration.
/// </summary>
/// <remarks>
/// <para>An agent that scopes each tool call as a read (SPEC-007) accepts a report only from a tool that implements
/// this interface, and hands the scope to nothing else: every other tool's result counts as
/// <see cref="SecurityLabel.SystemHigh"/>, the unreported-read rule. A labelled tool reports a label for all of its
/// result, and <see cref="SecurityLabel.Public"/> when it read nothing. It is handed a report-only surface, never the
/// <see cref="ReadScope"/>: the scope, and so completing or ending the read, belongs to its caller, so a tool that
/// reports and then throws still counts as <see cref="SecurityLabel.SystemHigh"/>. A report that is too low is a write-down with no downgrade once the guard enforces, so
/// a tool that cannot label every part of its result must not implement this.</para>
/// <para>The declaration is the tool's own. A decorator that wraps a labelled tool, or a toolbox that hides which tool
/// serves a call, is not labelled, so its calls count as <see cref="SecurityLabel.SystemHigh"/> (fail closed). While
/// the read has not ended, the tool's own egress is decided at <see cref="SecurityLabel.SystemHigh"/> like any
/// other tool's. Only <c>RAGTool</c> implements this in production, which a cert-gate convention pins; a host's own labelled
/// tool is the host's trusted base, as its <c>IEgressGuard</c> is.</para>
/// </remarks>
public interface ILabelledTool : ITool
{
    /// <summary>
    /// Invokes the tool as <see cref="ITool.InvokeAsync"/> does, and reports through <paramref name="report"/> a label
    /// for everything the result carries.
    /// </summary>
    /// <param name="toolCall">The tool call containing arguments.</param>
    /// <param name="s">The current world snapshot.</param>
    /// <param name="report">The caller's report channel for this read; it can report and nothing else.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tool result containing the action delta and optional payload.</returns>
    Task<ToolResult> InvokeLabelledAsync(ToolCall toolCall, WorldSnapshot s, ReadReporter report, CancellationToken ct);
}
