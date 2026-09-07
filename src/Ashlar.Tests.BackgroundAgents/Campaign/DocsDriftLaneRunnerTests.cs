using Ashlar.BackgroundAgents.Campaign;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Campaign;

/// <summary>Docs-drift specialist finds stale extracted paths and unpublished pins.</summary>
public sealed class DocsDriftLaneRunnerTests : IDisposable
{
    private readonly string _root;

    public DocsDriftLaneRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ashlar-docs-drift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "ci"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "background-agents"));
        File.WriteAllText(Path.Combine(_root, "VERSION"), "0.1.2\n");
        File.WriteAllText(Path.Combine(_root, "ci", "published-version"), "0.1.1\n");
        File.WriteAllText(Path.Combine(_root, "docs", "DogfoodCampaign.md"), "campaign\n");
        File.WriteAllText(Path.Combine(_root, "docs", "DogfoodValidation.md"), "automated dogfood campaign\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Flags_current_tense_extracted_release_manager_path()
    {
        File.WriteAllText(
            Path.Combine(_root, "docs", "IntegratorGuide.md"),
            "See `apps/release-manager/config/agent_set.release_manager.json`.\n");

        var report = await new DocsDriftLaneRunner().RunAsync(Context());

        report.Verdict.Should().Be(CampaignVerdictKind.Fail);
        report.Findings.Should().Contain(f => f.Code == "stale-extracted-path");
    }

    [Fact]
    public async Task Ignores_extracted_path_when_the_line_records_the_extraction()
    {
        File.WriteAllText(
            Path.Combine(_root, "docs", "IntegratorGuide.md"),
            "The `apps/release-manager/` vertical was extracted in 2026-09-01.\n");

        var report = await new DocsDriftLaneRunner().RunAsync(Context());

        report.Verdict.Should().Be(CampaignVerdictKind.Pass);
        report.Findings.Should().NotContain(f => f.Code == "stale-extracted-path");
    }

    [Fact]
    public async Task Flags_unpublished_package_pin()
    {
        File.WriteAllText(
            Path.Combine(_root, "docs", "Consuming.md"),
            "Add `<PackageReference Include=\"Ashlar.Sdk\" Version=\"0.1.2\" />`.\n");

        var report = await new DocsDriftLaneRunner().RunAsync(Context());

        report.Verdict.Should().Be(CampaignVerdictKind.Fail);
        report.Findings.Should().Contain(f => f.Code == "unpublished-version-pin");
    }

    [Fact]
    public async Task Clean_tree_passes()
    {
        var report = await new DocsDriftLaneRunner().RunAsync(Context());
        report.Verdict.Should().Be(CampaignVerdictKind.Pass);
    }

    private CampaignRunContext Context() => new(
        _root,
        "dev-tool-dogfood",
        "docs-drift",
        "docs-auditor",
        Full: false,
        SkipProcessLanes: true);
}
