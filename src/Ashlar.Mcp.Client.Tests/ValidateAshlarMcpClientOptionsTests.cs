using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ashlar.Mcp.Client.Tests;

[Collection(DeploymentProfileEnvironmentCollection.Name)]
public sealed class ValidateAshlarMcpClientOptionsTests
{
    private static readonly ValidateAshlarMcpClientOptions Validator = new();

    private static AshlarMcpClientOptions Enabled(params McpServerEndpointOptions[] servers)
    {
        var options = new AshlarMcpClientOptions { Enabled = true };
        foreach (var server in servers)
        {
            options.Servers.Add(server);
        }

        return options;
    }

    private static McpServerEndpointOptions Server(
        string name = "github",
        string? url = "https://mcp.example.com/mcp",
        string? header = null,
        string? envVar = null)
        => new() { Name = name, Url = url, ApiKeyHeader = header, ApiKeyEnvVar = envVar };

    [Fact]
    public void Disabled_options_are_always_valid()
    {
        var result = Validator.Validate(null, new AshlarMcpClientOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Valid_enabled_configuration_passes()
    {
        Validator.Validate(null, Enabled(Server())).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("secure-workstation")]
    [InlineData("workstation")]
    [InlineData("SecureWorkstation")]
    [InlineData("secure_workstation")]
    public void Enabled_under_secure_workstation_profile_fails(string profile)
    {
        Environment.SetEnvironmentVariable(ValidateAshlarMcpClientOptions.DeploymentProfileVariable, profile);
        try
        {
            var result = Validator.Validate(null, Enabled(Server()));

            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain("SecureWorkstation");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidateAshlarMcpClientOptions.DeploymentProfileVariable, null);
        }
    }

    [Fact]
    public void Enabled_under_airgapped_profile_fails()
    {
        Environment.SetEnvironmentVariable(ValidateAshlarMcpClientOptions.DeploymentProfileVariable, "AirGapped");
        try
        {
            var result = Validator.Validate(null, Enabled(Server()));

            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain("AirGapped");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidateAshlarMcpClientOptions.DeploymentProfileVariable, null);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("bad:name")]
    public void Server_names_outside_the_id_charset_fail(string name)
    {
        var result = Validator.Validate(null, Enabled(Server(name: name)));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Name");
    }

    [Fact]
    public void Duplicate_server_names_fail()
    {
        var result = Validator.Validate(null, Enabled(Server(), Server()));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("duplicate");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    [InlineData("/relative/path")]
    public void Non_http_urls_fail(string? url)
    {
        var result = Validator.Validate(null, Enabled(Server(url: url)));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Url");
    }

    [Fact]
    public void Api_key_header_without_env_var_fails()
    {
        var result = Validator.Validate(null, Enabled(Server(header: "X-Api-Key")));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("together");
    }

    [Fact]
    public void Referenced_but_unset_env_var_fails_at_startup()
    {
        var result = Validator.Validate(
            null,
            Enabled(Server(header: "X-Api-Key", envVar: "ASHLAR_TEST_MCP_KEY_THAT_DOES_NOT_EXIST")));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ASHLAR_TEST_MCP_KEY_THAT_DOES_NOT_EXIST");
    }

    [Fact]
    public void Referenced_and_set_env_var_passes()
    {
        Environment.SetEnvironmentVariable("ASHLAR_TEST_MCP_KEY", "secret");
        try
        {
            var result = Validator.Validate(null, Enabled(Server(header: "X-Api-Key", envVar: "ASHLAR_TEST_MCP_KEY")));

            result.Succeeded.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_TEST_MCP_KEY", null);
        }
    }

    [Fact]
    public void Negative_refresh_interval_fails()
    {
        var options = Enabled(Server());
        options.ToolListRefreshInterval = TimeSpan.FromSeconds(-1);

        Validator.Validate(null, options).Failed.Should().BeTrue();
    }
}
