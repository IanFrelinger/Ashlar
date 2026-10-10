using System.Reflection;
using Ashlar.Infrastructure.Egress;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

public sealed partial class EgressGuardConventionTests
{
    [Fact]
    public void Every_Ashlar_factory_registration_has_a_protected_opt_out_name()
    {
        // Share the inventory's production population: commercial and shipped TestKit code count;
        // test projects, build output and nested checkouts do not.
        var scan = Tree.Value;
        scan.Files.Count.Should().BeGreaterThan(1000);
        var units = scan.Files.Select(path => (Path: path, Text: File.ReadAllText(Path.Combine(scan.Root, path))))
            .Where(file => file.Text.Contains("AddHttpClient", StringComparison.Ordinal)
                || file.Text.Contains("HttpClientName", StringComparison.Ordinal))
            .Select(file => (file.Path, Unit: CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot())).ToArray();
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, unit) in units)
        {
            foreach (var field in unit.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword)))
            {
                var type = field.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
                foreach (var variable in field.Declaration.Variables)
                    if (variable.Initializer?.Value is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                        constants[type + "." + variable.Identifier.ValueText] = literal.Token.ValueText;
            }
        }
        var registrations = new List<(string Path, string Name)>();
        foreach (var (path, unit) in units)
        {
            foreach (var call in unit.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = call.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name,
                    SimpleNameSyntax simple => simple,
                    _ => null,
                };
                if (name?.Identifier.ValueText != "AddHttpClient") continue;
                string resolved;
                if (name is GenericNameSyntax generic)
                {
                    // These two typed registrations are also resolved through the real factory in
                    // EgressHostClientOptOutTests; a new generic shape requires an explicit review.
                    generic.TypeArgumentList.Arguments.Should().HaveCount(2);
                    call.ArgumentList.Arguments.Should().ContainSingle();
                    call.ArgumentList.Arguments[0].Expression.Should().BeAssignableTo<AnonymousFunctionExpressionSyntax>();
                    generic.TypeArgumentList.Arguments[0].Should().BeOfType<IdentifierNameSyntax>();
                    resolved = ((IdentifierNameSyntax)generic.TypeArgumentList.Arguments[0]).Identifier.ValueText;
                }
                else if (call.ArgumentList.Arguments.Count == 0) resolved = string.Empty;
                else
                {
                    var argument = call.ArgumentList.Arguments[0].Expression;
                    resolved = argument switch
                    {
                        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
                        InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } nameofCall =>
                            nameofCall.ArgumentList.Arguments.Single().Expression.ToString().Split('.').Last(),
                        _ => constants.GetValueOrDefault(argument.ToString(), "unresolved:" + argument),
                    };
                }
                registrations.Add((path, resolved));
            }
        }
        registrations.Should().HaveCountGreaterThanOrEqualTo(12);
        registrations.Should().Contain(entry => entry.Path.StartsWith("commercial/", StringComparison.Ordinal));
        registrations.Should().NotContain(entry => entry.Name.StartsWith("unresolved:", StringComparison.Ordinal),
            "a new dynamic registration needs a reviewed name resolution rule");
        var registry = typeof(EgressGuardOptions).Assembly.GetType("Ashlar.Infrastructure.Egress.AshlarFactoryClientNames", throwOnError: true)!;
        var protectedNames = (IReadOnlyList<string>)registry.GetField("All", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        protectedNames.Should().BeEquivalentTo(registrations.Select(entry => entry.Name).Distinct(StringComparer.Ordinal),
            "every Ashlar factory name must reject a host opt-out, and retired names should leave the registry");
    }
}
