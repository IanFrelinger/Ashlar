using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ashlar.Analyzers.Tests;

/// <summary>
/// Shared harness for the trust-loop analyzer catalog tests: compiles a real sample against the
/// real contract assemblies, asserts the sample itself is valid C# (a non-compiling sample
/// yields zero analyzer diagnostics and would read as a pass), then runs one analyzer.
/// </summary>
internal static class TrustLoopAnalyzerHarness
{
    /// <summary>Builds a compilable sample brick around the interesting bits.</summary>
    public static string BrickSource(
        string body = "",
        string ctorBody = "",
        string extraMembers = "",
        string extraUsings = "") => $$"""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        {{extraUsings}}
        using Ashlar.Core.Domain.Bricks;
        using Ashlar.Core.Domain.Execution;

        public sealed class SampleBrick : Brick
        {
            {{extraMembers}}

            public SampleBrick()
            {
                Interface = new BrickInterface { Inputs = [], Outputs = [] };
                {{ctorBody}}
            }

            public override Task<BrickOutput> ExecuteAsync(
                BrickInput input, ImplementationType implementation,
                IExecutionContext context, CancellationToken cancellationToken = default)
            {
                var output = new BrickOutput();
                {{body}}
                return Task.FromResult(output);
            }
        }
        """;

    /// <summary>Compiles <paramref name="source"/> and returns one analyzer's diagnostics.</summary>
    public static async Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(
        string source,
        DiagnosticAnalyzer analyzer,
        params Type[] referenceAnchors)
    {
        var compilation = CSharpCompilation.Create(
            "TrustLoopAnalyzerSample",
            new[] { CSharpSyntaxTree.ParseText(source) },
            ReferenceSet(referenceAnchors),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Disable));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        errors.Should().BeEmpty(
            "the sample must compile, otherwise the analyzer result is meaningless: "
            + string.Join(" | ", errors.Select(e => e.ToString())));

        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer));
        return (await withAnalyzers.GetAnalyzerDiagnosticsAsync()).ToArray();
    }

    /// <summary>
    /// The contract assembly plus any test-specific anchors (e.g. the DI assemblies for
    /// registration samples). See <see cref="AnalyzerReferenceSet"/> for why the set is built
    /// from declared inputs rather than from the test host's loaded-assembly list.
    /// </summary>
    private static IReadOnlyList<MetadataReference> ReferenceSet(Type[] anchors)
        => AnalyzerReferenceSet.For(
            anchors.Append(typeof(Ashlar.Core.Domain.Bricks.Brick)).ToArray());
}
