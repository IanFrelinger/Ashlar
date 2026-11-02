using Ashlar.Bricks.GitHub;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Bricks;

public sealed class GitHubProviderAssuranceRegressionTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RequiredCheck = "ci / unit";

    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(true, "fail")]
    public void A_name_only_failure_is_not_a_failed_requirement_until_its_provider_is_known(bool originsKnown, string assurance)
    {
        var snapshot = Snapshot(originsKnown);
        var answer = BlockerAnalysis.Analyze(snapshot);

        var pack = Pack(snapshot, answer);

        answer.Verdict.Should().Be("blocked", "the existing name-only verdict remains compatible");
        answer.Blockers.Should().ContainSingle();
        pack.Verdict.Should().Be(answer.Verdict);
        pack.Assurance.Should().Be(assurance);
        pack.Findings.Where(f => f.Id.StartsWith("blocker-", StringComparison.Ordinal))
            .Should().OnlyContain(f => f.Status == (originsKnown ? "fail" : "unknown"));
        if (!originsKnown)
        {
            pack.Findings.Should().NotContain(f => f.Status == "fail");
            pack.Findings.Should().Contain(f => f.Id == "required-check-origin" && f.Status == "unknown");
            pack.Findings.Where(IsRequiredObservation)
                .Should().OnlyContain(f => f.Status == "unknown");
        }
    }

    [Fact]
    public void A_status_failure_also_requires_verified_origin_before_it_is_asserted_as_a_failed_requirement()
    {
        var snapshot = Snapshot(false);
        var pr = snapshot.PullRequest!;
        snapshot = snapshot with
        {
            PullRequest = pr with
            {
                Ci = new CiFacts(Head, [], 0,
                    [new StatusFact(RequiredCheck, "failure", "https://ci.example.invalid/failed", "build failed")], 1)
            }
        };
        var answer = BlockerAnalysis.Analyze(snapshot);

        var pack = Pack(snapshot, answer);

        answer.Verdict.Should().Be("blocked");
        pack.Assurance.Should().Be("unknown");
        pack.Findings.Should().NotContain(f => f.Status == "fail");
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("conflict")]
    [InlineData("changes-requested")]
    public void Unverified_check_origins_do_not_suppress_independent_blockers(string blocker)
    {
        var snapshot = Snapshot(false);
        var pr = snapshot.PullRequest!;
        snapshot = snapshot with
        {
            PullRequest = pr with
            {
                Draft = blocker == "draft",
                Mergeable = blocker != "conflict",
                MergeableState = blocker == "conflict" ? "dirty" : "clean",
                Reviews = blocker == "changes-requested"
                    ? [new ReviewFact("reviewer", "CHANGES_REQUESTED", "https://github.com/eval/fixture/pull/1#review-1", DateTimeOffset.UnixEpoch, Head)]
                    : []
            }
        };
        var answer = BlockerAnalysis.Analyze(snapshot);

        var pack = Pack(snapshot, answer);

        pack.Assurance.Should().Be("fail");
        pack.Findings.Should().ContainSingle(f => f.Status == "fail");
        pack.Findings.Should().Contain(f => f.Status == "unknown" && f.Explanation.Contains("app/provider identity"));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    [InlineData("waiting")]
    [InlineData("requested")]
    [InlineData("pending")]
    public void A_fully_observed_pending_required_check_is_an_unmet_requirement(string state)
    {
        var snapshot = RequiredSnapshot(new CiFacts(Head, [new CheckFact(RequiredCheck, state, null, null)], 1, [], 0));
        var answer = BlockerAnalysis.Analyze(snapshot);
        var pack = Pack(snapshot, answer);

        answer.Verdict.Should().Be("blocked");
        pack.Assurance.Should().Be("fail");
        pack.Findings.Where(IsRequiredObservation).Should().OnlyContain(f => f.Status == "fail");
        pack.Findings.Should().Contain(f => f.Explanation.Contains("currently unmet"));
    }

    [Fact]
    public void A_fully_observed_pending_required_status_is_unmet()
    {
        var snapshot = RequiredSnapshot(new CiFacts(Head, [], 0, [new StatusFact(RequiredCheck, "pending", null, null)], 1));
        var pack = Pack(snapshot, BlockerAnalysis.Analyze(snapshot));
        pack.Assurance.Should().Be("fail");
        pack.Findings.Where(IsRequiredObservation).Should().OnlyContain(f => f.Status == "fail");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Complete_required_context_absence_is_unmet_including_a_collection_check(bool collection)
    {
        var snapshot = RequiredSnapshot(new CiFacts(Head, [], 0, [], 0));
        var pack = Pack(snapshot, BlockerAnalysis.Analyze(snapshot), collection ? RequiredCheck : null);
        pack.Verdict.Should().Be("blocked");
        pack.Assurance.Should().Be("fail");
        pack.Findings.Where(IsRequiredObservation).Should().OnlyContain(f => f.Status == "fail");
    }

    [Theory]
    [InlineData("checks-unreadable")]
    [InlineData("statuses-unreadable")]
    [InlineData("checks-truncated")]
    [InlineData("statuses-truncated")]
    public void Incomplete_CI_does_not_establish_a_pending_requirement_as_unmet(string missing)
    {
        var checks = new CheckFact[] { new(RequiredCheck, "queued", null, null) };
        var ci = new CiFacts(Head, missing == "checks-unreadable" ? null : checks,
            missing == "checks-truncated" ? 2 : 1, missing == "statuses-unreadable" ? null : [],
            missing == "statuses-truncated" ? 1 : 0);
        var snapshot = RequiredSnapshot(ci);
        var pack = Pack(snapshot, BlockerAnalysis.Analyze(snapshot));
        pack.Assurance.Should().Be("unknown");
        pack.Findings.Should().NotContain(f => f.Status == "fail");
        pack.Findings.Where(IsRequiredObservation).Should().OnlyContain(f => f.Status == "unknown");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_pending_check_is_unknown_when_its_requirement_or_provider_is_unverified(bool originsKnown, bool policyKnown)
    {
        var snapshot = RequiredSnapshot(new CiFacts(Head, [new CheckFact(RequiredCheck, "queued", null, null)], 1, [], 0), originsKnown, policyKnown);
        var pack = Pack(snapshot, BlockerAnalysis.Analyze(snapshot));
        pack.Assurance.Should().Be("unknown");
        pack.Findings.Should().NotContain(f => f.Status == "fail");
    }

    [Theory]
    [InlineData("waiting_on_magic", null)]
    [InlineData("completed", null)]
    [InlineData("completed", "unrecognized")]
    public void An_unrecognized_check_state_or_conclusion_remains_unknown(string state, string? conclusion)
    {
        var snapshot = RequiredSnapshot(new CiFacts(Head, [new CheckFact(RequiredCheck, state, conclusion, null)], 1, [], 0));
        var pack = Pack(snapshot, BlockerAnalysis.Analyze(snapshot));
        pack.Assurance.Should().Be("unknown");
        pack.Findings.Should().NotContain(f => f.Status == "fail");
    }

    [Fact]
    public void A_proven_required_pending_collection_check_is_circular_and_currently_unmet()
    {
        var snapshot = RequiredSnapshot(new CiFacts(Head, [new CheckFact(RequiredCheck, "in_progress", null, null)], 1, [], 0));
        var pack = Pack(snapshot, BlockerAnalysis.Analyze(snapshot), RequiredCheck);
        pack.Assurance.Should().Be("fail");
        pack.Findings.Should().Contain(f => f.Id == "collection-check" && f.Status == "fail"
            && f.Explanation.Contains("circular") && f.Explanation.Contains("currently unmet"));
    }

    [Fact]
    public void Incomplete_context_absence_is_not_a_definitive_missing_check_claim()
    {
        var snapshot = RequiredSnapshot(new CiFacts(Head, [], 1, [], 0));
        var pack = Pack(snapshot, BlockerAnalysis.Analyze(snapshot));
        pack.Assurance.Should().Be("unknown");
        pack.Findings.Where(f => f.Id.StartsWith("blocker-", StringComparison.Ordinal) || IsRequiredObservation(f))
            .Should().OnlyContain(f => f.Status == "unknown" && f.Explanation.Contains("not observed in incomplete CI results"));
    }

    [Fact]
    public void A_verified_observed_failure_remains_confirmed_even_when_other_CI_results_are_missing()
    {
        var snapshot = RequiredSnapshot(new CiFacts(Head, [new CheckFact(RequiredCheck, "completed", "failure", null)], 2, [], 0));
        var pack = Pack(snapshot, BlockerAnalysis.Analyze(snapshot));
        pack.Assurance.Should().Be("fail");
        pack.Findings.Where(IsRequiredObservation).Should().OnlyContain(f => f.Status == "fail");
    }

    private static bool IsRequiredObservation(EvidenceFinding finding) =>
        finding.Id.StartsWith("required-check-", StringComparison.Ordinal)
        && char.IsAsciiDigit(finding.Id["required-check-".Length]);

    private static BlockerSnapshot RequiredSnapshot(CiFacts ci, bool originsKnown = true, bool policyKnown = true)
    {
        var snapshot = Snapshot(originsKnown);
        var pr = snapshot.PullRequest!;
        return snapshot with
        {
            PullRequest = pr with
            {
                Ci = ci,
                Protection = pr.Protection with { RequiredChecks = policyKnown ? [RequiredCheck] : null, UnsupportedRules = [] }
            }
        };
    }

    private static BlockerSnapshot Snapshot(bool originsKnown)
    {
        GitHubTarget.TryParse("https://github.com/eval/fixture/pull/1", out var target, out _).Should().BeTrue();
        var checks = new CheckFact[]
        {
            new(RequiredCheck, "completed", "success", "https://github.com/eval/fixture/checks/required-provider"),
            new(RequiredCheck, "completed", "failure", "https://github.com/eval/fixture/checks/other-provider")
        };
        var policy = new ProtectionFacts([RequiredCheck], 0, "NONE", RulesRead: true,
            RequiredCheckOriginsKnown: originsKnown, UnsupportedRules: ["required_deployments"]);
        var pr = new PullRequestFacts(target!.WebUrl, "provider boundary", false, false, false, true, "clean", "main", [],
            new CiFacts(Head, checks, checks.Length, [], 0), policy, Head, Base);
        return new BlockerSnapshot(target!, pr, null, [], "stable");
    }

    private static ReleaseEvidencePack Pack(BlockerSnapshot snapshot, BlockerAnswer answer, string? collectionCheckName = null) =>
        ReleaseEvidence.Create(snapshot, answer, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "evaluation", false, collectionCheckName);
}
