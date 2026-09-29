using FluentAssertions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Every workflow file must parse as YAML — asserted from cert-gate, deliberately not from shell-lint.
///
/// <para><b>Why the location is the whole point.</b> A workflow GitHub cannot parse does not fail
/// loudly: the run is created with ZERO jobs and posts no check at all, so
/// <c>gh pr checks</c> lists nothing for it and the status rollup reads "0 failing". That is not a
/// hypothetical — <c>shell-lint.yml</c>'s own header records two workflows that sat in that state
/// from the day they were written, and it happened again while this test was being written: a step
/// named <c>Workflows keep outsider-controlled values out of run: scripts</c> put a <c>": "</c>
/// inside an unquoted scalar, shell-lint.yml stopped parsing, and shell-lint posted no check.</para>
///
/// <para><b>shell-lint cannot catch this for itself.</b> The checks that would notice live INSIDE
/// shell-lint.yml, so the one file whose breakage silences them is the one they cannot cover. The
/// fact has to run from a different required check, which is why it is here: cert-gate is required,
/// and its workflow is not the one under test.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class WorkflowFilesParseTests
{
    [Fact]
    public void EveryWorkflowFileParsesAsYaml()
    {
        var workflows = WorkflowDirectory();

        var files = workflows.GetFiles("*.yml").Concat(workflows.GetFiles("*.yaml"))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToArray();

        // POSITIVE CONTROL. A directory that yields no files would satisfy every assertion below
        // while checking nothing, which is the same "healthy by never running" shape this fact
        // exists to catch.
        files.Should().HaveCountGreaterThan(20,
            "POSITIVE CONTROL: this repository has dozens of workflows. Finding almost none means "
            + "the directory lookup is wrong and this fact is proving nothing.");

        var broken = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var yaml = new YamlStream();
                using var reader = new StreamReader(file.FullName);
                yaml.Load(reader);

                // A file that parses to nothing is as useless to GitHub as one that does not parse.
                if (yaml.Documents.Count == 0)
                    broken.Add($"{file.Name}: parsed to zero documents");
            }
            catch (Exception ex)
            {
                broken.Add($"{file.Name}: {ex.Message.Split('\n')[0].Trim()}");
            }
        }

        broken.Should().BeEmpty(
            "a workflow GitHub cannot parse is not a failing check, it is an ABSENT one: the run is "
            + "created with zero jobs, posts no check, and the pull request reads '0 failing'. If "
            + "the file is a required check, branch protection still blocks the merge - but nothing "
            + "tells you why, and 'no failures' reads as 'passing'. The commonest cause is ': ' "
            + "inside an unquoted scalar, usually a step name.");
    }

    private static DirectoryInfo WorkflowDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = new DirectoryInfo(Path.Combine(dir.FullName, ".github", "workflows"));
            if (candidate.Exists)
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate .github/workflows above " + AppContext.BaseDirectory
            + ". This fact must fail rather than silently scan nothing.");
    }
}
