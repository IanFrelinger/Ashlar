using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Composition.Models;
using Ashlar.Core.Application.Composition.Ports;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Composition;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Block 7: Compose an agent from capability components at runtime.
/// </summary>
public sealed class ComposeCommand : Command
{
    /// <summary>Creates a new ComposeCommand instance.</summary>
    public ComposeCommand() : base("compose", "Compose an agent from capability components (Block 7).")
    {
        var problemOpt = new Option<string>("--problem", "Problem description (e.g. 'test Ashlar CLI', 'test failure analyzer')");
        problemOpt.IsRequired = true;
        var capabilitiesOpt = new Option<string[]>("--capabilities", "Available capabilities (default: perception, validation, reporting)");
        capabilitiesOpt.AllowMultipleArgumentsPerToken = true;
        var supportLevelOpt = new Option<string[]>(
            "--support-level",
            () => new[] { "stable", "experimental", "placeholder" },
            "Allowed component support levels (stable, experimental, placeholder).");
        supportLevelOpt.AllowMultipleArgumentsPerToken = true;

        AddOption(problemOpt);
        AddOption(capabilitiesOpt);
        AddOption(supportLevelOpt);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            var problem = ctx.ParseResult.GetValueForOption(problemOpt)!;
            var capabilities = ctx.ParseResult.GetValueForOption(capabilitiesOpt) ?? Array.Empty<string>();
            var supportLevels = ctx.ParseResult.GetValueForOption(supportLevelOpt) ?? Array.Empty<string>();
            ctx.ExitCode = await ExecuteAsync(problem, capabilities, supportLevels);
        });
    }

    private static async Task<int> ExecuteAsync(string problem, string[] capabilities, string[] supportLevels)
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddCompositionInfrastructure()
            .BuildServiceProvider();

        var engine = services.GetRequiredService<ICompositionEngine>();
        var registry = services.GetRequiredService<ICapabilityComponentRegistry>();

        var parsedSupportLevels = ParseSupportLevels(supportLevels);
        if (parsedSupportLevels.Count == 0)
        {
            Console.Error.WriteLine("No valid support levels selected. Choose from: stable, experimental, placeholder.");
            return 1;
        }

        var allowedCapabilities = new HashSet<string>(
            registry.GetAll()
                .Where(descriptor => parsedSupportLevels.Contains(descriptor.SupportLevel))
                .Select(descriptor => descriptor.Capability),
            StringComparer.OrdinalIgnoreCase);

        var available = capabilities.Length > 0
            ? capabilities.ToList()
            : new List<string> { "perception", "validation", "reporting", "understanding", "code-analysis" };
        available = available
            .Where(capability => allowedCapabilities.Contains(capability))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (available.Count == 0)
        {
            Console.Error.WriteLine("No capabilities remain after support-level filtering.");
            return 1;
        }

        ComposedAgent? composed;
        try
        {
            composed = await engine.ComposeAsync(problem, available).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("Composition validation failed:");
            Console.Error.WriteLine($"  {ex.Message}");
            return 1;
        }

        if (composed == null)
        {
            Console.WriteLine("No composition found for the given problem and capabilities.");
            return 1;
        }

        Console.WriteLine("Composed Agent");
        Console.WriteLine($"  Problem: {composed.ProblemDescription}");
        Console.WriteLine($"  Pipeline: {string.Join(" → ", composed.ComponentIds)}");
        Console.WriteLine();
        Console.WriteLine("Components:");
        foreach (var id in composed.ComponentIds)
        {
            var desc = registry.GetById(id);
            Console.WriteLine($"  - {id}: {desc?.Capability ?? "?"} ({desc?.ImplementationType ?? "?"})");
        }
        return 0;
    }

    private static HashSet<ComponentSupportLevel> ParseSupportLevels(IEnumerable<string> values)
    {
        var parsed = new HashSet<ComponentSupportLevel>();
        foreach (var value in values)
        {
            if (Enum.TryParse<ComponentSupportLevel>(value, ignoreCase: true, out var level))
                parsed.Add(level);
        }

        return parsed;
    }
}
