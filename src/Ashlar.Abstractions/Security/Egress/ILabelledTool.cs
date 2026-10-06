namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// A tool that declares itself labelled: invoked through <see cref="InvokeLabelledAsync"/>, it reports to the
/// caller's <see cref="ReadScope"/> a label for everything its result carries. Implementing this interface is the
/// declaration.
/// </summary>
/// <remarks>
/// <para>An agent that scopes each tool call as a read (SPEC-007) accepts a report only from a tool that implements
/// this interface, and hands the scope to nothing else: every other tool's result counts as
/// <see cref="SecurityLabel.SystemHigh"/>, the unreported-read rule. A labelled tool reports a label for all of its
/// result, and <see cref="SecurityLabel.Public"/> when it read nothing; it never completes or disposes the scope,
/// which belongs to its caller. A report that is too low is a write-down with no downgrade once the guard enforces, so
/// a tool that cannot label every part of its result must not implement this.</para>
/// <para>The declaration is the tool's own. A decorator that wraps a labelled tool, or a toolbox that hides which tool
/// serves a call, is not labelled, so its calls count as <see cref="SecurityLabel.SystemHigh"/> (fail closed). While
/// the read has not ended, the tool's own egress is decided at <see cref="SecurityLabel.SystemHigh"/> like any
/// other tool's.</para>
/// </remarks>
public interface ILabelledTool : ITool
{
    /// <summary>
    /// Invokes the tool as <see cref="ITool.InvokeAsync"/> does, and reports to <paramref name="read"/> a label for
    /// everything the result carries.
    /// </summary>
    /// <param name="toolCall">The tool call containing arguments.</param>
    /// <param name="s">The current world snapshot.</param>
    /// <param name="read">The caller's read scope for this call. Report to it; never complete or dispose it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tool result containing the action delta and optional payload.</returns>
    Task<ToolResult> InvokeLabelledAsync(ToolCall toolCall, WorldSnapshot s, ReadScope read, CancellationToken ct);
}
