using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.10 (design §2.7): the deployment profile reaches Infrastructure and the application hosts only as the
/// value <c>AddAshlar</c> registers (<c>Ashlar.Infrastructure.Deployment.ResolvedDeploymentProfile</c>); none of them
/// reads <c>AshlarDeploymentProfileEnvironment</c> or <c>ASHLAR_DEPLOYMENT_PROFILE</c> itself. Until PR 4.3 this was a
/// compile-time fact (Abstractions granted no <c>InternalsVisibleTo</c> to Infrastructure); 4.3 grants it, so from then
/// on the rule is this convention.
/// </summary>
[Trait("Category", "Certification")]
public sealed class DeploymentProfileReadConventionTests
{
    private static readonly string[] Roots =
    [
        Path.Combine("src", "Ashlar.Infrastructure"),
        Path.Combine("application", "src", "Ashlar.API"),
        Path.Combine("application", "src", "Ashlar.CLI"),
    ];

    private static readonly string[] Forbidden = ["AshlarDeploymentProfileEnvironment.", "ASHLAR_DEPLOYMENT_PROFILE"];

    /// <summary>Non-vacuity floor: the three roots hold far more production files than this.</summary>
    private const int ScannedFilesFloor = 300;

    [Fact]
    public void Infrastructure_the_API_and_the_CLI_never_read_the_profile_source()
    {
        var root = TestPaths.FindRepoRoot();
        var scanned = 0;
        var offenders = new List<string>();
        foreach (var relative in Roots)
        {
            var directory = Path.Combine(root, relative);
            Directory.Exists(directory).Should().BeTrue($"{relative} is a scan root");
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Contains("bin") || parts.Contains("obj"))
                    continue;
                scanned++;
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    var code = line.TrimStart();
                    if (code.StartsWith("//", StringComparison.Ordinal))
                        continue; // a doc or line comment may name the variable to say that it is not read
                    foreach (var token in Forbidden)
                    {
                        if (code.Contains(token, StringComparison.Ordinal))
                            offenders.Add($"{Path.GetRelativePath(root, file)}:{lineNumber}: {token}");
                    }
                }
            }
        }

        scanned.Should().BeGreaterThanOrEqualTo(ScannedFilesFloor, "the scan must read the tree");
        offenders.Should().BeEmpty(
            "the profile reaches Infrastructure, Ashlar.API and Ashlar.CLI only as the ResolvedDeploymentProfile value AddAshlar registers (SPEC-007 PR 4.10, design §2.7)");
    }
}
