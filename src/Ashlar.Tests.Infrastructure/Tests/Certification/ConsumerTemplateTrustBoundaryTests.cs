using FluentAssertions;
using Ashlar.Tests.Infrastructure.Certification.Reuse;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Freezes the disclosure in the three entry points consumers read.</summary>
/// <remarks>
/// This is a text contract only. It does not classify code, prove verification or prove that a
/// host executes verified bytes. Replacing this template with a verified host requires executable
/// consumer tests and an intentional update of these disclosures in the same change.
/// </remarks>
[Trait("Category", "Certification")]
public sealed class ConsumerTemplateTrustBoundaryTests
{
    [Theory]
    [InlineData("host/Program.cs")]
    [InlineData("host/README.md")]
    [InlineData("CONSUMING.md")]
    public void Template_entry_points_disclose_the_current_trust_boundary(string relativePath)
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "consumer-template", relativePath);
        File.Exists(path).Should().BeTrue("the disclosure file must exist");
        File.ReadAllText(path).Should().Contain("executes an unverified brick",
            "this template demonstrates host structure and performs no certification verification");
    }
}
