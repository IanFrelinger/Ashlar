using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Campaign;

/// <summary>
/// The dogfood campaign and its Makefile front door must enter the repo's
/// dev/test container so the SDK is not a host install.
/// </summary>
public sealed class CampaignContainerConventionTests
{
    [Fact]
    public void Campaign_script_reenters_the_devcontainer_wrapper()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "run-dogfood-campaign.sh"));
        script.Should().Contain("run-in-devcontainer.sh");
        script.Should().Contain("ASHLAR_IN_DEVCONTAINER");
        script.Should().NotContain("install a host SDK");
    }

    [Fact]
    public void Makefile_dogfood_targets_use_the_container_wrapper()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var makefile = File.ReadAllText(Path.Combine(root, "Makefile"));
        makefile.Should().Contain("DEVBOX := bash scripts/run-in-devcontainer.sh");
        makefile.Should().Contain("$(DEVBOX) bash scripts/run-dogfood-campaign.sh");
        makefile.Should().Contain("$(DEVBOX) bash -lc '$(MAKE) dogfood-phase-c");
    }

    [Fact]
    public void Devtest_image_pins_the_devcontainer_base_and_net8_runtime()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var docker = File.ReadAllText(Path.Combine(root, ".docker", "Dockerfile.devtest"));
        docker.Should().Contain("mcr.microsoft.com/devcontainers/dotnet:10.0-noble");
        docker.Should().Contain("--channel 8.0 --runtime aspnetcore");
        docker.Should().Contain("ASHLAR_IN_DEVCONTAINER=1");
    }
}
