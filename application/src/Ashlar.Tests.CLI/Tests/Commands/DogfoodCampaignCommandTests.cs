using Ashlar.CLI.Commands;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>CLI surface for the automated dogfood campaign.</summary>
public sealed class DogfoodCampaignCommandTests
{
    [Fact]
    public void Dogfood_command_registers_campaign()
    {
        var command = new DogfoodCommand();
        command.Subcommands.Should().Contain(c => c.Name == "campaign");
        var campaign = command.Subcommands.Single(c => c.Name == "campaign");
        campaign.Options.Select(o => o.Name).Should().Contain(new[] { "full", "config", "output", "lane" });
    }
}
