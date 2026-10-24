# Usage Log

Track real-world usage of the PR Blocker Assistant. **Target: 10 assessments**

## Testing Instructions

### Prerequisites
1. Machine with .NET 10 SDK (or .NET 8)
2. GitHub personal access token (optional but recommended)
3. Clone and build the project:
   ```bash
   cd pr-blocker-assistant
   dotnet restore --configfile nuget.config
   dotnet build
   ```

### Running an Assessment

```bash
# Set GitHub token (recommended)
export GITHUB_TOKEN=ghp_your_token_here

# Run against a PR
dotnet run --project src/PrBlocker.CLI -- <PR_URL>

# Or use the test script
./test-real-pr.sh <PR_URL>
```

### Suggested Test PRs

Mix of different scenarios for comprehensive testing:

**Ashlar Repository:**
- Open PRs: https://github.com/IanFrelinger/Ashlar/pull/840
- Open PRs: https://github.com/IanFrelinger/Ashlar/pull/839
- Merged PRs: https://github.com/IanFrelinger/Ashlar/pull/843
- Merged PRs: https://github.com/IanFrelinger/Ashlar/pull/838

**External Projects (public):**
- Microsoft Terminal: https://github.com/microsoft/terminal/pull/[recent-number]
- .NET Runtime: https://github.com/dotnet/runtime/pull/[recent-number]

### What to Track

For each assessment, record:
1. **Command used** - Exact CLI command
2. **Tool output** - Summary of what the tool reported
3. **Manual verification** - What you found by checking GitHub manually
4. **Accuracy** - Did the tool get it right?
5. **Time saved** - Estimate vs manual checking
6. **Framework observations** - What worked well, what didn't

---

## Assessment Template

Copy this for each test:

```markdown
## Assessment #N - YYYY-MM-DD

**Target**: [PR/Release URL]
**Repository**: [owner/repo]
**PR State**: [Open/Merged/Closed]

### Command
```bash
[exact command used]
```

### Tool Output
```
[paste tool output here]
```

### Manual Verification
Checked GitHub directly:
- Commit status: [status]
- Check runs: [list with statuses]
- Reviews: [approved/changes requested/pending]
- Merge conflicts: [yes/no]
- Overall assessment: [blocked/ready]

### Accuracy
- ✅/❌ Correct conclusion? [Yes/No + explanation]
- ✅/❌ All blockers identified? [Any missed?]
- ✅/❌ Any false positives? [What was wrong?]

### Value
- Time saved: ~[N] minutes vs manual check
- Would use again? [Yes/No]
- Notes: [observations, edge cases found]

### Framework Observations
**Ashlar strengths:**
- [what worked well]

**Framework gaps:**
- [what was missing or difficult]

**Recommendations:**
- [suggestions for Ashlar improvements]
```

---

## Progress Tracker

| # | Date | PR | State | Accurate? | Time Saved | Notes |
|---|------|-----|-------|-----------|------------|-------|
| 1 | - | - | - | - | - | Pending |
| 2 | - | - | - | - | - | Pending |
| 3 | - | - | - | - | - | Pending |
| 4 | - | - | - | - | - | Pending |
| 5 | - | - | - | - | - | Pending |
| 6 | - | - | - | - | - | Pending |
| 7 | - | - | - | - | - | Pending |
| 8 | - | - | - | - | - | Pending |
| 9 | - | - | - | - | - | Pending |
| 10 | - | - | - | - | - | Pending |

---

## Assessments

### Assessment #1 - [READY FOR TESTING]

**Instructions**: Build the project on a .NET-capable machine and run against one of the suggested PRs above. Copy the assessment template and fill it in with your results.

**First Test Recommendation**: Start with an open Ashlar PR like #840 or #839, as these are easy to verify manually.
