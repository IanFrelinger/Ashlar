using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.10b: source syntax pins the resolved-profile and HTTP MCP integration conventions.
/// Identifiers include alias/static imports, and invocation names are independent of whitespace.
/// This is a source convention, not a defense against reflection or dynamically constructed names.
/// </summary>
[Trait("Category", "Certification")]
public sealed class DeploymentProfileReadConventionTests
{
    [Fact]
    public void Production_HTTP_MCP_callers_use_the_Ashlar_transport_wrapper()
    {
        var root = TestPaths.FindRepoRoot();
        var files = ProductionFiles(root, new[] { "src", "application/src", "commercial" }).ToArray();
        files.Length.Should().BeGreaterThan(300, "the scan must read the production tree");
        var calls = files.Where(file => CallsSdkHttpTransport(Parse(File.ReadAllText(file))))
            .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
        calls.Should().Equal(new[] { "src/Ashlar.Mcp.Server/AshlarMcpServerServiceCollectionExtensions.cs" },
            "only the wrapper may call the third-party transport directly; runtime validation also detects HTTP");
    }

    [Fact]
    public void Infrastructure_the_API_and_the_CLI_never_reference_the_profile_source()
    {
        var root = TestPaths.FindRepoRoot();
        var files = ProductionFiles(root, new[] { "src/Ashlar.Infrastructure", "application/src/Ashlar.API", "application/src/Ashlar.CLI" }).ToArray();
        files.Length.Should().BeGreaterThanOrEqualTo(300, "the scan must read all three production roots");
        var offenders = files.Where(file => ReferencesProfileSource(Parse(File.ReadAllText(file))))
            .Select(file => Path.GetRelativePath(root, file));
        offenders.Should().BeEmpty("these layers obtain the deployment profile through resolved options from Hosting");
    }

    [Theory]
    [InlineData("class C { object P = AshlarDeploymentProfileEnvironment . Effective(\"full\"); }", true)]
    [InlineData("using P = Ashlar.Abstractions.AshlarDeploymentProfileEnvironment; class C { object X = P.Effective(\"full\"); }", true)]
    [InlineData("using static Ashlar.Abstractions.AshlarDeploymentProfileEnvironment; class C { object X = Effective(\"full\"); }", true)]
    [InlineData("class C { object X = Environment.GetEnvironmentVariable(\"ASHLAR_DEPLOYMENT_PROFILE\"); }", true)]
    [InlineData("// AshlarDeploymentProfileEnvironment.Effective and ASHLAR_DEPLOYMENT_PROFILE\nclass C { }", false)]
    [InlineData("class C { object X = options.Value.Profile; }", false)]
    public void Profile_source_syntax_fixtures(string code, bool expected)
        => ReferencesProfileSource(Parse(code)).Should().Be(expected);

    [Theory]
    [InlineData("builder . WithHttpTransport ();", true)]
    [InlineData("WithHttpTransport(builder);", true)]
    [InlineData("Alias.WithHttpTransport(builder);", true)]
    [InlineData("builder?.WithHttpTransport();", true)]
    [InlineData("builder.WithAshlarHttpTransport();", false)]
    [InlineData("// builder.WithHttpTransport();", false)]
    public void MCP_invocation_syntax_fixtures(string code, bool expected)
        => CallsSdkHttpTransport(Parse(code)).Should().Be(expected);

    private static SyntaxNode Parse(string source) => CSharpSyntaxTree.ParseText(source).GetRoot();

    private static bool ReferencesProfileSource(SyntaxNode root) =>
        root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(name => name.Identifier.ValueText == "AshlarDeploymentProfileEnvironment") ||
        root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Any(literal => literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText == "ASHLAR_DEPLOYMENT_PROFILE");

    private static bool CallsSdkHttpTransport(SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
            (call.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                MemberBindingExpressionSyntax member => member.Name.Identifier.ValueText,
                SimpleNameSyntax name => name.Identifier.ValueText,
                _ => null,
            }) == "WithHttpTransport");

    private static IEnumerable<string> ProductionFiles(string root, IEnumerable<string> roots)
    {
        foreach (var relative in roots)
        {
            var directory = Path.Combine(root, relative);
            Directory.Exists(directory).Should().BeTrue($"{relative} is a scan root");
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!parts.Any(part => part is "bin" or "obj" or "tests" || part.Contains("Tests", StringComparison.Ordinal)))
                    yield return file;
            }
        }
    }
}
