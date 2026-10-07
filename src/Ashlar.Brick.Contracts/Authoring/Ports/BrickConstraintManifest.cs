using System.Text;

namespace Ashlar.Core.Domain.Bricks.Ports;

/// <summary>
/// Machine-checkable structural constraints for one brick class (trust-loop spec R3.5).
/// One manifest, used twice: rendered into the proposer's instructions
/// (<see cref="RenderInstructions"/>) AND enforced as a pre-gate on candidate ingest —
/// so the prompt and the gate can never drift apart. Every rule is optional; an unset
/// rule is not checked and not rendered.
/// </summary>
public sealed class BrickConstraintManifest
{
    /// <summary>Base type the candidate's class must derive from (e.g. <c>DomainBrick</c>).</summary>
    public string? RequiredBaseType { get; init; }

    /// <summary>
    /// Allowlist of permitted <c>using</c> directive bodies (exact text between
    /// <c>using</c> and <c>;</c>). Empty list = usings are unrestricted.
    /// </summary>
    public IReadOnlyList<string> AllowedUsings { get; init; } = Array.Empty<string>();

    /// <summary>Exact namespace the candidate must declare. A candidate without a namespace is rejected, never inferred.</summary>
    public string? RequiredNamespace { get; init; }

    /// <summary>Suffix the candidate's primary class name must end with (e.g. <c>Brick</c>).</summary>
    public string? RequiredClassNameSuffix { get; init; }

    /// <summary>Denylisted API tokens (e.g. <c>DateTime.Now</c>, <c>Random</c>); any occurrence rejects the candidate.</summary>
    public IReadOnlyList<string> ForbiddenApiTokens { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Denylisted namespaces (e.g. <c>System.IO</c>): any reference to a symbol inside one of
    /// these namespaces (or a sub-namespace) rejects the candidate. Enforced semantically by the
    /// certification analyzer gate (extension spec A2.2) — resolved symbols, not text — so
    /// fully-qualified calls and aliases cannot dodge it.
    /// </summary>
    public IReadOnlyList<string> ForbiddenNamespaces { get; init; } = Array.Empty<string>();

    /// <summary>Declarations the candidate must contain verbatim (e.g. <c>override ExecuteAsync</c>).</summary>
    public IReadOnlyList<string> RequiredDeclarations { get; init; } = Array.Empty<string>();

    /// <summary>Instruction line for <see cref="RequiredBaseType"/>.</summary>
    public string BaseTypeInstruction() => $"Inherit {RequiredBaseType}.";

    /// <summary>Instruction line for <see cref="AllowedUsings"/>.</summary>
    public string UsingsInstruction() => $"Use only these usings: {string.Join("; ", AllowedUsings.Select(u => $"using {u};"))}";

    /// <summary>Instruction line for <see cref="RequiredNamespace"/>.</summary>
    public string NamespaceInstruction() => $"Declare namespace {RequiredNamespace}.";

    /// <summary>Instruction line for <see cref="RequiredClassNameSuffix"/>.</summary>
    public string ClassNameInstruction() => $"The class name must end with {RequiredClassNameSuffix}.";

    /// <summary>Instruction line for one entry of <see cref="ForbiddenApiTokens"/>.</summary>
    public string ForbiddenApiInstruction(string token) => $"Never use {token}.";

    /// <summary>Instruction line for one entry of <see cref="ForbiddenNamespaces"/>.</summary>
    public string ForbiddenNamespaceInstruction(string ns) => $"Never reference anything from the {ns} namespace.";

    /// <summary>Instruction line for one entry of <see cref="RequiredDeclarations"/>.</summary>
    public string RequiredDeclarationInstruction(string declaration) => $"The artifact must contain: {declaration}";

    /// <summary>
    /// Renders the configured rules as proposer instructions, one bullet per rule.
    /// This is the exact text the pre-gate restates verbatim on violation (spec R3.4),
    /// which is what keeps instructions and enforcement from drifting.
    /// </summary>
    public string RenderInstructions()
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(RequiredBaseType))
            builder.Append("- ").AppendLine(BaseTypeInstruction());
        if (AllowedUsings.Count > 0)
            builder.Append("- ").AppendLine(UsingsInstruction());
        if (!string.IsNullOrWhiteSpace(RequiredNamespace))
            builder.Append("- ").AppendLine(NamespaceInstruction());
        if (!string.IsNullOrWhiteSpace(RequiredClassNameSuffix))
            builder.Append("- ").AppendLine(ClassNameInstruction());
        foreach (var token in ForbiddenApiTokens)
            builder.Append("- ").AppendLine(ForbiddenApiInstruction(token));
        foreach (var ns in ForbiddenNamespaces)
            builder.Append("- ").AppendLine(ForbiddenNamespaceInstruction(ns));
        foreach (var declaration in RequiredDeclarations)
            builder.Append("- ").AppendLine(RequiredDeclarationInstruction(declaration));
        return builder.ToString().TrimEnd('\r', '\n');
    }
}
