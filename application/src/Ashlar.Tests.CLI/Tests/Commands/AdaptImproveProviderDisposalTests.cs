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
        var root = FindRepoRoot();
        var adapt = File.ReadAllText(Path.Combine(root, "application/src/Ashlar.CLI/Commands/AdaptCommand.cs"));
        var improve = File.ReadAllText(Path.Combine(root, "application/src/Ashlar.CLI/Commands/ImproveCommand.cs"));

        // Exact statements: a bare "using var services" elsewhere must not mask a dropped dispose.
        adapt.Should().Contain("using var services = new ServiceCollection()",
            "adapt must using-dispose the ServiceCollection it builds");
        improve.Should().Contain("using var services = serviceCollection.BuildServiceProvider();",
            "improve ExecuteAsync must using-dispose the provider that owns ProviderFactory");
        improve.Should().Contain("using var services = new ServiceCollection()",
            "improve observation phase must using-dispose its ServiceProvider too");

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
