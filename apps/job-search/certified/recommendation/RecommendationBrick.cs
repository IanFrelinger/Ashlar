using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace JobSearch.Certified;

/// <summary>
/// jobsearch.recommendation: the rule that turns a screening's counts into a recommendation. Certified: this exact
/// source is what the job-search app runs, admitted by Ashlar's certification gate. Percentages and years are integers
/// (years in tenths) so every case compares exactly.
/// </summary>
public sealed class RecommendationBrick : DomainBrick
{
    public RecommendationBrick()
    {
        Id = "jobsearch.recommendation";
        Name = "Job fit recommendation";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "Turns screening counts into strong_fit, conditional_fit, stretch or not_currently_eligible, with the reason.";
        Implementations = new BrickImplementations
        {
            Deterministic = new DeterministicImplementation
            {
                Id = "rules", Name = "Rules", Executor = "code", Description = "Fixed thresholds; no model.",
            },
        };
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("requiredSkills", "int", "Skills the posting requires"),
                new BrickInputDefinition("matchedSkills", "int", "Required skills the résumé shows"),
                new BrickInputDefinition("yearsRequired", "int", "Minimum years the posting states; -1 when it states none"),
                new BrickInputDefinition("yearsHaveTenths", "int", "Candidate's years x 10 from résumé dates; -1 when unknown"),
                new BrickInputDefinition("blockers", "int", "User-confirmed facts that rule the job out"),
                new BrickInputDefinition("unknowns", "int", "Requirements awaiting the user's answer"),
                new BrickInputDefinition("preferenceMismatches", "int", "Conflicts with the user's stated preferences"),
                new BrickInputDefinition("deadlinePassed", "bool", "The posting's stated deadline has passed"),
                new BrickInputDefinition("strongPercent", "int", "Skill coverage for a strong fit"),
                new BrickInputDefinition("conditionalPercent", "int", "Skill coverage for a conditional fit"),
                new BrickInputDefinition("maxYearsShortTenths", "int", "Years below the minimum (x 10) still conditional"),
            ],
            Outputs =
            [
                new BrickOutputDefinition("recommendation", "string", "strong_fit, conditional_fit, stretch or not_currently_eligible"),
                new BrickOutputDefinition("coveragePercent", "int", "Required-skill coverage; -1 when no requirement was extracted"),
                new BrickOutputDefinition("reason", "string", "Why"),
            ],
        };
    }

    public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        var required = input.Get<int>("requiredSkills");
        var matched = input.Get<int>("matchedSkills");
        var yearsRequired = input.Get<int>("yearsRequired");
        var yearsHave = input.Get<int>("yearsHaveTenths");
        var blockers = input.Get<int>("blockers");
        var unknowns = input.Get<int>("unknowns");
        var mismatches = input.Get<int>("preferenceMismatches");
        var deadlinePassed = input.Get<bool>("deadlinePassed");
        var strong = input.Get<int>("strongPercent");
        var conditional = input.Get<int>("conditionalPercent");
        var maxShort = input.Get<int>("maxYearsShortTenths");

        var coverage = required > 0 ? matched * 100 / required : -1;
        var shortBy = yearsRequired >= 0 && yearsHave >= 0 ? Math.Max(0, yearsRequired * 10 - yearsHave) : 0;

        string recommendation;
        string reason;
        if (blockers > 0)
        {
            recommendation = "not_currently_eligible";
            reason = "a fact the user confirmed rules this job out";
        }
        else if (coverage < 0)
        {
            recommendation = "conditional_fit";
            reason = "no skill requirements could be read; review by hand";
        }
        else if (coverage >= strong && shortBy == 0)
        {
            recommendation = "strong_fit";
            reason = "skills and experience meet the posting";
        }
        else if (coverage >= conditional && shortBy <= maxShort)
        {
            recommendation = "conditional_fit";
            reason = "most requirements are met";
        }
        else
        {
            recommendation = "stretch";
            reason = "too many requirements are not shown";
        }

        if (recommendation == "strong_fit" && (unknowns > 0 || mismatches > 0))
        {
            recommendation = "conditional_fit";
            reason = unknowns > 0 ? "a strong match with questions for the user" : "a strong match that conflicts with a preference";
        }
        if (deadlinePassed && recommendation != "not_currently_eligible")
        {
            recommendation = "stretch";
            reason = "the stated deadline has passed";
        }

        var output = new BrickOutput { Summary = $"{recommendation}: {reason}" };
        output.Set("recommendation", recommendation);
        output.Set("coveragePercent", coverage);
        output.Set("reason", reason);
        return Task.FromResult(output);
    }
}
