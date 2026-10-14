using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

public sealed partial class EgressGuardConventionTests
{
    [Fact]
    public void F12_operator_initiator_writers_are_confined_to_the_three_export_files()
    {
        var scan = Tree.Value;
        scan.Files.Count.Should().BeGreaterThan(1000);
        var writers = scan.Files.Where(path => WritesInitiator(File.ReadAllText(Path.Combine(scan.Root, path)))).ToArray();
        writers.Should().BeEquivalentTo(new[]
        {
            "application/src/Ashlar.CLI/Commands/PkgCommand.cs",
            "application/src/Ashlar.CLI/Commands/NativeBundle.cs",
            "src/Ashlar.Infrastructure/Adaptation/SneakernetTransport.cs"
        });
    }

    [Theory]
    [InlineData("new R { Initiator = value };", true)]
    [InlineData("request.Initiator = value;", true)]
    [InlineData("request.Initiator == value;", false)]
    [InlineData("// request.Initiator = value;", false)]
    [InlineData("var text = \"Initiator = value\";", false)]
    public void F12_initiator_assignment_controls(string source, bool expected)
        => WritesInitiator(source).Should().Be(expected);

    // Syntax tripwire: all assignments named Initiator, including qualified accesses. Reflection is outside scope.
    private static bool WritesInitiator(string source)
        => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Any(a => a.Left switch
            {
                IdentifierNameSyntax name => name.Identifier.ValueText == "Initiator",
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "Initiator",
                _ => false
            });
}
