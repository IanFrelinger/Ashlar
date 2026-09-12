using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Ashlar.Analyzers;
using Xunit;

namespace Ashlar.Analyzers.Tests;

/// <summary>
/// Drives <see cref="BrickInterfaceDriftAnalyzer"/> over hand-written sample
/// bricks compiled against the real Ashlar.Brick.Contracts types.
///
/// The two halves that matter are equally weighted: the rule must FIRE on the
/// drift it exists to catch, and must stay SILENT on everything it cannot
/// actually prove. An analyzer that cries wolf on computed keys gets suppressed
/// project-wide within a week, and then it protects nothing.
/// </summary>
public sealed class BrickInterfaceDriftAnalyzerTests
{
    // ------------------------------------------------------- it must fire

    [Fact]
    public async Task Flags_an_input_key_the_brick_never_declared()
    {
        // The original drift bug: interface says "preferScaffold", code reads
        // "preferScaffolding". The read silently returns the default forever.
        var diagnostics = await AnalyzeAsync(Brick(
            inputs: """new BrickInputDefinition("preferScaffold", "bool", "d")""",
            body: """var v = input.Get<bool>("preferScaffolding", false);"""));

        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic.Id.Should().Be(BrickInterfaceDriftAnalyzer.UndeclaredInputId);
        diagnostic.GetMessage().Should().Contain("preferScaffolding");
        diagnostic.GetMessage().Should().Contain("preferScaffold",
            "the message must show what WAS declared so the typo is obvious");
    }

    [Fact]
    public async Task Flags_an_output_key_the_brick_never_declared()
    {
        var diagnostics = await AnalyzeAsync(Brick(
            outputs: """new BrickOutputDefinition("result", "string", "d")""",
            body: """output.Set("qtShims", "value");"""));

        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic.Id.Should().Be(BrickInterfaceDriftAnalyzer.UndeclaredOutputId);
        diagnostic.GetMessage().Should().Contain("qtShims");
    }

    [Fact]
    public async Task Flags_every_undeclared_key_not_merely_the_first()
    {
        var diagnostics = await AnalyzeAsync(Brick(
            body: """
                  var a = input.Get<string>("ghostOne", null);
                  var b = input.Get<string>("ghostTwo", null);
                  output.Set("ghostThree", "x");
                  """));

        diagnostics.Select(d => d.Id).Should().BeEquivalentTo(new[]
        {
            BrickInterfaceDriftAnalyzer.UndeclaredInputId,
            BrickInterfaceDriftAnalyzer.UndeclaredInputId,
            BrickInterfaceDriftAnalyzer.UndeclaredOutputId
        });
    }

    // ------------------------------------------------------ it must not

    [Fact]
    public async Task Clean_brick_that_declares_everything_it_touches_is_silent()
    {
        var diagnostics = await AnalyzeAsync(Brick(
            inputs: """new BrickInputDefinition("target", "string", "d")""",
            outputs: """new BrickOutputDefinition("result", "string", "d")""",
            body: """
                  var v = input.Get<string>("target", null);
                  output.Set("result", "ok");
                  """));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Non_literal_keys_are_never_flagged()
    {
        // A computed/injected key cannot be checked without guessing, and a
        // guess here would be a false positive. Silence is the correct answer.
        var diagnostics = await AnalyzeAsync(Brick(
            body: """
                  var dynamicKey = System.Guid.NewGuid().ToString();
                  var a = input.Get<string>(dynamicKey, null);
                  output.Set(dynamicKey, "x");
                  var fromField = _field;
                  var b = input.Get<string>(fromField, null);
                  var interpolated = $"prefix{dynamicKey}";
                  output.Set(interpolated, "x");
                  """,
            extraMembers: """private readonly string _field = "whatever";"""));

        diagnostics.Should().BeEmpty(
            "the analyzer must judge only keys it can resolve at compile time");
    }

    [Fact]
    public async Task Const_keys_are_resolved_on_both_sides()
    {
        // The ProvenanceOutputKey pattern: the declaration and the write both go
        // through a const. Treating consts as opaque would false-positive on the
        // framework's own bricks.
        var diagnostics = await AnalyzeAsync(Brick(
            outputs: "new BrickOutputDefinition(ResultKey, \"string\", \"d\")",
            body: """output.Set(ResultKey, "ok");""",
            extraMembers: """private const string ResultKey = "result";"""));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Out_of_order_named_argument_declarations_are_recognised()
    {
        // Resolving the name by POSITION would read "string" as the declared name
        // here and then flag the perfectly correct read below.
        var diagnostics = await AnalyzeAsync(Brick(
            inputs: """new BrickInputDefinition(type: "string", name: "target")""",
            outputs: """new BrickOutputDefinition(description: "d", type: "string", name: "result")""",
            body: """
                  var v = input.Get<string>("target", null);
                  output.Set("result", "ok");
                  """));

        diagnostics.Should().BeEmpty(
            "the declared name is whatever binds to the 'name' parameter, not whatever comes first");
    }

    [Fact]
    public async Task A_type_that_declares_no_BrickInterface_is_skipped_entirely()
    {
        // Contract lives somewhere the analyzer cannot see. Reporting here would
        // be guessing, so the whole type is left alone.
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Ashlar.Core.Domain.Bricks;
            using Ashlar.Core.Domain.Execution;

            public sealed class NoContractBrick : Brick
            {
                public override Task<BrickOutput> ExecuteAsync(
                    BrickInput input, ImplementationType implementation,
                    IExecutionContext context, CancellationToken cancellationToken = default)
                {
                    var v = input.Get<string>("anything", null);
                    var output = new BrickOutput();
                    output.Set("whatever", "x");
                    return Task.FromResult(output);
                }
            }
            """;

        (await AnalyzeAsync(source)).Should().BeEmpty();
    }

    [Fact]
    public async Task Non_brick_types_are_ignored()
    {
        const string source = """
            using Ashlar.Core.Domain.Execution;

            public sealed class NotABrick
            {
                public void Poke(BrickInput input, BrickOutput output)
                {
                    var v = input.Get<string>("undeclared", null);
                    output.Set("alsoUndeclared", "x");
                }
            }
            """;

        (await AnalyzeAsync(source)).Should().BeEmpty(
            "the rule is about brick contracts, not about every dictionary access");
    }

    // ------------------------------------------------------------ harness

    /// <summary>Builds a compilable sample brick around the interesting bits.</summary>
    private static string Brick(
        string inputs = "",
        string outputs = "",
        string body = "",
        string extraMembers = "") => $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using Ashlar.Core.Domain.Bricks;
            using Ashlar.Core.Domain.Execution;

            public sealed class SampleBrick : Brick
            {
                {{extraMembers}}

                public SampleBrick()
                {
                    Interface = new BrickInterface
                    {
                        Inputs = [{{inputs}}],
                        Outputs = [{{outputs}}]
                    };
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

    private static async Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string source)
    {
        var compilation = CSharpCompilation.Create(
            "AnalyzerSample",
            new[] { CSharpSyntaxTree.ParseText(source) },
            ReferenceSet(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Disable));

        // A sample that does not compile would produce zero analyzer diagnostics
        // and read as a pass. Fail loudly instead.
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        errors.Should().BeEmpty(
            "the sample brick must compile, otherwise the analyzer result is meaningless: "
            + string.Join(" | ", errors.Select(e => e.ToString())));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new BrickInterfaceDriftAnalyzer()));

        return (await withAnalyzers.GetAnalyzerDiagnosticsAsync()).ToArray();
    }

    /// <summary>
    /// The contracts assembly the samples compile against. See <see cref="AnalyzerReferenceSet"/>
    /// for why the set is built from declared inputs rather than from the test host's
    /// loaded-assembly list.
    /// </summary>
    private static IReadOnlyList<MetadataReference> ReferenceSet()
        => AnalyzerReferenceSet.For(typeof(Ashlar.Core.Domain.Bricks.Brick));
}
