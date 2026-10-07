using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Analysis.Models;

namespace Ashlar.Infrastructure.Adaptation;

/// <summary>
/// Roslyn-based code fixer for EmptyCatch violations. Adds a trace statement to make the block non-empty.
/// </summary>
internal sealed class EmptyCatchCodeFixer : ISourceCodeFixer
{
    /// <inheritdoc />
    public Task<bool> TryFixAsync(Violation violation, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(violation.Rule, "EmptyCatch", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        if (!File.Exists(violation.FilePath))
            return Task.FromResult(false);

        var source = File.ReadAllText(violation.FilePath);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);

        CatchClauseSyntax? targetCatch = null;
        if (violation.LineNumber.HasValue)
        {
            var line = violation.LineNumber.Value;
            foreach (var c in root.DescendantNodes().OfType<CatchClauseSyntax>())
            {
                var cLine = c.SyntaxTree?.GetLineSpan(c.Span).StartLinePosition.Line + 1;
                if (cLine == line && c.Block.Statements.Count == 0)
                {
                    targetCatch = c;
                    break;
                }
            }
        }
        else
        {
            targetCatch = root.DescendantNodes().OfType<CatchClauseSyntax>()
                .FirstOrDefault(c => c.Block.Statements.Count == 0);
        }

        if (targetCatch == null)
            return Task.FromResult(false);

        var newCatch = RewriteCatch(targetCatch);
        var newRoot = root.ReplaceNode(targetCatch, newCatch);
        var newSource = newRoot.NormalizeWhitespace().ToFullString();
        if (newSource == source)
            return Task.FromResult(false);

        File.WriteAllText(violation.FilePath, newSource);
        return Task.FromResult(true);
    }

    private static CatchClauseSyntax RewriteCatch(CatchClauseSyntax catchClause)
    {
        var block = catchClause.Block;
        var traceWriteLine = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("System"),
                    SyntaxFactory.IdentifierName("Diagnostics")),
                SyntaxFactory.IdentifierName("Trace")),
            SyntaxFactory.IdentifierName("WriteLine"));
        var statement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                traceWriteLine,
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal("Caught")))))));

        var newBlock = block.AddStatements(statement);
        return catchClause.WithBlock(newBlock);
    }
}
