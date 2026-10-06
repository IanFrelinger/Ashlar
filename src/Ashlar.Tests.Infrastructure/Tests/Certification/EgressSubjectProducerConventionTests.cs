using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.5, the floor pin: every place production code enters an <c>EgressSubject</c> frame
/// (<c>EgressSubject.Enter</c>, and <c>EgressSubject.BeginRead</c>, which enters a read's frame) is listed here with
/// its floor and the method it is in, and each one is a <c>using</c> in a method that is not an iterator.
/// </summary>
/// <remarks>
/// <para><b>Why the floor is pinned.</b> A runner declares the floor it can vouch for, and a frame below
/// <c>SystemHigh</c> may wrap only code whose reads are observed: entering a frame lowers the label from no subject
/// (<c>SystemHigh</c>) to the floor, so a floor nobody reviewed is a write-down with no downgrade once the guard
/// enforces. In PR 4 no production runner declares a floor below <c>SystemHigh</c>: self-extend's snapshot carries
/// unlabelled carry-over (the owner's 2026-10-05 answer to Q2).</para>
/// <para><b>Why a <c>using</c>, outside an iterator.</b> A flow leaves a frame only by disposing its own head while
/// that head is undisposed, so a frame disposed out of order keeps its flow inside the outer frame (PR 4.4's known
/// limits). A <c>using</c> on the flow that entered the frame disposes it there, in order, before the method returns;
/// a <c>using</c> block across a <c>yield return</c> of an async iterator does not, because the body resumes on its
/// consumer's flow.</para>
/// <para><b>What counts as production.</b> Every C# file in the repository, as the other convention scans read it
/// (build output, dot directories and nested checkouts pruned), except test code: a path with a directory named
/// <c>tests</c> (any case), or whose name contains <c>.Tests</c> or ends in <c>TestKit</c>. A tripwire, not a proof:
/// a call through an alias, a delegate or reflection is not seen, and a <c>using static</c> of <c>EgressSubject</c> in
/// production code fails the scan instead.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressSubjectProducerConventionTests
{
    private const int ScannedFileFloor = 1000;
    private const string SystemHighFloor = "new HighWaterMark(SecurityLabel.SystemHigh)";

    /// <summary>Path, enclosing method (with parameter types), call, and floor (for <c>Enter</c>).</summary>
    private static readonly string[] ExpectedSites =
    [
        "src/Ashlar.BackgroundAgents.HostRunners/SelfExtendRunnerAdapter.cs | "
            + "RunAsync(string, string?, string?, string?, string?, string?, CancellationToken) | Enter | " + SystemHighFloor,
        "src/Ashlar.BackgroundAgents/Agents/ToolCallingAgent.cs | "
            + "RunCycleAsync(WorldSnapshot, IToolbox, PolicyEngine, ChainRejectionCallback?, IAgentMemory?, CancellationToken) | BeginRead | -",
    ];

    /// <summary>Path and type name of every production type that declares itself labelled.</summary>
    private static readonly string[] ExpectedImplementers =
    [
        "src/Ashlar.BackgroundAgents/RAG/RAGTool.cs | RAGTool",
    ];

    [Fact]
    public void Every_production_frame_entry_is_listed_with_its_floor_and_its_method()
    {
        var scan = Scan();

        scan.Scanned.Should().BeGreaterThanOrEqualTo(ScannedFileFloor, "the scan must reach the repository's C#; an emptied scan proves nothing");
        scan.Sites.Select(s => s.Pin).Should().Equal(
            ExpectedSites,
            "a production frame entry decides the label its subject's egress starts from. A new one, or a changed floor, "
            + "needs its reason in SPEC-007 PR 4.5's producer rule and a row here; a missing one means a runner stopped "
            + "declaring its subject");
    }

    [Fact]
    public void No_production_runner_declares_a_floor_below_SystemHigh()
    {
        var enters = Scan().Sites.Where(s => s.Call == "Enter").ToList();

        enters.Should().NotBeEmpty("self-extend declares its frame");
        enters.Should().AllSatisfy(s => s.Floor.Should().Be(
            SystemHighFloor,
            "a runner may declare a floor below SystemHigh only for inputs it built and can vouch for, and in PR 4 none does ({0})",
            s.Pin));
    }

    [Fact]
    public void Every_production_frame_entry_is_a_using_in_a_method_that_is_not_an_iterator()
    {
        var scan = Scan();

        scan.StaticImports.Should().BeEmpty("a using static of EgressSubject would hide a frame entry from this scan");
        scan.Aliases.Should().BeEmpty("a using alias of EgressSubject would hide a frame entry from this scan");
        scan.Sites.Should().NotBeEmpty();
        scan.Sites.Should().AllSatisfy(s =>
        {
            s.IsUsing.Should().BeTrue("{0} must be the resource of a using statement or declaration, so the flow that entered the frame disposes it, in order", s.Pin);
            s.InIterator.Should().BeFalse("{0} must not be in an iterator: after a yield return the body resumes on its consumer's flow", s.Pin);
        });
    }

    [Fact]
    public void Only_RAGTool_declares_itself_labelled_in_production()
    {
        var scan = Scan();

        scan.Scanned.Should().BeGreaterThanOrEqualTo(ScannedFileFloor);
        scan.Implementers.Should().Equal(
            ExpectedImplementers,
            "a tool that declares itself labelled (ILabelledTool) is trusted to report a label for everything its result "
            + "carries, and a report that is too low is a write-down with no downgrade once the guard enforces: a new "
            + "implementer needs its reason in SPEC-007 PR 4.5's producer rule and a row here (a host's own labelled tool is "
            + "the host's trusted base, as its IEgressGuard is)");
    }

    [Fact]
    public void The_scan_reads_the_using_the_iterator_and_the_method_from_the_syntax()
    {
        const string source = """
            using Ashlar.Abstractions.Security;
            using Ashlar.Abstractions.Security.Egress;
            class Runner
            {
                async Task DeclarationAsync(string id) { using var frame = EgressSubject.Enter(id, new HighWaterMark(SecurityLabel.SystemHigh)); await Task.Yield(); }
                void Statement() { using (EgressSubject.Enter("s", new HighWaterMark())) { } }
                void Block() { using (var read = EgressSubject.BeginRead()) { read.Complete(); } }
                void Bare() { var frame = EgressSubject.Enter("b", new HighWaterMark(SecurityLabel.Public)); frame.Dispose(); }
                async IAsyncEnumerable<int> Iterator() { using var frame = Ashlar.Abstractions.Security.Egress.EgressSubject.Enter("i", new HighWaterMark()); yield return 1; await Task.Yield(); }
                void Lambda() { Action a = () => { using var read = EgressSubject.BeginRead(); }; }
                IEnumerable<int> IteratorAroundLambda() { Action a = () => { using var f = EgressSubject.Enter("l", new HighWaterMark()); }; yield return 1; }
            }
            """;

        var sites = SitesIn("fixture.cs", source).Select(s => $"{s.Pin} | using={s.IsUsing} | iterator={s.InIterator}").ToList();

        sites.Should().Equal(
            "fixture.cs | DeclarationAsync(string) | Enter | new HighWaterMark(SecurityLabel.SystemHigh) | using=True | iterator=False",
            "fixture.cs | Statement() | Enter | new HighWaterMark() | using=True | iterator=False",
            "fixture.cs | Block() | BeginRead | - | using=True | iterator=False",
            "fixture.cs | Bare() | Enter | new HighWaterMark(SecurityLabel.Public) | using=False | iterator=False",
            "fixture.cs | Iterator() | Enter | new HighWaterMark() | using=True | iterator=True",
            "fixture.cs | a lambda in Lambda() | BeginRead | - | using=True | iterator=False",
            "fixture.cs | a lambda in IteratorAroundLambda() | Enter | new HighWaterMark() | using=True | iterator=False");
    }

    [Fact]
    public void The_scan_reads_the_alias_the_static_import_and_the_implementers_from_the_syntax()
    {
        const string source = """
            using ES = Ashlar.Abstractions.Security.Egress.EgressSubject;
            using static Ashlar.Abstractions.Security.Egress.EgressSubject;
            using Ashlar.Abstractions.Security.Egress;
            sealed class Labelled : ITool, ILabelledTool { }
            sealed class Qualified : Ashlar.Abstractions.Security.Egress.ILabelledTool { }
            sealed class Plain : ITool { }
            interface IDerived : ILabelledTool { }
            sealed class Wrapper { sealed class Nested : ILabelledTool { } }
            """;

        var (staticImports, aliases) = DirectivesIn("fixture.cs", source);
        staticImports.Should().Equal("fixture.cs");
        aliases.Should().Equal("fixture.cs | ES");
        ImplementersIn("fixture.cs", source).Should().Equal(
            "fixture.cs | Labelled",
            "fixture.cs | Qualified",
            "fixture.cs | IDerived",
            "fixture.cs | Nested");
    }

    private sealed record Site(string Pin, string Call, string Floor, bool IsUsing, bool InIterator);

    private sealed record ScanResult(List<Site> Sites, List<string> StaticImports, List<string> Aliases, List<string> Implementers, int Scanned);

    private static ScanResult Scan()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var result = new ScanResult([], [], [], [], 0);
        var scanned = Collect(root, root, result);
        result.Sites.Sort((a, b) => string.CompareOrdinal(a.Pin, b.Pin));
        result.Implementers.Sort(string.CompareOrdinal);
        return result with { Scanned = scanned };
    }

    private static int Collect(string root, string directory, ScanResult result)
    {
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            scanned++;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsTestCode(relative))
                continue;

            var text = File.ReadAllText(file);
            if (!text.Contains("EgressSubject", StringComparison.Ordinal) && !text.Contains("ILabelledTool", StringComparison.Ordinal))
                continue;

            var (staticImports, aliases) = DirectivesIn(relative, text);
            result.StaticImports.AddRange(staticImports);
            result.Aliases.AddRange(aliases);
            result.Implementers.AddRange(ImplementersIn(relative, text));
            result.Sites.AddRange(SitesIn(relative, text));
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (!IsPruned(child))
                scanned += Collect(root, child, result);
        }

        return scanned;
    }

    // A `using static …EgressSubject;` (the file) and every `using X = …EgressSubject;` (the file and the alias).
    private static (List<string> StaticImports, List<string> Aliases) DirectivesIn(string relative, string text)
    {
        var staticImports = new List<string>();
        var aliases = new List<string>();
        var unit = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        foreach (var directive in unit.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (directive.Name?.ToString().EndsWith("EgressSubject", StringComparison.Ordinal) != true)
                continue;

            if (directive.StaticKeyword != default)
                staticImports.Add(relative);
            else if (directive.Alias is not null)
                aliases.Add($"{relative} | {directive.Alias.Name.Identifier.ValueText}");
        }

        return (staticImports, aliases);
    }

    // Every type whose base list names ILabelledTool, simple or qualified, nested or not, in declaration order.
    private static IEnumerable<string> ImplementersIn(string relative, string text) =>
        CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot()
            .DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(type => type.BaseList?.Types.Any(b => SimpleName(b.Type) == "ILabelledTool") == true)
            .Select(type => $"{relative} | {type.Identifier.ValueText}");

    private static string? SimpleName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => null,
    };

    private static IEnumerable<Site> SitesIn(string relative, string text)
    {
        var unit = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        foreach (var call in unit.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax access
                || access.Name.Identifier.ValueText is not ("Enter" or "BeginRead")
                || !NamesEgressSubject(access.Expression))
            {
                continue;
            }

            var name = access.Name.Identifier.ValueText;
            var floor = name == "Enter" && call.ArgumentList.Arguments.Count > 1
                ? Normalize(call.ArgumentList.Arguments[1].ToString())
                : "-";
            var (member, body) = EnclosingMember(call);
            yield return new Site($"{relative} | {member} | {name} | {floor}", name, floor, IsUsingResource(call), IsIterator(body));
        }
    }

    private static bool NamesEgressSubject(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText == "EgressSubject",
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "EgressSubject",
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText == "EgressSubject",
        _ => false,
    };

    // The resource of `using (X)`, `using (var v = X)` or `using var v = X;`, directly.
    private static bool IsUsingResource(InvocationExpressionSyntax call)
    {
        SyntaxNode node = call;
        while (node.Parent is ParenthesizedExpressionSyntax)
            node = node.Parent;

        if (node.Parent is UsingStatementSyntax statement)
            return statement.Expression == node;

        if (node.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } })
        {
            return declaration.Parent is UsingStatementSyntax
                || declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 };
        }

        return false;
    }

    private static (string Name, SyntaxNode? Body) EnclosingMember(SyntaxNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            switch (parent)
            {
                case AnonymousFunctionExpressionSyntax lambda:
                    var (outer, _) = EnclosingMember(lambda);
                    return ("a lambda in " + outer, lambda.Body);
                case LocalFunctionStatementSyntax local:
                    return (local.Identifier.ValueText + Parameters(local.ParameterList), (SyntaxNode?)local.Body ?? local.ExpressionBody);
                case MethodDeclarationSyntax method:
                    return (method.Identifier.ValueText + Parameters(method.ParameterList), (SyntaxNode?)method.Body ?? method.ExpressionBody);
                case ConstructorDeclarationSyntax constructor:
                    return (".ctor" + Parameters(constructor.ParameterList), (SyntaxNode?)constructor.Body ?? constructor.ExpressionBody);
                case AccessorDeclarationSyntax accessor:
                    return (accessor.Keyword.ValueText, (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody);
                case BaseTypeDeclarationSyntax type:
                    return (type.Identifier.ValueText, null);
            }
        }

        return ("(top level)", null);
    }

    private static string Parameters(ParameterListSyntax list) =>
        "(" + string.Join(", ", list.Parameters.Select(p => Normalize(p.Type?.ToString() ?? "?"))) + ")";

    // An iterator: a yield statement whose nearest enclosing function is this body's.
    private static bool IsIterator(SyntaxNode? body) =>
        body is not null && body.DescendantNodes(n => n == body || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OfType<YieldStatementSyntax>()
            .Any();

    private static string Normalize(string code) => string.Join(" ", code.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsTestCode(string relative) =>
        relative.Split('/')[..^1].Any(segment =>
            segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
            || segment.Contains(".Tests", StringComparison.Ordinal)
            || segment.EndsWith("TestKit", StringComparison.Ordinal));

    /// <summary>Build output, tool state and the root of any nested checkout, as the other convention tests prune.</summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);
        if (name is "bin" or "obj" || name.StartsWith('.'))
            return true;

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }
}
