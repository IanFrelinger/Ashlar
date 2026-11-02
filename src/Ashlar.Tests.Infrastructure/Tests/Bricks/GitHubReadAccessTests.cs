using Ashlar.Bricks.GitHub;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ExecutionContext = Ashlar.Infrastructure.Execution.ExecutionContext;

namespace Ashlar.Tests.Infrastructure.Tests.Bricks;

public sealed class GitHubReadAccessTests
{
    [Fact]
    public void Unconfigured_host_preserves_explicit_token_and_anonymous_library_behavior() =>
        GitHubReadAccess.FromConfiguration(_ => null).Should().BeNull();

    [Theory]
    [InlineData(null, "o/r")]
    [InlineData("secret", null)]
    [InlineData("", "o/r")]
    [InlineData("secret", " ")]
    public void Partial_or_empty_host_configuration_fails_without_disclosing_the_token(string? token, string? repository)
    {
        var act = () => GitHubReadAccess.FromConfiguration(key => key == GitHubReadAccess.TokenVariable ? token : repository);
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain("secret");
    }

    [Theory]
    [InlineData("https://github.com/o/r")]
    [InlineData("o/*")]
    [InlineData("o/r/elsewhere")]
    [InlineData("../r")]
    [InlineData("o/r?other")]
    public void Scope_is_an_exact_repository_not_a_url_or_pattern(string repository)
    {
        var act = () => new GitHubReadAccess("secret", repository);
        act.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("secret");
    }

    [Fact]
    public void A_caller_cannot_replace_the_host_credential_but_repository_case_is_accepted()
    {
        var access = new GitHubReadAccess("host-secret", "Org/Repo");
        access.ResolveToken("org", "repo", "caller-secret").Should().Be("host-secret");
        access.ResolveToken("ORG", "REPO", null).Should().Be("host-secret");
        access.ToString().Should().NotContain("host-secret");
    }

    [Fact]
    public async Task Blockers_refuses_out_of_scope_before_client_construction_even_with_caller_token()
    {
        var calls = 0;
        var brick = new GitHubBlockersBrick(NullLogger<GitHubBlockersBrick>.Instance,
            _ => { calls++; throw new InvalidOperationException("A client must not be constructed."); },
            access: new GitHubReadAccess("host-secret", "o/r"));
        var input = new BrickInput();
        input.Set("url", "https://github.com/o/other/pull/7");
        input.Set("githubToken", "caller-secret");

        var act = () => brick.ExecuteAsync(input, ImplementationType.Deterministic, Context());

        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).Which.Message.Should().NotContain("secret");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Blockers_supplies_the_host_token_to_the_client_instead_of_the_caller_token()
    {
        string? received = null;
        var brick = new GitHubBlockersBrick(NullLogger<GitHubBlockersBrick>.Instance,
            token => { received = token; throw new InvalidOperationException("stop before network"); },
            access: new GitHubReadAccess("host-secret", "o/r"));
        var input = new BrickInput();
        input.Set("url", "https://github.com/o/r/pull/7");
        input.Set("githubToken", "caller-secret");

        var act = () => brick.ExecuteAsync(input, ImplementationType.Deterministic, Context());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("stop before network");
        received.Should().Be("host-secret");
    }

    [Fact]
    public async Task Pr_and_ci_status_also_refuse_other_repositories()
    {
        var access = new GitHubReadAccess("host-secret", "o/r");
        var prInput = new BrickInput();
        prInput.Set("prUrl", "https://github.com/o/other/pull/7");
        var pr = await new PrStatusBrick(NullLogger<PrStatusBrick>.Instance, access)
            .ExecuteAsync(prInput, ImplementationType.Deterministic, Context());
        pr.Get<string>("state").Should().Be("error");
        pr.Summary.Should().Contain("outside").And.NotContain("host-secret");

        var ciInput = new BrickInput();
        ciInput.Set("owner", "o");
        ciInput.Set("repo", "other");
        ciInput.Set("ref", "master");
        var ci = await new CiStatusBrick(NullLogger<CiStatusBrick>.Instance, access)
            .ExecuteAsync(ciInput, ImplementationType.Deterministic, Context());
        ci.Get<string>("status").Should().Be("error");
        ci.Summary.Should().Contain("outside").And.NotContain("host-secret");
    }

    [Fact]
    public async Task Host_activation_injects_the_repository_policy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new GitHubReadAccess("host-secret", "o/r"));
        using var provider = services.BuildServiceProvider();
        // The SDK uses ActivatorUtilities as well: exercise its constructor choice, rather than
        // only constructing the guarded overload ourselves.
        var brick = ActivatorUtilities.CreateInstance<GitHubBlockersBrick>(provider);
        brick.Interface.Inputs.Should().NotContain(i => i.Name == "githubToken");
        var input = new BrickInput();
        input.Set("url", "https://github.com/o/other/pull/7");

        var act = () => brick.ExecuteAsync(input, ImplementationType.Deterministic, Context());
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public void Host_schemas_omit_the_token_while_default_schemas_preserve_it()
    {
        var access = new GitHubReadAccess("host-secret", "o/r");
        new PrStatusBrick(NullLogger<PrStatusBrick>.Instance, access).Interface.Inputs
            .Should().NotContain(i => i.Name == "githubToken");
        new CiStatusBrick(NullLogger<CiStatusBrick>.Instance, access).Interface.Inputs
            .Should().NotContain(i => i.Name == "githubToken");
        new GitHubBlockersBrick(NullLogger<GitHubBlockersBrick>.Instance).Interface.Inputs
            .Should().Contain(i => i.Name == "githubToken");
    }

    private static IExecutionContext Context() => new ExecutionContext { AgentId = "test", BehaviorId = "github.blockers" };
}
