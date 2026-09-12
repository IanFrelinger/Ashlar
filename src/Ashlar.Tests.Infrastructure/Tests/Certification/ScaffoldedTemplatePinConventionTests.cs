using System.Text.RegularExpressions;
using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The brick scaffolding template pins its test-runner packages INLINE, because scaffolded output
/// is a standalone project with no <c>Directory.Packages.props</c> above it. That is correct and it
/// is also a silent divergence: a bump to the central pins moves all 22 centrally managed test
/// projects and leaves the template on the old runner, so the repository stops testing with the
/// versions it hands to users, and nothing says so.
///
/// <para>Found the hard way while re-landing the Test.Sdk 18.9.0 / xunit.runner.visualstudio 4.0.0
/// bump: the template was still on 18.5.1 / 3.1.5 from the previous bump.</para>
///
/// <para>Both directions, per <c>docs/HowGatesGoQuiet.md</c> section 7.
/// <see cref="Template_runner_pins_match_the_central_pins"/> is the new-offender half: the next
/// central bump fails until the template moves with it.
/// <see cref="Template_still_pins_inline_so_the_comparison_is_not_vacuous"/> is the stale-row half:
/// if the template ever opts into central management its inline pins vanish, the comparison above
/// would find nothing to compare, and a test that compares nothing passes.</para>
/// </summary>
public sealed class ScaffoldedTemplatePinConventionTests
{
    private const string TemplateProjectRelativePath =
        "samples/templates/brick/__BrickName__Brick.Tests/__BrickName__Brick.Tests.csproj";

    private const string CentralPinsRelativePath = "Directory.Packages.props";

    /// <summary>The packages the template pins that the repository also pins centrally.</summary>
    private static readonly string[] SharedPackages =
        ["Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio"];

    [Fact]
    public void Template_runner_pins_match_the_central_pins()
    {
        var central = ReadPins(CentralPinsRelativePath, "PackageVersion");
        var template = ReadPins(TemplateProjectRelativePath, "PackageReference");

        var mismatches = new List<string>();
        foreach (var package in SharedPackages)
        {
            central.TryGetValue(package, out var centralVersion).Should().BeTrue(
                $"{package} is expected to be centrally pinned in {CentralPinsRelativePath}");
            template.TryGetValue(package, out var templateVersion).Should().BeTrue(
                $"{package} is expected to be pinned inline in {TemplateProjectRelativePath}");

            if (!string.Equals(centralVersion, templateVersion, StringComparison.Ordinal))
                mismatches.Add($"{package}: central {centralVersion}, template {templateVersion}");
        }

        mismatches.Should().BeEmpty(
            "a scaffolded brick test project must be built with the runner this repository tests "
            + "with; when they diverge the template ships a runner no gate here has ever run");
    }

    [Fact]
    public void Template_still_pins_inline_so_the_comparison_is_not_vacuous()
    {
        var templatePath = Path.Combine(RepoPathResolver.FindRepoRoot(), TemplateProjectRelativePath);
        var text = File.ReadAllText(templatePath);

        text.Should().Contain(
            "<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>",
            "the inline pins only exist because the template opts out; if it opts back in they "
            + "disappear and the comparison above has nothing left to compare");

        ReadPins(TemplateProjectRelativePath, "PackageReference").Keys
            .Should().Contain(SharedPackages,
                "each of these must still be found by the parser the other test relies on — a "
                + "regex that stops matching is indistinguishable from pins that agree");
    }

    private static IReadOnlyDictionary<string, string> ReadPins(string relativePath, string element)
    {
        var path = Path.Combine(RepoPathResolver.FindRepoRoot(), relativePath);
        File.Exists(path).Should().BeTrue(
            $"{relativePath} is the input to this convention; a missing input is a hard failure of "
            + "the check, never a clean result");

        var pattern = new Regex(
            $"<{element}\\s+Include=\"(?<id>[^\"]+)\"\\s+Version=\"(?<version>[^\"]+)\"",
            RegexOptions.CultureInvariant);

        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in pattern.Matches(File.ReadAllText(path)))
            pins[match.Groups["id"].Value] = match.Groups["version"].Value;

        return pins;
    }
}
