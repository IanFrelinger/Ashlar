using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// A convention test outside the cert-gate filter is advisory, whatever its author intended.
///
/// <para><b>Why this exists.</b> <c>cert-gate</c> is one of five required status checks on master, and
/// it does not run this assembly — it runs a SUBSTRING SELECTION of it
/// (<c>scripts/cert-gate-config.sh</c>). A merge-blocking convention placed one namespace to the
/// side is therefore not merge-blocking at all, and nothing says so: the test still runs somewhere,
/// still passes, still looks like a guard. <c>TimeoutConventionTests</c> sat in
/// <c>...Tests.Testing</c>, which no workflow references, from the day it was written.</para>
///
/// <para>This test reads the filter from the shell script rather than restating it, so the two
/// cannot drift apart. Editing <c>CERT_GATE_FILTER</c> to drop a namespace fails here rather than
/// quietly disarming everything in it.</para>
///
/// <para>Scope is the executing assembly. Convention tests in other test projects
/// (<c>Ashlar.Tests.Application.Ide</c> has three) are covered by their own gates, and asserting
/// about them from inside the required check would make it fail for a reason its own runner
/// cannot fix.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class CertGateFilterCoverageTests
{
    private const string FilterScriptRelativePath = "scripts/cert-gate-config.sh";

    /// <summary>
    /// A convention test is one whose type name ends in <c>ConventionTests</c>. That is the
    /// naming this repository already uses for "an assertion about how the repository is allowed
    /// to be shaped", and it is the set <c>ci/cert-gate-assertions.md</c> documents.
    /// </summary>
    private const string ConventionSuffix = "ConventionTests";

    [Fact]
    public void Every_convention_test_sits_in_a_namespace_the_cert_gate_selects()
    {
        var selectors = ReadFilterSelectors();

        selectors.Should().NotBeEmpty(
            "the filter is read from {0}; an empty parse would make this test vacuous and silently "
            + "stop guarding anything", FilterScriptRelativePath);

        var unreachable = typeof(CertGateFilterCoverageTests).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith(ConventionSuffix, StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .Where(fqn => !selectors.Any(s => fqn.Contains(s, StringComparison.Ordinal)))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        unreachable.Should().BeEmpty(
            "cert-gate selects tests by substring, so a convention test outside its selection is "
            + "advisory and nothing says so. Move it into "
            + "Ashlar.Tests.Infrastructure.Tests.Certification, or widen CERT_GATE_FILTER in {0} "
            + "deliberately. Unreachable: {1}",
            FilterScriptRelativePath, string.Join(", ", unreachable));
    }

    /// <summary>
    /// The guard above is only as good as its ability to find this assembly's convention tests at
    /// all. If the naming convention is ever abandoned, the set silently empties and every
    /// assertion above passes for the wrong reason.
    /// </summary>
    [Fact]
    public void The_convention_test_set_is_not_empty()
    {
        var conventionTests = typeof(CertGateFilterCoverageTests).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith(ConventionSuffix, StringComparison.Ordinal))
            .ToList();

        conventionTests.Should().NotBeEmpty(
            "this assembly is expected to hold convention tests; finding none means the "
            + "*{0} naming was abandoned and the coverage check above is passing vacuously",
            ConventionSuffix);
    }

    /// <summary>
    /// Parses the <c>FullyQualifiedName~...</c> substrings out of CERT_GATE_FILTER. Reading the
    /// script keeps this test and the gate from drifting; restating the filter here would let them.
    /// </summary>
    private static List<string> ReadFilterSelectors()
    {
        var path = Path.Combine(
            RepoPathResolver.FindRepoRoot(),
            FilterScriptRelativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(path).Should().BeTrue(
            "{0} defines the selection cert-gate runs; without it this test cannot know what is "
            + "merge-blocking", FilterScriptRelativePath);

        return Regex.Matches(File.ReadAllText(path), @"FullyQualifiedName~([A-Za-z0-9_.]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
