using Ashlar.BackgroundAgents.Campaign;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Campaign;

/// <summary>Dev-tool specialist requires the authoring / CLI surface.</summary>
public sealed class DevToolLaneRunnerTests : IDisposable
{
    private readonly string _root;

    public DevToolLaneRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ashlar-dev-tool-" + Guid.NewGuid().ToString("N"));
        SeedCompleteTree(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Complete_developer_surface_passes()
    {
        var report = await new DevToolLaneRunner().RunAsync(Context());
        report.Verdict.Should().Be(CampaignVerdictKind.Pass);
    }

    [Fact]
    public async Task Host_only_campaign_script_fails()
    {
        File.WriteAllText(
            Path.Combine(_root, "scripts", "run-dogfood-campaign.sh"),
            "dotnet run --project application/src/Ashlar.CLI -- dogfood campaign\n");

        var report = await new DevToolLaneRunner().RunAsync(Context());

        report.Verdict.Should().Be(CampaignVerdictKind.Fail);
        report.Findings.Should().Contain(f => f.Code == "campaign-not-containerized");
    }

    [Fact]
    public async Task Missing_campaign_registration_fails()
    {
        File.WriteAllText(
            Path.Combine(_root, "application", "src", "Ashlar.CLI", "Commands", "DogfoodCommand.cs"),
            "public sealed class DogfoodCommand { }\n");

        var report = await new DevToolLaneRunner().RunAsync(Context());

        report.Verdict.Should().Be(CampaignVerdictKind.Fail);
        report.Findings.Should().Contain(f => f.Code == "campaign-not-registered");
    }

    [Fact]
    public async Task Full_mode_requires_help_to_list_campaign()
    {
        var invoker = new StubInvoker(0, "block1\nblock2\ncampaign\n", string.Empty);
        var report = await new DevToolLaneRunner(invoker).RunAsync(Context() with { Full = true, SkipProcessLanes = false });
        report.Verdict.Should().Be(CampaignVerdictKind.Pass);
        invoker.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Full_mode_fails_when_help_omits_campaign()
    {
        var invoker = new StubInvoker(0, "block1\nblock2\n", string.Empty);
        var report = await new DevToolLaneRunner(invoker).RunAsync(Context() with { Full = true, SkipProcessLanes = false });
        report.Verdict.Should().Be(CampaignVerdictKind.Fail);
        report.Findings.Should().Contain(f => f.Code == "cli-help-missing-campaign");
    }

    internal static void SeedCompleteTree(string root)
    {
        Write(root, "application/src/Ashlar.CLI/Commands/DogfoodCommand.cs", "campaign\n");
        Write(root, "application/src/Ashlar.CLI/Commands/DogfoodCampaignCommand.cs", "class DogfoodCampaignCommand {}\n");
        Write(root, "application/src/Ashlar.CLI/Commands/NewCommand.cs", "brick\n");
        Write(root, "application/src/Ashlar.CLI/Ashlar.CLI.csproj", "<PackAsTool>true</PackAsTool>\n");
        Write(root, "docs/AuthoringBricks.md", "author\n");
        Write(root, "docs/GettingStarted.md", "start\n");
        Write(root, "docs/DogfoodCampaign.md", "campaign\n");
        Write(root, "docs/background-agents/examples/dogfood-campaign.json", "{}\n");
        Write(root, "scripts/run-in-devcontainer.sh", "#!/bin/bash\n");
        Write(root, "scripts/handoff/devbox.sh", "#!/bin/bash\n");
        Write(root, ".docker/Dockerfile.devtest", "FROM mcr.microsoft.com/devcontainers/dotnet:10.0-noble\n");
        Write(root, "scripts/run-dogfood-campaign.sh", "bash scripts/run-in-devcontainer.sh\n");
        Directory.CreateDirectory(Path.Combine(root, "samples", "templates", "brick"));
        Directory.CreateDirectory(Path.Combine(root, "consumer-template"));
        File.WriteAllText(Path.Combine(root, "consumer-template", "README.md"), "consume\n");
    }

    private static void Write(string root, string relative, string contents)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private CampaignRunContext Context() => new(
        _root,
        "dev-tool-dogfood",
        "dev-tool",
        "dev-tool-auditor",
        Full: false,
        SkipProcessLanes: true);

    private sealed class StubInvoker : ICampaignProcessInvoker
    {
        private readonly CampaignProcessResult _result;
        public int Calls { get; private set; }

        public StubInvoker(int exit, string stdout, string stderr)
            => _result = new CampaignProcessResult(exit, stdout, stderr);

        public Task<CampaignProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }
}
