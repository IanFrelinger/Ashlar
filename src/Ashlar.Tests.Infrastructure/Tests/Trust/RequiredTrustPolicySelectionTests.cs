using Ashlar.Infrastructure.Trust;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Trust;

[Collection("EnvironmentVariables")]
public sealed class RequiredTrustPolicySelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "required-trust-" + Guid.NewGuid().ToString("N"));
    private readonly EnvironmentVariableScope _required;
    private readonly EnvironmentVariableScope _pointer;
    private string StatePath => Path.Combine(_root, "state", "active.json");
    private string Definitions => Path.Combine(_root, "definitions");

    public RequiredTrustPolicySelectionTests()
    {
        Directory.CreateDirectory(Definitions);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        foreach (var id in new[] { "internal-only", "strict-enterprise" })
            File.WriteAllText(Path.Combine(Definitions, id + ".json"),
                $$"""{"id":"{{id}}","version":"1.0.0","categoryRules":{},"sourceRules":{},"projectRules":[]}""");
        File.WriteAllText(Path.Combine(Definitions, "active-pack.json"), Selection("internal-only"));
        _required = new("ASHLAR_TRUST_POLICY_REQUIRE_ACTIVE", "true");
        _pointer = new("ASHLAR_ACTIVE_TRUST_POLICY_PACK_PATH", StatePath);
    }

    private static string Selection(string id) => $$"""{"Id":"{{id}}","Version":"1.0.0"}""";

    [Fact]
    public void Missing_selection_requires_explicit_provisioning_even_with_shipped_default()
    {
        Assert.Throws<FileNotFoundException>(() => new TrustPolicyPackRegistry(Definitions));
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task Lost_selection_after_activation_does_not_revert_to_shipped_policy()
    {
        File.WriteAllText(StatePath, Selection("internal-only"));
        var registry = new TrustPolicyPackRegistry(Definitions);
        await registry.ActivateAsync("strict-enterprise");
        Assert.Equal("strict-enterprise", new TrustPolicyPackRegistry(Definitions).GetActivePack()!.Id);
        File.Delete(StatePath);
        Assert.Throws<FileNotFoundException>(() => new TrustPolicyPackRegistry(Definitions));
        Assert.False(File.Exists(StatePath));
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Id\":\"unknown\",\"Version\":\"1.0.0\"}")]
    [InlineData("{\"Id\":\"strict-enterprise\"}")]
    [InlineData("{\"Id\":\"strict-enterprise\",\"Version\":\"\"}")]
    [InlineData("{\"Id\":\"strict-enterprise\",\"Version\":\"999.0.0\"}")]
    public void Invalid_selection_is_rejected_without_overwriting_it(string content)
    {
        File.WriteAllText(StatePath, content);
        Assert.NotNull(Record.Exception(() => new TrustPolicyPackRegistry(Definitions)));
        Assert.Equal(content, File.ReadAllText(StatePath));
    }

    [Fact]
    public void Unreadable_selection_is_not_replaced_with_shipped_default()
    {
        Directory.CreateDirectory(StatePath);
        Assert.NotNull(Record.Exception(() => new TrustPolicyPackRegistry(Definitions)));
        Assert.True(Directory.Exists(StatePath));
    }

    [Fact]
    public void Valid_provisioned_selection_survives_reopen()
    {
        File.WriteAllText(StatePath, Selection("strict-enterprise"));
        for (var i = 0; i < 3; i++)
        {
            var active = new TrustPolicyPackRegistry(Definitions).GetActivePack()!;
            Assert.Equal("strict-enterprise", active.Id);
            Assert.Equal("1.0.0", active.Version);
        }
    }

    [Fact]
    public void Optional_mode_preserves_legacy_missing_state_behavior()
    {
        using var optional = new EnvironmentVariableScope("ASHLAR_TRUST_POLICY_REQUIRE_ACTIVE", null);
        Assert.Null(new TrustPolicyPackRegistry(Definitions).GetActivePack());
    }

    public void Dispose()
    {
        _pointer.Dispose();
        _required.Dispose();
        Directory.Delete(_root, true);
    }
}
