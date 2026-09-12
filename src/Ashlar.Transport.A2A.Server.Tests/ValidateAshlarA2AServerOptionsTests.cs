using FluentAssertions;
using Ashlar.Transport.A2A.Server;
using Xunit;

namespace Ashlar.Transport.A2A.Server.Tests;

[Collection(DeploymentProfileEnvironmentCollection.Name)]
public sealed class ValidateAshlarA2AServerOptionsTests
{
    private static readonly ValidateAshlarA2AServerOptions Validator = new();

    private static AshlarA2AServerOptions Enabled() => new()
    {
        Enabled = true,
        PublicBaseUrl = "https://peer.example.com",
        DefaultExecutionTimeout = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public void Disabled_options_are_always_valid()
    {
        Validator.Validate(null, new AshlarA2AServerOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("airgapped", "AirGapped")]
    [InlineData("air_gapped", "AirGapped")]
    [InlineData("secure-workstation", "SecureWorkstation")]
    [InlineData("workstation", "SecureWorkstation")]
    [InlineData("secure_workstation", "SecureWorkstation")]
    public void Enabled_under_no_egress_profiles_fails(string profile, string label)
    {
        Environment.SetEnvironmentVariable(ValidateAshlarA2AServerOptions.DeploymentProfileVariable, profile);
        try
        {
            var result = Validator.Validate(null, Enabled());

            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain(label);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidateAshlarA2AServerOptions.DeploymentProfileVariable, null);
        }
    }
}
