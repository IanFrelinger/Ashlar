using FluentAssertions;
using JobSearch.Host;
using Xunit;

namespace JobSearch.Tests;

/// <summary>The operator's side of data-subject requests that do not come through the Privacy page (PRIVACY.md).</summary>
public sealed class OperatorTests
{
    [Fact]
    public async Task The_operator_can_export_and_erase_an_account_for_a_request_that_arrives_by_email()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        var (sam, _) = h.Sam();

        var export = Path.Combine(h.Dir, "export.json");
        (await Program.Main(["--data", h.Dir, "user", "export", "--email", Harness.JordanEmail, "--out", export])).Should().Be(0);
        File.ReadAllText(export).Should().Contain("Jordan Rivera").And.NotContain("Sam Okafor", "an export holds one person's data");

        (await Program.Main(["--data", h.Dir, "user", "erase", "--email", Harness.JordanEmail])).Should().Be(2, "erasure needs --confirm ERASE");
        h.App.Accounts.FindUser(Harness.JordanEmail).Should().NotBeNull();

        (await Program.Main(["--data", h.Dir, "user", "erase", "--email", Harness.JordanEmail, "--confirm", "ERASE"])).Should().Be(0);
        h.App.Accounts.FindUser(Harness.JordanEmail).Should().BeNull();
        h.App.Compliance.WasErased(jordan).Should().BeTrue();
        h.App.Accounts.FindUser(Harness.SamEmail).Should().NotBeNull();
        h.App.Compliance.WasErased(sam).Should().BeFalse();
    }
}
