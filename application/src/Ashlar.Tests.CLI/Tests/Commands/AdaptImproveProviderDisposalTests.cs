using System.IO;
using System.Reflection;
using Ashlar.CLI.Commands;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// Card #809: <c>adapt</c> and <c>improve</c> must <c>using</c>-dispose their service providers so
/// <see cref="Ashlar.Infrastructure.Execution.ProviderFactory"/> flushes the refused-probe Warning summary.
/// Behavioural flush proof lives in <c>AdaptImproveProviderDisposalTwinTests</c> (Infrastructure).
/// </summary>
public sealed class AdaptImproveProviderDisposalTests
{
    [Fact]
    public void Adapt_and_Improve_BuildServiceProvider_calls_are_using_disposed()
    {
        static void AssertUsing(string relativePath)
        {
            var root = FindRepoRoot();
            var text = File.ReadAllText(Path.Combine(root, relativePath));
            text.Should().Contain("using var services",
                $"{relativePath} must dispose its ServiceProvider so ProviderFactory flushes event 7307");
            text.Should().Contain("BuildServiceProvider()",
                $"{relativePath} still builds a ServiceProvider");
        }

        AssertUsing("application/src/Ashlar.CLI/Commands/AdaptCommand.cs");
        AssertUsing("application/src/Ashlar.CLI/Commands/ImproveCommand.cs");
        typeof(ImproveCommand).GetMethod("RegisterProviderFactories",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Should().NotBeNull();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ashlar.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Ashlar.sln not found above " + AppContext.BaseDirectory);
    }
}
