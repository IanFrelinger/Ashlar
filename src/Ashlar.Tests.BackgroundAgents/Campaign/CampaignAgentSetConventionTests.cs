using Ashlar.BackgroundAgents.Campaign;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Campaign;

/// <summary>
/// Pins the shipped dogfood campaign agent set: one release manager, three
/// specialists that report to it, every required lane present.
/// </summary>
public sealed class CampaignAgentSetConventionTests
{
    [Fact]
    public async Task Shipped_agent_set_loads_as_a_real_background_agent_document()
    {
        var path = Path.Combine(RepoPathResolver.FindRepoRoot(), CampaignAgentSetLoader.DefaultRelativePath);
        File.Exists(path).Should().BeTrue();

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();
        var loader = new BackgroundAgentConfigLoader(configuration, new DataSensitivityRegistry());
        var configs = await loader.LoadAsync(default);

        configs.Should().HaveCount(4);
        configs.Single(c => c.Role == "release-manager").Id.Should().Be("release-manager");
        configs.Where(c => c.Id != "release-manager").Should().OnlyContain(c => c.ParentId == "release-manager");
    }

    [Fact]
    public async Task Shipped_agent_set_covers_every_campaign_lane()
    {
        var path = Path.Combine(RepoPathResolver.FindRepoRoot(), CampaignAgentSetLoader.DefaultRelativePath);
        var set = await CampaignAgentSetLoader.LoadAsync(path);

        set.ManagerId.Should().Be("release-manager");
        set.Specialists.Select(s => s.Lane).Should().BeEquivalentTo(new[]
        {
            CampaignLane.DocsDrift,
            CampaignLane.Regression,
            CampaignLane.DevTool
        });
        set.Specialists.Should().OnlyContain(s => s.ParentId == "release-manager");
    }

    [Fact]
    public async Task Rejects_a_set_with_no_release_manager()
    {
        var path = WriteTempSet("""
            {
              "BackgroundAgents": {
                "Agents": [
                  {
                    "Id": "docs-drift",
                    "Name": "Docs",
                    "Role": "docs-auditor",
                    "Commands": ["audit"],
                    "Schedule": { "Type": "Interval", "Interval": "01:00:00" },
                    "Enabled": true
                  }
                ]
              }
            }
            """);

        var act = () => CampaignAgentSetLoader.LoadAsync(path);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exactly one*release-manager*");
    }

    private static string WriteTempSet(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-campaign-set-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
