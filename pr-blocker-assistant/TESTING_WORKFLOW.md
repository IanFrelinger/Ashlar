# Testing Workflow - PR Blocker Assistant

## Phase 2: Real-World Validation

This document outlines the complete workflow for testing the PR Blocker Assistant with 10 real-world PRs.

## Goals

1. **Validate accuracy** - Does the tool correctly identify blockers?
2. **Measure value** - Does it save time vs manual checking?
3. **Discover gaps** - What's missing from Ashlar 0.3.0?
4. **Inform roadmap** - What should be in Ashlar 0.4.0?

## Setup Requirements

### Environment
- Machine with .NET 10 SDK (or .NET 8)
- Internet access (nuget.org + GitHub API)
- GitHub personal access token (optional but recommended)

### One-Time Setup

```bash
# Clone the repository
git clone https://github.com/IanFrelinger/Ashlar.git
cd Ashlar/pr-blocker-assistant

# Build the tool
dotnet restore --configfile nuget.config
dotnet build

# Verify build succeeded
ls -la src/PrBlocker.CLI/bin/Debug/net10.0/

# Optional: Set GitHub token
export GITHUB_TOKEN=ghp_your_token_here
```

### Getting a GitHub Token

1. Visit https://github.com/settings/tokens
2. Click "Generate new token (classic)"
3. Select scopes:
   - `public_repo` (for public repositories)
   - `repo` (if testing private repositories)
4. Generate and copy the token
5. Export: `export GITHUB_TOKEN=ghp_xxx`

**Why a token?**
- Without: 60 requests/hour
- With: 5,000 requests/hour

## Test Selection Strategy

Run 10 assessments covering diverse scenarios:

### PR States (mix of)
- ✅ **Ready to merge** - All checks green, approved
- 🚫 **Blocked** - Failed checks, change requests
- ⏳ **In progress** - Checks pending, reviews pending
- ✔️ **Merged** - Already merged, verify tool handles correctly
- ❌ **Closed** - Closed without merge

### PR Types (mix of)
- 🏠 **Ashlar repository** - Familiar context, easy verification
- 🌐 **External public repos** - Microsoft Terminal, .NET Runtime, etc.
- 📦 **Different sizes** - Small fixes, large features
- 🏷️ **Releases** - Test release tag checking (not just PRs)

### Suggested Test Suite

| # | Scenario | Example |
|---|----------|---------|
| 1 | Ashlar PR - Open, checks running | PR #840 or #839 |
| 2 | Ashlar PR - Recently merged | PR #843, #842, #841 |
| 3 | Ashlar PR - Your new PR! | PR #844 (this PR) |
| 4 | External - Large project | microsoft/terminal open PR |
| 5 | External - Large project | dotnet/runtime open PR |
| 6 | PR with failing checks | Find one with ❌ checks |
| 7 | PR with change requests | Find one with requested changes |
| 8 | Draft PR | Find a draft PR |
| 9 | PR with merge conflicts | Find one with conflicts |
| 10 | Release tag | github.com/org/repo/releases/tag/v1.0.0 |

## Running Each Assessment

### Step 1: Run the Tool

```bash
# Basic usage
dotnet run --project src/PrBlocker.CLI -- <PR_URL>

# Or with the test script
./test-real-pr.sh <PR_URL>

# Examples
dotnet run --project src/PrBlocker.CLI -- https://github.com/IanFrelinger/Ashlar/pull/840
./test-real-pr.sh https://github.com/microsoft/terminal/pull/12345
```

### Step 2: Capture Output

Copy the entire tool output. You'll need:
- Summary (✅ READY or ❌ BLOCKED)
- List of blockers
- Evidence JSON

### Step 3: Manual Verification

Open the PR in GitHub and check:
1. **Status checks** - Are they passing, failing, or pending?
2. **Reviews** - Any approvals or change requests?
3. **Merge conflicts** - Does it have conflicts?
4. **Draft status** - Is it a draft?
5. **Overall readiness** - Would YOU merge it?

### Step 4: Record in USAGE_LOG.md

Copy the assessment template from `USAGE_LOG.md` and fill it in:
- Command used
- Tool output
- Manual verification results
- Accuracy assessment
- Time saved estimate
- Framework observations

Update the progress tracker table.

### Step 5: Note Framework Gaps

As you test, track:
- **What worked well** - Where Ashlar made it easy
- **What was difficult** - Where you fought the framework
- **What was missing** - Features you wish existed
- **API friction** - Confusing or verbose patterns

## Measuring Success

### Accuracy Metrics
- **True Positives**: Tool correctly identified a blocker
- **True Negatives**: Tool correctly said "ready"
- **False Positives**: Tool flagged a non-issue as blocking
- **False Negatives**: Tool missed a real blocker

Target: ≥90% accuracy across 10 assessments

### Time Savings
- Estimate how long manual checking would take
- Compare to tool execution time
- Note: First few uses may be slower as you learn

Target: ≥50% time savings on average

### Framework Quality
- Note every friction point
- Record workarounds you had to implement
- Document features you wish existed

Target: ≥5 actionable recommendations for Ashlar 0.4.0

## After All 10 Assessments

### 1. Analyze Results

Review `USAGE_LOG.md` and calculate:
- Overall accuracy rate
- Average time saved
- Most common false positives/negatives
- Most frequent framework gaps

### 2. Update FINDINGS.md

Add a new section:
```markdown
## Real-World Testing Results

**Assessments completed**: 10
**Accuracy rate**: X%
**Average time saved**: Y minutes
**Would recommend**: Yes/No

### Key Findings
[Summary of what worked and what didn't]

### Top Framework Gaps
1. [Gap 1]
2. [Gap 2]
3. [Gap 3]
```

### 3. Create Recommendations Document

File: `ASHLAR_0.4.0_RECOMMENDATIONS.md`

Based on your testing, recommend:
1. New packages (e.g., `Ashlar.Bricks.GitHub`)
2. New features (e.g., output formatters)
3. API improvements (e.g., brick discovery)
4. Documentation additions
5. Example projects

### 4. Report Back

Share results with:
- Link to completed `USAGE_LOG.md`
- Summary of findings
- Recommendations for Ashlar roadmap
- Answer: "Should we invest more in this tool?"

## Iterating on the Tool

If you find bugs or missing features during testing:

### For Tool Bugs
1. Document the issue in assessment notes
2. Continue testing (don't block on tool bugs)
3. File issues after completing 10 assessments

### For Framework Gaps
1. Note them in each assessment
2. Collect patterns across assessments
3. Synthesize into Ashlar recommendations

### If Tool is Fundamentally Broken
- Stop testing
- Document what's broken
- Fix before continuing
- Restart from Assessment #1

## Expected Timeline

- **Setup**: 15-30 minutes (one-time)
- **Each assessment**: 10-15 minutes
- **Analysis**: 30-60 minutes
- **Total**: ~3-4 hours

Can be spread across multiple sessions.

## Questions During Testing?

If you're unsure about:
- **Tool behavior**: Check `EXAMPLE_OUTPUT.md` for expected patterns
- **Ashlar usage**: Review `PROJECT_SUMMARY.md` and `FINDINGS.md`
- **GitHub APIs**: Tool uses Octokit; see their docs
- **Anything else**: Document it as a question in your assessment notes

## Success Criteria

This testing phase succeeds if:
1. ✅ 10 assessments completed
2. ✅ Accuracy and time-savings measured
3. ✅ Framework gaps documented
4. ✅ Clear recommendations for Ashlar 0.4.0
5. ✅ Decision made: invest more or move on?

## Good Luck!

Remember: The goal isn't to prove the tool works perfectly. The goal is to learn what works, what doesn't, and what Ashlar needs to improve.

Be honest in your assessments. False positives and framework gaps are valuable findings!
