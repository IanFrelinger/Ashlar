using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Certification.Ports;

namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// Roslyn-backed <see cref="IExtensionCompileCheck"/>: compiles a proposal's <c>.cs</c> files
/// in-process (no .NET SDK), so an admission decision can rest on a REAL <c>build</c> course instead
/// of a self-reported one.
///
/// <para><b>The reference set is declared, not ambient.</b> It comes from
/// <see cref="CertifierReferenceSet"/> — the shared framework this host was launched with, plus the
/// named brick-authoring anchors — and is therefore a function of the deployment rather than of what
/// the process happened to load first. It used to be <c>AppDomain.CurrentDomain.GetAssemblies()</c>,
/// and the doc here used to call that "a conservative, fail-closed outcome, never a false
/// admission". Only half of that was true and the half that was false was the important one: a host
/// that had loaded a type for its own reasons compiled a proposal naming it, so identical bytes
/// could pass in a warm process and fail in a cold one. See <see cref="CertifierReferenceSet"/> for
/// what changed and why the node's own output directory is deliberately not part of the set.</para>
///
/// <para><b>Honest scope.</b> A pass means "these files compile in isolation against the shipped
/// authoring surface" — not "the node still builds". This compiles the proposal's <c>.cs</c> files as
/// a standalone assembly with no project context: no implicit usings, no analyzers, no source
/// generators, nothing loaded or executed. It does not rebuild the project or run tests.</para>
///
/// <para><b>How load-bearing the verdict is.</b> The <c>build</c> course this produces gates
/// admission only when the operator's policy lists <c>build</c> in <c>selfExtend.gatesRequired</c> —
/// that is the operator's choice, and the scaffold ships an empty list. Independently of the policy
/// the course is recorded on the signed, append-once proposal record, so a wrong verdict is
/// unrepairable history whether or not it blocked anything on the day.</para>
///
/// <para>A host that cannot supply a declared reference set (single-file, trimmed, or
/// ahead-of-time published) makes <see cref="CertifierReferenceSet.Shared"/> throw, and that throw
/// propagates out of <see cref="CheckAsync"/> deliberately: the caller records it as
/// <c>compile check errored</c>, which is fail-closed AND distinguishable from "the proposal does
/// not compile". A verifier fault must never be recorded as a fact about the change.</para>
/// </summary>
public sealed class RoslynExtensionCompileCheck : IExtensionCompileCheck
{
    private readonly ILogger<RoslynExtensionCompileCheck>? _logger;

    /// <summary>Creates the compile check.</summary>
    public RoslynExtensionCompileCheck(ILogger<RoslynExtensionCompileCheck>? logger = null) => _logger = logger;

    /// <inheritdoc />
    public Task<ExtensionCompileCheckResult> CheckAsync(
        IReadOnlyList<ProposedFileContent> files,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var csFiles = files
                .Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (csFiles.Count == 0)
            {
                return new ExtensionCompileCheckResult(true, "no .cs files in the proposal — nothing to compile");
            }

            var trees = csFiles
                .Select(f => CSharpSyntaxTree.ParseText(f.Content, path: f.Path, cancellationToken: cancellationToken))
                .ToList();

            // Resolved BEFORE the compilation so a host that cannot declare a reference set throws
            // out of here rather than producing a verdict against a partial one.
            var references = CertifierReferenceSet.Shared;

            var compilation = CSharpCompilation.Create(
                "ashlar-proposal-" + Guid.NewGuid().ToString("N"),
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms, cancellationToken: cancellationToken);
            var errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            if (emit.Success)
            {
                return new ExtensionCompileCheckResult(
                    true,
                    $"{csFiles.Count} file(s) compiled clean ({CertifierReferenceSet.Describe(references)})");
            }

            var shown = string.Join("; ", errors.Take(3).Select(e => e.GetMessage()));
            _logger?.LogInformation("Proposal compile check failed: {ErrorCount} error(s)", errors.Count);
            return new ExtensionCompileCheckResult(
                false,
                $"{errors.Count} compile error(s): {Truncate(shown, 300)} "
                + $"({CertifierReferenceSet.Describe(references)})");
        }, cancellationToken);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
