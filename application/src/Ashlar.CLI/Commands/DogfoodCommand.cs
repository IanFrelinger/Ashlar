using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.Observation;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Dogfood validation commands — verify Ashlar uses its own capabilities on itself.
/// North Star: Each block must pass its dogfood gate before moving on.
/// </summary>
public sealed class DogfoodCommand : Command
{
    /// <summary>Creates a new DogfoodCommand instance.</summary>
    public DogfoodCommand() : base("dogfood", "Dogfood validation: verify Ashlar observes/adapts itself (North Star gates)")
    {
        var jsonOpt = new Option<bool>("--format-json", () => false, "Emit JSON output");

        var block1Cmd = new Command("block1", "Block 1 dogfood gate: verify observation pipeline watches Ashlar's own dev workflow and stores patterns");
        block1Cmd.AddOption(jsonOpt);
        block1Cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            Environment.Exit(await DogfoodBlock1Command.ExecuteAsync(json));
        });
        AddCommand(block1Cmd);

        AddCommand(DogfoodTestCommand.Create("block2", "Block 2: static analyzer runs against Block 1 (Observation) code", "DogfoodBlock2Tests", jsonOpt));
        AddCommand(DogfoodTestCommand.Create("block3", "Block 3: adaptation engine decomposes/recompiles Ashlar brick", "DogfoodBlock3Tests", jsonOpt));
        AddCommand(DogfoodTestCommand.Create("block4", "Block 4: promote Ashlar fix via inheritance", "DogfoodBlock4Tests", jsonOpt));
        AddCommand(DogfoodTestCommand.Create("block5", "Block 5: autonomy controls on Ashlar dev workflow", "DogfoodBlock5Tests", jsonOpt));
        AddCommand(DogfoodTestCommand.Create("block6", "Block 6: SelfContextAssembler answers 24h question", "DogfoodBlock6Tests", jsonOpt));
        AddCommand(DogfoodTestCommand.Create("block7", "Block 7: Composition engine composes for Ashlar problem", "DogfoodBlock7Tests", jsonOpt));
        AddCommand(DogfoodTestCommand.Create("block8", "Block 8: Parallel test matrix against Ashlar tests", "DogfoodBlock8Tests", jsonOpt));
        AddCommand(DogfoodTestCommand.Create("block9", "Block 9: Instance mesh discover/advertise", "DogfoodBlock9Tests", jsonOpt));
        AddCommand(DogfoodTestCommand.Create("closedloop", "Closed-loop improve on Ashlar", "DogfoodClosedLoopTests", jsonOpt));
        AddCommand(DogfoodTestCommand.Create("phasef", "Phase F: changelog, test failure store", "DogfoodPhaseFTests", jsonOpt));

        var allCmd = new Command("all", "Run all dogfood blocks (1–9) + closedloop + phasef");
        allCmd.AddOption(jsonOpt);
        var verboseOpt = new Option<bool>("--verbose", () => false, "Stream test output to console");
        allCmd.AddOption(verboseOpt);
        allCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var verbose = ctx.ParseResult.GetValueForOption(verboseOpt);
            Environment.Exit(await DogfoodAllCommand.ExecuteAsync(json, verbose));
        });
        AddCommand(allCmd);

        var campaignCmd = new Command("campaign", "Automated dogfood campaign: specialist sub-agents report to the release manager");
        campaignCmd.AddOption(jsonOpt);
        var campaignVerboseOpt = new Option<bool>("--verbose", () => false, "List specialists before dispatch");
        var fullOpt = new Option<bool>("--full", () => false, "Run the full regression lane (cert-gate --fast) instead of the cheap slice");
        var configOpt = new Option<string?>("--config", () => null, "Campaign agent-set JSON (default: docs/background-agents/examples/dogfood-campaign.json)");
        var outputOpt = new Option<string?>("--output", () => null, "Directory for report.json / observations.jsonl (default: .ashlar/dogfood-campaign)");
        var laneOpt = new Option<string?>("--lane", () => null, "Run a single specialist (DocsDrift, Regression, DevTool, or an agent id)");
        campaignCmd.AddOption(campaignVerboseOpt);
        campaignCmd.AddOption(fullOpt);
        campaignCmd.AddOption(configOpt);
        campaignCmd.AddOption(outputOpt);
        campaignCmd.AddOption(laneOpt);
        campaignCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var verbose = ctx.ParseResult.GetValueForOption(campaignVerboseOpt);
            var full = ctx.ParseResult.GetValueForOption(fullOpt);
            var config = ctx.ParseResult.GetValueForOption(configOpt);
            var output = ctx.ParseResult.GetValueForOption(outputOpt);
            var lane = ctx.ParseResult.GetValueForOption(laneOpt);
            // Do not Environment.Exit here: leftover MSBuild / VBCSCompiler nodes from a
            // specialist's `dotnet test` make Exit hang inside the container.
            ctx.ExitCode = await DogfoodCampaignCommand.ExecuteAsync(json, full, verbose, config, output, lane, ctx.GetCancellationToken());
        });
        AddCommand(campaignCmd);
    }
}
